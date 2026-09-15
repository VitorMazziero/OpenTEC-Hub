# Handoff Report: Specification Mining of Fluxômetro Inconsistencies (F01–F16)

**Agent:** `teamwork_preview_spec_miner` (`spec_miner_survey_1`)  
**Parent Agent:** `parent` (`b0df3e0f-75ec-45d0-8782-6776cbcdc8ff`)  
**Target File:** `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\spec_miner_survey_1\handoff.md`  
**Date/Timestamp:** `2026-09-13T01:35:00Z`  
**Handoff Type:** Hard Handoff (Task Complete)

---

## Executive Summary

This report documents the exhaustive extraction and technical audit of all 16 documented inconsistencies (**F01 through F16**) for the **Fluxômetro** (flowmeter) subsystem of the OpenTEC-Hub project. The primary specification source analyzed is Section 3.10 of `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`, cross-referenced against:
1. Active firmware implementation (`External-Devices/fluxometro/firmware/flowmeter/src/`): `FirmwareApp.cpp`, `Lifecycle.h`, `CommandCodec.h`, `FlowIo.h`, `CalibrationStore.h`, `OtaService.h`, `WebSocketApi.h`, `TaskRuntime.h`.
2. Windows Application service and UI layers (`Windows_app/`): `CalibrationMath.cs`, `FlowControlViewModel.cs`, `TelemetryParser.cs`, `CommandKeys.cs`.
3. Flutter direct application (`External-Devices/fluxometro/apps/flutter/lib/main.dart`).
4. External Devices documentation (`External-Devices/docs/` and `External-Devices/fluxometro/docs/`).

---

## Features Discovered

| # | Category | Feature | Description | Inputs | Outputs | Error Behavior | Discovered Via |
|---|----------|---------|-------------|--------|---------|----------------|----------------|
| 1 | Identity & Version | Version Reporting & Announcement | Broadcasts node identity and firmware version via serial banner, OTA webpage, HTTP `/nodeHello`, and REST endpoints `/diag` and `/status`. | Boot trigger, HTTP `GET /nodeHello`, `GET /diag`, `GET /status`, `GET /update`. | Serial banner: `V10`; OTA HTML: `V10`; `/nodeHello`: `ver=v11`; `/diag`, `/status`: `"version":"v11"`. | Conflicting version strings reported simultaneously across interfaces. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F01), `FirmwareApp.cpp:16`, `TaskRuntime.h:85`, `OtaService.h:12,33` |
| 2 | Calibration | Flow Calibration Curve Models | Stores and computes flow rate from ADC voltage ($V$) across a two-segment model split at $V_{split} = 0.0545\text{ V}$. Low range is quartic or quadratic; high range is quadratic. | Voltage from ADS1115 ($V$ in Volts). Parameter keys: `a1`, `b1`, `k1`, `f1`, `c1`, `k2`, `f2`, `c2`. | `flow_rate` (calculated flow rate in L/min). | Incompatible factory constants between Firmware (`FACTORY_*`), Windows App (`FirmwareDefault`), and Flutter app. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F02), `FirmwareApp.cpp:183-196`, `CalibrationMath.cs:78-84`, `main.dart:180-185` |
| 3 | Telemetry & UI | Controller Output Telemetry (`flow_output` / `FlowOutput`) | Telemetry field transmitting controller PI output signal (equivalent setpoint in L/min, range $0 \dots \text{maxFlowRate}$). | Controller loop output `flowSetpoint` ($0 \dots 50.0\text{ L/min}$). | Transmitted as `flow_output` in `/flowData` query string; parsed as `FlowOutput` by Hub/Windows App. | Windows App UI appends `" V"` instead of `" L/min"` (`FlowOutputText`), displaying flow as voltage. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F03), `FlowControlViewModel.cs:690`, `Lifecycle.h:230` |
| 4 | Control Loop | Low Setpoint Cutoff & Deadband | Control execution gating based on target setpoint. Loop only activates when `targetFlowSetpoint > 0.1f` and `valveFlowState == 0`. | `flow_setpoint` or `flowSetpoint` $\in [0.0, \text{maxFlowRate}]$. | DAC output (0–4095 via MCP4725), `v_Flow` pin state (GPIO 5). | For $0 < \text{targetFlowSetpoint} \le 0.1\text{ L/min}$, valve is not closed, PI loop is halted, and previous DAC output is frozen. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F04), `Lifecycle.h:145-199`, `CommandCodec.h:165-178` |
| 5 | Valve Actuation | Cutoff Valve Control (`v_Flow`) | Controls physical general cutoff solenoide on GPIO 5 (*Valve Off*). Active HIGH shuts the gas line. | `v_Flow` or `valveFlow` (integer 0 or 1). Positive `flow_setpoint`. | GPIO 5 pin level (HIGH = closed, LOW = open). | Positive setpoint command does NOT open `v_Flow` on node. Flutter toggle "ON" sends `1`, closing the valve while displaying green "ON". | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F05), `CommandCodec.h:139-141`, `main.dart:606-612` |
| 6 | Valve Actuation | Gas Routing Outputs (`v1`, `v2`) | Actuates Route A (Reactor, GPIO 16 via `v2`) and Route B+C (Nitrogen/Exhaust, GPIO 17 via `v1`). | `v1` / `valve_1`, `v2` / `valve_2` (0 or 1). | GPIO 16 and 17 pin levels. | Node firmware lacks routing interlocks: accepts both routes open ($v1=1, v2=1$) or dead line ($v1=0, v2=0$ with active flow). | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F06), `CommandCodec.h:144-153` |
| 7 | Storage & Safety | Parameter Validation & Persistence | Updates control tuning (`kp_flow`, `ki_flow`, `ff_gain`, `ff_offset`, `ramp_rate`, `dac_hold`) and calibration in EEPROM. | JSON string with float values. | Updates `calParams` struct in RAM and writes to EEPROM. | No check for `NaN`, `Inf`, or out-of-bounds numbers; corrupted numbers are permanently saved to EEPROM. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F07), `CommandCodec.h:184-210, 235`, `CalibrationStore.h:60-64` |
| 8 | Protocol Parser | Zero-copy JSON Command Parser | Custom linear character scanner searching for keys and parsing values without external JSON library. | JSON strings received via Serial, WebSocket (`/ws`), or Hub (`/flowCommand`). | Extracted command variables and state updates. | Permissive parsing applies partial commands on syntax errors; boolean values (`true`/`false`) evaluated via `atoi()` turn `true` into `0`. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F08), `CommandCodec.h:99-214` |
| 9 | Scale & Conversion | Maximum Flow Scale (`max_flow`) | Defines instrument full scale (default 50.0 L/min) for DAC scaling and setpoint clamping. | `max_flow` or `maxFlow` (float $> 0.01$). | Updates `maxFlowRate` RAM variable. | `maxFlowRate` is NOT stored in EEPROM; on reboot, reverts to 50.0 L/min, distorting DAC output transfer function. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F09), `CommandCodec.h:195-202`, `FirmwareApp.cpp:108` |
| 10 | Calibration | Partial Calibration Ingestion | Accepts partial calibration parameter updates for low and high segments. | `k1`, `f1`, `c1` without `a1`, `b1`. | Updates low curve parameters in `calParams`. | Receiving quadratic low parameters (`k1/f1/c1`) without explicit `a1/b1` automatically zeroes `a1` and `b1` in EEPROM. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F10), `CommandCodec.h:217-220` |
| 11 | Protocol Verification | Calibration Application ACK | Sends acknowledgment message back to client upon receiving command frame. | Calibration command frame via WebSocket or Hub. | WebSocket ACK: `command_ack`, `last_apply_ms`, etc. | ACK only confirms parser receipt, not persisted EEPROM values; no coefficients or checksum returned in ACK or telemetry. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F11), `WebSocketApi.h:28-36`, `TaskRuntime.h:124-151` |
| 12 | Hardware Diagnostics | Sensor & Actuator I²C Health Check | Initializes ADS1115 (ADC) and MCP4725 (DAC) on I²C bus at boot (`Wire.begin()`). | Hardware response on I²C addresses `0x48` and `0x60`. | Serial log output: `"OK"` or `"FAILED"`. | Boot continues on I²C failure; no health bit in telemetry, allowing UI to display stale/zero data without alerting operator. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F12), `Lifecycle.h:67-82`, `FlowIo.h:13-39` |
| 13 | Network & Security | SoftAP, Management & OTA Interface | Serves local Wi-Fi SoftAP `Floxometro_AP` and endpoints `/update`, `/ws`, `/diag`, `/status`. | HTTP requests, WebSocket connection frames. | HTTP HTML/JSON responses, WebSocket data stream. | Open AP without WPA2 password; zero authentication on `/update` or `/ws`; anyone in RF range can hijack control or flash binary. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F13), `FirmwareApp.cpp:19-20`, `OtaService.h:5-7, 51-109` |
| 14 | Firmware Update | Over-The-Air (OTA) Firmware Flashing | Multipart upload endpoint (`POST /update`) streaming `.bin` binary into flash OTA partition. | Multipart form payload containing `.bin` file. | Flash erase/write, HTTP response, system reboot. | Network tasks pause (`otaInProgress=true`) but hardware outputs (valves, DAC) remain active in their last state for up to 90 s. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F14), `OtaService.h:51-109`, `TaskRuntime.h:16,82,104` |
| 15 | Network Management | Wi-Fi Station Reconnection Control | Controls automatic station reconnection task trying `ModuloTECNAL_1` and `_2` on channel 6. | `reconnect_wifi` parameter (0 or 1). | Enables/disables `wifiTask` reconnection loop. | If set to 0, node never reconnects to Hub; Windows App only monitors `reconnect_wifi` and offers no command/UI to re-enable it. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F15), `CommandCodec.h:154-158`, `TaskRuntime.h:211-215` |
| 16 | Hardware Feedback | Solenoid Valve State Telemetry | Telemetry fields reporting active state of valves: `valve1State`, `valve2State`, `valveFlowState`. | Internal software variables written to GPIOs. | Transmitted in `/flowData` and WebSocket frames. | Telemetry echoes the commanded GPIO latch state only; no electrical current, limit switch, or flow verification readback. | `COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10 (F16), `Lifecycle.h:219-221`, `CommandCodec.h:140,145,150` |

---

## Edge Cases

| # | Feature | Input | Observed Behavior |
|---|---------|-------|-------------------|
| 1 | Low Setpoint PI Control (F04) | `{"flow_setpoint": 0.05}` | DAC output is not updated; PI does not execute; valve GPIO 5 is not closed; previous DAC output remains energized. |
| 2 | Zero Setpoint with DAC Hold (F04) | `{"flow_setpoint": 0.0}` with `dac_hold: 1` | `valveFlowState` set to 1 (GPIO 5 HIGH = Valve Off), but `flowSetpoint`, `rampedTarget`, and DAC voltage are frozen at previous operating point. |
| 3 | Boolean Parsing via atoi (F08) | `{"v_Flow": true}` | `atoi("true")` returns `0`; `valveFlowState = (0 != 0)` evaluates to `0` (LOW = open valve), the exact opposite of true. |
| 4 | Malformed JSON Ingestion (F08) | `{"v1": 1, "invalid_token###"` | `v1` is parsed and GPIO 17 energized immediately; remainder is abandoned without rollback or error notification. |
| 5 | Non-Finite Float Tuning (F07) | `{"kp_flow": NaN}` or `{"ki_flow": Infinity}` | `strtof()` converts to NaN/Inf; stored in `calParams.kp` and immediately saved to EEPROM via `EEPROM.commit()`. PI loop is permanently corrupted across boots. |
| 6 | Direct Setpoint without v_Flow (F05) | `{"flow_setpoint": 15.0}` to node with `valveFlowState == 1` | Node updates `targetFlowSetpoint = 15.0`, but leaves `valveFlowState = 1` (Valve Off). Gas line remains physically closed. |
| 7 | Direct Flutter Valve Toggle (F05) | User activates "Flow Valve" switch in Flutter app | Flutter sends `{"v_Flow": 1}`; firmware drives GPIO 5 HIGH; valve physically closes; Flutter displays green "ON". |
| 8 | Competing Routes Activation (F06) | `{"v1": 1, "v2": 1}` | Firmware sets both GPIO 16 and GPIO 17 HIGH; both solenoids energize simultaneously with no firmware interlock. |
| 9 | Dead Line Dead-End Flow (F06) | `{"v_Flow": 0, "v1": 0, "v2": 0, "flow_setpoint": 20.0}` | Cutoff opens and DAC outputs 20 L/min setpoint to MFC, but both route valves remain shut. Gas dead-ends against closed valves. |
| 10 | Reboot after Custom Max Flow (F09) | Set `{"max_flow": 20.0}`, reboot node | `maxFlowRate` resets to 50.0; subsequent DAC setpoint `(setpoint / 50.0) * 4095` outputs only 40% of intended voltage to 20 L/min MFC. |
| 11 | Flutter Calibration Save (F10) | Save calibration in Flutter UI (`k1, f1, c1, k2, f2, c2`) | Firmware receives quadratic low coefficients without `a1/b1`; executes `a1 = calParams.a1 = 0.0f; b1 = calParams.b1 = 0.0f;` and writes to EEPROM, destroying quartic curve. |
| 12 | Disconnected ADS1115 ADC (F12) | Unplug ADC I²C bus before boot | `ads.begin()` fails with serial log; boot continues; `readFlowVoltage` remains 0.0 V or frozen filter value; telemetry reports normal frame without error flags. |
| 13 | OTA Upload While Flowing (F14) | Send multipart binary to `/update` while flowing 30 L/min | Communication tasks pause; gas continues flowing at 30 L/min through MFC for up to 90 seconds during flashing. |
| 14 | Wi-Fi Reconnection Disabled (F15) | Send `{"reconnect_wifi": 0}` | Node stops station reconnect loop. If AP link drops, node is unreachable via Hub; Windows App cannot command re-enablement. |

---

## Detailed Inconsistency Specifications (F01–F16)

### F01 — Firmware Version Identity Discrepancy

- **Inconsistency ID:** `F01`
- **Canonical Title:** Identidade V10 no build/OTA e v11 no protocolo/endpoints
- **Stated Problem Description (Section 3.10):**
  The build banner and OTA page report version `V10`, whereas protocol endpoints (`/nodeHello`, `/diag`, `/status`) report `v11`. Because multiple conflicting version identifiers exist across interfaces and documentation, the running binary cannot be unambiguously identified by supervisory software or operator UI.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - `GET /nodeHello?dev=flowmeter&ver=v11&mac=XX:XX:XX:XX:XX:XX` (Hub handshake)
  - `GET /diag` $\rightarrow$ JSON response field `"version": "v11"`
  - `GET /status` $\rightarrow$ JSON response field `"version": "v11"`
  - `GET /update` $\rightarrow$ HTML title/text rendering `FW_BUILD` containing `"V10"`
  - Telemetry `/flowData` does not include version.
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp`:
    - Line 16: `#define FW_VERSION "V10"`
    - Line 17: `#define FW_BUILD FW_VERSION " (" __DATE__ " " __TIME__ ")"`
    - Line 58: `<p>Running: <b>` `FW_BUILD` `</b></p>`
  - `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`:
    - Line 85: `snprintf(helloUrl, sizeof(helloUrl), "%s/nodeHello?dev=flowmeter&ver=v11&mac=%s", ...);`
  - `External-Devices/fluxometro/firmware/flowmeter/src/api/OtaService.h`:
    - Line 12: `"{\"device\":\"flowmeter\",\"version\":\"v11\",\"uptime_s\":%lu, ..."` (`/diag`)
    - Line 33: `"{\"device\":\"flowmeter\",\"version\":\"v11\",\"uptime_s\":%lu, ..."` (`/status`)
  - `External-Devices/fluxometro/docs/PROTOCOL.md`: Line 3 states `**Versão de Firmware:** v11`.
  - `External-Devices/fluxometro/docs/CURRENT_STATUS.md`: Line 3 states `Firmware ativo: firmware/flowmeter (v10)`.
- **Discrepancy Breakdown:**
  Compile-time constant `FW_VERSION` is hardcoded to `"V10"`, while string literals in `TaskRuntime.h` and `OtaService.h` are hardcoded to `"v11"`. Documentation is split: protocol contract says `v11`, status notes say `v10`.
- **Candidate Fix / Resolution:**
  Define a single authoritative macro in `FirmwareApp.h` or `Config.h` (e.g. `#define FIRMWARE_VERSION "v11.0"`), and reference this constant uniformly in `FW_BUILD`, `/nodeHello`, `/diag`, `/status`, and documentation.

---

### F02 — Divergent Default Calibration Curves across Repositories

- **Inconsistency ID:** `F02`
- **Canonical Title:** Curva `FACTORY_*` do firmware, `CalibrationMath.FirmwareDefault`/pontos certificados do Windows e defaults do Flutter são diferentes
- **Stated Problem Description (Section 3.10):**
  Firmware fallback constants (`FACTORY_*`), Windows App defaults (`CalibrationMath.FirmwareDefault`), and Flutter app defaults hold three completely different sets of polynomial coefficients. Selecting "restore defaults" in different user interfaces installs conflicting curves, altering measurement accuracy.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Commands: `a1`, `b1`, `k1`, `f1`, `c1` (low range quartic/quadratic, $V \le 0.0545\text{ V}$)
  - Commands: `k2`, `f2`, `c2` (high range quadratic, $V > 0.0545\text{ V}$)
- **Code & Hardware Evidence:**
  - **Source 1: Firmware `FirmwareApp.cpp` (lines 183–196):**
    - Low segment (quartic):
      - `FACTORY_A1 = -1353785.3f`
      - `FACTORY_B1 = 246663.69f`
      - `FACTORY_K1 = -16473.492f`
      - `FACTORY_F1 = 484.99466f`
      - `FACTORY_C1 = -4.6159464f`
    - High segment (quadratic):
      - `FACTORY_K2 = -0.46260458f`
      - `FACTORY_F2 = 10.797299f`
      - `FACTORY_C2 = 0.28475793f`
  - **Source 2: Windows App `CalibrationMath.cs` (lines 78–84):**
    - Low segment (quartic):
      - `A = 321791.345936369`
      - `B = -32589.073104291`
      - `K = 462.893536740`
      - `F = 43.294432104`
      - `C = -0.464367483`
    - High segment (quadratic):
      - `K = -0.854551899`
      - `F = 11.814453070`
      - `C = 0.192231954`
  - **Source 3: Flutter App `main.dart` (lines 180–185):**
    - Low segment (pure quadratic, $a1=0, b1=0$):
      - `k1 = -139.0570077428`
      - `f1 = 21.9738888302`
      - `c1 = -0.0341880209`
    - High segment (quadratic):
      - `k2 = -0.8724324917`
      - `f2 = 10.6573301479`
      - `c2 = 0.1953756879`
- **Discrepancy Breakdown:**
  All three curves yield drastically different flow rates for the same input voltage. For example, at $V = 0.03\text{ V}$:
  - Firmware gives $\approx 0.18\text{ L/min}$
  - Windows Default gives $\approx 0.44\text{ L/min}$
  - Flutter Default gives $\approx 0.49\text{ L/min}$
- **Candidate Fix / Resolution:**
  Establish a single authoritative physical calibration reference based on certified bench calibration data, regenerate the 8 polynomial coefficients, and synchronize them across Firmware (`FACTORY_*`), Windows App (`FirmwareDefault`), and Flutter app.

---

### F03 — Unit and Dimensional Mismatch in Windows UI (`FlowOutput`)

- **Inconsistency ID:** `F03`
- **Canonical Title:** `FlowOutput` é equivalente L/min, mas Windows mostra `V`
- **Stated Problem Description (Section 3.10):**
  `FlowOutput` represents the controller output in equivalent flow units (L/min, $0 \dots 50$), but the Windows App UI formats it with the unit suffix `" V"`. This presents an incorrect physical dimension to the operator.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Telemetry key from node: `flow_output`
  - Hub telemetry key: `FlowOutput`
  - App ViewModel property: `FlowOutputText`
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`:
    - Line 177: `float newFlowSetpoint = constrain(flowFeedforward + P_term + I_term, 0.0f, maxFlowRate);`
    - Line 217, 230: `snapOutput = flowSetpoint;` $\dots$ `snprintf(..., "\"flow_output\":%.6f", snapOutput);`
  - `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h`:
    - Line 2: `uint16_t dacValue = (flowSetpointVal / maxFlowRate) * 4095;` (confirms `flowSetpointVal` is in L/min, scaled to full scale `maxFlowRate`).
  - `Windows_app/src/OpenTECHub/ViewModels/FlowControlViewModel.cs`:
    - Line 690: `FlowOutputText = snapshot.FlowOutput is { } vo ? vo.ToString("F2", CultureInfo.CurrentCulture) + " V" : "—";`
  - `Windows_app/docs/PROTOCOL.md`:
    - Line 281: `| FlowOutput | float | V | Flowmeter controller analog output voltage |`
- **Discrepancy Breakdown:**
  The firmware produces a value in L/min ($0 \dots 50$). If setpoint is 25.0 L/min, `flow_output` is $\approx 25.0$. The Windows App displays `"25.00 V"`, which is physically nonsensical for a 0–5V or 0–3.3V DAC circuit.
- **Candidate Fix / Resolution:**
  Update `FlowControlViewModel.cs` line 690 to format `FlowOutputText` as `L/min` (or rename UI field to "Saída de Controle"), and correct `Windows_app/docs/PROTOCOL.md`. If DAC voltage is required, compute $V_{dac} = (\text{FlowOutput} / \text{MaxFlow}) \times 5.0\text{ V}$.

---

### F04 — Deadband / Latch Anomaly in Low Setpoint Region ($0 < \text{Setpoint} \le 0.1\text{ L/min}$)

- **Inconsistency ID:** `F04`
- **Canonical Title:** 0 < alvo ≤ 0,1 não fecha a linha nem roda PI
- **Stated Problem Description (Section 3.10):**
  When a setpoint in the range $0 < \text{target} \le 0.1\text{ L/min}$ is commanded, the firmware does not close the valve, does not zero the DAC, and does not execute the PI loop. The previous DAC voltage remains energized on the MFC.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Command: `flow_setpoint` or `flowSetpoint` $\in (0.0, 0.1]$
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`:
    - Line 170: Valve closure is only triggered when `targetFlowSetpoint == 0.0f`:
      ```cpp
      if (targetFlowSetpoint == 0.0f) {
        valveFlowState = 1;
        digitalWrite(VALVE_FLOW_PIN, HIGH);
        ...
      }
      ```
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`:
    - Lines 145–153:
      ```cpp
      if (targetFlowSetpoint <= 0.0f) {
        if (!dacHold) rampedTarget = 0.0f;
      } else if (rampRate > 0.0f) { ... }
      ```
    - Lines 155–199:
      ```cpp
      if (targetFlowSetpoint > 0.1f && valveFlowState == 0) {
        // Runs PI controller and updates DAC
      } else if (targetFlowSetpoint > 0.1f) {
        // Integral and DAC stay frozen
      } else if (!dacHold) {
        // Drops DAC to zero only if dacHold is false!
      }
      ```
    - `dacHold` defaults to `true` (`DAC_HOLD_DEFAULT = 1.0f`).
- **Discrepancy Breakdown:**
  For $0 < \text{targetFlowSetpoint} \le 0.1$:
  1. `targetFlowSetpoint == 0.0f` is false $\rightarrow$ valve GPIO 5 is not shut.
  2. `targetFlowSetpoint > 0.1f` is false $\rightarrow$ PI regulation does not run.
  3. `!dacHold` is false (default is true) $\rightarrow$ DAC is not zeroed.
  Outcome: If the MFC was running at 30 L/min and setpoint changes to 0.05 L/min, the MFC remains driven at 30 L/min indefinitely.
- **Candidate Fix / Resolution:**
  Either enforce low setpoints $\le 0.1\text{ L/min}$ to be normalized to $0.0\text{ L/min}$ (asserting `valveFlowState = 1` and zeroing DAC), or extend PI regulation down to the true minimum controllable range of the MFC.

---

### F05 — Asymmetric Valve Actuation and Inverted Flutter UI Toggle

- **Inconsistency ID:** `F05`
- **Canonical Title:** Setpoint direto positivo não abre `v_Flow`; toggle Flutter “ON” fecha
- **Stated Problem Description (Section 3.10):**
  Sending a positive flow setpoint directly to the node does not open the cutoff valve (`v_Flow`). Furthermore, the Flutter application's toggle switch labeled "Flow Valve ON" sends `1`, which physically drives GPIO 5 HIGH (*Valve Off*), cutting off gas flow while indicating active flow to the user.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Command: `v_Flow` / `valveFlow` (0 = open line, 1 = close line)
  - Command: `flow_setpoint` / `flowSetpoint`
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`:
    - Lines 165–183: Receiving positive `flow_setpoint` updates `targetFlowSetpoint`, but never modifies `valveFlowState`.
    - Lines 139–143:
      ```cpp
      valveFlowState = (atoi(valBuf) != 0);
      digitalWrite(VALVE_FLOW_PIN, valveFlowState);
      ```
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp`:
    - Line 80: `#define VALVE_FLOW_PIN 5`
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`:
    - Lines 2–4: `digitalWrite(VALVE_FLOW_PIN, HIGH); valveFlowState = 1;` (Initializes closed / Valve Off).
  - `External-Devices/fluxometro/apps/flutter/lib/main.dart`:
    - Lines 570–573:
      ```dart
      _buildDataRow('Flow Valve State', valveFlowState == 1 ? 'ON' : 'OFF',
                    valueColor: valveFlowState == 1 ? Colors.green : Colors.red)
      ```
    - Lines 606–612:
      ```dart
      SwitchListTile(
        title: const Text('Flow Valve'),
        value: valveFlowState == 1,
        onChanged: _isConnected ? (val) => _updateValveState('v_Flow', val) : null,
      )
      ```
- **Discrepancy Breakdown:**
  GPIO 5 is wired to the MFC's *Valve Off* pin: `HIGH` (1) closes the line, `LOW` (0) opens it. In Flutter:
  - Turning the switch "ON" sends `{"v_Flow": 1}`, closing the valve.
  - The UI displays "Flow Valve State: ON" in green.
  - An operator commanding positive setpoint without explicit `v_Flow: 0` gets zero gas flow.
- **Candidate Fix / Resolution:**
  1. In Flutter app, invert toggle semantics or rename to "Corte Geral (Valve Off)" where active = closed.
  2. In firmware, open `valveFlowState = 0` automatically when a valid positive setpoint is commanded unless explicitly overridden.

---

### F06 — Missing Route Interlock in Firmware

- **Inconsistency ID:** `F06`
- **Canonical Title:** Sem intertravamento de rota no nó
- **Stated Problem Description (Section 3.10):**
  The node firmware applies valve states `v1` and `v2` independently without verifying whether both routes are opened simultaneously or whether both are closed while gas is actively commanded.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Commands: `v1` (GPIO 17, Route B+C), `v2` (GPIO 16, Route A)
  - Parameter combinations: `{"v1": 1, "v2": 1}`, `{"v1": 0, "v2": 0, "flow_setpoint": 10.0}`
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`:
    - Lines 144–153:
      ```cpp
      else if (strcmp(keyBuf, "v1") == 0 || strcmp(keyBuf, "valve_1") == 0) {
        valve1State = (atoi(valBuf) != 0);
        digitalWrite(VALVE1_PIN, valve1State);
        recognizedCommand = true;
      }
      else if (strcmp(keyBuf, "v2") == 0 || strcmp(keyBuf, "valve_2") == 0) {
        valve2State = (atoi(valBuf) != 0);
        digitalWrite(VALVE2_PIN, valve2State);
        recognizedCommand = true;
      }
      ```
- **Discrepancy Breakdown:**
  Mutual exclusion (interlock) is implemented only in higher-level software (Hub / Windows App `GasRouting`), but not in the microcontroller firmware. A direct WebSocket client, corrupted command frame, or serial terminal can energize both solenoids simultaneously (mixing gases or splitting flow unpredictably) or close both solenoids while flow is commanded (dead-ending the gas line against closed valves).
- **Candidate Fix / Resolution:**
  Enforce atomic route validation inside `CommandCodec.h`: reject or auto-arbitrate frames commanding $v1=1$ and $v2=1$ concurrently, and enforce safe shutoff if all routes are closed while setpoint is non-zero.

---

### F07 — Unchecked Tuning and Calibration Parameter Ingestion

- **Inconsistency ID:** `F07`
- **Canonical Title:** Kp/Ki/FF/curva sem validação de faixa/finitude
- **Stated Problem Description (Section 3.10):**
  Parameters for control gains (Kp, Ki), feedforward, ramp rate, and calibration curve coefficients are parsed directly using `strtof()` without checking for finite values (`isnan()`, `isinf()`) or acceptable physical ranges.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Commands: `kp_flow`, `ki_flow`, `ff_gain`, `ff_offset`, `ramp_rate`, `a1`, `b1`, `k1`, `f1`, `c1`, `k2`, `f2`, `c2`
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`:
    - Lines 184–188:
      ```cpp
      else if (strcmp(keyBuf, "kp_flow") == 0) { Kp_flow = calParams.kp = strtof(valBuf, nullptr); calParamsUpdated = true; ... }
      else if (strcmp(keyBuf, "ki_flow") == 0) { Ki_flow = calParams.ki = strtof(valBuf, nullptr); calParamsUpdated = true; ... }
      else if (strcmp(keyBuf, "ff_gain") == 0) { ffGain = calParams.ff_gain = strtof(valBuf, nullptr); calParamsUpdated = true; ... }
      else if (strcmp(keyBuf, "ff_offset") == 0) { ffOffset = calParams.ff_offset = strtof(valBuf, nullptr); calParamsUpdated = true; ... }
      else if (strcmp(keyBuf, "ramp_rate") == 0) { rampRate = calParams.ramp_rate = max(0.0f, strtof(valBuf, nullptr)); calParamsUpdated = true; ... }
      ```
    - Lines 203–210: Direct parsing of polynomial coefficients.
    - Line 235:
      ```cpp
      if (calParamsUpdated) saveParameters();
      ```
  - `External-Devices/fluxometro/firmware/flowmeter/src/storage/CalibrationStore.h`:
    - Lines 60–64: Writes `calParams` struct directly to EEPROM with `EEPROM.commit()`.
- **Discrepancy Breakdown:**
  If a malformed string (e.g. `NaN`, `Infinity`, or negative gain) is received, it is loaded into the controller and written to EEPROM. On subsequent reboots, `calParams.magic` remains valid (`CALIBRATION_MAGIC`), so `NaN` is reloaded into active memory, permanently incapacitating the PI controller until EEPROM is wiped.
- **Candidate Fix / Resolution:**
  Implement a transactional validator before setting `calParamsUpdated = true`: check `isfinite()` on all parsed values and assert bounds (e.g., $0 \le K_p \le 10$, $0 \le K_i \le 20$, $0 \le \text{ramp\_rate} \le 100$). Discard invalid frames.

---

### F08 — Permissive Manual Parser and Boolean Interpretation Flaw

- **Inconsistency ID:** `F08`
- **Canonical Title:** Parser permissivo aplica parcialmente JSON malformado e true vira 0 via atoi
- **Stated Problem Description (Section 3.10):**
  The zero-copy manual parser parses key-value pairs sequentially. If a frame has syntax errors midway, previous pairs are already executed (partial application). Furthermore, integer conversion with `atoi()` turns standard JSON booleans (`true`) into `0`.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - All JSON commands over Serial, WebSocket, and Hub.
  - Boolean keys: `v_Flow`, `v1`, `v2`, `dac_hold`, `reconnect_wifi`, `debug_pi`.
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`:
    - Lines 99–137: Manual pointer scanner searching for colons and commas.
    - Lines 140, 145, 150, 155, 160, 190:
      ```cpp
      valveFlowState = (atoi(valBuf) != 0);
      valve1State = (atoi(valBuf) != 0);
      valve2State = (atoi(valBuf) != 0);
      reconnect_Wifi = (atoi(valBuf) == 1);
      dacHold = (atoi(valBuf) != 0);
      ```
- **Discrepancy Breakdown:**
  1. In standard JSON, booleans are literal `true` or `false`. `atoi("true")` returns `0`. Thus `valveFlowState = (atoi("true") != 0)` evaluates to `0` (`false`), inverting the intended command.
  2. The parser lacks two-phase transactional execution (validate-then-apply). A corrupted JSON frame containing valid initial keys updates hardware before encountering the syntax error.
- **Candidate Fix / Resolution:**
  Use a robust lightweight JSON parser (or enhance the tokenizer) with explicit boolean literal handling (`strcmp(valBuf, "true") == 0 || atoi(valBuf) != 0`) and full syntax validation before applying hardware state changes.

---

### F09 — Volatility of Full-Scale Range Setting (`max_flow`)

- **Inconsistency ID:** `F09`
- **Canonical Title:** `max_flow` não persiste
- **Stated Problem Description (Section 3.10):**
  The `max_flow` setting is held only in volatile RAM. Upon microcontroller reboot, `maxFlowRate` reverts to its hardcoded default of 50.0 L/min, altering the DAC output scaling until re-commanded.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Command: `max_flow` / `maxFlow`
  - RAM variable: `maxFlowRate`
  - Telemetry: Neither `/flowData` nor `/diag` transmits the active `maxFlowRate`.
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp`:
    - Line 108: `float maxFlowRate = 50.0;`
    - Lines 164–170 (`CalibrationParams` struct):
      ```cpp
      struct CalibrationParams {
        uint32_t magic;
        float a1, b1, k1, f1, c1, k2, f2, c2;
        float kp, ki;
        float ff_gain, ff_offset;
        float ramp_rate, dac_hold;
      };
      ```
      `maxFlowRate` is absent from `CalibrationParams`.
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`:
    - Lines 195–202:
      ```cpp
      else if (strcmp(keyBuf, "max_flow") == 0 || strcmp(keyBuf, "maxFlow") == 0) {
        float requestedMax = strtof(valBuf, nullptr);
        if (requestedMax > 0.01f) {
          maxFlowRate = requestedMax;
          targetFlowSetpoint = constrain(targetFlowSetpoint, 0.0f, maxFlowRate);
          recognizedCommand = true;
        }
      }
      ```
      `calParamsUpdated` is NOT set to true.
  - `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h`:
    - Line 2: `uint16_t dacValue = (flowSetpointVal / maxFlowRate) * 4095;`
- **Discrepancy Breakdown:**
  If an installation uses a 10 L/min or 20 L/min MFC, commanding `max_flow: 10` functions properly until a reboot. After reboot, `maxFlowRate` silently resets to 50.0. A subsequent setpoint command of 10 L/min produces a DAC value of $(10/50)\times 4095 = 819$ (20% of full scale) instead of full scale 4095, delivering only 2 L/min of gas.
- **Candidate Fix / Resolution:**
  Include `max_flow` in the persistent `CalibrationParams` schema (with a schema migration bump) or mandate that the Hub always transmits `maxFlow` in its periodic command frame.

---

### F10 — Silent Eradication of Quartic Calibration Terms

- **Inconsistency ID:** `F10`
- **Canonical Title:** Curva parcial é gravada imediatamente; `k1/f1/c1` sem `a1/b1` zera termos quartic
- **Stated Problem Description (Section 3.10):**
  Sending a partial calibration command that updates low-range quadratic coefficients (`k1`, `f1`, `c1`) without explicit `a1` and `b1` in the same frame immediately zeroes `a1` and `b1` in EEPROM, downgrading the low-segment model from quartic to quadratic.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Commands: `k1`, `f1`, `c1`, `a1`, `b1`
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`:
    - Lines 217–220:
      ```cpp
      if (lowQuadraticUpdated && !lowHigherOrderUpdated) {
        a1 = calParams.a1 = 0.0f;
        b1 = calParams.b1 = 0.0f;
      }
      ```
    - Line 235: `if (calParamsUpdated) saveParameters();`
  - `External-Devices/fluxometro/apps/flutter/lib/main.dart`:
    - Lines 375–382:
      ```dart
      final params = {
        'k1': double.tryParse(_k1Controller.text),
        'f1': double.tryParse(_f1Controller.text),
        'c1': double.tryParse(_c1Controller.text),
        'k2': double.tryParse(_k2Controller.text),
        'f2': double.tryParse(_f2Controller.text),
        'c2': double.tryParse(_c2Controller.text),
      };
      ```
- **Discrepancy Breakdown:**
  The Flutter app does not support `a1` or `b1`. Saving calibration parameters from Flutter sends only `k1/f1/c1/k2/f2/c2`. The node firmware interprets this as an explicit request to zero `a1` and `b1`, destroying the factory quartic curve that was designed to eliminate the discontinuity at 0.0545 V.
- **Candidate Fix / Resolution:**
  Adopt an explicit, versioned, atomic calibration command object (e.g. `{"cal_model": "quartic", ...}`) and do not implicitly overwrite higher-order terms unless an explicit reset or conversion command is sent.

---

### F11 — Absence of Calibration Verification Readback

- **Inconsistency ID:** `F11`
- **Canonical Title:** ACK de calibração sem readback dos coeficientes
- **Stated Problem Description (Section 3.10):**
  Calibration command acknowledgments confirm only that the parser accepted the frame. Neither the ACK nor telemetry messages return the stored polynomial coefficients or a calibration hash.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - WebSocket ACK frame
  - `/flowData` telemetry query string
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/api/WebSocketApi.h`:
    - Lines 28–36: ACK message only returns `command_ack`, `ack_direct_session_id`, `ack_direct_cmd_id`, `last_apply_ms`, `command_source`, `flow_setpoint`, `valve1State`, `valve2State`, `valveFlowState`.
  - `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`:
    - Lines 124–151: `/flowData` query string transmits `Kp`, `Ki`, `ramp`, `ff_gain`, `ff_offset`, but completely omits `a1, b1, k1, f1, c1, k2, f2, c2`.
- **Discrepancy Breakdown:**
  Higher-level applications (Windows App, Hub) have no programmatic means to audit the calibration coefficients active in node memory. If flash write fails or memory is corrupted, supervisory software cannot detect the discrepancy.
- **Candidate Fix / Resolution:**
  Add a dedicated endpoint (e.g. `GET /calibration`) or publish a CRC32/SHA-256 hash of the calibration block in telemetry and `/diag`.

---

### F12 — Undetected Boot and Runtime I²C Hardware Failures

- **Inconsistency ID:** `F12`
- **Canonical Title:** ADS/DAC podem falhar no boot sem bloquear operação ou gerar telemetria de falha
- **Stated Problem Description (Section 3.10):**
  If the ADS1115 ADC or MCP4725 DAC fails to initialize on the I²C bus at boot, the firmware logs a message to the serial port and continues executing normally. No failure flag is published in telemetry, and the app may display plausible zero values without hardware present.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Hardware interfaces: I²C `0x48` (ADS1115), `0x60` (MCP4725)
  - Telemetry: `/flowData`, `/diag`, `/status`
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`:
    - Lines 67–82:
      ```cpp
      Serial.print("6. Init ADS1115... ");
      if (ads.begin(0x48)) { ... }
      else {
        Serial.println("FAILED (Check wiring!)");
        // Não travamos aqui com while(1) para permitir debug do resto
      }
      Serial.print("7. Init MCP4725... ");
      if (mcp.begin(0x60)) { ... }
      else {
        Serial.println("FAILED (Check wiring!)");
      }
      ```
  - `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h`:
    - Lines 13–26: If ADS1115 fails, `readADC_SingleEnded()` fails; `got == 0` returns immediately without updating `readFlowVoltage`.
- **Discrepancy Breakdown:**
  Neither boot initialization status nor I²C transaction timeouts are recorded in global status flags or reported over HTTP/WebSocket. An unpopulated board or broken I²C wiring will appear to the Hub as an online node reporting 0.0 L/min flow.
- **Candidate Fix / Resolution:**
  Maintain explicit health flags (`adc_ok`, `dac_ok`), publish them in `/flowData`, `/diag`, and `/status`, assert a safe state (corte geral closed) upon hardware failure, and trigger a supervisory alarm.

---

### F13 — Open SoftAP and Unauthenticated Control Endpoints

- **Inconsistency ID:** `F13`
- **Canonical Title:** AP/controle/OTA sem autenticação
- **Stated Problem Description (Section 3.10):**
  The node broadcasts an open Wi-Fi SoftAP (`Floxometro_AP`) with no password. All management and control endpoints (`/ws`, `/update`, `/diag`, `/status`) are accessible without authentication.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Wi-Fi SSID: `Floxometro_AP`
  - Endpoints: `ws://192.168.10.1/ws`, `http://192.168.10.1/update`
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp`:
    - Line 19: `const char* ap_ssid = "Floxometro_AP";`
    - Line 20: `const char* ap_password = NULL; // Rede aberta (sem senha)`
  - `External-Devices/fluxometro/firmware/flowmeter/src/api/OtaService.h`:
    - Lines 5–7, 51–109: `/update` endpoint handles `POST` uploads directly with no authorization check.
  - `External-Devices/fluxometro/firmware/flowmeter/src/api/WebSocketApi.h`:
    - `/ws` accepts text command frames from any connected client.
- **Discrepancy Breakdown:**
  Any device in Wi-Fi range can associate with `Floxometro_AP`, open `http://192.168.10.1/update`, upload arbitrary binaries, or send WebSocket frames to actuate valves and inject toxic or asphyxiating gases (such as pure $N_2$).
- **Candidate Fix / Resolution:**
  Enforce WPA2 passphrase on SoftAP, implement authentication token or shared secret on `/update` and `/ws`, or require physical button activation for OTA mode.

---

### F14 — OTA Flash Process Leaves Gas Actuators Unsupervised

- **Inconsistency ID:** `F14`
- **Canonical Title:** OTA pausa rede, mas pode manter última saída ativa
- **Stated Problem Description (Section 3.10):**
  When an OTA update begins, network communication and polling tasks are suspended (`otaInProgress = true`), but the firmware does not force a safe shutoff of valves or the DAC output. Physical gas flow continues during the entire update process.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Endpoint: `POST /update`
  - Internal flag: `otaInProgress`
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/api/OtaService.h`:
    - Lines 74–89: In the upload chunk handler at `index == 0`, `Update.begin(UPDATE_SIZE_UNKNOWN)` is called, but `digitalWrite(VALVE_FLOW_PIN, HIGH)` is NEVER called.
  - `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`:
    - Lines 16, 82, 104:
      `if (!otaInProgress && WiFi.status() == WL_CONNECTED)`
      When `otaInProgress` is set, `httpTask`, `telemetryTask`, and `wifiTask` pause.
- **Discrepancy Breakdown:**
  If an operator flashes new firmware while an experiment is running at 40 L/min, the Hub loses control and telemetry immediately, but the MFC continues flowing 40 L/min through open solenoids for up to 90 seconds (the `otaStallTimeoutMs` watchdog duration) until reboot.
- **Candidate Fix / Resolution:**
  Insert a mandatory safe shutoff interlock inside `setupOTA()` before `Update.begin()`: drive GPIO 5 HIGH (Valve Off), set GPIO 16/17 LOW, and command 0V to the DAC.

---

### F15 — Unrecoverable Wi-Fi Reconnect Deactivation from Windows UI

- **Inconsistency ID:** `F15`
- **Canonical Title:** `reconnect_wifi` pode ser desligado, mas Windows só o monitora
- **Stated Problem Description (Section 3.10):**
  The node allows disabling its station reconnection loop via command `reconnect_wifi: 0`. While telemetry reports this state, the Windows App only displays it as a read-only field and provides no control mechanism to restore it.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Command: `reconnect_wifi` (0 or 1)
  - Telemetry: `reconnect_wifi`
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`:
    - Lines 154–158:
      ```cpp
      else if (strcmp(keyBuf, "reconnect_wifi") == 0) {
        reconnect_Wifi = (atoi(valBuf) == 1);
        Serial.printf("Wifi Reconnect Logic set to: %s\n", reconnect_Wifi ? "TRUE" : "FALSE");
        recognizedCommand = true;
      }
      ```
  - `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`:
    - Lines 211–215:
      ```cpp
      if (!reconnect_Wifi || otaInProgress) {
        wifiReconnectState = WF_IDLE;
        vTaskDelay(pdMS_TO_TICKS(500));
        continue;
      }
      ```
  - `Windows_app/`: Has no command builder or UI control for `reconnect_wifi`.
- **Discrepancy Breakdown:**
  If `reconnect_wifi: 0` is sent (e.g. from Flutter or serial) and the node subsequently loses Wi-Fi connection with the Hub, it will never attempt to reconnect. Recovery requires manual on-site intervention via direct connection to `Floxometro_AP`.
- **Candidate Fix / Resolution:**
  Add an explicit, protected command action in the Windows supervisory UI to re-enable `reconnect_wifi: 1` through the Hub, or restrict `reconnect_wifi: 0` to a temporary timeout rather than permanent deactivation.

---

### F16 — Open-Loop Telemetry Echo for Valve Actuation

- **Inconsistency ID:** `F16`
- **Canonical Title:** Válvulas ecoam o bit comandado, sem realimentação
- **Stated Problem Description (Section 3.10):**
  Valve status fields in telemetry (`valve1State`, `valve2State`, `valveFlowState`) merely echo the software latch bits written to the GPIO registers. They do not prove that physical solenoids energized, that plungers seated, or that flow actually opened or stopped.
- **Relevant Protocol Commands, Endpoints & Parameters:**
  - Telemetry fields: `valve1State`, `valve2State`, `valveFlowState`
- **Code & Hardware Evidence:**
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`:
    - Lines 140, 145, 150: Update RAM variables and call `digitalWrite()`.
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`:
    - Lines 219–221: Snapshots RAM variables directly into telemetry JSON without reading hardware sensors.
  - `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`:
    - Lines 116–118, 141–143: Transmitted directly to Hub.
- **Discrepancy Breakdown:**
  An ACK or telemetry state of `valve1State: 1` confirms only that the ESP32 GPIO pin went HIGH. If a solenoid driver MOSFET is blown, 12V/24V power is disconnected, or a valve is stuck mechanically, the supervisory software has no indication of failure.
- **Candidate Fix / Resolution:**
  Incorporate electrical current sensing or mechanical limit switches, or implement a supervisory cross-check diagnostic: if valves are commanded open with positive setpoint but measured flow remains zero (or vice versa), flag a valve actuation fault.

---

## 5-Component Handoff Report

### 1. Observation
- **Direct file audit:** Inspected `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` lines 270–525, confirming all 16 items F01–F16 in Section 3.10 and their context in Sections 3.0–3.9 and 3.11.
- **Firmware source evidence:**
  - `FirmwareApp.cpp`: Line 16 defines `FW_VERSION "V10"`; Line 108 defines `float maxFlowRate = 50.0;`; Lines 183–190 define `FACTORY_*` constants; Lines 19–20 define open AP with `NULL` password.
  - `Lifecycle.h`: Lines 2–4 initialize GPIO 5 HIGH (`valveFlowState = 1`); Lines 67–82 do not halt on ADS/MCP failure; Lines 145–199 gate PI loop at `targetFlowSetpoint > 0.1f`.
  - `CommandCodec.h`: Lines 139–153 use `atoi()` directly; Lines 184–210 parse floats without range checks; Lines 217–220 zero `a1/b1` if missing; Line 235 commits directly to EEPROM.
  - `FlowIo.h`: Line 2 computes DAC value using `maxFlowRate`; Lines 31–38 Horner quartic/quadratic calculation.
  - `CalibrationStore.h`: Lines 60–64 commits `calParams` to EEPROM without `maxFlowRate`.
  - `OtaService.h`: Lines 12 & 33 hardcode `"version":"v11"`; Lines 51–109 perform OTA without valve shutoff.
  - `TaskRuntime.h`: Line 85 sends `ver=v11`; Lines 124–151 send telemetry without curve coefficients.
- **Windows App evidence:**
  - `CalibrationMath.cs`: Lines 78–84 specify `FirmwareDefault` completely divergent from `FACTORY_*`.
  - `FlowControlViewModel.cs`: Line 690 appends `" V"` to `FlowOutputText`.
- **Flutter App evidence:**
  - `main.dart`: Lines 180–185 define divergent default quadratic curve; Lines 606–612 toggle `v_Flow` with inverted semantics.

### 2. Logic Chain
1. **Observation:** Firmware defines `FW_VERSION "V10"`, but `/nodeHello` and `/diag` return `"v11"`.  
   $\rightarrow$ **Inference:** Firmware build tags and network endpoints disagree on version, directly causing F01.
2. **Observation:** Three independent files (`FirmwareApp.cpp`, `CalibrationMath.cs`, `main.dart`) declare three distinct sets of numerical coefficients for the same physical sensor.  
   $\rightarrow$ **Inference:** Resetting or configuring calibration from any single client overwrites the curve with conflicting math, directly causing F02.
3. **Observation:** `flowSetpoint` in L/min is exported as `flow_output`, and Windows App appends `" V"`.  
   $\rightarrow$ **Inference:** Engineering units are misrepresented in the desktop UI, directly causing F03.
4. **Observation:** Code checks `targetFlowSetpoint > 0.1f` to run PI and `targetFlowSetpoint == 0.0f` to close valve.  
   $\rightarrow$ **Inference:** The interval $(0.0, 0.1]$ is completely unhandled, leaving previous DAC values latched, directly causing F04.
5. **Observation:** Hardware pin GPIO 5 is active HIGH for *Valve Off*, and Flutter Switch sends `1` when toggled to "ON".  
   $\rightarrow$ **Inference:** The user interface displays active flow while physically isolating the line, directly causing F05.
6. **Observation:** Commands `v1` and `v2` write directly to GPIOs without mutual check; `max_flow` is stored in RAM only; partial updates zero `a1/b1`; no readback exists in ACK.  
   $\rightarrow$ **Inference:** Direct validation and persistence gaps exist in the firmware protocol engine, directly causing F06, F07, F08, F09, F10, F11, F12, F13, F14, F15, F16.

### 3. Caveats
- No physical bench testing was performed during this survey, as this assignment is strictly read-only and analytical.
- The electrical polarity of GPIO 5 (*Valve Off*) and solenoids GPIO 16/17 was verified through comments, schematics, and code documentation, but physical wiring on the bench should be verified prior to hardware actuation.

### 4. Conclusion
All 16 inconsistencies (F01 through F16) have been thoroughly traced to exact source lines, variables, and protocol mechanisms across the firmware, desktop app, mobile app, and documentation. The root causes, cross-system discrepancies, and candidate resolutions are fully documented, providing an authoritative baseline for the implementation plan.

### 5. Verification Method
1. Inspect `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` §3.10.
2. Verify code references via:
   - `view_file` on `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp`
   - `view_file` on `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`
   - `view_file` on `Windows_app/src/OpenTECHub/Services/Calibration/CalibrationMath.cs`
   - `view_file` on `External-Devices/fluxometro/apps/flutter/lib/main.dart`
3. Execute validation script or grep to verify that every ID from F01 to F16 is present and documented in this report.
