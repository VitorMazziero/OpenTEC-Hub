## 2026-09-13T16:43:04Z

Read ORIGINAL_REQUEST.md at d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\ORIGINAL_REQUEST.md before starting.
Your working directory is d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\teamwork_preview_worker_m2_1.
You are Worker M2_1 assigned to Milestone 2: Biomass Sensor Firmware Implementation.

Exclusive Write Ownership:
- External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h
- External-Devices/sensor-biomassa/firmware/biomass-sensor/src/api/LocalHttpApi.h

Context & Instructions:
- Read PROJECT.md at d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\PROJECT.md
- Read IMPLEMENTATION_PLAN_BIOMASSA.md (Section 3: B04, B05, B07, B03, B02; and Section 5: Fase 1)
- Incorporate critical edge-case guidance from reviews:
  1. B04 (Critical thermal fix): In CommandCodec.h setManualGear():
     - Unconditionally turn off LED: pwmSetDutyPercent(0.0f);
     - Set g_autoRange = false;
     - Call enforceRefreshFloor(true);
     - Reschedule g_nextReadTime = millis() + currentRefreshFloor();
  2. B05 (NVS persistence): In CommandCodec.h lines ~260-279 and ~403-453:
     - When probe_period / probe_ms / refresh_ms, low, high, opt are modified, call saveConfig();
     - Ensure refresh_ms maintains thermal clamping against minSafeRefreshMs();
     - Ensure opt uses g_config.OPTIMAL_TARGET_RAW.
  3. B07 (Version tag): In LocalHttpApi.h line 80:
     - Update PROGMEM HTML string to "Biomass Sensor Firmware v11.0".
  4. B03 (Manual gear preservation in start): In CommandCodec.h line ~221:
     - In handleStartCommand / start command: only execute findOptimalBlankGear() if (g_autoRange || !blankIsValid(g_currentItIndex, g_currentPwmIndex)). If manual mode and blank is valid, preserve existing gear!

Verification & Testing:
- Run powershell command: python External-Devices/sensor-biomassa/apps/desktop-python/pc_client/tests/check_firmware.py
- Run powershell command: python verify_plan_biomassa.py
- Verify no compilation/syntax breaks or unbalanced braces.
- Document exact diffs and test command outputs in your handoff.md.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A teamwork_preview_auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

Report back via send_message when complete.
