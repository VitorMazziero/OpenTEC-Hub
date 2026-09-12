# Protocolo — Sensor de Biomassa (Firmware v11)

Especificação completa do protocolo de comunicação, telemetria, rotas HTTP, malha óptica e vocabulário de comandos do **Sensor de Biomassa (Turbidímetro/Fotômetro Óptico)** com ESP32-S3 e sensor de luz ambiente de alta faixa dinâmica (VEML7700).

---

## 1. Identidade e Registro

- **Dispositivo**: Sensor de Biomassa (`biomass-sensor` / `biomass`)
- **Versão do Firmware**: `v11`
- **Protocolo de Rede**: HTTP REST / Query params (compatibilidade de fio protocolo 10)
- **Hardware Óptico**: Sensor de luz ambiente VEML7700 (I2C) + LED emissor com controle PWM (2 kHz)

### Registro Automático (`/nodeHello`)
Ao conectar-se ao Wi-Fi, o nó anuncia sua presença ao Hub Central:
```http
GET /nodeHello?dev=biomass&ver=v11&mac=AA:BB:CC:DD:EE:FF HTTP/1.1
Host: 192.168.4.1
```
- `dev`: `biomass`
- `ver`: `v11`
- `mac`: Endereço MAC do ESP32-S3 do sensor.

---

## 2. Telemetria Periódica (`GET /biomassData`)

O nó envia periodicamente (conforme o intervalo configurado, ex. a cada 5000 ms) seus dados ópticos e estado operacional ao Hub:
```http
GET /biomassData?absorbance=0.452&raw=12450&it=100&pwm=25.0&hd_mode=0&ack_cmd_id=42&idle=0&gear=10&ema=0.800&probe_ms=5000 HTTP/1.1
Host: 192.168.4.1
```

Em repouso (`IDLE`), o nó envia pacotes de heartbeat com `idle=1` e `pwm=0.0` para manter o watchdog do Hub alimentado sem pulsar o LED desnecessariamente:
```http
GET /biomassData?absorbance=0.000&raw=0&it=25&pwm=0.0&hd_mode=0&ack_cmd_id=42&idle=1&gear=0&ema=0.800&probe_ms=5000 HTTP/1.1
Host: 192.168.4.1
```

### Campos do Push

| Parâmetro | Tipo | Unidade / Formato | Descrição |
| :--- | :--- | :--- | :--- |
| `absorbance` | `float` | AU (Absorbância / OD) | Valor calculado de absorbância: $A = -\log_{10}(I / I_0)$ |
| `raw` | `uint16` | contagens (0..65535) | Leitura digital do sensor após filtro mediano e EMA |
| `it` | `int` | ms | Tempo de integração ativo do VEML7700 (ex.: 25, 50, 100, 200, 400, 800) |
| `pwm` | `float` | % (0.0 a 100.0) | Nível de potência de emissão do LED |
| `hd_mode` | `int` | 0 ou 1 | `1` se em Modo de Alta Densidade (High Density Mode ativo) |
| `ack_cmd_id` | `uint32` | inteiro | ID do último comando recebido e aplicado com sucesso |
| `idle` | `int` | 0 ou 1 | `1` se nó em repouso (heartbeat); `0` durante medição contínua ativa |
| `gear` | `int` | 0 a 31 | Eco da marcha óptica combinada ativa: $\text{gear} = \text{IT}_{\text{idx}} \times 8 + \text{PWM}_{\text{idx}}$ |
| `ema` | `float` | 0.01 a 1.00 | Eco do coeficiente alfa do filtro passa-baixa exponencial (EMA) |
| `probe_ms` | `uint32` | ms | Eco do período de amostragem ativo entre pulsos de leitura |

---

## 3. Entrega Confiável de Comandos

O Hub responde à requisição `/biomassData` entregando o próximo comando pendente, ou o sensor consulta periodicamente `GET /biomassCommand`:

### Resposta do Hub com Comando
```json
{
  "cmd_id": 105,
  "command": "start"
}
```
ou ajuste de parâmetros:
```json
{
  "cmd_id": 106,
  "command": "set_gear",
  "value": 12
}
```

Quando o comando é processado com sucesso:
1. A ação é executada ou os parâmetros são atualizados e gravados na NVS.
2. O sensor armazena `g_lastAppliedHubCmdId = cmd_id`.
3. No próximo envio de `/biomassData`, o campo `ack_cmd_id=106` é emitido, confirmando a aplicação ao Hub.

---

## 4. Vocabulário de Comandos e Parâmetros

Comandos aceitos via Hub (`/biomassCommand` ou piggyback) ou localmente via `POST /command`:

### 4.1 Comandos de Operação (`command`)

| Valor de `command` | Descrição |
| :--- | :--- |
| `"start"` | Inicia o ciclo contínuo de medição óptica (`MEASURING`). Executa o *Smart Start* para escolher a melhor marcha e pulsa o LED a cada período. |
| `"stop"` | Interrompe medições contínuas, desliga o LED e retorna ao estado de repouso (`IDLE`). |
| `"blank"` | Executa a varredura completa da matriz de calibração de branco ($4 \times 8 = 32$ células) em meio limpo. |
| `"auto"` | Habilita auto-ranging contínuo (ajuste automático de marcha se sinal sair da faixa linear). |
| `"manual"` | Desabilita auto-ranging e trava a marcha óptica manual selecionada. |
| `"read_once"` | Executa uma leitura única de pulso no estado `IDLE` sem iniciar medição periódica contínua. |
| `"probe_period"`| Sem `value`: executa diagnóstico de conversão real de hardware. Com `value`: define o período de amostragem em ms. |
| `"set_gear"` | Seleciona a marcha óptica: aceita `{"gear": N}` ou `{"value": N}` ($0 \le N \le 31$), ou `{"it": i, "pwm": j}`. |
| `"set_pwm"` | Ajusta tabela de potências PWM: `{"index": 0..7, "value": 0..100}` ou `{"value": 0..100}` para a marcha atual. |
| `"set_it"` | Ajusta tabela de tempos de integração: `{"index": 0..3, "code": 0..5}` (ou `value`). |
| `"ema"` | Ajusta coeficiente do filtro EMA: `{"value": 0.01..1.00}` ou `{"ema": ...}`. |
| `"pwm_preset"` | Restaura a escada recomendada de potências PWM do LED. |
| `"led"` | Acendimento manual estático do LED para teste visual óptico: `{"duty": 0..100}`. |
| `"led_off"` | Desliga acendimento manual do LED. |
| `"hub_on"` / `"hub_off"` | Ativa ou desativa comunicação de rede com o Hub. |
| `"reset_health"` | Zera contadores de integridade (erros I2C, saturação, perdas de sincronismo de conversão). |
| `"factory"` | Restaura configurações de fábrica na memória NVS. |
| `"save_config"` / `"load_config"` | Persistência manual em NVS. |
| `"print_blank"` / `"print_config"` / `"print_health"` | Emite relatórios de diagnóstico na serial. |

### 4.2 Parâmetros Diretos (Propriedades JSON)

Parâmetros numéricos podem ser enviados em um objeto JSON sem o campo `command`:
- `refresh_ms`, `probe_ms`, `probe_period` (`long`, ms): Intervalo entre pulsos de leitura periódicos (mínimo respeita o limite térmico do LED).
- `ema` (`float`, 0.01 a 1.00): Coeficiente alfa de filtragem exponencial.
- `low` (`uint16`): Limiar inferior de contagens RAW para disparo de subida de marcha óptica.
- `high` (`uint16`): Limiar superior de contagens RAW para disparo de descida de marcha óptica.
- `opt` (`uint16`): Alvo ótimo de contagens no centro da escala linear do fotodetector.

---

## 5. Endpoints HTTP Locais do Nó

O nó executa um servidor Web na porta 80:

| Método | Rota | Descrição |
| :--- | :--- | :--- |
| `GET` | `/` | Painel Web HTML com gráficos em tempo real e controles |
| `GET` | `/readData` | Última leitura de absorbância e contagens (JSON) |
| `GET` | `/status` | Estado operacional, versão do firmware e integridade (JSON) |
| `GET` | `/diag` | Diagnóstico de conectividade, memória heap e métricas do nó (JSON) |
| `GET` | `/history` | Histórico dos últimos 1024 pontos de leitura em SRAM (JSON) |
| `GET` | `/blankTable`| Matriz completa de calibração de branco ($I_0$) por marcha (JSON) |
| `POST`| `/command` | Ingestão local de comandos JSON |
| `GET` | `/update` | Formulário Web para atualização de firmware OTA via navegador |
| `POST`| `/update` | Endpoint de recepção do binário de firmware compilado |

### Resposta de `GET /diag`
```json
{
  "device": "biomass-sensor",
  "version": "v11",
  "uptime_s": 7200,
  "free_heap": 248190,
  "wifi_status": 3,
  "ssid": "TECNAL-WIFI",
  "rssi": -52,
  "ip": "192.168.4.16",
  "mac": "34:85:18:XX:XX:XX",
  "hub_fail_streak": 0,
  "ota": false,
  "state": 2,
  "absorbance": 0.452,
  "raw": 12450
}
```

