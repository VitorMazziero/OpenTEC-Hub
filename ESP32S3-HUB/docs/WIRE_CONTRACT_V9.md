# Contrato HTTP do Hub 10

> O nome deste arquivo é histórico. A identidade emitida atualmente é firmware
> `10.0.0-dev`, `HubProtocolVersion=10`.

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

## Limite de responsabilidade

O Hub confirma entrega somente quando `motor_ack == motor_cmd_id`. O driver é
responsável pelo perfil do ASDA-B2, escrita/readback de P1-09, lease e remoção de
SON. A matriz física completa está em `docs/VALIDATION.md` e no plano
`ESP32S3-SERVO/PLANO_MIGRACAO_RPM_MODBUS.md`.
