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

*Not started.*
