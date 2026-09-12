# LED self-heating characterization

Measured 2026-08-06 on the v.02 device (ESP32-S3 + VEML7700), firmware v4.1,
**empty beam** — no fluid, no colorant, no biomass.

## Why

At short sampling intervals the readings drifted and the instrument appeared to
lose calibration. The suspected cause was LED self-heating. These runs quantify
it and set the safe operating limit now enforced in firmware v4.2.

## Method

The LED is pulsed, not continuous: it is energised for a settle time plus the
integration window per reading, then switched off. The heating driver is
therefore the **duty cycle** — that on-time as a fraction of the sampling
interval — not the interval by itself.

> The on-time per reading has since grown twice, for reasons unrelated to
> heating. It was `10 ms + IT + 5 ms` when these runs were made (v4.1),
> `10 ms + 2×IT + 8 ms` in v4.4/v4.5, and is `10 ms + 2×(1.15×IT + 8 ms)` in
> v4.6 — see the firmware changelog. **The duty-vs-drift curve below is
> unaffected**, because it is a function of duty cycle, not of how the duty is
> composed. Only the interval that a given duty corresponds to has changed.

The gear was locked in manual mode at **IT 200 ms / PWM 50 %** for every phase
(on-time 215 ms). Without the lock, auto-ranging would have changed gear
mid-experiment and confounded the drift with a gear change. Each stress phase
was held for roughly three thermal time constants to reach steady state, with a
recovery phase between them.

## Result

| LED duty | interval | raw drift | spurious absorbance |
|---|---|---|---|
| 4.3% | 5000 ms | −0.04% | 0.0002 AU |
| 6.0% | 3583 ms | +0.26% | within noise |
| 8.0% | 2688 ms | −0.34% | 0.0015 AU |
| 9.0% | 2389 ms | −0.45% | 0.0020 AU |
| **10.8%** | **2000 ms** | **−1.00%** | **0.0044 AU** ← knee |
| 21.5% | 1000 ms | −1.62% | 0.0071 AU |
| 43.0% | 500 ms | −2.62% | 0.0115 AU |

Drift is flat to about 9% duty, then turns sharply.

The positive value at 6% is residual recovery from the preceding phase, not a
real gain — read it as zero. It also shows the measurement floor: individual
phases carry roughly ±0.3% of slow baseline movement, so only the 10.8% row and
below it are resolved above noise.

**It is heating, not damage.** Output recovered every time the duty dropped,
with a fitted time constant of ~95 s in both directions.

**Why it looks like lost calibration.** I₀ is captured during blanking at one
thermal state. If the LED then runs hotter, its output falls, and the firmware
divides a reduced I by an I₀ that was measured when the LED was cooler. The
difference appears as absorbance that is not there. At OD 1.0 an error of
0.012 AU is negligible; at OD 0.05 — early exponential growth — it is ~24%.

## Consequences, implemented in firmware v4.2

- Sampling intervals that would exceed an **8% LED duty are refused**.
- The floor assumes the **longest integration time auto-ranging can reach**
  (800 ms), because an interval that is safe at IT 100 ms is 8× too fast at
  IT 800 ms.
- Locking a short IT manually lowers the floor, since the gear can no longer
  move.
- The device reports `min_refresh_ms` and `led_duty_limit` in `/api/status`, and
  the desktop app bounds its interval control to them.

The floor tracks the on-time, so it has moved as the on-time did — **read it
from `min_refresh_ms`, not from this file**:

| firmware | worst-case on-time at IT 800 ms | auto-mode floor |
|---|---|---|
| v4.2 | 815 ms | ~10.2 s |
| v4.4 / v4.5 | 1618 ms | ~20.2 s |
| v4.6 | 1866 ms | ~23.3 s |

v4.6 budgets the worst case but anchors each read on an observed conversion
boundary, so the *typical* on-time is roughly 1.7×IT rather than a flat 2×IT —
the device runs cooler than v4.5 at the same interval even though its declared
floor is higher.

## Operating guidance

- After changing the sampling rate, allow ~5 minutes (≈3 time constants) before
  trusting absolute values.
- **Blank at the same sampling interval you will measure at**, so I₀ is captured
  in the same thermal state. This matters more than the absolute rate.

## Files

| File | Contents |
|---|---|
| `led_thermal_sweep.csv` | Main sweep: 5 s / 0.5 s / 1 s / 2 s with recoveries |
| `led_thermal_knee.csv` | Follow-up resolving 6 / 8 / 9% duty |
| `led_thermal_timeseries.png` | Raw counts over the whole main sweep |
| `led_duty_vs_drift.png` | Combined duty-vs-drift curve, both runs |
| `run_thermal_sweep.py` | Main experiment (needs the device on COM5) |
| `run_thermal_knee.py` | Knee experiment |
| `analyse.py` | `python analyse.py <csv> <out.png>` |

Re-running either script drives the device directly and leaves it stopped, in
auto-range, at a safe interval.
