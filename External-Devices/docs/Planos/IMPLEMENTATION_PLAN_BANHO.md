# PLANO DE IMPLEMENTAÇÃO: BANHO EXTERNO (C404) COMO SEGUNDA VIA DO SETPOINT DE TEMPERATURA
## Hub 10.5, aplicativo Windows, aplicativo Android do Hub e ajustes no nó `banho-termostatico`

**Data de Emissão:** 2026-09-19
**Status:** Proposta — nenhum código alterado por este documento
**Referência Normativa:** `External-Devices/banho-termostatico/docs/PROTOCOL.md` (r2), `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`, `Windows_app/docs/PROTOCOL.md`, `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §6 (servo)
**Subsistemas Cobertos:**
- Nó do banho (`External-Devices/banho-termostatico/firmware/thermostatic-bath/`, r2 → r3)
- Gateway OpenTEC-Hub (`ESP32S3-HUB/ESP32S3-HUB/`, 10.4.0-dev → 10.5.0)
- Aplicativo Supervisor Windows (`Windows_app/`, 0.26.4 → 0.27.0)
- Aplicativo Android do Hub (`Android_app/`, Flutter `tecnal_app`)
- Documentação (`COMANDOS_DISPOSITIVOS_EXTERNOS.md` §7 novo, `Windows_app/docs/PROTOCOL.md`, `HUB_PROTOCOL_IMPROVEMENTS.md`)

O aplicativo Android **próprio** do banho (sem o Hub) tem plano separado: `IMPLEMENTATION_PLAN_BANHO_APP_ANDROID.md`.

---

## 1. Sumário Executivo

### 1.1 O problema e a ideia
O banho ultratermostático original do módulo TECNAL apresenta o problema que motivou o nó
`banho-termostatico`: um ESP32-S3 que "aperta" as teclas do controlador Contemp C404 de um
banho externo por relés e lê o seu display (r2: hold em malha fechada, modos manual/automático,
`sp_target` persistido). Hoje esse nó só é alcançável pelo seu próprio AP (`192.168.8.1`), pelo
`bath_app.py` e pela página `/ui`; o Hub responde `Unknown device type` ao seu `/nodeHello`.

A integração proposta segue **exatamente o que o servo drive fez com a agitação**: o servo não
criou um segundo setpoint de rotação — criou uma **segunda via** (`motorControlMode`) para o
mesmo `motorSetpoint`, escolhida num seletor dentro da linha de agitação. Aqui o banho externo
vira a **segunda via do `tempSetpoint`** (`tempControlMode`: 0 = módulo original por UART, 1 =
banho externo C404), com a gaveta do banho dentro da linha de temperatura. **Não** é um sétimo
cartão de "dispositivo externo" na interface.

No Hub, porém, o transporte é o dos nós Wi-Fi, não o do servo: o nó já fala o contrato de
**caixa confiável** (`ReliableMailbox`, `cmd_id`/`ack_cmd_id`, comando por carona na resposta
do push, `/nodeHello`), o mesmo do sensor de distância. O servo tem um canal próprio
(`/servoData` + `/servoCommand` com lease) porque nasceu antes da padronização 8.1; o banho
não precisa reproduzi-lo.

### 1.2 O que muda em cada camada (visão de 30 segundos)
| Camada | Muda | Não muda |
|---|---|---|
| Nó (r3) | Push ao Hub em tarefa própria (o hold silenciava o nó por dezenas de segundos); rota `/bathData`; `ver=r2`/`r3` no hello | Algoritmo de setpoint, modos, API local, `bath_app.py` |
| Hub (10.5) | `DEV_BATH` no registro; `bathBox` (caixa confiável); `/bathData` com carona; chaves `tempControlMode`, `bathComm`, `bathMode`, `bathSync`, `bathAbort`; campos `Bath*` e `TempControlViaBath` no `/readData`; via de temperatura em `processOutgoingCommands` | Comando `B` à placa original; `tempSetpoint` como única chave de setpoint; canal do servo; as outras cinco caixas |
| App Windows (0.27) | Gaveta "Banho externo" na linha de temperatura com seletor de via, cartão do banho, modo, sync, abort; alarmes; catálogo de nós; simulador; sessão | `SubsystemViewModel` de temperatura (só ganha `CanApplyNow` e faixa por via); PV da linha continua `Tempval` |
| App Android do Hub | Estado `ExternalBathState`, seletor de via na seção *Temperature Control*, cartão no dashboard | Demais seções |

### 1.3 Decisões de projeto (fechadas neste plano; ADRs em §8)
1. **Uma chave de setpoint.** `tempSetpoint` continua sendo o único setpoint de temperatura. A via decide para onde o Hub o entrega. A gaveta do banho não tem campo de setpoint próprio (o servo repete o campo de rotação na gaveta só por conveniência visual, ligado ao mesmo `SubsystemViewModel`; o banho pode fazer o mesmo).
2. **PV da linha continua sendo `Tempval`** (sensor do módulo no vaso). O banho publica o seu próprio PV (`BathPv`, sensor do C404 na água do banho), mostrado na gaveta como leitura secundária — como o torque do servo debaixo da rotação.
3. **Troca de via é *break-before-make*, sem reaproveitar setpoint**, como a rotação: ao ir para o banho externo o Hub desliga o controle térmico da placa original (`100B`) e ignora um `tempSetpoint` que venha no mesmo quadro; ao voltar, o banho externo **não** recebe nada (o C404 fica com o último SP; desligar um C404 remotamente não é possível — ele não tem comando de *off* pelas teclas) e a placa original só religa com um setpoint novo.
4. **`tempSetpoint = 0` na via do banho significa "liberar"**: o Hub não envia nada ao nó e passa a publicar `BathTarget` como está. É a mesma semântica de "desligado" que a placa tem, adaptada a um atuador que não desliga. O app mostra "sem setpoint comandado" em vez de "desligado".
5. **Sem *fallback* automático de via.** O `MotorRouteCoordinator` cai para UART/CN1 quando o servo some porque os dois caminhos movem o **mesmo** motor. Dois banhos são dois equipamentos; qual está encanado ao vaso é decisão física do operador. A via de temperatura é escolhida pelo operador e persistida; nó ausente vira alarme, não troca.
6. **Configuração do nó fica no canal local.** `hold_*`, `press_ms`, `sp_source`, `sense_*`, `mode_hold_ms`, `guard_delay_ms` não passam pelo Hub (economia de flash e de superfície de contrato; são ajustes de bancada). Pelo Hub passam só operação: setpoint, via, modo, sync, abort e o interruptor `bathComm`.
7. **Faixa por via.** A placa aceita 0–100 °C (`setTemperature` satura); o C404 aceita `in.L`–`in.H` (padrão do nó 5,0–90,0). O app valida o campo com a faixa da via ativa; o Hub repassa e deixa o nó recusar `range` (o erro aparece em `BathState`/`BathError`).

---

## 2. Arquitetura e Topologia

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ APLICATIVO WINDOWS (OpenTECHub 0.27)                                          │
│  ControlViewModel.TemperatureRow ─┬─ SubsystemViewModel(Temperatura, tempSetpoint) │
│                                   └─ gaveta: ExternalBathViewModel               │
│                                        via (tempControlMode), BathPv/Sp/Target,  │
│                                        modo, sync, abort, bathComm               │
│  TelemetryParser.ParseBath · AlarmService(ExternalBath*) · HubNodesViewModel     │
│  Simulator.DeviceModel(bath) · SessionLogger(colunas Bath*)                      │
└──────────────────────────────┬───────────────────────────────────────────────┘
                               │ USB/Wi-Fi: /command {"tempSetpoint":…,"tempControlMode":…}
                               ▼                          /readData {"Bath*":…}
┌──────────────────────────────────────────────────────────────────────────────┐
│ OPENTEC-HUB 10.5                                                             │
│  processOutgoingCommands: tempControlRoute == UartModule → "<sp*10>B" (UART)  │
│                           tempControlRoute == ExternalBath → queueReliable(bathBox) │
│  /bathData (push do nó, ack_cmd_id) → resposta = takeReliable(bathBox) (carona)│
│  /nodeHello?dev=bath · /nodes · /nodeDiag(bath /diag) · Telemetry Bath*       │
└──────────────┬───────────────────────────────────────────┬───────────────────┘
               │ UART "…B" (placa original, banho do módulo)  │ Wi-Fi STA → SoftAP do Hub
               ▼                                             ▼
   ┌──────────────────────┐                     ┌──────────────────────────────┐
   │ Módulo TECNAL (banho │                     │ Nó banho-termostatico r3     │
   │ original, sensor PV  │                     │ tarefa HubLink (push 1 s +   │
   │ do vaso = Tempval)   │                     │ comando por carona) → fila →  │
   └──────────────────────┘                     │ loop: SetpointManager/Guard  │
                                                │ relés → C404 → display       │
                                                └──────────────────────────────┘
```

Presença e via são ortogonais, como no servo (`ExternalDeviceStatus`): nó presente com via
no módulo é configuração; via no banho com nó ausente é o único estado de falha.

---

## 3. Contrato de Fio

### 3.1 Nó → Hub (push) — `GET /bathData`
O nó já monta este push (`FirmwareApp.cpp::pushToHub`), hoje para `/bath`. Parâmetros:

| Param | Tipo | Origem no nó | Obrigatório |
|---|---|---|---|
| `sp` | float | `g_spShadow` | sim |
| `known` | 0/1 | `g_spKnown` | sim |
| `target` | float | `g_spTarget` (último comandado, persistido) | sim |
| `state` | string | `idle/running/settling/done/error/aborted` | sim |
| `pv`, `pv_ok` | float, 0/1 | display superior do C404 (−1 se inválido) | sim |
| `mode` | 0/1 | manual/auto | sim (r2) |
| `dev`, `dev_ok` | float, 0/1 | `display_sp − sp_target` | sim (r2) |
| `err` | string | `seq_error` (`sp_mismatch`, …) | **novo em r3** |
| `time` | float s | uptime | sim |
| `ack_cmd_id` | uint | último `cmd_id` aplicado | sim |

Resposta do Hub: `200 application/json` com o comando pendente (`{"cmd_id":N,…}`) se houver,
senão `200 text/plain "Bath data received"` — idêntico ao `/distance`. O nó já trata os dois
casos (`body[0] == '{'`). Validação no Hub como no `/servoData`: parâmetros obrigatórios
ausentes → `400`; valores não finitos → `400` (`JsonUtils::parseFiniteFloat`).

### 3.2 Hub → Nó (caixa confiável `bathBox`)
Conteúdo interno (sem chaves externas), na sintaxe que o `ConfigCodec` do nó já aceita:

| Chave do app (JSON `/command`) | Hub roteia como | Regra no Hub |
|---|---|---|
| `tempSetpoint` (com `tempControlMode = 1`) | `"setpoint":<float>` | 0 = liberar (nada enviado); fora de [`in.L`,`in.H`] passa e o nó recusa `range` |
| `bathSync` | `"sync_sp":<float>` | float finito |
| `bathMode` | `"mode":"manual"` / `"auto"` | só `0/1` ou as duas strings |
| `bathAbort` | `"abort":1` | só valor 1 (como `distanceResetNvs`) |
| `bathComm` | — (interruptor do Hub) | persistido em NVS `bathComm`, padrão `false` como `pumpComm` |
| `tempControlMode` | — (via) | `0` módulo/UART, `1` banho; persistido em NVS `tempBath` como `motorModbus` |

Uma revisão por payload; `queueReliable` só com `bathCommOn`. Nunca chegam ao nó: `home`,
`key`, `hold_ms`, `reset_nvs`, configuração (§1.3 item 6).

### 3.3 Hub → App (`/readData`) — campos novos
Sempre publicados (presença observável, regra 8.1):

```json
"BathOnline":false,"BathCommEnabled":false,"BathCommandPending":false,
"BathCommandId":0,"BathCommandAck":0,"TempControlViaBath":false
```

Só com o nó presente (dentro de `BATH_TIMEOUT`):

```json
"BathSp":30.0,"BathKnown":true,"BathTarget":31.5,"BathState":"done","BathError":"",
"BathPv":29.8,"BathMode":1,"BathDeviation":0.0
```

`BathPv` e `BathDeviation` são `null` quando `pv_ok`/`dev_ok` = 0 (sentinela nunca vira
número no app — lição B13 da biomassa). Identidade e diagnóstico seguem o padrão 10.1/10.2:
`BathIP`, `BathNodeVer`, `BathNodeMac` no quadro; `/nodeDiag?dev=bath` lê o `/diag` do nó.
Orçamento: ~+280 B no pior caso; a reserva `HUB_TELEMETRY_JSON_RESERVE = 3072` comporta
(pior caso 10.2 estimado em ~2400 B), mas **remedir** após as adições da 10.4.

### 3.4 App → Hub — golden strings a fixar em `Windows_app/docs/PROTOCOL.md` §4 e nos testes
```text
{"tempControlMode":1}                       via do setpoint: banho externo
{"tempControlMode":0}                       via do setpoint: módulo (UART)
{"tempSetpoint":31.5}                       inalterado; a via decide o destino
{"bathComm":1} / {"bathComm":0}
{"bathMode":"auto"} / {"bathMode":"manual"}
{"bathSync":30.0}
{"bathAbort":1}
```

---

## 4. Mudanças por Camada

### 4.1 Nó `banho-termostatico` (r2 → r3) — pré-requisitos da integração
| # | Item | Arquivos | Detalhe |
|---|---|---|---|
| N1 | **Push em tarefa própria** | `src/core/FirmwareApp.cpp`, novo `src/network/HubLink.{h,cpp}` | Hoje `pushToHub()` roda no `loop()` e é suprimido enquanto `keypadBusy()` porque `httpGet` bloqueia até 2,5 s (alongaria um toque) — mas um hold dura dezenas de segundos e o Hub veria o nó **offline** no meio de cada setpoint longo. Solução: tarefa FreeRTOS no core 0 (`xTaskCreatePinnedToCore`, prioridade 1, pilha 6 kB) que a cada `send_period` monta o push a partir de um *snapshot* (`struct HubSnapshot`, copiado sob `portMUX`/mutex) e entrega o corpo da resposta numa fila (`QueueHandle_t` de `String*`, profundidade 2). O `loop()` consome a fila e chama `processCommand()` — o parser e o `SetpointManager` continuam single-threaded. O `hello` a cada 30 s vai para a mesma tarefa. `hub_enabled` continua desligado por padrão até a bancada (G8 → H1). |
| N2 | Rota e identidade | `src/config/BoardConfig.h` | `HubUrl = ".../bathData"` (nome alinhado a `/pumpData`, `/biomassData`, `/agitatorData`, `/servoData`); hello com `ver=r3` (derivar de `FirmwareTag` como o updater já faz, em vez do literal `v1`). |
| N3 | `err` no push | `FirmwareApp.cpp` | Acrescentar `&err=<seq_error>` (vazio quando não há); o `sp_mismatch` precisa chegar ao app para o alarme. |
| N4 | Carona com `cmd_id` | `ConfigCodec.cpp` | Já correto: reentrega do mesmo `cmd_id` responde `duplicate` sem reaplicar toques; `last_cmd_id` só avança em ação aceita. Cobrir com um cenário no `tests/host-sim` (comando por carona repetido três vezes = uma sequência). |
| N5 | Documentação | `docs/PROTOCOL.md` §6, `CHANGELOG.md`, `CURRENT_STATUS.md` | §6 deixa de ser "não implementada no Hub" e passa a apontar para este plano; tabela de campos = §3.1 acima. |

Tamanho: a tarefa adiciona ~2 kB de flash e 6 kB de RAM de pilha; o nó está em 82 % de flash (1 081 485 B), sem risco.

### 4.2 Hub (10.4.0-dev → 10.5.0)
| # | Arquivo | Mudança |
|---|---|---|
| H1 | `src/core/AppContext.h` | `DEV_BATH` antes de `DEV_COUNT` (5 → 6); entrada `{ "bath", … }` em `g_deviceRegistry`; `ReliableMailbox bathBox`; estado: `float bathSp, bathTarget, bathPv, bathDev; bool bathKnown, bathPvOk, bathDevOk; uint8_t bathMode; char bathState[10]; char bathErr[32]; unsigned long bathLastUpdate; bool bathCommOn;` `enum class TempControlRoute : uint8_t { UartModule = 0, ExternalBath = 1 }; TempControlRoute tempControlRoute = TempControlRoute::UartModule; volatile bool flagTempRouteDirty;` Cabeçalho da v9: parágrafo "10.5: banho externo como segunda via do setpoint de temperatura". |
| H2 | `src/protocol/Mailboxes.h` | `&bathBox` em `seedReliableMailboxes()` (semente aleatória por boot, regra 10.2 do `cmd_id`). |
| H3 | `src/network/HttpServer.h` | Handler `/bathData` espelhado no `/distance` (§3.1): valida obrigatórios, `recordDeviceActivity(DEV_BATH, …)`, `ackReliable(bathBox, readAckParam(request), "Bath")`, resposta = `takeReliable(bathBox)` ou texto. `/nodeHello`: `else if (devName == "bath") devId = DEV_BATH;`. `/nodes` e `/nodeDiag` percorrem `DEV_COUNT` — verificar que o buffer `resp[1280]` de `/nodes` comporta seis entradas (hoje cinco com IP/MAC/versão: ~200 B cada → 1280 fica justo; subir para 1536). |
| H4 | `src/network/NodeDiagTask.h` | Percorre `g_deviceRegistry`; ganha o banho de graça. Conferir `NodeDiagCache` (dimensionado por `DEV_COUNT`). |
| H5 | `src/protocol/Commands.h` | Bloco `bathComm` (cópia de `pumpComm`); bloco `tempControlMode` (cópia de `motorControlMode`: valida `0/1`, muda `tempControlRoute`, `flagTempRouteDirty = true`, marca `tempRouteChangedInFrame` e **ignora `tempSetpoint` do mesmo quadro** com aviso); bloco `bathMode`/`bathSync`/`bathAbort` → `bathInner` → `queueReliable(bathBox, bathInner, "Bath")` se `bathCommOn`. O hash de estado (`hash += …`) ganha `tempControlRoute` e `bathCommOn`. |
| H6 | `src/core/Runtime.h` | `processOutgoingCommands()`: (a) `if (flagTempRouteDirty)`: ao entrar na via do banho, `sendSensorCommand("100B")` (placa original desligada) e `tempOn = false`; ao voltar ao módulo, nada ao nó; `flagTempDirty = false` (setpoint anterior não é reaproveitado); (b) no bloco `3. TEMPERATURE UPDATE`: `if (tempControlRoute == ExternalBath) { if (tempReference < 0.001) log "liberado"; else queueReliable(bathBox, "\"setpoint\":" + String(tempReference,1), "Bath"); tempOn = tempReference > 0; } else { código atual }`. Se `!bathCommOn`, avisar e descartar (como `setMotor` faz com `servoCommOn`). |
| H7 | `src/sensor/Telemetry.h` | Snapshot dos `bath*` sob `stateMutex`; `bathOnline = bathLastUpdate > 0 && millis() − bathLastUpdate <= BATH_TIMEOUT` (`BATH_TIMEOUT = 5000` em `Config.h`, coerente com push de 1 s e com N1); campos de §3.3; `null` para `pv`/`dev` inválidos; atualizar o comentário de orçamento da reserva. |
| H8 | `src/storage/Settings.h` | `bathComm` (bool, padrão `false`) e `tempBath` (bool, padrão `false`) em `saveSettings`/`loadSettings`/`printSettings`. |
| H9 | `Config.h` | `HUB_FIRMWARE_VERSION "10.5.0"`, `BATH_TIMEOUT`, comentário sobre `{"bathComm":…}` ao lado do de `servoComm`. |
| H10 | `tests/contracts/` | `test_node_registry.py`: `DEVICES += ("bath",)`, `PREFIXES += ("Bath",)` (a segunda classe lê o fonte e conta as chaves — atualizar a contagem); novo `test_bath_mailbox.py` (modelo da carona: push com `ack_cmd_id` errado mantém o pendente; `ack` igual limpa; semente por boot não colide com eco antigo — copiar do modelo da distância); `test_json_keys.py` e `test_http_frames.py` com os campos de §3.3 e os frames de §3.4; novo `test_temp_route.py` espelhando `test_servo_motor_command.py` (troca de via ignora setpoint do mesmo quadro; `100B` na saída; setpoint 0 na via do banho não enfileira). |
| H11 | `docs/WIRE_CONTRACT_V9.md`, `ARCHITECTURE.md`, `VALIDATION.md` | Seção do banho; item de validação H1–H6 (§6.4 abaixo). |

Flash: a 10.2 estava em 85 % (1 125 420 B). Um nó de caixa confiável custa ~5–8 kB; estimar
10.5 em ~86–87 %. Se a 10.4 já estiver acima de 88 %, retirar antes as mensagens `ESP32_INFO`
redundantes do `printSettings` — decisão a tomar na compilação do M2, nunca depois.

### 4.3 Aplicativo Windows (0.26.4 → 0.27.0)
| # | Arquivo | Mudança |
|---|---|---|
| W1 | `OpenTECHub.Protocol/CommandKeys.cs` | Comando: `TempControlMode`, `BathComm`, `BathMode`, `BathSync`, `BathAbort`. Telemetria: `BathOnline`, `BathCommEnabled`, `BathCommandPending`, `BathCommandId`, `BathCommandAck`, `TempControlViaBath`, `BathSp`, `BathKnown`, `BathTarget`, `BathState`, `BathError`, `BathPv`, `BathMode`, `BathDeviation`, `BathIP`, `BathNodeVer`, `BathNodeMac`. |
| W2 | `OpenTECHub.Protocol/CommandActuators.cs` | `tempControlMode` mapeado a `ActuatorId.Temperature` (como `motorControlMode` → `Agitation`): trocar a via exige a posse do atuador de temperatura no `CommandArbiter`. `bathMode`/`bathSync`/`bathAbort` também sob `Temperature`. |
| W3 | `OpenTECHub.Protocol/CommandBuilders.cs` | `TempControlMode(bool viaBath)`, `BathComm(bool)`, `BathMode(bool auto)`, `BathSync(double)`, `BathAbort()`; faixas lançam como os demais. |
| W4 | `OpenTECHub.Protocol/SensorReadings.cs` | `HasBathTelemetry`, `BathOnline`, `BathCommEnabled`, `BathCommandPending`, `BathCommandId/Ack`, `TempControlViaBath`, `BathSp`, `BathKnown`, `BathTarget`, `BathState`, `BathError`, `BathPv` (nullable), `BathMode`, `BathDeviation` (nullable), `BathLastSeenAt`; cópia no `Clone()`/`With`. |
| W5 | `OpenTECHub.Protocol/TelemetryParser.cs` | `ParseBath(root, now)` com `Presence` e `BathTimeout = 5 s` (como `ParsePump`); `HasBathTelemetry = false` com Hub antigo (o app não pode inventar presença). |
| W6 | `ViewModels/ExternalBathViewModel.cs` (novo) | Espelho do `ServoDriveViewModel`: `Status : ExternalDeviceStatus` (presença × roteamento), `IsTempControlViaBath` (toggle; ao mudar envia `TempControlMode` pelo `IManualDispatcher`; reverte se recusado, como `OnIsMotorControlViaModbusChanged`), `IsTempControlRouteAvailable` (= `HasBathTelemetry`), `TempControlRouteText`, `PvText`, `SpText`, `TargetText`, `StateText`, `ModeText`, `DeviationText`, `IsAuto` (toggle → `BathMode`), `SyncCommand` (campo + `BathSync`), `AbortCommand`, `IsCommEnabled` (→ `BathComm`), `IsCommandPending`; jornal (`IEventJournal`) nas transições de presença, de via e em cada `sp_mismatch`. |
| W7 | `ViewModels/ControlViewModel.cs` | Propriedades `ExternalBath` e `IsExpandedExternalBath`; injeção no construtor; `TemperatureRow` recebe o `IconKey` de expansor. `SubsystemSpec` da temperatura: `CanApplyNow = () => !bath.IsTempControlViaBath || (bath.Status.IsOnline && bath.IsCommEnabled)` com mensagem "Via do banho externo sem nó presente"; `Minimum/Maximum` trocam para 5–90 quando a via é o banho, pelo mesmo mecanismo com que o fluxo ajusta o máximo (`_spec = _spec with { Maximum = … }`, `SubsystemViewModel.cs:379`) — generalizar para `Minimum` também. |
| W8 | `Views/ControlView.xaml` | Na linha `1. Temperatura`: `ToggleButton` de expansor (`RowExpanderToggleStyle`, `IsExpandedExternalBath`, tooltip "Banho externo: via do setpoint e estado do C404"); PV do banho sob o PV do vaso (como o torque sob a rotação); gaveta (`Border` com `Visibility` do expansor) com: título "Banho externo — Contemp C404", texto "Segunda via do setpoint de temperatura. O sensor do vaso continua sendo o do módulo.", seletor "Via do setpoint de temperatura" (`Módulo (UART)` ⟷ `Banho externo C404`, `ConnectedExternalDeviceToggleStyle`, `IsEnabled={IsTempControlRouteAvailable}`, tooltip explicando o *break-before-make*), cartão de estado (PV do banho, SP no C404 + "desconhecido", alvo, sequência/fase, erro), modo manual/automático com desvio, `Sincronizar` (campo numérico) e `Abortar`, interruptor "Banho no Hub" (`bathComm`) com o texto do servo adaptado. |
| W9 | `Services/Alarms/AlarmModels.cs`, `AlarmService.cs` | `ExternalBathOffline` (via = banho ∧ `bathComm` ∧ `!BathOnline`, on-delay 10 s, latching como `ServoDriveOffline`); `ExternalBathSetpointMismatch` (`BathError == "sp_mismatch"` ou `BathKnown == false` com via = banho); `ExternalBathDeviation` (modo manual, `|BathDeviation| ≥ 0,2 °C` por 30 s — aviso, não alarme). Sons/ack como os existentes. |
| W10 | `Services/Communication/NodeFirmwareCatalog.cs` | `Bath = "bath"`; `Devices += Bath`; `Validated[Bath] = { "r3" }`. `HubNodesViewModel` e o `NodeNetworkPanel` listam seis nós sem outra mudança (conferir colunas fixas). |
| W11 | `Services/Persistence/AppSettings.cs`, `SettingsViewModel` | `TemperatureRoute` (`Module`/`ExternalBath`) e `BathComm` persistidos ao lado dos demais flags de roteamento; restaurar **nunca envia** (regra existente). Reenvio no handshake como `motorControlMode`. |
| W12 | `Services/Telemetry/SessionLogger.cs`, `SessionFiles.cs`, `TelemetryHistory.cs`, `ChartsViewModel.cs` | Colunas `BathPv`, `BathSp`, `BathTarget`, `BathMode`, `TempRoute` no CSV principal (poucas colunas; sem *sidecar* como o do servo). `BathPv` como série opcional no gráfico de temperatura. |
| W13 | `Services/Recipes/RecipeEngine.Actuation.cs`, `RecipeEngine.Devices.cs`, `RecipeValidator.cs` | Passo de temperatura com via = banho: após enviar, aguardar `BathCommandAck == BathCommandId` **e** `BathState ∈ {done}`; `error`/`aborted` → o bloco entra em *hold* com o alarme `RecipeExternalDeviceHold` já existente; timeout do bloco = `2 × (|Δ| / 10 toques/s) + 20 s` até a bancada medir (G3b). O validador avisa quando a receita usa temperatura e a via configurada é o banho sem nó presente. |
| W14 | `OpenTECHub.Simulator/DeviceModel.cs`, `WireCodec.cs` | Nó simulado `bath`: `sp/known/target/state/pv/mode/dev`, sequência com duração proporcional à distância (usa os tempos do `tests/host-sim`), `sp_mismatch` injetável, presença ligável; chaves `Bath*` no `/readData` simulado; `tempControlMode` aceito. `SimulatorNodeConfigTests` ganha o banho. |
| W15 | `App.xaml.cs` | Registro do `ExternalBathViewModel` (ao lado da linha 444, `ServoDriveViewModel`). |
| W16 | `tests/OpenTECHub.Tests/ExternalBathTests.cs` (novo) | Parser: presença/timeout/`null`; builders: golden strings §3.4; VM: toggle de via recusado reverte, `CanApplyNow` bloqueia com nó ausente, faixa 5–90 na via do banho; alarmes: latch/on-delay; receita: espera ack+done, *hold* em `error`; `HubNodesViewModelTests` (6 nós); `SafetyCoordinatorTests` se a parada de emergência precisar tocar a via (decisão: parada de emergência **não** muda a via; envia `tempSetpoint 0` que na via do banho é "liberar" — registrar no ADR). |
| W17 | Docs | `docs/PROTOCOL.md` (§2.0.1 presença `Bath*`, §3.x chaves, §4 golden strings), `UI_DESIGN.md` (gaveta de temperatura, regra "via ≠ segundo setpoint"), `DECISIONS.md` (ADR "Banho externo é via, não dispositivo"; ADR "setpoint 0 na via do banho = liberar"; ADR "sem fallback de via térmica"), `ROADMAP.md` (Fase 4 WP1), `CHANGELOG.md` (0.27.0), `CURRENT_STATUS.md`, `PONTOS_DE_MELHORIA_EXPOSICAO_NOS.md` §0 (linha do banho), `MANUAL_DO_OPERADOR.md` (como trocar a via e o que significa). |

### 4.4 Aplicativo Android do Hub (`Android_app`)
| # | Arquivo | Mudança |
|---|---|---|
| A1 | `lib/models/external_bath_state.dart` (novo) | `fromJson` dos campos §3.3 com a mesma semântica de presença de `peristaltic_pump_state.dart` (`BathOnline` × `BathCommEnabled`; `null` preservado em PV/desvio); `viaBath` de `TempControlViaBath`. |
| A2 | `lib/providers/telemetry_provider.dart` | `_bathState = ExternalBathState.fromJson(json)` ao lado de `_servoState`/`_distanceState`; getter. |
| A3 | `lib/providers/device_control_provider.dart` | `setTempControlMode(bool viaBath)` → `{"tempControlMode": 0|1}`; `setBathComm(bool)`; `setBathMode(bool auto)`; `bathSync(double)`; `bathAbort()`; o `resetAll` (linha ~360, `"tempSetpoint": 0.0`) não toca na via. |
| A4 | `lib/screens/controls_screen.dart` | Dentro de `ControlSectionCard("Temperature Control")`: sub-bloco "Setpoint route" com `SwitchListTile` `Module (UART)` / `External bath (C404)` (mesmo desenho do interruptor do servo, linhas ~275–290), desabilitado sem telemetria do banho; linha de estado (PV do banho, SP/alvo, sequência, modo, desvio); botões `Manual`/`Auto`, `Sync`, `Abort`; interruptor `bathComm` com a mesma explicação dos outros nós. |
| A5 | `lib/widgets/external_bath_card.dart` (novo), `screens/dashboard_screen.dart` | Cartão no dashboard espelhando `distance_sensor_card.dart` (presença, via ativa, PV/SP/alvo, modo). |
| A6 | `lib/screens/graphs_screen.dart` | Série opcional `BathPv` junto de `Tempval`. |
| A7 | `lib/constants/api_constants.dart`, `test/` | Constantes das chaves; testes de `fromJson` (fixtures com e sem os campos, `null`), teste de widget do seletor desabilitado sem telemetria. |
| A8 | `pubspec.yaml`, `README.md` | `version: 1.1.0+2`; README com a seção do banho. |

### 4.5 Documentação transversal
- `COMANDOS_DISPOSITIVOS_EXTERNOS.md`: novo **§7 Banho externo (`banho-termostatico`, firmware r3)** com o mesmo esqueleto dos outros (7.0 painel de prontidão, 7.1 hardware assumido, 7.5 por onde chega um comando, 7.6 catálogo de chaves, 7.8 telemetria, 7.9 procedimentos do operador, 7.10 limitações/decisões, 7.11 checklist); a "Visão Geral de Prontidão" ganha a linha do banho.
- `HUB_PROTOCOL_IMPROVEMENTS.md`: matriz JSON ganha o nó (parser manual, `cmd_id` sem reaplicação, `range` validado no nó); nota de que o banho nasce com `/diag`, `ver=`/`mac=` e caixa confiável desde o primeiro dia.
- `External-Devices/README.md`: banho deixa de ser "ainda sem integração com o Hub".

---

## 5. Semântica da Via (o que o operador vê e o que acontece no fio)

| Situação | Módulo (UART) — via 0 | Banho externo — via 1 |
|---|---|---|
| Enviar 31,5 °C | `315B` na UART; `tempOn = true` | `{"cmd_id":N,"setpoint":31.5}` por carona no próximo push (≤ 1 s); nó executa hold/toques; `BathCommandAck = N` quando aplicado; `BathState` `running → settling → done` |
| Enviar 0 (desligar) | `100B`; `tempOn = false` | Nada ao nó ("liberado"); `BathTarget` inalterado; app mostra "sem setpoint comandado" |
| Trocar 0 → 1 | `100B` imediatamente; setpoint do mesmo quadro ignorado | Aguarda novo `tempSetpoint`; `TempControlViaBath = true` |
| Trocar 1 → 0 | Aguarda novo `tempSetpoint` | Nó não recebe nada; C404 mantém o SP (fisicamente ainda pode estar aquecendo — o operador decide o encanamento) |
| Nó ausente | irrelevante | `ExternalBathOffline`; `CanApplyNow` recusa; comando pendente fica retido na caixa até o nó voltar (semântica da caixa confiável — o app mostra "pendente") |
| Operador mexe no C404 | irrelevante | modo manual: `BathDeviation ≠ 0` (aviso); modo auto: o nó reverte sozinho após `guard_delay_ms`; `BathMode` na telemetria |
| Receita com passo de temperatura | como hoje | espera `ack` + `done`; `error`/`aborted` → *hold* da receita |
| Parada de emergência | `tempSetpoint 0` → `100B` | `tempSetpoint 0` → liberar (o C404 continua com o SP; documentar que a parada **não** desliga um banho externo) |

---

## 6. Plano de Validação

### 6.1 Nó
`tests/host-sim`: cenário "carona": três entregas do mesmo `cmd_id` → uma sequência; `ack_cmd_id` ecoado no snapshot. Bancada: G1–G9 do `VALIDATION.md` (pré-requisito absoluto: G3b mede a auto-repetição que o simulador supõe).

### 6.2 Hub
`python -m unittest discover tests/contracts` (hoje 86 contratos) + os novos de H10. Compilação com `ESP32S3-HUB/tools/compile.ps1`; registrar flash/RAM no `CHANGELOG`.

### 6.3 Aplicativos
`dotnet test Windows_app/OpenTECHub.slnx` (1255+ testes verdes hoje) com `ExternalBathTests`; `flutter analyze && flutter test` no `Android_app`. Capturas em `docs/evidence` só se a gaveta for o objeto do commit.

### 6.4 Bancada de integração (após G1–G9)
| # | Ensaio | Critério |
|---|---|---|
| H1 | Nó com `hub_enabled = 1` junto ao Hub 10.5 | `/nodes` mostra `bath` registrado com `ver=r3`, `BathOnline = true` estável **durante um hold de 60 s** (prova N1) |
| H2 | `{"tempControlMode":1}` pelo app | Placa original recebe `100B`; `TempControlViaBath = true`; gaveta muda de texto |
| H3 | `{"tempSetpoint":40.0}` | Nó executa; `BathCommandAck` avança; `BathState = done`; `BathSp = 40.0` em menos de `(|Δ|/taxa) + 5 s` |
| H4 | Reentrega: cortar o Wi-Fi do nó por 3 s no meio do H3 | O mesmo `cmd_id` chega de novo; o nó responde `duplicate`; nenhum toque extra (contar `presses_total`) |
| H5 | Operador muda o SP no C404 em modo auto | Nó reverte; `BathDeviation` volta a 0; app registra no jornal |
| H6 | Volta à via 0 e `{"tempSetpoint":30.0}` | Placa recebe `300B`; nó não recebe nada; `BathTarget` fica em 40,0 (mostrado como informação, não como erro) |

---

## 7. Cronograma e Marcos

| Marco | Conteúdo | Depende de | Fecha com |
|---|---|---|---|
| M0 | Bancada do nó: G1–G9 (`banho-termostatico/docs/VALIDATION.md`) | hardware | valores reais de `hold_*`, resposta a `▲+▼`, `sp_source = 1` |
| M1 | Nó r3: N1–N5 | M0 | host-sim verde, compilação, `CHANGELOG` do nó |
| M2 | Hub 10.5: H1–H11 | M1 (contrato `/bathData`) | contratos verdes, flash medido, `WIRE_CONTRACT` |
| M3 | App Windows 0.27: W1–W17 | M2 (chaves) — pode começar contra o simulador em paralelo | `dotnet test` verde, PROTOCOL/UI_DESIGN/DECISIONS/CHANGELOG |
| M4 | App Android do Hub: A1–A8 | M2 | `flutter test` verde |
| M5 | Bancada de integração H1–H6 + `COMANDOS` §7 + `PONTOS` §0 | M1–M4 | evidências em `docs/evidence` e `CURRENT_STATUS` dos três componentes |

Compatibilidade durante a transição: app 0.27 com Hub 10.4 → `HasBathTelemetry = false`, gaveta
mostra "Hub sem suporte ao banho externo" e o seletor fica desabilitado; Hub 10.5 sem nó →
`BathOnline = false` sempre publicado, nada mais muda. Um commit por camada, nunca misturando
`Windows_app`, `ESP32S3-HUB`, `Android_app` e `External-Devices` (política de commits).

---

## 8. Riscos, Decisões Abertas e ADRs a registrar

| # | Item | Tratamento |
|---|---|---|
| R1 | Hold silencia o nó (push suprimido com `keypadBusy`) → Hub declara offline no meio de todo setpoint longo | **N1** (tarefa de push). Sem N1, `BATH_TIMEOUT` teria de ser ≥ 60 s e a presença perderia o sentido. |
| R2 | `100B` desliga mesmo o controle térmico da placa original? | Confirmar na bancada (H2) medindo a saída do módulo; se não, a via 1 precisa de outro "off" da placa antes de valer. |
| R3 | `Tempval` é o sensor do vaso, não da água do banho interno? | Confirmar antes de M3: se for da água do banho interno, a linha precisa de outro PV na via 1 (então `BathPv` vira o PV principal e o ADR muda). |
| R4 | Flash do Hub (85 % em 10.2; 10.4 não medido) | Medir no início de M2; cortar logs antes de cortar campos. |
| R5 | Faixa 0–100 do app × `in.L`/`in.H` do C404 | Faixa por via no app (W7); nó recusa `range` e o erro sobe como `BathError`. |
| R6 | Guarda automático do nó × receita | Enquanto a receita comanda, o alvo do guarda é o alvo da receita (o nó só defende `sp_target`); coerente. Documentar que trocar a via para 0 **não** desliga o guarda do nó (ele fica defendendo o último alvo) — o operador deve pôr o nó em manual antes de abandonar a via, e a gaveta avisa isso. |
| R7 | Parada de emergência não desliga um C404 | ADR + texto na interface. |
| R8 | Dois banhos encanados ao mesmo tempo | Fora do escopo do software; documentar no manual do operador. |

ADRs (para `Windows_app/docs/DECISIONS.md` e `ESP32S3-HUB/docs/ARCHITECTURE.md`):
1. **Banho externo é via, não dispositivo** — `tempControlMode` como `motorControlMode`; uma chave de setpoint.
2. **Transporte de nó, não de servo** — caixa confiável com carona (`/distance`), presença por push, `/nodeHello`.
3. **`tempSetpoint = 0` na via do banho = liberar**; nem parada de emergência nem troca de via desligam o C404.
4. **Sem fallback automático de via térmica** (dois equipamentos, decisão física).
5. **Configuração do nó fica local** (Hub roteia só operação).
