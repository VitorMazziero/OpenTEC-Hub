# Changelog

All notable changes to TECNAL-Hub. Version numbers follow
[Semantic Versioning](https://semver.org/); the single source for the number is
`<Version>` in `Directory.Build.props`.

---

## [0.8.0] - 2026-08-20

Phase 1b WP7 — completed pages, history/audit tooling, and display units.

### Added
- **Históricos**, a persisted-session browser separate from Gráficos. It inventories
  name, date, duration, row count, size and connection medium; previews first/last rows;
  and refuses graph loading when the header differs from `SessionLogFormat.Header`.
- Session CSV export and loading into the existing dedicated two-panel graph workspace.
  Gráficos also gains PNG/visible-data CSV export and a synchronized cursor readout.
- **Eventos**, a bounded, filterable eight-source audit journal. Successful transport
  writes publish their exact merged JSON only after the write succeeds, so command
  evidence remains honest over USB and Wi-Fi.
- Session-log controls on Eventos: start/stop, new file, path, row count, size and folder.
- In-page Configurações navigation and the display-only `Unidades` section (`°C`/`°F`,
  `kPa`/`mmHg`/`bar`, nominal vessel volume). Protocol and session values stay canonical.
- Confirmation for `resetVariables` and `restart`, showing exact JSON and defaulting to
  cancellation.

### Changed
- The roadmap's proposed `Gráficos`→`Históricos` rename was intentionally not applied.
  `Gráficos` remains the focused live/session dual-chart page; `Históricos` owns file
  discovery and hands an accepted session to it.
- Session logging now emits state/row notifications consumed by Eventos and the shell.

### Fixed
- Display-unit conversion residue can no longer leak values such as
  `36.99999999999999` into command JSON.
- Eventos no longer dereferences its second filter during first-filter initialization.
- Read-only event/session previews use one-way bindings, so selecting a row displays its
  detail without a WPF binding failure.

### Verified
- 178/178 tests pass, including post-write command evidence, rejected-write exclusion,
  session header/row parsing, unit round trips, event initialization and destructive
  confirmation.
- Localhost simulator live at 1280×800; Gráficos, Históricos, Eventos and Unidades were
  exercised with zero XAML binding failures. Evidence is in `docs/evidence/ui/wp7-*.png`.
- The existing `SkiaSharp.Views.WPF` `NU1701` compatibility warning remains unchanged.

---

## [0.7.0] - 2026-08-20

Phase 1b WP6 - the all-setpoints Controle page and explicit valve control.

### Added
- **Controle page** with all five core subsystems in one table: live PV, last applied
  setpoint, staged setpoint, range, active state, mode, owner, and per-row actions.
- **One-frame bulk apply.** Dirty subsystem commands are merged into one flat JSON
  object; invalid cross-field flow/maxFlow combinations are refused rather than clamped.
- **Valve card** for `valve_1` (auxiliary) and `valve_2` (nitrogen), with the physical
  valve telemetry beside each toggle and the derived/inverted `v_Flow` state read-only.
- Typed, named setpoint presets. Loading a preset stages fields and is asserted never to
  send a command.
- **Parada segura**, the application's only red button, behind a default-cancel dialog
  that displays the exact JSON before sending the complete five-subsystem safe state.
- Eight WP6 acceptance tests and light/dark simulator evidence in `docs/evidence/ui/`.

### Changed
- The variable rail now obeys its responsive visibility state. It stays hidden on
  Controle because the page already shows every live PV and needs the full width at
  1280 px.
- `SubsystemViewModel` now separates command construction from commit, allowing bulk
  apply to reuse exactly the same validation and state transitions as per-row apply.

### Fixed
- Clean app shutdown no longer logs a fatal exception: the dependency container is now
  disposed asynchronously because settings, session logging, and the device service are
  async-only disposables.
- The shared `ListViewItem` template now honours its `Padding` property; WP6 can compact
  its five rows without clipping the pressure row.

### Verified
- 169/169 tests pass.
- Localhost simulator connected with live telemetry; all five rows and actions were
  visible at 1280×800 in both themes with zero XAML binding failures.

---

## [0.6.0] - 2026-08-19

Advanced settings - **Phase 1 complete**.

### Added
- **Settings page** - connection options, probe calibration, spike-filter tuning,
  session-log path, theme, and the device commands (reset variables, restart
  communications, restore factory values).
- Edits are **staged and applied together**, with Revert. Calibration coefficients are
  a pair; applying a new slope against an old intercept would put wrong numbers on
  screen and into the log.
- **Live calibration preview** - what the current raw count decodes to with the
  coefficients as typed.
- `FilterSettings` in the persisted settings record, and `AppSettings.ToParserConfig()`
  now carries filter tuning through to the running parser.
- `docs/evidence/ui/` - a capture of every page, for the Phase 5 interface review.

### Changed
- Advanced settings is a **page**, not the separate window UI_DESIGN specified. The nav
  rail already had the slot, the KPI strip keeps safety context visible, and it is one
  implementation rather than two. Doc updated to match.

### Fixed
- The calibration preview was computed once before any telemetry arrived and never
  recomputed, so it read "sem leitura bruta disponível" permanently.

---

## [0.5.0] - 2026-08-19

Charts and session logging - Phase 1 feature-complete.

### Added
- **Charts page** - at most two panels side by side, each selectable from 11 channels,
  with a selectable time window and a pause control. Mirrors what v.6's graphs page
  offered, minus OUR (Phase 2 soft sensor).
- **`TelemetryHistory`** - fixed-capacity ring buffer, ~48 h at the field `dataDelay`,
  stride-downsampled to 2000 points before reaching a plot. Sentinels stored as `NaN`
  so charts show a gap rather than a line diving to -1.
- **`SessionLogger`** - tab-separated log byte-compatible with v.6, so existing
  analysis scripts keep working. Invariant numbers, UTF-8 without BOM, header only
  when the file is new.
- **Log page** - device messages plus start/stop logging.
- Page switching from the nav rail; pages stay in the visual tree so a chart does not
  rebuild every time the operator glances elsewhere.
- Chart series brushes (Okabe-Ito) in the token dictionaries.

### Fixed
- `TextTrimming="MiddleEllipsis"` is a WinUI value; WPF only has `None`,
  `CharacterEllipsis` and `WordEllipsis`. It threw at XAML parse time.
- Selector combo boxes showed the record type name instead of the label.
- Collapsing the second chart panel now collapses its grid column, not just the
  border - otherwise the remaining chart stayed at half width beside an empty gap.

---

## [0.4.0] - 2026-08-19

Phase 1's two remaining screens: the synoptic and setpoint entry.

### Added
- **Synoptic** - vector schematic of the reactor with each live value pinned at the
  hardware that produces it: jacket, motor, probes, sparger, headplate. Clicking any
  element selects it. Themed and scalable, not an image.
- **Detail pane** - reading, setpoint entry, validation, apply/revert, and the
  acknowledged state. Only one subsystem's controls on screen at a time, which is what
  stops twelve of them becoming the wall the v.6 card board was.
- **Responsive layout** - side pane at 1200 px and above, drawer below. Same ViewModel,
  so behaviour is identical and only presentation changes.
- **Setpoint entry** for all five core subsystems, with ranges paired to their command
  builders so validation and the wire cannot drift apart.
- ViewModel tests: `TecnalHub.Tests` now references the app assembly.

### Fixed
- **Validation guarded only the button, not the send.** `Apply` checked whether the
  text parsed, so an out-of-range value like `60.1` on a 15-60 range parsed fine and
  was sent. Anything reaching the method other than the button - the Enter key, a
  future recipe engine - bypassed the range and integer rules. Found by the tests.
- The pending-change marker no longer fires when the constructor seeds the field from
  persisted settings; it lit on every subsystem at launch.

---

## [0.3.1] — 2026-08-19

### Added
- **`TecnalHub.Simulator`** — stands in for the ESP32-S3 and the bioreactor behind it.
  HTTP on localhost (no driver, no admin, no reboot) or serial over a virtual COM pair.
  Full design in [SIMULATOR.md](SIMULATOR.md).
  - Reproduces the measured firmware quirks: the buffered `OK` served by `/readData`
    after a POST, `[ESP32_` log lines interleaved on serial, ETag/304 per published
    frame, `/ping` → `pong`, 404 elsewhere.
  - Emits **raw ADC counts**, inverting the field calibration, so the app's calibration
    and spike-filter path is genuinely exercised rather than bypassed.
  - First-order process model with a 25 s oxygen probe dead time — the piece the
    cascade's prediction horizon exists to compensate.
  - Fault injection: `no-module`, `stall`, `dropout`, `spikes`, `noise`, `drift`,
    `garbage`.

### Fixed
- `TransportFaultException` now folds the inner exception's message into its own.
  "read failed" alone cannot distinguish a timeout from a refused connection, and that
  message is what reaches the connection popover.

### Verified
- App connected to the simulator over localhost showing live values; calibration round
  trip confirmed (`Oxyval:3926.6` → 94.9 %, `pHval:15178.7` → 7.01).
- The `stall` scenario correctly triggers the Wi-Fi silence timeout added in 0.2.1.

---

## [0.3.0] — 2026-08-19

Phase 1 begins: the application shell, running and connected to real hardware.

### Added
- Composition root with DI, Serilog rolling file, and crash handlers wired from the
  first line of startup.
- `AppSettings` - one typed record, JSON, debounced atomic writes, corrupt files
  quarantined rather than deleted. Replaces v.6's 370 lines of hand-marshalling.
- `DeviceService` - wraps `ConnectionManager` and marshals telemetry to the UI thread,
  so the protocol layer stays free of any UI dependency.
- `ThemeService` - light/dark, following the Windows app theme live.
- Shell: title bar with the connection chip and popover, navigation rail, and the
  always-visible KPI strip.
- Fluent control templates (Button, TextBox, ComboBox, RadioButton, CheckBox,
  Separator, ToolTip) - WPF's built-in chrome ignores the design tokens entirely.

### Verified on hardware
- **First frame at 662 ms** (budget 2000 ms); connected to COM3 **599 ms after process
  start**, with auto-connect firing only after the window was rendered.

### Notable
- Agitation is displayed as **commanded, not measured**: the firmware sends no RPM
  feedback, and v.6 logs the commanded value in the same column as measured ones.
- Sentinels render as an em dash, never `0` - zero is a legitimate reading for
  pressure and flow.
- Two WPF traps recorded in [PHASE_LOG.md](PHASE_LOG.md): a font stack containing CSS
  keywords plus a Windows-11-only family overflowed the stack in font fallback before
  the first frame, and `ConverterParameter` cannot be bound.

---

## [0.2.2] — 2026-08-19

### Changed
- **The DTR/RTS reset pulse is off by default.** Measured as the cause of the reboot
  on every USB connect: with the pulse the device clock fell 102.1 s to 2.8 s across a
  reconnect while 5.1 s of wall time passed; without it the clock advanced 5.5 s
  against 5.5 s. Connect 1903 ms -> 13 ms, discovery 1.9 s -> 0.1 s, and device state
  now survives a reconnect. The 1.8 s boot settle now applies only when pulsing, since
  it existed solely to wait out the self-inflicted reboot.
- `ConnectionManager` escalates to a pulsed (hardware-reset) connect after
  `FailuresBeforeHardwareReset` handshake failures, for the hung-firmware case that
  nothing else recovers.

### Added
- `tecnal-harness reset-test` — determines what reboots the board on connect and finds
  the minimum viable boot settle.
- [PHASE_LOG.md](PHASE_LOG.md) — record of decisions taken while executing each phase.

### Documented
- PROTOCOL.md Q6 answered and the USB parameter table corrected.
- ROADMAP.md now tracks Phase 0 deliverables and the P1/P2/P3 follow-ups as checkboxes.

---

## [0.2.1] — 2026-08-19

Two link-supervision defects found by review after the hardware runs, plus the first
tests for the state machine itself.

### Fixed
- **Wi-Fi had no silence detection.** The timeout was gated on `Medium == Usb`, so a
  Wi-Fi link whose telemetry stalled while the web server stayed up (answering 304
  forever) would never time out - the app would show frozen readings behind a healthy
  "Connected" indicator. `TelemetrySilenceTimeout` now covers both transports and is
  measured against parsed telemetry, not against any traffic.
- **The heartbeat never ran.** `HeartbeatInterval` was declared and
  `TestConnectionAsync` implemented, but nothing called either. Replaced with
  two-stage liveness: quiet for `LivenessProbeAfterSilence` (4 s) starts probing;
  quiet for `TelemetrySilenceTimeout` (8 s) drops the link even if the probe succeeds.
  A healthy link sends no probes at all - verified on hardware, 45 s USB run with
  `liveness probes: 0`.

### Added
- `ConnectionManagerTests` - 14 tests against a scriptable `FakeTransport`, covering
  silence on both media, probe failure, command coalescing, requeue after a rejected
  write, reconnect cycling, read faults and device-log/ack classification. The Wi-Fi
  silence test was verified to fail against the previous implementation.
- `ConnectionManager` accepts a transport factory, so the state machine can be driven
  without hardware.
- `LinkDiagnostics.LivenessProbes`, surfaced in the harness summary.

### Changed
- `ConnectionOptions.UsbSilenceTimeout` renamed to `TelemetrySilenceTimeout` (it is no
  longer USB-specific); `HeartbeatInterval` replaced by `LivenessProbeAfterSilence`.

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
