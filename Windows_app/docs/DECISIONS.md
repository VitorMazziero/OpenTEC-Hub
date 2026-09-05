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

## Open questions

| # | Question | Blocks |
|---|---|---|
| [PROTOCOL.md](PROTOCOL.md#5-open-questions-for-hardware-verification) Q1-Q5 | Firmware behaviours that could not be confirmed from Python source | Phase 0 exit |
| — | Does the nitrogen-enrichment path change the wire protocol, or only the control law? | Phase 2 |
| — | Is there a second bioreactor module to support (`HubStations`)? | Phase 3+ |
