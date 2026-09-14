# Plano — Expor no Hub e no app o que os nós externos passaram a oferecer (offset do sensor de distância, sintonia do fluxômetro, calibração da bomba, aquisição da biomassa, saúde dos nós)

> **Nota de supersessão — 2026-09-13:** este plano preserva o contrato linear da bomba
> 3.9/3.10 como evidência histórica. Para operação atual, a calibração foi substituída
> pela curva polinomial contínua C0+C1 da bomba 3.12 e pela transição editável do
> fluxômetro v12.0; consulte `2026-09-13-plano-calibracao-dupla-fluxometro-bomba.md`.
> Não reinterpretar os testes históricos abaixo como se já usassem o contrato novo.

**Data:** 2026-09-12 (reescrito em etapas no mesmo dia)
**Escopo:** auditoria dos cinco firmwares de `External-Devices/` (reorganização de 11–12/09/2026:
parsers zero-heap, Web OTA, `/diag`, backoff, Link Watchdog, `/nodeHello`, NVS no sensor de
distância) contra o que o Hub `10.1.0-dev` repassa e o que o app `OpenTECHub` expõe; e as **nove
etapas** para fechar as lacunas — no Hub (`10.2.0-dev`), nos nós (`v11`/`3.9`) e no app — sem
quebrar o fio congelado.
**Estado:** **executado em 12/09/2026** — as nove etapas na `main`, um commit por etapa
(`884ef58`, `4939e51`, `8fffba8`, `1473895`/`ddf10e9`/`8194301`, `e666a03`, `23aa797`, `54e166c`,
`10f2210`/`8befccf`, e o commit de docs desta etapa); suíte 1448 → 1497, contratos do Hub 38 → 72,
firmware do Hub compila. Decisões do §8 fechadas pelo usuário: recomendações adotadas (`speed` sai do
app; carona na resposta do push; um `command` por revisão; proxy com tarefa própria) e **sem
compatibilidade retroativa com nós `v10`/`3.8`**. Registro: [D-052](../DECISIONS.md),
[P3-10](../history/PHASE_LOG.md), `docs/PONTOS_DE_MELHORIA_EXPOSICAO_NOS.md`.
**Desvios:** (a) Etapa 7 — a prévia da calibração da bomba usa S = 250/500/1000 (unidade interna
0–1000), não PWM 64/128/255: a bomba 3.9 converte S em PWM 155–1023 e o rascunho calibraria o eixo
errado; (b) Etapa 7 — `BiomassGear` é a marcha óptica combinada 0–31 (`IT × 8 + PWM`), não ganho TIA
1–7, e `set_gear` vai antes de `set_it`/`set_pwm`; IT é exibido em ms e enviado como código 0–5;
período da sonda parte de 25 000 ms (o nó eleva valores abaixo do piso térmico); (c) Etapa 8 — a
Etapa 8 foi executada sem o recibo de bancada da D-051 (o proxy só depende do registro de IPs no
código, já testado por contrato); o número do heap **não** foi medido — não havia Hub conectado — e
fica como pendência, com o Hub registrando `heap antes/depois` no boot para a medição; (d) Etapa 9 —
`DocumentationEvidenceTests` regera capturas em `docs/evidence/screenshots/` a cada execução; não
foram commitadas. **Pendências de bancada:** §7.3 na íntegra (regravação da frota, offset, sintonia,
bomba, biomassa, tamanho do quadro, heap), listadas em `ROADMAP.md › Field readiness`.
**Pré-leitura:** `docs/PROTOCOL.md` §2.0.1, §2.0.2, §3.4, §3.5; `docs/DECISIONS.md` D-015 (árbitro
único do fio), D-021/D-022 (biomassa/bomba), D-051; `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`;
`ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h` (caixa confiável `queueReliable` /
`takeReliable` / `ackReliable`); `External-Devices/docs/HUB_PROTOCOL_IMPROVEMENTS.md`; o
`PROTOCOL.md` de cada nó em `External-Devices/<nó>/docs/`.

---

## 0. Como executar este plano em outra sessão

1. **Ponto de partida.** Ler este arquivo e a pré-leitura. Baselines a confirmar antes de tocar em
   qualquer coisa: `dotnet test Windows_app/OpenTECHub.slnx` (1448 aprovados em 12/09/2026); Hub:
   `python -m unittest discover tests/contracts` em `ESP32S3-HUB/` (38) e compilação (§10.1); nós:
   `Compile-ExternalDevices.ps1`, `Test-FirmwareBaselines.ps1`, `Test-HubDeviceContracts.ps1`,
   `Test-AgitatorManualParser.ps1` em `External-Devices/tools/`. O app não pode estar rodando
   durante `dotnet build` (`Get-Process OpenTECHub`).
2. **Ordem das etapas** (uma etapa = um commit, com os seus testes, na `main`):
   **1** Hub: caixa da distância e ecos → **2** Nó distância `v11` → **3** Hub: whitelists e ecos
   dos outros nós → **4** Nós fluxômetro/bomba/biomassa (ecos) → **5** App: fio (chaves, builders,
   parser, simulador) → **6** App: distância + fluxômetro na UI → **7** App: bomba + biomassa +
   Calibrações → **8** Hub + app: saúde dos nós (`/nodeDiag`) → **9** Documentação.
   1–4 antes de 5 porque os *golden strings* do app fixam o quadro real; o simulador (Etapa 5)
   sustenta 6–7 enquanto o Hub e os nós não são gravados. **Cortes possíveis:** 8 é independente;
   7 pode ficar para depois de 6 sem perda.
3. **Regras que não se negociam.** Fio do ESP32 congelado: **tudo aditivo** — chaves novas no
   comando e no quadro, nenhuma renomeada, `HubProtocolVersion` continua 10. Todo comando ao nó
   passa pelo Hub e pelo `CommandArbiter` (D-015); o app **não** fala direto com os nós para
   comandar (§3.1). Um valor que o nó não ecoa não vira campo editável no app (§3.3). `InvariantCulture`
   no fio; código em inglês, interface em pt-BR; ausência de chave nunca vira falha na tela.
4. **Critério de pronto por etapa:** listado em cada uma. Ao fim de tudo: Etapa 9.
5. **Cuidado operacional:** o `.git` está no OneDrive e outra sessão pode squashar/resetar a
   `main`; commitar cada etapa assim que os testes passam, verificar `git log` antes de cada commit.

---

## 1. Inventário: o que mudou nos nós, o que o Hub repassa, o que o app expõe

Fonte: diff do vocabulário de fio (chaves JSON, rotas, parâmetros de query) entre
`archive/active-baseline/*.ino` e `firmware/*/src/**` de cada nó, mais leitura dos parsers de
comando (`CommandCodec`/`OperationController`/`ConfigCodec`) e dos handlers do Hub
(`src/protocol/Commands.h`, `src/protocol/Mailboxes.h`, `src/network/HttpServer.h`).

### 1.1 Comum aos cinco nós (novo na reorganização)

| Capacidade | Hub repassa? | App expõe? | Veredito |
|---|---|---|---|
| `GET /nodeHello?dev&ver&mac` (auto-registro, re-anúncio a cada 30 s) | **sim** — `NodeRegistry`, `*IP`/`*NodeVer`/`*NodeMac`, `/nodes` | **sim** — D-051 | fechado em 12/09 |
| `GET /diag` e `/status` (`device, version, uptime_s, free_heap, wifi_status, ssid, rssi, ip, mac, hub_fail_streak, ota` + métricas do nó) | **não** — só direto do PC em Wi-Fi | parcial — *Abrir diagnóstico* abre no navegador; nada é lido | **Etapa 8** |
| `POST /update` (Web OTA com intertravamento) | não (não faz sentido) | não — `Publish-OtaFirmware.ps1` cobre, com baseline e headroom | diferido (ROADMAP) |
| Backoff exponencial, Task WDT 15 s, Link Watchdog (`g_hubFailStreak ≥ 8` → reassocia), canal 6 | n/a — `hub_fail_streak` só em `/diag` | indiretamente — reassociação vira "IP mudou"/"registrado" em Eventos (D-051) | `hub_fail_streak` entra na Etapa 8 |
| `boot_id` no push | Hub lê só do `/flowData` e **não publica**; os outros nós não enviam | não | **Etapa 3** publica `FlowmeterBootId`; os demais ficam para depois (`HUB_PROTOCOL_IMPROVEMENTS` "compatíveis" 2) |

### 1.2 Sensor de distância (`sensor-distancia`, `v10`) — **o caso do exemplo**

| Item | Nó | Hub | App |
|---|---|---|---|
| Push `GET /distance?distance=<mm int>&time=<s>` (`src/core/FirmwareApp.cpp:151-170`) | igual ao baseline; **já lê o corpo da resposta** (`httpGet(url, code, body)`) e o descarta | `HttpServer.h:118` lê `distance`, `time`; filtro de estagnação; `DistanceOnline`; `Distance` no quadro; responde `200 "Distance data received"` | `Distance` (mm), presença, roteamento (`distanceSensorComm`) |
| **NVS `dist_cfg`** (novo): `offset_mm` (padrão 20,0; `distance = mm − offset`), `sample_period`/`send_period` (ms), `cooldown_soft/bus/xshut`, L1/L2/L3, `reset_nvs` | `GET /config`, `POST /config` locais (`src/api/LocalHttpApi.cpp:156-163`) + serial; `processConfigUpdate(body)` em `src/protocol/ConfigCodec.cpp`; `offset_mm` ecoado em `/config`, `/diag`, `/status` | **nenhuma rota de comando para o nó** — só existem `biomassBox`, `pumpBox`, `agitatorBox` e a caixa do fluxômetro; `distanceSensorReference`, `foamStartDelay_s`, `foamPulse_s`, `foamInterval_s` são parâmetros do controle de espuma **dentro do Hub** | *Controle › Distância* (`FoamControlViewModel`) mostra Atraso inicial/Pulso/Intervalo (do Hub) e a leitura. **Nenhum campo de offset, período ou recuperação; nenhum eco** |

**Lacuna L1:** o offset é ajustável só por `curl -X POST http://<ip>/config` com o PC no Wi-Fi do
Hub. O Hub está defasado por construção: o nó nunca teve canal de comando.

### 1.3 Fluxômetro (`fluxometro`, `v10`)

| Item | Nó | Hub | App |
|---|---|---|---|
| Comandos aceitos (`src/protocol/CommandCodec.h`) | `flowSetpoint`/`flow_setpoint`, `maxFlow`/`max_flow`, `valve_1`/`v1`, `valve_2`/`v2`, `v_Flow`/`valveFlow`, `reconnect_wifi`, curva `a1 b1 k1 f1 c1 k2 f2 c2`, `quadratic`, **`kp_flow`, `ki_flow`, `ff_gain`, `ff_offset`, `ramp_rate`** (persistidos em `CalibrationStore`), `dac_hold`, `debug_pi`, `direct*` | `queueReliableFlowCommandFromJson` repassa **só** `flowSetpoint maxFlow reconnectWifi v_Flow valve_1 valve_2`; a curva por caminho próprio (10.0.1) | setpoint, válvulas, maxFlow, curva (Calibrações) |
| Push `GET /flowData?…` (`src/tasks/TaskRuntime.h`) | `seconds flow_voltage flow_rate flow_setpoint flow_setpoint_corrected flow_output ff_gain ff_offset valve1State valve2State valveFlowState ack_cmd_id last_apply_ms command_source boot_id reconnect_wifi` | lê `seconds flow_voltage flow_rate flow_setpoint valve*State ack_cmd_id command_source boot_id reconnect_wifi`; **ignora `flow_setpoint_corrected`, `flow_output`, `ff_gain`, `ff_offset`, `last_apply_ms`**; não publica `boot_id` | `FlowRate`, `FlowSetpoint`, `FlowVoltage`, válvulas, `FlowCommandSource`, `FlowmeterReconnectWifi` |

**Lacuna L2:** ganhos do PI (`kp_flow`, `ki_flow`), feed-forward (`ff_gain`, `ff_offset`) e rampa
(`ramp_rate`) — o que a pendência "controlador de vazão não regula para baixo na primeira abertura
do alívio" (`ROADMAP` › Field readiness; §I.4 do plano de 11/09) precisa — não passam pelo Hub, e o
nó **já ecoa** `ff_gain`/`ff_offset`/`flow_output` que o Hub descarta.

### 1.4 Bomba peristáltica (`bomba-peristaltica`, `3.8`)

| Item | Nó | Hub | App |
|---|---|---|---|
| Comandos aceitos (`src/control/OperationController.h`) | `command` ∈ {`start`, `stop`, **`reset_volume`**, `save_config`, `load_config`, `clear_nvs`, `print_config`}; `mode`, `speed`, `init_t`, `final_t`, `lambda_*`, `phi_*`, `p0…p20`, `num_segments`, `t0…/q0…`; **`pumpSlope`, `pumpIntercept`** (mL/min ↔ PWM); **`pid_kp`, `pid_ki`, `pid_kd`**; **`sensorEnable`, `sensorBypass`, `sensorButtonOverride`, `disablePot`** | `Commands.h:490` `simpleKeys = {pump_command→command, mode, pump_speed→speed, init_t, final_t, lambda_*, phi_*}` + `p0…p20` + `t/q` + `num_segments`; **nada de `pumpSlope/Intercept`, `pid_*`, `sensor*`, `disablePot`** | cinco perfis, janela, `pumpComm`; **sem `pump_command`** (não há `PumpCommand` em `CommandKeys`), sem calibração, sem PID |
| Push `GET /pumpData?mode pwm speed flow vol v_tgt active waiting ack_cmd_id` (`src/network/HubClient.h:168`) | igual | lê tudo | `PumpMode/PWM/Speed/Flow/Vol/TargetVol/Active/Waiting` |
| `speed` | o nó lê `speed` (`OperationController.h:280`) | só repassa **`pump_speed`** (e renomeia); o `"speed"` que o app manda no quadro de segurança `{"mode":0,"speed":0}` (`CommandBuilders.cs:384`) é **descartado** | `PROTOCOL.md` §3.5 já trata `speed` como vestigial |

**Lacuna L3:** `reset_volume` inalcançável pelo app; calibração e PID só via `POST /command` no nó;
o `speed` descartado é uma armadilha documental (o app acha que envia; o Hub joga fora).

### 1.5 Sensor de biomassa (`sensor-biomassa`, `v10`)

| Item | Nó | Hub | App |
|---|---|---|---|
| Comandos aceitos (`src/protocol/CommandCodec.h`; o nó usa o par `command`+`value` para os `set_*`) | `start stop blank low high opt test_period test_on/off read_once`, **`set_it`/`it`, `set_pwm`/`pwm`/`pwm_preset`, `set_gear`, `ema`, `probe_period`, `refresh_ms`, `duty`/`duty_pct`, `led`/`led_off`, `auto`/`manual`, `hub_on`/`hub_off`**, `save_config`/`load_config`/`factory`, `clear_history`, `print_*`, `reset_health` | `Commands.h:432-445` repassa `start stop blank low high opt test_period` | `start`, `stop`, `blank`, `low`, `high`, `opt` (D-021); `test_period` não |
| Push `GET /biomassData?absorbance raw it pwm idle ack_cmd_id` | igual | lê tudo | `BiomassAbs/Raw/IT/PWM` (leitura) |

**Lacuna L4:** tempo de integração, PWM do emissor, ganho, EMA e período de sonda são ajustáveis só
no nó (UI web própria ou `POST`). O app mostra IT/PWM e não pode alterá-los.

### 1.6 Frasco agitador (`frasco-agitador`, `v10`)

Comandos `RPM_percent`, `Dir`, `ActivePot`, `cmd_id` — o Hub envia exatamente estes; push
`pct dir pot src secs ack_cmd_id` — o Hub lê tudo menos `secs` (sem consequência). `/diag` traz
`duty`, `dir`, `pot`, `time_s`. **Sem lacuna funcional.** Só entra na Etapa 8.

### 1.7 Hub — já corrigido em 12/09

Leitura de pH/O₂/pressão/antiespumante **desacoplada** dos flags de malha (`Telemetry.h`,
`sendSensorCommand(…, true)` incondicional; flags governam atuação) — corrige o `-1.0`/"--" no app
Android com malha desligada. Identidade dos nós no quadro e `/nodes` completo (`10.1.0-dev`, D-051).

---

## 2. Resumo das lacunas e em que etapa cada uma fecha

| # | Lacuna | Componentes | Prioridade | Etapas |
|---|---|---|---|---|
| L1 | Offset/períodos/recuperação da distância só via `POST /config` no nó; sem canal pelo Hub; sem eco | nó, Hub, app | **P1** — pedido do operador | 1, 2, 5, 6 |
| L2 | Sintonia PI/FF/rampa do fluxômetro não passa pelo Hub; ecos `ff_*`/`flow_output`/`flow_setpoint_corrected` descartados | Hub, nó (kp/ki/ramp no push), app | **P1** — pendência §I.4 | 3, 4, 5, 6 |
| L3 | `reset_volume` inalcançável; `pumpSlope/Intercept`, `pid_*` só no nó; `speed` do app descartado | Hub, nó (eco), app | **P2** | 3, 4, 5, 7 |
| L4 | IT/PWM/ganho/EMA/período da biomassa só no nó | Hub, nó (eco), app | **P2** | 3, 4, 5, 7 |
| L5 | `/diag` dos nós invisível ao app e a quem está em USB | Hub, app | **P3** | 8 |
| L6 | `boot_id` do fluxômetro lido e não publicado | Hub, app | **P3** | 3, 5 |
| L7 | `PROTOCOL.md` dos nós só remete à matriz transversal | docs | junto com 2, 4 e 9 | 2, 4, 9 |

---

## 3. Decisões de design (valem para todas as etapas)

1. **Tudo pelo Hub, nada direto ao nó para comandar.** O app conhece o IP de cada nó desde D-051 e
   poderia fazer `POST http://<ip>/config`; **não faz**. Comandar direto contorna o `CommandArbiter`
   (D-015), o jornal de comandos, o `cmd_id`/`ack_cmd_id` e deixa quem está em USB sem a função. O
   caminho é o dos outros nós: caixa confiável no Hub, `takeReliable`/`ackReliable`, eco no quadro.
   Exceção mantida: *Abrir diagnóstico* (leitura, no navegador).
2. **Chaves planas com prefixo de dispositivo no comando app → Hub**, como já se faz
   (`pump_speed`→`speed`). O Hub traduz para o nome que o nó entende; a tradução fica documentada
   e testada. Tabela completa no §4 (Etapas 1 e 3).
3. **Eco obrigatório no quadro para tudo que vira controle.** O nó inclui o valor aplicado no seu
   push; o Hub publica a chave de eco; o app mostra *pedido* × *aplicado* como já faz com as válvulas
   (`Telemetria: {0}`). **Sem eco, o campo fica desabilitado** com o motivo no tooltip
   ("aguardando eco do nó") — é estado de *runtime* (o nó ainda não fez o primeiro push deste
   boot, ou está ausente), não de versão. Ecos **não** são sticky: um Hub que parou de ecoar é um nó
   que sumiu — a chave sai do quadro e o app volta a "—".
4. **Canal do sensor de distância por carona na resposta do push** (piggyback). O nó já faz um
   `GET /distance` por segundo e já lê o corpo; o Hub passa a responder o payload da `distanceBox`
   (`{"cmd_id":N,"offset_mm":25.5}`) quando há comando pendente, o nó chama o `processConfigUpdate`
   que já existe e confirma com `&ack_cmd_id=N` no push seguinte. Sem novo laço de *poll*, sem nova
   rota. *Alternativa rejeitada:* rota `GET /distanceCommand` com *poll* de 2 s como a bomba — custa
   um cliente HTTP a mais no nó e não traz nada; o `takeReliable` re-entrega em cada push até o ack
   de qualquer forma.
5. **Proxy de diagnóstico no Hub com tarefa própria.** `GET /nodeDiag?dev=` **não** faz HTTP dentro
   do handler do `AsyncWebServer`. Uma tarefa FreeRTOS de baixa prioridade percorre os nós
   registrados, faz `GET http://<ip>/diag` com timeout curto e guarda o corpo; o handler serve o
   cache. O quadro agregado **não** carrega isso (custa bytes na USB); em USB o app pede por comando
   serial `{"nodeDiag":"pump"}` e recebe uma linha `{"NodeDiag":{…}}`, tratada como não-telemetria.
6. **Versões — frota regravada de uma vez, sem compatibilidade retroativa.** Hub `10.2.0-dev`
   (aditivo no fio; protocolo 10). Nós: distância `v11`, fluxômetro `v11`, bomba `3.9`, biomassa
   `v11`, agitador `v10` (inalterado). `NodeFirmwareCatalog` passa a listar **só** estas versões:
   um nó `v10`/`3.8` que apareça depois da regravação é um esquecido, e o aviso de firmware fora do
   conjunto validado (D-051) é exatamente o sinal certo. Nenhum caminho de código trata "nó antigo
   sem eco" como caso especial — o único estado é "aguardando eco".
7. **`speed` da bomba: sai do app, não entra na whitelist.** O nó usa `speed` só para `g_cmdSpeed`
   no modo 0 e o `PROTOCOL.md` §3.5 já o chama de vestigial; abrir a whitelist só para honrar um
   valor sempre zero aumenta superfície. O quadro de segurança passa a `{"mode":0}` (Etapa 5),
   com o *golden string* atualizado e a paridade com v.6 anotada em §3.5. **Se o usuário preferir
   manter o byte a byte com v.6, a alternativa é acrescentar `"speed"` às `simpleKeys` do Hub na
   Etapa 3.** **Decidido em 12/09: retirar do app** (recomendação adotada).

---

## 4. Vocabulário novo (referência para todas as etapas)

### 4.1 Comando app → Hub → nó

| Chave no app (`CommandKeys`) | Fio app→Hub | Hub traduz para | Nó | Caixa |
|---|---|---|---|---|
| `DistanceOffsetMm` | `distanceOffsetMm` | `offset_mm` | distância | `distanceBox` (piggyback) |
| `DistanceSamplePeriodMs` | `distanceSamplePeriodMs` | `sample_period` | distância | idem |
| `DistanceSendPeriodMs` | `distanceSendPeriodMs` | `send_period` | distância | idem |
| `DistanceResetNvs` | `distanceResetNvs` | `reset_nvs` | distância | idem |
| `FlowKp` / `FlowKi` | `flowKp` / `flowKi` | `kp_flow` / `ki_flow` | fluxômetro | caixa do fluxômetro |
| `FlowFfGain` / `FlowFfOffset` | `flowFfGain` / `flowFfOffset` | `ff_gain` / `ff_offset` | fluxômetro | idem |
| `FlowRampRate` | `flowRampRate` | `ramp_rate` | fluxômetro | idem |
| `PumpCommand` | `pump_command` | `command` (string) — já existe no Hub | bomba | `pumpBox` |
| `PumpSlope` / `PumpIntercept` | `pumpSlope` / `pumpIntercept` | idem (sem tradução) | bomba | `pumpBox` |
| `PumpPidKp` / `PumpPidKi` / `PumpPidKd` | `pumpPidKp` … | `pid_kp` / `pid_ki` / `pid_kd` | bomba | `pumpBox` |
| `BiomassIt` | `biomassIt` | `{"command":"set_it","value":N}` | biomassa | `biomassBox` |
| `BiomassPwm` | `biomassPwm` | `{"command":"set_pwm","value":N}` | biomassa | idem |
| `BiomassGear` | `biomassGear` | `{"command":"set_gear","value":N}` | biomassa | idem |
| `BiomassEma` | `biomassEma` | `{"command":"ema","value":x}` | biomassa | idem |
| `BiomassProbePeriodMs` | `biomassProbePeriodMs` | `{"command":"probe_period","value":N}` | biomassa | idem |

### 4.2 Eco nó → Hub → quadro agregado → app

| Nó acrescenta ao push | Hub lê e publica no `/readData` | Tipo | Condição |
|---|---|---|---|
| `/distance … &offset=%.2f&sample_ms=%lu&send_ms=%lu&ack_cmd_id=%lu` | `DistanceOffsetMm`, `DistanceSamplePeriodMs`, `DistanceSendPeriodMs`, `DistanceCommandPending` | float, int, int, bool | só depois do primeiro push `v11` (sentinela: nunca ecoado) |
| `/flowData … &kp=%.4f&ki=%.4f&ramp=%.3f` (+ `ff_gain`, `ff_offset`, `flow_output`, `flow_setpoint_corrected`, `boot_id` já enviados) | `FlowKp`, `FlowKi`, `FlowFfGain`, `FlowFfOffset`, `FlowRampRate`, `FlowOutput`, `FlowSetpointCorrected`, `FlowmeterBootId` | float ×7, uint32 | `FlowFf*`/`FlowOutput`/`FlowSetpointCorrected`/`BootId` já com nó `v10`; `Kp/Ki/Ramp` só `v11` |
| `/pumpData … &slope=%.4f&intercept=%.4f` | `PumpSlope`, `PumpIntercept` | float | só `3.9` |
| `/biomassData … &gear=%d&ema=%.3f&probe_ms=%lu` | `BiomassGear`, `BiomassEma`, `BiomassProbePeriodMs` | int, float, int | só `v11` (IT/PWM já ecoados) |

Regra de emissão no Hub: cada bloco só entra no JSON quando o nó já o ecoou uma vez neste boot
(mesmo padrão condicional das dez chaves `Servo*` de amostra), e some quando o nó sai da janela de
presença — para o app, chave ausente = "—".

---

## 5. Etapas

### Etapa 1 — Hub `10.2.0-dev`: caixa confiável da distância por carona no push, e os ecos da distância

**Objetivo.** Dar ao sensor de distância um canal de comando pelo Hub sem mudar a cadência do nó,
reaproveitando a caixa confiável que a bomba, a biomassa e o agitador já usam.
**Pré-condições.** Baseline verde (§0.1). Ler `Mailboxes.h:1-60` (`queueReliable` monta
`{"cmd_id":N,<inner>}`, `takeReliable` re-entrega até o ack, `ackReliable` limpa só com a revisão
exata) e `HttpServer.h:118-170` (handler `/distance`) e `:300-320` (como `/pumpData` chama
`ackReliable(pumpBox, readAckParam(request), "Pump")`).
**Arquivos.** `src/core/AppContext.h`, `src/protocol/Commands.h`, `src/network/HttpServer.h`,
`src/sensor/Telemetry.h`, `Config.h`, `tests/contracts/test_node_commands.py` (novo),
`tests/contracts/test_json_keys.py`, `docs/WIRE_CONTRACT_V9.md`.

**Passos.**
1. `AppContext.h`: `ReliableMailbox distanceBox;` ao lado das três existentes; variáveis de eco
   `float distanceOffsetMm = NAN; uint32_t distanceSamplePeriodMs = 0, distanceSendPeriodMs = 0;
   bool distanceEchoSeen = false;` (comentário: NAN/0/false = nunca ecoado neste boot).
2. `Commands.h`, bloco novo `// ============ DISTANCE (config pass-through) ============` logo após
   o bloco de espuma (`foamInterval_s`, linha ~477): montar `innerJson` com a tradução da tabela
   §4.1 (`distanceOffsetMm→offset_mm`, `distanceSamplePeriodMs→sample_period`,
   `distanceSendPeriodMs→send_period`, `distanceResetNvs→reset_nvs`), usando `getValueFromJson`
   como os outros blocos; `if (found && distanceSensorCommOn) queueReliable(distanceBox, inner,
   "Distance");`. Validar faixa no Hub: `offset_mm` em [−50, 200], períodos em [100, 60000] ms;
   fora disso, `ESP32_EVT` e descarte (o nó também valida, mas o Hub é o lugar de dizer ao operador).
3. `HttpServer.h`, handler `/distance`: (a) antes do filtro de estagnação, ler `offset`, `sample_ms`,
   `send_ms` se presentes → gravar nas variáveis de eco sob `stateMutex` e `distanceEchoSeen = true`;
   (b) `ackReliable(distanceBox, readAckParam(request), "Distance")`; (c) na resposta, em vez do
   `"Distance data received"` fixo: `String pending = takeReliable(distanceBox); if (pending != "{}")
   request->send(200, "application/json", pending); else request->send(200, "text/plain", "Distance
   data received");`. Um nó `v10` recebe o JSON e o ignora (só imprime `Response: 200`) — sem efeito.
4. `Telemetry.h`: snapshot das variáveis de eco sob `stateMutex`; emitir, **só se
   `distanceEchoSeen` e o nó está na janela de presença**, `"DistanceOffsetMm":%.2f`,
   `"DistanceSamplePeriodMs":%lu`, `"DistanceSendPeriodMs":%lu`; sempre (como os outros)
   `"DistanceCommandPending":mailboxPending(distanceBox)`. Ao sair da janela, `distanceEchoSeen =
   false` (o mesmo lugar onde `distanceSensorCommOn` expira).
5. `Commands.h`, `resetVariables`: `distanceBox.awaiting = false; distanceBox.payload = "";` junto
   das outras caixas (linha ~179).
6. `Config.h`: `HUB_FIRMWARE_VERSION "10.2.0-dev"`, comentário de uma linha; protocolo 10.
7. Testes de contrato: `test_node_commands.py` (novo) com um modelo do piggyback — enfileira →
   push sem ack devolve o payload com `cmd_id` → push com `ack_cmd_id` igual limpa → ack diferente
   não limpa; e a tradução de chaves da distância (`distanceOffsetMm`→`offset_mm`, faixa).
   `test_json_keys.py`: os quatro comandos novos entram em `REAL_FRAMES`. Teste de fonte: o handler
   `/distance` chama `ackReliable(distanceBox` e `takeReliable(distanceBox`; `Telemetry.h` emite as
   três chaves de eco e `DistanceCommandPending`.
8. `WIRE_CONTRACT_V9.md`: seção "Comandos por nó (tradução app→nó)" iniciada com a distância e
   seção "Ecos por nó" com as quatro chaves.

**Testes/verificação.** `python -m unittest discover tests/contracts`; compilar (headroom > 300 KB
esperado; anotar); `Test-HubDeviceContracts.ps1` (acrescentar `hasParam("offset")`,
`hasParam("sample_ms")`, `hasParam("send_ms")` a *Hub distance fields*).
**Critério de pronto.** Contratos verdes; compila; `curl "http://192.168.4.1/distance?distance=100&time=1"`
após `POST /command {"distanceOffsetMm":25.5}` devolve `{"cmd_id":1,"offset_mm":25.5}` (pode ser
verificado no simulador do Hub apenas por leitura de código — a bancada confirma na Etapa 2).
**Commit.** `feat(hub): caixa confiavel da distancia por carona no push e ecos de offset/periodos (10.2.0-dev)`.
**Esforço.** M (um período).

### Etapa 2 — Nó sensor de distância `v11`: aplicar config vinda do Hub e ecoar

**Objetivo.** Fechar o laço: o nó aplica o que o Hub responde ao push, confirma com `ack_cmd_id` e
ecoa o valor aplicado.
**Pré-condições.** Etapa 1 gravada no Hub ou, sem bancada, aceitar a verificação por código +
`Test-HubDeviceContracts.ps1`. Ler `src/core/FirmwareApp.cpp:151-170` e
`src/protocol/ConfigCodec.cpp` (`processConfigUpdate`, persistência NVS já feita lá).
**Arquivos.** `src/core/FirmwareApp.cpp`, `src/core/AppContext.h/.cpp` (`g_lastCmdId`),
`src/protocol/ConfigCodec.cpp/.h`, `src/network/NetworkManager.cpp` (versão no hello),
`src/api/LocalHttpApi.cpp` (`/diag` ganha `last_cmd_id`), `docs/PROTOCOL.md` do nó.

**Passos.**
1. `AppContext`: `uint32_t g_lastCmdId = 0;`.
2. `ConfigCodec`: `processConfigUpdate` passa a devolver `bool` (algo reconhecido) e a extrair
   `cmd_id` (`getJsonValue(payload, "cmd_id")`) para `g_lastCmdId` quando presente. Sem `cmd_id`
   (POST local ou serial) o comportamento é o de hoje.
3. `FirmwareApp.cpp`, após `httpGet(url, code, body)` bem-sucedido: `if (code == 200 && body.length()
   > 1 && body[0] == '{') processConfigUpdate(body.c_str());` — o Hub `10.1` responde texto e nada
   acontece; o `10.2` responde JSON só quando há pendência.
4. O push passa a `"%s?distance=%d&time=%.1f&offset=%.2f&sample_ms=%lu&send_ms=%lu&ack_cmd_id=%lu"`
   (`url[128]` → `char url[192]`). `ack_cmd_id` = `g_lastCmdId` (0 = nada aplicado ainda; o Hub
   ignora 0 por construção).
5. `reset_nvs`: após restaurar padrões, o próximo push ecoa `offset=20.00` — é o eco que fecha o
   comando no app; **manter** o `cmd_id` do reset em `g_lastCmdId` para o ack.
6. Hello: `ver=v11` (`NetworkManager.cpp`). `/diag`: `"last_cmd_id":%lu`.
7. `docs/PROTOCOL.md` do nó: reescrever — push (com os campos novos), resposta do Hub (piggyback),
   `POST /config` local, `/diag`, `/config`, NVS, versões `v10`/`v11`.
8. `External-Devices/tools/Test-HubDeviceContracts.ps1`: *Distance node* exige `&offset=`,
   `&ack_cmd_id=`, `processConfigUpdate(body`.

**Testes/verificação.** `Compile-ExternalDevices.ps1` (headroom > 160 KB; hoje 210 KB);
`Test-FirmwareBaselines.ps1` (o baseline arquivado não muda); `Test-HubDeviceContracts.ps1`.
Bancada mínima (pode ser adiada para §10.3): `POST /command {"distanceOffsetMm":25.5}` no Hub →
em ≤ 2 s `curl http://<ip>/config` mostra `25.50`; reiniciar o nó → persiste.
**Critério de pronto.** Compila com headroom; contratos verdes; `docs/PROTOCOL.md` do nó descreve
o vocabulário completo (fecha L7 para este nó).
**Commit.** `feat(sensor-distancia): aplicar config do hub na resposta do push e ecoar offset/periodos (v11)`.
**Esforço.** P (meio período).

### Etapa 3 — Hub `10.2.0-dev`: whitelists e ecos do fluxômetro, da bomba e da biomassa

**Objetivo.** Repassar os comandos que os três nós já aceitam e publicar os ecos que dois deles já
enviam (e os que a Etapa 4 acrescenta).
**Pré-condições.** Etapa 1 commitada (mesmo `Config.h`, mesmos padrões de eco).
**Arquivos.** `src/core/AppContext.h`, `src/protocol/Commands.h`, `src/protocol/Mailboxes.h`
(`queueReliableFlowCommandFromJson`), `src/network/HttpServer.h` (`/flowData`, `/pumpData`,
`/biomassData`), `src/sensor/Telemetry.h`, `tests/contracts/*.py`, `docs/WIRE_CONTRACT_V9.md`.

**Passos.**
1. **Fluxômetro — comando.** Em `queueReliableFlowCommandFromJson` (`Mailboxes.h`), acrescentar à
   lista as cinco traduções `flowKp→kp_flow`, `flowKi→ki_flow`, `flowFfGain→ff_gain`,
   `flowFfOffset→ff_offset`, `flowRampRate→ramp_rate`, com a mesma revisão/ack da caixa do
   fluxômetro (é **um** comando confiável por vez: um ajuste de sintonia enquanto um setpoint está
   pendente entra na mesma revisão — documentar).
2. **Fluxômetro — eco.** Handler `/flowData`: ler `ff_gain`, `ff_offset`, `flow_output`,
   `flow_setpoint_corrected` (já enviados pelo `v10`) e `kp`, `ki`, `ramp` (Etapa 4); guardar sob
   `cmdMutex` com o restante do estado do fluxômetro; `boot_id` já é lido — guardar em
   `flowmeterBootId`.
3. **Bomba — comando.** `simpleKeys` (`Commands.h:490`): acrescentar `pumpSlope`, `pumpIntercept`,
   `pumpPidKp`, `pumpPidKi`, `pumpPidKd`; a tradução `pumpPid*→pid_*` entra no mesmo laço que já
   remove o prefixo `pump_` (tratar `pumpPidKp` explicitamente, porque o prefixo é `pump` sem
   sublinhado). **Não** acrescentar `speed` (decisão §3.7) — salvo decisão contrária do usuário.
4. **Bomba — eco.** `/pumpData`: ler `slope`, `intercept` (Etapa 4) → `pumpSlope`, `pumpIntercept`
   (NAN = nunca).
5. **Biomassa — comando.** No bloco da biomassa (`Commands.h:432-445`): para `biomassIt`,
   `biomassPwm`, `biomassGear`, `biomassEma`, `biomassProbePeriodMs`, montar
   `"command":"set_it","value":N` etc. **Um `command` por revisão**: se o app mandar dois numa só
   frame, o Hub enfileira o primeiro e registra `ESP32_EVT` para o descartado — o app (Etapa 7)
   envia um por vez, esperando `BiomassCommandPending` cair.
6. **Biomassa — eco.** `/biomassData`: ler `gear`, `ema`, `probe_ms` (Etapa 4) → `biomassGear`,
   `biomassEma`, `biomassProbePeriodMs`.
7. **`Telemetry.h`:** blocos condicionais por nó (§4.2): `FlowKp FlowKi FlowFfGain FlowFfOffset
   FlowRampRate FlowOutput FlowSetpointCorrected FlowmeterBootId`; `PumpSlope PumpIntercept`;
   `BiomassGear BiomassEma BiomassProbePeriodMs`. Atualizar o comentário de pior caso da reserva
   (estimativa +200 B com tudo ecoado; a reserva 3072 aguenta — medir na bancada, §10.3.5).
8. Contratos: `test_json_keys.py` (todas as chaves novas em `REAL_FRAMES`, incluindo a montagem
   `command`+`value` da biomassa); `test_node_commands.py` (whitelists: cada chave nova é repassada,
   `speed` continua fora, `pumpPidKp` vira `pid_kp`); teste de fonte para as chaves de eco.
   `Test-HubDeviceContracts.ps1`: novos `hasParam` em *Hub flow/pump/biomass fields*.
9. `WIRE_CONTRACT_V9.md`: completar as duas seções iniciadas na Etapa 1.

**Testes/verificação.** Contratos; compilação; `Test-HubDeviceContracts.ps1`.
**Critério de pronto.** Tudo verde; um `POST /command {"flowKp":0.8}` enfileira `{"cmd_id":N,"kp_flow":0.8}`
em `/flowCommand` (verificável por leitura ou bancada).
**Commit.** `feat(hub): repassar sintonia do fluxometro, calibracao/PID da bomba e aquisicao da biomassa; publicar seus ecos`.
**Esforço.** M.

### Etapa 4 — Nós fluxômetro `v11`, bomba `3.9`, biomassa `v11`: ecoar o que aplicam

**Objetivo.** Cada nó passa a ecoar no push os valores que a Etapa 3 quer publicar. Nenhum comando
novo nos nós: eles já aceitam tudo.
**Pré-condições.** Etapa 3 commitada (para o `Test-HubDeviceContracts.ps1` conferir os pares).
**Arquivos e passos.**

| Nó | Arquivo | Mudança |
|---|---|---|
| Fluxômetro `v11` | `src/tasks/TaskRuntime.h` (push `flowData`) | acrescentar `&kp=%.4f&ki=%.4f&ramp=%.3f` a partir de `Kp_flow`, `Ki_flow`, `rampRate` (já globais); conferir o tamanho do buffer da URL (hoje ~300 B; +40 B); `ver=v11` no hello (`TaskRuntime.h:85`) |
| Bomba `3.9` | `src/network/HubClient.h:165-178` | acrescentar `&slope=%.4f&intercept=%.4f` (`g_config.pumpSlope/pumpIntercept` — confirmar nomes em `ConfigStore.h`); `url[256]` comporta; `ver=3.9` (`HubClient.h:143`) e a string `"version":"3.9"` do `/diag` |
| Biomassa `v11` | `src/protocol/TelemetryAndHub.h` (push `biomassData`) | acrescentar `&gear=%d&ema=%.3f&probe_ms=%lu`; `ver=v11` (`TelemetryAndHub.h:60`); `/diag` idem |
| Todos | `docs/PROTOCOL.md` do nó | reescrever com o vocabulário completo (comandos aceitos, push, `/diag`, persistência) — fecha L7 |
| Ferramentas | `Test-HubDeviceContracts.ps1` | *Flowmeter node* exige `&kp=`; *Pump node* `&slope=`; *Biomass node* `&gear=` |

**Testes/verificação.** `Compile-ExternalDevices.ps1` (headroom: fluxômetro é o mais apertado,
169 KB hoje — se cair abaixo de 160 KB, cortar `flow_setpoint_corrected` do push em vez de crescer);
`Test-FirmwareBaselines.ps1`; `Test-HubDeviceContracts.ps1`; `Test-AgitatorManualParser.ps1`
(inalterado, só regressão).
**Critério de pronto.** Três firmwares compilam com headroom; contratos verdes; três `PROTOCOL.md`
reescritos. Um commit por nó:
`feat(fluxometro): ecoar kp/ki/ramp no push (v11)`,
`feat(bomba-peristaltica): ecoar slope/intercept no push (3.9)`,
`feat(sensor-biomassa): ecoar gear/ema/probe_period no push (v11)`.
**Esforço.** P cada.

### Etapa 5 — App: camada de fio (chaves, builders, parser, catálogo, simulador)

**Objetivo.** O app fala e entende o vocabulário do §4 — sem UI ainda; tudo testado por *golden
strings* e por frames reais dos Hubs `10.1` (sem ecos) e `10.2` (com ecos).
**Pré-condições.** Etapas 1 e 3 commitadas (quadros reais para os testes). Decisão §3.7 tomada.
**Arquivos.** `src/OpenTECHub.Protocol/CommandKeys.cs`, `CommandBuilders.cs`, `SensorReadings.cs`,
`TelemetryParser.cs`; `src/OpenTECHub/Services/Communication/NodeFirmwareCatalog.cs`;
`src/OpenTECHub.Simulator/DeviceModel.cs`, `WireCodec.cs`, `HttpEndpoint.cs`, `SerialEndpoint.cs`;
testes `WireFormatTests.cs`, `TelemetryParserTests.cs` (ou novo `NodeConfigParserTests.cs`),
`SimulatorNodeConfigTests.cs` (novo), `NodeIdentityTests.cs` (catálogo).

**Passos.**
1. `CommandKeys`: bloco `// ---- External-node configuration (Hub 10.2) ----` com as chaves da
   tabela §4.1 e o comentário de que o Hub traduz. `PumpCommand = "pump_command"` com a nota de que
   o Hub a renomeia para `command` e os valores aceitos.
2. `CommandBuilders`: `DistanceConfig(double? offsetMm, int? samplePeriodMs, int? sendPeriodMs)`
   (só as chaves não nulas), `DistanceResetNvs()`, `FlowTuning(double? kp, double? ki, double?
   ffGain, double? ffOffset, double? rampRate)`, `PumpResetVolume()` → `{"pump_command":"reset_volume"}`,
   `PumpCalibration(double slope, double intercept)`, `PumpPid(double kp, double ki, double kd)`,
   `BiomassTuning(...)` que devolve **uma lista** de comandos (um `command` por frame, §Etapa 3.5).
   Quadro de segurança da bomba (`CommandBuilders.cs:384`): remover `Speed` conforme §3.7 (ou manter,
   se a decisão for a alternativa) e atualizar o *golden string* + o comentário em `PROTOCOL.md` §3.5.
3. `TelemetryKeys` + `SensorReadings`/`SensorSnapshot` + `TelemetryParser`: os ecos do §4.2 como
   `double?`/`int?`/`long?` (null = ausente), **não sticky** (limpar quando a chave falta —
   diferente da identidade); `DistanceCommandPending` como `bool?` (mesma família dos outros
   `*CommandPending`, entra em `ExternalDeviceStatus.Update` do sensor de distância, que hoje passa
   `pending: null`). `FlowmeterBootId` sticky (`long?`).
4. `NodeFirmwareCatalog`: `distance {v11}`, `flowmeter {v11}`, `pump {3.9}`, `biomass {v11}`,
   `agitator {v10}` — substituindo os conjuntos atuais (frota regravada, §3.6). Nada de
   `EchoesConfig`: o gating dos campos é só "eco presente no quadro".
5. Simulador: `DeviceModel` guarda `DistanceOffsetMm` (20,0), períodos, `FlowKp/Ki/Ff*/Ramp`,
   `PumpSlope/Intercept`, `BiomassGear/Ema/ProbePeriodMs`; `HttpEndpoint.HandleCommand` e
   `SerialEndpoint` aplicam as chaves novas (com a mesma tradução do Hub, para o frame ecoar o valor
   aplicado); `WireCodec` emite os ecos condicionalmente à presença do nó (cenário `legacy-hub`
   omite tudo; `node-dropout` derruba os ecos junto com a presença — é o que exercita os campos
   desabilitados); `DistanceCommandPending` cai no frame seguinte ao comando.
6. `docs/PROTOCOL.md`: §2.0.3 "Node configuration echoes `[hub 10.2]`" e §3.7 "Node configuration
   commands" com as duas tabelas do §4 (o Hub traduz; o app nunca escreve `offset_mm` ou
   `command` direto).

**Testes.** `WireFormatTests`: um *golden string* por builder, em `pt-BR` também (vírgula nunca
chega ao fio); `PumpResetVolume` é exatamente `{"pump_command":"reset_volume"}`; o quadro de
segurança da bomba é `{"mode":0}` (sem `speed`). Parser: frame `10.2` com todos os ecos → valores;
frame `10.1` → nulls; eco presente num frame e ausente no seguinte → null (não sticky);
`DistanceCommandPending` true/false/ausente. Simulador: comando aplicado aparece ecoado no frame
seguinte; `node-dropout` derruba os ecos. Catálogo: `NodeIdentityTests` atualizado para os novos
conjuntos (`v10` da distância passa a gerar aviso).
**Critério de pronto.** Suíte verde; `dotnet build` sem avisos; PROTOCOL atualizado.
**Commit.** `feat(protocol): comandos de configuracao e ecos por no (distancia, fluxometro, bomba, biomassa)`.
**Esforço.** M.

### Etapa 6 — App: Controle › Distância (offset) e Controle › Vazão de Ar (sintonia)

**Objetivo.** As duas lacunas P1 na tela, com o padrão pedido × aplicado das válvulas.
**Pré-condições.** Etapa 5. Ler `FoamControlViewModel.cs:120-150` (despacho via
`_dispatcher.Dispatch(CommandBuilders.FoamControl(...))`, `DispatchRefusal.Describe`, `_settings.Update`)
e a gaveta de Vazão de Ar em `ControlView.xaml:1234-1370` (os três `Telemetria: {0}`).
**Arquivos.** `ViewModels/FoamControlViewModel.cs`, `ViewModels/FlowControlViewModel.cs`,
`Views/ControlView.xaml`, `Services/Persistence/AppSettings.cs` (`FoamControlSettings` ganha os
três campos do nó; `FlowControlSettings` ganha a sintonia — persistidos como *pedido*, com
versão de esquema), `Services/Documentation/DocumentationCatalog.cs`; testes `DosingAuxiliariesTests`
/`ExternalDeviceTests`, `FlowmeterV05SyncTests`, `ControlWorkspaceContractTests`, `CompactLayoutTests`.

**Passos — Distância.**
1. `FoamControlViewModel`: propriedades `OffsetMmText` (pedido), `SamplePeriodMsText`,
   `SendPeriodMsText`; leituras `AppliedOffsetText` etc. a partir de `snapshot.DistanceOffsetMm`
   (`"—"` quando null); `CanEditNodeConfig => snapshot.DistanceOffsetMm is not null` +
   `NodeConfigUnavailableText` ("aguardando eco do nó" ou, com o nó ausente, o `PresenceText` do
   `Status`).
2. Comando **Enviar configuração do nó** (`SendNodeConfigCommand`): valida (offset [−50, 200] mm,
   períodos [100, 60000] ms), `_dispatcher.Dispatch(CommandBuilders.DistanceConfig(...))`,
   `Status.MarkCommandDispatched()`, `StatusText` com a frase de pendência; o `Status.Update` passa a
   receber `snapshot.DistanceCommandPending` em vez de `pending: null`.
3. Comando **Restaurar padrões do nó** (`ResetNodeConfigCommand`): `IDialogService.ConfirmDestructive`
   com o texto "restaura offset 20 mm e períodos de fábrica no sensor"; despacha `DistanceResetNvs()`.
4. XAML (gaveta Distância, `ControlView.xaml:1448+`): abaixo dos campos de espuma, um `Expander`
   **Configuração do nó** com *Offset (mm)* + `Telemetria: {0} mm`, *Período de amostragem (ms)*,
   *Período de envio (ms)*, cada um com o eco, os dois botões; `IsEnabled` por `MultiDataTrigger`
   `Status.CanSend` × `CanEditNodeConfig`, tooltip `NodeConfigUnavailableText`.
5. Persistência: `FoamControlSettings` (ou um `DistanceNodeSettings` novo) guarda o **pedido**;
   ao reconectar, a tela mostra pedido × aplicado e o operador decide reenviar (não reenviar
   sozinho — é configuração do nó, persistida no NVS dele).

**Passos — Vazão de Ar.**
6. `FlowControlViewModel`: `KpText`, `KiText`, `FfGainText`, `FfOffsetText`, `RampRateText`
   (pedido) e `AppliedKp…` (eco); leituras `FlowOutputText` (V) e `FlowSetpointCorrectedText`;
   `CanEditTuning` = eco de `FlowKp` presente (nó `v11`); `SendTuningCommand` despacha
   `CommandBuilders.FlowTuning(...)` pelo mesmo caminho que o setpoint (árbitro: `ActuatorId.Aeration`
   — durante um ensaio de kLa/Potência é **recusado**, e `DispatchRefusal.Describe` explica).
7. XAML (gaveta Vazão de Ar): `Expander` **Sintonia do controlador** com os cinco campos +
   `Telemetria: {0}` e as duas leituras; aviso `TextMuted`: "a sintonia é persistida no nó e afeta
   os próximos ensaios".
8. Proveniência: `ExternalNodeProvenance` (D-051) ganha `FlowTuning` (kp, ki, ff, ramp) quando
   ecoados, gravado em `ensaio.json`/`teste.json` — um ensaio feito com outra sintonia deve dizer.
9. `DocumentationCatalog` (tópico Controle › gavetas): os dois expanders, controle a controle.

**Testes.** VM: enviar offset gera o frame do *golden string* e marca pendência; pendência cai com
`DistanceCommandPending=false`; campo desabilitado sem eco; validação de faixa; reset pede
confirmação e só despacha com "sim". Fluxômetro: recusa durante ensaio (árbitro), frame correto,
eco reflete. `ControlWorkspaceContractTests`: rótulos exatos (`Offset (mm)`, `Sintonia do
controlador`). `CompactLayoutTests` em 936 × 534. `DocumentationTests`: termos novos.
**Critério de pronto.** Suíte verde; no simulador (`--scenario normal`), Offset 20 → 25,5 muda o
eco no frame seguinte; em `node-dropout` os campos aparecem desabilitados com a frase certa.
**Commit.** `feat(controle): offset do sensor de distancia e sintonia do fluxometro com eco`.
**Esforço.** M.

### Etapa 7 — App: Bomba Externa (zerar volume, PID), Calibrações › Bomba externa, Absorbância (aquisição)

**Objetivo.** As lacunas P2 na tela, com os mesmos padrões da Etapa 6.
**Pré-condições.** Etapa 5 (Etapa 6 recomendada antes, para reutilizar o `Expander` de configuração
do nó como estilo). Ler `FlowCalibrationViewModel.cs` (recibos em `Calibracoes/`) e
`PumpControlViewModel.cs:680-700`.
**Arquivos.** `ViewModels/PumpControlViewModel.cs`, `ViewModels/BiomassControlViewModel.cs`,
`ViewModels/CalibrationViewModel.cs` (+ novo `PumpCalibrationViewModel.cs`), `Views/ControlView.xaml`,
`Views/CalibrationView.xaml`, `AppSettings.cs`, `DocumentationCatalog.cs`; testes `BiomassPumpTests`,
`CalibrationTests`, `CompactLayoutTests`, `ControlWorkspaceContractTests`.

**Passos — Bomba.**
1. `PumpControlViewModel`: `ResetVolumeCommand` → `CommandBuilders.PumpResetVolume()`; habilitado por
   `Status.CanSend && snapshot.PumpOnline`; **não** zera `PumpVolumeText` localmente — espera o eco
   `PumpVol` ≈ 0 no quadro seguinte; se em 5 s o volume não caiu, `StatusText` avisa.
2. Expander **PID do nó**: Kp/Ki/Kd com eco quando o nó ecoar (a bomba `3.9` **não** ecoa `pid_*`
   nesta rodada — os campos ficam desabilitados com "sem eco no firmware atual"; entram na próxima
   versão da bomba se o operador precisar). Registrar em §11.
3. XAML: botão **Zerar volume** ao lado do volume acumulado; expander.

**Passos — Calibrações › Bomba externa.**
4. `PumpCalibrationViewModel`: `SlopeText`, `InterceptText`, prévia `mL/min = slope·PWM + intercept`
   para três PWMs (64, 128, 255), leitura dos ecos `PumpSlope`/`PumpIntercept`, `ApplyCommand`
   despacha `CommandBuilders.PumpCalibration(...)`, recibo JSON em `Calibracoes/bomba-externa-<data>.json`
   (pedido, eco após confirmação, `HubFirmwareVersion`, `PumpNode` da D-051).
5. `CalibrationView.xaml`: cartão **Bomba externa** ao lado dos existentes; `CalibrationViewModel`
   expõe o novo VM.

**Passos — Absorbância.**
6. `BiomassControlViewModel`: expander **Aquisição** com IT (µs/ms conforme o nó — confirmar em
   `Veml7700Driver.h`), PWM do emissor, Ganho (`set_gear`), EMA (0–1), Período de sonda (ms); os
   ecos `BiomassIT`/`BiomassPWM` já existem, `BiomassGear`/`Ema`/`ProbePeriodMs` chegam com o `v11`;
   **um comando por vez**: `SendAcquisitionCommand` enfileira a lista de `BiomassTuning(...)` e
   despacha o próximo quando `BiomassCommandPending` cai (fila no VM, cancelável, com `StatusText`
   "2 de 3 enviados").
7. `DocumentationCatalog`: gaveta Bomba Externa, Absorbância e o cartão de Calibrações.

**Testes.** Zerar volume: frame exato, não zera localmente, aviso após 5 s sem eco. Calibração:
prévia, recibo gravado com o eco, arquivo legível. Biomassa: fila envia um por vez e só avança com
a pendência caída; cancelamento; validação de faixas. Layout e contratos como na Etapa 6.
**Critério de pronto.** Suíte verde; simulador: zerar volume → `PumpVol` 0; calibração → eco;
aquisição → três frames em sequência.
**Commit.** `feat(controle, calibracoes): zerar volume, calibracao e PID da bomba, aquisicao da biomassa`.
**Esforço.** M.

### Etapa 8 — Hub `10.2.x` + app: saúde dos nós (`/nodeDiag`, serial `nodeDiag`)

**Objetivo.** RSSI, heap, uptime, `hub_fail_streak` e `ota` de cada nó na tabela *Nós na rede do Hub*,
também por USB — sem pôr bytes no quadro agregado.
**Pré-condições.** Recibo de bancada da D-051 (§6.3 do plano anterior) — o proxy depende de o
registro de IPs estar certo na prática. Medir `ESP.getFreeHeap()` do Hub antes.
**Arquivos.** Hub: `src/core/AppContext.h` (`struct NodeDiagCache { char body[512]; unsigned long
fetchedMs; int code; }` × 5), novo `src/network/NodeDiagTask.h`, `src/network/HttpServer.h`
(`/nodeDiag`), `src/protocol/Commands.h` (comando serial `nodeDiag`), `Runtime.h` (criar a
tarefa), `tests/contracts/test_node_diag.py`. App: `src/OpenTECHub.Protocol/HubNodeDiagClient.cs`
(novo), `ConnectionManager.cs` (linha `{"NodeDiag":…}` como não-telemetria, evento
`NodeDiagReceived`), `ViewModels/HubNodesViewModel.cs`, `Views/SettingsView.xaml`,
`NodeFirmwareCatalog.cs` (métricas específicas), testes `HubNodesViewModelTests`,
`ConnectionManagerTests` (linha não-telemetria), `TelemetryParserTests` (`ParseOutcome`).

**Passos — Hub.**
1. `NodeDiagTask`: `xTaskCreatePinnedToCore(..., 6144, ..., 1, ...)`; laço: a cada 30 s, para cada
   `i` com `g_deviceRegistry[i].registered && ip != 0.0.0.0`, `HTTPClient http; http.setTimeout(500);
   http.begin(String("http://") + ip.toString() + "/diag"); int code = http.GET(); if (code == 200)
   copiar até 511 B do corpo;` gravar `{code, fetchedMs}` sob `stateMutex`; `vTaskDelay` entre nós
   (200 ms) para não rajar o rádio. **Nunca** chamado de um handler HTTP.
2. `/nodeDiag[?dev=]`: serve `{"hub_time_ms":…,"nodes":[{"dev":"pump","code":200,"age_ms":…,
   "diag":<corpo tal qual>}]}`; `code 0` = nunca; buffer de resposta 3 KB (5 × 512 + moldura).
3. Serial: `{"nodeDiag":"pump"}` → `Serial.println("{\"NodeDiag\":{...uma entrada...}}")`; `"all"` →
   as cinco, uma linha por nó (mantém cada linha < 1 KB).
4. `test_node_diag.py`: modelo do cache (idade, `code 0` sem IP, corpo truncado em 511); teste de
   fonte: handler não contém `http.GET(` (a chamada vive só na tarefa).
5. Medir heap e anotar em `docs/evidence/hub-node-diag-heap-<data>.md`.

**Passos — App.**
6. `HubNodeDiagClient.Parse(body)` → `HubNodeDiag(Device, Code, Age, Rssi, FreeHeap, UptimeS,
   HubFailStreak, Ota, Extra: IReadOnlyDictionary<string,string>)`; `FetchAsync(hubIp)` em Wi-Fi.
7. `ConnectionManager`: uma linha JSON com raiz `NodeDiag` é `ParseOutcome.DeviceLog`-like (não conta
   como falha de parse, não vira telemetria) e dispara `NodeDiagReceived(string json)`; `DeviceService`
   repassa; `IDeviceService.RequestNodeDiag(device)` envia `{"nodeDiag":"<dev>"}` pelo árbitro como
   comando de sistema (não toma posse de atuador).
8. `HubNodesViewModel`: colunas **RSSI · Heap · Uptime · Falhas c/ Hub · OTA** + rodapé "Diagnóstico
   via Hub há N s"; em Wi-Fi usa `/nodeDiag` no mesmo ciclo de 10 s; em USB pede por serial a cada
   30 s enquanto a seção está aberta; `hub_fail_streak ≥ 8` → texto de aviso, nunca alarme.
   `NodeFirmwareCatalog.DescribeDiag(device, extra)` → "distância 118 mm · leitura há 0,4 s" etc.
9. `DocumentationCatalog` (Configurações › Conexão): as colunas novas.

**Testes.** Hub: `test_node_diag.py`. App: parse de `/nodeDiag` (10.2) e de um Hub sem a rota (null);
linha serial `NodeDiag` não é telemetria nem falha; VM enriquece por Wi-Fi e por USB; aviso de
`hub_fail_streak`; layout.
**Critério de pronto.** Heap do Hub medido e aceitável (> 150 KB livres após a tarefa); por USB a
tabela mostra RSSI/heap dos cinco; nó desligado → `code 0` e idade crescendo.
**Commits.** `feat(hub): proxy de diagnostico dos nos com tarefa propria (/nodeDiag, serial nodeDiag)`
e `feat(configuracoes): saude dos nos na tabela (RSSI, heap, uptime, falhas, OTA)`.
**Esforço.** M + M.

### Etapa 9 — Documentação e fechamento

**Objetivo.** O conjunto de docs que cada incremento exige, mais os `PROTOCOL.md` dos nós que as
Etapas 2 e 4 já reescreveram.
**Arquivos e conteúdo.**
- `docs/PROTOCOL.md`: §2.0.3 (ecos por nó), §3.7 (comandos por nó e tradução do Hub), §3.5
  atualizado (`speed`, decisão §3.7), §5 (perguntas de hardware: tamanho do quadro com ecos).
- `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`: "Comandos por nó", "Ecos por nó", "Diagnóstico dos nós".
- `External-Devices/docs/HUB_PROTOCOL_IMPROVEMENTS.md`: marcar "diagnósticos padronizados" e
  `boot_id` (fluxômetro) como feitos; `boot_id` nos demais nós como próximo.
- `docs/DECISIONS.md` **D-052** — "Configuração dos nós só pelo Hub; eco obrigatório antes de campo
  editável; carona na resposta do push para a distância; um `command` por revisão na biomassa;
  proxy de diagnóstico com tarefa própria; `speed` da bomba retirado do app".
- `docs/history/PHASE_LOG.md` **P3-10**; `docs/CHANGELOG.md` (Added/Changed); `docs/ROADMAP.md`
  (recibo de bancada §10.3; o item §I.4 passa a "instrumentado — sintonia exposta"; `boot_id` nos
  demais nós e OTA pelo app permanecem diferidos); `docs/CURRENT_STATUS.md`.
- `docs/MANUAL_DO_OPERADOR.md`: §7.D "Offset do sensor de distância", §7.E "Calibração da bomba
  externa", §6 "Sintonia do controlador de vazão (quando e como)", §4 "Saúde dos nós".
- `DocumentationCatalog`: já tocado nas Etapas 6–8; revisar em uma passada, `DocumentationEvidenceTests`
  regera a evidência em `docs/evidence/ui-documentation/` (commitar só se as capturas forem o ponto).
- Cabeçalho de estado deste plano: executado/desvios/pendências, como no plano anterior.

**Critério de pronto.** `dotnet test` verde; `DocumentationTests` cobre os tópicos novos; todos os
arquivos acima tocados no mesmo commit.
**Commit.** `docs: configuracao dos nos pelo hub (PROTOCOL 2.0.3/3.7, D-052, P3-10, protocolos dos nos)`.
**Esforço.** M.

---

## 6. Ordem, commits e esforço (resumo)

| # | Etapa | Componente | Esforço | Depende de |
|---|---|---|---|---|
| 1 | Caixa da distância + ecos | Hub | M | — |
| 2 | Distância `v11` | nó | P | 1 |
| 3 | Whitelists + ecos (fluxômetro, bomba, biomassa) | Hub | M | 1 |
| 4 | Ecos nos três nós (`v11`, `3.9`, `v11`) | nós | P ×3 | 3 |
| 5 | Fio, parser, catálogo, simulador | app | M | 1, 3 (decisão §3.7) |
| 6 | Distância + Vazão de Ar na UI | app | M | 5 |
| 7 | Bomba + Calibrações + Absorbância | app | M | 5 (6 recomendada) |
| 8 | Saúde dos nós | Hub + app | M + M | D-051 bancada; 5 |
| 9 | Documentação | docs | M | 1–8 |

P ≈ meio período; M ≈ um período. Total ≈ 9–10 períodos + bancada. Mínimo útil para o pedido do
operador (offset): **1, 2, 5, 6** (≈ 3,5 períodos).

---

## 7. Verificação global

### 7.1 Automatizada

```powershell
# Hub
python -m unittest discover ESP32S3-HUB/tests/contracts
.\External-Devices\tools\.bin\arduino-cli.exe compile --config-file .\External-Devices\tools\arduino-cli.local.yaml --fqbn esp32:esp32:esp32s3 .\ESP32S3-HUB\ESP32S3-HUB
# Nós
powershell.exe -ExecutionPolicy Bypass -File .\External-Devices\tools\Test-FirmwareBaselines.ps1
powershell.exe -ExecutionPolicy Bypass -File .\External-Devices\tools\Test-HubDeviceContracts.ps1
powershell.exe -ExecutionPolicy Bypass -File .\External-Devices\tools\Test-AgitatorManualParser.ps1
powershell.exe -ExecutionPolicy Bypass -File .\External-Devices\tools\Compile-ExternalDevices.ps1
# App
dotnet test Windows_app/OpenTECHub.slnx
```

### 7.2 Simulador (sem bancada)

Cada campo novo: digitar → frame esperado em `RawTelemetryReceived`/`CommandSent` → eco muda no
quadro seguinte → `Telemetria: {0}` acompanha. `--scenario node-dropout`: campos desabilitados com o
motivo. `--scenario legacy-hub`: nada novo aparece, nada quebra.

### 7.3 Bancada (recibo)

0. **Regravação da frota** (pré-requisito de tudo abaixo): Hub `10.2.0-dev` por serial ou OTA;
   os quatro nós por `Publish-OtaFirmware.ps1 -Device <nó> -Compile` (auto-descoberta via `/nodes`);
   `curl http://192.168.4.1/nodes` mostra `v11`/`3.9`/`v11`/`v11`/`v10` e a tabela *Nós na rede do
   Hub* não exibe nenhum aviso de firmware.
1. **Distância:** no app, Offset 20 → 25,5 → eco em ≤ 2 s;
   `curl http://<ip>/config` mostra `25.50`; reiniciar o sensor → persiste; `POST /config` direto
   continua a funcionar (compatibilidade).
2. **Fluxômetro:** com o alívio aberto após uma parada segura, ajustar `ramp_rate`/`kp_flow` no app
   e observar se o controlador passa a regular para baixo — fecha ou reformula o §I.4.
3. **Bomba:** *Zerar volume* → `PumpVol` 0 no quadro seguinte; calibração aplicada → `PumpSlope`
   ecoado; recibo em `Calibracoes/`.
4. **Biomassa:** três ajustes em sequência → três `cmd_id` e três acks no log do Hub.
5. **Quadro:** `Content-Length` de `/readData` com tudo ecoado — anotar em `docs/evidence/`
   (teto 2,6 KB).
6. **Saúde:** com o PC em USB, RSSI/heap dos cinco na tabela; desligar um nó → `code 0`; heap do
   Hub antes/depois anotado.

---

## 8. Riscos e decisões (fechadas em 12/09/2026 — recomendações adotadas pelo usuário)

| Risco / decisão | Tratamento |
|---|---|
| `speed` da bomba (§3.7) | **Fechado: retirar do app.** O quadro de segurança passa a `{"mode":0}`; `PROTOCOL.md` §3.5 registra a saída da paridade byte a byte com v.6 e o motivo (o Hub nunca repassou `speed`). |
| Nós ainda em `v10`/`3.8` | **Fechado: não há compatibilidade retroativa.** A frota inteira é regravada (§7.3.0); o catálogo lista só as versões novas; um nó antigo é um esquecido e o aviso de firmware da D-051 o denuncia. |
| Piggyback na distância perde comandos sob backoff (push a cada 15 s em falha) | `takeReliable` re-entrega em cada push até o ack; o app mostra "aguardando" pelo `DistanceCommandPending`. Se a bancada mostrar espera > 30 s, cair para a rota de *poll* (registrado como alternativa). |
| Uma caixa por nó = um comando pendente por vez | Fluxômetro: sintonia e setpoint entram na mesma revisão (documentado). Biomassa: fila no VM, um `command` por vez. Distância: os quatro campos vão numa frame só. |
| Tamanho do quadro na USB com todos os ecos | Emissão condicional a "já ecoado" e à presença; medir (§7.3.5); teto 2,6 KB → cortar `FlowOutput`/`FlowSetpointCorrected` primeiro. |
| Heap do Hub com a tarefa de diagnóstico | Buffers fixos 5 × 512 B; `HTTPClient` reutilizado; timeout 500 ms; medir; cair para 256 B se apertar. |
| Ajustar a sintonia do fluxômetro durante um ensaio | O árbitro recusa (aeração pertence ao ensaio); fora de ensaio, aviso de que é persistido no nó; proveniência grava a sintonia usada. |
| Headroom do fluxômetro (169 KB) | Push maior custa pouco (~40 B de formato), mas medir; se cair abaixo de 160 KB, retirar `flow_setpoint_corrected` do push. |
| `pid_*` da bomba sem eco nesta rodada | Campos existem, ficam desabilitados; próxima versão da bomba ecoa se o operador precisar (registrar no ROADMAP). |
