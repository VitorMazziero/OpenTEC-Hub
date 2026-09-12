"""Exercise the serial line classifier and the push path without hardware."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from biomass_core import SerialTransport, SensorClient

t = SerialTransport("COM_FAKE")
fails = []
def check(c, m):
    print(("  PASS  " if c else "  FAIL  ") + m)
    if not c: fails.append(m)

# Lines exactly as the v4 firmware emits them.
lines = [
 "--- Biomass Sensor Firmware v4.0 (Direct Control) ---",
 "Boot ID: 3 | Hub mode: DISABLED (direct only)",
 '{"seq":1,"t_ms":6000,"boot_id":3,"absorbance":0.412,"raw":18342,"i0":47120,"it_ms":400,"pwm_pct":25.0,"blank_done":true,"searching":false,"measuring":true,"blanking":false,"hd_mode":false,"sat":false}',
 '{"fw":"4.0","name":"x","boot_id":3,"uptime_ms":6000,"state":"measuring","seq":1,"blank_done":true,"hd_mode":false,"low":10000,"high":40000,"opt":25000,"hub_enabled":false,"free_heap":200000}',
 '{"boot_id":3,"seq":2,"first_seq":1,"samples":[{"seq":2,"t_ms":11000,"absorbance":0.41,"raw":1,"i0":2,"it_ms":400,"pwm_pct":25.0,"hd_mode":false,"sat":false}],"count":1,"more":false}',
 '{"blank_done":true,"timestamp":123,"it_ms":[100,200,400,800],"pwm_pct":[5.0],"i0":[[1,2],[3,4]]}',
 "  PWM 25.00%: RAW = 18342",
 "{not valid json at all",
 "",
]
for l in lines: t._classify(l)

check(t._samples.qsize() == 1, f"1 sample queued, got {t._samples.qsize()}")
check(t._status.qsize() == 1, f"1 status queued, got {t._status.qsize()}")
check(t._history.qsize() == 1, f"1 history queued, got {t._history.qsize()}")
check(t._blank.qsize() == 1, f"1 blank table queued, got {t._blank.qsize()}")
check(t._log_lines.qsize() == 4, f"4 log lines, got {t._log_lines.qsize()}")

# blank table must NOT be mistaken for a sample (both carry an "i0" key)
b = t._blank.get()
check(isinstance(b["i0"], list), "blank table routed by list-valued i0")

# push path through the client
t2 = SerialTransport("COM_FAKE")
logs = []
c = SensorClient(t2, on_log=logs.append)
c.boot_id = 3
for l in lines[2:3]: t2._classify(l)
c._last_status_poll = 9e18   # skip the status request (no real port)
new = c.poll()
check(len(new) == 1, f"pushed sample ingested, got {len(new)}")
check(new[0].absorbance == 0.412, "absorbance parsed")
check(new[0].i0 == 47120, "i0 parsed")
check(new[0].valid, "sample valid")
check(abs(new[0].transmittance - 18342/47120) < 1e-9, "transmittance computed")

print("\n" + ("ALL PASS" if not fails else f"{len(fails)} FAILURES"))
sys.exit(1 if fails else 0)
