#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
ESP32 Serial Simulator
~~~~~~~~~~~~~~~~~~~~~~
Simulates the TECNAL ESP32 controller over a virtual COM port.

Expected protocol (from data_parser.py):
  - Handshake IN:  {"comTest":1}   → reply "OK"
  - Command IN:    any JSON dict   → parsed and applied to internal state
  - Data OUT:      JSON with all sensor fields, emitted every ~1 s

JSON keys emitted (matching DataParser expectations):
  Tempval, Oxyval, pHval, Pressure, FlowRate, Distance, Antifoam,
  BiomassAbs, PumpFlow, PumpVol, Time, FlowVoltage,
  raw, it_ms, pwm_pct, hd_mode
"""

import json
import math
import random
import threading
import time
import tkinter as tk
from tkinter import scrolledtext, ttk
import serial


# ---------------------------------------------------------------------------
# Sensor state: realistic, slowly-drifting values
# ---------------------------------------------------------------------------

class SensorState:
    """Holds the current simulated sensor values and evolves them each tick."""

    def __init__(self):
        self.start_time = time.time()

        # Setpoints that can be updated by incoming commands
        self.temp_setpoint    = 37.0
        self.flow_setpoint    = 5.0
        self.motor_rpm        = 300.0
        self.ph_setpoint      = 7.0
        self.data_delay_ms    = 1000

        # Internal "real" values that drift toward setpoints
        self._temp      = 25.0
        self._oxy_raw   = 820.0   # raw ADC ~= 100% DO at 37°C with default cal
        self._ph_raw    = 7.0
        self._pressure  = 760.0
        self._flow      = 0.0
        self._distance  = 45.0
        self._pump_vol  = 0.0

    # ------------------------------------------------------------------
    def tick(self) -> dict:
        """Advance simulation one step and return the JSON payload dict."""
        t = time.time() - self.start_time   # elapsed seconds

        # Temperature: first-order lag toward setpoint + small noise
        self._temp += 0.05 * (self.temp_setpoint - self._temp) + random.gauss(0, 0.02)

        # Oxygen: slow sinusoidal drift around a mid-value + noise
        oxy_target = 1820.0 + 500 * math.sin(t / 60.0)
        self._oxy_raw += 0.03 * (oxy_target - self._oxy_raw) + random.gauss(0, 0.5)
        self._oxy_raw = max(200.0, min(3000.0, self._oxy_raw))

        # pH raw: drifts gently, matches ph_setpoint over long timescale
        self._ph_raw += 0.01 * (self.ph_setpoint - self._ph_raw) + random.gauss(0, 0.005)
        self._ph_raw = max(0.0, min(14.0, self._ph_raw))

        # Pressure: small random walk around 760 mmHg
        self._pressure += random.gauss(0, 0.1)
        self._pressure = max(740.0, min(780.0, self._pressure))

        # Flow rate: tracks setpoint with lag
        self._flow += 0.1 * (self.flow_setpoint - self._flow) + random.gauss(0, 0.05)
        self._flow = max(0.0, self._flow)

        # Distance: slow oscillation (simulates foam sensor)
        self._distance = 45.0 + 5.0 * math.sin(t / 60.0) + random.gauss(0, 0.1)

        # Pump volume: accumulates slowly when flow > 0
        self._pump_vol += self._flow / 60.0 * (self.data_delay_ms / 1000.0)

        # Biomass: slow exponential growth proxy
        biomass_abs = 0.05 + 0.001 * t / 60.0 + random.gauss(0, 0.001)
        biomass_abs = max(0.0, biomass_abs)

        flow_voltage = 0.5 + self._flow * 0.08 + random.gauss(0, 0.002)

        return {
            "Tempval":    round(self._temp, 3),
            "Oxyval":     round(self._oxy_raw, 2),
            "pHval":      round(self._ph_raw, 4),
            "Pressure":   round(self._pressure, 2),
            "FlowRate":   round(self._flow, 3),
            "Distance":   round(self._distance, 2),
            "Antifoam":   0.0,
            "BiomassAbs": round(biomass_abs, 4),
            "PumpFlow":   round(self._flow * 1000.0 / 60.0, 3),   # mL/min
            "PumpVol":    round(self._pump_vol, 3),
            "Time":       round(t, 2),
            "FlowVoltage":round(flow_voltage, 4),
            # Biomass detail fields
            "raw":        int(biomass_abs * 65535),
            "it_ms":      50,
            "pwm_pct":    50.0,
            "hd_mode":    False,
        }

    def apply_command(self, cmd: dict):
        """Update setpoints from a parsed command dict."""
        if "Temp" in cmd or "tempSetpoint" in cmd:
            self.temp_setpoint = float(cmd.get("Temp", cmd.get("tempSetpoint", self.temp_setpoint)))
        if "flowSetpoint" in cmd:
            self.flow_setpoint = float(cmd["flowSetpoint"])
        if "motorSetpoint" in cmd or "Motor" in cmd:
            self.motor_rpm = float(cmd.get("motorSetpoint", cmd.get("Motor", self.motor_rpm)))
        if "pHSetpoint" in cmd:
            self.ph_setpoint = float(cmd["pHSetpoint"])
        if "dataDelay" in cmd:
            self.data_delay_ms = max(200, int(cmd["dataDelay"]))


# ---------------------------------------------------------------------------
# Main application
# ---------------------------------------------------------------------------

class ESP32SimulatorApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("ESP32 TECNAL Simulator")
        self.root.resizable(True, True)

        self.serial_conn: serial.Serial | None = None
        self.running = False
        self.sensor = SensorState()
        self._lock = threading.Lock()

        self._build_ui()

    # ------------------------------------------------------------------
    # UI
    # ------------------------------------------------------------------

    def _build_ui(self):
        # ── Top bar: port config + connect ──────────────────────────────
        top = tk.Frame(self.root, pady=6, padx=8)
        top.pack(fill=tk.X)

        tk.Label(top, text="COM Port:").pack(side=tk.LEFT)
        self.port_var = tk.StringVar(value="COM2")
        tk.Entry(top, textvariable=self.port_var, width=8).pack(side=tk.LEFT, padx=(2, 8))

        tk.Label(top, text="Baud:").pack(side=tk.LEFT)
        self.baud_var = tk.StringVar(value="115200")
        baud_cb = ttk.Combobox(top, textvariable=self.baud_var, width=8,
                               values=["9600", "115200", "230400"])
        baud_cb.pack(side=tk.LEFT, padx=(2, 8))

        self.open_btn  = tk.Button(top, text="Open",  width=8, command=self.open_serial,
                                   bg="#4CAF50", fg="white")
        self.close_btn = tk.Button(top, text="Close", width=8, command=self.close_serial,
                                   bg="#f44336", fg="white", state=tk.DISABLED)
        self.open_btn.pack(side=tk.LEFT, padx=2)
        self.close_btn.pack(side=tk.LEFT, padx=2)

        self.status_lbl = tk.Label(top, text="● Disconnected", fg="gray")
        self.status_lbl.pack(side=tk.LEFT, padx=12)

        # ── Sensor override sliders ──────────────────────────────────────
        sliders_frame = tk.LabelFrame(self.root, text="Sensor Overrides", padx=6, pady=4)
        sliders_frame.pack(fill=tk.X, padx=8, pady=2)

        self._sliders: dict[str, tk.DoubleVar] = {}
        slider_defs = [
            ("Temp Setpoint (°C)", "temp_setpoint",     20.0, 60.0,  37.0),
            ("Flow Setpoint (L/min)", "flow_setpoint",   0.0, 30.0,   5.0),
            ("pH Setpoint",         "ph_setpoint",       2.0, 12.0,   7.0),
        ]
        for label, attr, lo, hi, default in slider_defs:
            row = tk.Frame(sliders_frame)
            row.pack(fill=tk.X, pady=1)
            tk.Label(row, text=label, width=24, anchor="w").pack(side=tk.LEFT)
            var = tk.DoubleVar(value=default)
            self._sliders[attr] = var
            tk.Scale(row, variable=var, from_=lo, to=hi, resolution=0.1,
                     orient=tk.HORIZONTAL, length=300,
                     command=lambda v, a=attr, dv=var: self._on_slider(a, dv)
                     ).pack(side=tk.LEFT)
            tk.Label(row, textvariable=var, width=6).pack(side=tk.LEFT)

        # ── Live sensor readout ──────────────────────────────────────────
        readout_frame = tk.LabelFrame(self.root, text="Last Emitted Values", padx=6, pady=4)
        readout_frame.pack(fill=tk.X, padx=8, pady=2)

        self._readout_vars: dict[str, tk.StringVar] = {}
        fields = ["Tempval", "Oxyval", "pHval", "Pressure", "FlowRate",
                  "Distance", "BiomassAbs", "PumpVol", "Time"]
        cols = 3
        for i, key in enumerate(fields):
            r, c = divmod(i, cols)
            tk.Label(readout_frame, text=f"{key}:", anchor="e", width=12
                     ).grid(row=r, column=c*2, sticky="e", padx=(4, 0))
            var = tk.StringVar(value="—")
            self._readout_vars[key] = var
            tk.Label(readout_frame, textvariable=var, anchor="w", width=10
                     ).grid(row=r, column=c*2+1, sticky="w")

        # ── Log area ────────────────────────────────────────────────────
        log_frame = tk.LabelFrame(self.root, text="Log", padx=4, pady=4)
        log_frame.pack(fill=tk.BOTH, expand=True, padx=8, pady=4)

        self.log_text = scrolledtext.ScrolledText(log_frame, height=14, state=tk.DISABLED,
                                                  font=("Consolas", 9))
        self.log_text.pack(fill=tk.BOTH, expand=True)

        btn_row = tk.Frame(log_frame)
        btn_row.pack(fill=tk.X, pady=2)
        tk.Button(btn_row, text="Clear Log", command=self._clear_log).pack(side=tk.RIGHT)

        # ── Manual send ─────────────────────────────────────────────────
        send_frame = tk.Frame(self.root, padx=8, pady=4)
        send_frame.pack(fill=tk.X)

        tk.Label(send_frame, text="Manual send:").pack(side=tk.LEFT)
        self.cmd_var = tk.StringVar()
        tk.Entry(send_frame, textvariable=self.cmd_var, width=50).pack(side=tk.LEFT, padx=4)
        self.send_btn = tk.Button(send_frame, text="Send", command=self._manual_send,
                                  state=tk.DISABLED)
        self.send_btn.pack(side=tk.LEFT)

    # ------------------------------------------------------------------
    # Slider callback
    # ------------------------------------------------------------------

    def _on_slider(self, attr: str, var: tk.DoubleVar):
        setattr(self.sensor, attr, var.get())

    # ------------------------------------------------------------------
    # Serial open / close
    # ------------------------------------------------------------------

    def open_serial(self):
        port = self.port_var.get().strip()
        baud = int(self.baud_var.get())
        try:
            self.serial_conn = serial.Serial(port, baud, timeout=1)
            self.running = True
            self._set_status(True)
            self._log(f"Opened {port} @ {baud} baud.")
            threading.Thread(target=self._rx_loop,   daemon=True).start()
            threading.Thread(target=self._tx_loop,   daemon=True).start()
        except Exception as exc:
            self._log(f"ERROR opening {port}: {exc}")

    def close_serial(self):
        self.running = False
        with self._lock:
            if self.serial_conn:
                try:
                    self.serial_conn.close()
                except Exception:
                    pass
                self.serial_conn = None
        self._set_status(False)
        self._log("Serial closed.")

    # ------------------------------------------------------------------
    # Worker threads
    # ------------------------------------------------------------------

    def _rx_loop(self):
        """Read commands from the main app and respond to handshake."""
        while self.running:
            try:
                with self._lock:
                    conn = self.serial_conn
                if conn is None:
                    break
                if conn.in_waiting:
                    raw = conn.readline()
                    line = raw.decode("utf-8", errors="replace").strip()
                    if not line:
                        time.sleep(0.02)
                        continue
                    self._log(f"← RX: {line}")
                    self._handle_rx(line, conn)
                else:
                    time.sleep(0.02)
            except Exception as exc:
                self._log(f"RX error: {exc}")
                break

    def _handle_rx(self, line: str, conn: serial.Serial):
        """Parse incoming JSON and reply if necessary."""
        try:
            cmd = json.loads(line)
        except json.JSONDecodeError:
            self._log(f"  (non-JSON, ignored)")
            return

        # Handshake
        if cmd.get("comTest") == 1:
            self._send_raw("OK", conn)
            self._log(f"  → Handshake OK")
            return

        # Apply any setpoint updates
        self.sensor.apply_command(cmd)
        self._log(f"  Applied command: {cmd}")

    def _tx_loop(self):
        """Periodically emit JSON sensor data."""
        while self.running:
            interval = self.sensor.data_delay_ms / 1000.0
            time.sleep(interval)
            if not self.running:
                break
            with self._lock:
                conn = self.serial_conn
            if conn is None:
                break
            try:
                payload = self.sensor.tick()
                self._update_readout(payload)
                data = json.dumps(payload, separators=(",", ":"))
                self._send_raw(data, conn)
                # Only log abbreviated version to avoid flooding
                brief = {k: payload[k] for k in
                         ("Tempval", "Oxyval", "pHval", "FlowRate", "Time")}
                self._log(f"→ TX: {brief} …")
            except Exception as exc:
                self._log(f"TX error: {exc}")
                break

    def _send_raw(self, text: str, conn: serial.Serial):
        conn.write((text + "\n").encode("utf-8"))
        conn.flush()

    # ------------------------------------------------------------------
    # Manual send
    # ------------------------------------------------------------------

    def _manual_send(self):
        text = self.cmd_var.get().strip()
        if not text:
            return
        with self._lock:
            conn = self.serial_conn
        if conn:
            try:
                self._send_raw(text, conn)
                self._log(f"→ Manual TX: {text}")
            except Exception as exc:
                self._log(f"Manual send error: {exc}")

    # ------------------------------------------------------------------
    # UI helpers (thread-safe via .after)
    # ------------------------------------------------------------------

    def _log(self, msg: str):
        ts = time.strftime("%H:%M:%S")
        line = f"[{ts}] {msg}"
        self.root.after(0, self._append_log, line)

    def _append_log(self, line: str):
        self.log_text.configure(state=tk.NORMAL)
        self.log_text.insert(tk.END, line + "\n")
        self.log_text.see(tk.END)
        self.log_text.configure(state=tk.DISABLED)

    def _clear_log(self):
        self.log_text.configure(state=tk.NORMAL)
        self.log_text.delete("1.0", tk.END)
        self.log_text.configure(state=tk.DISABLED)

    def _set_status(self, connected: bool):
        self.root.after(0, self._apply_status, connected)

    def _apply_status(self, connected: bool):
        if connected:
            self.status_lbl.config(text="● Connected", fg="#4CAF50")
            self.open_btn.config(state=tk.DISABLED)
            self.close_btn.config(state=tk.NORMAL)
            self.send_btn.config(state=tk.NORMAL)
        else:
            self.status_lbl.config(text="● Disconnected", fg="gray")
            self.open_btn.config(state=tk.NORMAL)
            self.close_btn.config(state=tk.DISABLED)
            self.send_btn.config(state=tk.DISABLED)

    def _update_readout(self, payload: dict):
        def _do():
            for key, var in self._readout_vars.items():
                val = payload.get(key, "—")
                var.set(f"{val:.3f}" if isinstance(val, float) else str(val))
        self.root.after(0, _do)


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    root = tk.Tk()
    root.geometry("680x660")
    app = ESP32SimulatorApp(root)
    root.mainloop()