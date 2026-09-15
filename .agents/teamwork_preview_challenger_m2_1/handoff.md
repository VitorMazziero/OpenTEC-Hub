# Empirical Challenge Handoff Report — Milestone 2: Biomass Firmware Verification

## 1. Observation

### Direct Observations from Source Code
1. **File `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h`**:
   - Lines 78-96 (`setManualGear`):
     ```cpp
     void setManualGear(int itIndex, int pwmIndex) {
       if (itIndex < 0 || itIndex >= g_config.IT_COUNT ||
           pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) {
         Serial.println("Error: gear index out of range.");
         return;
       }
       vemlSetConfig(itIndex);
       pwmSetLevel(pwmIndex);
       pwmSetDutyPercent(0.0f);
       g_autoRange = false;
       g_prefs.putBool(NVS_KEY_AUTO, false);
       enforceRefreshFloor(true);
       g_nextReadTime = millis() + currentRefreshFloor();
       ...
     ```
     *Verification*: `pwmSetDutyPercent(0.0f)` executes unconditionally directly following `pwmSetLevel(pwmIndex)`. The legacy condition `if (g_state == IDLE)` is removed. `g_autoRange` is set to `false` and persisted. `g_nextReadTime` is rescheduled by `currentRefreshFloor()`.
   - Lines 73-76 (`currentRefreshFloor`):
     ```cpp
     inline uint32_t currentRefreshFloor() {
       uint32_t floorMs = minSafeRefreshMs();
       return (g_config.itRefreshTimes[g_currentItIndex] > floorMs) ? g_config.itRefreshTimes[g_currentItIndex] : floorMs;
     }
     ```
   - Lines 223-242 (`cmd.equals("start")`):
     ```cpp
     } else if (cmd.equals("start")) {
       if (!g_blankIsDone)
         Serial.println("Error: Please run 'blank' first.");
       else if (g_state == IDLE) {
         Serial.println("--- Starting Measurement ---");

         int startIt = g_currentItIndex;
         int startPwm = g_currentPwmIndex;
         if (g_autoRange || !blankIsValid(startIt, startPwm)) {
           findOptimalBlankGear(startIt, startPwm); // Smart Start
         }

         g_state        = MEASURING;
         g_nextReadTime = millis(); // Start first read immediately
         vemlSetConfig(startIt);
         pwmSetLevel(startPwm);

         g_consecutiveGearSearches = 0;
         g_highDensityMode         = false;
       }
     ```
     *Verification*: `findOptimalBlankGear` is executed only if `g_autoRange == true` OR `!blankIsValid(startIt, startPwm)`. When in manual mode with a valid blank, `startIt` and `startPwm` preserve operator gear choice.
   - Lines 416-485 (Numeric parameters & coalesced NVS persistence):
     ```cpp
     bool configModified = false;
     ...
     val = getJsonValue(json, "low");
     if (val != -999999) {
       if (g_config.LOW_THRESHOLD_RAW != (uint16_t)val) {
         g_config.LOW_THRESHOLD_RAW = (uint16_t)val;
         configModified = true;
       }
       ...
     }
     ...
     if (configModified) {
       saveConfig();
     }
     ```
     *Verification*: `configModified` triggers `saveConfig()` only when values differ from RAM. Multiple fields in one payload trigger a single save. Identical values do not trigger `saveConfig()`.

2. **File `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/FirmwareApp.cpp` and `BlankingAndRange.h`**:
   - `LED_DUTY_LIMIT = 0.08f` (8.0% duty limit constant).
   - `IT_MS = {25, 50, 100, 200, 400, 800}` ms.
   - `integrationGuardMs(itMs) = (uint32_t)(1.20f * (float)itMs) + 8`.
   - `ledOnMsFor(itMs) = 10 + 2 * integrationGuardMs(itMs)`.
   - `minSafeRefreshMs() = (uint32_t)(ledOnMsFor(itMs) / LED_DUTY_LIMIT)`.

### Direct Tool Execution Results
- **Command**: `python External-Devices/sensor-biomassa/tests/test_firmware_m2_empirical.py`
  - Output: `Ran 21 tests in 0.003s - OK`
  - Tests covering AST structure, zero-duty guarantee, duty cycle limits across all 6 gears, NVS coalescing, wear-leveling, start state transitions (auto, manual valid, manual invalid fallback, unblanked, busy, and re-entrant start).
- **Command**: `python verify_plan_biomassa.py`
  - Output: `Taxa de Conformidade: 100.0%`, all items B01 to B15 approved with metrological rigor.
- **Command**: `python External-Devices/sensor-biomassa/tests/test_verify_plan_biomassa_adversarial.py`
  - Output: `Ran 15 tests in 0.315s - OK`.
- **Command**: `python -m unittest discover -s ESP32S3-HUB/tests/contracts/`
  - Output: `Ran 83 tests in 0.018s - OK`.
- **Command**: `dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"`
  - Output: `Aprovado! - Com falha: 0, Aprovado: 60, Total: 60`.

---

## 2. Logic Chain

### 2.1 setManualGear() 0.0f Duty Guarantee
1. In the legacy firmware, `setManualGear()` executed `pwmSetLevel(pwmIndex)`, energizing the LED to `pwmSettings[pwmIndex]`, followed by `if (g_state == IDLE) pwmSetDutyPercent(0.0f);`.
2. When called during `g_state == MEASURING`, the condition failed and the LED stayed illuminated continuously between reads, creating a thermal hazard.
3. In the updated `CommandCodec.h` (Observation 1), `pwmSetDutyPercent(0.0f)` is called unconditionally directly after `pwmSetLevel(pwmIndex)`.
4. Immediately following duty zeroing, `g_nextReadTime = millis() + currentRefreshFloor()` guarantees that the next pulsed acquisition cannot occur before the required dark cooling period has elapsed.
5. In empirical simulation (`test_setManualGear_during_MEASURING_guarantees_zero_duty`), calling `setManualGear` during an active measurement immediately resets duty to 0.0% and delays next execution by `currentRefreshFloor()`.

### 2.2 Duty Cycle $\le$ 8% Ceiling Across All IT Gears
1. The duty cycle is defined as `duty = ledOnMsFor(itMs) / refresh_ms`.
2. For all valid integration times $IT \in \{25, 50, 100, 200, 400, 800\}$ ms:
   - $IT = 25$ ms: `guard` = 38 ms, `ledOn` = 86 ms, `floor` = $\lfloor 86 / 0.08 \rfloor$ = 1075 ms $\rightarrow$ `duty` = $86 / 1075 = 0.080000$ (8.000%)
   - $IT = 50$ ms: `guard` = 68 ms, `ledOn` = 146 ms, `floor` = $\lfloor 146 / 0.08 \rfloor$ = 1825 ms $\rightarrow$ `duty` = $146 / 1825 = 0.080000$ (8.000%)
   - $IT = 100$ ms: `guard` = 128 ms, `ledOn` = 266 ms, `floor` = $\lfloor 266 / 0.08 \rfloor$ = 3325 ms $\rightarrow$ `duty` = $266 / 3325 = 0.080000$ (8.000%)
   - $IT = 200$ ms: `guard` = 248 ms, `ledOn` = 506 ms, `floor` = $\lfloor 506 / 0.08 \rfloor$ = 6325 ms $\rightarrow$ `duty` = $506 / 6325 = 0.080000$ (8.000%)
   - $IT = 400$ ms: `guard` = 488 ms, `ledOn` = 986 ms, `floor` = $\lfloor 986 / 0.08 \rfloor$ = 12325 ms $\rightarrow$ `duty` = $986 / 12325 = 0.080000$ (8.000%)
   - $IT = 800$ ms: `guard` = 968 ms, `ledOn` = 1946 ms, `floor` = $\lfloor 1946 / 0.08 \rfloor$ = 24325 ms $\rightarrow$ `duty` = $1946 / 24325 = 0.080000$ (8.000%)
3. Because `ledOnMs` is always an even integer ($10 + 2 \times \text{guard}$), `ledOnMs / 0.08 = ledOnMs * 12.5` is an exact integer with no truncation error.
4. In Auto Mode, `itMs` is evaluated as $\max(IT) = 800$ ms, enforcing a global floor of 24325 ms. Any faster gear chosen by the auto-ranger produces duty strictly $< 8.000\%$ (e.g. $86 / 24325 = 0.35\%$).
5. Clamping logic in `refresh_ms` and `probe_period` ensures any requested value $< \text{floorMs}$ is clamped to $\text{floorMs}$.
6. Empirical test `test_duty_cycle_across_all_it_gears_manual_mode` and `test_refresh_clamping_enforces_safety` verify $\text{duty} \le 0.080000$ in all cases.

### 2.3 Coalesced NVS `saveConfig()` and Loop Avoidance
1. In `processJsonCommand`, `configModified` is initialized to `false`.
2. When parsing `low`, `high`, `opt`, and `refresh_ms`, each field compares the parsed value against RAM. `configModified` is set to `true` only if at least one value changed.
3. At line 483, `if (configModified) saveConfig();` executes once at the conclusion of the numeric parameter checks.
4. If a payload contains 4 modified fields, exactly one write occurs. If a payload contains redundant values, 0 writes occur (protecting flash endurance).
5. `saveConfig()` in `Stores.h` executes CRC32 and `g_prefs.putBytes()`. It contains no loops, no recursive calls, and does not invoke command dispatchers.
6. Stress test `test_coalesced_saveConfig_stress_loop` executed 1000 successive writes in 0.003s with no infinite loops or deadlocks.

### 2.4 Start Command State Transitions: Manual vs Auto Mode
1. In Auto Mode (`g_autoRange == true`), the condition `(g_autoRange || !blankIsValid(startIt, startPwm))` evaluates to `true`. `findOptimalBlankGear(startIt, startPwm)` ("Smart Start") executes, and state transitions to `MEASURING`.
2. In Manual Mode (`g_autoRange == false`) with a valid blank (`blankIsValid(startIt, startPwm) == true`), the condition evaluates to `false`. `findOptimalBlankGear` is skipped, preserving the operator's manual gear. State transitions to `MEASURING`.
3. In Manual Mode with an invalid blank (`blankIsValid == false`), the condition evaluates to `true`. `findOptimalBlankGear` triggers as safety fallback, ensuring acquisition does not begin on an uncalibrated gear. State transitions to `MEASURING`.
4. If `!g_blankIsDone`, the command is rejected with an error; state remains `IDLE`.
5. If already `MEASURING`, `start` does not re-prime or clobber state.

---

## 3. Caveats

1. **Threshold Ordering Validation**:
   - `CommandCodec.h` independently assigns `LOW_THRESHOLD_RAW` and `HIGH_THRESHOLD_RAW` from JSON without asserting `LOW_THRESHOLD_RAW < HIGH_THRESHOLD_RAW`. While not part of Milestone 2 scope, an operator sending `{"low": 50000, "high": 1000}` would invert the auto-range hysteretic band. A sanity check in a future milestone is recommended.
2. **Flash Endurance Testing**:
   - Simulation validated 1000 rapid cycles. Real ESP32 SPI flash endurance (typically ~100,000 erase/write cycles) is effectively protected by the idempotency guard `if (RAM != val)`, but applications should avoid periodic polling loops that continuously send differing values.

---

## 4. Conclusion

**Verdict: EXPLICIT CONFIRMATION / VERDICT: PASS (APPROVED)**

All Milestone 2 firmware modifications meet specifications with zero defects:
- **`setManualGear()` duty guarantee**: Confirmed. Setting a manual gear unconditionally forces `pwmSetDutyPercent(0.0f)` and reschedules `g_nextReadTime` by `currentRefreshFloor()`, preventing thermal damage during `MEASURING`.
- **8% Duty Cycle Ceiling**: Confirmed. Across all 6 IT gears (25 ms to 800 ms) and Auto Mode, average LED duty cycle never exceeds 8.0000%. User-configured intervals below the safe threshold are clamped.
- **Coalesced NVS Persistence**: Confirmed. Multi-field JSON updates trigger exactly one atomic `saveConfig()`, redundant updates are filtered to avoid flash wear, and no recursion or infinite loops exist.
- **Start Command Transitions**: Confirmed. Manual gears with valid blanks are preserved verbatim, Auto Mode properly executes Smart Start, and invalid blanks safely fall back to optimal calibrated gears.

---

## 5. Verification Method

To independently verify this evaluation, execute the following commands from the repository root (`d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL`):

1. **Run Empirical Challenge Test Suite (Challenger M2_1)**:
   ```powershell
   python External-Devices/sensor-biomassa/tests/test_firmware_m2_empirical.py
   ```
   *Expected Result*: `Ran 21 tests in 0.003s - OK`.

2. **Verify Implementation Plan Conformity**:
   ```powershell
   python verify_plan_biomassa.py
   ```
   *Expected Result*: `Exit Code 0`, 100% compliance across all 15 items.

3. **Verify Plan Adversarial Rigor**:
   ```powershell
   python External-Devices/sensor-biomassa/tests/test_verify_plan_biomassa_adversarial.py
   ```
   *Expected Result*: `Ran 15 tests - OK`.

4. **Verify Hub Contract Tests**:
   ```powershell
   python -m unittest discover -s ESP32S3-HUB/tests/contracts/
   ```
   *Expected Result*: `Ran 83 tests - OK`.

5. **Verify Windows App Biomass Integration Tests**:
   ```powershell
   dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"
   ```
   *Expected Result*: `60 passed, 0 failed`.

### Invalidation Conditions
- Any occurrence of `pwmSetDutyPercent` failing to execute 0.0f in `setManualGear`.
- Any duty cycle calculation exceeding $0.08000$ for any IT gear.
- Any multi-variable JSON frame triggering multiple `saveConfig()` calls or recursion.
- Any override of manual gear selection during `start` when `blankIsValid()` is true.
