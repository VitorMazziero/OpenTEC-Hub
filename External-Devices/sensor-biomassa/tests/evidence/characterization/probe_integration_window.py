"""
Hypothesis: the pulsed read does not guarantee a COMPLETE integration
window with the LED on.

The VEML7700 free-runs, completing a conversion every IT. The firmware
turns the LED on and waits only IT+15 ms before reading. The most recently
completed conversion can therefore have started before the LED came on, so
it captures anywhere from 0% to 100% of the illuminated period depending on
where the sensor's internal boundary happens to fall.

If true, repeated single-shot reads at a FIXED gear will scatter wildly
instead of repeating. Scatter should grow with IT.
"""
import json, statistics, sys, time
import serial

ser = serial.Serial("COM5", 115200, timeout=0.5)
time.sleep(0.4); ser.reset_input_buffer()

def send(o):
    ser.write((json.dumps(o, separators=(",", ":")) + "\n").encode()); ser.flush()
    time.sleep(0.3)

def read_once(timeout=6):
    ser.reset_input_buffer()
    send({"command": "read_once"})
    t0 = time.time()
    while time.time() - t0 < timeout:
        line = ser.readline()
        if not line: continue
        s = line.decode("utf-8", "replace").strip()
        if s.startswith("{") and '"absorbance"' in s:
            try: return json.loads(s)
            except json.JSONDecodeError: pass
    return None

send({"command": "stop"})
send({"command": "manual"})

for it_idx, it_ms in ((0, 100), (1, 200), (3, 800)):
    send({"command": "set_gear", "it": it_idx, "pwm": 3})
    time.sleep(2.0)
    vals = []
    for _ in range(12):
        d = read_once()
        if d: vals.append(d["raw"])
    if not vals:
        print(f"IT {it_ms:>4} ms: no data"); continue
    lo, hi = min(vals), max(vals)
    spread = 100.0 * (hi - lo) / hi if hi else 0
    print(f"IT {it_ms:>4} ms  n={len(vals):2d}  min={lo:6d} max={hi:6d} "
          f"median={int(statistics.median(vals)):6d}  spread={spread:5.1f}%")
    print(f"            {vals}")

send({"command": "auto"})
send({"command": "stop"})
ser.close()
