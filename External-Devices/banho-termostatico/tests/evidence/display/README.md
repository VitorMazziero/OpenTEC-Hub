# Evidências — leitura do display do C404 (2026-09-26)

Arquivos gerados por `bath_app.py capture` (analisador lógico, `GET /capture`) e
`bath_app.py scope` (ADC, `GET /scope`). Análise em `docs/CURRENT_STATUS.md` e
`docs/HARDWARE.md` §3.

| Arquivo | Montagem | Painel | O que mostrou |
|---|---|---|---|
| `capture_20260926_105352.json` | divisores 3,3 k/5,1 k nos pads, 2000 × 10 µs | 21,5 / 21,5 | só `2DISP` comuta (~108 Hz); segmentos e `1A…1D` em LOW |
| `capture_20260926_110009.json` | idem, 8000 × 2 µs | 21,5 / 21,5 | idem: não são pulsos curtos |
| `capture_20260926_110021.json` | idem, 8000 × 100 µs | 21,5 / 21,5 | idem: não é varredura lenta |
| `capture_20260926_110050.json` | idem, 8000 × 100 µs, `▲` mantida | SP mudando | idem |
| `scope_gpio8/9/10_20260926_1115*.json` | idem, ADC em `A`/`B`/`C` | 21,5 / 21,5 | aceso ≈ 1,5–2,4 V no pad (lado do LED); janelas de ~1 ms |
| `scope_gpio8_20260926_124012.json` | `A` por 20 kΩ série, sem divisor | 23,6 / 23,6 | aceso 2,0–2,5 V no GPIO |
| `capture_20260926_124013.json` | `A`/`B` por 20 kΩ, 8000 × 20 µs | 23,6 / 23,6 | janelas de ~1 ms lidas; `A B` = `10 11 11 00` (+`01` LEDs) = `6 3 2 branco`, direita → esquerda |
