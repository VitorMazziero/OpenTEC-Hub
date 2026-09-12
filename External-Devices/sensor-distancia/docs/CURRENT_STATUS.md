# Estado atual — Sensor de distância

- Firmware ativo: `firmware/distance-sensor` (v10)
- Compilação: aprovada em 2026-09-11 com ESP32 core 3.3.11
- Contrato com o Hub: preservado estaticamente; consulte `PROTOCOL.md`
- Baseline: preservado por SHA-256 em `archive/active-baseline`
- Bancada/hardware: **pendente**

O sentinela `-1` e os tempos de presença/intertravamento são semântica de segurança e precisam permanecer separados.

Este arquivo descreve o que está demonstrado hoje. Build não comprova sensores, atuadores, temporização real, Wi-Fi, persistência ou segurança física.
