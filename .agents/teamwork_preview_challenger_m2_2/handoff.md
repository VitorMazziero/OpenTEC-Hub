# Handoff Report — Challenger M2_2: Firmware Edge-Case Stress Testing

## 1. Observation

### Direct Observations on Codebase
1. **Item 1: LocalHttpApi.h:80 vs Release Version**:
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/api/LocalHttpApi.h:80`:
     ```html
     <p>Running: <b>Biomass Sensor Firmware v11.0</b></p>
     ```
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/FirmwareApp.cpp:25`:
     ```cpp
     static const char* FW_VERSION = "v11";
     ```
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/TelemetryAndHub.h:60`:
     ```cpp
     snprintf(url, sizeof(url), "%s?dev=biomass&ver=v11&mac=%s",
              sensorHubHelloURL.c_str(), WiFi.macAddress().c_str());
     ```
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/api/LocalHttpApi.h:24-30`:
     ```cpp
     snprintf(json, sizeof(json),
              "{\"device\":\"biomass-sensor\",\"version\":\"%s\",\"uptime_s\":%lu,"
              ...
              FW_VERSION,
     ```
   - `Windows_app/src/OpenTECHub/Services/Communication/NodeFirmwareCatalog.cs:39-41`:
     ```csharp
     [Flowmeter] = new(StringComparer.OrdinalIgnoreCase) { "v11", "v11.0" },
     [Biomass] = new(StringComparer.OrdinalIgnoreCase) { "v11" },
     ```
   - `IMPLEMENTATION_PLAN_BIOMASSA.md:521-526` (B07 action plan):
     ```markdown
     1. Em FirmwareApp.cpp: Padronizar FW_VERSION = "v11.0" e FW_NAME = "biomass_sensor".
     2. Em LocalHttpApi.h: Tornar a renderização da versão dinâmica na página Web OTA, injetando FW_VERSION:
        "<p>Running: <b>Biomass Sensor Firmware " + String(FW_VERSION) + "</b></p>"
     ```

2. **Item 2: currentRefreshFloor() Helper Implementation and Call Sites**:
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:73-76`:
     ```cpp
     inline uint32_t currentRefreshFloor() {
       uint32_t floorMs = minSafeRefreshMs();
       return (g_config.itRefreshTimes[g_currentItIndex] > floorMs) ? g_config.itRefreshTimes[g_currentItIndex] : floorMs;
     }
     ```
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:90`:
     ```cpp
     g_nextReadTime = millis() + currentRefreshFloor();
     ```
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/Lifecycle.h:218`:
     ```cpp
     case MEASURING:
       if (now >= g_nextReadTime) {
         runMeasurementLoop(); // This performs one pulsed read
       }
       break;
     ```
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/core/FirmwareApp.cpp:149-164`:
     ```cpp
     struct DeviceConfig {
       uint16_t LOW_THRESHOLD_RAW;
       uint16_t HIGH_THRESHOLD_RAW;
       uint16_t OPTIMAL_TARGET_RAW;

       static const int IT_COUNT = 4;
       uint16_t itSettings[IT_COUNT];      // VEML7700 register values for IT
       uint32_t itDelays[IT_COUNT];        // Wait time in ms for each IT
       uint32_t itRefreshTimes[IT_COUNT];  // Poll interval for each IT

       static const int PWM_COUNT = 8;
       float pwmSettings[PWM_COUNT];       // % duty cycle levels

       uint32_t crc32; // Checksum for config integrity
     };
     ```

3. **Item 3: g_prefs.putBool(NVS_KEY_AUTO, false) Invocation**:
   - `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:87-88`:
     ```cpp
     g_autoRange = false;
     g_prefs.putBool(NVS_KEY_AUTO, false);
     ```
   - Return type of Arduino ESP32 `Preferences::putBool`: `size_t` (returns 1 on success, 0 on failure).
   - ESP-IDF NVS architecture: Log-structured flash storage with four entry state flags: `Empty (0xFFFFFFFF)` -> `Written (0xFFFFFFFE)` -> `Committed (0xFFFFFFFC)` -> `Erased (0xFFFFFFF8)`.

### Executed Tool Commands and Results
- **Command**: `python External-Devices/sensor-biomassa/tests/test_milestone2_edge_cases.py`
  - Output: Ran 4 tests, `OK`.
  - Details:
    - Confirmed `LocalHttpApi.h:80` outputs `"v11.0"`, but `FW_VERSION` is `"v11"` and `NodeFirmwareCatalog` only validates `["v11"]`.
    - Confirmed `currentRefreshFloor()` lacks both lower (`>= 0`) and upper (`< IT_COUNT`) boundary checks.
    - Confirmed `Lifecycle.h:218` unsigned comparison `now >= g_nextReadTime` immediately fires upon `millis()` wrap-around (simulated at `now = 0xFFFFFFFB`, `g_nextReadTime = 19374` -> evaluates to `True`).
    - Confirmed `g_prefs.putBool` return code is completely unhandled and lacks `if (g_autoRange)` flash wear guard.
- **Command**: `python External-Devices/sensor-biomassa/tests/test_firmware_m2_empirical.py`
  - Output: Ran 21 tests, `OK`.
- **Command**: `python verify_plan_biomassa.py`
  - Output: `100.0%` compliance across all 15 items (B01-B15).
- **Command**: `python External-Devices/sensor-biomassa/tests/test_verify_plan_biomassa_adversarial.py`
  - Output: Ran 15 tests, `OK`.
- **Command**: `dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"`
  - Output: 60 passed, 0 failed.
- **Command**: `python -m unittest discover -s ESP32S3-HUB/tests/contracts/`
  - Output: 83 passed, 0 failed.

---

## 2. Logic Chain

### 1. Analysis of Edge Case 1: LocalHttpApi.h:80 String vs Release Version
- *Premise*: The prompt asks: "Check if LocalHttpApi.h:80 string matches release version exactly."
- *Inference Step 1*: Line 80 of `LocalHttpApi.h` reads: `<p>Running: <b>Biomass Sensor Firmware v11.0</b></p>`, which presents the version string as `"v11.0"`.
- *Inference Step 2*: In the firmware source, the canonical version constant is defined in `FirmwareApp.cpp:25` as `static const char* FW_VERSION = "v11";`.
- *Inference Step 3*: The registration handshake sent by the node to the hub via `/nodeHello` (`TelemetryAndHub.h:60`) explicitly hardcodes `?dev=biomass&ver=v11`.
- *Inference Step 4*: The diagnostic JSON endpoint (`LocalHttpApi.h:24-30`) serializes `FW_VERSION`, reporting `"version": "v11"`.
- *Inference Step 5*: The desktop application's official catalog (`NodeFirmwareCatalog.cs:40`) defines `[Biomass] = new(...) { "v11" }`. Unlike the Flowmeter (which accepts both `"v11"` and `"v11.0"`), Biomass strictly accepts only `"v11"`. If a node were to report `"v11.0"`, the app would flag it as unvalidated (`Advisory`).
- *Inference Step 6*: `IMPLEMENTATION_PLAN_BIOMASSA.md` (item B07) planned to update `FirmwareApp.cpp` to `v11.0` and inject `String(FW_VERSION)` dynamically into `LocalHttpApi.h`. Worker M2_1 only modified `LocalHttpApi.h:80` with a static string `"v11.0"`, omitting the corresponding update to `FirmwareApp.cpp:25`.
- *Verdict for Item 1*: **DISCREPANCY (NO EXACT MATCH)**. While `LocalHttpApi.h:80` reflects the planned `"v11.0"` label, it does NOT match the active firmware release constant (`FW_VERSION = "v11"`), the wire protocol (`ver=v11`), or the application validation catalog (`"v11"`).

### 2. Analysis of Edge Case 2: Boundary Checks and Overflow in currentRefreshFloor()
- *Premise*: The prompt asks: "Check if the newly added currentRefreshFloor() helper has proper boundary checks or potential overflow."
- *Inference Step 1 (Boundary Checks)*:
  - `currentRefreshFloor()` directly references `g_config.itRefreshTimes[g_currentItIndex]`.
  - The function contains zero validation that `0 <= g_currentItIndex < g_config.IT_COUNT`.
  - In `struct DeviceConfig`, `itRefreshTimes[4]` is immediately followed by `pwmSettings[8]` (32-bit floats).
  - If `g_currentItIndex == 4`, `itRefreshTimes[4]` aliases with `pwmSettings[0]`. If `pwmSettings[0]` is `100.0f`, reinterpreting its IEEE 754 bit pattern (`0x42C80000`) as `uint32_t` yields $1,120,403,456\text{ ms} \approx 13\text{ days}$.
  - While `setManualGear()` validates `itIndex` prior to calling `vemlSetConfig()`, `currentRefreshFloor()` is declared as a global inline helper without internal defensive guards.
  - *Boundary Checks Verdict*: **MISSING INTERNAL BOUNDS CHECKS** (defensive mitigation required: clamp or return `floorMs` if `g_currentItIndex` is out of `[0, IT_COUNT)`).
- *Inference Step 2 (Arithmetic Overflow)*:
  - Inside `currentRefreshFloor()`, the expression is `(a > b) ? a : b` between `uint32_t` operands.
  - No addition, multiplication, or type narrowing occurs.
  - *Internal Overflow Verdict*: **NO INTERNAL ARITHMETIC OVERFLOW**.
- *Inference Step 3 (Call Site Timer Rollover)*:
  - At line 90: `g_nextReadTime = millis() + currentRefreshFloor()`.
  - When `millis()` reaches $2^{32} - \text{interval}$ (~49.7 days), the addition wraps around modulo $2^{32}$.
  - In `Lifecycle.h:218`, the scheduler checks: `if (now >= g_nextReadTime)`.
  - Under unsigned comparison, when `now` is $\approx 2^{32}-1$ and `g_nextReadTime` wraps to $\approx 20000$, `now >= g_nextReadTime` evaluates to `true` instantly and continuously on every loop cycle until `millis()` itself rolls over.
  - *Call Site Overflow Verdict*: **UNSAFE UNSIGNED ROLLOVER COMPARISON** in `Lifecycle.h:218`.

### 3. Analysis of Edge Case 3: NVS putBool Failure and Corruption Risk
- *Premise*: The prompt asks: "Check if g_prefs.putBool(NVS_KEY_AUTO, false) can fail or cause NVS corruption."
- *Inference Step 1 (Can it fail?)*:
  - `g_prefs.putBool(NVS_KEY_AUTO, false)` calls `nvs_set_u8` and `nvs_commit`.
  - It can fail due to: (a) NVS partition full (`ESP_ERR_NVS_NOT_ENOUGH_SPACE`), (b) uninitialized/closed NVS handle, or (c) flash hardware/write timeout.
  - In `setManualGear()`, the return value is discarded. If `putBool` fails, `g_autoRange` is `false` in RAM but remains `true` in NVS. Upon reboot, the sensor reverts to auto-range.
  - *Failure Verdict*: **YES, CAN FAIL SILENTLY**.
- *Inference Step 2 (Can it cause NVS corruption?)*:
  - ESP-IDF NVS uses a log-structured architecture with atomic state flags.
  - Power cuts or interrupted writes leave an uncommitted entry header. During the subsequent boot or sector garbage collection, uncommitted entries are cleanly ignored and erased.
  - Calling `putBool` cannot corrupt the NVS partition table or damage unrelated keys.
  - *Corruption Verdict*: **NO NVS CORRUPTION POSSIBLE**.
- *Inference Step 3 (Flash Wear Protection)*:
  - `setManualGear()` writes to NVS unconditionally without checking `if (g_autoRange)`.
  - While ESP-IDF skips flash writing when the value in flash matches, checking `if (g_autoRange)` in RAM before calling `putBool` aligns with the project's wear-leveling architecture (as in Distance Sensor §2.7.1).

---

## 3. Caveats

1. **Physical Hardware Bench Execution**: Verification was conducted via static AST analysis, memory layout simulation, unit tests, and empirical python harnesses. Real hardware flash endurance and power-cut brownout tests belong to bench validation (§4.11 / Milestone 6).
2. **Review Scope Restraint**: Per Challenger identity guidelines, no firmware source files were modified to fix these observations. These findings are presented as actionable recommendations for upcoming refactoring passes or Milestone 3/4.

---

## 4. Conclusion

### Explicit Confirmation / Verdict Matrix

| # | Item Under Challenge | Empirical Finding | Verdict | Severity | Recommendation |
|---|----------------------|-------------------|---------|----------|----------------|
| **1** | **`LocalHttpApi.h:80` vs Release Version** | Line 80 hardcodes `"v11.0"`, but `FW_VERSION` (`FirmwareApp.cpp:25`), `/nodeHello` query (`ver=v11`), `/diag`, and `NodeFirmwareCatalog.Biomass` all use `"v11"`. | **DISCREPANCY (NO EXACT MATCH)** | Low (Cosmetic / Catalog) | Harmonize `FW_VERSION = "v11.0"` across `FirmwareApp.cpp`, `nodeHello`, and add `"v11.0"` to `NodeFirmwareCatalog.cs`. |
| **2a**| **`currentRefreshFloor()` Boundary Checks** | Directly indexes `g_config.itRefreshTimes[g_currentItIndex]` without bounds check. An invalid index causes out-of-bounds read into `pwmSettings`. | **VULNERABLE (MISSING BOUNDS CHECK)** | Medium (Defensive Fragility) | Add `if (idx < 0 \|\| idx >= g_config.IT_COUNT) return floorMs;`. |
| **2b**| **`currentRefreshFloor()` Arithmetic Overflow** | Evaluates `(a > b) ? a : b` on `uint32_t`. No arithmetic operations inside helper. | **SAFE (NO INTERNAL OVERFLOW)** | None | Retain implementation. |
| **2c**| **Timer Rollover at Call Site** | Line 90 `millis() + currentRefreshFloor()` paired with `Lifecycle.h:218` `if (now >= g_nextReadTime)` triggers immediately on 49.7d rollover. | **VULNERABLE (ROLLOVER FLAW)** | Medium (Longevity / Uptime) | Update loop comparison to `if ((long)(now - g_nextReadTime) >= 0)` or `(now - lastRead >= interval)`. |
| **3a**| **`g_prefs.putBool` Failure Modes** | Returns `size_t` which is ignored. If NVS is full, fails silently causing RAM/NVS desynchronization upon reboot. | **CAN FAIL SILENTLY** | Low-Medium (Operational) | Check return code or log warning; add `if (g_autoRange)` guard to avoid redundant calls. |
| **3b**| **`g_prefs.putBool` NVS Corruption Risk** | ESP-IDF NVS provides transactional, power-fail-safe two-phase commit. | **SAFE (NO CORRUPTION RISK)** | None | NVS architecture protects filesystem integrity. |

---

## 5. Verification Method

To independently verify these conclusions, execute the following commands from the repository root (`d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL`):

1. **Run Empirical Edge-Case Verification Suite**:
   ```powershell
   python External-Devices/sensor-biomassa/tests/test_milestone2_edge_cases.py
   ```
   *Expected Output*: Ran 4 tests, `OK`. Confirms:
   - `LocalHttpApi.h:80` version string (`v11.0`) vs `FW_VERSION` (`v11`) discrepancy.
   - Missing bounds check in `currentRefreshFloor()` and out-of-bounds aliasing simulation.
   - Premature firing under `millis()` rollover at `Lifecycle.h:218`.
   - Ignored return value of `g_prefs.putBool` and absence of `if (g_autoRange)` guard.

2. **Run Challenger 1 Verification Suite**:
   ```powershell
   python External-Devices/sensor-biomassa/tests/test_firmware_m2_empirical.py
   ```
   *Expected Output*: Ran 21 tests, `OK`.

3. **Verify Plan Compliance & Adversarial Tests**:
   ```powershell
   python verify_plan_biomassa.py
   python External-Devices/sensor-biomassa/tests/test_verify_plan_biomassa_adversarial.py
   ```
   *Expected Output*: Both exit with code 0.

4. **Run Cross-Subsystem Tests**:
   ```powershell
   python -m unittest discover -s ESP32S3-HUB/tests/contracts/
   dotnet test Windows_app/tests/OpenTECHub.Tests --filter "FullyQualifiedName~Biomass"
   ```
   *Expected Output*: All 83 Hub tests and all 60 .NET tests pass.

### Invalidation Conditions
- Updating `FirmwareApp.cpp` to `"v11.0"` and updating `NodeFirmwareCatalog.cs` to include `"v11.0"` would invalidate Finding 1.
- Adding `if (g_currentItIndex < 0 || g_currentItIndex >= g_config.IT_COUNT) return floorMs;` would invalidate Finding 2a.
- Changing `Lifecycle.h:218` to use signed elapsed time difference would invalidate Finding 2c.
- Adding error handling to `g_prefs.putBool` would invalidate Finding 3a.
