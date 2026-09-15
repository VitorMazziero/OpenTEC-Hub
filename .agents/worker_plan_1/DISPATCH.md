# Task Dispatch: Implementation Plan Worker (Fluxômetro F01-F16)

## Objective
Author the authoritative implementation plan document `IMPLEMENTATION_PLAN_FLUXOMETRO.md` at the project workspace root (`d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\IMPLEMENTATION_PLAN_FLUXOMETRO.md`), based on the exhaustive exploration findings from:
1. `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\spec_miner_survey_1\handoff.md`
2. `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_firmware_1\handoff.md`
3. `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_hubapp_1\handoff.md`
4. `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\External-Devices\docs\COMANDOS_DISPOSITIVOS_EXTERNOS.md` (Section 3.10)

Also author and run the verification script `verify_plan.py` at the project root (`d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\verify_plan.py`) to confirm all IDs from F01 to F16 are verified.

IMPORTANT: Do NOT modify any other production codebase files. This task is purely analytical and documentary.

## Mandatory Structure for IMPLEMENTATION_PLAN_FLUXOMETRO.md
For EACH of the 16 inconsistencies (F01 to F16):
- **Section / ID**: Explicit header with ID (e.g. `### F01 — Identidade V10 no build/OTA e v11 no protocolo/endpoints`)
- **Stated Problem & Root Cause**: Detailed description of the inconsistency, root cause in firmware/Hub/App with exact file paths and line numbers.
- **Cross-System Impact**: How Firmware, Hub, and App interact and are affected.
- **Technical Action Decision**: Either:
  1. A concrete, technical implementation plan detailing what code needs to change, in which files, exact function names, and code logic / diff snippets.
  2. OR a clear technical justification for why the item should not be fixed at this time (e.g. hardware limitation, deferred redesign, intentional decoupling).
- **Feasibility & Priority**: (e.g. Low/Medium/High/Critical, breaking change risk).

Also include:
- Executive Summary & System Architecture overview.
- Implementation Roadmap / Execution Phases (e.g. Phase 1: High Safety & Immediate Fixes; Phase 2: Protocol Harmonization; Phase 3: Hardware Diagnostics & Telemetry).
- Risk Matrix & Backward Compatibility Analysis.

## Acceptance Criteria
1. `IMPLEMENTATION_PLAN_FLUXOMETRO.md` exists at the workspace root.
2. `verify_plan.py` exists at workspace root and executes successfully with exit code 0, verifying that all IDs from F01 to F16 are present and properly documented.

## 2026-09-13T01:40:00Z
Received dispatch instruction to author IMPLEMENTATION_PLAN_FLUXOMETRO.md and verify_plan.py.

