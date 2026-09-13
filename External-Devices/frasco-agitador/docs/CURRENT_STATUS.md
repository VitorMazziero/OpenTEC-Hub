# Estado atual — Frasco agitador

- Firmware ativo: `firmware/flask-agitator` (**v10**; baseline histórico rev H)
- Compilação: aprovada em 2026-09-13 com ESP32 core 3.3.11
- Contrato com o Hub: preservado estaticamente; consulte `PROTOCOL.md`
- Baseline: preservado por SHA-256 em `archive/active-baseline`
- Bancada/hardware: **pendente**

O boot é seguro por construção: motor freado, alvo 0 % e potenciômetro bloqueado. O knob só reassume após `ActivePot:1`; inversões passam por zero com rampa. Esses comportamentos foram compilados e cobertos por teste estático, mas ainda precisam de comprovação física.

Este arquivo descreve o que está demonstrado hoje. Build não comprova sensores, atuadores, temporização real, Wi-Fi, persistência ou segurança física.
