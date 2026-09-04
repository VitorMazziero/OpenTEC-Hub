#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SerialApp – soft-sensor for dissolved oxygen (percentage scale, 1-Hz TX)
------------------------------------------------------------------------
* Handshake:        {"comTest": 1}  →  OK\r\n
* Continuous mode:  {"oxygenMonitor": <num>}   (>0 start, ≤0 stop)
* Payload (1 Hz):   {"Time": <s>, "Oxyval": <12-bit DO-%>}\r\n
* Serial:           115200-8-N-1, CR+LF frames.
"""

from __future__ import annotations
import json, threading, time, tkinter as tk
from tkinter import scrolledtext
from typing import Callable

import numpy as np
import serial

# optional SciPy surface ------------------------------------------------------
try:
    from scipy.interpolate import griddata  # type: ignore
except ImportError:                         # pragma: no cover
    griddata = None

class SerialApp:
    # ------------------------------------------------------------------ 1 GUI
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("Serial Communication")

        # ---------- serial ---------------------------------------------------
        self.serial_connection: serial.Serial | None = None
        self.running = False
        self._t0 = time.monotonic()                   # reference for "Time" field

        # ---------- soft-sensor state (percentage) ---------------------------
        self.C_star = 100.0        # % at saturation
        self.C = 90.0              # initial 90 %
        self.OUR_h = 2812          # % h⁻¹
        self.Q_target = np.nan
        self.N_target = np.nan
        self.last_step: float | None = None           # monotonic seconds

        self.oxygen_monitor_active = False
        self._next_o2_push = time.monotonic() + 1.0

        # ---------- kLa surface ----------------------------------------------
        self.kla_interp: Callable[[float, float], float] = lambda *_: 0.0

        # ---------- receive buffer -------------------------------------------
        self.rx_buffer = ""

        # ---------- GUI widgets ----------------------------------------------
        self.open_button = tk.Button(root, text="Open Serial",
                                     command=self.open_serial)
        self.open_button.pack(pady=5)

        self.close_button = tk.Button(root, text="Close Serial",
                                      command=self.close_serial,
                                      state=tk.DISABLED)
        self.close_button.pack(pady=5)

        self.log = scrolledtext.ScrolledText(root, width=60, height=22,
                                             state=tk.DISABLED)
        self.log.pack(pady=10)

        self.command_entry = tk.Entry(root, width=45)
        self.command_entry.pack(pady=5)

        self.send_button = tk.Button(root, text="Send Command",
                                     command=self.send_command,
                                     state=tk.DISABLED)
        self.send_button.pack(pady=5)

    # ------------------------------------------------------------------ 2 kLa
    def configure_kla_surface(self, pts: np.ndarray, vals: np.ndarray) -> None:
        if griddata is None:
            self.log_message("SciPy absent – kLa interpolation disabled.")
            return
        if len(pts) < 4:
            self.log_message("Need ≥4 (Q,N) points for interpolation.")
            return
        try:
            _pts = np.asarray(pts, dtype=float)
            _vals = np.asarray(vals, dtype=float)
            def _interp(q: float, n: float) -> float:
                return float(griddata(_pts, _vals, (q, n), method="cubic", fill_value=np.nan))
            self.kla_interp = _interp
            self.log_message("kLa surface configured.")
        except Exception as exc:                       # pragma: no cover
            self.kla_interp = lambda *_: 0.0
            self.log_message(f"kLa surface failed: {exc}")
        print(f"Configured kLa surface with {len(pts)} points.")  # debug
        self.log_message(f"kLa surface: {pts.shape[0]} points, "
                         f"{pts.shape[1]} dimensions.")
        if pts.shape[1] != 2 or vals.ndim != 1 or len(pts) != len(vals):
            self.log_message("Warning: kLa surface dimensions mismatch!")
        else:
            self.log_message("kLa surface dimensions are correct (Q,N).")
        self.log_message(f"Example kLa at (Q,N)=(12,250): "
                         f"{self.kla_interp(12, 250):.2f} %/h") 
        self.log_message(f"Example kLa at (Q,N)=(6,500): "
                         f"{self.kla_interp(6, 500):.2f} %/h")

    # ------------------------------------------------------------------ 3 log
    def log_message(self, msg: str) -> None:
        self.log.configure(state=tk.NORMAL)
        self.log.insert(tk.END, msg + "\n")
        self.log.see(tk.END)
        self.log.configure(state=tk.DISABLED)

    # ------------------------------------------------------------------ 4 I/O
    def open_serial(self) -> None:
        try:
            self.serial_connection = serial.Serial(
                port="COM2", baudrate=115200,
                bytesize=serial.EIGHTBITS, stopbits=serial.STOPBITS_ONE,
                parity=serial.PARITY_NONE, timeout=0.1,
            )
            self.running = True
            self.log_message("Serial opened on COM2 (115200-8-N-1).")
            self.open_button.config(state=tk.DISABLED)
            self.close_button.config(state=tk.NORMAL)
            self.send_button.config(state=tk.NORMAL)
            threading.Thread(target=self._reader, daemon=True).start()
        except Exception as exc:                       # pragma: no cover
            self.log_message(f"Open error: {exc}")

    def close_serial(self) -> None:
        self.running = False
        if self.serial_connection:
            self.serial_connection.close()
            self.serial_connection = None
        self.open_button.config(state=tk.NORMAL)
        self.close_button.config(state=tk.DISABLED)
        self.send_button.config(state=tk.DISABLED)
        self.log_message("Serial closed.")

    def send_command(self) -> None:
        if self.serial_connection and (txt := self.command_entry.get()):
            self.serial_connection.write((txt + "\r").encode())
            self.log_message(f"Sent (manual): {txt}")

    # ------------------------------------------------------------------ 5 RX-loop
    def _reader(self) -> None:
        while self.running:
            try:
                # absorb bytes --------------------------------------------------
                if self.serial_connection and self.serial_connection.in_waiting:
                    self.rx_buffer += self.serial_connection.read(
                        self.serial_connection.in_waiting
                    ).decode("utf-8", "replace")

                # decode complete JSON frames ----------------------------------
                start = self.rx_buffer.find("{")
                while start != -1:
                    brace, end = 0, None
                    for i, ch in enumerate(self.rx_buffer[start:], start):
                        brace += (ch == "{") - (ch == "}")
                        if brace == 0:
                            end = i
                            break
                    if end is None:
                        break
                    frame = self.rx_buffer[start:end + 1]
                    self.rx_buffer = self.rx_buffer[end + 1:]
                    self._handle_json(frame)
                    start = self.rx_buffer.find("{")

                # periodic DO output -------------------------------------------
                now = time.monotonic()
                while self.oxygen_monitor_active and now >= self._next_o2_push:
                    self._step_and_send()
                    self._next_o2_push += 1.0           # exactly 1 Hz

                time.sleep(0.01)

            except Exception as exc:                    # pragma: no cover
                self.log_message(f"Serial error: {exc}")
                break

    # ------------------------------------------------------------------ 6 JSON
    def _handle_json(self, text: str) -> None:
        try:
            data = json.loads(text)
            print(f"Received JSON: {data}")             # debug
        except json.JSONDecodeError:
            self.log_message("Malformed JSON ignored.")
            return

        if "flowSetpoint" in data:
            self.Q_target = float(data["flowSetpoint"])
        if "motorSetpoint" in data:
            self.N_target = float(data["motorSetpoint"])

        if "comTest" in data:
            self.serial_connection.write(b"OK\r\n")
            self.log_message("Sent: OK (comTest)")

        if "oxygenMonitor" in data:
            prev_state = self.oxygen_monitor_active
            try:
                self.oxygen_monitor_active = float(data["oxygenMonitor"]) > 0
            except (ValueError, TypeError):
                self.oxygen_monitor_active = False
            # only reset schedule on state change
            if self.oxygen_monitor_active and not prev_state:
                self._next_o2_push = time.monotonic()   # send immediately

        self.log_message(f"Updated set-points: {data}")

    # ------------------------------------------------------------------ 7 model + TX
    def _step_and_send(self) -> None:
        t_now = time.monotonic()
        dt = 1.0 if self.last_step is None else max(t_now - self.last_step, 1e-3)
        self.last_step = t_now

        # simple %-based model (kLa, OUR scaled accordingly) -------------------
        Q = float(np.clip(self.Q_target, 5.0, 15.0))
        N = float(np.clip(self.N_target, 200.0, 800.0))
        kLa_h = float(self.kla_interp(Q, N))
        if not (5.0 <= self.Q_target <= 15.0 or 200.0 <= self.N_target <= 800.0):
            self.log_message(f"Set-point out of bounds: Q={self.Q_target}, N={self.N_target} — clipped.")

        print(f"Q_target: {self.Q_target}, N_target: {self.N_target}")  # debug
        print(f"Calculated kLa: {kLa_h:.2f} %/h")  # debug
        if np.isnan(kLa_h):
            kLa_h = 0.0

        # Debugging each part of the equation
        kla_term = (self.C_star - self.C) * (kLa_h / 3600.0)
        our_term = -(self.OUR_h / 3600.0)
        dC_dt = kla_term + our_term

        # Print debug information
        print(f"kLa term: {kla_term:.4f} %/s")
        print(f"OUR term: {our_term:.4f} %/s")
        print(f"Calculated dC/dt: {dC_dt:.4f} %/s")
        print(f"Current DO: {self.C:.2f} % (C_star={self.C_star})")  # debug
        print(f"dt: {dt:.3f} s")
        self.C = float(np.clip(self.C + dC_dt * dt, 0.0, self.C_star))

        elapsed = round(time.monotonic() - self._t0, 2)
        do_pct = round(self.C, 2)                       # already %
        do_12bit = int(round(4095 * do_pct / 100.0))

        payload = json.dumps({"Time": elapsed, "Oxyval": do_12bit}) + "\r\n"
        self.serial_connection.write(payload.encode())
        print(f"TX: {payload.strip()}")                 # debug
        self.log_message(f"Sent DO: {do_pct:.2f}% ({do_12bit}) at {elapsed:.2f}s")

# ----------------------------------------------------------------------
# 8 Stand-alone test harness
# ----------------------------------------------------------------------
if __name__ == "__main__":
    root = tk.Tk()
    app = SerialApp(root)

    # Example kLa data
    if griddata is not None:
        # CORRECT POINT ORDER: [Q, N]
        pts = np.array([[5, 200],
                        [15, 200],
                        [5, 800],
                        [15, 800],
                        [10, 500]])
        vals = np.array([26.35, 71.85, 100.58, 117.32, 83.12])
        app.configure_kla_surface(pts, vals)

    root.mainloop()
