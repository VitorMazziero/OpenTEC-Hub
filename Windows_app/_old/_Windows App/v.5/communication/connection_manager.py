#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
communication/connection_manager.py
~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
ConnectionManager: orchestrates TransportLayer + DataParser behind a clean
state machine.  Emits Qt signals so the UI reacts to state, never polls.

State transitions
-----------------

    DISCONNECTED ──(user connect / auto-retry)──▶ CONNECTING
    CONNECTING   ──(handshake OK)────────────────▶ CONNECTED
    CONNECTING   ──(handshake failed, backup on)─▶ RECONNECTING
    CONNECTING   ──(handshake failed, backup off)▶ ERROR
    CONNECTED    ──(link lost, backup on)─────────▶ RECONNECTING
    CONNECTED    ──(user disconnect)──────────────▶ DISCONNECTED
    RECONNECTING ──(handshake OK)────────────────▶ CONNECTED
    RECONNECTING ──(user disconnect)──────────────▶ DISCONNECTED
    ERROR        ──(user connect)────────────────▶ CONNECTING
    *            ──(shutdown)─────────────────────▶ DISCONNECTED
"""
from __future__ import annotations

import json
import logging
import os
import threading
import time
from enum import Enum, auto
from typing import Any, Dict, Iterator, List, Optional, Tuple
from queue import Queue, Empty
from dataclasses import replace

from PySide6.QtCore import QObject, Signal

from .transport import (
    TransportLayer, USBTransport, WiFiTransport,
    USBConfig, WiFiConfig,
)
from .data_parser import DataParser, ParserConfig, SpikeFilterConfig
import threading as _threading
import platform as _platform  
from config.preferences import load_preferences, default_preferences

log = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# State enum
# ---------------------------------------------------------------------------

class ConnectionState(Enum):
    DISCONNECTED  = auto()   # No transport, no retries scheduled
    CONNECTING    = auto()   # Handshake in progress (worker busy)
    CONNECTED     = auto()   # Heartbeat OK, data flowing
    RECONNECTING  = auto()   # Link lost, backup cycle running
    ERROR         = auto()   # Backup disabled, persistent failure


# ---------------------------------------------------------------------------
# Connect request (queued from UI thread → worker thread)
# ---------------------------------------------------------------------------

class _Request:
    """Base class for requests posted to the worker loop."""


class _ConnectRequest(_Request):
    def __init__(self, medium: str, usb_cfg: Optional[USBConfig], wifi_cfg: Optional[WiFiConfig]) -> None:
        self.medium = medium
        self.usb_cfg = usb_cfg
        self.wifi_cfg = wifi_cfg


class _DisconnectRequest(_Request):
    pass


class _ShutdownRequest(_Request):
    pass

class _LinkLostRequest(_Request):
    def __init__(self, reason: str) -> None:
        self.reason = reason


# ---------------------------------------------------------------------------
# ConnectionManager
# ---------------------------------------------------------------------------

class ConnectionManager(QObject):
    """
    Single-responsibility orchestrator.

    Signals (emit from any thread – Qt queues them for the UI automatically):
        connection_state_changed(ConnectionState, str)  – new state, medium name
        sensor_data_updated()                           – new SensorReadings ready
        alarm_triggered(str)                            – alarm message
        log_message(str)                                – human-readable log line
    """

    connection_state_changed = Signal(ConnectionState, str)
    sensor_data_updated = Signal()
    alarm_triggered = Signal(str)
    log_message = Signal(str)

    # Tunables
    _HEARTBEAT_INTERVAL_S = 1.0
    _SAME_TIME_LIMIT = 10           # consecutive identical time readings → ESP32 off

    def __init__(self, parent: Optional[QObject] = None) -> None:
        super().__init__(parent)

        # --- State ---
        self._state = ConnectionState.DISCONNECTED
        self._medium: str = ""         # "USB" or "WiFi"
        self._state_lock = threading.Lock()

        # --- Transport (lives only during CONNECTED/CONNECTING) ---
        self._transport: Optional[TransportLayer] = None
        self._transport_lock = threading.RLock()

        # --- Data Parser ---
        parser_cfg = self._build_parser_config()
        self._parser = DataParser(parser_cfg, on_ph_cal_update=self._on_ph_cal_update)

        # Expose readings as a public attribute for backward compatibility
        self.readings = self._parser.readings

        # --- Command buffer ---
        self._cmd_buffer: Dict[str, Any] = {}
        self._cmd_lock = threading.Lock()

        # --- Reconnect / backup settings ---
        self.backup_enabled = True
        self.backup_delay_s = 5.0
        self._backup_index = -1        # cycles through available methods

        # Stored connectivity params (needed for auto-reconnect)
        self._usb_cfg: Optional[USBConfig] = None
        self._wifi_cfg: Optional[WiFiConfig] = None

        # --- Worker thread ---
        self._request_queue: Queue[_Request] = Queue()
        self._queue_event = threading.Event()   # wake up worker on new request
        self._shutdown_event = threading.Event()
        self._connect_cancel_event = threading.Event()

        self._worker = threading.Thread(
            target=self._worker_loop,
            name="cm-worker",
            daemon=True,
        )
        self._worker.start()
        

        # --- ESP32 stale-data detection ---
        self._last_time_value: Optional[float] = None
        self._same_time_count = 0

        self._io_fail_streak = 0
        self._parse_fail_streak = 0
        self._good_rx_timestamp = 0.0
        self._usb_same_port_failures = 0

        # --- Alarm (Nativo via Qt) ---
        self.alarm_mute_flag = threading.Event()
        self._last_alarm_msg: Optional[str] = None
        
        
        self._alarm_playing = False
        self._alarm_thread: Optional[_threading.Thread] = None
        self._alarm_wav_path = os.path.join(
            os.path.dirname(os.path.abspath(__file__)), "alarm.wav"
        )

        # --- Sensor Module state (separate from USB/WiFi connection state) ---
        self._sensor_module_online: bool = True

        # --- Poll cadence (Wi-Fi) ---
        self._min_poll_period_s = 1.0

        # --- Attempt counters (diagnostics) ---
        self._attempts: Dict[str, int] = {"USB": 0, "WiFi": 0}

    # =================================================================
    # Public API (called from Qt main thread)
    # =================================================================

    # --- Connection control ---

    def connect_usb(
        self,
        port: str,
        baud: int = 115200,
        data_bits: int = 8,
        stop_bits: int = 1,
        parity: str = "None",
        **kwargs,
    ) -> None:
        """Request a USB connection (non-blocking)."""
        port = (port or "").strip()
        if not port or "Nenhuma porta" in port:
            self.log_aviso("USB: porta inválida; abortando.")
            self._transition(ConnectionState.ERROR, "USB")
            return

        parity_norm = USBTransport.normalize_parity(parity)
        cfg = USBConfig(
            port=port,
            baud_rate=baud,
            data_bits=data_bits,
            stop_bits=stop_bits,
            parity=parity_norm,
            cancel_event=self._connect_cancel_event,
        )
        self._usb_cfg = cfg
        self._post_request(_ConnectRequest("USB", usb_cfg=cfg, wifi_cfg=self._wifi_cfg))

    def connect_wifi(self, ip: str) -> None:
        """Request a Wi-Fi connection (non-blocking)."""
        ip = (ip or "").strip()
        if not ip:
            self.log_aviso("WiFi: IP não definido; abortando.")
            self._transition(ConnectionState.ERROR, "WiFi")
            return

        cfg = WiFiConfig(
            ip=ip,
            min_poll_period_s=self._min_poll_period_s,
            cancel_event=self._connect_cancel_event,
        )
        self._wifi_cfg = cfg
        self._post_request(_ConnectRequest("WiFi", usb_cfg=self._usb_cfg, wifi_cfg=cfg))

    def disconnect(self) -> None:
        """Tear down the current connection and cancel any retries."""
        self._connect_cancel_event.set()
        self._post_request(_DisconnectRequest())

    def shutdown(self) -> None:
        """Graceful shutdown (call from closeEvent)."""
        self._shutdown_event.set()
        self._post_request(_ShutdownRequest())
        self._worker.join(timeout=3.0)
        self._teardown_transport()

    # --- Runtime configuration ---

    def set_poll_period_ms(self, ms: int) -> None:
        self._min_poll_period_s = max(0.1, ms / 1000.0)
        with self._transport_lock:
            if isinstance(self._transport, WiFiTransport):
                self._transport.set_poll_period(self._min_poll_period_s)

    def set_backup_options(self, enabled: bool, delay_s: float) -> None:
        new_enabled = bool(enabled)
        new_delay_s = max(1.0, float(delay_s))

        changed = (
            self.backup_enabled != new_enabled or
            abs(self.backup_delay_s - new_delay_s) > 1e-9
        )

        self.backup_enabled = new_enabled
        self.backup_delay_s = new_delay_s

        if changed:
            state = "ativado" if new_enabled else "desativado"
            self.log_info(f"Backup {state}; delay={self.backup_delay_s:.1f}s.")

    def set_oxygen_calibration(self, a: float, b: float) -> None:
        cfg = self._parser._cfg

        changed = (
            abs(cfg.oxy_cal_a - a) > 1e-12 or
            abs(cfg.oxy_cal_b - b) > 1e-12
        )

        if not changed:
            return

        new_cfg = ParserConfig(
            oxy_cal_a=a, oxy_cal_b=b,
            ph_slope=cfg.ph_slope, ph_intercept=cfg.ph_intercept,
            ph_filter=cfg.ph_filter, oxy_filter=cfg.oxy_filter,
        )
        self._parser.update_config(new_cfg)
        self.log_info(f"Calibração O₂: a={a}, b={b}")

    def set_pH_calibration(self, slope: float, intercept: float) -> None:
        cfg = self._parser._cfg

        changed = (
            abs(cfg.ph_slope - slope) > 1e-12 or
            abs(cfg.ph_intercept - intercept) > 1e-12
        )

        if not changed:
            return

        new_cfg = ParserConfig(
            oxy_cal_a=cfg.oxy_cal_a, oxy_cal_b=cfg.oxy_cal_b,
            ph_slope=slope, ph_intercept=intercept,
            ph_filter=cfg.ph_filter, oxy_filter=cfg.oxy_filter,
        )
        self._parser.update_config(new_cfg)
        self.log_info(f"Calibração pH: slope={slope}, intercept={intercept}")

    def reload_filter_settings(self, prefs: dict | None = None) -> None:
        old_cfg = self._parser._cfg
        new_cfg = self._build_parser_config(prefs)

        changed = (
            abs(old_cfg.oxy_cal_a - new_cfg.oxy_cal_a) > 1e-12 or
            abs(old_cfg.oxy_cal_b - new_cfg.oxy_cal_b) > 1e-12 or
            abs(old_cfg.ph_slope - new_cfg.ph_slope) > 1e-12 or
            abs(old_cfg.ph_intercept - new_cfg.ph_intercept) > 1e-12 or
            old_cfg.ph_filter.abs_threshold != new_cfg.ph_filter.abs_threshold or
            old_cfg.ph_filter.follow_tolerance != new_cfg.ph_filter.follow_tolerance or
            old_cfg.ph_filter.confirm_runs != new_cfg.ph_filter.confirm_runs or
            old_cfg.oxy_filter.abs_threshold != new_cfg.oxy_filter.abs_threshold or
            old_cfg.oxy_filter.follow_tolerance != new_cfg.oxy_filter.follow_tolerance or
            old_cfg.oxy_filter.confirm_runs != new_cfg.oxy_filter.confirm_runs
        )

        if not changed:
            return

        self._parser.update_config(new_cfg)

    # --- Command sending ---

    def send_command(self, cmd: Dict[str, Any]) -> None:
        """
        Buffer *cmd* for the next flush cycle.
        Calls from any thread are safe (protected by _cmd_lock).
        """
        with self._cmd_lock:
            self._cmd_buffer.update(cmd)

    # --- Data acquisition (called from QTimer in main thread) ---
    def read_and_parse(self) -> bool:
        if self._state is not ConnectionState.CONNECTED:
            return False

        self._flush_commands()

        with self._transport_lock:
            transport = self._transport
        if transport is None:
            return False

        raw = transport.read()
        if not raw:
            if self._medium == "USB":
                self._io_fail_streak += 1
                if self._io_fail_streak >= 4:
                    self._post_request(_LinkLostRequest("sem dados USB"))
            return False

        raw = raw.strip()

        # 1) Linhas de log do ESP32: registrar e ignorar sem contar como falha
        raw = raw.strip()

        # 1) Linhas de log do ESP32: registrar e ignorar sem contar como falha
        if raw.startswith("[ESP32_"):
            self._log(raw)
            return False

        # 2) Mantém visibilidade do tráfego útil com timestamp
        self._log_rx(raw)

        ok = self._parser.parse(raw)
        if not ok:
            if self._medium == "USB":
                self._parse_fail_streak += 1
                if self._parse_fail_streak >= 3:
                    self._post_request(_LinkLostRequest("falhas consecutivas de parse"))
            return False

        self._io_fail_streak = 0
        self._parse_fail_streak = 0
        self._good_rx_timestamp = time.monotonic()

        self._check_sensor_hardware_status()
        self.sensor_data_updated.emit()
        return True

    # =================================================================
    # Worker thread
    # =================================================================

    def _post_request(self, req: _Request) -> None:
        self._request_queue.put(req)
        self._queue_event.set()

    def _drain_requests(self) -> list[_Request]:
        out = []
        while True:
            try:
                out.append(self._request_queue.get_nowait())
            except Empty:
                return out

    def _worker_loop(self) -> None:
        """
        Single infinite loop.  All state transitions happen here.
        The only role of the main thread is to POST requests via _post_request().
        """
        while not self._shutdown_event.is_set():
            # Wait for a request or heartbeat timeout
            woke = self._queue_event.wait(timeout=self._HEARTBEAT_INTERVAL_S)

            if self._shutdown_event.is_set():
                break

            # Drain the request queue
            requests_now: list[_Request] = []
            if woke:
                self._queue_event.clear()
                requests_now = self._drain_requests()

            for req in requests_now:
                if isinstance(req, _ShutdownRequest):
                    return
                elif isinstance(req, _DisconnectRequest):
                    self._handle_disconnect()
                elif isinstance(req, _ConnectRequest):
                    self._handle_connect(req)
                elif isinstance(req, _LinkLostRequest):
                    if self._state is ConnectionState.CONNECTED:
                        self.log_aviso(f"{self._medium}: link perdido ({req.reason}). Tentando recuperação...")
                        self._teardown_transport()
                        if self.backup_enabled:
                            self._transition(ConnectionState.RECONNECTING, self._medium)
                            self._run_reconnect_cycle()
                        else:
                            self.trigger_alarm(f"Conexão {self._medium} perdida")
                            self._transition(ConnectionState.ERROR, self._medium)

            # Periodic heartbeat when connected
            if self._state is ConnectionState.CONNECTED:
                self._run_heartbeat()

        self.log_evt("Worker encerrado.")

    # ------------------------------------------------------------------
    def _handle_connect(self, req: _ConnectRequest) -> None:
        self._connect_cancel_event.clear()
        self._teardown_transport()

        medium = req.medium
        self._attempts[medium] = self._attempts.get(medium, 0) + 1
        n = self._attempts[medium]
        self.log_evt(f"{medium}: tentativa #{n} de conexão")
        self._transition(ConnectionState.CONNECTING, medium)

        transport = self._build_transport(req)
        if transport is None:
            self.log_aviso(f"{medium}: configuração inválida")
            self._on_connect_failed(medium)
            return

        success = transport.connect()

        if self._connect_cancel_event.is_set():
            self.log_evt(f"{medium}: tentativa de conexão cancelada pelo usuário")
            self._handle_disconnect()
            return

        if success:
            with self._transport_lock:
                self._transport = transport
            self._transition(ConnectionState.CONNECTED, medium)
            self.log_evt(f"{medium}: conectado com sucesso")
            self._same_time_count = 0
            self._last_time_value = None
            self._io_fail_streak = 0
            self._parse_fail_streak = 0
            self._good_rx_timestamp = time.monotonic()
            self._usb_same_port_failures = 0
            self.stop_alarm()
        else:
            try:
                transport.disconnect()
            except Exception:
                pass
            self.log_aviso(f"{medium}: handshake falhou")
            self._on_connect_failed(medium)

    def _handle_disconnect(self) -> None:
        """Worker-thread: tear everything down, go to DISCONNECTED."""
        medium = self._medium
        self._teardown_transport()
        self.stop_alarm()
        self._transition(ConnectionState.DISCONNECTED, medium)
        self.log_evt("Desconectado a pedido do usuário")

    def _on_connect_failed(self, medium: str) -> None:
        """Called inside worker after a failed handshake attempt."""
        if self.backup_enabled:
            self._transition(ConnectionState.RECONNECTING, medium)
            self._run_reconnect_cycle()
        else:
            self.trigger_alarm(f"Falha persistente na conexão {medium}")
            self._transition(ConnectionState.ERROR, medium)
            self.log_erro(f"{medium}: backup desativado; estado ERROR")

    def _run_heartbeat(self) -> None:
        with self._transport_lock:
            transport = self._transport
        if transport is None:
            return

        alive = transport.test_connection()
        if not alive:
            self.log_aviso(f"{self._medium}: link perdido; tentando recuperação")
            self._teardown_transport()
            
            if self.backup_enabled:
                self._transition(ConnectionState.RECONNECTING, self._medium)
                self._run_reconnect_cycle()
            else:
                # Só dispara o alarme imediatamente se não houver backup configurado
                self.trigger_alarm(f"Conexão {self._medium} perdida")
                self._transition(ConnectionState.ERROR, self._medium)

    def _run_reconnect_cycle(self) -> None:
        """
        Blocking reconnect loop executed entirely inside the worker thread.
        Exits when connected, when the user posts a _DisconnectRequest,
        or when the shutdown event fires.
        """
        self.log_evt("iniciando ciclo de reconexão (backup)")

        while (not self._shutdown_event.is_set()
            and self._state is ConnectionState.RECONNECTING):

            pending = self._drain_requests()
            for req in pending:
                if isinstance(req, (_DisconnectRequest, _ShutdownRequest)):
                    self._handle_disconnect()
                    return
                elif isinstance(req, _ConnectRequest):
                    self._handle_connect(req)
                    return

            medium = self._next_medium()
            if medium is None:
                self.log_info("Backup: nenhum meio disponível; aguardando")
                self._queue_event.wait(timeout=self.backup_delay_s)
                self._queue_event.clear()
                continue

            self.log_evt(f"Backup: tentando {medium}")
            self._attempts[medium] = self._attempts.get(medium, 0) + 1
            n = self._attempts[medium]
            self.log_evt(f"{medium}: tentativa #{n} (backup)")

            cfg_req = _ConnectRequest(
                medium,
                usb_cfg=self._usb_cfg,
                wifi_cfg=self._wifi_cfg,
            )
            transport = self._build_transport(cfg_req)

            if transport is None:
                self.log_aviso(f"Backup {medium}: sem configuração. Pulando.")
            else:
                success = transport.connect()
                if success:
                    with self._transport_lock:
                        self._transport = transport
                    self._transition(ConnectionState.CONNECTED, medium)
                    self.log_evt(f"Backup: reconectado via {medium}")
                    self.stop_alarm()
                    self._same_time_count = 0
                    self._last_time_value = None
                    self._io_fail_streak = 0
                    self._parse_fail_streak = 0
                    self._good_rx_timestamp = time.monotonic()
                    self._usb_same_port_failures = 0
                    return
                else:
                    try:
                        transport.disconnect()
                    except Exception:
                        pass

                    if medium == "USB":
                        self._usb_same_port_failures += 1
                        self.log_info(
                            f"USB: falha consecutiva na mesma porta ({self._usb_cfg.port if self._usb_cfg else 'desconhecida'}) = {self._usb_same_port_failures}"
                        )

                    # Only start scanning other ports after a few failures
                    if medium == "USB" and self._usb_cfg and self._usb_same_port_failures >= 3:
                        self.log_evt(
                            f"USB: iniciando reprobe após {self._usb_same_port_failures} falhas na porta {self._usb_cfg.port}"
                        )

                        if hasattr(USBTransport, "describe_ports"):
                            self.log_info(f"USB: portas visíveis = {USBTransport.describe_ports()}")

                        new_port = USBTransport.probe_ports(self._usb_cfg)

                        if new_port:
                            self.log_evt(f"USB: porta alternativa válida encontrada: {new_port}")
                        else:
                            self.log_aviso("USB: nenhuma porta alternativa válida encontrada nesta rodada")

                        if new_port and new_port != self._usb_cfg.port:
                            self.log_evt(f"USB: migrando da porta {self._usb_cfg.port} para {new_port}")
                            self._usb_cfg = replace(self._usb_cfg, port=new_port)
                            self._usb_same_port_failures = 0

            self.log_info(f"Backup: próxima tentativa em {self.backup_delay_s:.1f}s")
            self._queue_event.wait(timeout=self.backup_delay_s)
            self._queue_event.clear()

    # =================================================================
    # State machine helpers
    # =================================================================

    def _transition(self, new_state: ConnectionState, medium: str = "") -> None:
        with self._state_lock:
            if self._state == new_state and self._medium == medium:
                return
            self._state = new_state
            if medium:
                self._medium = medium

        self.log_evt(f"estado: {new_state.name} [{medium or self._medium}]")
        self.connection_state_changed.emit(new_state, medium or self._medium)

    @property
    def _state(self) -> ConnectionState:
        return self.__state

    @_state.setter
    def _state(self, v: ConnectionState) -> None:
        self.__state = v

    # =================================================================
    # Transport helpers
    # =================================================================

    def _build_transport(self, req: _ConnectRequest) -> Optional[TransportLayer]:
        if req.medium == "USB" and req.usb_cfg:
            return USBTransport(req.usb_cfg)
        if req.medium == "WiFi" and req.wifi_cfg:
            return WiFiTransport(req.wifi_cfg)
        return None

    def _teardown_transport(self) -> None:
        with self._transport_lock:
            t, self._transport = self._transport, None
        if t is not None:
            try:
                t.disconnect()
            except Exception:
                pass

    def _next_medium(self) -> Optional[str]:
        """Cycle through available media for backup."""
        available: List[str] = []
        if self._wifi_cfg and self._wifi_cfg.ip:
            available.append("WiFi")
        if self._usb_cfg and self._usb_cfg.port:
            available.append("USB")
        if not available:
            return None
        self._backup_index = (self._backup_index + 1) % len(available)
        return available[self._backup_index]

    # =================================================================
    # Command buffer
    # =================================================================

    def _flush_commands(self) -> None:
        with self._cmd_lock:
            if not self._cmd_buffer:
                return
            payload = self._cmd_buffer.copy()
            self._cmd_buffer.clear()

        try:
            data = json.dumps(payload, separators=(",", ":"))
        except Exception as exc:
            self.log_erro(f"Erro ao serializar comando: {exc}")
            with self._cmd_lock:
                replay = self._cmd_buffer.copy()
                self._cmd_buffer = payload | replay
            return

        with self._transport_lock:
            transport = self._transport

        if transport is None:
            with self._cmd_lock:
                replay = self._cmd_buffer.copy()
                self._cmd_buffer = payload | replay
            return

        ok = transport.write(data)
        if ok:
            if "pHCal" in payload:
                try:
                    self._parser.mark_ph_sent(float(payload["pHCal"]))
                except Exception:
                    pass
            return

        self.log_aviso(f"{self._medium}: falha ao enviar comando")
        with self._cmd_lock:
            replay = self._cmd_buffer.copy()
            self._cmd_buffer = payload | replay
        self._post_request(_LinkLostRequest("falha de escrita"))

    def _on_ph_cal_update(self, calibrated_ph: float) -> None:
        """Called by DataParser when a new calibrated pH is ready to be transmitted."""
        self.send_command({"pHCal": format(calibrated_ph, ".2f")})

    # =================================================================
    # Stale-data / ESP32 power-down detection
    # =================================================================

    def _check_sensor_hardware_status(self) -> None:
        """
        Monitora o status do módulo de sensores (UART interna do ESP32).
        
        IMPORTANTE: este evento é INDEPENDENTE da conexão USB/WiFi com o PC.
        Quando o módulo de sensores cai, a USB continua ativa — não se deve
        fechar a porta serial. O ESP32 detecta o retorno do módulo
        automaticamente via syncAllSensorSettings() e volta a enviar
        SensorCommOK: true.
        """
        sensor_ok = bool(getattr(self.readings, 'sensor_comm_ok', True))

        if not sensor_ok and self._sensor_module_online:
            # Transição online → offline: aciona alarme, mantém USB aberta
            self._sensor_module_online = False
            self.log_aviso("Módulo TECNAL offline: falha de comunicação UART no ESP32")
            self.trigger_alarm("Falha no SensorModule")

        elif sensor_ok and not self._sensor_module_online:
            # Transição offline → online: módulo voltou, cancela alarme
            self._sensor_module_online = True
            self.log_evt("Módulo TECNAL voltou online; alarme cancelado")
            self.stop_alarm()

    # =================================================================
    # Parser config builder (reads preferences)
    # =================================================================

    def _build_parser_config(self, prefs: dict | None = None) -> ParserConfig:
        try:
            prefs = prefs or load_preferences() or default_preferences()
            cfg_prefs = prefs.get("Configurations", {})
            fil_prefs = prefs.get("Filters", {})

            return ParserConfig(
                oxy_cal_a=float(cfg_prefs.get("oxy_cal_a", "0.030573419314")),
                oxy_cal_b=float(cfg_prefs.get("oxy_cal_b", "-25.09036520919")),
                ph_slope=float(cfg_prefs.get("ph_cal_slope", "1.0")),
                ph_intercept=float(cfg_prefs.get("ph_cal_intercept", "0.0")),
                ph_filter=SpikeFilterConfig(
                    abs_threshold=float(fil_prefs.get("spike_abs_threshold_ph", "500.0")),
                    follow_tolerance=float(fil_prefs.get("follow_tolerance_ph", "200.0")),
                    confirm_runs=int(fil_prefs.get("spike_confirm_runs", "3")),
                ),
                oxy_filter=SpikeFilterConfig(
                    abs_threshold=float(fil_prefs.get("spike_abs_threshold_oxy", "150.0")),
                    follow_tolerance=float(fil_prefs.get("follow_tolerance_oxy", "50.0")),
                    confirm_runs=int(fil_prefs.get("spike_confirm_runs", "3")),
                ),
            )
        except Exception:
            return ParserConfig()

    # =================================================================
    # Logging
    # =================================================================

    def _emit_log(self, tipo: str, msg: str) -> None:
        texto = (msg or "").strip()

        # Se já veio padronizado do ESP32 ou do próprio Python, não encapsula de novo
        if texto.startswith("[ESP32_") or texto.startswith("[PY_"):
            line = texto
        else:
            tipo = (tipo or "INFO").upper()
            if tipo not in {"INFO", "EVT", "AVISO", "ERRO"}:
                tipo = "INFO"
            line = f"[PY_{tipo}]: {texto}"

        try:
            self.log_message.emit(line)
        except Exception:
            print(line, flush=True)

    def log_info(self, msg: str) -> None:
        self._emit_log("INFO", msg)

    def log_evt(self, msg: str) -> None:
        self._emit_log("EVT", msg)

    def log_aviso(self, msg: str) -> None:
        self._emit_log("AVISO", msg)

    def log_erro(self, msg: str) -> None:
        self._emit_log("ERRO", msg)

    def _log_rx(self, raw: str) -> None:
        ts = time.strftime("%H:%M:%S")
        line = f"[{ts}] Rx: {raw}"
        try:
            self.log_message.emit(line)
        except Exception:
            print(line, flush=True)

    # Compatibilidade com chamadas antigas
    def _log(self, msg: str) -> None:
        self.log_info(msg)

    # =================================================================
    # Alarm
    # =================================================================

    def trigger_alarm(self, msg: str) -> None:
        if self.alarm_mute_flag.is_set() or msg == self._last_alarm_msg:
            return
        self._last_alarm_msg = msg
        self.log_aviso(f"Alarme: {msg}")
        self.alarm_triggered.emit(msg)
        if not self._alarm_playing:
            self._alarm_playing = True
            self._alarm_thread = _threading.Thread(
                target=self._alarm_loop, daemon=True
            )
            self._alarm_thread.start()

    def stop_alarm(self) -> None:
        self._last_alarm_msg = None
        self._alarm_playing = False

    def _alarm_loop(self) -> None:
        """Toca o alarme em loop em uma thread separada (sem Qt Multimedia)."""
        try:
            if _platform.system() == "Windows":
                import winsound
                while self._alarm_playing:
                    if os.path.isfile(self._alarm_wav_path):
                        winsound.PlaySound(
                            self._alarm_wav_path,
                            winsound.SND_FILENAME | winsound.SND_NODEFAULT
                        )
                    else:
                        # Fallback: beep do sistema se .wav não for encontrado
                        winsound.MessageBeep(winsound.MB_ICONEXCLAMATION)
                        import time
                        time.sleep(1.5)
            else:
                # Linux/macOS: usa subprocess com aplay ou afplay
                import subprocess, time
                while self._alarm_playing:
                    if os.path.isfile(self._alarm_wav_path):
                        cmd = (
                            ["aplay", self._alarm_wav_path]
                            if _platform.system() == "Linux"
                            else ["afplay", self._alarm_wav_path]
                        )
                        try:
                            subprocess.run(cmd, timeout=5, capture_output=True)
                        except Exception:
                            time.sleep(1.5)
                    else:
                        time.sleep(1.5)
        except Exception as e:
            self.log_erro(f"Falha no loop de alarme: {e}")

    # =================================================================
    # Backward-compatibility shims
    # (keep old attribute names alive so existing UI code doesn't break)
    # =================================================================

    @property
    def connection_status(self) -> str:
        _MAP = {
            ConnectionState.DISCONNECTED: "Desconectado",
            ConnectionState.CONNECTING:   "Conectando…",
            ConnectionState.CONNECTED:    "Ok",
            ConnectionState.RECONNECTING: "Reconectando…",
            ConnectionState.ERROR:        "Erro",
        }
        return _MAP.get(self._state, "Desconectado")

    # Sensor value shims – read from parser.readings
    @property
    def tempReadVal(self) -> float:        return self.readings.temperature
    @property
    def oxyReadVal(self) -> float:         return self.readings.oxygen_cal
    @property
    def phReadVal(self) -> float:          return self.readings.ph_cal
    @property
    def phRead(self) -> float:             return self.readings.ph_raw
    @property
    def pressureReadVal(self) -> float:    return self.readings.pressure
    @property
    def flowReadVal(self) -> float:        return self.readings.flow_rate
    @property
    def distanceReadVal(self) -> float:    return self.readings.distance
    @property
    def antifoamReadVal(self) -> float:    return self.readings.antifoam
    @property
    def biomassAbsorbance(self) -> float:  return self.readings.biomass_abs
    @property
    def biomassRaw(self) -> int:           return self.readings.biomass_raw
    @property
    def biomassItMs(self) -> int:          return self.readings.biomass_it_ms
    @property
    def biomassPwmPct(self) -> float:      return self.readings.biomass_pwm_pct
    @property
    def biomassHdMode(self) -> bool:       return self.readings.biomass_hd_mode
    @property
    def pumpFlowReadVal(self) -> float:    return self.readings.pump_flow
    @property
    def pumpVolReadVal(self) -> float:     return self.readings.pump_vol
    @property
    def timeReadVal(self) -> float:        return self.readings.time_min
    @property
    def FlowVoltage(self) -> float:        return self.readings.flow_voltage

    @property
    def timeZeroOffset(self) -> float:
        return self.readings.time_offset_min

    @timeZeroOffset.setter
    def timeZeroOffset(self, v: float) -> None:
        self.readings.time_offset_min = v

    # Mute shim (old UI connects checkbox to this slot)
    def _mute_changed(self, state: int) -> None:
        from PySide6.QtCore import Qt
        if state == Qt.CheckState.Checked:
            self.alarm_mute_flag.set()
        else:
            self.alarm_mute_flag.clear()

    # read_and_parse_data shim
    def read_and_parse_data(self) -> None:
        self.read_and_parse()