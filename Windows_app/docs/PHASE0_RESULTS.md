# Phase 0 — Hardware Validation Results

> **Date:** 2026-08-19 · **Status:** USB and Wi-Fi both validated
> **Hardware:** ESP32-S3 — USB on COM3 (`USB-Enhanced-SERIAL CH343`, wch.cn, VID_1A86/PID_55D3);
> Wi-Fi on the `Modulo_OpenTEC_1` SoftAP (client 192.168.4.2, gateway 192.168.4.1)
> **Configuration:** board alone, **no bioreactor module attached** — firmware-level test only
>
> **Docs:** [ROADMAP](ROADMAP.md) · [PROTOCOL](PROTOCOL.md) · [MIGRATION](MIGRATION.md) · [DECISIONS](DECISIONS.md)

---

## Verdict

**Both transports work against real hardware.**

- **USB:** C#'s `System.IO.Ports` reproduces pyserial's DTR/RTS pulse and 1.8 s
  boot-settle behaviour exactly; the handshake succeeds on the first attempt, every
  attempt.
- **Wi-Fi:** 20 of 21 checks passed with zero warnings on the first run. The single
  failure was an over-strict assertion in the test itself, and it caught a real
  firmware behaviour — see [finding 5](#5-wi-fi-serves-the-handshake-ack-on-readdata).

What remains is listed under [What is not yet
validated](#what-is-not-yet-validated) — chiefly that every reading so far has been a
sentinel, because no bioreactor was attached.

---

## Measured results

Final run: `opentec-harness usb --probe --for 60`

| Metric | Result |
|---|---|
| Connect attempts | **1** (no retries) |
| Telemetry frames in 60 s | **32** (~2.0 s period, matching `dataDelay: 2000`) |
| Parse failures | **0** |
| Commands sent | 2 (no-op probe) |
| Command acks recognised | 1 |
| Device log lines | 1 |
| Spurious reconnects | **0** |

**Auto-discovery with no port specified: 1.9 s.** COM1 and COM3 are probed
concurrently, so the wall-clock cost is one boot settle rather than the sum. v.6
probes serially and would need roughly 12 s for the same machine — one 1.8 s settle
plus up to 8.5 s of handshake timeouts on COM1 before even reaching COM3.

Expected with no bioreactor attached, and correctly represented: `SensorCommOK:false`,
all probe channels at the not-received sentinel, and the ESP32 reporting internal UART
failures to the absent module.

### Wi-Fi (`opentec-harness wifi-test`)

| Check | Result |
|---|---|
| SoftAP association | 192.168.4.2, gateway 192.168.4.1, ICMP 1 ms |
| `/ping` | 200, body `pong` |
| `/readData` | 200, JSON, ETag `"496"` |
| `/command` | 200, body `OK` |
| Conditional poll (`If-None-Match`) | **304 as expected** |
| Handshake at v.6 default timeouts | Succeeded (250 ms connect / 750 ms read) |
| Round-trip latency, 20 x `POST /command` | min 5 · median 9 · **p95 33** · max 33 ms, 20/20 |
| 90 s soak | **45 frames, 0 parse failures, 1 connect attempt** |
| Frame cadence | ~2.1 s — 100% of the expected count, none dropped |
| Time to first frame | 1.1 s |
| Disconnect / reconnect | Telemetry resumed; ETag correctly cleared |
| Endpoint discovery | Only `/ping`, `/readData`, `/command` exist; everything else 404 |

The v.6 timeouts have roughly **15x headroom** over the observed p95 on this link.
They are tight, but they are not wrong — no reason to change them.

---

## What the hardware taught us

Five findings, all of which changed the code. None were visible from reading the
Python source.

### 1. Not every line is telemetry — `OK` and `[ESP32_` share the stream

On USB the command acknowledgement comes back on the **same serial stream** as
telemetry, and the device interleaves human-readable log lines:

```text
[ESP32_AVISO]: Falha de leitura UART do Módulo OpenTEC após comando 'b'
[ESP32_AVISO]: Limite de falhas UART atingido; reinicializando UART e iniciando cooldown de 5s
```

A reader that assumes "every line is JSON" counts these as corruption. v.6 does
exactly that for `OK`, and survives only because it needs three *consecutive* parse
failures and telemetry usually interleaves — **sending several commands in quick
succession can trip a false link-loss in v.6.**

*Fixed:* `ParseOutcome.DeviceLog` and `ParseOutcome.CommandAck` are first-class
outcomes. Parse failures went from 1 per probe to 0.

### 2. Silence detection must be a duration, not a count of empty reads

The first bench run reconnected every ~4 seconds in a loop. Cause: silence was
detected after 4 empty reads at a 250 ms poll — **1 s** — against a device that emits
every **2 s**. Guaranteed false positive.

v.6 counts empty reads too, and gets away with it only because its Qt timer happens
to poll slowly. That couples failure detection to the poll rate: tuning the poll
faster silently makes the link look dead sooner.

*Fixed:* `ConnectionOptions.UsbSilenceTimeout`, a `TimeSpan` (default 8 s ≈ four
missed frames). Any traffic at all — including a log line — counts as liveness.

### 3. A USB reconnect reboots the board and discards its state

The churn in finding 2 exposed something worse. Every reconnect trace showed the
device clock returning to the same boot value:

```text
18:06:38  rx  time=2.8 ...
18:06:42  rx  time=2.8 ...
18:06:46  rx  time=2.8 ...
```

The DTR/RTS pulse on connect is a hardware reset, so **each reconnect reboots the
ESP32 and discards its process state, including any applied setpoints.** A spurious
link-loss mid-cultivation would not merely blink an icon — it would reset the
controller.

*Made explicit:* `SerialTransportConfig.PulseResetOnConnect`, default `true` to match
v.6. Suppressing it on *recovery* (while keeping it for the initial connect) is a
strong candidate for Phase 1, but needs hardware validation first — without the pulse
a genuinely wedged board may never come back. Tracked as
[PROTOCOL.md](PROTOCOL.md#5-open-questions-for-hardware-verification) Q6.

### 4. The culture hazard is real, and it appeared in our own code

Running the harness under the machine's real pt-BR locale immediately produced
`t=0,05min` and `Found ESP32 in 1,9 s` — from interpolated `{value:F2}` holes that
silently use the current culture.

Those were display-only; the wire itself was safe because `OpenTECCommand` formats
invariantly by construction. But it is exactly the slip that breaks the firmware
parse when it happens in a command builder, and it happened within an hour of writing
the rule down.

*Fixed:* every formatting site swept; `CultureInvarianceTests` forces `pt-BR`
explicitly rather than trusting the CI machine's locale.

### 5. Wi-Fi serves the handshake ack on `/readData`

The one FAIL in the Wi-Fi run:

```text
[PASS] read returned a frame after 2 attempt(s)
     OK
[FAIL] frame did not parse as telemetry (outcome: CommandAck)
```

The device appears to serve `/readData` from a shared response buffer, so the first
`GET /readData` after a `POST /command` returns that command's `OK` rather than a
telemetry frame. Since the Wi-Fi handshake *is* a `POST /command`, this happens on
**every** connect:

```text
POST /command {"comTest":1}   -> 200 "OK"      (handshake)
GET  /readData                -> 200 "OK"      <- buffered ack, not telemetry
GET  /readData                -> 200 {"Time":...}
```

Finding 1 had already made the parser treat `OK` as `CommandAck` rather than
corruption, so the transport handled this correctly and the 90 s soak recorded
**zero** parse failures. Only the test's assertion — "the first frame after connect
must be telemetry" — was too strict.

*Fixed:* the assertion now reads until telemetry arrives and reports the ack as
expected traffic. Documented in [PROTOCOL.md](PROTOCOL.md#20-not-every-line-is-telemetry).

### Bonus: Wi-Fi reconnects do not reboot the board

The device clock advanced monotonically across the disconnect/reconnect cycle
(996.6 → 998.9 → 1000.6 s), where USB resets to its boot value every time. There is
no reset line to pulse over Wi-Fi.

That makes Wi-Fi the **safer transport for mid-run recovery** — a reconnect does not
discard setpoints. Worth weighing when finding 3 is resolved.

---

## Post-validation fixes (P1)

Two defects found by reviewing the implementation after the hardware runs. Neither
was visible in the bench results, because neither transport misbehaved during them.

### Wi-Fi had no silence detection at all

The check was gated on the medium:

```csharp
if (transport.Medium == TransportMedium.Usb &&      // Wi-Fi never reached
    now - lastTraffic > UsbSilenceTimeout)
```

The reasoning was that Wi-Fi returns null for every 304 and so cannot distinguish
"unchanged" from "silent". That was wrong. A *hard* Wi-Fi failure does surface (the
read throws `TransportFaultException`), but a **soft** one does not: if the ESP32's
web server stays up while its telemetry task stalls, it answers 304 forever and the
app would sit indefinitely on frozen readings behind a healthy "Connected" indicator.

For a control application, stale data presented as live is a worse failure than an
honest disconnect.

*Fixed:* `TelemetrySilenceTimeout` now applies to both transports, and is measured
against the last **parsed telemetry frame** rather than against any traffic - device
log lines must not mask stalled telemetry.

### The heartbeat was configured but never ran

`HeartbeatInterval` was declared and `TestConnectionAsync` implemented on both
transports, but nothing ever called it. v.6 probed every second; the behaviour was
dropped during the port.

*Fixed:* liveness detection is now two-stage.

| Silence | Behaviour |
|---|---|
| under `LivenessProbeAfterSilence` (4 s) | nothing - no probe traffic at all |
| 4 s to `TelemetrySilenceTimeout` (8 s) | probe the transport on that cadence; a failed probe drops the link immediately |
| over 8 s | drop the link **even if the probe still succeeds** - this is the stalled-telemetry case |

The staging matters: a healthy link generates **zero** extra traffic, which was
verified on hardware (45 s USB run, 22 frames, `liveness probes: 0`). The device
shares one UART with the sensor module, so needless chatter is not free.

*Tested:* `ConnectionManagerTests` drives the state machine against a fake transport -
14 tests covering silence on both media, probe failure, command coalescing, requeue
after a rejected write, reconnect cycling and read faults. The Wi-Fi silence test was
confirmed to **fail** against the previous implementation before the fix was kept.

---

## Deliverables status

| Deliverable | Status |
|---|---|
| `ITransport`, `SerialTransport`, `HttpTransport` | Done |
| `OpenTECCommand` — invariant by construction | Done |
| `TelemetryParser` — key-by-key port | Done |
| `SpikeFilter` — direct port, same thresholds | Done |
| `ConnectionManager` — async state machine | Done |
| Console harness | Done |
| Golden-string tests, incl. pt-BR culture | Done — **49 tests, all passing** |
| USB verified on hardware | **Done** |
| Wi-Fi verified on hardware | **Done** |
| Byte-comparison against a v.6 capture | Not started |

---

## What is not yet validated

Phase 0 is **not** complete. Two items remain, and both need the bioreactor.

1. **Byte-comparison against v.6.** The exit criterion is that both apps emit
   identical command bytes for the same operator actions. Our side is captured
   (`harness-trace-*.log`); the v.6 side needs a capture from `command_logs/` of the
   same actions.
2. **Telemetry with real sensors.** Every reading so far has been a sentinel
   (`SensorCommOK:false`, all probes at -1). Never yet exercised against live data:
   - the oxygen and pH **calibration** path,
   - the **spike filters** (thresholds are in raw ADC counts),
   - the **`pHCal` echo-back**, which is the only place the app writes a *string* to
     the wire and therefore the highest-risk culture site,
   - the flow, valve and biomass telemetry keys, which the firmware does not emit
     at all with no module attached.

Both are covered by unit tests against synthetic data; neither has met the real
instrument.

---

## Reproducing

```bash
dotnet run --project src/OpenTECHub.Harness -- ports
```

```bash
dotnet run --project src/OpenTECHub.Harness -- usb --probe --for 60
```

```bash
dotnet test tests/OpenTECHub.Tests
```

Each session writes `harness-trace-<timestamp>.log`: one record per line,
`ISO8601 TAB direction TAB payload`, for diffing against v.6 captures.
