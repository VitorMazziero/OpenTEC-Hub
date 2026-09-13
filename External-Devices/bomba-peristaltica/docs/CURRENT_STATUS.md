# Estado atual — Bomba peristáltica

- Firmware ativo: `firmware/peristaltic-pump` (3.10)
- Compilação: aprovada em 2026-09-12 com ESP32 core 3.3.11 (1 096 479 B, 83%)
- Motor: DC escovado em ponte H de PWM duplo (confirmado pelo operador em 2026-09-12); o NEMA 17 do CAD é só referência mecânica
- Contrato com o Hub: preservado estaticamente; consulte `PROTOCOL.md`
- Baseline: preservado por SHA-256 em `archive/active-baseline`
- Bancada/hardware: **pendente**

A modularização mantém uma única unidade de tradução para preservar globais e inicialização. O perfil de dosagem e a parada física precisam de bancada.

Este arquivo descreve o que está demonstrado hoje. Build não comprova sensores, atuadores, temporização real, Wi-Fi, persistência ou segurança física.
