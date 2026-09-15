# Orchestrator Plan

## Objective
Analyze Section 3.10 of `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` (inconsistencies F01 to F16 regarding Fluxômetro) across the codebase (Firmware, Hub, App), produce a comprehensive implementation plan in `IMPLEMENTATION_PLAN_FLUXOMETRO.md`, and verify with `verify_plan.py`.

## Phases & Steps
1. **Survey / Exploration (Iteration 1)**
   - Dispatch 3 Explorers:
     - Explorer 1: Document Mining & Specification Analysis (Extract F01-F16 from COMANDOS_DISPOSITIVOS_EXTERNOS.md, check definitions, protocol, specs).
     - Explorer 2: Firmware & Embedded Architecture (Trace Fluxômetro commands in firmware repos/files, root causes for F01-F16).
     - Explorer 3: Hub & App Integration (Trace Fluxômetro commands in Hub backend and App frontend/clients, root causes for F01-F16).
   - Synthesize explorer findings into a comprehensive matrix of F01-F16 with root cause, feasibility, affected components, and recommended approach.

2. **Drafting Implementation Plan (Iteration 1 - Worker)**
   - Dispatch Worker:
     - Read synthesized exploration reports and COMANDOS_DISPOSITIVOS_EXTERNOS.md.
     - Author `IMPLEMENTATION_PLAN_FLUXOMETRO.md` at project root with:
       - Detailed technical breakdown for each F01-F16 item (root cause, affected files/functions, code changes, or justification for no-fix).
       - Impact analysis across Firmware, Hub, and App.
       - Architectural recommendations.
     - Author and run `verify_plan.py` to confirm that all F01 to F16 IDs are present and verified.

3. **Review & Gate Verification (Iteration 1 - Reviewers, Challengers, Auditor)**
   - Dispatch 2 Reviewers independently to check technical depth, accuracy against codebase, and criteria satisfaction.
   - Dispatch 2 Challengers to verify completeness, consistency, and run verification script.
   - Dispatch 1 Forensic Auditor (`teamwork_preview_auditor`) for integrity audit (ensure no fake/dummy content, authentic technical details).
   - Gate evaluation: verify all pass before completion.

4. **Reporting**
   - Synthesize handoff report and notify Sentinel.
