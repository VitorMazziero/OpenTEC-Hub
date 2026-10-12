# Estado atual — Bomba peristáltica

- Firmware ativo: `firmware/peristaltic-pump` (3.13 — contrato de integração com o Hub 10.9; binários e SHA-256 da 3.12.1 e da 3.13 em `tests/evidence`)
- Compilação: aprovada em 2026-10-11 com ESP32 core 3.3.11 (1 111 207 B, 84%)
- Testes: contrato estático (`tests/*.py`) e simulação no PC dos cabeçalhos puros (`tests/host-sim/build.cmd`)
- Motor: DC escovado em ponte H de PWM duplo (confirmado pelo operador em 2026-09-12); o NEMA 17 do CAD é só referência mecânica
- Contrato com o Hub: preservado estaticamente; consulte `PROTOCOL.md`
- Baseline: preservado por SHA-256 em `archive/active-baseline`
- Bancada/hardware: **pendente** — roteiro e registro dos portões G0–G3 em `BANCADA_3.13.md`

A modularização mantém uma única unidade de tradução para preservar globais e inicialização. O perfil de dosagem e a parada física precisam de bancada.

Este arquivo descreve o que está demonstrado hoje. Build não comprova sensores, atuadores, temporização real, Wi-Fi, persistência ou segurança física.
