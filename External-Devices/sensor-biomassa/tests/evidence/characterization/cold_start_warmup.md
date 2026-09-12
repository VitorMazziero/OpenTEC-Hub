# Cold-start warm-up — a known limitation

Measured 2026-08-06 on the v.02 device (ESP32-S3 + VEML7700, COM5), firmware
v5.0 and v5.1, **clear media, blank against blank** — no colorant, no biomass.

Status: **partly fixed.** Firmware v5.2 and the matching app add the paced
("slow") blanking sweep described under fix C below, which removes the sweep's
own heat pulse from the calibration. The cold-start component — the instrument
warming from power-on with the blank frozen at the cold state — is **still
unaddressed**; the workaround is to warm up for ~30 minutes before zeroing.

What follows is the measurement, the mechanism as far as the data supports it,
and the design problem behind the part that remains.

## The limitation in three lines

A blanking sweep run on a cold instrument captures I₀ at a temperature the
instrument will not stay at. Over the following ~25 minutes the light falls
about 2.4 %, and with I₀ frozen at the cold value that loss reads as **+12 mAU
of absorbance that is not there**. Wait ~30 minutes before zeroing and the
residual is under 0.5 mAU.

## What was measured

Two runs, same optics, same gear (IT 400 ms / PWM 18 %), 25 s cadence.

| | run A | run B |
|---|---|---|
| boot | #27 | #29 (immediately after a firmware flash) |
| blank taken at | ~6 min after boot | ~25 s after boot |
| I₀ | 35148 | 34814 |
| excursion | +3.5 mAU over ~45 min | **+12.5 mAU over ~25 min** |
| settled value | 0.00314 AU | (run ended at 24 min) |

The colder the instrument when the sweep runs, the larger the excursion that
follows. Run A's blank was taken six minutes into a boot and moved a third as
far as run B's.

### Run B in detail

57 samples over 24.0 min. `raw` falls **34847 → 34002**, i.e. **−2.42 %**
(−845 counts) at a fixed gear and a fixed drive current. At a typical LED
output coefficient of 0.3–0.5 %/K that is roughly **6 K** of warming.

Absorbance: −0.41 mAU → +10.25 mAU.

## The shape matters more than the size

Three models fitted to run B:

| model | rms residual |
|---|---|
| single exponential from t=0 | 1.622 mAU |
| two coupled time constants | 1.080 mAU |
| **flat for 7.5 min, then exponential with τ = 7.0 min** | **0.391 mAU** |

The best description has a **dead time**: nothing moves for the first
~7.5 minutes, then the reading rises with a 7-minute time constant to a total
of +12.5 mAU.

That dead time is the useful diagnostic. An LED junction reaches its new
temperature in seconds, so a pure "the LED heats during each pulse" effect
would start moving immediately and decay from t=0. A lag of seven minutes is
heat **diffusing through mass** to reach the optical path — board, LED slug,
housing. Run B began about a minute after a power cycle, so what is being
watched is the whole instrument warming from cold, not the LED alone.

Whether the black enclosure contributes is **not established**. A closed dark
box holding heat is plausible, but nothing measured here separates enclosure
from board, and it should not be asserted without a test that varies it.

## The blanking sweep is itself a heat pulse

Computed from the firmware's own timing (`ledOnMsFor`, `integrationGuardMs`)
and the stored blank table, counting only the cells the sweep actually pulses
(a saturated row short-circuits the rest of its PWM levels):

| IT | cells pulsed | LED on-time | PWM·s |
|---|---|---|---|
| 100 ms | 8 | 1.62 s | 46.3 |
| 200 ms | 7 | 2.67 s | 49.3 |
| 400 ms | 6 | 4.45 s | 53.4 |
| 800 ms | 4 | 5.85 s | 32.2 |
| **total** | **25** | **14.6 s** | **181.1** |

Delivered in about 24 s of wall time. Steady-state measuring at the working
gear is 0.74 s of on-time at PWM 18 % every 24.3 s.

> The sweep delivers as much LED energy as **5 minutes** of normal measuring,
> and delivers it in 24 seconds — an average rate **14×** the steady state.
> LED duty is ~60 % during the sweep against **3.1 %** while measuring.

So I₀ is captured *during* a thermal spike, and the 32 cells are each captured
at a different point along it. This is a second, separate error from the
cold-start one, and warming the instrument up before the sweep does not remove
it.

## What the operator would have to wait

From run B's fit, the baseline movement still ahead once the LED has been
pulsing for:

| warm-up | still to come |
|---|---|
| 20 min | 2.1 mAU |
| 25 min | 1.0 mAU |
| 30 min | 0.5 mAU |
| 35 min | 0.25 mAU |

For reference, a settled instrument holds **0.069 mAU** point-to-point and
drifts **0.05 mAU/day** (run A, 2.2 h settled). So the warm-up error is the
dominant term by two orders of magnitude, and everything else about the
instrument's stability is excellent.

## Why "wait until the readings look stable" does not work

The obvious software fix is to watch the trailing drift and declare the
instrument settled when it falls below a threshold. **Tested against run B, it
fails**, because during the dead time the readings genuinely are stable.

Each detector below was given a threshold at 4× its own noise floor
(point-to-point noise 0.069 mAU, cadence 26 s):

| detector | noise floor | threshold | first fires |
|---|---|---|---|
| block step, n=4 | 0.049 mAU | 0.20 mAU | 3.3 min |
| block step, n=6 | 0.040 mAU | 0.16 mAU | 5.5 min |
| block step, n=8 | 0.035 mAU | 0.15 mAU | 6.3 min |
| trailing slope, n=8 | 1.50 mAU/h | 5.98 mAU/h | 3.7 min |
| trailing slope, n=12 | 0.81 mAU/h | 3.26 mAU/h | 5.5 min |
| trailing slope, n=16 | 0.53 mAU/h | 2.11 mAU/h | 6.3 min |

Every one of them clears **before 7.5 min — before the transient begins**, with
the entire +12.5 mAU still ahead. Longer windows (n=10 step, n=20 slope) never
fire inside the 24-minute run, because they are still averaging the steep part
when the data ends.

**Conclusion for any future implementation:** a stability test cannot be the
primary gate. It has to sit behind a minimum warm-up time, and serve only as
confirmation that the minimum was long enough.

## The re-zero problem, and why one cell is not enough

The natural companion fix is to re-measure I₀ after the instrument has settled.
The question is how much of the table to re-measure.

### A. Re-measure only the gear in use — **rejected**

Writes one cell of the 4×8 table. The gear in use becomes correct; the other
31 cells still hold the cold-sweep values. As soon as auto-ranging changes gear
— which is the normal course of a cultivation as it gets denser — absorbance
**steps** by the difference between the two cells' thermal errors. Trading a
smooth 12 mAU offset for a discontinuity mid-curve is a bad trade: an offset
can be subtracted afterwards, a step in the middle of a kinetics fit cannot.

### B. Re-measure one cell, rescale all 32 by the ratio — **plausible, unverified**

Take one pulsed read at the current gear once settled, compute
`f = I_now / I₀_stored`, multiply every valid cell by `f`.

The argument that this is sound: per-gear self-heating *during* a pulse is
repeatable, happens identically whenever that gear is used, and therefore
cancels between the blank and the measurement. What does not cancel is the
bulk board/ambient temperature difference between blank time and measurement
time — and that acts as a common multiplicative factor on LED output across
all gears.

What it does **not** correct is any intra-sweep thermal gradient (§ "the sweep
is itself a heat pulse"), which is not common-mode. That residual is probably
small, because with a 7.5-minute lag most of the sweep's heat reaches the
optics well after the 24-second sweep has finished — **but this has not been
measured.**

Safety hazard, and it is a serious one: the refresh read must be taken with
**clear media in the beam**. A culture in the path would be folded into the
blank, silently and permanently. A ±5 % sanity clamp on `f` rejects anything
above ~0.022 AU, which is not sufficient on its own — this needs the same
operator confirmation as a full blanking sweep, and probably a hard rule that
it can only run inside the warm-up flow, before the sample is loaded.

### C. Thermally-settled ("slow") sweep — **implemented in v5.2**

Pace the sweep instead of running it back to back, so every cell is captured
under the same thermal load. Removes the intra-sweep gradient and the sweep's
own heat pulse, and needs no assumption about common-mode behaviour.

Shipped as `{"command":"blank","duty_pct":D}`. After each cell that actually
pulsed the LED, the routine idles with the LED off for
`ledOnMsFor(itMs) × (100/D − 1)` — derived from that cell's own on-time, so a
long integration slot waits proportionally longer. At `D = 8`, the firmware's
own `LED_DUTY_LIMIT`, the sweep takes about three minutes and runs at the same
duty the firmware already permits for continuous measuring.

The idle is chunked and checks the abort flag, so "stop" during a sweep is not
made to wait out a 22-second gap. Omitting `duty_pct` reproduces the old
behaviour exactly, so a client written against v5.1 is unaffected.

`/api/blank` now reports `sweep_duty_pct` and `sweep_ms`, and the app copies
them into each run's `meta.json`, so a dataset says how its I₀ was produced.
Both read -1/0 after a reboot: the table survives in NVS, the knowledge of how
it was swept does not.

Not done: pacing at the *measurement* duty (3.1 %) rather than the duty ceiling
(8 %), which would take ~8 min. Whether the remaining 2.6× matters is open
question 1 below.

### D. Temperature-referenced correction — **needs data first**

The firmware computes `soc_temp_c` but publishes it **only in the status
object** — never in the sample stream, the ring buffer, or the CSV. Adding it
per sample is the prerequisite for correcting I₀ against temperature, which
would remove the wait entirely rather than merely making it explicit.

Unproven: the ESP32 die sensor is a coarse proxy for LED and optics
temperature, it is affected by the SoC's own load, and its absolute accuracy is
poor (it read 50.8–53.8 °C on a device that had just been powered on cold).
Whether it tracks the optical drift tightly enough to correct against is an
open question, and cheap to answer once the field is logged.

## What to do today

- **Use the slow sweep** (New experiment, or the Blank button) for anything you
  intend to fit. The fast one is for a range map on an already-warm instrument.
- **Blank at the same interval and gear you intend to measure at**, after the
  instrument has been pulsing for **~30 minutes**. This is the existing README
  advice; the number is now measured rather than guessed. The slow sweep does
  not remove this — it fixes the sweep's own heat, not the instrument's.
- If a run was blanked cold, the offset is stable once settled — it can be
  subtracted as a constant. `raw` and `i0` are both in the CSV, so absorbance
  is recomputable against any later I₀.
- Do not read a few mAU of baseline as an instrument fault. Settled, this
  device is holding 0.07 mAU.

## Open questions

1. **Is the intra-sweep gradient real, and how large — and is 8 % duty slow
   enough?** Test: sweep fast, then sweep slow, and compare the two tables
   cell by cell. A ratio that is flat across all cells means the fast sweep
   was already common-mode and the pacing bought nothing; a ratio that varies
   systematically with sweep order is the gradient, and its size says whether
   8 % is enough or the pacing should go to the measurement duty (3.1 %,
   ~8 min).
2. **Does `soc_temp_c` track the optical drift?** Test: log raw counts and SoC
   temperature together from a cold start for ~40 min, at a fixed gear. A tight
   correlation makes fix D viable.
3. **Is the 7.5-minute lag reproducible, and does it move with ambient
   temperature?** It is currently a single observation from a single run.
4. **How much of the warming is the enclosure?** Would need a run with the
   housing open against one closed.

## Provenance

| | |
|---|---|
| device | v.02, ESP32-S3 + VEML7700, COM5, boot #27 and #29 |
| firmware | v5.0 (run A), v5.1 (run B) |
| run A | `transmitance_app/runs/` — recovered from the device ring buffer, 421 samples, 19:59–23:00 |
| run B | `transmitance_app/runs/20260806_231210_run/`, 57 samples, 23:12–23:36 |
| gear | IT 400 ms / PWM 18 %, auto-range on, 25 s cadence (24.3 s device floor) |

### A note on reading run B's metadata

`meta.json` for run B records `blank_table.i0[2][4] = 34905`, but every row in
`data.csv` divides by **34814**. The recorder captured the blank table when it
opened, which for a new experiment is *before* the blanking sweep that
experiment asked for — so the metadata documents the calibration the run
replaced, not the one its rows were computed against. Trust the `i0` column,
not the table, for that file.

(That one is a straightforward bug and has since been fixed in the app, along
with the same gap on the manual **Blank** button.)

## Related

- `characterization/README.md` — LED duty-vs-drift, the fast per-reading
  heating this document's slow transient sits on top of.
- `transmitance_app/pc_client/README.md` — "LED heating and the sampling
  interval".
