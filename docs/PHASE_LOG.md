# Phase Execution Log

> Running record of decisions taken **while executing** the phases — what was chosen,
> why, what the evidence was, and what was deliberately deferred.
>
> Distinct from [DECISIONS.md](DECISIONS.md), which holds durable architectural
> decisions (ADRs). This file is chronological and narrower: it captures the
> judgement calls made mid-build, so that in six months nobody has to reconstruct
> them from commit messages.
>
> **Docs:** [ROADMAP](ROADMAP.md) · [PHASE0_RESULTS](PHASE0_RESULTS.md) · [PROTOCOL](PROTOCOL.md) · [DECISIONS](DECISIONS.md)

---

## How to use this file

Add an entry when a decision changes *how a phase is executed* rather than what the
architecture is. Typical triggers: a measurement contradicts an assumption, scope is
pulled forward or deferred, a plan step turns out to be unnecessary, or a defect is
found and consciously scheduled rather than fixed immediately.

Each entry: **what was decided · why · evidence · consequence.** Keep evidence
concrete — a number from a run beats an opinion.

---

# Phase 0 — Protocol spike

**Started and completed (except two bioreactor-dependent criteria): 2026-08-19**

---

### P0-01 · Port the v.6 communication layer rather than redesign it

**Decided:** keep `transport.py`'s structure, `data_parser.py`'s semantics and the
`ConnectionManager` state machine shape. Rewrite only the threading model (Qt signals
and a worker thread become events and an async loop).

**Why:** the layer with the worst reputation in v.6 turned out to be its best code.
The request-queue design and the state transition table are sound; the mess is in
`main.py` above it.

**Consequence:** the C# port is a translation, not a redesign, which keeps it
comparable to v.6 line by line when debugging the wire.

---

### P0-02 · Build a console harness before any UI

**Decided:** ship `TecnalHub.Harness` as a real project, not a throwaway script.

**Why:** the exit criterion is a byte-comparison against v.6 traffic. That needs a
wire trace, which needs somewhere to put it. A harness also makes every later
hardware question answerable in minutes.

**Consequence:** paid for itself three times over — it produced the USB findings, the
unattended Wi-Fi suite, and the reset experiment. Kept as a permanent diagnostic tool;
excluded from the installer.

---

### P0-03 · Recognise `OK` and `[ESP32_` as first-class read outcomes

**Decided:** `ParseOutcome.CommandAck` and `ParseOutcome.DeviceLog` alongside
`Updated` / `Malformed`.

**Why:** the device interleaves log lines and command acknowledgements with telemetry
on the same stream. A reader that assumes "every line is JSON" counts them as
corruption.

**Evidence:** bench run showed 1 parse failure per command sent; after the change, 0.
Confirmed independently on Wi-Fi, where `/readData` serves the handshake's `OK` from a
shared response buffer on **every** connect.

**Consequence:** v.6 has this bug latently — it counts the ack as a parse failure and
survives only because it needs three *consecutive* ones. Several commands in quick
succession can trip a false link-loss there. Documented rather than fixed, since v.6
is frozen.

---

### P0-04 · Express link silence as a duration, not a count of empty reads

**Decided:** replace v.6's "N consecutive empty reads" with a `TimeSpan`.

**Why:** counting couples failure detection to the poll rate — polling faster makes
the link look dead sooner. That is an invisible trap for anyone tuning the poll later.

**Evidence:** 4 empty reads at a 250 ms poll declared the link dead after 1 s, against
a device emitting every 2 s. The first bench run reconnected in a loop every ~4 s.

**Consequence:** the poll interval became a free parameter again.

---

### P0-05 · Treat the culture hazard as a type-level concern

**Decided:** `TecnalCommand` has no API for putting a raw string on the wire. Every
value is formatted invariantly inside the builder.

**Why:** a `{value:F2}` interpolation on a pt-BR machine emits `6,98` where the
firmware expects `6.98`. Discipline alone is not enough — within an hour of writing
the rule down, the harness's own display code violated it.

**Evidence:** `t=0,05min` and `Found ESP32 in 1,9 s` appeared in the first run under
the machine's real locale.

**Consequence:** `CultureInvarianceTests` forces `pt-BR` explicitly rather than
trusting the CI machine's locale.

---

### P0-06 · Probe serial ports concurrently, remembered port first

**Decided:** replace v.6's serial probe loop with a parallel race, short-circuiting on
the last-known-good port.

**Why:** v.6 pays a full boot settle plus up to ten handshake timeouts per dead port —
over 10 s each, uncancellable. That is the bulk of the startup delay users complain
about.

**Evidence:** discovery with no port configured: 1.9 s, versus ~12 s serially on the
same machine (later 0.1 s, see P0-09).

**Deferred:** the race opens every visible COM port at once, which will collide with
other software — including v.6, kept installed as the fallback. Logged as a P3
follow-up: treat "access denied" as *busy, skip*.

---

### P0-07 · Run the Wi-Fi validation unattended, with results on disk

**Decided:** a seven-step suite writing a report to a fixed temp path, with raw HTTP
probes deliberately **not** routed through `HttpTransport`.

**Why:** joining the device's access point costs the machine its internet connection,
so nobody can be watching and a re-run is expensive. Separating raw probes from our
transport means a failure can be attributed to the network or the device rather than
to our code.

**Evidence:** dry-run offline confirmed the failure path short-circuits in 37 s with
an actionable checklist instead of sitting through four minutes of timeouts.

**Consequence:** 20/21 checks passed on the first real run.

---

### P0-08 · Keep the v.6 Wi-Fi timeouts unchanged

**Decided:** leave 250 ms connect / 750 ms read / 500 ms write as they are.

**Why:** they looked aggressive when ported, so the suite measures latency and
auto-retries with relaxed timeouts on handshake failure to distinguish "too tight"
from "unreachable".

**Evidence:** `POST /command` round trip — min 5, median 9, **p95 33**, max 33 ms over
20 samples. Roughly 15x headroom.

**Consequence:** tight but not wrong. No change.

---

### P0-09 · Disable the DTR/RTS reset pulse by default

**Decided:** `PulseResetOnConnect` defaults to **false**, the boot settle applies only
when pulsing, and `ConnectionManager` escalates to a pulsed connect after repeated
handshake failures.

**Why:** the pulse was inherited from v.6 without anyone asking what it was for. It is
the ESP32 auto-reset mechanism used by `esptool` for flashing — but we are attaching
to a *running application*, not flashing. A reset discards process state, setpoints
included.

Worth noting: v.6's sequence is not even a well-formed reset. The auto-reset circuit
responds only to *differential* DTR/RTS states, and v.6 drives both lines to the same
value at each end. Whatever reset it causes comes from the transient between two
non-atomic line changes — an accident, not a design.

**Evidence** (`tecnal-harness reset-test COM3`):

| Configuration | Wall clock | Device clock | Verdict |
|---|---|---|---|
| Pulse enabled | +5.1 s | **−99.3 s** (102.1 → 2.8) | rebooted |
| Pulse disabled | +5.5 s | **+5.5 s** (2.8 → 8.3) | kept running |

Minimum viable settle with the pulse off: **0 ms**, handshake still succeeding.

| Metric | Before | After |
|---|---|---|
| Connect | 1903 ms | **13 ms** |
| Discovery, no port configured | 1.9 s | **0.1 s** |
| Device state across reconnect | lost | **preserved** |

Verified end to end: two separate harness processes 26 s apart read device clocks of
108.1 s and 134.4 s — a 26.3 s advance matching wall-clock exactly.

**Consequence:** answers [PROTOCOL.md](PROTOCOL.md) Q6. The reset stays reachable for
the one case that needs it — a hung firmware, where nothing else recovers the board —
but as a deliberate escalation rather than the default. Note the firmware also exposes
`{"restart":1}`, a cleaner software-level restart for when the user *wants* one.

**Method note:** the first run of this experiment reported "no reset" in both
configurations. The detector only looked for the clock going *backwards*, which never
happens when *every* connect resets to the same value — three identical 2.8 s
readings. Fixed to compare the device-clock advance against wall-clock elapsed.
A detector that cannot distinguish "always broken" from "never broken" is worse than
none.

---

### P0-10 · Fix Wi-Fi silence detection and the dormant heartbeat before Phase 1

**Decided:** treat both as P1 and fix them before starting UI work.

**Why:** the silence check was gated on `Medium == Usb`, so a Wi-Fi link whose
telemetry stalled while the web server stayed up would show frozen readings behind a
healthy "Connected" indicator, indefinitely. For a control application, stale data
presented as live is worse than an honest disconnect. Separately,
`HeartbeatInterval` was declared and `TestConnectionAsync` implemented, but nothing
ever called them — dropped during the port.

**Consequence:** two-stage liveness — probe after 4 s of silence, drop the link at 8 s
*even if the probe still succeeds*. A healthy link sends no probes at all, verified on
hardware (45 s USB run, `liveness probes: 0`); the device shares one UART with the
sensor module, so needless chatter is not free.

---

### P0-11 · Pull the state-machine tests forward from P2

**Decided:** build `FakeTransport` and 14 `ConnectionManagerTests` during Phase 0
rather than in the planned P2 slot.

**Why:** the P1 fixes were untestable without them. Link supervision cannot be
verified by unplugging cables repeatably, and "it worked on the bench for 60 seconds"
is not a regression test.

**Evidence:** the Wi-Fi silence test was run against the *previous* implementation and
does fail there — confirming it is a real regression test rather than one written to
pass what had already been written.

**Consequence:** `ConnectionManager` gained a transport-factory parameter purely for
testability. Most of the P2 testing item is now closed.

---

### Phase 0 — what was deliberately NOT done

| Item | Why deferred |
|---|---|
| Byte-comparison against a v.6 capture | Needs the bioreactor; both sides must perform the same operator actions |
| Live-sensor telemetry | Needs the bioreactor. Calibration, spike filters and the `pHCal` string echo have only ever seen synthetic data |
| Port-probe politeness and descriptor ranking | P3; matters when v.6 is running alongside, not for validation |
| `LastRoundTripMs` correlation | P2; misleading on USB but not wrong anywhere it is currently displayed |

---

# Phase 1 — Shell, connection UX, core loop

**Started 2026-08-19.**

---

### P1-01 · Build a vertical slice before any screen is finished

**Decided:** foundation (DI, settings, theme, logging) plus shell, connection chip and
KPI strip first - launch, auto-connect, live values - rather than completing one
screen at a time.

**Why:** it exercises every layer against real hardware on day one. A beautiful
synoptic bound to nothing proves less than a plain window showing a real reading.

**Evidence:** first run reached **first frame in 662 ms** (budget 2000 ms) and was
connected to COM3 **599 ms after process start**.

---

### P1-02 · Settings are one typed record, not two marshalling functions

**Decided:** `AppSettings` as a nested record tree, `System.Text.Json`, debounced
atomic writes.

**Why:** v.6's `collect_preferences` / `apply_preferences` are 370 lines that must be
kept in sync by hand - the largest single source of silently lost settings. Adding a
setting here is now one line, with no read site and no write site.

**Consequence:** a corrupt settings file is quarantined rather than deleted, and the
app starts on defaults. An operator who cannot launch the app cannot reach the
equipment either, so failing to start is never the right answer.

---

### P1-03 · Agitation is displayed as commanded, not measured

**Decided:** `ProcessVariableViewModel.IsCommandedOnly`, and the KPI tile labels the
agitation figure *comandado*.

**Why:** while wiring telemetry I mapped motor RPM to a telemetry field, then found
the firmware sends **no RPM feedback at all**. v.6 logs the commanded value in the
same column as measured ones, which quietly implies a verification the equipment never
provided.

**Consequence:** the display distinguishes the two. Anything with no feedback path must
say so.

---

### P1-04 · Sentinels display as an em dash, never as zero

**Decided:** `-1` and below render as `—`.

**Why:** zero is a legitimate reading for pressure and flow. Showing the sentinel as
`0.0` would be indistinguishable from a real measurement.

**Evidence:** with no bioreactor attached, the first run correctly showed `—` for
temperature, oxygen, pH and flow, and `0,0 kPa` for the one channel genuinely
reporting zero.

---

### P1-05 · Two WPF defects worth recording

**Fonts.** The token dictionary named `Segoe UI Variable Text, Segoe UI, sans-serif`.
Two faults: `sans-serif` is a CSS keyword and not a WPF font family, and **Segoe UI
Variable ships only with Windows 11** while this machine is Windows 10. WPF's fallback
resolution recursed until the stack overflowed, inside `TextBlock.MeasureOverride`,
before the first frame. Now pinned to `Segoe UI`, which exists on both.

**Bindings.** `ConverterParameter={Binding Id}` throws at parse time -
`ConverterParameter` is not a `DependencyProperty`. Replaced the templated
`RadioButton` nav rail with a `ListBox` using `SelectedValuePath`, which expresses
selection directly and removes the converter entirely.

Both were caught only because unhandled exceptions are logged to file from the first
line of `OnStartup`. Neither appears in a build.

---

### P1-06 · Simulate the ESP32, not the sensor module

**Decided:** the simulator replaces the ESP32 and speaks the documented PC-side
protocol. The proposed alternative — app → real ESP32 → simulated sensor module — was
assessed and rejected.

**Why:** the ESP32's module link is a hardware UART on GPIO pins, physically separate
from the USB CDC the app uses. They would not contend, so the original concern about
port conflict was unfounded — but **virtual COM port software cannot bridge to it**
either, since it only creates PC-internal pairs. It would need a USB-TTL adapter wired
to those pins, plus reverse-engineering an undocumented single-character protocol, in
order to test the **firmware** — which is frozen and already works — rather than the
app.

**Consequence:** the existing Python simulator had already reached the same conclusion;
despite being named `Simulated_MODULE.py`, it simulates the ESP32. Rewritten in C# so
it shares `TecnalHub.Protocol` and is therefore held to the same golden-string tests as
the app. A simulator that drifted from the contract would quietly certify a broken
client.

---

### P1-07 · HTTP on localhost is the default, not the serial pair

**Decided:** the primary way to run the simulator is HTTP on `127.0.0.1`; the virtual
COM pair is secondary.

**Why:** the app's Wi-Fi transport already speaks HTTP to an address, so pointing it at
localhost needs no driver, no administrator rights and no reboot. The serial path needs
a com0com install and exists only to exercise the serial stack specifically.

**Worth noting:** this is only clean because the DTR/RTS pulse was removed
([P0-09](#p0-09--disable-the-dtrrts-reset-pulse-by-default)). Virtual ports do not
meaningfully emulate control lines, so v.6's pulse plus 1.8 s settle would have made
the serial route awkward.

---

### P1-08 · The simulator emits raw ADC counts, not engineering units

**Decided:** invert the field calibration so the wire carries counts, as the firmware
does.

**Why:** emitting `40` for oxygen directly would leave the app's entire calibration and
spike-filter path untested — and a wrong coefficient is exactly the defect that then
survives to the lab.

**Evidence:** `Oxyval:3926.6` decodes through the app to **94.9 %**; `pHval:15178.7`
to **7.01**. The round trip is verified end to end rather than assumed.

---

### P1-09 · A simulator bug, caught by the app

**What happened:** the first version incremented the ETag only when sending a 200. After
the first frame it therefore answered **304 forever** while its process kept advancing —
accidentally implementing the `stall` scenario.

The app responded correctly: `sem telemetria por 8s`, then reconnect. The Wi-Fi silence
timeout added in [P0-10](#p0-10--fix-wi-fi-silence-detection-and-the-dormant-heartbeat-before-phase-1)
caught a genuinely stalled feed on its first real encounter with one.

**Fixed:** the simulator now publishes a frame on a timer at `dataDelay` and tags *that*,
so 304 means "you already have the current frame" rather than "the tag never changes".

**Also fixed:** `TransportFaultException` now folds the inner exception's message into
its own. `read failed` alone cannot distinguish a timeout from a refused connection, and
that message is what reaches the connection popover.

---

### P1-10 · Validation guards the send, not just the button

**Decided:** `SubsystemViewModel.Apply` re-validates before sending, rather than relying
on the command's `CanExecute`.

**Why:** found by the tests, and it was a real defect. `CanExecute` only greys out the
button. `Apply` itself checked whether the text *parsed* - so `60.1` on a 15-60 range
parsed perfectly well and was sent to the device. Anything reaching the method by
another route (the Enter key, a future recipe engine) would have bypassed the range and
integer rules entirely.

**Consequence:** the guard now sits where the send happens. This is the same class of
defect as v.6's `except: ph_value = 7` - a setpoint reaching the reactor without the
operator's intent - arrived at from the opposite direction.

**Evidence:** four tests failed on first run and pass after the fix.

---

### P1-11 · Test project references the app assembly

**Decided:** `TecnalHub.Tests` moved to `net10.0-windows` with `UseWPF`, so ViewModels
can be tested.

**Why:** setpoint validation is what stands between a typo and a reactor. Leaving it
untested because the assembly was awkward to reference would be the wrong trade, and
P1-10 is the proof - the bug was invisible until the tests existed.

**Consequence:** no test starts an `Application`; the ViewModels take interfaces, so
they run headless. 86 tests.

---

### P1-12 · Restoring a setpoint is not an operator edit

**Decided:** the pending-change marker is suppressed while the constructor seeds the
field from persisted settings.

**Why:** the first build lit "não aplicado" on all five subsystems at launch. It was
*technically* honest - those values genuinely had not been sent this session - but a
warning that is always on is one nobody reads, and it would have devalued the marker
in the case that matters: a half-typed setpoint mid-run.

---

### P1-13 · Two chart panels, not five

**Decided:** the charts page shows at most two panels, side by side, each selectable
from every channel the app produces.

**Why:** the first build stacked one small chart per variable. All five fitted on
screen and none of them answered a question - a few hundred pixels of height is not
enough to read a trend from. A bioreactor question is almost always *one variable
against one other*: temperature against DO, agitation against DO, flow against
pressure.

Side by side rather than stacked because a trend is read along the time axis, and on a
wide screen that is where the pixels are. Collapsing the right panel gives the left one
the full width - which required collapsing the grid *column*, not just hiding the
border, or the chart would have stayed at half width beside an empty gap.

**Consequence:** the selectable set mirrors v.6's graphs page, minus OUR. That arrives
with the Phase 2 soft sensor, and offering an always-empty chart would be worse than
not offering it.

---

### P1-14 · History is a ring buffer, sentinels become NaN

**Decided:** fixed-capacity ring buffer sized for ~48 h at the field `dataDelay`, with
stride downsampling to 2000 points before anything reaches the plot.

**Why:** the roadmap's target is flat memory across a 24 h run, and a cultivation can
run longer. Growing a list would breach it. Downsampling is separate and equally
necessary: no display has 86,000 horizontal pixels, so handing a plotting library every
point costs time and shows nothing extra.

**Also:** the not-received sentinel is stored as `NaN`, so a chart shows a **gap**. Left
as -1 it would draw a line diving to a value that looks like a real measurement - the
same failure the em-dash rule prevents on the readouts.

---

### P1-15 · The session log format is a contract

**Decided:** byte-compatible with v.6 - column set, order, tab separator, decimal
places, UTF-8 without BOM, header only when the file is new.

**Why:** existing analysis scripts read these files. Columns outside the Phase 1 scope
are still emitted carrying the sentinel, so the column count never changes between
versions of this app and a script never has to ask which version wrote a file.

Numbers are invariant-formatted. The file is data for downstream tools, not text for a
person, so a pt-BR decimal comma would silently break every consumer - exactly as it
would on the wire. There is a test that asserts this under a `pt-BR` culture.

**Consequence:** `BuildRow` is `internal` with `InternalsVisibleTo` for the tests.
Widening the public surface just to assert on an implementation detail would have been
the wrong trade; leaving a contract untested would have been worse.

---

### P1-16 · Advanced settings is a page, not a window

**Decided:** built as a page in the nav rail, contrary to [UI_DESIGN](UI_DESIGN.md),
which had specified a separate window.

**Why:** the nav rail already had the slot; the KPI strip stays visible so no safety
context is lost by navigating there; and it is one implementation rather than two. The
popover's "Configurações avançadas" button navigates to it, which was the only thing
the window arrangement bought.

**Consequence:** UI_DESIGN updated rather than left to disagree with the code.

---

### P1-17 · Settings edits are staged, not applied per keystroke

**Decided:** every field stages, and one Apply writes them together.

**Why:** calibration coefficients are entered as a **pair**. Applying a new slope
against an old intercept - even for the half-second before the second field is typed -
would put visibly wrong numbers on screen and into the session log. Staging also makes
Revert meaningful and gives "restore factory values" somewhere safe to land.

**Also:** each pair shows a live preview of what the current raw count decodes to.
Coefficients are otherwise impossible to sanity-check by eye - `0.0305473419314` looks
exactly as plausible as `0.305473419314`, and only the decoded value reveals which is
right.

**Bug found doing it:** the preview was computed once at construction, before any
telemetry had arrived, and never recomputed - so it read "sem leitura bruta disponível"
permanently. It now re-evaluates on each frame.

---

### P1-18 · Spike-filter thresholds surfaced, not hidden

**Decided:** the filter tuning card carries an explicit note that its thresholds are in
raw ADC counts, not engineering units.

**Why:** that coupling is a known v.6 defect ([MIGRATION](MIGRATION.md) item 4) -
recalibrating a probe silently changes what the filter treats as a spike. It cannot be
fixed without a bench comparison, so the next best thing is to make sure anyone tuning
one knows about the other. Hiding a known sharp edge is worse than labelling it.

---

## Phase 1b — Design-system refit and shell completion

### P1B-01 · Controle reuses the detail pane's subsystem state

**Decided:** the all-setpoints table binds to the same five `SubsystemViewModel`
instances as the detail pane. `SubsystemViewModel` now separates building a validated
command from committing the staged state, so per-row apply and bulk apply use one set of
rules rather than parallel implementations.

**Why:** copying values into a second table model would let the two screens disagree about
what is dirty, valid or applied. The page is a second view of the same control state, not
a second owner of it.

**Consequence:** bulk apply builds each dirty row, merges the flat command objects, queues
one frame, then commits those same rows. A flow setpoint above the staged `maxFlow` is
refused before the builder can clamp it.

---

### P1B-02 · Valve control sends complete desired flow state

**Decided:** changing either gas valve sends `flowmeterComm`, `flowSetpoint`, `maxFlow`,
`valve_1`, `valve_2`, and the derived `v_Flow` together. The vent state is read-only in
the UI and shown as both physical state and wire flag.

**Why:** a one-key valve write would recreate the legacy hub's stale-state hazard. The
reliable flow protocol is desired-state based, idempotent and acknowledgement-visible;
the operator should never have to remember that `v_Flow` is inverted.

**Evidence:** golden tests pin valves open at zero flow as `v_Flow:1`, and the complete
safe-stop as both gas valves closed. The simulator displayed `Aberta · v_Flow = 1` while
the measured flow was zero.

---

### P1B-03 · Presets stage; only an explicit apply reaches the wire

**Decided:** a named preset persists values, enabled flags, both valve requests and
`maxFlow`, but loading it only fills the fields.

**Why:** a preset is a pre-run verification aid, not a recipe. Reasserting an old set of
commands merely because it was selected would be an unsafe surprise.

**Evidence:** `Loading_a_preset_only_stages_fields_and_never_sends` observes an empty
device-command capture after load. The page says explicitly that nothing was sent.

---

### P1B-04 · Safe stop previews exact bytes and defaults to cancel

**Decided:** `Parada segura` is the only red action in the application. It opens the
destructive-confirmation dialog with the consequence and exact JSON, with Cancel as the
default. Confirm sends one complete core safe-stop.

**Why:** five individual writes can be interrupted between subsystems, and a generic
"are you sure?" does not tell an operator whether nitrogen closes. The exact command is
both the safety explanation and the protocol evidence.

**Evidence:** cancellation and confirmation are separate tests. The confirmed frame
contains temperature, motor, oxygen, pressure, flow disable, both gas valves closed, and
the inverted vent flag asserted.

---

### P1B-05 · Runtime review found two shell defects outside the page

**Found and fixed:**

1. The variable rail had responsive state in the ViewModel but its `Border` never bound
   `Visibility`, so it was always present. Controle now also hides the duplicate rail by
   design, leaving every table column visible at 1280 px.
2. Clean shutdown called synchronous `ServiceProvider.Dispose()` even though the
   container owns async-only services. Every normal exit logged a fatal exception.
   `DisposeAsync()` now closes the session logger, device and settings once, in container
   order. A fresh start/exit at 10:12 logged the exit and settings save with no later fatal.

**Visual evidence:** `docs/evidence/ui/wp6-controle-{light,dark}.png`; all five rows and
their Apply/Revert actions were also enumerated as visible through UI Automation.

**Test evidence:** 169/169 pass. The existing SkiaSharp `NU1701` compatibility warning
remains; it is unrelated to WP6 and was present in the 161-test baseline.

---

### P1B-06 · Graphs and session discovery are separate destinations

**Decided:** the requested dedicated `Gráficos` page remains in the rail. Históricos is
a separate session inventory that validates a file and then loads it into the same two
chart panels.

**Why:** file management and visual comparison are different operator tasks. Combining
them would make the graph workspace carry discovery chrome permanently; renaming the
only live graph destination would also obscure its primary use.

**Evidence:** a 58-row simulator session was inventoried with its exact header, first and
last rows, loaded into both panels, and returned to live mode without reconnecting.
Screenshots: `wp7-historicos.png`, `wp7-historico-nos-graficos.png`, and
`wp7-graficos-dual.png`.

---

### P1B-07 · Eventos records only successful transport bytes

**Decided:** the protocol layer publishes `CommandSent` after `WriteAsync` returns true,
carrying the exact merged JSON. Rejected writes are requeued but not reported as sent.
The bounded journal adds connection, setpoint, calibration, application and USB-only
equipment facts around that source.

**Why:** logging an operator intent as a transmission would create false evidence in the
one page intended for v.6 byte comparison. Device-log lines alone are unavailable over
Wi-Fi, so the application must expose the facts it actually knows on either medium.

**Runtime findings:** the first live launch found an initialization-order null in the
two event filters. Selecting an event then exposed a two-way binding against its
read-only `Detail`. Both are regression-covered/fixed; a fresh simulator run logged zero
XAML binding failures.

---

### P1B-08 · Display units stop at the protocol boundary

**Decided:** temperature and pressure preferences re-express readouts, staged fields,
ranges and charts. Stored telemetry, session files and command builders remain in °C and
kPa. Conversion results are normalized to twelve decimal places before command JSON.

**Why:** changing presentation must never change the firmware contract or historical
file format. The normalization was added after a focused test showed that 98.6 °F could
otherwise leave as `36.99999999999999` °C.

**Test evidence:** 178/178 pass. Focused coverage pins successful-vs-rejected command
events, session header/rows/duration, pressure round trips, Fahrenheit command bytes,
event initialization and default-cancel reset/restart behavior. The existing
SkiaSharp `NU1701` warning remains unchanged.
