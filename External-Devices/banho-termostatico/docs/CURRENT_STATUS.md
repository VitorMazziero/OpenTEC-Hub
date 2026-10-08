# Estado atual — Banho termostático

**Atualizado:** 2026-10-07 (software); última bancada registrada: 2026-09-28

Firmware ativo r3.3: correções de reconexão, registro e preservação da ligação OTA
compiladas e verificadas em simulações no PC. Ver `NETWORK_R3_3.md` para alterações,
comando de atualização direta pelo AP e validação física pendente. Os resultados
abaixo descrevem a bancada r3.2 e não comprovam o comportamento físico da r3.3.

## Conclusão (2026-09-28)

**O nó controla o setpoint do C404 pelas teclas e lê o painel.** O firmware r3.2 é de 26/09 e
foi recompilado em 28/09 com o mesmo tamanho. Com ele, o modo automático reverte mudanças manuais
e não reage a leituras ruins do display. Na bancada de 2026-09-28 funcionaram:

- setpoint absoluto pelo `bath_app.py`;
- setpoint pelo app Android próprio do nó (`apps/flutter`), direto no AP, com `hub_enabled = 0`;
- gesto de modo e correção automática.

Nenhum erro nem leitura ignorada apareceu (`guard_ignored = 0`) em ~8 min de registro contínuo
com vários testes.

### Evidência (`bath_app.py status` e `log`, 2026-09-28)

| Teste | Registro (`millis`) | Resultado |
|---|---|---|
| 6 toques manuais em `▼`, o 1.º mantido: SP 35,0 → 32,0 | armado 51 662; último toque 54 687; `pending` 60 508; reversão 70 512 | hold de `▲` 3,1 s a 13,8 toques/s, solta em 34,6, 4 toques; `done` 76 975, correções 0 |
| `sp 30` (de 35,0) | 96 117 → 104 456 | hold de `▼` 5,3 s a 11,6 toques/s, solta em 30,3, 3 toques; `done` em 8,3 s |
| `▲` mantido ~1,5 s: SP 30,0 → 31,1 | armado 181 825; `pending` 184 451; reversão 194 455 | 11 toques de `▼` (abaixo de `hold_min_steps = 15`); `done` 199 309 |
| Comando 30,0 → 28,0 | 414 786 → 420 538 | hold 2,7 s a 12,0 toques/s, 3 toques; `done` em 5,8 s |
| Gesto `▲`+`▼` duas vezes | toque 454 412 → `manual` 455 481; toque 456 660 → `auto` 457 694 | troca em ~1,0 s, uma vez por gesto |
| SP 28,0 → 28,3 (auto-repetição durante o gesto) | `pending` 464 455; reversão 474 461 | 3 toques; `done` 476 873 |

Conclusões medidas:

- **Reversão no automático:** 12–16 s depois do último toque solto. É o esperado com
  `guard_delay_ms = 5000`, `guard_check_ms = 10000` e confirmação em duas avaliações.
- **Hold:** taxa de 11,6–13,8 toques/s. A perna final ficou em 3–4 toques e nenhuma sequência
  precisou de correção, então a cauda da auto-repetição está coberta por `hold_stop_steps = 3`
  e `hold_lag_ms = 80` (G3b).
- **Sensoriamento:** `manual_presses` conta só os toques físicos. As sequências dos relés, com
  até 50 toques, não mudam o contador (G7).
- **Guarda armado por toque:** o guarda só agiu depois de um toque físico ou do gesto. Não houve
  desvio sem toque (`guard_ignored = 0`).

### Configuração em uso (NVS do nó)

Os valores diferentes dos padrões do firmware precisam ser reaplicados depois de um `reset_nvs`:

| Chave | Valor | Por quê |
|---|---|---|
| `enter_key`, `confirm_key` | `0`, `0` | neste C404 as setas mudam o SP direto e o valor fica gravado (padrões do firmware: 1/2) |
| `sp_source` | `1` | SP lido do display |
| `sense_enabled`, `sense_mask` | `1`, `6` | sensoriamento de `▲`/`▼` |
| `disp_mode`, `disp_seg_low`, `disp_slot_us`, `disp_sp_bank`, `disp_decimals` | `1`, `0`, `1023`, `0`, `1` | leitor por janelas de `2DISP` |
| `sp_min`, `sp_max` | `-20`, `90` | faixa aceita na leitura e nos comandos; `-20` supõe o `in.L` observado (conferir no `ConF`, G4) |
| `mode_hold_ms`, `guard_delay_ms`, `guard_check_ms` | `1000`, `5000`, `10000` | gesto de 1 s; guarda a cada 10 s |
| `hold_*`, `press_ms`, `gap_ms` | padrões | validados pelas sequências acima |
| `hub_enabled` | `0` | sem Hub até o G10 |

### Montagem final

| Função | Ligação | GPIO |
|---|---|---|
| Teclas `*`, `ENTER`, `▲`, `▼` | relé N (HW-280, jumpers em `H`) → `CHN` do CN2 | 4, 5, 6, 7 |
| Sensoriamento `▲`, `▼` | `NO` dos relés 3/4 → 100 kΩ → GPIO, 150 kΩ ao GND | 2, 42 |
| Segmentos `A…G`, `PD` | pad do CN2 → 20 kΩ → GPIO, sem resistor ao GND | 8–15 |
| `2DISP` (banco do display) | pad → 3,3 kΩ → GPIO, 5,1 kΩ ao GND | 38 |
| LED de modo | GPIO → 330 Ω → LED → GND | 40 |
| GND | comum entre ESP32 e C404 | — |

Chaves táteis novas só em `▲` e `▼`. `*` e `ENTER` são acionados apenas pelos relés.
`1A…1D` e `1L` não são usados. Regras que continuam valendo:

- nunca ligar o USB do ESP32 a um PC aterrado com o GND comum ao C404;
- ligar o ESP32 antes do C404 e desligá-lo depois;
- nunca alimentar o ESP32 pelo `+5V`/`5VA` do C404.

### O que falta

- **Teste de ruído longo:** horas em `auto` sem tocar no painel, conferindo `guard_ignored` e o
  `/log`. Fazer antes de confiar no automático num cultivo.
- G4: comportamento em `in.L`, que habilita o `home`, e conferência de `sp_min`/`sp_max` com o
  `in.L`/`in.H` do `ConF`.
- G5 (reboot no meio de uma sequência) e G8 (OTA com sequência em curso; o OTA simples já é
  usado).
- G6 item 4: `display_sp` nulo dentro do `ConF`.
- G9 itens 4–5: SP mudado no modo manual; `abort` durante uma correção.
- G10: enlace com o Hub (`hub_enabled = 1`) e cascata.
- Linha `PD` (GPIO 15) intermitente no passado; não se repetiu na bancada de 28/09.
- Risco aceito: picos de ~5 V dos pads chegam ao GPIO pelos diodos de proteção através de
  20 kΩ (~70 µA), fora da especificação do ESP32.
- Identificação térmica e sintonia com água antes de qualquer cultivo.

## Histórico da bancada (2026-09-24 a 2026-09-26)

O incidente do oscilador do PIC parado, descrito no fim desta lista, não se repetiu: o painel e a
leitura funcionam normalmente desde então. A causa e o reparo não foram registrados aqui.

- **`CH1…CH4` da placa do C404 são as teclas**, confirmado por continuidade:
  `CH1 = *`, `CH2 = ENTER`, `CH3 = ▲`, `CH4 = ▼`; o outro terminal das quatro é o +5 V do
  C404 e cada `CH` tem pull-down (~10 kΩ). Teclas ativas em HIGH. A primeira montagem tratou
  os `CH` como dígitos do display (mapa corrigido em `HARDWARE.md` §3 e `WIRING.md` §2.3/§3).
- Sintomas da primeira montagem e causas:
  - SP descendo/subindo sozinho, entrada em menus: chaves táteis danificadas pelo calor
    (a carga dos divisores nos `CH` puxa para o nível de "solta" e não explica toques);
  - display "sem sinal": nenhuma linha de dígito real ligada;
  - visor superior com segmentos errados com o ESP32 desligado: divisores nos segmentos
    presos pelos diodos de proteção do ESP32 sem alimentação.
- Com todos os fios retirados, as chaves táteis soldadas nos terminais continuavam fechadas
  depois de um toque (auto-repetição, entrada em menus): dano térmico. Troca das chaves
  pendente; relés passam a ser soldados nos `CH`.
- Relés HW-280: com jumpers em `L` os quatro LEDs ficavam acesos fracos em repouso e o canal
  comandado só ficava mais forte (GPIO a 3,3 V contra o VCC de 5 V do módulo). Causa do "liga e
  não desliga". Correção: jumpers em `H` + firmware com `RelayActiveLow = false` (binário
  de 2026-09-24 16:25, enviado por OTA). **G1 aprovado com jumpers em `H`:** LEDs apagados em
  repouso, `hold` de 2 s com clique de fechar e de abrir.
- Firmware: revisão da lógica dos relés não encontrou toque sem limite de tempo nem escrita
  fora do laço principal. O sensoriamento das teclas estava escrito para ativo em LOW;
  corrigido para ativo em HIGH (`BoardConfig::SenseActiveHigh`), senão `▲`/`▼` seriam
  lidas como apertadas em repouso (gesto de modo e `sp_known` falsos).
- Fuga nas linhas das teclas: sem chave nenhuma, os `CH` ficavam acima de 0 V em repouso
  (medido entre os pads, com pull-down externo de 5,1 kΩ: `▲`/`ENTER` ~1 V, `*` ~2 V,
  `▼` ~3 V → `▼` lido como apertado, SP descia até −20; `*` entrava em menus). Um pull-down
  de **1 kΩ** do `CH4` ao GND do C404 eliminou a descida do SP. Origem da fuga (caminho na
  placa ou pino do microcontrolador) ainda não medida; acompanhar a tensão dos `CH`.
- 2026-09-25: display desconectado da placa e relés soldados direto nos `CH`; os toques
  fantasmas pararam. Novo mapa: **relé N → `CHN`** (IN1/GPIO 4 = `*`, IN2/GPIO 5 = `ENTER`,
  IN3/GPIO 6 = `▲`, IN4/GPIO 7 = `▼`); firmware com a tag `relays H, map CH1-4=relay1-4`.
- Estado físico: relés ligados aos `CH`; display e sensoriamento desconectados; sem GND
  comum entre ESP32 e C404.
- **Etapa 1 aprovada (2026-09-25):** relés nos `CH`, setpoints pelo app atingidos corretamente,
  inclusive com tecla mantida. Próximo: etapa 2 (display), começando pela medição das linhas
  (`MONTAGEM_ETAPAS.md` §2.1).
- Montagem em 2026-09-25 (relato do operador):
  - pull-downs de correção removidos; ficam só os originais do C404 (a fuga foi atribuída a
    solda ruim e ligação errada). Conferir ≥ 4 V entre os pads de cada tecla em repouso;
  - no C404 deste banho **as setas mudam o SP direto**, sem `*` antes nem `ENTER` depois.
    Confirmado pelo operador: o SP mudado só com as setas fica gravado. Configuração adotada:
    `enter_key = 0`, `confirm_key = 0` (NVS do nó; os padrões do firmware continuam 1/2, então
    um `reset_nvs` exige reaplicar);
  - chaves táteis novas só em `▲` e `▼` (uso do operador);
  - LED de modo montado no GPIO 40;
  - sensoriamento de `▲`/`▼` montado (2026-09-26): `NO` do relé 3 (`CH3`) e do relé 4 (`CH4`)
    → 100 kΩ série → GPIO 2 / GPIO 42, com 150 kΩ do GPIO ao GND. `sense_enabled` ainda 0 até
    a conferência de nível (GPIO ~0 V solta, ~3 V apertada);
  - display por divisores 3,3 k/5,1 k em 13 linhas (`A…G`, `PD`, `1A…1D`, `2DISP`); `1L`
    (GPIO 39) ainda não ligado; não é necessário para decodificar.
- Auto-repetição das setas medida pelo operador (2026-09-26): ao manter `▲`/`▼`, um passo de
  0,1 °C imediato, espera de ~0,5 s e depois ~1 °C/s (~10 passos/s). Compatível com os padrões
  de hold do firmware (`hold_stall_ms` 2500 > 0,5 s; `press_ms` 150 < 0,5 s, então um toque
  nunca vira repetição); simulação no PC ajustada para 0,5 s. Cauda após soltar: a medir com o
  display (G3b).
- Etapa 1 com setas apenas: `enter_key=0`, `confirm_key=0`, `sense_enabled=1` aplicados;
  `delta` 21,0 → 21,5 correto; `manual_presses` conta os toques físicos em `▲`/`▼`.
- Primeira captura do display (`capture_20260926_105352.json`, 2000 × 10 µs, painel 21,5/21,5):
  `2DISP` é uma onda quadrada limpa de ~108 Hz (4,1 ms alto / 5,1 ms baixo); **segmentos e
  `1A…1D` lidos em LOW quase o tempo todo** (uma única amostra com todos em HIGH, padrão de
  segmentos do dígito `2`). Com o divisor 3,3 k/5,1 k, esses pontos nunca passam do limiar do
  GPIO: ou estão do lado dos LEDs (nível limitado a ~2 V), ou os pulsos são curtos demais para
  10 µs, ou o divisor carrega a linha. Próximas capturas: 2 µs e 100 µs, e tensão DC no ponto
  com o divisor ligado.
- Capturas de 2026-09-26 11:00 (2 µs × 8000; 100 µs × 8000 parado; 100 µs × 8000 segurando `▲`,
  só o display de baixo mudou): **mesmo resultado**. `2DISP` = onda quadrada lógica de ~108 Hz,
  ~44% em HIGH; segmentos e `1A…1D` em LOW em > 99,9% das amostras, com raras amostras isoladas
  em HIGH (ruído ou pulsos muito estreitos). Não são pulsos curtos perdidos (2 µs não mostrou
  nada) nem varredura lenta (0,8 s não mostrou nada).
- Tensão DC com o divisor ligado (C404 / GPIO): `A` 1,5 / 0,9 V (sem divisor 1,51 V: não
  carrega); `1A` 1,0 / 0,6 V (sem divisor 2,35 V: **o divisor derruba a linha**, ponto de alta
  impedância); `2DISP` 1,75 / 1,01 V (sem divisor 1,91 V).
- Conclusão: só `2DISP` é sinal lógico 0/5 V. Os pontos `A…G`, `PD` e `1A…1D` ficam em níveis
  intermediários constantes (~1–2,4 V), típicos do lado dos LEDs (depois dos resistores ou de um
  CI driver), e nunca cruzam o limiar do GPIO. O leitor digital não funciona nesses pontos.
  Próximo: `/scope` (osciloscópio pelo ADC) para ver forma de onda e níveis, e identificação
  dos CIs do lado do display (fotos, códigos) para achar o lado lógico ou o barramento do driver.
  Recomendação até lá: desligar os divisores de `1A…1D` do C404 (carga alta sem utilidade).
- 2026-09-26, fotos da placa e `/scope` em `A`/`B`/`C`: C404 = placas `C504 - CPU` (PIC16F76,
  20 MHz, 24LC16B, LM324, HEF4051) e `C504 - DISPLAY`, ligadas pelo CN2. Segmentos saem do PIC
  por 100 Ω e os pads do CN2 estão do lado do LED (aceso ≈ 1,5–2,4 V); dígitos por Q9–Q13.
  Varredura: ~1 ms por dígito, 5 janelas por display, 2 displays (108 Hz no `2DISP`).
  **Decisão:** ler segmentos e dígitos no lado do PIC dos resistores, com divisor 3,3 k + 6,8 k
  (`HARDWARE.md` §3, `MONTAGEM_ETAPAS.md` §2.4). Continuidade pad → resistor/transistor pendente.
- **Mudança de rumo (2026-09-26, 12:40):** sem acesso fácil ao lado do PIC e sem transistores,
  o operador tirou o 5,1 kΩ e passou o série para **20 kΩ** em `A` e `B` (pad → 20 kΩ → GPIO,
  sem resistor ao GND). `scope 8`: aceso 2,0–2,5 V no GPIO, apagado 0, picos raros até o
  limite do ADC. `capture 8000 20` com o painel em ` 23.6` nos dois displays: `A` e `B` alternam
  em janelas de ~1 ms com apagamento de ~20 µs; `2DISP` 4,1 ms em HIGH (4 janelas) e 5,1 ms em
  LOW (5 janelas: 4 dígitos + LEDs). Por janela, a partir de cada borda de `2DISP`: `A B` =
  `10, 11, 11, 00` (+ `01` na janela dos LEDs) = `6, 3, 2, branco`, ou seja, **varredura da
  direita para a esquerda**. Os dígitos saem da contagem de janelas: `1A…1D` não são necessários.
  Risco aceito pelo operador: picos de ~5 V passam pelo diodo de proteção do GPIO através de
  20 kΩ (~70 µA), fora da especificação do ESP32.
- Firmware: leitor no modo 1 (`disp_mode`), janelas contadas a partir de `2DISP`, amostragem por
  timer de 100 µs e maioria por janela; `disp_seg_low` passa a 0 por padrão; `disp_sp_bank` a
  confirmar (qual nível de `2DISP` é o display do SP).
- **Display lido (2026-09-26, 12:5x):** firmware de 12:51 por OTA, `disp_mode=1`,
  `disp_seg_low=0`, os 8 segmentos com 20 kΩ. `bath_app.py display` com o SP sendo baixado nas
  setas: ` 25.7 28.9` → ` 27.9 25.6` → ` 28.1 23.5` (`pv`/`sp` decodificados, `sp_live` igual a
  `sp`, `frames` e `live_frames` crescendo). PV subindo (banho aquecendo) e SP descendo:
  `disp_sp_bank = 0` coerente. As duas primeiras leituras tiveram `?` (segmento C lido aceso no
  `2`, raw 95 em vez de 91), provavelmente com a fiação ainda sendo terminada; a observar.
  `leds` = 34/35 (janela dos LEDs de sinalização). Próximo: `sp_source=1` e G6.
- **Incidente 2026-09-26:** com `sp_source=1`, o ponto decimal não foi lido (`" 272 234"`);
  `sp 25.0` planejou 2100 passos de descida a partir de "234" e manteve `▼` até o `abort`.
  Depois, com o ponto lido, `sp 25.0` (de 15,7) e `sp 35.0` (de 25,0) funcionaram com hold.
  Firmware corrigido: `disp_decimals`, faixa `sp_min`–`sp_max` na leitura e salto no hold.
  A investigar: linha `PD` (GPIO 15) intermitente.
- Modo automático testado pelo operador (2026-09-26): funcionou, mas depois de um tempo o SP
  oscilou 35,0 ↔ 34,9 três vezes e o guarda se suspendeu (`guard_corrections: 4`,
  `sp_mismatch`); a mudança manual seguinte não foi corrigida (suspenso). Causa provável:
  leitura ruim do último dígito disparando correções reais. Firmware corrigido: guarda só arma
  com toque manual, confirma o valor em duas avaliações de 10 s; `/log` para diagnosticar;
  gesto `▲`+`▼` em 1 s.
- **Incidente 2026-09-26:** teste com `A` e `B` ligados aos GPIO 8/9 só por 20 kΩ em série (sem o
  5,1 kΩ para GND). Soldado com tudo desligado; GND do ESP32 ligado ao do C404. Ao religar, os
  dois displays apagaram totalmente, sem cheiro ou ruído; +5 V normal. Medições com o C404
  sozinho: `2DISP` 4,2 V fixo (antes ~1,9 V, 108 Hz), `A` 0,86 V, `1A` 2,85 V; pernas do cristal
  de 20 MHz em **0,8 V e 4,7 V → oscilador do PIC parado**. Hipóteses: curto/resíduo no circuito
  do cristal (fica ao lado do CN2 e da coluna de resistores), capacitor cerâmico trincado por
  flexão da placa, cristal danificado, ou PIC danificado. A mudança elétrica em si (carga menor
  no pad, GPIO como entrada) não explica a parada. Pendente: inspeção, resistência das pernas do
  cristal para `0V`/`+5V`, limpeza, ressolda do cristal e dos capacitores, troca do cristal.

## Implementado

- Firmware ativo: `firmware/thermostatic-bath` r3.2, com a tag `BathClient r3.2 (relays H, map
  CH1-4=relay1-4, …)`.
- Compilação com ESP32 core 3.3.11 e FQBN `esp32:esp32:esp32s3`: 1 108 105 B de flash (84%) e
  67 312 B de RAM global (20%).
- Controle do C404 validado na bancada: setpoint com hold e toques, leitura do display, modos,
  guarda armado por toque, sensoriamento, gesto, `/log` e OTA.
- Enlace r3.2 com o Hub (implementado e testado só em software):
  - tarefa FreeRTOS própria para hello/push/HTTP;
  - snapshot protegido e fila fixa de comandos;
  - `/bathData`, `ver=r3.2`, MAC real, fase/erro/display/mode/guard/ACK observáveis;
  - recusa de comando do Hub publicada (`rej_cmd_id`/`rej_err`) e faixa `sp_min`/`sp_max`;
  - posse do Hub (`X-Hub-Owner`, 10 s): a API local só aceita `abort`/`stop`; a ação `stop`
    (abort + manual) atende a parada do Hub; re-hello imediato após 403/404;
  - com `hub_enabled=1`, período efetivo limitado a 2 s para respeitar a janela do Hub;
  - push não é suspenso durante hold;
  - `hub_enabled=0` por padrão até o G10.
- `tests/host-sim`: todos os cenários passam com os fontes reais de setpoint, guarda, teclado,
  parser e contexto. Isso inclui reentrega idempotente, recusa publicada, posse do Hub, `stop`,
  ruído ignorado (T10) e guarda a cada 10 s (T11).
- Aplicativos: `apps/flutter` (Android, 1.1.0+2) e `apps/desktop-python/bath_app.py`, ambos
  usados contra o nó real em 2026-09-28.

## Integração com o Hub e os aplicativos gerais

O lado do Hub está no `ESP32S3-HUB` 10.6.0-dev (`DEV_BATH`, `/bathData`, cascata; veja o
changelog do Hub). O Windows App e o `Android_app/` do Hub têm os próprios planos. Nenhum deles
foi validado com este nó real: a validação é o G10.

Planos:

- visão geral: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO.md`;
- Hub consolidado: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO_HUB.md`;
- Windows App: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO_WINDOWS_APP.md`;
- app Android próprio: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO_APP_ANDROID.md`.

## Gates de validação física

Situação de cada gate em `VALIDATION.md`:

- aprovados: G1, G2, G3, G3b, G7 e G7b;
- parciais: G6 (falta o item 4) e G9 (faltam os itens 4–5);
- pendentes: G4, G5, G8 com sequência em curso, G10, o teste de ruído longo e a
  identificação térmica.

## Decisões tomadas na bancada

- `enter_key = 0` e `confirm_key = 0`: neste C404 as setas mudam o SP direto.
- `step_c = 0,1`, com 10 toques = 1,0 °C. O comportamento em `in.L` ainda não foi medido (G4).
- Auto-repetição: passo imediato, ~0,5 s de espera e ~10–14 passos/s, sem aceleração. A cauda é
  coberta pelos padrões de `hold_*`.
- `▲`+`▼` juntas não levam o C404 a nenhum menu, então o gesto de modo é viável. O SP pode andar
  alguns passos durante o gesto (28,0 → 28,3 no teste) e o guarda reverte depois.
- Display: segmento aceso em HIGH, varredura da direita para a esquerda, banco do SP = `2DISP`
  em 0 (`disp_sp_bank = 0`).
- Pendente fora da bancada: confirmar que `Tempval` mede o reator e que `100B` desabilita a via
  térmica original.

Build e testes comprovam coerência de software, não segurança térmica nem desempenho do
processo.
