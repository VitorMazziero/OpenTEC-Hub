"""
LED thermal drift experiment.

Hypothesis: at short sampling intervals the LED spends a large fraction of
its time on, the junction heats, output falls, and raw counts drift down.
Because I0 was captured during blanking at one thermal state, that drift
shows up as a spurious rise in absorbance -- "loss of calibration".

Method: lock the gear (manual mode) so auto-ranging cannot confound the
signal, then step the sampling interval through stress/recovery phases with
nothing in the beam. Raw counts at a fixed gear are the thermal signal.

Duty cycle is what matters, not the interval alone: one read holds the LED on
for ~(10 ms settle + IT + 5 ms). At IT=200 ms that is 215 ms, so a 500 ms
interval means the LED is on 43% of the time versus 4.3% at 5 s.
"""
import csv
import json
import sys
import time
from datetime import datetime
from pathlib import Path

import serial

PORT = "COM5"
OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("thermal.csv")

IT_INDEX = 1     # 200 ms
PWM_INDEX = 5    # 50 %
ON_MS = 215      # 10 settle + 200 IT + 5 buffer

# (label, interval_ms, duration_s)
PHASES = [
    ("baseline_5s",   5000, 180),
    ("stress_500ms",   500, 360),
    ("recover_5s",    5000, 300),
    ("stress_1s",     1000, 300),
    ("recover2_5s",   5000, 240),
    ("stress_2s",     2000, 300),
    ("recover3_5s",   5000, 240),
]


def send(ser, obj):
    ser.write((json.dumps(obj, separators=(",", ":")) + "\n").encode())
    ser.flush()
    time.sleep(0.25)


def main():
    ser = serial.Serial(PORT, 115200, timeout=0.4)
    time.sleep(0.4)
    ser.reset_input_buffer()

    print(f"writing {OUT}", flush=True)
    fh = open(OUT, "w", newline="", encoding="utf-8")
    w = csv.writer(fh)
    w.writerow(["wall_iso", "phase", "interval_ms", "duty_pct", "phase_elapsed_s",
                "seq", "t_ms", "absorbance", "raw", "i0", "it_ms", "pwm_pct", "sat"])
    fh.flush()

    # Lock the gear so auto-ranging cannot move it mid-experiment.
    send(ser, {"command": "stop"})
    send(ser, {"command": "manual"})
    send(ser, {"command": "set_gear", "it": IT_INDEX, "pwm": PWM_INDEX})
    time.sleep(1.5)

    for label, interval, duration in PHASES:
        duty = 100.0 * ON_MS / interval
        print(f"\n=== {label}: interval {interval} ms "
              f"(LED duty {duty:.1f}%) for {duration}s ===", flush=True)
        send(ser, {"refresh_ms": interval})
        send(ser, {"command": "start"})
        ser.reset_input_buffer()

        t0 = time.time()
        n = 0
        first_raw = None
        last_raw = None
        while time.time() - t0 < duration:
            line = ser.readline()
            if not line:
                continue
            s = line.decode("utf-8", "replace").strip()
            if not s.startswith("{") or '"absorbance"' not in s:
                continue
            try:
                d = json.loads(s)
            except json.JSONDecodeError:
                continue
            el = time.time() - t0
            w.writerow([datetime.now().isoformat(), label, interval,
                        f"{duty:.1f}", f"{el:.1f}",
                        d.get("seq"), d.get("t_ms"), d.get("absorbance"),
                        d.get("raw"), d.get("i0"), d.get("it_ms"),
                        d.get("pwm_pct"), int(bool(d.get("sat")))])
            fh.flush()
            n += 1
            if first_raw is None:
                first_raw = d.get("raw")
            last_raw = d.get("raw")
            if n % 20 == 0:
                print(f"  {el:6.1f}s  n={n:4d}  raw={d.get('raw')}  "
                      f"A={d.get('absorbance')}", flush=True)

        drift = (last_raw - first_raw) if (first_raw and last_raw) else 0
        pct = (100.0 * drift / first_raw) if first_raw else 0
        print(f"  -> {label}: {n} samples, raw {first_raw} -> {last_raw} "
              f"({drift:+d}, {pct:+.2f}%)", flush=True)
        send(ser, {"command": "stop"})
        time.sleep(0.5)

    # Leave the device in a sane state.
    send(ser, {"refresh_ms": 5000})
    send(ser, {"command": "auto"})
    send(ser, {"command": "stop"})
    fh.close()
    ser.close()
    print("\nDONE", flush=True)


if __name__ == "__main__":
    main()
