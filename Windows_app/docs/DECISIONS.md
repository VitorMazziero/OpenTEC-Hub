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
OpenTEC-Hub has completed a full cultivation.

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

*Rejected:* .NET 8 (matches ReceitasOpenTEC, but expiring). Framework-dependent
(smaller download, but a runtime prerequisite on every lab machine).

---

### D-004 · Fluent light + dark, following the system theme
**Status:** Accepted · 2026-08-19

Gives the "integrated Windows application" feel that was the goal. Colour is reserved
for equipment state; chrome stays greyscale. See [UI_DESIGN.md](UI_DESIGN.md).

*Rejected:* carrying over the ReceitasOpenTEC navy palette — it is that product's
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
in pt-BR, in `.resx` files. Diverges from the ReceitasOpenTEC mixed convention.

*Rationale:* the work is attached to an international manuscript and may be read by
collaborators outside Brazil; English code stays readable to any C# developer, while
the operators keep a Portuguese interface.

---

### D-008 · kLa mapping and path allocation are an in-app experimental workflow
**Status:** Implemented in Phase 2 WP5 · corrected by the project owner 2026-08-20

The operational feature is a dedicated **Mapeamento kLa** window. It is not a selector
for a surface bundled with the application. The operator creates a named experiment,
defines the agitation and airflow domain, and enters the experimentally measured
triples `(Q_g, N, kLa)`. The application then applies the method described by the paper
to estimate the broth-specific surface `kLa(Q_g, N)` and construct the allocation path.

The conceptual contract is documented in
`04_Cascata_kLa/app/kLa Control Lab/equations.md`; the executable numerical oracle is
`04_Cascata_kLa/analysis/1_kla_mapping_gradient/gradient_path_score.py`:

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

*Implemented:* WP5 ports the Clough–Tocher construction, nearest fill, Gaussian filter,
not-a-knot bicubic surface and SciPy-compatible adaptive RK45/headroom search into a pure
managed service. Every numerical parameter and algorithm identity is visible; custom/preview
runs cannot be published as the paper method. Mutable drafts preserve blank measurements,
while create-only versioned receipts carry complete inputs, diagnostics, allocation and
SHA-256 fingerprints. See [KLA_MAPPING.md](KLA_MAPPING.md).

*Superseded:* the original proposal to ship a pre-computed surface and let the app only
evaluate it. A Python runtime sidecar remains rejected because it would reintroduce the
runtime and deployment dependency removed by this rebuild.

---

### D-009 · Recipes — node canvas, new implementation, new identity
**Status:** Accepted · 2026-08-19

The canvas concept and the engine architecture from ReceitasOpenTEC carry over; the
UI, visual language and product identity do not. OpenTEC-Hub is a distinct product,
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

The Phase 2 control law lives in `src/OpenTECHub/Services/Control/` as pure C# (no WPF, no
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

### D-016 · System alarms latch, acknowledge and clear on a deadband, with a timed audible silence
**Status:** Accepted and implemented · 2026-08-21 · see [PHASE_LOG P2-09](PHASE_LOG.md#p2-09--the-operational-alarm-engine-latches-acknowledges-and-silences-on-a-timer)

The six system alarms (§5.4.3) run through one `AlarmService`. Each alarm is a small state
machine: a condition must hold for an **on-delay** before it raises, it then **latches**, and
it clears only once it has been both **acknowledged** and clear for an **off-deadband**. The
deadband stops a value sitting on a boundary from chattering; the latch means a fault that
comes and goes is not lost.

**Returned-unacknowledged is a first-class state.** An alarm whose condition clears while it
is still unacknowledged stays in the list, flagged `Normalizado, não reconhecido` — an alarm
nobody saw is exactly the one worth keeping. Only the acknowledgement, once the condition is
already normal, removes it.

**The audible is timed, never a permanent mute.** While any alarm is annunciating the
annunciator sounds; `Silenciar` mutes it for a fixed window and then it sounds again, and a
newly-raised alarm re-sounds immediately through an existing silence so a second fault cannot
be hidden by a silence taken for the first. The audio device is behind `IAlarmAnnunciator`,
so the whole policy is unit-tested against an injected clock without making a sound.

**Conditions are mapped to concrete, already-available signals:** link state leaving Connected;
`SensorCommOK` false; `FlowmeterOnline` false while flow control is enabled; no accepted frame
for more than three emission periods; the oxygen probe reading its sentinel past a grace period;
and — reusing the [D-015](DECISIONS.md) command lifecycle — an actuator whose command timed out
without transport acceptance.

*Rejected:* auto-clearing alarms (a transient fault would vanish unseen); a permanent mute
(the one silence nobody remembers to lift); coupling the audible policy to WPF so it could not
be tested; inventing a bespoke "sensor absent" signal when the probe sentinel already carries it.

*Scope:* this is the WP4 system-alarm engine. The **Alarmes** page and configurable per-variable
HH/H/L/LL limits with the persistent-foam process alarm are Phase 5 and build on this engine;
today the operator surface is the shell banner. The Phase 0 P2/P3 link cleanup remains open but
is hygiene, not part of this gate.

---

### D-017 · The cascade actuates through the arbiter, on the published kLa path, with a bumpless engage and safe abort
**Status:** Accepted and implemented · 2026-08-21 · see [PHASE_LOG P2-10](PHASE_LOG.md#p2-10--the-cascade-takes-the-wire-through-the-arbiter-on-the-published-path), [P2-11](PHASE_LOG.md#p2-11--the-cascade-becomes-observable-detail-tabs-and-a-live-tuning-chart)

Live oxygen cascade control (WP6) sits on the pieces below it: the validated velocity-form
controller ([D-013](DECISIONS.md)), the command arbiter ([D-015](DECISIONS.md)) and the alarm
gate ([D-016](DECISIONS.md)). It changes only the allocation and adds an ownership handshake.

**The allocator, not the controller, changes.** A `CascadeAllocation` maps the scalar control
effort to the two actuators. `KlaPathAllocation` turns effort into a kLa demand across the
published receipt's range and reads the monotonic allocation table for the (aeration, agitation)
that realises it along the paper's gradient/headroom path — replacing the linear window split
while the controller above is untouched. Two v.6 fallbacks (`Somente agitação`, `Somente
aeração`) drive one actuator and hold the other.

**Automatic is a real owner, engaged bumplessly.** `Ativar Automático` claims agitation, aeration
and the O₂ monitor through the arbiter, so a running cascade and the operator cannot both write —
the arbiter refuses whichever does not own the actuator. The loop is preloaded to the effort that
reproduces the actuator the operator left running (found by bisecting the allocation's inverse),
so the transfer has no setpoint jump. Each telemetry step dispatches the combined frame; because
the frame is re-sent every step, a dropped one is naturally retried on the next.

**Safe abort is layered.** Stale oxygen (three consecutive blind frames), a link loss (the
arbiter revokes ownership) or a manual takeover each disengage the cascade, return the actuators
to the operator and fall back to the advisory display — the loop never actuates on a value it
cannot trust or a wire it no longer owns.

*Rejected:* baking the kLa path into the controller (it must stay pure and testable without a
receipt); a single global mode that could not express the cascade owning O₂ while the operator
holds pH; engaging without a bumpless preload; auto-clearing a safe abort. A trajectory run
refuses to engage until a published map is selected — a fresh install ships none.

*Scope:* part 1 was the actuation engine, the ownership handshake, the safe abort and the
workspace controls; part 2 made the loop observable — the oxygen detail-pane `Cascata`/`PID`/
`Saída` tabs (read-only; tuning stays on Controle) and the live PV/SP/kLa/output tuning chart,
fed by a fixed-capacity `CascadeTrend` ring the service fills each armed step. Simulator
actuation is validated; field actuation on a real bioreactor remains the hardware gate, and no
result tuned only against the simulator closes the WP.

---

### D-018 · Dosing auxiliaries are three owned pumps plus an unowned foam sensor; intensity `× 10` is pH-only
**Status:** Accepted and implemented · 2026-08-21 · see [PHASE_LOG P2-12](PHASE_LOG.md#p2-12--the-cultivation-auxiliaries-three-owned-pumps-and-an-unowned-foam-sensor)

WP7 adds the remaining v.6 cultivation auxiliaries: nutrient dosing, antifoam dosing, the
level/foam sensor and the separate flask agitator. Each is modelled on the WP3 pH card — a
validated desired state, staged and reverted, that reaches the wire only through the arbiter.

**The roadmap's "all three" are the three that actuate.** Nutrient, antifoam and the flask
agitator become owned `ActuatorId`s, so each gets command ownership, lifecycle tracking and a
place in the global safe-stop. The **level/foam sensor** keys (`distanceSensorComm`,
`distanceSensorReference`, `foam*` timers) are deliberately **unowned** — the same treatment
calibration and the `pHCal` echo get. They configure a sensor and the module's automatic
response, not an actuator held against another owner. The direct consequence: `Parada segura`
stops the antifoam *pump* but never disables the sensor, so foam monitoring survives a stop. The
foam card therefore applies on its own rather than through the bulk apply.

**Intensity `× 10` is a pH quirk, not a dosing convention.** v.6 sends `pHIntensity` as
`percent × 10` (0-990). Nutrient and antifoam carry the **raw** operator percent (0-99);
PROTOCOL.md §3.3 states antifoam's range as 0-99 with no scaling, and treating every pump like pH
would have dosed at a tenth of the commanded speed. The builders encode the raw percent and a test
pins it.

**The agitator's sign never reaches the wire.** The operator picks a magnitude (0-100) and a
direction (Horário/Anti-horário); the ViewModel combines them into a signed percent that
`CommandBuilders.FlaskAgitator` splits into the wire's separate magnitude (`agitatorPercent`) and
direction (`agitatorDir`, 1 CW / 0 CCW) keys. A golden test asserts `-75` becomes magnitude `75`
with direction `0` and that the string `-75` is absent. The agitator is a **bench device** and has
no place on the reactor synoptic; it gets a Controle card only.

**Commanded-only stays commanded-only.** Nutrient and the agitator have no telemetry, so their
tiles show what was commanded (a duty cycle for nutrient), tagged `comandado`, and never a false
healthy state. Antifoam and level *do* have telemetry (`Antifoam`, `Distance`) and show it.

*Rejected:* folding the foam sensor into the antifoam actuator (it feeds the shared `Distance`
channel and the persistent-foam alarm, and owning the pump should not seize the sensor); a fourth
`Foam` actuator (the sensor does not actuate, so ownership would be ceremony); leaking the signed
agitator value onto the wire; putting the flask agitator on the reactor drawing. Bioreactor
actuation of the pumps rides the standing hardware gate — simulator sends are not biological
validation.

---

### D-019 · The conditional-OUR soft sensor inverts the O₂ balance under a causal quasi-steady gate
**Status:** Accepted and implemented (part 1) · 2026-08-21 · see [PHASE_LOG P2-13](PHASE_LOG.md#p2-13--the-conditional-our-soft-sensor-is-the-manuscripts-inversion-made-causal)

WP8 part 1 adds the manuscript's conditional oxygen-uptake-rate soft sensor
(`analysis/2_our_soft_sensor`) to live data. It is an observation, not a controller — it never
touches the wire.

**The physics is an inversion of the oxygen balance.** At quasi-steady state
`dC/dt = kLa·(C*−C) − OUR` gives `OUR = kLa·C*·(1 − DOT/100)`. The C# `OurSoftSensor.InferOur`
reproduces the manuscript's OUR column exactly across a 40-row cross-language oracle; the same
static method the parity test calls is the one the live loop uses, so they cannot drift.

**"Conditional" is a quasi-steady gate, and refusal has no value.** A reading is accepted only when
raw DOT is within the stability band of the setpoint **and** |dDOT/dt| is within the rate limit,
after DOT has first entered a tighter gate band (the run has left startup). Every other frame is
refused with a single named reason, and its conditional value is **null — never zero**. The
accepted-interval integral (∫OUR dt) never advances across a refused sample or a data gap, which is
what keeps the conditional total honest and separate from any notion of total consumption.

**Causal where the paper is offline.** The manuscript estimates dDOT/dt with a centred
Savitzky-Golay derivative that reads the future; a live sensor cannot. So the rate is a windowed
least-squares slope — the same `LeastSquaresRateEstimator` the cascade already trusts against the
probe's quantisation staircase ([D-013](DECISIONS.md)). The parity fixture therefore takes the
paper's smoothed DOT and rate as given inputs and pins only the two scientific claims — the OUR
inversion and the acceptance predicate; the causal rate itself is covered by unit tests and the
live run.

**kLa comes from the active published map, not the cascade.** The sensor reconstructs the selected
receipt's surface (`IKlaMappingEngine.Reconstruct`, from its anchors) and evaluates kLa at the live
airflow and the commanded agitation, independent of whether the cascade is engaged — the paper
computes OUR from recorded (Q,N,DOT), not from a controller. An operating point outside the mapped
domain yields no kLa (status `NoKla`) rather than an extrapolation the manuscript explicitly refuses.

*Rejected:* using the cascade's demanded kLa (would tie OUR to engagement and to a demand rather
than the operating point); zero-filling refused intervals or integrating across them; reproducing
the offline late-stage cutoff (a whole-run concept with no causal meaning). Bioreactor validation
of the OUR trace rides the standing hardware gate.

---

### D-020 · Gain scheduling is opt-in, scheduled by effort, with slew-bounded and journalled transitions
**Status:** Accepted and implemented · 2026-08-21 · see [PHASE_LOG P2-14](PHASE_LOG.md#p2-14--gain-scheduling-is-opt-in-effort-scheduled-and-slew-bounded)

WP8 part 2 adds gain scheduling to the oxygen cascade. It is a controller refinement, not a
requirement, and it never changes what the cascade sends beyond the gains it uses.

**Off by default, because the manuscript says a single set is enough.** The paper's tuning study
shows the loop gain scales as 1/kLa and the closed-loop sensitivity is nearly flat across a
fourteen-fold kLa change, which is what makes one fixed, robust gain set workable *without*
scheduling. So `GainScheduleSettings.Enabled` defaults to false — the cascade keeps its single base
tuning ([D-013](DECISIONS.md)) unless an operator turns scheduling on.

**Scheduled by the control effort, which maps onto kLa.** The effort is the loop's own output and
is always available, and along the published path it increases monotonically with kLa — so raising
the gains with effort is the causal way to hold the loop gain roughly constant. `GainSchedule` is a
piecewise-linear map of effort → (Kp, Ki, Kd), interpolated between breakpoints and held flat
outside them; the default breakpoints scale the gains up with effort and are provisional simulator
values, not a field tuning.

**Bounded transitions, bumpless substitution.** `GainScheduler` slew-limits the effective gains so
even a fast effort excursion cannot step-change the loop's responsiveness. The velocity-form
controller makes the substitution bumpless in the output — the gains multiply the increment, not
the absolute output — so `CascadeService.Retune` swaps gains each armed frame without touching the
rate window or the probe history.

**Versioned and journalled.** The schedule carries a version bumped on every applied edit, and each
segment crossing (the effort passing a breakpoint) is written to Eventos, so a change in the active
gains is auditable rather than invisible.

*Rejected:* baking the schedule into the controller (it must stay a pure, single-tuning law);
scheduling by raw kLa (unavailable without an active map, whereas effort always is); an unbounded
gain change on an effort excursion; making scheduling the default (the paper's single set is the
honest baseline). Field breakpoint values ride the standing bioreactor gate; the simulator's
provisional gains are not a field tuning.

---

### D-023 · The recipe engine owns the wire under `CommandOwner.Recipe`; the cascade block drives the ported controller in-process, and blocks are declared once
**Status:** Accepted and implemented · 2026-08-22 · see [PHASE_LOG P3-03](PHASE_LOG.md#p3-03--receitas-one-owner-declared-once-blocks-and-a-re-targeted-engine)

> Numbering note: D-021/D-022 arrived through the merged WP1/WP2 branch and are retained
> below D-023–D-025 to avoid rewriting either branch's accepted decision records.

WP4 adds Receitas — the graphical experimental-protocol editor. Its concept and engine slicing
come from ReceitasOpenTEC, but that app drives a **different machine** (a OpenTEC HMI over Modbus,
with setpoints written by screen-scraping over VNC), so what ports over is the node graph, the
validator and the control mathematics — never the transport.

**One command queue, one owner.** The engine drives the **same** `ICommandArbiter` as manual
control, under `CommandOwner.Recipe`. Starting a recipe **claims every actuator**, which is the
mechanism that must deactivate the manual surfaces: the arbiter then refuses any Manual dispatch, so a
running recipe and an operator cannot fight over the link. A link or feedback loss revokes ownership
and safe-aborts the run; stopping safe-stops the declared subsystems and returns the wire to Manual.
This is why the `Recipe` owner was reserved back in Phase 2 WP4 rather than retrofitted here.

> **Implementation audit — 2026-08-26:** dispatch exclusivity is implemented, but the manual UI does
> not yet visibly disable from Recipe ownership, and the shell/global Manual safe-stop can be refused
> while Recipe owns the wire. D-023 remains the target architecture; AUD-001/AUD-002 in
> [CURRENT_STATUS.md](CURRENT_STATUS.md) are required corrections before field release.

**The cascade block drives `CascadeController` in-process, not `CascadeService`.** `CascadeService`
claims the oxygen actuators as `Automatic`, which would fight the recipe's `Recipe` ownership and
break the one-owner rule. So the engine's `.Cascade` slice owns a `CascadeController` (the ported
science) directly, steps it on each valid-oxygen frame, and dispatches the combined frame under
`Recipe`. The loop pair (`Saída Loop`/`Entrada Loop`) fires the loop body each iteration; a finite
loop ends when O₂ settles at the setpoint, an infinite one when a loop-body `Intervenção Manual`
passes (*Pular Cascata*).

**Blocks are declared once and generated.** ReceitasOpenTEC hand-wrote a model + viewmodel + view
triple per node type (~10 near-duplicates). `RecipeNodeCatalog` declares each of the nineteen blocks
once — category, ports, parameter schema — and the library rows, the property editor and the
per-node defaults are all projected from it. Recipe JSON is versioned from v1 with a migration hook,
and canonical type/connector names are written while legacy spellings are only read.

**Re-targeted to the ESP32-S3.** Dropped entirely: the VNC layer, the Modbus register map, the
×10/×100 `ScaleType` factors, `IntervaloAtuacaoVnc`, and the pipetting-robot panel — the hardware
here has no HMI to scrape, so blocks send JSON straight to the ESP32 over the existing protocol.
Setpoints carry engineering units; loops map to the subsystem's own enable (`flowmeterComm`) or a
zero setpoint; the pump blocks map to the ESP32 dosing keys. The **O₂-enrichment (gas-mixer) path
is deferred** ([Explicitly deferred](ROADMAP.md#explicitly-deferred)) — an O₂ setpoint writes
`oxygenMonitor` only, and the gas mixer ships disabled.

*Rejected:* a second command source that bypasses the arbiter (defeats the one-owner guarantee);
reusing `CascadeService` for the recipe cascade (its `Automatic` ownership fights `Recipe`); a
hand-written triple per block (the near-duplication the roadmap calls out); porting the VNC/Modbus
transport or the ×10/×100 scaling (a different machine's artefacts); shipping the enrichment path
(needs the manuscript's changes, and is deferred). Pump-block field mapping and the vvm→L/min
aeration coupling ride the standing bioreactor gate.

---

### D-024 · Unified Oxygen Control (Controle ↔ Receitas sync, single activation, and removal of gas-mixer/consultiva)
**Status:** Accepted and implemented · 2026-08-23 · see [UI_DESIGN.md §5.2](UI_DESIGN.md#52-controle) and [§5.3.7](UI_DESIGN.md#537-the-cascade-block)

**Four distinct allocation modes.** Oxygen control is unified across the application under four mutually exclusive modes:
`Agitação` (`AgitationOnly`), `Aeração` (`AerationOnly`), `Cascata` (`DualCascade`), and `Mapa` (`KlaPath`).
The recipe block parameter is standardized on enum `modo` rather than disjoint boolean flags.

**Single activation point (Toggle "Ativo" = Engate).** Turning on the "Ativo" toggle on the Oxygen row in `Controle`
or clicking the action button in `Controle de oxigênio` is the single point of activation that engages `CascadeService`
under `CommandOwner.Automatic`. Overridden actuators (Agitation / Aeration depending on mode) are locked in the `ON` state and
disabled from manual edits, displaying provenance badges. Safe Stop disengages the cascade cleanly.

**Bidirectional synchronization.** When an active recipe tab has a `Controle Cascata O2` block, the `Controle de oxigênio`
workspace reads from and applies directly to the recipe node, while simultaneously reconfiguring the running controller.
When no recipe is active, it seamlessly falls back to editing global `AppSettings.Cascade`.

**Removal of Gas Mixer and "Consultiva".** The unsupported gas mixer / O₂ enrichment loop and the legacy "consultiva/advisory"
surface framing are removed from both UI and backend recipes, leaving a clean, responsive control interface.

---

### D-025 · Dissolved Oxygen UI Simplification (Modal Pop-up ⚙, Process Table Reorganization, and Dual-Loop PID Controller Architecture)
**Status:** Accepted and implemented · 2026-08-23 · see [UI_DESIGN.md §5.2](UI_DESIGN.md#52-controle)

**Process table reorganization in `Controle`.**
1. The "Modo" selector is positioned as the **last column** in the Process Parameters table.
2. Individual per-row "Reverter" buttons are removed, keeping the per-row "Aplicar" and the global "Reverter tudo" in the footer.
3. On the Oxygen parameter row, a gear button (**⚙**) directly opens the modal configuration dialog (`OxygenConfigDialog`).

**Modal configuration pop-up (`OxygenConfigDialog`).**
The secondary tab "Controle de oxigênio" inside the Control view is removed in favor of a clean, dedicated pop-up window:
- **Independent 4-Mode PID gains:** Each mode (`Agitação`, `Aeração`, `Cascata`, `Mapa`) maintains and persists its own independent PID tuning parameters ($K_{DOT}$, $K_P$, $K_I$, $K_D$, $T_{pred}$, $\tau_D$, $I_{min}$, $I_{max}$, $M_{WINDOW}$, $J_{AVG}$, $N_{PRED}$).
- **Gain Scheduling:** Confined exclusively to `Cascata` mode (sequential agitation + aeration).
- **No embedded charts:** The pop-up is focused entirely on configuration. Live cascade control signals (`CascadeEffort`, `CascadePredictedO2`, `CascadeRateSetpoint`, `CascadeRateMeasured`, `CascadeKlaDemand`) are streamed directly to the main `Gráficos` (Charts) tab for telemetry visualization and CSV export.

**Dual-Loop Cascade Controller Architecture (`CascadeTwoLoopPidController.cs`).**
Replaced single-loop PID with the industrial standard ported from `BlocosDeControle`:
1. **Outer loop:** Least-squares linear regression slope estimation and dead-time compensation ($DOT_{pred} = DOT + \text{rate}_{pred} \cdot T_{pred}$) generating rate setpoint $r_{SP} = K_{DOT} \cdot (SP - DOT_{pred})$.
2. **Inner loop:** Velocity-form PID on the rate error ($e = r_{SP} - \text{rate}$), with derivative low-pass filtering ($\tau_D$) and sliding-window integrator anti-windup ($M_{WINDOW}$).
3. **Actuator Gain Scheduling:** Smooth gain factor transition ($g$) across agitation and aeration windows.
---

### D-021 · Biomass is an owned actuator but stays out of the safe-stop; no HD-mode is shown
**Status:** Accepted and implemented · 2026-08-22 · see [PHASE_LOG P3-1](PHASE_LOG.md#p3-1--biomass-sensor-and-guided-procedure)

Phase 3 WP1 adds the biomass optical sensor: enable, the momentary blank/start/stop, the integration
thresholds and the live Abs/Raw/IT/PWM readouts, plus a guided Calibrações procedure.

**The wire contract came from the firmware, not v.6's Python.** v.6's biomass block issues
`biomassComm`, `blank`, `start`, `stop` and `{low,high,opt}`, and `OpenTEC_ESP32_v7.ino` forwards
exactly those (plus a `test_period` v.6 never sends). `start`/`stop` were absent from the earlier
protocol notes; they are on the wire, so they are now `CommandKeys.BiomassStart`/`BiomassStop` and
golden-string pinned. **The firmware exposes no HD-mode state** — the WP asked us to confirm this
before displaying one, and the answer is no, so none is shown.

**Owned for arbitration, excluded from the safe-stop for safety.** Biomass is a *measurement*, but
its blank/threshold commands are things a future recipe could issue concurrently with the operator,
so `ActuatorId.Biomass` is an owned arbiter actuator — a recipe claiming it blocks a manual blank,
and vice versa. It is nonetheless left out of the operator safe-stop: stopping an optical read is not
a safety action and blinds a measurement, the same reasoning that keeps the level/foam sensor running
through a stop ([D-018](DECISIONS.md)).

**The enable is immediate; thresholds are staged.** The enable toggle sends `biomassComm` at once, as
v.6's checkbox does and because the firmware gates the sub-commands on it. The three thresholds are
staged and applied together (`{low,high,opt}`), matching the app's dosing-card discipline. The
Calibrações procedure is a thin guided wrapper — enable → capture blank → confirm Abs ≈ 0 → thresholds
— with a link-loss refusal, since biomass has no PC-side coefficient to fit.

*Rejected:* leaving biomass unowned like pure config (a recipe and the operator could then both drive
the blank with no arbitration); including it in the safe-stop (blinds monitoring); inventing an
HD-mode toggle the firmware does not have.

---

### D-022 · The external pump keeps v.6's exact profile frames; proportional gas is arbitrated as flow
**Status:** Accepted and implemented · 2026-08-22 · see [PHASE_LOG P3-2](PHASE_LOG.md#p3-2--external-pump-profiles-preview-and-proportional-gas)

Phase 3 WP2 adds the external pump: the five firmware profile modes with a live preview, the safe
disabled frame, versioned persistence and the optional proportional-gas coupling.

**Byte-parity restored two keys the docs had dropped.** Every v.6 pump-mode frame carries `init_t`
and `final_t` (the operating window in minutes), and the firmware forwards them; earlier protocol
revisions omitted them. They are now on every profile frame, and `t' = t − init_t` with flow zero
before the start. The disable frame reproduces v.6's `speed:0` even though the firmware forwards
`pump_speed` and ignores `speed` — parity over tidiness.

**Proportional gas is aeration, not a pump command.** `Q_g = (V₀ + PumpVol/1000)·vvm` computes an air
flow, so it is dispatched as a standard aeration frame through the command arbiter, owned as
`Aeration` — which means it is refused when the cascade owns aeration, exactly as the single-owner
model requires. It resends only on a material change to avoid flooding the bus. v.6's proportional
path opened the nitrogen valve at zero flow (`valve_2:1`), which contradicts the safe-stop rule and
looks like an oversight; OpenTEC-Hub closes both valves on the frame and records the deviation here.

**Payload-size validation is the firmware's array bounds.** The `.ino` holds `p0..p20` and
`t0..t99`/`q0..q99`, so the app validates 1–21 coefficients and 2–100 segments (with `t0 = 0` and
strictly increasing times) rather than guessing a byte cap for the still-open payload question
([PROTOCOL Q2](PROTOCOL.md#5-open-questions-for-hardware-verification)).

**One shared operating window, not v.6's per-mode windows.** v.6 stored `t_initial`/`t_final`
separately under each mode; OpenTEC-Hub shares one window across modes, because the window is *when the
profile runs*, independent of its shape, and per-mode windows are a data-entry trap. `PumpControlSettings`
is versioned (each applied send bumps it) and keeps every mode's parameters so switching mode loses
nothing.

*Rejected:* omitting `init_t`/`final_t` (breaks byte-parity); reproducing the nitrogen-open-at-zero
quirk (unsafe); a background sender that resends identical flow frames every cycle; per-mode operating
windows. Pump actuation and the proportional flow ride the standing bioreactor gate.

---

## A recipe holds for an external device; it never advances on faith

**Context.** `CanStart` checks the *link*, and the arbiter checks *ownership*. Neither knows
whether the peripheral on the far end exists. A `Múltiplos Pontos de Ajuste` block asking for
25 L/min dispatched the frame, logged the intent, marked itself completed and let the recipe
finish — with the flowmeter offline the whole time and no gas ever flowing. The telemetry that
would have said so (`FlowmeterOnline`, the `FlowSetpoint` echo) was already on the wire and was
read by nobody outside the alarm engine.

**Decision.** A block that commands an external device **holds until that device confirms**,
using the same evidence the arbiter confirms aeration with. The hold is indefinite by design:
aborting a cultivation because a sensor went quiet is worse than waiting for it. After a
grace of four telemetry periods the engine publishes `IRecipeEngine.Waiting`, which logs a
warning and latches the `Receita aguardando dispositivo` alarm, and the operator resolves it —
`SkipWait()` moves on without confirmation, `StopAsync` ends the run. A zero setpoint is exempt:
a stop must never wait on the device that is failing.

**Scope.** The flowmeter (air flow) is wired now; the distance sensor, external pump, biomass
sensor and flask agitator join the same `AwaitDeviceAsync` mechanism as their actuation lands.

*Rejected:* refusing to start when a device is absent (the operator often knows it is coming up);
aborting the run on a silent device (loses the cultivation over a peripheral); re-sending the
frame on a timer (changes actuation behaviour to fix an observability problem); and leaving the
condition to the existing `Fluxômetro offline` alarm, which needs the firmware to report
`FlowControlEnabled` and says nothing about the recipe that is stuck behind it.

---

### D-026 · Every external device gets the flowmeter's contract: presence, routing and acceptance kept apart

**Decision.** The five external Wi-Fi nodes are modelled the way the flowmeter already was, with
three states that are never collapsed into one: **requested** (the operator's switch),
**routed** (`*CommEnabled`, the Hub's own persisted forwarding flag) and **present**
(`*Online`, the Hub's staleness window). `ExternalDeviceStatus` is that vocabulary, shared by
biomass, the external pump, the flask agitator and the level/foam sensor;
`FlowControlViewModel` keeps its own property names so its bindings and tests are untouched.

**Why.** Only the flowmeter told the truth about the device on the other end. Every other row
bound its state dot to `IsEnabled` — the operator's own checkbox — so the UI reported the
operator's intent back to them and called it hardware state. Three concrete consequences:
stock Hub v8 has no staleness window for the pump at all and republishes a dead node's last
sample forever; the biomass node pushes only while measuring, so "stopped" and "gone" were
indistinguishable and the parser held a stale absorbance indefinitely; and the Hub persists its
routing flags in NVS while the app persists the switches on the PC, so after a Hub reboot every
biomass or pump sub-command could be dropped by `if (cmdFound && commOn)` without a word.

**Consequence.** When a device reports absent, the app **invalidates** its readings rather than
holding them. Absence of evidence is handled separately: a device the Hub has never mentioned
reads *aguardando telemetria*, never *desconectado*, and is **not blocked** — blocking on
`HasTelemetry` would deadlock the biomass sensor, which reports nothing until it is started and
cannot be started while it reports nothing. Against a Hub without the presence keys the app
falls back to ageing the value keys locally, which keeps it honest unflashed.

**Rejected.** Deriving presence from `HubStations` (a radio-association count that says nothing
about which node is which); treating a missing key as `false` (it is the same signal an old Hub
emits, so it would report every device as failed); and blocking every card until its first frame
(the biomass deadlock above).

---

### D-027 · A stop and the routing switch that follows it are two ordered frames

**Decision.** Turning an external device off is **two frames, in order**: the stop while the Hub
is still routing, then the routing flag on its own. `{"stop":1}` → `{"biomassComm":0}` for
biomass; `{"mode":0,"speed":0}` → `{"pumpComm":0}` for the pump. The outgoing buffer merges by
design, so the second frame goes through a new ordered-frame path
(`ConnectionManager.SendCommandAfterCurrentFrame` → `IDeviceService.SendAfterCurrentFrame` →
`ICommandArbiter.DispatchSeparateFrame`) rather than a plain `Send`.

**Why.** `processJsonCommand` parses the routing flag before it reaches the device's command
block, and that block is gated on the flag it just wrote. v.6's single
`{"pumpComm":0,"mode":0,"speed":0}` therefore clears routing and then discards its own `mode:0`:
**the pump keeps dosing and only its telemetry goes quiet**, which is the worst failure mode a
feed pump has. The same holds for biomass. Once the first frame is in the Hub's mailbox it
survives the second — neither `/pumpCommand` nor `/biomassCommand` has a routing gate — so
ordering is the only requirement, not a delay.

**Consequence.** `CommandBuilders.PumpDisable` is replaced by `PumpStopProfile` and
`PumpRoutingDisabled`, and the `pump disable` golden string in
[PROTOCOL.md](PROTOCOL.md#4-golden-strings) is replaced by two. This is a deliberate departure
from v.6 byte-parity, taken because the v.6 frame is demonstrably wrong against this firmware
rather than merely redundant. The operator safe-stop keeps its single merged frame for
everything else and appends only `{"pumpComm":0}` after it, so the destructive-confirmation
preview still shows what actually goes out.

**Rejected.** Waiting for an acknowledgement between the two frames (nothing acknowledges, and
the mailbox makes it unnecessary); redefining "disable" as profile-stop-only and leaving routing
to a separate control (hides a real switch from the operator); and patching the Hub to reorder
its own parse (a firmware change to work around a two-line app change).

---

### D-028 · The agitator safe-stop locks the bench potentiometer out; an ordinary stop does not

**Decision.** `CommandBuilders.FlaskAgitatorSafeStop` sends `agitatorReEnablePot:0` alongside
`agitatorOn:0`. `FlaskAgitatorOff` — the card's ordinary **Desligar** — omits the key and leaves
the Hub's persisted preference alone, with a warning on the card when the node reports the knob
live.

**Why.** The Hub turns `agitatorOn:0` into `{"RPM_percent":0,"ActivePot":agitatorReEnablePot}`,
and the node's loop re-reads the potentiometer on its very next pass whenever `ActivePot` is 1 —
the persisted default. A safe stop with the bench knob at 60 % restarts the motor at 60 %. A
stop a knob can undo is not a stop.

**Consequence.** The lockout persists on the Hub until the operator deliberately re-arms it with
the existing **Reativar potenciômetro** action. That is the intended posture after an emergency
stop. The ordinary off keeps the documented meaning of the operator's own switch, because
handing the motor back to the knob is what that switch is for; the card says so rather than
silently overriding it. The app never tracks the flag locally — the Hub owns it and the momentary
re-enable is the only thing that sets it back — so there is no local preference to desynchronise.

**Rejected.** Forcing the lockout on every stop (removes a working bench workflow); leaving the
safe stop alone and only warning (a safety action that depends on the operator having read a
warning is not a safety action); and tracking the flag in `AppSettings` (a second copy of state
the Hub already persists, with nothing to reconcile it against).

---

### D-029 · Manual sends report acceptance; UI state commits only after it

**Decision.** `IManualDispatcher` wraps the arbiter's Manual dispatch and returns
`CommandDispatchResult`. The biomass, pump, agitator and foam cards commit — persist settings,
clear `HasPendingChange`, bump a profile version, advance the proportional-gas last-sent value —
**only when the dispatch was accepted**, and otherwise keep the staged input and name the owner
that refused it.

**Why.** `IDeviceService.Send` is `void`, and in the composition root it is a Manual dispatch
through `CommandArbiter`, which atomically refuses a whole frame when a recipe or the cascade
owns any actuator it touches. The `void` signature discards that. A card could persist a
calibration the sensor never received and report success (AUD-003), and
`PumpControlViewModel.MaybeSendProportionalGas` recorded a refused target as sent, suppressing
every retry until the calculated flow moved by the resend threshold (AUD-004).

**Consequence.** The view-models take the dispatcher as an optional constructor argument that
defaults to wrapping whatever `IDeviceService` they were given, so existing tests and the
simulator harness keep working. `DispatchRefusal.Describe` supplies the pt-BR wording. This
closes AUD-003 and AUD-004 for these four cards; the remaining manual surfaces and AUD-001's
owner-aware safe stop are unchanged and still open.

---

### D-030 · Os três dispositivos externos ganham blocos de receita que esperam confirmação

**Decisão.** `Bomba Externa` (o antigo `Controle da Bomba`, que era um marcador), `Sensor de
Biomassa` e `Agitador de Frasco` são blocos de receita numa categoria nova, **Dispositivos
Externos**, separada de **Bombas**. Cada um despacha e então **segura** até o dispositivo
confirmar, pelo mesmo `AwaitDeviceAsync` que o fluxômetro já usava.

**Por quê.** O bloco da bomba externa registrava a intenção e não enviava nada — foi escrito
enquanto a atuação da WP2 ainda não existia. Agora existe, e uma receita que alimenta um cultivo
precisa poder acionar a bomba. A categoria separada não é cosmética: estes três são ESP32s
independentes no SoftAP do Hub e podem sumir sozinhos, enquanto as bombas de dosagem ficam dentro
do módulo e não podem — um bloco que pode segurar esperando um dispositivo pertence ao lado dos
outros que também podem.

**Consequências.**

- **O silêncio prossegue.** Todo predicado é satisfeito quando o Hub não disse nada sobre o
  dispositivo. Contra um Hub anterior às chaves de presença, segurar seria segurar por evidência
  que aquele firmware não produz: os blocos travariam todos e o operador teria de pular um a um.
- **A parada do agitador numa receita bloqueia o potenciômetro** (`agitatorReEnablePot:0`), ao
  contrário do **Desligar** manual, que respeita a preferência do operador. Uma parada de receita
  precisa ser determinística: com o potenciômetro ativo o nó relê o botão no ciclo seguinte, a
  parada não para, e o bloco ficaria preso esperando um zero que nunca chega.
- **As duas sequências de desligamento são dois quadros ordenados**, como no caminho manual, por
  meio de `DispatchRecipeSeparateFrame`. O Hub descarta o sub-comando de um dispositivo assim que
  o flag de roteamento é limpo, então parar e desativar no mesmo quadro não para nada.
- **O branco da biomassa não é confirmável** e o bloco não segura: o nó varre por ~15 s e não
  publica nada que distinga uma referência nova da anterior. O bloco diz isso no log e sugere um
  temporizador.
- **Iniciar a aquisição espera uma absorbância**, não o flag de online. Com os dois relógios do
  Hub, "online" só diz que o nó responde; uma amostra chegando é o que prova que o laço de
  aquisição está rodando.
- O resumo do bloco no canvas passa a respeitar `VisibleWhen`. Listar os campos de todos os cinco
  perfis leria como se todos estivessem em vigor.

**Rejeitado.** Um bloco por ação (multiplicaria a paleta por seis sem ganho); segurar sempre até
`*Online` ser verdadeiro (trava contra Hub antigo, e para a biomassa é circular — ela só publica
depois do `start` que ficaria bloqueado); e manter a preferência do potenciômetro na parada de
receita (uma parada que um botão desfaz não é uma parada).

---

### D-031 · Trocar o workspace copia os dados e reinicia o aplicativo

**Decisão.** "Alterar Pasta..." nas Configurações copia todo o workspace atual para a pasta
escolhida, grava o novo caminho em `workspace.txt` e **reinicia** o OpenTEC-Hub com
`--workspace <nova> --no-workspace-prompt`. A cópia nunca apaga a origem e nunca sobrescreve
nada que já exista no destino.

**Por quê.** `AppPaths.InitializeWorkspace` troca um estático, mas metade do aplicativo já leu
o caminho no construtor: `SettingsService._path`, `RecipeStore`, `KlaProfileStore`,
`KlaTestStore`, `BackupService` e o sink de arquivo do Serilog são resolvidos uma única vez na
raiz de composição. Trocar só o estático deixava o aplicativo escrevendo metade dos dados na
pasta que o operador acabara de abandonar — sessões e histórico na pasta nova, receitas, mapas,
testes de kLa, `settings.json` e log na antiga — sem nenhum aviso. Reiniciar move tudo de uma
vez, e os argumentos de inicialização necessários já existiam.

**Consequências.**

- **Copiar, nunca mover.** O workspace guarda a única cópia de corridas concluídas; uma
  movimentação interrompida na metade perde dado que não se reproduz. Uma migração que falha
  custa espaço em disco e nada mais.
- **O destino tem precedência.** Arquivo que já existe na pasta nova é contado como preservado,
  não sobrescrito — apontar duas máquinas para a mesma pasta compartilhada é uso normal.
- **`Logging.SessionLogPath` é reescrito na cópia**, e só nela. É um caminho absoluto: intacto,
  o aplicativo abriria na pasta nova e seguiria gravando telemetria no arquivo da antiga.
- **A troca é recusada com receita ou ensaio de kLa em execução.** O reinício abandonaria a
  corrida. Com o equipamento apenas conectado ela é permitida, com aviso: a aquisição para por
  alguns segundos e os setpoints seguem ativos no Hub.
- **O processo atual não muda de pasta.** O caminho novo é persistido, não aplicado; se o
  reinício falhar, o aplicativo continua íntegro na pasta em que abriu e diz para reabrir.

**Rejeitado.** Reapontar os serviços a quente — exigiria um `IWorkspaceService` observável,
propriedades calculadas no lugar dos campos capturados, um `Rebase` no `SettingsService`,
reconfiguração do Serilog em runtime e recarga das listas em memória das telas, tudo enquanto
telemetria chega, para poupar um reinício de dois segundos.

---

### D-032 · A página de Potência não exporta PNG; o dado sai em CSV

**Decisão.** As páginas de potência não oferecem exportação de imagem. `PowerNavigationContractTests`
proíbe a própria palavra "PNG" na `PowerView` e no seu code-behind, e o teste falha se ela voltar.
O que sai do aplicativo é dado: `resumo-resultados.csv` por ensaio, o CSV unificado de comparação de
impelidores e a folha de dimensionamento (CSV, ou PDF pela impressora do sistema).

**Por quê.** Um PNG de um gráfico é uma captura do que o app desenhou naquele instante — sem as
âncoras, sem o `IC₉₅`, sem a proveniência do ponto, sem dizer se o `Np` era absoluto ou relativo.
Publicado num artigo, ele não é reprodutível nem auditável: ninguém consegue refazer o ajuste a
partir dele. O CSV carrega os números que geraram a figura, e a figura de publicação é montada na
ferramenta de análise do autor a partir desses números. Um botão "Exportar PNG" ao lado do "Exportar
CSV" convida justamente ao caminho que não se quer.

**Consequências.**

- **O gráfico na tela é instrumento de leitura, não entregável.** Ele existe para o operador decidir
  durante o ensaio; a figura final não nasce aqui.
- **A captura de tela do sistema continua disponível** para quem quiser uma imagem informal. O que o
  app não faz é oferecer isso como se fosse um formato de exportação de resultado.
- **Se um dia for necessário**, a decisão a rever é esta, e o teste de contrato é o lugar onde a
  reversão precisa ser declarada — não um botão acrescentado em silêncio.

**Registrado porque** uma auditoria listou "a exportação PNG não foi restaurada" como pendência.
Não é pendência: é esta decisão.

---

### D-033 · O shell constrói só a página que abre; as demais vão para a fila ociosa

**Decisão.** Cada destino do shell é hospedado por um `DeferredPageHost`, que guarda a página como
`DataTemplate` e a materializa (a) na hora, se for a página selecionada, ou (b) em
`DispatcherPriority.ApplicationIdle`, depois que a janela já está de pé. Navegar para uma página
ainda não construída a materializa na hora.

**Por quê.** O shell declarava as onze páginas como irmãs e alternava `Visibility`. Elemento
`Collapsed` continua sendo construído, medido e recebe `Loaded` — medido: `Loaded` disparou em
`PowerView` e `PowerMapView` com `IsVisible=false` abrindo no painel. Ou seja, toda inicialização
pagava por todas as páginas, inclusive a leitura de workspace em disco e os timers de redesenho
delas. O primeiro frame levava 2375–2502 ms **independentemente** da página de destino, que o app
já conhecia antes de renderizar.

**Consequências.**

- **Primeiro frame de 968–1280 ms** nas onze rotas, dentro do orçamento de 2000 ms que nunca havia
  sido cumprido, e agora variando por página — sinal de que só se constrói o necessário.
- **Só as views são adiadas.** Todo ViewModel de página é singleton recebido no construtor do
  `ShellViewModel`, então assinatura de telemetria e estado acumulado não dependem de a view existir.
  Adiar a view de uma página não faz o app perder dado dela.
- **Um id de navegação sem host renderiza página em branco** e nada mais acusaria isso — o build
  compila e o smoke abre só um destino. `Every_shell_destination_is_hosted_by_a_deferred_page`
  compara os ids do `ShellViewModel` com os hosts do `MainWindow.xaml`.

---

### D-034 · Coordenador de segurança unificado e despacho privilegiado no CommandArbiter (AUD-001)

**Decisão.** A ação global de parada segura no painel (`ControlViewModel.SafeStopCommand`) é coordenada
pelo serviço central `ISafetyCoordinator` (`SafetyCoordinator`), em vez de disparar quadros manuais
diretos via `IDeviceService.Send`. Quando acionada:
1. O coordenador comanda a parada da receita em execução (`IRecipeEngine.StopAsync`), o desengajamento
   da cascata (`ICascadeService.Disengage`) e o aborto dos ensaios de automação (`IKlaTestRunner`, `IPowerTestRunner`).
2. Verifica explicitamente o estado da conexão física com o hardware (`_device.State == ConnectionState.Connected`),
   retornando falha explícita se o link estiver desconectado e impedindo confirmação falsa de desligamento.
3. O `CommandArbiter` expõe métodos dedicados de despacho de segurança (`DispatchSafety` e `DispatchSeparateSafetyFrame`),
   que atuam como rota prioritária de emergência: transmitem o frame de segurança com zeros e fechamento de válvulas
   ao hardware e forçam o retorno incondicional de todos os atuadores para `CommandOwner.Manual`, disparando
   `OwnershipRevoked(isSafeAbort: true)` para notificar consumidores de automação.
4. A UI do `ControlViewModel` somente reporta sucesso após a confirmação de aceite pelo coordenador/árbitro.
   Qualquer recusa ou falha de transporte é exibida com motivo claro ao operador.

**Por quê.** Na arquitetura anterior, `ControlViewModel.SafeStop` chamava `_device.Send(...)`. No composition
root, `_device` é o `CommandArbiter`, cujo método `Send` trata todo comando como `CommandOwner.Manual`.
Durante a execução de uma receita, os atuadores pertencem a `CommandOwner.Recipe`. O árbitro rejeitava o
quadro de parada por conflito atômico de posse, mas como `IDeviceService.Send` retorna `void`, a rejeição
era descartada silenciosamente. A UI então zerava as propriedades locais e exibia falsamente que todos os
atuadores haviam sido desligados, criando um risco crítico de segurança física (AUD-001).

**Consequências.**
- **Garantia de atuação de emergência.** O botão de parada segura tem precedência funcional e física sobre qualquer
  automação em execução (receita, cascata ou ensaios kLa/potência).
- **Sem falsos positivos de desligamento.** Uma desconexão do cabo ou falha de transporte é notificada na hora ao
  operador, deixando claro que os atuadores podem ainda estar energizados no reator.
- **Testabilidade determinística.** A coordenação de parada segura passa a ser coberta por testes automatizados em
  `SafetyCoordinatorTests` e `ControlViewModelTests`, garantindo que futuras modificações não reintroduzam conflitos
  de posse silenciosos.

---

### D-035 · Bloqueio visual dinâmico de controles manuais por posse e desacoplamento do botão de parada segura (AUD-002)

**Decisão.** O travamento dos controles manuais na interface do `ControlView` passa a refletir dinamicamente a posse individual de cada atuador registrada no `ICommandArbiter`:
1. Cada linha de processo (Temperatura, Agitação, Oxigênio, Vazão de Ar, Pressão) e cada cartão periférico (Bomba Externa, Sensor de Biomassa, Agitador de Frascos, Dosagem de pH, Nutriente, Antiespumante) monitora os eventos `OwnershipChanged` e `OwnershipRevoked` do árbitro através do `ControlViewModel.UpdateOwnershipFromArbiter()`.
2. As propriedades de bloqueio e proveniência são expostas individualmente:
   - `CurrentOwner`: enum do proprietário (`Manual`, `Recipe`, `Automatic`, `KlaAssay`, `PowerAssay`);
   - `IsOwnedByOther`: booleano (`CurrentOwner != Manual`);
   - `HasOwnerBadge`: ativa a exibição do crachá visual `ctl:ProvenanceBadge` (para o oxigênio, suprimido quando em `Automatic` pois a linha atua como seletor da cascata);
   - `OwnerBadgeText`: texto em minúsculas padronizado (`receita`, `controle o₂`, `ensaio kla`, `ensaio pot`);
   - `OwnerLockReason`: justificativa contextual em pt-BR utilizada como `ToolTip` de bloqueio e feedback em caso de tentativa de envio.
3. No XAML (`ControlView.xaml`), o bloqueio genérico de página inteira (`<Grid Grid.Row="1" IsEnabled="{Binding IsManualOperationEnabled}">`) é **eliminado**, garantindo que a barra de status e a ação de parada segura ("Parada segura") permaneçam permanentemente ativas e acessíveis ao operador, mesmo com receita em execução.
4. Cada elemento interativo de entrada (sliders, caixas de texto numéricas, toggles de estado, botões de aplicação local) tem seu `IsEnabled` vinculado a `IsOwnedByOther` via `conv:InverseBoolConverter`, tornando os controles inertes e exibindo o crachá de proveniência ao lado do cabeçalho.
5. Defesa em profundidade nos comandos de disparo: os métodos `Apply`, `ApplyProfile`, `ApplyThresholds`, `SendMomentary` e `ApplyAll` abortam a operação se o atuador estiver sob posse de outro processo (`IsOwnedByOther`), informando o motivo no `StatusText` e evitando disparos espúrios pela interface.
6. A linha de oxigênio permite o desengajamento da cascata pelo toggle ativo quando o atuador estiver em `Automatic` ou `Manual`.

**Por quê.** O apontamento AUD-002 identificou que os controles manuais não ficavam inertes sob a posse da receita e não exibiam crachás de proveniência de comando. Além disso, a implementação preliminar com bloqueio de página inteira via `IsManualOperationEnabled` desabilitava o contêiner onde ficavam a barra de status e o botão de emergência, violando a regra de segurança física de que a parada de emergência deve ser incondicionalmente acessível a qualquer momento.

**Consequências.**
- **Clareza operacional instantânea.** O operador visualiza exatamente qual processo ou automação tem autoridade sobre cada atuador e por que os controles manuais correspondentes estão desabilitados.
- **Segurança irrestrita.** O botão global de parada segura nunca é bloqueado por contêineres pais ou estado de receita, permanecendo sempre funcional.
- **Zero conflitos na camada física.** Impossibilidade de acionamentos manuais acidentais concorrendo com o motor de receitas ou rotinas de ensaio.

### D-036 · Adoção de IManualDispatcher e Observabilidade de Aceitação de Comandos Manuais (AUD-003)

**Decisão.** Todas as superfícies de comando e atuação manual do aplicativo migram da chamada legado `IDeviceService.Send` (que retorna `void`) para a abstração observável `IManualDispatcher` (`CommandDispatchResult Dispatch(OpenTECCommand)`):
1. A interface `IManualDispatcher` e a implementação `ManualDispatcher` expõem `Ownership` (snapshot atual dos proprietários dos atuadores quando o serviço for o `CommandArbiter`).
2. O utilitário `DispatchRefusal.Describe` formata mensagens detalhadas em pt-BR identificando o atuador e o processo conflitante (`"Comando recusado: {atuador} sob controle de {proprietário}."`), com sobrecargas recebendo diretamente `IManualDispatcher` ou `IDeviceService`.
3. Todos os ViewModels de atuação (`SubsystemViewModel`, `ControlViewModel`, `PHControlViewModel`, `NutrientControlViewModel`, `AntifoamControlViewModel`, `FlowCalibrationViewModel`, `BiomassCalibrationViewModel`) recebem `IManualDispatcher? dispatcher = null` no construtor (com fallback automático retrocompatível para testes legados).
4. Em qualquer tentativa de aplicação manual (`Apply`, `ApplyAll`, `ApplyFlowState`, etc.):
   - Se `result.Accepted == false`: o estado comitado **não é atualizado**, o valor digitado permanece nos campos de edição, o marcador de pendência (`HasPendingChange`) **permanece ativo** e a mensagem de recusa do árbitro é exibida no `StatusText`;
   - Se `result.Accepted == true`: o estado é comitado (`CommitPendingCommand()`), o marcador de pendência é limpo e o status de envio bem-sucedido é informado ao operador.
5. No `ControlViewModel.ApplyAll`, os disparos combinados em lote são avaliados diretamente pelo árbitro através do `_dispatcher.Dispatch`, abortando sem comitar nenhuma linha em caso de conflito e exibindo a recusa específica.
6. A propagação de status dos subsistemas individuais é encaminhada para o `ControlViewModel.StatusText` quando disparos pontuais ocorrem nas linhas de processo.

**Por quê.** A assinatura `void` do método legado `IDeviceService.Send` descartava silenciosamente a recusa emitida pelo `CommandArbiter` caso houvesse conflito atômico de posse (por exemplo, durante receita ativa, controle de oxigênio ou ensaios kLa/potência). As ViewModels comitavam os valores e anunciavam falso sucesso ao operador enquanto o hardware continuava inalterado, violando o princípio de honestidade da interface (AUD-003).

**Consequências.**
- **Transparência e Integridade de Estado:** A interface do operador nunca exibe valores confirmados ou limpa marcas de alteração pendente quando o comando foi rejeitado pelo árbitro.
- **Diagnóstico Claro:** O operador é informado exatamente sobre qual parâmetro foi recusado e qual controlador detém o atuador (Receita, Controle O₂, Ensaio de kLa, Ensaio de Potência).
- **Retrocompatibilidade e Robustez:** Testes unitários com dublês simples (`RecordingDeviceService`) continuam funcionando normalmente graças ao fallback interno no `ManualDispatcher`.

### D-037 · Retentativa reativa automática do acoplamento de gás proporcional sob liberação de posse (AUD-004)

**Decisão.** O `PumpControlViewModel` passa a monitorar ativamente os eventos de posse do `ICommandArbiter` (`OwnershipChanged` e `OwnershipRevoked`):
1. O construtor recebe `ICommandArbiter? arbiter = null` (com fallback retrocompatível `_arbiter = arbiter ?? (device as ICommandArbiter)`).
2. Quando `ActuatorId.Aeration` é reivindicado por outro controlador (`transfer.To != CommandOwner.Manual`), o ViewModel invalida o último setpoint gravado (`_lastGasFlowSentLpm = null`) e sinaliza a pendência de retentativa (`_gasRetryPending = true; _aerationOverridden = true;`).
3. Quando a aeração retorna para `CommandOwner.Manual` (`transfer.To == CommandOwner.Manual`), o ViewModel intercepta a transição e, se o acoplamento estiver ativo (`GasProportionalEnabled && IsEnabled`), dispara imediatamente um despacho forçado (`MaybeSendProportionalGas(force: true)`).
4. O parâmetro `force: true` ignora a verificação de banda morta (`Math.Abs(qg - last) < GasFlowResendThresholdLpm`), assegurando que a vazão proporcional correta seja restabelecida no hardware sem depender de variação no volume ou de novas telemetrias.
5. O avanço de `_lastGasFlowSentLpm = qg` ocorre **exclusivamente** após aceite pelo árbitro (`result.Accepted == true`).
6. Se o despacho for recusado pelo árbitro, `_lastGasFlowSentLpm` permanece nulo, a pendência permanece ativa e a mensagem descritiva de recusa com atuador e proprietário é exibida no `StatusText`.
7. O atuador `ActuatorId.ExternalPump` também é rastreado para manter `CurrentOwner` sincronizado diretamente pelos eventos do árbitro.

**Por quê.** O apontamento AUD-004 diagnosticou que quando a cascata de oxigênio ou receitas em execução assumiam a aeração, o quadro de gás proporcional era recusado atomicamente pelo árbitro, mas a bomba não monitorava a liberação da posse. Caso o volume não variasse acima de 0,01 L/min ou não chegassem novas telemetrias, o acoplamento permanecia suprimido indefinidamente pela banda morta, deixando o biorreator na vazão residual deixada pelo controlador anterior.

**Consequências.**
- **Restauração garantida do bioprocesso:** Ao término de uma cascata de oxigênio ou receita, o acoplamento de gás proporcional reassume o controle da aeração de forma determinística e imediata.
- **Zero supressão indevida:** A banda morta filtra ruído em regime permanente normal, mas nunca bloqueia a reconexão pós-desengajamento de automação prioritária.
- **Integridade de testes:** A suíte passa a contar com testes de integração cobrindo recusa por posse externa, target inalterado e retentativa imediata com sucesso após liberação da posse.

---

### D-038 · Retenção deliberada de envio de setpoints via LostKeyboardFocus e Enter (AUD-005: Waived por Decisão de UX)

**Status:** Accepted · 2026-09-05

**Decisão.** Os campos de entrada de setpoint numérico (caixas de texto de processo e cartões de periféricos, incluindo os limiares de biomassa em `ControlView.xaml.cs`) retêm intencionalmente o comportamento de envio imediato do comando ao pressionar a tecla `Enter` ou ao perder o foco do teclado (`LostKeyboardFocus` direcionado a `ApplyFor`). A proposta de exigir exclusivamente clique no botão "Enviar" (item de auditoria AUD-005) foi rejeitada e o item formalmente arquivado como *waived / by design*.

**Por quê.** Na rotina operacional do laboratório de bioprocessos, os operadores frequentemente digitam os valores de processo (temperatura, agitação, vazão, limiares de biomassa, etc.) e avançam imediatamente para a checagem física da bancada ou clicam em outros pontos da interface. Exigir um clique adicional obrigatório em um botão específico de envio quebraria o fluxo de uso habitual e aumentaria a sobrecarga de interação durante as operações de campo. A perda de foco após a digitação expressa a conclusão da intenção do operador, desde que os valores passem pela validação de formato e limites de segurança antes do despacho físico.

**Consequências.**
- **Experiência do Operador Fluida:** O operador pode confirmar qualquer valor digitado pressionando `Enter` ou simplesmente mudando o foco para outro campo/área da tela.
- **Segurança Mantida:** O envio acionado por perda de foco continua submetido à validação de limites físicos do protocolo e ao árbitro de comandos (`IManualDispatcher`), impedindo despachos inválidos ou sob posse externa de outro processo (conforme D-035 e D-036).
- **Fechamento do AUD-005:** Decisão de produto documentada para evitar reaberturas futuras da mesma discussão de UX.

---

### D-039 · Higiene de formatação com dotnet format, regras de nomenclatura no .editorconfig e barreira de CI (AUD-008)

**Status:** Accepted · 2026-09-05

**Decisão.** A dívida técnica de formatação, codificação de caracteres e advertências de analisadores estáticos da solução foi sanada de forma completa e padronizada (AUD-008):
1. **Execução de Formatação Automatizada:** Aplicado `dotnet format` em toda a solução, padronizando quebras de linha, identação e estilo de código C#.
2. **Harmonização do `.editorconfig` com `CONVENTIONS.md`:**
   - Adicionada regra explícita `dotnet_naming_rule.constants_are_pascal` (com `applicable_kinds = field` e `required_modifiers = const`) para que constantes privadas sejam formatadas em `PascalCase` (ex.: `BootSettleMilliseconds`, `MaxFlow`, `ErrorWindowFrames`), em conformidade com as diretrizes da Microsoft e da documentação do projeto.
   - Adicionada regra explícita `dotnet_naming_rule.static_fields_are_pascal` (com `applicable_kinds = field` e `required_modifiers = static, readonly`) para que campos estáticos somente-leitura sejam formatados em `PascalCase` (ex.: `DefaultWindowSize`, `Anchors`, `Phase1Actuators`).
   - Mantida a regra `dotnet_naming_rule.private_fields_underscore` (`_camelCase`) para os demais campos privados de instância e campos estáticos mutáveis (`_customDataDirectory`).
3. **Supressão de Avisos em Mocks de Teste:** Configurado `<NoWarn>$(NoWarn);CS0067</NoWarn>` em `OpenTECHub.Tests.csproj` para silenciar advertências de eventos de interface não invocados em stubs de teste.
4. **Barreira de Verificação Integrada:** O comando `dotnet format OpenTECHub.slnx --verify-no-changes --no-restore` passa a retornar código 0 com zero divergências e zero advertências, estabelecendo um gate rigoroso e automatizável para prevenção de regressões.

**Por quê.** A auditoria apontou que a solução acumulava pequenas divergências de charset (arquivos com e sem BOM UTF-8), espaçamentos e estilos, impedindo a adoção de uma barreira estrita de CI baseada em `dotnet format --verify-no-changes`. Além disso, a ausência de regras específicas para constantes no `.editorconfig` gerava falsos positivos `IDE1006` que o mecanismo automático de correção não conseguia solucionar em lote.

**Consequências.**
- **Zero Dívida de Estilo:** Código 100% formatado de maneira uniforme em todos os projetos (`OpenTECHub.Protocol`, `OpenTECHub.Simulator`, `OpenTECHub`, `OpenTECHub.Tests`).
- **Build e Análise Estática Limpos:** Zero advertências de compilador (CS) e zero advertências de IDE/analisador de nomenclatura na solução.
- **Portão de Qualidade Confiável:** Qualquer colaborador ou pipeline de CI pode executar `dotnet format --verify-no-changes --no-restore` e `dotnet test --nologo` como verificação determinística e reprodutível.

---

### D-040 · Captura automatizada de telas via RenderTargetBitmap em thread STA isolada e resolução do erro COM 0x80004002

**Status:** Accepted · 2026-09-05

**Decisão.** A estratégia de captura automatizada de telas da aplicação foi migrada de clientes externos de automação do Windows (`UIAutomationClient.dll` / GDI screen scraping) para renderização determinística em memória via `RenderTargetBitmap` operando sobre um dispatcher STA isolado (`WpfRenderingHost`):
1. **Host STA Dedicado (`WpfRenderingHost`):** Criado um harness de teste que inicializa uma thread STA com message pump próprio (`Dispatcher.Run()`), configurando explicitamente `Application.ShutdownMode = ShutdownMode.OnExplicitShutdown` e um provedor de DI em memória (`RecordingDeviceService`) seguro e desacoplado de hardware físico.
2. **Renderização em Memória (`RenderTargetBitmap`):** As views e controles são medidos, organizados e renderizados diretamente em superfícies de bitmap (`PixelFormats.Pbgra32`) de forma desacoplada do Desktop Window Manager (DWM) ou do subsistema COM de acessibilidade.
3. **Eliminação de Loops de Recursão em `RadioButton` (`UpdateRadioButtonGroup`):**
   - Removido o atributo `GroupName` em pares de `RadioButton` com binding bidirecional para booleanos mutuamente exclusivos nas telas `ControlView.xaml` (`AgitatorDir`), `PowerView.xaml` (`PowerChartTab`), `CalibrationView.xaml` e `MainWindow.xaml`.
   - Adicionados guards de validação de alteração de valor nos setters das ViewModels (`FlaskAgitatorViewModel`, `ConnectionViewModel`, `OxygenCalibrationViewModel`, `PHCalibrationViewModel`).
4. **Resolução de Conflitos de Thread e Dispatching:**
   - O teste `ComboBoxSelectionBoxTests` foi integrado ao `WpfRenderingHost.Run()` para executar na mesma thread STA do `Application.Current`, prevenindo erros de cross-thread ownership.
   - Os despachos de telemetria e estado de runner em `PowerTestViewModel.RunOnUi` e `KlaDeterminationViewModel.OnRunnerStateChanged` foram padronizados para utilizar `dispatcher.Invoke` síncrono quando fora da UI thread, garantindo transições determinísticas durante a execução paralela de testes.
5. **Reativação de `ThemeServiceTests`:** O teste de ciclo de temas claro/escuro (`ThemeServiceTests`), anteriormente suprimido com `[Fact(Skip = ...)]`, foi reativado e passa com 100% de sucesso.
6. **Suíte e Artefatos Visuais:** 23 testes em `ScreenshotCaptureTests` executam em ~11s e geram 30 artefatos PNG sob `docs/evidence/screenshots/{100dpi,125dpi,150dpi}/` cobrindo todas as telas principais em temas Claro e Escuro, com validação algorítmica contra truncamento de texto por reticências e entropia mínima de pixels.

**Por quê.** O erro Windows `0x80004002: E_NOINTERFACE` era causado pela tentativa da biblioteca de UI Automation do Windows de consultar a interface `IRawElementProviderSimple` através de chamadas COM inter-processos. Em ambientes não interativos, sem desktop ativo ou em esteiras de CI, a composição de tela do Windows para janelas DirectX/WPF é desativada, inviabilizando a agregação COM e gerando imagens em preto em rotinas de print de tela via GDI. A renderização direta via `RenderTargetBitmap` opera no pipeline de rasterização de software do WPF em nível de memória bitmap, independente de composição de desktop ou provedores COM do sistema operacional.

**Consequências.**
- **Independência de Ambiente:** A geração de screenshots funciona perfeitamente em modo interativo, headless ou background CI sem lançar exceções COM `0x80004002`.
- **Suíte de Testes 100% Verde:** A suíte de testes da solução alcança 1116 testes aprovados, com 0 falhas e 0 testes ignorados.
- **Auditoria Visual Confiável:** Evidências em alta definição em 100%, 125% e 150% de escala DPI estão salvas e prontas para revisão de espaçamentos, layout sinótico e responsividade.

---

### D-041 · Empacotamento, distribuição com Inno Setup, Crash Reporting estruturado e Manual do Operador (Fase 6)

**Status:** Accepted · 2026-09-05

**Decisão.** A infraestrutura de entrega em campo, suporte ao operador e diagnóstico de falhas críticas foi concluída conforme a arquitetura da Fase 6:
1. **Instalador Inno Setup (`installer/OpenTECHub_Setup.iss`):** Criado o script de empacotamento baseado no modelo de referência de `BlocosDeControle.iss`. Lê dinamicamente a versão oficial do produto via `GetStringFileInfo(MyAppDll, PRODUCT_VERSION)` a partir do binário compilado (`Directory.Build.props` como fonte única), gera pacote *self-contained* para `win-x64` (.NET 10 embutido sem dependência de runtimes externos na máquina do laboratório), configura atalhos de desktop e menu iniciar, registro limpo de desinstalação e assistente em português brasileiro.
2. **Automação de Build (`installer/build_installer.ps1`):** Script PowerShell que realiza a publicação self-contained do projeto e invoca o compilador `ISCC.exe` para produzir o instalador executável sob `installer/Output/`.
3. **Serviço de Diagnóstico e Pânico (`CrashReporter`):**
   - Intercepta os três canais de exceções não tratadas do .NET: `DispatcherUnhandledException` (thread de UI), `AppDomain.CurrentDomain.UnhandledException` (threads secundárias/pool) e `TaskScheduler.UnobservedTaskException` (tarefas assíncronas abandonadas).
   - Coleta métricas de processo (WorkingSet, PrivateBytes, GC Heap, threads ativas, tempo de atividade), informações completas da máquina e sistema operacional, além de decodificação recursiva de todas as inner exceptions e aggregate exceptions com seus HResults e dados adicionais (`Exception.Data`).
   - Implementa gravação de dupla contingência (`{Workspace}/Logs/Crash/` como destino primário e `%LOCALAPPDATA%\OpenTEC-Hub\CrashDumps\` como destino de contingência para falhas precoces ou de disco) e diálogo informativo amigável ao operador.
4. **Manual do Operador (`docs/MANUAL_DO_OPERADOR.md`):** Documento técnico abrangente em português cobrindo a topologia de hardware, instalação e drivers, gerenciamento de workspaces, controle manual, árbitro de comandos, Parada Segura Global, calibrações analíticas e ensaios de $k_L a$ e potência.

**Por quê.** A implantação de software em ambiente de laboratório fabril/acadêmico exige processos de instalação sem atrito (sem necessidade de instalar manualmente SDKs ou runtimes do .NET), documentação clara e operacional para os técnicos e garantia de que qualquer exceção imprevista gere um relatório detalhado de pânico em disco para permitir diagnóstico e suporte rápido da engenharia.

**Consequências.**
- **Fechamento de 100% do Eixo 1:** Todas as 10 etapas do Eixo 1 (Software Desktop) estão plenamente concluídas e verificadas.
- **Distribuição Autônoma:** Geração reprodutível de instaladores para bancadas de bioprocessos.
- **Rastreabilidade de Falhas:** Dumps de pânico estruturados evitam perda de contexto em erros não tratados.

---

### D-042 · Exclusão mútua bidirecional estrita entre Gás Proporcional e Controle de Oxigênio Dissolvido

**Status:** Accepted · 2026-09-05

**Decisão.** Estabelecida a exclusão mútua bidirecional em nível de domínio de bioprocesso e de interface entre o acoplamento de Gás Proporcional ($vvm$ constante em batelada alimentada) e qualquer malha de controle de oxigênio dissolvido ($DO\%$ via cascata agitação/aeração, aeração pura, agitação pura sob malha de O₂ ou mapa de $k_L a$):
1. **Controle de Oxigênio Bloqueado por Gás Proporcional:** A interface `ICascadeService` ganha a propriedade `Func<bool>? ProportionalGasActivePredicate { get; set; }`. Em `CascadeService.CanEngage(out string? reason)`, se o predicado retornar `true` (isto é, a bomba peristáltica está ativa com acoplamento habilitado, `IsGasProportionalActive == true`), a ativação da malha de oxigênio é rejeitada atomicamente com a mensagem: *"O acoplamento de gás proporcional ao volume dosado está ativo na Bomba Externa. Desative-o para iniciar o controle de oxigênio (cascata/mapa)."* No `ControlViewModel`, a tentativa do operador reverte imediatamente o switch visual da linha de oxigênio.
2. **Gás Proporcional Bloqueado por Controle de Oxigênio:** No `PumpControlViewModel`, ao tentar habilitar o toggle `GasProportionalEnabled` ou ligar a bomba (`IsEnabled = true`) com gás proporcional enquanto `_cascade.IsEngaged == true`, a ação é imediatamente revertida para desligado com notificação de status (*"Gás proporcional indisponível: controle de oxigênio (cascata/mapa) em execução."*).
3. **Guarda de Despacho:** O método `MaybeSendProportionalGas` aborta imediatamente qualquer emissão de setpoint caso `_cascade is { IsEngaged: true }`.
4. **Ciclo de Vida Limpo:** Ao descartar o `PumpControlViewModel`, o predicado registrado em `_cascade` é desregistrado se pertencer à instância.

**Por quê.**
- **Fundamentação Biológica:** Em cultivos alimentados (*fed-batch*), a adição de volume líquido altera a relação estequiométrica/hidrodinâmica de aeração. O acoplamento volumétrico de gás proporcional impõe uma taxa fixa de volumes de ar por volume de caldo por minuto ($vvm$), operando em **malha aberta volumétrica** estrita ($Q_g(t) = \min((V_0 + V_{\text{bomba}}/1000) \cdot \text{vvm}, Q_{\text{max}})$).
- **Incompatibilidade com Malha Fechada de $DO\%$:** O controle de oxigênio dissolvido (cascata clássica ou mapa de $k_L a$) opera em **malha fechada com realimentação de sensor** ($DO\%$), variando a vazão de gás de forma oportunista para manter a saturação de oxigênio em um valor alvo. Ambas as estratégias atuam sobre o mesmo recurso físico (`ActuatorId.Aeration`). Permitir a concorrência causaria conflito direto e desvio da premissa estequiométrica de $vvm$ fixo.

**Consequências.**
- **Zero Conflito de Controle:** Impossibilidade arquitetural de concorrência entre o controle de $DO\%$ e o $vvm$ constante.
- **Feedback Transparente:** O operador compreende exatamente por que um modo bloqueia o outro em qualquer uma das direções de ativação.
- **Suíte de Testes Validada:** Cobertura de testes unitários e de integração adicionada em `CascadeServiceTests.cs` e `ExternalDeviceTests.cs` (1124 testes aprovados, 0 falhas).

---

### D-043 · Higiene de portas seriais (A-6), priorização WMI com CH343 (A-7) e medição fidedigna de RTT (A-8)

**Status:** Accepted and implemented · 2026-09-05 · see [HARDWARE_VALIDATION.md §Block A](hardware/HARDWARE_VALIDATION.md)

**Decisão.** Implementadas todas as salvaguardas de software e telemetria fidedigna de enlace serial e Wi-Fi da Etapa 4.1:
1. **Tratamento Seguro de Portas Ocupadas (A-6):**
   - Criação da exceção de domínio `PortBusyException`.
   - Detecção defensiva via `SerialTransport.IsPortBusyException`: identifica `UnauthorizedAccessException` e `IOException` com HResults Win32 `0x80070005` (`ERROR_ACCESS_DENIED`) e `0x80070020` (`ERROR_SHARING_VIOLATION`), comuns quando o v.6 legado ou um terminal serial está com o descritor aberto.
   - Em vez de travar ou tentar sequestrar o descritor com falhas opacas, a porta é pulada com segurança durante a varredura automática (`ProbePortsAsync`) e, em conexão explícita, reporta mensagem amigável em português ao operador (*"Porta COMx está ocupada por outra aplicação (ex.: v.6 ou outro software serial)."*).
2. **Priorização Inteligente WMI e Ordenação Natural (A-7):**
   - A consulta WMI no Windows agora inspeciona propriedades estendidas (`PNPDeviceID`, `Manufacturer`, `Description`, `DeviceID`).
   - Categorização em três níveis de prioridade (*Tiers*):
     - **Tier 1 (Alta prioridade):** Somente o adaptador oficial: `VID_1A86&PID_55D4` ou `CH343`. O par VID+PID é casado inteiro, e não `VID_1A86` isolado — a WCH também fabrica o CH340 (`VID_1A86&PID_7523`), que não é o conversor que acompanha o ESP32-S3 da TECNAL.
     - **Tier 2 (Média prioridade):** Demais conversores USB-UART conhecidos, incluindo o restante da família WCH (`VID_1A86` genérico, `wch`, `CH340`, `CH910`) e `CP210`, `FTDI`, `Silicon Labs`, `ESP32`, `USB Serial`.
     - **Tier 3 (Baixa prioridade):** Portas COM seriais genéricas ou integradas.
   - Ordenação natural estrita por número de porta (`COM3` precede `COM10`), evitando o ordenamento lexicográfico defeituoso.
3. **Medição Fidedigna de Tempo de Resposta / RTT (A-8):**
   - Remoção do falso "0 ms" em USB decorrente do tempo de escrita no buffer do kernel do sistema operacional.
   - Propriedade `LastRoundTripMs` adicionada a `LinkDiagnostics`.
   - Em Wi-Fi (`HttpTransport`): a requisição HTTP POST é síncrona com confirmação da controladora, sendo seu tempo o RTT genuíno (`LastRoundTripMs = LastWriteMs`).
   - Em USB (`SerialTransport`):
     - Correlaciona confirmações explícitas de comandos de fluxo (`snapshot.FlowCommandAck >= expectedId`) com o timestamp de despacho.
     - Correlaciona linhas de confirmação de texto (`OK\r\n` -> `ParseOutcome.CommandAck`) retornadas pelo firmware com o timestamp de envio de comandos.
     - Enquanto nenhuma confirmação real for processada, exibe travessão `"—"`, prevenindo diagnósticos enganosos de latência nula.
   - Exibição de `LatencyText` adicionada ao pop-up de diagnóstico de conexão na barra de título (`MainWindow.xaml`).

**Por quê.**
- O operador de laboratório frequentemente alterna ou esquece o software legado v.6 aberto em segundo plano. Sem a higiene adequada, a tentativa de abrir a porta falhava com erro de acesso genérico e assustador ou reiniciava o fluxo de forma instável.
- Em computadores modernos com múltiplos adaptadores USB e portas seriais virtuais Bluetooth, a varredura sequencial ou sem prioridade perde tempo tentando portas irrelevantes.
- Exibir "0 ms" como latência em USB é tecnicamente incorreto e enganoso para a equipe de controle e validação de bancada.

**Consequências.**
- Resiliência operacional máxima mesmo com aplicações concorrentes abertas na máquina.
- Conexão e auto-descoberta ultrarrápidas priorizando o hardware TECNAL oficial.
- Observabilidade real da saúde e latência da comunicação com o ESP32-S3.
- 1133 testes automatizados aprovados na suíte, com 0 falhas e conformidade total de código.

**Emenda · auditoria da Etapa 4.1 (2026-09-05).** A revisão da implementação encontrou quatro pontos em que o comportamento entregue não correspondia à decisão acima; todos corrigidos:

1. **Causa de falha obsoleta (A-6).** `_lastError` sobrevivia de uma tentativa para a outra. Uma porta liberada entre as tentativas continuava sendo anunciada como *"ocupada por outra aplicação"* quando a falha real era de handshake, mandando o operador caçar um processo que já havia soltado a porta. A causa passou a ter escopo de tentativa (`HandleConnectAsync`) e é reescrita a cada volta do ciclo de reconexão.
2. **RTT de Wi-Fi sobrescrito (A-8).** O timestamp de correlação era armado também em Wi-Fi. Como o POST já é síncrono e o RTT já estava medido, qualquer linha `OK` subsequente substituía a medida verdadeira por uma correlação fabricada. A correlação agora só é armada em USB.
3. **Confirmação atrasada publicada como latência (A-8).** Uma confirmação perdida deixava o timestamp armado indefinidamente, e a próxima confirmação — possivelmente minutos depois — virava a "latência do enlace". Introduzido `ConnectionOptions.RoundTripCorrelationWindow` (5 s): fora da janela a correlação é descartada e o pop-up mantém `"—"`.
4. **Publicação não atômica.** `LastRoundTripMs` era um `double?` escrito no laço de requisições e lido de outra thread, sujeito a leitura rasgada. Passou a ser publicado como um único `long` (ticks) via `Interlocked`.

5. **Consulta WMI bloqueando a thread de UI no caminho do primeiro quadro.** A priorização de A-7 foi entregue como uma enumeração única e síncrona: `ConnectionViewModel.RefreshPorts` era um `[RelayCommand]` chamado no **construtor** e disparava `ManagementObjectSearcher` sobre `Win32_PnPEntity` — medido em **~1090 ms a frio e 256–364 ms a quente**, sem nenhum dispositivo COM conectado. Com o *first-frame* orçado em `< 2 s` e medido entre 968 e 1280 ms (AUD-006), a consulta a frio quase dobrava a partida e congelava o pop-up a cada "atualizar portas".
   A enumeração foi separada em duas: `ListPortNames()` lê apenas o mapa `SERIALCOMM` (milissegundos, segura na thread de UI) e `ListCandidatePortsAsync()` faz o ranqueamento com o WMI fora da thread chamadora. O construtor publica a lista barata na hora e sobrepõe o ranqueamento quando ele chega — o conjunto de portas é o mesmo nas duas, só a ordem melhora, então nada é exibido errado no intervalo. `ProbePortsAsync` mantém a versão síncrona, pois já roda fora da thread de UI.

Removido ainda o helper morto `SerialTransport.IsPortBusy(string)`: responder "esta porta está ocupada?" exige abrir a porta — exatamente o sequestro que A-6 existe para evitar — e a resposta já nasce obsoleta. O estado ocupado é descoberto na tentativa real de abertura, que levanta `PortBusyException`.

Acrescentado `ConnectionPopoverContractTests`, que prende os nomes de comando ligados em `MainWindow.xaml` ao que a *view model* expõe. Uma ligação `Command` que não resolve falha em silêncio no WPF — sem exceção, sem log, o botão apenas não responde — e foi exatamente o risco criado ao converter `RefreshPorts` em `RefreshPortsAsync`.

---

### D-044 · Biblioteca de taras por eixo (perfis nomeados)

**Status:** Accepted and implemented · 2026-09-05

**Decisão.** A curva de tara deixa de existir apenas dentro do ensaio e passa a ter também uma biblioteca nomeada, com um arquivo por eixo em `Testes-Potencia/Taras/<nome>.json`:

- `TareCurve.ProfileName` registra sob qual eixo a curva foi arquivada.
- `IPowerTestStore` ganha `ListTareProfiles`, `LoadTareProfile`, `SaveTareProfile` e `DeleteTareProfile`.
- O ensaio continua gravando a sua própria cópia em `tara.json`. A biblioteca é a origem reaproveitável; o `tara.json` é o registro imutável do que aquele ensaio de fato usou.
- A pasta `Taras/` é reservada: não aparece em `ListTests` e não pode ser tomada como nome de ensaio.
- Na aba de Validação, o assistente de tara ganhou seleção de perfil, **Aplicar**, **Salvar perfil** e **Excluir**. Uma varredura iniciada com o campo de nome preenchido é arquivada automaticamente no perfil ao terminar.

**Por quê.** A bancada opera dois biorreatores com eixos distintos (`eixo_furo_unico` e `eixo_furo_duplo`). O atrito de selo e mancal muda entre eles, então há duas taras simultaneamente válidas. Com uma única tara por ensaio, trocar de eixo obrigava a repetir a varredura no ar a cada ensaio, ou — pior — a aceitar silenciosamente a `P_vazio` do eixo errado, que entra subtraída na potência de eixo e contamina o Np.

**Consequências.**
- Trocar de eixo passa a ser escolher um perfil, sem repetir a varredura no ar.
- A verificação de compatibilidade não foi afrouxada: `TareStatus` continua confrontando `ImpellerSetHash` e `CalibrationHash`, e um perfil aplicado a um conjunto diferente é rotulado como *"Tara de outro conjunto"* na hora da troca.
- Excluir um perfil não afeta ensaios que já o aplicaram — cada um guarda a sua cópia.

---

### D-045 · Contrato responsivo para notebooks: 1024 × 640 DIP, drawer sobreposto e adaptação por breakpoint em XAML

**Status:** Accepted and implemented · 2026-09-10

**Decisão.** A janela passa a ter um contrato de tamanho explícito, e as páginas adaptam o layout por breakpoint medido no container, não no monitor:

- Menor janela suportada: **1024 × 640 DIP**. `MainWindow` declara esse mínimo e `WindowChromeMaximizeFix` passa a preencher `MinTrackSize` em pixels físicos ao responder `WM_GETMINMAXINFO` — com `handled = true` o `DefWindowProc` não roda, então o mínimo declarado no WPF sozinho não segurava o arraste.
- Abaixo de **1440 DIP** de largura de janela a navegação vira uma barra de ícones de 56 DIP, e o menu completo abre como **drawer sobreposto**, sobre o conteúdo, sem empurrar a página. `Esc`, clique fora e a troca de destino fecham o drawer; o editor de nome de sessão continua acessível dentro dele.
- A adaptação por página é feita por `Controls.Responsive`, uma propriedade anexada que publica `IsNarrow`/`IsShort` a partir do tamanho real do container. Os limiares ficam no XAML da página, junto do layout que governam, e não em code-behinds independentes.
- Onde três colunas ou três gráficos não cabem, o conteúdo passa a um seletor de seção — Mapeamento kLa (Dados / Superfície / Diagnóstico), gráficos de kLa (Oxigênio / Regressão / Diagnóstico) e gráficos de Potência (Torque e rotação / Np × Re). Nada é removido: a seção não exibida mantém estado e volta assim que a janela cresce.
- Os templates dos botões de legenda (minimizar, maximizar, fechar) passam a existir uma única vez, em `Themes/Controls.xaml`.
- Modais são limitados à área do proprietário por `DialogBounds` antes de `ShowDialog`, com margem de 16 DIP por borda.

**Por quê.** Os 36 prints de 09/09 mostram a aplicação cortando cabeçalho, métricas e gráficos em janela estreita, e a página de Potência perdendo colunas inteiras. As páginas somavam larguras fixas — 938 DIP só na tabela de Controle, 1000 DIP no corpo do Mapeamento kLa, 620 DIP de altura obrigatória na pilha de gráficos de kLa — que um notebook de 1366 × 768 não tem para dar. Reduzir a fonte encolheria a interface inteira sem resolver a distribuição; redistribuir resolve.

**Consequências.**
- `CompactLayoutTests` arranja cada destino na área que a menor janela deixa (936 × 534 DIP) e falha se algo visível ficar além da borda direita ou se um texto sem elipse for truncado. Rolagem horizontal **local** de tabela continua permitida; rolagem horizontal de página, não.
- Em WPF um valor local vence um `Setter` de `Trigger`. Larguras e colunas que a adaptação precisa mudar ficam no `Style`, nunca como atributo — o teste de largura das seções compactas existe exatamente para pegar esse erro.
- `1280 × 720 a 125%` e `1920 × 1080 a 175%` ficam abaixo do mínimo de altura e permanecem fora do suporte declarado.
- O contrato é um alvo de projeto verificado por medida de layout e por execução real do aplicativo; ele ainda não substitui a matriz de validação visual por escala e tema descrita no plano.

### D-046 · Todo dado bruto medido é gravado: colunas de kLa, tara ao vivo e ponto único

**Status:** Accepted and implemented · 2026-09-10

**Decisão.** Quatro medidas que o aplicativo tomava e não registrava passam a chegar ao disco:

- **kLa, esquema 2.** `dados-brutos.csv` e `serie-global.csv` ganham `TemperatureC` e `RpmMeasured`, **anexadas ao fim** da linha. O leitor consome as duas colunas apenas quando a linha as traz, então um ensaio do esquema 1 continua abrindo e sendo reanalisado sem conversão.
- **Tara.** Cada amostra aceita pelo controlador é anexada a `Taras-Brutas/tara-<início>.csv` **enquanto a varredura corre**, um arquivo por varredura. `tara.json` continua sendo escrito só quando a curva converge, e passa a apontar o arquivo bruto.
- **Ponto único.** As leituras vão para `Pontos-Unicos/ponto-<início>_N####_<gás>.csv`, com manifesto irmão em JSON (`N` e `Q_g` comandadas, `T_nom`, ensaio, término). Sem ensaio aberto, o arquivo vai para `Testes-Potencia/Pontos-Unicos/`, pasta agora reservada como a lixeira e a biblioteca de taras.
- **Leitura ausente é célula vazia**, nunca zero, nas quatro — a mesma convenção que o sidecar do servo já usava.

**Por quê.** As três primeiras eram perda de dado irrecuperável, não falta de comodidade:

- A temperatura define `C*` e é a referência da correção de kLa para 20 °C; ela chegava em toda telemetria e não era registrada em nenhum arquivo do ensaio. A rotação **medida** é a única evidência de que a agitação sustentou a condição relatada — uma corrida de kLa comanda `N` e nunca a verifica, de modo que o próprio arquivo do ensaio não permitia checar o que o vaso rodou. Recuperar as duas depois exigia cruzar o log de sessão por timestamp, quando ele estava ligado.
- A varredura de tara acumulava tudo em memória e só escrevia ao final: cancelamento, patamar que não converge ou limite de torque que dispara descartavam a varredura inteira. É justamente o caso em que o operador mais quer olhar o que aconteceu.
- O ponto único gira o mesmo eixo com o mesmo instrumento de uma corrida — o que ele mede é dado. Ficava em `LivePoints`, cortado em 6000 pontos e descartado ao fechar o painel.

**Consequências.**

- **Colunas novas vão para o fim, sempre.** Inseri-las junto das leituras a que pertencem deslocaria índices que scripts de análise já usam. O custo é um arquivo menos legível a olho nu; o benefício é que nenhum leitor existente quebra e nenhuma conversão de acervo é necessária.
- `KlaTestDocument.SchemaVersion` passa a 2. Só o **escritor** muda com a versão: ambos os leitores aceitam os dois formatos, e é isso que mantém um ensaio antigo reanalisável.
- Um arquivo por varredura de tara e por conferência significa que **nada é sobrescrito** — inclusive duas tentativas iniciadas dentro do mesmo segundo, que recebem sufixo próprio. A pasta cresce por acúmulo, o que é a troca desejada num acervo científico.
- O arquivo bruto da tara é aberto **antes** de reivindicar a agitação: um armazenamento inacessível falha com nada a desfazer, em vez de parar um eixo já girando. No ponto único a escolha é a oposta e deliberada — a conferência é operação de bancada, então uma falha de gravação avisa e a captura segue sem registro, sem impedir o eixo de girar.
- `Pontos-Unicos/` entra na mesma regra de `Taras/` e da lixeira: não aparece em `ListTests` e não pode ser tomada como nome de ensaio.
- Cobertura em `RawDataIntegrityTests` (12 casos): as colunas e o vazio-em-vez-de-zero, o round-trip do armazenamento, a leitura de um arquivo do esquema 1, a corrida que grava temperatura e rotação medida (e a bancada sem servo, que grava só a temperatura), o round-trip da tara bruta, duas varreduras que não se sobrescrevem, a **varredura cancelada que mantém suas leituras**, e o ponto único gravado com e sem ensaio aberto.

### D-047 · A explicação sai do card e vira documentação no aplicativo

**Status:** Accepted and implemented · 2026-09-10

**Decisão.** O aplicativo passa a ter um manual próprio, e as páginas param de imprimir a própria explicação:

- **Nova seção `Documentação`** em Configurações, logo abaixo de *Comandos do equipamento*, com ícone próprio (`IconBookGeometry`, livro aberto — distinto de `IconFile`, que quer dizer arquivo).
- **O conteúdo é dado, não XAML.** `DocumentationCatalog` declara tópicos, seções e blocos (parágrafo, item, **campo** e observação); a view só sabe desenhar os quatro. Documentar uma página é escrever registros.
- **Cada página é escrita do mesmo jeito:** primeiro *como a página é organizada*, depois o que cada controle faz — nomeado exatamente como a tela o nomeia, no bloco `Field` (rótulo + o que faz).
- **Painel e Controle primeiro**, nessa ordem, como manda o plano de implementação. *Controle* cobre linha a linha o detalhe de cada variável interna e de cada dispositivo externo.
- **Link direto.** `ShellViewModel.OpenDocumentationCommand(topicId)` abre a seção já no assunto. Os cards da página de Potência que perderam parágrafos ganharam um botão `?` que chama esse comando.
- **Com o manual aberto, a página inteira é dele:** a ilustração do biorreator é escondida e o formulário passa a ocupar as duas colunas.
- **Página de Potência reorganizada:** o card `Ensaio` vira dois subcards (o ensaio aberto; a tara que ele aplica), `Abrir` vira **`Carregar Ensaio`** em linha própria e largura cheia, e os textos que estavam sendo cortados na coluna de 340 DIP foram encurtados ou passados para linhas próprias.

**Por quê.** As capturas de 09/09 mostram a coluna esquerda da página de Potência com mais prosa do que controle: o card do Ponto Único gastava quatro linhas explicando para que ele serve antes de mostrar dois campos, e o da tara abria com um parágrafo de metrologia. Três consequências, todas verificáveis nas imagens: o operador que já sabe lê tudo de novo a cada visita; o que não sabe recebe a explicação em 11 px dentro de um card, sem contexto nem índice; e o texto empurra os controles para fora da tela. Some-se a isso que a explicação escrita dentro do card **não é alcançável** de nenhum outro lugar — não há como mandar alguém "ler a página tal".

Os cortes eram do mesmo problema em outra forma: `Pot. Mecânica (W)` em 112 DIP fixos, sete colunas fixas somando 358 DIP numa coluna de ~300 DIP úteis, e um rótulo de estado dividindo a faixa com três botões.

**Adendo de 10/09/2026 — Receitas.** A página de Receitas entrou no manual com três assuntos, e não um: `receitas` (o conceito, o que dá para automatizar, a página parte por parte, montagem, JSON, execução), `receitas-blocos` (os dezenove blocos, um verbete cada) e `receitas-cascata` (o bloco que contém um controlador). A divisão não é estética: a cascata precisa falar de PID, horizonte de predição, anti-windup e quatro modos de atuação, e enterrar isso numa lista de dezenove itens seria o mesmo erro que o card cometia. Um teste lê o `RecipeNodeCatalog` e exige verbete para cada bloco declarado lá — um bloco novo sem documentação falha a suíte em vez de chegar ao operador sem explicação. No mesmo passo, o selo **SAI AO ESTABILIZAR** substituiu o parágrafo que o card da cascata imprimia sobre os próprios rótulos de porta, e a altura do card passou a ter uma origem só, compartilhada com o cálculo das portas — eram duas constantes calculando o mesmo número, e é por isso que crescer o conteúdo empurrava o texto sobre as portas em vez de empurrar as portas para baixo. O manual ganhou `**negrito**` inline (`InlineMarkup`), deliberadamente o único marcador: qualquer outro seria uma segunda forma de expressar o que o tipo do bloco já expressa.

**Adendo de 10/09/2026 — equações, vocabulário e a cadeia entre as páginas.** Três acréscimos fecham o manual:

- **Equação é equação.** O bloco `Formula` desenha a fórmula na face numérica, com uma legenda obrigatória que lê os símbolos — um teste recusa fórmula sem legenda. Os assuntos de kLa, Potência, Mapa de Potência, Controle de O₂ e Integração trazem as suas: `dC/dt = kLa·(C*−C)` e a linearização que dá a inclinação, `P = τ·ω`, `Np`, `Re`, `Fl_G`, `Fr`, a fronteira de Nienow, van't Riet e a lei de dois laços do controlador. Prosa que contorna a equação custa mais ao leitor do que a equação.
- **"Cascata" é um método, não o bloco.** O bloco é **Controle de O₂** e tem quatro métodos de atuação: Agitação, Aeração, Cascata (percentuais) e Mapa (trajetória kLa). O assunto virou `receitas-controle-o2`, e um teste varre o catálogo recusando qualquer texto que volte a chamar o bloco de "bloco Cascata". O vocabulário importava porque o método Mapa é justamente o que **não** é cascata.
- **A cadeia tem assunto próprio** (`integracao-kla-potencia`): Determinar kLa mede → Mapeamento kLa interpola e publica → Potência mede o custo → Mapa de Potência ajusta van't Riet → o Controle de O₂ no método Mapa consome o perfil publicado. Cada seta é uma importação explícita, e o assunto nomeia o botão que a faz. Nenhuma página isolada podia explicar isso, e era o que faltava para o operador entender por que existem quatro páginas e não uma.

**Consequências.**

- A explicação existe uma vez e é citável (`potencia-tara`, `controle`, …). Renomear um tópico não quebra o botão: `SelectDocumentation` cai no primeiro tópico quando o id é desconhecido, porque um `?` que não faz nada é pior do que um que erra o assunto.
- O bloco `Field` obriga a nomear o controle. Um campo sem rótulo é recusado pelo teste, porque documentação que não usa a palavra que está na tela não é encontrável por quem está olhando para ela.
- `DocumentationTests` guarda os dois lados: o catálogo tem conteúdo real e cobre todas as linhas da página Controle, **e** a página de Potência de fato abriu mão dos parágrafos — um card que ficasse com o texto *e* ganhasse o `?` seria a falha mais provável.
- A evidência visual é gerada por `DocumentationEvidenceTests` a cada execução da suíte, em `docs/evidence/ui-documentation/`. Captura feita à mão envelhece sem avisar.
- Páginas ainda não documentadas (Receitas, kLa, Mapeamentos, Históricos, Eventos, Calibrações) seguem sem `?`. A ausência é honesta: o botão só existe onde há tópico.

### D-048 · O I/O dos ensaios sai da thread da UI por uma fila única e ordenada

**Status:** Accepted and implemented · 2026-09-11

**Decisão.** Tudo o que os ensaios de potência e de kLa gravam passa a ser **formatado ou
serializado na thread chamadora e executado por um único consumidor em segundo plano**, na ordem
em que foi enfileirado (`Services/Persistence/BackgroundFileWriter.cs`, `System.Threading.Channels`,
um consumidor de longa duração):

- Os `Append*` (linha bruta da corrida, série global, journal, tara bruta, ponto único) enfileiram a
  linha já formatada; os `Save*` (manifesto, tabela, resultado, resumo, análise, tara, calibração)
  serializam no chamador — o que preserva a semântica de consistência do documento — e enfileiram
  a escrita `.tmp` + `Move`. Um consumidor único preserva exatamente a ordenação que o
  `lock (_ioLock)` dava; as assinaturas `void` dos stores e os runners **não mudam**.
- Toda leitura (`Load*`, `List*`) drena a fila antes de ler, e `FlushAsync()` entra nas duas
  interfaces para quem precisa entregar os arquivos a alguém de fora. Mover ou apagar uma pasta
  drena e fecha primeiro.
- O consumidor mantém um `StreamWriter` por arquivo **só enquanto há fila acumulada** e fecha tudo
  quando a fila esvazia: um arquivo aberto para escrita impede qualquer outro processo — a
  planilha do operador, um script, o próprio `Load*` — de abri-lo (`FileShare.Read` recusa um
  handle de escrita existente), e era por isso que os stores abriam/fechavam por linha. O custo
  de abrir/fechar fica, mas na thread do consumidor.
- **O SHA-256 do dado bruto é selado sem reler o arquivo.** `PowerTestStore` alimenta um
  `IncrementalHash` por CSV de corrida com exatamente os bytes que entrega ao escritor (BOM,
  cabeçalho, linhas), e `SaveRunResult` sela a partir dele; `KlaTestStore.SaveRunRawData` devolve o
  hash do conteúdo que acabou de enfileirar (preâmbulo incluído, igual ao que
  `File.WriteAllText(…, Encoding.UTF8)` põe no disco). Um teste confirma a igualdade com o hash
  do arquivo depois do drain.
- **`ensaio.json` e `tara.json` deixam de embutir `tare.samples`.** As leituras da tara vão para o
  arquivo lateral que o esquema já previa (`tare.rawSamplesFileName`, em `Taras-Brutas/`, o mesmo
  que a D-046 grava ao vivo); o manifesto guarda só `tare.points`. Um manifesto antigo é migrado
  uma vez ao carregar. 832 KB → ~120 KB.
- Falha de escrita no consumidor é registrada (`ILogger`) e exposta por `WriteFailed`; os runners
  marcam `IsStorageCompromised`, prefixam o status com "⚠ Gravação comprometida" e seguem — o
  quadro seguinte também é dado. Sem escritor explícito, um store grava inline: é o comportamento
  anterior e o que os testes que leem os arquivos de volta esperam.

**Por quê.** Bancada de 11/09/2026: a cada quadro de telemetria (~1 Hz) a thread da UI fazia dois
`File.AppendAllText` (abre/escreve/fecha; 1–2 ms médios, picos de 12–22 ms) e, a cada mudança de
fase, reescrevia um `ensaio.json` **de 832 KB** — 490 KB de `tare.samples` — com `File.Move` e
`Thread.Sleep(20)` em retentativa: 55 ms médios, picos de 330 ms, medidos nesta máquina. Era o
engasgo "quando os documentos são salvos". A regra da §2 de `ARCHITECTURE.md` diz que nada bloqueia
a thread da UI; os stores eram a lista dos lugares em que ela era violada.

**Consequências.**
- **Nenhuma amostra deixa de ser gravada** (D-046 preservada): muda quem grava e quando, não o
  quê. `PowerTestRunnerTests.The_queued_store_writes_the_same_assay_files_as_the_synchronous_one`
  roda o mesmo ensaio pelos dois caminhos e exige `dados-brutos.csv` idêntico byte a byte e
  `serie-global.csv`, `eventos.jsonl`, `resumo-resultados.csv` e `resultado.csv` idênticos a menos
  dos ids.
- Um `Load*` logo após um `Save*` espera o drain — na prática instantâneo; no pior caso (mudança
  de fase com quatro reescritas) alguns ms. É a troca certa: leituras são raras e quem lê quer o
  arquivo mais recente.
- Criação de pastas e os `Begin*Capture` continuam síncronos, de propósito: a D-046 exige que um
  armazenamento inacessível falhe **antes** de reivindicar um atuador.
- Cobertura: `BackgroundFileWriterTests` (paridade byte a byte com `File.AppendAllText`,
  cabeçalho uma vez, ordem entre appends/reescritas/trabalho, falha reportada sem parar o
  consumidor, modo síncrono sem arquivo aberto, selo do hash na potência e no kLa, leituras que
  veem a fila, migração da tara) e os dois testes do runner (equivalência de arquivos; gravação
  comprometida).

### D-049 · O `DllNotFoundException` do descarregamento do CRT no encerramento não é um crash

**Status:** Accepted and implemented · 2026-09-11

**Decisão.** `CrashReporter.IsShutdownCrtUnloadException` reconhece a exceção que o .NET levanta ao
descarregar os assemblies C++/CLI do WPF (`DirectWriteForwarder`, `System.Printing`) **depois** de o
`vcruntime` já ter sido descarregado, e o `App.OnDomainUnhandledException` e o próprio
`GenerateAndSaveReport` a suprimem: uma linha de aviso no log, nenhum relatório, nenhuma janela de
pânico. A assinatura exige as **duas** coisas — um `DllNotFoundException` (direto ou como causa) **e**
quadros de teardown na pilha (`SingletonDomainUnload`, `ModuleUninitializer`,
`__scrt_uninitialize_type_info`, `__std_type_info_destroy_list`, `_app_exit_callback`). Um
`DllNotFoundException` real, com a pilha de quem o provocou, continua gerando relatório.

**Por quê.** O relatório `Logs/Crash/crash_20260911_103551_35c1e6.log` é o caso: o operador fechou o
aplicativo normalmente e recebeu a janela de pânico com
`__std_type_info_destroy_list → __scrt_uninitialize_type_info → _app_exit_callback →
SingletonDomainUnload`. É um defeito conhecido do encerramento do WPF quando há uma cópia nativa do
CRT no processo — aqui a que o SkiaSharp/ScottPlot traz — e acontece **depois** de todo o estado
do aplicativo já estar salvo. Mostrar um relatório de pânico ali é alarme falso, e um alarme falso
a cada fechamento ensina o operador a ignorar o alarme verdadeiro.

**Consequências.**
- A supressão é uma assinatura estreita e explícita, não um `catch` genérico: dois testes em
  `CrashReporterTests` verificam que a pilha de teardown é suprimida e que um `DllNotFoundException`
  comum e um `InvalidOperationException` não são. O teste sobrescreve `StackTrace` para fabricar a
  pilha, porque ela não é reproduzível em processo de teste.
- Os relatórios `crash_20260909_233817` e `crash_20260910_000426` **não** são este caso: eram
  `XamlParseException` de `StaticResource` em builds de desenvolvimento de 09–10/09, corrigidos em
  `f08ba27`. Um `XamlParseException` continua sendo um crash.

### D-053 · Roteamento de gás por intenção com nomes do hardware; B e C no mesmo MOSFET; pré-estabilização por C obrigatória; `t = 0` na comutação; N₂ na fonte confirmado pelo operador; setpoint > 0 exige destino

**Status:** Accepted and implemented · 2026-09-12 · plano `docs/plans/2026-09-12-plano-valvulas-abc-ensaios.md` · documento físico `docs/plans/sistema_valvulas_ensaios_potencia_kLa.md` · [P3-11](history/PHASE_LOG.md)

**Contexto.** O arranjo físico de válvulas dos ensaios passou a ser fixo: depois do fluxômetro, um T
leva à válvula **A** (ar ao reator, pelo aspersor) e à válvula **C** (descarga/purga de ar); a válvula
**B** é a linha de N₂ e está **no mesmo canal elétrico que C** — abrem e fecham juntas. O fluxômetro
tem duas entradas de MOSFET, 1 e 2; a ligação padrão é MOSFET 1 → B + C e MOSFET 2 → A. O app até
então falava em "válvula auxiliar" e "válvula de N₂" por pino, escolhidas por ensaio, e o runner de
kLa fazia "fechar N₂ → esperar → abrir ar", com uma estabilização no alívio opcional.

**Decisão.**
1. **Um roteador único** (`OpenTECHub.Protocol.GasRouting`): os produtores de comando de gás — runners
   de kLa e potência, receitas, cascata, gás proporcional da bomba, ponto único, calibração de vazão,
   painel de detalhe — pedem uma **intenção** (`Closed`, `Reactor` = A, `VentAndNitrogen` = B + C) e o
   roteador resolve o par `valve_1`/`valve_2` pela ligação em `AppSettings.GasRig`. O fio não muda.
   A telemetria é lida de volta pelo mesmo intérprete (`Closed / Reator (A) / Descarga + N₂ (B/C) /
   Gás sem destino / A e B/C abertas`), com os nomes do hardware em toda página.
2. **Setpoint > 0 exige destino** no builder (`FlowRoute` recusa `Closed` com vazão) — para ensaios e
   automação. **Operação livre não tem intertravamento** (decisão do usuário): em Controle › Avançado
   qualquer combinação é enviada; "as duas acionadas" e "linha sem saída" são avisos, e a telemetria
   denuncia (alarmes *Gás sem destino* após 3 s e *A e B/C abertas*).
3. **A comutação B/C → A é uma frame só** (`valve_1` e `valve_2` no mesmo JSON); nunca há estado
   intermediário no fio.
4. **kLa:** N₂ por B (B/C, setpoint 0) → ao atingir o piso, a vazão do ensaio é pedida **na mesma
   rota** (ar sai por C, N₂ continua) → só com vazão **e** piso de DO assentados a frame `Reactor` sai
   → **a confirmação do eco é `t = 0`**. DO já no piso ao iniciar → sem fase de N₂, mas a
   pré-estabilização continua ("baixo **e** estável"). Pré-voo: "Confirmo que o N₂ está aberto na
   fonte", gravado no manifesto e no jornal.
5. **Potência:** toda condição gaseificada sobe a vazão por C e comuta para A numa frame; sem guarda
   de N₂ e sem confirmação de fonte — a linha B fica pinçada ou desconectada e o cilindro nunca é
   aberto (garantia física).
6. **A ligação é configuração, não código** (Configurações › Gás e válvulas, com o fluxograma e a
   descrição do setup) e é **proveniência**: `gasRig` em `teste.json`/`ensaio.json`, `# gas_rig:` no
   sidecar servo, arranjo e rota nos pontos de calibração. Um manifesto sem `gasRig` é anterior ao
   arranjo: abre só para revisão; um ensaio iniciado noutro arranjo não é continuado.
7. **O simulador é o arranjo físico** (`DeviceModel.GasRig`, `NitrogenSourceOpen`): ar oxigena só por
   A, N₂ desoxigena só por B/C com a fonte aberta, linha morta com pressão subindo, degrau de carga
   do aspersor na comutação C→A. É o que exercita as máquinas de estado sem bancada
   (`KlaRunnerSimulatorTests`, E2E de potência).

**Consequências.** `VentStabilizationEnabled`, `SelectedNitrogenValve`/`SelectedVentValve`,
`PowerVentValve`, `VentAgitationRpm`, `PostNitrogen*` saíram das configurações (campos de válvula ficam
como legado de leitura); `Vent*` virou `Prestage*`; `RelativeSeconds` do CSV de kLa continua do início
da corrida e o `t = 0` vai explícito (`SwitchRelativeSeconds/FlowRateLpm/DoPercent`). Pendente de
bancada: §7.3 do plano (o que o GPIO 5 aciona; pulso na descarga; transiente após `t = 0`; comparação
do kLa com o arranjo antigo).

### D-052 · Configuração dos nós só pelo Hub; eco obrigatório antes de campo editável; diagnóstico por proxy com tarefa própria

**Status:** Accepted and implemented · 2026-09-12 · plano `docs/plans/2026-09-12-plano-exposicao-config-nos-externos.md` · [P3-10](history/PHASE_LOG.md)

**Contexto.** A reorganização dos firmwares dos nós (11–12/09) expôs parâmetros que antes eram
constantes: offset e períodos do sensor de distância (NVS, `POST /config`), sintonia do controlador
de vazão (`kp_flow`, `ki_flow`, `ff_*`, `ramp_rate`), calibração linear e PID da bomba, zerar
volume, e a aquisição óptica da biomassa (IT, PWM, gear, EMA, período). O Hub `10.1` repassava só
parte disso — as whitelists descartavam `ff_*`, `pid_*`, `reset_volume` — e o app não expunha nada;
o operador precisava do navegador e do IP do nó. Cada nó também tem um `/diag` (RSSI, heap, uptime,
`hub_fail_streak`, `ota`) inalcançável de um PC em USB.

**Decisão.**

- **Todo comando a um nó passa pelo Hub e pelo árbitro (D-015).** O app nunca fala `POST /config`
  com um nó, mesmo em Wi-Fi: um único caminho de escrita, uma única fila por nó (`queueReliable`),
  um único lugar para recusar durante um ensaio. O Hub traduz chaves camelCase do app para o
  vocabulário de cada nó (`PROTOCOL.md` §3.7).
- **Sem eco, sem campo editável.** Só vira campo na tela o que o nó ecoa no push e o Hub publica no
  quadro (`PROTOCOL.md` §2.0.3). Os ecos **não são sticky**: chave ausente é "aguardando eco do nó"
  e o campo fica desabilitado com o motivo — estado de *runtime*, não de versão. O PID da bomba
  existe na UI mas fica desabilitado até a bomba ecoar `pid_*`.
- **Distância: carona na resposta do push.** O nó só acorda para empurrar; a caixa confiável da
  distância é entregue no corpo da resposta ao `GET /distanceData`, re-entregue a cada push até o
  ack. `DistanceCommandPending` diz ao app que há comando em trânsito.
- **Biomassa: um `command` por revisão**, fila no VM, `gear` antes de `set_it`/`set_pwm` porque
  esses escrevem nos *slots* selecionados pelo gear. Fluxômetro: sintonia e setpoint na mesma
  revisão da caixa. Distância: os quatro campos numa frame só.
- **`speed` sai do quadro de segurança da bomba.** `{"mode":0}` e depois `{"pumpComm":0}`; o Hub
  nunca repassou `speed` e a bomba 3.9 leria `speed:0` como modo de velocidade armado. Fim da paridade
  byte a byte com v.6 nesse quadro (`PROTOCOL.md` §3.5).
- **Sem compatibilidade retroativa com nós `v10`/`3.8`.** A frota é regravada junto com o Hub
  `10.2.0-dev`; `NodeFirmwareCatalog` lista só `v11`/`3.9`/`v11`/`v11`/`v10` (agitador inalterado).
  Um nó antigo é um esquecido e o aviso de firmware da D-051 o denuncia; nenhum código trata "nó
  antigo sem eco" como caso especial.
- **Diagnóstico por proxy, nunca de um handler HTTP.** Uma tarefa FreeRTOS (`NodeDiag`, 6144 B,
  prioridade 1) coleta o `/diag` de cada nó registrado a cada 30 s (timeout 500 ms, 200 ms entre nós)
  num cache de 5 × 512 B; `GET /nodeDiag` e o comando serial `{"nodeDiag":…}` só leem o cache. A
  linha `{"NodeDiag":…}` é `ParseOutcome.NodeDiag` no app — não é telemetria nem falha de parse — e
  `RequestNodeDiag` é um pedido de sistema que não toma posse de atuador. Nada de saúde entra no
  quadro agregado.
- **Sintonia durante ensaio é recusada** pelo árbitro (a aeração pertence ao ensaio); fora de ensaio
  o app avisa que o valor é persistido no nó; a proveniência dos ensaios grava a sintonia usada.

**Consequências.** Hub `10.2.0-dev` (protocolo continua 10; tudo aditivo): caixa da distância por
carona, whitelists completas, 17 ecos novos no quadro, `/nodeDiag`, serial `nodeDiag`. Nós:
distância `v11`, fluxômetro `v11`, bomba `3.9`, biomassa `v11`. App: 25 chaves de comando, 17 de eco,
`CommandBuilders` com validação de faixa, expansores de configuração nas gavetas Distância, Vazão de
Ar, Bomba Externa e Absorbância, aba Calibrações › Bomba externa com recibo, tabela Nós na rede do
Hub com RSSI · Heap · Uptime · Falhas c/ Hub · OTA em USB e Wi-Fi. Suíte 1448 → 1497; contratos do
Hub 38 → 72. Pendências de bancada no plano §7.3 (regravação da frota, tamanho do quadro com ecos,
heap do Hub com a tarefa, espera da carona sob backoff). Melhorias registradas em
`docs/PONTOS_DE_MELHORIA_EXPOSICAO_NOS.md`.

### D-051 · Identidade de rede dos nós externos: aditiva, sticky, sem alarme; ações de rede só por Wi-Fi

**Status:** Accepted and implemented · 2026-09-12 · plano `docs/plans/2026-09-12-plano-identidade-nos-externos-app.md` · [P3-09](history/PHASE_LOG.md)

**Contexto.** A Fase 3 dos dispositivos externos deu ao Hub um registro de nós (`/nodeHello`,
`/nodes`): IP extraído da conexão TCP, MAC e versão declarados pelo nó. O app mostrava *se* cada nó
estava presente, mas não *quem* era nem *onde* respondia; abrir o `/diag` de um nó ou gravar um
firmware por OTA exigia adivinhar o IP, e uma reassociação do Link Watchdog ou uma renumeração do
DHCP só era visível no monitor serial.

**Decisão.**

- **O quadro agregado é a fonte; `/nodes` só enriquece.** O Hub 10.1 publica `*IP` (sempre) e
  `*NodeVer`/`*NodeMac` (só depois do hello) no `/readData`, que chega por USB e Wi-Fi. `GET /nodes`
  é consultado apenas em Wi-Fi, a cada 10 s enquanto Configurações › Conexão está aberta, por um
  `HubNodeDirectoryClient` **fora do `ITransport`** — o transporte tem um dono (`ConnectionManager`)
  e isto é diagnóstico; uma consulta falhada muda só um rodapé.
- **Sticky e anulável, como `HubFirmwareVersion`.** `ExternalNodeIdentity` mantém versão e MAC
  dentro do enlace; `0.0.0.0` limpa o IP. Ausência é *desconhecido* e a tela diz isso em palavras
  ("Identidade de rede desconhecida (Hub anterior à 10.1 ou nó não registrado)"), nunca um chip, nunca
  "offline". Presença continua sendo os `*Online`.
- **Um lugar para a semântica.** `ExternalDeviceStatus.Node` e os derivados; as cinco VMs só
  alimentam. `NodeFirmwareCatalog` guarda o **conjunto** de versões validadas por nó — igualdade a um
  conjunto, não ordenação, porque `v10`, `3.8` e `rev-h` não se comparam — e uma versão fora do
  conjunto é um **aviso em texto**, não um alarme: um nó mais novo pode estar perfeitamente bem.
- **Alcance é duas condições, ambas visíveis.** "Abrir diagnóstico" e "Copiar IP" nas gavetas exigem
  que o Hub tenha o IP *e* que o enlace seja Wi-Fi (`ControlViewModel.IsHubOnWiFi`). Em USB os
  botões ficam desabilitados com o motivo no tooltip; escondê-los faria a função parecer inexistente.
  O navegador abre por `IFileInteractionService.OpenUri` (só `http`/`https` absolutos), testável.
- **Mudanças de identidade são eventos, perda não.** `NodeIdentityTracker` (puro) emite
  Registered / IpChanged (Info) / FirmwareChanged / MacChanged (Warning) para `eventos.jsonl`; um IP
  que volta a `0.0.0.0` já é a história da presença, e o próximo endereço é um novo registro. Um novo
  enlace zera o rastreador.
- **Proveniência.** Versão e IP dos nós entram ao lado de `HubFirmwareVersion` no preâmbulo do
  sidecar servo (`# nodes:`), no `ensaio.json` de potência (`externalNodes`) e no `teste.json` de kLa
  (que ganha também `hubFirmwareVersion`/`hubProtocolVersion`). Manifesto antigo carrega vazio.
- **Diferido.** OTA a partir do app (`Publish-OtaFirmware.ps1` já cobre, com verificação de baseline
  que o app não tem). O proxy `GET /nodeDiag` foi feito em [D-052](#d-052--configuração-dos-nós-só-pelo-hub-eco-obrigatório-antes-de-campo-editável-diagnóstico-por-proxy-com-tarefa-própria).

**Consequências.** Quinze chaves novas no parser, cinco gavetas com um cartão "Rede", um painel em
Configurações › Conexão, uma linha no popover ("Nós do Hub n/5"), quatro tipos de evento, três
cabeçalhos de proveniência. O simulador reproduz a regra condicional do Hub e ganha os cenários
`node-renumber` e o `legacy-hub` estendido. 62 testes novos.

### D-050 · Corrida sem captura nunca é aceitável; falha de sequência não é revisão de resultado

**Status:** Accepted and implemented · 2026-09-11

**Decisão.** Uma corrida de potência que parou para revisão **antes de capturar** — tempo limite do
alívio, de confirmação de válvula ou de rotação — não tem resultado, e por isso não pode ser aceita:

- `PowerTestRunner.AcceptRunAsync`/`AcceptRunCore` recusam (`InvalidOperationException`, "Corrida sem
  captura: repita ou rejeite") quando `SampleCount == 0` ou `NetPowerW` não é finito. O aceite
  automático nunca chega aqui: só é consultado após uma captura bem-sucedida.
- A faixa de revisão muda de natureza: com captura, *Aceitar · Rejeitar · Repetir*; sem captura, o
  título é **Corrida não realizada**, o texto é *Sem captura — {motivo}*, não há P/IC95/Np e só
  existem **Repetir** e **Rejeitar**. Confirmado com o operador: "não deve aparecer para eu aceitar
  nada, pois não se conseguiu fazer o teste".
- *Alternar Aceite* na tabela de pontos aplica a mesma regra ao ir para `Accepted`.
- **Migração ao carregar.** Um manifesto anterior a esta decisão pode trazer corridas `Accepted`
  com `sampleCount = 0` e `netPowerW = 0`: `PowerTestStore.LoadTest` as rebaixa para `Rejected` uma
  vez, recalcula `AcceptedReplicates`/`RejectedReplicates`, reabre a condição (`Completed →
  Pending`), volta um ensaio `Completed` para `Interrupted` se alguma condição reabriu, regrava
  manifesto e tabela, regenera `resumo-resultados.csv` e registra um `RunRejected` "sem captura
  (migração)" por corrida em `eventos.jsonl`. A corrida fica no manifesto: nada medido é apagado.
- `resumo-resultados.csv` exclui linhas sem captura mesmo que um manifesto as traga.

**Por quê.** Ensaio IsojetB-Combijet, 2026-09-11: com `MaxVentStabilizationSeconds = 120`, três
estabilizações a 200 rpm expiraram com zero amostras e foram para revisão. Em duas o operador clicou
*Aceitar*: as corridas ficaram `Accepted` com `n = 0` e `P = 0 W`, entraram no resumo e marcaram as
condições como `Completed` — a sequência pularia por cima delas. O runner só verificava a fase
`Reviewing`; a faixa oferecia *Aceitar* sem condição. Uma falha de sequência era apresentada como um
resultado a revisar, e a interface convidava a aceitar um ponto falso.

**Consequências.**
- O predicado de "fantasma" para linhas de resumo é **sem amostras e sem potência utilizável**
  (`IsRunWithoutCapture`): as duas condições, porque uma linha de manifesto antigo que nunca gravou
  `sampleCount` mas carrega potência medida não pode ser confundida com uma corrida não realizada.
  No runner, sobre a corrida viva, o predicado é o estrito (`HasCapture`: amostras > 0 e P finita).
- Em modo autônomo, a política de falha de sequência (§I do plano de 11/09) decide entre parar para
  revisão e repetir/pular — mas nunca aceitar.
- Cobertura: `PowerRunWithoutCaptureTests` (predicados, faixa sem *Aceitar*, *Alternar Aceite*
  recusado, migração com reabertura e journal único, manifesto limpo intocado) e
  `PowerTestRunnerTests.A_run_that_timed_out_before_capturing_cannot_be_accepted_only_rejected_or_repeated`.

---

## Open questions

| # | Question | Blocks |
|---|---|---|
| [PROTOCOL.md](PROTOCOL.md#5-open-questions-for-hardware-verification) Q1-Q5 | Firmware behaviours that could not be confirmed from Python source | Phase 0 exit |
| — | Does the nitrogen-enrichment path change the wire protocol, or only the control law? | Phase 2 |
| — | Is there a second bioreactor module to support (`HubStations`)? | Phase 3+ |
