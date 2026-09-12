# v5.0 validation

Evidence that the transmittance device measures what it claims to, gathered
2026-08-06 on the v.02 hardware (ESP32-S3 + VEML7700, COM5), **empty beam**.

## What was wrong

Three defects, found in one session and fixed across v4.6 and v5.0.

**The stored blank was the dark.** The table in NVS held real light only in its
100 ms row; the 200/400/800 ms rows were 0–109 counts. It had been written by a
pre-v4.6 build and survived three firmware upgrades because nothing checked
which measurement path produced a stored blank. Worse, `blankIsValid()` only
required a cell to be non-zero, so a gear whose I₀ was 9 counts counted as
usable and the auto-ranger could pick it.

**The integration time is a label, not a period.** v4.5 waited exactly 2× the
*nominal* integration time, reasoning that conversion boundaries are one period
apart so one must fall inside the LED pulse. The reasoning is right; the premise
is not. Measured here, the real period is **1.092× its label in every slot**.
Where the period ran longer than assumed, the wait landed before the second
boundary and returned the first — a conversion that began before the LED came
on. That is a reading several percent low, appearing at random rather than as
drift, i.e. 0.02–0.05 AU of absorbance that is not there.

The VEML7700 datasheet (Rev. 1.0, doc 84286) shows an internal oscillator in its
block diagram and publishes **no tolerance for it** in the Basic Characteristics
table, and no temperature coefficient. There was never anything to design
against.

**The fix.** A read now anchors on a conversion boundary it *observes* — LED on,
poll the data register until it leaves the dark, then wait one guarded period —
so accuracy no longer depends on the period being what the label says.
`IT_PERIOD_GUARD` (1.20 in v5.0) survives only as a timeout and a worst-case
thermal budget.

## The plots

| Plot | What it shows |
|---|---|
| `dropouts_before_after.png` | The headline. Worst reading below the median, per gear, v4.5 vs v5.0. |
| `period_vs_label.png` | The cause. Real conversion period ÷ its nominal label, against both guards. |
| `period_vs_temperature.png` | The period through a hard LED heating cycle. **Read the caveat below before quoting this one.** |
| `it_linearity_led_held.png` | That the sensor is sound: with the LED held on, counts double per IT step. |
| `blank_table_before_after.png` | The stale table against the one v5.0 measures. |

## Reproducing

Run in this order — it is not arbitrary. The blank has to exist before anything
reads through it, reads have to be taken cold, and the temperature sweep
deliberately heats the hardware, so it goes last.

```bash
python ../verify_firmware_on_device.py
```
```bash
python collect_validation_data.py
```
```bash
python run_temperature_sweep.py
```
```bash
python make_plots.py
```

`make_plots.py` skips any plot whose input is missing rather than failing, so it
is usable part-way through.

**The temperature sweep drives the LED at 100 % duty for 6 minutes** to move the
board temperature. That is ordinary continuous operation for the LED — the 8 %
duty limit elsewhere in this project protects *measurement stability*, not the
part — but the hardware will be warm afterwards. Let it cool before trusting
absolute absorbance; the LED's thermal time constant is ~95 s (see
`../README.md`).

## Data provenance

| File | Notes |
|---|---|
| `data/v45_pulsed_reads_before.csv` | **Summary statistics only.** The v4.5 session recorded n, min, median, max and the individually identified dropouts; the other ~25 in-family readings per slot were never stored and are deliberately not listed. Reproducing per-reading detail needs a reflash of v4.5. |
| `data/blank_table_stale_pre_v46.csv` | The stale table as read off the device before any v4.6 change. |
| `data/v46_period_measured.csv` | Three `probe_period` runs on v4.6. No temperature column — `soc_temp_c` arrived in v5.0. |
| `data/v50_*.csv` | Written by the scripts here. |

## The temperature test did not do what it was designed to do

Run 2026-08-06. **It is a null result, and the honest summary is: the guard's
temperature robustness remains untested.**

What happened: six minutes of LED at 100 % duty moved the only temperature this
board can report — the ESP32 die — by **2 °C**, and the reading then kept
*rising* through the six-minute cooling phase (55.8 → 56.8 °C). So the proxy
never tracked the optical assembly at all; it tracked the SoC's own dissipation
and the room. Total span across the whole run: 3.0 °C.

The VEML7700 exposes **no temperature register** (its registers are 0x00–0x06),
and the LED junction has no sensor either, so there is nothing better to read.

What the run does establish, which is worth having:

- Across cold → hot → recovered, the period/label ratio stayed in 1.085–1.106
  on every slot, against a guard of 1.20.
- The period is unaffected by the heating **the device does to itself**, which
  is the thermal excursion it will actually see in normal operation.

What it does **not** establish:

- Any temperature coefficient, in ppm/°C or otherwise.
- Any bound on behaviour at an ambient this bench never reached. A cold room or
  a warm incubator is entirely outside what was tested.

To actually test it, change the *ambient* — a fridge, an incubator, a heat gun
at a sane distance — let it settle, and re-run `probe_period`; it reports
`within_guard` per slot. Until then, the 1.20 guard rests on a 9.9 % margin over
one bench temperature, which is why it is 1.20 and not 1.15.

## Chart conventions

Categorical hues are taken in fixed slot order from the project's reference
palette (blue `#2a78d6`, orange `#eb6834`) and never cycled; magnitude uses a
single-hue blue ramp; reserved status red marks limits and thresholds only,
never a data series. One y-axis per plot, solid hairline grid, labels in text
ink rather than series color.

The palette validator (`scripts/validate_palette.js`) **could not be run** — no
JavaScript runtime is installed on this machine. These are the reference
palette's slots 1–2 used verbatim, which that palette documents as validated
all-pairs in light mode (worst pair CVD ΔE 9.2, normal-vision ΔE 24.0). Re-run
the validator before substituting any other hues.
