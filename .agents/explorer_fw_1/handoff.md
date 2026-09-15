# Handoff Report — Embedded Firmware Audit (External Peristaltic Pump)

**Agent:** `explorer_fw_1` (Embedded Firmware Explorer)  
**Date:** 2026-09-13  
**Working Directory:** `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_fw_1`  
**Primary Deliverable:** `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_fw_1\firmware_analysis.md`

---

## 1. Observation

Direct code observations from inspecting the codebase:

1. **Firmware Source Structure:**
   - Active firmware code is located in `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/`:
     - `src/core/FirmwareApp.cpp` and `FirmwareApp.h`
     - `src/hardware/PwmRuntime.h`
     - `src/core/Lifecycle.h`
     - `src/control/OperationController.h`
     - `src/control/SensorAndConversion.h`
     - `src/storage/ConfigStore.h`
     - `src/storage/RuntimeStateStore.h`
     - `src/protocol/TelemetryCodec.h`
     - `src/network/HubClient.h`
     - `peristaltic-pump.ino`

2. **Core 0 Real-Time Volume Integration & LEDC PWM:**
   - In `src/hardware/PwmRuntime.h:1-60`, `pwmTask` runs pinned to Core 0 (`Lifecycle.h:58`) at 2 ms (`TASK_DELAY_MS = 2`).
   - Line 14: `dirPos = (s >= 0.0f);`
   - Lines 33–39: `ledcWrite(R_PWM_PIN, duty); ledcWrite(L_PWM_PIN, 0);` for positive speed.
   - Lines 48–56:
     ```cpp
     float q_actual_mlmin = pwmDutyToMlmin(duty);
     q_actual_ml_per_sec = q_actual_mlmin / 60.0f;
     if (q_actual_ml_per_sec != 0.0f) {
         taskDISABLE_INTERRUPTS();
         g_cumulativeVolumeMl += (q_actual_ml_per_sec * (double)dt_sec);
         taskENABLE_INTERRUPTS();
     }
     ```

3. **Preservation of Session Volume & Cycle Isolation:**
   - In `src/control/OperationController.h:41-54`, `startCycle()` sets `g_cycleStartVolumeMl = vol;` without clearing `g_cumulativeVolumeMl`.
   - Lines 251–257: `cmd.equals("stop")` calls `startCycle()` and `clearRuntimeState()`, keeping `g_cumulativeVolumeMl`.
   - Lines 258–261: `cmd.equals("reset_volume")` is the ONLY handler calling `resetOperationState()`.
   - Lines 26–32: `resetOperationState()` clears `g_cumulativeVolumeMl = 0.0f;` and `g_cycleStartVolumeMl = 0.0f;`.
   - Lines 63–68: `V_actual_ml = g_cumulativeVolumeMl - g_cycleStartVolumeMl;` feeds the volume PID.

4. **Potentiometer Unlock & Deadline:**
   - In `src/control/OperationController.h:314-328`:
     `"pot": 1` executes `disablePot = false; hasUsbSpeed = false; usbSpeedSteps = 0.0f; g_usbSpeedUntilMs = 0;`.
   - Lines 305–312: `"speed_ms"` sets `g_usbSpeedUntilMs = millis() + (unsigned long)msVal;`.
   - In `src/core/Lifecycle.h:138-142`:
     `if (hasUsbSpeed && g_usbSpeedUntilMs != 0 && (long)(now - g_usbSpeedUntilMs) >= 0)` resets `usbSpeedSteps = 0.0f;`.

5. **NVS Checkpoint and State Recovery:**
   - In `src/storage/RuntimeStateStore.h:41-56`, `saveRuntimeState()` saves `s_active`, `s_vol`, `s_time`, `s_mode`, `s_cvol`.
   - Lines 1–39: `checkAndRecoverState()` restores `savedVol` and `savedCycleVol` (`s_cvol`), entering `OP_RUNNING`.

6. **Hub Gateway Filtering & Seeding:**
   - In `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h:596, 605-614`, `allowedPumpCommands[] = { "reset_volume", "start", "stop" };` blocks `clear_nvs` with `ESP32_AVISO`.
   - In `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h:6-18`, `seedReliableMailboxes()` seeds `pumpBox.revision` with `((esp_random() % 900000UL) + 100000UL) * 1000UL` upon boot.

7. **Telemetry Echo:**
   - In `src/network/HubClient.h:170-190`, push URL includes `&slope=%.4f&intercept=%.4f&kp=%.4f&ki=%.4f&kd=%.4f&pot=%d&cyc_vol=%.3f`.

---

## 2. Logic Chain

1. **Auditing §1.10 (Limitations, Risks, and Decisions):**
   - Observations 2 and 3 confirm that volume is estimated from the nominal duty-to-flow calibration curve, which is mechanically expected for a sensorless brushed DC peristaltic head (Item 1).
   - Observation 3 confirms that `stop`, `mode:0`, parameter updates, and profile completion call `startCycle()`, which isolates the cycle volume in `g_cycleStartVolumeMl` while preserving `g_cumulativeVolumeMl`, and only `reset_volume` resets it (Item 2).
   - Observation 4 confirms that `pot:1` unlocks the bench knobs and forgets USB speed, while `speed_ms` provides autonomous watchdog shutdown (Items 3 and 4).
   - Observation 6 confirms that `clear_nvs` is blocked on the Hub while remaining available locally (Item 5).
   - Observation 5 confirms that the 60 s checkpoint includes `s_cvol` and automatically resumes operation on reboot (Item 6).
   - Observation from `TelemetryCodec.h` and `HubClient.h` confirms that the liquid contact sensor (pino 15) is retained as a local hardware interlock and intentionally not exposed in telemetry (Item 7).
   - Observation 7 confirms that PID gains `kp`, `ki`, `kd` are pushed to Hub at 1 Hz, freeing the UI edit fields (Item 8).
   - Observation from `Lifecycle.h` and `Mailboxes.h` confirms that the 2 s polling interval and latest-wins mailbox meet process requirements (Item 9).
   - Observation from `OperationController.h:242` confirms that `start` with `mode=0` sets `OP_RUNNING` with `Q=0`, which is innocuous since the App only uses `mode: 1..5` and `PumpStopProfile()` (Item 10).
   - Observation 2 confirms that the motor is a brushed DC motor on BTS7960 H-bridge, not a stepper (Item 11).
   - Observation from `OperationController.h:184-204` confirms that the "gain" potentiometer is bipolar speed scaling centered at 2047 (Item 12).

2. **Auditing §1.11 (Bench Checklist):**
   - Observation 2 shows that $S > 0$ activates `R_PWM_PIN`; whether fluid moves toward the vessel depends on DC wire polarity and tubing direction (Item 1).
   - Observation 7 shows that telemetry pushes at 1 Hz (`DATA_PUSH_PERIOD_MS = 1000`), allowing the Hub's 4 s presence timeout to detect offline status within 4 s (Item 2).
   - Observation 7 and `SensorAndConversion.h` show that `pumpSlope` and `pumpIntercept` are applied in real-time, supporting the volumetric calibration procedure (Item 3).
   - Observation 4 confirms that `pot:1` restores knob operation (Item 4) and `speed_ms` autonomously halts the motor when the link is disconnected (Item 5).
   - Observation 3 confirms that 5-minute timed profiles retain session volume, and consecutive profiles accumulate session volume while isolating the PID cycle volume (Items 6 and 7).
   - Observation 5 and 7 confirm that PID gains persist across reboot and checkpoint recovery resumes `OP_RUNNING` (Items 8 and 9).
   - Observation 6 confirms that Hub reboot seeds high `cmd_id` revisions, preventing ID collision (Item 10).
   - Observation from `SensorAndConversion.h:1-19` and `Lifecycle.h:174-202` confirms 50 ms debounce and 500 ms motor-on latch (Item 11).

---

## 3. Caveats

- **No physical bench execution performed:** As an analytical read-only investigation, physical bench testing with actual liquids, scale, graduated cylinder, and oscilloscope could not be performed directly by this agent.
- **Physical DC lead polarity:** The physical direction of flow ($S > 0$) must be verified once on bench to ensure the motor leads to OUT1/OUT2 match the head's tube routing.
- **Assumed stable NVS flash:** Checkpoint every 60 s during `OP_RUNNING` assumes standard ESP32 flash endurance (>100,000 write cycles per sector).

---

## 4. Conclusion

1. The external peristaltic pump firmware v3.10 is **feature-complete, robust, modular, and fully aligned with the software architecture**.
2. **No code modifications to the pump firmware are required for normal production use.**
3. All pending items in §1.11 are physical bench verification procedures, not software gaps.
4. Comprehensive analysis report with line-by-line evidence and technical justifications has been generated at:
   `d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\.agents\explorer_fw_1\firmware_analysis.md`.

---

## 5. Verification Method

To independently verify the observations and analysis:

1. **Verify Source Files & Line Numbers:**
   - Inspect `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/control/OperationController.h` lines 26–54, 251–261, 305–328, 345–350.
   - Inspect `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/hardware/PwmRuntime.h` lines 14, 33–56.
   - Inspect `External-Devices/bomba-peristaltica/firmware/peristaltic-pump/src/storage/RuntimeStateStore.h` lines 1–62.
   - Inspect `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Commands.h` lines 596, 605–624.
   - Inspect `ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h` lines 6–18.

2. **Verify Automated Contract Tests:**
   - Run Hub and App contract test suites:
     ```powershell
     cd d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\ESP32S3-HUB
     pytest tests/
     ```
     and
     ```powershell
     cd d:\OneDrive\PosDoc_Fapesp\Automacao_e_Controle\ProjetoTECNAL\Windows_app
     dotnet test --filter "FullyQualifiedName~Pump"
     ```

3. **Invalidation Conditions:**
   - If physical bench trials show $S > 0$ aspirates instead of infusing, swap OUT1/OUT2 on BTS7960 terminal block.
   - If remote telemetry of the liquid contact switch is formally requested in the future, follow the proposal documented in `firmware_analysis.md` §2 Item 7.
