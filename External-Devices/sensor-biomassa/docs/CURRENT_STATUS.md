# Estado atual — Sensor de biomassa

- Firmware ativo: `firmware/biomass-sensor` (v11.1)
- Compilação: aprovada em 2026-09-13 com ESP32 core 3.3.11 (1 129 932 B, 86 %; 69 984 B RAM) — **5,1 kB acima do piso de flash**; nenhuma chave nova sem remedir
- Auditoria B01–B15 aplicada em 2026-09-13 (`CHANGELOG.md`); B02 e B14 abertos por decisão
- Contrato com o Hub: preservado estaticamente; consulte `PROTOCOL.md`
- Baseline: preservado por SHA-256 em `archive/active-baseline`
- Bancada/hardware: **pendente**

Blanking e aquisição são operações sensíveis e parcialmente bloqueantes. A UI humana está em `web/index.html`; `web_ui.h` é gerado.

Este arquivo descreve o que está demonstrado hoje. Build não comprova sensores, atuadores, temporização real, Wi-Fi, persistência ou segurança física.
