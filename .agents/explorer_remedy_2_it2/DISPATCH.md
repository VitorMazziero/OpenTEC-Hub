# Task Dispatch: Explorer 2 (Iteration 2 - EEPROM Schema & Persistence Remediation)

## Objective
Analyze the feedback from Challenger 2 (`d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\challenger_2\handoff.md`) regarding **F09** (EEPROM schema migration and `max_flow` persistence).
Design the exact technical remediation strategy:
1. Migration logic in `CalibrationStore.h` for upgrading from `CALIBRATION_MAGIC_V5` (`0xCAFEBAC3`) to V6, preserving existing calibration parameters (`a1..c2`, `kp`, `ki`, `ff_*`, `ramp_rate`, `dac_hold`) while initializing `calParams.max_flow = 50.0f;`.
2. Explicit initialization of `calParams.max_flow = 50.0f;` in the default/reset fallback block of `CalibrationStore.h`.
3. Ensuring `loadParameters()` assigns `maxFlowRate = calParams.max_flow;`.
4. Preventing division-by-zero or negative infinity in `FlowIo.h:2` if `maxFlowRate <= 0.01f`.

Write your recommended fix strategy to `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_remedy_2_it2\handoff.md`.
