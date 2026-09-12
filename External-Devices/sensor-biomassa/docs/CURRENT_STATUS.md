# Estado atual — Sensor de biomassa

- Firmware ativo: `firmware/biomass-sensor` (v5.3)
- Compilação: aprovada em 2026-09-11 com ESP32 core 3.3.11
- Contrato com o Hub: preservado estaticamente; consulte `PROTOCOL.md`
- Baseline: preservado por SHA-256 em `archive/active-baseline`
- Bancada/hardware: **pendente**

Blanking e aquisição são operações sensíveis e parcialmente bloqueantes. A UI humana está em `web/index.html`; `web_ui.h` é gerado.

Este arquivo descreve o que está demonstrado hoje. Build não comprova sensores, atuadores, temporização real, Wi-Fi, persistência ou segurança física.
