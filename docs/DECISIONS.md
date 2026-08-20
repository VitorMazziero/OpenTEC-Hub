# Decision Log

> One entry per decision that would otherwise be re-litigated in three months.
> Newest last. A decision is only "Open" if it genuinely blocks work.
>
> **Docs:** [README](README.md) · [Roadmap](ROADMAP.md) · [Architecture](ARCHITECTURE.md) · [Calibration](CALIBRATION.md) · [UI Design](UI_DESIGN.md)

---

### D-001 · Rebuild in C# / WPF rather than fix the Python app
**Status:** Accepted · 2026-08-19

v.6 works. The rebuild is justified by cold-start time, a UI that does not scale to
12 subsystems, and a 1086-line `main.py` whose preference marshalling makes changes
unsafe. WPF also gives a genuinely native Windows result, which is a stated goal.

*Consequence:* v.6 stays installed and is the reference implementation until
TECNAL-Hub has completed a full cultivation.

---

### D-002 · The ESP32-S3 firmware and wire protocol are frozen
**Status:** Accepted · 2026-08-19

The board is not being reflashed. The wire format is therefore a fixed contract,
documented in [PROTOCOL.md](PROTOCOL.md) and enforced by golden-string tests.

*Consequence:* oddities on the wire (`v_Flow` inversion, the `pHCal` string echo,
the USB-only trailing newline) are **preserved verbatim**. Cleanups happen above the
wire only.

---

### D-003 · .NET 10 LTS, self-contained
**Status:** Accepted · 2026-08-19

.NET 10 is supported to Nov 2028; .NET 8 LTS ends Nov 2026 — about three months from
today, which would mean shipping onto an already-expiring runtime. Self-contained
publish plus an installer means lab PCs need nothing preinstalled.

*Rejected:* .NET 8 (matches ReceitasTECNAL, but expiring). Framework-dependent
(smaller download, but a runtime prerequisite on every lab machine).

---

### D-004 · Fluent light + dark, following the system theme
**Status:** Accepted · 2026-08-19

Gives the "integrated Windows application" feel that was the goal. Colour is reserved
for equipment state; chrome stays greyscale. See [UI_DESIGN.md](UI_DESIGN.md).

*Rejected:* carrying over the ReceitasTECNAL navy palette — it is that product's
identity, and this is a different product.

---

### D-005 · Dashboard is a synoptic + detail pane + KPI strip ("2+1 hybrid")
**Status:** Accepted · 2026-08-19

The synoptic answers "what is the equipment doing" with physical context; the detail
pane answers "change this one parameter" without putting 12 subsystems on screen at
once; the KPI strip keeps the safety glance available from every page.

*Rejected:* pure synoptic (clutters as soon as controls land on the diagram); pure
instrument rail (loses the physical relationship between variables); KPI strip + tabs
alone (still reads as a software dashboard rather than a reactor interface).

---

### D-006 · Auto-connect with a status chip; Configurations stops being a destination
**Status:** Accepted · 2026-08-19

Connecting was the only reason most users opened the v.6 Configurations window.
The app now restores the last link and connects in the background after the shell is
visible. Advanced Settings keeps every existing option, out of the way.

---

### D-007 · pt-BR UI, English code
**Status:** Accepted · 2026-08-19

All identifiers, comments, logs and commit messages in English; all user-facing text
in pt-BR, in `.resx` files. Diverges from the ReceitasTECNAL mixed convention.

*Rationale:* the work is attached to an international manuscript and may be read by
collaborators outside Brazil; English code stays readable to any C# developer, while
the operators keep a Portuguese interface.

---

### D-008 · kLa mapping and path allocation are an in-app experimental workflow
**Status:** Accepted · corrected by the project owner 2026-08-20

The operational feature is a dedicated **Mapeamento kLa** window. It is not a selector
for a surface bundled with the application. The operator creates a named experiment,
defines the agitation and airflow domain, and enters the experimentally measured
triples `(Q_g, N, kLa)`. The application then applies the method described by the paper
to estimate the broth-specific surface `kLa(Q_g, N)` and construct the allocation path.

The scientific contract is the reference workflow in
`04_Cascata_kLa/app/kLa Control Lab/equations.md`:

1. validate and normalize the actuator domain;
2. reconstruct the continuous kLa surface from the experimental anchors using the
   reference sequence: 300×300 Clough-Tocher cubic grid, nearest fill outside the convex
   hull, Gaussian smoothing with `sigma=5`, then a continuous bivariate spline;
3. calculate the spline derivatives in normalized actuator coordinates;
4. evaluate candidate initial points and select the path with maximum mean actuator
   headroom;
5. integrate the normalized gradient in both directions, orient the result from low to
   high kLa, and consolidate it into a monotonic allocation relation;
6. publish a reviewed, versioned profile mapping a requested kLa to simultaneous
   `(Q_g, N)` commands.

Editing an anchor or a scientific setting invalidates the fitted surface, gradient,
path and any unpublished result downstream. A controller may consume only an explicitly
reviewed and published profile. Production starts with **no active kLa map**. Paper data
may exist only in test fixtures or documentation; it is not installed in the production
profile store and is never offered as an operational selection.

*Implementation consequence:* the numerical C# implementation must be compared against
fixtures generated by the paper's Python/SciPy reference pipeline. A visually plausible
surface is not sufficient. The fitting window remains outside the real-time controller;
only the immutable published path enters the control loop.

*Superseded:* the original proposal to ship a pre-computed surface and let the app only
evaluate it. A Python runtime sidecar remains rejected because it would reintroduce the
runtime and deployment dependency removed by this rebuild.

---

### D-009 · Recipes — node canvas, new implementation, new identity
**Status:** Accepted · 2026-08-19

The canvas concept and the engine architecture from ReceitasTECNAL carry over; the
UI, visual language and product identity do not. TECNAL-Hub is a distinct product,
not a re-skin.

Improvements to make while re-implementing rather than copying forward:
- Node definitions declared once and generated, instead of a hand-written
  model + viewmodel + view triple per node type.
- The recipe engine drives the **same command queue** as manual control, so a running
  recipe and an operator cannot fight over the link.
- Recipe JSON versioned from v1 with a migration hook.

*Scope:* Phase 3. Not before the cascade control is validated.

---

### D-010 · kLa gassing-out and torch leave the controller app
**Status:** Accepted · 2026-08-19

`import torch` is the largest single contributor to v.6's cold start, and neural
inference is not something a control loop should carry. The gassing-out estimator
becomes its own project.

*Consequence:* the `.pth` model, the gassing-out page and its scipy dependencies are
not ported. A plan for the standalone module is written separately.

---

### D-011 · The KPI strip belongs to Receitas, not to every page
**Status:** Accepted · 2026-08-20

WP4 delivered the strip and the variable rail together, and Painel ended up carrying
**two viewers of the same six variables** — the horizontal strip on top and the rail on
the left, showing identical values a few hundred pixels apart.

*Decision:* the KPI strip appears **only on Receitas**. On process and record pages the
variable rail is the variable display and is no longer optional. **Controle is the one
exception:** its all-setpoints table already carries every PV, so the rail is hidden there
to keep the complete configuration and action columns visible at 1280 px.

*Rationale:* the rail keeps its meaning next to the synoptic, where a value sits beside
the drawing of the thing that produced it. On Receitas the strip earns its place for the
opposite reason — the canvas there describes the *intended* process, and the strip is the
only thing on screen showing the *actual* one. That contrast is the whole point of
[UI_DESIGN.md](UI_DESIGN.md#5315-process-versus-recipe).

*Consequence:* reverses "KPI strip visible on every page" from the original §4.4. The
strip is kept as `Views/KpiStripView.xaml` — built, tested, and unused until Phase 3
places it on Receitas. The shell now binds the rail's actual visibility to its responsive
state; before WP6 that property existed but the rail border ignored it.

---

### D-012 · The synoptic becomes a photorealistic render with editable anchored cards
**Status:** Implemented · 2026-08-20 · see [UI_DESIGN.md §5.1.1](UI_DESIGN.md#511-the-reactor-image) and [asset provenance](ASSET_PROVENANCE.md)

The flat vector drawing reads as a diagram of a tank rather than as the machine in the
room. Reference for the target: `docs/UI_design_guides/Bioreactor Panel.png`.

*Decision:* the reactor becomes a high-resolution render, with callout cards anchored to
the physical ports by leader lines. **Those cards are the primary way an operator edits a
setpoint** — not read-only labels.

*Rationale:* the reactor drawing is what makes this a bioreactor application rather than a
dashboard. Anchoring the control to the physical port also teaches the process: the
operator learns where the pH probe enters the vessel while adjusting it.

*Constraint:* nothing that changes at 1 Hz may be baked into the image. The render is a
static asset; values, state colours, leader lines and hit targets stay as WPF overlays on
top of it.

*Implementation:* one 1024 × 1536 neutral RGBA master serves both themes. True alpha was
verified, so the solid-background light/dark contingency was not needed. The accepted
geometry shows a double-wall jacket, top entries and probes, two Rushton turbine levels,
and a separately fed annular sparger; the shaft stops below the lower turbine rather than
continuing to the sparger. Normalized anchors ship beside the image, pH now has its own
card, and a decode failure exposes the vector fallback. The generated image carries no
liquid level or operational state.

---

### D-013 · Cascade controller is velocity-form with structural anti-windup, in the app assembly
**Status:** Accepted · 2026-08-20 · see [PHASE_LOG P2-01…P2-03](PHASE_LOG.md#phase-2--cascade-control--dosing)

The Phase 2 control law lives in `src/TecnalHub/Services/Control/` as pure C# (no WPF, no
wire, no telemetry), per the [ARCHITECTURE.md](ARCHITECTURE.md#3-directory-layout) layout,
and is unit-tested through the app-assembly reference the test project already carries.

The controller is **true velocity form**: the output is the clamped integrator, so it holds
the actuator at setpoint rather than collapsing to zero, and windup is structural — there is
no unbounded integral state to unwind. `I_min`/`I_max` bound and display the integral
*contribution*; they are an explicit operator cap, not the binding windup protection.

*Rejected:* a positional PID with a separately clamped integral accumulator. It read as a
faithful "sliding integral + I_min/I_max", but froze the output above setpoint when the
accumulator railed — a measured **45.9 %** steady-state error against a 30 % target
([PHASE_LOG P2-02](PHASE_LOG.md#p2-02--the-output-is-velocity-form-anti-windup-is-structural-not-a-clamped-integrator)).

*Consequence:* the prediction horizon folds into the error, so `Kp` is deliberately small
(`Kp·horizon` is the effective derivative). This is a property of the corrected design, not a
tuning accident, and it is why the defaults are gentle. Field gains still need the bioreactor.

---

### D-014 · Calibration ownership is split by channel; pH calibration always interlocks dosing off
**Status:** Accepted and implemented · 2026-08-20 · see [CALIBRATION.md](CALIBRATION.md)

pH and oxygen calibration curves belong to the app parser. The module receives quoted
`pHCal` values because its display/controller needs the app-calibrated result; that echo is
not an actuator command. Airflow differs: its two-segment coefficients belong to the
dedicated flowmeter firmware and cross the wire only after explicit operator review.

The pH control loop was never absent from v.6 or the firmware. The new app had postponed
its UI, leaving pH read-only despite already feeding calibrated values back to the module.
WP3 therefore exposes all five control fields atomically and keeps them separate from the
probe curve.

*Safety consequence:* starting one- or two-point pH calibration sends a complete pH-off
state before the probe leaves the vessel. Cancel/apply never re-arms the pump. Link loss
cancels acquisition, equal raw points are refused, and malformed control input cannot fall
back to a plausible value. Airflow capture similarly invalidates its prepared state after
link loss and retains an explicit safe-stop.

*Rejected:* treating all calibration as firmware-side; sending O₂ coefficients that v.6
never sent; using `pHSetpoint` as a calibration target; automatically restoring pH dosing
after the operator applies a curve; silently installing `slope=1` for equal pH raw points.

*Validation boundary:* 259 automated tests and the localhost simulator validate equations,
state transitions and bytes. They do not certify physical buffers, reference instruments,
pump direction or the real bioreactor interlocks.

---

### D-015 · One command arbiter owns the wire; ownership is per actuator and safe-aborts on link loss
**Status:** Accepted and implemented (part 1) · 2026-08-20 · see [PHASE_LOG P2-07](PHASE_LOG.md#p2-07--the-command-arbiter-is-the-single-gate-onto-the-wire)

Every command reaches the transport through a single `CommandArbiter`. It decorates the
transport wrapper, so the `IDeviceService` the whole application resolves *is* the arbiter:
a plain `Send` is a Manual dispatch, and nothing can reach the wire without an owner. The
arbiter also exposes `ICommandArbiter` for the ownership and command-lifecycle surface.

Ownership is tracked **per `ActuatorId`** (temperature, agitation, oxygen, aeration,
pressure, pH dosing), not globally: the cascade can own the oxygen actuators while the
operator still holds temperature. A command is sent only if the requester owns **every**
actuator it touches; one owned-by-another actuator refuses the whole frame — a partial send
leaves the reactor in a state neither owner asked for. `Manual` owns everything until
something explicitly `Claim`s it, transfers are journalled and carry the last commanded
state so a new owner starts bumpless, and a link/feedback loss **revokes every non-Manual
owner back to Manual** and raises an alarm-severity event.

The command lifecycle is honest about what the wire can prove: `Issued → TransportAccepted`
for every actuator, and `→ TelemetryConfirmed` **only for aeration**, the one actuator whose
applied setpoint the firmware echoes (`FlowSetpoint`, with `FlowCommandAck` alongside).
Temperature, agitation, oxygen, pressure and pH have no setpoint echo, so they rest at
`TransportAccepted` and label the channel "sem eco" rather than claiming a confirmation the
wire never gave. A command the transport never accepts within the budget, or that is
outstanding when the link drops, becomes `TimedOut`.

*Rejected:* a single global `Manual/Automático/Receita` mode (it cannot express the cascade
owning O₂ while the operator holds pH); routing sends straight to `IDeviceService` with
ownership bolted on at each call site (a future control surface would forget it); claiming a
telemetry confirmation for setpoints the firmware does not echo.

*Consequence:* `CommandOwner` moved from the shell into the communication layer. Only
`Manual` is reachable from the UI today; `Automatic` arrives with live actuation (WP6) and
`Recipe` with the engine (Phase 3). The arbiter, its lifecycle and its safe abort ship now
so the model is real and enforced from the start rather than retrofitted onto twelve control
surfaces later. The pH-calibration echo (`pHCal`) is a protocol-internal reflex below
`IDeviceService` and is intentionally unowned, so it is never blocked by pH-dosing ownership.

*Scope of part 1:* command ownership, the lifecycle, safe abort and the operator session
clock. The full operational **alarm engine** (link/module/flowmeter/frozen/sensor-absent
alarms with deadband, acknowledgement and timed audible silence) and the remaining Phase 0
link cleanup remain the rest of WP4.

---

## Open questions

| # | Question | Blocks |
|---|---|---|
| [PROTOCOL.md](PROTOCOL.md#5-open-questions-for-hardware-verification) Q1-Q5 | Firmware behaviours that could not be confirmed from Python source | Phase 0 exit |
| — | Does the nitrogen-enrichment path change the wire protocol, or only the control law? | Phase 2 |
| — | Is there a second bioreactor module to support (`HubStations`)? | Phase 3+ |
