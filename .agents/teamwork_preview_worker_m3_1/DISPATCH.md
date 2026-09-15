## 2026-09-13T16:54:13Z
Read ORIGINAL_REQUEST.md at d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\ORIGINAL_REQUEST.md before starting.
Your working directory is d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\teamwork_preview_worker_m3_1.
You are Worker M3_1 assigned to Milestone 3: ESP32S3-HUB Implementation & Unit/Integration Tests.

Exclusive Write Ownership:
- ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h
- ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h
- ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h
- ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h
- ESP32S3-HUB/tests/contracts/test_node_commands.py

Context & Tasks:
- Read PROJECT.md at d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\PROJECT.md
- Read IMPLEMENTATION_PLAN_BIOMASSA.md (Section 3: B01, B03, B12; Section 5: Fase 2)
- Read survey_hub.md at d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\teamwork_preview_explorer_m1_2\survey_hub.md

Implement the following:
1. B01 (Dynamic presence window for Biomass):
   - In AppContext.h: Add helper:
     inline unsigned long biomassPresenceWindowMs(int probePeriodMs) {
         if (probePeriodMs <= 0) return BIOMASS_TIMEOUT;
         unsigned long dynamicWin = (unsigned long)(probePeriodMs * 2.5f);
         return (dynamicWin > BIOMASS_TIMEOUT) ? dynamicWin : BIOMASS_TIMEOUT;
     }
   - In Telemetry.h:
     - Line ~94: Update biomassEchoSeen to use bioWin = biomassPresenceWindowMs(snapBiomassProbePeriodMs); so echo fields (gear, ema, probeMs) are not suppressed after 10s.
     - Lines ~171-177: Update biomassOnline and validBiomass to check age <= bioWin.
   - In HttpServer.h line ~571:
     - For DEV_BIOMASS: check (now - biomassLastUpdate <= biomassPresenceWindowMs(biomassProbePeriodMs)).
2. B03 (Auto-range routing):
   - In Commands.h line ~466-472:
     Add routing for "biomassAutoRange":
     If value is "auto", enqueue {"command":"auto"}.
     If value is "manual", enqueue {"command":"manual"}.
3. B12 (Cleanup unused test_period):
   - In Commands.h: Remove unused "test_period" key and routing.
   - In ESP32S3-HUB/tests/contracts/test_node_commands.py:
     - Update contract tests to verify biomassAutoRange routing and removal of test_period.
4. Run Verification Commands:
   - python -m unittest discover -s ESP32S3-HUB/tests/contracts/
   - powershell -ExecutionPolicy Bypass -File External-Devices/tools/Test-HubDeviceContracts.ps1
   - python verify_plan_biomassa.py
   - dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"
- Document diffs and test command outputs in your handoff.md.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_preview_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Report back via send_message when complete.
