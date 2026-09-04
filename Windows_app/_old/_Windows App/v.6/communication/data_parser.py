#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
communication/data_parser.py
~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
All sensor-data concerns live here:

  • SpikeFilter   – per-channel spike / step-change filter
  • SensorReadings – frozen snapshot of the last good parsed values
  • DataParser    – JSON → SensorReadings, applying calibration & filters
"""
from __future__ import annotations

import json
import time
from dataclasses import dataclass, field
from typing import Optional, Callable


# ---------------------------------------------------------------------------
# Spike / step-change filter
# ---------------------------------------------------------------------------

@dataclass
class _ChannelState:
    """Mutable internal state for one filtered channel."""
    last_good_raw: Optional[float] = None
    candidate_raw: Optional[float] = None
    candidate_runs: int = 0


@dataclass(frozen=True)
class SpikeFilterConfig:
    """Tunable parameters for a single channel filter."""
    abs_threshold: float    # accept immediately if |new - last_good| <= this
    follow_tolerance: float # candidate window: reset if |new - cand| > this
    confirm_runs: int       # promote candidate after this many consecutive confirmations


class SpikeFilter:
    """
    Stateful, single-channel spike / step filter.

    Usage::

        f = SpikeFilter(SpikeFilterConfig(abs_threshold=500, follow_tolerance=200, confirm_runs=3))
        accepted = f.update(new_raw_value)
    """

    def __init__(self, cfg: SpikeFilterConfig) -> None:
        self._cfg = cfg
        self._state = _ChannelState()

    def update(self, new_raw: Optional[float]) -> Optional[float]:
        """
        Feed a new reading; returns the *accepted* value (last_good or promoted).
        Returns None only if new_raw is None AND no good value exists yet.
        """
        if new_raw is None:
            return self._state.last_good_raw

        s = self._state
        cfg = self._cfg

        # Bootstrap: accept the very first real reading unconditionally
        if s.last_good_raw is None:
            s.last_good_raw = new_raw
            s.candidate_raw = None
            s.candidate_runs = 0
            return new_raw

        # Normal evolution: within abs_threshold → accept and clear candidate
        if abs(new_raw - s.last_good_raw) <= cfg.abs_threshold:
            s.last_good_raw = new_raw
            s.candidate_raw = None
            s.candidate_runs = 0
            return new_raw

        # --- Spike region ---
        cand = s.candidate_raw
        if cand is None or abs(new_raw - cand) > cfg.follow_tolerance:
            # Start / restart candidacy
            s.candidate_raw = new_raw
            s.candidate_runs = 1
            return s.last_good_raw   # hold previous value

        # Existing candidate persists
        s.candidate_runs += 1
        if s.candidate_runs >= cfg.confirm_runs:
            # Promote: real step-change confirmed
            s.last_good_raw = new_raw
            s.candidate_raw = None
            s.candidate_runs = 0
            return new_raw

        return s.last_good_raw   # still holding

    def reset(self) -> None:
        self._state = _ChannelState()

    @property
    def last_good(self) -> Optional[float]:
        return self._state.last_good_raw


# ---------------------------------------------------------------------------
# Sensor readings snapshot
# ---------------------------------------------------------------------------

@dataclass
class SensorReadings:
    """
    Mutable snapshot – updated in-place by DataParser every cycle.
    All values default to -1.0 (sentinel for "not yet received").
    """
    temperature: float = -1.0
    oxygen_raw: float = -1.0      # accepted raw ADC count
    oxygen_cal: float = -1.0      # calibrated mg/L
    ph_raw: float = -1.0          # accepted raw ADC count
    ph_cal: float = -1.0          # calibrated pH in display units
    pressure: float = -1.0
    flow_rate: float = -1.0
    flow_setpoint: float = -1.0
    flow_valve_1: int = -1
    flow_valve_2: int = -1
    flow_valve_main: int = -1
    flowmeter_online: bool = False
    flow_control_enabled: bool = False
    flow_command_pending: bool = False
    flow_command_id: int = 0
    flow_command_ack: int = 0
    flow_command_deliveries: int = 0
    flow_command_age_ms: int = 0
    flow_command_source: str = "unknown"
    hub_stations: int = 0
    distance: float = -1.0
    antifoam: float = -1.0
    biomass_abs: float = -1.0
    biomass_raw: int = 0
    biomass_it_ms: int = 0
    biomass_pwm_pct: float = 0.0
    biomass_hd_mode: bool = False
    sensor_comm_ok: bool = True
    pump_flow: float = -1.0
    pump_vol: float = -1.0
    time_raw_s: float = 0.0       # seconds since controller boot
    time_offset_min: float = 0.0  # user-zeroed offset (minutes)
    flow_voltage: float = 0.0
    _distance_last_seen: float = field(default_factory=time.monotonic, repr=False)

    @property
    def time_min(self) -> float:
        return round(self.time_raw_s / 60.0 - self.time_offset_min, 2)

    def zero_time(self) -> None:
        self.time_offset_min = self.time_raw_s / 60.0


# ---------------------------------------------------------------------------
# DataParser
# ---------------------------------------------------------------------------

@dataclass
class ParserConfig:
    """Calibration coefficients and filter settings used by DataParser."""
    # Oxygen linear calibration: value = a * raw + b
    oxy_cal_a: float = 0.030573419314
    oxy_cal_b: float = -25.09036520919
    # pH linear calibration: ph = slope * raw + intercept
    ph_slope: float = 1.0
    ph_intercept: float = 0.0
    # Spike filter config for each channel
    ph_filter: SpikeFilterConfig = field(
        default_factory=lambda: SpikeFilterConfig(500.0, 200.0, 3)
    )
    oxy_filter: SpikeFilterConfig = field(
        default_factory=lambda: SpikeFilterConfig(150.0, 50.0, 3)
    )
    # Distance timeout: report -1 if key absent for longer than this
    distance_timeout_s: float = 3.0


class DataParser:
    """
    Parses a raw JSON string from the controller into a SensorReadings object.

    Responsibilities:
      • JSON decode (with graceful failure)
      • Spike filtering on O₂ and pH raw channels
      • Linear calibration for O₂ and pH
      • Keeping track of "last valid calibrated pH" for transmission back

    NOT responsible for:
      • Sending commands
      • State transitions
      • Alarms
    """

    def __init__(
        self,
        cfg: ParserConfig,
        on_ph_cal_update: Optional[Callable[[float], None]] = None,
    ) -> None:
        """
        Args:
            cfg: calibration + filter configuration.
            on_ph_cal_update: optional callback invoked when the accepted,
                calibrated pH differs from what was last sent.  The argument
                is the new calibrated pH value.  Caller should buffer a
                ``{"pHCal": value}`` command.
        """
        self._cfg = cfg
        self._on_ph_cal_update = on_ph_cal_update

        self._ph_filter = SpikeFilter(cfg.ph_filter)
        self._oxy_filter = SpikeFilter(cfg.oxy_filter)

        self._ph_last_valid_cal: Optional[float] = None
        self._ph_last_sent_cal: Optional[float] = None

        # Public readings object – mutated in-place each cycle
        self.readings = SensorReadings()

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    def update_config(self, cfg: ParserConfig) -> None:
        """Hot-swap calibration/filter settings (thread-safe: called from Qt thread)."""
        self._cfg = cfg
        self._ph_filter = SpikeFilter(cfg.ph_filter)
        self._oxy_filter = SpikeFilter(cfg.oxy_filter)

    def mark_ph_sent(self, value: float) -> None:
        """
        Called by ConnectionManager after the pHCal command is flushed,
        to prevent redundant re-transmission.
        """
        self._ph_last_sent_cal = value

    def parse(self, raw_json: str) -> bool:
        """
        Parse *raw_json*, update self.readings in-place.

        Returns True if parsing succeeded, False on JSON error.
        """
        try:
            data: dict = json.loads(raw_json)
        except (json.JSONDecodeError, ValueError):
            return False

        r = self.readings
        cfg = self._cfg

        self._parse_temperature(data, r)
        self._parse_oxygen(data, r, cfg)
        self._parse_ph(data, r, cfg)
        self._parse_misc(data, r)
        self._parse_biomass(data, r)
        self._parse_pump(data, r)
        self._parse_time(data, r)

        return True

    # ------------------------------------------------------------------
    # Private parsing helpers
    # ------------------------------------------------------------------

    @staticmethod
    def _safe_float(data: dict, key: str, default: float = -1.0) -> float:
        try:
            return float(data[key])
        except (KeyError, TypeError, ValueError):
            return default

    def _parse_temperature(self, data: dict, r: SensorReadings) -> None:
        val = self._safe_float(data, "Tempval", -1.0)
        if 10.0 < val < 100.0:
            r.temperature = val

    def _parse_oxygen(self, data: dict, r: SensorReadings, cfg: ParserConfig) -> None:
        raw = self._safe_float(data, "Oxyval", 0.0)
        if raw <= 0.1:
            return   # sentinel: sensor absent/not yet valid

        accepted = self._oxy_filter.update(raw)
        if accepted is None:
            return

        r.oxygen_raw = accepted
        a, b = cfg.oxy_cal_a, cfg.oxy_cal_b
        r.oxygen_cal = round(max(a * accepted + b, 0.0), 4)

    def _parse_ph(self, data: dict, r: SensorReadings, cfg: ParserConfig) -> None:
        raw = self._safe_float(data, "pHval", 0.0)
        if raw <= 0.1:
            return

        accepted_raw = self._ph_filter.update(raw)
        if accepted_raw is None:
            return

        r.ph_raw = accepted_raw
        ph_cal = round(cfg.ph_slope * accepted_raw + cfg.ph_intercept, 2)

        if ph_cal < 0.0:
            ph_cal = 0.0

        if 0.0 <= ph_cal < 25_000.0:
            r.ph_cal = ph_cal
            self._ph_last_valid_cal = ph_cal

        # Notify ConnectionManager only when value has changed
        if (self._ph_last_valid_cal is not None
                and self._ph_last_valid_cal != self._ph_last_sent_cal
                and self._on_ph_cal_update is not None):
            self._on_ph_cal_update(self._ph_last_valid_cal)

    def _parse_misc(self, data: dict, r: SensorReadings) -> None:
        r.pressure = self._safe_float(data, "Pressure")
        r.flow_rate = self._safe_float(data, "FlowRate")
        r.flow_setpoint = self._safe_float(data, "FlowSetpoint", r.flow_setpoint)
        r.antifoam = self._safe_float(data, "Antifoam")
        r.flow_voltage = self._safe_float(data, "FlowVoltage", 0.0)
        r.sensor_comm_ok = data.get("SensorCommOK", True)

        r.flowmeter_online = bool(data.get("FlowmeterOnline", r.flowmeter_online))
        r.flow_control_enabled = bool(data.get("FlowControlEnabled", r.flow_control_enabled))
        r.flow_command_pending = bool(data.get("FlowCommandPending", False))
        r.flow_command_source = str(data.get("FlowCommandSource", r.flow_command_source))
        for key, attr in (
            ("Valve1", "flow_valve_1"),
            ("Valve2", "flow_valve_2"),
            ("ValveFlow", "flow_valve_main"),
            ("FlowCommandId", "flow_command_id"),
            ("FlowCommandAck", "flow_command_ack"),
            ("FlowCommandDeliveries", "flow_command_deliveries"),
            ("FlowCommandAgeMs", "flow_command_age_ms"),
            ("HubStations", "hub_stations"),
        ):
            if key in data:
                try:
                    setattr(r, attr, int(data[key]))
                except (TypeError, ValueError):
                    pass

        now = time.monotonic()
        if "Distance" in data:
            val = self._safe_float(data, "Distance", -1.0)
            if 0.0 <= val < 1_000.0:
                r.distance = val
                r._distance_last_seen = now
        else:
            if now - r._distance_last_seen > self._cfg.distance_timeout_s:
                r.distance = -1.0

    def _parse_biomass(self, data: dict, r: SensorReadings) -> None:
        if "BiomassAbs" in data:
            r.biomass_abs = self._safe_float(data, "BiomassAbs", -1.0)
        if "BiomassRaw" in data:
            try:
                r.biomass_raw = int(data["BiomassRaw"]) 
            except (TypeError, ValueError):
                pass
        if "BiomassIT" in data:
            try:
                r.biomass_it_ms = int(data["BiomassIT"])  
            except (TypeError, ValueError):
                pass
        if "BiomassPWM" in data:
            r.biomass_pwm_pct = self._safe_float(data, "BiomassPWM", 0.0)  

    def _parse_pump(self, data: dict, r: SensorReadings) -> None:
        if "PumpFlow" in data:
            r.pump_flow = self._safe_float(data, "PumpFlow", -1.0)
        if "PumpVol" in data:
            r.pump_vol = self._safe_float(data, "PumpVol", -1.0)

    def _parse_time(self, data: dict, r: SensorReadings) -> None:
        val = self._safe_float(data, "Time", 0.0)
        r.time_raw_s = val
