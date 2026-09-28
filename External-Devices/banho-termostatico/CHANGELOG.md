# Changelog — Banho termostático

## 2026-09-28 — validação na bancada (documentação)

- Firmware de 2026-09-26 recompilado (sem mudança de código; mesmo tamanho) e enviado por OTA.
- Aprovados:
  - setpoint com hold e toques pelo `bath_app.py` e pelo app Android;
  - leitura do display sem leitura ignorada;
  - guarda armado por toque, com reversão 12–16 s após soltar;
  - gesto `▲`+`▼` em 1 s.
- Gates: G3b, G7 e G7b aprovados; G6, G8 e G9 parciais. Tabela em `docs/VALIDATION.md`.
- `docs/CURRENT_STATUS.md` reescrito com a conclusão, a configuração em uso, a montagem final e
  o que falta. Histórico de 24–26/09 mantido. `docs/MONTAGEM_ETAPAS.md` com as etapas 2 e 3
  aprovadas.

## 2026-09-26 — proteções da leitura do display

- Incidente: com o ponto decimal não lido, o SP 23,4 foi lido como 234; `sp 25.0` planejou 2100
  passos de **descida** e manteve `▼` até o `abort` do operador.
- `disp_decimals` (padrão 1): leitura sem o ponto na casa esperada é inválida.
- `display_sp` fora de `sp_min`–`sp_max` é inválido (estável e ao vivo).
- Hold: salto maior que 20 passos entre leituras solta a tecla (`salto na leitura do display`).
- Gesto `▲`+`▼` para trocar o modo: `mode_hold_ms` padrão 3000 → **1000** (pedido do operador);
  README, PROTOCOL e o padrão do app Flutter atualizados.
- Modo automático avalia o display a cada `guard_check_ms` (novo, padrão **10000**; pedido do
  operador). Reversão de uma mudança manual passa a levar 10–20 s.
- **Guarda só arma com toque manual** em `▲`/`▼` (sensoriamento) e confirma o mesmo valor em
  duas avaliações antes de corrigir; desvio sem toque vai para `guard_ignored` e o `/log`.
  Motivo: o SP oscilou 35,0 ↔ 34,9 com o guarda corrigindo leituras ruins. `/status` ganha
  `guard_armed` e `guard_ignored`. Entrar no `auto` pelo gesto já arma.
- **Registro de eventos** `GET /log` (últimas 64 linhas de `[SP]`, `[GUARD]`, `[SENSE]`,
  `[CMD]`, com `millis`) e `bath_app.py log`: o nó montado não tem serial.
- Simulação: `guard_check_ms = 100` nos cenários; novos T10 (ruído ignorado) e T11 (padrão de
  10 s); T9 com sensoriamento.
- Compilado (ESP32 core 3.3.11, `esp32:esp32:esp32s3`): 1 108 105 B de flash (84%), 67 312 B de
  RAM (20%; +18,6 kB do registro de eventos e do buffer de `/log`). Simulação no PC: todos os
  cenários passam. `Test-FirmwareBaselines` e `Test-HubDeviceContracts` passam.

## 2026-09-26 — leitor do display por janelas de `2DISP` (modo 1)

- `DisplayReader`: novo modo `disp_mode = 1` (padrão). A ISR de `2DISP` marca o início de cada
  banco; um timer de hardware de 100 µs amostra os 8 segmentos; cada janela de `disp_slot_us`
  (1023 µs) é decidida por maioria, descartando 180 µs em cada divisa. Janelas 0–3 = dígitos da
  direita para a esquerda; janela 4 = LEDs (`/display` → `leds`). O banco do SP é
  `disp_sp_bank`. Modo 0 (linhas de dígito) mantido.
- `disp_seg_low` passa a 0 por padrão (segmento aceso em HIGH com 20 kΩ em série, sem divisor).
- Novas chaves `disp_mode`, `disp_slot_us`, `disp_sp_bank` (NVS, `/config`, `bath_app.py`).
- Base: captura de 2026-09-26 12:40 com `A`/`B` ligados por 20 kΩ (`docs/CURRENT_STATUS.md`).
- Compilado (ESP32 core 3.3.11, `esp32:esp32:esp32s3`): 1 106 557 B de flash (84%), 48 720 B de RAM (14%).

## 2026-09-26 — mapa da placa CPU do C404 (documentação)

- `HARDWARE.md` §3: placa `C504 - CPU` (PIC16F76, 24LC16B, LM324, HEF4051, Q9–Q13, resistores
  de 100 Ω dos segmentos, 1,5 kΩ e 10 kΩ das teclas), ordem do CN2 e resultados do `/scope`
  (pad do segmento aceso em ~2 V por estar do lado do LED; ~1 ms por dígito).
- Decisão: ler segmentos e dígitos no lado do PIC, divisor 3,3 kΩ + 6,8 kΩ; `2DISP` no pad.
  `WIRING.md` §3 e `MONTAGEM_ETAPAS.md` §2.4 (tabelas de continuidade a preencher).

## 2026-09-26 — capturas do display e osciloscópio pelo ADC

- Auto-repetição das setas medida (passo imediato, ~0,5 s, ~10 passos/s); simulação no PC com
  `repeatDelayMs = 500`. Padrões de hold do firmware já compatíveis, sem mudança.
- Capturas do display: só `2DISP` é lógico (~108 Hz); segmentos e `1A…1D` ficam em níveis
  intermediários e não são lidos pelo GPIO via divisor (análise em `docs/CURRENT_STATUS.md`).
- Nova rota `GET /scope?pin=&n=&us=` (ADC1, GPIO 1–10) e `bath_app.py scope <gpio> [n] [us]`,
  com mínimo/média/máximo e histograma, e a tensão estimada no C404 pelo divisor.
- Compilado (ESP32 core 3.3.11, `esp32:esp32:esp32s3`): 1 098 953 B de flash (83%), 48 608 B de RAM (14%).

## 2026-09-25 — etapa 2: mapa do display e analisador lógico

- Medição DC dos pontos do display registrada em `docs/MONTAGEM_ETAPAS.md` §2.1: os 14 pontos
  comutam; segmentos provavelmente ativos em HIGH; `G` e `C` fora do padrão esperado.
- Mapa adotado como hipótese: `1A…1D` → GPIO 16/17/18/21 (dígitos), `2DISP` → 38 (banco),
  `1L` → 39 (nova `BoardConfig::DisplayLedLinePin`, só para captura).
- Nova rota `GET /capture?n=&us=`: o ESP32 amostra as 14 linhas como analisador lógico
  (`displayCapture`). `bath_app.py capture [n] [us]` salva o JSON e imprime duty, bordas e
  frequência por linha.
- Sem CD74HC4050 disponível: display por divisores 3,3 k/5,1 k (14 linhas), com o ESP32
  sempre ligado antes do C404.
- Docs: WIRING §3 (tabela com `1A…1D`, `2DISP`, `1L`), PROTOCOL §1,
  MONTAGEM_ETAPAS §2.1/§2.3.
- Compilado (ESP32 core 3.3.11, `esp32:esp32:esp32s3`): 1 087 505 B de flash (82%), 48 360 B de RAM (14%).

## 2026-09-25 — relé N no `CHN`

- Mapa dos relés refeito para a fiação direta nos pontos `CH`: IN1/GPIO 4 → relé 1 → `CH1`
  (`*`), IN2/GPIO 5 → `CH2` (`ENTER`), IN3/GPIO 6 → `CH3` (`▲`), IN4/GPIO 7 → `CH4` (`▼`).
  Antes: `▲` = GPIO 5, `▼` = GPIO 6, `ENTER` = GPIO 7.
- `FirmwareTag` identifica o mapa (`relays H, map CH1-4=relay1-4`); `FirmwareVersion`
  (`r3.2`, anunciado ao Hub) inalterado.
- Simulação no PC referencia os relés pelo nome (`BoardConfig::Relay*Pin`), não pelo número.
- Docs: WIRING §2, HARDWARE §2, MONTAGEM_ETAPAS (tabela de mapa e teste 1.1b), CURRENT_STATUS.
- Compilado (ESP32 core 3.3.11, `esp32:esp32:esp32s3`): 1 085 721 B de flash (82%), 48 344 B de RAM (14%).

## 2026-09-24 — bancada: `CH1…CH4` são as teclas (documentação)

- Mapa da placa do C404 corrigido em `docs/HARDWARE.md` §3 e `docs/WIRING.md` §2.3/§3:
  `CH1…CH4` são as linhas das quatro teclas, confirmado por continuidade; as linhas de
  dígito do display ficam "a identificar" (candidatas `1A…1D`), com tabela de medição.
- Mapa medido: `CH1 = *`, `CH2 = ENTER`, `CH3 = ▲`, `CH4 = ▼`; outro terminal das teclas no
  +5 V do C404, `CH` com pull-down. Teclas ativas em HIGH.
- Incidente registrado: os divisores de "dígito" ligados em `CH1…CH4` não eram dígitos (o
  display nunca foi lido); com o ESP32 desligado, os divisores restantes deformavam o visor
  superior. Os toques fantasmas (SP mudando, menus) vinham das chaves táteis soldadas nos
  terminais, que travavam após um toque (dano térmico).
- Relés passam a ser soldados nos pontos `CH` (`NO`) e no +5 V das teclas (`COM`).
- CD74HC4050 passa a ser a opção recomendada para o display.
- Firmware: sensoriamento das teclas passa a ativo em HIGH (`BoardConfig::SenseActiveHigh`,
  linhas sem fio com pull-down); a simulação no PC segue a mesma constante. Pinos inalterados.
- Firmware: polaridade dos relés em `BoardConfig::RelayActiveLow` (padrão `true`, jumpers `L`);
  `false` para jumpers `H` se o HW-280 não soltar com 3,3 V. Simulação no PC segue as duas
  constantes (todos os cenários passam; stub ganhou `INPUT_PULLDOWN`).
- Relés passam a **ativos em HIGH** (`RelayActiveLow = false`, jumpers do HW-280 em `H`): no
  modo `L` os LEDs ficavam acesos fracos em repouso e o relé não soltava. Docs (WIRING §2,
  HARDWARE §2, MONTAGEM_ETAPAS 1.1) atualizados; simulação no PC com verificações de relé
  independentes da polaridade.
- Roteiro de remontagem por etapas: `docs/MONTAGEM_ETAPAS.md` (etapa 1: só relés, 5 fios,
  sem GND comum).
- Compilado (ESP32 core 3.3.11, `esp32:esp32:esp32s3`): 1 085 689 B de flash (82%),
  48 344 B de RAM (14%).

## 2026-09-22 — posse do Hub, parada e recusa publicada (r3.2)

- Posse do Hub: com `X-Hub-Owner: 1` na resposta do `/bathData` (validade de 10 s), a API
  local recusa com `409 hub_owned` tudo exceto `abort`/`stop`; `/status` publica `hub_owned`
  e `hub_owner_age_ms`; a página `/ui` avisa quando o Hub controla.
- Nova ação `stop` (abort + modo manual persistido) usada pela parada do Hub.
- Push publica `rej_cmd_id`/`rej_err` (recusa de comando do Hub) e `sp_min`/`sp_max`;
  campos de texto codificados para URL.
- `403`/`404` do Hub refazem o `nodeHello` no ciclo seguinte (antes: até 30 s).
- Pilha da tarefa `HubLink` 6 → 7 kB; anúncio `ver=r3.2`.
- Simulação no PC: cenários W (recusa publicada), X (posse) e Y (stop).

## 2026-09-22 — compatibilidade e cadência integrada (r3.1)

- Anúncio ao Hub passa a `ver=r3.1`.
- Com `hub_enabled=1`, o push efetivo fica limitado a 2 s para permanecer dentro da
  janela de validade de 5 s do Hub, independentemente do período escolhido para uso local.
- Reconexão Wi-Fi e reboot pós-OTA usam comparações seguras no rollover de `millis()`.
- API local permanece compatível; app Flutter reconhece revisões r3.x.

## 2026-09-21 — enlace assíncrono e contrato do Hub (r3)

- HTTP do Hub movido para `network/HubLink`: tarefa FreeRTOS própria no core 0, snapshot
  protegido e fila fixa de comandos. O parser e os relés permanecem no loop principal; timeout
  de rede não alonga toque/hold.
- Push periódico continua durante `running/settling`, usa `/bathData` e anuncia
  `dev=bath&ver=r3&mac=<MAC real>`.
- Push r3 publica `state`, `phase`, `err`, PV/SP do display com flags de validade,
  `sp_source`, modo/guarda/desvio e `ack_cmd_id`.
- `/diag` publica `hub_task_stack_min`; `hub_enabled` continua 0 por padrão até a integração.
- Host-sim passou a compilar `ConfigCodec.cpp` e cobre três reentregas do mesmo `cmd_id`
  (uma sequência) e comando recusado sem avanço do ACK: 34 cenários aprovados.
- Compilado com ESP32 core 3.3.11: 1 083 125 B de flash (82%) e 48 248 B de RAM (14%).
- Integração do Hub/Windows App continua não implementada; planos individuais em
  `../docs/Planos/IMPLEMENTATION_PLAN_BANHO_HUB.md` e
  `../docs/Planos/IMPLEMENTATION_PLAN_BANHO_WINDOWS_APP.md`.

## 2026-09-19 — tecla mantida em malha fechada (r2)

- `SetpointManager`: no modo display, uma distância ≥ `hold_min_steps` é vencida mantendo
  `▲`/`▼` fechada enquanto o display é lido ao vivo; solta a `hold_stop_steps` mais a
  compensação `taxa medida × hold_lag_ms`, reavalia após `hold_settle_ms` (até três holds)
  e termina a toques discretos. Display ilegível por 300 ms ou parado por `hold_stall_ms`
  soltam a tecla e desligam o hold na sequência. Um `sp_mismatch` de até 20 toques na
  verificação final dispara uma correção automática antes de virar erro.
- `KeyPresser`: passo de hold (relé fechado até `keypadRelease()` ou um teto por passo);
  contadores zerados por `keypadResetCounters()` no início de cada sequência.
- `DisplayReader`: quadro coerente (duas varreduras iguais) copiado na ISR e `displayLiveSp()`
  sem o filtro de ~300 ms; `GET /display` ganha `sp_live` e `live_frames`.
- Protocolo (aditivo): `hold_enabled`, `hold_min_steps`, `hold_stop_steps`, `hold_lag_ms`,
  `hold_settle_ms`, `hold_stall_ms` em `/config`; `seq_phase`, `hold_ms`, `hold_rounds`,
  `hold_rate` em `/status`; `{"key":…,"hold_ms":…}` para medir a auto-repetição na bancada;
  resposta de `setpoint`/`delta` passa a trazer `presses` = distância em toques de seta e
  `hold`. Chaves novas na NVS (`hold_*`), com padrões para quem vem do r1.
- `bath_app.py` e `/ui`: campos de configuração do hold, botões de tecla mantida, fase e
  taxa na linha de progresso; CLI `hold <tecla> <ms>`.
- Modos `manual`/`auto` (`SetpointGuard`): `sp_target` passa a ser o último SP comandado,
  persistido; `deviation_c` reporta `display − alvo`. No modo `auto` (só com display) o guarda
  reverte mudanças manuais depois de `guard_delay_ms` com o painel parado e as teclas soltas,
  suspende-se após 3 falhas ou um abort, e rearma com qualquer comando de setpoint. Comando
  `{"mode":…}`, gesto `▲`+`▼` por `mode_hold_ms` (sensoriamento), LED de modo no GPIO 40
  (aceso = `auto`; WIRING.md §5) e botão opcional (`ModeButtonPin`, desligado). Telemetria: `mode`, `guard`, `deviation_c`,
  `guard_corrections`, `arrows_held_ms`; push ao Hub ganha `mode`, `dev`, `dev_ok`.
- Sensoriamento das teclas: linhas em `INPUT` sem pull-up (com o divisor, o pull-up interno
  levantava o "pressionada" a ~1,9 V) e `sense_mask` para ler só as linhas ligadas (padrão:
  `▲`/`▼`, tomadas nos bornes `NO` dos relés 2 e 3 — WIRING.md §3b). `g_manualActivityMs`
  segue a tecla mantida, não só o início do toque.
- `tests/host-sim/`: simulação no PC do C404 (auto-repetição, aceleração, toques perdidos,
  display ilegível, cauda, operador nas teclas) compilando os fontes reais do firmware;
  30 cenários passam.
- Compilado (ESP32 core 3.3.11): 1 081 485 B de flash (82%), 48 088 B de RAM (14%).
- Modo sombra e `home` inalterados (mesma contagem de toques); manual do C404 confirmado:
  as setas ajustam o SP direto na tela principal e manter `*` volta à tela principal.

## 2026-09-18 — criação do dispositivo (r1)

- Firmware `thermostatic-bath` para ESP32-S3 no padrão dos demais dispositivos externos
  (AP+STA, OTA por `/update`, NVS, parser JSON manual, watchdog de 15 s).
- Motor de toques não bloqueante para os quatro relés do HW-280 (`*`, `▲`, `▼`, `ENTER`),
  um relé por vez, relés abertos desde o boot.
- Setpoint-sombra persistido com marca de sequência em curso; `sync_sp`, `delta`,
  `setpoint`, `home` (saturação em `in.L`), toques crus e `abort`.
- Leitor do display multiplexado do C404 (8 segmentos + 4 dígitos + `2DISP`) com decodificação
  7 segmentos, filtro de estabilidade e verificação da sequência (`sp_source = 1`).
- Sensoriamento opcional das teclas para detectar intervenção manual e confirmar toques.
- Aplicativo Python de bancada (Tkinter, sem dependências) com CLI equivalente; página `/ui`
  embarcada para celular.
- Registrado em `tools/Compile-ExternalDevices.ps1`, `tools/Publish-OtaFirmware.ps1` e no
  catálogo do `updater_app` como `bath` (AP `192.168.8.1`).
- Compilado (ESP32 core 3.3.11): 1 073 425 B de flash (81%), 47 904 B de RAM (14%).
- Hub e aplicativo principal inalterados; integração descrita em `docs/PROTOCOL.md` §6.
