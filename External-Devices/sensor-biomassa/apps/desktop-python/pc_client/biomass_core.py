"""
biomass_core.py -- transport, protocol and recording for the biomass sensor.

Deliberately free of GUI dependencies: this module only needs pyserial and/or
requests, so it can run headless on a lab PC for a multi-day cultivation while
the GUI in biomass_logger.py is an optional viewer on top.

Talks to firmware biomass_sensor_analog_v04_direct (v5.0) over either:
  * USB serial  -- the device pushes one JSON line per sample, unprompted
  * HTTP        -- the device AP at 192.168.7.1, polled

Both transports expose the same protocol, so nothing above this layer cares
which one is in use.

Gap handling is the reason this file is more than a print loop. The device
keeps a 1024-sample ring buffer and tags every sample with a monotonic `seq`.
If the PC misses samples (sleep, USB unplug, WiFi drop) the client notices the
seq discontinuity and backfills from the device buffer, so the CSV has no
holes as long as the outage was shorter than the buffer. That horizon is set
by the device's sampling interval, which since v5.0 has a hard floor of
~24.3 s in auto-range (the LED thermal duty limit), so 1024 samples is about
7 hours -- comfortably longer than the ~85 min it was at the old 5 s cadence.

Because those samples are older than their arrival, every timestamp is
reconstructed rather than taken from the clock on receipt: the device stamps
each sample with its own millis(), and the client maps that to wall clock
through one anchor per boot, derived from the uptime the device reports in
its status. See SensorClient.anchor_from_status.
"""

from __future__ import annotations

import csv
import json
import queue
import threading
import time
from dataclasses import dataclass, asdict, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Iterable, Optional

# --- Sentinels used by the firmware ---------------------------------------
ABS_ERROR_BLANK = -99.0  # blank was 0 or saturated -> absorbance meaningless
ABS_ERROR_ZERO = 9.9     # measured I == 0 -> effectively infinite absorbance

# --- Blank-table cell validity (must match blankIsValid() in the firmware) --
# A cell the firmware will not select as a gear must not be shown as usable,
# or the UI contradicts the device. Both bounds are firmware constants:
#   SATURATION_RAW  = 65530   (sensor pegged during the sweep)
#   MIN_VALID_BLANK =   500   (v5.0; below this the cell measured the dark)
# The lower bound exists because "non-zero" used to be the whole test, which
# let a pre-v4.6 blank of 9 counts pass as a usable gear.
BLANK_SATURATED = 65530
MIN_VALID_BLANK = 500


def blank_cell_state(i0: int) -> str:
    """Classifies a blank cell exactly as the firmware's blankIsValid() does.

    Returns "saturated", "too_dim" or "ok". Only "ok" cells can produce
    absorbance; the device refuses to select the others as a gear.
    """
    if i0 >= BLANK_SATURATED:
        return "saturated"
    if i0 < MIN_VALID_BLANK:
        return "too_dim"
    return "ok"

DEFAULT_HTTP_HOST = "192.168.7.1"
DEFAULT_BAUD = 115200

# USB VIDs seen on these boards: Espressif native CDC, CH340, CP210x, FTDI.
KNOWN_VIDS = {0x303A, 0x1A86, 0x10C4, 0x0403}


# ==========================================================================
# Sample
# ==========================================================================

@dataclass
class Sample:
    seq: int
    t_ms: int
    boot_id: int
    absorbance: float
    raw: int
    i0: int
    it_ms: int
    pwm_pct: float
    hd_mode: bool = False
    sat: bool = False
    #: single-shot read: unfiltered, so it does not belong in a kinetics fit
    #: alongside median+EMA filtered points
    single: bool = False
    #: taken with auto-ranging disabled (operator locked the gear)
    manual: bool = False
    #: wall-clock time this sample was *taken*, reconstructed from t_ms via
    #: the transport anchor. Not the time it reached the PC -- backfilled
    #: samples are older than their arrival.
    wall_epoch: float = 0.0

    @property
    def valid(self) -> bool:
        """False for the firmware's absorbance error sentinels."""
        return ABS_ERROR_BLANK + 1 < self.absorbance < ABS_ERROR_ZERO - 0.001

    @property
    def transmittance(self) -> Optional[float]:
        if not self.i0:
            return None
        return self.raw / self.i0

    @property
    def iso(self) -> str:
        return datetime.fromtimestamp(self.wall_epoch, tz=timezone.utc).astimezone().isoformat()

    @classmethod
    def from_json(cls, d: dict, boot_id: int = 0) -> "Sample":
        return cls(
            seq=int(d.get("seq", 0)),
            t_ms=int(d.get("t_ms", 0)),
            boot_id=int(d.get("boot_id", boot_id)),
            absorbance=float(d.get("absorbance", 0.0)),
            raw=int(d.get("raw", 0)),
            i0=int(d.get("i0", 0)),
            it_ms=int(d.get("it_ms", 0)),
            pwm_pct=float(d.get("pwm_pct", 0.0)),
            hd_mode=bool(d.get("hd_mode", False)),
            sat=bool(d.get("sat", False)),
            single=bool(d.get("single", False)),
            manual=bool(d.get("manual", False)),
        )


# ==========================================================================
# Transports
# ==========================================================================

class TransportError(Exception):
    pass


class Transport:
    """Common interface. Implementations must be safe to call from one thread."""

    name = "base"

    def open(self) -> None: ...
    def close(self) -> None: ...
    def get_status(self) -> dict: raise NotImplementedError
    def get_blank_table(self) -> dict: raise NotImplementedError
    def get_history(self, since: int) -> dict: raise NotImplementedError
    def send_command(self, payload: dict) -> None: raise NotImplementedError

    def drain_pushed(self) -> list[dict]:
        """Samples the device sent unprompted. Empty for poll-only transports."""
        return []

    def drain_log(self) -> list[str]:
        """Human-readable device output. Empty for transports that have none.

        Declared on the base so callers need not know which transport they
        hold -- HTTP genuinely has no log channel, but serial and the demo
        both do, and diagnostics like probe_period report through it.
        """
        return []


class HttpTransport(Transport):
    """Polls the device AP. Requires `requests`."""

    name = "http"

    #: Seconds allowed for the TCP handshake, separately from the read. An
    #: unreachable AP fails here, and it is the number that decides how long a
    #: caller is stuck when the link drops -- the GUI polls this transport from
    #: the Qt thread, so every second here is a second of frozen window. The
    #: device is one hop away on its own AP, so a handshake that takes longer
    #: than this is not slow, it is gone.
    CONNECT_TIMEOUT = 1.5

    #: Cool-off after a failed poll, doubling up to the cap. Without it a dead
    #: link costs a full CONNECT_TIMEOUT on *every* GUI tick, which is what
    #: made the app crawl instead of simply reporting itself offline. Operator
    #: commands ignore this and always go out -- a button press is a question
    #: about right now, and it answers in CONNECT_TIMEOUT either way.
    BACKOFF_START = 2.0
    BACKOFF_MAX = 30.0

    def __init__(self, host: str = DEFAULT_HTTP_HOST, timeout: float = 4.0):
        self.host = host if host.startswith("http") else f"http://{host}"
        self.timeout = timeout
        self._session = None
        self._down_until = 0.0
        self._backoff = 0.0

    def open(self) -> None:
        import requests  # imported here so serial-only users need not install it
        self._session = requests.Session()
        self._down_until = 0.0
        self._backoff = 0.0
        self.get_status()  # fail fast if unreachable

    def close(self) -> None:
        if self._session is not None:
            self._session.close()
            self._session = None

    # -- reachability ------------------------------------------------------
    def _mark_up(self) -> None:
        self._down_until = 0.0
        self._backoff = 0.0

    def _mark_down(self) -> None:
        self._backoff = (self.BACKOFF_START if not self._backoff
                         else min(self._backoff * 2, self.BACKOFF_MAX))
        self._down_until = time.monotonic() + self._backoff

    @property
    def offline_for(self) -> float:
        """Seconds until the next poll will be attempted; 0 when reachable."""
        return max(0.0, self._down_until - time.monotonic())

    def _get(self, path: str, honour_backoff: bool = True) -> dict:
        if self._session is None:
            raise TransportError("transport not open")
        if honour_backoff:
            wait = self.offline_for
            if wait > 0:
                # Raised without touching the network: the point is to cost
                # the caller nothing while the device is known to be away.
                raise TransportError(
                    f"device unreachable; retrying in {wait:.0f}s")
        try:
            r = self._session.get(self.host + path,
                                  timeout=(self.CONNECT_TIMEOUT, self.timeout))
            r.raise_for_status()
            data = r.json()
        except Exception as exc:
            self._mark_down()
            raise TransportError(f"GET {path}: {exc}") from exc
        self._mark_up()
        return data

    def get_status(self) -> dict:
        return self._get("/api/status")

    def get_blank_table(self) -> dict:
        return self._get("/api/blank")

    def get_history(self, since: int) -> dict:
        return self._get(f"/api/history?since={since}")

    def send_command(self, payload: dict) -> None:
        if self._session is None:
            raise TransportError("transport not open")
        # Compact separators, matching SerialTransport and the device's own web
        # UI. requests' `json=` kwarg would use json.dumps() defaults -- ": "
        # and ", " -- and firmware up to v5.2 parses commands by searching for
        # the literal `"command":"`, so a space after the colon made every
        # string command a no-op that still answered 200. v5.3 accepts either;
        # sending what the firmware has always understood keeps this app
        # working against devices in the field that have not been reflashed.
        body = json.dumps(payload, separators=(",", ":"))
        try:
            r = self._session.post(
                self.host + "/api/command", data=body.encode("utf-8"),
                headers={"Content-Type": "application/json"},
                timeout=(self.CONNECT_TIMEOUT, self.timeout))
            r.raise_for_status()
        except Exception as exc:
            self._mark_down()
            raise TransportError(f"POST command: {exc}") from exc
        self._mark_up()


class SerialTransport(Transport):
    """
    USB serial. A reader thread classifies every JSON line the device emits.

    The firmware interleaves human-readable log text with machine-readable
    JSON on the same port; only lines starting with '{' are parsed, which is
    the contract the firmware documents.
    """

    name = "serial"

    def __init__(self, port: str, baud: int = DEFAULT_BAUD, timeout: float = 4.0):
        self.port = port
        self.baud = baud
        self.timeout = timeout
        self._ser = None
        self._thread: Optional[threading.Thread] = None
        self._stop = threading.Event()
        self._samples: "queue.Queue[dict]" = queue.Queue()
        self._status: "queue.Queue[dict]" = queue.Queue()
        self._history: "queue.Queue[dict]" = queue.Queue()
        self._blank: "queue.Queue[dict]" = queue.Queue()
        self._log_lines: "queue.Queue[str]" = queue.Queue()
        self._write_lock = threading.Lock()

    def open(self) -> None:
        import serial  # pyserial
        self._ser = serial.Serial(self.port, self.baud, timeout=0.4)
        # Some ESP32-S3 boards reset when DTR/RTS toggle on open. Give the
        # firmware time to boot before we expect it to answer anything.
        time.sleep(0.3)
        self._ser.reset_input_buffer()
        self._stop.clear()
        self._thread = threading.Thread(target=self._reader, daemon=True)
        self._thread.start()

    def close(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
        if self._ser is not None:
            try:
                self._ser.close()
            finally:
                self._ser = None

    def _reader(self) -> None:
        buf = b""
        while not self._stop.is_set():
            try:
                chunk = self._ser.read(512)
            except Exception:
                break
            if not chunk:
                continue
            buf += chunk
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                self._classify(line.decode("utf-8", errors="replace").strip())
            if len(buf) > 65536:      # runaway guard on a line with no newline
                buf = b""

    def _classify(self, line: str) -> None:
        if not line:
            return
        if not line.startswith("{"):
            self._log_lines.put(line)
            return
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            self._log_lines.put(line)
            return
        if "samples" in obj:
            self._history.put(obj)
        elif "i0" in obj and isinstance(obj.get("i0"), list):
            self._blank.put(obj)
        elif "fw" in obj and "state" in obj:
            self._status.put(obj)
        elif "absorbance" in obj and "seq" in obj:
            self._samples.put(obj)
        else:
            self._log_lines.put(line)

    def _request(self, payload: dict, q: "queue.Queue[dict]", what: str) -> dict:
        # Drop anything stale so we do not return a previous reply.
        while not q.empty():
            try:
                q.get_nowait()
            except queue.Empty:
                break
        self.send_command(payload)
        try:
            return q.get(timeout=self.timeout)
        except queue.Empty:
            raise TransportError(f"timed out waiting for {what}")

    def get_status(self) -> dict:
        return self._request({"command": "status"}, self._status, "status")

    def get_blank_table(self) -> dict:
        return self._request({"command": "print_blank"}, self._blank, "blank table")

    def get_history(self, since: int) -> dict:
        return self._request({"command": "history", "since": int(since)},
                             self._history, "history")

    def send_command(self, payload: dict) -> None:
        if self._ser is None:
            raise TransportError("transport not open")
        data = (json.dumps(payload, separators=(",", ":")) + "\n").encode()
        with self._write_lock:
            self._ser.write(data)
            self._ser.flush()

    def drain_pushed(self) -> list[dict]:
        out = []
        while True:
            try:
                out.append(self._samples.get_nowait())
            except queue.Empty:
                return out

    def drain_log(self) -> list[str]:
        out = []
        while True:
            try:
                out.append(self._log_lines.get_nowait())
            except queue.Empty:
                return out


# ==========================================================================
# Discovery
# ==========================================================================

def list_candidate_ports() -> list[str]:
    """Serial ports whose USB VID matches a board we might be talking to."""
    try:
        from serial.tools import list_ports
    except ImportError:
        return []
    ranked, other = [], []
    for p in list_ports.comports():
        (ranked if p.vid in KNOWN_VIDS else other).append(p.device)
    return ranked + other


def discover(prefer: str = "serial", http_host: str = DEFAULT_HTTP_HOST,
             log: Callable[[str], None] = print) -> Transport:
    """
    Finds a device: tries serial ports, then the AP.

    Ports are probed by actually asking for status -- VID matching alone would
    happily hand back an unrelated CH340 device sitting on the same bench.
    """
    attempts: list[Transport] = []
    ports = list_candidate_ports()
    serial_first = prefer == "serial"

    def serial_attempts():
        return [SerialTransport(p, timeout=3.0) for p in ports]

    def http_attempts():
        return [HttpTransport(http_host, timeout=3.0)]

    attempts = (serial_attempts() + http_attempts()) if serial_first else \
               (http_attempts() + serial_attempts())

    for t in attempts:
        label = f"{t.name}:{getattr(t, 'port', getattr(t, 'host', ''))}"
        try:
            log(f"probing {label} ...")
            t.open()
            st = t.get_status()
            if "fw" in st:
                log(f"found firmware v{st['fw']} on {label}")
                return t
            t.close()
        except Exception as exc:
            log(f"  no: {exc}")
            try:
                t.close()
            except Exception:
                pass
    raise TransportError("no biomass sensor found on serial ports or the AP")


# ==========================================================================
# Recorder
# ==========================================================================

CSV_COLUMNS = [
    "iso_time", "epoch", "t_ms", "seq", "boot_id",
    "absorbance", "transmittance", "raw", "i0",
    "it_ms", "pwm_pct", "hd_mode", "saturated", "single", "manual", "valid",
]

ANCHOR_NOTE = ("wall clock for device t_ms=0; iso_time = anchor + t_ms/1000")


class Recorder:
    """
    Writes one run to `<outdir>/<timestamp>_<name>/` as data.csv + meta.json.

    Every row is flushed immediately. A cultivation run is long and the whole
    point of the PC logger is that a crash or power cut costs you the last
    sample, not the last six hours.
    """

    def __init__(self, outdir: Path, name: str = "run"):
        safe = "".join(c if (c.isalnum() or c in "-_") else "_" for c in name).strip("_")
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        self.dir = Path(outdir) / f"{stamp}_{safe or 'run'}"
        self.dir.mkdir(parents=True, exist_ok=True)
        self.csv_path = self.dir / "data.csv"
        self.meta_path = self.dir / "meta.json"
        self.name = name
        self.count = 0
        self._fh = open(self.csv_path, "w", newline="", encoding="utf-8")
        self._writer = csv.writer(self._fh)
        self._writer.writerow(CSV_COLUMNS)
        self._fh.flush()
        self._meta = {
            "run_name": name,
            "started": datetime.now().astimezone().isoformat(),
            "csv": self.csv_path.name,
            "columns": CSV_COLUMNS,
            "notes": (
                "absorbance = -log10(raw/i0), computed on-device. raw and i0 are "
                "recorded so absorbance can be recomputed if the blank is redone. "
                "absorbance -99 means the blank for that gear was 0 or saturated; "
                "9.9 means the measured signal was 0. valid=0 marks both."
            ),
        }
        self._write_meta()

    def _write_meta(self) -> None:
        self.meta_path.write_text(json.dumps(self._meta, indent=2), encoding="utf-8")

    @property
    def meta(self) -> dict:
        """The metadata written so far. Read-only by convention."""
        return self._meta

    def set_meta(self, **kw) -> None:
        self._meta.update(kw)
        self._write_meta()

    def copy_rows_from(self, csv_path: Path) -> int:
        """
        Appends another run's data rows to this one, header excluded.

        This is how a run follows the operator when they rename it or point it
        at a different folder mid-experiment: the samples already on disk are
        carried into the new file rather than stranded in a folder named after
        a decision that was since changed.
        """
        try:
            with open(csv_path, newline="", encoding="utf-8") as fh:
                rows = list(csv.reader(fh))
        except OSError:
            return 0
        moved = 0
        for row in rows:
            if not row or row[0] == CSV_COLUMNS[0]:
                continue                      # header, or a blank line
            self._writer.writerow(row)
            moved += 1
        self._fh.flush()
        self.count += moved
        return moved

    def write(self, s: Sample) -> None:
        t = s.transmittance
        self._writer.writerow([
            s.iso, f"{s.wall_epoch:.3f}", s.t_ms, s.seq, s.boot_id,
            f"{s.absorbance:.4f}", "" if t is None else f"{t:.6f}",
            s.raw, s.i0, s.it_ms, f"{s.pwm_pct:.1f}",
            int(s.hd_mode), int(s.sat), int(s.single), int(s.manual),
            int(s.valid),
        ])
        self._fh.flush()
        self.count += 1

    def close(self) -> None:
        if self._fh and not self._fh.closed:
            self._fh.close()
        self._meta["ended"] = datetime.now().astimezone().isoformat()
        self._meta["samples"] = self.count
        self._write_meta()


# ==========================================================================
# Client
# ==========================================================================

class SensorClient:
    """
    Drives a transport, deduplicates by seq, backfills gaps, feeds a Recorder.

    Call `poll()` on a timer (or use `run_forever`). Callbacks fire on the
    calling thread, so a GUI can drive this from its own timer without locks.
    """

    def __init__(self, transport: Transport, recorder: Optional[Recorder] = None,
                 on_sample: Optional[Callable[[Sample], None]] = None,
                 on_status: Optional[Callable[[dict], None]] = None,
                 on_log: Optional[Callable[[str], None]] = None):
        self.t = transport
        self.recorder = recorder
        self.on_sample = on_sample
        self.on_status = on_status
        self.on_log = on_log or (lambda m: None)

        self.last_seq = 0
        self.boot_id: Optional[int] = None
        self.status: dict = {}
        self.samples: list[Sample] = []
        #: samples accepted during the current poll(), from whichever path
        #: produced them. Both the push path and backfill feed this, so
        #: poll()'s return value is complete regardless of which ran.
        self._emitted: list[Sample] = []
        #: wall-clock epoch corresponding to device t_ms == 0, per boot.
        self._anchor: Optional[float] = None
        #: how that anchor was derived, recorded so a CSV can be trusted or
        #: doubted on its own; see anchor_from_status
        self._anchor_source: Optional[str] = None
        self._last_status_poll = 0.0
        self._connected = True

    # -- time base ---------------------------------------------------------
    def _set_anchor(self, epoch: float, source: str) -> None:
        self._anchor = epoch
        self._anchor_source = source
        if self.recorder:
            self.recorder.set_meta(time_anchor_epoch=epoch,
                                   time_anchor_note=ANCHOR_NOTE,
                                   time_anchor_source=source)

    def anchor_from_status(self, st: dict) -> bool:
        """
        Anchors device t_ms=0 on the PC clock using the device's own uptime.

        This is the only trustworthy anchor. The obvious alternative -- assume
        the first sample we see was taken just now -- is wrong exactly when it
        matters: the first sample is normally the OLDEST record in the device
        ring buffer, so it declares an hour-old sample to be current and
        shifts the entire run, backfill and live samples alike, an hour into
        the future.
        """
        try:
            uptime_ms = float(st["uptime_ms"])
        except (KeyError, TypeError, ValueError):
            return False
        # Biased late by the transport round trip (the device measured its
        # uptime before we read the clock), which is milliseconds on serial
        # against a 25 s sampling interval.
        self._set_anchor(time.time() - uptime_ms / 1000.0, "uptime_ms")
        return True

    def _anchor_wall(self, t_ms: int) -> float:
        """
        Maps a device uptime to wall clock.

        One anchor per boot: device millis() and the PC clock drift apart
        slowly, and over a 48 h run that is seconds -- far below the 25 s
        sampling interval -- so re-anchoring would add jitter, not accuracy.
        """
        if self._anchor is None:
            # Firmware too old to report uptime_ms, or a pushed sample that
            # beat the first status. Assuming this sample is current is only
            # safe for the NEWEST one, which is what _backfill seeds from.
            self._set_anchor(time.time() - t_ms / 1000.0, "first_sample")
        return self._anchor + t_ms / 1000.0

    def _reset_for_new_boot(self, boot_id: int) -> None:
        self.on_log(f"device boot #{boot_id} detected -- restarting sample stream")
        self.boot_id = boot_id
        self.last_seq = 0
        self._anchor = None
        self._anchor_source = None

    # -- ingest ------------------------------------------------------------
    def _ingest(self, raw: dict) -> Optional[Sample]:
        bid = int(raw.get("boot_id", self.boot_id or 0))
        if self.boot_id is None:
            self.boot_id = bid
        elif bid != self.boot_id:
            self._reset_for_new_boot(bid)

        s = Sample.from_json(raw, boot_id=bid)
        if s.seq <= self.last_seq:
            return None                      # already have it
        if s.seq == 0:
            return None                      # pre-first-sample placeholder
        s.wall_epoch = self._anchor_wall(s.t_ms)

        if self.last_seq and s.seq > self.last_seq + 1:
            missing = s.seq - self.last_seq - 1
            self.on_log(f"gap of {missing} sample(s) before seq {s.seq}; backfilling")
            self._backfill(self.last_seq, stop_before=s.seq)

        self.last_seq = max(self.last_seq, s.seq)
        self._accept(s)
        return s

    def _accept(self, s: Sample) -> None:
        """Single funnel for every accepted sample: store, record, notify."""
        self.samples.append(s)
        self._emitted.append(s)
        if self.recorder:
            self.recorder.write(s)
        if self.on_sample:
            self.on_sample(s)

    def _backfill(self, since: int, stop_before: Optional[int] = None) -> int:
        """Pulls the device ring buffer to fill a hole. Returns count recovered."""
        recovered = 0
        cursor = since
        for _ in range(64):  # bounded: 64 * 60 records covers the whole buffer
            try:
                h = self.t.get_history(cursor)
            except TransportError as exc:
                self.on_log(f"backfill failed: {exc}")
                return recovered
            batch = h.get("samples", [])
            if not batch:
                return recovered
            if self._anchor is None:
                # Fallback path only (no uptime_ms in status). Seed from the
                # newest record in the batch: it is at most one sampling
                # interval old, where the oldest can be hours old.
                newest = max(int(r.get("t_ms", 0)) for r in batch)
                self._set_anchor(time.time() - newest / 1000.0, "newest_sample")
            if cursor == 0 and h.get("first_seq", 1) > 1:
                self.on_log(
                    f"device buffer starts at seq {h['first_seq']}; samples "
                    f"1-{h['first_seq'] - 1} were taken before this session "
                    f"and are not recoverable"
                )
            for raw in batch:
                seq = int(raw.get("seq", 0))
                if stop_before is not None and seq >= stop_before:
                    return recovered
                if seq <= self.last_seq:
                    cursor = max(cursor, seq)
                    continue
                # Inline, not via _ingest, to avoid recursive backfill.
                s = Sample.from_json(raw, boot_id=self.boot_id or 0)
                s.wall_epoch = self._anchor_wall(s.t_ms)
                self.last_seq = s.seq
                self._accept(s)
                recovered += 1
                cursor = s.seq
            if not h.get("more"):
                return recovered
        return recovered

    # -- recorder ----------------------------------------------------------
    def attach_recorder(self, rec: Optional[Recorder],
                        blank_table: bool = True) -> None:
        """
        Points the client at a new file and stamps the run's context into it.

        Every recorder needs the same three things to be interpretable on its
        own -- what device produced it, what time base its t_ms column is on,
        and which blank the absorbances were computed against -- and a run
        started mid-session used to get only the first of them.
        """
        self.recorder = rec
        if rec is None:
            return
        if self.status:
            rec.set_meta(device=self.status, transport=self.t.name)
        if self._anchor is not None:
            # Including the source. A run that starts after the anchor was
            # already established -- which is every run begun from the window
            # rather than at connect -- otherwise recorded the time base
            # without saying where it came from.
            rec.set_meta(time_anchor_epoch=self._anchor,
                         time_anchor_note=ANCHOR_NOTE,
                         time_anchor_source=self._anchor_source or "unknown")
        if blank_table:
            try:
                rec.set_meta(blank_table=self.t.get_blank_table())
            except TransportError as exc:
                self.on_log(f"could not read blank table: {exc}")

    def replay_history(self, since: int = 0) -> int:
        """
        Re-emits samples the device still holds, oldest first.

        Used to begin a run from what the sensor recorded before the operator
        opened the app: the ring buffer is the only copy of it.
        """
        self.last_seq = since
        return self._backfill(since)

    def clear_device_history(self) -> bool:
        """
        Asks the device to drop its ring buffer. True if it actually did.

        Firmware older than the command ignores unknown commands silently, so
        the device's own hist_stored count is the only honest confirmation.
        """
        self.command("clear_history")
        try:
            st = self.t.get_status()
        except TransportError as exc:
            self.on_log(f"could not confirm the buffer clear: {exc}")
            return False
        self.status = st
        # Nothing older than the device's current seq exists to fetch any
        # more; leaving last_seq behind would make the next poll ask for
        # samples the device just discarded.
        self.last_seq = max(self.last_seq, int(st.get("seq", 0)))
        return int(st.get("hist_stored", -1)) == 0

    # -- polling -----------------------------------------------------------
    def prime(self) -> None:
        """Initial catch-up: status, blank table into metadata, full backfill."""
        st = self.t.get_status()
        self.status = st
        self.boot_id = int(st.get("boot_id", 0))
        # Before any sample is ingested: _anchor_wall() is called for every
        # one of them and would otherwise fall back to the oldest record.
        self.anchor_from_status(st)
        if self.on_status:
            self.on_status(st)
        if self.recorder:
            self.attach_recorder(self.recorder)
        n = self._backfill(0)
        if n:
            self.on_log(f"backfilled {n} sample(s) from the device buffer")

    def poll(self, status_period: float = 5.0) -> list[Sample]:
        """
        One polling step. Returns every sample accepted during this call,
        no matter which path produced it (push, status-triggered backfill,
        or the HTTP history poll).
        """
        self._emitted = []

        for raw in self.t.drain_pushed():          # serial: unprompted samples
            self._ingest(raw)

        # Every transport declares drain_log(); the ones without a log channel
        # return nothing. Diagnostics such as probe_period report only here,
        # so gating this on the transport type would silently swallow them.
        for line in self.t.drain_log():
            self.on_log(f"[dev] {line}")

        now = time.time()
        if now - self._last_status_poll >= status_period:
            self._last_status_poll = now
            try:
                st = self.t.get_status()
                if not self._connected:
                    self._connected = True
                    self.on_log("reconnected")
                self.status = st
                bid = int(st.get("boot_id", 0))
                if self.boot_id is not None and bid != self.boot_id:
                    self._reset_for_new_boot(bid)
                # Re-anchor before backfilling, not after: the samples that
                # backfill pulls are timestamped against whatever anchor is
                # in place when they arrive.
                if self._anchor is None:
                    self.anchor_from_status(st)
                if int(st.get("seq", 0)) > self.last_seq:
                    self._backfill(self.last_seq)
                if self.on_status:
                    self.on_status(st)
            except TransportError as exc:
                if self._connected:
                    self._connected = False
                    self.on_log(f"lost contact: {exc}")

        # HTTP has no push channel, so history is the only way new samples
        # arrive. Harmless if the status branch above already caught up.
        #
        # Skipped entirely while the transport knows it cannot reach the
        # device: attempting it would cost a connect timeout on the caller's
        # thread -- the GUI's -- every tick, and log the same failure every
        # tick. The status branch above owns the "lost contact" message.
        if (not isinstance(self.t, SerialTransport)
                and not getattr(self.t, "offline_for", 0.0)):
            try:
                self._backfill(self.last_seq)
            except TransportError as exc:
                self.on_log(f"poll failed: {exc}")

        return self._emitted

    def command(self, name: str, **kw) -> None:
        payload = {"command": name}
        payload.update(kw)
        self.t.send_command(payload)
        self.on_log(f"-> {payload}")

    def set_param(self, **kw) -> None:
        """Sends a bare settings payload, e.g. set_param(low=8000, ema=0.5)."""
        self.t.send_command(dict(kw))
        self.on_log(f"-> {kw}")

    # -- notes -------------------------------------------------------------
    def annotate(self, text: str) -> None:
        """Records an operator note in the run metadata, timestamped."""
        if not self.recorder:
            return
        notes = self.recorder.meta.get("operator_notes", [])
        notes.append({"time": datetime.now().astimezone().isoformat(),
                      "seq": self.last_seq, "text": text})
        self.recorder.set_meta(operator_notes=notes)
        self.on_log(f"note: {text}")

    def run_forever(self, interval: float = 2.0) -> None:
        self.prime()
        while True:
            self.poll()
            time.sleep(interval)
