# Changelog — Banho termostático

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
