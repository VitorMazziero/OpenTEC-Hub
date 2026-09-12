"""
Collects the v5.0 validation datasets that the plots are built from.

Everything here is measured with an EMPTY beam. Writes into data/:

  v50_pulsed_reads.csv    30 pulsed reads per IT slot -- the "after" side of
                          the dropout comparison against v4.5
  v50_it_linearity.csv    counts vs IT with the LED held on continuously,
                          which isolates the sensor from the pulse
  v50_blank_table.csv     the 4x8 I_0 table
"""
import csv
import json
import statistics
import time
from pathlib import Path

import serial

PORT = "COM5"
DATA = Path(__file__).resolve().parent / "data"
GEARS = ((0, 100), (1, 200), (2, 400), (3, 800))
N_READS = 30
PWM_INDEX = 3          # 10.5%, same gear the v4.5 baseline used
PWM_PCT = 10.5


ser = serial.Serial(PORT, 115200, timeout=0.5)
time.sleep(2.0)
ser.reset_input_buffer()


def send(o, wait=0.35):
    ser.write((json.dumps(o, separators=(",", ":")) + "\n").encode())
    ser.flush()
    time.sleep(wait)


def status(timeout=5.0):
    ser.reset_input_buffer()
    send({"command": "status"}, wait=0.05)
    t0 = time.time()
    while time.time() - t0 < timeout:
        line = ser.readline()
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
    raise RuntimeError("no status")


def read_once(timeout=12.0):
    ser.reset_input_buffer()
    send({"command": "read_once"}, wait=0.05)
    t0 = time.time()
    while time.time() - t0 < timeout:
        line = ser.readline()
        if not line:
            continue
        s = line.decode("utf-8", "replace").strip()
        if s.startswith("{") and '"absorbance"' in s:
            try:
                return json.loads(s)
            except json.JSONDecodeError:
                pass
    return None


def blank_table(timeout=6.0):
    ser.reset_input_buffer()
    send({"command": "print_blank"}, wait=0.05)
    t0 = time.time()
    while time.time() - t0 < timeout:
        line = ser.readline()
        if not line:
            continue
        s = line.decode("utf-8", "replace").strip()
        if s.startswith("{"):
            try:
                o = json.loads(s)
            except json.JSONDecodeError:
                continue
            if isinstance(o.get("i0"), list):
                return o
    raise RuntimeError("no blank table")


DATA.mkdir(parents=True, exist_ok=True)
st = status()
fw = st["fw"]
print(f"firmware {fw}  soc={st.get('soc_temp_c')} C")

send({"command": "stop"})
send({"command": "manual"})

# --- 1. pulsed reads, the "after" side of the dropout comparison ----------
# Note the ordering: this runs FIRST, on a thermally cold device. probe_period
# and the LED-held sweep both work the LED hard, and the LED needs minutes to
# recover, so reads taken afterwards ride a cooling transient.
print(f"\n[1/3] {N_READS} pulsed reads per IT slot (LED off between reads)")
send({"command": "led_off"})
rows = []
for it_idx, it_ms in GEARS:
    send({"command": "set_gear", "it": it_idx, "pwm": PWM_INDEX}, wait=0.3)
    time.sleep(3.0)
    vals = []
    for i in range(N_READS):
        d = read_once()
        if d:
            vals.append(d["raw"])
            rows.append({"firmware": fw, "it_nominal_ms": it_ms,
                         "reading_index": len(vals), "raw": d["raw"]})
    med = statistics.median(vals)
    print(f"  IT {it_ms:>4} ms  n={len(vals)}  median={med:.0f}  "
          f"min={min(vals)}  max={max(vals)}  "
          f"worst low={100*(med-min(vals))/med:.2f}%")
with (DATA / "v50_pulsed_reads.csv").open("w", newline="", encoding="utf-8") as fh:
    fh.write(f"# {N_READS} single-shot pulsed reads per IT slot, firmware {fw},\n")
    fh.write(f"# empty beam, gear locked at PWM {PWM_PCT}%, auto-ranging off.\n")
    fh.write("# Pair with v45_pulsed_reads_before.csv for the before/after.\n")
    w = csv.DictWriter(fh, fieldnames=["firmware", "it_nominal_ms",
                                       "reading_index", "raw"])
    w.writeheader()
    w.writerows(rows)
print(f"  -> {DATA / 'v50_pulsed_reads.csv'}")

# --- 2. IT linearity with the LED held on --------------------------------
# With the LED already on there is no off->on transition, so this measures the
# SENSOR alone. Counts must double per IT step; if they do not, the ALS_IT
# codes are wrong and nothing downstream is trustworthy.
print("\n[2/3] IT linearity, LED held on continuously")
send({"command": "led", "duty": PWM_PCT})
time.sleep(1.0)
lin = []
for it_idx, it_ms in GEARS:
    send({"command": "set_gear", "it": it_idx, "pwm": PWM_INDEX}, wait=0.3)
    time.sleep(4.0)
    vals = []
    for _ in range(6):
        d = read_once()
        if d:
            vals.append(d["raw"])
    med = statistics.median(vals)
    lin.append({"firmware": fw, "it_nominal_ms": it_ms, "n": len(vals),
                "median_raw": med, "min_raw": min(vals), "max_raw": max(vals)})
    print(f"  IT {it_ms:>4} ms  median={med:.0f}  (n={len(vals)})")
send({"command": "led_off"})
with (DATA / "v50_it_linearity.csv").open("w", newline="", encoding="utf-8") as fh:
    fh.write(f"# Counts vs integration time with the LED held ON continuously\n")
    fh.write(f"# ({PWM_PCT}% duty), firmware {fw}, empty beam. No off->on\n")
    fh.write("# transition, so this isolates the sensor from the pulse.\n")
    w = csv.DictWriter(fh, fieldnames=list(lin[0].keys()))
    w.writeheader()
    w.writerows(lin)
print(f"  -> {DATA / 'v50_it_linearity.csv'}")

# --- 3. blank table -------------------------------------------------------
print("\n[3/3] blank table")
send({"command": "auto"})
bt = blank_table()
brows = []
for i, row in enumerate(bt["i0"]):
    for j, v in enumerate(row):
        brows.append({"firmware": fw, "it_nominal_ms": bt["it_ms"][i],
                      "pwm_pct": bt["pwm_pct"][j], "i0": v,
                      "saturated": int(v >= 65530)})
with (DATA / "v50_blank_table.csv").open("w", newline="", encoding="utf-8") as fh:
    fh.write(f"# Blanking table (I_0) from firmware {fw}, empty beam.\n")
    fh.write("# i0 = 65535 means the cell saturated during the sweep.\n")
    w = csv.DictWriter(fh, fieldnames=list(brows[0].keys()))
    w.writeheader()
    w.writerows(brows)
print(f"  -> {DATA / 'v50_blank_table.csv'}  (blank_done={bt['blank_done']})")

send({"command": "stop"})
ser.close()
print("\ndone")
