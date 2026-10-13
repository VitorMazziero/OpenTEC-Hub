# Changelog — Fluxômetro

## v12.1 — 2026-10-12

- Consulta de comandos a 2 Hz, telemetria a 2 Hz, contadores de falha separados e recuo de telemetria limitado a 1 s.
- Disputa do mutex não consome o período de envio; presença é anunciada com intervalo mínimo de 5 s na recuperação.
- Reassociação exige falhas de transporte e 15 s sem resposta HTTP de qualquer endpoint; erros HTTP 4xx/5xx não derrubam o Wi-Fi. Rechecagem de OTA, conexão e resposta recente sob mutex.
- Diagnóstico com contadores separados; controle local e schema EEPROM v7 preservados.
- Testes de software e compilação; confirmação física pendente.

## v12.0 — 2026-09-13

- `transition_v` passou a definir a tensão editável que separa os segmentos; `0.0545 V` é somente default e valor de migração.
- Schema EEPROM v7 com 68 bytes e CRC32 cobrindo a transição.
- Parser aceita `transition_v`/`flowTransitionVoltage` apenas com os dois segmentos completos e rejeita descontinuidade de valor ou derivada.
- `/flowData`, `/calibration`, telemetria serial e WebSocket ecoam a transição.
- Integração automatizada aprovada com Hub 10.3 e aplicativo; ensaio metrológico físico permanece pendente.

## Reorganização de 2026-09-11

- Selecionada e nomeada a versão ativa v10.
- Separados firmware, aplicativo, hardware, testes/evidências e histórico.
- Preservado o monólito ativo anterior em `archive/active-baseline`.
- Removidos cabeçalhos extensos e históricos embutidos do código ativo; decisões foram transferidas para Markdown.
- Criada estrutura modular por responsabilidade sem alteração intencional do contrato de fio.
- Compilado no ESP32 core 3.3.11 usando as bibliotecas compartilhadas da máquina de upload.

Validação de hardware permanece pendente.
