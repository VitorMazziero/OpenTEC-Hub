#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
communication/transport.py
~~~~~~~~~~~~~~~~~~~~~~~~~~
Transport layer: abstract base + USB and Wi-Fi implementations.

Each transport is responsible ONLY for the physical link:
  connect / disconnect / read / write / test_connection
No state management, no retry logic – that belongs to ConnectionManager.
"""
from __future__ import annotations

import threading
import time
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Optional

import requests
import serial
from serial.tools import list_ports


# ---------------------------------------------------------------------------
# Configuration value objects (immutable, safe to pass across threads)
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class USBConfig:
    port: str
    baud_rate: int = 115200
    data_bits: int = 8
    stop_bits: int = 1
    parity: str = serial.PARITY_NONE
    read_timeout_s: float = 0.75
    write_timeout_s: float = 1.0
    inter_byte_timeout_s: float = 0.1
    # Handshake tuning
    handshake_tries: int = 10
    boot_settle_s: float = 1.8     # CRITICAL: allow ESP32 bootloader to finish
    cancel_event: Optional[threading.Event] = field(default=None, compare=False, repr=False)

@dataclass(frozen=True)
class WiFiConfig:
    ip: str
    handshake_tries: int = 3
    connect_timeout_s: float = 0.25
    read_timeout_s: float = 0.75
    ping_timeout_s: float = 1.0
    min_poll_period_s: float = 1.0
    cancel_event: Optional[threading.Event] = field(default=None, compare=False, repr=False)

# ---------------------------------------------------------------------------
# Abstract base
# ---------------------------------------------------------------------------

class TransportLayer(ABC):
    """
    Minimal physical-link contract.

    Thread-safety contract:
      • connect() and disconnect() are called ONLY from the worker thread.
      • read() and write() are called from the main/Qt thread AFTER connect()
        returns True.  Implementations must be re-entrant for read+write.
    """

    @abstractmethod
    def connect(self) -> bool:
        """Attempt the full physical connection + handshake.  Returns True on success."""

    @abstractmethod
    def disconnect(self) -> None:
        """Release all resources unconditionally (must not raise)."""

    @abstractmethod
    def read(self) -> Optional[str]:
        """Return one line of data or None if nothing is available."""

    @abstractmethod
    def write(self, data: str) -> bool:
        """Send *data* to the device.  Returns True if the write succeeded."""

    @abstractmethod
    def test_connection(self) -> bool:
        """Lightweight liveness probe – called every second from the worker."""


# ---------------------------------------------------------------------------
# USB Transport
# ---------------------------------------------------------------------------

class USBTransport(TransportLayer):
    """Serial (USB CDC) transport for the TECNAL ESP32 controller."""

    _HANDSHAKE_PAYLOAD = b'{"comTest":1}\n'
    _HANDSHAKE_EXPECTED = "OK"

    def __init__(self, cfg: USBConfig) -> None:
        self._cfg = cfg
        self._ser: Optional[serial.Serial] = None
        self._rw_lock = threading.Lock()   # guards concurrent read/write

    # ------------------------------------------------------------------
    def connect(self) -> bool:
        self.disconnect()   # clean slate

        ser, err = self._open_with_timeout()
        if err or ser is None:
            return False

        # Hardware reset handshake (DTR/RTS pulse lets ESP32 boot cleanly)
        self._pulse_dtr_rts(ser)

        cancel_event = self._cfg.cancel_event
        settle_deadline = time.monotonic() + self._cfg.boot_settle_s
        while time.monotonic() < settle_deadline:
            if cancel_event and cancel_event.is_set():
                try:
                    ser.close()
                except Exception:
                    pass
                return False
            time.sleep(0.05)

        try:
            ser.reset_input_buffer()
            ser.reset_output_buffer()
        except Exception:
            pass

        if self._handshake(ser):
            self._ser = ser
            return True

        try:
            ser.close()
        except Exception:
            pass
        return False

    def disconnect(self) -> None:
        with self._rw_lock:
            if self._ser is not None:
                try:
                    self._ser.close()
                except Exception:
                    pass
                self._ser = None

    def read(self) -> Optional[str]:
        """
        Return only the most recent complete line available on the serial link.

        This prevents the application from lagging behind when the ESP32 sends
        data faster than the GUI/control loop consumes it.
        """
        with self._rw_lock:
            if self._ser is None:
                return None

            try:
                if not self._ser.in_waiting:
                    return None

                latest: Optional[str] = None
                drained = 0

                while self._ser.in_waiting:
                    line = self._ser.readline().decode(errors="ignore").strip()
                    if line:
                        latest = line
                        drained += 1

                return latest

            except Exception:
                return None

    def write(self, data: str) -> bool:
        with self._rw_lock:
            if self._ser is None:
                return False
            try:
                self._ser.write((data + "\n").encode("utf-8"))
                self._ser.flush()
                return True
            except Exception:
                return False

    def test_connection(self) -> bool:
        with self._rw_lock:
            if self._ser is None:
                return False
            try:
                _ = self._ser.in_waiting    # raises if port was unplugged
                return self._ser.is_open
            except Exception:
                return False

    # ------------------------------------------------------------------
    # Private helpers
    # ------------------------------------------------------------------

    def _open_with_timeout(self, timeout_s: float = 3.0):
        """Open the serial port in a side-thread so we can enforce a hard deadline."""
        holder: dict = {"ser": None, "err": None}

        def _opener() -> None:
            cfg = self._cfg
            try:
                holder["ser"] = serial.Serial(
                    port=cfg.port,
                    baudrate=cfg.baud_rate,
                    bytesize=cfg.data_bits,
                    stopbits=cfg.stop_bits,
                    parity=cfg.parity,
                    timeout=cfg.read_timeout_s,
                    write_timeout=cfg.write_timeout_s,
                    inter_byte_timeout=cfg.inter_byte_timeout_s,
                )
            except Exception as exc:
                holder["err"] = exc

        t = threading.Thread(target=_opener, daemon=True)
        t.start()
        t.join(timeout_s)
        if t.is_alive():
            return None, TimeoutError(f"open({self._cfg.port}) timed out after {timeout_s:.1f}s")
        return holder["ser"], holder["err"]

    @staticmethod
    def _pulse_dtr_rts(ser: serial.Serial) -> None:
        """Brief DTR/RTS low→high pulse to trigger ESP32 reset pin."""
        try:
            ser.dtr = False
            ser.rts = False
            time.sleep(0.05)
            ser.dtr = True
            ser.rts = True
        except Exception:
            pass   # Not all adapters support DTR/RTS; ignore silently

    def _handshake(self, ser: serial.Serial) -> bool:
        cfg = self._cfg
        cancel_event = cfg.cancel_event

        for attempt in range(1, cfg.handshake_tries + 1):
            if cancel_event and cancel_event.is_set():
                return False

            try:
                ser.write(self._HANDSHAKE_PAYLOAD)
                ser.flush()
            except Exception:
                pass

            reply: Optional[str] = None
            try:
                line = ser.readline()
                reply = line.decode(errors="ignore").strip() or None
            except Exception:
                pass

            if cancel_event and cancel_event.is_set():
                return False

            if reply == self._HANDSHAKE_EXPECTED:
                return True

            sleep_deadline = time.monotonic() + 0.1
            while time.monotonic() < sleep_deadline:
                if cancel_event and cancel_event.is_set():
                    return False
                time.sleep(0.01)

        return False

    # ------------------------------------------------------------------
    # Class-level helpers (no instance needed)
    # ------------------------------------------------------------------

    @classmethod
    def normalize_parity(cls, raw) -> str:
        """Convert human-readable parity string to a pyserial constant."""
        if isinstance(raw, str):
            mapping = {
                "NONE": serial.PARITY_NONE,
                "N":    serial.PARITY_NONE,
                "EVEN": serial.PARITY_EVEN,
                "E":    serial.PARITY_EVEN,
                "ODD":  serial.PARITY_ODD,
                "O":    serial.PARITY_ODD,
                "MARK": serial.PARITY_MARK,
                "M":    serial.PARITY_MARK,
                "SPACE": serial.PARITY_SPACE,
                "S":    serial.PARITY_SPACE,
            }
            return mapping.get(raw.strip().upper(), serial.PARITY_NONE)
        # Already a pyserial constant
        if raw in (serial.PARITY_NONE, serial.PARITY_EVEN,
                   serial.PARITY_ODD, serial.PARITY_MARK, serial.PARITY_SPACE):
            return raw
        return serial.PARITY_NONE

    @classmethod
    def list_candidate_ports(cls, include_all_non_bluetooth: bool = True) -> list[str]:
        """
        Return visible serial ports that are worth testing.

        By default:
        - prefer ESP-like ports first
        - then optionally include other non-Bluetooth serial ports
        """
        esp_keywords = (
            "CP210", "CH340", "CH910", "USB Serial", "ESP32",
            "Silicon Labs", "wch"
        )

        preferred = []
        fallback = []

        for info in list_ports.comports():
            desc = info.description or ""
            manu = getattr(info, "manufacturer", None) or ""
            hwid = info.hwid or ""

            if "Bluetooth" in desc:
                continue

            text = f"{desc} {manu} {hwid}".lower()

            if any(k.lower() in text for k in esp_keywords):
                preferred.append(info.device)
            elif include_all_non_bluetooth:
                fallback.append(info.device)

        # remove duplicates preserving order
        seen = set()
        ordered = []
        for port in preferred + fallback:
            if port not in seen:
                seen.add(port)
                ordered.append(port)

        return ordered

    @classmethod
    def probe_ports(cls, cfg: USBConfig) -> Optional[str]:
        """
        Try visible serial ports and return the first one that responds
        to the handshake.

        Strategy:
        1. Prefer likely ESP32/USB-UART adapters
        2. Fall back to other non-Bluetooth serial ports
        """
        candidate_ports = cls.list_candidate_ports(include_all_non_bluetooth=True)

        for port_name in candidate_ports:
            if port_name == cfg.port:
                # skip current port here; ConnectionManager already tried it
                continue

            candidate = USBConfig(
                port=port_name,
                baud_rate=cfg.baud_rate,
                data_bits=cfg.data_bits,
                stop_bits=cfg.stop_bits,
                parity=cfg.parity,
                read_timeout_s=cfg.read_timeout_s,
                write_timeout_s=cfg.write_timeout_s,
                inter_byte_timeout_s=cfg.inter_byte_timeout_s,
                handshake_tries=cfg.handshake_tries,
                boot_settle_s=cfg.boot_settle_s,
            )

            t = cls(candidate)
            try:
                if t.connect():
                    t.disconnect()
                    return port_name
            except Exception:
                try:
                    t.disconnect()
                except Exception:
                    pass

        return None

    @classmethod
    def describe_ports(cls) -> list[str]:
        """
        Human-readable serial port descriptions for diagnostics/logging.
        """
        out = []
        for info in list_ports.comports():
            desc = info.description or ""
            manu = getattr(info, "manufacturer", None) or ""
            hwid = info.hwid or ""
            out.append(f"{info.device} | desc={desc} | manufacturer={manu} | hwid={hwid}")
        return out
    
# ---------------------------------------------------------------------------
# Wi-Fi Transport
# ---------------------------------------------------------------------------

class WiFiTransport(TransportLayer):
    """HTTP transport (ESP32 Wi-Fi AP) for the TECNAL controller."""

    _HANDSHAKE_PAYLOAD = '{"comTest":1}'
    _HANDSHAKE_EXPECTED = "OK"

    def __init__(self, cfg: WiFiConfig) -> None:
        self._cfg = cfg
        self._session = requests.Session()
        self._session.trust_env = False
        self._etag: Optional[str] = None
        self._next_poll = 0.0

    @property
    def _base(self) -> str:
        return f"http://{self._cfg.ip}"

    def connect(self) -> bool:
        cfg = self._cfg
        
        # 1. Destroy the old session pool and create a entirely fresh one
        if hasattr(self, '_session'):
            self._session.close()
        self._session = requests.Session()
        self._session.trust_env = False
        
        # 2. Add 'Connection: close' to force a new TCP handshake, 
        # bypassing any OS-level cached sockets.
        headers = {
            "Content-Type": "application/json",
            "Connection": "close" 
        }
        url = f"{self._base}/command"

        cancel_event = cfg.cancel_event

        for attempt in range(1, cfg.handshake_tries + 1):
            if cancel_event and cancel_event.is_set():
                return False

            try:
                r = self._session.post(
                    url,
                    data=self._HANDSHAKE_PAYLOAD,
                    headers=headers,
                    timeout=(cfg.connect_timeout_s, cfg.read_timeout_s),
                )
                if r.text.strip() == self._HANDSHAKE_EXPECTED:
                    self._etag = None
                    self._next_poll = 0.0
                    return True
            except Exception:
                pass

            sleep_deadline = time.monotonic() + 0.1
            while time.monotonic() < sleep_deadline:
                if cancel_event and cancel_event.is_set():
                    return False
                time.sleep(0.01)

        return False
        
    def disconnect(self) -> None:
        self._etag = None
        self._next_poll = 0.0
        # 3. Explicitly close the session on disconnect
        if hasattr(self, '_session'):
            self._session.close()

    def read(self) -> Optional[str]:
        cfg = self._cfg
        now = time.monotonic()
        if now < self._next_poll:
            return None
        self._next_poll = now + getattr(self, '_override_poll_period_s', cfg.min_poll_period_s) * 0.98

        headers: dict = {}
        if self._etag:
            headers["If-None-Match"] = self._etag

        try:
            r = self._session.get(
                f"{self._base}/readData",
                headers=headers,
                timeout=(cfg.connect_timeout_s, cfg.read_timeout_s),
            )
        except Exception:
            return None

        if r.status_code == 304:
            return None
        if r.status_code != 200:
            return None

        etag = r.headers.get("ETag")
        if etag:
            self._etag = etag
        return r.text.strip() or None

    def write(self, data: str) -> bool:
        try:
            self._session.post(
                f"{self._base}/command",
                data=data,
                headers={"Content-Type": "application/json"},
                timeout=0.5,
            )
            return True
        except Exception:
            return False

    def test_connection(self) -> bool:
        try:
            r = self._session.get(f"{self._base}/ping", timeout=self._cfg.ping_timeout_s)
            return r.status_code == 200
        except Exception:
            return False

    def set_poll_period(self, period_s: float) -> None:
        self._override_poll_period_s = max(0.1, period_s)