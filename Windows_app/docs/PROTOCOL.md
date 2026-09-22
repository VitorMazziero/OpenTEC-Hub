# ESP32-S3 Protocol Contract

> **Nota de organização (2026-09-11):** os firmwares externos agora estão em
> `External-Devices/<dispositivo>/firmware/`; seus contratos ficam nos diretórios
> `docs/`. O Hub autoritativo está em `ESP32S3-HUB/ESP32S3-HUB`. A reorganização
> preserva o fio, verificado por `External-Devices/tools/Test-HubDeviceContracts.ps1`.
>
> **Status:** the **v.6 core loop is frozen** — every byte in sections 1 to 3.3 is
> byte-identical to what v.6 puts on the wire and stays that way.
> The **external-device sections (3.4, 3.5 and the presence keys in section 2) are not**:
> the Hub firmware moved to v8 and is being extended for them. Anything below marked
> **`[hub-patch]`** requires the Hub build described in
> [FIRMWARE_DISPOSITIVOS_EXTERNOS.md](FIRMWARE_DISPOSITIVOS_EXTERNOS.md); that build **exists
> in source** as of 2026-08-29 and is pending a flash. The app degrades to ageing the value
> keys locally against a Hub that does not publish them, so both states work.
>
> **Source of truth:** the frozen desktop wire contract remains derived from
> `v.6/communication/{transport,data_parser,connection_manager}.py`; for external
> devices, use the current `ESP32S3-HUB/ESP32S3-HUB` implementation together with
> the active firmware under `External-Devices/`.
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

**…but only telemetry collapses** `[hub 10.2]`. The Hub answers `{"nodeDiag":"all"}` with
five `{"NodeDiag":…}` lines in one burst, and `OK` acks and `[ESP32_` log lines share the
stream. Draining to the single newest line would keep at most one of them. `SerialTransport`
therefore collapses only telemetry frames (a JSON object that is not a `NodeDiag` envelope)
to the newest and queues every other line, handing them out one per read in arrival order
(`SerialLineCoalescer`, bounded at 64). Found while writing the bench suite for
`docs/processes/TESTES_AUTOMATICOS_BANCADA.md` B2.6, before it ran on hardware.

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
| `Distance` | float | mm | Accepted if `0 <= v < 1000`. **If the key is absent for > 3 s, force `-1`**. May be absent while `DistanceOnline` is `true`: the node pushes `-1` when its VL53L0X fails, and the Hub withholds the value but not the presence or the `Distance*` echoes (Hub 10.2 no longer applies its own stagnation filter) |
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

### 2.0.2 External-node identity `[hub 10.1]`

The Hub keeps a registry of the five external nodes, filled by their `GET /nodeHello?dev=&ver=&mac=`
handshake (External-Devices, phase 3) and updated opportunistically by their data pushes. From
Hub **10.0.1** the aggregate frame carries each node's address; from **10.1.0** also what the node
said about itself. All fifteen keys are **additive** — `HubProtocolVersion` stays 10.

| JSON key | Type | When present | Meaning |
|---|---|---|---|
| `DistanceIP`, `AgitatorIP`, `PumpIP`, `FlowmeterIP`, `BiomassIP` | string | every frame (10.0.1+) | IP the Hub extracted from the node's TCP connection; `0.0.0.0` = never seen |
| `DistanceNodeVer`, `AgitatorNodeVer`, `PumpNodeVer`, `FlowmeterNodeVer`, `BiomassNodeVer` | string ≤ 15 | only after the node's `/nodeHello` (10.1+) | the `ver=` the node sent (`v10`, `3.8`, …) |
| `DistanceNodeMac`, `AgitatorNodeMac`, `PumpNodeMac`, `FlowmeterNodeMac`, `BiomassNodeMac` | string 17 | only after the node's `/nodeHello` (10.1+) | the `mac=` the node sent |

> **Parsing rules.** `TelemetryParser` folds the three into one `ExternalNodeIdentity(Ip, Mac,
> FirmwareVersion)` per node, every member nullable and null meaning *unknown*. Version and MAC
> are **sticky** within the link, like `HubFirmwareVersion`: absent from a frame, they keep the
> last value, because the Hub emits them only once the node registered and an older Hub never
> does. The IP is **not** sticky through `0.0.0.0`: that value is an explicit "never seen" the
> Hub sends on every frame after it reboots and forgets, so the app forgets too. Identity says
> nothing about presence — that stays with the `*Online` flags of §2.0.1 — and unknown identity
> is never rendered as a fault.

> **`GET /nodes` (Wi-Fi only).** The same registry as one document, read by
> `HubNodeDirectoryClient` outside the telemetry transport (D-051):
> `{"hub_time_ms":48210,"nodes":[{"dev":"pump","ip":"192.168.4.3","mac":"…","version":"3.8",
> "online":true,"age_ms":420,"registered":true,"last_hello_ms":41000,"last_data_ms":47790},…]}`.
> `registered`, `last_*_ms` and `hub_time_ms` are 10.1; the client tolerates a 10.0 body without
> them. Reachable only while the PC is on the Hub's SoftAP — over USB the app knows each node's
> address from the frame but cannot get to `/nodes`, nor to the nodes' own `/diag`. Contract in
### 2.0.3 External-node configuration echoes `[hub 10.2]`

Hub 10.2 publishes the parameters applied and echoed by each external node. Unlike node identity
(which is sticky), these echoes are **strictly non-sticky**: if the node is absent or the key is
missing from the frame, the reading resolves to null (except `FlowmeterBootId`, which is sticky).

| JSON key | Type | Unit / Range | Meaning |
|---|---|---|---|
| `DistanceOffsetMm` | float | mm | Distance sensor surface-to-probe offset calibration |
| `DistanceSamplePeriodMs` | int | ms | Distance sensor internal sampling period |
| `DistanceSendPeriodMs` | int | ms | Distance sensor push period |
| `DistanceCommandPending` | bool | — | A command is queued for the distance node and not yet acknowledged |
| `FlowKp` | float | — | Flowmeter PID proportional gain |
| `FlowKi` | float | — | Flowmeter PID integral gain |
| `FlowFfGain` | float | — | Flowmeter feedforward gain |
| `FlowFfOffset` | float | — | Flowmeter feedforward offset |
| `FlowRampRate` | float | mL/min/s | Flowmeter setpoint ramp rate |
| `FlowOutput` | float | L/min | Flowmeter controller output equivalent flow rate (FMA-5400) |
| `FlowSetpointCorrected` | float | mL/min | Flowmeter active setpoint after ramp |
| `FlowmeterBootId` | int | — | Flowmeter boot cycle counter (sticky across session) |
| `FlowmeterCalCrc` | int | — | Calibration parameters CRC32 hash (v11.0+) |
| `FlowmeterHwStatus` | int | 0-7 | Hardware health bitmask (bit 0=ADS, bit 1=DAC, bit 2=healthy latch) |
| `PumpPidKp`, `PumpPidKi`, `PumpPidKd` | float | — | Volume-PID gains the pump node runs (pump 3.10+; absent on 3.9). Non-sticky. The PID expander unlocks only while these are present, and a sent triple is persisted only when echoed back |
| `PumpPotEnabled` | bool | — | Bench potentiometers in command of the motor (3.10+). False after a `pump_speed` run until `pump_pot:1` |
| `PumpCycleVol` | float | mL | Volume of the current profile cycle (3.10+). `PumpVol` is the session counter: on 3.10 it survives stop/profile changes and only `reset_volume` zeroes it |
| `BiomassGear` | int | 0-31 | Biomass combined optical gear (`IT index × 8 + PWM index`); the node also echoes `BiomassIT` (ms) and `BiomassPWM` (%) from §2.0.1 |
| `BiomassEma` | float | 0.0-1.0 | Biomass sensor EMA smoothing filter factor |
| `BiomassProbePeriodMs` | int | ms | Biomass sensor acquisition probe period |

The pump node (3.9) does **not** echo `pid_kp`/`pid_ki`/`pid_kd`; the app keeps those fields
disabled until a pump firmware echoes them (§3.3 of the plan: no echo, no editable field).

**Node health is not in the frame.** RSSI, free heap, uptime, `hub_fail_streak` and `ota` of each
node reach the app through `GET /nodeDiag[?dev=]` (Wi-Fi) or the serial request
`{"nodeDiag":"<dev>|all"}` (USB), which the Hub answers with one `{"NodeDiag":{…}}` line per node
from a cache filled by its own task every 30 s. Contract in `ESP32S3-HUB/docs/WIRE_CONTRACT_V9.md`
("Diagnóstico dos nós"). See §2.0 for how the line is classified. Each entry also carries
`truncated` and `body_bytes` (Hub 2026-09-12+): a `code 200` with `diag: null` and
`truncated: true` means the node's `/diag` outgrew the Hub's 511 B cache, and the row says so
("atualize o Hub") instead of "Sem métricas específicas". Both fields are optional in the parser
(`HubNodeDiag.Truncated` defaults to false, `BodyBytes` to null) so older Hubs keep working.

### 2.0 Not every line is telemetry

**Confirmed on hardware 2026-08-19** (ESP32-S3 on COM3, CH343 adapter, no bioreactor
module attached). Three kinds of line arrive on the same stream, and a reader that
assumes "every line is JSON telemetry" will tear down a healthy link:

| Line | Meaning | Correct handling |
|---|---|---|
| `{...}` | Telemetry frame | Parse |
| `[ESP32_AVISO]: ...` | Device log line | Log, ignore, **do not count as a parse failure** |
| `OK` | Command acknowledgement | Recognise, **do not count as a parse failure** |
| `{"NodeDiag":{…}}` | Cached node health, answer to `{"nodeDiag":…}` `[hub 10.2]` | `ParseOutcome.NodeDiag`: raise `NodeDiagReceived`, **not telemetry, not a parse failure** |

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
| | `valve_1`, `valve_2` | `0`/`1` — the flowmeter's two MOSFET **inputs 1 and 2**. What each input drives is app configuration (`GasRigConfiguration`, Configurações › Gás e válvulas), not protocol: on the default wiring input 2 drives **A** (air to the reactor) and input 1 drives **B + C** together (N₂ line and air purge, one channel). The app never sends a setpoint above zero with both at `0` from an assay or automation (`CommandBuilders.FlowRoute`); Controle › Avançado can. |
| | `v_Flow` | **Line shutoff, active high: `1` closes the line** (GPIO 5 on the node). It does not route: assumption that it drives none of A/B/C — bench receipt pending (`docs/plans/2026-09-12-plano-valvulas-abc-ensaios.md` §7.3). `1` whenever `flowSetpoint == 0`; may also be `1` with a nonzero setpoint |

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
> inputs are deliberately forced closed on disable rather than preserving the operator's manual
> selection, because leaving the N₂ line (B) open on a safe-stop is a hazard.
>
> **A/B/C rig (2026-09-12, [D-053](DECISIONS.md)).** The wire did not change; who decides what each
> input receives did. `GasRouting.Resolve(route, rig)` maps an intention — `Closed`, `Reactor`
> (A), `VentAndNitrogen` (B + C) — to the pair, and `GasRouting.Interpret` reads a frame back into
> `Closed / Reactor / VentAndNitrogen / DeadEnd / BothOpen`. The kLa runner strips through B, pre-stages
> the assay airflow on the same B/C output (air out of C, N₂ still in) and switches to A in **one
> frame** — `valve_1` and `valve_2` in the same JSON, which the node applies in one cycle — so no
> intermediate state is ever on the wire; the confirmed echo of that frame is `t = 0`. The power
> runner does the same pre-stage on C for every gassed condition.

> **`flowmeterComm` is the Hub's loop-enabled flag, not part of the v05 command.** The Hub v7
> builds and delivers the v05 mailbox from `flowSetpoint`/valves regardless of it, so it never
> gates the gas and is deliberately absent from the setpoint and safe-stop frames. It is not
> unused, though: `processJsonCommand` parses it, persists it under the `flowComm` preference,
> folds it into the settings hash and publishes it back as `FlowControlEnabled` — which is the
> only record of whether the loop is on, and what the `Fluxômetro offline` alarm is conditioned
> on. The app therefore sends it **on its own frame, where the loop is switched**: the Vazão de
> Ar enable and the recipe's aeration-loop block.

### 3.2 Flow calibration

Two-segment piecewise curve, split at editable **`Vt`**. `0.0545 V` is the
v12.0 default/migration value, not flow and not an immutable threshold:

| Keys | Segment |
|---|---|
| `a1`, `b1`, `k1`, `f1`, `c1` | `V <= Vt` — anchored quartic `flow = a1·V⁴ + b1·V³ + k1·V² + f1·V + c1` |
| `k2`, `f2`, `c2` | `V > Vt` |
| `flowTransitionVoltage` | App→Hub name for `transition_v`, in volts |

`a1`/`b1` are sent first, followed by the other coefficients and `flowTransitionVoltage`:
the flowmeter's V10 firmware
zeroes them only when `k1/f1/c1` arrive **without** them, so a low segment sent as `k1/f1/c1`
alone is taken as a quadratic. Hub `10.0.1-dev` forwards them; `10.0.0-dev` dropped them

`transition_v` is a calibration field, not an operational setting. Hub 10.4 omits it
from ordinary setpoint/safe-stop frames and from the state reassertion performed after a
flowmeter reboot. It is serialized only when `a1,b1,k1,f1,c1,k2,f2,c2` are all pending
in the same reliable mailbox revision. A partial calibration frame is never emitted.

`FlowCommandId` and `FlowCommandAck` originate as `uint32_t` in the Hub. Consumers must
therefore preserve values through `4294967295`; the Windows app represents both as
signed 64-bit integers solely to avoid overflow while keeping comparisons simple.
(2026-09-11). On v12.0 the whole curve and `Vt` go on **one frame**, under the Hub's
1024-byte serial line; the node rejects an incomplete or discontinuous update.

Preparing or fine-adjusting one certified point uses the Hub-v7 routed state below. The
operator's real-flow value comes from an external standard; the app then averages
distinct `FlowVoltage` telemetry frames.

```json
{"flowSetpoint":1.5,"valve_1":0,"valve_2":0,"v_Flow":0}
```

The regression uses the complete low quartic/high quadratic model. A proposed curve remains
local until both segments satisfy their fit requirements. The v12.0 UI never reports a
partial send as applied: ACK, transition echo and CRC are required. Point capture and fitting are specified
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

> **The hub's biomass mailbox holds exactly one revision.** Since Hub 10.2 it is a
> `ReliableMailbox`: the node polls `/biomassCommand` every 2 s, the Hub keeps re-delivering
> until the node's push carries the matching `ack_cmd_id`, and `BiomassCommandPending` says so.
> A new order still *replaces* a revision the node has not fetched yet — so a `blank` followed
> within 2 s by a `start` is silently replaced. The app serialises the three momentary actions
> behind `BiomassCommandPending` rather than offering all of them at once. Note the node does not
> poll while it blanks or searches gears (20–40 s), so a queued order lands only afterwards.

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
> OpenTEC-Hub therefore sends **`{"mode":0}` first, while routing is still on**, then
> `{"pumpComm":0}` as a separate frame. Once the first is in the Hub's mailbox it survives the
> second — `/pumpCommand` has no routing gate, so the node still collects it on its next poll.
> The two only have to arrive in order. As decided in 2026-09-12 §3.7, `speed` is omitted because
> the pump node (v3.9) misinterprets `speed:0` as a command to arm or run in speed mode.

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
> The disable frame formerly carried `speed:0` for byte-parity with v.6, but it has been removed
> to prevent the pump firmware from interpreting it as an armed speed command. The proportional-gas coupling
> `Q_g = (V₀ + PumpVol/1000)·vvm` is not a pump key at all: it computes an **aeration** setpoint and is
> sent as a flow frame through the command arbiter (owned as `Aeration`). OpenTEC-Hub routes that frame
> to the reactor (A, `valve_2:1` on the default wiring) rather than reproducing v.6's stray
> `valve_2:1`-at-zero-flow behaviour.

### 3.6 System

| Key | Meaning |
|---|---|
| `comTest` | `1` — handshake probe |
| `dataDelay` | Telemetry period in **ms** (field value: 2000) |
| `resetVariables` | `1` — reset the module's process variables |
| `restart` | `1` — restart all controller communications |
| `nodeDiag` | `"distance"`, `"agitator"`, `"pump"`, `"flowmeter"`, `"biomass"` or `"all"` — asks the Hub for the cached `/diag` of that node (`all`: five lines). A read-only system request: it claims no actuator in the arbiter, mutates no NVS and does not change the Hub's state hash `[hub 10.2]` |

### 3.7 External-node configuration commands `[hub 10.2]`

Configuration commands for external nodes pass through the Hub's reliable mailboxes. The app sends
camelCase keys, and the Hub translates them before enqueuing to each node's mailbox:

| App command key | Type | Node / Hub mailbox | Wire key on node |
|---|---|---|---|
| `distanceOffsetMm` | float | Distance node | `offset_mm` |
| `distanceSamplePeriodMs` | int | Distance node | `sample_period` |
| `distanceSendPeriodMs` | int | Distance node | `send_period` |
| `distanceResetNvs` | int (`1`) | Distance node | `reset_nvs` |
| `flowKp` | float | Flowmeter node | `kp_flow` |
| `flowKi` | float | Flowmeter node | `ki_flow` |
| `flowFfGain` | float | Flowmeter node | `ff_gain` |
| `flowFfOffset` | float | Flowmeter node | `ff_offset` |
| `flowRampRate` | float | Flowmeter node | `ramp_rate` |
| `pump_command` | string | Pump node | `command` — the Hub forwards only `reset_volume`, `start`, `stop` (2026-09-12). On pump 3.10 `stop` and `mode:0` keep the session volume; only `reset_volume` zeroes it |
| `pump_speed` | int 0..1000 | Pump node | `speed` — hold the motor at S in idle mode; `0` stops. The sender owns the stop (the volumetric calibration sends `0` from the app clock). `speed` without the prefix is rejected by the Hub |
| `pump_speed_ms` | int ms | Pump node | `speed_ms` — node-side deadline for `pump_speed` (pump 3.10+); the calibration sends duration + 3 s as the safety net |
| `pump_pot` | `1`/`0` | Pump node | `pot` — hand the motor back to the bench potentiometers (forgets any manual speed) / lock them out (pump 3.10+) |
| `pumpPidKp` | float | Pump node | `pid_kp` |
| `pumpPidKi` | float | Pump node | `pid_ki` |
| `pumpPidKd` | float | Pump node | `pid_kd` |
| `pumpA1`..`pumpC1` | float | Pump node v3.12+ | `a1`..`c1`; quarto grau inferior; conjunto atômico |
| `pumpK2`..`pumpC2` | float | Pump node v3.12+ | `k2`..`c2`; quadrático superior; conjunto atômico |
| `pumpTransitionSpeed` | float | Pump node v3.12+ | `transition_speed` (`St`); `Qt` é derivado |
| `flowTransitionVoltage` | float | Flowmeter v12.0+ | `transition_v` em volts; deve viajar com os dois segmentos completos |
| `biomassIt` | int | Biomass node | `command:"set_it",value:N` |
| `biomassPwm` | float | Biomass node | `command:"set_pwm",value:N` |
| `biomassGear` | int | Biomass node | `command:"set_gear",value:N` |
| `biomassEma` | float | Biomass node | `command:"ema",value:N` |
| `biomassProbePeriodMs` | int | Biomass node | `command:"probe_period",value:N` — the node clamps to its LED thermal floor; persisted on v11.1+. The Hub sizes the biomass presence window from the echoed `probe_ms` (`max(10 s, 2.5 × probe_ms)`) |
| `biomassAutoRange` | `"auto"`/`"manual"` | Biomass node | `command:"auto"` / `command:"manual"` — the node persists it but does not echo it; `biomassGear` implies `manual` on v11.1+ |

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
Reactor (A), A on 2   {"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}
B + C, A on 2         {"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":0}
N2 only (B/C, sp 0)   {"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":1}
Reactor (A), A on 1   {"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":1,"valve_2":0,"v_Flow":0}
B + C, A on 1         {"flowSetpoint":3.0,"maxFlow":50.0,"valve_1":0,"valve_2":1,"v_Flow":0}
Flow cal setpoint  {"flowSetpoint":1.5,"valve_1":1,"valve_2":0,"v_Flow":0}      (through C on the default wiring)
Flow cal curve     {"maxFlow":50.0,"a1":0.0,"b1":0.0,"k1":0.001234567,"f1":0.0,"c1":0.0,"k2":0.002345678,"f2":0.0,"c2":0.0,"flowTransitionVoltage":0.0545}
Core safe-stop     {"tempSetpoint":0.0,"motorSetpoint":0,"oxygenMonitor":0.0,"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1,"pressureReference":0.0}
Operator safe-stop {"tempSetpoint":0.0,"motorSetpoint":0,"oxygenMonitor":0.0,"flowSetpoint":0.0,"maxFlow":50.0,"valve_1":0,"valve_2":0,"v_Flow":1,"pressureReference":0.0,"pHSetpoint":0.0,"pHError":0.15,"pHOperation":1.0,"pHMix":60.0,"pHIntensity":0.0}
                   ... plus the dosing, agitator and pump-profile fragments, then {"pumpComm":0} on the next frame
Cascade combined   {"flowSetpoint":2.5,"valve_1":0,"valve_2":1,"v_Flow":0,"oxygenMonitor":40.0,"motorSetpoint":300}   (air to A)
biomass enable     {"biomassComm":1}
biomass blank      {"blank":1}
biomass start/stop {"start":1}   /   {"stop":1}
biomass thresholds {"low":10000,"high":40000,"opt":25000}
pump enable        {"pumpComm":1}
pump stop profile  {"mode":0}
pump clear routing {"pumpComm":0}
agitator on        {"agitatorOn":1,"agitatorAuto":0,"agitatorPercent":80.0,"agitatorDir":1}
agitator off       {"agitatorOn":0,"agitatorAuto":0,"agitatorPercent":80.0,"agitatorDir":1}
agitator safe-stop {"agitatorOn":0,"agitatorAuto":0,"agitatorPercent":80.0,"agitatorDir":1,"agitatorReEnablePot":0}
pump constant      {"mode":1,"init_t":0.0,"final_t":60.0,"lambda_const":1.5}
pump linear        {"mode":2,"init_t":0.0,"final_t":60.0,"lambda_linear":1.0,"phi_linear":0.5}
pump exponential   {"mode":3,"init_t":0.0,"final_t":60.0,"lambda_exp":1.0,"phi_exp":0.1}
pump polynomial    {"mode":4,"init_t":0.0,"final_t":60.0,"p0":1.0,"p1":0.5,"p2":0.1}
pump piecewise     {"mode":5,"init_t":0.0,"final_t":60.0,"num_segments":3,"t0":0.0,"q0":1.0,"t1":30.0,"q1":2.0,"t2":60.0,"q2":3.0}
distance config    {"distanceOffsetMm":25.5,"distanceSamplePeriodMs":200,"distanceSendPeriodMs":1000}
distance reset     {"distanceResetNvs":1}
flow tuning        {"flowKp":0.8,"flowKi":0.05,"flowFfGain":1.2,"flowFfOffset":0.1,"flowRampRate":5.0}
pump reset vol     {"pump_command":"reset_volume"}
pump manual speed  {"pump_speed":500,"pump_speed_ms":63000}   (calibration run; {"pump_speed":0} stops; node stops itself at 63 s)
pump potentiometers {"pump_pot":1}                              (hand the motor back to the bench knobs)
pump calibration   {"pumpA1":0.0,"pumpB1":0.0,"pumpK1":0.0,"pumpF1":0.003906,"pumpC1":0.0,"pumpK2":0.0,"pumpF2":0.003906,"pumpC2":0.0,"pumpTransitionSpeed":200.0}
pump PID           {"pumpPidKp":1.5,"pumpPidKi":0.2,"pumpPidKd":0.05}
biomass IT         {"biomassIt":100}
biomass PWM        {"biomassPwm":75.0}
biomass gear       {"biomassGear":3}
biomass EMA        {"biomassEma":0.25}
biomass probe      {"biomassProbePeriodMs":500}
biomass auto-range {"biomassAutoRange":"manual"}                      (lock the selected gear; "auto" hunts again)
```

Key order within an object is not believed to matter (the firmware parses JSON),
but the tests pin v.6's emission order anyway — it costs nothing and removes the
question from the table if a problem ever appears in the field.

### 4.1 Confirmação das calibrações v12/3.12

- O fluxômetro confirma a aplicação moderna com ACK da revisão, eco de
  `FlowTransitionVoltage` e CRC que cobre também `transition_v`.
- A bomba confirma somente quando `PumpCommandPending=false`, os nove valores ecoados
  coincidem com o pedido e `PumpCalCrc` está presente. Eco parcial ou apenas ACK não basta.
- Hub anterior a 10.3 ou fluxômetro anterior a v12.0 bloqueia a transição editável.
- A bomba pertence à primeira implantação e tem um único contrato: nove parâmetros
  polinomiais. O app, Hub e nó não reconhecem comandos ou ecos lineares e não aguardam versão
  para liberar o cartão; a ausência de curva válida mantém a conversão bloqueada no nó.

---

## 4.2 Banho externo C404 e cascata térmica (Hub 10.6)

The Windows app treats `Tempval` as the real reactor temperature read by the module sensor via
the Hub UART. It is distinct from `BathPv` (the C404 bath temperature). The PC sends only the
reactor reference (`tempSetpoint`) and route/mode/acknowledgement commands; the Hub owns the PI
and emits `BathCascadePvFiltered`, `BathCascadeError`, `BathCommandSetpoint`,
`BathCommandConfirmed`, `BathCascadeP`, `BathCascadeI`, saturation, guard and pause state.

When the bath node is absent, all bath fields remain nullable/absent and the app reports
"aguardando telemetria" rather than inventing an offline node. The frozen v6 session log is not
changed; bath telemetry is written to the versioned `-bath-cascade.tsv` sidecar.

Hub 10.6 additions consumed by the app: `BathOwned`, `BathCascadeActive`,
`BathCascadeFaultReason`, `BathStopPending`, `BathCommandCompletion`, `BathOperationError`,
`BathNodeRejectId/Error`, `BathNodeSpMin/Max`, `TempSetpointCommanded`, `TempModuleActuatorOn`,
`TempvalValid`, `TempvalAgeMs`, the vigent tuning (`BathCascadeKp` … `BathCascadeOutputMaxC`)
and `BathCascadeConfigError`. `bathAbort=1` is the bath stop (node aborted and left in manual,
cascade off, route unchanged); on the bath route `tempSetpoint=0` does the same. Tuning frames
are validated with `BathCascadeTuning.Validate`, a one-to-one copy of the firmware check.

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
| Q7 | How large is `/readData` with the five nodes registered **and every §2.0.3 echo present** (`[hub 10.2]`)? | The frame `String` reserve is 3072 B and the USB line limit is 1024 B per *command*, not per frame; the plan's ceiling is 2.6 KB. If the bench measures above it, `FlowOutput` and `FlowSetpointCorrected` are the first keys to drop. Recorded in `docs/evidence/` when measured. |
| Q8 | Hub free heap after the `NodeDiag` task is created (`[hub 10.2]`)? | The task costs a 6144 B stack plus 5 × ~520 B of cache; the Hub logs `heap antes/depois` at boot. Acceptance: > 150 KB free. Not yet measured. |
| Q5 | Does `dataDelay` apply to both transports? | Drives the Wi-Fi poll period and the chart sample rate. **Partly answered 2026-08-19:** the measured USB emission period is ~2.0 s, matching the field `dataDelay` of 2000 ms. |
| ~~Q6~~ | ~~Should the DTR/RTS reset pulse be suppressed?~~ | **Answered 2026-08-19** by `opentec-harness reset-test`. Yes. With the pulse the device clock fell 102.1 s to 2.8 s across a reconnect while 5.1 s of wall time passed; without it the clock advanced 5.5 s against 5.5 s of wall time. The pulse is what reboots the board. Suppressing it takes a connect from 1903 ms to 13 ms and preserves device state. Now default-off, with an escalation to a pulsed connect after repeated handshake failures for the hung-firmware case. |
