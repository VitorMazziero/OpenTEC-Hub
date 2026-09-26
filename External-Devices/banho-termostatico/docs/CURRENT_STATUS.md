# Estado atual — Banho termostático

**Atualizado:** 2026-09-24

## Bancada (2026-09-24)

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

Próximos passos: trocar as chaves danificadas → relés nos `CH` (G1–G3) → tabela de medição
do display (`WIRING.md` §3.2) → CD74HC4050 → G6.

## Implementado e validado em software

- Firmware ativo: `firmware/thermostatic-bath` r3.2 — `BathClient r3.2`.
- Compilação com ESP32 core 3.3.11 e FQBN `esp32:esp32:esp32s3`: 1 085 689 B de flash
  (82%) e 48 344 B de RAM global (14%) (r3.2).
- Controle do C404: setpoint/hold/toques/correção/abort, display, modos e guarda.
- Enlace r3.2 do Hub:
  - tarefa FreeRTOS própria para hello/push/HTTP;
  - snapshot protegido e fila fixa de comandos;
  - `/bathData`, `ver=r3.2`, MAC real, fase/erro/display/mode/guard/ACK observáveis;
  - recusa de comando do Hub publicada (`rej_cmd_id`/`rej_err`) e faixa `sp_min`/`sp_max`;
  - posse do Hub (`X-Hub-Owner`, 10 s): API local só aceita `abort`/`stop`; ação `stop`
    (abort + manual) para a parada do Hub; re-hello imediato após 403/404;
  - com `hub_enabled=1`, período efetivo limitado a 2 s para respeitar a janela do Hub;
  - push não é suspenso durante hold;
  - `hub_enabled=0` por padrão até a integração.
- `tests/host-sim`: 37 cenários passam com os fontes reais de setpoint, guarda, teclado,
  parser e contexto. Inclui três reentregas do mesmo `cmd_id` causando uma única sequência e
  comando recusado sem avanço do ACK, recusa publicada (W), posse do Hub (X) e `stop` (Y).
- Aplicativo Android próprio do banho: `apps/flutter`, versão 1.1.0+2, quatro abas
  (Operação, Modos, Bancada, Config), reentrega idempotente, traço de 10 min, diagnóstico e
  aviso de firmware incompatível. `flutter analyze`, `flutter test` e build de APK passam.
- Aplicativo desktop `bath_app.py` testado contra servidor HTTP simulado (CLI e janela).

## Não implementado

- O Hub 10.4 ainda não possui `DEV_BATH`, `/bathData`, `bathBox`, segunda via térmica nem
  controlador de cascata.
- O Windows App 0.26.4 ainda não possui protocolo, simulador, interface, alarmes, receitas ou
  registros do banho.
- A integração do banho no aplicativo geral `Android_app/` do Hub não faz parte do app Android
  próprio citado acima e não foi implementada aqui.

Planos:

- visão geral: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO.md`;
- Hub consolidado: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO_HUB.md`;
- Windows App: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO_WINDOWS_APP.md`;
- app Android próprio: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO_APP_ANDROID.md`.

## Pendente de validação física

- Gates G1–G9 de `VALIDATION.md`: nenhum fechado.
- Relés, temporização real, display, sensoriamento, NVS, Wi-Fi/OTA e guarda contra o C404 real.
- Enlace r3.2 durante hold de 60 s, stack watermark e reentrega por perda de Wi-Fi após o Hub
  implementar o contrato.
- App Android contra o dispositivo real, incluindo G3b/G7b.
- Identificação térmica e sintonia com água antes de qualquer cultivo.

## Estado da montagem conhecido

- Relés, display e fonte ligados em 2026-09-19.
- Sensoriamento de `▲`/`▼` ainda deve ser ligado nos bornes `NO` dos relés 2 e 3 para GPIO
  2/42, com conferência de nível antes de conectar ao ESP32.
- `sense_mask=6` é padrão; habilitar `sense_enabled=1` somente após a conferência.
- Não usar o ponto `5VA` do C404; usar fonte 5 V dedicada.

## Decisões que a bancada ainda resolve

- `enter_key` e `confirm_key` reais;
- `step_c` e comportamento em `in.L`;
- atraso, taxa, aceleração e cauda da auto-repetição (`hold_*`);
- efeito de `▲+▼` juntas e viabilidade do gesto de modo;
- polaridade, fase e ordem dos dígitos do display;
- confirmação de que `Tempval` mede o reator e `100B` desabilita a via térmica original.

Build e testes comprovam coerência de software, não segurança térmica, acionamento físico nem
desempenho do processo.
