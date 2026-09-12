# Estado atual — Fluxômetro

- Firmware ativo: `firmware/flowmeter` (v10)
- Compilação: aprovada em 2026-09-11 com ESP32 core 3.3.11
- Contrato com o Hub: preservado estaticamente; consulte `PROTOCOL.md`
- Baseline: preservado por SHA-256 em `archive/active-baseline`
- Bancada/hardware: **pendente**

O FQBN preserva opções da compilação importada. Calibração, válvulas, OTA e concorrência entre comandos Hub/diretos exigem bancada.

Este arquivo descreve o que está demonstrado hoje. Build não comprova sensores, atuadores, temporização real, Wi-Fi, persistência ou segurança física.
