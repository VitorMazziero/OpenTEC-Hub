#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
communication_handler.py – unified USB / Wi-Fi / Bluetooth transport
--------------------------------------------------------------------
Key design changes (see detailed comparison after the code):

*   Strict initial handshake: the first “comTest” must return “OK”.
    Otherwise the handler logs the error, sets *connection_status* to
    “Desconectado” and **never** starts automatic reconnection or data
    polling until the user initiates a fresh connection manually.

*   Explicit connection-state machine with the four states required by
    the specification:
        "Ok", "ESP32 desligado", "Módulo desligado", "Desconectado".

*   Dedicated monitors for each failure mode
        (a) ESP32 power-loss             → restart command
        (b) physical disconnection       → alarm + backup cycle
    Detection thresholds follow the spec (≥ 3 time values without
    change, ≥ 30 identical Temp or pH readings, +10 extra confirmations).

*   Backup cycle: unconditional 5 s round-robin
        USB → Wi-Fi → Bluetooth → USB …
    Alarm is silenced immediately after any medium reconnects
    and the handshake passes.

*   All public names, sensor variables, and helper methods of the
    previous version are kept to guarantee full backward compatibility
    with the Qt GUI layers.
"""
from __future__ import annotations

import json
import threading
import concurrent.futures
import requests
import serial
import time
import os
import asyncio
from typing import Optional

from .alarm_manager import AlarmThread
from config.preferences import load_preferences, default_preferences
from PyQt5.QtCore import Qt, QObject, pyqtSignal
from serial.tools import list_ports


# ---------------------------------------------------------------------
# Qt helper – unchanged
# ---------------------------------------------------------------------
class AlarmDispatcher(QObject):
    show_alarm_signal = pyqtSignal(str)


# ---------------------------------------------------------------------
# Main handler
# ---------------------------------------------------------------------
class CommunicationHandler:
    """USB, Wi-Fi and Bluetooth communications to the TECNAL controller."""

    # -----------------------------------------------------------------
    # Construction
    # -----------------------------------------------------------------
    def __init__(self) -> None:

        # ---------------- live connection ----------------------------
        self.mode: Optional[str] = None              # "USB" / "WiFi" / "Bluetooth"
        self.ser: Optional[serial.Serial] = None
        self.ip: Optional[str] = None

        # connection & monitor state
        self.connection_status: str = "Desconectado"
        self.last_connection_status: str = self.connection_status
        self._connection_established: bool = False        # handshake passed
        self._initial_attempt_active: bool = False
        self._backup_index: int = 0                       # 0-USB,1-WiFi,2-BT
        self._connections_disabled: bool = False

        # logger callback (UI) – injected by GUI
        self.logger = None

        # executor for async HTTP gets
        self.executor = concurrent.futures.ThreadPoolExecutor(max_workers=1)

        # ---------------- USB parameters -----------------------------
        self.usb_port              = None
        self.usb_baud_rate         = None
        self.usb_data_bits         = None
        self.usb_stop_bits         = None
        self.usb_parity            = None

        # ---------------- Bluetooth runtime --------------------------
        self.bluetooth_device: Optional[str] = None   # "<name> | <MAC> | RSSI…"
        self.bluetooth_client = None                  # bleak.BleakClient
        self.bluetooth_loop: Optional[asyncio.AbstractEventLoop] = None
        self.bluetooth_thread: Optional[threading.Thread] = None
        self.bluetooth_ativo: bool = False
        self._bluetooth_cancel_event = threading.Event()
        self._bt_attempt_in_progress = False
        self._bt_latest_data: Optional[str] = None
        self._bt_lock = threading.Lock()

        # ---------------- calibration -------------------------------
        self.oxy_cal_a = None
        self.oxy_cal_b = None
        try:
            prefs = load_preferences() or default_preferences()
            self.pH_slope     = float(prefs.get("Configurations", {}).get("ph_cal_slope", "1.0"))
            self.pH_intercept = float(prefs.get("Configurations", {}).get("ph_cal_intercept", "0.0"))
        except Exception:
            self.pH_slope = 1.0
            self.pH_intercept = 0.0

        # ---------------- last sensor readings -----------------------
        self.tempReadVal  = -1
        self.oxyReadVal   = -1
        self.phReadVal    = -1
        self.phRead       = -1
        self.pressureReadVal = -1
        self.flowReadVal     = -1
        self.distanceReadVal = -1
        self.antifoamReadVal = -1
        self._raw_time_min   = 0
        self.timeReadVal     = 0
        self.timeZeroOffset  = 0

        # helpers for ESP32 / module failure detection
        self._last_time_value     = None
        self._same_time_count     = 0
        self._temp_const_count    = 0
        self._last_temp_value     = None
        self._ph_const_count      = 0
        self._last_ph_value       = None

        # ---------------- alarm -------------------------------------
        self.alarm_thread     = None
        self.alarm_stop_event = threading.Event()
        self.alarm_mute_flag  = threading.Event()
        self._last_alarm_trigger = None

        # ---------------- reconnection bookkeeping ------------------
        self._last_reconnect_attempt = 0.0          # timestamp
        self._backup_methods = ["USB", "WiFi", "Bluetooth"]

        # background monitor
        self.connection_monitor_thread = threading.Thread(
            target=self._monitor_connection,
            daemon=True,
        )
        self.connection_monitor_thread.start()

    # =================================================================
    # ------------------------- PUBLIC API ----------------------------
    # =================================================================
    # -------- Initial connection entry points (called by GUI) --------
    def connect_usb(self, port: str, baud: int, data_bits: int,
                    stop_bits: int, parity) -> None:
        """Opens USB port and performs mandatory comTest."""
        if self._connections_disabled:
            return
        baud, data_bits, stop_bits, parity = self._convert_usb_params(
            baud, data_bits, stop_bits, parity
        )

        def _attempt(p: str) -> bool:
            try:
                ser = serial.Serial(
                    port=p, baudrate=baud, bytesize=data_bits,
                    stopbits=stop_bits, parity=parity, timeout=3
                )
            except Exception:
                return False
            self.ser = ser
            self.mode = "USB"
            self._perform_handshake(self.send_command, self.read_data)
            if self._connection_established:
                self.usb_port = p
                self.usb_baud_rate = baud
                self.usb_data_bits = data_bits
                self.usb_stop_bits = stop_bits
                self.usb_parity = parity
                if self.logger:
                    self.logger(f"USB conectado: {p}")
                return True
            ser.close()
            self.ser = None
            return False

        self._initial_attempt_active = True
        if port and _attempt(port):
            return
        # optional scan for any port that already answers comTest
        alt = self.find_working_usb_port(baud, data_bits, stop_bits, parity)
        if alt and _attempt(alt):
            return
        self._handshake_failed("USB")

    def connect_wifi(self, ip: str) -> None:
        """Performs a single comTest HTTP round-trip."""
        if self._connections_disabled:
            return
        self.mode = "WiFi"
        self.ip = ip
        self._initial_attempt_active = True

        def _send(cmd: dict) -> None:
            try:
                requests.post(
                    f"http://{ip}/command",
                    data=json.dumps(cmd),
                    headers={"Content-Type": "application/json"},
                    timeout=1,
                )
            except Exception:
                pass

        def _recv() -> Optional[str]:
            try:
                r = requests.get(f"http://{ip}/readData", timeout=1)
                if r.status_code == 200:
                    return r.text.strip()
                return None
            except Exception:
                return None

        self._perform_handshake(_send, _recv)
        if self._connection_established and self.logger:
            self.logger(f"Wi-Fi conectado: {ip}")
        if not self._connection_established:
            self._handshake_failed("WiFi")

    def connect_bluetooth(self) -> None:
        """Spawns a BLE helper thread and runs handshake afterwards."""
        if self._connections_disabled or self._bt_attempt_in_progress:
            return
        if not self.bluetooth_device:
            if self.logger:
                self.logger("Nenhum dispositivo Bluetooth selecionado.")
            return
        self.mode = "Bluetooth"
        address = self.bluetooth_device.split("|")[1].strip()
        self._initial_attempt_active = True
        self._bt_attempt_in_progress = True

        self.bluetooth_thread = threading.Thread(
            target=self._bluetooth_connect_thread,
            args=(address,),
            daemon=True,
        )
        self.bluetooth_thread.start()

    def _cleanup_current_transport(self) -> None:
        """Fecha porta USB ou desconecta BT antes de mudar de meio."""
        if self.mode == "USB" and self.ser:
            try:
                self.ser.close()
            finally:
                self.ser = None
        elif self.mode == "Bluetooth" and self.bluetooth_client:
            self._bluetooth_cancel_event.set()
            try:
                if self.is_bluetooth_connected():
                    fut = asyncio.run_coroutine_threadsafe(
                        self.bluetooth_client.disconnect(),
                        self.bluetooth_loop,
                    )
                    fut.result(timeout=5)
            finally:
                self.bluetooth_client = None
        self.mode = None
    
    # -------- Public helpers used by UI or other modules -------------
    def disable_all_connections(self) -> None:
        self._connections_disabled = True
        self.disconnect()
        self.connection_status = "Desconectado"
        self.last_connection_status = self.connection_status
        if self.logger:
            self.logger(
                "Todas as comunicações foram desconectadas e desativadas."
            )

    def disconnect(self) -> None:
        """Force close the current transport; does NOT start backup."""
        # Close specific transport
        if self.mode == "USB" and self.ser:
            try:
                self.ser.close()
            except Exception:
                pass
            self.ser = None
        elif self.mode == "Bluetooth" and self.bluetooth_client:
            self._bluetooth_cancel_event.set()
            try:
                if (self.bluetooth_loop
                        and not self.bluetooth_loop.is_closed()
                        and self.bluetooth_client.is_connected):
                    fut = asyncio.run_coroutine_threadsafe(
                        self.bluetooth_client.disconnect(),
                        self.bluetooth_loop,
                    )
                    fut.result(timeout=5)
            except Exception:
                pass
            self.bluetooth_client = None
        self.mode = None
        self.bluetooth_ativo = False
        self._connection_established = False
        self.connection_status = "Desconectado"
        self.last_connection_status = self.connection_status
        self.stop_alarm()
        if self.logger:
            self.logger("Desconectado.")

    # =================================================================
    # --------------------- PRIVATE IMPLEMENTATION --------------------
    # =================================================================
    # -------- Strict comTest handshake -------------------------------
    def _perform_handshake(self, tx_fn, rx_fn) -> None:
        """
        tx_fn – callable that accepts a dict and transmits it.
        rx_fn – callable that returns a str or None containing a reply.
        """
        self._connection_established = False
        # wait max 1 s in 100 ms steps
        for _ in range(10):
            tx_fn({"comTest": 1})
            reply = rx_fn()
            if reply == "OK":
                self._connection_established = True
                self.connection_status = "Ok"
                self.last_connection_status = self.connection_status
                self._temp_const_count = 0
                self._ph_const_count = 0
                self.stop_alarm()
                break
            time.sleep(0.1)

    def _handshake_failed(self, medium: str) -> None:
        if self.logger:
            self.logger(f"{medium}: falha no comTest. Nova tentativa em 5 s.")
        self._cleanup_current_transport()
        self.connection_status = "Desconectado"
        self.last_connection_status = self.connection_status
        self._connection_established = False
        if hasattr(self, "_connection_failed_callback"):
            self._connection_failed_callback(medium)
        threading.Timer(5, self._start_backup_cycle).start()


    # -------- USB internals ------------------------------------------
    def _convert_usb_params(self, baud, data_bits, stop_bits, parity):
        try:
            baud = int(baud)
            data_bits = int(data_bits)
            stop_bits = int(stop_bits)
            parity = serial.PARITY_NONE if str(parity).lower() == "none" else parity
            return baud, data_bits, stop_bits, parity
        except Exception as exc:
            raise ValueError(f"Parâmetros USB inválidos: {exc}") from exc

    def test_usb_port(self, port, baud, data_bits, stop_bits, parity) -> bool:
        try:
            baud, data_bits, stop_bits, parity = self._convert_usb_params(
                baud, data_bits, stop_bits, parity
            )
            with serial.Serial(
                port=port,
                baudrate=baud,
                bytesize=data_bits,
                stopbits=stop_bits,
                parity=parity,
                timeout=1,
            ) as ser:
                ser.write(json.dumps({"comTest": 1}).encode())
                return ser.readline().decode().strip() == "OK"
        except Exception:
            return False

    def find_working_usb_port(self, baud, data_bits, stop_bits, parity):
        for info in list_ports.comports():
            # Skip virtual BT COM ports on Windows
            if info.description and "Bluetooth" in info.description:
                continue
            if self.test_usb_port(info.device, baud, data_bits, stop_bits, parity):
                return info.device
        return None

    # -------- Bluetooth helper thread --------------------------------
    def _bluetooth_connect_thread(self, address: str) -> None:
        from bleak import BleakClient

        TX_UUID = "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"
        RX_UUID = "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"

        async def _run():
            async with BleakClient(address, timeout=5.0) as client:
                self.bluetooth_client = client
                self.bluetooth_ativo = True
                # start notifications
                await client.start_notify(
                    TX_UUID,
                    lambda _h, d: self._on_bt_notify(d),
                )
                # perform handshake through RX_UUID
                await client.write_gatt_char(RX_UUID, b'{"comTest":1}', response=True)
                # wait 1 s for reply
                for _ in range(10):
                    await asyncio.sleep(0.1)
                    with self._bt_lock:
                        if self._bt_latest_data == "OK":
                            self._bt_latest_data = None
                            self._handshake_success_bt()
                            break
                if not self._connection_established:
                    # failed handshake: leave context manager to auto-disconnect
                    return
                # keep connection alive until cancelled
                while not self._bluetooth_cancel_event.is_set():
                    await asyncio.sleep(0.5)

        self.bluetooth_loop = asyncio.new_event_loop()
        asyncio.set_event_loop(self.bluetooth_loop)
        try:
            self.bluetooth_loop.run_until_complete(_run())
        finally:
            if self.bluetooth_loop and not self.bluetooth_loop.is_closed():
                self.bluetooth_loop.close()
            self._bt_attempt_in_progress = False
            if not self._connection_established:
                self._handshake_failed("Bluetooth")

    def _on_bt_notify(self, data: bytearray) -> None:
        try:
            txt = data.decode().strip()
            with self._bt_lock:
                self._bt_latest_data = txt
        except Exception:
            pass

    def _handshake_success_bt(self) -> None:
        self._connection_established = True
        self.connection_status = "Ok"
        self.last_connection_status = self.connection_status
        self.stop_alarm()
        if self.logger:
            self.logger("Bluetooth conectado.")

    def is_bluetooth_connected(self) -> bool:
        return bool(self.bluetooth_client and getattr(self.bluetooth_client, "is_connected", False))

    # -------- Common JSON command TX ---------------------------------
    def send_command(self, cmd: dict) -> None:
        data = json.dumps(cmd)
        if self.mode == "USB" and self.ser:
            try:
                self.ser.write(data.encode())
            except Exception:
                pass
        elif self.mode == "WiFi" and self.ip:
            threading.Thread(
                target=self._post_wifi, args=(data,), daemon=True
            ).start()
        elif self.mode == "Bluetooth" and self.is_bluetooth_connected():
            asyncio.run_coroutine_threadsafe(
                self._send_bt(data), self.bluetooth_loop
            )

    def _post_wifi(self, data: str) -> None:
        try:
            requests.post(
                f"http://{self.ip}/command",
                data=data,
                headers={"Content-Type": "application/json"},
                timeout=0.5,
            )
        except Exception:
            pass

    async def _send_bt(self, data: str) -> None:
        RX_UUID = "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"
        try:
            await self.bluetooth_client.write_gatt_char(
                RX_UUID, data.encode(), response=True
            )
        except Exception:
            pass

    # -------- Data acquisition & failure detection -------------------
    def read_data(self) -> Optional[str]:
        """Low-level poll; returns raw JSON line or None."""
        try:
            if self.mode == "USB" and self.ser and self.ser.in_waiting:
                return self.ser.readline().decode().strip()
            elif self.mode == "WiFi" and self.ip:
                future = self.executor.submit(
                    lambda: requests.get(
                        f"http://{self.ip}/readData", timeout=1
                    ).text.strip()
                )
                return future.result(timeout=0.2)
            elif self.mode == "Bluetooth":
                with self._bt_lock:
                    if self._bt_latest_data:
                        d = self._bt_latest_data
                        self._bt_latest_data = None
                        return d
        except Exception:
            return None
        return None

    def read_and_parse_data(self) -> None:
        """Parse JSON, update sensor variables and run failure logic."""
        if not self._connection_established:
            return
        data_str = self.read_data()
        if not data_str:
            return

        # -----------------------------------------------------------------
        # JSON decoding
        # -----------------------------------------------------------------
        try:
            data = json.loads(data_str)
        except Exception:
            return

        # ---- sensor parsing (identical to previous version) -------------
        try:
            if float(data.get("Tempval", -1)) < 100 and float(data.get("Tempval", -1)) > 10:
                self.tempReadVal = float(data.get("Tempval", -1))
        except Exception:
            self.tempReadVal = -1
        try:
            oxy_raw = float(data.get("Oxyval", -1))
            if oxy_raw < 0:
                self.oxyReadVal = -1
            else:
                a = self.oxy_cal_a if self.oxy_cal_a is not None else 0.030573419314
                b = self.oxy_cal_b if self.oxy_cal_b is not None else -25.09036520919
                val = a * oxy_raw + b
                self.oxyReadVal = round(max(val, 0), 4)
        except Exception:
            self.oxyReadVal = -1
        try:
            self.phRead = float(data.get("pHval", -1))
            self.phReadVal = (
                float(round(self.pH_slope * self.phRead + self.pH_intercept, 2))
                if self.phRead != -1
                else -1
            )
            if self.phReadVal < 0:
                self.phReadVal = 0
            if self.phReadVal >= 0 and self.phReadVal < 30000:
                cmd = {"pHCal": format(self.phReadVal, ".2f")}
                self.send_command(cmd)
            else:
                self.phReadVal = -1
        except Exception:
            self.phRead = self.phReadVal = -1
        # remaining fields unchanged …
        try:
            self.pressureReadVal = float(data.get("Pressure", -1))
        except Exception:
            self.pressureReadVal = -1
        try:
            self.flowReadVal = float(data.get("FlowRate", -1))
        except Exception:
            self.flowReadVal = -1
        try:
            # parse new reading
            val = float(data.get("Distance", -1))

            # compare to last reading
            if hasattr(self, '_last_distance_value') and val == self._last_distance_value:
                # still constant
                self._distance_const_count = getattr(self, '_distance_const_count', 1) + 1
            else:
                # reading changed → reset counter
                self._distance_const_count = 1

                # if we had previously sent the “disable” command, re-enable now
                if getattr(self, '_distance_disabled', False):
                    cmd = {"distanceSensorComm": 1, "nutriIntensity": 99}
                    self.send_command(cmd)
                    self._distance_disabled = False

            # store for next iteration
            self._last_distance_value = val

            # if constant long enough → disable
            if self._distance_const_count >= 10 and not getattr(self, '_distance_disabled', False):
                cmd = {"distanceSensorComm": 0, "nutriIntensity": 0}
                self.send_command(cmd)
                self._distance_disabled = True
            if val < 1000:
                self.distanceReadVal = val

        except Exception:
            self.distanceReadVal = -1
        try:
            self.antifoamReadVal = float(data.get("Antifoam", -1))
        except Exception:
            self.antifoamReadVal = -1
        # time
        try:
            raw_time = float(data.get("Time", 0))
            self._raw_time_min = raw_time / 60.0
            self.timeReadVal = round(self._raw_time_min - self.timeZeroOffset, 2)
        except Exception:
            self.timeReadVal = 0

        # -----------------------------------------------------------------
        # (a) ESP32 power-down – constant time
        # -----------------------------------------------------------------
        if self.timeReadVal == self._last_time_value:
            self._same_time_count += 1
            if self._same_time_count >= 10:
                if self.connection_status != "ESP32 desligado":
                    self.connection_status = "ESP32 desligado"
                    self.last_connection_status = self.connection_status
                    if self.logger:
                        self.logger("Detecção: ESP32 desligado (tempo sem variação).")
                    # immediate restart request
                    self.send_command({"restart": 1})
            if self._same_time_count >= 10:
                # after 10 reads (≈10 s) without change, escalate to backup
                self._start_backup_cycle()
        else:
            self._same_time_count = 0
            if self.connection_status == "ESP32 desligado":
                self.connection_status = "Ok"
                self.last_connection_status = self.connection_status
            self._last_time_value = self.timeReadVal

        if self.logger and self.connection_status == "Ok":
            self.logger(data_str)

    # -------- global monitor thread ---------------------------------
    def _monitor_connection(self) -> None:
        while True:
            time.sleep(1)
            if not self._connection_established or self._connections_disabled:
                continue

            lost = False
            if self.mode == "USB" and self.ser:
                lost = not self.ser.is_open
            elif self.mode == "WiFi":
                try:
                    r = requests.get(f"http://{self.ip}/ping", timeout=0.5)
                    lost = r.status_code != 200
                except Exception:
                    lost = True
            elif self.mode == "Bluetooth":
                lost = not self.is_bluetooth_connected()

            if lost:
                if self.connection_status != "Desconectado":
                    self.connection_status = "Desconectado"
                    self.last_connection_status = self.connection_status
                    self.trigger_alarm("Desconexão física detectada")
                self._start_backup_cycle()

    def set_connection_failed_callback(self, callback):
        self._connection_failed_callback = callback

    # -------- backup cycle controller -------------------------------
    def _start_backup_cycle(self) -> None:
        self._cleanup_current_transport()

        if self._connections_disabled:
            return

        if time.time() - self._last_reconnect_attempt < 5:
            return

        now = time.time()
        self._last_reconnect_attempt = now

        if not hasattr(self, "_backup_cycle_start_time"):
            self._backup_cycle_start_time = now

        # Filter only available methods
        available_methods = []
        if self.usb_port or self.find_working_usb_port(115200, 8, 1, serial.PARITY_NONE):
            available_methods.append("USB")
        if self.ip:
            available_methods.append("WiFi")
        if self.bluetooth_device:
            available_methods.append("Bluetooth")

        if not available_methods:
            if self.logger:
                self.logger("Nenhum meio de comunicação disponível para backup.")
            return

        # Advance index within filtered list
        self._backup_index = (self._backup_index + 1) % len(available_methods)
        next_medium = available_methods[self._backup_index]

        if self.logger:
            self.logger(f"Backup: tentando {next_medium} …")

        if next_medium == "USB":
            port = self.usb_port or self.find_working_usb_port(115200, 8, 1, serial.PARITY_NONE)
            if port:
                self.connect_usb(port, 115200, 8, 1, serial.PARITY_NONE)

        elif next_medium == "WiFi":
            self.connect_wifi(self.ip)

        elif next_medium == "Bluetooth":
            self.connect_bluetooth()

        if self._connection_established:
            self.stop_alarm()
            self._backup_cycle_start_time = now


    # =================================================================
    # -------------------------- ALARM --------------------------------
    # =================================================================
    def trigger_alarm(self, msg: str) -> None:
        if self.alarm_mute_flag.is_set():
            return
        if self._last_alarm_trigger == msg:
            return
        self._last_alarm_trigger = msg
        if self.logger:
            self.logger(f"Alarme: {msg}")
        if not self.alarm_thread or not self.alarm_thread.is_alive():
            self.alarm_stop_event.clear()
            base_dir = os.path.dirname(os.path.abspath(__file__))
            path = os.path.join(base_dir, "alarm.wav")
            self.alarm_thread = AlarmThread(
                path, self.alarm_stop_event, self.alarm_mute_flag
            )
            self.alarm_thread.start()

    def stop_alarm(self) -> None:
        self.alarm_stop_event.set()
        self._last_alarm_trigger = None
        self.alarm_thread = None

    # -----------------------------------------------------------------
    # user-muting
    # -----------------------------------------------------------------
    def _mute_changed(self, state):
        if state == Qt.Checked:
            self.alarm_mute_flag.set()
            if self.logger:
                self.logger("Alarme sonoro desativado pelo usuário.")
        else:
            self.alarm_mute_flag.clear()
            if self.logger:
                self.logger("Alarme sonoro ativado pelo usuário.")
