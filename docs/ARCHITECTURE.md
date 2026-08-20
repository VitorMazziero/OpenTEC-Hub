# Architecture

> How TECNAL-Hub is put together and why.
>
> **Docs:** [README](README.md) · [Roadmap](ROADMAP.md) · [Protocol](PROTOCOL.md) · [Migration](MIGRATION.md) · [UI Design](UI_DESIGN.md) · [Conventions](CONVENTIONS.md)

---

## 1. Layers

```text
┌─────────────────────────────────────────────────────────────┐
│  Views (XAML)          shell · synoptic · detail · charts    │
│  visual-only code-behind; no domain logic or service calls   │
├─────────────────────────────────────────────────────────────┤
│  ViewModels            screen state + commands               │
│  CommunityToolkit.Mvvm source generators                     │
├─────────────────────────────────────────────────────────────┤
│  Services              connection · control · telemetry ·    │
│                        persistence · dialogs                 │
│  every one behind an interface, registered in App.xaml.cs    │
├─────────────────────────────────────────────────────────────┤
│  TecnalHub.Protocol    SEPARATE ASSEMBLY                     │
│  transports · command builder · telemetry parser · filters   │
│  no WPF reference — headlessly testable                      │
└─────────────────────────────────────────────────────────────┘
                              │
                         ESP32-S3
```

The protocol layer is a **separate assembly**, not just a folder. That is load-bearing:
it makes it structurally impossible for wire-format code to reach for a `Dispatcher`
or a UI type, and it lets the whole ESP32 contract be tested without starting WPF.
If something in `TecnalHub.Protocol` needs the UI thread, it is in the wrong project.

---

## 2. Threading model

One rule, and it removes an entire class of bug that v.6 has
([MIGRATION.md](MIGRATION.md#3-known-defects-carried-in-from-v6) item 9):

> **The link has exactly one owner.** `ConnectionManager` owns the transport. Nothing
> else calls `ITransport.Read` or `.Write` — ever. Callers post commands to its queue
> and observe its events.

- All transport I/O is `async`, on the thread pool, never on the dispatcher.
- Telemetry is marshalled to the UI thread **once**, at the ViewModel boundary.
- Manual control and the recipe engine share the same command queue, so an operator
  and a running recipe cannot issue contradictory writes into the same link.
- Cancellation tokens are threaded through everything, including port probing.

Nothing blocks the UI thread. A synchronous file read in `App.OnStartup` is a bug,
not a shortcut — the 2 s cold-start budget in [ROADMAP.md](ROADMAP.md#non-functional-targets)
has no room for one.

---

## 3. Directory layout

```text
ProjetoTECNAL/
├─ TecnalHub.slnx
├─ Directory.Build.props        version + language standards, ALL projects
├─ global.json                  SDK pin (.NET 10)
├─ .editorconfig                formatting + naming, enforced at build
│
├─ docs/                        this documentation
│
├─ src/
│  ├─ TecnalHub.Protocol/       net10.0 — no WPF
│  │   ITransport · SerialTransport · HttpTransport
│  │   TecnalCommand · TelemetryParser · SpikeFilter · SensorReadings
│  │
│  └─ TecnalHub/               net10.0-windows — the WPF app
│     ├─ App.xaml(.cs)          composition root
│     ├─ MainWindow.xaml(.cs)   shell
│     ├─ Models/                process variables, recipe nodes
│     ├─ ViewModels/
│     ├─ Views/
│     ├─ Services/
│     │  ├─ Communication/      ConnectionManager (owns the link)
│     │  ├─ Control/            cascade + kLa path controllers
│     │  ├─ Telemetry/          ring buffers, session files, audit journal
│     │  ├─ Persistence/        typed settings, recipe storage
│     │  ├─ Platform/           file dialogs, Explorer, clipboard, window placement
│     │  ├─ Dialogs/            IDialogService — VMs never open dialogs
│     │  └─ Diagnostics/        logging, crash capture
│     ├─ Converters/
│     ├─ Themes/                Tokens.Light / Dark / Shared
│     └─ Resources/              icons + neutral reactor art/anchors
│
└─ tests/
   └─ TecnalHub.Tests/          golden wire strings, parser, filters, controllers
```

---

## 4. Key decisions

**MVVM with source generators.** `[ObservableProperty]` and `[RelayCommand]` from
CommunityToolkit.Mvvm. No hand-written `INotifyPropertyChanged`.

**DI, one composition root.** Everything is registered in `App.xaml.cs`. Services are
constructor-injected behind interfaces. No service locator, no statics holding state.

**ViewModels never open dialogs.** They call `IDialogService`. This keeps them testable
and keeps every pt-BR user-facing string in one reviewable place.

**Settings are a typed record**, serialised with `System.Text.Json`. This replaces
v.6's 370 lines of hand-written `collect_preferences` / `apply_preferences` marshalling —
the single biggest source of silently-lost settings in the old app.

**Shell placement is persisted as data, not WPF objects.** `UiSettings` stores nullable
normal bounds, maximized state and the last page's stable id. The platform resolver turns
those values into a visible current-desktop rectangle; `MainWindow` alone applies and
captures WPF geometry. A monitor-layout change therefore cannot strand the application
off screen, and no UI-framework type leaks into the settings file.

**Controle is another view of the core subsystem state, not a copy.** The all-setpoints
table and the detail pane share the same `SubsystemViewModel` instances. A subsystem can
build its validated pending command without sending it, which lets bulk apply merge dirty
rows into one wire frame and then commit the same state transition per row.

**Presets stage; they never command.** Named core-loop presets live in the typed settings
record. Loading one fills setpoint, enable, valve and `maxFlow` fields; only an explicit
Apply action can call `IDeviceService.Send`.

**Dialogs are behind `IDialogService`.** The destructive-confirmation implementation is
a WPF service, while `ControlViewModel` sees only a boolean result. Tests can prove that
Cancel sends nothing without opening a window.

**Charts are ring buffers.** Fixed capacity, allocated once. A 24 h run must not grow
memory ([ROADMAP.md](ROADMAP.md#non-functional-targets)).

**History browsing and charting stay separate.** `SessionFileService` verifies and
parses the frozen v.6-compatible file contract. `HistoricalViewModel` owns discovery;
`ChartsViewModel` owns both live and loaded-session rendering. Loading a file changes
only the graph source and never initiates equipment communication.

**Audit command evidence is post-write.** `ConnectionManager.CommandSent` is raised
only after the active transport accepts the merged frame. `EventJournal` records that
exact JSON plus app-known connection, setpoint, calibration and application facts, so
Eventos is useful on Wi-Fi without inventing serial device messages.

**The reactor image is scenery, not state.** `reactor-neutral.png` is one true-alpha
cross-theme equipment master. Values, units, path colours, selection, focus, hit targets
and the missing-level statement remain native WPF overlays calibrated by
`reactor-anchors.json`. `SynopticView` code-behind does one visual-only job: reveal the
vector fallback if image decoding fails. Generation provenance is recorded in
[ASSET_PROVENANCE.md](ASSET_PROVENANCE.md).

**ScottPlot follows live token changes explicitly.** A plotting surface is not a WPF
resource consumer. `TrendSpark` therefore listens to the shared surface brush's change
notification and restyles after the theme service completes its repaint pass; without
that bridge, a chart created in light mode remains white after the rest of Painel turns
dark.

**The cascade control law is pure and headlessly tested.** `Services/Control/` holds the
Phase 2 controllers as plain C# with no WPF, wire or telemetry reference, driven a step at a
time and unit-tested against a simulated first-order DOT plant with dead time. The output is
velocity-form — the clamped output is the integrator, so it holds the actuator at setpoint
and cannot wind up — and the error carries a prediction horizon to compensate the probe dead
time. The controller meets the wire in exactly one place, `CascadeController.BuildCommand`,
which goes through `CommandBuilders.CascadeActuation`; it never sends, so a running cascade
and an operator share the one command queue. See [D-013](DECISIONS.md).

**Logging through Serilog only.** One rolling file plus an in-app pane. No ad-hoc
`.txt` writes — v.6 has three separate logging mechanisms (`crash_log.txt`, per-session
`command_logs/`, and an in-window pane) that do not agree with each other.

---

## 5. Testing strategy

| Layer | Tested how |
|---|---|
| Wire format | Golden-string assertions against [PROTOCOL.md](PROTOCOL.md#4-golden-strings) §4, including one fixture running under **pt-BR culture** |
| Telemetry parser | Recorded real device JSON, replayed; sentinel and stickiness semantics asserted |
| Spike filter | Property tests — a held value must never be overwritten by a single outlier |
| Controllers | Simulated first-order DOT plant with dead time; assert no windup, no zero-at-setpoint collapse |
| Themes | Light and dark dictionaries must define an identical key set (a missing key crashes the runtime theme switch) |
| Reactor asset | PNG signature/dimensions/RGBA encoding, transparent canvas, anchor schema, pH overlay and decode fallback |
| Transports | Not unit-tested. Verified by the Phase 0 hardware harness — mocking a serial port proves nothing about a real ESP32. |
