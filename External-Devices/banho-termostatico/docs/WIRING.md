# Guia de conexão — ESP32-S3 DevKit ↔ C404 ↔ HW-280

Guia fio a fio para a montagem definida em 2026-09-18 e completada em 2026-09-19:

- acionamento das quatro teclas do C404 por relés;
- leitura do display do C404;
- sensoriamento físico das teclas `▲` e `▼`;
- troca de modo por `▲` + `▼` mantidas pressionadas;
- LED externo para indicar o modo atual;
- fonte de 5 V dedicada para ESP32 e HW-280.

Pinos conforme `firmware/thermostatic-bath/src/config/BoardConfig.h` (r2/r3), com o LED de modo
adicionado ao **GPIO 40**. O firmware deve configurar `ModeLedPin = 40` para usar esse LED.

---

## 0. Antes de soldar qualquer fio de sinal

1. O C404 deve estar **desligado da tomada** durante soldagem, teste de continuidade e montagem.
2. O ESP32 deve ficar **sem USB** enquanto estiver eletricamente ligado ao GND do C404.
   Isso evita um possível laço de terra; ver `HARDWARE.md` §1.
   Grave o firmware por USB antes, com o ESP32 separado do C404; depois use OTA.
3. Monte primeiro os 13 divisores do display em uma placa perfurada.
4. Antes de ligar qualquer saída desses divisores ao ESP32, teste cada uma com o multímetro:
   **5 V na entrada devem resultar em aproximadamente 3,0 V na saída**.
5. Nunca use o modo de continuidade ou resistência do multímetro com o C404 energizado.

---

## 1. Alimentação

| De | Para | Observação |
|---|---|---|
| Fonte 5 V (+) | ESP32 `5V`/`VIN` | fonte dedicada ≥ 1 A; **não** usar `+5V`/`5VA` do C404 |
| Fonte 5 V (+) | HW-280 `DC+`/`VCC` | |
| Fonte 5 V (−) | ESP32 `GND` | |
| Fonte 5 V (−) | HW-280 `DC-`/`GND` | |
| ESP32 `GND` | C404 `GND` | **um único fio**, necessário para a leitura do display e das teclas |

Portanto, ESP32, HW-280 e o ponto `GND` usado para leitura do C404 compartilham a mesma
referência elétrica.

```text
Fonte 5 V (−)
      │
      ├──── ESP32 GND
      ├──── HW-280 GND
      └──── C404 GND   ← por um único fio entre ESP32 e C404
```

---

## 2. Relés — ESP32 → HW-280 → teclas do C404

Cada canal do HW-280 deve ficar com o jumper em **`CENTRAL–H`**, para que o relé seja acionado
quando o GPIO correspondente for colocado em nível HIGH (`BoardConfig::RelayActiveLow = false`).
O modo `L` foi abandonado em 2026-09-24: com o GPIO em 3,3 V e o módulo em 5 V, os LEDs ficavam
acesos fracos em repouso e o relé não soltava.

### 2.1 Entradas do HW-280

Mapa de 2026-09-25: **relé N no ponto `CHN`**.

| ESP32 GPIO | HW-280 | Relé | Ponto do C404 | Tecla do C404 |
|---|---|---|---|---|
| **4** | IN1 | 1 | `CH1` | `*` |
| **5** | IN2 | 2 | `CH2` | `ENTER` |
| **6** | IN3 | 3 | `CH3` | `▲` |
| **7** | IN4 | 4 | `CH4` | `▼` |

### 2.2 Contatos dos relés ligados às teclas

Use apenas `COM` e `NO` de cada relé. Deixe `NC` sem ligação.

Os contatos do relé ficam em paralelo com os botões físicos do C404: quando o relé fecha,
ele simplesmente faz a mesma conexão elétrica que o botão faz quando é pressionado.

| Relé | `COM` | `NO` |
|---|---|---|
| 1 (`*`) | +5 V (outro terminal da tecla `*`) | `CH1` |
| 2 (`ENTER`) | +5 V (outro terminal da tecla `ENTER`) | `CH2` |
| 3 (`▲`) | +5 V (comum `▲/▼`) | `CH3` |
| 4 (`▼`) | +5 V (comum `▲/▼`) — mesmo ponto usado pelo relé 3 | `CH4` |

Foi confirmado por teste de continuidade que `▲` e `▼` têm esta estrutura:

```text
terminal exclusivo de ▲ ── botão ▲ ──┐
                                     │
                                     ├──── terminal comum de ▲/▼
                                     │
terminal exclusivo de ▼ ── botão ▼ ──┘
```

Assim, a ligação dos relés 3 e 4 fica:

```text
terminal comum de ▲/▼ ───── COM relé 3
                      └──── COM relé 4

terminal exclusivo de ▲ ─── NO relé 3
terminal exclusivo de ▼ ─── NO relé 4
```

O mesmo fio do terminal comum pode ser bifurcado para os dois bornes `COM`.

Total: **7 fios únicos** entre o painel do C404 e os contatos dos quatro relés.
Os contatos do relé não têm polaridade.

### 2.3 Onde soldar: pontos `CH1…CH4`, nunca nos terminais das chaves

Confirmado em bancada (2026-09-24): os pontos `CH1`, `CH2`, `CH3` e `CH4` da placa do C404 são
as **linhas das quatro teclas** (CH = chave), no mesmo nó elétrico do terminal "quente" de cada
chave. Solde o fio `NO` de cada relé no `CH` correspondente, ou em uma via da mesma trilha, e
não nas pernas da chave tátil.

Motivo: o calor do ferro nos terminais deformou o mecanismo das chaves; depois de um toque
elas continuavam fechadas por dentro e o C404 fazia auto-repetição (SP descendo sem parar,
entrando em menu sozinho). Isso aconteceu com todos os fios já retirados, e só o toque na
tecla disparava o defeito.

Correspondência medida por continuidade (C404 fora da tomada), 2026-09-24:

| Ponto | Tecla | Outro terminal da tecla | Relé | `NO` do relé | `COM` do relé | GPIO do relé |
|---|---|---|---|---|---|---|
| `CH1` | `*` | +5 V do C404 | 1 | `CH1` | +5 V | 4 |
| `CH2` | `ENTER` | +5 V do C404 | 2 | `CH2` | +5 V | 5 |
| `CH3` | `▲` | +5 V (comum `▲/▼`) | 3 | `CH3` | +5 V | 6 |
| `CH4` | `▼` | +5 V (comum `▲/▼`) | 4 | `CH4` | +5 V | 7 |

Como funcionam as teclas: cada `CH` tem um pull-down no C404 (os ~10 kΩ medidos com a
tecla solta) e a tecla liga o `CH` ao +5 V. **Tecla solta = `CH` em ~0 V; apertada = 5 V.**
O relé repete isso: `COM` no lado +5 V da tecla, `NO` no `CH`.

Se o lado +5 V das quatro teclas for o mesmo nó (confirme com continuidade entre o terminal
+5 V do `*`, do `ENTER` e o comum `▲/▼`), basta **um** fio desse nó até o HW-280, jumpeado
nos quatro `COM`: são 5 fios ao painel em vez de 7. Se não for o mesmo nó, mantenha um fio
por tecla como na §2.2.

Este +5 V é o do próprio C404, mas passa só pelo contato do relé, igual ao botão: nada do
ESP32 ou da fonte dedicada é ligado a ele. Nunca ligue esse ponto ao `5V` do ESP32.

Nos `CH` só entram o contato do relé (aberto em repouso), os pull-downs de correção abaixo e,
se usado, o sensoriamento de alta impedância da §4. Não ligue divisores de display neles.

**Pull-downs de correção (2026-09-24; removidos em 2026-09-25, ficam só os originais do C404).** O C404 deste banho tem fuga do +5 V para os `CH`:
em repouso eles ficavam entre 1 e 3 V, e o `▼` era lido como apertado. Com **1 kΩ do `CH` ao
GND do C404** a linha volta a ~0 V. Critério: medindo entre os dois pads da tecla (`CH` ↔
+5 V), tecla solta deve dar **≥ 4 V**. Quando a tecla ou o relé fecha, o 1 kΩ consome ~5 mA do
+5 V do C404, o que é aceitável. O relé e o sensoriamento continuam funcionando igual.

---

## 3. Leitura do display — C404 → divisor → ESP32

> **Montagem adotada em 2026-09-26 (12:40):** segmentos `A…G`, `PD` do **pad do CN2 → 20 kΩ em
> série → GPIO 8…15, sem resistor para o GND**; `2DISP` → divisor 3,3 k/5,1 k → GPIO 38; `1A…1D`
> e `1L` **desligados** (o firmware conta as janelas a partir de `2DISP`, `disp_mode = 1`). O
> aviso abaixo, de ler do lado do PIC, fica como alternativa sem o risco dos picos de 5 V.
>
> **Atualização 2026-09-26: os pads `A…G`, `PD` e `1A…1D` do CN2 não servem.** Neles o sinal
> aceso fica em ~2 V (LED ligado direto ao pad). Os segmentos são lidos na ponta do resistor de
> 100 Ω do lado do PIC, e os dígitos no resistor de base de Q9–Q13 do lado do PIC, com divisor
> 3,3 kΩ + **6,8 kΩ**. `2DISP` continua no pad. Mapa da placa e medições em `HARDWARE.md` §3;
> passo a passo em `MONTAGEM_ETAPAS.md` §2.4. A tabela abaixo continua valendo para os GPIOs;
> muda só o ponto do C404.

> **Estado em 2026-09-24: seção suspensa.** A primeira montagem ligou os divisores de "dígito"
> em `CH1…CH4`, que são as teclas (§2.3), e o display nunca foi lido. As linhas de dígito do
> display ainda não foram identificadas. Não religue esta seção antes da tabela de medição
> da §3.2 estar preenchida. Preferir o CD74HC4050 aos divisores (HARDWARE.md §3).

O C404 trabalha com sinais de aproximadamente 5 V e os GPIOs do ESP32-S3 não devem receber
5 V diretamente. Por isso, cada linha do display passa por um divisor de tensão.

Cada linha usa este mesmo circuito:

```text
ponto do C404 ──[ 3,3 kΩ ]──●── GPIO do ESP32
                            │
                         [ 5,1 kΩ ]
                            │
                           GND
```

Com aproximadamente 5 V na entrada, o ponto ligado ao GPIO fica em aproximadamente **3,04 V**.
Os 26 resistores dos 13 divisores compartilham o mesmo GND do item 1.

| # | Ponto na placa do C404 | Função | Resistor série 3,3 k → | GPIO ESP32-S3 | Rótulo no DevKit |
|---|---|---|---|---|---|
| 1 | `A` | segmento A | → | **8** | `8` |
| 2 | `B` | segmento B | → | **9** | `9` |
| 3 | `C` | segmento C | → | **10** | `10` |
| 4 | `D` | segmento D | → | **11** | `11` |
| 5 | `E` | segmento E | → | **12** | `12` |
| 6 | `F` | segmento F | → | **13** | `13` |
| 7 | `G` | segmento G | → | **14** | `14` |
| 8 | `PD` | ponto decimal | → | **15** | `15` |
| 9 | `1A` | dígito 1 (hipótese 2026-09-25) | → | **16** | `16` |
| 10 | `1B` | dígito 2 | → | **17** | `17` |
| 11 | `1C` | dígito 3 | → | **18** | `18` |
| 12 | `1D` | dígito 4 | → | **21** | `21` |
| 13 | `2DISP` | seleção display superior/inferior (hipótese) | → | **38** | `38` |
| 14 | `1L` | varredura dos LEDs (só captura, não decodificado) | → | **39** | `39` |
| — | `GND` | referência | fio direto | `GND` | `GND` |

Montagem adotada em 2026-09-25 (sem CD74HC4050 disponível): **divisor 3,3 kΩ série + 5,1 kΩ
para GND em cada uma das 14 linhas**, como no esquema acima. Regras que passam a valer:
ESP32 ligado **antes** do C404 e desligado **depois** (com o ESP32 sem alimentação os diodos de
proteção prendem as entradas perto de 0,6 V e deformam o display); ligar uma linha por vez no
início e conferir o painel e os `CH` a cada grupo (MONTAGEM_ETAPAS.md §2.3). Se aparecer um
CD74HC4050, ele substitui os divisores sem mudar os GPIOs.

Não ligar ao ESP32: `CH1`, `CH2`, `CH3`, `CH4` (teclas, §2.3), `+5V` e `5VA`.

Os GPIOs 16, 17, 18 e 21 continuam reservados no firmware para as quatro linhas de dígito;
muda só o ponto do C404 ligado a eles. Hipótese de trabalho para os pontos restantes:
`1A, 1B, 1C, 1D` = dígitos do display 1 (A–D), `1L` = LEDs de sinalização, `2DISP` = seleção
ou habilitação do display 2. Nenhuma delas foi medida.

### 3.2 Medição obrigatória antes de religar

C404 ligado, ESP32 **sem nenhum fio ao C404**, multímetro em tensão DC com a ponta preta no GND
do C404 (nunca continuidade com o C404 energizado):

| Ponto | DC parado | Reage a alguma tecla? | Frequência (Hz) | Conclusão |
|---|---|---|---|---|
| `1A` | | | | |
| `1B` | | | | |
| `1C` | | | | |
| `1D` | | | | |
| `1L` | | | | |
| `2DISP` | | | | |
| `A` | | | | |
| `PD` | | | | |

Linha de dígito ou segmento: média intermediária (ex.: 1–4 V), frequência de centenas de Hz
a alguns kHz, não reage a teclas. Linha de tecla: ~5 V parada, cai a ~0 V com uma tecla.
Se algum segmento reagir a tecla, o teclado é varrido pelas linhas do display e qualquer carga
nelas volta a gerar toques fantasmas: nesse caso só o CD74HC4050 serve.

Regra permanente: **nunca energize o C404 com os divisores ligados e o ESP32 desligado.**
O ESP32 sem alimentação prende cada entrada perto de 0,6 V pelos diodos de proteção e deforma
o display (segmentos errados, "Y" no visor superior).

### 3.1 Ordem assumida dos segmentos

```text
     A
   ┌───┐
 F │ G │ B
   ├───┤
 E │   │ C
   └───┘ · PD
     D
```

Se o painel real associar as letras aos segmentos de outra forma, `/display` poderá mostrar `?`
em alguns dígitos. Nesse caso, basta reordenar `SegmentPins` em `BoardConfig.h`; não é necessário
alterar a fiação dos divisores.

---

## 4. Sensoriamento físico de `▲` e `▼`

Esta parte permite que o ESP32 perceba quando alguém pressiona fisicamente `▲` ou `▼` no painel.
Ela serve para:

- detectar toques manuais;
- confirmar os toques realizados pelos próprios relés;
- detectar o gesto `▲` + `▼` mantidas pressionadas por `mode_hold_ms`;
- trocar o modo uma vez por pressionamento prolongado, exigindo soltar as teclas antes de repetir.

### 4.1 De onde vem o sinal

Não é necessário adicionar outro fio até a placa das teclas do C404.

Os fios dos terminais exclusivos de `▲` e `▼` já chegam aos bornes `NO` dos relés 3 e 4:

```text
terminal exclusivo de ▲ ───── NO do relé 3
terminal exclusivo de ▼ ───── NO do relé 4
```

O sensoriamento é retirado desses mesmos bornes `NO`.

Em outras palavras: em cada `NO` haverá o fio da tecla **e** o resistor de 100 kΩ usado pelo
sensoriamento.

```text
NO relé 3
   ├── fio que já vai ao terminal exclusivo de ▲
   └── resistor de 100 kΩ → circuito de leitura do GPIO 2

NO relé 4
   ├── fio que já vai ao terminal exclusivo de ▼
   └── resistor de 100 kΩ → circuito de leitura do GPIO 42
```

O terminal comum de `▲/▼`, ligado aos `COM` dos relés, **não** vai diretamente aos GPIOs de
sensoriamento.

### 4.2 Divisor usado em cada tecla

O terminal exclusivo da tecla é o próprio `CH` (§2.3): ~0 V com a tecla solta e **5 V com
ela apertada** (pelo dedo ou pelo relé). O ESP32 não deve receber 5 V diretamente. Por isso
cada tecla usa este divisor, e o firmware lê a tecla como **ativa em HIGH**
(`BoardConfig::SenseActiveHigh`):

```text
borne NO do relé ──[ 100 kΩ ]──●── GPIO do ESP32
                               │
                            [ 150 kΩ ]
                               │
                              GND
```

Com 5 V no borne `NO`, o GPIO recebe aproximadamente **3,0 V**.
A corrente é de aproximadamente **20 µA**, suficientemente pequena para não carregar de forma
significativa o circuito da tecla do C404.

Ligação completa:

```text
                         ▲

terminal exclusivo ▲ ───┬──── NO relé 3
                        │
                      100 kΩ
                        │
                        ●──── GPIO 2
                        │
                      150 kΩ
                        │
                       GND


                         ▼

terminal exclusivo ▼ ───┬──── NO relé 4
                        │
                      100 kΩ
                        │
                        ●──── GPIO 42
                        │
                      150 kΩ
                        │
                       GND
```

| Tecla | Ponto usado no HW-280 | GPIO ESP32-S3 | Rótulo no DevKit |
|---|---|---|---|
| `▲` | relé 3, `NO` | **2** | `2` |
| `▼` | relé 4, `NO` | **42** | `42` |

### 4.3 Configuração dos GPIOs

GPIO 2 e GPIO 42 devem ser configurados como `INPUT` **sem pull-up interno**.

Não habilite `INPUT_PULLUP` nesses dois pinos. O pull-up interno de aproximadamente 45 kΩ do
ESP32 poderia elevar o nível da tecla pressionada para aproximadamente 1,9 V, impedindo que ela
fosse reconhecida de forma confiável como LOW.

Esses dois GPIOs não são usados pelos relés, pelo display, pela USB nem pela flash/PSRAM nesta
montagem:

- relés: GPIO 4–7;
- display: GPIO 8–18, 21 e 38;
- USB nativo: GPIO 19/20;
- flash/PSRAM do módulo: GPIO 26–37.

Os pinos de strapping são 0, 3, 45 e 46; portanto GPIO 2 e GPIO 42 não são pinos de strapping.
O GPIO 42 corresponde a `MTMS` para JTAG por pinos, mas, como entrada comum nesta montagem, não
há conflito enquanto esse JTAG não for reativado pela configuração correspondente.

### 4.4 `sense_mask`

Nesta montagem somente `▲` e `▼` são sensoriados:

```text
sense_enabled = 1
sense_mask    = 6
```

Os GPIOs 1 e 47 permanecem reservados, mas sem fio, para possível sensoriamento futuro de `*`
e `ENTER`.

Se essas duas teclas forem adicionadas no futuro, use o mesmo circuito de 100 kΩ + 150 kΩ nos
bornes `NO` dos relés 1 e 4 e altere:

```text
sense_mask = 15
```

### 4.5 Como conferir sem osciloscópio

Não é necessário ter osciloscópio para a primeira validação.
Faça os testes abaixo **antes de fechar o equipamento**, enquanto os pontos ainda estiverem
acessíveis.

#### Etapa A — verificar os bornes `NO`

Com o C404 ligado, ESP32 desligado e multímetro em tensão contínua:

```text
ponta preta    → GND do C404
ponta vermelha → NO do relé testado
```

Resultado esperado:

| Medição | Tecla solta | Tecla pressionada |
|---|---:|---:|
| `NO` relé 3 (`▲`, `CH3`) | ~0–0,8 V (pull-down) | ~5 V |
| `NO` relé 4 (`▼`, `CH4`) | ~0–0,8 V (pull-down) | ~5 V |
| `COM` de `▲/▼` (+5 V) | ~5 V | ~5 V |

(Corrigido em 2026-09-25: as teclas do C404 são ativas em HIGH; o `CH` fica em ~0 V solto e vai a
5 V apertado. O firmware lê assim, `BoardConfig::SenseActiveHigh = true`.) Se o `NO` ficar em
~5 V com a tecla solta, pare: ou o sensoriamento foi ligado ao `COM` (+5 V), que é comum às duas
setas e as tornaria indistinguíveis, ou o `CH` tem fuga (ver `CURRENT_STATUS.md`).

Se o multímetro mostrar um valor claramente instável ou intermediário em vez de aproximadamente
5 V estáveis com a tecla solta, o C404 pode estar verificando as teclas por pulsos. Um multímetro
não mostra a forma desses pulsos, mas isso pode ser investigado depois com o próprio ESP32.
**Não é necessário comprar um osciloscópio apenas para prosseguir com a montagem.**

O filtro atual do firmware exige aproximadamente 30 ms / 6 amostras seguidas. Se o sinal for
muito pulsado, esse filtro pode não reconhecer a tecla e terá de ser ajustado depois da
caracterização pelo ESP32.

#### Etapa B — verificar a saída do divisor antes de ligar o GPIO

Monte os dois divisores de 100 kΩ + 150 kΩ e conecte-os aos bornes `NO`, mas ainda deixe os
GPIOs 2 e 42 desconectados.

Meça no ponto entre os dois resistores:

```text
NO ── 100 kΩ ──●── futuro GPIO
               │
             150 kΩ
               │
              GND

medir aqui ────●
```

Com a tecla solta, esse ponto deve ficar entre **2,5 V e 3,6 V**.
Com a tecla pressionada, deve ficar abaixo de **0,5 V**.

Além disso, o próprio borne `NO` deve permanecer em pelo menos aproximadamente **4 V** quando a
tecla estiver solta, para garantir que o divisor não esteja carregando demais o circuito do C404.

Se a saída do divisor ficar abaixo de 2,5 V com a tecla solta, o circuito interno do C404 pode
ser fraco demais para o divisor de 100 kΩ / 150 kΩ. Nesse caso, substitua o resistor de 150 kΩ
por **220 kΩ ou 330 kΩ** e repita as medições antes de ligar ao ESP32.

Nunca conecte ao GPIO se a saída medida ultrapassar **3,6 V**.

#### Etapa C — ligar ao ESP32 e validar pelo software

Depois que as tensões estiverem dentro dos limites acima:

- ponto do divisor de `▲` → GPIO **2**;
- ponto do divisor de `▼` → GPIO **42**;
- `sense_enabled = 1`;
- `sense_mask = 6`.

No `/status`:

- pressionar fisicamente `▲` deve incrementar `manual_presses`;
- manter `▲` + `▼` pressionadas deve fazer `arrows_held_ms` crescer;
- ao ultrapassar `mode_hold_ms`, `mode` deve alternar uma única vez;
- é necessário soltar as duas teclas antes de uma nova troca de modo;
- um comando `{"key":"up"}` enviado pelo app não deve contar como toque manual;
- após um toque comandado pelo app, `presses_unconfirmed` deve permanecer em `0` quando a
  confirmação estiver funcionando corretamente.

Se o multímetro tiver mostrado comportamento estranho na Etapa A, o próprio ESP32 pode ser
usado temporariamente para contar quantas leituras HIGH e LOW aparecem em cada GPIO. Isso permite
identificar um sinal pulsado sem osciloscópio antes de ativar a lógica normal de detecção.

---

## 5. LED externo de indicação de modo

O LED mostra visualmente em qual modo o sistema está sem precisar consultar o app.

Use o **GPIO 40**.

### 5.1 Ligação

```text
GPIO 40 ──[ 330 Ω ]───|>|─── GND
                       LED
```

Ligação física usual de um LED comum:

- perna mais longa: lado que vai ao resistor de 330 Ω e ao GPIO 40;
- perna mais curta: lado que vai ao GND.

O resistor pode ser montado antes ou depois do LED; eletricamente o efeito é o mesmo. Nesta
documentação ele é mostrado entre o GPIO e o LED.

Use o mesmo GND do ESP32:

```text
GPIO 40
   │
 [330 Ω]
   │
   LED
   │
   └──── ESP32 GND
```

Esse LED não precisa de nenhum fio adicional para o C404 nem para os contatos do HW-280.

### 5.2 Comportamento adotado

```text
LED apagado → modo MANUAL
LED aceso   → modo AUTO
```

Quando `▲` + `▼` forem mantidas pressionadas por `mode_hold_ms` e o modo for alterado, o estado
do LED deve ser atualizado imediatamente.

O LED é uma indicação contínua do estado atual; não é necessário usar os relés de `▲` e `▼`
para produzir uma confirmação visual ou sonora.

### 5.3 Firmware

Configurar o pino de indicação de modo para:

```text
ModeLedPin = 40
```

O GPIO 40 deve ser configurado como saída.

---

## 6. Pinos do DevKit — usados, reservados, livres e proibidos

### Em uso

- GPIO **2** — sensoriamento `▲`;
- GPIO **4** — relé `*`;
- GPIO **5** — relé `▲`;
- GPIO **6** — relé `▼`;
- GPIO **7** — relé `ENTER`;
- GPIO **8–18**, **21** e **38** — leitura do display;
- GPIO **40** — LED externo de indicação de modo;
- GPIO **42** — sensoriamento `▼`.

### Reservados no firmware, mas sem fio nesta montagem

- GPIO **1** — sensoriamento futuro de `*`;
- GPIO **47** — sensoriamento futuro de `ENTER`.

### Livres para uso futuro

- GPIO **39**;
- GPIO **41**;
- GPIO **48** — pode estar ligado ao LED RGB em alguns modelos de DevKit, portanto verificar a
  placa antes de usar.

### Não usar nesta montagem

- GPIO **0, 3, 45 e 46** — pinos de strapping, podem alterar o comportamento de boot;
- GPIO **19 e 20** — USB nativo;
- GPIO **26–37** — flash/PSRAM no módulo WROOM.

---

## 7. Conferência final antes de energizar tudo

Faça nesta ordem.

1. **C404 desligado:** multímetro em continuidade.
   - nenhum curto entre `5V` e `GND`;
   - nenhum curto acidental entre GPIOs vizinhos;
   - confirme novamente que `▲` e `▼` só fecham contato quando pressionadas.

2. **C404 ligado, ESP32 desligado:** confirme as tensões dos divisores.
   - divisores do display: saída entre aproximadamente 0 e 3 V, nunca 5 V;
   - divisores de `▲/▼`: tecla solta entre 2,5 e 3,6 V; pressionada abaixo de 0,5 V;
   - se qualquer saída destinada a um GPIO ultrapassar 3,6 V, não conecte o ESP32.

3. **Ligue o ESP32.**
   Abra:

   ```text
   http://192.168.8.1/display
   ```

   Verifique:
   - `alive:true`;
   - `frames` crescendo;
   - `text` reproduzindo o painel.

   Ajuste pelo app, se necessário:
   - `disp_seg_low`;
   - `disp_dig_low`;
   - `disp_seg_lead`.

   Esses ajustes devem permitir acertar a leitura sem recompilar.

4. **Teste o sensoriamento `▲/▼`.**
   - `sense_enabled = 1`;
   - `sense_mask = 6`;
   - validar `manual_presses`;
   - validar `arrows_held_ms`;
   - validar troca de `mode` após `mode_hold_ms`;
   - validar que o LED no GPIO 40 acompanha o modo.

5. Depois, executar G7/G7b de `VALIDATION.md` para o gesto das duas setas.

6. Só depois habilitar `sp_source = 1` e executar os testes G2–G3 de `VALIDATION.md`.

---

## 8. Resumo rápido das conexões adicionadas para `▲`, `▼` e LED

```text
                         SENSO UP

NO relé 3 ──[100 kΩ]──●──── GPIO 2
                      │
                   [150 kΩ]
                      │
                     GND


                        SENSO DOWN

NO relé 4 ──[100 kΩ]──●──── GPIO 42
                      │
                   [150 kΩ]
                      │
                     GND


                        LED DE MODO

GPIO 40 ──[330 Ω]── LED ── GND

LED apagado = MANUAL
LED aceso   = AUTO
```
