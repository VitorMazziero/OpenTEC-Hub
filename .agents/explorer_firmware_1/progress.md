# Progress — explorer_firmware_1

Last visited: 2026-09-13T01:50:00Z

## Status: COMPLETE
- [x] Initialized BRIEFING.md and progress.md
- [x] Read and extract Section 3.10 of External-Devices/docs/COMANDOS_DISPOSITIVOS_EXTERNOS.md
- [x] Map firmware directory structure in External-Devices/ (active code in External-Devices/fluxometro/firmware/flowmeter/)
- [x] Complete deep-dive code investigation on all 16 items F01 through F16:
  - F01: Firmware version mismatch (V10 vs v11) across code and endpoints
  - F02: Inconsistent calibration curves (firmware FACTORY_*, Windows CalibrationMath, Flutter defaults)
  - F03: FlowOutput unit mismatch (L/min in firmware vs Volts 'V' assumed by Windows App)
  - F04: Low setpoint deadband bug (0 < target <= 0.1) leaving stale DAC/valve state
  - F05: Direct setpoint doesn't open cut-off valve; inverted Flutter toggle
  - F06: Absence of route interlock in node firmware
  - F07: Lack of range/finitude checks for tuning gains and calibration polynomials
  - F08: Ad-hoc JSON parser, partial application on malformed frames, atoi("true")==0 bug
  - F09: max_flow not persisted across reboots
  - F10: Partial calibration updates wipe out quartic low-range terms (a1, b1)
  - F11: Calibration ACK lacks readback or verification hash
  - F12: Unhandled I2C failures on ADS1115 and MCP4725
  - F13: Unauthenticated AP, WebSocket, HTTP, and OTA
  - F14: OTA upload pauses network tasks while leaving active gas outputs energized
  - F15: reconnect_wifi can isolate node from Hub without easy recovery
  - F16: Valve telemetry echoes commanded GPIO bit without physical confirmation
- [x] Draft structured handoff.md following 5-component protocol (670 lines, fully self-contained)
- [x] Send completion message to parent
