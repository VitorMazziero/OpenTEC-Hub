# Estado atual — Frasco agitador

- Firmware ativo: `firmware/flask-agitator` (rev H)
- Compilação: aprovada em 2026-09-11 com ESP32 core 3.3.11
- Contrato com o Hub: preservado estaticamente; consulte `PROTOCOL.md`
- Baseline: preservado por SHA-256 em `archive/active-baseline`
- Bancada/hardware: **pendente**

O comando do Hub e o potenciômetro disputam autoridade conforme `ActivePot`. Parada segura e retomada local precisam de teste físico.

Este arquivo descreve o que está demonstrado hoje. Build não comprova sensores, atuadores, temporização real, Wi-Fi, persistência ou segurança física.
