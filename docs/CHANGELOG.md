# Changelog

All notable changes to TECNAL-Hub. Version numbers follow
[Semantic Versioning](https://semver.org/); the single source for the number is
`<Version>` in `Directory.Build.props`.

---

## [0.2.0] — 2026-08-19

Phase 0: the protocol stack, built and validated over USB against a real ESP32-S3.
No UI yet. See [PHASE0_RESULTS.md](PHASE0_RESULTS.md) for the measurements.

### Added
- `TecnalHub.Protocol`: `ITransport` + `SerialTransport` + `HttpTransport`,
  `TecnalCommand` (culture-invariant by construction), `CommandKeys`/`TelemetryKeys`,
  `CommandBuilders`, `TelemetryParser`, `SpikeFilter`, `ConnectionManager`.
- `TecnalHub.Harness`: console harness for hardware validation, with a wire-trace
  log for byte comparison against v.6 `command_logs/`.
- 49 tests: golden wire strings, telemetry semantics, spike-filter behaviour, and a
  culture fixture that forces pt-BR.

### Verified on hardware
- USB handshake, 60 s continuous telemetry, 0 parse failures, 0 spurious reconnects.
- Parallel port discovery finds the board in **1.9 s** with no port configured
  (v.6 would take ~12 s serially on the same machine).
- Wi-Fi on the `Modulo_TECNAL_1` SoftAP: 20/21 checks passed, 0 warnings. 90 s soak
  gave 45 frames at ~2.1 s with 0 parse failures on a single connect; ETag 304
  conditional polling confirmed; latency p95 33 ms, ~15x headroom over the v.6
  timeouts; reconnect resumes telemetry with the ETag correctly cleared.
- Wi-Fi reconnects do **not** reboot the device (no reset line), making Wi-Fi the
  safer transport for mid-run recovery.

### Fixed relative to v.6
- `OK` acknowledgements and `[ESP32_` log lines are recognised instead of being
  counted as parse failures. v.6 counts the ack, so several commands in quick
  succession can trip a false link-loss there.
- Link-silence detection is a duration, not a count of empty reads — the count
  couples failure detection to the poll rate.
- Wi-Fi read faults raise `TransportFaultException` instead of returning null, which
  in v.6 made link loss indistinguishable from "no new data".
- Parser defaults now match the calibration actually in the field; v.6's hard-coded
  defaults disagreed with `preferences.json`.

### Documented
- Wire-level finding: `OK` and device log lines share the USB telemetry stream
  ([PROTOCOL.md](PROTOCOL.md) section 2.0), answering open question Q4.
- Hardware finding: the DTR/RTS pulse means every USB reconnect reboots the board and
  discards its process state. Raised as Q6; `PulseResetOnConnect` defaults to v.6
  behaviour pending validation.

---

## [0.1.0] — 2026-08-19

Project scaffold and plan. No application features yet.

### Added
- Solution scaffold: `TecnalHub.Protocol` (net10.0, no WPF) + `TecnalHub`
  (net10.0-windows, WPF) + `TecnalHub.Tests`. Builds clean with 0 warnings.
- Build configuration: `Directory.Build.props` (single-source version, analyzers,
  language standards), `global.json` (SDK pin), `.editorconfig` (naming rules
  enforced at build).
- Fluent design tokens for light and dark themes, plus the shared brush, typography
  and geometry scales.
- Documentation set: [ROADMAP](ROADMAP.md), [PROTOCOL](PROTOCOL.md),
  [MIGRATION](MIGRATION.md), [ARCHITECTURE](ARCHITECTURE.md), [UI_DESIGN](UI_DESIGN.md),
  [DECISIONS](DECISIONS.md), [CONVENTIONS](CONVENTIONS.md).

### Decided
- .NET 10 LTS, self-contained publish ([D-003](DECISIONS.md#d-003--net-10-lts-self-contained)).
- Fluent light + dark following the system theme ([D-004](DECISIONS.md)).
- Dashboard as synoptic + detail pane + KPI strip ([D-005](DECISIONS.md)).
- Auto-connect with a status chip; Configurations stops being a destination ([D-006](DECISIONS.md)).
- pt-BR UI, English code ([D-007](DECISIONS.md)).
- Recipes as a node canvas, new implementation and new identity ([D-009](DECISIONS.md)).
- kLa gassing-out and torch leave the controller app ([D-010](DECISIONS.md)).

### Documented from the v.6 source
- The complete ESP32-S3 wire contract: both transports, all telemetry keys, ~60
  command keys, signal conditioning, and the timing constants that are load-bearing.
- Startup cost analysis: `import torch`, and serial probing that can exceed 10 s per
  unresponsive port with no way to cancel it.
- 11 defects found while reading v.6, each with a disposition.
