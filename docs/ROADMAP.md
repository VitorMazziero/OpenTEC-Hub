# TECNAL-Hub — Build Roadmap

> **Version:** 0.1.0 · **Written:** 2026-08-19
> Phased plan to rebuild the working Python v.6 controller as a C# / WPF application
> without ever losing a working link to the ESP32-S3.
>
> **Docs:** [README](README.md) · [Architecture](ARCHITECTURE.md) · [Protocol](PROTOCOL.md) · [Migration](MIGRATION.md) · [UI Design](UI_DESIGN.md) · [Decisions](DECISIONS.md) · [Conventions](CONVENTIONS.md)

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

- [ ] Shell: title bar, nav rail, always-visible KPI strip
- [ ] **Auto-connect on launch** to the last-used port/IP, with the status chip and
      its popover ([UI_DESIGN.md](UI_DESIGN.md#3-connection-ux))
- [ ] Synoptic view of the reactor + side detail pane, responsive fallback to the
      bottom drawer under 1200 px
- [ ] Settings persistence — **typed and serialised, not hand-marshalled**
- [ ] Live charts (ScottPlot) for the five core variables
- [ ] CSV session logging, matching v.6's column format so old analysis scripts keep working
- [ ] Serilog rolling file + an in-app log pane
- [ ] Light/dark toggle following the Windows system theme

**Exit criteria:** a real cultivation run controlled end-to-end by TECNAL-Hub with
v.6 closed, temperature and agitation and flow all holding setpoint, and a CSV that
the existing analysis scripts read without modification.

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

- [ ] `CascadeController` — velocity-form, anti-windup, prediction horizon, gain scheduling
- [ ] Actuator-window allocation (agitation / aeration / enrichment, overlapping windows)
- [ ] kLa surface evaluation + gradient ascent
- [ ] OUR soft sensor
- [ ] pH · Nutrient · Antifoam · Distance-foam · Agitator flask
- [ ] Controller tuning UI with live term display (P, I, D contributions visible)

**Exit criteria:** a kLa-path-controlled run whose DOT tracking is at least as good
as the v.6 runs already recorded in the manuscript dataset.

---

## Phase 3 — Remaining subsystems + recipes

**Goal:** feature parity with v.6, plus the automation that v.6 never had.

- [ ] Biomass sensor (blank, thresholds, integration time)
- [ ] External pump, all five profile modes including polynomial `p0..p20` and piecewise
- [ ] Flow calibration dialog (two-segment curve at 0.0545 V)
- [ ] **Recipe canvas** — node graph, execution engine, JSON persistence

On recipes: the concept and the engine architecture come from ReceitasTECNAL —
node graph, validator, engine sliced by responsibility. The **UI, visual language
and product identity are new**; this is not a re-skin of that app. Improvements to
make while re-implementing, rather than copying forward:

- Node definitions declared once and generated, instead of hand-written model +
  viewmodel + view per node type (ReceitasTECNAL has ~10 near-duplicate triples).
- The engine drives the same `ITransport` as manual control, so a recipe and an
  operator cannot fight over the link — one command queue, one owner.
- Recipe JSON versioned from v1, with a migration hook. ReceitasTECNAL learned this late.

---

## Phase 4 — Packaging and field readiness

- [ ] Inno Setup installer, self-contained, no runtime prerequisite
- [ ] Crash reporting that survives a hard kill (v.6's `crash_log.txt`, done properly)
- [ ] First-run experience when no hardware is present
- [ ] Operator documentation in pt-BR
- [ ] Startup-time regression test in CI

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
Phase 0  ──▶  Phase 1  ──▶  Phase 2  ──▶  Phase 3  ──▶  Phase 4
protocol      shell +       cascade +     rest +        installer
spike         core loop     kLa path      recipes

     ▲            ▲             ▲
     │            │             └── the scientific payload; needs a stable
     │            │                 link AND a stable core loop under it
     │            └── first genuinely usable build
     └── the only true unknown: does C# reproduce the wire exactly?
```

Risk is front-loaded on purpose. Phase 0 is small, unglamorous, and the only phase
that can invalidate the whole plan — so it goes first, and it is allowed to fail
cheaply.
