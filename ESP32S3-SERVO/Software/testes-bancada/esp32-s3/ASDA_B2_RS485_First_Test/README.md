# Primeiro teste RS-485 do Delta ASDA-B2

Este sketch testa a telemetria do servo drive `ASD-B2-0421-B` com o
servomotor `ECMA-C20604ES`. Ele e independente do firmware
`TECNAL_ESP32_v7.ino` e nao deve ser incorporado ao firmware principal nesta
etapa.

O teste e deliberadamente somente leitura: o unico codigo Modbus transmitido e
`03` (Read Holding Registers). O sketch nao habilita o servo, nao muda a
velocidade e nao grava parametros.

## Arquivos

```text
ASDA_B2_RS485_First_Test/
├── ASDA_B2_RS485_First_Test.ino
└── README.md
```

Abra `ASDA_B2_RS485_First_Test.ino` pela Arduino IDE. O nome da pasta e o nome
do arquivo `.ino` devem permanecer iguais.

## Configuracao da Arduino IDE

Use estas opcoes:

```text
Board:                    ESP32S3 Dev Module
USB Mode:                 Hardware CDC and JTAG
USB CDC On Boot:          Disabled
Upload Mode:              UART0 / Hardware CDC
Monitor Serial:           115200 baud
```

Conecte o computador ao conector **`UART` / `COM`** da placa. Ele passa pelo
chip USB-serial embarcado (CH340/CP2102), que fala com o ESP32-S3 por `UART0`
(`GPIO43`/`GPIO44`).

`USB CDC On Boot` precisa ficar **desabilitado**: com essa opcao ligada, o
objeto `Serial` do Arduino aponta para o USB nativo em vez do `UART0`, e o
Monitor Serial pela porta COM fica mudo.

Divisao dos dois UARTs neste projeto:

```text
UART0  GPIO43 / GPIO44   console e upload, pela entrada COM
UART1  GPIO17 / GPIO18   barramento RS-485 (HW-519)
```

Caso a porta nao apareca no primeiro upload:

1. mantenha `BOOT` pressionado;
2. pressione e solte `RESET`;
3. solte `BOOT`;
4. selecione a nova porta na Arduino IDE;
5. repita o upload.

Use um cabo USB com transmissao de dados.

## Ligacoes

### ESP32-S3 para HW-519

```text
ESP32-S3 3V3     -> VCC do HW-519
ESP32-S3 GND     -> GND do HW-519
ESP32-S3 GPIO17  -> RXD do HW-519
ESP32-S3 GPIO18  <- TXD do HW-519
```

O HW-519 controla automaticamente a direcao do barramento; nao existe ligacao
`DE/RE` neste teste.

Antes de ligar TX/RX, confirme com multimetro que o lado TTL do HW-519 opera
entre aproximadamente 0 e 3,3 V.

### Nao use GPIO43/GPIO44 para o RS-485

Correcao importante em relacao a versao anterior deste teste.

`GPIO43` e `GPIO44` sao `U0TXD`/`U0RXD` e estao ligados ao chip USB-serial da
propria placa — aquele que da origem a entrada `COM`. Esse chip fica alimentado
junto com a placa e mantem a saida dele empurrando o `GPIO44`, brigando com o
`TXD` do HW-519.

Sintoma exato desse conflito:

```text
LED RXD do modulo pisca em sincronia com o ESP   -> transmissao OK
nenhum byte volta                                -> recepcao morta
tudo aparece como TIMEOUT na tabela
```

GPIOs a evitar no ESP32-S3: `0`, `3`, `45`, `46` (strapping), `19`/`20` (USB
nativo), `26`-`32` (flash SPI), `33`-`37` em modulos com PSRAM octal
(N8R8/N16R8) e `43`/`44` (UART0).

### HW-519 para CN3 do ASDA-B2

```text
HW-519 A+   -> CN3-5 RS485+
HW-519 B-   -> CN3-6 RS485-
HW-519 GND  -> CN3-1 GND     (terceiro borne do bloco de 3 vias)
HW-519 R0   -> nao conectar
malha/dreno -> nao conectar
```

O terceiro borne do bloco, antes nao identificado, e o **GND** do modulo. A
ligacao ao `CN3-1` nao e opcional: RS-485 e diferencial, mas exige que a tensao
de modo comum entre os dois lados fique dentro de -7 V a +12 V.

O `R0` **nao e um sinal**: e o jumper que insere o terminador de 120 ohms entre
`A` e `B`. Em cabo curto de bancada a 38400 baud, deixe **aberto** — fecha-lo
carrega a rede de bias do modulo e derruba a tensao diferencial de repouso.
Terminacao so faz sentido em cabo longo, e aí com 120 ohms nas duas pontas.

## Parametros esperados no drive

```text
P1-01 = 0002
P3-00 = 0001
P3-01 = 0033
P3-02 = 0066
P3-05 = 0000
P3-06 = 0000

P0-17 = 00007
P0-18 = 00012
P0-45 = 00054
```

### Dois padroes de fabrica bloqueiam a comunicacao

Conferido no manual (revisao maio/2018):

| Parametro | Fabrica | Necessario | Bloqueia? |
|-----------|---------|------------|-----------|
| P3-00 endereco  | **007F** (127) | `0001` | **Sim** — o drive nao atende no endereco 1 |
| P3-01 baud      | 0033 | `0033` | Nao — ja correto |
| P3-02 formato   | 0066 | `0066` | Nao — ja correto |
| P3-05 mecanismo | **1** | `0` | **Sim** — porta dedicada ao ASDA-Soft |

Baud e formato ja saem de fabrica nos valores deste projeto. Endereco e
mecanismo, nao. Se a auditoria falhar em tudo e esses dois nunca foram
alterados pelo painel, o problema esta aqui — nao na fiacao.

`P3-00` so entra em vigor apos power-cycle.

O protocolo resultante e:

```text
Slave:       1
Baud:        38400
Formato:     8N2
Protocolo:   Modbus RTU
```

O sketch le esses parametros na inicializacao e informa `OK`, `DIVERGENTE` ou
uma falha de comunicacao. A auditoria nunca corrige valores automaticamente.

## Sequencia do primeiro teste

1. Deixe o servo inicialmente desabilitado.
2. Confira todas as ligacoes com o equipamento desligado.
3. Energize o ESP32-S3 e abra o Monitor Serial em 115200 baud.
4. Confirme `Autoteste CRC-16 Modbus: OK`.
5. Confirme que a auditoria mostra os valores esperados.
6. Com o motor parado, verifique velocidade proxima de zero.
7. Acione o motor somente pelo controle ja existente no CN1.
8. Confira se RPM acompanha a rotacao e se torque/carga crescem sob carga.
9. Verifique que nenhum alarme novo aparece no drive.

## Saida no Monitor Serial

A tabela e atualizada uma vez por segundo:

```text
tempo_ms | COMM | DRIVE | ALM | RPM | torque_% | torque_Nm | carga_% | potencia_W | energia_Wh | OK/ERROS
```

- `COMM`: resultado da transacao Modbus.
- `DRIVE`: `OFF`, `READY`, `SON` ou `ALARM`, derivado de `P0-46`.
- `ALM`: codigo de `P0-01`; `0000` significa ausencia de alarme.
- `RPM`: velocidade de `P0-09`, com resolucao de 0,1 rpm.
- `torque_%`: torque de `P0-44`, com resolucao de 0,1%.
- `torque_Nm`: torque calculado usando o nominal de 1,27 N.m.
- `carga_%`: carga media de `P0-10`.
- `potencia_W`: potencia mecanica instantanea no eixo.
- `energia_Wh`: integral assinada da potencia mecanica desde o boot ou ultimo
  comando `R`.
- `OK/ERROS`: numero acumulado de transacoes validas e invalidas.

A potencia e a energia calculadas sao mecanicas no eixo; elas nao representam
diretamente a potencia e a energia eletricas consumidas da rede. Valores
negativos podem representar sentido oposto ou regeneracao, dependendo dos
sinais de velocidade e torque fornecidos pelo drive.

Comandos locais aceitos pelo Monitor Serial:

```text
H  mostra a ajuda
R  zera a energia acumulada
```

Esses comandos sao tratados apenas no ESP32 e nao sao enviados ao drive.

## Diagnostico

### TIMEOUT

**Antes de mexer na fiacao, rode `../ASDA_B2_RS485_Scan`.** Aquele sketch nao
assume endereco, baud nem formato: ele varre as tres coisas e imprime os bytes
crus, o que separa **silencio** de **eco** de **lixo** — tres diagnosticos
diferentes que este sketch reporta todos como `TIMEOUT`.

Suspeitos em ordem de probabilidade:

1. `P3-00` ainda em `007F` (padrao de fabrica);
2. `P3-05` ainda em `1` (padrao de fabrica);
3. RS-485 ligado em `GPIO43`/`GPIO44` (conflito com o chip USB-serial);
4. `CN3-1` (GND) sem contato;
5. contato mecanico do conector CN3;
6. `A`/`B` invertidos.

O item 3 tem sintoma proprio: o LED `RXD` do modulo pisca em sincronia com o
ESP e nada volta. Isso prova que a transmissao funciona e isola a falha no
caminho `TXD do modulo -> RX do ESP`.

Verifique tambem:

- `38400`, `8N2` e Modbus RTU;
- alimentacao de 3,3 V do HW-519;
- jumper `R0` aberto.

Se tudo estiver correto, inverta A/B apenas como diagnostico, pois alguns
fabricantes usam nomenclatura oposta.

### CRC ou INTERBYTE

Verifique terminacao, comprimento do cabo, aterramento, proximidade dos cabos do
motor e conexoes frouxas. O sketch utiliza 12 ms de silencio entre frames e
timeout total de 250 ms.

### Upload ou Monitor Serial falha

Confirme que o cabo esta no conector USB nativo/OTG e que `USB CDC On Boot` esta
habilitado. Os pinos GPIO43/GPIO44 devem permanecer reservados exclusivamente ao
HW-519 durante este teste.

