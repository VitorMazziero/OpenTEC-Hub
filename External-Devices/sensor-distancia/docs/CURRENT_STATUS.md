# Estado atual — Sensor de distância

- Firmware ativo: `firmware/distance-sensor` (v11 — `DistanceClient r11`)
- Compilação: aprovada em 2026-09-13 com ESP32 core 3.3.11 (1 100 796 B flash, 83%; 50 680 B RAM)
- Auditoria D01–D09 aplicada em 2026-09-13 (`CHANGELOG.md`); escada de recuperação I²C e desacoplamento de laços pendentes de bancada
- Contrato com o Hub: preservado estaticamente; consulte `PROTOCOL.md`
- Baseline: preservado por SHA-256 em `archive/active-baseline`
- Bancada/hardware: **pendente**

O sentinela `-1` e os tempos de presença/intertravamento são semântica de segurança e precisam permanecer separados.

Este arquivo descreve o que está demonstrado hoje. Build não comprova sensores, atuadores, temporização real, Wi-Fi, persistência ou segurança física.
