# Task Dispatch: Explorer 1 (Iteration 2 - Control Loop & Safety Remediation)

## Objective
Analyze the feedback from Challenger 2 (`d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\challenger_2\handoff.md`) and design the exact technical remediation strategy for:
1. **F04**: Correct variable identifier in `Lifecycle.h` (`integralError` instead of `integral_term`), preserve `dacHold` behavior, handle setpoint finitude.
2. **F06**: Enforce unconditional closure of general cutoff valve (`stagedVFlow = 1`) when `stagedV1 == 0 && stagedV2 == 0`, preventing dead-end pressurized manifold under any setpoint condition.
3. **F12**: Continuous hardware fault latching (`!adsHealthy || !dacHealthy`) blocking `valveFlowState = 0` and gating PI loop execution in `Lifecycle.h`.
4. **F14**: Mutex synchronization (`commandMutex`) during OTA safe stop, and safe latch on OTA watchdog stall.

Write your recommended fix strategy to `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_remedy_1_it2\handoff.md`.

## 2026-09-13T01:48:25Z
You are a teamwork_preview_explorer investigating remediation for F04, F06, F12, and F14.
Your working directory is: d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_remedy_1_it2
Read d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\ORIGINAL_REQUEST.md first before doing anything else.
Read your dispatch file at: d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_remedy_1_it2\DISPATCH.md
Read the challenger feedback at: d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\challenger_2\handoff.md

Design the exact technical code diff recommendations for:
- F04: Lifecycle.h variable naming (integralError), dacHold logic, setpoint finitude
- F06: Unconditional cutoff closure on zero routes (stagedV1==0 && stagedV2==0)
- F12: Continuous hardware health latch in Lifecycle.h
- F14: commandMutex protection and OTA watchdog stall safe latch

Write your report to: d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_remedy_1_it2\handoff.md
When done, message your parent with the report path.
