# Project: Biomass Sensor Inconsistencies Resolution (B01-B13)

## Architecture
The system comprises four interconnected architectural tiers for the Biomass Sensor (Device 4):
1. **Biomass Node Firmware (`External-Devices/sensor-biomassa/firmware/biomass-sensor/`)**:
   - ESP32-S3 microcontroller interfacing with VEML7700 optical light sensor and PWM-driven emitter LED.
   - State machine: `IDLE`, `MEASURING`, `BLANKING`, `ERROR`.
   - Communicates with the Hub via HTTP POST `/biomassData` and polls HTTP GET `/biomassCommands` over local Wi-Fi SoftAP (`192.168.4.1:80`).
   - Local Web/OTA and USB serial for direct diagnosis.
2. **Central Hub (`ESP32S3-HUB/`)**:
   - ESP32-S3 central coordinator bridging external devices to PC supervision.
   - Wi-Fi SoftAP server (`HttpServer.h`), central context (`AppContext.h`), command routing (`Commands.h`), telemetry caching & publishing (`Telemetry.h`).
   - Maintains mailbox queues (`biomassBox`) and device presence detection.
3. **Supervisory Applications (`Windows_app/` and Python `pc_client/`)**:
   - Windows desktop app (`OpenTECHub` in .NET 8 / C# WPF) with ViewModels (`BiomassControlViewModel.cs`), telemetry parsing (`TelemetryParser.cs`), command generation (`CommandBuilders.cs`), alarm monitoring (`AlarmService.cs`), and recipe execution (`RecipeEngine.ExternalDevices.cs`).
   - Python client (`biomass_core.py`, `biomass_gui.py`) for bench testing and calibration.
4. **Documentation & Protocol Specifications**:
   - Canonical hardware manual: `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` (Section 4).
   - Architectural evolution index: `HUB_PROTOCOL_IMPROVEMENTS.md`.

## Feature Inventory
Every item from Section 4.10 of COMANDOS_DISPOSITIVOS_EXTERNOS.md is inventoried and mapped to its respective milestone:

| # | Feature / Issue ID | Description | Milestone | Source |
|---|--------------------|-------------|-----------|--------|
| 1 | B01 | Dynamic presence window in MEASURING (Hub timeout 10s vs 25s probe) | M1, M3 | §4.10 |
| 2 | B02 | Non-blocking service and dynamic window during blanking/gear search (20-40s) | M1, M2, M3 | §4.10 |
| 3 | B03 | Auto-range routing and manual gear lock across Hub, App, and Node | M1, M2, M3, M4 | §4.10 |
| 4 | B04 | Critical thermal bug: turn off LED unconditionally in `setManualGear()` | M1, M2 | §4.10 |
| 5 | B05 | NVS persistence for thresholds (`low`, `high`, `opt`) and `probe_period` | M1, M2 | §4.10 |
| 6 | B06 | Supervision alarm for silent node reboot after brownout | M1, M4 | §4.10 |
| 7 | B07 | Version string harmonization to `v11.0` (wire, local OTA, docs) | M1, M2, M5 | §4.10 |
| 8 | B08 | Deprecated JSON syntax cleanup in documentation | M1, M5 | §4.10 |
| 9 | B09 | Recipe blank timeout extension (20-40s real sweep vs 15s timer) | M1, M4 | §4.10 |
| 10 | B10 | Architectural decision: IT/PWM changes invalidate optical blank baseline ($I_0$) | M1, M5 | §4.10 |
| 11 | B11 | Architectural decision: `hub_off` and `factory` restricted to local channel | M1, M5 | §4.10 |
| 12 | B12 | Cleanup unused `test_period` key from Hub `Commands.h` | M1, M3 | §4.10 |
| 13 | B13 | Explicit handling and masking of sentinel values (`-99.0` and `9.9`) in App | M1, M4 | §4.10 |
| 14 | B14 | Extra telemetry fields (`hd_mode`) evaluation and buffer constraint justification | M1, M3, M5 | §4.10 |
| 15 | B15 | Acquisition tuning order documented in accordance with CommandBuilders | M1, M5 | §4.10 |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|--------------|--------|
| M1 | Plan & Verifier | Create `IMPLEMENTATION_PLAN_BIOMASSA.md` and `verify_plan_biomassa.py` | None | DONE |
| M2 | Firmware Updates | Apply B04 (LED off), B05 (NVS save), B07 (v11.0), B03 (preserve gear) in `biomass-sensor/` | M1 | DONE |
| M3 | Hub Updates | Apply B01 (dynamic timeout), B03 (`biomassAutoRange`), B12 (`test_period` cleanup) in `ESP32S3-HUB/` | M1 | IN_PROGRESS |
| M4 | Apps Updates | Apply B03 (AutoRange UI/Keys), B06 (Supervision Alarm), B09 (Recipe timeout), B13 (Sentinels) in `Windows_app/` | M1 | PLANNED |
| M5 | Documentation Updates | Update `COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§4.0 table) and `HUB_PROTOCOL_IMPROVEMENTS.md` | M1, M2, M3, M4 | PLANNED |
| M6 | Git Commits & Verification | Create separate commits (Firmware, Hub, Apps/Docs) and execute full test suites | M2, M3, M4, M5 | PLANNED |

## Interface Contracts
### Hub ↔ Biomass Node HTTP API
- **Endpoint POST `/biomassData`**: Node pushes JSON or URL-encoded metrics:
  `absorbance`, `raw`, `it`, `pwm`, `gear`, `ema`, `probe_ms`, `hd_mode`.
- **Endpoint GET `/biomassCommands`**: Node receives JSON mailbox queue:
  `{"command": "start"|"stop"|"blank"|"auto"|"manual", ...}`.
- **Hub Presence Window**:
  `biomassPresenceWindowMs(probe_ms) = max(10000UL, (unsigned long)(probe_ms * 2.5f))`

### Hub ↔ Supervisory App (Windows / Python)
- **Telemetry `/readData` & Serial CDC**:
  Exposes `BiomassOnline`, `BiomassAbs`, `BiomassRaw`, `BiomassIt`, `BiomassPwm`, `BiomassGear`, `BiomassEma`, `BiomassProbeMs`.
- **Commands `/sendCommand` & Serial CDC**:
  Accepts `biomassStart`, `biomassStop`, `biomassBlank`, `biomassAutoRange` (`"auto"` or `"manual"`), `biomassGear`, `biomassProbePeriodMs`.

## Code Layout
- Biomass Node: `External-Devices/sensor-biomassa/firmware/biomass-sensor/src/`
  - `protocol/CommandCodec.h`
  - `protocol/TelemetryAndHub.h`
  - `core/Lifecycle.h`
  - `core/ServiceRuntime.h`
  - `api/LocalHttpApi.h`
- Hub: `ESP32S3-HUB/ESP32S3-HUB/src/`
  - `core/AppContext.h`
  - `protocol/Commands.h`
  - `sensor/Telemetry.h`
  - `network/HttpServer.h`
- Windows App: `Windows_app/src/`
  - `OpenTECHub/ViewModels/BiomassControlViewModel.cs`
  - `OpenTECHub/Protocol/CommandKeys.cs`
  - `OpenTECHub/Protocol/CommandBuilders.cs`
  - `OpenTECHub.Core/Domain/SensorReadings.cs`
  - `OpenTECHub.Core/Services/AlarmService.cs`
  - `OpenTECHub.Core/Services/RecipeEngine.ExternalDevices.cs`
- Documentation:
  - `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`
  - `HUB_PROTOCOL_IMPROVEMENTS.md`
  - `IMPLEMENTATION_PLAN_BIOMASSA.md`
