# Original User Request

## Initial Request — 2026-09-13T01:30:00Z

# Teamwork Project Prompt — Draft

> Status: Step 9 — Assemble and Validate (Ready for user approval)
> Goal: Craft prompt → get user approval → delegate to teamwork_preview
> Requested team: [none — teamwork routes from the description]

Evaluate the hardware/software inconsistencies in the external devices documentation (specifically Section 3.10) and generate an implementation plan to resolve them, explaining any decisions to not implement certain fixes. This task is purely analytical and documentary; do not modify the codebase.

Working directory: d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL
Integrity mode: development

## Requirements

### R1. Analyze Inconsistencies
Analyze the 16 inconsistencies (F01 to F16) listed in Section 3.10 of `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`. Cross-reference these items with the actual codebase (firmware, Hub, and App) to determine their root causes and the feasibility of fixing them. Focus specifically on the Fluxômetro module.

### R2. Generate Implementation Plan Document
Create a document named `IMPLEMENTATION_PLAN_FLUXOMETRO.md` in the working directory. For each of the 16 items, you must either:
1. Provide a concrete, technical plan of what code needs to change (and where) to fix it.
2. Or, provide a clear technical justification for why the item should not be fixed at this time.

## Acceptance Criteria

### Completeness
- [ ] The file `IMPLEMENTATION_PLAN_FLUXOMETRO.md` exists in the working directory.
- [ ] A python script (`verify_plan.py`) can successfully read `IMPLEMENTATION_PLAN_FLUXOMETRO.md` and confirm that all IDs from F01 to F16 are explicitly mentioned as sections or list items.

## Follow-up — 2026-09-13T01:41:01Z

# Teamwork Project Prompt — Draft

> Status: Launched
> Goal: Task delegated to teamwork_preview
> Requested team: small, focused team

This is a single self-contained fix; keep it small and focused.
Auditar o firmware do sensor de distância (v11) (fontes: `ConfigCodec.cpp`, `FirmwareApp.cpp`, `PROTOCOL.md`), identificar/explicar inconsistências e preencher a seção 2 do arquivo `COMANDOS_DISPOSITIVOS_EXTERNOS.md`. Além disso, gerar um plano de implementação para corrigir as inconsistências identificadas.

Working directory: d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL
Integrity mode: development

## Requirements

### R1. Atualizar Documentação
Preencher a seção 2 do `COMANDOS_DISPOSITIVOS_EXTERNOS.md` descrevendo o comportamento real do código do Sensor de Distância. Manter a mesma anatomia (tabelas de hardware, catálogo de chaves, recuperação, telemetria e limitações) usada nos outros dispositivos (ex: bomba peristáltica).

### R2. Análise de Inconsistências
Explicar claramente na documentação as inconsistências entre o firmware atual (ConfigCodec.cpp, FirmwareApp.cpp) e o contrato (PROTOCOL.md). Se algo não foi implementado no código (ex: deduplicação de cmd_id, NVS só em mudança), documentar a decisão e a justificativa técnica.

### R3. Plano de Correção
Criar um artefato separado de plano de implementação listando as mudanças necessárias no código-fonte (se houver) para corrigir os desvios entre o que está implementado e o que é esperado no contrato.

## Acceptance Criteria

### Completude e Verificação
- [ ] O arquivo `COMANDOS_DISPOSITIVOS_EXTERNOS.md` tem sua seção 2 preenchida seguindo estritamente os mesmos tópicos numéricos (2.0 a 2.11) do dispositivo 1.
- [ ] Todas as chaves mapeadas pelo Hub (`distanceOffsetMm`, `distanceSamplePeriodMs`, `distanceSendPeriodMs`, `distanceResetNvs`) estão explicadas em tabelas.
- [ ] O tratamento de falha do VL53L0X (push distance=-1, escada de recuperação) está documentado.
- [ ] Há um checklist claro gerado para ensaios em bancada física.
- [ ] O revisor humano pode cruzar as explicações da documentação com o código listado e confirmar sua veracidade.

## Follow-up — 2026-09-13T11:16:14Z

# Teamwork Project Prompt — Draft

> Status: Step 9 — Assemble and Validate (Ready for user approval)
> Goal: Craft prompt → get user approval → delegate to teamwork_preview
> Requested team: [none — teamwork routes from the description]

Evaluate the hardware/software inconsistencies, limitations, and pending checklist items in the external devices documentation (specifically Section 1. Bomba peristáltica externa, paragraphs 1.10 and 1.11). Generate an implementation plan to resolve them, explaining any decisions to not implement certain fixes. This task is purely analytical and documentary; do not modify the codebase.

Working directory: d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL
Integrity mode: development

## Requirements

### R1. Analyze Limitations and Pending Items
Analyze the items listed in Section 1.10 (Limitações, riscos e decisões) and Section 1.11 (Checklist de bancada) of `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`. Cross-reference these items with the actual codebase (firmware, Hub, and App) to determine their current status, root causes, and the feasibility of fixing or completing them.

### R2. Generate Implementation Plan Document
Create a document named `IMPLEMENTATION_PLAN_BOMBA.md` in the working directory. For each item in the audit and checklist, you must either:
1. Provide a concrete, technical plan of what code needs to change (and where) to fix/implement it.
2. Or, provide a clear technical justification for why the item should not be fixed or implemented at this time.

## Acceptance Criteria

### Completeness
- [ ] The file `IMPLEMENTATION_PLAN_BOMBA.md` exists in the working directory.
- [ ] A python script (`verify_plan_bomba.py`) can successfully read `IMPLEMENTATION_PLAN_BOMBA.md` and confirm that all items from §1.10 and §1.11 are explicitly mentioned as sections or list items.

## Follow-up — 2026-09-13T16:22:36Z

# Teamwork Project Prompt — Draft

> Status: Step 9 — Assemble and Validate (Ready for user approval)
> Goal: Craft prompt → get user approval → delegate to teamwork_preview
> Requested team: [none — teamwork routes from the description]

Evaluate the hardware/software inconsistencies for the Biomass Sensor (Section 4.10 of COMANDOS_DISPOSITIVOS_EXTERNOS.md) and generate an implementation plan. After defining the plan, apply the code changes across the firmware, Hub, and apps. Finally, update the documentation and commit the changes separated by component.

Working directory: d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL
Integrity mode: development

## Requirements

### R1. Evaluate Inconsistencies and Generate Plan
Analyze Section 4.10 (Lacunas, Inconsistências e Decisões) of `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`. Generate a document named `IMPLEMENTATION_PLAN_BIOMASSA.md` detailing the technical plan to resolve the inconsistencies (B01 to B13) or providing justifications for items that will not be implemented.

### R2. Implement Code Changes
Apply the necessary code changes derived from the implementation plan across the affected components: Biomass firmware, Hub (ESP32S3-HUB), and Apps (Python and OpenTECHub/Windows).

### R3. Update Documentation
Update `HUB_PROTOCOL_IMPROVEMENTS.md` and `COMANDOS_DISPOSITIVOS_EXTERNOS.md` to reflect the newly implemented changes, maintaining the standard table format under "🔵 Diretrizes de Segurança e Decisões de Arquitetura Fechadas" (similar to what was done for the other devices).

### R4. Commit Changes
Create distinct git commits separating the changes by component: one commit for firmware changes, one for Hub changes, and one for Apps.

## Acceptance Criteria

### Verification (Agent-as-Judge)
- [ ] An independent agent judge must review `IMPLEMENTATION_PLAN_BIOMASSA.md` to confirm all items from §4.10 are addressed with a code plan or a clear justification.
- [ ] The agent judge must verify that the codebase modifications accurately reflect the proposed plan.
- [ ] The git history (`git log`) must reflect separated commits for firmware, Hub, and apps.
- [ ] Both documentation files (`HUB_PROTOCOL_IMPROVEMENTS.md` and `COMANDOS_DISPOSITIVOS_EXTERNOS.md`) must contain the updated architectural decision tables for the Biomass Sensor.



