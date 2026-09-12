# Plano — Expor no Hub e no app o que os nós externos passaram a oferecer (offset do sensor de distância, ganhos do fluxômetro, calibração da bomba, ajustes da biomassa, saúde dos nós)

**Data:** 2026-09-12
**Escopo:** auditoria dos cinco firmwares de `External-Devices/` (reorganização de 11–12/09/2026:
parsers zero-heap, Web OTA, `/diag`, backoff, Link Watchdog, `/nodeHello`, NVS no sensor de
distância) contra o que o Hub `10.1.0-dev` repassa e o que o app `OpenTECHub` expõe; e o plano
para fechar as lacunas, no Hub (`10.2.0-dev`) e no app, sem quebrar o fio congelado.
**Estado:** proposto. Depende do recibo de bancada do plano anterior
(`2026-09-12-plano-identidade-nos-externos-app.md`, §6.3) só para o §4.D (saúde dos nós), que
reaproveita o registro de IPs; o resto é independente.
**Pré-leitura:** `docs/PROTOCOL.md` §2.0.1, §2.0.2, §3.4, §3.5; `docs/DECISIONS.md` D-015 (árbitro
único do fio), D-021/D-022 (biomassa/bomba), D-051; `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`;
`External-Devices/docs/HUB_PROTOCOL_IMPROVEMENTS.md`; o `PROTOCOL.md` de cada nó em
`External-Devices/<nó>/docs/`.

---

## 0. Como executar este plano em outra sessão

1. **Ponto de partida.** Ler este arquivo e a pré-leitura. Baselines: `dotnet test
   Windows_app/OpenTECHub.slnx` (1448 aprovados em 12/09/2026); Hub: `python -m unittest discover
   tests/contracts` em `ESP32S3-HUB/` (38) e compilação com o `arduino-cli` embarcado (§6.1); nós:
   `External-Devices/tools/Compile-ExternalDevices.ps1`, `Test-FirmwareBaselines.ps1`,
   `Test-HubDeviceContracts.ps1`, `Test-AgitatorManualParser.ps1`.
2. **Ordem** (uma etapa = um commit com testes): §4.A (Hub: whitelists + eco no quadro) → §4.B
   (nós: distância piggyback, fluxômetro `kp/ki`, bomba e biomassa sem mudança) → §4.C (app:
   fio + builders) → §4.D (app: UI por dispositivo) → §4.E (Hub: proxy `/nodeDiag`) → §4.F (app:
   saúde dos nós) → §4.G (docs). A/B antes de C porque os golden strings do app precisam do quadro
   real; o simulador sustenta C–D enquanto o Hub não é gravado.
3. **Regras.** Fio do ESP32 congelado: **tudo aditivo** — chaves novas no comando e no quadro,
   nenhuma renomeada, `HubProtocolVersion` continua 10. Todo comando ao nó passa pelo Hub e pelo
   `CommandArbiter` (D-015); o app **não** fala direto com os nós para comandar (§3). Um nó que não
   ecoa o valor aplicado não recebe controle no app sem eco — três estados, sempre: pedido, roteado,
   aplicado. `InvariantCulture` no fio; código em inglês, UI em pt-BR.
4. **Docs ao fim:** `PROTOCOL.md` (§2.0.3 ecos, §3.7 comandos por nó), `WIRE_CONTRACT_V9.md`,
   `External-Devices/<nó>/docs/PROTOCOL.md` (que hoje só apontam para a matriz transversal),
   `DECISIONS.md` (D-052), `PHASE_LOG.md` (P3-10), `CHANGELOG.md`, `ROADMAP.md`, `DocumentationCatalog`
   (gavetas de Controle e Calibrações), `MANUAL_DO_OPERADOR.md`.

---

## 1. Inventário: o que mudou nos nós, o que o Hub repassa, o que o app expõe

Fonte: diff do vocabulário de fio (chaves JSON, rotas, parâmetros de query) entre
`archive/active-baseline/*.ino` e `firmware/*/src/**` de cada nó, mais leitura dos parsers de
comando (`CommandCodec`/`OperationController`/`ConfigCodec`) e dos handlers do Hub
(`src/protocol/Commands.h`, `src/protocol/Mailboxes.h`, `src/network/HttpServer.h`).

### 1.1 Comum aos cinco nós (novo na reorganização)

| Capacidade | Nó | Hub repassa? | App expõe? | Veredito |
|---|---|---|---|---|
| `GET /nodeHello?dev&ver&mac` (auto-registro, re-anúncio a cada 30 s) | todos | **sim** — `NodeRegistry`, `*IP`/`*NodeVer`/`*NodeMac` no quadro, `/nodes` | **sim** — D-051 | fechado em 12/09 |
| `GET /diag` e `/status` (JSON: `device, version, uptime_s, free_heap, wifi_status, ssid, rssi, ip, mac, hub_fail_streak, ota` + métricas do nó) | todos | **não** — só alcançável direto do PC em Wi-Fi | parcial — botão *Abrir diagnóstico* abre no navegador; nada é lido | **§4.E/§4.F**: proxy no Hub + "Saúde" na tabela de nós |
| `POST /update` (Web OTA, com intertravamento de segurança) | todos | não (não faz sentido) | não — `Publish-OtaFirmware.ps1` cobre, com baseline e headroom | diferido (ROADMAP) |
| Backoff exponencial, Task WDT 15 s, Link Watchdog (`g_hubFailStreak ≥ 8` → reassocia), canal 6 | todos | n/a — comportamento interno; `hub_fail_streak` só em `/diag` | indiretamente — reassociação vira "IP mudou"/"registrado" em Eventos (D-051) | `hub_fail_streak` entra com o §4.E |
| `boot_id` no push | fluxômetro (já); os outros não | Hub lê `boot_id` só do `/flowData` e **não publica** | não | **§4.A**: `FlowmeterBootId` no quadro; nós restantes ficam para depois (`HUB_PROTOCOL_IMPROVEMENTS` §"compatíveis" 2) |

### 1.2 Sensor de distância (`sensor-distancia`, `v10`) — **o caso do exemplo**

| Item | Nó | Hub | App |
|---|---|---|---|
| Push `GET /distance?distance=<mm int>&time=<s>` | igual ao baseline | lê `distance`, `time`; presença `DistanceOnline`; `Distance` no quadro | `Distance` (mm), presença, roteamento (`distanceSensorComm`) |
| **NVS `dist_cfg`** (novo): `offset_mm` (padrão 20,0; `distance = mm − offset`), `sample_period`/`send_period` (ms), `cooldown_soft/bus/xshut`, limiares L1/L2/L3, `reset_nvs` | **`GET /config` e `POST /config` locais** + serial; `offset_mm` ecoado em `/config`, `/diag`, `/status` | **nenhuma rota de comando para o nó** — o Hub só recebe o push; `distanceSensorReference`, `foamStartDelay_s`, `foamPulse_s`, `foamInterval_s` são parâmetros do controle de espuma **dentro do Hub**, não do sensor | *Controle › Distância* mostra Atraso inicial/Pulso/Intervalo (do Hub) e a leitura. **Nenhum campo de offset, período ou recuperação; nenhum eco** |
| O nó **já lê o corpo da resposta** do seu push (`httpGet(url, code, body)`) e o descarta | — | responde `200 "Distance data received"` | — |

**Lacuna:** o offset é ajustável só por `curl -X POST http://<ip>/config` com o PC no Wi-Fi do Hub.
O Hub está defasado por construção: o nó nunca teve canal de comando.

### 1.3 Fluxômetro (`fluxometro`, `v10`)

| Item | Nó | Hub | App |
|---|---|---|---|
| Comandos aceitos (`CommandCodec.h`) | `flowSetpoint`/`flow_setpoint`, `maxFlow`/`max_flow`, `valve_1`/`v1`, `valve_2`/`v2`, `v_Flow`/`valveFlow`, `reconnect_wifi`, calibração `a1 b1 k1 f1 c1 k2 f2 c2`, `quadratic`, **`kp_flow`, `ki_flow`, `ff_gain`, `ff_offset`, `ramp_rate`**, `dac_hold`, `debug_pi`, `direct`/`direct_cmd_id`/`direct_session_id` | `queueReliableFlowCommandFromJson` repassa **só** `flowSetpoint maxFlow reconnectWifi v_Flow valve_1 valve_2`; a curva `a1…c2` por caminho próprio (10.0.1) | setpoint, válvulas, maxFlow, calibração da curva (Calibrações) |
| Push `GET /flowData?…` | `seconds flow_voltage flow_rate flow_setpoint flow_setpoint_corrected flow_output ff_gain ff_offset valve1State valve2State valveFlowState ack_cmd_id last_apply_ms command_source boot_id reconnect_wifi` | lê `seconds flow_voltage flow_rate flow_setpoint valve*State ack_cmd_id command_source boot_id reconnect_wifi`; **ignora `flow_setpoint_corrected`, `flow_output`, `ff_gain`, `ff_offset`, `last_apply_ms`** e não publica `boot_id` | `FlowRate`, `FlowSetpoint`, `FlowVoltage`, válvulas, `FlowCommandSource`, `FlowmeterReconnectWifi` |

**Lacuna:** os ganhos do PI (`kp_flow`, `ki_flow`), o feed-forward (`ff_gain`, `ff_offset`) e a rampa
(`ramp_rate`) — exatamente o que a pendência de bancada "controlador de vazão não regula para
baixo na primeira abertura do alívio" (`ROADMAP` › Field readiness, §I.4 do plano de 11/09) precisa
para ser investigada — não passam pelo Hub, e o nó **já ecoa** `ff_gain`/`ff_offset`/`flow_output`
que o Hub descarta.

### 1.4 Bomba peristáltica (`bomba-peristaltica`, `3.8`)

| Item | Nó | Hub | App |
|---|---|---|---|
| Comandos aceitos (`OperationController.h`) | `command` ∈ {`start`, `stop`, **`reset_volume`**, `save_config`, `load_config`, `clear_nvs`, `print_config`}; `mode`, `speed`, `init_t`, `final_t`, `lambda_*`, `phi_*`, `p0…p20`, `num_segments`, `t0…/q0…`; **`pumpSlope`, `pumpIntercept`** (calibração mL/min ↔ PWM); **`pid_kp`, `pid_ki`, `pid_kd`**; **`sensorEnable`, `sensorBypass`, `sensorButtonOverride`, `disablePot`** | whitelist `pump_command→command`, `mode`, **`pump_speed→speed`**, `init_t`, `final_t`, `lambda_*`, `phi_*`, `p0…p20`, `t/q`, `num_segments` — **nada de `pumpSlope/Intercept`, `pid_*`, `sensor*`, `disablePot`** | cinco perfis, janela, `pumpComm`; **sem `pump_command`** (não há tecla `PumpCommand` em `CommandKeys`), sem calibração, sem PID, sem sensor |
| Push `GET /pumpData?mode pwm speed flow vol v_tgt active waiting ack_cmd_id` | igual | lê tudo | `PumpMode/PWM/Speed/Flow/Vol/TargetVol/Active/Waiting` |
| `speed` | o nó lê `speed` | o Hub só repassa **`pump_speed`** (e renomeia); o `"speed"` que o app manda no quadro de segurança `{"mode":0,"speed":0}` é **descartado** pelo Hub | `PROTOCOL.md` §3.5 já trata `speed` como vestigial |

**Lacunas:** zerar o volume acumulado (`reset_volume`) não é alcançável pelo app; calibração
`pumpSlope`/`pumpIntercept` e PID só via `POST /command` no nó; o `speed` descartado é uma
armadilha documental (o app acha que envia; o Hub joga fora).

### 1.5 Sensor de biomassa (`sensor-biomassa`, `v10`)

| Item | Nó | Hub | App |
|---|---|---|---|
| Comandos aceitos (`CommandCodec.h`) | `start`, `stop`, `blank`, `low`, `high`, `opt`, `test_period`, `test_on/off`, `read_once`, **`set_it`/`it`, `set_pwm`/`pwm`/`pwm_preset`, `set_gear`, `ema`, `probe_period`, `refresh_ms`, `duty`/`duty_pct`, `led`/`led_off`, `auto`/`manual`, `hub_on`/`hub_off`**, `save_config`/`load_config`/`factory`, `clear_history`, `print_*`, `reset_health` | whitelist `start stop blank low high opt test_period` | `start`, `stop`, `blank`, `low`, `high`, `opt` (D-021); `test_period` não |
| Push `GET /biomassData?absorbance raw it pwm idle ack_cmd_id` | igual | lê tudo | `BiomassAbs/Raw/IT/PWM` (readback) |

**Lacuna:** tempo de integração, PWM do emissor, ganho, EMA e período de sonda são ajustáveis só no
nó (UI web própria ou `POST`). O app mostra IT/PWM como leitura e não pode alterá-los.

### 1.6 Frasco agitador (`frasco-agitador`, `v10`)

| Item | Nó | Hub | App |
|---|---|---|---|
| Comandos | `RPM_percent`, `Dir`, `ActivePot`, `cmd_id` | envia exatamente estes (`agitatorPercent`/`agitatorDir`/`agitatorOn`/`agitatorReEnablePot`/`agitatorAuto` → mailbox) | intensidade, sentido, reativar potenciômetro, automação por espuma |
| Push `GET /agitatorData?pct dir pot src secs ack_cmd_id` | igual | lê `pct dir pot src ack_cmd_id` (ignora `secs`, sem consequência) | `AgitatorPercent/Dir/PotActive/Source` |
| `/diag` | `duty`, `dir`, `pot`, `time_s` + comuns | — | — |

**Sem lacuna funcional.** Só entra no §4.E/§4.F (saúde).

### 1.7 Hub — correções já feitas nesta rodada (12/09)

- Leitura de pH/O₂/pressão/antiespumante **desacoplada** dos flags de malha (`Telemetry.h`,
  `sendSensorCommand(..., true)` incondicional; flags governam atuação) — corrige o `-1.0`/"--" no
  app Android com malha desligada. Aditivo; sem mudança de chave.
- Identidade dos nós no quadro e `/nodes` completo (`10.1.0-dev`, D-051).

---

## 2. Resumo das lacunas ("o Hub está defasado" — onde, exatamente)

| # | Lacuna | Onde | Prioridade |
|---|---|---|---|
| L1 | Offset (e períodos/recuperação) do sensor de distância só via `POST /config` no nó; sem canal pelo Hub; sem eco no quadro | nó (canal), Hub (mailbox + eco), app (UI) | **P1** — pedido do operador |
| L2 | Ganhos PI/FF/rampa do fluxômetro não passam pelo Hub; `ff_gain`/`ff_offset`/`flow_output`/`flow_setpoint_corrected` já ecoados e descartados | Hub (whitelist + eco), app (UI de ajuste + leitura) | **P1** — ligado à pendência §I.4 de bancada |
| L3 | `reset_volume` da bomba inalcançável; calibração `pumpSlope/Intercept` e PID só no nó; `speed` do app descartado pelo Hub | Hub (whitelist + eco de calibração), nó (eco), app (`PumpCommand`, Calibrações › Bomba externa) | **P2** |
| L4 | IT/PWM/ganho/EMA/período da biomassa só no nó | Hub (whitelist), app (drawer Absorbância) | **P2** |
| L5 | `/diag` dos nós invisível ao app (e a quem está em USB) | Hub (proxy com tarefa própria), app (Saúde na tabela de nós) | **P3** |
| L6 | `boot_id` do fluxômetro lido e não publicado; os outros nós não o enviam | Hub (quadro), nós (depois) | **P3** |
| L7 | `PROTOCOL.md` dos nós só remete à matriz transversal; nenhum descreve seu próprio vocabulário | docs | junto com cada etapa |

---

## 3. Decisões de design

1. **Tudo pelo Hub, nada direto ao nó para comandar.** O app conhece o IP de cada nó desde D-051 e
   poderia fazer `POST http://<ip>/config`; **não faz**. Comandar direto bypassa o `CommandArbiter`
   (D-015), o jornal de comandos, o `cmd_id`/`ack_cmd_id` e deixa quem está em USB sem a função. O
   caminho é o mesmo dos outros nós: mailbox no Hub, `takeReliable`/`ackReliable`, eco no quadro.
   Exceção mantida: *Abrir diagnóstico* (leitura, no navegador).
2. **Chaves planas com prefixo de dispositivo no comando do app → Hub**, como já se faz
   (`pump_speed`→`speed`): `distanceOffsetMm`, `distanceSamplePeriodMs`, `distanceSendPeriodMs`,
   `distanceResetNvs`; `flowKp`, `flowKi`, `flowFfGain`, `flowFfOffset`, `flowRampRate`;
   `pump_command` (já existe no Hub, falta no app), `pumpSlope`, `pumpIntercept`, `pumpPidKp/Ki/Kd`;
   `biomassIt`, `biomassPwm`, `biomassGear`, `biomassEma`, `biomassProbePeriodMs`. O Hub traduz para
   o nome que o nó entende; a tradução fica documentada e testada (`test_json_keys.py`).
3. **Eco obrigatório no quadro para tudo que vira controle.** O nó inclui o valor aplicado no seu
   push; o Hub publica `DistanceOffsetMm`, `DistanceSamplePeriodMs`, `DistanceSendPeriodMs`;
   `FlowKp`, `FlowKi`, `FlowFfGain`, `FlowFfOffset`, `FlowRampRate`, `FlowOutput`,
   `FlowSetpointCorrected`; `PumpSlope`, `PumpIntercept`; `BiomassGear`, `BiomassEma`,
   `BiomassProbePeriodMs` (IT/PWM já são ecoados). O app mostra *pedido* × *aplicado* como já faz
   com as válvulas (`Telemetria: {0}`); sem eco não há campo editável.
4. **Canal do sensor de distância por carona na resposta do push** (piggyback). O nó já faz um
   `GET /distance` por segundo e já lê o corpo da resposta; o Hub passa a responder
   `{"cmd_id":N,"offset_mm":25.5,…}` quando há comando pendente na `distanceBox`, o nó chama o
   `processConfigUpdate(body)` que já existe e confirma com `&ack_cmd_id=N` no push seguinte. Sem um
   novo laço de *poll* no nó, sem nova rota. (Alternativa uniforme — rota `GET /distanceCommand`
   com *poll* de 2 s como a bomba — custa mais no nó e não traz nada; registrar como rejeitada.)
5. **Proxy de diagnóstico no Hub com tarefa própria.** `GET /nodeDiag?dev=` **não** faz HTTP dentro
   do handler do `AsyncWebServer` (bloquearia a tarefa de rede). Uma tarefa FreeRTOS de baixa
   prioridade percorre os nós registrados a cada 30 s, faz `GET http://<ip>/diag` com timeout de
   500 ms, guarda o corpo (≤ 512 B) e a idade; o handler só serve o cache. O quadro agregado **não**
   carrega isso (custa bytes na USB); em USB o app pede ao Hub por comando serial
   `{"nodeDiag":"pump"}` e o Hub responde uma linha `{"NodeDiag":{…}}` — o parser trata a linha como
   não-telemetria (§2.0 do PROTOCOL) e a entrega ao painel.
6. **Versões:** Hub `10.2.0-dev` (aditivo; protocolo 10). Nós: distância `v11` (piggyback +
   `ack_cmd_id` + eco no push), fluxômetro `v11` (eco de `kp/ki/ramp` no push; já ecoa `ff_*`),
   bomba `3.9` (eco de `slope/intercept`), biomassa `v11` (eco de `gear/ema/probe_period`).
   `NodeFirmwareCatalog` do app passa a listar as duas versões de cada nó (a atual continua válida:
   sem eco, o app só não mostra os campos).

---

## 4. Alterações por componente

### 4.A Hub `10.2.0-dev` (`ESP32S3-HUB/ESP32S3-HUB`)

1. **`AppContext.h`:** `ReliableBox distanceBox`; variáveis de eco (`distanceOffsetMm`,
   `distanceSamplePeriodMs`, `distanceSendPeriodMs`, `flowKp`, `flowKi`, `flowFfGain`,
   `flowFfOffset`, `flowRampRate`, `flowOutput`, `flowSetpointCorrected`, `pumpSlope`,
   `pumpIntercept`, `biomassGear`, `biomassEma`, `biomassProbePeriodMs`, `flowmeterBootId`),
   sentinela `NaN`/`-1` = nunca ecoado.
2. **`Commands.h`:** blocos de repasse por nó, cada um com a tradução app→nó e a mesma regra
   `if (found && <dev>CommOn)`:
   - distância: `distanceOffsetMm→offset_mm`, `distanceSamplePeriodMs→sample_period`,
     `distanceSendPeriodMs→send_period`, `distanceResetNvs→reset_nvs` → `queueReliable(distanceBox)`;
   - fluxômetro: `flowKp→kp_flow`, `flowKi→ki_flow`, `flowFfGain→ff_gain`, `flowFfOffset→ff_offset`,
     `flowRampRate→ramp_rate` na `queueReliableFlowCommandFromJson`;
   - bomba: `pumpSlope`, `pumpIntercept`, `pumpPidKp→pid_kp`, `pumpPidKi→pid_ki`, `pumpPidKd→pid_kd`
     nas `simpleKeys`; **`speed`** entra na lista (o app já o manda; hoje é descartado) — decidir
     entre repassar ou remover do app (§7);
   - biomassa: `biomassIt→{"command":"set_it","value":…}`, `biomassPwm→set_pwm`, `biomassGear→set_gear`,
     `biomassEma→ema`, `biomassProbePeriodMs→probe_period` — o `CommandCodec` do nó usa
     `command`+`value`, então o Hub monta esse par.
3. **`HttpServer.h`:** `/distance` responde o payload da `distanceBox` quando há comando pendente
   (senão o `"Distance data received"` de sempre) e lê `ack_cmd_id`, `offset`, `sample_ms`,
   `send_ms` do push; `/flowData` passa a ler `ff_gain`, `ff_offset`, `flow_output`,
   `flow_setpoint_corrected`, `kp`, `ki`, `ramp` (novos no nó), e guarda `boot_id`; `/pumpData` lê
   `slope`, `intercept`; `/biomassData` lê `gear`, `ema`, `probe_ms`.
4. **`Telemetry.h`:** as chaves de eco da decisão 3, condicionais a "já ecoado" (mesmo padrão
   dos `Servo*` de amostra), + `FlowmeterBootId`; `DistanceCommandPending`. Estimativa +200 B com
   tudo ecoado; a reserva de 3072 aguenta; medir.
5. **Contrato/testes:** `tests/contracts/test_json_keys.py` (traduções app→nó, cada chave nova),
   `test_node_commands.py` (novo: modelo do piggyback da distância — pendente → resposta com
   `cmd_id` → `ack_cmd_id` limpa; e das whitelists), `Test-HubDeviceContracts.ps1` (rotas/params
   novos). `WIRE_CONTRACT_V9.md`: seções "Comandos por nó (tradução)" e "Ecos por nó".

### 4.B Nós (`External-Devices/`)

| Nó | Mudança | Arquivos |
|---|---|---|
| **Distância `v11`** | Ao receber `2xx` no push, se o corpo começa com `{` chama `processConfigUpdate(body)` e guarda `cmd_id` para o `&ack_cmd_id=` do próximo push; push passa a incluir `&offset=<%.2f>&sample_ms=&send_ms=`; `ver=v11` no hello; `/diag` inalterado | `src/core/FirmwareApp.cpp`, `src/protocol/ConfigCodec.cpp` (já persiste em NVS), `src/network/NetworkManager.cpp` |
| **Fluxômetro `v11`** | Push inclui `&kp=&ki=&ramp=` (já inclui `ff_gain`/`ff_offset`); nada mais — o nó já aceita os comandos | `src/tasks/TaskRuntime.h` |
| **Bomba `3.9`** | Push inclui `&slope=&intercept=`; nada mais | `src/network/HubClient.h` |
| **Biomassa `v11`** | Push inclui `&gear=&ema=&probe_ms=`; nada mais | `src/protocol/TelemetryAndHub.h` |
| Agitador | nenhuma | — |

Cada nó: `Compile-ExternalDevices.ps1` (headroom > 160 KB), baseline SHA intacto (o baseline é o
monólito arquivado, não muda), `Test-HubDeviceContracts.ps1`, e o `docs/PROTOCOL.md` do nó
**deixa de remeter só à matriz** e passa a listar o seu vocabulário (comandos aceitos, push,
`/diag`, `/config`), com a versão.

### 4.C App — camada de fio (`OpenTECHub.Protocol`)

1. `CommandKeys`: as chaves planas da decisão 2 (`DistanceOffsetMm`, …, `PumpCommand =
   "pump_command"`, `PumpSlope`, …, `BiomassIt`, …). `CommandBuilders`: `DistanceConfig(offsetMm,
   samplePeriodMs?, sendPeriodMs?)`, `DistanceResetNvs()`, `FlowTuning(kp, ki, ffGain, ffOffset,
   rampRate)`, `PumpResetVolume()` (`{"pump_command":"reset_volume"}`), `PumpCalibration(slope,
   intercept)`, `PumpPid(kp, ki, kd)`, `BiomassTuning(it?, pwm?, gear?, ema?, probePeriodMs?)`.
   Golden strings em `WireFormatTests`, com o teste em `pt-BR`.
2. `TelemetryKeys` + `SensorReadings/Snapshot` + parser: os ecos da decisão 3 como `double?`/`int?`
   (null = nunca ecoado; **não** sticky — um Hub que parou de ecoar é um nó que sumiu),
   `FlowmeterBootId` (`long?`, sticky) e `DistanceCommandPending` (`bool?`, como os outros).
3. `NodeFirmwareCatalog`: `distance {v10, v11}`, `flowmeter {v10, v05, v11}`, `pump {3.8, 3.9}`,
   `biomass {v10, v11}`.
4. Simulador: `DeviceModel` guarda os valores aplicados, `WireCodec` ecoa, `HttpEndpoint`/`SerialEndpoint`
   aplicam os comandos novos; `/distance` piggyback não é simulado (é Hub↔nó).

### 4.D App — interface por dispositivo (`ViewModels/`, `Views/ControlView.xaml`, Calibrações)

| Onde | O que | Regras |
|---|---|---|
| **Controle › Distância** (`FoamControlViewModel`) | Campo **Offset (mm)** com `Telemetria: {DistanceOffsetMm}`; expander *Avançado*: Período de amostragem (ms), Período de envio (ms), botão *Restaurar padrões do nó* (`reset_nvs`, confirmação destrutiva) | Editável só com eco presente (`DistanceOffsetMm != null`) — senão o campo mostra "nó v10 não ecoa" no tooltip; despacho pelo `ManualDispatcher`, `Status.MarkCommandDispatched()`, pendência por `DistanceCommandPending` |
| **Controle › Vazão de Ar** (`FlowControlViewModel`) | Expander *Sintonia do controlador*: Kp, Ki, FF ganho, FF offset, Rampa, cada um com `Telemetria: {0}`; leitura `Saída (V)` (`FlowOutput`) e `Setpoint corrigido` (`FlowSetpointCorrected`) | Mesma regra de eco; **recusado** enquanto um ensaio (kLa/Potência) detém a aeração — o árbitro já faz isso |
| **Controle › Bomba Externa** (`PumpControlViewModel`) | Botão **Zerar volume** (`reset_volume`), expander *PID* (Kp/Ki/Kd com eco quando o nó ecoar) | `Zerar volume` só com `PumpOnline`; não zera o `PumpVol` local antes do eco |
| **Calibrações › Bomba externa** (novo em `CalibrationViewModel`) | `pumpSlope`/`pumpIntercept` com prévia `mL/min = slope·PWM + intercept`, *Aplicar no nó*, recibo em `Calibracoes/` como as outras | Mesmo padrão do fluxômetro (`FlowCalibrationViewModel`) |
| **Controle › Absorbância** (`BiomassControlViewModel`) | Expander *Aquisição*: IT, PWM, Ganho, EMA, Período de sonda, cada um com `Telemetria: {0}` (IT/PWM já são lidos) | Mesma regra de eco |
| `ExternalDeviceStatus` | nada novo — `IsAwaitingAck` já cobre | — |

Testes: `DosingAuxiliariesTests`/`ExternalDeviceTests`/`BiomassPumpTests`/`FlowmeterV05SyncTests`
(despacho e frames), `CalibrationTests` (bomba), `CompactLayoutTests` (expanders em 936 × 534),
`ControlWorkspaceContractTests` (rótulos exatos).

### 4.E Hub — proxy de diagnóstico (`/nodeDiag`, serial `{"nodeDiag":…}`)

Tarefa `nodeDiagTask` (prio 1, stack 6 KB): a cada 30 s, para cada nó com `registered && ip != 0`,
`HTTPClient` com `setTimeout(500)` em `http://<ip>/diag`; guarda `char body[512]`, `fetchedMs`,
`httpCode` em `g_nodeDiag[DEV_COUNT]` sob `stateMutex`. `GET /nodeDiag?dev=` serve
`{"dev":…,"age_ms":…,"code":…,"diag":<body>}`; sem `dev`, os cinco. Serial: `{"nodeDiag":"pump"}` →
uma linha `{"NodeDiag":{"dev":"pump",…}}`. Medir heap antes/depois (5 × 512 B + task). Contrato:
`test_node_diag.py` (cache, idade, nó sem IP → `code 0`).

### 4.F App — saúde dos nós

`HubNodeDiagClient` (Protocol; Wi-Fi: `GET /nodeDiag`; USB: comando serial + linha `NodeDiag`
capturada pelo `ConnectionManager` como não-telemetria e entregue por evento). `HubNodesViewModel`
ganha colunas **RSSI · Heap · Uptime · Falhas c/ Hub · OTA** e o rodapé "Diagnóstico via Hub há N s".
`NodeFirmwareCatalog` mapeia as métricas específicas (`distance_mm`, `current_rpm`, `flow_rate`,
`od_estimated`, `dispensed_ml`) para uma linha de texto por nó. Alarme **não**; `hub_fail_streak ≥
8` vira aviso em texto.

### 4.G Documentação

`PROTOCOL.md` §2.0.3 (ecos por nó) e §3.7 (comandos por nó e tradução do Hub);
`WIRE_CONTRACT_V9.md`; `External-Devices/<nó>/docs/PROTOCOL.md` reescritos (L7);
`HUB_PROTOCOL_IMPROVEMENTS.md` marca `boot_id` e diagnósticos como "em curso"; `DECISIONS.md`
**D-052** ("comando ao nó só pelo Hub; eco obrigatório; piggyback na distância; proxy de
diagnóstico com tarefa própria"); `PHASE_LOG.md` P3-10; `CHANGELOG.md`; `ROADMAP.md` (fecha o item
§I.4 quando a sintonia do fluxômetro estiver exposta); `DocumentationCatalog` (Controle › gavetas,
Calibrações › Bomba externa, Configurações › Conexão › Saúde); `MANUAL_DO_OPERADOR.md` §7.D
"Offset do sensor de distância" e §7.E "Calibração da bomba externa".

---

## 5. Ordem, commits e esforço

| # | Etapa | Commit | Esforço |
|---|---|---|---|
| 1 | §4.A | `feat(hub): mailbox da distancia por carona no push, whitelists por no e ecos no quadro (10.2.0-dev)` | M |
| 2 | §4.B distância | `feat(sensor-distancia): aplicar config vinda do hub na resposta do push e ecoar offset/periodos (v11)` | P |
| 3 | §4.B fluxômetro/bomba/biomassa | um commit por nó: `feat(<no>): ecoar <campos> no push (<versao>)` | P cada |
| 4 | §4.C | `feat(protocol): comandos e ecos por no (distancia, fluxometro, bomba, biomassa)` | M |
| 5 | §4.D distância + fluxômetro | `feat(controle): offset do sensor de distancia e sintonia do fluxometro com eco` | M |
| 6 | §4.D bomba + biomassa + Calibrações | `feat(controle, calibracoes): zerar volume, calibracao e PID da bomba, aquisicao da biomassa` | M |
| 7 | §4.E | `feat(hub): proxy de diagnostico dos nos com tarefa propria (/nodeDiag, serial nodeDiag)` | M |
| 8 | §4.F | `feat(configuracoes): saude dos nos na tabela (RSSI, heap, uptime, falhas, OTA)` | M |
| 9 | §4.G | `docs: comandos e ecos por no, D-052, P3-10, protocolos dos nos` | M |

P ≈ meio período; M ≈ um período. Total ≈ 7–8 períodos + bancada. **Cortes possíveis:** 7–8 (saúde)
são independentes; 6 pode ficar para depois de 5 sem perda.

---

## 6. Verificação

### 6.1 Automatizada

```powershell
# Hub
python -m unittest discover ESP32S3-HUB/tests/contracts
.\External-Devices\tools\.bin\arduino-cli.exe compile --config-file .\External-Devices\tools\arduino-cli.local.yaml --fqbn esp32:esp32:esp32s3 .\ESP32S3-HUB\ESP32S3-HUB
# Nós
powershell.exe -ExecutionPolicy Bypass -File .\External-Devices\tools\Test-FirmwareBaselines.ps1
powershell.exe -ExecutionPolicy Bypass -File .\External-Devices\tools\Test-HubDeviceContracts.ps1
powershell.exe -ExecutionPolicy Bypass -File .\External-Devices\tools\Compile-ExternalDevices.ps1
# App
dotnet test Windows_app/OpenTECHub.slnx
```

### 6.2 Simulador

Cada campo novo: digitar → frame esperado no `RawTelemetryReceived`/`CommandSent` → eco muda →
`Telemetria: {0}` acompanha; Hub legado (`--scenario legacy-hub`): campos desabilitados com o motivo.

### 6.3 Bancada

1. Distância: gravar `v11` e o Hub `10.2.0-dev`; no app, Offset 20 → 25,5 → o eco muda em ≤ 2 s;
   reiniciar o sensor → `GET /config` no nó mostra `offset_mm: 25.50` (NVS); `curl` direto continua
   funcionando (compatibilidade).
2. Fluxômetro: com o alívio aberto após uma parada segura, ajustar `ramp_rate`/`kp_flow` no app e
   observar se o controlador regula para baixo (fecha ou reformula o §I.4).
3. Bomba: *Zerar volume* → `PumpVol` volta a 0 no quadro seguinte; calibração aplicada → `PumpSlope`
   ecoado.
4. Saúde: com o PC em USB, a tabela mostra RSSI/heap dos cinco vindos do Hub; desligar um nó →
   `code 0` e idade crescendo, sem alarme.
5. Quadro: `Content-Length` de `/readData` com tudo ecoado — anotar em `docs/evidence/`.

---

## 7. Riscos e decisões em aberto

| Risco / decisão | Tratamento |
|---|---|
| `speed` da bomba: repassar (`speed` na whitelist do Hub) ou remover do app? | O nó usa `speed` para `g_cmdSpeed` no modo 0; hoje é vestigial e descartado. **Recomendação:** remover do quadro de segurança do app (mantendo `{"mode":0}`), documentar em §3.5 e não abrir a whitelist — menos superfície. Golden string muda: exige o teste de paridade com v.6 anotado. |
| Piggyback na distância vs. rota de *poll* | Piggyback (decisão 4). Se a bancada mostrar perda de comandos por push atrasado (backoff em falha), o `takeReliable` re-entrega a cada push até o `ack_cmd_id`. |
| Tamanho do quadro na USB com todos os ecos | Emissão condicional a "já ecoado"; medir (§6.3.5); teto 2,6 KB → se passar, `FlowOutput`/`FlowSetpointCorrected` saem do quadro e ficam só em `/diag`. |
| Proxy `/nodeDiag` e heap do Hub | Buffer fixo 5 × 512 B; `HTTPClient` reutilizado; timeout 500 ms; medir `ESP.getFreeHeap()` antes/depois em `docs/evidence/`. Se apertar, cair para 256 B e cortar métricas específicas. |
| Mudar ganhos do fluxômetro durante um ensaio | O árbitro recusa (aeração pertence ao ensaio); fora de ensaio, o operador é avisado de que a sintonia é persistida no nó (NVS do fluxômetro) e afeta os próximos ensaios — o manifesto passa a gravar `FlowKp/Ki/Ff*` (proveniência, como D-051). |
| Biomassa: `command`+`value` no nó | Tradução única no Hub, testada em `test_json_keys.py`; o app nunca escreve `command` direto. |
| Nós ainda em `v10`/`3.8` | Sem eco → campos desabilitados com o motivo; nada quebra; `NodeFirmwareCatalog` aceita as duas versões. |
