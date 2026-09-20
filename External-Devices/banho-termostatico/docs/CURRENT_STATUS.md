# Estado atual — Banho termostático

- Firmware ativo: `firmware/thermostatic-bath` (r2 — `BathClient r2`)
- Compilação: aprovada em 2026-09-19 com ESP32 core 3.3.11, FQBN `esp32:esp32:esp32s3`
  (1 081 485 B flash, 82%; 48 088 B RAM, 14%)
- Lógica de setpoint (hold em malha fechada, toques, correção, abort) e de modos (guarda,
  gesto, suspensão): 30 cenários passam em `tests/host-sim/` (fontes reais do firmware contra
  um C404 simulado, MSVC). Os parâmetros do C404 no simulador (0,6 s até repetir, 10 toques/s,
  "▲ vence" com as duas setas) são hipóteses até G3b/G7b
- Montagem (2026-09-19): relés, display e fonte ligados; sensoriamento de `▲`/`▼` a ligar nos
  bornes `NO` dos relés 2 e 3 → GPIO 2/42 (WIRING.md §3b, com a conferência de nível antes de
  ligar ao ESP32). `sense_mask = 6` já é o padrão; falta `sense_enabled = 1` após a conferência
- Aplicativos de bancada (HTTP direto, sem o Hub):
  - `apps/desktop-python/bath_app.py` testado contra um servidor HTTP simulado (CLI e janela);
    não testado contra o dispositivo
  - `apps/flutter` (`bath_app`, Android): quatro abas (Operação, Modos, Bancada, Config),
    `cmd_id` com reentrega, traço de 10 min, diagnóstico. `flutter analyze` limpo e `flutter test`
    (modelos com fixtures do PROTOCOL.md + serviço contra um `HttpServer` falso) passam;
    **não testado contra o dispositivo** (gates G1–G9 a reproduzir pelo app na bancada)
- Hub: sem integração (`hub_enabled = 0`); nenhuma alteração no Hub ou no aplicativo principal.
  Plano: `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO.md` (banho como segunda via do setpoint de
  temperatura; exige o push em tarefa própria, r3). Aplicativo Android próprio do nó:
  `../../docs/Planos/IMPLEMENTATION_PLAN_BANHO_APP_ANDROID.md`
- Bancada/hardware: **pendente** — gates G1–G8 em `VALIDATION.md`, nenhum executado
- Display: leitor implementado a partir dos pontos identificados na placa (`2DISP, A…G, PD,
  CH1…CH4`); polaridade, fase e ordem dos dígitos são hipóteses até G6
- Alimentação: não usar o ponto `5VA` do C404; fonte 5 V dedicada (`HARDWARE.md` §1)

Decisões abertas que a bancada resolve: `enter_key` (o operador usa `*` antes das setas; o
manual §7.1 diz que as setas agem direto na tela principal), `confirm_key` (`ENTER` grava,
segundo o operador), `step_c` (0,1 °C por toque pelo relato "20 → 21 = 10 toques"),
comportamento em `in.L` (habilita ou não o `home`), a auto-repetição das setas (G3b):
atraso, taxa, aceleração e cauda após soltar dimensionam `hold_min_steps`, `hold_stop_steps`,
`hold_lag_ms` e `hold_stall_ms`, e o que o C404 faz com `▲`+`▼` juntas (G7b: decide se o gesto
de modo pelo painel serve). O manual (`docs/Manual_C304,_C404,_C407,_C409_view.pdf`) não
documenta a taxa nem o par de setas; só que manter `*` volta à tela principal e que o bloqueio
`loC` travaria também os relés.

Este arquivo descreve o que está demonstrado hoje. Build não comprova relés, temporização,
display, Wi-Fi, persistência ou segurança física.
