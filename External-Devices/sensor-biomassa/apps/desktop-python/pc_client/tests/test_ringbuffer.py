"""
Port of the v04 firmware's ring buffer + buildHistoryJson(), line for line,
so the pagination/off-by-one logic can be exercised without hardware --
then drive the REAL SensorClient against it.
"""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from biomass_core import SensorClient, Transport

HISTORY_SIZE = 1024
HISTORY_MAX_RESPONSE = 60

fails = []
def check(c, m):
    print(("  PASS  " if c else "  FAIL  ") + m)
    if not c: fails.append(m)


class FirmwareRing:
    """Mirrors the C++ globals g_history / g_historyHead / g_historyCount."""

    def __init__(self, size=HISTORY_SIZE):
        self.size = size
        self.history = [None] * size
        self.head = 0
        self.count = 0
        self.seq = 0
        self.boot_id = 5

    def push_sample(self):
        self.seq += 1
        s = {"seq": self.seq, "t_ms": self.seq * 5000, "absorbance": 0.1,
             "raw": 100, "i0": 200, "it_ms": 400, "pwm_pct": 25.0,
             "hd_mode": False, "sat": False}
        # historyPush()
        self.history[self.head] = s
        self.head = (self.head + 1) % self.size
        self.count += 1

    def build_history_json(self, since_seq):
        # --- verbatim translation of buildHistoryJson() ---
        if self.count > self.size:
            oldest_index = self.count - self.size
            read_pos = self.head
        else:
            oldest_index = 0
            read_pos = 0

        emitted = 0
        out = []
        stored = self.size if self.count > self.size else self.count

        for i in range(stored):
            s = self.history[(read_pos + i) % self.size]
            if s["seq"] <= since_seq:
                continue
            if emitted >= HISTORY_MAX_RESPONSE:
                break
            out.append(s)
            emitted += 1

        more = False
        if emitted >= HISTORY_MAX_RESPONSE:
            last = self.history[(self.head + self.size - 1) % self.size]
            more = last["seq"] > since_seq + emitted

        return {"boot_id": self.boot_id, "seq": self.seq,
                "first_seq": oldest_index + 1, "samples": out,
                "count": emitted, "more": more}


class RingTransport(Transport):
    name = "ring"
    def __init__(self, ring): self.ring = ring
    def open(self): pass
    def close(self): pass
    def get_status(self):
        return {"fw": "4.0", "state": "measuring", "boot_id": self.ring.boot_id,
                "seq": self.ring.seq, "blank_done": True, "low": 1, "high": 2,
                "opt": 1, "hub_enabled": False}
    def get_blank_table(self): return {"i0": [[1] * 8] * 4}
    def get_history(self, since): return self.ring.build_history_json(since)
    def send_command(self, p): pass


print("[A] empty buffer")
r = FirmwareRing()
h = r.build_history_json(0)
check(h["count"] == 0, "no samples emitted")
check(h["samples"] == [], "empty list")
check(h["more"] is False, "more=false (must not read uninitialised slot)")
check(h["first_seq"] == 1, "first_seq=1 when empty")

print("\n[B] partially full (not wrapped)")
r = FirmwareRing(size=10)
for _ in range(4):
    r.push_sample()
h = r.build_history_json(0)
check([s["seq"] for s in h["samples"]] == [1, 2, 3, 4], "returns 1..4 in order")
check(h["first_seq"] == 1, "first_seq=1")
check(h["more"] is False, "no more")
h = r.build_history_json(2)
check([s["seq"] for s in h["samples"]] == [3, 4], "since=2 returns 3,4")

print("\n[C] exactly full")
r = FirmwareRing(size=10)
for _ in range(10):
    r.push_sample()
h = r.build_history_json(0)
check([s["seq"] for s in h["samples"]] == list(range(1, 11)), "all 10 in order")
check(h["first_seq"] == 1, "first_seq=1 at exactly full")

print("\n[D] wrapped: oldest samples evicted")
r = FirmwareRing(size=10)
for _ in range(13):
    r.push_sample()
h = r.build_history_json(0)
seqs = [s["seq"] for s in h["samples"]]
check(seqs == list(range(4, 14)), f"returns 4..13 oldest-first, got {seqs}")
check(h["first_seq"] == 4, f"first_seq={h['first_seq']}, want 4")
check(seqs == sorted(seqs), "wrapped read is still chronological")

print("\n[E] pagination at the 60-record cap")
r = FirmwareRing()
for _ in range(200):
    r.push_sample()
h = r.build_history_json(0)
check(h["count"] == 60, f"first page has 60, got {h['count']}")
check(h["more"] is True, "more=true with 140 still pending")
check([s["seq"] for s in h["samples"]] == list(range(1, 61)), "page 1 = 1..60")
h2 = r.build_history_json(60)
check([s["seq"] for s in h2["samples"]] == list(range(61, 121)), "page 2 = 61..120")
check(h2["more"] is True, "more=true on page 2")
h3 = r.build_history_json(180)
check([s["seq"] for s in h3["samples"]] == list(range(181, 201)), "last page 181..200")
check(h3["more"] is False, "more=false on the last page")

print("\n[F] exact boundary: precisely 60 pending")
r = FirmwareRing()
for _ in range(60):
    r.push_sample()
h = r.build_history_json(0)
check(h["count"] == 60, "emits all 60")
check(h["more"] is False, f"more={h['more']}, want False (nothing left)")

print("\n[G] caught up: since == newest")
r = FirmwareRing()
for _ in range(30):
    r.push_sample()
h = r.build_history_json(30)
check(h["count"] == 0, "nothing new")
check(h["more"] is False, "more=false")

print("\n[H] real SensorClient against the firmware ring, full buffer + overflow")
r = FirmwareRing()
for _ in range(3000):          # 3x the buffer: 1976 samples are unrecoverable
    r.push_sample()
logs = []
c = SensorClient(RingTransport(r), on_log=logs.append)
c.prime()
seqs = [s.seq for s in c.samples]
check(len(seqs) == 1024, f"client recovered {len(seqs)}, want 1024 (buffer size)")
check(seqs == list(range(1977, 3001)), "recovered exactly the surviving range")
check(seqs == sorted(set(seqs)), "no duplicates, strictly increasing")
check(any("not recoverable" in m for m in logs),
      "client warned about the unrecoverable range")
check(c.last_seq == 3000, f"last_seq={c.last_seq}")

print("\n[I] client keeps up incrementally across pages")
for _ in range(150):
    r.push_sample()
c._last_status_poll = 0
new = c.poll()
check(len(new) == 150, f"poll() recovered {len(new)} across 3 pages, want 150")
check([s.seq for s in new] == list(range(3001, 3151)), "contiguous, in order")

print("\n" + ("ALL PASS" if not fails else f"{len(fails)} FAILURES:"))
for f in fails:
    print("  - " + f)
sys.exit(1 if fails else 0)
