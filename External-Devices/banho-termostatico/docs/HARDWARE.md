# Hardware — Banho termostático (C404 + ESP32-S3)

Complementa `../plano_controle_remoto_C404_ESP32S3.md` (relés, 7 fios das teclas, jumpers
`L` do HW-280) com o que o firmware r1 precisa além dos relés: alimentação, leitura do
display e sensoriamento das teclas.

## 1. Alimentação — o ponto `5VA` do C404

O manual do C404 (Contemp, v1.00 rev.4, §4.5) informa **consumo total de 5 VA** para o
instrumento inteiro. Isso é a potência aparente que o controlador tira da rede; não é uma
tensão. Na placa, um ponto marcado `5VA` ligado ao `+5V` é, com quase certeza, o **trilho
analógico de 5 V** (`VA` = *V analógico*): o mesmo 5 V passando por um filtro RC/LC para
alimentar o front-end do ADC de 16 bits que lê o PT100/termopar.

Consequências:

- **Não alimentar o ESP32-S3 nem o HW-280 por `5VA`.** Os picos de Wi-Fi do ESP32-S3
  (300–500 mA) e a bobina do JQC3F-05VDC (~70 mA) passariam pelo filtro do trilho analógico e
  apareceriam como ruído/offset na leitura de temperatura do banho.
- A fonte interna (flyback TNY264PN) é dimensionada para os 5 VA do instrumento, cuja maior
  carga é o próprio display de LED. A folga para um ESP32-S3 com Wi-Fi é, na melhor hipótese,
  marginal. **Use uma fonte 5 V dedicada (≥ 1 A)** para ESP32 + HW-280. O procedimento de
  medição do plano (item "Alimentação") continua válido se quiser confirmar.
- **GND comum:** só é necessário se o ESP32 for ler o display ou as teclas (sinais
  referenciados ao GND lógico do C404). Nesse caso ligue `GND` da fonte dedicada ao `GND` do
  C404 em um único ponto. Os contatos `COM/NO` dos relés são flutuantes e não precisam de GND
  comum.
- **Laço de terra pela USB:** com o GND do ESP32 preso ao GND do C404, ligar a USB a um
  notebook aterrado cria um caminho de retorno pelo PC até o circuito do sensor. Grave por USB
  **sempre com o ESP32 desconectado do C404** (é o cenário do primeiro upload) e depois só por
  OTA. Se precisar de serial com tudo montado, use um isolador USB ou um notebook na bateria.

## 2. Mapa de pinos (BoardConfig.h, r1)

| Função | GPIO | Sentido | Observação |
|---|---|---|---|
| Relé `*` (IN1) | 4 | saída, ativo HIGH | HW-280 jumper `H` (desde 2026-09-24) |
| Relé `ENTER` (IN2) | 5 | saída, ativo HIGH | relé N → `CHN` (mapa 2026-09-25) |
| Relé `▲` (IN3) | 6 | saída, ativo HIGH | |
| Relé `▼` (IN4) | 7 | saída, ativo HIGH | |
| Segmentos `A B C D E F G PD` | 8 9 10 11 12 13 14 15 | entrada | via divisor, ver §3 |
| Dígitos 1–4 (pontos do C404 a identificar; **não** `CH1…CH4`) | 16 17 18 21 | entrada, interrupção | via divisor |
| `2DISP` (banco superior/inferior, a confirmar) | 38 | entrada, interrupção | via divisor |
| Sense `*` `▲` `▼` `ENTER` | 1 2 42 47 | entrada, pull-up interno | opcional, ver §4 |

Evitados de propósito: 0, 3, 45, 46 (strapping), 19/20 (USB nativo), 26–37 (flash/PSRAM).
Os relés partem abertos (GPIO em LOW) antes de qualquer outra inicialização; durante o reset
os GPIOs ficam em alta impedância e, com os jumpers em `H`, o `IN` é puxado para 0 V e o relé
fica desligado. O modo `L` não serve com o ESP32: o GPIO em 3,3 V contra o VCC de 5 V do módulo
deixa o optoacoplador conduzindo parcialmente (LEDs acesos fracos, relé sem soltar).

## 3. Leitura do display

### Placa CPU do C404 e origem dos sinais (fotos de 2026-09-26)

O C404 deste banho é montado em duas placas: **`C504 - CPU`** (`034.00206 REV.00`, 09/06/10) e
**`C504 - DISPLAY`** (`034.00207 REV.00`, 01/06/10), ligadas pelo conector **CN2**.

| Componente (placa CPU) | Função |
|---|---|
| **PIC16F76-I/SS** (28 pinos SSOP) | Microcontrolador: lê o sensor, faz o PID, varre display e teclas |
| Cristal 20,000 MHz | Relógio do PIC |
| 24LC16B | EEPROM I²C: guarda SP e parâmetros |
| LM324A | Amplificador do sensor |
| HEF4051BT | Multiplexador analógico (entradas) |
| Q9–Q13 | 5 transistores: acionam `1A`, `1B`, `1C`, `1D`, `1L` (4 dígitos + LEDs) |
| R46, R53, R50, R48, R47, R52, R51, R49 (`101` = 100 Ω) | Em série em cada segmento: pino do PIC → 100 Ω → pad do CN2 |
| R57, R54, R55, R56 (`152` = 1,5 kΩ) | Em série nas linhas das teclas `CH` |
| R58, R59, R60 (`103` = 10 kΩ) | Pull-downs das teclas |

Ordem do CN2 pela serigrafia: `CH4 CH1 CH2 CH3 PD A D F G B C E 1C 1B 1D 1L 1A 2DISP`
(+5 V e 0 V nas pontas). A placa do display tem só LEDs, resistores e transistores (Q1–Q10),
estes comandados pelo `2DISP` para escolher o display de cima ou o de baixo.

Resistor provável de cada segmento (a confirmar por continuidade pad ↔ resistor):
`PD` R46, `A` R53, `D` R50, `F` R48, `G` R47, `B` R52, `C` R51, `E` R49.

**Por que os pads do CN2 não servem para leitura digital.** Osciloscópio pelo ADC
(`/scope`, 2026-09-26, GPIO 8/9/10 = `A`/`B`/`C`): no pad, o segmento aceso fica em apenas
**1,5–2,4 V** (depois do resistor de 100 Ω o pad está ligado direto ao LED, que limita a tensão à
sua queda direta; os dois grupos de níveis devem ser as duas cores de LED), apagado em 0 V, com
picos raros de ~5 V nos intervalos apagados. Janela de cada dígito **~1 ms**, intervalo apagado
de ~57 µs entre janelas, 5 janelas por display e 2 displays: ~9,2 ms por varredura, os 108 Hz do
`2DISP`. Pelo divisor, 2 V viram ~1,2 V no GPIO, abaixo do limiar: daí a captura digital ver só
LOW. `1A…1D` são coletores de Q9–Q13 e ficam em níveis intermediários pelo mesmo motivo.
`2DISP` é lógico no pad (0/5 V).

**Onde ler.** No **lado do PIC**:
- segmentos: na ponta do resistor de 100 Ω oposta ao pad (0/5 V lógico);
- dígitos: na ponta do resistor de base de Q9–Q13 ligada ao PIC (0/5 V lógico; a polaridade
  depende de o transistor ser NPN ou PNP, `disp_dig_low` ajusta);
- `2DISP`: no próprio pad.

Divisor para o lado do PIC: **3,3 kΩ série + 6,8 kΩ para GND** (5 V → 3,37 V; ~4 V com o pino
fornecendo corrente ao LED → 2,7 V). O 5,1 kΩ para GND daria só 2,4 V nos 4 V.

### Mapa dos pontos da placa do C404 (atualizado 2026-09-24)

| Pontos | Função | Situação |
|---|---|---|
| `CH1 CH2 CH3 CH4` | **Linhas das quatro teclas** (CH = chave) | Confirmado em bancada. Ponto de solda dos relés (WIRING.md §2.3); nunca carga de display |
| `A B C D E F G PD` | Segmentos do display (7 segmentos + ponto) | Hipótese pela serigrafia; medir (WIRING.md §3.2) |
| `1A 1B 1C 1D` | Candidatos a dígitos 1–4 do display | Hipótese; medir |
| `1L` | Candidato a LEDs de sinalização | Hipótese; não necessário para o SP |
| `2DISP` | Candidato a seleção/habilitação do display 2 | Hipótese; medir |
| `+5V`, `5VA` | Alimentação interna (5VA = trilho analógico) | Nunca usar (§1) |

O firmware assume um display multiplexado: oito linhas de segmento compartilhadas, quatro
linhas de dígito e uma linha de banco (`2DISP`) que escolhe entre o display superior (PV) e o
inferior (SP); índice do dígito = linha + 4 × banco, 0–3 para PV e 4–7 para SP
(`BoardConfig::PvDigits/SpDigits`). A primeira montagem ligou as quatro linhas de dígito em
`CH1…CH4`: o display nunca foi lido (`alive:false`). Como as teclas são ativas em HIGH com
pull-down, essa carga para GND não simulava toque; os toques fantasmas vieram das chaves
táteis danificadas pelo calor da soldagem. Se `1A…1D` forem dígitos de um display só, o segundo display terá outras linhas e
o mapa de bancos muda; a tabela de medição decide.

Fiação: 8 + 4 + 1 = **13 fios + GND**. Cada linha passa por um **divisor resistivo**
(**3,3 kΩ em série + 5,1 kΩ para GND**, 5 V → 3,04 V) porque os GPIOs do ESP32-S3 **não toleram 5 V**
(V_IH máx. = VDD + 0,3 V = 3,6 V; o `5V` do DevKit passa por um LDO e o chip roda a 3,3 V). O que
fixa a tensão é a proporção R_baixo/(R_série + R_baixo) ≈ 0,6; 6,8 k/10 k ou 6,6 k/10,2 k dão o mesmo
resultado. Combinações a evitar: 5,1 k/5,1 k (2,5 V, no limiar de HIGH) e 3,3 k/10 k (3,76 V, acima
do limite). A carga de 8,4 kΩ só é aceitável em linhas acionadas com força; em linha de tecla ela
simula tecla apertada. Com o ESP32 desligado, os diodos de proteção prendem cada entrada perto de
0,6 V e deformam o display. Por isso a opção **recomendada** passa a ser o **CD74HC4050** alimentado
em 3,3 V: entradas de alta impedância e sem diodo para o VCC, seguras com 5 V mesmo com a placa
desligada. O 74LVC245 também serve (entradas 5 V-tolerantes).

Antes de ligar, meça em cada ponto, com o C404 ligado e referenciado ao GND dele:

1. tensão em nível alto (esperado 5 V lógico; se for tensão de LED pós-driver, o ponto é o
   errado — procure o lado do microcontrolador);
2. polaridade: segmento aceso é LOW ou HIGH? dígito ativo é LOW ou HIGH? → `disp_seg_low`,
   `disp_dig_low`;
3. frequência de varredura (tipicamente 100 Hz a 1 kHz por dígito) e se os segmentos mudam
   **antes** ou **depois** da troca do dígito → `disp_seg_lead`.

Com isso, `GET /display` deve mostrar em `text` os mesmos oito caracteres do painel (ex.:
`" 25.3 30.0"`). Só então troque `sp_source` para `1`.

## 4. Sensoriamento das teclas (montado para `▲`/`▼` em 2026-09-19; `*`/`ENTER` opcionais)

Mapa medido (2026-09-24): `CH1 = *`, `CH2 = ENTER`, `CH3 = ▲`, `CH4 = ▼`. O outro terminal
das quatro teclas é o +5 V do C404 (comum às setas) e cada `CH` tem pull-down (~10 kΩ).
**As teclas são ativas em HIGH**: `CH` em ~0 V solta, 5 V apertada. Para ler, leve o `CH`
de cada tecla a um GPIO por **divisor de alta impedância** (100 kΩ série + 150 kΩ para GND,
5 V → 3 V); o firmware lê HIGH como tecla fechada (`BoardConfig::SenseActiveHigh`). O lado quente não exige fio novo ao C404: está no
borne `NO` do relé correspondente do HW-280 (WIRING.md §3b, com a conferência de polaridade e
de nível). O GPIO fica em `INPUT` sem pull-up interno, senão o "pressionada" sobe a ~1,9 V.
Se o teclado for varrido em matriz (linhas pulsadas), o sinal será intermitente e o filtro de
30 ms do firmware (6 amostras seguidas) **não** verá a tecla; a caracterização com
osciloscópio decide antes de ligar.

`sense_mask` diz quais linhas existem (bit0 `*`, bit1 `▲`, bit2 `▼`, bit3 `ENTER`; padrão 6 =
só as setas): linhas sem fio não são lidas, para não virarem toques fantasmas.

O que o sensoriamento resolve e o que não resolve:

- resolve: saber **que** alguém mexeu (marca o setpoint-sombra como desconhecido), o gesto
  `▲`+`▼` de troca de modo (PROTOCOL.md §3.2) e confirmar que cada toque do relé fechou a
  linha (`presses_unconfirmed` em `/status`);
- não resolve: saber **quanto** mudou. Tecla mantida gera auto-repetição com aceleração no
  C404, e `*`/`ENTER` navegam por menus onde `▲`/`▼` não mexem no SP. Contar toques manuais
  não reconstrói o setpoint; por isso a fonte definitiva é o display (§3).

## 5. Resumo de conexões extras para os firmwares r2/r3

| Objetivo | Fios ao C404 | Componentes |
|---|---|---|
| Só relés (modo sombra, sem confirmação) | 7 (plano original) | HW-280 |
| + leitura do display (modo display) | + 13 sinais + 1 GND | 13 divisores 3,3k/5,1k (26 resistores; ou 4050/LVC245) |
| + sensoriamento de `▲`/`▼` | 0 ao C404 (bornes `NO` dos relés 3 e 4) | 2 divisores 100k/150k — **montado 2026-09-19**: gesto de modo e toque manual |
| + sensoriamento de `*`/`ENTER` | 0 ao C404 (bornes `NO` dos relés 1 e 4) | 2 divisores — opcional, `sense_mask = 15` |
| Alimentação | nenhum ao `5VA`/`+5V` | fonte 5 V dedicada ≥ 1 A, GND comum em um ponto |
