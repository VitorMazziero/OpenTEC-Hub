# Handoff Report — Explorer 2: ESP32S3-HUB Biomass Integration & B01–B13 Audit

**Date:** 2026-09-13  
**Agent:** Explorer 2 (`teamwork_preview_explorer_m1_2`)  
**Mission:** Audit ESP32S3-HUB codebase for Biomass Sensor integration, evaluate items B01–B13 from §4.10 of `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`, and verify build and test tools.  
**Report Artifact:** `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\teamwork_preview_explorer_m1_2\survey_hub.md`

---

## 1. Observation

1. **Biomass Presence & Timeout in Hub:**
   - In `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h:371`:
     ```cpp
     const unsigned long BIOMASS_TIMEOUT = 10000;
     ```
   - In `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h:171-177`:
     ```cpp
     bool biomassOnline = snapBiomassUpdate > 0 && (millis() - snapBiomassUpdate <= BIOMASS_TIMEOUT);
     bool validBiomass = false;
     if (snapBiomassComm && snapBiomassSampleUpdate > 0) {
         unsigned long age = millis() - snapBiomassSampleUpdate;
         if (age <= BIOMASS_TIMEOUT) validBiomass = true;
     }
     ```
   - In `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h:571`:
     ```cpp
     else if (i == DEV_BIOMASS) isOnline = (biomassLastUpdate > 0 && (now - biomassLastUpdate <= BIOMASS_TIMEOUT));
     ```
   - In `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/TelemetryAndHub.h:87`:
     In `MEASURING`, the node only pushes `/biomassData` at `itRefreshTimes` (standard `probe_ms = 25000` ms), with no intermediate heartbeats (`idle=1` only sent in `IDLE`).

2. **Command Handling & Whitelist in Hub:**
   - In `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:437-490`:
     - Short keys extracted: `start`, `stop`, `blank`, `low`, `high`, `opt`, `test_period`.
     - Translated keys: `biomassIt` -> `set_it`, `biomassPwm` -> `set_pwm`, `biomassGear` -> `set_gear`, `biomassEma` -> `ema`, `biomassProbePeriodMs` -> `probe_period`.
     - `biomassAutoRange` (or `auto`/`manual`) is **not** present in `Commands.h`.
     - `test_period` is extracted (`Commands.h:459`), but `test_on` and `test_off` are never routed.

3. **Node Acceptance of Auto-Range:**
   - In `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/CommandCodec.h:124-125, 236-239`:
     Node explicitly accepts `{"command":"auto"}` / `{"auto":1}` and `{"command":"manual"}` / `{"manual":1}`.

4. **Telemetry Fields & Ecos:**
   - In `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h:336-352`:
     Hub extracts `absorbance`, `raw`, `it`, `pwm`, `gear`, `ema`, `probe_ms`.
   - In `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/protocol/TelemetryAndHub.h:76`:
     Node already sends `&hd_mode=%d` in query parameters of `/biomassData`. The Hub currently ignores it.

5. **NVS Keys:**
   - In `ESP32S3-HUB/ESP32S3-HUB/src/storage/Settings.h:44, 86`:
     Only `bioComm` (`biomassCommOn`) is stored in Hub NVS. No other biomass parameters are persisted on the Hub.

6. **Serial, Network, MQTT, and Bluetooth Architecture:**
   - Biomass communicates via HTTP over Wi-Fi SoftAP (`192.168.4.1:80`), not serial.
   - PC commands and telemetry broadcast use standard USB CDC `Serial` (115200 baud).
   - Bioreactor board uses `sensorSerial` (UART2, pins 16/17).
   - There is **no MQTT** and **no Bluetooth/BLE** in `ESP32S3-HUB/`.

7. **Build & Test Infrastructure:**
   - No PlatformIO (`platformio.ini` does not exist).
   - Arduino CLI configuration present in `ESP32S3-HUB/ESP32S3-HUB/sketch.yaml` (FQBN `esp32:esp32:esp32s3`).
   - Python contract test suite in `ESP32S3-HUB/tests/contracts/`: 83 tests passing via `python -m pytest ESP32S3-HUB/tests/contracts`.
   - PowerShell cross-contract test: `External-Devices/tools/Test-HubDeviceContracts.ps1` passing.

---

## 2. Logic Chain

1. **B01 Impact on Hub:**
   - Observation 1 demonstrates that in `MEASURING`, the node pushes once every ~25 s (`probe_ms`).
   - The Hub hardcodes `BIOMASS_TIMEOUT = 10000` (10 s) for both `biomassOnline` and `validBiomass`.
   - Therefore, after 10 s, the Hub flags the sensor offline, causing telemetry fields (`BiomassAbs`, `BiomassRaw`, etc.) to disappear from `/readData` for ~15 s until the next push.
   - Inference: The Hub must implement a dynamic presence window `biomassPresenceWindowMs(probe_ms)` following the exact pattern of D03 (`distancePresenceWindowMs`).

2. **B03 Impact on Hub:**
   - Observation 2 proves that `Commands.h` contains no routing for auto-range or manual mode.
   - Observation 3 proves that the node firmware already accepts `auto` and `manual` commands.
   - Inference: Adding `biomassAutoRange` translation to `Commands.h` solves B03 on the Hub without requiring node firmware changes.

3. **B12 Impact on Hub:**
   - Observation 2 proves `test_period` is extracted in `Commands.h:459`, but neither `test_on` nor `test_off` can be routed through the Hub.
   - Inference: `test_period` is dead code in the Hub and should be cleanly removed.

4. **Items B04, B05, B06, B07, B08, B09, B10, B11, B13:**
   - B04 is an internal node issue (LED PWM during measurement).
   - B05 is a node NVS issue (calling `saveConfig()`); Hub intentionally blocks raw `save_config` for flash wear protection.
   - B06 is an App alarm policy decision; Hub must not automatically force `start` on reboot to avoid overriding operator intent.
   - B07, B08 are documentation/version consistency items.
   - B09 is recipe timing in the App.
   - B10 and B11 are intentional hardware physics and safety boundaries.
   - B13 is an App-level sentinel value interpretation (`<= -90` and `>= 9.9`); Hub acts as a transparent float pipeline.

---

## 3. Caveats

1. Physical hardware bench testing (optical VEML7700 response, real LED thermal drift) is pending per §4.11 checklist.
2. `arduino-cli` binary is not located in the global Windows `PATH`, though local platform packages exist in `AppData\Local\Arduino15`. Static verification via pytest (83 tests) and PowerShell contract scripts executes reliably.

---

## 4. Conclusion

- **ESP32S3-HUB requires code changes for 3 items:**
  1. **B01 (Critical):** Implement dynamic timeout `biomassPresenceWindowMs(probePeriodMs)` in `AppContext.h`, `Telemetry.h`, and `HttpServer.h`.
  2. **B03 (Feature):** Add `biomassAutoRange` translation to `Commands.h`.
  3. **B12 (Cleanup):** Remove dead `test_period` key from `Commands.h` and update `tests/contracts/test_node_commands.py`.
- **Items B04 through B11 and B13 require NO changes to the Hub.**
- **Opportunity (B14):** The node already sends `&hd_mode=%d` in `/biomassData`. If desired, the Hub can trivially expose `BiomassHdMode` with zero node firmware changes.
- **Detailed implementation report:** Full details, line-by-line references, and proposed code snippets are in `.agents/teamwork_preview_explorer_m1_2/survey_hub.md`.

---

## 5. Verification Method

To verify these findings and test future modifications:

1. **Execute Python contract suite:**
   ```powershell
   python -m pytest ESP32S3-HUB/tests/contracts
   # or
   python -m unittest discover -s ESP32S3-HUB/tests/contracts -p "test_*.py"
   ```
   *Expected:* 83 tests passing.

2. **Execute PowerShell cross-device contract validation:**
   ```powershell
   powershell -ExecutionPolicy Bypass -File External-Devices/tools/Test-HubDeviceContracts.ps1
   ```
   *Expected:* Output confirming all 12 routes and node identity keys pass.

3. **Inspect code references:**
   - `ESP32S3-HUB/ESP32S3-HUB/src/core/AppContext.h`: lines 348–372.
   - `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h`: lines 437–490.
   - `ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h`: lines 308–357.
   - `ESP32S3-HUB/ESP32S3-HUB/src/sensor/Telemetry.h`: lines 171–177, 302–315.
