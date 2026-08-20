# Device Simulator

> A stand-in for the ESP32-S3 and the bioreactor behind it, so the application can be
> developed and tested without hardware — and so the cascade controllers can be tuned
> against a process that responds before one is trusted with a real cultivation.
>
> **Docs:** [ROADMAP](ROADMAP.md) · [PROTOCOL](PROTOCOL.md) · [PHASE0_RESULTS](PHASE0_RESULTS.md) · [ARCHITECTURE](ARCHITECTURE.md)

---

## 1. Why

Two distinct needs, and the second is the more important one.

**Development without hardware.** The board lives on a bench that is not always
reachable, and with no bioreactor attached every reading is a sentinel. Charts,
parsing, validation, alarm states and the whole UI cannot be exercised against `—`.

**A bench for the scientific core.** A cascade controller cannot be tuned against a
board with no vessel. The velocity-form PID, the anti-windup, the 30–60 s prediction
horizon and the kLa gradient path all need a process that *responds* — with the right
dead time and the right coupling between agitation, aeration and dissolved oxygen —
before any of it is trusted with a real cultivation. The manuscript already specifies
that model, so the physics is not guesswork.

> The simulator is not test scaffolding. For Phase 2 it is the instrument the control
> work is developed on.

---

## 2. What it is not

It does **not** simulate the TECNAL sensor module, and it does not sit behind a real
ESP32.

That topology — app → real ESP32 → simulated module — was considered and rejected:

- The ESP32-S3's module link is a **hardware UART on GPIO pins**, physically separate
  from the USB CDC interface the app uses. Reaching it needs a USB-TTL adapter wired
  to those pins; **virtual COM port software cannot bridge to it**, because it only
  creates PC-internal port pairs.
- The module-side protocol is undocumented and entirely different from the PC-side
  one. All that is known is that it uses single-character commands — visible in
  `[ESP32_AVISO]: Falha de leitura UART do Módulo TECNAL após comando 'b'`.
- It would exercise the **firmware**, which is frozen and already works, rather than
  the app, which is what needs testing.

The simulator therefore replaces the ESP32 itself, speaking the documented PC-side
protocol.

---

## 3. Topologies

```
 Level 1  in-process            ┌─────────┐
          (unit tests)          │   app   │──▶ FakeTransport
                                └─────────┘        already in tests

 Level 2  localhost HTTP        ┌─────────┐        ┌───────────┐
          NO SETUP REQUIRED     │   app   │──HTTP─▶│ simulator │
                                └─────────┘  127.0.0.1:8080    │
                                                    └───────────┘

 Level 2b virtual serial pair   ┌─────────┐        ┌───────────┐
          needs com0com         │   app   │─COM10⇄COM11────────▶│ simulator │
                                └─────────┘        └───────────┘
```

**Level 2 (HTTP on localhost) is the default**, because the app's Wi-Fi transport
already speaks HTTP to an address — pointing it at `127.0.0.1` needs no driver, no
administrator rights and no reboot.

**Level 2b (virtual serial pair)** exists to exercise the *serial* stack specifically:
line framing, the drain-to-newest read, the `OK` ack sharing the stream. It requires a
one-time com0com install — see [section 6](#6-virtual-serial-pair-setup).

> Worth noting: this only became clean once the DTR/RTS reset pulse was removed
> ([PHASE_LOG](PHASE_LOG.md) P0-09). Virtual ports do not meaningfully emulate control
> lines, so v.6's pulse plus 1.8 s settle would have made the serial path awkward.
> Connect is now 13 ms with no line manipulation at all.

---

## 4. Fidelity: the quirks are the point

A simulator that only implements the happy path tests nothing worth testing. These
behaviours were **measured on real hardware** ([PHASE0_RESULTS](PHASE0_RESULTS.md))
and are reproduced deliberately:

| Behaviour | Why it matters |
|---|---|
| `OK` ack shares the **serial** stream with telemetry | A reader assuming "every line is JSON" counts it as corruption. v.6 does. |
| `GET /readData` returns the buffered `OK` right after a `POST /command` | Happens on **every** Wi-Fi connect, because the handshake is a POST |
| `[ESP32_AVISO]: …` log lines interleaved with telemetry | Must be recognised, not treated as a parse failure |
| `/ping` answers `pong`; unknown paths answer 404 `Not found` | Matches the firmware's actual surface |
| ETag on `/readData`, `304` for `If-None-Match` | If the cached tag is not cleared on reconnect, telemetry silently stops |
| Absent sensors emit `-1` sentinels, `SensorCommOK:false` | Exactly the state of a board with no module attached |
| Raw ADC counts, not engineering units | Exercises the calibration and spike-filter path, which is where a wrong coefficient hides |
| Telemetry every `dataDelay` ms, honouring the command | The app's silence timeout must be derived from this |

**Raw counts matter.** The device sends ADC counts and the *app* calibrates. The
simulator therefore inverts the field calibration to emit counts that decode back to
the intended engineering value:

```text
Oxyval = (oxygen% + 25.09136520919) / 0.0305473419314     40 %  ->  ~2131
pHval  = (pH      +  0.600385955239) / 0.0005012405704     7.00 -> ~15163
```

A simulator that emitted `40` directly would leave the entire calibration path
untested — and a wrong coefficient is precisely the kind of defect that survives to
the lab.

---

## 5. Usage

```bash
dotnet run --project src/TecnalHub.Simulator -- http
```

Then point the app at `127.0.0.1` (connection chip → Wi-Fi → address). No setup.

```bash
dotnet run --project src/TecnalHub.Simulator -- serial COM11
```

Serial mode, where `COM11` is one half of a virtual pair; the app connects to the
other half.

### Options

| Option | Effect |
|---|---|
| `--scenario <name>` | Start in a named scenario (see below) |
| `--port <n>` | HTTP port (default 8080) |
| `--data-delay <ms>` | Initial telemetry period (default 2000) |
| `--no-module` | Start with the sensor module offline, as the bare board does |
| `--quiet` | Suppress the per-frame console echo |

### Scenarios

Fault injection, switchable at runtime by typing the name:

| Scenario | What it does |
|---|---|
| `normal` | Healthy process, all sensors reporting |
| `no-module` | `SensorCommOK:false`, all probes at sentinel — a bare board |
| `stall` | Keeps answering `/ping` but stops updating telemetry. **The failure the Wi-Fi silence timeout exists to catch** |
| `dropout` | Link disappears entirely, to exercise reconnect |
| `spikes` | Injects single-sample outliers, to prove the spike filter holds |
| `noise` | Heavy measurement noise on every channel |
| `drift` | Slow calibration drift, to test long-run charts |
| `garbage` | Emits malformed lines, to exercise the parse-failure path |

---

## 6. Virtual serial pair setup

Only needed for Level 2b. HTTP mode requires none of this.

1. Install [com0com](https://sourceforge.net/projects/com0com/) — free, open source.
   Use a **signed** build, or Windows will refuse the driver.
2. In its setup GUI, create a pair and name the two ends `COM10` and `COM11`.
3. Run the simulator on one end, point the app at the other:

```bash
dotnet run --project src/TecnalHub.Simulator -- serial COM11
```

Then connect the app to `COM10`.

> **Do not** pick port numbers that already exist. `COM1` and `COM3` are taken on this
> machine; `COM10`/`COM11` are safe.

---

## 7. Process model

Phase 1 ships a first-order model — enough to make charts move, validation fire and
setpoint changes visibly take effect.

| Variable | Model |
|---|---|
| Temperature | First-order approach to setpoint, plus ambient loss and jacket lag |
| Flow | First-order approach to the commanded setpoint |
| Pressure | Rises with flow against a vessel restriction |
| Dissolved oxygen | `dC/dt = kLa(N,Q)·(C* − C) − OUR`, then a first-order probe with dead time |
| pH | Drifts with metabolism, stepped by dosing pulses |
| Biomass | Logistic growth |

**Deliberately simplified for now.** The rigorous model — the bicubic kLa surface from
the manuscript, a realistic OUR trajectory, the 20–40 s polarographic probe dead time
— is a **Phase 2 deliverable**, because that is when it becomes load-bearing. See
[ROADMAP](ROADMAP.md).

The probe dead time is the one piece worth getting right early: it is the reason the
cascade needs a prediction horizon at all, and a controller tuned against a
zero-lag process will oscillate the moment it meets a real probe.

---

## 8. What the simulator cannot tell you

Being honest about the limits, so results are not over-trusted:

- **Timing fidelity.** A virtual COM pair is far faster than a real 115200 link, and
  localhost HTTP is faster than the SoftAP. Latency-sensitive behaviour still needs
  the board. Measured reference values are in [PHASE0_RESULTS](PHASE0_RESULTS.md).
- **Firmware behaviour.** Only what has been observed and documented is reproduced.
  An undocumented firmware quirk will not appear here — which is exactly why the
  byte-comparison against a v.6 capture remains a Phase 0 exit criterion.
- **The real process.** A first-order model is not a cultivation. It validates that
  the controller is *structurally* sound — no windup, no collapse at setpoint, dead
  time compensated — not that a given tuning is correct for *B. subtilis*.
