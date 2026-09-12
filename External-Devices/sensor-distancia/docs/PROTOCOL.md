# Protocolo — Sensor de Distância (`sensor-distancia`)

**Versão do firmware:** `v11` (`DistanceClient r11`)  
**Compatibilidade com Hub:** `10.2.0-dev` (Protocolo 10)  
**Hardware:** ESP32 + sensor de distância a laser I2C VL53L0X

---

## 1. Visão Geral e Versões

| Versão | Capacidades |
|---|---|
| `v10` | Push simples `distance` e `time`. Configuração NVS acessível apenas via `POST /config` local ou serial. Sem canal de comando a partir do Hub; sem eco no quadro. |
| `v11` | **Canal de comando confiável por carona (piggyback)** na resposta do push. Ecos de configuração (`offset`, `sample_ms`, `send_ms`) e confirmação de revisão (`ack_cmd_id`). Hello registrado como `ver=v11`. `/diag` com `last_cmd_id`. |

---

## 2. Auto-Registro no Hub (`/nodeHello`)

Ao conectar-se ao Wi-Fi do Hub (`ModuloTECNAL_1` ou `ModuloTECNAL_2`, canal 6), o nó registra-se periodicamente:

```text
GET http://192.168.4.1/nodeHello?dev=distance&ver=v11&mac=<MAC_DO_ESP32>
```

- **Resposta do Hub (200):** `{"status":"ok","registered":"distance","assigned_ip":"<ip>","hub_time_ms":<millis>}`
- O Hub armazena a versão `v11`, MAC e IP na tabela `g_deviceRegistry[DEV_DISTANCE]`.

---

## 3. Push Periódico de Telemetria (Nó $\rightarrow$ Hub)

A cada `SEND_PERIOD_MS` (padrão 1000 ms), o nó executa uma requisição HTTP GET para o Hub:

```text
GET http://192.168.4.1/distance?distance=%d&time=%.1f&offset=%.2f&sample_ms=%lu&send_ms=%lu&ack_cmd_id=%lu
```

### Parâmetros:
| Parâmetro | Tipo | Exemplo | Descrição |
|---|---|---|---|
| `distance` | int | `125` | Distância medida em mm compensada: `raw_mm - offset_mm`. Sentinela `-1` se inválida ou sensor em falha. |
| `time` | float | `14.2` | Tempo de operação do nó em segundos desde o boot. |
| `offset` | float | `20.00` | Eco do offset em mm atualmente aplicado e persistido na NVS. |
| `sample_ms` | uint32 | `1000` | Eco do período de amostragem do sensor óptico em ms. |
| `send_ms` | uint32 | `1000` | Eco do período de envio HTTP em ms. |
| `ack_cmd_id` | uint32 | `1` | Revisão do último comando de configuração aplicado (`g_lastCmdId`). Sentinela `0` se nenhum comando foi recebido/aplicado. |

---

## 4. Canal de Comando por Carona no Push (Hub $\rightarrow$ Nó)

O nó não necessita de uma tarefa adicional de *polling* nem de uma rota HTTP cliente dedicada: ele já consulta o Hub a cada segundo e lê o corpo da resposta.

### 4.1 Resposta com Comando Pendente
Quando o operador altera uma configuração no aplicativo (ou via `POST /command` no Hub), o Hub enfileira a carga na caixa confiável `distanceBox`. No próximo push do nó, o Hub responde:

- **Status HTTP:** `200 OK`
- **Content-Type:** `application/json`
- **Corpo:**
  ```json
  {"cmd_id":1,"offset_mm":25.50}
  ```
  *(podendo conter também `sample_period`, `send_period` ou `reset_nvs`)*

### 4.2 Execução no Nó
Ao receber a resposta:
1. O nó avalia: `code == 200 && body.length() > 1 && body[0] == '{'`.
2. Chama `processConfigUpdate(body.c_str())`.
3. Extrai `cmd_id` e grava em `g_lastCmdId`.
4. Aplica os parâmetros recebidos (validando faixas: offset $[-50, 200]$ mm, períodos $[100, 60000]$ ms).
5. Se houver alteração válida, persiste na NVS (`saveNvsConfig()`).
6. Se `reset_nvs == 1`, restaura padrões de fábrica (`offset = 20.0 mm`), limpa a NVS e mantém o `cmd_id` em `g_lastCmdId`.

### 4.3 Confirmação (ACK) e Fechamento de Laço
No push seguinte (1 s depois):
- O nó envia `&ack_cmd_id=1&offset=25.50...`.
- O Hub recebe o `ack_cmd_id` igual à revisão pendente e limpa a `distanceBox` (`ackReliable`).
- No aplicativo, o indicador `DistanceCommandPending` cai para `false` e a telemetria reflete o novo valor aplicado.

### 4.4 Resposta sem Comando Pendente
Quando a `distanceBox` do Hub está vazia:
- **Status HTTP:** `200 OK`
- **Content-Type:** `text/plain`
- **Corpo:** `"Distance data received"` (como `body[0] == 'D' != '{'`, o nó não aciona o parser).

---

## 5. Endpoints HTTP Locais do Nó

O nó disponibiliza um servidor Web na porta 80 acessível em sua rede STA ou no SoftAP local (`Distance Sensor`):

### 5.1 `GET /config`
Retorna a configuração atual em formato JSON:
```json
{
  "sample_period": 1000,
  "send_period": 1000,
  "cooldown_soft": 15000,
  "cooldown_bus": 15000,
  "cooldown_xshut": 30000,
  "l1_reinit": 5,
  "l2_clear": 10,
  "l3_xshut": 20,
  "offset_mm": 20.00
}
```

### 5.2 `POST /config`
Aceita atualização de configuração local direta via JSON no corpo. Não requer `cmd_id`. Exemplo:
```json
{"offset_mm": 25.5, "sample_period": 500}
```
Ou restauração de fábrica:
```json
{"reset_nvs": 1}
```

### 5.3 `GET /diag` e `GET /status`
Retorna telemetria de saúde e diagnóstico:
```json
{
  "device": "distance-sensor",
  "version": "DistanceClient r11 (AP+STA, Configurable, Non-Blocking)",
  "uptime_s": 345,
  "free_heap": 284152,
  "wifi_status": 3,
  "ssid": "ModuloTECNAL_1",
  "rssi": -55,
  "ip": "192.168.4.2",
  "mac": "AA:BB:CC:DD:EE:02",
  "hub_fail_streak": 0,
  "ota": "false",
  "distance": 125,
  "sample_time": 345.1,
  "offset_mm": 20.00,
  "last_cmd_id": 1
}
```

### 5.4 `GET /update` e `POST /update`
Interface Web OTA com intertravamento para upload e validação de nova imagem de firmware `.bin`.

---

## 6. Persistência NVS (`dist_cfg`)

O subsistema NVS da Preferences API armazena as seguintes chaves:
- `offset_mm`: valor do offset do sensor em mm (padrão `20.0f`).
- `sample_ms`: período entre amostras I2C (padrão `1000` ms).
- `send_ms`: período entre requisições de push HTTP (padrão `1000` ms).
- `cd_soft`, `cd_bus`, `cd_xshut`: tempos de cooldown para recuperação de barramento I2C.
- `l1_reinit`, `l2_clear`, `l3_xshut`: limites de tentativas para cada camada de recuperação.
