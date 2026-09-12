"""
Locate the LED duty knee between 4.3% (clean) and 10.8% (-1.0%).

The main sweep left a gap exactly where the useful limit lives, so this
probes 6%, 8% and 9% with a clean 5 s recovery between each. Recovery
matters: without it, residual heat from the previous phase would be scored
against the next one.
"""
import csv
import json
import sys
import time
from datetime import datetime
from pathlib import Path

import serial

PORT = "COM5"
OUT = Path(sys.argv[1])
IT_INDEX, PWM_INDEX = 1, 5     # 200 ms, 50 %
ON_MS = 215

def interval_for(duty_pct):
    return int(round(ON_MS / (duty_pct / 100.0)))

PHASES = []
for duty in (6.0, 8.0, 9.0):
    PHASES.append((f"rest_before_{duty:g}", 5000, 200))
    PHASES.append((f"duty_{duty:g}", interval_for(duty), 330))


def send(ser, obj):
    ser.write((json.dumps(obj, separators=(",", ":")) + "\n").encode())
    ser.flush()
    time.sleep(0.25)


def main():
    ser = serial.Serial(PORT, 115200, timeout=0.4)
    time.sleep(0.4)
    ser.reset_input_buffer()
    fh = open(OUT, "w", newline="", encoding="utf-8")
    w = csv.writer(fh)
    w.writerow(["wall_iso", "phase", "interval_ms", "duty_pct",
                "phase_elapsed_s", "seq", "absorbance", "raw", "i0"])
    fh.flush()

    send(ser, {"command": "stop"})
    send(ser, {"command": "manual"})
    send(ser, {"command": "set_gear", "it": IT_INDEX, "pwm": PWM_INDEX})
    time.sleep(1.5)

    for label, interval, duration in PHASES:
        duty = 100.0 * ON_MS / interval
        print(f"\n=== {label}: {interval} ms (duty {duty:.1f}%) {duration}s ===",
              flush=True)
        send(ser, {"refresh_ms": interval})
        send(ser, {"command": "start"})
        ser.reset_input_buffer()
        t0 = time.time()
        vals = []
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
                        f"{duty:.1f}", f"{el:.1f}", d.get("seq"),
                        d.get("absorbance"), d.get("raw"), d.get("i0")])
            fh.flush()
            vals.append(d.get("raw"))
        if len(vals) >= 10:
            start = sorted(vals[:5])[2]
            end = sorted(vals[-9:])[4]
            drift = 100.0 * (end - start) / start
            print(f"  -> {label}: n={len(vals)} raw {start} -> {end} "
                  f"({drift:+.2f}%)", flush=True)
        send(ser, {"command": "stop"})
        time.sleep(0.5)

    send(ser, {"refresh_ms": 10187})
    send(ser, {"command": "auto"})
    send(ser, {"command": "stop"})
    fh.close()
    ser.close()
    print("\nDONE", flush=True)


if __name__ == "__main__":
    main()
