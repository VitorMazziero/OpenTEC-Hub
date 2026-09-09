# ESP32S3-driver do servo Delta ASDA-B2

Firmware candidato de produção `2.0.0-dev`. O nó é o único mestre Modbus RTU do
ASDA-B2: lê telemetria, integra energia e aplica o setpoint de velocidade que
puxa do TECNAL Hub.

O plano de implantação, rollback e ensaios obrigatórios está em
[`../../../PLANO_MIGRACAO_RPM_MODBUS.md`](../../../PLANO_MIGRACAO_RPM_MODBUS.md).

## Hardware

```text
ESP32-S3 GPIO17 (TX) ---> DI do HW-097
ESP32-S3 GPIO16      ---> DE e /RE em curto
ESP32-S3 GPIO18 (RX) <--- RO por divisor 1 kΩ/2 kΩ
ESP32-S3 GND         ---  GND do HW-097 e terra comum

HW-097 VCC -> 5 V
HW-097 A   -> CN3-5 RS-485(+)
HW-097 B   -> CN3-6 RS-485(-)
```

O divisor no RO é obrigatório: o MAX485 opera em 5 V e o GPIO do ESP32-S3 não é
tolerante a 5 V. Prever 470–1000 µF mais 100 nF junto ao ESP32 e medir o rail de
5 V durante transmissões Wi-Fi.

**O módulo ecoa a própria transmissão.** Apesar de DE e /RE estarem em curto, o
receptor permanece ativo enquanto o nó transmite, e todo quadro enviado retorna
pelo RO precedido de um `0x00` — o glitch de quando DE sobe. Toda transação
precisa descartar esse eco depois de devolver a linha à recepção e antes de ler
a resposta; é o que faz `releaseBusAndDropEcho()`, usada pelos três caminhos
Modbus. Uma escrita que pule esse descarte lê o próprio quadro de volta e falha
por CRC, sem nunca ver a resposta do drive.

Configuração: slave 1, 9600 baud, 8N2. O seletor
`MODULO_TECNAL_ALVO` deve coincidir com `MODULO_TECNAL` do Hub; nesta implantação
ambos estão em `2` (`ModuloTECNAL_2`).

## Controle direto e segurança

O nó só assume o comando depois de receber os quatro campos v10 do Hub,
confirmar `P1-01 = 0x0002` (modo velocidade) e descobrir onde estão as DIs de
que precisa. O mapa **não é fixo**: ele varre `P2-10..P2-17` e `P2-36`,
identifica SON (`0x01`), SPD0 (`0x14`) e SPD1 (`0x15`) pelo byte baixo, aceita
apenas contato tipo A, e monta a máscara de P3-06 e os estados de P4-07 a partir
disso. No drive do Módulo 2 o resultado é:

```text
SON=DI1  SPD0=DI2  SPD1=DI3
P3-06 = 0x0007      DIs por comunicação
P4-07 = 0x0002      parado (SON=0, SPD0=1, SPD1=0)
P4-07 = 0x0003      rodando (SON=1, SPD0=1, SPD1=0)
```

Faltando SON ou SPD0, o nó recusa a tomada de controle, lista as nove DIs no log
e reporta falha 1. Sem SPD0 o par SPD1/SPD0 fica em `00`, que em modo S seleciona
o comando analógico do CN1 e nunca P1-09.

Depois ele mantém P2-30=5 (escritas em RAM). P1-09 é escrito por `10H`, duas
words, unidade 0,1 rpm e word baixa primeiro, e confirmado por `03H`. A escrita
de P4-07 é confirmada apenas nos bits que P3-06 delega ao software: o registrador
não é espelho, pois a leitura devolve o estado das DIs após a combinação com
P3-06 e a escrita define só as SDI de software.

P3-06 é bit a bit — as DIs fora da máscara continuam integralmente no CN1 — e é
volátil, de modo que religar o drive devolve tudo ao comando físico. O nó nunca
escreve `P2-1x`: atribuir função a uma DI é ato de comissionamento, não efeito
colateral de firmware.

O Hub é consultado a cada 500 ms. Se nenhum comando válido chegar dentro do
lease de 3000 ms, o nó escreve P1-09=0 e remove SON. Se o nó reiniciar e detectar
que P3-06 ainda pertence a uma sessão direta anterior, faz a mesma parada antes
de iniciar o Wi-Fi. Com Hub antigo e P3-06 físico, permanece passivo — por isso
este firmware deve ser gravado primeiro.

Quando o Hub pede a via UART/CN1 e P3-06 já está físico, a liberação é um no-op:
não há nada a soltar, e reescrever P1-09 ou P4-07 mexeria num drive que pertence
à placa original. O nó apenas confirma a via.

Essa proteção não cobre travamento total do ESP32 ou rompimento do RS-485.
Configure e ensaie o timeout interno do drive (P3-10, se disponível nesta
revisão), a reação P3-03 e o E-stop físico antes de uso de campo.

## Protocolo HTTP

`GET /servoCommand`, a cada 500 ms:

```json
{"motor_cmd_id":123,"motor_rpm":1000,"motor_enable":1,"motor_lease_ms":3000}
```

Reset de energia e `poll_ms` podem vir no mesmo JSON e permanecem eventos FIFO.

`GET /servoData`, a cada 1 s, envia a telemetria existente mais:

```text
control_capable, motor_ack, motor_applied_rpm,
motor_control_active, motor_control_fault
```

Códigos de falha: `0=ok`, `1=perfil incompatível`, `2=falha de aplicação`,
`3=lease expirado`, `4=comando inválido`, `5=drive indisponível`.

## Compilação e verificação

```powershell
arduino-cli compile --fqbn esp32:esp32:esp32s3 --warnings all ASDA_B2_Servo_Node
python .\ASDA_B2_Servo_Node\verify_direct_motor.py
```

Na Arduino IDE:

```text
Board:           ESP32S3 Dev Module
USB Mode:        Hardware CDC and JTAG
USB CDC On Boot: Disabled
Upload Mode:     UART0 / Hardware CDC
Serial Monitor:  115200 baud
```

Os sketches em `Software/testes-bancada/` permanecem separados e não devem ser
gravados como firmware final.
