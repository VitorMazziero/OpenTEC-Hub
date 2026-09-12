# Biomass Sensor desktop app

Desktop control, live plots, and CSV logging for the VEML7700
transmittance/biomass sensor. The complete control surface targets the latest
**`biomass-sensor` firmware (v5.3)**. Basic logging works with
v4.0. Anything the running firmware cannot do is visibly disabled rather than
silently ignored: manual controls need v4.1, the conversion-period diagnostic,
the anchoring counter and the SoC temperature need v5.0, clearing the device's
sample buffer needs v5.1, and the slow blanking sweep needs v5.2.

## Install

```bash
python -m pip install -r pc_client/requirements.txt
```

For an unattended lab PC you can install only `pyserial` and `requests` and
always pass `--headless`; PySide6/pyqtgraph are needed for the GUI only.

## Run the desktop app

From the `software` folder, the normal user entry point is:

```bash
python app.py
```

With no arguments, the app opens a connection dialog. USB is recommended for
long runs; WiFi connects to the device AP at `192.168.7.1`.

To explore the complete interface without a connected sensor, run:

```bash
python app.py --demo
```

Demo mode opens with a populated plot and a simulated v4.1 device. Press
**Start** to generate live samples. The title and header remain marked
`DEMO`/`SIMULATED`, and recording does not start automatically, so generated
data cannot be confused with a hardware run. You can start a demo recording
manually from the Run tab if needed.

## Starting an experiment

Opening the app records nothing. It connects, pulls whatever the sensor still
holds in its own memory, and draws it, so you can see what the instrument has
been doing — but no folder is created and no file is written until you ask for
one. Pass `--autosave` for unattended logging that starts at connection.

**New experiment** (Run tab) is the normal way to begin. It closes the current
data file, clears the plot, restarts the elapsed clock, optionally re-zeros the
instrument with a blanking sweep, and begins recording into a folder you pick.

This matters because the plot and the recording are separate concerns: without
it, starting a second run leaves the previous curve on screen and the new
points land hours along the x axis.

It also offers to **start from the samples already in device memory**. The
sensor keeps its last 1024 samples in RAM whether or not a PC is listening, so
an experiment that was already running before you opened the app is not lost:
tick the box and those samples are written into the new run with the times they
were actually measured. It is off by default — a new experiment starts now.

**Run name and folder** apply to the file being written, not just to the next
one. Change either while a run is recording and the app asks whether to move
the samples already saved into the new file (the folder they came from is then
removed) or to leave them where they are and start a new, empty file.

**Stop recording** sits under New experiment. It closes the CSV; the device
keeps measuring, and its buffer keeps the samples, so a run started later can
still recover them.

**Clear device memory** (Advanced tab) drops that buffer. Use it when you want
certainty that a new experiment cannot pick up the previous one. It needs
firmware v5.1; older firmware ignores the command, and the app says so rather
than reporting a success that did not happen.

## Zeroing: fast sweep or slow sweep

Both **New experiment** and the **Blank** button offer two ways to run the
blanking sweep. Clear media must be in the beam for either.

| | fast | slow |
|---|---|---|
| duration | ~25 s | ~3 min |
| LED duty during the sweep | ~60 % | 8 % |
| every gear zeroed at the same temperature | no | yes |

The sweep drives the LED far harder than measuring does. Back to back it
delivers as much heat in 25 seconds as **five minutes** of normal measuring, so
each gear is captured at a different point along a rising temperature, and the
finished table belongs to a thermal state the instrument leaves as soon as it
starts measuring. Measured on this hardware, a blank swept on a cold instrument
left **+12 mAU** of absorbance that was not there, decaying over ~25 min.

The slow sweep paces itself: after each gear it idles with the LED off, long
enough to hold the whole sweep at 8 % duty — the same ceiling the firmware
enforces for continuous measuring. Every cell is then zeroed under the load it
will be used at.

Use the slow sweep for anything you intend to fit. The fast one is for a quick
range map on an instrument that is already warm. It needs firmware v5.2; on
older firmware the option is disabled rather than sending a parameter the
device would ignore.

Warming the instrument up for ~30 minutes before zeroing removes the remaining
cold-start error; see `characterization/cold_start_warmup.md`.

**Clear plot** (below the graph) discards only what is drawn. The recorded file
is untouched — use it to re-scale the view during a long run.

## Reading the absorbance plot

The y axis is labelled in absolute AU and never factors a power out of the tick
labels, because `3` under a small `×10⁻³` reads as a thousandfold larger drift
than the one on screen. The visible window is also floored at 10 mAU, so a flat
trace looks flat instead of being autoscaled until sensor noise fills the plot,
and the current window is printed beside the point count.

For scale: on this hardware a settled blank-against-blank measurement holds to
about **0.07 mAU** point to point, so a full 10 mAU window is already deep into
noise territory.

## The log

The device narrates what it is doing on the same wire it sends data on — a gear
search is twenty lines, a sweep is thirty — and every setting the window sends
is echoed back. Both are off by default and switched on with **Device output**
and **Commands sent** above the log. Anything the app itself has to say,
including every failure, is never filtered; the count on the right says how
much is being held back.

## LED heating and the sampling interval

The LED is pulsed: it is on only for `10 ms + integration time + 5 ms` per
reading. What heats it is the *duty cycle* — that on-time as a fraction of the
sampling interval. Measured on this hardware with an empty beam and the gear
locked at IT 200 ms / PWM 50 %:

| LED duty | interval | raw drift | spurious absorbance |
|---|---|---|---|
| 4.3% | 5000 ms | −0.04% | 0.0002 AU |
| 10.8% | 2000 ms | −1.00% | 0.0044 AU |
| 21.5% | 1000 ms | −1.62% | 0.0071 AU |
| 43.0% | 500 ms | −2.62% | 0.0115 AU |

The junction warms, output falls, and since I₀ was captured during blanking at
one thermal state, the loss reads as absorbance that is not there. It is
heating rather than damage — output recovers within minutes, with a fitted time
constant of about 95 s in both directions.

The firmware therefore refuses intervals that would push the LED past an 8%
duty, and the app's interval slider is bounded by the floor the device reports.
**The floor depends on the longest integration time the device could select.**
While auto-ranging is on that is the 800 ms slot, giving a **24.3 s minimum**
on v5.0; locking a short integration time in the Optics tab lowers it. The
default interval is **25 s** — the round value just above that floor.

The floor grew as the firmware learned to time a reading properly: it was
~10.2 s when the LED was held on for one nominal integration time, and is
24.3 s now that a reading budgets two *guarded* conversion periods. Typical
LED on-time is lower than that budget, because a read stops as soon as it has
seen a real conversion boundary.

Two practical consequences:

- After changing the sampling rate, allow ~5 minutes (≈3 time constants) before
  trusting absolute values.
- Blank at the same sampling interval you intend to measure at, so I₀ is
  captured in the same thermal state.

## Language

Use the **Português (Brasil)** button in the connection dialog or app header to
change the complete interface while it is running. The same button becomes
**English** for switching back. The connection, plotted data, recording, and
device state are preserved during the change, and no command is sent to the
sensor.

To start directly in Brazilian Portuguese:

```bash
python app.py --demo --language pt-BR
```

| Command | What it does |
|---|---|
| `python app.py` | Opens the desktop app and connection dialog. |
| `python app.py --auto --name ensaio01` | Probes USB, then the device AP. |
| `python app.py --serial COM5 --name ec01` | Connects to an explicit USB port. |
| `python app.py --http` | Connects over the device's own WiFi AP. |
| `python app.py --demo` | Runs the full interactive UI without hardware. |
| `python app.py --language pt-BR` | Opens the interface in Brazilian Portuguese. |
| `python app.py --autosave` | Records from the moment it connects, buffer included. |

For unattended operation without a window, use the separate logger path:

```bash
python pc_client/biomass_logger.py --auto --headless --start --name ensaio01
```

`--no-save` disables recording (viewing only). `--outdir` changes where run
folders go (default `./runs`).

## Output

Each run creates `runs/<YYYYmmdd_HHMMSS>_<name>/` containing:

**`data.csv`** — one row per sample, flushed immediately (a power cut costs you
the last sample, not the last six hours):

| column | meaning |
|---|---|
| `iso_time`, `epoch` | wall clock, reconstructed from `t_ms` via the anchor in `meta.json` |
| `t_ms` | device uptime when the sample was taken |
| `seq`, `boot_id` | monotonic sample number and boot counter — a `seq` jump means samples were lost, a `boot_id` change means the device reset |
| `absorbance` | `-log10(raw/i0)`, computed on-device |
| `transmittance` | `raw/i0` |
| `raw`, `i0` | measured signal and the blank used for it |
| `it_ms`, `pwm_pct` | the auto-ranging gear in use |
| `hd_mode`, `saturated` | high-density mode active / sensor saturated |
| `valid` | 0 when absorbance is one of the firmware's error sentinels |

**`meta.json`** — run name, start/end, firmware and device status at connect,
the full 4×8 blanking table (I₀) that was in effect, the time anchor, and the
threshold configuration. A run that was moved also carries `moved_from` and
`moved_samples`.

### How timestamps are reconstructed

Samples are not dated on arrival. Backfilled ones are older than the moment
they reach the PC — sometimes by hours — so every row is dated from the device
clock instead: `iso_time = time_anchor_epoch + t_ms/1000`, where the anchor is
the wall-clock instant of device `t_ms = 0`, derived once per boot from the
`uptime_ms` the device reports in its status.

It must come from the uptime. Anchoring on the first sample seen assumes the
oldest record in the buffer was measured just now, which throws the whole run —
backfill and every live sample after it — as far into the future as the device
had been running. `meta.json` records which source was used in
`time_anchor_source`; `uptime_ms` is the normal one.

Because `raw` **and** `i0` are both recorded, absorbance is recomputable
offline if you later decide the blank was bad:

```python
import pandas as pd, numpy as np
df = pd.read_csv("runs/20260804_101500_ensaio01/data.csv", parse_dates=["iso_time"])
df["hours"] = (df.epoch - df.epoch.iloc[0]) / 3600
df["A_recalc"] = -np.log10(df.raw / NEW_I0)
```

## Gear selection

The device chooses an integration time and LED level ("gear") to keep the
reading in range. Two rules matter when reading your data:

- **Only gears with a valid blank are used.** A gear whose blank saturated
  during the sweep stores 65535 as I₀ and can never yield absorbance. The
  blank viewer (Advanced tab) marks those cells; the highlighted cell is the
  gear currently in use.
- **Smart Start begins at the brightest valid gear inside the threshold band.**
  Absorbance only rises as biomass accumulates, so starting near the top of the
  band maximises how far a run gets before the first gear change. Each change
  puts a small step in the trace, so fewer is better.

Total measurable absorbance is set by the brightest gear with a valid blank and
the noise floor — not by where you start. If you need more range, the lever is
the optics, not the starting gear.

## Absorbance error sentinels

Straight from the firmware, kept as-is so the CSV matches what the device said:

- `-99.0` — the blank for that gear was 0 or saturated
- `9.9` — measured signal was 0

Both are flagged `valid=0`. The GUI plots them as gaps rather than spikes.

## Gap recovery

The device keeps the last **1024 samples** in RAM. If the PC misses samples —
laptop slept, USB unplugged, WiFi dropped — the client detects the `seq`
discontinuity and pulls the missing range from the device buffer, so the CSV
has no holes as long as the outage was shorter than the buffer (**~7 hours**
at the default 25 s cadence). Longer than that and the oldest samples are gone;
the client logs exactly which `seq` range was unrecoverable.

The buffer survives the app closing but not a reset, and it is the only copy of
anything measured while nothing was recording. To keep it, start a new
experiment with **start from the samples already in device memory** ticked
before power-cycling or reflashing the device.

## Serial vs WiFi

**USB serial is the more reliable choice for unattended runs.** No reconnect
logic, no AP contention, and it powers the device. Over serial the firmware
pushes each sample as it is taken, so the client does not poll at all.

The AP (`BiomassSensor`, open, `192.168.7.1`) is for untethered bench work.
Note that while your laptop is associated with it, that adapter has no route to
the internet.

If you are not using the TECNAL hub, turn hub mode off (GUI button, or
`{"command":"hub_off"}`). The device otherwise scans for WiFi networks every
10 s, and each scan briefly disrupts clients associated with its own AP.

Some ESP32-S3 boards reset when the serial port is opened (DTR/RTS toggle). If
that happens the client sees a new `boot_id` and starts a fresh stream — which
is why the boot counter exists.

## Packaging for a lab PC

```bash
pip install pyinstaller
pyinstaller --paths pc_client --onefile --windowed --name BiomassSensor app.py
```

## Tests

No hardware needed — they run against a simulated device:

```bash
python pc_client/tests/run_all.py
```

| File | Covers |
|---|---|
| `tests/test_client.py` | backfill on connect, deduplication, outage recovery, reboot detection, CSV/meta output, pandas readback |
| `tests/test_ringbuffer.py` | the firmware's ring buffer and history pagination, ported line-for-line to Python: empty/partial/exactly-full/wrapped buffers, the 60-record page cap, and buffer overflow |
| `tests/test_serial.py` | the serial line classifier against real firmware output, including log text and malformed lines |
| `tests/check_firmware.py` | structural checks on the `.ino` and `web_ui.h` (brace balance, raw-string delimiters, re-entrancy guards, hub gating, NVS compatibility, routes) |
| `tests/test_gui.py` | real Qt widgets against a simulated v4.1 device: layout, state gates, every control mapping, plots, recording, and old-firmware fallback |
| `tests/test_app_entry.py` | the public `app.py` launcher and its connection options |
| `tests/test_demo.py` | the persistent hardware-free v4.1 simulation and live sample generation |

`check_firmware.py` is not a substitute for compiling. Build the sketch in the
Arduino IDE before flashing.

## Structure

- `../app.py` — the single user-facing desktop entry point.
- `biomass_desktop.py` — desktop arguments and launch setup.
- `biomass_gui.py` — Qt window, plots, and firmware controls.
- `biomass_demo.py` — interactive firmware v4.1 simulation for `--demo`.
- `biomass_core.py` — transports, protocol, gap recovery, recorder. No GUI
  dependencies; import it directly for custom analysis scripts.
- `biomass_logger.py` — compatibility CLI and unattended headless logger.
- `tests/` — hardware-free protocol, firmware-structure, entry, and GUI checks.
