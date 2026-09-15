# Task Dispatch: Implementation Plan Worker (Iteration 2 - Remediation Update)

## Objective
Update `IMPLEMENTATION_PLAN_FLUXOMETRO.md` at the project workspace root (`d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\IMPLEMENTATION_PLAN_FLUXOMETRO.md`) to integrate the verified remediation designs for F04, F06, F07, F08, F09, F12, and F14 from:
1. `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_remedy_1_it2\handoff.md` (F04, F06, F12, F14)
2. `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_remedy_2_it2\handoff.md` (F09)
3. `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_remedy_3_it2\handoff.md` (F07, F08)

And ensure `python verify_plan.py` runs and passes with exit code 0.

IMPORTANT: Do NOT modify any other production codebase files.

## Specific Updates to Apply:
1. **F04**:
   - Correct variable identifier in `Lifecycle.h`: change `integral_term = 0.0f;` to `integralError = 0.0f;`.
   - Preserve `dacHold` behavior: only call `writeFlowSetpointToDAC(0.0f)` if `!dacHold`. When `dacHold == true`, actuate mechanical cutoff (`valveFlowState = 1; digitalWrite(VALVE_FLOW_PIN, HIGH);`) but retain DAC analog voltage and ramped target.
   - Add finitude validation (`!isnan(newTarget) && !isinf(newTarget)`).
2. **F06**:
   - Enforce unconditional closure of cutoff valve (`stagedVFlow = 1`) whenever all route valves are closed (`stagedV1 == 0 && stagedV2 == 0`), eliminating dead-end pressurized manifold regardless of setpoint.
   - Handle partial command frames by initializing staged variables with actual hardware state (`valve1State`, `valve2State`, `valveFlowState`).
3. **F07**:
   - Set calibration polynomial float validation bounds to `[-1.0e7f, 1.0e7f]` to accommodate real quartic Horner coefficients ($A_1 \approx -1.35 \times 10^6, B_1 \approx 2.47 \times 10^5, K_1 \approx -1.65 \times 10^4$) and steep fits without false rejection.
   - Clamp PI gains ($[0, 100]$), feedforward ($ff\_gain \in [0, 10]$, $ff\_offset \in [-5, 5]$ accommodating negative opening bias), and ramp rate ($[0, 100]$).
4. **F08**:
   - Replace `atoi()` boolean conversion with case-insensitive `parseJsonBool` supporting `"true"`, `"false"`, `"True"`, `"False"`, `"TRUE"`, `"FALSE"`, `"1"`, `"0"`.
   - Incorporate two-phase transactional staging (`StagedCommands`) committed atomically under `commandMutex`.
5. **F09**:
   - Add explicit migration for `CALIBRATION_MAGIC_V5` (`0xCAFEBAC3`) in `CalibrationStore.h`, preserving user calibration coefficients (`a1..c2`), tuning, and initializing `calParams.max_flow = 50.0f;`.
   - Explicitly initialize `calParams.max_flow = 50.0f;` in the default/reset fallback block.
   - Add `maxFlowRate = calParams.max_flow;` at the end of `loadParameters()`.
   - Add defensive clamping against division-by-zero or negative infinity in `FlowIo.h`.
   - Update Hub `HttpServer.h` to re-assert `pendingMaxFlow = true` on reboot detection.
6. **F12**:
   - Implement continuous hardware fault latch (`!adsHealthy || !dacHealthy`) preventing `valveFlowState = 0` and gating PI loop execution in `Lifecycle.h`.
7. **F14**:
   - Protect OTA Safe Stop mutations with `xSemaphoreTake(commandMutex, portMAX_DELAY)`.
   - Ensure an OTA watchdog stall or failure latches safe stop persistently (`otaSafeLatch`), preventing automatic reactivation of previous high flow commands.

Write your handoff report to `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\worker_plan_2\handoff.md`.
