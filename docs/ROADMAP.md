# TECNAL-Hub — Build Roadmap

> **Version:** 0.12.0 · **Written:** 2026-08-19 · **Updated:** 2026-08-20
> Phased plan to rebuild the working Python v.6 controller as a C# / WPF application
> without ever losing a working link to the ESP32-S3.
>
> **Docs:** [README](README.md) · [Architecture](ARCHITECTURE.md) · [Protocol](PROTOCOL.md) · [Calibration](CALIBRATION.md) · [Migration](MIGRATION.md) · [UI Design](UI_DESIGN.md) · [Decisions](DECISIONS.md) · [Conventions](CONVENTIONS.md)

---

## The governing principle

> **v.6 stays installed and working until TECNAL-Hub has run a full cultivation.**

There is no cutover date, no big-bang switch. v.6 is the reference implementation
and the fallback. Every phase below ends in something that can be *run against real
hardware* and compared against v.6 — not in a pile of classes that compile.

The rebuild is worth doing for three concrete reasons, in priority order:

1. **Startup time.** v.6 spends most of its cold start on `import torch` plus a
   serial-probe loop that can take a minute (see [MIGRATION.md](MIGRATION.md#1-why-startup-is-slow)).
2. **UI.** The card board does not scale to 12 subsystems and gives no process picture.
3. **The 1086-line `main.py`.** ~370 lines of it are hand-written preference
   marshalling. That is the "patchwork" and it is where changes go to die.

Nothing else about v.6 is broken. The communication layer in particular is
*better than its reputation* — `transport.py` and `data_parser.py` are clean,
documented and directly portable. **The mess is above the wire, not on it.**

---

## Non-functional targets

Every phase is measured against these. They are acceptance criteria, not aspirations.

| Target | Value | How it is measured |
|---|---|---|
| Cold start to first frame | **< 2 s** | Stopwatch from process start to `Window.ContentRendered` |
| Cold start to live telemetry | **< 5 s** | Includes auto-connect on the remembered port |
| UI thread never blocks | **> 0 dropped frames = bug** | No sync I/O on the dispatcher, ever |
| Telemetry to screen latency | **< 250 ms** | Parse timestamp to render timestamp |
| Command round trip (USB) | **< 100 ms** | Write to `FlowCommandAck` change |
| Memory after 24 h run | **< 300 MB, flat** | Chart buffers must be ring buffers, not lists |
| Publish size (self-contained) | **< 200 MB** | `dotnet publish -c Release` |

> The 2 s target is why torch is gone and why nothing heavy is allowed in
> `App.OnStartup`. If a phase regresses startup, that phase is not done.

---

## Phase 0 — Protocol spike *(highest risk, do it first)*

**Goal:** prove C# can talk to the ESP32-S3 over both transports, byte-identically,
before a single pixel of UI exists.

This phase exists because it is the only genuinely unknown part of the project. If
`System.IO.Ports` behaves differently from pyserial around DTR/RTS and the 1.8 s
boot settle, that must surface now — not in month three.

**Deliverables**

- [x] `TecnalHub.Protocol` — `ITransport`, `SerialTransport`, `HttpTransport`
- [x] `TecnalCommand` — flat JSON builder, **`InvariantCulture` enforced at the type level**
- [x] `TelemetryParser` — key-by-key port of `data_parser.py`, sentinel semantics intact
- [x] `SpikeFilter` — direct port, same thresholds
- [x] `ConnectionManager` — the state machine, as an `async` service (no Qt signals)
- [x] A **console harness** (`dotnet run`) that connects, prints live telemetry, sends a setpoint
- [x] Golden-string tests, including one that runs under `pt-BR` culture
- [x] *(added)* `ConnectionManagerTests` — 14 state-machine tests against a fake transport
- [x] *(added)* `wifi-test` — unattended Wi-Fi validation suite
- [x] *(added)* `reset-test` — determines what reboots the board on connect

**Exit criteria — on real hardware**

1. [x] USB connect, sustained telemetry, no dropped link.
2. [x] Wi-Fi connect, sustained telemetry, ETag 304s handled correctly.
3. [ ] A captured v.6 session and a captured TECNAL-Hub session produce **identical
   command bytes** for the same operator actions. *(needs the bioreactor)*
4. [ ] Telemetry with **live sensors** — calibration, spike filters and the `pHCal`
   echo have never met real data. *(needs the bioreactor)*

> Capture v.6's side first: it already writes `command_logs/command_log_*.txt`.
> That is the comparison baseline — do not skip collecting it.

See [PHASE0_RESULTS.md](PHASE0_RESULTS.md) for measurements and
[PHASE_LOG.md](PHASE_LOG.md) for the decisions taken during execution.

### Device simulator

Stands in for the ESP32-S3 and the bioreactor behind it, so the app can be built and
tested without hardware — and so the cascade can be tuned against a process that
responds. Full design in [SIMULATOR.md](SIMULATOR.md).

**Built during Phase 1**

- [x] Protocol simulator, both transports: HTTP on localhost (no setup) and serial
      over a virtual COM pair
- [x] Reproduces the measured firmware quirks — buffered `OK` on `/readData`,
      `[ESP32_` log lines interleaved on serial, ETag/304 per published frame,
      `/ping` → `pong`, 404 elsewhere
- [x] Emits **raw ADC counts**, inverting the field calibration, so the app's
      calibration and spike-filter path is genuinely exercised
- [x] First-order process model with a 25 s oxygen probe dead time
- [x] Complete pH command state in the model (inactive band, duty timing and pump
      intensity), plus acceptance of the quoted `pHCal` echo and both flow-curve segments
- [x] Fault injection: `no-module`, `stall`, `dropout`, `spikes`, `noise`, `drift`,
      `garbage`

**Phase 2 — the model becomes load-bearing**

- [ ] Replace the placeholder `kLa = k·N^a·Q^b` with the manuscript's fitted **bicubic
      B-spline surface** (ties to [D-008](DECISIONS.md), same exported data)
- [ ] Realistic OUR trajectory across a cultivation, rather than a biomass proportion
- [ ] Configurable probe dead time and measurement quantisation, to reproduce the
      "staircase" signal the least-squares rate estimator exists to handle
- [ ] Scripted cultivation profiles for repeatable controller comparison
- [ ] Headless run mode: fixed seed, accelerated clock, CSV out — so a tuning change
      can be regression-tested rather than eyeballed

**Later**

- [ ] Record/replay: capture a real session's telemetry and replay it into the app

### Phase 0 follow-ups

Found by review and by hardware measurement. Priorities are as agreed with the
project owner; P1 is done, the rest ride along with Phase 1.

**P1 — done**

- [x] Wi-Fi silence detection (was gated on USB; a stalled Wi-Fi link showed stale
      data as live indefinitely)
- [x] Liveness probe actually runs (`HeartbeatInterval` was declared but never used)
- [x] DTR/RTS pulse disabled by default — measured as the cause of the reboot on every
      connect; connect went 1900 ms → 13 ms, discovery 1.9 s → 0.1 s, and device state
      now survives a reconnect

**P2 — do alongside Phase 1**

- [x] State-machine tests (pulled forward; the P1 fixes were untestable without them)
- [ ] Correlate `LastRoundTripMs` against `FlowCommandAck`, or rename it — on USB it
      times a fire-and-forget write and reports ~0 ms

**P3 — before the app ships**

- [ ] Port probing opens **every** COM port simultaneously; treat "access denied" as
      *busy, skip* rather than *not the device*. It will collide with v.6, which stays
      installed as the fallback
- [ ] Rank ports by USB descriptor (WMI) so the likely adapter is tried first and
      unrelated ports are never opened. The board is a **CH343**, which is not in v.6's
      keyword list and matches only via the `wch` manufacturer string
- [ ] Stop exposing the mutable `Readings` object; callers should only get `Snapshot()`
- [ ] Wire `HttpTransport.SetPollPeriod` to the configured `dataDelay`

---

## Phase 1 — Shell, connection UX, core loop

**Goal:** the first version an operator could actually use for a simple run.

**Scope (locked):** Temperature · Motor/agitation · Oxygen monitor · Flowmeter +
valves · Pressure. Nothing else.

**Deliverables**

- [x] Shell: title bar, nav rail, always-visible KPI strip
- [x] **Auto-connect on launch** to the last-used port/IP, with the status chip and
      its popover ([UI_DESIGN.md](UI_DESIGN.md#8-connection-ux))
- [x] Settings persistence — **typed and serialised, not hand-marshalled**
- [x] Light/dark theme tokens + control templates (WPF's default chrome ignores them)
- [x] Synoptic view of the reactor + side detail pane, responsive fallback to the
      bottom drawer under 1200 px
- [x] Setpoint entry and send for the five core subsystems, with visible validation
- [x] **Valve control** — `valve_1` (auxiliary), `valve_2` (nitrogen), physical valve
      telemetry, and the derived/inverted `v_Flow` state. Delivered by Phase 1b, WP6
- [x] Live charts (ScottPlot) — two selectable panels side by side, 11 channels,
      selectable time window, ring-buffered history
- [x] Session logging, byte-compatible with v.6's tab-separated format so old analysis
      scripts keep working
- [x] Serilog rolling file
- [x] In-app log pane (device messages + logging control)
- [x] Light/dark toggle following the Windows system theme
- [x] Advanced settings — calibration, spike filters, connection options, logging,
      appearance, device commands

**Exit criteria:** a real cultivation run controlled end-to-end by TECNAL-Hub with
v.6 closed, temperature and agitation and flow all holding setpoint, and a CSV that
the existing analysis scripts read without modification.

> **Status 2026-08-20:** Phase 1 and Phase 1b are **software-complete**. Every deliverable
> above, WP1-WP8, the professional reactor synoptic and the responsive shell closure have
> shipped. The cultivation exit gate remains **hardware-pending**: only a real
> bioreactor run can establish end-to-end process performance and replace v.6.

---

## Phase 1b — Design-system refit and shell completion

**Goal:** land the revised [UI_DESIGN.md](UI_DESIGN.md) across the surface that already
exists, **before** Phases 2-3 add roughly seven more subsystems and four more pages to it.

**Why this is a phase and not a polish pass.** The Phase 1 shell is structurally right and
was built against a token set that has since changed direction: dark-first Fluent `#0067C0`
became light-first `#2563D9`, four navigation destinations became eight, and the state
palette gained text variants it needs to pass contrast. None of that is expensive today.
All of it is expensive once twelve subsystems, a node canvas and four more pages are
sitting on top of it. This is the cheapest hour in the project.

**Scope:** no new subsystems. Everything here is the five Phase 1 subsystems plus valves,
re-presented — with two exceptions called out as open decisions below.

### WP1 — Tokens and typography *(blocks everything else)* — **done 2026-08-20**

- [x] Re-tint `Tokens.Light.xaml` to [UI_DESIGN §3.1-3.5](UI_DESIGN.md#3-design-tokens)
- [x] **Light is the default theme, even when Windows is set to dark.** `AppSettings.Theme`
      defaults to `Light` rather than `System`; dark is now chosen deliberately rather than
      inherited from the desktop. Pinned by `ThemeDefaultTests`
- [x] Re-tint `Tokens.Dark.xaml` to [§3.6](UI_DESIGN.md#36-dark-theme)
- [x] New tokens: `SurfaceHover`, `SurfaceSelected`, `StrokeSubtle`, `TextMuted`,
      `AccentBorder`/`AccentSelection`/`AccentSubtle`, six `State*TextColor` variants,
      eight `Path*` synoptic colours, five `Chart*` role colours
- [x] `FontNumeric` Consolas → Segoe UI with `Typography.NumeralAlignment="Tabular"`;
      added `NumericTextStyle` for non-headline figures and `FontMono` (Cascadia Mono)
      for the recipe JSON panel. `Segoe UI Variable` is still **not** named first
- [x] Radii to 4 px (inputs) / 6 px (cards); `RadiusLarge` retired as unused
- [x] Control fills repointed off `SurfaceSunken`, which the re-tint made wrong for inputs

**Three things WP1 turned up that were not on the list:**

- [x] **`TokenParityTests` written.** Both token files carried a comment claiming they were
      "asserted against each other in TecnalHub.Tests". **They were not** — no such test
      existed. WP1 added ~25 keys to each file, where a key in one and not the other is a
      crash on theme switch, so the test was written before the keys were added. It also
      computes WCAG contrast from the token files on every run.
- [x] **Amber `#D99000` failed contrast and was darkened to `#C67F00`.** The proposed fill
      measures **2.66:1 on white**, under the 3:1 WCAG floor for non-text marks — an 8 px
      amber state dot nobody can see. Caught by the new test, fixed in both the tokens and
      [UI_DESIGN §3.3](UI_DESIGN.md#33-process-state--the-colour-discipline).
- [x] **`SurfaceInput` / `SurfaceControl` semantic tokens added.** The correct fill for an
      input **inverts between themes**: white *above* an off-white card in light, but a
      well *below* the card in dark. Without this the dark theme's fields and buttons
      dissolved into their panels. Control templates now ask for "the input surface"
      rather than a specific grey.

**Acceptance — met:**

- [x] Both themes render with live simulator data; no missing-key crash on switch
      (`docs/evidence/ui/wp1-painel-{light,dark}.png`)
- [x] A grep for colour literals in `Views/` and `MainWindow.xaml` returns nothing —
      it already did before the re-tint, and still does
- [x] State text variants clear 4.5:1 and fills clear 3:1, asserted per-token per-run
- [x] 116/116 tests pass

> **The Consolas → Segoe UI swap was verified, not assumed.** Segoe UI's *default* digits
> are proportional: `1` has advance 0.402 against `0` at 0.555. Rendering `111,1` versus
> `000,0` at the 28 px readout size differs by **17.1 px** without the typography setter —
> the swap alone would have introduced exactly the jitter the monospace face existed to
> prevent. With `NumeralAlignment=Tabular` both measure 68.927 px. Segoe UI does ship
> `tnum`; a face that did not would be ignored gracefully rather than failing.

### WP2 — Control templates — **done 2026-08-20**

WPF's default chrome ignores the tokens, so anything without a template is visibly not
part of the application. Present before this WP: `Button`, `TextBox`, `ComboBox`,
`RadioButton`, `CheckBox`, `Separator`, `ToolTip`. **Added:**

- [x] `ToggleSwitch` — **blue when on, never green** ([§3.3](UI_DESIGN.md#33-process-state--the-colour-discipline)).
      Replaces the `CheckBox` for "Subsistema ativo"
- [x] `SegmentedControl` (`TabControl`/`TabItem`) — the `[SP & Limites][Cascata][PID][Saída]` strip
- [x] `Expander` — `Calibração`, `Saúde do Sensor`, `Avançado`, the JSON panel
- [x] `ScrollBar` — 10 px, no track, no arrow buttons. It was the most obvious piece of
      foreign chrome in the app, visible on Configurações
- [x] `ListView` / `ListViewItem` / `GridViewColumnHeader` for the Controle table —
      row separators only, no grid lines, no header chrome
- [x] Shared components in `src/TecnalHub/Controls/`: `StateDot`, `StateChip`,
      `ProvenanceBadge`, `SetpointField`
- [x] `StateToBrushConverter` gained a `Text` parameter for the darkened variants;
      new `StateToLabelConverter` so **every state reaches the screen as a word too**

> **`CommandedBadge` shipped as `ProvenanceBadge`.** It is named for what it does rather
> than for one of its values: `comandado` is the common case, but `calculado` (derived
> volume) and `estimado` (the Phase 2 OUR soft sensor) are the same idea and have to look
> identical. One component is the point — agitation, nutrient and the flask agitator have
> no feedback path at all, and five hand-written call sites is how one of them eventually
> forgets to say so.

**Two things WP2 turned up:**

- [x] **The runtime theme switch never worked.** The in-app `Tema` button logged success
      and repainted nothing; the theme was only ever correct because it is applied before
      the window is shown. **Pre-existing, not a WP1 regression** — `git diff HEAD` showed
      `ThemeService`, `App.xaml` and `App.xaml.cs` untouched by WP1.
      *Cause:* the brushes live in `Tokens.Shared.xaml` and the colours in the theme
      dictionary **beside** it. A `DynamicResource` written in one resource dictionary
      pointing at a sibling resolves on first read and is never re-evaluated — WPF
      invalidates the visual tree when a dictionary changes, but a brush sitting in
      another dictionary is not in the visual tree.
      *Fix:* `ThemeService` now owns slot 0 and refills its entries, then assigns
      `SolidColorBrush.Color` on each live brush. 53 brushes repaint per switch, verified
      light → dark → light by sampling rendered pixels.
- [x] **`Every_token_brush_has_a_matching_colour` test added.** `RepaintBrushes` pairs
      `FooBrush` with `FooColor` by naming convention, and a brush with no matching colour
      key is skipped **in silence** — it keeps its first colour and stops following the
      theme, which looks like one stubbornly light element on a dark page.

**Acceptance — met:**

- [x] No default WPF chrome on any page in either theme
      (`docs/evidence/ui/wp2-*.png`)
- [x] `ResourceKeyTests` proves every `{DynamicResource}` and `{StaticResource}` key in
      every XAML file resolves. This matters because three of the new templates have no
      page using them yet: a bad key is a **runtime** failure, not a compile error, so
      nothing would have caught it until WP5 or WP6 opened the page
- [x] Runtime theme toggle verified by pixel sampling, not by restart
- [x] 133/133 tests pass

> **Found in passing, for WP4:** the nav rail's items expose their raw record
> `ToString()` to accessibility tools — a screen reader announces
> `NavigationItem { Id = dashboard, Label = Painel, Glyph = }`. They need
> `AutomationProperties.Name` bound to `Label`.

### WP3 — Icon system — **done 2026-08-20**

- [x] `Resources/Icons/Icons.xaml` — **24 vector geometries**, authored in a 24 x 24 space,
      outline only. **No icon font**
- [x] `Icon` control with a `Key` string form, so `NavigationItem.Glyph` stays a plain
      string and no WPF type reaches the ViewModel
- [x] Wired the nav rail: icon + label, slate by default, accent when selected, plus a
      3 px accent left marker so selection is not carried by colour alone
- [x] Instrument glyphs for the whole device inventory — temperature, pH, O₂, impeller,
      airflow, pressure, level, foam, nutrient, pump, biomass, valve — drawn now so
      Phases 2-3 do not each invent their own thermometer
- [x] Nav rail 132 → 184 px (the WP4 width, pulled forward since the rail was being
      rebuilt anyway)

> **The `Glyph` field was not empty — it held Segoe MDL2 codepoints.** `""`,
> `""`, `""`, `""`: Home, chart, list, Settings. **Nothing anywhere sets
> an icon font**, so they would have rendered as tofu had anything bound them, and the
> DataTemplate never did. Someone started down the icon-font path and stopped. That path
> was the wrong one regardless — the Fluent set is Windows 11 only, and this project has
> already lost a session to a Win11-only font overflowing the stack in
> `TextBlock.MeasureOverride` before the first frame.

**Constant stroke weight.** The `Icon` control scales by `RenderTransform` and pre-divides
the pen by that scale, rather than using `Stretch`. `Stretch` folds the stroke into the
layout bounds, so a thicker pen visibly shrinks the glyph; this way the geometry occupies
exactly `Size` pixels and the rendered stroke is ~1.5 px at 16, 20 or 24.

**Four icons were redrawn after review.** Rendering the set as a contact sheet
(`docs/evidence/ui/wp3-icon-inventory.png`, each glyph at 72 px and at its real 20 px)
showed that **Gear** read as a sun, **Vessel** as a lightbulb, **Pump** as a pie chart,
and **Foam** was indistinguishable from **Biomass** — which matters, because those two sit
side by side in the dosing cards. The gear is now a real 8-tooth outline, generated
numerically rather than hand-placed.

- [x] **`AutomationProperties.Name` fixed** — the defect flagged during WP2. It has to go
      on the **container** via `ItemContainerStyle`, not on a child in the `DataTemplate`:
      a list item's automation peer takes its name from the bound object, so a screen
      reader was announcing `NavigationItem { Id = dashboard, Label = Painel, Glyph = }`.
      Now announces `Painel`.

**Acceptance — met:**

- [x] Icons take the theme stroke brush, verified by toggling at runtime — slate in light,
      lifted slate in dark, accent when selected (`wp3-painel-{light,dark}.png`)
- [x] No font dependency anywhere in the app
- [x] `IconTests` — every geometry parses, is non-empty, has extent, fits the 24 x 24 box,
      follows the `Icon{Name}Geometry` key convention, and every navigation glyph resolves.
      One case asserts glyphs are **not** private-use codepoints, so the MDL2 state cannot
      return
- [x] 161/161 tests pass

### WP4 — Shell refit — **done 2026-08-20**

- [x] Nav rail 132 → 184 px (WP3); three groups (Operação / Registro / Sistema) as
      dividers with no headings
- [x] Nav rail footer: the `Modo` command-ownership selector
      ([§4.2](UI_DESIGN.md#42-navigation-rail--184-px-expanded-52-px-collapsed))
- [x] KPI strip: horizontal scroll, `Configurar indicadores` checklist, persisted
      selection and order
- [x] KPI strip: `comandado` badge and **suppressed trend arrow** for commanded-only
      variables; setpoint line omitted rather than showing `SP —`
- [x] **Status bar** ([§4.6](UI_DESIGN.md#46-status-bar--32-px)) — new. Receita, Fase,
      Decorrido, Próxima ação, Registro, a composite system dot, and `Última atualização`,
      which turns `StateWarning` when telemetry stalls
- [x] Sensor-module chip in the title bar — already present and correctly conditional
- [x] Collapsible variable rail ([§4.3](UI_DESIGN.md#43-variable-rail--220-250-px-collapsed-by-default))
- [x] Responsive: rail folds away below 1400 px, detail pane becomes a drawer below
      1200 px ([§4.7](UI_DESIGN.md#47-responsive-behaviour))
- [x] Nav rail collapses to a 52 px icon strip below 1400 px; labels remain available as
      automation names/tooltips, and the complete rail returns without changing a saved
      preference

**Open decisions — resolved as recommended:**

- [x] **pH was exposed as a read-only KPI for Phase 1b.** It was already parsed, spike-filtered,
      calibrated, echoed back as `pHCal` and offered as a chart channel — the charts
      advertised a variable the dashboard denied existed. Phase 1b used a dedicated
      `ReadOnlyDetailView` rather than an empty entry field: showing a setpoint box for a
      loop that had not landed was the same class of lie as showing a commanded figure as
      measured. Phase 2 WP3 subsequently replaced it with the complete dosing panel.
- [x] **Variable rail shipped.** It changes the workspace column grid, so retrofitting it
      after four more pages exist was the expensive order.
- [x] **`Modo` ships with the ownership plumbing.** `Automático` and `Receita` are
      **disabled with a reason in the tooltip**, not hidden — verified through UI
      Automation. One command queue, one owner, visible from the start.

**Three things WP4 turned up:**

- [x] **Selection was not one selection.** The rail two-way binds `SelectedVariable`
      while KPI tiles raise a command, and the subsystem sync lived *inside the command* —
      so clicking a rail row changed the selection without changing the detail pane,
      leaving the previous subsystem's controls on screen under the wrong heading. The
      sync moved onto `OnSelectedVariableChanged`, so the property is what reacts and all
      three routes agree.
- [x] **`ComboToggleStyle` has no `ContentPresenter`.** It is ComboBox drop-down chrome:
      anything placed inside it silently vanishes, which turned the KPI configuration
      button into a bare chevron. Added `IconToggleStyle` for icon-only toggles.
- [x] **A failed binding is silent, and one shipped.**
      `{Binding SelectedSubsystem, RelativeSource={RelativeSource AncestorType=Window}}`
      asks the *Window object* for a property only its DataContext has. WPF resolved it to
      nothing, `Visibility` kept its default, and **two detail panes rendered on top of
      each other** with nothing reporting a problem. Fixed with `DataContext.` on the
      path, and `App.OnStartup` now routes `PresentationTraceSources.DataBindingSource`
      into Serilog in Debug builds so the next one is a log line rather than a
      screenshot review.

**Acceptance — met:**

- [x] The KPI strip scrolls rather than wrapping or truncating; verified at 1000 px where
      the sixth tile clips and the strip gains a scrollbar with the configuration button
      still reachable
- [x] **A stalled link degrades to `—`.** Verified against the simulator's `stall`
      scenario, which keeps answering `/ping` while telemetry freezes: the shell flagged
      it at **6.4 s**, 1.6 s ahead of the transport's own 8 s silence timeout, and every
      readout became an em dash (`wp4-stalled-link.png`). The UI stops trusting the data
      before the protocol gives up, which is the right order
- [x] Rail preference survives narrowing: it folds away below 1400 px and returns on its
      own, without rewriting what the operator chose
- [x] Zero XAML binding failures logged at startup
- [x] 161/161 tests pass

### WP5 — Detail pane restructure — **done 2026-08-20**

- [x] Header with state chip and deselect
- [x] `PV / SP / Δ` three-column readout. Δ is deliberately **not** coloured by sign:
      a positive deviation is not "good", and whether the process is healthy is what the
      state chip says
- [x] Inline trend chart (`TrendSpark`, 120 px, role palette — solid blue PV, dashed
      green setpoint), redrawing at 1 Hz only while loaded
- [x] Segmented tabs, **present only where the device actually has them**
- [x] `Saúde do Sensor` expander
- [x] `ToggleSwitch` replaces the enable `CheckBox` (WP2)

**Also, by request:**

- [x] **KPI strip removed from Painel, kept for Receitas** — [D-011](DECISIONS.md). Painel
      carried two viewers of the same six variables; the variable rail is now the single
      variable display and is no longer optional. The strip lives on as
      `Views/KpiStripView.xaml`, built and unplaced until Phase 3
- [x] **Photoreal reactor render implemented** — [D-012](DECISIONS.md),
      [UI_DESIGN §5.1.1](UI_DESIGN.md#511-the-reactor-image) and
      [ASSET_PROVENANCE](ASSET_PROVENANCE.md). One true-alpha neutral master serves both
      themes; calibrated native overlays and a vector decode fallback remain editable

**Acceptance — met:**

- [x] **Temperature shows no PID or Saída tab.** Verified through UI Automation: it
      renders **zero** tab items, so the strip is hidden entirely and the setpoint section
      shown directly — a one-segment segmented control is a heading pretending to be a
      choice. It also has no Calibração expander, because nothing about temperature is
      decoded on the PC side
- [x] **Flow is the only Phase 1 subsystem with two tabs** — `[SP & Limites][Saída]` —
      because it is the only one whose actuator state comes back on the wire
- [x] Zero XAML binding failures logged
- [x] 161/161 tests pass

> **The health expander says what it cannot show.** Raw counts, the spike filter's hold
> state and the rejected-sample count live inside the parser and are not exposed;
> `SpikeFilter` publishes only `LastGood`. Claiming to show sensor health while silently
> omitting the filter would be worse than admitting the gap, so the panel names it.
> Exposing that state is a parser change, deferred rather than faked.

### WP6 — Controle page *(new main window)* — **done 2026-08-20**

- [x] All-setpoints table: PV, applied SP, new SP, range, active, mode, owner, per-row apply
- [x] **Valve card — `valve_1` auxiliary, `valve_2` nitrogen, and the derived `v_Flow`
      vent state shown read-only.** This closes the one unbuilt item in Phase 1's locked
      scope
- [x] Bulk apply, combining dirty rows into one command object where the protocol allows
- [x] Setpoint presets: save, load, and **load-without-sending**
- [x] `Parada segura` — the application's only red button, behind a confirmation

**Acceptance — met:** a run's whole configuration is verifiable on one screen before
starting; `v_Flow` inversion is visible rather than remembered.

- [x] Five rows, both valve states, `maxFlow`, and the vent rule render at 1280×800 in
      both themes (`docs/evidence/ui/wp6-controle-{light,dark}.png`)
- [x] `ControlViewModelTests` proves dirty rows share one frame, preset loading sends
      nothing, an SP above staged `maxFlow` is refused, and safe stop defaults to cancel
- [x] Golden strings pin valve-open-at-zero-flow (`v_Flow:1`) and the complete core safe-stop
- [x] 169/169 tests pass; localhost simulator live, zero XAML binding failures

### WP7 — Page completion and navigation — **done 2026-08-20**

- [x] Keep **`Gráficos` as a dedicated dual-graph workspace** and add separate
      **`Históricos`** session browsing (file list, row count, exact-header check, open
      folder, CSV export, and load into Gráficos); add graph PNG/CSV export and cursor
      readout
- [x] `Registro` → **`Eventos`**: expand from device-log lines to the eight sources in
      [§5.6](UI_DESIGN.md#56-eventos), **including the exact JSON put on the wire**
- [x] Configurações: in-page section navigation; add the `Unidades` section
- [x] **Confirmation dialog on `resetVariables` and `restart`** — both destroy module
      process state, mid-cultivation, on one click today

**Acceptance — met:** the Eventos page remained populated over the localhost Wi-Fi
simulator, where `[ESP32_` lines never appear; exact successful command frames were
selectable without a debugger. A recorded 58-row session passed the frozen-header check
and rendered in the same two graph panels. Runtime review found and fixed the initial
filter-order crash and read-only detail binding. 178/178 tests pass; evidence is in
`docs/evidence/ui/wp7-*.png`.

### WP8 — Persistence and keyboard — **done 2026-08-20**

- [x] Persist window size, position and last page (rail state and KPI configuration
      already persist — done in WP4)
- [x] Keyboard map, searchable command palette and 2 px focus treatment
      ([§11](UI_DESIGN.md#11-localisation-and-accessibility)). `Ctrl+S` is reserved with
      an explicit Phase 3 reason. WP3 now uses `Ctrl+6` for Calibrações and `Ctrl+7` for
      Configurações; `Ctrl+8` remains reserved

**Acceptance — met:** UI Automation exercised `Ctrl+3`, `Ctrl+4`, `Ctrl+5`, `Ctrl+K`,
`Ctrl+R`, `Ctrl+S`, `F5`, `Space`, `Esc` and Tab against the live localhost simulator.
Eventos, a 1450×850 window at (140, 90), and the expanded variable rail restored after a
full close/relaunch. 185/185 tests pass; the fresh run contained zero XAML binding
failures or fatal exceptions. Evidence is in `docs/evidence/ui/wp8-*.png`.

### Phase 1 visual and responsive closure — **done 2026-08-20**

- [x] Replaced the schematic as the normal path with a 1024 × 1536 true-alpha PBR
      equipment render. The accepted structure has a double-wall jacket, top drive and
      entries, process probes, two Rushton turbine levels, a shaft that ends below the
      lower turbine, and a separately fed annular sparger
- [x] Kept readings, units, state dots, leader lines, selection, focus and hit targets as
      theme-aware WPF overlays; added the previously missing pH callout and an explicit
      `Equipamento vazio · nível não monitorado` state
- [x] Repainted the inline ScottPlot trend after a live theme switch so the dark Painel
      has no white foreign plotting surface
- [x] Closed the final responsive backlog item with the specified 52 px navigation rail
      at 1200-1400 px (and the same compact mode below 1200 px)
- [x] Added regression coverage for RGBA encoding, transparent canvas, asset dimensions,
      normalized anchors, fallback wiring and compact navigation. **190/190 tests pass**
- [x] Final evidence: `phase1-final-painel-{light,dark}.png` and
      `phase1-final-responsive-1280.png`

This closes the Phase 1/1b software scope. It does **not** close the cultivation gate:
the generated equipment is a visual asset, simulator data is not biological validation,
and the real bioreactor still has to demonstrate the locked exit criterion above.

### Resolved decisions for Phase 1b

| # | Question | Recommendation |
|---|---|---|
| 1 | **Expose pH as a read-only KPI?** It is parsed, spike-filtered, calibrated, echoed back as `pHCal` and offered as a chart channel — but has no tile and is not in `Variables`. Adding it touches the locked scope | **Yes for Phase 1b.** Phase 2 WP3 superseded the read-only limit with the complete v.6 five-field dosing state; probe calibration remains separate |
| 2 | **Ship the variable rail in 1b, or defer it?** | **Ship it.** It changes the workspace column grid, and retrofitting a column after four more pages exist is the expensive order |
| 3 | **Does `Modo` do anything before Phase 2?** With no cascade and no recipe engine, only `Manual` is reachable | Ship the control, wire the ownership plumbing, leave `Automático` and `Receita` disabled with a reason tooltip. The plumbing is what Phase 2 needs; adding it later means revisiting every control |

**Phase 1b exit criteria — closed against tests and the simulator:**

1. [x] Every current page passes light/dark review with no default WPF chrome; the
       resource suite rejects colour literals in Views.
2. [x] **Superseded by [D-011](DECISIONS.md):** process pages use the scrolling variable
       rail, not KPI tiles. `KpiStripView` remains scrollable and reserved for the Phase 3
       recipe surface, where twelve-variable reachability becomes relevant.
3. [x] Valve control sends the correct bytes, verified by golden-string test, including
       the `v_Flow` inversion.
4. [x] A stalled simulator degrades every readout to `—` and turns
       `Última atualização` to warning before the transport gives up (`wp4-stalled-link.png`).
5. [x] Both destructive device commands require confirmation.
6. [x] Page/WP and final Painel screenshots are in `docs/evidence/ui/`, including both
       themes and the 1280 px compact-shell breakpoint.

---

## Phase 2 — Cascade control + dosing

**Goal:** the scientific core. This is the phase the paper depends on.

**Cascade controllers.** Ported from the **ReceitasTECNAL** implementation, which
is the corrected one — not from v.6. The design is already written up in that
project's `docs/CASCATA_OD.md` and fixes three structural defects:

| v.6 defect | Fix carried over |
|---|---|
| Integral windup over long transients | Sliding-window integral + explicit `I_min`/`I_max` saturation |
| Positional PID drives output to 0 at setpoint (structurally wrong — the organism keeps consuming O2) | **Velocity-form** output: inner loop emits `dOutput`, `Output[i] = Output[i-1] + dOutput[i]` |
| 20-40 s polarographic probe dead time causes oscillation | **Prediction horizon**: `DOT_pred = DOT + (dDOT/dt) * t_pred`, `t_pred` 30-60 s |

Plus: rate estimation by **least squares** over the window rather than endpoint
difference, which cancels the probe's quantisation staircase.

**kLa gradient-path allocation.** The method from
[the submitted manuscript](../../../Doutorado_CNPq/_Artigos_e_Coorientacoes/Artigos/04_Cascata_kLa):
map kLa over agitation x aeration, fit a bicubic B-spline surface, pick the start
point by maximising mean actuator headroom, then track the steepest-ascent kLa
trajectory with the cascade PID.

> **Open decision — see [DECISIONS.md](DECISIONS.md) D-008.** The surface fit and
> gradient are currently computed in Python (scipy). Three options: embed a
> pre-computed surface as a data file the app interpolates; implement bicubic
> B-spline in C# (`MathNet.Numerics`); or keep a small Python sidecar. **Recommendation:
> embed the pre-computed surface** — the fitting is an offline research activity, the
> app only needs evaluation and gradient, and this keeps Python out of the runtime.

**Deliverables**

- [~] `CascadeController` — velocity-form, anti-windup, prediction horizon *(WP1 done; **gain
      scheduling** deferred to a later WP)*
- [~] Actuator-window allocation — agitation / aeration overlapping windows *(WP1 done;
      **enrichment (N₂)** window rides with the enrichment path)*
- [ ] kLa surface evaluation + gradient ascent *(gated on [D-008](DECISIONS.md); replaces
      the WP1 linear allocator without touching the controller)*
- [ ] OUR soft sensor
- [~] Dosing subsystems — **pH delivered in WP3**; Nutrient · Antifoam · Distance-foam ·
      Agitator flask remain
- [~] Controller tuning UI with live term display (P, I, D contributions visible) *(WP2 done,
      **advisory**: it computes on live telemetry and is tunable, but does not actuate; the
      kLa contour and a live tuning chart ride with later WPs)*

> **The UI for this phase is already specified**, so none of it needs designing twice.
> Tuning lives on **Controle → `Cascata e sintonia`**
> ([UI_DESIGN §5.2](UI_DESIGN.md#52-controle)), with the overlapping actuator windows
> drawn as a stacked bar and a live marker rather than six numeric fields. The `oxygen`
> detail pane gains its `Cascata` / `PID` / `Saída` tabs
> ([§6.1](UI_DESIGN.md#61-devicedetailpane)); no other device gets them, because no other
> device has a controller the app can observe. Each dosing subsystem gets a card on
> Controle and a position on the synoptic **in this phase**, alongside the subsystem
> itself. `Misturador de gases` — the nitrogen-enrichment actuator — ships disabled.

**Exit criteria:** a kLa-path-controlled run whose DOT tracking is at least as good
as the v.6 runs already recorded in the manuscript dataset.

### WP1 — Cascade controller core — **done 2026-08-20**

The scientific core's control law, built and validated headlessly before any of it is
wired to the wire or the UI. It is pure math in `src/TecnalHub/Services/Control/`, held to
the [ARCHITECTURE §5](ARCHITECTURE.md#5-testing-strategy) controller strategy — a simulated
first-order DOT plant with dead time, asserting no windup and no zero-at-setpoint collapse.

- [x] `LeastSquaresRateEstimator` — windowed slope fit that rejects the polarographic
      probe's quantisation staircase, so the derivative and the prediction consume a clean
      rate rather than an endpoint difference
- [x] `VelocityPidController` — the three corrected-design fixes: **velocity-form output**
      (holds the actuator at setpoint instead of collapsing to zero), **structural
      anti-windup** (the clamped output is the integrator; a reported integral bounded by
      `I_min`/`I_max` is held while railed), and the **prediction horizon**
      `DOT_pred = DOT + rate·t_pred`. Exposes the exact live terms the tuning workspace
      shows — `P`, `I`, `D`, `dSaída`, `Saída`, `DOT_pred`
- [x] `ActuatorWindowAllocator` — splits one control effort across agitation and aeration
      with overlapping windows (the [§5.2](UI_DESIGN.md#52-controle) stacked bar)
- [x] `CascadeController` — composes the loop and the split, and maps one step onto the
      frozen combined frame through `CommandBuilders.CascadeActuation`, `v_Flow` inversion
      and all. It does **not** send: the caller queues it on the shared command queue
- [x] 37 tests, including staircase rejection, the setpoint-hold and no-windup properties,
      a prediction-reduces-overshoot comparison on a dead-time plant, closed-loop tracking,
      and a culture-pinned golden cascade frame. **227/227 pass**

> **The gains are provisional simulator defaults, not a field tuning.** They were set once
> the loop was understood — a small `Kp` because the prediction folds into the error and
> `Kp·horizon` is the effective derivative, so a large proportional gain on a dead-time
> process injects a huge derivative and limit-cycles. Real gains come from a bioreactor
> run; anything tuned against the simulator's placeholder kLa is provisional.

**Deferred to later Phase 2 WPs, on purpose:** the [D-008](DECISIONS.md) kLa surface and
its gradient-path allocation (replaces the linear allocator, not the controller); gain
scheduling; the OUR soft sensor; the five dosing subsystems and their synoptic positions;
the `oxygen` detail-pane `Cascata`/`PID`/`Saída` tabs; mode ownership and live actuation.
WP1 is the piece every one of those builds on, and the only piece that needs neither the
bioreactor nor D-008.

### WP2 — Cascade tuning workspace *(advisory)* — **done 2026-08-20**

The tuning UI specified in [§5.2](UI_DESIGN.md#52-controle), wired to the WP1 controller —
but in an **advisory** role: it runs the cascade against live oxygen telemetry so the
operator can watch it track and tune it, without any actuation. This is the honest,
safe increment; the controller must be trusted before anything sends its output to a reactor.

- [x] `CascadeService` — an advisory runtime that steps the controller on each telemetry
      frame (real elapsed time via an injectable `TimeProvider`) and exposes the terms. It
      **never** calls `IDeviceService.Send`; arming resets the loop, disarming clears it
- [x] `Controle → Cascata e sintonia`, the second tab: **Malha** (O₂ controlled, setpoint,
      manipulated checklist), **PID** (all seven parameters, staged apply/revert with
      validation), **Janelas de atuação** (editable windows + a live stacked allocation bar
      with the effort marker), **Termos ao vivo** (`P`/`I`/`D`/`dSaída`/`Saída`/`DOT_pred`
      plus O₂, rate, error and the allocated agitation/aeração), and named `Salvar/Carregar
      sintonia` that only stage
- [x] `CascadeSettings`/`CascadeTuningPreset` in the typed settings; `TimeProvider` in the
      composition root
- [x] 15 tests (242/242). Live simulator review: opened on the tab, connected, streamed
      telemetry, rendered with **zero XAML binding failures** (first frame 1017 ms)

**Deferred to later WPs:** live actuation under `Automático` ownership; the `oxygen`
detail-pane `Cascata`/`PID`/`Saída` tabs; the kLa `Trajetória` contour and gradient path
([D-008](DECISIONS.md)); and a live tuning chart.

### WP3 — pH control + guided calibration — **done 2026-08-20**

The missing pH control was not a firmware gap: v.6 and the module already expose an
explicit five-field dosing state. What was missing was the new app's UI and the ownership
boundary between probe calibration, module display and actuation. This WP closes that gap
and pulls the three mature v.6 calibration procedures forward from Phase 3.

- [x] Complete pH desired state on Painel and Controle: `pHSetpoint`, `pHError`,
      `pHOperation`, `pHMix`, `pHIntensity`, emitted atomically and validated against the
      firmware ranges. Pump speed preserves v.6's `percent × 10`; malformed text is refused
      rather than replaced with pH 7
- [x] Dedicated **Calibrações** destination (`Ctrl+6`; Configurações moves to `Ctrl+7`):
      - pH one/two-point stability + averaging with a full dosing safe-stop before the
        probe leaves the vessel;
      - direct oxygen two-point calibration of the app parser only;
      - certified airflow points, distinct-frame `FlowVoltage` averaging, the 0.0545 V
        two-segment fit and explicit partial/complete coefficient send
- [x] Calibration math isolated from WPF, persisted points/coefficients, settings live
      synchronization, and a professional theme-aware flow curve. The pH and oxygen curves
      stay in the app; accepted pH is echoed to the module as quoted `pHCal`; the flow curve
      belongs to the dedicated flowmeter firmware
- [x] Operator safe-stop now merges the frozen Phase 1 core frame with the complete pH-off
      state. Link loss cancels acquisition and invalidates prepared flow state
- [x] 259/259 tests; localhost HTTP accepted the combined pH/echo/flow frame and subsequent
      safe-stop. Light/dark 1280×800 evidence is in `docs/evidence/ui/phase2-calibration-*`

**Hardware gate remains open:** software tests do not certify buffers, O₂ references,
the external flow standard, pump direction or physical interlocks. See
[CALIBRATION.md](CALIBRATION.md#6-estados-de-recusa-e-limite-de-validação).

---

## Phase 3 — Remaining subsystems + recipes

**Goal:** feature parity with v.6, plus the automation that v.6 never had.

- [ ] Biomass sensor (blank, thresholds, integration time)
- [ ] External pump, all five profile modes including polynomial `p0..p20` and piecewise
- [ ] **Remaining calibration procedures** — the level reference and biomass blank/
      thresholds. The dedicated page and pH, oxygen and airflow procedures were delivered
      early in Phase 2 WP3 ([UI_DESIGN §5.7](UI_DESIGN.md#57-calibrações))
- [ ] **Receitas page** — node canvas, execution engine, JSON persistence

On recipes: the concept and the engine architecture come from ReceitasTECNAL —
node graph, validator, engine sliced by responsibility. The **UI, visual language
and product identity are new**; this is not a re-skin of that app. Improvements to
make while re-implementing, rather than copying forward:

- Node definitions declared once and generated, instead of hand-written model +
  viewmodel + view per node type (ReceitasTECNAL has ~10 near-duplicate triples).
- The engine drives the same `ITransport` as manual control, so a recipe and an
  operator cannot fight over the link — one command queue, one owner.
- Recipe JSON versioned from v1, with a migration hook. ReceitasTECNAL learned this late.

> **Read [UI_DESIGN §5.3](UI_DESIGN.md#53-receitas) before starting.** It carries the full
> block inventory — nineteen types in six categories, taken from ReceitasTECNAL's
> `NodeType` enum — with each block's parameters and its **re-targeting to the ESP32-S3**.
>
> The re-targeting is the part that is easy to get wrong: **ReceitasTECNAL drives a
> different machine.** It reads a TECNAL HMI over Modbus and writes setpoints by
> screen-scraping that HMI over VNC. The graph, the engine slicing and the control
> mathematics port over; the VNC layer, the Modbus register map, the ×10/×100 scale
> factors and `IntervaloAtuacaoVnc` **must not**. Nor may the robot-configuration panel
> that fills the right-hand side of both concept mockups — pipetting automation is not
> part of this product.

---

## Phase 4 — Packaging and field readiness

- [ ] Inno Setup installer, self-contained, no runtime prerequisite
- [ ] Crash reporting that survives a hard kill (v.6's `crash_log.txt`, done properly)
- [ ] First-run experience when no hardware is present
- [ ] Operator documentation in pt-BR
- [ ] Startup-time regression test in CI

---

## Phase 5 — Alarms and field polish

**Goal:** the surfaces that need the whole app to exist before they can be built.

> **Most of this phase was absorbed.** It began as an unagreed list seeded from a page
> review (`docs/evidence/ui/`). The [UI_DESIGN.md](UI_DESIGN.md) revision of 2026-08-20
> specified all eight main windows, which moved the shell, KPI-strip, settings-navigation,
> confirmation, chart-export, event-log, icon, persistence and keyboard items into
> **Phase 1b**. pH, oxygen and airflow calibration then moved forward into **Phase 2 WP3**;
> level and biomass procedures remain in Phase 3. What remains genuinely needs the later
> phases underneath it.

### Alarms

- [ ] **Alarm engine.** v.6 had an audible alarm and an alarm state; the state colours
      exist here but nothing raises or acknowledges an alarm. Needs per-variable HH/H/L/LL
      limits with deadband and delay, plus the seven system alarms — link lost, module
      offline, flowmeter offline, frozen data, sensor absent, unacknowledged command,
      persistent foam ([UI_DESIGN §5.4](UI_DESIGN.md#54-alarmes)).
- [ ] **Alarmes page** — active, history, configuration. Design is specified and ready to
      build once the engine exists.
- [ ] Alarm limits are in **engineering units** and must not be confused with the
      spike-filter thresholds, which are in raw ADC counts.

### Synoptic coverage

- [ ] The drawing covers the core loop only. Dosing pumps, level/foam, biomass and the
      external pump gain their positions **alongside the subsystems that introduce them**
      in Phases 2 and 3, not in a single later pass.
- [ ] The flask agitator is a **separate bench device** and must not appear on the reactor
      drawing.

### Field readiness

- [ ] Audible alarm, with a silence timeout.
- [ ] Operator walkthrough of every page against the pt-BR wording, in one review.

---

## Explicitly deferred

| Item | Why | Revisit |
|---|---|---|
| **kLa gassing-out estimation** | Depends on torch + a `.pth` model. The single largest startup cost and an awkward runtime dependency. | Own project, as a standalone analysis module — not inside the controller app. |
| **torch / neural inference of any kind** | Same. | With the gassing-out module. |
| **Nitrogen enrichment path** | The kLa controller core is stable in ReceitasTECNAL but *without* enrichment; the enrichment path needs the changes described in the manuscript. | Phase 2, after the base cascade is validated. |
| **Multi-station hub support** | Telemetry exposes `HubStations` but v.6 never used it. | Only when a second module physically exists. |

---

## Sequencing rationale

```text
Phase 0  ──▶  Phase 1  ──▶  Phase 1b ──▶  Phase 2  ──▶  Phase 3  ──▶  Phase 4  ──▶  Phase 5
protocol      shell +       design        cascade +     rest +        installer     alarms +
spike         core loop     refit         kLa path      recipes                     polish

     ▲            ▲            ▲              ▲
     │            │            │              └── the scientific payload; needs a stable
     │            │            │                  link AND a stable core loop under it
     │            │            └── the cheapest hour in the project: the shell is
     │            │                re-tinted while it is four pages, not twelve
     │            └── first genuinely usable build
     └── the only true unknown: does C# reproduce the wire exactly?
```

Risk is front-loaded on purpose. Phase 0 is small, unglamorous, and the only phase
that can invalidate the whole plan — so it goes first, and it is allowed to fail
cheaply.

**Phase 1b is scheduled where it is for a second reason.** Phase 0's exit criteria 3 and
4, and Phase 1's exit criterion, all require the bioreactor. Phase 1b requires only the
simulator. It is therefore the right work to do while the hardware is unavailable, and
doing it later means re-checking every page Phases 2-3 will have added.
