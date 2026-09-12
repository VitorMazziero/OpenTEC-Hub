"""Exercises the hardware-free firmware v5.2 transport."""

from __future__ import annotations

import sys
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from biomass_core import SensorClient
from biomass_demo import DemoTransport


fails = []


def check(cond, msg):
    print(("  PASS  " if cond else "  FAIL  ") + msg)
    if not cond:
        fails.append(msg)


def main() -> int:
    demo = DemoTransport()
    demo.open()

    print("[identity and seed data]")
    status = demo.get_status()
    check(status["fw"] == "5.2" and status["simulated"],
          "identifies itself as simulated firmware v5.2")
    check(status["state"] == "idle" and status["blank_done"],
          "opens idle with a usable demonstration blank")
    check(status["min_refresh_ms"] == 24325 and status["led_duty_limit"] == 0.08,
          f"reports the v5.0 thermal floor, got {status.get('min_refresh_ms')}")
    check(status["refresh_ms"] == 25000,
          f"defaults to the standard 25 s interval, got {status['refresh_ms']}")
    check("boundary_misses" in status and "soc_temp_c" in status,
          "reports the v5.0 health fields the GUI binds to")
    client = SensorClient(demo)
    client.prime()
    check(len(client.samples) == 120, "main plot starts with 120 seeded samples")

    print("\n[live measurement]")
    client.command("start")
    check(demo.state == "measuring", "Start enters measuring state")
    # A too-fast request must be refused exactly as the firmware refuses it --
    # asking for 500 ms and getting it would mean the demo lets the GUI be
    # tested against a device more permissive than the real one.
    client.set_param(refresh_ms=500)
    check(demo.refresh_ms == demo.min_refresh_ms(),
          f"a 500 ms request is clamped to the LED thermal floor, "
          f"got {demo.refresh_ms}")
    # Advance by whatever interval the device actually settled on.
    demo._last_sample_clock -= (demo.refresh_ms / 1000.0) * 2.2
    new = client.poll(status_period=0)
    check(len(new) >= 2, "live polling generates samples at the accepted interval")
    check(new and new[-1].seq > 120 and new[-1].valid,
          "generated sample is valid and sequential")

    print("\n[v5.2 blanking and buffer]")
    stamp = demo.get_blank_table()["timestamp"]
    demo.send_command({"command": "blank", "duty_pct": 8.0})
    table = demo.get_blank_table()
    check(table["timestamp"] != stamp,
          "a sweep stamps the table, so the app can tell it finished")
    check(table["sweep_duty_pct"] == 8.0 and table["sweep_ms"] > 100000,
          f"a paced sweep is recorded as such: {table['sweep_duty_pct']}%, "
          f"{table['sweep_ms']} ms")
    demo.send_command({"command": "blank"})
    check(demo.get_blank_table()["sweep_duty_pct"] == 0.0,
          "and an unpaced one is recorded as unpaced")
    before = demo.get_status()["hist_stored"]
    demo.send_command({"command": "clear_history"})
    check(before > 0 and demo.get_status()["hist_stored"] == 0,
          f"clear_history empties the buffer ({before} -> "
          f"{demo.get_status()['hist_stored']})")

    print("\n[v5.0 diagnostics]")
    client.command("probe_period")
    lines = demo.drain_log()
    check(len(lines) == len(demo.it_table),
          f"probe_period reports one line per IT slot, got {len(lines)}")
    import json as _json
    probes = [_json.loads(x) for x in lines]
    check(all(p.get("probe") == "period" for p in probes),
          "probe lines are tagged so a client can pick them out of the log")
    check(all(p["within_guard"] for p in probes),
          "simulated period sits inside the firmware guard")
    check(all(p["ratio"] > 1.0 for p in probes),
          "simulated period runs longer than its nominal label, as measured")

    print("\n[firmware controls]")
    client.command("manual")
    client.command("set_gear", it=3, pwm=7)
    check(not demo.auto_range and (demo.it_index, demo.pwm_index) == (3, 7),
          "manual gear controls update simulated hardware")
    client.set_param(low=8000, opt=24000, high=42000, ema=0.35)
    check((demo.low, demo.opt, demo.high) == (8000, 24000, 42000),
          "threshold controls update simulated firmware")
    check(abs(demo.ema - 0.35) < 1e-9, "EMA control updates simulated firmware")
    client.command("stop")
    before = demo.seq
    client.command("read_once")
    sample = demo.get_history(before)["samples"][0]
    check(sample["single"], "single reading is flagged like the real firmware")
    client.command("set_pwm", index=0, value=7.5)
    check(not demo.blank_done and demo.state == "idle",
          "gear-table edits invalidate the demonstration blank")

    demo.close()
    print("\n" + ("ALL PASS" if not fails else f"{len(fails)} FAILURES:"))
    for failure in fails:
        print("  - " + failure)
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
