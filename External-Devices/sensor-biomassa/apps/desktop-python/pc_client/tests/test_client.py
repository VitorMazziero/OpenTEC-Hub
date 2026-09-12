"""Smoke test: fake device -> SensorClient -> Recorder. No hardware needed."""
import math, sys, tempfile, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from biomass_core import Transport, SensorClient, Recorder, Sample

HIST_MAX = 60  # mirrors HISTORY_MAX_RESPONSE in the firmware
RING = 1024


class FakeDevice(Transport):
    """Mimics the v4 firmware: ring buffer, seq, boot_id, capped history."""
    name = "fake"

    def __init__(self):
        self.seq = 0
        self.boot_id = 7
        self.t_ms = 1000
        self.ring = []
        self.commands = []
        self.i0 = 47000

    def tick(self, n=1):
        for _ in range(n):
            self.seq += 1
            self.t_ms += 5000
            a = 0.05 * math.exp(0.0008 * self.seq)
            raw = int(self.i0 * (10 ** -a))
            self.ring.append({
                "seq": self.seq, "t_ms": self.t_ms, "absorbance": round(a, 3),
                "raw": raw, "i0": self.i0, "it_ms": 400, "pwm_pct": 25.0,
                "hd_mode": False, "sat": False,
            })
            if len(self.ring) > RING:
                self.ring.pop(0)

    def open(self): pass
    def close(self): pass

    def get_status(self):
        return {"fw": "5.1", "state": "measuring", "boot_id": self.boot_id,
                "seq": self.seq, "blank_done": True, "hd_mode": False,
                "low": 10000, "high": 40000, "opt": 25000,
                "hub_enabled": False, "uptime_ms": self.t_ms,
                "i2c_errors": 0, "saturation_events": 0, "free_heap": 200000,
                "ap_clients": 1, "hist_size": RING,
                "hist_stored": len(self.ring)}

    def get_blank_table(self):
        return {"blank_done": True, "i0": [[100] * 8] * 4}

    def get_history(self, since):
        newer = [s for s in self.ring if s["seq"] > since]
        batch = newer[:HIST_MAX]
        return {"boot_id": self.boot_id, "seq": self.seq,
                "first_seq": self.ring[0]["seq"] if self.ring else 1,
                "samples": batch, "count": len(batch),
                "more": len(newer) > HIST_MAX}

    def send_command(self, payload):
        self.commands.append(payload)
        # v5.1: drops the ring buffer but keeps seq counting.
        if payload.get("command") == "clear_history":
            self.ring = []


def main():
    fails = []

    def check(cond, msg):
        print(("  PASS  " if cond else "  FAIL  ") + msg)
        if not cond:
            fails.append(msg)

    tmp = Path(tempfile.mkdtemp())
    dev = FakeDevice()
    dev.tick(150)   # device already ran for a while before we connected

    logs = []
    rec = Recorder(tmp, "smoke")
    c = SensorClient(dev, recorder=rec, on_log=logs.append)

    print("\n[1] prime() backfills the whole device buffer")
    c.prime()
    check(len(c.samples) == 150, f"got {len(c.samples)} samples, want 150")
    check(c.last_seq == 150, f"last_seq={c.last_seq}, want 150")
    check(rec.count == 150, f"recorder wrote {rec.count}, want 150")
    check([s.seq for s in c.samples] == list(range(1, 151)), "seq order 1..150")

    print("\n[1b] backfilled samples are dated when they were TAKEN")
    # The regression this guards: anchoring on the first sample seen assumes
    # the oldest record in the buffer was measured just now, which throws the
    # whole run -- backfill and every live sample after it -- as far into the
    # future as the device had been running.
    now = time.time()
    span_s = (dev.ring[-1]["t_ms"] - dev.ring[0]["t_ms"]) / 1000.0
    newest, oldest = c.samples[-1], c.samples[0]
    check(newest.wall_epoch <= now + 1.0,
          f"newest sample is not in the future "
          f"({newest.wall_epoch - now:+.1f} s from now)")
    check(all(s.wall_epoch <= now + 1.0 for s in c.samples),
          "no backfilled sample is dated in the future")
    check(abs(newest.wall_epoch - now) < 5.0,
          f"newest sample is dated about now ({newest.wall_epoch - now:+.1f} s)")
    check(abs((now - oldest.wall_epoch) - span_s) < 5.0,
          f"oldest sample is dated {now - oldest.wall_epoch:.0f} s ago, "
          f"want about {span_s:.0f} s")
    check(rec.meta.get("time_anchor_source") == "uptime_ms",
          f"anchor came from the device uptime, "
          f"got {rec.meta.get('time_anchor_source')!r}")

    print("\n[2] steady polling picks up new samples, no duplicates")
    dev.tick(3)
    c._last_status_poll = 0
    new = c.poll()
    check(len(new) == 3, f"got {len(new)} new, want 3")
    check(len(c.samples) == 153, f"total {len(c.samples)}, want 153")
    c._last_status_poll = 0
    check(c.poll() == [], "second poll with no new data returns nothing")
    check(len(c.samples) == 153, "no duplicates appended")

    print("\n[3] outage: 200 samples missed while disconnected")
    dev.tick(200)
    c._last_status_poll = 0
    new = c.poll()
    check(len(new) == 200, f"poll() returned {len(new)} recovered, want 200")
    check(c.last_seq == 353, f"last_seq={c.last_seq}, want 353")
    check(len(c.samples) == 353, f"total {len(c.samples)}, want 353")
    seqs = [s.seq for s in c.samples]
    check(seqs == list(range(1, 354)), "no holes after multi-page backfill")

    print("\n[4] ring buffer overflow: data older than the buffer is gone")
    dev.tick(1500)
    c._last_status_poll = 0
    c.poll()
    check(c.last_seq == dev.seq, f"caught up to {dev.seq}, at {c.last_seq}")
    got = set(s.seq for s in c.samples)
    check(len(got) == len(c.samples), "still no duplicates")
    check(max(got) == dev.seq, "have the newest sample")

    print("\n[5] device reboot resets the stream")
    dev.boot_id = 8
    dev.seq = 0
    dev.t_ms = 1000
    dev.ring = []
    dev.tick(4)
    c._last_status_poll = 0
    new = c.poll()
    check(len(new) == 4, f"poll() returned {len(new)} post-reboot, want 4")
    check(c.boot_id == 8, f"boot_id={c.boot_id}, want 8")
    check(c.last_seq == 4, f"last_seq={c.last_seq} after reboot, want 4")
    check(any("boot" in m for m in logs), "reboot was logged")

    print("\n[6] wall-clock anchoring")
    s = c.samples[-1]
    check(abs(s.wall_epoch - time.time()) < 60, "wall_epoch is sane")
    check(s.iso.startswith("20"), f"iso renders: {s.iso}")
    # The reboot in [5] restarted device millis(), so the anchor had to be
    # re-derived; keeping the old one would date post-reboot samples an hour
    # before the ones they follow.
    check(all(a.wall_epoch <= b.wall_epoch
              for a, b in zip(c.samples[-4:], c.samples[-3:])),
          "post-reboot samples are still in order")

    print("\n[7] derived fields")
    v = Sample(seq=1, t_ms=0, boot_id=1, absorbance=0.3, raw=100, i0=200,
               it_ms=400, pwm_pct=25.0)
    check(v.valid, "normal sample is valid")
    check(abs(v.transmittance - 0.5) < 1e-9, "transmittance = raw/i0")
    check(not Sample(1, 0, 1, -99.0, 0, 0, 400, 25.0).valid, "-99 sentinel invalid")
    check(not Sample(1, 0, 1, 9.9, 0, 100, 400, 25.0).valid, "9.9 sentinel invalid")
    check(Sample(1, 0, 1, 0.3, 100, 0, 400, 25.0).transmittance is None,
          "i0=0 -> transmittance None")

    print("\n[7b] replaying and clearing the device buffer")
    dev2 = FakeDevice()
    dev2.tick(30)
    rec2 = Recorder(tmp, "replay")
    c2 = SensorClient(dev2, recorder=rec2, on_log=logs.append)
    c2.prime()
    check(rec2.count == 30, f"primed with {rec2.count} samples, want 30")
    # A run started from device memory re-reads what the client already has.
    rec3 = Recorder(tmp, "seeded")
    c2.attach_recorder(rec3)
    again = c2.replay_history(0)
    check(again == 30, f"replayed {again} samples, want 30")
    check(rec3.count == 30, f"replay wrote {rec3.count} rows into the new run")
    check(c2.last_seq == 30, f"last_seq back at the head: {c2.last_seq}")
    check(rec3.meta.get("blank_table") is not None,
          "a run started mid-session still records the blank it used")
    # Clearing is confirmed by the device's own count, not by the send call.
    check(c2.clear_device_history(), "clear reported success")
    check(any(cm.get("command") == "clear_history" for cm in dev2.commands),
          "clear_history command sent")
    dev2.tick(2)
    c2._last_status_poll = 0
    fresh = c2.poll()
    check(len(fresh) == 2, f"{len(fresh)} samples after a clear, want 2")
    check([s.seq for s in fresh] == [31, 32],
          f"seq keeps counting across a clear: {[s.seq for s in fresh]}")

    print("\n[7c] a run that moves keeps its rows")
    written = rec3.count            # 30 replayed + the 2 polled above
    rec4 = Recorder(tmp, "moved_into")
    moved = rec4.copy_rows_from(rec3.csv_path)
    check(moved == written, f"copied {moved} rows, want {written}")
    check(rec4.count == written, "the new recorder counts what it inherited")
    body = rec4.csv_path.read_text(encoding="utf-8").strip().splitlines()
    check(len(body) == written + 1,
          f"{len(body)} lines ({written} rows + one header)")
    check(body[0].startswith("iso_time"), "header written once")
    check(not any(l.startswith("iso_time") for l in body[1:]),
          "the copied file has no second header buried in it")
    rec2.close(), rec3.close(), rec4.close()

    print("\n[8] commands")
    c.command("start")
    c.command("history", since=5)
    check(dev.commands[-2] == {"command": "start"}, "start command sent")
    check(dev.commands[-1] == {"command": "history", "since": 5}, "kwargs passed")

    print("\n[9] CSV + meta")
    rec.close()
    lines = rec.csv_path.read_text(encoding="utf-8").strip().splitlines()
    check(len(lines) == rec.count + 1, f"{len(lines)-1} rows for {rec.count} samples")
    check(lines[0].startswith("iso_time,epoch,t_ms,seq,boot_id,absorbance"),
          "header as documented")
    ncols = len(lines[0].split(","))
    check(all(len(l.split(",")) == ncols for l in lines[1:]), "all rows same width")
    import json
    meta = json.loads(rec.meta_path.read_text(encoding="utf-8"))
    for k in ("run_name", "started", "ended", "samples", "device",
              "blank_table", "time_anchor_epoch", "columns"):
        check(k in meta, f"meta has {k}")

    print("\n[10] pandas can read it")
    try:
        import pandas as pd
        df = pd.read_csv(rec.csv_path)
        check(len(df) == rec.count, f"pandas read {len(df)} rows")
        check(df["absorbance"].notna().all(), "absorbance column numeric")
    except ImportError:
        print("  SKIP  pandas not installed")

    print("\n" + ("ALL PASS" if not fails else f"{len(fails)} FAILURES:"))
    for f in fails:
        print("  - " + f)
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
