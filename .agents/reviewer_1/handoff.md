# Technical Review and Adversarial Audit Report: Peristaltic Pump Implementation Plan

**Reviewer**: Reviewer 1 (Technical Reviewer & Adversarial Critic)  
**Deliverable Under Review**: `IMPLEMENTATION_PLAN_BOMBA.md`  
**Verification Script**: `verify_plan_bomba.py`  
**Authoritative Contracts**: `ORIGINAL_REQUEST.md`, `SCOPE.md`, `External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md` (§1.0 to §1.11)  
**Date**: 2026-09-13T11:45:00Z  
**Verdict**: **APPROVE**  
**Integrity Status**: **CLEAN (0 Integrity Violations Detected)**  

---

## 1. Observation

Direct observations and source code verifications conducted across all subsystems:

### 1.1 Automated Verification Script Execution
- Tool execution: `python verify_plan_bomba.py` in workspace `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL`.
- Output:
  ```
  ==========================================================================================
   AUDITORIA DE CONFORMIDADE: IMPLEMENTATION_PLAN_BOMBA.md
   Arquivo Alvo: D:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\IMPLEMENTATION_PLAN_BOMBA.md
  ==========================================================================================
   Documento carregado: 671 linhas, 59139 caracteres.

  [-] Auditando Seção 1.10 (Limitações, Riscos e Decisões de Engenharia)...
  [-] Auditando Seção 1.11 (Checklist de Homologação em Bancada Física)...

  [ESTATÍSTICAS GERAIS]
    - Total de Itens Auditados: 27 (16 em §1.10 e 11 em §1.11)
    - Aprovados (PASS):         27
    - Reprovados (FAIL):        0
    - Taxa de Conformidade:     100.0%

  [RESULTADO DA AUDITORIA]: SUCESSO ABSOLUTO (Exit Code 0)
  ```

### 1.2 Firmware Subsystem Source Cross-Verification (`External-Devices/bomba-peristaltica/firmware/peristaltic-pump/`)
- **`src/hardware/PwmRuntime.h:46-56`**:
  - Direct observation: Line 48 converts duty to mL/min (`pwmDutyToMlmin(duty)`), line 54 integrates volume under critical section (`taskDISABLE_INTERRUPTS(); g_cumulativeVolumeMl += ...; taskENABLE_INTERRUPTS();`).
- **`src/control/SensorAndConversion.h:21-37`**:
  - Direct observation: Line 22 checks `if (fabsf(g_config.pumpSlope) < 1e-6f) return 0.0f;` (confirming D-SEC-04 zero-division guard); lines 26-28 implement linear conversion $Q = a \cdot S + b$; lines 30-37 implement breakaway duty offset (`PWM_BREAKAWAY = 155`).
- **`src/control/OperationController.h`**:
  - Lines 41-54: `startCycle()` stores `g_cycleStartVolumeMl = vol` while preserving session volume `g_cumulativeVolumeMl`.
  - Lines 60-75: `runOperationLogic()` computes `V_actual_ml -= g_cycleStartVolumeMl` and passes cycle volume to `updatePID`.
  - Lines 184-204: `calcPotSpeed()` computes bipolar direction `dir = (filtGain - ADC_CENTER) / ADC_CENTER;` on GPIO 35.
  - Lines 251-261: `stop` invokes `startCycle()` keeping `g_cumulativeVolumeMl`; `reset_volume` calls `resetOperationState()` zeroing `g_cumulativeVolumeMl` and `g_cycleStartVolumeMl`.
  - Lines 269-274: `clear_nvs` performs `g_prefs.clear(); delay(1000); ESP.restart();`.
  - Lines 305-312: `speed_ms` calculates `g_usbSpeedUntilMs = millis() + msVal`.
  - Lines 314-328: `"pot": 1` restores `disablePot = false; hasUsbSpeed = false; usbSpeedSteps = 0.0f;`.
  - Lines 345-350: `pid_kp`, `pid_ki`, `pid_kd` update config and mark `g_configDirty = true`.
- **`src/core/Lifecycle.h:138-143, 174-202`**:
  - Direct observation: Lines 138-142 handle `speed_ms` timeout `(long)(now - g_usbSpeedUntilMs) >= 0` setting `usbSpeedSteps = 0.0f`.
  - Lines 174-202: Implements `MIN_MOTOR_ON_TIME_MS = 500` latching to prevent motor chattering.
- **`src/storage/RuntimeStateStore.h:1-56`**:
  - Direct observation: Lines 41-56 checkpoint `s_active`, `s_vol`, `s_time`, `s_mode`, `s_cvol` in `OP_RUNNING`. Lines 1-38 `checkAndRecoverState()` recovers state after unexpected reboot, printing `>>> DETECTED UNEXPECTED RESET! RECOVERING STATE <<<`.
- **`src/network/HubClient.h:185-188`**:
  - Direct observation: Push query string formats `&kp=%.4f&ki=%.4f&kd=%.4f&pot=%d&cyc_vol=%.3f` with `(disablePot || hasUsbSpeed) ? 0 : 1`.

### 1.3 Hub Subsystem Source Cross-Verification (`ESP32S3-HUB/ESP32S3-HUB/`)
- **`src/protocol/Commands.h:588-620`**:
  - Line 588-592: `simpleKeys[]` contains `pump_command`, `mode`, `pump_speed`, `pump_speed_ms`, `pump_pot`, `init_t`, `final_t`, `pumpSlope`, `pumpIntercept`, `pumpPidKp`, `pumpPidKi`, `pumpPidKd`.
  - Line 596: Whitelist `allowedPumpCommands[] = { "reset_volume", "start", "stop" };`.
  - Lines 605-614: Rejects unauthorized verbs (like `clear_nvs`) with `ESP32_AVISO`.
  - Lines 617-620: Translates `pumpPidK*` -> `pid_k*` and removes `pump_` prefix.
- **`src/sensor/Telemetry.h:331-352`**:
  - Directly exposes `PumpOnline`, `PumpCommEnabled`, `PumpCommandPending`, `PumpMode`, `PumpPWM`, `PumpSpeed`, `PumpFlow`, `PumpVol`, `PumpTargetVol`, `PumpActive`, `PumpWaiting`, `PumpSlope`, `PumpIntercept`, `PumpPidKp`, `PumpPidKi`, `PumpPidKd`, `PumpPotEnabled`, `PumpCycleVol`.
- **`src/protocol/Mailboxes.h:6-18, 22-52`**:
  - Line 9: Pseudo-random high-entropy seed `base = ((esp_random() % 900000UL) + 100000UL) * 1000UL;` (> 100,000,000) prevents command ID collisions after Hub reboot.
  - Lines 22-52: Mailbox single-item latest-wins queue with ACK tracking.
- **`tests/contracts/test_node_commands.py:410-441`**:
  - Lines 410-423: Contract test verifying whitelist blocks `clear_nvs`, `save_config`, etc.
  - Lines 424-433: Contract test verifying `pump_speed_ms` and `pump_pot` forwarding.
  - Lines 434-441: Contract test verifying `PumpPidKp`, `PumpPidKi`, `PumpPidKd`, `PumpPotEnabled`, `PumpCycleVol`.

### 1.4 Flutter App Subsystem Source Cross-Verification (`Android_app/`)
- **`lib/providers/device_control_provider.dart:304-310`**:
  - Direct observation:
    ```dart
    Future<bool> stopPump() async {
      return sendRawCommand({
        "mode": 0,
        "speed": 0,
      });
    }
    ```
    Confirms the bug identified in the plan: sending `"speed": 0` along with `"mode": 0` sets `hasUsbSpeed = true` in the firmware, inadvertently disabling bench potentiometers (`pot = 0`).
- **`lib/models/peristaltic_pump_state.dart:30, 43-44, 85, 165`**:
  - Line 30: Documented as `"RPM"`.
  - Line 165: Formatted as `"${speed.toStringAsFixed(1)} RPM"`.
  - Model lacks: `cycleVolume`, `potEnabled`, `pidKp`, `pidKi`, `pidKd`, `slope`, `intercept`.
- **`lib/screens/controls_screen.dart:1504-1520`**:
  - The `applyPumpProfile` button checks `_pumpValidationError == null`, but does not verify `!pumpState.isCommandPending`, permitting command overruns into the Hub mailbox.

---

## 2. Logic Chain

1. **Completeness Deduction**:
   - The authoritative specification (`COMANDOS_DISPOSITIVOS_EXTERNOS.md` §1.10 and §1.11) requires coverage of 12 numbered limitation items (items 1 to 11 plus the unnumbered "gain" potentiometer item #12), 4 closed architectural safety decisions (D-SEC-01 to D-SEC-04), and 11 bench test checklist items (1.11.1 to 1.11.11).
   - Inspection of `IMPLEMENTATION_PLAN_BOMBA.md` reveals dedicated sections for all 16 §1.10 items and all 11 §1.11 items.
   - Running `verify_plan_bomba.py` dynamically scans and validates the presence, structural integrity, and technical substance of each section, returning 27/27 PASS (100% compliance).

2. **Technical Depth & Accuracy Deduction**:
   - Cross-referencing citations across the firmware, hub, and app repositories confirmed that all file paths, line numbers, and logic explanations in `IMPLEMENTATION_PLAN_BOMBA.md` are 100% accurate.
   - The root cause analysis for the Flutter `stopPump()` bug is technically impeccable: sending `"speed": 0` sets `hasUsbSpeed = true`, locking out manual knobs until a reboot or `pot: 1`. The proposed fix (`{"mode": 0}`) restores standard profile cessation and volume retention without unintended side effects.
   - Proposed Dart code diffs for `DeviceControlProvider`, `PeristalticPumpState`, and `controls_screen.dart` strictly adhere to the contracts defined by `Commands.h` and `Telemetry.h`.

3. **Justification Soundness Deduction**:
   - Deferrals and non-implementation justifications for items 1.10.5 (`clear_nvs`), 1.10.6 (60 s checkpoint), 1.10.7 (liquid sensor pin 15), 1.10.10 (`start` with `mode=0`), 1.10.12 (gain knob), and D-SEC-01..04 are grounded in verified engineering rationale:
     - 1.10.5 is already enforced by the Hub whitelist.
     - 1.10.6 is already functioning in firmware v3.10.
     - 1.10.7 is a local safety interlock (D-SEC-02); exposing remote control would compromise operator safety.
     - 1.10.10 is unused by the application.
     - D-SEC-01..04 are frozen architectural decisions.

4. **Safety & Bench Feasibility Deduction**:
   - Section 2 (§1.11 protocols) systematically addresses:
     - **Electrical Safety**: BTS7960 12V/24V power disconnection tests (P4 disconnect, circuit breaker drop), GPIO 15 electrode circuit integrity, overcurrent protection.
     - **Mechanical Safety**: Direction of rotation check ($S > 0$), Watson-Marlow 5-roller pinch hazards (mandatory safety gloves, hands-clear protocol), 500 ms anti-chattering motor latch, autonomous timeout stops (`speed_ms`).
     - **Chemical / Fluidic Safety**: Mandate of distilled water for all bench commissioning, preventing biohazard exposure; session volume preservation preventing bioreactor overflow.

5. **Adversarial Critic Evaluation & Edge Cases**:
   - **Finding 1 (Minor / Operational Safety - UI Volume Reset Guard)**: While `reset_volume` operates correctly at the firmware level, invoking `reset_volume` during an active dosing profile (`OP_RUNNING`) resets `g_cumulativeVolumeMl` and `g_cycleStartVolumeMl` to zero while `t_min` continues to advance. This induces an instantaneous target-volume error step ($V_{\text{target}} - 0$), saturating the PID integrator to maximum speed.  
     *Recommendation*: The Flutter UI should disable the "Zerar Volume Acumulado" action while `pumpState.isDosing == true`, or provide an explicit modal warning.
   - **Finding 2 (Minor / Validation Guard - Calibration Slope)**: The Flutter assisted calibration module should enforce $\text{slope} > 0$ during regression calculation, rejecting negative slopes before dispatch.
   - **Finding 3 (Minor / UI Ergonomics - PWM Deadband Tooltip)**: The UI tooltip should clarify that speed step $S = 1$ maps to the breakaway duty cycle ($155 / 1023 \approx 15.15\%$) to set operator expectations regarding minimum motor actuation.

---

## 3. Caveats

- **Physical Bench Testing**: This review verifies the software architecture, code citations, and technical protocols. Physical bench execution with actual peristaltic heads, motors, and glassware is scheduled for the subsequent commissioning phase (Roadmap Phase 3).
- **Flutter Code Modifications**: In accordance with the non-modification constraint of this task, Flutter source changes remain planned in the deliverable and have not yet been committed to `Android_app/lib/`.

---

## 4. Conclusion

The deliverable `IMPLEMENTATION_PLAN_BOMBA.md` represents an exceptionally thorough, technically precise, and actionable engineering plan. It satisfies all requirements of `ORIGINAL_REQUEST.md` and `SCOPE.md`, provides 100% accurate source citations across all 4 software repositories, details robust physical safety protocols, and successfully passes automated verification (`verify_plan_bomba.py`).

**Final Verdict**: **APPROVE**

---

## 5. Verification Method

To independently reproduce and verify this review:

1. **Execute the automated verification script**:
   ```powershell
   cd d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL
   python verify_plan_bomba.py
   ```
   *Expected result*: Exit code 0, 27/27 items PASS.

2. **Verify Hub contract test suite**:
   ```powershell
   cd d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\ESP32S3-HUB
   pytest tests/contracts/test_node_commands.py -k "pump"
   ```
   *Expected result*: All pump whitelist, echo, and timeout contract tests PASS.

3. **Inspect Flutter `stopPump()` bug location**:
   Inspect line 306-310 of `Android_app/lib/providers/device_control_provider.dart` to confirm the presence of `"speed": 0`.
