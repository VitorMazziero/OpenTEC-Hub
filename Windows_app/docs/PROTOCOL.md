# ESP32-S3 Protocol Contract

> **Status:** the **v.6 core loop is frozen** — every byte in sections 1 to 3.3 is
> byte-identical to what v.6 puts on the wire and stays that way.
> The **external-device sections (3.4, 3.5 and the presence keys in section 2) are not**:
> the Hub firmware moved to v8 and is being extended for them. Anything below marked
> **`[hub-patch]`** requires the Hub build described in
> [FIRMWARE_DISPOSITIVOS_EXTERNOS.md](FIRMWARE_DISPOSITIVOS_EXTERNOS.md); that build **exists
> in source** as of 2026-08-29 and is pending a flash. The app degrades to ageing the value
> keys locally against a Hub that does not publish them, so both states work.
>
> **Source of truth:** reverse-engineered from `v.6/communication/{transport,data_parser,connection_manager}.py`
> and every `send_command()` call site in the v.6 tree, plus a read of
> `OpenTEC_ESP32_v8.ino` and the five node firmwares on 2026-08-29.
>
> **Docs:** [README](README.md) · [Architecture](ARCHITECTURE.md) · [Roadmap](ROADMAP.md) · [Calibration](CALIBRATION.md) · [Migration](MIGRATION.md) · [UI Design](UI_DESIGN.md) · [Decisions](DECISIONS.md)

---

## 0. The rule

The ESP32-S3 firmware is **fixed**. Every byte this app puts on the wire must be
byte-identical to what v.6 puts on the wire. This document is the contract; the
C# implementation is validated against it by golden-string tests in
`tests/OpenTECHub.Tests`, not by inspection.

If something here looks wrong or redundant — **it stays**. v.6 talks to real
hardware successfully today. Cleanups happen *above* the wire, never on it.

---

## 1. Framing

Both transports carry the **same payload**: a single flat JSON object, no nesting,
no arrays. Only the framing differs.

| | USB (Serial CDC) | Wi-Fi (HTTP) |
|---|---|---|
| Write frame | `<json>\n` — **newline terminated** | `<json>` — **no newline** |
| Write target | the open COM port | `POST http://{ip}/command` |
| Write ack | none (fire and forget) | body must equal `OK` |
| Read | `readline()` | `GET http://{ip}/readData` |
| Liveness | `in_waiting` does not throw | `GET http://{ip}/ping` returns 200, body `pong` |

> **The trailing newline differs by transport.** USB appends `\n`; Wi-Fi must
> not. This is the single easiest way to break USB while Wi-Fi keeps working (or
> vice versa) and is covered by a dedicated test.

### 1.1 Handshake

Identical intent, different framing:

```text
USB    ->  {"comTest":1}\n              <-  OK
Wi-Fi  ->  POST /command {"comTest":1}  <-  200, body "OK"
```

Reply is compared **after trimming**, case-sensitively, against exactly `OK`.

- USB: up to **10** attempts, 100 ms apart.
- Wi-Fi: up to **3** attempts, 100 ms apart.

### 1.2 USB link parameters

| Parameter | Value | Why it matters |
|---|---|---|
| Baud | 115200 | |
| Frame | 8 data bits, 1 stop bit, parity none | |
| Read timeout | 0.75 s | |
| Write timeout | 1.0 s | |
| Inter-byte timeout | 0.1 s | |
| DTR/RTS | **not pulsed by default** | The pulse hardware-resets the board. Measured 2026-08-19; see below. Available as an escalation after repeated handshake failures. |
| Boot settle | **1.8 s, only when pulsing** | Exists solely to wait out the reboot the pulse causes. Do not shorten it on the pulsed path. Skipped entirely when not pulsing. |

Port auto-detection ranks by USB descriptor keywords — `CP210`, `CH340`, `CH910`,
`USB Serial`, `ESP32`, `Silicon Labs`, `wch` — then falls back to any non-Bluetooth
serial port. Ports whose description contains `Bluetooth` are always skipped.

**The DTR/RTS pulse is a hardware reset, and we do not want one.** The auto-reset
circuit responds only to *differential* DTR/RTS states; v.6 drives both lines to the
same value at each end, so whatever reset it causes comes from the transient between
two non-atomic line changes rather than from the sequence as designed. Either way the
board reboots, losing its process state - which is wrong when attaching to a running
application rather than flashing firmware. Use `{"restart":1}` for a deliberate
software restart.

**Reading drains to the newest line.** v.6 does not consume one line per cycle: it
reads *every* buffered line and returns only the last. This is deliberate — it stops
the UI from lagging minutes behind when the ESP32 emits faster than the app consumes.
Preserve this behaviour; a naive "read one line per tick" port will appear to work
on the bench and fall progressively behind during a real cultivation.

### 1.3 Wi-Fi link parameters

| Parameter | Value |
|---|---|
| Default IP | `192.168.4.1` (ESP32 SoftAP) |
| Connect timeout | 0.25 s |
| Read timeout | 0.75 s |
| Ping timeout | 1.0 s |
| Minimum poll period | 1.0 s (v.6 polls at `0.98x` this) |
| Proxy | **bypassed** — v.6 sets `trust_env = False` |

`GET /readData` is **ETag-conditional**: send back the last `ETag` as
`If-None-Match`; a **304** means "nothing new", not an error. On reconnect the
cached ETag must be cleared, or the first poll after reconnect returns 304 forever.

v.6 also forces `Connection: close` and builds a brand-new HTTP session on every
connect, specifically to defeat OS-level socket reuse after the ESP32 reboots.
Keep this behaviour: in C#, use a fresh `HttpClient` / `SocketsHttpHandler` per
connect rather than a long-lived singleton.

**Measured 2026-08-19** on the `Modulo_OpenTEC_1` SoftAP (client at 192.168.4.2,
gateway 192.168.4.1):

| | |
|---|---|
| `POST /command` round trip | min 5 ms · median 9 ms · p95 33 ms · max 33 ms (20/20) |
| Telemetry cadence | ~2.1 s, 45 frames in 90 s, none dropped |
| Time to first frame after connect | 1.1 s |
| ETag | present (`"496"`), and `If-None-Match` correctly answers **304** |

The v.6 timeouts (250 ms connect, 750 ms read, 500 ms write) therefore have ample
headroom on this link — roughly 15x the observed p95. They are tight but not wrong.

Endpoints other than `/ping`, `/readData` and `/command` return **404 "Not found"**;
there is no `/`, `/status`, `/info` or `/config`.

Unlike USB, a Wi-Fi reconnect does **not** reboot the device — there is no reset line
to pulse, and the device clock was observed advancing monotonically across a
disconnect/reconnect cycle. See section 5, Q6.

---

## 2. Telemetry: device to app

One flat JSON object per read. **Every key is optional** — the parser must treat a
missing key as "no update", never as zero. v.6 uses `-1.0` as the "never received"
sentinel for floats.

| JSON key | Type | Unit | Handling |
|---|---|---|---|
| `Tempval` | float | degC | Accepted only if `10 < v < 100`; otherwise the previous value is held |
| `Oxyval` | float | raw ADC | `<= 0.1` means sensor absent, ignore. Spike-filtered, then calibrated |
| `pHval` | float | raw ADC | `<= 0.1` means sensor absent, ignore. Spike-filtered, then calibrated |
| `Pressure` | float | kPa | |
| `FlowRate` | float | L/min | |
| `FlowSetpoint` | float | L/min | Echo of the accepted setpoint |
| `FlowVoltage` | float | V | Raw flowmeter voltage, used by calibration |
| `Antifoam` | float | — | |
| `Distance` | float | mm | Accepted if `0 <= v < 1000`. **If the key is absent for > 3 s, force `-1`** |
| `SensorCommOK` | bool | — | Defaults to `true` when absent |
| `FlowmeterOnline` | bool | — | Sticky: holds the previous value when absent |
| `FlowControlEnabled` | bool | — | Sticky |
| `FlowCommandPending` | bool | — | **Not sticky** — defaults to `false` when absent |
| `FlowCommandSource` | string | — | Sticky, free-form |
| `Valve1`, `Valve2`, `ValveFlow` | int | 0/1 | Only assigned when the key is present. `ValveFlow` is the main shutoff echoed back: `1` = path closed |
| `FlowCommandId`, `FlowCommandAck` | int | — | Command round-trip correlation |
| `FlowCommandDeliveries`, `FlowCommandAgeMs` | int | — | Parsed by v.6, never displayed |
| `HubStations` | int | — | Number of stations seen by the hub |
| `BiomassAbs` | float | AU | |
| `BiomassRaw` | int | counts | |
| `BiomassIT` | int | ms | Integration time |
| `BiomassPWM` | float | % | |
| `PumpFlow` | float | mL/min | External pump, instantaneous |
| `PumpVol` | float | mL | External pump, accumulated |
| `PumpMode` | int | — | Profile mode the node reports running (1-5; `0` idle) |
| `PumpPWM` | int | counts | Motor duty the node is driving |
| `PumpSpeed` | float | — | Commanded speed the node derived from the profile |
| `PumpTargetVol` | float | mL | The node's own profile integral — what it *should* have dosed by now |
| `PumpActive` | bool | — | Inside the operating window and dosing |
| `PumpWaiting` | bool | — | Profile loaded, still before `init_t` |
| `Time` | float | s | Seconds since controller boot; the app subtracts a user-zeroed offset |

The whole `Pump*` block is emitted **only while `pumpComm` is set**, and stock v8 has
**no staleness window for it at all** — a dead node's last sample is republished
indefinitely. `PumpOnline` below is what fixes that; until the Hub carries it, the app ages
`PumpFlow`/`PumpVol` locally instead (`ParserConfig.PumpTimeout`, 5 s).

### 2.0.1 External-device presence and routing `[hub-patch]`

The flowmeter has published `FlowmeterOnline` and `FlowControlEnabled` since v7, and it is
the only external device that did. The rest were observable only through the **absence** of
their value keys, which cannot separate three different things: the operator switched the
device off, the Hub is not routing to it, or the node is gone.

These keys give every external device the same three states. Each is **always present** once
the Hub carries them, so absence of the key means "this Hub predates it", not "false".

| JSON key | Type | Meaning |
|---|---|---|
| `BiomassOnline`, `PumpOnline`, `DistanceOnline`, `AgitatorOnline` | bool | The Hub received a push from the node inside its window |
| `BiomassCommEnabled`, `PumpCommEnabled`, `DistanceCommEnabled` | bool | Echo of the routing flag the Hub persists in NVS |
| `BiomassCommandPending`, `PumpCommandPending`, `AgitatorCommandPending` | bool | A command is queued for the node and not yet acknowledged |
| `AgitatorPercent` | float | Magnitude the node is actually driving, 0-100 % |
| `AgitatorDir` | int | Direction the node is actually driving: `1` CW, `0` CCW |
| `AgitatorPotActive` | bool | The bench potentiometer is live and outranks the app |
| `AgitatorSource` | string | What last moved the motor: `Pot`, `Hub`, `Wi-Fi`, `USB` |

> **The routing echo is not cosmetic.** The Hub persists `bioComm`, `pumpComm`, `distComm`
> and `flowComm` in NVS while the app persists the operator's switches on the PC. After a Hub
> reboot the two can differ, and the Hub then drops every biomass or pump sub-command in
> silence (`if (cmdFound && commOn)`). Without the echo there is nothing to notice that with.

> **Parsing rules.** The `*Online` flags are authoritative when present. The `*CommEnabled`
> flags are **sticky and nullable**: null means "this Hub does not publish it", which is not
> the same as `false`. The `*CommandPending` flags are **not sticky and nullable** for the
> same reason — a Hub with no acknowledgement channel for a device is silent about it, and
> silence is not a confirmation. When a device's `*Online` is false the app **invalidates**
> that device's values rather than holding them, exactly as it already does for the
> flowmeter.

> **Servo migration (2026-09-04).** The app-facing command remains the frozen
> `motorSetpoint` field. Hub 10 routes it to the ESP32S3-driver, which applies
> P1-09 directly over Modbus with command ID, acknowledgement and lease; the old
> UART/CN1 command path is no longer used for speed. The Hub additionally emits
> `ServoControlCapable` and `ServoMotor*` diagnostic fields. Older app builds may
> ignore those additive keys without changing the command frame, while bench
> acceptance must inspect them through `/readData`.

### 2.0 Not every line is telemetry

**Confirmed on hardware 2026-08-19** (ESP32-S3 on COM3, CH343 adapter, no bioreactor
module attached). Three kinds of line arrive on the same stream, and a reader that
assumes "every line is JSON telemetry" will tear down a healthy link:

| Line | Meaning | Correct handling |
|---|---|---|
| `{...}` | Telemetry frame | Parse |
| `[ESP32_AVISO]: ...` | Device log line | Log, ignore, **do not count as a parse failure** |
| `OK` | Command acknowledgement | Recognise, **do not count as a parse failure** |

Observed device log lines:

```text
[ESP32_AVISO]: Falha de leitura UART do Módulo OpenTEC após comando 'b'
[ESP32_AVISO]: Limite de falhas UART atingido; reinicializando UART e iniciando cooldown de 5s
```

> On USB the `OK` acknowledgement travels on the **same serial stream** as telemetry,
> so it lands in the reader rather than at the transport. v.6 counts it as a parse
> failure; it survives only because it needs three *consecutive* failures and
> telemetry usually interleaves. Sending several commands in quick succession can
> trip a false link-loss there.

**The same applies on Wi-Fi, for a different reason.** Confirmed 2026-08-19: the
device appears to serve `/readData` from a shared response buffer, so the first
`GET /readData` after a `POST /command` returns that command's `OK` instead of a
telemetry frame. Subsequent polls return telemetry normally.

This bites every Wi-Fi connect, because the handshake *is* a `POST /command`:

```text
POST /command {"comTest":1}   -> 200 "OK"      (handshake)
GET  /readData                -> 200 "OK"      <- the buffered ack, not telemetry
GET  /readData                -> 200 {"Time":...}
```

A reader that requires the first frame after connect to be telemetry will report a
spurious failure. Treat `OK` as an expected line on **both** transports.

### 2.1 Client-side signal conditioning

Two stages, in order, **inside the parser** — the firmware sends raw counts.

**Stage 1 — spike / step filter** (per channel, stateful):

```text
if no good value yet        -> accept unconditionally (bootstrap)
if |new - lastGood| <= absThreshold
                            -> accept, clear candidate
else                                                     (spike region)
    if no candidate, or |new - candidate| > followTolerance
                            -> start a new candidate, HOLD lastGood
    else
        candidateRuns++
        if candidateRuns >= confirmRuns
                            -> promote candidate (a real step change)
        else                -> HOLD lastGood
```

| Channel | `absThreshold` | `followTolerance` | `confirmRuns` |
|---|---|---|---|
| pH | 500 | 200 | 3 |
| Oxygen | 150 | 50 | 3 |

> These thresholds are in **raw ADC counts**, not engineering units. Changing the
> calibration therefore silently changes what the filter considers a spike — see
> [MIGRATION.md](MIGRATION.md#3-known-defects-carried-in-from-v6), item 4.

**Stage 2 — linear calibration:**

```text
oxygen_mg_per_L = max(oxy_a * acceptedRaw + oxy_b, 0)                    rounded to 4 dp
pH              = clamp(ph_slope * acceptedRaw + ph_intercept, 0, 25000) rounded to 2 dp
```

Calibration currently in the field (`v.6/preferences.json`):

```text
oxy_a    = 0.0305473419314      oxy_b        = -25.09136520919
ph_slope = 0.0005012405704      ph_intercept = -0.600385955239
```

### 2.2 The pH echo-back

Non-obvious and **mandatory**: the app computes the calibrated pH and sends it
*back* to the device whenever it changes.

```json
{"pHCal": "6.98"}
```

The value is a **string with exactly 2 decimal places**, not a JSON number
(v.6 uses `format(calibrated_ph, ".2f")`). The firmware needs the calibrated value
because calibration lives on the PC side.

> **Culture hazard.** On a pt-BR Windows machine, naive .NET formatting yields
> `"6,98"` and the firmware parse fails. Every numeric-to-string conversion on
> this wire must pass `CultureInfo.InvariantCulture`. This is enforced by a test
> that runs under a `pt-BR` culture.

---

## 3. Commands: app to device

Flat JSON. Multiple keys may be combined into one object, and v.6 relies on this
(the kLa cascade sends flow + oxygen + motor together to save bus time). Combining
is **preferred** — it reduces round trips on the shared UART.

### 3.1 Core loop (Phase 1 scope)

| Subsystem | Keys | Range / encoding |
|---|---|---|
| Temperature | `tempSetpoint` | degC; `0` = disabled |
| Motor | `motorSetpoint` | **int** rpm, 15-1000; `0` = off |
| Oxygen monitor | `oxygenMonitor` | % setpoint; `0` = disabled |
| Pressure | `pressureReference` | 1-380; `0` = disabled |
| Flowmeter v05 via Hub v7 | `flowSetpoint` | L/min, clamped to `maxFlow` |
| | `maxFlow` | L/min ceiling |
| | `valve_1`, `valve_2` | `0`/`1` — auxiliary / nitrogen valves |
| | `v_Flow` | **Main gas-path shutoff, active high: `1` closes the path.** `1` whenever `flowSetpoint == 0`; may also be `1` with a nonzero setpoint |

> **`v_Flow` closes the gas path when it is 1**, which is the opposite of what "flow" in the
> name suggests and is easy to get backwards. It is a physical shutoff, not a vent: the v05
> writes it straight to `VALVE_FLOW_PIN` (`flowmeter_OpenTECHUB_V05.ino`, key `v_Flow` /
> `valveFlow`), and the Hub, when the app omits the key, derives it as
> `desiredFlowValveFlow = (setpoint > 0) ? 0 : 1` and forces it to `1` on `resetVariables`.
>
> A zero setpoint therefore always sends `1`, but the two are independent: the app can close the
> path while preserving a nonzero setpoint (`FlowSetpoint(..., mainValveClosed: true)`), which is
> how the operator's main shutoff on Controle works without erasing what was staged.
>
> Disabling the flow subsystem sends `flowSetpoint:0, v_Flow:1, valve_1:0, valve_2:0` — both
> valves are deliberately forced closed on disable rather than preserving the operator's manual
> selection, because leaving a nitrogen valve open on a safe-stop is a hazard.

> **`flowmeterComm` is the Hub's loop-enabled flag, not part of the v05 command.** The Hub v7
> builds and delivers the v05 mailbox from `flowSetpoint`/valves regardless of it, so it never
> gates the gas and is deliberately absent from the setpoint and safe-stop frames. It is not
> unused, though: `processJsonCommand` parses it, persists it under the `flowComm` preference,
> folds it into the settings hash and publishes it back as `FlowControlEnabled` — which is the
> only record of whether the loop is on, and what the `Fluxômetro offline` alarm is conditioned
> on. The app therefore sends it **on its own frame, where the loop is switched**: the Vazão de
> Ar enable and the recipe's aeration-loop block.

### 3.2 Flow calibration

Two-segment piecewise curve, split at **0.0545 V**:

| Keys | Segment |
|---|---|
| `a1`, `b1`, `k1`, `f1`, `c1` | `V <= 0.0545` — anchored quartic `flow = a1·V⁴ + b1·V³ + k1·V² + f1·V + c1` |
| `k2`, `f2`, `c2` | `V > 0.0545` |

`a1`/`b1` are sent first, in the order `a1,b1,k1,f1,c1,k2,f2,c2`: the flowmeter's V10 firmware
zeroes them only when `k1/f1/c1` arrive **without** them, so a low segment sent as `k1/f1/c1`
alone is taken as a quadratic. Hub `10.0.1-dev` forwards them; `10.0.0-dev` dropped them
(2026-09-11). The whole curve goes on **one frame**, under the Hub's 1024-byte serial line.

Preparing or fine-adjusting one certified point uses the Hub-v7 routed state below. The
operator's real-flow value comes from an external standard; the app then averages
distinct `FlowVoltage` telemetry frames.

```json
{"flowSetpoint":1.5,"valve_1":0,"valve_2":0,"v_Flow":0}
```

The regression is `flow = k*V² + f*V + c`. The low segment requires at least three
points. The high segment uses a line (`k2=0`) with two points and a quadratic with three
or more. v.6 accepts a partial calibration, so each valid segment may be sent alone; the
UI must label that state as partial. Point capture and coefficient fitting are specified
operationally in [CALIBRATION.md](CALIBRATION.md#5-calibração-da-vazão-de-ar).

### 3.3 Dosing (Phase 2 scope)

| Subsystem | Keys |
|---|---|
| pH | `pHSetpoint`, `pHError`, `pHOperation`, `pHMix`, `pHIntensity` (= speed % x 10) |
| Nutrient | `nutriOperation`, `nutriMix`, `nutriOpCycle`, `nutriMixCycle`, `nutriIntensity` |
| Antifoam | `antifoamOperation` (0-999), `antifoamMix` (1-999), `antifoamIntensity` (0-99) |
| Distance / foam | `distanceSensorComm`, `distanceSensorReference`, `foamStartDelay_s`, `foamPulse_s`, `foamInterval_s` |
| Agitator flask | `agitatorAuto`, `agitatorReEnablePot`, `agitatorPercent` (0-100 magnitude), `agitatorDir` (`1` CW / `0` CCW), `agitatorOn` |

The pH state is **atomic** in OpenTEC-Hub: all five keys are emitted together. Operator
speed is 0-99%; `pHIntensity` carries that value multiplied by ten. The firmware ranges
reproduced from v.6 are:

| pH field | Operator range | Off encoding |
|---|---:|---:|
| `pHSetpoint` | 1-14 pH | `0.0` |
| `pHError` | `> 0` and `< 2` pH | retained valid value |
| `pHOperation` | integer 1-999 s, encoded as JSON float | retained valid value |
| `pHMix` | integer 1-999 s, encoded as JSON float | retained valid value |
| `pHIntensity` | 0-990 (`percent × 10`) | `0.0` |

Calibration must send the complete off state before a probe is removed from the vessel.
This is independent of the quoted `pHCal` display echo in §2.2.

> The agitator UI encodes direction as a **signed** percent (-100..100) but the
> wire carries magnitude and direction as two separate keys. Do not leak the
> signed form onto the wire.

> **`agitatorOn:0` is not, by itself, a stop.** The Hub turns it into
> `{"RPM_percent":0,"Dir":d,"ActivePot":agitatorReEnablePot}`, and the node's loop re-reads
> the bench potentiometer on its very next pass whenever `ActivePot` is `1` — which is the
> persisted default. A stop issued with the knob at 60 % restarts the motor at 60 %.
>
> So the two stops differ deliberately. The **ordinary off** omits `agitatorReEnablePot` and
> keeps whatever the operator chose; the **operator safe-stop sends `agitatorReEnablePot:0`**
> in the same frame, locking the knob out until it is deliberately re-armed with
> `{"agitatorReEnablePot":1}`. The Hub reads that key into its persisted flag before it acts
> on `agitatorOn`, whichever order the two appear in — it matches on raw text — so one frame
> is enough. See [DECISIONS D-026](DECISIONS.md).

> **Intensity `× 10` is pH-only.** `pHIntensity` is `percent × 10` (0-990); `nutriIntensity`
> and `antifoamIntensity` carry the **raw** operator percent (0-99). Nutrient and antifoam are
> atomic like pH — all their keys travel in one frame — and their timing/cycle values are
> floating-point JSON, matching v.6's `float()` handling.
>
> **Ownership (WP7).** The nutrient pump, the antifoam pump and the flask agitator are owned
> actuators in the command arbiter; a safe-stop zeroes each pump and stops the agitator. The
> level/foam **sensor** keys (`distanceSensorComm`, `distanceSensorReference`, `foam*`) are
> unowned configuration, so a stop never disables foam monitoring. See
> [DECISIONS D-018](DECISIONS.md).

### 3.4 Biomass (Phase 3 WP1)

| Keys | Meaning |
|---|---|
| `biomassComm` | `1`/`0` enable. Handled on the hub; while `0` it drops the sub-commands below — **including any travelling in the same frame** |
| `blank` | `1` — momentary, capture the blank (zero-absorbance) reference |
| `start` | `1` — momentary, start the acquisition loop |
| `stop` | `1` — momentary, stop the acquisition loop |
| `low`, `high`, `opt` | Integration-time thresholds (ints, raw counts), sent together |

> **Disabling is two frames, in order.** `processJsonCommand` parses `biomassComm` before it
> reaches the biomass block, so `{"stop":1,"biomassComm":0}` clears routing and then discards
> its own stop: the node keeps acquiring while the operator looks at a switch that says
> otherwise. OpenTEC-Hub therefore sends `{"stop":1}` and then `{"biomassComm":0}` as a
> separate frame. The outgoing buffer merges by default, so the second frame goes through the
> ordered-frame path (`IDeviceService.SendAfterCurrentFrame`) rather than a plain `Send`.

> **The hub's biomass mailbox holds exactly one command, and reading it clears it.** The node
> polls `/biomassCommand` every 2 s and never acknowledges, and `setPending` overwrites — so a
> `blank` issued just before a `start` is silently replaced. The app serialises the three
> momentary actions behind a pending lock rather than offering all of them at once.

> **`start`/`stop` were not in v.6's Python `send_command` table** — v.6's biomass block issues
> them (`send_biomass_start`/`send_biomass_stop`) and the firmware forwards them
> (`OpenTEC_ESP32_v7.ino`: `start`, `stop`, `blank`, `low`, `high`, `opt`, `test_period`). They are on
> the wire, so they are in the contract. `test_period` exists in the firmware but v.6 never sends it,
> so OpenTEC-Hub does not either. **The firmware exposes no HD-mode state**, so the app shows none.

### 3.5 External pump (Phase 3 WP2)

`pumpComm` (`1`/`0`) enables the hub's routing. A profile carries a `mode`, the operating window
`init_t`/`final_t` (minutes) and the mode's parameters.

> **v.6's disable frame does not stop the pump.** `{"pumpComm":0,"mode":0,"speed":0}` is one
> frame, and the Hub parses `pumpComm` before it reaches the pump block — so its own `mode:0`
> is dropped by `if (pumpCmdFound && pumpCommOn)`. The node keeps dosing; only the telemetry
> goes quiet, which is the worst possible failure for a feed pump.
>
> OpenTEC-Hub therefore sends **`{"mode":0,"speed":0}` first, while routing is still on**, then
> `{"pumpComm":0}` as a separate frame. Once the first is in the Hub's mailbox it survives the
> second — `/pumpCommand` has no routing gate, so the node still collects it on its next poll.
> The two only have to arrive in order. `speed` remains vestigial either way (§ below).

| `mode` | Profile | Parameters (after `mode`, `init_t`, `final_t`) |
|---|---|---|
| 1 | Constant | `lambda_const` |
| 2 | Linear | `lambda_linear`, `phi_linear` |
| 3 | Exponential | `lambda_exp`, `phi_exp` |
| 4 | Polynomial | `p0` ... `p20` (flattened, one key per coefficient, only as many as entered) |
| 5 | Piecewise | `num_segments`, then interleaved `t0,q0,t1,q1,…` (t in minutes, q in mL/min) |

> **`init_t`/`final_t` were missing from earlier revisions of this table** but are emitted by every
> v.6 pump-mode frame (`pump_mode_window.send_commands`) and forwarded by the firmware, so they are
> part of the contract. Times are relative to the profile: `t' = t − init_t`, and flow is zero before
> `init_t`. The firmware holds `p0..p20` (≤ 21 coefficients) and `t0..t99`/`q0..q99` (2–100 segments);
> those are the payload-size bounds the app validates against.
>
> The disable frame's `speed:0` is **vestigial**: the firmware forwards `pump_speed`, not `speed`, so
> it is ignored — reproduced only for byte-parity with v.6. The proportional-gas coupling
> `Q_g = (V₀ + PumpVol/1000)·vvm` is not a pump key at all: it computes an **aeration** setpoint and is
> sent as a flow frame through the command arbiter (owned as `Aeration`). OpenTEC-Hub closes both gas
> valves on that frame rather than reproducing v.6's stray `valve_2:1`-at-zero-flow behaviour.

### 3.6 System

| Key | Meaning |
|---|---|
| `comTest` | `1` — handshake probe |
| `dataDelay` | Telemetry period in **ms** (field value: 2000) |
| `resetVariables` | `1` — reset the module's process variables |
| `restart` | `1` — restart all controller communications |

---

## 4. Golden strings

These exact byte sequences are asserted in `tests/OpenTECHub.Tests/WireFormatTests.cs`.
Extend this table before adding any new command, never after.

```text
USB handshake      {"comTest":1}\n
Wi-Fi handshake    {"comTest":1}
pH echo            {"pHCal":"6.98"}
pH control         {"pHSetpoint":6.8,"pHError":0.17,"pHOperation":5.0,"pHMix":20.0,"pHIntensity":500.0}
pH safe-stop       {"pHSetpoint":0.0,"pHError":0.17,"pHOperation":5.0,"pHMix":20.0,"pHIntensity":0.0}
Motor setpoint     {"motorSetpoint":790}
Valve state at 0   {"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":1,"valve_2":1,"v_Flow":1}
Flow safe-stop     {"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1}
Flow cal setpoint  {"flowSetpoint":1.5,"valve_1":0,"valve_2":0,"v_Flow":0}
Flow cal curve     {"maxFlow":50.0,"a1":-1.2E-05,"b1":0.00034,"k1":2.0,"f1":3.0,"c1":4.0,"k2":0.0,"f2":5.0,"c2":1.0}
Core safe-stop     {"tempSetpoint":0.0,"motorSetpoint":0,"oxygenMonitor":0.0,"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1,"pressureReference":0.0}
Operator safe-stop {"tempSetpoint":0.0,"motorSetpoint":0,"oxygenMonitor":0.0,"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1,"pressureReference":0.0,"pHSetpoint":0.0,"pHError":0.15,"pHOperation":1.0,"pHMix":60.0,"pHIntensity":0.0}
                   ... plus the dosing, agitator and pump-profile fragments, then {"pumpComm":0} on the next frame
kLa combined       {"flowSetpoint":2.5,"valve_1":0,"valve_2":0,"v_Flow":0,"oxygenMonitor":40.0,"motorSetpoint":300}
biomass enable     {"biomassComm":1}
biomass blank      {"blank":1}
biomass start/stop {"start":1}   /   {"stop":1}
biomass thresholds {"low":10000,"high":40000,"opt":25000}
pump enable        {"pumpComm":1}
pump stop profile  {"mode":0,"speed":0}
pump clear routing {"pumpComm":0}
agitator on        {"agitatorOn":1,"agitatorAuto":0,"agitatorPercent":80.0,"agitatorDir":1}
agitator off       {"agitatorOn":0,"agitatorAuto":0,"agitatorPercent":80.0,"agitatorDir":1}
agitator safe-stop {"agitatorOn":0,"agitatorAuto":0,"agitatorPercent":80.0,"agitatorDir":1,"agitatorReEnablePot":0}
pump constant      {"mode":1,"init_t":0.0,"final_t":60.0,"lambda_const":1.5}
pump linear        {"mode":2,"init_t":0.0,"final_t":60.0,"lambda_linear":1.0,"phi_linear":0.5}
pump exponential   {"mode":3,"init_t":0.0,"final_t":60.0,"lambda_exp":1.0,"phi_exp":0.1}
pump polynomial    {"mode":4,"init_t":0.0,"final_t":60.0,"p0":1.0,"p1":0.5,"p2":0.1}
pump piecewise     {"mode":5,"init_t":0.0,"final_t":60.0,"num_segments":3,"t0":0.0,"q0":1.0,"t1":30.0,"q1":2.0,"t2":60.0,"q2":3.0}
```

Key order within an object is not believed to matter (the firmware parses JSON),
but the tests pin v.6's emission order anyway — it costs nothing and removes the
question from the table if a problem ever appears in the field.

---

## 5. Open questions for hardware verification

These are behaviours v.6 relies on that could not be confirmed from the Python
source alone. Each should be checked against the firmware or on the bench before
the Phase 0 transport is declared done.

| # | Question | Why it matters |
|---|---|---|
| Q1 | Does the firmware tolerate **unknown keys** in a command object? | Determines whether the app can send one combined object per cycle or must split by subsystem. |
| Q2 | Is there a **maximum payload size** for `POST /command`? | The polynomial pump mode sends 21 coefficients plus mode in a single object. |
| Q3 | Does `POST /command` ever reply something other than `OK` / non-200? | v.6 treats everything else as failure and silently drops the command. |
| ~~Q4~~ | ~~Does the ESP32 emit a boot banner after reset?~~ | **Answered 2026-08-19.** No banner, but it does emit `[ESP32_` log lines and bare `OK` acks on the telemetry stream. See section 2.0. |
| Q5 | Does `dataDelay` apply to both transports? | Drives the Wi-Fi poll period and the chart sample rate. **Partly answered 2026-08-19:** the measured USB emission period is ~2.0 s, matching the field `dataDelay` of 2000 ms. |
| ~~Q6~~ | ~~Should the DTR/RTS reset pulse be suppressed?~~ | **Answered 2026-08-19** by `opentec-harness reset-test`. Yes. With the pulse the device clock fell 102.1 s to 2.8 s across a reconnect while 5.1 s of wall time passed; without it the clock advanced 5.5 s against 5.5 s of wall time. The pulse is what reboots the board. Suppressing it takes a connect from 1903 ms to 13 ms and preserves device state. Now default-off, with an escalation to a pulsed connect after repeated handshake failures for the hung-firmware case. |
