# Recibo USB — fluxômetro v12 / Hub 10.4 / app Windows

- Data: 2026-09-14 12:26 -03:00
- Interface: `USB-Enhanced-SERIAL CH343 (COM5)`, 115200 8N1
- Cultura do Windows: `pt-BR`
- Estado físico preservado: setpoint 5,00 L/min, válvulas `0/1/0`
- Resultado: 7 quadros, 0 falhas de parse, 1 ACK serial, nenhuma falha de enlace

Quadro representativo após a correção do parser:

```text
time=403 temp=21 flow=5.0074 flowSp=5 flowV=0.4459 valves=0/1/0
flowOnline=True cmdId=3313643496 cmdAck=3313643496 cmdDeliveries=2
cmdAgeMs=0 hubStations=2 sensorOk=True
```

O ID acima excede `Int32.MaxValue` e foi preservado integralmente. Antes da correção,
o mesmo caminho saturava o identificador em `2147483647`, invalidando a correlação do
ACK no aplicativo. A sonda permaneceu em operação durante o teste; foram enviados apenas
`comTest` e a reaplicação idempotente de `dataDelay=2000`, sem alterar atuadores.
