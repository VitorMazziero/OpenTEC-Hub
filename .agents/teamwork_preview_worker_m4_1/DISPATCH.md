## 2026-09-13T16:59:50Z
Read ORIGINAL_REQUEST.md at d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\ORIGINAL_REQUEST.md before starting.
Your working directory is d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\teamwork_preview_worker_m4_1.
You are Worker M4_1 assigned to Milestone 4: Windows App Implementation & Tests (B03, B06, B09, B13).

Exclusive Write Ownership:
- Files under Windows_app/src/ and Windows_app/tests/OpenTECHub.Tests/

Context & Inputs:
- Read PROJECT.md at d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\PROJECT.md
- Read IMPLEMENTATION_PLAN_BIOMASSA.md (Section 3: B03, B06, B09, B13; Section 5: Fase 3)
- Read survey_apps_docs.md at d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\teamwork_preview_explorer_m1_3\survey_apps_docs.md
- CRITICAL ARCHITECTURAL ADVICE (from Challenger 2):
  In AlarmService.cs for B06: Maintain a state flag _biomassAcquisitionActiveWhenLastSeen (or similar). Only trigger the silent-reboot / sampling-stalled alarm if the sensor was actively acquiring samples before becoming silent. Do NOT raise an alarm when the sensor is intentionally in IDLE (e.g. before start or after stop).

Tasks:
1. B03 (AutoRange UI & Command Keys):
   - In CommandKeys.cs: add `public const string BiomassAutoRange = "biomassAutoRange";`
   - In CommandBuilders.cs: add `public static Dictionary<string, object> BiomassAutoRange(string mode)`
   - In BiomassControlViewModel.cs: add auto-range toggle / command so user can switch between auto and manual, sending "auto" or "manual".
2. B06 (Supervision Alarm):
   - In AlarmService.cs: add check detecting if Biomass sensor was actively acquiring but fresh samples have ceased for > 2.5 * probe_ms (or ~65s) while still online, signaling a brownout reboot to IDLE.
3. B09 (Recipe Blank Timeout):
   - In RecipeEngine.ExternalDevices.cs (and/or RecipeEngine.cs): adjust blank sweep completion timeout from 15s to 60s (real blank sweep takes 20-40s).
   - In RecipeEnums.cs (at Windows_app/src/OpenTECHub/Services/Recipes/RecipeEnums.cs): update comments/descriptions from ~15s to 20-40s.
4. B13 (Sentinels -99.0 and 9.9):
   - In SensorReadings.cs: declare BiomassBlankInvalidSentinel = -99.0f and BiomassZeroLightSentinel = 9.9f.
   - In BiomassControlViewModel.cs: handle absorbance values <= -90.0f (display invalid blank warning) and >= 9.0f (display dark/opaque warning) instead of displaying misleading numbers.
   - In RecipeEngine.Devices.cs (line ~132): ensure recipe step readiness check excludes sentinels (requires valid absorbance not equal to -99.0 and < 9.0).
5. Add/Update Unit Tests:
   - In Windows_app/tests/OpenTECHub.Tests/ (e.g. BiomassPumpTests.cs or new BiomassTests.cs): add unit tests for BiomassAutoRange command builder, sentinel handling, and recipe timeout/readiness.
   - Run `dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"`
   - Run full suite: `dotnet test Windows_app/tests/OpenTECHub.Tests`
- Document diffs and test results in your handoff.md.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_preview_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Report back via send_message when complete.
