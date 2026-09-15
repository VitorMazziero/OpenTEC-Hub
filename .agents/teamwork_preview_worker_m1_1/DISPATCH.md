## 2026-09-13T16:31:15Z

Read ORIGINAL_REQUEST.md at d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\ORIGINAL_REQUEST.md before starting.
Your working directory is d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\teamwork_preview_worker_m1_1.
You are Worker M1_1 assigned to Milestone 1: Technical Implementation Plan for Biomass Sensor (B01 to B13/B15).

Write Ownership:
You exclusively own:
- d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\IMPLEMENTATION_PLAN_BIOMASSA.md
- d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\verify_plan_biomassa.py

Context & Inputs:
- Read PROJECT.md at d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\PROJECT.md
- Read Explorer reports:
  - Firmware survey: d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\teamwork_preview_explorer_m1_1\survey_firmware.md
  - Hub survey: d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\teamwork_preview_explorer_m1_2\survey_hub.md
  - Apps/Docs survey: d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\teamwork_preview_explorer_m1_3\survey_apps_docs.md
- Reference existing plans and verifiers:
  - IMPLEMENTATION_PLAN_FLUXOMETRO.md and verify_plan.py
  - IMPLEMENTATION_PLAN_BOMBA.md and verify_plan_bomba.py

Tasks:
1. Generate IMPLEMENTATION_PLAN_BIOMASSA.md at project root:
   - Must follow the exact 6-section structure of previous plans:
     1. Sumário Executivo
     2. Arquitetura do Sistema e Topologia de Comunicação
     3. Análise Técnica Detalhada das Lacunas e Inconsistências (B01 a B13, plus B14-B15)
     4. Matriz de Compatibilidade e Interoperabilidade
     5. Roteiro de Implementação em Fases (Firmware, Hub, Apps, Documentação, Validação)
     6. Critérios de Aceitação e Plano de Testes
   - For every single item from B01 to B13 (and B14-B15): provide either a concrete, line-by-line technical plan of what code needs to change (files, functions, logic) OR a clear technical justification for why the item is closed as an architectural decision/safety boundary.
2. Create verify_plan_biomassa.py at project root:
   - Must verify that IMPLEMENTATION_PLAN_BIOMASSA.md exists and contains all IDs B01 to B13 (and B14-B15) as section titles or list items.
   - Must verify presence of the 6 core macro sections.
   - Run `python verify_plan_biomassa.py` using powershell command and confirm it passes with Exit Code 0.
3. Document verification command output in your handoff.md.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_preview_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.
