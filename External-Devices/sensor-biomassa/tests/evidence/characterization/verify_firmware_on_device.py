"""
Post-upload verification of firmware v4.6 against the real device.

Run with an EMPTY beam (no fluid, no colorant). Checks the thermal interval
floor, the geometric LED ladder and the gear-selection policy, plus the two
things v4.6 exists for: that the sensor's real conversion period is inside
IT_PERIOD_GUARD, and that pulsed reads no longer drop below a full
integration window. A full blanking sweep runs so the saturation pattern and
the Smart Start choice can be inspected for real.
"""
import json
import struct
import sys
import time
from pathlib import Path

import serial

PORT = "COM5"
FW_EXPECTED = "5.0"

# Must mirror the firmware exactly -- these are what the thermal floor is
# derived from. See ledOnMsFor() / integrationGuardMs() in the .ino.
IT_PERIOD_GUARD = 1.20
LED_SETTLE_MS = 10
INTEGRATION_MARGIN_MS = 8
LED_DUTY_LIMIT = 0.08


def f32(x):
    """Round to IEEE single precision, the way the firmware's floats behave.

    Not a nicety: 1.15f * 800.0f rounds UP to exactly 920.0f in float, while
    the same product in Python's double is 919.9999999999999 and truncates to
    919. That is a 25 ms disagreement in the derived floor -- enough to fail a
    comparison against a device that is doing exactly the right thing.
    """
    return struct.unpack("f", struct.pack("f", x))[0]


def led_on_ms_for(it_ms):
    guard = int(f32(f32(IT_PERIOD_GUARD) * f32(it_ms))) + INTEGRATION_MARGIN_MS
    return LED_SETTLE_MS + 2 * guard


def min_safe_refresh_ms(it_ms):
    return int(f32(led_on_ms_for(it_ms) / f32(LED_DUTY_LIMIT)))


fails = []


def check(cond, msg, detail=""):
    print(("  PASS  " if cond else "  FAIL  ") + msg + (f"  [{detail}]" if detail else ""))
    if not cond:
        fails.append(msg)


class Dev:
    def __init__(self, port=PORT):
        self.ser = serial.Serial(port, 115200, timeout=0.5)
        time.sleep(0.5)
        self.ser.reset_input_buffer()

    def send(self, obj):
        self.ser.write((json.dumps(obj, separators=(",", ":")) + "\n").encode())
        self.ser.flush()

    def collect(self, seconds, want_key=None, echo=False):
        """Reads lines for `seconds`; returns (json_objs, text_lines)."""
        objs, text = [], []
        t0 = time.time()
        while time.time() - t0 < seconds:
            line = self.ser.readline()
            if not line:
                continue
            s = line.decode("utf-8", "replace").strip()
            if not s:
                continue
            if echo:
                print("     |", s[:110])
            if s.startswith("{"):
                try:
                    o = json.loads(s)
                    objs.append(o)
                    if want_key and want_key in o:
                        return objs, text
                except json.JSONDecodeError:
                    text.append(s)
            else:
                text.append(s)
        return objs, text

    def status(self, timeout=4):
        self.ser.reset_input_buffer()
        self.send({"command": "status"})
        objs, _ = self.collect(timeout, want_key="fw")
        for o in objs:
            if "fw" in o:
                return o
        raise RuntimeError("no status returned")

    def blank_table(self, timeout=5):
        self.ser.reset_input_buffer()
        self.send({"command": "print_blank"})
        objs, _ = self.collect(timeout)
        for o in objs:
            if isinstance(o.get("i0"), list):
                return o
        raise RuntimeError("no blank table returned")

    def close(self):
        self.ser.close()


def main():
    d = Dev()
    print("=== [1] firmware identity ===")
    st = d.status()
    print(f"  fw={st['fw']}  boot={st['boot_id']}  state={st['state']}")
    check(st["fw"] == FW_EXPECTED, f"running firmware v{FW_EXPECTED}",
          f"got {st['fw']}")
    if st["fw"] != FW_EXPECTED:
        print(f"\n!! Device is not running v{FW_EXPECTED} -- upload it first.")
        d.close()
        return 1
    print(f"  it_table = {st['it_table']}")

    # ORDER MATTERS. Repeatability is measured FIRST, on a thermally cold
    # device. probe_period works the LED hard for ~40 s, and the LED needs
    # minutes to recover (~95 s time constant, see the characterization
    # README), so reads taken straight afterwards drift downward and inflate
    # the spread of whichever gear happens to be tested first. Measured: IT
    # 100 ms spread 0.69 % cold vs 1.32 % immediately after the probe.
    # The probe is unaffected by the reverse ordering -- it times the gap
    # between conversion boundaries, which does not care how bright the LED is.
    print("\n=== [1a] read repeatability at a fixed gear ===")
    # Two defects show up here, and they have different shapes.
    #
    # Before v4.4, twelve reads at IT 100 ms scattered 573..5222 counts (89 %)
    # in a sawtooth, because the conversion that completed could have started
    # before the LED came on. v4.5 fixed that at 100 ms but still assumed the
    # nominal period, so 200/400/800 ms kept dropping 5-11 % at random --
    # which is why 400 ms is tested here.
    #
    # Both failures are DROPOUTS: a reading that caught a partly dark
    # conversion lands BELOW the others, asymmetrically. That is what this
    # tests for. Symmetric scatter is the instrument's own noise floor
    # (~0.3 % relative, roughly constant across IT, so it is multiplicative
    # LED/supply noise rather than counting statistics) and is bounded
    # separately and more loosely.
    d.send({"command": "stop"})
    time.sleep(0.6)
    d.send({"command": "manual"})
    time.sleep(0.4)
    worst_low = 0.0
    for it_idx, it_ms in ((0, 100), (1, 200), (2, 400), (3, 800)):
        d.send({"command": "set_gear", "it": it_idx, "pwm": 3})
        time.sleep(max(2.0, (it_ms * 3) / 1000.0))
        vals = []
        for _ in range(8):
            d.ser.reset_input_buffer()
            d.send({"command": "read_once"})
            objs, _ = d.collect(max(6.0, it_ms / 100.0))
            for o in objs:
                if "absorbance" in o and "raw" in o:
                    vals.append(o["raw"])
                    break
        if len(vals) < 4:
            check(False, f"IT {it_ms} ms: enough reads collected",
                  f"got {len(vals)}")
            continue
        vals.sort()
        med = vals[len(vals) // 2]
        low = 100.0 * (med - vals[0]) / med
        high = 100.0 * (vals[-1] - med) / med
        spread = 100.0 * (vals[-1] - vals[0]) / vals[-1]
        worst_low = max(worst_low, low)
        print(f"  IT {it_ms:>4} ms  n={len(vals)}  median={med:6d}  "
              f"low={low:5.2f}%  high={high:5.2f}%  spread={spread:5.2f}%")
        # The regression test: no reading may sit far below the median.
        check(low < 2.0, f"IT {it_ms} ms has no dropout below the median",
              f"worst low deviation {low:.2f}%")
        # And the scatter must stay near the noise floor either way.
        check(spread < 2.5, f"IT {it_ms} ms scatter stays near the noise floor",
              f"spread {spread:.2f}%")
    print(f"  worst low deviation across all gears: {worst_low:.2f}% "
          f"(89% before v4.4, 11% before v4.6)")

    st = d.status()
    check(st.get("boundary_misses", -1) == 0,
          "every read anchored on an observed conversion boundary",
          f"boundary_misses={st.get('boundary_misses')}")

    print("\n=== [1b] real conversion period vs its nominal label ===")
    # The nominal integration time is a label, not a guaranteed period: the
    # datasheet specifies no tolerance for the internal oscillator, and this
    # part measures ~9 % long. Everything in [1a] depends on the guard
    # covering that.
    d.ser.reset_input_buffer()
    d.send({"command": "probe_period", "pwm": 4})
    rows = []
    t0 = time.time()
    while time.time() - t0 < 150 and len(rows) < len(st["it_table"]):
        objs, _ = d.collect(5.0)
        rows += [o for o in objs if o.get("probe") == "period"]
    check(len(rows) >= 3, "probe_period reported every IT slot",
          f"got {len(rows)}")
    worst_ratio = 0.0
    for o in rows:
        if not o.get("resolved"):
            print(f"  IT {o['nominal_ms']:>4} ms: no trial resolved "
                  f"({o['trials']} attempted)")
            continue
        worst_ratio = max(worst_ratio, o["max_ms"] / o["nominal_ms"])
        print(f"  IT {o['nominal_ms']:>4} ms -> period {o['period_ms']:>7} ms "
              f"({o['min_ms']}..{o['max_ms']})  ratio {o['ratio']:.3f}  "
              f"resolved {o['resolved']}/{o['trials']}")
        check(o["within_guard"],
              f"IT {o['nominal_ms']} ms period stays inside the guard",
              f"ratio {o['ratio']:.3f} vs guard {o['guard']}")
    if worst_ratio:
        print(f"  worst period/label ratio: {worst_ratio:.3f} "
              f"(guard {IT_PERIOD_GUARD})")
        check(worst_ratio < IT_PERIOD_GUARD,
              "IT_PERIOD_GUARD covers this part with margin",
              f"{worst_ratio:.3f}")

    d.send({"command": "auto"})
    time.sleep(0.8)
    d.send({"command": "stop"})
    time.sleep(1.0)

    print("\n=== [2] LED thermal interval floor ===")
    st = d.status()
    check("min_refresh_ms" in st, "status reports min_refresh_ms")
    check("led_duty_limit" in st, "status reports led_duty_limit")
    floor = st.get("min_refresh_ms", 0)
    limit = st.get("led_duty_limit", 0)
    max_it = max(st["it_table"])
    expect = min_safe_refresh_ms(max_it)
    print(f"  floor={floor} ms  duty_limit={limit}  max_it={max_it} ms  "
          f"worst-case LED on-time={led_on_ms_for(max_it)} ms")
    # Exact, not approximate: the mirror emulates the firmware's float
    # arithmetic, so any disagreement is a real divergence worth knowing about.
    check(floor == expect,
          "floor matches worst-case LED on-time / duty limit",
          f"expected {expect}")
    check(st["auto_range"] and floor > 9000,
          "auto-ranging floor assumes the 800 ms IT", f"{floor} ms")

    # Ask for something far too fast and confirm the device refuses it.
    d.send({"refresh_ms": 500})
    time.sleep(1.0)
    st = d.status()
    check(st["refresh_ms"] >= floor,
          "a 500 ms request is clamped to the floor",
          f"device kept {st['refresh_ms']} ms")

    # A manual lock on a short IT must lower the floor.
    d.send({"command": "manual"})
    time.sleep(0.5)
    d.send({"command": "set_gear", "it": 0, "pwm": 3})
    time.sleep(2.5)
    st = d.status()
    print(f"  manual IT={st['it_ms']} ms -> floor {st['min_refresh_ms']} ms")
    check(st["min_refresh_ms"] < floor,
          "locking a short IT lowers the floor",
          f"{st['min_refresh_ms']} ms vs {floor} ms")
    check(st["min_refresh_ms"] == min_safe_refresh_ms(st["it_ms"]),
          "the locked-gear floor matches the same formula",
          f"expected {min_safe_refresh_ms(st['it_ms'])}")
    d.send({"command": "auto"})
    time.sleep(1.0)

    print("\n=== [3] recommended LED ladder ===")
    d.send({"command": "pwm_preset"})
    time.sleep(2.0)
    st = d.status()
    table = st["pwm_table"]
    print(f"  pwm_table = {table}")
    expected = [2.0, 3.5, 6.0, 10.5, 18.0, 32.0, 57.0, 100.0]
    check(table == expected, "geometric ladder applied", f"{table}")
    check(not st["blank_done"], "changing the ladder invalidated the blank")
    ratios = [table[i + 1] / table[i] for i in range(len(table) - 1)]
    print("  step ratios: " + ", ".join(f"{r:.2f}" for r in ratios))
    check(max(ratios) / min(ratios) < 1.06, "ratios uniform within 6%")

    print("\n=== [4] blanking sweep (empty beam) ===")
    d.ser.reset_input_buffer()
    d.send({"command": "blank"})
    t0 = time.time()
    st = None
    # v4.6 adds a dark settle before every pulse, so the sweep is slower than
    # the ~25 s of v4.5 -- budget generously. Serial IS serviced during the
    # sweep (since v4.3), so status should answer throughout.
    while time.time() - t0 < 240:
        try:
            st = d.status(timeout=3)
            if st["state"] == "idle" and st["blank_done"]:
                break
        except RuntimeError:
            pass
        time.sleep(1.0)
    elapsed = time.time() - t0
    check(st is not None and st["blank_done"],
          "blanking completed and stored")
    print(f"  sweep + resync took ~{elapsed:.0f}s")

    bt = d.blank_table()
    rows = bt["i0"]
    its, pwms = bt["it_ms"], bt["pwm_pct"]
    print(f"\n  {'IT':>6} | " + " ".join(f"{p:>7g}%" for p in pwms))
    print("  " + "-" * (8 + 9 * len(pwms)))
    MIN_VALID_BLANK = 500          # must match the firmware constant
    n_sat = n_valid = n_dim = 0
    for i, row in enumerate(rows):
        cells = []
        for v in row:
            if v >= 65530:
                cells.append("    sat")
                n_sat += 1
            elif v < MIN_VALID_BLANK:
                cells.append(f"{v:6d}-")   # below the plausibility floor
                n_dim += 1
            else:
                cells.append(f"{v:7d}")
                n_valid += 1
        print(f"  {its[i]:>4}ms | " + " ".join(f"{c:>8}" for c in cells))
    print(f"\n  {n_valid} usable gears, {n_sat} saturated, "
          f"{n_dim} below the {MIN_VALID_BLANK}-count floor (marked -)")
    check(n_valid > 0, "at least one usable gear")
    # The failure this catches: a pre-v4.6 sweep filled the 200/400/800 ms
    # rows with 9..109 counts. If a whole long-IT row is under the floor the
    # blank did not measure light, it measured the dark.
    for i, row in enumerate(rows):
        if all(v < MIN_VALID_BLANK for v in row):
            check(False, f"IT {its[i]} ms row measured actual light",
                  "entire row below the plausibility floor")
    # Down a column the counts must climb with integration time.
    mid = len(pwms) // 2
    colvals = [rows[i][mid] for i in range(len(rows))]
    check(any(v >= MIN_VALID_BLANK for v in colvals[1:]),
          "long integration times produced light during blanking",
          f"PWM {pwms[mid]:g}% column: {colvals}")
    # Saturation is not a failure and not required -- it just tells us the
    # top of the ladder exceeds the sensor with this optical geometry.
    print(f"  (saturated cells are informational, not a pass/fail: {n_sat})")

    # The whole point of the ladder: neighbouring valid cells should differ
    # by a roughly constant factor.
    row0 = [v for v in rows[0] if v < 65530 and v > 200]
    if len(row0) >= 3:
        r = [row0[i + 1] / row0[i] for i in range(len(row0) - 1)]
        print("  measured I0 ratios (shortest IT): " +
              ", ".join(f"{x:.2f}" for x in r))
        check(max(r) / min(r) < 1.9,
              "measured light ratios roughly uniform (LED is near-linear "
              "in duty)", f"{min(r):.2f}-{max(r):.2f}")

    print("\n=== [4b] integration-time ladder direction ===")
    # The v2.5..v4.1 bug wrote wrong ALS_IT codes, so longer "IT" collected
    # LESS light. Down any PWM column the counts must now roughly double
    # per row (100 -> 200 -> 400 -> 800 ms).
    col = len(pwms) // 2                       # a mid column, well off both rails
    colvals = [rows[i][col] for i in range(len(rows))]
    print(f"  PWM {pwms[col]:g}% column: " +
          " -> ".join(str(v) for v in colvals))
    if all(v < 65530 and v > 0 for v in colvals):
        r = [colvals[i + 1] / colvals[i] for i in range(len(colvals) - 1)]
        print("  row ratios: " + ", ".join(f"{x:.2f}" for x in r))
        check(all(x > 1.0 for x in r),
              "longer integration time collects MORE light",
              f"ratios {['%.2f' % x for x in r]}")
        check(all(1.6 <= x <= 2.4 for x in r),
              "each IT step roughly doubles the signal",
              f"ratios {['%.2f' % x for x in r]}")
    else:
        # Saturation in this column is itself evidence the ladder now climbs.
        print("  (column saturates at the top -- ladder is climbing)")
        first_sat = next(i for i, v in enumerate(colvals) if v >= 65530)
        check(first_sat > 0, "saturation appears only at longer IT")

    print("\n=== [5] Smart Start picks the brightest valid gear <= HIGH ===")
    st = d.status()
    high = st["high"]
    best = None
    for i, row in enumerate(rows):
        for j, v in enumerate(row):
            if v >= 65530 or v == 0 or v > high:
                continue
            if best is None or v > best[2]:
                best = (i, j, v)
    print(f"  HIGH={high}; brightest valid gear at or below it: "
          f"IT {its[best[0]]}ms / PWM {pwms[best[1]]}% (I0={best[2]})")

    d.send({"command": "start"})
    time.sleep(4.0)
    st = d.status()
    print(f"  device chose IT {st['it_ms']}ms / PWM {st['pwm_pct']}%")
    check((st["it_index"], st["pwm_index"]) == (best[0], best[1]),
          "Smart Start chose exactly the brightest valid gear",
          f"want {(best[0], best[1])}, got {(st['it_index'], st['pwm_index'])}")
    chosen_i0 = rows[st["it_index"]][st["pwm_index"]]
    import math
    headroom = math.log10(chosen_i0 / st["low"]) if chosen_i0 > st["low"] else 0
    print(f"  headroom before first gear change: {headroom:.2f} AU")
    check(headroom > 0.45, "headroom beats the old mid-scale policy (~0.40 AU)",
          f"{headroom:.2f} AU")

    print("\n=== [6] live readings with an empty beam ===")
    # The window has to span several sampling intervals, and the interval is
    # now the thermal floor (~23 s), not the 5 s the old script assumed.
    window = max(80.0, 3.5 * st["refresh_ms"] / 1000.0)
    print(f"  collecting for {window:.0f} s at a {st['refresh_ms']} ms interval")
    objs, _ = d.collect(window)
    samples = [o for o in objs if "absorbance" in o and "seq" in o]
    print(f"  {len(samples)} samples in {window:.0f} s")
    check(len(samples) >= 2, "device is streaming samples")
    if samples:
        for s in samples[:4]:
            print(f"    seq={s['seq']} A={s['absorbance']:+.4f} "
                  f"raw={s['raw']} i0={s['i0']} "
                  f"{s['it_ms']}ms/{s['pwm_pct']}%")
        a = [s["absorbance"] for s in samples]
        check(all(x > -90 for x in a),
              "no -99 error sentinels (every gear used has a valid blank)")
        check(max(abs(x) for x in a) < 0.05,
              "absorbance ~0 with nothing in the beam",
              f"max |A| = {max(abs(x) for x in a):.4f}")

    print("\n=== [7] restore a sane state ===")
    d.send({"command": "stop"})
    time.sleep(0.5)
    st = d.status()
    check(st["state"] == "idle", "device left idle")
    print(f"  interval={st['refresh_ms']} ms  auto_range={st['auto_range']}  "
          f"blank_done={st['blank_done']}")

    d.close()
    print("\n" + ("ALL PASS" if not fails else f"{len(fails)} FAILURES:"))
    for f in fails:
        print("  - " + f)
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
