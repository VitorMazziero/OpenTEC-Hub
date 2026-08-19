# Phase 0 — Hardware Validation Results

> **Date:** 2026-08-19 · **Status:** USB validated · Wi-Fi pending
> **Hardware:** ESP32-S3 on COM3 (`USB-Enhanced-SERIAL CH343`, wch.cn, VID_1A86/PID_55D3)
> **Configuration:** board alone, **no bioreactor module attached** — firmware-level test only
>
> **Docs:** [ROADMAP](ROADMAP.md) · [PROTOCOL](PROTOCOL.md) · [MIGRATION](MIGRATION.md) · [DECISIONS](DECISIONS.md)

---

## Verdict

**The core Phase 0 risk is retired.** C#'s `System.IO.Ports` reproduces pyserial's
DTR/RTS pulse and 1.8 s boot-settle behaviour exactly; the device completes the
`{"comTest":1}` → `OK` handshake on the first attempt, every attempt.

The parts of Phase 0 still outstanding are listed under [What is not yet
validated](#what-is-not-yet-validated).

---

## Measured results

Final run: `tecnal-harness usb --probe --for 60`

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

---

## What the hardware taught us

Four findings, all of which changed the code. None of them were visible from reading
the Python source.

### 1. Not every line is telemetry — `OK` and `[ESP32_` share the stream

On USB the command acknowledgement comes back on the **same serial stream** as
telemetry, and the device interleaves human-readable log lines:

```text
[ESP32_AVISO]: Falha de leitura UART do Módulo TECNAL após comando 'b'
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

Those were display-only; the wire itself was safe because `TecnalCommand` formats
invariantly by construction. But it is exactly the slip that breaks the firmware
parse when it happens in a command builder, and it happened within an hour of writing
the rule down.

*Fixed:* every formatting site swept; `CultureInvarianceTests` forces `pt-BR`
explicitly rather than trusting the CI machine's locale.

---

## Deliverables status

| Deliverable | Status |
|---|---|
| `ITransport`, `SerialTransport`, `HttpTransport` | Done |
| `TecnalCommand` — invariant by construction | Done |
| `TelemetryParser` — key-by-key port | Done |
| `SpikeFilter` — direct port, same thresholds | Done |
| `ConnectionManager` — async state machine | Done |
| Console harness | Done |
| Golden-string tests, incl. pt-BR culture | Done — **49 tests, all passing** |
| USB verified on hardware | **Done** |
| Wi-Fi verified on hardware | Not started |
| Byte-comparison against a v.6 capture | Not started |

---

## What is not yet validated

Phase 0 is **not** complete. Three items remain, and two need the bioreactor.

1. **Wi-Fi transport against hardware.** Untested. The ETag/304 path and the
   fresh-client-per-connect behaviour are ported but unproven.
2. **Byte-comparison against v.6.** The exit criterion is that both apps emit
   identical command bytes for the same actions. Our side is captured
   (`harness-trace-*.log`); the v.6 side needs a capture from `command_logs/`.
3. **Telemetry with real sensors.** Everything read so far was a sentinel. The
   calibration path, spike filters, and the `pHCal` echo-back are covered by unit
   tests but have never seen a live probe.

Items 2 and 3 need the bioreactor connected. Item 1 does not — it only needs the
board in Wi-Fi mode.

---

## Reproducing

```bash
dotnet run --project src/TecnalHub.Harness -- ports
```

```bash
dotnet run --project src/TecnalHub.Harness -- usb --probe --for 60
```

```bash
dotnet test tests/TecnalHub.Tests
```

Each session writes `harness-trace-<timestamp>.log`: one record per line,
`ISO8601 TAB direction TAB payload`, for diffing against v.6 captures.
