# TECNAL-Hub — Hardware validation plan

> **Version:** 1.0 · **Written:** 2026-08-22
> The single checklist of everything that could only be proven on the real ESP32-S3 and the
> TECNAL bioreactor. Everything below has passed in software and against the device simulator;
> none of it is biologically or electrically validated until it passes here.
>
> **Docs:** [ROADMAP](ROADMAP.md) · [PROTOCOL](PROTOCOL.md) · [PHASE0_RESULTS](PHASE0_RESULTS.md) · [CALIBRATION](CALIBRATION.md) · [DECISIONS](DECISIONS.md) · [SIMULATOR](SIMULATOR.md)

---

## The governing principle

> **v.6 stays installed and working until TECNAL-Hub has run a full cultivation.**

v.6 is the reference and the fallback. Every block below either compares TECNAL-Hub against v.6
or holds it to a criterion v.6 already meets. Nothing here is a reason to uninstall v.6; the
**cultivation run in Block E** is what earns that.

**Safety first.** Blocks D–G command real actuators (motor, valves, gas, dosing pumps, heaters).
Run them with the vessel prepared for it — water or spent medium before live culture — an operator
at the `⛔ Parada segura` button, and the ability to cut power. The command arbiter and the safe-stop
were built for exactly these tests, but the first time they meet real hardware is here.

---

## Before you start — capture the v.6 baselines

These cannot be recovered later. Do them **while v.6 is still the only thing that has talked to the
box.**

| # | Capture | Why | How |
|---|---|---|---|
| B-1 | A full v.6 **command log** for a representative session (connect, set every subsystem, safe-stop) | It is the byte-for-byte comparison baseline for Block A-3 | v.6 already writes `command_logs/command_log_*.txt` — just run a session and keep the file |
| B-2 | A v.6 **telemetry/session CSV** for a real run with live sensors | Baseline for calibration, spike-filter and DOT-tracking comparisons | v.6's session log |
| B-3 | The **field calibration** currently in `v.6/preferences.json` | TECNAL-Hub ships these defaults; confirm they still match the probes in use | Copy `oxy_a/oxy_b/ph_slope/ph_intercept` and the flow `k/f/c` segments |
| B-4 | A v.6 **DOT-control run** (the manuscript dataset, or a fresh one) | Block E compares TECNAL-Hub's DOT tracking against it | v.6 session log + notes on agitation/airflow |

---

## How to read a block

Each block lists **prerequisites**, **steps**, and **pass criteria**. Record for every block: the
firmware build, the transport used (USB/Wi-Fi), the exact TECNAL-Hub version (`Directory.Build.props`),
screenshots of the relevant page, and the session CSV / Eventos export. File evidence under
`docs/evidence/hardware/<block>/`.

The blocks are in **dependency order** — a later block assumes the earlier ones passed.

---

## Block A — Link and protocol (Phase 0 close-out)

**Goal:** prove the wire is byte-identical to v.6 and stable on real sensors, and close the four
Phase-0 link-hygiene items that need real ports.

**Prerequisites:** ESP32-S3 on USB (CH343 adapter) and reachable on Wi-Fi (SoftAP `Modulo_TECNAL_1`,
192.168.4.1). v.6 uninstalled or closed for the exclusive-port tests, available for the comparison.

| # | Test | Steps | Pass criteria |
|---|---|---|---|
| A-1 | **USB link, sustained** | Auto-connect on the remembered COM port; leave running 30+ min | Continuous telemetry, no dropped link, first frame < 5 s (target < 2 s to first paint) |
| A-2 | **Wi-Fi link, sustained + ETag** | Connect to 192.168.4.1; leave running 30+ min | Continuous telemetry; `If-None-Match` 304s handled (no false parse failures); reconnect does **not** reboot the board |
| A-3 | **Byte-identical commands (Phase 0 exit crit. 3)** | Repeat the B-1 operator actions in TECNAL-Hub; capture the command frames from Eventos (each carries the exact JSON) | For every action, the TECNAL-Hub frame equals the v.6 frame **byte for byte** — key order, `v_Flow` inversion, `pHCal` quoting, `InvariantCulture` decimals |
| A-4 | **Live-sensor telemetry (Phase 0 exit crit. 4)** | Watch DOT, pH, temperature, flow with real probes across a step change | Spike filter holds through genuine steps and rejects single-sample spikes; calibrated pH/O₂ match a reference reading; the `pHCal` echo is accepted by the firmware |
| A-5 | **Configured Wi-Fi poll period** *(now wired — verify)* | Set `dataDelay` in Settings to 2000 ms and to 1000 ms; watch the network cadence | The Wi-Fi transport polls at ≈ the configured period, telemetry keeps up, no missed frames, no 304 storm |
| A-6 | **Safe busy-port handling** *(remaining item)* | Leave **v.6 connected** on the CH343 port; run TECNAL-Hub port discovery | TECNAL-Hub treats the busy port as *busy, skip* — it does **not** hijack or fault v.6's connection, and reports no false "device" on it |
| A-7 | **WMI / CH343 port ranking** *(remaining item)* | With several COM ports present, run discovery | The CH343 (matched by the `wch` manufacturer string, since CH343 is not in v.6's keyword list) is ranked and tried first; unrelated ports are not opened |
| A-8 | **Truthful round-trip timing** *(remaining item)* | Send commands on USB and on Wi-Fi; read `LastRoundTripMs` in the connection popover | Decide the honest semantics: correlate the value against the `FlowCommandAck` echo (a real confirmation) rather than timing a fire-and-forget USB write that reads ≈ 0 ms — then rename/relabel it to match |

> A-6/A-7/A-8 are the three link-hygiene items still open. The code for A-5 and immutable snapshots
> shipped in v0.20.1; A-6/A-7/A-8 were left until real ports/adapter/echo exist to validate them.

---

## Block B — Probe and flow calibration (WP3)

**Goal:** certify the calibrations software cannot certify — buffers, references and physical
interlocks. Software tests pin the math and the frames; only hardware certifies the values.
See [CALIBRATION.md](CALIBRATION.md).

**Prerequisites:** Block A passed. pH buffers, an O₂ reference (air-saturated + N₂-sparged, or a
certified probe), and an external airflow standard.

| # | Test | Pass criteria |
|---|---|---|
| B-b1 | **pH one/two-point** | Stability + averaging reach a plausible slope/intercept; the guided **full dosing safe-stop fires before the probe is removed** from the vessel (the interlock) |
| B-b2 | **O₂ two-point** | App-side two-point calibration tracks the reference at 0 % and 100 % air saturation |
| B-b3 | **Airflow curve** | Certified points averaged from distinct `FlowVoltage` frames; the 0.0545 V two-segment fit matches the standard; partial vs complete coefficient send behaves as labelled |
| B-b4 | **pHCal echo** | The accepted calibrated pH is echoed to the module as the quoted 2-decimal string and accepted |
| B-b5 | **Interlocks** | Link loss during acquisition cancels it and invalidates prepared flow state; the dosing pump never runs with the probe out of the vessel |

---

## Block C — Core-loop cultivation (Phase 1 exit)

**Goal:** the Phase 1 exit criterion — a real run controlled end to end by TECNAL-Hub with v.6
closed.

**Prerequisites:** Blocks A–B passed. Vessel with medium (a real or trial cultivation).

| # | Test | Pass criteria |
|---|---|---|
| C-1 | **Temperature holds** | Jacket control holds the temperature setpoint through the run |
| C-2 | **Agitation holds** | Motor holds the commanded rpm (note: no RPM feedback on the wire — confirm visually/by the drive) |
| C-3 | **Airflow + valves** | Flow holds setpoint; `valve_1`/`valve_2`/`v_Flow` states match physical valves; disable forces both gas valves closed and the main path shut (`v_Flow:1`, PROTOCOL §3.1) |
| C-4 | **Pressure** | Head-space pressure reference is honoured |
| C-5 | **CSV parity** | The session CSV is read **without modification** by the existing v.6 analysis scripts (frozen header, column order, decimals) |
| C-6 | **Stalled-link degradation** | If the link stalls, every readout degrades to `—` and `Última atualização` warns *before* the transport gives up |

---

## Block D — Safety kernel (WP4)

**Goal:** prove the safety behaviours that only a real link loss and real actuators can exercise.
The alarm engine, arbiter and safe-abort passed in the simulator; this is their first real link drop.

**Prerequisites:** Block C passed. An operator at `⛔ Parada segura`. A way to force a link loss
(pull USB / drop Wi-Fi) safely.

| # | Test | Pass criteria |
|---|---|---|
| D-1 | **Link-loss safe-abort** | Pull the link while a subsystem is active: automatic/recipe ownership is revoked to Manual, **one** journalled safe-abort event is written, and a command in flight times out honestly |
| D-2 | **Latched, acknowledgeable alarm** | The link-lost alarm latches, shows in the shell banner, is acknowledgeable, and clears only on a real recovery past the deadband |
| D-3 | **Audible indication + timed silence** | The audible sounds; `Silenciar` mutes it for the window (not permanently); a *fresh* alarm re-sounds through an active silence — **verify in the real control-room audio environment** |
| D-4 | **Module-offline vs link** | With the PC link healthy but the sensor module's UART down, the module-offline chip shows without claiming the PC link is lost |
| D-5 | **Command lifecycle echo** | Aeration reaches `TelemetryConfirmed` via the `FlowSetpoint`/`FlowCommandAck` echo; other actuators honestly rest at `TransportAccepted` ("sem eco") |
| D-6 | **Ownership exclusivity + bumpless transfer** | Manual ↔ Automatic transfer carries the last commanded state so there is no setpoint jump; a frame touching an actuator owned by another owner is refused whole |
| D-7 | **Frozen-data / flowmeter / sensor-absent alarms** | Each of the six system alarms latches on its real trigger with its delay/deadband |

---

## Block E — Oxygen cascade (WP5 + WP6) · the scientific gate

**Goal:** the Phase 2 exit criterion — a kLa-path-controlled run whose **DOT tracking is at least as
good as the v.6 runs** in the manuscript dataset. This is the headline result the paper depends on.

**Prerequisites:** Blocks A–D passed. A real cultivation (or a gassing-out/spent-medium proxy for a
first pass). The B-4 v.6 DOT-control baseline.

| # | Test | Pass criteria |
|---|---|---|
| E-1 | **kLa mapping from real anchors** | Enter real experimental `(Q_g, N, kLa)` points; the surface/gradient-path/headroom result is physically sensible; publish a versioned receipt and reopen it byte-for-byte |
| E-2 | **Bumpless engage** | `Ativar Automático` claims agitation/aeration/O₂ through the arbiter and starts from the currently commanded actuators with **no setpoint jump** |
| E-3 | **DOT tracking** | The cascade holds DOT to setpoint on the real process; **RMSE and settling ≥ the v.6 baseline** (B-4). Tune `Kp/Ki/Kd` from this run — the simulator gains are provisional |
| E-4 | **Manual takeover** | Taking Manual mid-run disengages the cascade cleanly and returns the actuators without a bump |
| E-5 | **Stale-O₂ safe abort** | Three blind O₂ frames disengage the cascade to the advisory display rather than actuating on a stale value |
| E-6 | **Feedback-timeout policy** | A missing flow/setpoint echo retries within the bounded policy, then safe-aborts |
| E-7 | **Live tuning surfaces** | The tuning chart and the O₂ `Cascata`/`PID`/`Saída` detail tabs render the real terms with no binding failures over a long run |

---

## Block F — Cultivation auxiliaries (WP7)

**Goal:** prove the dosing pumps and the flask agitator actuate correctly — timing, intensity and
direction — and that the antifoam/foam loop closes.

**Prerequisites:** Block C passed. Pumps and the flask agitator connected. Antifoam/foam sensor in
place.

| # | Test | Pass criteria |
|---|---|---|
| F-1 | **Nutrient dosing** | The pump runs the commanded operation/mix timing and cycles at the commanded **raw** intensity (0-99 %, **not** × 10 — that is pH-only); the commanded-only tile never shows a false healthy state |
| F-2 | **Antifoam pump** | Operation/mix/intensity command the pump correctly |
| F-3 | **Foam loop closes** | With the level/foam sensor active, foam past the reference triggers the antifoam pump per `foamStartDelay_s`/`foamPulse_s`/`foamInterval_s`; a `Parada segura` stops the pump but **leaves the sensor monitoring** |
| F-4 | **Flask agitator direction** | `Horário`/`Anti-horário` produce the physically correct rotation — confirm the signed UI value reaches the wire as magnitude (`agitatorPercent`) + direction (`agitatorDir` 1 CW / 0 CCW), with the sign never leaked |
| F-5 | **Potentiometer re-enable** | `Reativar potenciômetro` restores physical pot control as expected |
| F-6 | **Global safe-stop** | `Parada segura` stops nutrient, antifoam pump and the flask agitator in one frame, alongside the core loop and pH |

---

## Block G — Soft sensor and gain scheduling (WP8)

**Goal:** validate the conditional-OUR trace and turn the provisional gain schedule into a real one.

**Prerequisites:** Block E passed (a published kLa map + a controlled DOT run). Ideally the
manuscript's *B. subtilis* / *Serratia* conditions for a direct comparison.

| # | Test | Pass criteria |
|---|---|---|
| G-1 | **Conditional OUR trace** | Over a real cultivation, the accepted OUR intervals and the mean/max/cumulative are consistent with the manuscript's Bacillus/Serratia results (`analysis/2_our_soft_sensor`); refused intervals carry **no value**, never zero |
| G-2 | **Quasi-steady gate** | Acceptance turns on when DOT is on-band and quiet and off during transients; the causal `dDOT/dt` behaves against the real polarographic staircase |
| G-3 | **Off-map refusal** | An operating point outside the published map yields `Sem kLa` rather than an extrapolated OUR |
| G-4 | **Gain-schedule field values** | The default breakpoints are provisional simulator numbers. Using the E-3 tuning, set real (effort → Kp/Ki/Kd) breakpoints; confirm the slew-bounded transitions and that each segment crossing is journalled to Eventos |
| G-5 | **Scheduling vs single set** | Confirm the paper's finding on the real loop: whether the single robust set suffices, or scheduling measurably improves DOT tracking across the kLa range |

---

## Block H — Biomass, external pump and Receitas ownership (Phase 3)

**Goal:** close the merged Phase 3 hardware behavior and prove that the recipe owner, manual UI and
global safety action agree about what reached the wire.

**Prerequisites:** Blocks A–D passed. Biomass probe and external pump connected; a short validated
recipe that actuates at least one core actuator and one dosing/pump path.

| # | Test | Pass criteria |
|---|---|---|
| H-1 | **Biomass procedure** | Enable → blank → start → stop and low/high/optimal thresholds reach the firmware exactly; live raw/Abs/IT/PWM update; focus loss alone does not send staged thresholds |
| H-2 | **External-pump profiles** | Constant, linear, exponential, polynomial and piecewise frames match the preview and physical flow/accumulated volume within the agreed tolerance |
| H-3 | **Proportional gas** | `Q_g=(V_0+V_p)·vvm` reaches aeration when Manual owns it; a cascade-owned refusal is shown honestly and the unchanged target retries after ownership returns |
| H-4 | **Recipe UI ownership** | Starting a recipe visibly disables every conflicting manual editor/action with `receita` provenance; stopping/aborting restores it without losing staged values |
| H-5 | **Global safe-stop during Recipe** | Pressing `Parada segura` during an active recipe produces one accepted safe frame under valid ownership, stops the recipe, returns owners to Manual and never reports success after refusal/failure |
| H-6 | **Recipe pump mapping** | Pump-block fields, the external-pump target and vvm→L/min coupling actuate the intended physical subsystem with captured command/event receipts |

---

## What the simulator already covered (so hardware can skip re-proving it)

The device simulator ([SIMULATOR.md](SIMULATOR.md)) already exercised, headlessly and against a
localhost/serial device model: the parser, spike filter and calibration path (it emits raw ADC
counts); both transports incl. the buffered-`OK` and 304 quirks; the command arbiter, alarm
latching and safe-abort on a simulated link drop; the cascade arming, engaging, tracking and safe
abort; the dosing frames; and the OUR inversion + acceptance against a 40-row manuscript oracle.
Hardware does not need to re-prove the **software** logic — it needs to prove the **physics, the
electrical actuation, the real sensors, the timing and the audio**, which no simulator can.

---

## Priority order if time is short

1. **Block A (link + byte-identical commands)** and **B-3/B-4 v.6 baselines** — irreplaceable.
2. **Block C (core-loop cultivation)** — the Phase 1 exit; earns closing v.6.
3. **Block E (cascade DOT tracking vs v.6)** — the scientific gate the paper depends on.
4. **Block D (safety on a real link loss + audio)** — required before trusting automatic control.
5. **Block H** — the merged Phase 3 devices and Recipe/global-stop ownership boundary.
6. **Blocks F, G, and A-6/A-7/A-8** — the remaining auxiliaries, the OUR/scheduling field tuning,
   and the last link-hygiene items.
