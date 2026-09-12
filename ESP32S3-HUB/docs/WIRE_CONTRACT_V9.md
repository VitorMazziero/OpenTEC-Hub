# Contrato HTTP do Hub 10

> O nome deste arquivo é histórico. A identidade emitida atualmente é firmware
> `10.2.0-dev`, `HubProtocolVersion=10`. O aplicativo grava `hubFirmwareVersion` no
> manifesto de cada ensaio, por isso toda mudança de comportamento do Hub sobe a versão
> — `10.0.1-dev` é o leitor serial em linhas e o repasse de `a1`/`b1` (2026-09-11);
> `10.1.0-dev` é a identidade dos nós externos no quadro agregado e o `/nodes` completo
> (2026-09-12); `10.2.0-dev` é a caixa confiável da distância por carona no push e
> os ecos de configuração dos nós externos. Chaves aditivas não sobem o protocolo.

## Compatibilidade com o aplicativo

Os endpoints e chaves do aplicativo permanecem. Em particular,
`{"motorSetpoint":N}` continua representando 0..1000 rpm e zero significa
desabilitar. A mudança ocorre depois do Hub: o comando não usa mais a UART da
placa intermediária; segue ao ESP32S3-driver por `/servoCommand`.

O Hub não retoma `motorSetpoint` salvo após reboot. Ele inicia com zero e exige
um novo comando de movimento.

## Estado desejado do motor

`GET /servoCommand` sempre inclui:

```json
{"motor_cmd_id":123,"motor_rpm":1000,"motor_enable":1,"motor_lease_ms":3000}
```

- o setpoint é latest-wins e não ocupa a FIFO;
- `motor_cmd_id` muda a cada novo estado e nunca vale zero;
- o mesmo JSON permanece disponível após o ACK como heartbeat do lease;
- `servoComm=0` cria imediatamente uma nova revisão de parada e rejeita novos
  comandos não nulos;
- `resetVariables` cria uma parada e limpa os eventos Servo.

O mesmo JSON pode conter um evento consumível:

```json
{"motor_cmd_id":123,"motor_rpm":0,"motor_enable":0,"motor_lease_ms":3000,"reset_energy":1}
```

`reset_energy` não é coalescido. `poll_ms` aceita 250..10000 ms e atualiza o
evento ainda não entregue mais recente. A FIFO comporta oito eventos e nunca
sobrescreve o mais antigo.

## Push do ESP32S3-driver

`GET /servoData` exige:

```text
rpm, torque_pct, power_w, state,
control_capable, motor_ack, motor_applied_rpm,
motor_control_active, motor_control_fault
```

Continuam opcionais `torque_nm`, `load_pct`, `energy_wh`, `alarm`, `ok` e `err`.
Floats devem ser finitos; `state` aceita 0..3; rpm aplicada aceita 0..1000; flags
aceitam booleano ou 0/1; falha não pode ser negativa. Push inválido retorna 400
e não renova presença nem ACK.

## Campos agregados

`GET /readData` sempre publica:

```text
ServoOnline, ServoCommEnabled, ServoCommandPending,
ServoCommandQueueDepth, ServoControlCapable,
ServoMotorCommandId, ServoMotorCommandAck,
ServoMotorCommandPending, ServoMotorCommandDeliveries,
ServoMotorCommandAgeMs, ServoMotorRequestedRpm,
ServoMotorAppliedRpm, ServoMotorLeaseMs, ServoMotorEnabled,
ServoMotorControlActive, ServoMotorControlFault
```

Quando a amostra está publicável, também inclui `ServoRpm`, `ServoTorquePct`,
`ServoTorqueNm`, `ServoLoadPct`, `ServoPowerW`, `ServoEnergyWh`, `ServoState`,
`ServoAlarm`, `ServoCommOk` e `ServoCommErr`.

`ServoCommandPending` é verdadeiro se há evento na FIFO ou comando de motor sem
ACK. `ServoCommandQueueDepth` conta apenas eventos. Presença é independente do
roteamento e expira após 6000 ms.

### Identidade dos nós externos (10.1)

Para cada nó externo — prefixos `Distance`, `Agitator`, `Pump`, `Flowmeter`, `Biomass` —
o quadro agregado publica:

| Chave | Quando | Valor |
|---|---|---|
| `<Prefixo>IP` | sempre | IP que o Hub extraiu da conexão TCP do nó; `0.0.0.0` = nunca visto |
| `<Prefixo>NodeVer` | só depois de um `/nodeHello` | string `ver` enviada pelo nó (`v10`, `3.8`…), ≤ 15 caracteres |
| `<Prefixo>NodeMac` | só depois de um `/nodeHello` | string `mac` enviada pelo nó, 17 caracteres |

A ausência de `*NodeVer`/`*NodeMac` significa "o nó ainda não se registrou", não "vazio";
o aplicativo trata as três como *sticky* dentro do enlace. O `/agitatorHello` legado renova
IP e registro mas **não** informa versão, para não sobrescrever a que o nó enviou por
`/nodeHello`. Os handlers de dados (`/distance`, `/flowData`, …) atualizam o IP de forma
oportunista mesmo antes do hello.

## Registro de nós: `/nodeHello` e `/nodes`

`GET /nodeHello?dev=<distance|agitator|pump|flowmeter|biomass>&ver=<str>&mac=<str>` →
`200 {"status":"ok","registered":"<dev>","assigned_ip":"<ip>","hub_time_ms":<millis>}`;
`400` sem `dev` ou com `dev` desconhecido. O IP registrado é o remoto da conexão, nunca o
declarado pelo nó.

`GET /nodes[?dev=<nome>]` →

```json
{"hub_time_ms":48210,"nodes":[
  {"dev":"pump","ip":"192.168.4.3","mac":"AA:BB:CC:DD:EE:FF","version":"3.8",
   "online":true,"age_ms":420,"registered":true,"last_hello_ms":41000,"last_data_ms":47790}
]}
```

`online` usa a mesma janela de presença do quadro agregado (`*Online`); `age_ms` é
`hub_time_ms − max(last_hello_ms, last_data_ms)` ou `999999` quando nunca visto —
clientes devem preferir os `*_ms` brutos. `registered`, `last_hello_ms`, `last_data_ms` e
`hub_time_ms` são do 10.1; clientes toleram a ausência em Hubs 10.0.

## Serial USB: comandos em linhas

O aplicativo termina cada quadro com `\n`; o Hub só processa um comando quando o seu fim
de linha chega. O que estiver no buffer USB entre uma chamada e outra é acumulado e
mantido — o leitor anterior tomava os bytes disponíveis como uma linha inteira, o que
funcionava para um setpoint de ~40 B (um pacote USB) e fragmentava o comando de
calibração do fluxômetro (~300 B, vários pacotes): cada pedaço falhava a verificação
`{...}` e era descartado, e o aplicativo ficava em "aguardando ack".

- Uma linha tem no máximo **1024 B**. Acima disso a linha é descartada com aviso e o Hub
  ignora tudo até o próximo fim de linha (`discarding`), para que a cauda não vire o
  início de uma linha seguinte.
- `\r\n` e `\n` valem como fim de linha; várias linhas num mesmo lote são processadas
  uma a uma, na ordem.

## Curva do fluxômetro: `a1`/`b1`

O segmento baixo da curva de vazão é uma **quártica ancorada**: `a1` (x⁴) e `b1` (x³)
além de `k1`, `f1`, `c1`. O Hub repassa os oito termos ao fluxômetro **na ordem
`a1,b1,k1,f1,c1,k2,f2,c2`**, porque o firmware V10 do fluxômetro só zera `a1`/`b1` quando
recebe `k1/f1/c1` **sem** eles — e até 2026-09-11 o Hub os descartava, de modo que toda
curva enviada chegava ao nó como quadrática. Chaves do aplicativo: `a1`, `b1`, `k1`,
`f1`, `c1`, `k2`, `f2`, `c2`, `maxFlow` (ver `docs/PROTOCOL.md` §3.2 do aplicativo).

## Comandos por nó (tradução app→nó)

Todo comando destinado a um nó externo passa pelo Hub e pelo `CommandArbiter` (D-015),
usando caixas confiáveis (`ReliableMailbox`). O aplicativo envia chaves planas com prefixo
do dispositivo, e o Hub valida as faixas e traduz para as chaves nativas do nó:

### Sensor de distância (`distanceBox` via carona no push)

| Chave no app (`CommandKeys`) | Fio app→Hub | Hub traduz para | Faixa aceita no Hub | Mecanismo de entrega |
|---|---|---|---|---|
| `DistanceOffsetMm` | `distanceOffsetMm` | `offset_mm` | `[-50.0, 200.0]` mm | Carona na resposta de `GET /distance` |
| `DistanceSamplePeriodMs` | `distanceSamplePeriodMs` | `sample_period` | `[100, 60000]` ms | Idem |
| `DistanceSendPeriodMs` | `distanceSendPeriodMs` | `send_period` | `[100, 60000]` ms | Idem |
| `DistanceResetNvs` | `distanceResetNvs` | `reset_nvs` | inteiro | Idem |

Valores fora da faixa são descartados no Hub com `ESP32_EVT`.

O sensor de distância não requer rota de poll dedicada: ele já faz um push `GET /distance`
por segundo e lê o corpo da resposta. Quando há comando pendente na `distanceBox`, o Hub
responde `200 application/json {"cmd_id":N,"offset_mm":...}`; o nó aplica e responde com
`&ack_cmd_id=N` no push seguinte. Sem comando pendente, o Hub responde `200 text/plain "Distance data received"`.

### Fluxômetro (caixa confiável do fluxômetro via poll `/flowCommand`)

| Chave no app (`CommandKeys`) | Fio app→Hub | Hub traduz para | Formato |
|---|---|---|---|
| `FlowKp` | `flowKp` | `kp_flow` | float (%.4f) |
| `FlowKi` | `flowKi` | `ki_flow` | float (%.4f) |
| `FlowFfGain` | `flowFfGain` | `ff_gain` | float (%.4f) |
| `FlowFfOffset` | `flowFfOffset` | `ff_offset` | float (%.4f) |
| `FlowRampRate` | `flowRampRate` | `ramp_rate` | float (%.3f, >= 0.0) |

Os campos de sintonia trafegam junto ao estado dos atuadores (`flow_setpoint`, `v1`, `v2`, `v_Flow`)
e permanecem pendentes até o ack (`ack_cmd_id == flowCommandRevision`).

### Bomba Peristáltica (`pumpBox` via poll `/pumpCommand`)

| Chave no app (`CommandKeys`) | Fio app→Hub | Hub traduz para | Formato / Ação |
|---|---|---|---|
| `PumpCommand` | `pump_command` | `command` | string (ex.: `"reset_volume"`) |
| `PumpSlope` | `pumpSlope` | `pumpSlope` | float (pass-through) |
| `PumpIntercept` | `pumpIntercept` | `pumpIntercept` | float (pass-through) |
| `PumpPidKp` | `pumpPidKp` | `pid_kp` | float |
| `PumpPidKi` | `pumpPidKi` | `pid_ki` | float |
| `PumpPidKd` | `pumpPidKd` | `pid_kd` | float |

Nota: `speed` desacompanhado de `pump_` é rejeitado pelo Hub (§3.7 do plano).
O desligamento seguro da bomba utiliza `{"mode":0}`.

### Sensor de Biomassa (`biomassBox` via poll `/biomassCommand`)

| Chave no app (`CommandKeys`) | Fio app→Hub | Hub traduz para | Formato / Ação |
|---|---|---|---|
| `BiomassIt` | `biomassIt` | `{"command":"set_it","value":N}` | índice de tempo de integração (0-3) |
| `BiomassPwm` | `biomassPwm` | `{"command":"set_pwm","value":N}` | duty cycle LED (0-100%) |
| `BiomassGear` | `biomassGear` | `{"command":"set_gear","value":N}` | marcha óptica |
| `BiomassEma` | `biomassEma` | `{"command":"ema","value":x}` | coeficiente do filtro EMA (0.01-1.0) |
| `BiomassProbePeriodMs` | `biomassProbePeriodMs` | `{"command":"probe_period","value":N}` | período de amostragem em ms |

Regra: **Um `command` por revisão**. Se o aplicativo enviar múltiplos comandos na mesma requisição,
o Hub enfileira o primeiro e descarta os excedentes com registro em `ESP32_EVT`.

## Ecos por nó (10.2)

Valores de configuração aplicados pelo nó são ecoados em seus pushes e republicados no
quadro agregado (`GET /readData`).

Regra de emissão:
- Cada bloco de eco só entra no JSON quando o nó já o ecoou pelo menos uma vez neste boot
  (`*EchoSeen = true`) e o nó está dentro da janela de presença (`*Online = true`).
- Se o nó sai da janela de presença, `*EchoSeen` é redefinido para `false` e as chaves de
  eco são removidas do quadro (não são *sticky*).
- `*CommandPending` é publicado sempre (como os outros atuadores e periféricos).

### Sensor de distância

| Chave no quadro | Tipo | Quando | Significado |
|---|---|---|---|
| `DistanceCommandPending` | bool | sempre | Verdadeiro enquanto houver comando pendente de confirmação |
| `DistanceOffsetMm` | float (%.2f) | `DistanceOnline` e `distanceEchoSeen` | Offset em mm aplicado e mantido em NVS pelo sensor |
| `DistanceSamplePeriodMs` | uint32 | `DistanceOnline` e `distanceEchoSeen` | Período de leitura do sensor em ms |
| `DistanceSendPeriodMs` | uint32 | `DistanceOnline` e `distanceEchoSeen` | Período de envio HTTP em ms |

### Fluxômetro

| Chave no quadro | Tipo | Quando | Significado |
|---|---|---|---|
| `FlowCommandPending` | bool | sempre | Verdadeiro enquanto houver comando ou sintonia pendente |
| `FlowKp` | float (%.4f) | `FlowmeterOnline` e `flowmeterEchoSeen` | Ganho proporcional da malha PI |
| `FlowKi` | float (%.4f) | `FlowmeterOnline` e `flowmeterEchoSeen` | Ganho integral da malha PI |
| `FlowFfGain` | float (%.4f) | `FlowmeterOnline` e `flowmeterEchoSeen` | Ganho do feedforward |
| `FlowFfOffset` | float (%.4f) | `FlowmeterOnline` e `flowmeterEchoSeen` | Offset do feedforward |
| `FlowRampRate` | float (%.3f) | `FlowmeterOnline` e `flowmeterEchoSeen` | Taxa da rampa de setpoint (L/min/s) |
| `FlowOutput` | float (%.4f) | `FlowmeterOnline` e `flowmeterEchoSeen` | Saída calculada do controlador |
| `FlowSetpointCorrected` | float (%.4f) | `FlowmeterOnline` e `flowmeterEchoSeen` | Setpoint corrigido com feedforward |
| `FlowmeterBootId` | uint32 | `FlowmeterOnline` e `flowmeterEchoSeen` | Identificador único de boot do nó |

### Bomba Peristáltica

| Chave no quadro | Tipo | Quando | Significado |
|---|---|---|---|
| `PumpCommandPending` | bool | sempre | Verdadeiro enquanto houver comando pendente |
| `PumpSlope` | float (%.4f) | `PumpOnline` e `pumpEchoSeen` | Coeficiente angular de calibração |
| `PumpIntercept` | float (%.4f) | `PumpOnline` e `pumpEchoSeen` | Coeficiente linear de calibração |

### Sensor de Biomassa

| Chave no quadro | Tipo | Quando | Significado |
|---|---|---|---|
| `BiomassCommandPending` | bool | sempre | Verdadeiro enquanto houver comando pendente |
| `BiomassGear` | int | `BiomassOnline` e `biomassEchoSeen` | Marcha óptica ativa (IT + PWM) |
| `BiomassEma` | float (%.3f) | `BiomassOnline` e `biomassEchoSeen` | Fator alfa do filtro EMA aplicado |
| `BiomassProbePeriodMs` | uint32 | `BiomassOnline` e `biomassEchoSeen` | Período de amostragem em ms |

## Limite de responsabilidade

O Hub confirma entrega somente quando `motor_ack == motor_cmd_id`. O driver é
responsável pelo perfil do ASDA-B2, escrita/readback de P1-09, lease e remoção de
SON. A matriz física completa está em `docs/VALIDATION.md` e no plano
`ESP32S3-SERVO/PLANO_MIGRACAO_RPM_MODBUS.md`.
