# Montagem por etapas — Banho termostático

Remontagem a partir de 2026-09-24, depois do incidente registrado em `CURRENT_STATUS.md`.
Uma etapa só começa quando a anterior passou em todos os testes.

| Etapa | Objetivo | Ligações ao C404 | Firmware |
|---|---|---|---|
| **1** ✔ | Controlar as teclas | 5 fios (relés); **sem GND comum** | padrões: `sp_source=0`, `sense_enabled=0`, `mode=manual`, `hub_enabled=0` |
| **2** ✔ | Ler o display | + `A…G`, `PD` por 20 kΩ série, `2DISP` por divisor + GND (§2.5) | `disp_mode=1`, `sp_source=1` |
| **3** ✔ | Estado das teclas | + divisores 100 k/150 k nos `NO` dos relés 3/4 | `sense_enabled=1`, modo `auto` |

**Todas as etapas foram aprovadas em 2026-09-28.** O resultado está no fim deste documento.

## Estado da placa do C404 no início

- Chaves táteis removidas (as originais foram danificadas pelo calor).
- Pull-downs de correção do `CH` ao GND: 1 kΩ no `▼` (`CH4`), 4,7 kΩ nos demais.
  Critério: tecla solta, entre os dois pads, **≥ 4 V** (medido: ~4,2 V nos quatro).
- Mapa: `CH1 = *`, `CH2 = ENTER`, `CH3 = ▲`, `CH4 = ▼`; o outro pad das quatro teclas é o
  mesmo nó de +5 V (0 Ω entre eles).

---

## Etapa 1 — Controle das teclas

**Mapa atual (2026-09-25): relé N → `CHN`.**

| Comando | GPIO | Relé | `CH` | Tecla |
|---|---|---|---|---|
| `key star` | 4 | 1 | `CH1` | `*` |
| `key enter` | 5 | 2 | `CH2` | `ENTER` |
| `key up` | 6 | 3 | `CH3` | `▲` |
| `key down` | 7 | 4 | `CH4` | `▼` |

Firmware com esse mapa: tag `BathClient r3.2 (relays H, map CH1-4=relay1-4, …)`, visível em
`version` no `status`. Firmware anterior (▲ no relé 2, ▼ no 3, ENTER no 4) acionaria as teclas
erradas com esta fiação.

### 1.1b Teste dos relés já ligados ao C404, com o C404 **fora da tomada** (2026-09-25)

Display desligado da placa; relés soldados direto nos `CH`; nada mais do ESP32 ligado ao C404.

1. Enviar o firmware por OTA e conferir a tag no `status`.
2. LEDs do HW-280 apagados em repouso.
3. Multímetro em continuidade entre o `CH` e o pad +5 V de cada tecla. Em repouso: ~3,6 kΩ
   (4,7 kΩ de pull-down) ou menos no `CH4` (1 kΩ). Durante `hold <tecla> 3000`: ~0 Ω por 3 s,
   depois volta. Conferir que **só** o `CH` da tabela acima muda.
4. Só depois, C404 na tomada e passo 1.2/1.3.

### 1.0 Gravar o firmware (ESP32 sem nenhum fio ao C404)

Com o DevKit na USB do PC e nada ligado ao C404:

```powershell
External-Devices\tools\.bin\arduino-cli.exe upload --config-file External-Devices\tools\arduino-cli.local.yaml --fqbn esp32:esp32:esp32s3 --input-dir External-Devices\tools\.build\bath -p COM3 External-Devices\banho-termostatico\firmware\thermostatic-bath
```

(trocar `COM3` pela porta do DevKit). Depois de montado, atualizar só por OTA
(`http://192.168.8.1/update`).

Depois de gravar, garantir os padrões desta etapa (se a NVS tiver restos de testes antigos):

```powershell
python bath_app.py config sp_source=0 sense_enabled=0 hub_enabled=0
python bath_app.py mode manual
```

### 1.1 Relés sozinhos (gate G1)

Fonte 5 V dedicada → `5V` do ESP32 e `DC+` do HW-280; negativo → `GND` dos dois.
GPIO 4/5/6/7 → IN1/IN2/IN3/IN4. Jumpers do HW-280 em **`H`** (firmware com `RelayActiveLow = false`,
desde 2026-09-24). **Nada ligado aos bornes dos relés.**

Conecte o PC ao Wi-Fi `Banho Termostatico` e, para cada tecla:

```powershell
python bath_app.py key star
python bath_app.py key up
python bath_app.py key down
python bath_app.py key enter
```

| Verificação | Esperado |
|---|---|
| LEDs em repouso | **todos apagados** |
| Som | um clique de fechar e um de abrir, ~150 ms depois |
| LED do canal | acende e **apaga** |
| Continuidade `COM`–`NO` depois do toque | **aberto** |
| `python bath_app.py hold up 2000` | fecha 2 s e abre |
| Botão `RST` do ESP32 | nenhum relé fecha durante o reset |

**Resultado de 2026-09-24 com jumpers em `L`:** os quatro LEDs ficavam acesos fracos em repouso
e o canal comandado só ficava mais forte. No modo `L` o `IN` é puxado para o VCC de 5 V do
módulo; o GPIO em 3,3 V deixa ~1,7 V sobre o LED do optoacoplador, que conduz parcialmente, e o
relé não solta direito. Por isso o projeto passou a usar jumpers em `H` com o firmware ativo em
HIGH: repouso em 0 V (LED apagado) e relé aberto também durante o reset do ESP32.

Ordem para trocar, com **nada ligado aos bornes dos relés**:

1. Enviar por OTA o firmware com `RelayActiveLow = false`. Com os jumpers ainda em `L`, os
   quatro relés ficam fechados até o passo 3; sem nada nos bornes, isso não causa efeito.
2. Desligar a fonte do ESP32/HW-280.
3. Mover os **quatro** jumpers para `H`.
4. Ligar: LEDs apagados em repouso. Refazer a tabela acima.

Resultado com jumpers em `H` (2026-09-24): LEDs apagados em repouso e `hold up 2000` com os dois
cliques. **G1 aprovado.**

Se com os jumpers em `H` algum LED ainda acender em repouso, meça o `IN` desse canal: deve dar
~0 V. Se der 0 V e o LED continuar aceso, o problema é o módulo, não o firmware.

### 1.2 Um relé no C404 (gate G2)

C404 **fora da tomada** para soldar. Solde com pouco tempo de ferro, nos pads vazios das
chaves ou nos pontos `CH`, nunca com o ferro parado mais de 2–3 s.

| Fio | De (C404) | Para (HW-280) |
|---|---|---|
| comum | pad +5 V de qualquer tecla | `COM` do relé 3 |
| `▲` | `CH3` | `NO` do relé 3 |

**Não ligue o GND do ESP32 ao C404 nesta etapa.** Os contatos dos relés são isolados; sem GND
comum, nenhum circuito do ESP32 alcança o C404.

Ligue o C404 (com o sensor de temperatura conectado) e anote o SP do painel. Então:

```powershell
python bath_app.py key up
```

| Verificação | Esperado |
|---|---|
| Um toque | o C404 reage como a um toque físico em `▲` (SP +0,1 ou abre a edição, conforme a tela) |
| `python bath_app.py key up 10` | exatamente 10 incrementos |
| Depois do último toque | SP para de mexer |

Se um toque às vezes não for aceito, aumente `press_ms` (`config press_ms=200`). Se dois toques
seguidos contarem como um, aumente `gap_ms`. O contato do relé passa só ~1 mA com o pull-down
de 4,7 kΩ; se houver falhas intermitentes com `press_ms` já alto, troque o pull-down desse `CH`
por 1 kΩ (~5 mA), o que dá contato mais confiável.

### 1.3 Os quatro relés (gate G3)

| Fio | De (C404) | Para (HW-280) |
|---|---|---|
| comum | pad +5 V | `COM` dos relés 1, 2, 3 e 4 (um fio, jumpeado nos quatro bornes) |
| `*` | `CH1` | `NO` relé 1 |
| `ENTER` | `CH2` | `NO` relé 2 |
| `▲` | `CH3` | `NO` relé 3 |
| `▼` | `CH4` | `NO` relé 4 |

Total: 5 fios. Testes, nesta ordem:

1. `key down`, `key star`, `key enter`: cada um tem o mesmo efeito do toque físico.
2. Sequência manual pelo app, observando o painel: `key star` → `key up` ×5 → `key enter`.
   O SP deve subir 0,5 e ficar gravado (desligar e ligar o C404 para confirmar).
3. Sincronizar a sombra com o painel e testar a sequência automática:

   ```powershell
   python bath_app.py sync 30.0          # o SP que o painel mostra
   python bath_app.py delta 0.5
   python bath_app.py status
   ```

   `seq_state` deve terminar em `done`, `sp_shadow` = 30,5, e o painel também em 30,5.
4. `delta -0.5` volta a 30,0. `sp 31.0` e `sp 30.0` idem.
5. `abort` no meio de uma sequência longa (`sp 35.0`): todos os relés abrem na hora.

Ajustes decididos aqui (anotar em `CURRENT_STATUS.md`): `press_ms`, `gap_ms`, `menu_ms`
(espera depois do `*`), se `*` é mesmo necessário antes das setas (`enter_key`) e se `ENTER`
grava (`confirm_key`).

### Segurança durante a etapa 1

- Banho sem amostra e sob supervisão: um erro de sequência muda o SP.
- Tirar a alimentação do ESP32/HW-280 abre todos os relés; o painel volta a ser só manual.
- Sem chaves táteis na placa, o C404 só é operável pelos relés. Instalar chaves novas (mesmo
  tamanho e altura, solda rápida) antes do uso normal do banho.

### Resultado da etapa 1 (2026-09-25)

Firmware `relays H, map CH1-4=relay1-4` por OTA. Teste 1.1b aprovado (cada `hold` fecha só o
`CH` certo). Com o C404 ligado, os setpoints enviados pelo app foram atingidos corretamente,
inclusive com tecla mantida. **Etapa 1 aprovada.** Pendentes opcionais desta etapa: G4
(comportamento em `in.L`, habilita o `home`) e G5 (reboot no meio de uma sequência).

---

## Etapa 2 — Leitura do display

Objetivo: o ESP32 ler PV e SP do painel (`sp_source = 1`), o que fecha a malha e absorve
mudanças manuais. Três passos, cada um só depois do anterior.

### 2.1 Identificar as linhas do display (só multímetro, nada soldado)

C404 ligado com o sensor; ESP32 **sem nenhum fio novo** ao C404. Multímetro em tensão DC com a
ponta preta no GND do C404 (nunca continuidade com o C404 energizado). Se o multímetro tiver
modo Hz, meça também a frequência.

| Ponto | DC (V) | Frequência (Hz) | Muda ao apertar uma tecla pelo app? |
|---|---|---|---|
| `A` | | | |
| `B` | | | |
| `C` | | | |
| `D` | | | |
| `E` | | | |
| `F` | | | |
| `G` | | | |
| `PD` | | | |
| `1A` | | | |
| `1B` | | | |
| `1C` | | | |
| `1D` | | | |
| `1L` | | | |
| `2DISP` | | | |

Procure também, perto do display inferior, pontos com nomes como `2A…2D`: são candidatos aos
dígitos do SP. Anote qualquer outro ponto rotulado.

Leitura esperada: segmento ou dígito tem DC intermediário (algo entre 0,5 e 4,5 V, a média da
varredura) e frequência de centenas de Hz a alguns kHz, e **não** reage a teclas. Um ponto que
fica parado em 0 ou 5 V é alimentação, habilitação ou outra coisa. Um ponto que muda ao apertar
tecla indica teclado varrido pelas linhas do display: nesse caso, avise antes de ligar qualquer
coisa nele.

Com a tabela preenchida, o `BoardConfig.h` é ajustado (linhas de dígito, bancos) e só então se
passa ao 2.2.

**Medição de 2026-09-25** (painel superior ` 34.x`, com o último dígito variando; inferior
`33.6`; tensão DC média, ponta preta no GND):

| `A` | `B` | `C` | `D` | `E` | `F` | `G` | `PD` | `1A` | `1B` | `1C` | `1D` | `1L` | `2DISP` |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1,51 | 1,45 | 1,26 | 1,25 | 0,50 | 0,96 | 0,25 | 1,24 | 2,35 | 2,07 | 2,07 | 2,06 | 2,52 | 1,91 |

Leitura:

- Os 14 pontos comutam (nenhum parado em 0 ou 5 V): todos são linhas de varredura.
- Nos segmentos, a média cresce com o número de dígitos acesos naquele segmento: `E` (aceso
  só no `6`) é baixo, `F` (no `4` e no `6`) o dobro, `A`/`B`/`D` (4 dígitos) mais altos. Isso
  indica **segmento aceso = HIGH** (`disp_seg_low = 0`), ~0,4–0,5 V por dígito aceso, o que dá
  uns 10–12 intervalos na varredura (8 dígitos + LEDs + possivelmente teclado).
- Duas exceções: `G` (0,25 V) deveria ser dos mais altos (aceso em `3`, `4` e `6`), e `C`
  ficou baixo. Ou o rótulo `G` da placa não é o segmento G, ou essas linhas têm outro
  acionamento. A captura decide.
- `1A…1D`, `1L` e `2DISP` com médias de ~2 V: compatíveis com 4 linhas de dígito, uma de LEDs e
  uma de banco, que é o modelo do firmware. Mapa adotado como hipótese: `1A…1D` → GPIO
  16/17/18/21, `2DISP` → 38, `1L` → 39.

Médias DC não dizem polaridade nem fase. Por isso o 2.3 começa por uma **captura** com o
próprio ESP32 como analisador lógico (`/capture`), antes de ligar a decodificação.

### 2.2 Montar a interface (fora do C404)

**Montagem adotada em 2026-09-25: divisores**, porque não há CD74HC4050 disponível. Uma placa
com 14 divisores (3,3 kΩ em série + 5,1 kΩ para GND), um por linha, GPIOs de `WIRING.md` §3.
Teste de bancada antes de ir ao C404: 5 V na entrada de cada divisor → ~3,0 V na saída.
Regras: ESP32 ligado **antes** do C404 e desligado **depois**; nunca o C404 ligado com os
divisores conectados e o ESP32 sem alimentação.

Alternativa (se aparecer o CI): buffer CD74HC4050, descrito abaixo; mesmos GPIOs.


Três CIs (6 canais cada, 18 canais para até 14 linhas). O 4050 alimentado em 3,3 V aceita 5 V
nas entradas mesmo desligado (não tem diodo da entrada para o VCC) e não carrega as linhas do
C404, ao contrário dos divisores.

Pinagem do CD74HC4050 (DIP-16):

| Pino | Função | Pino | Função |
|---|---|---|---|
| 1 | VCC → **3V3 do ESP32** | 16 | NC |
| 2 | 1Y (saída) | 15 | 6Y (saída) |
| 3 | 1A (entrada) | 14 | 6A (entrada) |
| 4 | 2Y | 13 | NC |
| 5 | 2A | 12 | 5Y |
| 6 | 3Y | 11 | 5A |
| 7 | 3A | 10 | 4Y |
| 8 | GND | 9 | 4A |

Cada canal: ponto do C404 → entrada `A` → saída `Y` → GPIO do ESP32 (tabela de GPIOs em
`WIRING.md` §3). Entradas sem uso ligadas ao GND. Capacitor de 100 nF entre VCC e GND de cada
CI, bem perto dos pinos 1 e 8.

Teste de bancada antes de ir ao C404: com o ESP32 ligado, entrada em 5 V → saída ~3,3 V;
entrada em GND → saída ~0 V, em todos os canais usados.

### 2.3 Ligar ao C404, uma linha por vez no início

1. C404 fora da tomada. Um fio de **GND** do C404 ao GND do ESP32/4050: a partir daqui o GND é
   comum. Nunca ligar a USB do ESP32 a um PC aterrado com esse fio conectado.
2. Ligar **um** segmento (`A`) e **uma** linha de dígito. Ligar o C404: painel normal, sem
   toques fantasmas por 5 min, e os quatro `CH` ainda com ≥ 4 V entre os pads.
3. Captura: `python bath_app.py capture` (2000 amostras a 10 µs). Imprime, por linha, % do
   tempo em HIGH, número de bordas e frequência, e salva `capture_<data>.json`. As duas linhas
   ligadas devem mostrar bordas; as não ligadas ficam paradas.
4. Ligar o resto, um grupo por vez (segmentos, depois `1A…1D`, depois `2DISP` e `1L`),
   repetindo a verificação do item 2 a cada grupo.
5. Com tudo ligado, nova captura com o painel mostrando valores conhecidos; enviar o arquivo
   `.json` para análise. Dela saem a polaridade (`disp_seg_low`, `disp_dig_low`), a fase
   (`disp_seg_lead`), a ordem dos dígitos e dos bancos e o rótulo real do `G`.
6. `bath_app.py display` até `text` reproduzir o painel. Então `sp_source = 1` e
   VALIDATION.md G6.

### 2.4 Refazer os pontos de leitura no lado do PIC (2026-09-26)

Motivo: `HARDWARE.md` §3, "Placa CPU do C404". Os GPIOs não mudam; muda o ponto do C404 e o
resistor do divisor para o GND (5,1 kΩ → **6,8 kΩ** nas linhas do lado do PIC).

1. **Continuidade (C404 fora da tomada).** Para cada pad `A…G`, `PD`, achar o resistor `101`
   com 0 Ω até o pad; a outra ponta (100 Ω até o pad) é o lado do PIC. Anotar:

   | Pad | Resistor | Ponta do lado do PIC confirmada? |
   |---|---|---|
   | `A` | (R53?) | |
   | `B` | (R52?) | |
   | `C` | (R51?) | |
   | `D` | (R50?) | |
   | `E` | (R49?) | |
   | `F` | (R48?) | |
   | `G` | (R47?) | |
   | `PD` | (R46?) | |

2. **Dígitos.** Para cada pad `1A…1D` (e `1L`), achar o transistor (Q9–Q13) com um pino em
   0 Ω até o pad (coletor) e o resistor ligado à base desse transistor. Anotar pad → Q → resistor
   de base e enviar **antes de soldar**: NPN e PNP têm lógica invertida.

   | Pad | Transistor | Resistor de base | Ponta do lado do PIC |
   |---|---|---|---|
   | `1A` | | | |
   | `1B` | | | |
   | `1C` | | | |
   | `1D` | | | |
   | `1L` | | | |

3. **Religar** (C404 fora da tomada): cada divisor 3,3 kΩ + 6,8 kΩ na ponta do lado do PIC;
   `2DISP` continua no pad (divisor 3,3 k/5,1 k serve). Solda rápida: são pads 0805 ao lado do PIC.
4. **Conferir** (ESP32 ligado antes do C404): painel normal, `CH` com ≥ 4 V entre os pads,
   e `scope 8` mostrando níveis de 0 e ~2,7–3,4 V no GPIO.
5. `capture` e envio do `.json` (itens 5–6 do §2.3).

### 2.5 Montagem adotada: 20 kΩ em série nos pads, janelas por `2DISP` (2026-09-26)

Substitui o §2.4 (lado do PIC), que ficou como alternativa.

| Linha | Ligação | GPIO |
|---|---|---|
| `A B C D E F G PD` | pad do CN2 → **20 kΩ** → GPIO, sem resistor ao GND | 8 9 10 11 12 13 14 15 |
| `2DISP` | pad → 3,3 kΩ → GPIO, 5,1 kΩ do GPIO ao GND (como antes) | 38 |
| `1A…1D`, `1L` | desligados | — |
| GND | GND do C404 ↔ GND do ESP32 | — |

Feito em `A` e `B` (captura de 12:40 com leitura limpa). Passos para completar:

1. C404 fora da tomada; repetir em `C`, `D`, `E`, `F`, `G`, `PD`: tirar o 5,1 kΩ, trocar o
   3,3 kΩ por 20 kΩ.
2. Firmware com `disp_mode = 1` por OTA; ESP32 ligado antes do C404.
3. `bath_app.py config disp_mode=1 disp_seg_low=0` (a NVS pode guardar `disp_seg_low = 1` de antes).
4. `bath_app.py display`: `text` deve mostrar os oito dígitos. Se o valor do PV aparecer no
   lugar do SP, `config disp_sp_bank=1`.
5. Mudar o SP (setas) com o PV parado: só os quatro dígitos do SP em `display` mudam.
6. `config sp_source=1` e VALIDATION.md G6.

---

## Resultado das etapas 2 e 3 (2026-09-28)

Montagem do §2.5 completa nos oito segmentos, com o sensoriamento de `▲`/`▼` (`WIRING.md` §4).
Firmware de 2026-09-26 (recompilado em 28/09) por OTA, com a configuração reaplicada:

```bash
python bath_app.py config mode_hold_ms=1000 guard_check_ms=10000 disp_decimals=1 sense_enabled=1
python bath_app.py config sp_min=-20 sp_max=90
```

Resultados:

- `display_text` igual ao painel e `guard_ignored = 0`;
- `sp 30` e `sp 28` terminaram em `done` com hold e 3 toques finais;
- no modo `auto`, mudanças manuais (toques em `▼`, `▲` mantido) foram revertidas 12–16 s
  depois de soltar;
- o gesto `▲`+`▼` alternou o modo em 1 s;
- o app Android (`apps/flutter`) também comandou o banho corretamente.

**Etapas 2 e 3 aprovadas.** O registro completo e o que falta estão em `CURRENT_STATUS.md`
("Conclusão").
