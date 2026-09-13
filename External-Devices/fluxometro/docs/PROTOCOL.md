# Protocolo de Comunicação — Nó Fluxômetro

**Versão de Firmware:** `v12.0`
**Dispositivo:** `flowmeter` (ESP32)  
**Papel no Sistema:** Medição e controle de vazão de gases (ar/N₂) via sensor/controlador de fluxo mássico (MFC Omega FMA-5400) com DAC I2C MCP4725, ADC I2C ADS1115 e acionamento de válvulas solenoides.

---

## 1. Arquitetura de Comunicação

O nó fluxômetro opera em rede Wi-Fi associado ao SoftAP do Hub ESP32-S3 ou em rede local compartilhada. A troca de dados com o Hub é estritamente cliente-servidor HTTP em três vias:

1. **Anúncio de Presença (`/nodeHello`):** Notifica o Hub sobre identidade, versão (`v12.0`) e MAC a cada 30 segundos ou na recuperação de enlace.
2. **Push de Telemetria (`/flowData`):** Periódico a cada 500 ms (ou sob backoff exponencial em caso de falha), enviando leitura de processo, diagnóstico interno, ecos de sintonia, `cal_crc` e `hw_status`.
3. **Pull de Comandos (`/flowCommand`):** O nó consulta o Hub periodicamente buscando comandos pendentes sob máquina de confirmação `cmd_id` / `ack_cmd_id`.

```
  Fluxômetro (v12.0)                           Hub (10.3)
       │                                            │
       ├──── GET /nodeHello?dev=flowmeter&ver=v12.0>│ (Registro dinâmico)
       │                                            │
       ├──── GET /flowData?seconds=...&kp=... ─────>│ (Push de telemetria e ecos)
       │<─── 200 "Flowmeter data received" ─────────┤
       │                                            │
       ├──── GET /flowCommand ─────────────────────>│ (Pull de comando confiável)
       │<─── 200 {"cmd_id":N,"flow_setpoint":...} ──┤
       │                                            │
       ├──── GET /flowData?...&ack_cmd_id=N ───────>│ (Confirmação de aplicação)
```

---

## 2. Mensagens HTTP com o Hub

### 2.1 Anúncio de Presença (`GET /nodeHello`)
- **Frequência:** A cada 30 segundos ou quando `hubAnnounced == false`.
- **Formato da URL:**
  ```http
  GET /nodeHello?dev=flowmeter&ver=v12.0&mac=XX:XX:XX:XX:XX:XX
  ```

### 2.2 Push de Telemetria e Ecos (`GET /flowData`)
- **Frequência:** Padrão de 500 ms (ajustável; sob falhas, adota backoff exponencial de até 15 s).
- **Parâmetros da Query String:**
  | Parâmetro | Tipo | Exemplo | Descrição |
  |---|---|---|---|
  | `seconds` | float (%.3f) | `125.450` | Tempo de operação em segundos desde o boot |
  | `flow_voltage` | float (%.6f) | `1.652140` | Tensão bruta lida no ADC ADS1115 (V) |
  | `flow_rate` | float (%.6f) | `10.500000` | Vazão calibrada calculada (L/min) (-1.0 em falha) |
  | `flow_setpoint` | float (%.6f) | `10.000000` | Setpoint alvo ativo (L/min) |
  | `flow_setpoint_corrected`| float (%.6f) | `10.250000` | Setpoint após correção por feedforward |
  | `flow_output` | float (%.6f) | `10.320000` | Sinal final de atuação em L/min equivalente |
  | `ff_gain` | float (%.4f) | `0.8500` | Ganho aplicado do feedforward |
  | `ff_offset` | float (%.4f) | `-0.0500`| Offset aplicado do feedforward |
  | `valve1State` | uint8 (0/1) | `1` | Estado da válvula 1 (ar/N₂) |
  | `valve2State` | uint8 (0/1) | `0` | Estado da válvula 2 |
  | `valveFlowState` | uint8 (0/1) | `1` | Válvula principal de corte de fluxo |
  | `ack_cmd_id` | uint32 | `4` | Último `cmd_id` aplicado e consolidado |
  | `last_apply_ms` | uint32 | `125300` | Timestamp do millis() da última aplicação |
  | `command_source` | string | `hub` | Origem do último comando (`hub`, `serial`, `web`) |
  | `boot_id` | uint32 | `3829104` | ID exclusivo da sessão de boot (detecta reinicialização) |
  | `reconnect_wifi`| int (0/1) | `1` | Estado da flag de persistência de reconexão |
  | `kp` *(novo v11)* | float (%.4f) | `0.1000` | Ganho proporcional PI ativo |
  | `ki` *(novo v11)* | float (%.4f) | `0.0500` | Ganho integral PI ativo |
  | `ramp` *(novo v11)* | float (%.3f) | `3.000` | Taxa da rampa de aceleração (L/min/s) |
  | `cal_crc` *(v11.0)* | hex uint32 | `A1B2C3D4` | CRC32 dos coeficientes de calibração em EEPROM |
  | `transition_v` *(v12.0)* | float (%.4f) | `0.0545` | Tensão de transição vigente; é tensão em volts, não vazão |
  | `hw_status` *(v11.0)* | uint8 | `7` | Bitmask de saúde de hardware (bit 0=ADS, 1=DAC, 2=NoLatch) |

---

## 3. Comandos Aceitos pelo Nó (`GET /flowCommand`)

O nó faz poll em `/flowCommand`. O corpo JSON de resposta do Hub pode conter uma ou mais das seguintes chaves:

| Chave JSON | Tipo | Faixa / Padrão | Descrição |
|---|---|---|---|
| `cmd_id` | uint32 | `1 .. 4294967295` | Identificador único de revisão do comando |
| `flow_setpoint` | float | `0.0 .. max_flow` | Novo setpoint alvo de vazão (L/min) |
| `v1` | int (0/1) | `0 ou 1` | Acionamento da solenoide de gás 1 |
| `v2` | int (0/1) | `0 ou 1` | Acionamento da solenoide de gás 2 |
| `v_Flow` | int (0/1) | `0 ou 1` | Comando de corte geral (Valve OFF): 1 = corte ativo / linha fechada (GPIO 5 HIGH), 0 = fluxo liberado / válvula aberta (GPIO 5 LOW) |
| `kp_flow` | float | `>= 0.0` (padrão 0.1) | Ganho proporcional do controlador PI |
| `ki_flow` | float | `>= 0.0` (padrão 0.1) | Ganho integral do controlador PI |
| `ff_gain` | float | qualquer (padrão 0.85)| Ganho da compensação feedforward |
| `ff_offset` | float | qualquer (padrão -0.05)| Offset da compensação feedforward |
| `ramp_rate` | float | `>= 0.0` (0 = degrau) | Taxa máxima de variação do setpoint (L/min/s) |
| `dac_hold` | int (0/1) | `1` | Se 1, mantém valor do DAC através de setpoint zero |
| `max_flow` / `maxFlow` | float | `> 0.01` (padrão 50.0)| Fundo de escala do sensor |
| `a1`, `b1`, `k1`, `f1`, `c1` | float | coeficientes | Curva baixa de calibração (polinômio quártico ancorado) |
| `k2`, `f2`, `c2` | float | coeficientes | Curva alta de calibração (polinômio quadrático) |
| `transition_v` / `flowTransitionVoltage` | float | `0 < Vt < 3.3` | Tensão de transição em volts; exige os dois segmentos completos no mesmo quadro |
| `reconnect_wifi` | int (0/1) | `1` | Habilita/desabilita laço de reconexão Wi-Fi |

### 3.1 Comportamento de Aplicação e Persistência
- **Sintonia e Calibração:** Quando `kp_flow`, `ki_flow`, `ff_gain`, `ff_offset`, `ramp_rate` ou uma curva completa com `transition_v` são recebidos, eles são gravados na EEPROM através de `saveCalibration()`.
- **Aplicação atômica v12.0:** um quadro que altera `transition_v` deve conter os dois segmentos completos. O parser rejeita curva com descontinuidade de valor ou derivada acima dos limites do firmware; não há aplicação parcial.
- **Setpoints Dinâmicos:** `flow_setpoint` e comandos de válvula são aplicados imediatamente em RAM e não desgastam a memória flash.
- **Liberação Lógica de Rotas (F06):** O hardware pneumático não impõe restrição física de rota; qualquer combinação lógica de solenoides ($V_1$, $V_2$, $V_\text{Flow}$) é executada fielmente pelo firmware e repassada pelo Hub. A governança de rotas e segurança de processo residem no Windows App.
- **Confirmação:** O nó registra `snapAck = lastAppliedHubCommandId = cmd_id`. No próximo push de `/flowData`, o parâmetro `&ack_cmd_id=<cmd_id>` fecha a transação no Hub.

---

## 4. Endpoints Locais HTTP (Diagnóstico e OTA)

O nó executa um servidor HTTP local (`ESPAsyncWebServer`) com os endpoints (SoftAP e endpoints operam abertos por decisão de projeto):

### 4.1 `GET /diag` e `GET /status`
Devolve status de saúde e conectividade em JSON:
```json
{
  "device": "flowmeter",
  "version": "v12.0",
  "uptime_s": 1420,
  "free_heap": 172400,
  "wifi_status": 3,
  "ssid": "TECNAL_HUB_AP",
  "rssi": -55,
  "ip": "192.168.4.5",
  "mac": "AA:BB:CC:DD:EE:FF",
  "hub_fail_streak": 0,
  "ota": false,
  "flow_rate": 12.4500,
  "flow_sp": 12.5000
}
```

### 4.2 `GET /update` e `POST /update`
Interface web HTML para upload de firmware compilado via OTA. Antes de iniciar a gravação do binário (`index == 0`), o firmware realiza parada segura atômica sob mutex (corte fechado, rotas desenergizadas, DAC em zero). Em caso de stall da rede por mais de 90 segundos, a trava `otaSafeLatch` é armada para evitar acionamentos acidentais.

### 4.3 `GET /calibration`
Endpoint JSON de auditoria que retorna todos os coeficientes polinomiais ativos em EEPROM, fundo de escala e hash CRC32:
```json
{
  "a1": -1.353785e+06,
  "b1": 2.466637e+05,
  "k1": -1.647349e+04,
  "f1": 4.849947e+02,
  "c1": -4.615946e+00,
  "k2": -4.626046e-01,
  "f2": 1.079730e+01,
  "c2": 2.847579e-01,
  "max_flow": 50.00,
  "transition_v": 0.0545,
  "crc": "A1B2C3D4"
}
```

---

## 5. Persistência EEPROM (Schema v7)

A partir da versão `v12.0`, o nó adota o Schema v7 (`CALIBRATION_MAGIC = 0xCAFEBAC5`, 68 bytes), acrescentando `transition_v` ao final do registro e ao CRC32.
- **Migração transparente:** schemas v5 e v6 preservam coeficientes, PI, feedforward, rampa e `max_flow`; o novo campo recebe o default legado `0.0545 V`. Esse número não é vazão nem limiar imutável: é apenas o valor inicial/migrado.
- **Fail-Safe Pneumático:** A função de quantização do DAC é protegida contra divisão por zero e normalizada estritamente em ponto flutuante $[0.0, 1.0]$.
