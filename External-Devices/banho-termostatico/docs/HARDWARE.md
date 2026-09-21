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
| Relé `*` (IN1) | 4 | saída, ativo LOW | HW-280 jumper `L` |
| Relé `▲` (IN2) | 5 | saída, ativo LOW | |
| Relé `▼` (IN3) | 6 | saída, ativo LOW | |
| Relé `ENTER` (IN4) | 7 | saída, ativo LOW | |
| Segmentos `A B C D E F G PD` | 8 9 10 11 12 13 14 15 | entrada | via divisor, ver §3 |
| Dígitos `CH1 CH2 CH3 CH4` | 16 17 18 21 | entrada, interrupção | via divisor |
| `2DISP` (banco superior/inferior) | 38 | entrada, interrupção | via divisor |
| Sense `*` `▲` `▼` `ENTER` | 1 2 42 47 | entrada, pull-up interno | opcional, ver §4 |

Evitados de propósito: 0, 3, 45, 46 (strapping), 19/20 (USB nativo), 26–37 (flash/PSRAM).
Os relés partem em HIGH (abertos) antes de qualquer outra inicialização; durante o reset os
GPIOs ficam em alta impedância e o optoacoplador do HW-280 mantém o relé desligado.

## 3. Leitura do display (pontos `2DISP, A…G, PD, CH1…CH4`)

Os pontos que você identificou na placa correspondem a um display de 7 segmentos
multiplexado: oito linhas de segmento (`A`–`G` + ponto decimal `PD`) compartilhadas por todos
os dígitos, quatro linhas de dígito (`CH1`–`CH4`) e uma linha `2DISP` que escolhe entre o
display superior (PV) e o inferior (SP). O firmware assume exatamente isso: índice do dígito
= `CH` + 4 × banco(`2DISP`), com os índices 0–3 mapeados para PV e 4–7 para SP
(`BoardConfig::PvDigits/SpDigits`). Se a bancada mostrar outra ordem, só a tabela muda.

Pontos `1A, 1L, 1D, 1B, 1C`: não batem com segmentos nem com dígitos. As hipóteses são os
LEDs de sinalização do painel (`A1, A2, SP, PG, AT, C1` no manual) ou linhas do teclado.
Identifique com o osciloscópio antes de soldar; nenhum deles é necessário para o SP.

Fiação: 8 + 4 + 1 = **13 fios + GND**. Cada linha passa por um **divisor resistivo**
(**3,3 kΩ em série + 5,1 kΩ para GND**, 5 V → 3,04 V) porque os GPIOs do ESP32-S3 **não toleram 5 V**
(V_IH máx. = VDD + 0,3 V = 3,6 V; o `5V` do DevKit passa por um LDO e o chip roda a 3,3 V). O que
fixa a tensão é a proporção R_baixo/(R_série + R_baixo) ≈ 0,6; 6,8 k/10 k ou 6,6 k/10,2 k dão o mesmo
resultado. Combinações a evitar: 5,1 k/5,1 k (2,5 V, no limiar de HIGH) e 3,3 k/10 k (3,76 V, acima
do limite). As linhas do display são acionadas com força (driver de LED), então 8,4 kΩ de carga
(0,6 mA) não as perturbam — confirme na bancada que o brilho não muda com os 13 divisores ligados. Alternativa mais limpa: um CD74HC4050 ou 74LVC245 alimentado em 3,3 V.

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

Cada tecla tem um lado "quente" (pull-up no microcontrolador do C404) e um lado comum. O
teste de continuidade mostrou que `▲` e `▼` compartilham o comum; `*` e `ENTER` têm pares
próprios. Para ler, leve o lado quente de cada tecla a um GPIO por **divisor de alta
impedância** (100 kΩ série + 150 kΩ para GND) — impedância alta para não carregar o pull-up
do C404 e não "apertar" a tecla sozinho. O lado quente não exige fio novo ao C404: está no
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
| + sensoriamento de `▲`/`▼` | 0 ao C404 (bornes `NO` dos relés 2 e 3) | 2 divisores 100k/150k — **montado 2026-09-19**: gesto de modo e toque manual |
| + sensoriamento de `*`/`ENTER` | 0 ao C404 (bornes `NO` dos relés 1 e 4) | 2 divisores — opcional, `sense_mask = 15` |
| Alimentação | nenhum ao `5VA`/`+5V` | fonte 5 V dedicada ≥ 1 A, GND comum em um ponto |
