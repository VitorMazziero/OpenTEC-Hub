# Plano de implementação: padronização dos dispositivos externos

> **Data:** 2026-08-29 · **Base:** `d221db7` em `codex/receitas-fluxometro-pos-merge`
> **Alvo:** 0.25.0 · **Referência de estilo:** fluxômetro v05 (`FlowControlViewModel`, linha "Vazão de Ar")
>
> **Docs:** [PROTOCOL](PROTOCOL.md) · [CURRENT_STATUS](CURRENT_STATUS.md) · [ROADMAP](ROADMAP.md) · [UI_DESIGN](UI_DESIGN.md) · [DECISIONS](DECISIONS.md) · [Plano do fluxômetro](FLOWMETER_V05_HUB_V7_SYNC_PLAN.md)

## Objetivo

Aplicar aos cinco dispositivos externos restantes o mesmo contrato que o fluxômetro v05 já
tem com o Hub: **presença observável, comando confirmado, estado do enlace separado do estado
do interruptor do operador**. Hoje o fluxômetro é a única linha da página Controle que diz a
verdade sobre o dispositivo do outro lado; as demais mostram o próprio checkbox do operador
como se fosse o dispositivo.

Este documento é o plano de correção. Ele parte de uma leitura dos seis firmwares e da camada
`TecnalHub.Protocol` / ViewModels / XAML, e separa o que é corrigível **só no app** do que
exige **regravação de firmware** (seção 4, para upload pelo operador).

---

## 1. O que os firmwares realmente fazem hoje

Leitura de 2026-08-29 sobre:

| Dispositivo | Firmware lido |
|---|---|
| Hub | `TECNAL_ESP32_v8/TECNAL_ESP32_v8.ino` (2306 linhas) |
| Fluxômetro | `flowmeter_TECNALHUB_V05.ino` |
| Biomassa | `biomass_sensor_analog_v04_direct.ino` |
| Bomba peristáltica | `v_3_2_DC_motor_peristaltic.ino` |
| Frasco agitador | `frasco_agitador_03.ino` |
| Sensor de distância | `SensorDistanciaTECNAL_v03_reconnect.ino` |

### 1.1 Topologia real

O Hub é um SoftAP `ModuloTECNAL_1` (`WiFi.softAP(..., canal 6, máx. 8 estações)`). Cada
dispositivo externo é uma estação que:

- **empurra** telemetria por `GET /<algo>Data?...` (query string, não JSON), e
- **puxa** comando por `GET /<algo>Command`, respondido com um objeto JSON.

O PC nunca fala com um dispositivo externo. Ele fala só com o Hub (`POST /command`,
`GET /readData`), e o Hub reencaminha. **Toda a observabilidade que o app pode ter de um
dispositivo externo tem que caber no JSON de `/readData`.**

### 1.2 Matriz de comportamento — a assimetria a corrigir

| | **Fluxômetro** | **Biomassa** | **Bomba** | **Agitador** | **Distância** | **Servo (v8)** |
|---|---|---|---|---|---|---|
| Empurra telemetria | `/flowData` a 2 Hz | `/biomassData` — **só ao publicar amostra** | `/pumpData` a 1 Hz | **nenhuma** | `/distance` a 1 Hz | `/servoData` |
| Puxa comando | `/flowCommand` a **10 Hz** | `/biomassCommand` a 0,5 Hz | `/pumpCommand` a 0,5 Hz | `/agitatorCommand` a 2 Hz | — (não recebe comando) | `/servoCommand` |
| Caixa de comando no Hub | **revisionada, retida até ACK** | consome-na-leitura | consome-na-leitura | consome-na-leitura | — | consome-na-leitura |
| `cmd_id` / idempotência | **sim** (`lastAppliedHubCommandId`) | não | não | não | — | não |
| ACK de volta ao Hub | **sim** (`ack_cmd_id` em `/flowData`) | não | não | não | — | não |
| Timeout de presença no Hub | **6 s** → `FlowmeterOnline:false` | 10 s → **omite as chaves** | **nenhum** | — | 1,2 s → **omite a chave** | 6 s → `ServoOnline` |
| Flag de presença publicada | `FlowmeterOnline` **sempre presente** | não — só ausência de chave | não — só ausência de chave | não existe | não — só ausência de chave | `ServoOnline` **sempre presente** |
| Flag de habilitação publicada | `FlowControlEnabled` | **não** | **não** | não existe | **não** | **não** |
| Comando bloqueado pelo `*Comm` | **não** (sempre enfileira) | **sim** | **sim** | não | — | sim |

### 1.3 Defeitos concretos que essa assimetria produz

**D-1 — A bomba externa publica valores mortos indefinidamente.**
`readAndBroadcastSensorData()` emite o bloco `Pump*` sob `if (pumpCommOn)` e **não existe
`pumpLastUpdate`**. Se o nó da bomba cair, o Hub continua republicando o último `PumpFlow`,
`PumpVol`, `PumpActive:true` para sempre. O app exibe uma vazão que não está acontecendo, e o
acoplamento de gás proporcional (`Q_g = (V₀ + PumpVol/1000)·vvm`) continua calculando em cima
de um volume congelado. É o pior dos cinco casos, e é uma correção **de firmware**.

**D-2 — "Biomassa offline" e "biomassa parada" são indistinguíveis.**
O nó de biomassa só chama `sendDataToHub()` de dentro de `publishSample()`, isto é, **apenas
enquanto MEASURING**. Em IDLE ele não empurra nada, o `biomassLastUpdate` envelhece em 10 s, o
Hub omite `BiomassAbs/Raw/IT/PWM`, e o app não tem como saber se o sensor foi parado ou se o
ESP32 caiu. Pior: `TelemetryParser.ParseBiomass` é **pegajoso** — nunca envelhece —, então a
absorbância de dez minutos atrás fica na tela como se fosse atual.

**D-3 — O agitador de frasco é completamente cego.**
Ele **nunca** empurra telemetria ao Hub. Não há endpoint `/agitatorData`, não há campo
`Agitator*` em `/readData`. O `/agitatorHello` só devolve o IP ao próprio nó, sem registrar
nada no Hub. O app envia `agitatorOn/agitatorPercent/agitatorDir` e não recebe nenhuma
evidência de que chegaram.

**D-4 — O "desligar" do agitador pode não desligar.**
`agitatorOn:0` faz o Hub enfileirar `{"RPM_percent":0,"Dir":d,"ActivePot":agitatorReEnablePot?1:0}`.
Com `agitatorReEnablePot = true` (o padrão persistido), `ActivePot:1` reabilita o potenciômetro,
e o `loop()` do firmware volta a escrever `targetPercent = rawToPercent(potRaw)` no ciclo
seguinte. **Um "parar" com o potenciômetro na bancada em 60 % religa o motor em 60 %.** Isso
entra no caminho de parada segura do operador e é uma decisão de projeto do app (seção 5.4),
não um bug de firmware.

**D-5 — Comando perdido é comando perdido, para quatro dos cinco.**
`takePending()` limpa a caixa na leitura. Se a resposta HTTP se perder no ar, o comando
desaparece sem que ninguém saiba. Pior, `setPending()` **sobrescreve**: `blank` seguido de
`start` dentro da janela de 2 s de polling da biomassa entrega só o `start`. O app hoje habilita
os três botões momentâneos sem nenhuma serialização.

**D-6 — Os interruptores `*Comm` são write-only.**
O Hub **persiste** `bioComm`, `distComm`, `pumpComm`, `servoComm` e `flowComm` em NVS, mas
**só republica `FlowControlEnabled`**. Depois de um reboot do Hub, o app carrega o estado dos
checkboxes de `AppSettings` (PC) e o Hub carrega o dele da NVS. Os dois divergem em silêncio.
O operador vê "Biomassa: Ativo" com `biomassCommOn == false` no Hub, e todo `blank`/`start`/
`stop`/limiar é descartado por `if (biomassCmdFound && biomassCommOn)` sem uma palavra.

**D-7 — O app ignora metade da telemetria da bomba.**
O Hub publica `PumpMode`, `PumpPWM`, `PumpSpeed`, `PumpFlow`, `PumpVol`, `PumpTargetVol`,
`PumpActive`, `PumpWaiting`. `TelemetryKeys` conhece **duas**: `PumpFlow` e `PumpVol`. O
`PumpTargetVol` (volume-alvo calculado pelo próprio nó) e o par `PumpActive`/`PumpWaiting`
(estado da máquina do perfil) são exatamente o que a linha da bomba precisaria mostrar.

**D-8 — O bloco `Servo*` do Hub v8 existe no fio e não no app — [**FORA DE ESCOPO**].**
O v8 acrescentou um nó Modbus RS-485 do servo-drive Delta ASDA-B2 que publica `ServoOnline`,
`ServoRpm`, `ServoTorquePct`, `ServoTorqueNm`, `ServoLoadPct`, `ServoPowerW`, `ServoEnergyWh`,
`ServoState`, `ServoAlarm`, `ServoCommOk/Err`, e aceita `servoComm`, `resetServoEnergy` e
`servoPollMs`.

> **Adiado por decisao do operador (29/08/2026).** As ligacoes RS-485 do ASDA-B2 ainda nao foram
> feitas e o no sera validado em bancada contra a v8 antes de qualquer trabalho no app. Nada de
> `Servo*` entra em `TelemetryKeys`, `CommandKeys`, `SensorSnapshot`, alarmes ou UI nesta rodada.
> O parser ja ignora chave desconhecida, entao um Hub v8 publicando `Servo*` nao quebra o app.
> Quando voltar ao escopo, o gancho natural e `ServoPowerW` como fonte de P/V em kLa.

---

## 2. O contrato-alvo (generalizar o fluxômetro)

Um dispositivo externo padronizado expõe **três estados independentes**, nunca colapsados num só:

| Estado | Origem | Significado |
|---|---|---|
| **Habilitado** (`*Comm`) | eco do Hub | o roteamento do Hub para este dispositivo está ligado |
| **Presente** (`*Online`) | timeout do Hub | o Hub recebeu telemetria do nó dentro da janela |
| **Pendente** (`*CommandPending`) | caixa do Hub | há comando emitido e ainda não confirmado pelo nó |

E o app deriva, por dispositivo, exatamente o vocabulário que `FlowControlViewModel` já tem:

```
Has<Dev>Telemetry     — o Hub já falou deste dispositivo alguma vez
Is<Dev>Online         — presente
Is<Dev>Offline        — HasTelemetry && !IsOnline      (o chip "desconectado")
IsAwaitingAck         — comando despachado, ainda não confirmado
CanSend<Dev>Commands  — HasTelemetry && IsOnline && !IsAwaitingAck
Show<Dev>PendingChip  — HasTelemetry && IsOnline && IsAwaitingAck   (o chip "aguardando")
<Dev>StatusText       — texto pt-BR único, na ordem offline > pendente > ok
MarkCommandDispatched() / MarkHubUnavailable() / UpdateTelemetry(snapshot)
```

Três regras que valem para todos:

1. **Ausência de chave nunca vira zero, e nunca vira "estável".** Se `*Online` for falso, o
   parser **invalida** os valores daquele dispositivo (`NotReceived`), como já faz para
   `FlowRate/FlowSetpoint/FlowVoltage/Valve*`.
2. **Nenhum estado de UI é confirmado antes do despacho aceito.** Fecha AUD-003 para estes
   dispositivos: `IDeviceService.Send` (void) é substituído por despacho que devolve
   `CommandDispatchResult`.
3. **A parada segura nunca é bloqueada pela ausência do dispositivo.** Fluxômetro já faz isso
   (o Hub enfileira comando de vazão mesmo offline). Biomassa e bomba **não** — o Hub descarta
   se `*Comm == 0`. Consequência de projeto: o app **nunca** desliga `*Comm` antes de mandar o
   quadro de parada; ordem obrigatória é *parar → desabilitar*, num único frame quando possível.

---

## 3. Alterações no app

### 3.1 `TecnalHub.Protocol` — camada de fio

**`TelemetryKeys`** — acrescentar:

```csharp
// Presença e habilitação por dispositivo (Hub v8)
BiomassOnline = "BiomassOnline";          BiomassCommEnabled = "BiomassCommEnabled";
PumpOnline    = "PumpOnline";             PumpCommEnabled    = "PumpCommEnabled";
DistanceOnline= "DistanceOnline";         DistanceCommEnabled= "DistanceCommEnabled";
AgitatorOnline= "AgitatorOnline";         AgitatorCommandPending = "AgitatorCommandPending";
AgitatorPercent = "AgitatorPercent";      AgitatorDir = "AgitatorDir";
AgitatorPotActive = "AgitatorPotActive";  AgitatorSource = "AgitatorSource";
BiomassCommandPending = "BiomassCommandPending";
PumpCommandPending    = "PumpCommandPending";

// Bomba — chaves já publicadas e hoje ignoradas
PumpMode = "PumpMode"; PumpPwm = "PumpPWM"; PumpSpeed = "PumpSpeed";
PumpTargetVolume = "PumpTargetVol"; PumpActive = "PumpActive"; PumpWaiting = "PumpWaiting";

```

> `Servo*` fica fora desta lista — ver D-8.

**`CommandKeys`** — nada de servo nesta rodada.

**`SensorReadings` / `SensorSnapshot`** — espelhar tudo acima. Novos campos booleanos de
presença começam `false`; os de habilitação são **pegajosos** (`bool?`, ausente = desconhecido,
para não fingir "desligado" antes do primeiro frame de um Hub v7 antigo).

**`TelemetryParser`** — três mudanças estruturais:

- `ParseBiomass` e `ParsePump` ganham a mesma invalidação que `ParseFlowAndMisc` já faz:
  ao ler `BiomassOnline:false` / `PumpOnline:false`, zerar para `NotReceived` em vez de manter
  o último valor bom. Fecha **D-1** e **D-2** no lado do app.
- Fallback de compatibilidade com Hub v7 (sem as flags novas): se a chave `*Online` estiver
  **ausente**, derivar presença por envelhecimento local da mesma forma que `ParseDistance` já
  faz — última vez que a chave de valor apareceu, com `DistanceTimeout` generalizado para um
  `DeviceTimeout` por dispositivo. Isso mantém o app funcionando contra um Hub que o operador
  ainda não regravou.
**`CommandActuators`** — sem alteração nesta rodada; nenhum ator novo.

### 3.2 Um lugar só para o padrão: `ExternalDeviceStatus`

Em vez de copiar oito propriedades em quatro ViewModels, extrair de `FlowControlViewModel` a
parte que não é do fluxômetro:

```
src/TecnalHub/ViewModels/ExternalDeviceStatus.cs
```

`ObservableObject` com `HasTelemetry`, `IsOnline`, `IsOffline`, `IsAwaitingAck`, `CanSend`,
`ShowPendingChip`, `StatusText`, `IsCommEnabledOnHub` (`bool?`), `HasCommMismatch`,
`MarkCommandDispatched()`, `MarkHubUnavailable()`, `Update(bool online, bool pending, bool? commEnabled)`,
e um `DeviceName` para compor os textos pt-BR. `FlowControlViewModel` passa a **compor** essa
classe (mantendo as propriedades atuais como delegação, para não quebrar os bindings existentes
nem os testes de `FlowmeterV05SyncTests`); `Biomass`, `Pump`, `FlaskAgitator` e `FoamControl`
recebem uma instância cada.

`HasCommMismatch` é novo e responde a **D-6**: verdadeiro quando o checkbox local e o
`*CommEnabled` do Hub discordam por mais de um período de telemetria.

### 3.3 Despacho com resultado (fecha AUD-003 nestes dispositivos)

`BiomassControlViewModel`, `PumpControlViewModel` e `FlaskAgitatorViewModel` hoje chamam
`_device.Send(...)` e **comitam a UI logo em seguida**. Substituir por um
`IManualDispatcher.Dispatch(command)` que devolve `CommandDispatchResult`, e:

- comitar `_committed` / `_settings.Update` / `HasPendingChange = false` **apenas** se `Accepted`;
- em recusa, manter o valor encenado, exibir o proprietário conflitante em `StatusText`
  ("Recusado: a receita controla o sensor de biomassa") e **não** chamar `MarkCommandDispatched()`;
- em `PumpControlViewModel.MaybeSendProportionalGas`, só avançar `_lastGasFlowSentLpm` quando
  aceito — que é exatamente AUD-004, e cai junto de graça.

### 3.4 Serialização dos comandos momentâneos da biomassa (D-5)

Com a caixa consome-na-leitura e polling de 2 s, dois momentâneos em sequência perdem o
primeiro. Regra do app:

- `Blank`, `Start` e `Stop` ficam **mutuamente exclusivos** enquanto `IsAwaitingAck`;
- `IsAwaitingAck` da biomassa é liberado por `BiomassCommandPending:false` vindo do Hub
  (requer a caixa revisionada — seção 4.2) **ou**, contra um Hub antigo, por um temporizador
  de 2,5 × `HUB_POLL_PERIOD_MS` = **5 s**, com o chip "aguardando" visível o tempo todo;
- `ApplyThresholds` nunca dispara junto com um momentâneo.

### 3.5 Ordem obrigatória parar-antes-de-desabilitar

`BiomassControlViewModel.OnIsEnabledChanged(false)` hoje manda só `{"biomassComm":0}`. Se a
aquisição estiver rodando, o nó **continua medindo** — o Hub apenas para de aceitar `/biomassData`
(403) e de encaminhar comandos. Corrigir para um único frame `{"stop":1,"biomassComm":0}`: o
`processJsonCommand` do Hub lê `biomassComm` **antes** do bloco de biomassa, mas usa o valor
já atualizado no gate, então o `stop` seria descartado. **Portanto são dois frames, nesta ordem:**
`{"stop":1}` → aguardar ACK/temporizador → `{"biomassComm":0}`. O mesmo vale para a bomba:
`PumpDisable()` já manda `pumpComm:0` junto de `mode:0`, e `mode:0` é descartado pelo mesmo gate.
Separar em `{"mode":0,"speed":0}` → `{"pumpComm":0}`.

> Isto muda dois golden strings de `PROTOCOL.md` §4 (`pump disable`). É uma mudança de
> comportamento deliberada e precisa de entrada em `DECISIONS.md` + atualização da tabela.

### 3.6 Alarmes

Novas definições em `AlarmService`, no mesmo molde de `FlowmeterOffline`
(`Warning`, 2 s/2 s), condicionadas ao respectivo `*CommEnabled` para não gritar sobre um
dispositivo que o operador desligou de propósito:

| `AlarmId` | Título | Condição |
|---|---|---|
| `BiomassOffline` | Sensor de biomassa offline | `connected && s.BiomassCommEnabled == true && !s.BiomassOnline` |
| `ExternalPumpOffline` | Bomba externa offline | `connected && s.PumpCommEnabled == true && !s.PumpOnline` |
| `FlaskAgitatorOffline` | Agitador de frasco offline | `connected && s.AgitatorOnline == false` |
| `DistanceSensorOffline` | Sensor de distância offline | `connected && s.DistanceCommEnabled == true && !s.DistanceOnline` |
| `DeviceCommMismatch` | Roteamento divergente | qualquer `HasCommMismatch` (Info/Warning) |

Corrigir de passagem: `AlarmId.FlowmeterOffline` hoje é `connected && FlowmeterOnline == false`,
mas `PROTOCOL.md` §3.1 diz que a condição é qualificada por `FlowControlEnabled`. Alinhar código
e doc — um fluxômetro fisicamente ausente com a malha desligada não é alarme.

### 3.7 Receitas

`RecipeEngine.Devices.cs` já tem `AwaitDeviceAsync` genérico. Só faltam os predicados:

```csharp
AwaitBiomassAppliedAsync  → s.BiomassOnline && s.BiomassCommEnabled == true
AwaitPumpProfileAsync     → s.PumpOnline && s.PumpMode == modoEsperado
AwaitAgitatorAppliedAsync → s.AgitatorOnline && |s.AgitatorPercent - alvo| <= 2
```

Mesma graça de 8 s, mesmo alarme `RecipeAwaitingDevice`, mesmas saídas pular/parar. Nenhuma
estrutura nova.

### 3.8 Simulador e testes

`TecnalHub.Simulator/DeviceModel.cs` passa a emitir as flags novas e a **simular queda de nó**
(um comando de harness `drop biomass` / `drop pump` / `drop agitator`), porque é o único jeito
de exercitar os caminhos offline sem desligar hardware.

Testes novos, no molde de `FlowmeterV05SyncTests`:

- `ExternalDeviceStatusTests` — as transições do vocabulário compartilhado;
- `TelemetryParserTests` — invalidação em `*Online:false`; fallback por envelhecimento quando a
  chave `*Online` está ausente (compatibilidade Hub v7);
- `BiomassPumpTests` (existente) — despacho recusado não comita; momentâneos serializados;
  ordem parar-antes-de-desabilitar;
- `AlarmServiceTests` — as cinco condições novas + a correção do `FlowmeterOffline`;
- `WireFormatTests` — golden strings novos (`{"stop":1}`, `{"pumpComm":0}` separados) e servo.

---

## 4. Alterações de firmware — **para você regravar**

Ordenadas por severidade. As três primeiras são as que realmente pagam o plano; sem elas o app
só pode inferir presença por envelhecimento local, que funciona mas é mais lento e mais frágil.

### 4.1 Hub v8 — presença e habilitação para todos (obrigatório)

**Arquivo:** `TECNAL_ESP32_v8.ino` · **Motivo:** D-1, D-2, D-6

Acrescentar `unsigned long pumpLastUpdate = 0;` e `const unsigned long PUMP_TIMEOUT = 4000;`
(4 × o `DATA_PUSH_PERIOD_MS` de 1 s da bomba), gravar `pumpLastUpdate = millis();` no handler
`/pumpData`, e trocar o bloco de telemetria da bomba por:

```cpp
bool pumpOnline = pumpCommOn && pumpLastUpdate > 0 &&
                  (millis() - pumpLastUpdate <= PUMP_TIMEOUT);
jsonResponse += ",\"PumpOnline\":" + String(pumpOnline ? "true" : "false");
if (pumpOnline) { /* bloco Pump* existente */ }
```

E, no mesmo padrão de `ServoOnline` (que já está certo), publicar **sempre**:

```cpp
jsonResponse += ",\"BiomassOnline\":"       + String(validBiomass ? "true" : "false");
jsonResponse += ",\"DistanceOnline\":"      + String(validDistance ? "true" : "false");
jsonResponse += ",\"BiomassCommEnabled\":"  + String(biomassCommOn ? "true" : "false");
jsonResponse += ",\"PumpCommEnabled\":"     + String(pumpCommOn ? "true" : "false");
jsonResponse += ",\"DistanceCommEnabled\":" + String(distanceSensorCommOn ? "true" : "false");
```

Custo: ~180 bytes no pior caso. O `jsonResponse.reserve(2048)` atual comporta; o
`MAX_HTTP_PAYLOAD` de 2048 é do `/command`, não do `/readData`, então não é limite aqui.

### 4.2 Hub v8 — caixa revisionada para biomassa, bomba e agitador (recomendado)

**Motivo:** D-5 · **Escopo:** generalizar `buildFlowCommandLocked` / `getReliableFlowCommand` /
`queueReliableFlowCommandFromJson`.

O mecanismo já existe e está correto. A generalização é mecânica: uma `struct ReliableMailbox`
com `{ String payload; uint32_t revision; uint32_t ack; bool awaiting; uint32_t deliveries;
unsigned long queuedAt; }`, uma instância por dispositivo, e:

- `/<dev>Command` devolve o payload com `"cmd_id":<revision>` e **não limpa**;
- `/<dev>Data` passa a aceitar `&ack_cmd_id=` e limpa quando `ack == revision`;
- `/readData` publica `<Dev>CommandPending` e `<Dev>CommandId`/`<Dev>CommandAck`.

Do lado dos nós, o custo é pequeno e idêntico ao que o v05 já faz:

- **biomassa** (`biomass_sensor_analog_v04_direct.ino`): guardar `lastAppliedHubCommandId`,
  ignorar duplicata em `processJsonCommand`, e anexar `&ack_cmd_id=` em `sendDataToHub()`;
- **bomba** (`v_3_2_DC_motor_peristaltic.ino`): idem, em `pollHubForCommands()` /
  `sendDataToHub()`;
- **agitador** (`frasco_agitador_03.ino`): idem, em `pollHub()` — mas depende de 4.3.

> Se você preferir não mexer nos três nós agora, **4.1 sozinho já entrega a maior parte do
> valor**: presença honesta e roteamento observável. O app fica com o temporizador de 5 s da
> seção 3.4 no lugar do `*CommandPending`, o que é aceitável e está previsto no plano.

### 4.3 Frasco agitador — telemetria ao Hub (obrigatório para tirá-lo do escuro)

**Arquivos:** `frasco_agitador_03.ino` **e** `TECNAL_ESP32_v8.ino` · **Motivo:** D-3

O nó já monta telemetria a cada 500 ms para USB e `/read`. Falta só empurrá-la:

```cpp
// frasco_agitador_03.ino — dentro do bloco de telemetria de 500 ms, quando WL_CONNECTED
HTTPClient http;
String url = String("http://") + HUB_IP.toString() + "/agitatorData"
           + "?pct="    + String(targetPercent, 1)
           + "&dir="    + String(dirRight ? 1 : 0)
           + "&pot="    + String(potEnabled ? 1 : 0)
           + "&src="    + String(srcName(lastSource))
           + "&secs="   + String(millis() / 1000);
http.begin(url); http.setTimeout(500); http.GET(); http.end();
```

E no Hub, um handler `/agitatorData` espelhando `/pumpData`, com
`AGITATOR_TIMEOUT = 3000` e a publicação de `AgitatorOnline`, `AgitatorPercent`,
`AgitatorDir`, `AgitatorPotActive`, `AgitatorSource`.

O campo `src` é o que resolve **D-4** de forma honesta: o app passa a saber quando o
potenciômetro assumiu o controle e pode dizê-lo ("comandado pelo potenciômetro") em vez de
mostrar o setpoint que ele mesmo mandou.

### 4.4 Sensor de biomassa — empurrar batimento em IDLE (recomendado)

**Arquivo:** `biomass_sensor_analog_v04_direct.ino` · **Motivo:** D-2

Hoje `sendDataToHub()` só é chamado de `publishSample()`. Acrescentar no `loop()`, sob
`g_hubEnabled && WiFi.status() == WL_CONNECTED`, um empurrão de manutenção a cada 5 s **quando
`g_state == IDLE`**, com `&idle=1` para o Hub distinguir batimento de amostra. Sem isso,
"parado" e "caído" continuam indistinguíveis mesmo com 4.1 — o `validBiomass` do Hub vence o
timeout de 10 s de qualquer jeito.

Alternativa sem tocar no nó: o Hub publica `BiomassOnline` derivado da **última vez que o nó
bateu na porta**, incluindo os `403` de `/biomassData` recusados por `biomassCommOn == false`
(hoje o handler retorna 403 **antes** de gravar `biomassLastUpdate`). Mover o
`biomassLastUpdate = millis();` para antes do teste de `biomassCommOn` resolve metade do
problema com uma linha — mas continua sem cobrir o caso IDLE, porque em IDLE o nó não bate.

### 4.5 Sensor de distância — nada a fazer

`SEND_PERIOD_MS = 1000` contra `DISTANCE_TIMEOUT = 1200` no Hub é apertado (uma perda de pacote
já expira), mas o filtro de estagnação e o `-1` já dão semântica correta. Sugestão opcional:
`DISTANCE_TIMEOUT = 3000` no Hub, para não piscar "offline" a cada retransmissão perdida.

### 4.6 Resumo do que regravar

| Firmware | Mudança | Prioridade |
|---|---|---|
| `TECNAL_ESP32_v8` | 4.1 presença + habilitação | **obrigatória** |
| `TECNAL_ESP32_v8` | 4.3 handler `/agitatorData` | **obrigatória** |
| `frasco_agitador_03` | 4.3 push de telemetria | **obrigatória** |
| `biomass_sensor_analog_v04_direct` | 4.4 batimento em IDLE | recomendada |
| `TECNAL_ESP32_v8` + 3 nós | 4.2 caixa revisionada | recomendada |
| `TECNAL_ESP32_v8` | 4.5 `DISTANCE_TIMEOUT` 3 s | opcional |

---

## 5. O que muda na interface

### 5.1 Página Controle — seção "Dispositivos Externos"

Hoje as linhas 9–12 (`Sensor de Distância`, `Bomba Dosadora Externa`, `Sensor de Biomassa`,
`Frasco Agitador`) usam `<ctl:StateDot State="{Binding IsEnabled, Converter={StaticResource ActiveToState}}" />`
— **o ponto de estado reflete o checkbox do próprio operador**, não o dispositivo. Só a linha 8
(`Vazão de Ar`) tem os chips reais.

Padronização, replicando exatamente o bloco da linha 8 em cada uma das quatro:

1. **`StateDot` passa a ser um `MultiBinding`** de `IsEnabled` × `Status.IsOnline`, com a
   mesma semântica do `ActiveVariableState` do fluxômetro: cinza = desligado, verde = ligado e
   presente, vermelho = ligado e ausente.
2. **Chips ao lado do nome**, idênticos aos existentes: `aguardando`
   (`StateWarningBrush`, ligado a `Status.ShowPendingChip`) e `desconectado`
   (`StateAlarmBrush`, ligado a `Status.IsOffline`).
3. **Chip novo `roteamento`** (`StateWarningBrush`), ligado a `Status.HasCommMismatch`, com
   tooltip "O Hub reporta este dispositivo como desligado" — a resposta visual a D-6.
4. **Entradas e toggles desabilitados por `Status.CanSend`**, com o mesmo `MultiDataTrigger`
   que a linha de vazão usa, e tooltip explicando o motivo em vez de simplesmente cinza.
5. **Eco de telemetria nos drawers**, no formato `Telemetria: {0}` que a gaveta do fluxômetro já
   usa para as válvulas.

Conteúdo novo por gaveta:

| Linha | Acrescentar na gaveta |
|---|---|
| **Bomba Dosadora Externa** | `Modo` (eco de `PumpMode`, comparado ao encenado), `Estado` (Ativa / Aguardando janela / Parada, de `PumpActive`/`PumpWaiting`), `Volume alvo` (`PumpTargetVol`) ao lado do acumulado, `PWM`/`Rotação` (`PumpPWM`/`PumpSpeed`) |
| **Sensor de Biomassa** | `Aquisição` (Medindo / Parado / Sem contato), estado dos três momentâneos serializados, eco dos limiares aplicados |
| **Frasco Agitador** | `Telemetria: {0} %` e `Sentido`, e — o item que muda a operação — **`Comandado por: Potenciômetro / Hub / USB`** vindo de `AgitatorSource`, com aviso explícito quando o potenciômetro está ativo |
| **Sensor de Distância** | `Última leitura há {0} s`, para diferenciar "0 mm" de "sem contato" |

### 5.2 Frasco Agitador — o aviso do potenciômetro (D-4)

Mudança de comportamento, não só visual. Quando `RequestedIsEnabled == false` e
`agitatorReEnablePot == true`, a UI passa a exibir, ao lado do botão Aplicar:

> ⚠ Desligar com "reativar potenciômetro" ligado devolve o controle ao potenciômetro da
> bancada. Se ele não estiver em zero, o motor volta a girar.

E o caminho de **parada segura** do operador passa a mandar `agitatorReEnablePot:0` junto do
`agitatorOn:0`, independentemente da preferência — parada segura é parada segura. A preferência
do operador continua valendo para o botão "Desligar" normal. Registrar como decisão nova em
`DECISIONS.md`.

### 5.3 Painel (Synoptic)

Dois problemas, um de classificação e um de cobertura.

**Classificação.** Hoje o card `PARÂMETROS INTERNOS` contém `Sensor de Distância` e
`Sensor de Biomassa` — que são nós Wi-Fi externos —, e o card `DISPOSITIVOS EXTERNOS` contém
`Dosagem de Nutrientes` e `Dosagem de Antiespumante` — que são bombas do módulo TECNAL na UART
interna. Está trocado em relação à topologia física. Reorganizar para:

- **PARÂMETROS INTERNOS** (UART do módulo): Agitação, Temperatura, pH, Oxigênio, Alívio de
  Pressão, Dosagem de Nutrientes, Dosagem de Antiespumante;
- **DISPOSITIVOS EXTERNOS** (estações Wi-Fi do Hub): Vazão de Ar, Sensor de Distância, Sensor de
  Biomassa, Bomba Dosadora Externa e **Frasco Agitador** (ausente hoje).

**Cobertura.** Os chips `aguardando`/`desconectado` que hoje existem só no `Grid` da Vazão de Ar
passam a existir em todos os cards do segundo grupo, via um `ControlTemplate` compartilhado
(`ExternalInstrumentTagStyle`) em vez do `Grid` + dois `Border` copiados — hoje são 20 linhas
de XAML por dispositivo, e cinco cópias seria dívida imediata.

**Contagem de estações.** O cabeçalho já tem `HubStations`. Passa a exibir
`{presentes}/{habilitados} dispositivos` derivado das flags novas, que é a informação que o
operador realmente quer antes de iniciar um cultivo. `HubStations` continua no popover de
conexão como diagnóstico de rádio.

### 5.4 Onde os enhancements aparecem

| Enhancement | Superfície afetada |
|---|---|
| Presença honesta (4.1) | Ponto de estado e chips em Controle e Painel; cinco alarmes novos; contagem no cabeçalho |
| ACK por dispositivo (4.2) | Chip `aguardando`; serialização dos momentâneos da biomassa; bloqueio de receita por dispositivo |
| Telemetria do agitador (4.3) | Linha do agitador ganha coluna "Valor Lido" (hoje vazia); card novo no Painel; aviso do potenciômetro |
| Chaves da bomba já publicadas (D-7) | Gaveta da bomba: modo, estado, volume-alvo, PWM — sem custo de firmware |

---

## 6. Sequência de execução

Cada etapa é um commit num branch dedicado, com testes verdes, conforme
[CONVENTIONS.md](CONVENTIONS.md).

> **Estado em 2026-08-29: todas as etapas concluídas em código** (`dotnet test -c Release`:
> 641 aprovados, 1 ignorado — o teste de tema hospedado de sempre; `dotnet build`: 0 avisos,
> 0 erros). Os firmwares estão escritos e aguardam gravação; ver
> [FIRMWARE_DISPOSITIVOS_EXTERNOS.md](FIRMWARE_DISPOSITIVOS_EXTERNOS.md). Falta a passagem visual
> da etapa 3 e os recibos de bancada.

**Etapa 0 — contrato e testes que falham. ✅ concluída.** Atualizar `PROTOCOL.md` com a seção 1.2 deste
documento (matriz real do Hub v8, incluindo `Servo*`, `Pump*` completo e as flags novas).
Escrever os testes de parser/alarme que falham. Nenhuma mudança de comportamento.

**Etapa 1 — app contra Hub v7/v8 antigo (sem regravação). ✅ concluída.**
`ExternalDeviceStatus`, fallback de presença por envelhecimento, invalidação de biomassa/bomba,
despacho com resultado (`IManualDispatcher`), serialização dos momentâneos, ordem
parar-antes-de-desabilitar em quadro ordenado, bloqueio do potenciômetro na parada segura do
agitador e as seis chaves `Pump*` que já existiam no fio. **Entrega valor imediato sem tocar em
firmware.**

Arquivos: [`ExternalDeviceStatus.cs`](../src/TecnalHub/ViewModels/ExternalDeviceStatus.cs),
[`ManualDispatcher.cs`](../src/TecnalHub/Services/Communication/ManualDispatcher.cs),
[`TelemetryParser.cs`](../src/TecnalHub.Protocol/TelemetryParser.cs),
[`ConnectionManager.cs`](../src/TecnalHub.Protocol/ConnectionManager.cs),
[`CommandBuilders.cs`](../src/TecnalHub.Protocol/CommandBuilders.cs), os quatro ViewModels de
dispositivo externo e [`ExternalDeviceTests.cs`](../tests/TecnalHub.Tests/ExternalDeviceTests.cs)
(24 testes novos). Decisões em [D-026 a D-029](DECISIONS.md).

> **O que a etapa 1 ainda não pode fazer.** Sem a regravação da seção 4, o Hub não publica
> `*Online` nem `*CommEnabled`: a presença vem do envelhecimento local (mais lenta e mais
> grosseira), o chip `roteamento` nunca acende porque não há eco para comparar, e o agitador
> permanece em *aguardando telemetria* porque não existe nenhuma chave dele para envelhecer. As
> correções de comando — ordem, serialização, parada segura, despacho com resultado — valem
> desde já contra o firmware atual.

**Etapa 2 — firmware 4.1 + 4.3. ✅ escrita; aguarda gravação.** Hub v8 editado no lugar;
`frasco_agitador_04`, `v_4_DC_motor_peristaltic` e `biomass_sensor_analog_v05_hubsync` criados
ao lado das versões anteriores. Os cinco alarmes entraram
(`BiomassOffline`, `ExternalPumpOffline`, `DistanceSensorOffline`, `FlaskAgitatorOffline`,
`DeviceRoutingMismatch`), cada um qualificado pelo eco de roteamento do Hub para não gritar
sobre um dispositivo que o operador desligou de propósito.

> **Um estado novo apareceu no caminho.** O heartbeat de IDLE da biomassa (4.4) resolve
> "parado × caído", mas cria "online sem amostra" — e o push de heartbeat carrega a última
> absorbância que o nó tinha. Registrá-la como fresca deixaria a leitura de um sensor parado na
> tela como se fosse atual, que é o defeito que o heartbeat existe para corrigir. O Hub passou
> a ter **dois relógios** para a biomassa (presença e amostra), e o parser trata a ausência do
> bloco de amostra num quadro que *tem* a chave de presença como uma afirmação: "está lá, não
> está medindo".

**Etapa 3 — UI. ✅ concluída em código; falta a passagem visual.** O controle compartilhado
ficou `ctl:ExternalDeviceChips` (não `ExternalInstrumentTagStyle`: os chips são conteúdo
sobreposto, não um estilo de botão). As quatro linhas de Controle trocaram o ponto de estado
pelo `ExternalDeviceStateConverter`, e o Painel foi reagrupado por topologia.

> **O card do agitador no Painel não entrou, deliberadamente.** As tiles do sinóptico são
> `ProcessVariableViewModel` ligadas a um `TelemetryChannel`, e um canal novo atravessa o enum,
> o `TelemetryHistory`, o **mapa de colunas do CSV de sessão** (índices fixos), o
> `ChartsViewModel` e a barra de KPI. Mudar o esquema do CSV de sessão para acomodar um
> acessório de bancada com um percentual comandado é desproporcional ao ganho. A telemetria do
> agitador aparece em Controle, que é onde o dispositivo é operado, e a legenda do Painel diz
> isso.

**Passagem visual feita em 2026-08-29** contra o simulador, tema claro e escuro, 1280×720 e o
equivalente de 125 % (1024×576 lógicos). Evidências em `docs/evidence/ui/wp9-*`. O simulador
ganhou as chaves novas e dois cenários (`node-dropout`, `routing-drift`) — sem eles não havia como
pôr os chips na tela.

Sete correções saíram dela:

1. **Um chip por linha, não três.** Dois chips estouravam a coluna do nome e invadiam *Valor
   Lido*. A precedência é a mesma do texto de status: desconectado > aguardando > roteamento, com
   a frase completa no tooltip.
2. **O chip fica colado ao nome**, com um espaçador antes de *Valor Lido*; o nome trunca com
   reticências quando precisa, porque um chip empurrado para fora levaria o estado junto.
3. **O ponto de estado carrega a mesma severidade do chip.** Vermelho com *desconectado*, âmbar
   com *aguardando* ou *roteamento*. Antes o ponto seguia só o interruptor: um chip vermelho ao
   lado de um ponto âmbar são duas respostas para a mesma pergunta.
4. **O fluxômetro entrou no mesmo controle de chips** e ganhou o chip `roteamento`, que não tinha.
   O Hub persiste `flowComm` na NVS exatamente como os outros flags — e `FlowControlEnabled` é a
   condição do alarme `Fluxômetro offline`, então uma divergência silenciosa desliga o alarme
   junto com a malha.
5. **A faixa de alarme quebra linha em vez de esconder texto.** Os dois painéis eram filhos
   sobrepostos de uma célula única, e um `StackPanel` horizontal dá largura infinita ao filho —
   o `TextTrimming` nunca disparava e o fim da frase ficava atrás dos botões.
6. **A linha do agitador mostrava o valor encenado em *Valor Lido***, que é o mesmo defeito que
   este trabalho existe para remover. Agora mostra o que o nó reporta.
7. **Título de duas linhas cortado** nos cards do Painel, e o chip sobrepondo o título — o chip
   passou para a base do card, onde há espaço justamente quando ele aparece.

**Etapa 4 — receitas. ✅ concluída, com os blocos que faltavam.** O plano pressupunha blocos que
não existiam. Eles agora existem, numa categoria nova **Dispositivos Externos**:

- **Bomba Externa** — era `Controle da Bomba`, um marcador que registrava a intenção e não enviava
  nada, escrito enquanto a atuação da WP2 estava pendente. Agora envia os cinco perfis pelo mesmo
  `PumpProfileMath.BuildCommand` do card manual, e a parada usa os dois quadros ordenados.
- **Sensor de Biomassa** — ativar, branco, iniciar, parar, limiares e parar-e-desativar.
- **Agitador de Frasco** — acionar com intensidade e sentido, ou parar.

Os predicados de `AwaitDeviceAsync` entraram junto. Todos compartilham uma cláusula de escape: são
satisfeitos quando o Hub **não disse nada** sobre o dispositivo, porque contra um Hub anterior às
chaves de presença segurar seria segurar por evidência que aquele firmware não produz. Ver
[DECISIONS D-030](DECISIONS.md).

**Etapa 5 — firmware 4.2. ✅ escrita; aguarda gravação.** Deixou de ser opcional: a caixa
revisionada entrou no Hub como `ReliableMailbox` e nos três nós como `cmd_id` idempotente mais
`ack_cmd_id`. O app já prefere `*CommandPending` quando o Hub o publica e cai no temporizador de
5 s quando não.

### Critérios de aceitação

Marcado ✅ o que já está provado por teste automatizado; o restante precisa de bancada.

- [x] Presença invalida o valor lido em vez de congelá-lo *(teste; falta o recibo de bancada
      com cada nó fisicamente desligado)*
- [x] Os quatro cards de dispositivo externo não reportam sucesso após despacho recusado
- [ ] Nenhuma **outra** superfície manual reporta sucesso após despacho recusado (AUD-003 segue
      aberta para as demais)
- [x] `blank` seguido de `start` dentro de 2 s não perde o `blank`
- [x] Desabilitar biomassa/bomba envia a parada antes de limpar o roteamento *(teste; falta
      confirmar em bancada que o nó realmente para)*
- [x] Parada segura do agitador envia `agitatorReEnablePot:0`
- [x] Reboot do Hub com checkbox divergente acende o chip `roteamento` e o alarme
      `Roteamento divergente no Hub` *(teste; o recibo de bancada depende da gravação)*
- [x] `dotnet test TecnalHub.slnx -c Release` verde; `dotnet build` com 0 avisos

---

## 7. Riscos

| Risco | Mitigação |
|---|---|
| App novo contra Hub não regravado | Fallback por envelhecimento (3.1) é obrigatório na Etapa 1, não opcional |
| Hub regravado contra app antigo | Chaves novas são aditivas; `TelemetryParser` já ignora chave desconhecida |
| `/readData` estourar buffer | +180 B sobre `reserve(2048)`; medir `heap_caps_get_free_size` no log de 60 s após regravar |
| Separar `stop`+`biomassComm` em dois frames aumenta latência de desligamento | ~2 s no pior caso (um período de polling do nó); aceitável para uma medição, e é a única ordem que o gate do Hub permite |
| Mudança dos golden strings de `pump disable` | Entrada em `DECISIONS.md` + atualização de `PROTOCOL.md` §4 no mesmo commit |
