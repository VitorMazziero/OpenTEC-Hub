# Bring-up do enlace RS-485 no ESP32-S3 — diário de diagnóstico

**Data:** 2026-09-01
**Objetivo:** subir o mestre Modbus RTU embarcado (leitura de telemetria do
Delta ASDA-B2, função `03H`) contra um drive que já sabíamos bom.
**Resultado:** enlace localizado até o último componente. **O receptor do
módulo HW-097 está defeituoso** — transmite, mas não recebe. Tudo o mais na
cadeia (ESP32, firmware, divisor, cabo, drive) está provado bom. **Ação:
trocar o módulo.**

> **RESOLVIDO 2026-09-01:** módulo HW-097 trocado. Sem mais nenhuma alteração
> (mesmo firmware, divisor, GPIOs, terra, A/B), o `Autoscan_ESP32` travou em
> `9600 8N2` e leu todos os parâmetros — `RESPOSTA(de 0x01, P3-00=0x0001)`,
> P3-01=0x0011, P3-02=0x0066, P3-07=0x0064, P1-01=0x0002, P0-00=0x03F6.
> **Fase 3 completa: a cadeia ESP32-S3 fala Modbus com o drive.** Diagnóstico
> confirmado — o módulo antigo tinha o receptor morto. Próximo passo: parser
> `/servoData` do plano de integração v7.

---

## 0. Ponto de partida

O dongle USB-RS485 (CH340) já havia conversado com o drive na primeira
tentativa — 9600 8N2, endereço 1, `P3-00 = 0x0001` (ver
`PC_Modbus_Scan/asda_scan.py`). Portanto **drive, protocolo e a fiação do CN3
(5=D+, 6=D−) estavam provados** antes desta sessão. O que faltava era
reproduzir isso a partir do microcontrolador.

---

## 1. Arduino UNO — abandonado (contenda no D0)

Primeiro alvo foi o UNO com UART de hardware (`ASDA_B2_Scan_HWUART`,
`ASDA_B2_Autoscan_HWUART`). Sintoma: **silêncio total**, e o PC não conseguia
nem mandar tecla para o Arduino.

**Causa:** no UNO o barramento divide os pinos `D0/D1` com o chip USB. Neste
clone (CH340), o `RO` do módulo e o TX do chip USB disputam o `D0`, e a
recepção nunca acontece. Confirmado: com o `RO` soldado no `D0`, as teclas do
PC também eram bloqueadas.

**Decisão:** migrar para o ESP32-S3, cuja `UART1` (GPIO17/18) é independente do
console USB. É também o alvo final do plano de integração.

---

## 2. ESP32-S3 — a eliminação sistemática

Ligações: `DI`→GPIO17, `RO`→divisor→GPIO18, `DE`+`RE`→GPIO16, `A`→CN3-5,
`B`→CN3-6, `VCC`=5V (placa), terra em estrela (ESP32 + HW-097 + CN3 + drive).
O `RO` é 5 V; desce a 3,3 V por **divisor 1 kΩ/2 kΩ** (GPIO do ESP32 não tolera
5 V).

Cada teste isolou uma parte da cadeia. Os sketches foram preservados em
`../testes-bancada/esp32-s3/`.

| # | Teste | Sketch | Resultado | Conclusão |
|---|-------|--------|-----------|-----------|
| 1 | Varredura Modbus | `Autoscan_ESP32` | silêncio | drive não responde à nossa leitura |
| 2 | Escuta passiva (não transmite) | `Diag_ESP32` | 0 bytes | linha quieta em repouso — **sem problema de bias** |
| 3 | TX estático (driver forçado, multímetro) | `TXtest_ESP32` | `DE`=3,3 V · `DI`=3,3 V · `A−B`=**+2,9 V** | driver **funciona**; DE/DI chegam |
| 4 | RX estático (multímetro) | `TXtest_ESP32` | `RO`=4,82 V → GPIO18=3,32 V | caminho RX passa **nível DC** |
| 5 | Fluxo de TX (0x55 contínuo) | `TXflow_ESP32` | `DI`=1,7 V · `A−B`=0,27 V | **TX transmite dado de verdade** (média casa com 0x55) |
| 6 | Loopback interno da UART (sem pinos) | `IntLoop_ESP32` | **864 bytes/s** | periférico UART1 (TX+RX) **perfeito** |
| 7 | Loopback de pino (jumper 17→18) | `PinLoop_ESP32` | **864 bytes/s** | pino **GPIO18 recebe sinal externo — OK** |
| 8 | Loopback pelo módulo (RE no GND) | `TXflow_ESP32` | 0 bytes; `RO`=4,72 V **preso** | `RO` não segue o dado — receptor não gera saída |
| 9 | Divisor | ohmímetro | 1 kΩ / 2 kΩ | valores **corretos**; passa `RO` fielmente (GPIO18 = RO×2/3) |
| 10 | Cabo A/B até o drive | continuidade | ok (o mesmo do dongle) | bus **chega ao drive** |
| 11 | **Sniffer com o dongle** (paralelo) | `asda_scan` / PySerial | ver abaixo | **prova final** |

### A prova final — dongle escutando o barramento

Com o dongle em paralelo no barramento, ouvindo passivamente a 9600 8N2
enquanto o ESP32 transmitia:

```
FF 03 03 00 00 01 91 90   01 03 02 00 01 79 84
└──── pedido do ESP32 ────┘ └──── RESPOSTA do drive ────┘
```

Decodificando a resposta: `01`=escravo 1, `03`=função, `02`=2 bytes,
**`00 01` = P3-00 = 0x0001**, `79 84`=CRC válido.

Ou seja, no fio existem, limpos, **o nosso pedido E a resposta do drive**. O
dongle (outro transceptor, no mesmo barramento) ouve os dois. O HW-097 não.

---

## 3. Conclusão

| Componente | Veredito | Como foi provado |
|------------|----------|------------------|
| ESP32-S3 / UART1 | ✅ bom | loopback interno 864 B/s |
| Pino GPIO18 (RX) | ✅ bom | loopback de pino 864 B/s |
| Divisor 1k/2k no RO | ✅ bom | valores conferidos; passa RO fielmente |
| Transmissão (DI→driver→A/B) | ✅ boa | `A−B`=2,9 V estático, toggla no dinâmico; **sniffer vê o quadro limpo** |
| Cabo A/B até o CN3 | ✅ bom | mesmo cabo do dongle; continuidade |
| Drive ASDA-B2 | ✅ bom | **sniffer vê a resposta `01 03 02 00 01 79 84`** |
| **Receptor do HW-097** | ❌ **defeituoso** | `RO` preso em mark; não entrega a resposta que está no fio |

**O receptor do módulo HW-097 está morto.** Ele transmite (o driver funciona e
o quadro sai limpo no barramento), mas o lado de recepção não segue a linha:
`RO` fica preso em nível de mark, mesmo com a resposta do drive presente no
fio. É um modo de falha conhecido desses módulos MAX485 baratos.

### Ação

**Trocar o HW-097** por outro (ou qualquer módulo MAX485 / MAX3485). Toda a
demais montagem permanece: firmware, divisor no `RO`, GPIO17/18/16, terra em
estrela, `A`→CN3-5, `B`→CN3-6, `VCC`=5 V. Ao trocar, rodar o `Autoscan_ESP32`
— deve ler todos os parâmetros de imediato.

---

## 4. Falsas pistas registradas (para não repetir)

- **Conversor de nível bidirecional (BSS138/TXS0108E) no RO:** mascarava a
  linha, segurando o GPIO18 em alto pelo pull-up interno. Passava DC mas
  escondia o sinal real. **Trocado pelo divisor passivo 1k/2k**, que revela o
  nível verdadeiro. Para uma saída 5 V→3,3 V unidirecional (o `RO`), o divisor
  é mais confiável que o conversor auto-direção.
- **Fluxo de 0x00 no RX:** era artefato do próprio TX — durante a transmissão o
  receptor fica desligado (RE alto), o `RO` vai a alta impedância e o divisor
  puxa o GPIO18 para baixo, gerando 0x00. **Corrigido drenando o buffer logo
  após transmitir.**
- **Polaridade A/B:** a troca não mudou nada porque o problema era o receptor,
  não a polaridade. Mantida a original (`A`→5, `B`→6), a do dongle.
- **Temporização do DE:** segurar o DE por um quadro inteiro após o `flush()`
  atropela uma resposta rápida do drive. Reduzido para ~1 caractere de folga.
  (`flush()` no ESP32 já espera o quadro sair — o loopback interno confirma.)
- **Drive desligado:** parte dos testes iniciais foi feita com o drive
  desligado; o "lixo" recebido nesse período era ringing da própria transmissão
  numa linha sem carga, não resposta.
- **"Silêncio" ≠ conectado:** um MAX485 com A/B soltos puxa `RO` para mark
  (fail-safe), que se lê igual a "silêncio". Não confundir.

---

## 5. Configuração final validada (para o módulo novo)

```
   ESP32-S3                 HW-097 (MAX485, 5 V)         ASDA-B2 CN3
   GPIO17 (TX1) ─────────▶  DI                    A (D+) ──▶ CN3-5
   GPIO18 (RX1) ◀── divisor ─ RO                  B (D−) ──▶ CN3-6
   GPIO16       ─────────▶  DE + RE (em curto)
   3V3 ── (LV, se usar shifter)      VCC ──── 5 V da placa
   GND ─────────────────── GND ──── terra em estrela ──── CN3-1 / CN1

   Divisor no RO (obrigatório, RO é 5 V):
     RO ──[1k]──┬──▶ GPIO18
                │
              [2k]
                │
               GND
```

Parâmetros: 9600 8N2, endereço 1 (curinga `0xFF` também responde).
Resposta esperada a `03H`/`P3-00`: `01 03 02 00 01 79 84` (P3-00 = 0x0001).

### Firmware e ferramentas produzidos nesta sessão

```
ASDA_B2_Autoscan_ESP32/   varredura automática baud×formato + leitura de parâmetros (principal)
ASDA_B2_Diag_ESP32/       escuta passiva + probes com dump cru (bias vs TX)
ASDA_B2_TXtest_ESP32/     driver forçado ligado, medida DC de DE/DI/A-B
ASDA_B2_TXflow_ESP32/     TX 0x55 contínuo + contagem de eco (loopback)
ASDA_B2_IntLoop_ESP32/    loopback INTERNO da UART1 (isola o periférico)
ASDA_B2_PinLoop_ESP32/    loopback de pino GPIO17→GPIO18 (isola o GPIO18)
ASDA_B2_Autoscan_HWUART/  versão UNO (abandonada: contenda no D0)
```

Upload (arduino-cli embutido na Arduino IDE):
`arduino-cli compile/upload -p COM<x> --fqbn esp32:esp32:esp32s3 <pasta>`

---

# Sessão de 2026-09-02 — Gate A na bancada

**Objetivo:** fechar o Gate A (valores de leitura conferidos contra o painel).
**Resultado:** Gate A aprovado, com um item declarado não testável por limitação
física. Um defeito silencioso de telemetria foi encontrado e corrigido no
caminho — e a regra de projeto "somente `03H`" foi revogada por causa dele.

## O defeito: `P0-45` é volátil, e o erro não dá sinal

O torque lia `0x0000` constante enquanto a carga oscilava. Não era falha de
comunicação: `err=0` o tempo todo.

O manual (cap. 7) define `P0-44`/`P0-45` como *Status Monitor Register **(for PC
Software)***, default `0x0`, e o par é **volátil** — zera a cada religamento do
**drive** (não da placa). Com `P0-45 = 0`, `P0-44` devolve a variável de código
0, que é a **posição do encoder em pulsos**, e responde Modbus normalmente.

A prova veio com o motor girando: `P0-44` passou a rampar
(`0x15E6 0x0090` → `0x2BE9 0x0060` → `0x8215 0x0086`), que é um contador de
posição subindo, não um torque.

**Por que não bastou trocar de seletor:** o torque de retorno é o código 54, e
54 só existe na faixa 0~127 que **apenas `P0-45`** aceita. Os seletores
`P0-17`~`P0-21` param em 18, e nessa lista não há torque de retorno (os códigos
10 e 11 são torque de *comando*). A B2 também **não tem** os parâmetros de
mapeamento `P0-25`/`P0-35` das famílias A2/A3 — `P0-22`~`P0-24` são Reserved e
o capítulo salta para `P0-44`; a linha da tabela de variáveis que os cita é
texto herdado de outro manual.

**Correção:** o §8.4 libera o Grupo 0 para escrita por comunicação exceto
`P0-00`~`P0-01`, `P0-08`~`P0-13` e `P0-46` — `P0-45` é gravável. Os dois
firmwares passaram a escrever `P0-45 = 54` (`06H` em `005AH`) e a **confirmar
por leitura**: o eco do `06H` prova que o quadro chegou, não que o drive reteve
o valor. A verificação roda a cada amostra, então um religamento do drive é
curado sem reiniciar a placa. No nó de produção, amostra sem mapeamento válido
é **descartada**, não publicada.

## Curva de calibração — 9 pontos, em ensaio contínuo

Varredura de rotação durante o soak de 10 min (o operador mudou o comando
enquanto o ensaio corria):

| Comandado | Lido (média) | Erro | Torque médio |
|---:|---:|---:|---:|
| 100 | 97,85 | −2,15 % | 1,36 % |
| 200 | 197,59 | −1,20 % | 1,53 % |
| 300 | 297,39 | −0,87 % | 1,59 % |
| 400 | 397,23 | −0,69 % | 1,70 % |
| 500 | 497,74 | −0,45 % | 1,76 % |
| 600 | 598,37 | −0,27 % | 2,10 % |
| 800 | 800,36 | +0,05 % | 2,16 % |
| 900 | 901,53 | +0,17 % | 2,26 % |
| 1000 | 1003,42 | +0,34 % | 2,46 % |

```text
ajuste:  lido = 1,00617 x comandado - 4,24     residuo maximo 1,49 rpm (0,15% do FE)
```

O erro **não** é percentual constante: é negativo embaixo, cruza zero perto de
750 rpm e fica positivo em cima. Isso é ganho + offset do **comando analógico do
CN1** (placa controladora de código fechado), não escala errada da telemetria —
uma escala errada daria erro proporcional, não afim. A leitura Modbus é linear
com resíduo abaixo de 1,5 rpm em toda a faixa.

O torque acompanha: 1,36 % a 100 rpm subindo monotonicamente até 2,46 % a
1000 rpm, que é atrito viscoso e ventilação crescendo com a rotação. Nas paradas
cai a 0,00–0,01 % com `ZSPD=1`. Nenhum desses comportamentos apareceria se
`P0-44` ainda estivesse devolvendo posição.

## Estabilidade

| Ensaio | Duração | Leituras | `err` | Quadros com falha |
|---|---:|---:|---:|---:|
| Motor parado | 690 s | 1638 | 0 | 0 |
| Motor girando, 9 rotações | 619 s | 1389 | 0 | 0 |

Zero erros nos dois. A linha de base e o ensaio sob ruído dão o mesmo resultado:
**não há degradação do RS-485 com o servo em operação** — que era a dúvida que
motivou escolher RS-485 neste projeto.

## O sinal do torque: não testável nesta bancada

Duas limitações independentes, ambas físicas:

1. A placa controladora do CN1 é de **código fechado** e só comanda um sentido.
2. O "zerar" dela **desenergiza o servo** em vez de desacelerar sob controle. Na
   captura a 337 ms/amostra, o estado vai de `0x0083` (SON=1) direto para
   `0x0005` (SON=0) numa única amostra — e em t=117,10 s o `SON` caiu com o
   motor ainda a 503 rpm, ou seja, ele **desce por inércia**. Sem desaceleração
   controlada não há torque de frenagem, logo não há torque negativo para medir.

**O que ficou provado assim mesmo:** o caminho de decodificação com sinal
funciona. Numa amostra de parada o `P0-09` leu `words=[0xFFFD 0xFFFF]` →
`low-first` = −3 → **−0,30 rpm**. É exatamente o caso que um erro de sinal
quebraria (word alto em `0xFFFF`), e ele decodificou certo. Falta a prova do
*sentido*, não a do parsing.

## Ordem de words — decidida nas duas grandezas

| Grandeza | `low-first` | `high-first` |
|---|---:|---:|
| `P0-09` a 100 rpm | 96,60 rpm | 6.330.777 rpm |
| `P0-44` torque | 1,40 % | 91.750 % |
| `P0-09` negativo | −0,30 rpm | −13.107 rpm |

## Escalas confirmadas

- `P0-09` (código 7): **0,1 rpm** — manual e bancada concordam (raw 966 = 96,6
  contra ~96 no painel). Note que a tabela do `P0-02`, do capítulo 4, dá o
  código 7 como `[r/min]`; a tabela de variáveis de monitoramento do capítulo 7,
  que é a que vale para os parâmetros de mapeamento, dá `0,1 rpm`. A bancada
  confirma a segunda.
- `P0-10` (código 12): **% inteiro, escala 1** — o nó já estava certo. O plano
  de testes mandava trocar para `loadRaw * 0.1f` se o painel batesse com 0,1;
  seguir aquilo teria introduzido um erro de 10x. Instrução corrigida.
- `P0-44` (código 54): **0,1 %**.

## Derivados conferidos à mão

```text
torque   1,60 % x 1,27 N.m            = 0,02032 N.m   (o no imprimiu 0,0203)
potencia 0,0203 x 96,8 x 2*pi/60      = 0,206 W       (o no imprimiu 0,21)
```

Motor confirmado como ECMA-C20604ES, `T_nominal = 1,27 N·m`.

## Ferramentas desta sessão

O `ASDA_B2_Monitor_ESP32` ganhou: leitura de `P0-11` (6 words a partir de
`0x0012`), escrita `06H` de `P0-45` com confirmação por leitura, e
`SAMPLE_INTERVAL_MS` ajustável — em 0 o ciclo fica em **337 ms** medidos, que é
o que permitiu pegar a transição de parada. Restaurado para 1000 ms ao fim do
ensaio.

---

# Sessão de 2026-09-02 (continuação) — Gates B e C

## Gate B — o nó de produção na placa

Gravado em COM9. Subiu, associou e empurrou telemetria de imediato. Como o hub
ainda estava com firmware pré-servo, a resposta foi `404`:

```text
 No de telemetria - Delta ASDA-B2 -> TECNAL Hub v9
 Leitura 03H + escrita 06H apenas em P0-45 (seletor volatil).
[SERVO] Watchdog ativo com timeout de 10 s
[SERVO] Associado ao hub. IP: 192.168.4.3
[SERVO] /servoData respondeu 404
```

O `404` é um bom sinal: o nó **só empurra depois da primeira leitura Modbus
válida**, então esse log prova Modbus, Wi-Fi e caminho de push de uma vez.

Duas observações que economizam tempo de diagnóstico no futuro:

- **O nó é silencioso no sucesso do autoteste de CRC.** Diferente do monitor, ele
  só imprime em caso de falha. Não procure por uma linha de "OK" — a prova de que
  passou é ele seguir para as tarefas.
- **`E (137) task_wdt: TWDT already initialized`** aparece antes do watchdog subir.
  É ruído do ESP-IDF, que já inicializa o watchdog pelo core, e **não** é a
  "Falha ao configurar o watchdog" da §5.1 do plano de testes.

## Gate C — os dois juntos

Hub gravado com o v9 (`HubFirmwareVersion 9.0.0-dev`, `HubProtocolVersion 9`).

> **Nota de build:** o `sketch.yaml` do v9 fixa o core esp32 **3.3.11** e a
> máquina tinha o 3.3.10. A primeira compilação baixa ~1 GB de toolchain
> (673 MB RISC-V + 395 MB Xtensa) e demora bastante. Não é erro. As seguintes
> são rápidas.

### O laço fechado

O plano tratava o hub como observador passivo. Ele é mais que isso: **também
comanda** a rotação, por um caminho fisicamente independente do de medida.

```text
comando:   {"motorSetpoint":N} -> serial -> UART2 (1V + NA) -> Modulo TECNAL -> CN1 -> drive
medida:    drive -> RS-485 -> no -> Wi-Fi -> /servoData -> /readData
```

Os dois só se encontram no eixo do motor, então a medida seguir o comando prova
a integração inteira de uma vez:

| Comandado | `ServoRpm` medido | Estado |
|---:|---|---|
| 200 | 34,2 → 197,5 → 198,0 → 198,7 → 199,1 → 198,5 | `ServoState:2` |
| 600 | 277,4 → 598,8 → 598,9 → 599,1 → 599,5 → 598,7 | `ServoState:2` |
| 0 | 0,0 no quadro seguinte | `ServoState:1` |

`ServoCommErr:0` em todos. Os primeiros valores de cada patamar são a rampa
capturada em quadro, não erro.

### Presença

```text
t=1199.0  false  sem valores
t=1201.0  true   com valores, ok=9        <- reconhecido no 1o quadro apos voltar
t=1233.1  true   ok=84 congelado
t=1237.1  true   ok=84 congelado          <- presenca ainda fresca
t=1239.1  false  valores somem            <- janela expirou
```

O `ServoCommOk` congela três quadros antes do flag virar, o que localiza o
último push entre `t=1231,1` e `t=1233,1`. O `false` aparece em `t=1239,1`:
expiração entre **6,0 e 8,0 s**, coerente com `kPresenceTimeoutMs = 6000`
observado através de um quadro a cada 2 s. Os dez campos `Servo*` somem no
mesmo quadro em que o flag cai.

### Comandos hub→nó

```text
t=294.9  ServoCommandPending: true   fila=1   Wh=0.019873   <- enfileirado
t=296.9  ServoCommandPending: false  fila=0   Wh=0.020717   <- o no puxou
t=298.9  ServoCommandPending: false  fila=0   Wh=0.000588   <- executou
```

Sem ACK neste enlace, a confirmação do reset é a queda do `ServoEnergyWh` — feita
com o motor a 600 rpm, o que é prova mais forte do que com o motor parado. O
`{"servoPollMs":2000}` mudou a taxa de `ServoCommOk` de +6 para +3 por quadro de
2 s, exatamente o pedido.

## Erro de método: não simule ausência segurando a placa em reset

Custou três medidas e quase virou um relatório de bug no hub.

Segurando o nó em reset pelo pino EN, o hub emite ~3 quadros e **para de emitir
completamente**, voltando só quando a placa é liberada. O `Time` do hub continua
monotônico, então ele não reinicia — apenas emudece. Como o silêncio começava
por volta de 6 s após o último push, parecia que a expiração da janela de
presença estava travando o hub.

Não está. Com o nó **desconectado da alimentação**, o hub publica
`ServoOnline:false` em 60 quadros consecutivos a 2,0 s, sem uma única lacuna,
com os dez campos de valor ausentes e o `ServoCommEnabled` presente.

Uma placa travada em reset não é uma placa ausente: o rádio morre sem encerrar a
associação, e isso produz no hub um efeito que o desligamento real não produz.
**Para testar ausência, corte a alimentação.**

## `HubStations` não reflete estações em tempo real

O campo é `WiFi.softAPgetStationNum()`, a contagem da tabela de associação do
SoftAP. Ele permaneceu em `1` com o nó presente **e** com o nó desconectado por
mais de 3 minutos. Não é erro de contagem: quando uma estação some sem se
desassociar, a entrada só cai no tempo de inatividade do AP, que no ESP-IDF é de
5 minutos por padrão; e ao reassociar, o nó volta com o mesmo MAC, ocupando a
mesma entrada.

**Nenhum flag `*Online` depende desse número** — todos usam frescor de push, com
janelas próprias (a do servo é `kPresenceTimeoutMs = 6000`). Se for preciso que o
campo responda rápido, a alteração seria `esp_wifi_set_inactive_time(WIFI_IF_AP, N)`
na subida do AP, mas isso muda o comportamento do Wi-Fi para todos os nós e um
valor agressivo pode derrubar nó lento em ambiente ruidoso. Não alterado.

## O que ficou por fazer

- **Gate B:** reescrita do `P0-45` após religar o **drive**; descarte de amostra
  com mapeamento inválido; 10 min com o hub ausente.
- **Gate C:** `{"servoComm":0/1}` e persistência em NVS (itens 5 a 7); extremos e
  recusas de `servoPollMs` e a fila de 8 (itens 9 a 15); robustez do enlace com
  queda do hub (itens 16 a 19).
- **Etapa 4:** regressão dos demais dispositivos — nenhum está na bancada.
- **Etapa 5:** soak de 2 h.
- **Gates D, E e F:** o aplicativo no PC, ainda não iniciado.

---

# Sessão de 2026-09-02 (final) — migração para o Módulo TECNAL 2

O nó saiu da bancada do Módulo 1 e foi para o Módulo 2, que tem **outro drive**
e outro Módulo TECNAL. Os dois firmwares foram regravados com o seletor em `2`
(`MODULO_TECNAL 2` e `MODULO_TECNAL_ALVO 2`), confirmado pelos banners.

Três problemas apareceram, e a ordem em que foram separados é o que interessa.

## 1. O drive novo: parâmetros e alarme

Ao chegar, o drive não respondia. Duas causas somadas:

- **Parâmetros de fábrica.** Drive novo vem em `P3-00 = 007F` e RS-232
  (`P3-05 = 1`). Nessa condição ele ignora um mestre pedindo o slave 1 em RS-485,
  sem devolver erro nenhum.
- **`AL011` — Encoder Error.** O motor não estava conectado. A tabela de causas
  do manual lista só fiação e encoder; **nenhum parâmetro corrige isso**, e o
  manual diz que o alarme se limpa religando o drive.

O scanner do PC (`asda_scan.py`, dongle USB-RS485) foi o que decidiu: ele
conversou com o drive na primeira tentativa e leu `P3-00=0x0001`,
`P3-01=0x0011`, `P3-02=0x0066`, `P3-05=0x0000`, `P1-01=0x0002`. Isso provou, em
um comando, que drive e parâmetros estavam bons — e que o problema estava na
nossa montagem, exatamente o propósito para o qual o script foi escrito.

**Divergência anotada:** `P3-07 = 0x0040` (64) neste drive, contra `0x0064` (100)
no do Módulo 1. É o atraso de resposta, em unidades de 0,5 ms. Não impede
comunicação, mas é uma variável a menos se for igualado em 100.

## 2. O RS-485 mudo — o cerco por eliminação

Com o drive provado bom, o nó continuava em `resposta curta: 0 byte(s)`.

O **sniffer** decidiu a metade do problema. Escutando o barramento com o dongle,
apareceram os dois lados no mesmo quadro:

```text
01 03 00 12 00 06 65 CD                  <- pedido do ESP32
01 03 0C 03 A2 00 00 ... 2F CE           <- RESPOSTA do drive
```

Ou seja: **o ESP32 transmite, o drive responde, e o ESP32 não ouve.** Isso
elimina de uma vez baud, formato, endereço, fiação A/B, terra e o transmissor.

O resto foi isolamento por sketch, um elo por vez:

| Teste | Resultado | O que elimina |
|---|---|---|
| `ASDA_B2_Autoscan_ESP32` | silêncio em todo baud × formato, inclusive curinga `0xFF` | configuração serial |
| `asda_scan.py` (dongle) | drive responde a tudo | drive e parâmetros |
| sniffer no barramento | vê pedido **e** resposta | transmissor, `DE//RE`, A/B, terra |
| `ASDA_B2_IntLoop_ESP32` | 864 bytes/s, `0x55` | UART, firmware, temporização |
| `ASDA_B2_PinLoop_ESP32` | 864 bytes/s, `0x55` | GPIO18, solda, fiação da placa |

Sobrou um trecho: **`RO` → divisor 1k/2k → GPIO18**. Refazer a ligação resolveu.

**Terceira vez que este mesmo trecho falha neste projeto.** Recomendação: soldar
as três ligações do transceptor em vez de manter Dupont.

## 3. O teste do `P0-45`, finalmente

O religamento do drive (feito para limpar o `AL011`) zerou o `P0-45`, e o
sniffer registrou `01 03 02 00 00 B8 44` — `P0-45 = 0x0000`. Com a recepção
restaurada, o nó publicou:

```text
ServoOnline      True          ServoState        2 (SON)
ServoRpm         92.7          ServoAlarm        0
ServoTorquePct   1.4           ServoCommOk       255
ServoTorqueNm    0.0178        ServoCommErr      1
ServoPowerW      0.17          SensorCommOK      True
```

**O torque são é a prova.** Com o seletor em 0, o `P0-44` devolveria a posição do
encoder — ordem de 10⁵ e rampando. Um valor de 1,4 % só é possível se o nó leu o
seletor em zero, escreveu 54 por `06H` e confirmou por leitura. O item do Gate B
que motivou toda a escrita `06H` fechou aqui, num drive que nunca tinha sido
tocado.

Derivados conferidos: `1,4 % × 1,27 = 0,01778 N·m` (publicado `0,0178`);
`0,0178 × 92,7 × 2π/60 = 0,173 W` (publicado `0,17`).

`ServoCommErr = 1` em 256 leituras — um único erro, na primeira transação após o
boot. Não é padrão de ruído: com ruído o contador cresceria, e ele está parado.

## Pendência que não pode ser esquecida

O hub do **Módulo 1** continua com `servoComm` em `true` e sem nó. Isso publica
`ServoOnline:false` + `ServoCommEnabled:true` para sempre, que o aplicativo lê
como falha permanente em vez de "este módulo não tem servo". A correção é um
comando único na serial daquele hub: `{"servoComm":0}`. Ver §2.2 do plano de
testes.

## Fecho de 2026-09-02

**`{"servoComm":0}` aplicado no hub do Módulo 1.** O estado enganoso
(`ServoOnline:false` + `ServoCommEnabled:true`, que o aplicativo leria como falha
permanente) foi corrigido:

```text
ServoCommEnabled: false
[ESP32_INFO]: Parametros salvos na NVS (Flash)
```

Persistência confirmada por reinício: após o boot o hub já sobe com
`ServoOnline:false` + `ServoCommEnabled:false`. O Módulo 1 agora se declara
corretamente como "sem servo".

**A montagem do Módulo 2 foi soldada e encapsulada em resina.** Isso encerra a
recomendação sobre Dupont — e muda a natureza do risco. O trecho
`RO` → divisor 1k/2k → GPIO18 falhou três vezes neste projeto, e agora não é mais
acessível para medição nem reparo. Se aquele enlace voltar a emudecer, o
diagnóstico por software continua válido (sniffer, `IntLoop`, `PinLoop`), mas a
correção passa a ser a substituição do conjunto inteiro.

# Sessão de 2026-09-08 — o RPM que não saía por nenhuma das duas vias

Sintoma relatado: depois da mudança que introduziu a via selecionável, o setpoint
de rotação parou de funcionar tanto por UART/CN1 quanto por Modbus. Duas vias
independentes quebrando ao mesmo tempo é um sinal forte de causa comum a
montante, e foi por aí que o cerco começou.

## 1. O portão do Hub que engolia todo setpoint, em silêncio

`motorRouteTransitionPending` nasce `true` e só era limpo pelo ACK do
ESP32S3-driver. Em `processOutgoingCommands()`:

```cpp
if (motorRouteTransitionPending && motorRPM > 0) {
  // Mantem o pedido pendente; ele sera aplicado depois do ACK de P3-06.
}
```

O ramo não tem prazo, não emite log e não limpa `flagMotorDirty`. Pior: valia
para **as duas vias**. A via UART/CN1 não depende do nó do servo em nada, mas
ficava refém dele. Nó que nunca confirma ⇒ Hub mudo para sempre.

A telemetria ao vivo do hub fechou o diagnóstico antes de qualquer alteração:

```text
MotorControlViaModbus:false   ServoOnline:true
ServoMotorRouteAck:-1         ServoMotorCommandAck:0
ServoMotorCommandPending:true ServoMotorCommandAgeMs:11135 (crescendo)
ServoMotorControlFault:2
```

Correção: a retenção passa a valer só para a via UART/CN1 — a única que corre o
risco de topar com P3-06 ainda em modo software — e sempre termina, por ACK, por
ausência do nó ou por prazo de 5 s. Na via Modbus não há retenção: o próprio
`setMotorDesired()` substitui o comando da troca por uma revisão que já leva via
e rotação juntas, e o nó sequencia P4-07/P3-06 sozinho.

**Regra que este defeito ensina:** um periférico ausente ou defeituoso nunca pode
desabilitar um caminho que não depende dele. E retenção sem prazo e sem log é
indistinguível de comando perdido.

## 2. Por que o nó nunca confirmava (falha 2 perpétua)

`releaseMotorToUartCn1()` reescrevia P1-09 e P4-07 mesmo quando P3-06 já estava
em modo físico. Nesse estado não há nada a liberar, e a escrita ainda mexe num
drive que pertence à placa original. Falhava sempre, sem ACK, alimentando o
item 1. Agora sai cedo quando `P3-06 == 0x0000`, e cada etapa registra qual
escrita reprovou.

## 3. `P4-07` não é registrador espelho — confirmado no manual

Manual ASDA-B2, P4-07 (`040EH`), pág. 7-88:

> Read parameters: shows the DI status after combination
> Write parameters: writes the software SDI status

Confirmar a escrita comparando a palavra inteira reprovava escritas corretas
sempre que qualquer DI fora da máscara estivesse ativa, e reprovava sempre com
P3-06 físico. A confirmação passa a comparar apenas os bits que P3-06 delega ao
software — correto sob as duas semânticas.

Registro de método: esta correção foi feita **por hipótese**, antes da leitura do
manual, e só depois confirmada. Ela não era a causa do sintoma relatado; a causa
era o item 2. Vale como endurecimento, não como o conserto.

## 4. Falsa pista corrigida: o perfil de DIs não é constante do projeto

O log antigo dizia:

```text
[MOTOR] Perfil recusado: P1-01=0002 P2-10=0101 P2-12=0115 P2-13=0117
```

Disso concluiu-se, precipitadamente, que **nenhuma DI tinha SPD0** e que seria
inevitável comissionar o drive — escrevendo `P2-1x`, o que mudaria a função de um
pino do CN1. **A conclusão estava errada.** O código só lia DI1, DI3 e DI4,
exatamente as três que esperava; DI2 nunca foi olhada.

Com a varredura completa de `P2-10..P2-17` e `P2-36`:

```text
[MOTOR] Mapa de DIs: SON=DI1 SPD0=DI2 SPD1=DI3 -> P3-06=0007, P4-07 run=0003 stop=0002
[MOTOR] Perfil direto confirmado; controle habilitado.
```

Este drive tem SPD0 em **DI2**, SPD1 em DI3 e TCM1 em DI4 (inerte em modo
velocidade). O drive já estava corretamente comissionado — só num arranjo
diferente do que o firmware assumia. **Nenhuma mudança de fiação ou de parâmetro
foi necessária.**

O firmware passa a descobrir o mapa em vez de exigi-lo: lê as nove DIs, localiza
SON, SPD0 e SPD1 por código de função (byte baixo de `P2-1x`), aceita apenas
contato tipo A e monta máscara e estados de P4-07 a partir disso. SPD1 só entra
na máscara se existir — uma DI sem função já vale zero, mas uma DI com SPD1 no
CN1 selecionaria P1-10/P1-11 pelas costas do nó.

**Regra que esta falsa pista ensina:** um diagnóstico que lê só os campos que
espera encontrar confirma a própria hipótese. Quando o perfil é recusado, o log
agora despeja as nove DIs.

## 5. Por que SPD0 é indispensável (manual, pág. 6-14)

| SPD1 | SPD0 | Fonte do comando de velocidade |
|---|---|---|
| 0 | 0 | analógico externo V-REF/GND (modo S) — **é como a placa original comanda** |
| 0 | 1 | **P1-09** — o registrador que o nó escreve |
| 1 | 0 | P1-10 |
| 1 | 1 | P1-11 |

Sem SPD0 atribuído a alguma DI, o par nunca sai de `00`/`10` e P1-09 jamais é
selecionado, por mais correta que seja a escrita. É a diferença entre "o comando
não chegou" e "o drive não tinha como obedecer".

## 6. `P3-06` é bit a bit — o CN1 não é afetado

Manual, P3-06 (`030CH`):

> Bit0 ~ Bit8 correspond to DI1 ~ DI9.
> 0: The input status is controlled by the external hardware.
> 1: The input status is controlled by P4-07.

A tomada de controle é cirúrgica: só as DIs cujo bit está em 1 saem do CN1; todas
as demais continuam no hardware externo. E P3-06 é volátil — religar o drive
devolve tudo ao CN1 físico, o que é uma rede de segurança e não um efeito
colateral.

Consequência prática: a via Modbus não altera nada de permanente no drive. P2-30
e P3-06 são voláteis, e o nó deliberadamente **não escreve** `P2-1x`.

## 7. Pendência aberta — escritas Modbus com CRC inválido

Estado ao fim da sessão: perfil aceito, controle habilitado, e `applyMotorCommand()`
falhando nas escritas.

```text
[MODBUS] 06H em 040E: CRC invalido na resposta.   (83x em 40 s)
[MODBUS] 10H em 0112: CRC invalido na resposta.   (42x em 40 s)
```

Chegam exatamente 8 bytes e o CRC não fecha. Não é exceção Modbus e não é
ausência de resposta — o drive responde algo que não é quadro válido.

O que **está descartado**: o quadro de requisição `06H` tem os mesmos 8 bytes do
quadro de leitura `03H`, mesmo tempo de linha e mesmo chaveamento de DE/RE. Como
toda leitura `03H` passa (telemetria contínua, as nove DIs, P3-06, P2-30), a
camada física, o baud, o endereço de escravo e o tempo de direção estão provados
bons. Sobram enquadramento e conteúdo da resposta.

Suspeita principal, ainda não confirmada: em `writeSingleRegister()` e
`writeMultipleRegisters()` a limpeza do buffer de recepção acontece **antes** da
pausa de silêncio, não depois:

```cpp
while (rs485Serial.available() > 0) (void)rs485Serial.read();  // limpa
vTaskDelay(pdMS_TO_TICKS(RTU_SILENCE_MS));                     // e só então espera
digitalWrite(RS485_DE_RE_PIN, HIGH);                           // transmite
```

Qualquer byte que chegue atrasado dentro desses 12 ms fica no buffer e é lido
como o começo da próxima resposta, deslocando o quadro e quebrando o CRC. A
ordem correta é esperar o silêncio e limpar imediatamente antes de transmitir.

**Isto não é regressão desta sessão.** O caminho de escrita nunca havia sido
exercido neste drive: o perfil era recusado antes de chegar lá, e antes disso o
portão do Hub impedia até o comando de chegar. O defeito é latente e só ficou
alcançável depois que os itens 1, 2 e 4 foram corrigidos.

Instrumentação já gravada para a próxima medida: os dois helpers de escrita
despejam bytes enviados e recebidos, decodificam exceção Modbus e distinguem
ausência de resposta, quadro curto e CRC inválido. Falta apenas a leitura.

## Estado ao fim da sessão

```text
UART/CN1   funcionando, confirmado em bancada
Modbus     hub destravado, perfil aceito, escritas reprovando por CRC
Manual     P3-06 bit a bit, P3-06 volátil, P4-07 com leitura e escrita distintas
Hardware   nenhuma alteração de fiação ou de parâmetro foi necessária
```
