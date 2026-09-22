# Validação — Banho termostático

Build aprovado não prova relés, temporização, display nem persistência. Os gates abaixo
são executados nesta ordem; cada um tem o ajuste de configuração que ele decide.

## G1 — Relés fora do C404

Com o HW-280 alimentado e sem nada ligado aos contatos: `{"key":"star"}`, `up`, `down`,
`enter` pelo app. Cada toque deve produzir um clique de ~150 ms no relé certo e nunca dois
relés ao mesmo tempo. Reset do ESP32 não pode fechar relé algum (medir `COM/NO` no boot).

## G2 — Um relé em paralelo a uma tecla

`COM/NO` do relé `▲` em paralelo à tecla `▲`. Na tela principal do C404, `{"key":"up"}` deve
ter o mesmo efeito de um toque físico. Ajustar `press_ms`: o menor valor que o C404 aceita
sempre, e o maior valor que **não** dispara auto-repetição. Ajustar `gap_ms` para o menor
intervalo em que dois toques seguidos contam como dois.

## G3 — Sequência completa e regras do C404

Com os quatro relés ligados, `{"sync_sp":<SP do painel>}` e depois `{"delta":0.5}`.
Decide-se:

- `enter_key`: a tecla `*` é mesmo necessária antes das setas (observado pelo operador) ou as
  setas agem direto na tela principal (manual §7.1)? Se agirem direto, `enter_key = 0`.
- `confirm_key`: o `ENTER` grava o SP (observado) — confirmar que **sem** ele o valor não
  fica, e que **com** ele o C404 volta à tela principal sem entrar em outro menu.
- `step_c`: 10 toques devem mover exatamente 1,0 °C (`d.P = 1`).
- `menu_ms`: espera mínima após `*` para o primeiro `▲` contar.
- Timeout de edição: quanto tempo sem tecla o C404 leva para sair da edição (deve ser
  maior que `gap_ms`, senão sequências longas se perdem).

## G3b — Auto-repetição das setas (dimensiona o hold)

Com o display já legível (G6) ou, antes disso, olhando o painel: `{"key":"up","hold_ms":2000}`
e depois 5000 e 10000, anotando quanto o SP andou em cada um. Decide-se:

- **Atraso até a primeira repetição** e **taxa** (toques/s); se a taxa cresce com o tempo
  (aceleração) ou o passo muda (0,1 → 1,0 °C), em quantos segundos. `hold_stall_ms` deve
  ficar acima do atraso inicial; `hold_min_steps` é a distância a partir da qual o hold
  ganha dos toques discretos (`atraso + distância/taxa` contra `distância × (press+gap)`).
- **Cauda após soltar**: quantos incrementos o C404 ainda aplica depois de o relé abrir.
  `hold_stop_steps` deve cobrir a cauda; `hold_lag_ms` cobre o resto (leitura → relé).
  O alvo é que a perna a toques depois do hold tenha de 1 a 5 toques e nenhuma correção.
- Se o C404 **não** auto-repete, `hold_enabled = 0` (cada hold custaria `hold_stall_ms`).
- O manual (§7.1) diz que manter `*` volta à tela principal: confirmar que manter `▲`/`▼`
  não tem efeito colateral de tela.

Com `sp_source = 1`: `{"setpoint":<atual + 3,0>}` deve terminar `done` com
`hold_rounds = 1` e `presses_total` pequeno; `/status` durante a sequência mostra
`seq_phase = hold`, `hold_ms` e `hold_rate`. Registrar taxa e cauda em `CURRENT_STATUS.md`.

## G4 — Comportamento em `in.L` (habilita o `home`)

Com o SP perto de `in.L`, enviar mais `▼` do que o necessário. O C404 deve **travar** em
`in.L`; se der a volta para `in.H`, o `home` é inutilizável e deve ficar desabilitado no
app. Configurar `sp_min`/`sp_max` iguais a `in.L`/`in.H` (ou apertar `in.L`/`in.H` no bloco
`ConF` do C404 para faixas mais curtas: o `home` leva `(in.H − in.L)/step_c × (press+gap)`,
≈ 1 min para 100 °C a 0,1 °C/300 ms).

## G5 — Reboot no meio de uma sequência

Iniciar um `setpoint` longo, resetar o ESP32. Após o boot `/status` deve mostrar
`sp_known:false`; `{"setpoint":…}` deve ser recusado com `sp_unknown` até `sync_sp` ou `home`.

## G6 — Caracterização do display (HARDWARE.md §3)

1. Osciloscópio em `CH1`…`CH4`, `2DISP`, `A` e `PD` (referência no GND do C404): registrar
   tensão, polaridade, frequência e a ordem segmento/dígito. Anexar as capturas em
   `tests/evidence/display/`.
2. Ligar os 13 divisores. `GET /display`: `alive:true` e `frames` crescendo.
3. Ajustar `disp_seg_low`, `disp_dig_low`, `disp_seg_lead` até `text` reproduzir o painel.
   Se os oito caracteres saírem embaralhados, corrigir `PvDigits/SpDigits` no `BoardConfig.h`.
4. Entrar no bloco `ConF` do C404: `display_sp` deve virar `null` (texto) e voltar ao sair.
5. `sp_live` em `GET /display` deve seguir o painel sem os ~300 ms de atraso de `sp`, e
   `live_frames` deve crescer; durante `{"key":"up","hold_ms":3000}` o `sp_live` tem de
   acompanhar cada incremento (é o que fecha a malha do hold).
6. Trocar `sp_source = 1`. `{"setpoint":x}` deve terminar em `done` com `display_sp = x`;
   mexer manualmente no SP deve refletir em `sp_shadow` sem comando algum. Depois, G3b.

## G7 — Sensoriamento das teclas (opcional)

Com os divisores das teclas: toque físico em `▲` → `manual_presses` incrementa e, no modo
sombra, `sp_known:false`. Toques do relé não podem contar como manuais e
`presses_unconfirmed` deve ficar em 0 numa sequência normal.

## G7b — Par `▲`+`▼` e gesto de modo

Sem nada ligado ao sensoriamento ainda: na tela principal, pressionar `▲` e `▼` **juntas** por
5 s e observar o SP e a tela. Registrar o que o C404 faz (nada; anda para um lado; alterna;
entra em algum menu). O manual não atribui função ao par. Se entrar em menu, o gesto não
serve e a troca de modo fica pelo comando `mode` ou pelo botão da caixa (`ModeButtonPin`).

Com o sensoriamento de `▲`/`▼` montado (WIRING.md §3b, conferências 1–3) e
`sense_enabled = 1`: manter as duas por mais de `mode_hold_ms` → `/status` mostra
`arrows_held_ms` subindo e `mode` alternando uma única vez por pressionamento; soltar e
repetir alterna de volta. Um toque só em `▲` não pode trocar o modo.

## G9 — Modo automático

Pré-requisitos: G6 (display), G7b. `{"setpoint":x}` pelo app, depois `{"mode":"auto"}`.

1. Mudar o SP no painel em +0,5 °C com toques curtos: `guard` vai a `pending`; ~`guard_delay_ms`
   depois de o último toque ser solto o nó reverte (`guard = correcting` → `watch`), o SP volta
   a `x`, `guard_corrections` incrementa. Registrar o tempo entre soltar e o primeiro relé.
2. Manter `▲` por 3 s (auto-repetição): o mesmo, com a reversão usando hold.
3. Continuar tocando no painel por 10 s: o guarda não pode agir enquanto há tecla pressionada
   ou o display mudando.
4. `{"mode":"manual"}`, mudar o SP: nada acontece; `deviation_c` mostra a diferença.
5. Em `auto`, `{"abort":1}` durante uma correção: `guard = suspended`; `{"setpoint":x}` rearma.

## G8 — OTA e Wi-Fi

Upload de `thermostatic-bath.ino.bin` por `/update` com uma sequência em andamento: os
relés abrem no início do upload (`abort`), o nó reinicia e `/status` responde. Desligar o
ESP32 durante o controle: o C404 continua controlando e as teclas físicas funcionam.

## G10 — Enlace r3.1 com o Hub (executar com o Hub 10.5.1)

1. Habilitar `hub_enabled=1` somente depois de G1–G9 e do handler `/bathData` existir.
2. Confirmar `/nodes`: `dev=bath`, `ver=r3.1` e MAC real.
3. Executar um hold de 60 s: `/bathData` deve continuar próximo de `send_period`; nenhum
   toque/hold pode ser alongado por HTTP. Registrar `hub_task_stack_min` antes/depois.
4. Entregar a mesma revisão três vezes por perda de Wi-Fi: uma única sequência física e
   `ack_cmd_id` estável.
5. Enviar comando fora da faixa e durante `busy`: o ACK não avança.
6. Iniciar OTA durante sequência: relés abrem, tarefa de rede deixa de iniciar requests e o
   nó reinicia atendendo `/status`.

Este gate valida o enlace e a concorrência no hardware; os cenários U/V do host-sim validam
apenas o parser/idempotência.
