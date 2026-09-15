## 2026-09-13T01:59:33Z
# Task Dispatch: Challenger 2 (Iteration 2 - Re-Testing of 5 Challenged Items)

## Objective
Re-challenge and stress-test `IMPLEMENTATION_PLAN_FLUXOMETRO.md` specifically regarding the 5 critical edge-case findings from Iteration 1:
1. **F04**: Check that `integralError` is used (not `integral_term`), that `dacHold` contract is strictly respected (retaining DAC voltage and ramped target under mechanical cutoff), and that finitude checks (`!isnan && !isinf`) are present.
2. **F06**: Check that cutoff valve is unconditionally closed (`stagedVFlow = 1`) when `stagedV1 == 0 && stagedV2 == 0` regardless of setpoint, eliminating pressurized dead-end manifold, and that partial commands initialize stages from hardware.
3. **F07 / F08**: Check that calibration float bounds are extended to `[-1.0e7f, 1.0e7f]` accommodating real quartic terms ($A_1 \approx -1.35 \times 10^6$), and that boolean parsing is case-insensitive (`parseJsonBool` with `strcasecmp`) with 2-phase transactional staging.
4. **F09**: Check that EEPROM schema v6 includes explicit migration for `CALIBRATION_MAGIC_V5` (`0xCAFEBAC3`), that fallback/reset initializes `calParams.max_flow = 50.0f;`, that `loadParameters()` assigns `maxFlowRate = calParams.max_flow;`, and that `FlowIo.h` guards against division-by-zero or negative floats.
5. **F12 / F14**: Check continuous hardware fault latch in F12 (`!adsHealthy || !dacHealthy`), and in F14 check `commandMutex` synchronization and persistent latch (`otaSafeLatch`) on watchdog stall.

Also run `python verify_plan.py` and confirm `git status`.

Write your findings and explicit verdict (`APPROVE` or `REQUEST_CHANGES`) to:
`d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\challenger_2_it2\handoff.md`
