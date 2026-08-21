# Phase Execution Log

> Running record of decisions taken **while executing** the phases — what was chosen,
> why, what the evidence was, and what was deliberately deferred.
>
> Distinct from [DECISIONS.md](DECISIONS.md), which holds durable architectural
> decisions (ADRs). This file is chronological and narrower: it captures the
> judgement calls made mid-build, so that in six months nobody has to reconstruct
> them from commit messages.
>
> **Docs:** [ROADMAP](ROADMAP.md) · [PHASE0_RESULTS](PHASE0_RESULTS.md) · [PROTOCOL](PROTOCOL.md) · [CALIBRATION](CALIBRATION.md) · [DECISIONS](DECISIONS.md)

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

---

### P1B-09 · Keyboard actions are searchable and unavailable actions stay honest

**Decided:** the shell owns one searchable catalog for its present pages and global
actions. `Ctrl+1`–`Ctrl+6` navigate the six current destinations; the handler reserves
slots 7–8. `Ctrl+K` opens the catalog, `Ctrl+R` toggles the optional variable rail, `F5`
performs an explicit disconnect/connect sequence, `Space` pauses Gráficos away from
interactive controls, and `Esc` dismisses the active shell context.

`Ctrl+S` is claimed now but does not simulate recipe persistence. It opens a disabled
`Salvar receita` result saying that the editor arrives in Phase 3. This keeps the final
operator map stable without implying a capability that does not exist.

**Persistence boundary:** settings store normal window bounds, maximized state and the
last page as plain serializable values. A platform resolver clamps valid geometry and
centres an off-screen window after monitor changes. The WPF window is the only layer that
reads or writes `Rect` values.

**Runtime evidence:** UI Automation exercised the keyboard map against the localhost
simulator. F5 produced Disconnected → Connecting → Connected journal entries; after a
full close and relaunch, Eventos, the expanded variable rail and bounds (140, 90,
1450×850) were restored exactly. A Tab traversal displayed the shared 2 px accent focus
ring. Screenshots: `wp8-command-palette.png` and `wp8-keyboard-focus.png`.

**Test evidence:** 185/185 pass. The fresh runtime interval contains no XAML binding
failure, fatal exception or unhandled exception; the existing SkiaSharp `NU1701` warning
is unchanged.

---

### P1B-10 · The equipment is neutral scenery; the Painel owns operational truth

**Decided:** Painel uses one transparent, neutral PBR reactor master in both themes.
Readings, units, state dots, selection, leader lines, keyboard hit targets and missing
level state remain WPF. If the PNG cannot decode, only the equipment layer falls back to
the vector schematic.

**Mechanical correction:** an early render incorrectly continued the agitator shaft to
the sparger. The accepted asset has two Rushton disc-turbine levels, the shaft terminating
below the lower hub, a visible clearance gap, and an independent annular sparger fed by a
separate wall-side dip tube. It also makes the jacket, top-drive motor, top entries and
four process probes readable at HMI scale.

**Transparency gate:** one intermediate edit encoded a checkerboard into 24-bit RGB and
was rejected. The packaged 1024 × 1536 PNG is colour type 6 RGBA with alpha-zero corners
and an opaque equipment centre. Because the same neutral lighting remained legible on
both token palettes, the solid-background light/dark contingency was unnecessary. The
prompt, hash and asset contract are recorded in `docs/ASSET_PROVENANCE.md`.

**Control-room closure:** pH now has its missing physical callout; the selected equipment
card agrees with the rail/detail selection; the header shows system state and update age;
the footer says explicitly that level is not monitored. Live theme switching now repaints
the inline ScottPlot trend. The final responsive backlog item is also closed: below
1400 px the navigation becomes a 52 px icon strip while automation names and tooltips
retain the labels.

**Evidence:** 190/190 tests pass. Runtime review covered light, dark and 1280 px layouts
against the localhost simulator; the final interval logged no binding failure, fatal
exception or unhandled exception. Release publish succeeded with the PNG and anchor JSON
inside `TecnalHub.g.resources`. Screenshots:
`phase1-final-painel-{light,dark}.png` and `phase1-final-responsive-1280.png`.

**Boundary:** this closes Phase 1 and Phase 1b software. It does not satisfy the real
cultivation exit gate or validate biological process performance; v.6 remains the
production fallback until the bioreactor run is completed.

---

# Phase 2 — Cascade control + dosing

**Started 2026-08-20.**

---

### P2-01 · Build the cascade controller core first, and headlessly

**Decided:** the first Phase 2 increment is the control law alone — rate estimation,
velocity-form PID, actuator-window allocation — in `src/TecnalHub/Services/Control/`, with
no wire, no telemetry, no UI, validated against a simulated first-order DOT plant with dead
time.

**Why:** it is the piece every other Phase 2 deliverable sits on, and the only one that
needs neither the bioreactor nor the then-open [D-008](DECISIONS.md) kLa-surface decision
(resolved later by P2-06). Risk
is front-loaded exactly as it was for Phase 0: the controller math is the genuinely unknown
part, so it goes first and is allowed to be proven or disproven cheaply, before a pixel of
the tuning UI or a byte of live actuation depends on it.

**Consequence:** the controllers are pure C# even though they live in the WPF app assembly
per [ARCHITECTURE.md](ARCHITECTURE.md#3-directory-layout); the test project already
references that assembly, so they are unit-tested directly. Moving them to a separate
no-WPF assembly later is a file move, not a redesign.

---

### P2-02 · The output is velocity-form; anti-windup is structural, not a clamped integrator

**Decided:** implement true velocity form — `du = ΔP + Ki·e·dt + ΔD`,
`Output = clamp(Output_prev + du, OutMin, OutMax)` — and let the clamped output *be* the
integrator. The reported `I` term is a separate bounded accumulator, held while the output
is railed, for the live display and for the operator's explicit `I_min`/`I_max` cap.

**Why:** the first attempt kept a separate clamped integral accumulator and fed *its change*
into `du`. That looked like it honoured "sliding-window integral + I_min/I_max saturation",
but it introduced a steady-state offset: when the accumulator hit its floor, `ΔI` went to
zero and the output froze above the level that actually held setpoint.

**Evidence:** the closed-loop cascade settled at **45.9 %** against a 30 % setpoint. Feeding
`Ki·e·dt` straight into `du` — the textbook incremental form — drove the steady-state error
to zero. Windup is then structurally impossible: there is no unbounded integrator state,
only the clamped output, so the proportional term pulls it off the rail the instant the
error reverses. A separate no-windup test confirms the output leaves an unreachable-setpoint
rail within one step once the target becomes reachable.

**Consequence:** `I_min`/`I_max` bound and display the integral contribution and let the
operator cap integral authority; the *binding* windup protection is the output saturation.
This is documented on the controller and in [UI_DESIGN §5.2](UI_DESIGN.md#52-controle)'s
terms, honestly rather than as a decorative clamp.

---

### P2-03 · Small `Kp`, because the prediction is most of the derivative

**Decided:** default `Kp = 0.25`, `Ki = 0.02`, `Kd = 0`, prediction horizon 25 s (≈ the
probe dead time), rate window 25 s.

**Why:** the prediction folds into the error — `e = SP − (DOT + rate·horizon)` — so the
proportional path also carries the loop's derivative action, with effective gain
`Kp·horizon`. The first guess (`Kp = 2`, horizon 40) was an effective `Kd ≈ 80`, which turned
the dead-time loop into a **sustained limit cycle** — the true DO swung 47 → 19 % while the
effort pinned at 100 %. Explicit `Kd` defaults to zero for the same reason: stacking a second
derivative on the prediction's is what destabilises it.

**Evidence:** with the corrected gains the cascade tracks 30 % on the simulator's placeholder
kLa within ~2 %, and a with-vs-without comparison shows the prediction horizon strictly
reduces overshoot on a 25 s-dead-time plant.

**Consequence:** these are **provisional simulator defaults**, stated as such in the code and
the roadmap. Real gains come from a bioreactor run; the placeholder kLa is not biology. The
tuning UI will expose every one of them.

---

### Phase 2 — what was deliberately NOT done in WP1

| Item | Why deferred |
|---|---|
| kLa surface evaluation + gradient-path allocation | At WP1 it was gated on [D-008](DECISIONS.md); P2-06 later resolved it as an in-app mapping workflow. It replaces the linear allocator, not the controller |
| Gain scheduling | Wanted, but not needed to prove the core law; a later WP |
| OUR soft sensor | Builds on the validated controller |
| Dosing subsystems + their synoptic positions | Separate WP; touch the UI and the wire |
| Tuning UI, `oxygen` detail-pane tabs, mode ownership, live actuation | The controller must be trusted before anything sends its output to a reactor |

---

### P2-04 · The tuning workspace is advisory: it computes on live telemetry, but never sends

**Decided:** wire the cascade into `Controle → Cascata e sintonia` through a `CascadeService`
that steps the controller on each dissolved-oxygen frame and shows the live terms, but does
**not** call `IDeviceService.Send`. Arming it starts the computation; nothing reaches the
wire.

**Why:** the tuning UI's whole job is to let the operator watch the cascade track a real
process and adjust it — which needs the loop running against live telemetry, not the loop
actuating. Actuation is the dangerous half and is gated on command ownership (`Automático`
mode) and a bioreactor. Splitting "compute and show" from "send" lets WP2 deliver the entire
tuning surface now, safely, and leaves the send path for the WP that also brings mode
ownership. It mirrors how `Automático` already ships disabled with a reason rather than
half-built.

**Evidence:** the service is asserted to leave the device's command capture empty across an
armed run (`An_armed_service_computes_actuation_and_never_sends`), and the workspace's apply
and save/load paths are all asserted to send nothing. A live simulator run opened straight
onto the tab, connected, streamed telemetry and rendered with zero XAML binding failures.

**Consequence:** `CascadeService` takes a `TimeProvider` so the loop's real-elapsed-time
step is deterministic in tests rather than depending on wall-clock spacing between pushed
frames. The `Trajetória kLa` contour ([D-008](DECISIONS.md)) and a live tuning chart are
shown as deferred rather than faked, the same discipline the health expander used in Phase 1b.

---

### P2-05 · Calibration follows v.6 ownership; pH control was a missing UI, not missing firmware

**Decided:** deliver the complete pH dosing state and the pH/O₂/airflow calibration
procedures together. Calibration and actuation remain separate: pH and oxygen curves live
in the app parser, accepted pH returns as quoted `pHCal` for the module display/controller,
and only the airflow curve belongs to dedicated firmware.

**Why pH was not controlled:** Phase 1b intentionally exposed only facts that had a complete
operator path. It already parsed, filtered, calibrated and echoed pH, but postponed the
five-field control surface. Reading v.6 and the firmware showed there was no protocol gap:
`pHSetpoint`, `pHError`, `pHOperation`, `pHMix` and `pHIntensity` were already explicit.
Leaving the value read-only in Phase 2 would therefore preserve a UI omission, not a safety
boundary.

**Safety decisions:** all five pH keys travel atomically; speed preserves `% × 10`; invalid
enabled state is refused instead of falling back to pH 7. Starting calibration first sends
the complete pH-off state and never re-arms it automatically. pH stability and final means
count distinct telemetry events, not repeated polls of one frame. Equal raw points are
refused rather than inheriting v.6's `slope=1` fallback. Flow point editing locks during
capture; a lost connection cancels acquisition and discards prepared command certainty.

**Evidence:** pure math, state and exact-wire tests bring the suite to 259/259. The updated
localhost HTTP simulator accepted one combined pH-control + quoted-echo + six-flow-coefficient
frame, returned its buffered `OK`, then accepted the explicit pH/flow safe-stop. Runtime
review at 1280×800 covered pH, oxygen and airflow in both themes; it found and fixed a clipped
flow capture button and a ScottPlot/list theme repaint defect. The final interval logged no
new XAML binding failure, fatal exception or unhandled exception. Screenshots are
`docs/evidence/ui/phase2-calibration-*.png`.

**Boundary:** no physical calibration was performed. Buffers, the O₂ standard, certified
flow reference, pump direction and real interlocks remain part of the bioreactor hardware
gate; v.6 remains the production fallback until that gate and a cultivation run close.

---

### P2-06 · D-008 is an in-app mapping experiment, not a bundled surface

**Corrected by the project owner:** the kLa feature is a dedicated window in which the
operator enters experimental `(Q_g,N,kLa)` values, the paper method estimates
`kLa(Q_g,N)`, and the gradient/headroom trajectory is constructed for actuator allocation.
The earlier proposal to ship a fitted surface and only evaluate it in C# is superseded.

**Evidence reviewed:** v.6 `kla_cascade_page.py` already exposes named profiles and an
interactive experimental-point editor before calculating its path. The manuscript project
defines the authoritative sequence as normalized surface reconstruction, normalized local
gradient, candidate start selection by maximum mean actuator headroom, bidirectional path
integration, low-to-high orientation and kLa-to-`(Q_g,N)` interpolation. The accompanying
kLa Path Control Lab preserves the same visible Map → Gradient → Headroom → Path chain.

**Operational decision:** production starts with no active map. A fit remains a draft until
it is reviewed and published as an immutable, versioned receipt; edits invalidate the
surface and all downstream results. Paper datasets may serve tests/examples but are never
silently selected. The live controller consumes only a published path and cannot fit or
mutate one while running.

**Roadmap consequence:** remaining v.6 parity is now ordered P0-P2. Field protocol closure,
alarms and exclusive command ownership precede automatic actuation; the D-008 mapping
workspace precedes live kLa-path control; v.6 dosing auxiliaries, OUR, biomass and the
external pump close before the new Receitas engine is allowed to become another command
source.

---

### P2-07 · The command arbiter is the single gate onto the wire

**Decided:** build WP4 as a `CommandArbiter` that decorates the transport wrapper and register
it as the `IDeviceService` the whole application resolves. A plain `Send` becomes a Manual
dispatch, so every existing call site routes through the arbiter without a line of change, and
nothing can reach the wire without an owner. The arbiter also exposes `ICommandArbiter` for the
ownership and lifecycle surface.

**Why the decorator, not a new dependency at each call site:** five view-models send today, and
a sixth control surface added in a later WP could quietly forget to ask the arbiter first. Making
the arbiter *be* the device service closes that hole structurally — there is no un-arbitrated
`Send` for anyone to call. Ownership is per `ActuatorId`, not a single global mode, because WP6
needs the cascade to own the oxygen actuators while the operator still holds pH; a global
`Manual/Automático/Receita` switch cannot express that.

**Safety decisions:** a frame touching an actuator owned by another owner is refused **whole** —
a partial send leaves the reactor in a state neither owner asked for. A link or feedback loss
revokes every non-Manual owner back to Manual and raises an alarm-severity event, and any command
still merely issued is timed out. Ownership transfers carry the last commanded state so a new
owner can start bumpless. The lifecycle refuses to over-claim: only aeration reaches
`TelemetryConfirmed`, because it is the one actuator whose applied setpoint the firmware echoes
(`FlowSetpoint`, with `FlowCommandAck`); temperature, agitation, oxygen, pressure and pH rest at
`TransportAccepted` and label the channel "sem eco" rather than pretend the wire confirmed them.
The pH-calibration echo (`pHCal`) stays a protocol-internal reflex below `IDeviceService` and is
intentionally unowned, so it is never blocked by pH-dosing ownership.

**Session clock:** the `TimeOffsetMinutes`/`ZeroTime` math already sat in `SensorReadings` from
Phase 0, unused. WP4 wired the operator path — `ConnectionManager.ZeroSessionTime` posts a
request handled on the worker thread (the readings are the worker's to own), which zeroes the
local offset and echoes it back for the header and the journal. The device clock and samples
already logged are never touched.

**Evidence:** 280/280 tests (21 new) cover atomic refusal, journalled bumpless transfer, the full
lifecycle including timeout and the aeration-only confirmation, safe abort on link loss, and the
session-clock rebase at both the `SensorReadings` and `ConnectionManager` levels. A live localhost
simulator run connected over Wi-Fi, rendered the first frame in 1210 ms, streamed telemetry
through the arbiter, showed the `Zerar` button enabled, and logged zero XAML binding failures or
exceptions (`docs/evidence/ui/phase2-wp4-arbiter-painel.png`).

**Boundary:** this is WP4 part 1. The exit criterion's *latched, acknowledgeable* alarm and the
timed audible silence arrive with the operational alarm engine; the safe abort produces a
journalled event today, not yet an ackable latched alarm. The remaining Phase 0 link cleanup
(busy-port handling, WMI/CH343 ranking, immutable snapshots, configured poll period, round-trip
naming) is also still open. No automatic subsystem can send until the whole gate passes on the
bioreactor.

---

### P2-08 · WP5 reproduces the paper method; headroom includes the RK45 sampling policy

**Decided:** implement D-008 as a pure managed scientific pipeline and a dedicated main
destination (`Ctrl+8`, the eighth currently implemented destination). Production begins with no map: the operator creates a named experiment, declares
the physical `Q_g/N` domain, enters measured triples and moves explicitly through
`Rascunho → Superfície estimada → Trajetória válida → Revisada → Publicada`. The 3² helper
creates coordinates only; it never inserts a paper kLa value.

**Numerical contract:** the executable oracle is the current paper script
`analysis/1_kla_mapping_gradient/gradient_path_score.py`, not the older February workbook.
The managed sequence is 300² Clough–Tocher, nearest fill, Gaussian `sigma=5`, zero-smoothing
not-a-knot bicubic spline, normalized derivatives, a 150² candidate scan, bidirectional
Dormand–Prince RK45, mean boundary headroom, low-to-high orientation and strictly increasing
kLa allocation. The paper takes a plain mean over the adaptive solver's returned points;
therefore SciPy's initial-step selection, RMS error norm, step-growth policy and dense event
location are reproduced as part of the algorithm. Before that alignment, surface values
matched while a 7² search selected the wrong start — a useful example of why a plausible plot
is not a scientific fixture.

**Operator decisions:** every reference parameter is visible. A fast preview or any custom
value may calculate, but the identity changes to `Método parametrizado` and review/publication
is refused until all reference values are restored. Incomplete or temporarily invalid table
cells can be saved exactly as draft text but never enter a calculation. Surface/path work runs
off the dispatcher with progress, cancellation and stale-input fingerprint rejection.

**Publication boundary:** receipts are create-only, versioned JSON carrying inputs, method,
diagnostics, allocation and surface/path fingerprints. Their SHA-256 is verified on load,
read, export and import; export is byte-for-byte. Import always creates a new local draft and
requires recomputation/review. The fitting layer has no command-service dependency and
publication does not activate a profile. WP6 is the first consumer and must still acquire
automatic ownership before it can command.

**Evidence:** managed-vs-SciPy fixtures cover surface values, normalized derivatives, fixed
path endpoints/headroom and the full 150² candidate selection with declared tolerances. The
fresh SciPy 1.17.1 oracle selects `(q,n)=(0.5972959732,0.6308463087)` with
`H=0.2024300198`; the managed result agrees to `1e-10`. Draft round-trip,
receipt versioning, byte identity, tamper refusal, blank-install and custom-publication refusal
are headless tests. The complete 150² managed search takes about 3.2 s on the development
machine. The reproducible oracle generator and precise operator/scientific contract are in
`tools/kla-reference/` and [KLA_MAPPING.md](KLA_MAPPING.md).

**Boundary:** this is an implementation-parity result on the paper dataset, not biological
validation of a new broth. WP5 neither activates nor sends the allocation, and the bioreactor
hardware/cultivation gate remains open for WP6.

---

### P2-09 · The operational alarm engine latches, acknowledges and silences on a timer

**Decided:** implement WP4's six system alarms as one `AlarmService` of small per-alarm state
machines — on-delay to raise, latch, acknowledge, off-deadband to clear — driven by an injected
`TimeProvider` and polled from the shell's existing 1 Hz tick plus every device/arbiter event.
The audible indication lives behind `IAlarmAnnunciator`, so the whole silence policy is tested
without making a sound.

**Why the shell tick, not a timer in the service:** the service must evaluate the clock even when
no telemetry arrives — link loss, on-delay expiry and the audio-silence timeout are all
time-based. Reusing the shell's `DispatcherTimer` (which already exists for staleness) keeps the
service free of a `Dispatcher` dependency and therefore trivially testable: `Poll()` is public and
tests call it directly after advancing a `TestClock`.

**Safety decisions:** an alarm latches and does not auto-clear — a fault that comes and goes while
unacknowledged is held in the returned-unacknowledged state, because an alarm nobody saw is the
one worth keeping. The audible is a **timed** silence, never a permanent mute, and a freshly-raised
alarm resets the silence so a second fault cannot hide behind a silence taken for the first. Each
condition is a signal that already exists — link state, `SensorCommOK`, `FlowmeterOnline` gated on
flow being in use, the three-period staleness clock, the oxygen sentinel, and the [D-015](DECISIONS.md)
command lifecycle's `TimedOut` — rather than a new bespoke detector.

**Evidence:** 303/303 tests (14 new) cover the latch/ack/deadband machine, returned-unacknowledged,
the silence expiry and the re-sound through silence, all six conditions, and the exit criterion that
one link loss yields exactly one latched alarm. A live simulator run connected, then the simulator
was killed: the link faulted, the `Link perdido` banner latched with its `Silenciar` and `Reconhecer`
actions, every reading degraded to an em dash, and no XAML binding failure or exception was logged
(`docs/evidence/ui/phase2-wp4-alarms-painel.png`).

**Boundary:** the operator surface is the shell banner. The full **Alarmes** page and configurable
per-variable HH/H/L/LL limits with the persistent-foam alarm are Phase 5 on this same engine. The
Phase 0 P2/P3 link cleanup (busy-port handling, WMI/CH343 ranking, immutable snapshots, poll period,
round-trip naming) is the one WP4 item still open, and it is hygiene rather than a gate on WP6.

---

### P2-10 · The cascade takes the wire through the arbiter, on the published path

**Decided:** implement WP6 as a change of *allocation* plus an ownership handshake, leaving the
velocity-form controller ([P2-01](#p2-01)) untouched. A `CascadeAllocation` abstraction maps the
control effort to the two actuators; `KlaPathAllocation` reads the published receipt's monotonic
table, and two single-actuator fallbacks cover the v.6 agitation-only / aeration-only modes.
`CascadeService` gains a live role on top of its advisory one: `Engage` claims the O₂ actuators
through the arbiter and dispatches the combined frame each step; `Arm` still only computes.

**Why sequence it after the alarm gate:** the operator directed "do the WP4 alarm gate first,"
and it is the right order — a loop that actuates unattended needs the latched, acknowledgeable
alarms to surface a fault. With [D-016](DECISIONS.md) shipped, live actuation is unblocked; the
only remaining gate is the bioreactor itself.

**Bumpless is an inverse, not a guess.** The controller is preloaded to the effort that
reproduces the actuator the operator left running, found by bisecting the allocation (agitation
is monotonic in effort for the path and the agitation-only mode; aeration for the aeration-only
mode). A test pins that engaging on the path at the operator's 440 rpm / 5 L/min reproduces those
values on the first automatic frame rather than snapping.

**Safe abort is layered and tested:** three blind oxygen frames, an arbiter ownership revocation
on link loss, or a manual `ReturnToManual` each disengage the cascade and hand the actuators back.
Because Engage sets the engaged flag after `Claim` and Disengage clears it before `Release`, the
service's own ownership events never masquerade as an external takeover.

**Evidence:** 319/319 tests (16 new) cover the path allocation and its inverse, the modes, the
per-frame dispatch under Automatic ownership, the bumpless transfer, and each safe-abort trigger,
plus the advisory-never-sends regression. A live simulator run reached `Controle → Cascata e
sintonia`, rendered the Automático card, and correctly gated the engage button until a published
map is selected, with zero binding failures
(`docs/evidence/ui/phase2-wp6-cascade-automatic.png`).

**Boundary:** part 1 is the actuation engine and its workspace controls. The oxygen detail-pane
`Cascata`/`PID`/`Saída` tabs and the live PV/SP/kLa/output chart are part 2. Simulator actuation
is not biological validation; the bioreactor run — arm, track, manual takeover, feedback timeout,
safe abort against a real process — remains the hardware gate that closes WP6.

---

### P2-11 · The cascade becomes observable: detail tabs and a live tuning chart

**Decided:** close WP6 by making the loop observable without touching how it is controlled.
Oxygen — the one device with an app-side controller the app can watch — gains its
`Cascata`/`PID`/`Saída` detail-pane tabs, and `Controle → Cascata e sintonia` gains the live
PV/SP/kLa/output chart. Both are read-only; tune, mode and engage stay where they were.

**Where the cascade data comes from.** The detail pane binds to the selected `SubsystemViewModel`,
which is generic and has no cascade state, so a small `CascadeDetailViewModel` wraps the service
and the pane reaches it through the window's shell `DataContext` — exactly how it already reaches
the telemetry history. Only oxygen turns the tabs on (`HasCascade`/`HasPid`), so no other device
shows them.

**The chart is a ring, not a binding.** ScottPlot is not in WPF's data-binding tree, so — like
`TrendSpark` — `CascadeChart` reads a snapshot on a 1 Hz timer and redraws, theme-aware. The
source is `CascadeTrend`, a fixed-capacity ring the service fills on every armed step (advisory
or engaged), so a long run cannot grow memory. The kLa series is sparse — it exists only while
engaged on the path — so it carries its own x-coordinates and is drawn against a second right
axis in per-hour units rather than crammed onto the percent scale.

**Placement.** The chart moved to the top of the tuning column: it is the element the operator
watches while adjusting the fields below it, so burying it under them was the wrong order.

**Evidence:** 328/328 tests (9 new) cover the ring (relative timing, the sparse kLa series,
capacity, clear), the service recording one sample per armed frame and clearing on disarm, and
the detail view's three states and change notifications. A live simulator run plotted PV/SP/effort
with the advisory loop computing, and the oxygen detail pane rendered the four tabs with the
Cascata overview, both with zero binding failures
(`docs/evidence/ui/phase2-wp6-tuning-chart.png`, `docs/evidence/ui/phase2-wp6-oxygen-detail.png`).

**Boundary:** this closes WP6's software scope. The bioreactor run — arm, track, manual takeover,
feedback timeout and safe abort against a real process — remains the hardware gate, and the WP4
Phase-0 link hygiene is the one Phase-2 P0 item still open.

---

### P2-12 · The cultivation auxiliaries: three owned pumps and an unowned foam sensor

**Decided:** deliver WP7 as four dosing ViewModels mirroring the WP3 pH card — nutrient, antifoam,
the level/foam sensor and the flask agitator — with the three that actuate becoming owned arbiter
actuators and the sensor staying unowned. See [D-018](DECISIONS.md).

**"All three" means the three that actuate.** Nutrient, antifoam and the flask agitator each become
an `ActuatorId`, so a plain Manual `Send` owns them, they get lifecycle tracking and they join the
global safe-stop. The level/foam sensor keys (`distanceSensorComm`, `distanceSensorReference`,
`foam*`) map to `null` like the calibration keys, because a stop that disabled the sensor to "stop"
would blind foam monitoring — the exact opposite of safe. `Parada segura` therefore zeroes the
antifoam pump but leaves the sensor running, and the foam card applies on its own rather than
through the bulk `Aplicar alterações`. Verified in `ControlViewModelTests`: the safe-stop JSON
carries `nutriIntensity:0`, `antifoamIntensity:0` and `agitatorOn:0` but no `distanceSensorComm`.

**Intensity is raw, not `× 10` — this was the trap.** pH sends `pHIntensity = percent × 10`
(0-990), and copying that pattern onto nutrient and antifoam would have dosed at a tenth of the
commanded speed. PROTOCOL.md §3.3 gives antifoam's range as 0-99 with no scaling, so the builders
send the raw percent and `DosingAuxiliariesTests` pins `antifoamIntensity:45.0` for a 45% command.

**The agitator's sign is split off before the wire.** `CommandBuilders.FlaskAgitator` takes the
operator's signed percent and emits magnitude (`agitatorPercent`) and direction (`agitatorDir`,
1 CW / 0 CCW) separately. The test asserts `-75` → `agitatorPercent:75.0, agitatorDir:0` and that
the substring `-75` never appears. The slider (0-100) and the text entry stay in step through a
guarded sync so neither fights the other.

**Commanded-only stays honest.** Nutrient and the agitator have no telemetry; the nutrient tile
shows a commanded duty cycle tagged `comandado`, pushed from the ViewModel's applied state exactly
as `motor` is. Antifoam (`Antifoam`) and level (`Distance`) show their live figures. The read-only
detail pane gained a per-variable note so each points at its Controle card rather than repeating
pressure's "measured but uncontrolled" line.

**Evidence:** 350/350 tests (22 new) — the frozen frames and safe-stops, the actuator map (three
owned, foam free), a recipe-owned nutrient refusing a Manual frame while foam config passes, the
four ViewModels, and the two `ControlViewModelTests` integration checks. A live simulator run
reached the Controle page over the localhost Wi-Fi simulator and rendered the four cards and the
three synoptic tags with zero binding failures (first frame 1452 ms).

**Boundary:** software and simulator only. Actuating the real nutrient/antifoam pumps and the
agitator is part of the standing bioreactor gate; nothing here is biologically validated.
