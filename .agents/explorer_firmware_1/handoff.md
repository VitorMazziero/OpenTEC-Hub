# Handoff Report: Firmware Codebase Exploration (F01–F16)

**Agent**: `explorer_firmware_1`  
**Date**: 2026-09-13  
**Working Directory**: `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_firmware_1`  
**Subject**: Technical investigation of the 16 inconsistencies (F01 to F16) listed in Section 3.10 of `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` against the active firmware codebase (`External-Devices/fluxometro/firmware/flowmeter/`).

---

## 1. Observation

Direct code observations from the active firmware codebase (`External-Devices/fluxometro/firmware/flowmeter/`), related documentation (`External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md`, `External-Devices/fluxometro/docs/PROTOCOL.md`), Hub codebase (`ESP32S3-HUB/`), and client applications (`Windows_app/`, `External-Devices/fluxometro/apps/flutter/`).

---

### F01 — Identidade V10 no build/OTA e v11 no protocolo/endpoints

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp`: lines 16–17, 58
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`: line 10
  - `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`: line 85
  - `External-Devices/fluxometro/firmware/flowmeter/src/api/OtaService.h`: lines 12, 33
  - `External-Devices/fluxometro/docs/PROTOCOL.md`: lines 3, 20
- **Verbatim Code & Observations**:
  - In `FirmwareApp.cpp`:
    ```cpp
    16: #define FW_VERSION "V10"
    17: #define FW_BUILD FW_VERSION " (" __DATE__ " " __TIME__ ")"
    ...
    58: <p>Running: <b>)rawliteral" FW_BUILD R"rawliteral(</b></p>
    ```
  - In `Lifecycle.h`:
    ```cpp
    10: Serial.println("Firmware " FW_BUILD);
    ```
  - In `TaskRuntime.h` (function `telemetryTask`):
    ```cpp
    85: snprintf(helloUrl, sizeof(helloUrl), "%s/nodeHello?dev=flowmeter&ver=v11&mac=%s",
    86:          sensorHubURL.c_str(), WiFi.macAddress().c_str());
    ```
  - In `OtaService.h` (endpoints `/diag` and `/status`):
    ```cpp
    11: snprintf(json, sizeof(json),
    12:          "{\"device\":\"flowmeter\",\"version\":\"v11\",\"uptime_s\":%lu,"
    ...
    32: snprintf(json, sizeof(json),
    33:          "{\"device\":\"flowmeter\",\"version\":\"v11\",\"uptime_s\":%lu,"
    ```
- **Current Implementation Logic & Root Cause**:
  `FW_VERSION` is defined as `"V10"` in `FirmwareApp.cpp`, used by serial boot banners and the `/update` HTML page. However, the runtime telemetry task (`/nodeHello`) and the HTTP endpoints `/diag` and `/status` hardcode the literal string `"v11"`. There is no single canonical constant used across all endpoints, resulting in conflicting version reporting.

---

### F02 — Curva `FACTORY_*` do firmware, `CalibrationMath.FirmwareDefault`/pontos certificados do Windows e defaults do Flutter são diferentes

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp`: lines 183–196
  - `Windows_app/src/OpenTECHub/Services/Calibration/CalibrationMath.cs`: lines 78–84
  - `External-Devices/fluxometro/archive/apps/flutter-pre-v05/lib/main.dart`: lines 151–156
- **Verbatim Code & Observations**:
  - In firmware `FirmwareApp.cpp`:
    ```cpp
    183: const float FACTORY_A1 = -1353785.3f;
    184: const float FACTORY_B1 = 246663.69f;
    185: const float FACTORY_K1 = -16473.492f;
    186: const float FACTORY_F1 = 484.99466f;
    187: const float FACTORY_C1 = -4.6159464f;
    188: const float FACTORY_K2 = -0.46260458f;
    189: const float FACTORY_F2 = 10.797299f;
    190: const float FACTORY_C2 = 0.28475793f;
    ```
  - In `Windows_app` (`CalibrationMath.cs`):
    ```csharp
    78: public static FlowCalibrationCurve FirmwareDefault { get; } = new(
    79:     new PolynomialCalibration(462.893536740, 43.294432104, -0.464367483)
    80:     {
    81:         A = 321791.345936369,
    82:         B = -32589.073104291,
    83:     },
    84:     new PolynomialCalibration(-0.854551899, 11.814453070, 0.192231954));
    ```
  - In Flutter (`flutter-pre-v05/lib/main.dart`):
    ```dart
    151: _k1Controller.text = '-139.0570077428';
    152: _f1Controller.text = '21.9738888302';
    153: _c1Controller.text = '-0.0341880209';
    154: _k2Controller.text = '-0.8724324917';
    155: _f2Controller.text = '10.6573301479';
    156: _c2Controller.text = '0.1953756879';
    ```
- **Current Implementation Logic & Root Cause**:
  Three different calibration fittings from different project development phases coexist across codebases: Flutter retains the pre-v05 quadratic-quadratic fit; Windows App retains the V05 quartic-anchored fit; and firmware `FirmwareApp.cpp` was updated in V09/V10 with a third fit (`FACTORY_*`). When an operator restores defaults in Windows or uploads parameters from Flutter or resets EEPROM, conflicting calibration polynomials are applied to the same physical sensor.

---

### F03 — `FlowOutput` é equivalente L/min, mas Windows mostra `V`

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h`: lines 1–4
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`: lines 177–183, 217, 230
  - `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`: lines 116, 127
  - `Windows_app/src/OpenTECHub/ViewModels/FlowControlViewModel.cs`: line 690
  - `Windows_app/docs/PROTOCOL.md`: line 281
- **Verbatim Code & Observations**:
  - In `FlowIo.h`:
    ```cpp
    1: bool writeFlowSetpointToDAC(float flowSetpointVal) {
    2:   uint16_t dacValue = (flowSetpointVal / maxFlowRate) * 4095;
    3:   dacValue = constrain(dacValue, 0, 4095);
    ```
  - In `Lifecycle.h`:
    ```cpp
    177: float newFlowSetpoint = constrain(flowFeedforward + P_term + I_term, 0.0f, maxFlowRate);
    ...
    182: if (writeFlowSetpointToDAC(newFlowSetpoint)) flowSetpoint = newFlowSetpoint;
    ...
    217: snapOutput = flowSetpoint;
    ...
    230: ",\"flow_setpoint\":%.6f,\"flow_setpoint_corrected\":%.6f,\"flow_output\":%.6f,\"valve1State\":%d"
    ```
  - In `TaskRuntime.h`:
    ```cpp
    116: snapOutput = flowSetpoint;
    ...
    127: "&flow_setpoint=%.6f&flow_setpoint_corrected=%.6f&flow_output=%.6f"
    ```
  - In `Windows_app` (`FlowControlViewModel.cs`):
    ```csharp
    690: FlowOutputText = snapshot.FlowOutput is { } vo ? vo.ToString("F2", CultureInfo.CurrentCulture) + " V" : "—";
    ```
- **Current Implementation Logic & Root Cause**:
  In firmware, `flow_output` is `flowSetpoint`, which is the PI controller output constrained to `0.0f .. maxFlowRate` (i.e. units of **L/min**). The Windows App protocol documentation incorrectly documented this field as "analog output voltage (V)", and the UI appends `" V"`. The firmware is not emitting Volts; the client is misinterpreting the unit.

---

### F04 — 0 < alvo ≤ 0,1 não fecha a linha nem roda PI

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`: lines 168–179
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`: lines 155–199
- **Verbatim Code & Observations**:
  - In `CommandCodec.h`:
    ```cpp
    168: if (fabs(newTarget - targetFlowSetpoint) > 0.001f || newTarget == 0.0f || targetFlowSetpoint == 0.0f) {
    169:   targetFlowSetpoint = newTarget;
    170:   if (targetFlowSetpoint == 0.0f) {
    171:     valveFlowState = 1;
    172:     digitalWrite(VALVE_FLOW_PIN, HIGH);
    173:     if (!dacHold) {
    174:       flowSetpoint = 0.0f;
    175:       rampedTarget = 0.0f;
    176:       writeFlowSetpointToDAC(0.0f);
    177:     }
    178:   }
    ```
  - In `Lifecycle.h`:
    ```cpp
    155: if (targetFlowSetpoint > 0.1f && valveFlowState == 0) {
    ... // PI controller executes, updates flowSetpoint and writes DAC
    190: } else if (targetFlowSetpoint > 0.1f) {
    191:   // Live target but Valve Off asserted: no flow can exist... Integral and DAC stay frozen
    192: } else if (!dacHold) {
    193:   flowFeedforward = 0.0f;
    194:   if (flowSetpoint > 0) {
    195:     if (writeFlowSetpointToDAC(0)) flowSetpoint = 0;
    196:   }
    197: }
    ```
- **Current Implementation Logic & Root Cause**:
  When a target is commanded in the range `0.0 < targetFlowSetpoint <= 0.1f`:
  1. `CommandCodec.h` line 170 only asserts `VALVE_FLOW_PIN = HIGH` if `targetFlowSetpoint == 0.0f`. Since `newTarget > 0.0f`, line 171 does not execute.
  2. In `Lifecycle.h`, line 155 checks `targetFlowSetpoint > 0.1f`. Since target is `<= 0.1f`, the PI loop does not execute.
  3. Line 190 checks `targetFlowSetpoint > 0.1f`, which fails.
  4. Line 192 checks `else if (!dacHold)`. Since `dacHold` defaults to `true`, this branch is skipped.
  **Result**: The cut-off valve is NOT closed, the PI loop does NOT run, the DAC is NOT updated, and whatever previous DAC voltage was on the MCP4725 remains active, leaving gas flowing at the prior setpoint while the system appears to have accepted a near-zero setpoint.

---

### F05 — Setpoint direto positivo não abre `v_Flow`; toggle Flutter “ON” fecha

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`: lines 140–143, 168–179
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`: lines 2–4
  - `External-Devices/fluxometro/apps/flutter/lib/main.dart`: lines 570–573, 607–612
- **Verbatim Code & Observations**:
  - In `Lifecycle.h`:
    ```cpp
    2: pinMode(VALVE_FLOW_PIN, OUTPUT);
    3: digitalWrite(VALVE_FLOW_PIN, HIGH);
    4: valveFlowState = 1;
    ```
  - In `CommandCodec.h`:
    ```cpp
    140: if (strcmp(keyBuf, "v_Flow") == 0 || strcmp(keyBuf, "valveFlow") == 0) {
    141:   valveFlowState = (atoi(valBuf) != 0);
    142:   digitalWrite(VALVE_FLOW_PIN, valveFlowState);
    143:   recognizedCommand = true;
    144: }
    ...
    170: if (targetFlowSetpoint == 0.0f) {
    171:   valveFlowState = 1;
    172:   digitalWrite(VALVE_FLOW_PIN, HIGH);
    ```
  - In Flutter `main.dart`:
    ```dart
    570: 'Flow Valve State',
    571: valveFlowState == 1 ? 'ON' : 'OFF',
    572: valueColor: valveFlowState == 1 ? Colors.green : Colors.red,
    ...
    607: SwitchListTile(
    608:   title: const Text('Flow Valve'),
    609:   value: valveFlowState == 1,
    610:   onChanged: _isConnected
    611:       ? (val) => _updateValveState('v_Flow', val)
    612:       : null,
    613: ),
    ```
- **Current Implementation Logic & Root Cause**:
  `VALVE_FLOW_PIN` (GPIO 5) drives the Omega MFC *Valve Off* input. Electrical logic is active-high cut-off: `HIGH (1)` shuts off gas flow; `LOW (0)` permits gas flow.
  1. In firmware, commanding `flow_setpoint > 0` alone never resets `valveFlowState = 0` or writes `digitalWrite(VALVE_FLOW_PIN, LOW)`. Since the node boots with `valveFlowState = 1`, commanding a setpoint directly via WebSocket or Serial produces no flow until an explicit `{"v_Flow": 0}` is also delivered.
  2. In the Flutter app, toggling "Flow Valve" switch to `ON (val = true)` sends `v_Flow: 1`, which asserts *Valve Off* (closing the line) while displaying a green "ON" label.

---

### F06 — Sem intertravamento de rota no nó

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`: lines 140–153
- **Verbatim Code & Observations**:
  ```cpp
  140: if (strcmp(keyBuf, "v_Flow") == 0 || strcmp(keyBuf, "valveFlow") == 0) {
  141:   valveFlowState = (atoi(valBuf) != 0);
  142:   digitalWrite(VALVE_FLOW_PIN, valveFlowState);
  ...
  145: else if (strcmp(keyBuf, "v1") == 0 || strcmp(keyBuf, "valve_1") == 0) {
  146:   valve1State = (atoi(valBuf) != 0);
  147:   digitalWrite(VALVE1_PIN, valve1State);
  ...
  150: else if (strcmp(keyBuf, "v2") == 0 || strcmp(keyBuf, "valve_2") == 0) {
  151:   valve2State = (atoi(valBuf) != 0);
  152:   digitalWrite(VALVE2_PIN, valve2State);
  ```
- **Current Implementation Logic & Root Cause**:
  `valveFlowState`, `valve1State`, and `valve2State` are written independently and directly to GPIOs 5, 17, and 16. The firmware accepts invalid or hazardous pneumatic states without validation:
  - Dual open (`v1 == 1 && v2 == 1`): simultaneously feeds reactor and discharge.
  - Blocked line (`v_Flow == 0 && v1 == 0 && v2 == 0` with `targetFlowSetpoint > 0`): gas is driven against closed solenoids, building up pressure in the manifold.
  Route interlocking currently exists solely on the Windows App client side (`GasRouting.cs`).

---

### F07 — `Kp/Ki/FF/curva` sem validação de faixa/finitude

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`: lines 184–210, 235
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`: lines 165–177
- **Verbatim Code & Observations**:
  - In `CommandCodec.h`:
    ```cpp
    184: else if (strcmp(keyBuf, "kp_flow") == 0) { Kp_flow = calParams.kp = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    185: else if (strcmp(keyBuf, "ki_flow") == 0) { Ki_flow = calParams.ki = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    186: else if (strcmp(keyBuf, "ff_gain") == 0) { ffGain = calParams.ff_gain = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    187: else if (strcmp(keyBuf, "ff_offset") == 0) { ffOffset = calParams.ff_offset = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    ...
    203: else if (strcmp(keyBuf, "a1") == 0) { a1 = calParams.a1 = strtof(valBuf, nullptr); lowHigherOrderUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
    ...
    235: if (calParamsUpdated) saveParameters();
    ```
- **Current Implementation Logic & Root Cause**:
  Parameters parsed via `strtof` are assigned directly to runtime variables and `calParams` without checking `isfinite()` or verifying operational ranges. If `NaN`, `Infinity`, negative numbers, or arbitrarily large values are submitted, they are immediately committed to EEPROM via `saveParameters()`. In `Lifecycle.h`, arithmetic with `NaN` poisons `P_term`, `I_term`, and `newFlowSetpoint`, crashing the control loop or producing unpredictable DAC outputs, persisting even across reboots.

---

### F08 — Parser permissivo aplica parcialmente JSON malformado e `true` vira 0 via `atoi`

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`: lines 99–156, 190
- **Verbatim Code & Observations**:
  ```cpp
  140: valveFlowState = (atoi(valBuf) != 0);
  ...
  145: valve1State = (atoi(valBuf) != 0);
  ...
  150: valve2State = (atoi(valBuf) != 0);
  ...
  155: reconnect_Wifi = (atoi(valBuf) == 1);
  ...
  190: dacHold = (atoi(valBuf) != 0);
  ```
- **Current Implementation Logic & Root Cause**:
  1. The custom parser scans tokens and converts values using standard C `atoi(valBuf)`. When standard JSON boolean values are passed (e.g. `{"valve_1": true}` or `{"reconnect_wifi": true}`), `atoi("true")` encounters the non-digit character `'t'` and returns `0`. Consequently, sending `true` sets `valve1State = 0` (OFF) and `reconnect_Wifi = false`!
  2. The parser applies mutations to hardware variables sequentially as each key is found in the buffer. If subsequent tokens in the payload are corrupt or malformed, the earlier keys have already been written to the GPIOs, resulting in partial application.

---

### F09 — `max_flow` não persiste

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp`: lines 108, 164–171
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`: lines 195–202
  - `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h`: line 2
- **Verbatim Code & Observations**:
  - In `FirmwareApp.cpp`:
    ```cpp
    108: float maxFlowRate = 50.0;
    ...
    164: struct CalibrationParams {
    165:   uint32_t magic;
    166:   float a1, b1, k1, f1, c1, k2, f2, c2;
    167:   float kp, ki;
    168:   float ff_gain, ff_offset;
    169:   float ramp_rate, dac_hold;
    170: };
    ```
  - In `CommandCodec.h`:
    ```cpp
    195: else if (strcmp(keyBuf, "max_flow") == 0 || strcmp(keyBuf, "maxFlow") == 0) {
    196:   float requestedMax = strtof(valBuf, nullptr);
    197:   if (requestedMax > 0.01f) {
    198:     maxFlowRate = requestedMax;
    199:     targetFlowSetpoint = constrain(targetFlowSetpoint, 0.0f, maxFlowRate);
    200:     recognizedCommand = true;
    201:   }
    202: }
    ```
- **Current Implementation Logic & Root Cause**:
  `maxFlowRate` is a volatile RAM variable initialized to `50.0`. It is not included in `struct CalibrationParams` and is never saved to EEPROM. On reboot (power loss, watchdog, or OTA), `maxFlowRate` resets to 50.0. If the node is connected to a 10 L/min or 20 L/min MFC, the DAC scaling `dacValue = (flowSetpointVal / maxFlowRate) * 4095` becomes completely distorted until a new `max_flow` command is issued.

---

### F10 — Curva parcial é gravada imediatamente; `k1/f1/c1` sem `a1/b1` zera termos quartic

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`: lines 203–220, 235
- **Verbatim Code & Observations**:
  ```cpp
  203: else if (strcmp(keyBuf, "a1") == 0) { a1 = calParams.a1 = strtof(valBuf, nullptr); lowHigherOrderUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
  204: else if (strcmp(keyBuf, "b1") == 0) { b1 = calParams.b1 = strtof(valBuf, nullptr); lowHigherOrderUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
  205: else if (strcmp(keyBuf, "k1") == 0) { k1 = calParams.k1 = strtof(valBuf, nullptr); lowQuadraticUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
  206: else if (strcmp(keyBuf, "f1") == 0) { f1 = calParams.f1 = strtof(valBuf, nullptr); lowQuadraticUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
  207: else if (strcmp(keyBuf, "c1") == 0) { c1 = calParams.c1 = strtof(valBuf, nullptr); lowQuadraticUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
  ...
  217: if (lowQuadraticUpdated && !lowHigherOrderUpdated) {
  218:   a1 = calParams.a1 = 0.0f;
  219:   b1 = calParams.b1 = 0.0f;
  220: }
  ...
  235: if (calParamsUpdated) saveParameters();
  ```
- **Current Implementation Logic & Root Cause**:
  Lines 217–220 contain a legacy compatibility heuristic: if any low-curve quadratic key (`k1`, `f1`, `c1`) is received in a payload without `a1` or `b1`, the firmware assumes a quadratic model and unconditionally overwrites `a1 = b1 = 0.0f`. If a client merely updates `c1` (zero-offset trim), the quartic model is erased. Furthermore, receiving any single key immediately marks `calParamsUpdated = true` and overwrites EEPROM.

---

### F11 — ACK de calibração sem readback dos coeficientes

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/api/WebSocketApi.h`: lines 27–37
  - `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`: lines 125–151
- **Verbatim Code & Observations**:
  - In `WebSocketApi.h`:
    ```cpp
    28: snprintf(ackMessage, sizeof(ackMessage),
    29:          "{\"command_ack\":%s,\"ack_direct_session_id\":%lu"
    30:          ",\"ack_direct_cmd_id\":%lu"
    31:          ",\"last_apply_ms\":%lu,\"command_source\":\"direct\""
    32:          ",\"flow_setpoint\":%.6f,\"valve1State\":%d"
    33:          ",\"valve2State\":%d,\"valveFlowState\":%d}",
    ```
  - In `TaskRuntime.h`:
    ```cpp
    125: snprintf(url, sizeof(url),
    126:          "%s/flowData?seconds=%.3f&flow_voltage=%.6f&flow_rate=%.6f"
    127:          "&flow_setpoint=%.6f&flow_setpoint_corrected=%.6f&flow_output=%.6f"
    128:          "&ff_gain=%.4f&ff_offset=%.4f&valve1State=%u&valve2State=%u"
    129:          "&valveFlowState=%u&ack_cmd_id=%lu&last_apply_ms=%lu"
    130:          "&command_source=%s&boot_id=%lu&reconnect_wifi=%d"
    131:          "&kp=%.4f&ki=%.4f&ramp=%.3f",
    ```
- **Current Implementation Logic & Root Cause**:
  Neither the WebSocket ACK nor the `/flowData` telemetry stream includes the eight calibration coefficients (`a1..c2`) or a calibration checksum/CRC. The `command_ack:true` or `ack_cmd_id` merely confirms that the incoming JSON string was parsed; it does not confirm what values were actually written to EEPROM.

---

### F12 — ADS/DAC podem falhar no boot sem bloquear operação ou gerar telemetria de falha

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`: lines 67–82
  - `External-Devices/fluxometro/firmware/flowmeter/src/hardware/FlowIo.h`: lines 1–10, 13–26
  - `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`: lines 124–151
- **Verbatim Code & Observations**:
  - In `Lifecycle.h`:
    ```cpp
    67: Serial.print("6. Init ADS1115... ");
    68: if (ads.begin(0x48)) {
    ...
    72: } else {
    73:   Serial.println("FAILED (Check wiring!)");
    74:   // Não travamos aqui com while(1) para permitir debug do resto
    75: }
    77: Serial.print("7. Init MCP4725... ");
    78: if (mcp.begin(0x60)) {
    ...
    81: } else {
    82:   Serial.println("FAILED (Check wiring!)");
    83: }
    ```
  - In `FlowIo.h`:
    ```cpp
    4: if (i2cMutex != NULL && xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(250)) == pdTRUE) {
    5:   mcp.setVoltage(dacValue, false);
    6:   xSemaphoreGive(i2cMutex);
    7:   return true;
    8: }
    ```
- **Current Implementation Logic & Root Cause**:
  If `ads.begin()` or `mcp.begin()` fails at boot, the error is printed to the serial console, but no health status flags are recorded. In `FlowIo.h`, `writeFlowSetpointToDAC` returns `true` as long as it acquires `i2cMutex`, even if the MCP4725 is missing. Telemetry reports normal status, and `readFlowVoltage` reports filtered zero/floating values, presenting a false normal status on supervisory dashboards when hardware is dead.

---

### F13 — AP/controle/OTA sem autenticação

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp`: lines 19–20, 40–41
  - `External-Devices/fluxometro/firmware/flowmeter/src/api/OtaService.h`: lines 4–110
  - `External-Devices/fluxometro/firmware/flowmeter/src/api/WebSocketApi.h`: lines 1–41
- **Verbatim Code & Observations**:
  - In `FirmwareApp.cpp`:
    ```cpp
    19: const char* ap_ssid = "Floxometro_AP";
    20: const char* ap_password = NULL; // Rede aberta (sem senha)
    ...
    40: AsyncWebServer server(80);
    41: AsyncWebSocket ws("/ws");
    ```
- **Current Implementation Logic & Root Cause**:
  `Floxometro_AP` is an open Wi-Fi network with null password. Anyone within Wi-Fi range can connect, establish a WebSocket connection on `/ws` to actuate valves and gas flow, query `/diag`, or upload arbitrary binary files to `/update`.

---

### F14 — OTA pausa rede, mas pode manter última saída ativa

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/api/OtaService.h`: lines 74–94
  - `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`: lines 16, 104, 211
- **Verbatim Code & Observations**:
  - In `OtaService.h`:
    ```cpp
    74: [](AsyncWebServerRequest *request, String filename, size_t index, uint8_t *data, size_t len, bool final) {
    75:   if (index == 0) {
    ...
    88:     if (!Update.begin(UPDATE_SIZE_UNKNOWN)) Update.printError(Serial);
    89:   }
    90:   if (otaRejectReason.length()) return;
    91:   otaLastChunkMs = millis();
    92:   otaInProgress = true;
    ```
  - In `TaskRuntime.h`:
    ```cpp
    16: if (!otaInProgress && WiFi.status() == WL_CONNECTED) { // Hub command polling paused
    ...
    104: if (!otaInProgress && WiFi.status() == WL_CONNECTED && ... // Telemetry paused
    ```
- **Current Implementation Logic & Root Cause**:
  When an OTA update starts (`index == 0`), `otaInProgress` is asserted, pausing `httpTask` and `telemetryTask`. However, `setupOTA()` does not change `valveFlowState`, `valve1State`, `valve2State`, or the MCP4725 DAC output. If gas was flowing at 20 L/min when an OTA upload begins, gas continues to flow unchecked for the entire 30–90 seconds of the flashing process. If the upload stalls or fails, the valve remains energized indefinitely.

---

### F15 — `reconnect_wifi` pode ser desligado, mas Windows só o monitora

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`: lines 154–158
  - `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`: lines 211–215
- **Verbatim Code & Observations**:
  - In `CommandCodec.h`:
    ```cpp
    154: else if (strcmp(keyBuf, "reconnect_wifi") == 0) {
    155:   reconnect_Wifi = (atoi(valBuf) == 1);
    156:   Serial.printf("Wifi Reconnect Logic set to: %s\n", reconnect_Wifi ? "TRUE" : "FALSE");
    157:   recognizedCommand = true;
    158: }
    ```
  - In `TaskRuntime.h`:
    ```cpp
    211: if (!reconnect_Wifi || otaInProgress) {
    212:   wifiReconnectState = WF_IDLE;
    213:   vTaskDelay(pdMS_TO_TICKS(500));
    214:   continue;
    215: }
    ```
- **Current Implementation Logic & Root Cause**:
  Setting `reconnect_wifi = 0` puts `wifiTask` into idle. If station Wi-Fi drops, the node never attempts reconnection. While the Hub and Windows App receive `reconnect_wifi` in telemetry, the Windows App provides no UI action or command builder to re-enable it.

---

### F16 — Válvulas ecoam o bit comandado, sem realimentação

- **Target Files & Locations**:
  - `External-Devices/fluxometro/firmware/flowmeter/src/protocol/CommandCodec.h`: lines 140–153
  - `External-Devices/fluxometro/firmware/flowmeter/src/core/Lifecycle.h`: lines 219–221, 230–231
  - `External-Devices/fluxometro/firmware/flowmeter/src/tasks/TaskRuntime.h`: lines 116–118, 128
- **Verbatim Code & Observations**:
  - In `Lifecycle.h`:
    ```cpp
    219: snapValve1 = valve1State;
    220: snapValve2 = valve2State;
    221: snapValveFlow = valveFlowState;
    ...
    230: ",\"flow_setpoint\":%.6f,\"flow_setpoint_corrected\":%.6f,\"flow_output\":%.6f,\"valve1State\":%d"
    231: ",\"valve2State\":%d,\"valveFlowState\":%d"
    ```
- **Current Implementation Logic & Root Cause**:
  The telemetry fields `valve1State`, `valve2State`, and `valveFlowState` simply reflect the last written GPIO state. There is no electrical current sensing, auxiliary limit switch, or feedback pin on the solenoids. An ACK proves the MCU pin was set; it does not prove the coil energized, the plunger actuated, or gas flowed.

---

## 2. Logic Chain

From these observations, we trace the step-by-step logic to determine root causes, feasibility, proposed changes, and subsystem boundaries:

```
[Observation F01] Literal "v11" vs macro "V10"
   │
   └──> Step 1: Decentralized version strings in code.
        Trivial to unify in firmware via single canonical macro.

[Observation F02] 3 divergent curve fits in codebase
   │
   └──> Step 2: Firmware applyFactoryCurve() uses V10 coefficients; Windows uses V05; Flutter uses pre-v05.
        Restoring defaults creates conflicting flow math.
        Must elect single authoritative bench dataset and synchronize all three codebases.

[Observation F03] flow_output is clamped L/min, Windows appends " V"
   │
   └──> Step 3: Firmware correctly outputs controller effort in L/min.
        Windows protocol doc and UI mislabeled it as Volts.
        Fix belongs in Windows App UI label/suffix; firmware should remain in L/min to avoid breaking contracts.

[Observation F04] 0 < target <= 0.1 bypasses PI AND cut-off valve
   │
   └──> Step 4: Logic hole between CommandCodec.h (only sets valveFlowState=1 if target == 0.0)
        and Lifecycle.h (only runs PI if target > 0.1).
        Leaves stale DAC and open valve. High safety impact.
        Firmware fix: normalize target <= 0.1 as 0.0f (or execute controller down to zero).

[Observation F05] Direct setpoint doesn't reset valveFlowState; Flutter toggle inverted
   │
   └──> Step 5: GPIO 5 is active-high cut-off (Valve Off).
        Node boots with valveFlowState=1. Commanding setpoint without v_Flow keeps valve closed.
        Flutter sends v_Flow=1 for "ON", closing the valve.
        Fix: Firmware auto-opens v_Flow on valid positive setpoint if v_Flow omitted; Flutter UI inverted.

[Observation F06] Independent GPIO writes for v1, v2, v_Flow
   │
   └──> Step 6: Firmware has no manifold safety state machine.
        Allows dual routes or dead-end pressurization.
        Firmware fix: Add route validation protecting against invalid combinations while preserving all-off Safe Stop.

[Observation F07] Raw strtof without isfinite() or range clamping, immediate save
   │
   └──> Step 7: NaN/Inf permanently corrupts EEPROM and crashes PI math.
        Firmware fix: Strict range and finitude validation before updating calParams and EEPROM.

[Observation F08] atoi("true") returns 0; incremental JSON parsing
   │
   └──> Step 8: Standard boolean literals turn valves OFF. Corrupt packets partially apply.
        Firmware fix: Handle "true"/"false" strings and staging struct for atomic commit.

[Observation F09] maxFlowRate only in RAM (defaults to 50.0)
   │
   └──> Step 9: Reboots cause 5x DAC scaling error if MFC is 10 L/min.
        Firmware fix: Persist max_flow in CalibrationParams (schema v6) or rely on Hub desired-state.

[Observation F10] lowQuadraticUpdated zeroes a1 and b1
   │
   └──> Step 10: Updating single key c1 erases quartic curve.
        Firmware fix: Remove automatic zeroing of higher-order terms; require explicit curve configuration.

[Observation F11] Telemetry lacks curve coefficients
   │
   └──> Step 11: High telemetry frequency prevents echoing 8 floats every 500 ms.
        Firmware fix: Echo 32-bit CRC in telemetry and provide dedicated /calibration read endpoint.

[Observation F12] ads.begin() and mcp.begin() errors ignored
   │
   └──> Step 12: Silent hardware failure reports normal status.
        Firmware fix: Health flags (adsOk, dacOk), safe stop on failure, error telemetry.

[Observation F13] Open AP and unauthenticated endpoints
   │
   └──> Step 13: Local network attack surface.
        Firmware fix: Standard WPA2 password on SoftAP; token or maintenance switch for OTA.

[Observation F14] OTA doesn't close valves or zero DAC
   │
   └──> Step 14: Gas flows uncontrolled during flashing.
        Firmware fix: Force Safe Stop (valves off, DAC zero) in Update.begin().

[Observation F15] reconnect_wifi=0 strands node
   │
   └──> Step 15: Disabling reconnection prevents Hub recovery.
        Fix: Safety timeout in firmware; administrative re-enable button in App.

[Observation F16] No physical feedback from solenoids
   │
   └──> Step 16: Hardware limitation (no auxiliary contacts/current sensors).
        Firmware fix: Soft-diagnostic inferring valve fault from flow sensor readings.
```

---

## 3. Caveats

1. **Physical Bench Access**: No physical test was conducted in this exploration phase; all analyses are based strictly on static code analysis of firmware, Hub, App, and protocol documentation.
2. **Schema Migration Compatibility**: Adding `max_flow` to `CalibrationParams` in firmware requires bumping `CALIBRATION_MAGIC` (to schema v6). Older boards flashed with new firmware will execute a migration step that re-seeds factory defaults unless migration logic preserves existing coefficients.
3. **Bandwidth Ceiling on Hub Bus**: Echoing full calibration coefficients in `/flowData` or `/readData` would exceed the 2.6 KB frame ceiling established in `Windows_app/docs/PROTOCOL.md`. The recommendation for F11 is to echo a CRC32 or provide an on-demand endpoint.
4. **Active-High Cut-off Verification**: The analysis of GPIO 5 (*Valve Off*) is based on the Omega FMA-5400 manual and Section 3.1 of `COMANDOS_DISPOSITIVOS_EXTERNOS.md`. Bench testing is required to confirm physical polarity of the installed solenoid hardware.

---

## 4. Conclusion & Item-by-Item Assessment

### Comprehensive Summary Matrix

| ID | Title | Firmware Source Location | Root Cause in Firmware | Recommended Fix Location | Feasibility in Firmware | Technical Recommendation / Trade-offs |
|---|---|---|---|:---:|:---:|---|
| **F01** | Version Mismatch | `FirmwareApp.cpp:16`, `TaskRuntime.h:85`, `OtaService.h:12,33` | Hardcoded `"v11"` literals in tasks vs `#define FW_VERSION "V10"` | Firmware + Docs | **Immediate (Trivial)** | Unify on a single macro `FW_VERSION "v11"` across all files, serial logs, OTA, and `/nodeHello`. |
| **F02** | Calibration Divergence | `FirmwareApp.cpp:183-196` | Out-of-sync `FACTORY_*` constants vs Windows V05 vs Flutter pre-v05 | Firmware + App + Docs | **High** | Elect single authoritative certified calibration dataset, re-calculate quartic/quadratic fit, and synchronize all three codebases. |
| **F03** | `FlowOutput` Unit | `FlowIo.h:1-4`, `Lifecycle.h:177,230` | Firmware outputs L/min; Windows App assumes Volts (`" V"`) | **Windows App** | **Inadvisable in FW** | **Do not change firmware.** Firmware correctly reports controller output in L/min. Fix Windows App UI label/suffix (`" L/min"`). |
| **F04** | Low Setpoint Bug (0 < SP ≤ 0.1) | `CommandCodec.h:170`, `Lifecycle.h:155,190` | `target <= 0.1` skips PI loop, but cut-off valve only closes if `target == 0.0` | **Firmware** | **Immediate (Critical)** | In `CommandCodec.h` and `Lifecycle.h`, treat any setpoint `< 0.1f` as `0.0f` (assert Safe Stop), or execute controller down to minimum controllable flow. |
| **F05** | Direct SP Valve Off & Flutter Toggle | `CommandCodec.h:170`, `Lifecycle.h:3`, Flutter `main.dart:607` | Positive setpoint doesn't clear `valveFlowState`; Flutter toggle inverted | **Firmware + Flutter** | **High** | Firmware: automatically clear `valveFlowState = 0` on positive setpoint when `v_Flow` is omitted. Flutter: invert toggle and rename to "Corte Geral". |
| **F06** | Manifold Interlock | `CommandCodec.h:140-153` | Unvalidated independent writes to GPIO 5, 16, 17 allow dual open or dead-end | **Firmware** | **High** | Implement atomic validation of routing states (`SafeStop`, `RouteA`, `RouteB`). Reject commands that energize both routes or block flow into a dead end. |
| **F07** | Parameter Validation | `CommandCodec.h:184-210,235` | Raw `strtof()` without `isfinite()` or bounds check saved directly to EEPROM | **Firmware** | **Immediate (Critical)** | Validate `isfinite()` and clamp all gains (`Kp`, `Ki`, `FF`, `ramp`, polynomial coefficients) before updating `calParams` or committing to EEPROM. |
| **F08** | JSON Parser & Boolean `atoi` | `CommandCodec.h:99-156` | Ad-hoc parser; `atoi("true")` evaluates to 0; partial sequential application | **Firmware** | **High** | Parse into staging struct; explicitly match `"true"`/`"false"` before calling `atoi()`; commit to hardware only if entire payload is valid. |
| **F09** | `max_flow` Non-persistence | `FirmwareApp.cpp:108`, `CommandCodec.h:195` | `maxFlowRate` stored only in volatile RAM; resets to 50.0 on reboot | **Firmware (+ Hub)** | **Medium-High** | Add `max_flow` to `CalibrationParams` (schema v6) to persist in EEPROM. Alternatively, ensure Hub always resends `maxFlow` on boot. |
| **F10** | Low Curve Erasure on Partial Update | `CommandCodec.h:217-220` | Heuristic zeroes `a1, b1` whenever quadratic keys (`k1, f1, c1`) are received | **Firmware** | **High** | Remove automatic zeroing of `a1, b1`. Require explicit `"curve_type": "quadratic"` or require all 8 coefficients in calibration commands. |
| **F11** | Calibration Readback / Hash | `WebSocketApi.h:28`, `TaskRuntime.h:125` | Telemetry ACK only confirms parsing, not stored EEPROM values | **Firmware** | **Medium** | Add 32-bit CRC of calibration to `/flowData` push and provide on-demand `/calibration` endpoint. Do not inflate 500 ms push with 8 floats. |
| **F12** | I2C Failure Handling | `Lifecycle.h:67-83`, `FlowIo.h:1-26` | Failure of ADS1115/MCP4725 logged to Serial but ignored by runtime | **Firmware** | **High** | Track `adsOk`/`dacOk`. On failure, assert Safe Stop, report `hw_err=1` in telemetry, and set `flow_rate = -1.0` to trip supervisory alarms. |
| **F13** | Network & OTA Authentication | `FirmwareApp.cpp:19-20`, `OtaService.h:4-110` | SoftAP has null password; endpoints have no auth headers or tokens | **Firmware** | **Medium** | Set standard WPA2 password on SoftAP. Add token/basic auth to `/update` or require physical button hold to enter OTA mode. |
| **F14** | OTA Output Interlock | `OtaService.h:74-92` | `Update.begin()` pauses communication but leaves valves and DAC active | **Firmware** | **Immediate (Safety)** | In `setupOTA()` chunk 0, unconditionally force Safe Stop (`v_Flow=1, v1=0, v2=0, DAC=0`) before `Update.begin()`. |
| **F15** | `reconnect_wifi` Recovery | `CommandCodec.h:154`, `TaskRuntime.h:211` | Disabling reconnection isolates node; Windows App lacks re-enable command | **App + Firmware** | **Medium** | App: Add administrative "Reconectar Wi-Fi" action. Firmware: Add watchdog timer (e.g. 15 min) to auto-restore `reconnect_Wifi = true`. |
| **F16** | Valve Readback Feedback | `CommandCodec.h:140-153`, `Lifecycle.h:219` | Telemetry echoes commanded GPIO, not physical valve state | **Firmware (Soft) / HW** | **Partial (Inferred)** | True readback requires hardware sensors. In firmware, implement soft flow-crosscheck: flag `FLOW_BLOCKED` or `VALVE_LEAK` if flow contradicts valve state. |

---

## 5. Verification Method

To independently verify these findings:

1. **Codebase Inspection**:
   - Inspect `External-Devices/fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp` at line 16 (`FW_VERSION "V10"`) vs `TaskRuntime.h` line 85 (`ver=v11`).
   - Inspect `src/core/Lifecycle.h` line 155 (`targetFlowSetpoint > 0.1f`) vs `src/protocol/CommandCodec.h` line 170 (`targetFlowSetpoint == 0.0f`) to confirm F04 logic hole.
   - Inspect `src/protocol/CommandCodec.h` line 217 (`lowQuadraticUpdated && !lowHigherOrderUpdated`) to confirm F10 automatic zeroing of `a1, b1`.
   - Inspect `src/api/OtaService.h` lines 74–94 to confirm F14 absence of valve shutdown in OTA handler.
   - Inspect `Windows_app/src/OpenTECHub/ViewModels/FlowControlViewModel.cs` line 690 to confirm F03 appending `" V"` to `snapshot.FlowOutput`.

2. **Automated Unit & Contract Test Commands**:
   - In `Windows_app`:
     ```powershell
     dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --filter "FullyQualifiedName~Calibration"
     dotnet test Windows_app/tests/OpenTECHub.Tests/OpenTECHub.Tests.csproj --filter "FullyQualifiedName~ExternalDevice"
     ```
   - In `ESP32S3-HUB`:
     ```powershell
     pytest ESP32S3-HUB/tests/contracts/test_json_keys.py
     ```

3. **Invalidation Conditions**:
   - If a newer firmware branch or hardware revision already incorporates current-sensing circuitry on solenoid drivers, F16's conclusion regarding physical readback is invalidated.
   - If the Omega FMA-5400 MFC hardware cannot physically regulate below 0.5 L/min, the recommendation for F04 should clamp strictly to zero rather than executing PI control in the 0.01–0.1 L/min band.
