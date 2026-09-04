#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
communication_handler.py – unified USB / Wi-Fi transport with robust, chatty retry/backup loop

States used:
    "Ok", "ESP32 desligado", "Módulo desligado", "Desconectado"
"""
from __future__ import annotations

import json
import threading
import concurrent.futures
import requests
import serial
import time
import os
from typing import Optional, Callable, List

from .alarm_manager import AlarmThread
from config.preferences import load_preferences, default_preferences
from PyQt6.QtCore import Qt, QObject, pyqtSignal
from serial.tools import list_ports


# ---------------------------------------------------------------------
# Qt helper – unchanged
# ---------------------------------------------------------------------
class AlarmDispatcher(QObject):
    show_alarm_signal = pyqtSignal(str)


# ---------------------------------------------------------------------
# Main handler (USB/Wi-Fi)
# ---------------------------------------------------------------------
class CommunicationHandler:
    """USB and Wi-Fi communications to the TECNAL controller with resilient retry/backup cycle."""

    # Tunables for snappy, bounded attempts
    _WIFI_TRIES = 3
    _WIFI_TIMEOUT = (0.25, 0.35)   # (connect, read)
    _USB_TRIES = 10                # 10 * (read 0.3 + small gaps) ≈ ~3 s worst case
    _USB_READ_TIMEOUT = 0.75

    def __init__(self) -> None:
        # ---------------- live connection ----------------------------
        self.mode: Optional[str] = None              # "USB" / "WiFi"
        self.ser: Optional[serial.Serial] = None
        self.ip: Optional[str] = None

        # connection & monitor state
        self.connection_status: str = "Desconectado"
        self.last_connection_status: str = self.connection_status
        self._connection_established: bool = False
        self._connections_disabled: bool = False

        # background connect worker bookkeeping
        self._connect_lock = threading.RLock()
        self._connect_thread: Optional[threading.Thread] = None
        self._connect_label: Optional[str] = None  # "USB" or "WiFi"

        # retry/backup cycle
        self._backup_index: int = -1
        self.backup_enabled: bool = True          # if True: cycle USB/WiFi; if False: retry preferred only
        self.backup_delay_s: float = 5.0
        self._last_reconnect_attempt = 0.0
        self._timers: set[threading.Timer] = set()
        self._shutdown = threading.Event()

        # which medium the user picked last (or last connected)
        self._preferred_medium: Optional[str] = None  # "USB" / "WiFi"

        # logger callback (UI) – injected by GUI
        self.logger: Optional[Callable[[str], None]] = None

        # executor for async HTTP gets (readData)
        self.executor = concurrent.futures.ThreadPoolExecutor(max_workers=1)

        # ---------------- USB parameters -----------------------------
        self.usb_port               = None
        self.usb_baud_rate          = None
        self.usb_data_bits          = None
        self.usb_stop_bits          = None
        self.usb_parity             = None

        # ---------------- calibration -------------------------------
        self.oxy_cal_a = None
        self.oxy_cal_b = None
        try:
            prefs = load_preferences() or default_preferences()
            self.pH_slope       = float(prefs.get("Configurations", {}).get("ph_cal_slope", "1.0"))
            self.pH_intercept = float(prefs.get("Configurations", {}).get("ph_cal_intercept", "0.0"))
        except Exception:
            self.pH_slope = 1.0
            self.pH_intercept = 0.0

        # ---------------- last sensor readings -----------------------
        self.tempReadVal    = -1
        self.oxyReadVal   = -1
        self.phReadVal    = -1
        self.phRead       = -1
        self.pressureReadVal = -1
        self.flowReadVal     = -1
        self.distanceReadVal = -1
        self.antifoamReadVal = -1
        self.biomassAbsorbance = -1.0
        self.biomassRaw = 0
        self.biomassItMs = 0
        self.biomassPwmPct = 0.0
        self.biomassHdMode = False
        self._raw_time_min   = 0
        self.timeReadVal     = 0
        self.timeZeroOffset  = 0
        self.pumpFlowReadVal = -1.0
        self.pumpVolReadVal = -1.0

        self.load_filter_settings()

        # Per-channel filter state
        self._ph_state = {
            "last_good_raw": None,     # last accepted raw pH (from controller units)
            "candidate_raw": None,     # quarantined candidate raw pH
            "candidate_runs": 0,       # confirmations accumulated
        }
        self._oxy_state = {
            "last_good_raw": None, 
            "candidate_raw": None,
            "candidate_runs": 0,
        }

        # Last valid calibrated values, used for outbound updates
        self._ph_last_valid_cal = None   # float in [0, 30000)


        # helpers for ESP32 / module failure detection
        self._last_time_value      = None
        self._same_time_count      = 0
        self._temp_const_count     = 0
        self._last_temp_value      = None
        self._ph_const_count       = 0
        self._last_ph_value        = None
        self._distance_last_seen   = 0.0

        # ---------------- alarm -------------------------------------
        self.alarm_thread       = None
        self.alarm_stop_event = threading.Event()
        self.alarm_mute_flag  = threading.Event()
        self._last_alarm_trigger = None

        # background HTTP session for readData (reused, no proxies)
        self._session = requests.Session()
        self._session.trust_env = False

        # UI callbacks
        self._connection_failed_callback: Optional[Callable[[str], None]] = None
        self._connection_changed_callback: Optional[Callable[[bool, Optional[str]], None]] = None

        # Monitor thread (detect link loss)
        self.connection_monitor_thread = threading.Thread(
            target=self._monitor_connection,
            daemon=True,
        )
        self.connection_monitor_thread.start()

        # polling cadence
        self._min_period_s = 1.0
        self._next_poll = 0.0
        self._etag = None

        # attempt counters (for logging)
        self._attempts = {"USB": 0, "WiFi": 0}

    # -----------------------------------------------------------------
    # Logging helper (thread-safe)
    # -----------------------------------------------------------------
    def _log(self, msg: str) -> None:
        ts = time.strftime("%H:%M:%S")
        line = f"[{ts}] {msg}"
        try:
            if self.logger:
                self.logger(line)
            else:
                print(line, flush=True)
        except Exception:
            # If UI logger ever fails (e.g., modal dialog/state), still print.
            print(line, flush=True)
    
    def load_filter_settings(self):
        """Carrega as configurações do filtro de spike do arquivo de preferências."""
        try:
            prefs = load_preferences() or default_preferences()
            filter_prefs = prefs.get("Filters", {})
            self.SPIKE_ABS_THRESHOLD_PH   = float(filter_prefs.get("spike_abs_threshold_ph", "500.0"))
            self.SPIKE_ABS_THRESHOLD_OXY  = float(filter_prefs.get("spike_abs_threshold_oxy", "150.0"))
            self.FOLLOW_TOLERANCE_PH      = float(filter_prefs.get("follow_tolerance_ph", "200.0"))
            self.FOLLOW_TOLERANCE_OXY     = float(filter_prefs.get("follow_tolerance_oxy", "50.0"))
            self.SPIKE_CONFIRM_RUNS       = int(filter_prefs.get("spike_confirm_runs", "3"))
        except (ValueError, TypeError):
            # Fallback para valores seguros em caso de erro no arquivo de preferências
            self.SPIKE_ABS_THRESHOLD_PH   = 500.0
            self.SPIKE_ABS_THRESHOLD_OXY  = 150.0
            self.FOLLOW_TOLERANCE_PH      = 200.0
            self.FOLLOW_TOLERANCE_OXY     = 50.0
            self.SPIKE_CONFIRM_RUNS       = 3
        
        if self.logger:
            self.logger("Configurações do filtro de spike carregadas/atualizadas.")


    # =================================================================
    # ------------------------- PUBLIC API ----------------------------
    # =================================================================
    def connect_usb(self, port: str, baud: int, data_bits: int, stop_bits: int, parity) -> None:
        """Request a USB connection (non-blocking; runs in a worker)."""
        if self._connections_disabled:
            self._log("USB: conexão bloqueada (todas as conexões desativadas).")
            return

        port = (port or "").strip()
        if (not port) or ("Nenhuma porta" in port):
            self._log("USB: porta inválida/indisponível; falhando handshake imediatamente.")
            self._preferred_medium = "USB"
            self._handshake_failed("USB")
            self._arm_retry_once(reason="Porta USB inválida/não selecionada") # Manually arm retry
            return

        self._preferred_medium = "USB"
        args = (port, baud, data_bits, stop_bits, parity)
        self._start_connect_worker("USB", self._connect_usb_sync, *args)

    def connect_wifi(self, ip: str) -> None:
        """Request a Wi-Fi connection (non-blocking; runs in a worker)."""
        if self._connections_disabled:
            self._log("WiFi: conexão bloqueada (todas as conexões desativadas).")
            return

        ip = (ip or "").strip()
        if not ip:
            self._log("WiFi: IP não definido; falhando handshake imediatamente.")
            self._preferred_medium = "WiFi"
            self._handshake_failed("WiFi")
            self._arm_retry_once(reason="Endereço IP não definido") # Manually arm retry
            return

        self._preferred_medium = "WiFi"
        self._start_connect_worker("WiFi", self._connect_wifi_sync, ip)

    def set_poll_period_ms(self, ms: int) -> None:
        """Limit read cadence for both transports to ~dataDelay."""
        self._min_period_s = max(0.1, ms / 1000.0)
        self._log(f"Cadência de leitura definida para ~{self._min_period_s:.2f}s.")

    def set_backup_options(self, enabled: bool, delay_seconds: float) -> None:
        """Configure retry cadence and strategy.

        enabled=True  -> cycle media (USB/WiFi) on failure (backup mode)
        enabled=False -> retry only the preferred/selected medium
        """
        self.backup_enabled = bool(enabled)
        try:
            self.backup_delay_s = max(1.0, float(delay_seconds))
        except Exception:
            pass
        state = "ativado" if self.backup_enabled else "desativado"
        self._log(f"Backup {state}; período={self.backup_delay_s:.1f}s.")
        # IMPORTANT: do NOT auto-arm here; wait for an explicit failure or loss

    def set_connection_failed_callback(self, callback):
        self._connection_failed_callback = callback

    def set_connection_changed_callback(self, callback: Callable[[bool, Optional[str]], None]):
        """callback(connected: bool, medium: Optional[str])"""
        self._connection_changed_callback = callback

    def disable_all_connections(self) -> None:
        self._connections_disabled = True
        self.disconnect()
        self.connection_status = "Desconectado"
        self.last_connection_status = self.connection_status
        self._log("Todas as comunicações foram desconectadas e desativadas.")

    def disconnect(self) -> None:
        """Force close the current transport; cancels scheduled retries."""
        with self._connect_lock:
            last_mode = self.mode
            if self.mode == "USB" and self.ser:
                try:
                    self.ser.close()
                except Exception:
                    pass
                self.ser = None
            self.mode = None
            self._connection_established = False
            self.connection_status = "Desconectado"
            self.last_connection_status = self.connection_status
            self.stop_alarm()
        self._cancel_retry_timers()
        self._log("Desconectado.")
        self._emit_connection_changed(False, last_mode)

    # convenience for UI calls
    def set_oxygen_calibration(self, a: float, b: float) -> None:
        self.oxy_cal_a, self.oxy_cal_b = a, b
        self._log(f"Calibração de O₂ atualizada: a={a}, b={b}")

    def set_pH_calibration(self, slope: float, intercept: float) -> None:
        self.pH_slope, self.pH_intercept = slope, intercept
        self._log(f"Calibração de pH atualizada: slope={slope}, intercept={intercept}")

    # =================================================================
    # --------------------- PRIVATE IMPLEMENTATION --------------------
    # =================================================================
    # -------- Connect worker management (non-blocking) ----------------
    def _start_connect_worker(self, label: str, fn: Callable, *args) -> None:
        with self._connect_lock:
            if self._connect_thread and self._connect_thread.is_alive():
                self._log(f"{label}: tentativa ignorada (conexão já em progresso via {self._connect_label}).")
                return
            self._connect_label = label
            self._attempts[label] += 1
            n = self._attempts[label]
            self._log(f"{label}: tentativa #{n} iniciada.")
            thread_args = (label, fn) + args
            t = threading.Thread(target=self._run_connect_wrapper, args=thread_args, daemon=True)
            self._connect_thread = t
            t.start()

    def _run_connect_wrapper(self, label: str, fn: Callable, *fn_args) -> None:
        """Wrapper to execute connection function, with unified error handling."""
        try:
            self._log(f"{label}: worker iniciado.")
            fn(*fn_args)
        except Exception as e:
            self._log(f"{label}: exceção inesperada durante conexão: {e!r}")
            self._handshake_failed(label)
        finally:
            with self._connect_lock:
                self._connect_thread = None
                self._connect_label = None
            self._log(f"{label}: worker finalizado.")
            if (not self._connection_established) and (not self._shutdown.is_set()):
                self._arm_retry_once(reason=f"Tentativa de {label} terminou sem sucesso")

    def _open_serial_with_timeout(self, port, baud, data_bits, stop_bits, parity, timeout_s=3.0):
        holder = {"ser": None, "err": None}
        def _opener():
            try:
                holder["ser"] = serial.Serial(
                    port=port, baudrate=baud, bytesize=data_bits,
                    stopbits=stop_bits, parity=parity,
                    write_timeout=1.0, inter_byte_timeout=0.1,
                    timeout=self._USB_READ_TIMEOUT
                )
            except Exception as e:
                holder["err"] = e
        t = threading.Thread(target=_opener, daemon=True)
        t.start()
        t.join(timeout_s)
        if t.is_alive():
            return None, TimeoutError(f"open({port}) excedeu {timeout_s:.1f}s")
        return holder["ser"], holder["err"]

    # -----------------------------------------------------------------
    # Handshakes (bounded, chatty)
    # -----------------------------------------------------------------
    def _wifi_handshake(self, session: requests.Session, ip: str) -> bool:
        url = f"http://{ip}/command"
        headers = {"Content-Type": "application/json"}
        for i in range(1, self._WIFI_TRIES + 1):
            try:
                self._log(f"WiFi: handshake tentativa {i}/{self._WIFI_TRIES} → enviando comTest…")
                r = session.post(url, data='{"comTest":1}', headers=headers, timeout=self._WIFI_TIMEOUT)
                reply = (r.text or "").strip()
            except Exception as e:
                self._log(f"WiFi: erro na tentativa {i}: {e!r}")
                reply = None
            self._log(f"WiFi: handshake tentativa {i}/{self._WIFI_TRIES} → {reply!r}")
            if reply == "OK":
                return True
            time.sleep(0.1)
        return False

    def _usb_handshake(self, ser: serial.Serial) -> bool:
        ser.timeout = self._USB_READ_TIMEOUT
        for i in range(1, self._USB_TRIES + 1):
            try:
                ser.write(b'{"comTest":1}\n')
                ser.flush()
            except Exception:
                pass
            reply = None
            try:
                line = ser.readline()
                reply = line.decode(errors="ignore").strip() or None
            except Exception:
                reply = None
            self._log(f"USB: handshake tentativa {i}/{self._USB_TRIES} → {reply!r}")
            if reply == "OK":
                return True
            time.sleep(0.1)
        return False

    def _handshake_success(self, medium: str) -> None:
        self._connection_established = True
        self._preferred_medium = medium or self._preferred_medium
        self.connection_status = "Ok"
        self.last_connection_status = self.connection_status
        self._temp_const_count = 0
        self._ph_const_count = 0
        self.stop_alarm()
        self._emit_connection_changed(True, medium)
        self._log(f"{medium}: handshake OK.")
        self._cancel_retry_timers()
        self._log("Backup/retry pausado (conectado). Será reativado automaticamente em caso de falha.")

    def _handshake_failed(self, medium: str) -> None:
        """Simplified to only handle state changes; retry is armed by the caller or worker."""
        self._log(f"{medium}: falha no comTest.")
        self._cleanup_current_transport()
        self.connection_status = "Desconectado"
        self.last_connection_status = self.connection_status
        self._connection_established = False

        if hasattr(self, "_connection_failed_callback"):
            try:
                self._connection_failed_callback(medium)
            except Exception:
                pass

        self._emit_connection_changed(False, medium)

    # -----------------------------------------------------------------
    # Wi-Fi connect (sync; called from worker)
    # -----------------------------------------------------------------
    def _connect_wifi_sync(self, ip: str) -> None:
        with self._connect_lock:
            self._cleanup_current_transport()
            self.mode = "WiFi"
            self.ip = ip

        self._log(f"WiFi: conectando a {ip} …")

        self._log("WiFi: iniciando handshake (POST /command com comTest)…")
        ok = self._wifi_handshake(self._session, ip)

        if ok:
            self._next_poll = 0.0
            self._etag = None
            self._log(f"WiFi: conectado em {ip}")
            self._handshake_success("WiFi")
        else:
            self._handshake_failed("WiFi")

    # -----------------------------------------------------------------
    # USB connect (sync; called from worker)
    # -----------------------------------------------------------------
    def _connect_usb_sync(self, port: str, baud: int, data_bits: int, stop_bits: int, parity) -> None:
        try:
            baud, data_bits, stop_bits, parity = self._convert_usb_params(baud, data_bits, stop_bits, parity)
        except ValueError as e:
            self._log(f"USB: Parâmetros inválidos: {e}")
            self._handshake_failed("USB")
            return

        self._log(f"USB: abrindo porta {port} @ {baud},{data_bits},{stop_bits},{parity} …")

        ser, err = self._open_serial_with_timeout(port, baud, data_bits, stop_bits, parity, timeout_s=3.0)
        if err is not None:
            self._log(f"USB: erro ao abrir porta {port}: {err!r}")
            self._handshake_failed("USB")
            return
        if ser is None:
            self._log(f"USB: abertura da porta {port} travou; abortando tentativa.")
            self._handshake_failed("USB")
            return

        self._log(f"USB: porta {port} aberta, iniciando handshake…")

        try:
            ser.dtr = False; ser.rts = False
            time.sleep(0.05)
            ser.dtr = True;  ser.rts = True
        except Exception:
            pass

        time.sleep(1.8)
        try:
            ser.reset_input_buffer()
            ser.reset_output_buffer()
        except Exception:
            pass

        with self._connect_lock:
            self.ser = ser
            self.mode = "USB"

        ok = self._usb_handshake(ser)
        if ok:
            with self._connect_lock:
                self.usb_port = port
                self.usb_baud_rate = baud
                self.usb_data_bits = data_bits
                self.usb_stop_bits = stop_bits
                self.usb_parity = parity
            self._log(f"USB conectado: {port}")
            self._handshake_success("USB")
        else:
            try:
                ser.close()
            except Exception:
                pass
            with self._connect_lock:
                self.ser = None
                self.mode = None
            self._handshake_failed("USB")

    # -----------------------------------------------------------------
    # USB helpers
    # -----------------------------------------------------------------
    def _convert_usb_params(self, baud, data_bits, stop_bits, parity):
        """CORRIGIDO: Converte robustamente os parâmetros da porta serial."""
        try:
            baud = int(baud)
            data_bits = int(data_bits)
            stop_bits = int(stop_bits)

            # Conversão robusta de paridade
            p_val = serial.PARITY_NONE # Padrão
            if isinstance(parity, str):
                p_str = parity.strip().upper()
                if p_str in ("NONE", "N"):
                    p_val = serial.PARITY_NONE
                elif p_str in ("EVEN", "E"):
                    p_val = serial.PARITY_EVEN
                elif p_str in ("ODD", "O"):
                    p_val = serial.PARITY_ODD
                elif p_str in ("MARK", "M"):
                    p_val = serial.PARITY_MARK
                elif p_str in ("SPACE", "S"):
                    p_val = serial.PARITY_SPACE
            
            # Se já for o objeto pyserial (ex: serial.PARITY_NONE)
            elif parity in (serial.PARITY_NONE, serial.PARITY_EVEN, serial.PARITY_ODD, serial.PARITY_MARK, serial.PARITY_SPACE):
                p_val = parity
            
            return baud, data_bits, stop_bits, p_val
        except Exception as exc:
            raise ValueError(f"Parâmetros USB inválidos: {exc}") from exc

    def test_usb_port(self, port, baud, data_bits, stop_bits, parity) -> bool:
        try:
            baud, data_bits, stop_bits, parity = self._convert_usb_params(
                baud, data_bits, stop_bits, parity
            )
            with serial.Serial(
                port=port, baudrate=baud, bytesize=data_bits,
                stopbits=stop_bits, parity=parity,
                timeout=self._USB_READ_TIMEOUT, write_timeout=0.5, inter_byte_timeout=0.1
            ) as ser:
                try:
                    ser.dtr = False; ser.rts = False
                    time.sleep(0.05)
                    ser.dtr = True;  ser.rts = True
                except Exception:
                    pass
                time.sleep(0.4)
                ser.reset_input_buffer()

                ser.write(b'{"comTest":1}\n')
                ser.flush()
                line = ser.readline()
                return line.decode(errors="ignore").strip() == "OK"
        except Exception:
            return False

    def find_working_usb_port(self, baud, data_bits, stop_bits, parity) -> Optional[str]:
        for info in list_ports.comports():
            if info.description and "Bluetooth" in info.description:
                continue
            if self.test_usb_port(info.device, baud, data_bits, stop_bits, parity):
                return info.device
        return None

    # -----------------------------------------------------------------
    # Command TX
    # -----------------------------------------------------------------
    def send_command(self, cmd: dict) -> None:
        if "nutriIntensity" in cmd:
            try:
                v = int(cmd["nutriIntensity"])
                if v > 0:
                    self._nutri_intensity_target = v
            except Exception:
                pass
        data = json.dumps(cmd, separators=(",", ":"))
        if self.mode == "USB":
            with self._connect_lock:
                s = self.ser
            if s:
                try:
                    s.write((data + "\n").encode("utf-8"))
                    s.flush()
                except Exception:
                    pass
        elif self.mode == "WiFi" and self.ip:
            threading.Thread(target=self._post_wifi, args=(data,), daemon=True).start()

    def _post_wifi(self, data: str) -> None:
        try:
            self._session.post(
                f"http://{self.ip}/command",
                data=data,
                headers={"Content-Type": "application/json"},
                timeout=0.5,
            )
        except Exception:
            pass

    # -----------------------------------------------------------------
    # Data acquisition & failure detection
    # -----------------------------------------------------------------
    def read_data(self) -> Optional[str]:
        try:
            if self.mode == "USB":
                with self._connect_lock:
                    s = self.ser
                if s:
                    try:
                        if s.in_waiting:
                            line = s.readline()
                            return line.decode(errors="ignore").strip() or None
                    except Exception:
                        return None

            elif self.mode == "WiFi" and self.ip:
                # cadence gate
                now = time.time()
                if now < self._next_poll:
                    return None
                self._next_poll = now + self._min_period_s * 0.98

                def _do_get():
                    headers = {}
                    if self._etag:
                        headers["If-None-Match"] = self._etag
                    r = self._session.get(
                        f"http://{self.ip}/readData",
                        headers=headers,
                        timeout=(0.25, 0.75)
                    )
                    return r

                future = self.executor.submit(_do_get)
                r = future.result(timeout=0.9)

                if r.status_code == 304:
                    return None
                if r.status_code != 200:
                    return None

                et = r.headers.get("ETag")
                if et:
                    self._etag = et
                return r.text.strip()

        except Exception:
            return None

        return None

    def _filter_spiky_signal(self, state: dict, new_raw: float,
                             abs_threshold: float, follow_tol: float,
                             confirm_runs: int) -> tuple[float, bool]:
        """
        Spike/step filter for raw sensor channels.

        Returns (accepted_raw_value, updated_flag).
        updated_flag is True only when the returned value differs from state's last_good_raw.

        Rules:
          1) If no last_good_raw exists, accept new_raw.
          2) If |new_raw - last_good_raw| <= abs_threshold, accept immediately,
             clear any candidate.
          3) Otherwise quarantine as a candidate. Promote to last_good_raw after
             'confirm_runs' subsequent readings that remain within 'follow_tol'
             of the candidate. Reset candidate if a new reading deviates from the current candidate
             by more than 'follow_tol'.
        """
        if new_raw is None:
            return state.get("last_good_raw"), False

        lg = state.get("last_good_raw")
        if lg is None:
            state["last_good_raw"] = new_raw
            state["candidate_raw"] = None
            state["candidate_runs"] = 0
            return new_raw, True

        if abs(new_raw - lg) <= abs_threshold:
            # Normal evolution, accept and clear candidate
            state["last_good_raw"] = new_raw
            state["candidate_raw"] = None
            state["candidate_runs"] = 0
            return new_raw, True

        # Spike detected, manage candidate
        cand = state.get("candidate_raw")
        if cand is None or abs(new_raw - cand) > follow_tol:
            # Start fresh candidate
            state["candidate_raw"] = new_raw
            state["candidate_runs"] = 1
            return lg, False

        # Candidate persists within follow window
        state["candidate_runs"] += 1
        if state["candidate_runs"] >= confirm_runs:
            # Promote step change
            state["last_good_raw"] = new_raw
            state["candidate_raw"] = None
            state["candidate_runs"] = 0
            return new_raw, True

        # Keep previous value until confirmed
        return lg, False

    def read_and_parse_data(self) -> None:
        """Parse JSON, update sensor variables and run failure logic."""
        if not self._connection_established:
            return
        data_str = self.read_data()
        if not data_str:
            return

        # JSON decoding
        try:
            data = json.loads(data_str)
        except Exception:
            return

        # ---- sensor parsing -----------------------------------------
        try:
            if 10 < float(data.get("Tempval", -1)) < 100:
                self.tempReadVal = float(data.get("Tempval", -1))
        except Exception:
            self.tempReadVal = -1
        # ---- oxygen: raw read, spike filter on raw, then calibrate ----
        try:
            oxy_raw_in = float(data.get("Oxyval", -1))
            if oxy_raw_in <= 0.1:
                # treat as missing; do not change last_good
                pass
            else:
                accepted_oxy_raw, oxy_updated = self._filter_spiky_signal(
                    self._oxy_state,
                    oxy_raw_in,
                    self.SPIKE_ABS_THRESHOLD_OXY,
                    self.FOLLOW_TOLERANCE_OXY,
                    self.SPIKE_CONFIRM_RUNS,
                )
                # Calibrate accepted raw
                a = self.oxy_cal_a if self.oxy_cal_a is not None else 0.030573419314
                b = self.oxy_cal_b if self.oxy_cal_b is not None else -25.09036520919
                oxy_val = a * float(accepted_oxy_raw) + b
                self.oxyReadVal = round(max(oxy_val, 0.0), 4)
        except Exception:
            # preserve last known value on parsing/calibration failure
            pass

        # ---- pH: raw read, spike filter on raw, then calibrate --------
        try:
            ph_raw_in = float(data.get("pHval", -1))
            if ph_raw_in <= 0.1:
                # treat as missing; do not change last_good
                pass
            else:
                # Filter on raw controller units, not on calibrated pH
                accepted_ph_raw, ph_updated = self._filter_spiky_signal(
                    self._ph_state,
                    ph_raw_in,
                    self.SPIKE_ABS_THRESHOLD_PH,
                    self.FOLLOW_TOLERANCE_PH,
                    self.SPIKE_CONFIRM_RUNS,
                )
                # Keep both raw and calibrated representations
                self.phRead = accepted_ph_raw
                # Apply linear calibration
                ph_cal = float(round(self.pH_slope * accepted_ph_raw + self.pH_intercept, 2))
                # Clamp negative to zero per previous behavior
                if ph_cal < 0:
                    ph_cal = 0.0
                # Enforce operating range [0, 30000)
                if 0.0 <= ph_cal < 30000.0:
                    self.phReadVal = ph_cal
                    # Update the "last valid calibrated" holder
                    self._ph_last_valid_cal = ph_cal
                else:
                    # Do not accept calibrated out-of-range values, keep last good
                    pass

                # Requirement 1: transmit the last valid calibrated pH only
                if self._ph_last_valid_cal is not None:
                    self.send_command({"pHCal": format(self._ph_last_valid_cal, ".2f")})
        except Exception:
            # On error, do not overwrite phRead/phReadVal or the last valid holder
            pass
        try:
            self.pressureReadVal = float(data.get("Pressure", -1))
        except Exception:
            self.pressureReadVal = -1
        try:
            self.flowReadVal = float(data.get("FlowRate", -1))
        except Exception:
            self.flowReadVal = -1

        # Distance (no auto-disable, no pump side effects)
        try:
            if "Distance" in data:
                val = float(data["Distance"])
                if 0 <= val < 1000:
                    self.distanceReadVal = val
                    self._distance_last_seen = time.time()
            else:
                if time.time() - getattr(self, "_distance_last_seen", 0.0) > 3.0:
                    self.distanceReadVal = -1
        except Exception:
            pass
        try:
            self.antifoamReadVal = float(data.get("Antifoam", -1))
        except Exception:
            self.antifoamReadVal = -1

        try:
            if "BiomassAbs" in data:
                self.biomassAbsorbance = float(data.get("BiomassAbs", -1.0))
            if "raw" in data:
                self.biomassRaw = int(data.get("raw", 0))
            if "it_ms" in data:
                self.biomassItMs = int(data.get("it_ms", 0))
            if "pwm_pct" in data:
                self.biomassPwmPct = float(data.get("pwm_pct", 0.0))
            if "hd_mode" in data:
                self.biomassHdMode = bool(data.get("hd_mode", False))
        except Exception:
            # Em caso de erro, mantém os valores anteriores
            pass

        try:
            if "PumpFlow" in data:
                self.pumpFlowReadVal = float(data.get("PumpFlow", -1.0))
            if "PumpVol" in data:
                self.pumpVolReadVal = float(data.get("PumpVol", -1.0))
        except Exception:
            # Em caso de erro, zera os valores
            self.pumpFlowReadVal = -1.0
            self.pumpVolReadVal = -1.0
        
        try:
            raw_time = float(data.get("Time", 0))
            self._raw_time_min = raw_time / 60.0
            self.timeReadVal = round(self._raw_time_min - self.timeZeroOffset, 2)
        except Exception:
            self.timeReadVal = 0

        # -------- ESP32 power-down – constant time -------------------
        if self.timeReadVal == self._last_time_value:
            self._same_time_count += 1
            if self._same_time_count >= 10:
                if self.connection_status != "ESP32 desligado":
                    self.connection_status = "ESP32 desligado"
                    self.last_connection_status = self.connection_status
                    self._log("Detecção: ESP32 desligado (tempo sem variação). Forçando reconexão.")
                    self.trigger_alarm("ESP32 não responde")
                
                # Se a conexão ESTIVER estabelecida, mas os dados estiverem obsoletos,
                # devemos forçar o estado de "desconexão" ANTES de tentar reconectar.
                if self._connection_established:
                    self._log("Dados obsoletos detectados. Fechando transporte atual para forçar o ciclo de reconexão.")
                    self._connection_established = False
                    self._cleanup_current_transport() # Fecha a porta/sessão
                    self._emit_connection_changed(False, self.mode) # Notifica a UI
                
                # Agora, _start_reconnect_cycle() funcionará porque _connection_established é False
                self._start_reconnect_cycle()
        else:
            self._same_time_count = 0
            if self.connection_status == "ESP32 desligado":
                self.connection_status = "Ok"
                self.stop_alarm()
                self.last_connection_status = self.connection_status
            self._last_time_value = self.timeReadVal

        if self.connection_status == "Ok":
            self._log(data_str)

    # -----------------------------------------------------------------
    # Connection monitor (loss/retry)
    # -----------------------------------------------------------------
    def _monitor_connection(self) -> None:
        while not self._shutdown.is_set():
            time.sleep(1)
            if not self._connection_established or self._connections_disabled:
                continue

            lost = False
            mode = self.mode
            if mode == "USB":
                with self._connect_lock:
                    s = self.ser
                if not s:
                    lost = True
                else:
                    try:
                        _ = s.in_waiting
                        lost = not s.is_open
                    except Exception:
                        lost = True
            elif mode == "WiFi":
                try:
                    r = self._session.get(f"http://{self.ip}/ping", timeout=1.0)
                    lost = r.status_code != 200
                except Exception:
                    lost = True

            if lost:
                self._log(f"{mode or 'Desconhecido'}: conexão falhou ou foi perdida.")
                self.trigger_alarm(f"Conexão {mode or ''} perdida")
                last_mode = mode
                self.disconnect()  # emits connection_changed(False, last_mode) and cancels timers
                if hasattr(self, "_connection_failed_callback"):
                    try:
                        self._connection_failed_callback(last_mode or "Unknown")
                    except Exception:
                        pass
                # Start retries immediately (backup or single-medium based on setting)
                self._start_reconnect_cycle()

    # -----------------------------------------------------------------
    # Retry / backup loop (always visible, never silent)
    # -----------------------------------------------------------------
    def _arm_retry_once(self, reason: str = "") -> None:
        if self._shutdown.is_set():
            return
        t = threading.Timer(self.backup_delay_s, self._start_reconnect_cycle)
        t.daemon = True
        self._timers.add(t)
        t.start()
        if reason:
            self._log(f"{'Backup' if self.backup_enabled else 'Retry'} armado (em {self.backup_delay_s:.1f}s). Motivo: {reason}")

    def _cancel_retry_timers(self) -> None:
        for t in list(self._timers):
            try:
                t.cancel()
            except Exception:
                pass
        self._timers.clear()

    def _cleanup_current_transport(self) -> None:
        """Fecha porta USB antes de mudar de meio."""
        with self._connect_lock:
            if self.mode == "USB" and self.ser:
                try:
                    self.ser.close()
                except Exception:
                    pass
                finally:
                    self.ser = None
            self.mode = None

    def _available_methods(self) -> List[str]:
        """Return methods that are currently *possible* to try."""
        methods: List[str] = []
        ip = (self.ip or "").strip()
        if ip:
            methods.append("WiFi")

        # USB: consider even without pre-selected port; probe can find one later
        try:
            has_known_usb = bool(self.usb_port) and "Nenhuma porta" not in str(self.usb_port)
            has_any_usb = any(True for _ in list_ports.comports())
        except Exception:
            has_known_usb = False
            has_any_usb = False

        if has_known_usb or has_any_usb:
            methods.append("USB")

        return methods

    def _start_reconnect_cycle(self) -> None:
        if self._shutdown.is_set():
            return
        if self._connection_established and not self._connections_disabled:
            self._log(f"{'Backup' if self.backup_enabled else 'Retry'} tick: conectado; ciclo em espera.")
            return
        with self._connect_lock:
            connecting = bool(self._connect_thread and self._connect_thread.is_alive())
            label = self._connect_label
        if connecting:
            self._log(f"{'Backup' if self.backup_enabled else 'Retry'} tick: tentativa em progresso via {label}; aguardando próxima janela.")
            self._arm_retry_once(reason="Tentativa atual ainda em andamento")
            return
        now = time.time()
        if now - self._last_reconnect_attempt < self.backup_delay_s * 0.95:
            self._log(f"{'Backup' if self.backup_enabled else 'Retry'} tick: ignorado (throttle).")
            self._arm_retry_once(reason="Throttle")   
            return

        if self.backup_enabled:
            methods = self._available_methods()
            usb_info = "disponível" if "USB" in methods else "indisponível"
            wifi_info = "disponível" if "WiFi" in methods else "indisponível"
            self._log(f"Backup tick: Wi-Fi {wifi_info}; USB {usb_info}.")
            if not methods:
                self._log("Backup: nenhum meio disponível no momento; vou tentar de novo.")
                self._arm_retry_once(reason="Sem meios disponíveis")
                return
            self._backup_index = (self._backup_index + 1) % len(methods)
            next_medium = methods[self._backup_index]
            self._log(f"Backup: tentando {next_medium} …")
            self._try_medium(next_medium, is_backup=True)
        else:
            target = self._preferred_medium
            if target is None:
                methods = self._available_methods()
                if "WiFi" in methods:
                    target = "WiFi"
                elif "USB" in methods:
                    target = "USB"
                else:
                    self._log("Retry: nenhum meio disponível; aguardando próxima janela.")
                    self._arm_retry_once(reason="Sem meios disponíveis")
                    return
                self._preferred_medium = target
            self._log(f"Retry: tentando {target} …")
            self._try_medium(target, is_backup=False)

        self._arm_retry_once(reason=f"Tentativa {'backup' if self.backup_enabled else 'retry'} em andamento")

    # -----------------------------------------------------------------
    # Helper to attempt a given medium from the tick
    # -----------------------------------------------------------------
    def _try_medium(self, medium: str, is_backup: bool) -> None:
        """Launch a connection attempt for the given medium."""
        medium = medium or ""
        if medium == "USB":
            # Prefer known port; otherwise probe once
            port = self.usb_port if (self.usb_port and "Nenhuma porta" not in str(self.usb_port)) else None
            if not port:
                port = self.find_working_usb_port(115200, 8, 1, serial.PARITY_NONE)
                if not port:
                    self._log(f"{'Backup' if is_backup else 'Retry'} USB: nenhuma porta funcional encontrada nesta rodada.")
                    return
            self.connect_usb(port, 115200, 8, 1, serial.PARITY_NONE)

        elif medium == "WiFi":
            ip = (self.ip or "").strip()
            if not ip:
                self._log(f"{'Backup' if is_backup else 'Retry'} WiFi: IP não definido nesta rodada.")
                return
            self.connect_wifi(ip)

        else:
            self._log(f"Tentativa ignorada: meio desconhecido '{medium}'.")

    # =================================================================
    # SHUTDOWN
    # =================================================================
    def shutdown(self) -> None:
        self._shutdown.set()
        self._cancel_retry_timers()
        try:
            self.executor.shutdown(wait=False, cancel_futures=True)
        except Exception:
            pass
        self.disconnect()

    # =================================================================
    # ALARM
    # =================================================================
    def trigger_alarm(self, msg: str) -> None:
        if self.alarm_mute_flag.is_set():
            return
        if self._last_alarm_trigger == msg:
            return
        self._last_alarm_trigger = msg
        self._log(f"Alarme: {msg}")
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

    # =================================================================
    # User muting
    # =================================================================
    def _mute_changed(self, state):
        if state == Qt.CheckState.Checked:
            self.alarm_mute_flag.set()
            self._log("Alarme sonoro desativado pelo usuário.")
        else:
            self.alarm_mute_flag.clear()
            self._log("Alarme sonoro ativado pelo usuário.")

    # =================================================================
    # Emit helper
    # =================================================================
    def _emit_connection_changed(self, connected: bool, medium: Optional[str]):
        try:
            if self._connection_changed_callback:
                self._connection_changed_callback(connected, medium)
        except Exception:
            pass