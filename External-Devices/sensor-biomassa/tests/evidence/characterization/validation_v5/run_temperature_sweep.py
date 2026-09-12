"""
Does the VEML7700's conversion period move with temperature?

WHY THIS EXISTS
The whole timing path now rests on one assumption: that the real conversion
period stays below IT_PERIOD_GUARD x its nominal label. The period was
measured at 1.092x at bench temperature, and the guard is 1.20. Whether that
9.9% headroom is generous or marginal depends on the oscillator's temperature
coefficient -- which the datasheet does not publish, alongside the tolerance
it also does not publish. So measure it.

METHOD
There is no way to command a temperature here, so the device heats itself:
the LED is the only substantial heat source on the board, and running it at a
high duty raises board temperature by several degrees. Each phase holds a
thermal state, then measures the period in every IT slot with probe_period,
which reports the temperature it measured at alongside each period.

  1. cold      device idle from the start of the run
  2. heating   LED driven at a high duty for HEAT_MINUTES
  3. hot       period measured immediately, before it can cool
  4. cooling   LED off
  5. recovered period measured again after COOL_MINUTES

WHAT THIS CAN AND CANNOT SHOW
soc_temp_c is the ESP32 die sensor, not the VEML7700 and not the LED
junction. It reads well above ambient and responds faster than the board as
a whole. It is a proxy: good enough to establish the SIGN and rough SIZE of
any period drift over the range the device actually reaches in use, and not
good enough to quote a datasheet-style ppm/degC figure for the sensor.

The span reachable this way is maybe 10-20 degC. A result of "no measurable
drift over this span" bounds the risk over the span tested; it does not prove
the part is stable at 50 degC. Say so in any write-up.

Output: data/v50_period_vs_temperature.csv
"""
import csv
import json
import time
from pathlib import Path

import serial

PORT = "COM5"
HEAT_MINUTES = 6.0
COOL_MINUTES = 6.0
HEAT_DUTY = 100.0          # LED duty while heating, percent
OUT = Path(__file__).resolve().parent / "data" / "v50_period_vs_temperature.csv"


class Dev:
    def __init__(self, port=PORT):
        self.ser = serial.Serial(port, 115200, timeout=0.5)
        time.sleep(2.0)
        self.ser.reset_input_buffer()

    def send(self, obj, wait=0.35):
        self.ser.write((json.dumps(obj, separators=(",", ":")) + "\n").encode())
        self.ser.flush()
        time.sleep(wait)

    def status(self, timeout=5.0):
        self.ser.reset_input_buffer()
        self.send({"command": "status"}, wait=0.05)
        t0 = time.time()
        while time.time() - t0 < timeout:
            line = self.ser.readline()
            if not line:
                continue
            s = line.decode("utf-8", "replace").strip()
            if s.startswith("{"):
                try:
                    o = json.loads(s)
                except json.JSONDecodeError:
                    continue
                if "fw" in o:
                    return o
        raise RuntimeError("no status returned")

    def probe(self, n_slots, timeout=180.0):
        self.ser.reset_input_buffer()
        self.send({"command": "probe_period", "pwm": 4}, wait=0.05)
        rows, t0 = [], time.time()
        while time.time() - t0 < timeout and len(rows) < n_slots:
            line = self.ser.readline()
            if not line:
                continue
            s = line.decode("utf-8", "replace").strip()
            if not s.startswith("{"):
                continue
            try:
                o = json.loads(s)
            except json.JSONDecodeError:
                continue
            if o.get("probe") == "period":
                rows.append(o)
        return rows

    def close(self):
        self.ser.close()


def hold(d, minutes, duty, label):
    """Holds the LED at `duty` for `minutes`, logging temperature as it goes."""
    end = time.time() + minutes * 60.0
    if duty > 0:
        d.send({"command": "led", "duty": duty})
    else:
        d.send({"command": "led_off"})
    last = 0.0
    while time.time() < end:
        time.sleep(5.0)
        if time.time() - last >= 30.0:
            last = time.time()
            try:
                st = d.status()
                remain = (end - time.time()) / 60.0
                print(f"    {label}: soc={st['soc_temp_c']:.1f} C  "
                      f"led={st['led_duty']:.0f}%  {remain:.1f} min left")
            except RuntimeError:
                pass
    if duty > 0:
        # Off before measuring: probe_period drives the LED itself, and a
        # phase that ended with the LED already off is not the hot state.
        d.send({"command": "led_off"})


def main():
    d = Dev()
    st = d.status()
    print(f"firmware {st['fw']}  soc={st['soc_temp_c']} C")
    if st["fw"] != "5.0":
        print("!! Expected firmware 5.0 (soc_temp_c / probe temp_c).")
        d.close()
        return 1
    n_slots = len(st["it_table"])
    guard = None

    d.send({"command": "stop"})
    d.send({"command": "manual"})

    records = []

    def measure(phase):
        rows = d.probe(n_slots)
        for o in rows:
            if not o.get("resolved"):
                print(f"  [{phase}] IT {o['nominal_ms']}: no trial resolved")
                continue
            print(f"  [{phase}] {o['temp_c']:>5.1f} C  IT {o['nominal_ms']:>4} ms"
                  f" -> {o['period_ms']:>7} ms  ratio {o['ratio']:.3f}"
                  f"  within_guard={o['within_guard']}")
            records.append({
                "phase": phase,
                "temp_c": o["temp_c"],
                "it_nominal_ms": o["nominal_ms"],
                "period_mean_ms": o["period_ms"],
                "period_min_ms": o["min_ms"],
                "period_max_ms": o["max_ms"],
                "ratio": o["ratio"],
                "resolved": o["resolved"],
                "trials": o["trials"],
                "guard": o["guard"],
                "within_guard": o["within_guard"],
            })
        return rows

    print("\n=== phase 1/4: cold ===")
    rows = measure("cold")
    if rows:
        guard = float(rows[0]["guard"])

    print(f"\n=== phase 2/4: heating ({HEAT_MINUTES:.0f} min at "
          f"{HEAT_DUTY:.0f}% LED duty) ===")
    hold(d, HEAT_MINUTES, HEAT_DUTY, "heating")

    print("\n=== phase 3/4: hot (measured immediately) ===")
    measure("hot")

    print(f"\n=== phase 4/4: cooling ({COOL_MINUTES:.0f} min, LED off) ===")
    hold(d, COOL_MINUTES, 0.0, "cooling")
    print("\n--- recovered ---")
    measure("recovered")

    d.send({"command": "led_off"})
    d.send({"command": "auto"})
    d.send({"command": "stop"})
    d.close()

    OUT.parent.mkdir(parents=True, exist_ok=True)
    with OUT.open("w", newline="", encoding="utf-8") as fh:
        fh.write("# Conversion period vs temperature, firmware v5.0.\n")
        fh.write("# temp_c is the ESP32 SoC die sensor standing in for board\n")
        fh.write("# temperature -- the VEML7700 exposes no temperature\n")
        fh.write("# register. Heating is LED self-heating at "
                 f"{HEAT_DUTY:.0f}% duty for {HEAT_MINUTES:.0f} min.\n")
        w = csv.DictWriter(fh, fieldnames=list(records[0].keys()))
        w.writeheader()
        w.writerows(records)
    print(f"\nwrote {OUT} ({len(records)} rows)")

    temps = [r["temp_c"] for r in records]
    span = max(temps) - min(temps)
    worst = max(r["period_max_ms"] / r["it_nominal_ms"] for r in records)
    print(f"\ntemperature span exercised: {span:.1f} C "
          f"({min(temps):.1f} -> {max(temps):.1f})")
    print(f"worst period/label ratio over the whole sweep: {worst:.3f}"
          + (f"  (guard {guard})" if guard else ""))
    if span < 4.0:
        print("!! Span is small -- LED self-heating did not move the board "
              "much. Treat the drift figure as a bound, not a coefficient.")
    if guard and worst >= guard:
        print("!! GUARD EXCEEDED somewhere in the sweep.")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
