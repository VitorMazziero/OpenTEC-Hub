"""In-process firmware v5.0 simulator for the interactive desktop demo mode."""

from __future__ import annotations

import math
import struct
import time

from biomass_core import Transport


def _f32(x: float) -> float:
    """Rounds to IEEE single precision, the way the firmware's floats behave.

    Not pedantry: 1.20f * 800.0f is 960.00006 in float and truncates to 960,
    while the same product in Python's double is 959.9999999999999 and
    truncates to 959. That is a 25 ms disagreement in the derived floor, and
    the demo exists partly so the GUI can be tested against numbers the real
    device would actually send.
    """
    return struct.unpack("f", struct.pack("f", x))[0]


class DemoTransport(Transport):
    """A deterministic sensor simulation that never accesses USB or the network."""

    name = "demo"
    HISTORY_CAPACITY = 1024
    HISTORY_PAGE = 60
    IT_OPTIONS = [25, 50, 100, 200, 400, 800]

    # --- mirrors of the firmware's timing constants (v5.0) ---------------
    LED_DUTY_LIMIT = 0.08
    IT_PERIOD_GUARD = 1.20
    LED_SETTLE_MS = 10
    INTEGRATION_MARGIN_MS = 8
    #: What the sensor's conversion period really is, as a multiple of its
    #: nominal label. Measured on the hardware at 1.092; the simulation uses
    #: it so probe_period returns something with the shape of real data.
    REAL_PERIOD_RATIO = 1.092
    DEFAULT_REFRESH_MS = 25000

    def __init__(self):
        self._opened = False
        self._boot_clock = time.monotonic()
        self._last_sample_clock = self._boot_clock
        self.boot_id = 1
        self.seq = 0
        self.t_ms = 0
        self.state = "idle"
        self.blank_done = True
        # A sweep stamps the table. The app tells a finished sweep from an
        # aborted one by watching this change, so a demo that never moved it
        # would report every blank as aborted.
        self.blank_stamp = 1
        self.blank_duty_pct = -1.0
        self.blank_sweep_ms = 0
        self.auto_range = True
        self.it_table = [100, 200, 400, 800]
        # The geometric ladder v4.2 made the default: a constant ~1.75x light
        # ratio per step, so every gear change is a uniform ~0.24 AU jump.
        self.pwm_table = [2.0, 3.5, 6.0, 10.5, 18.0, 32.0, 57.0, 100.0]
        self.it_index = 2
        self.pwm_index = 4
        self.refresh_ms = self.DEFAULT_REFRESH_MS
        self.low, self.opt, self.high = 10000, 25000, 40000
        self.ema = 0.8
        self.hub_enabled = False
        self.led_duty = 0.0
        self.led_test = False
        self.test_period = 50
        self.i2c_errors = 0
        self.saturation_events = 0
        self.sensor_resets = 0
        self.failed_searches = 0
        self.boundary_misses = 0
        self._log: list[str] = []
        self._history: list[dict] = []
        for _ in range(120):
            self._append_sample()

    # -- timing, mirroring integrationGuardMs()/ledOnMsFor()/minSafeRefreshMs()
    def _guard_ms(self, it_ms: int) -> int:
        return int(_f32(_f32(self.IT_PERIOD_GUARD) * _f32(it_ms))) + \
            self.INTEGRATION_MARGIN_MS

    def _led_on_ms(self, it_ms: int) -> int:
        return self.LED_SETTLE_MS + 2 * self._guard_ms(it_ms)

    def min_refresh_ms(self) -> int:
        """Auto-ranging must assume the longest IT it could pick; a manual
        lock only has to cover the IT actually in use."""
        it_ms = max(self.it_table) if self.auto_range \
            else self.it_table[self.it_index]
        return int(_f32(self._led_on_ms(it_ms) / _f32(self.LED_DUTY_LIMIT)))

    def open(self) -> None:
        self._opened = True
        self._last_sample_clock = time.monotonic()

    def close(self) -> None:
        self._advance()
        self._opened = False

    def _i0(self) -> int:
        value = (self.it_index + 1) * (self.pwm_index + 1) * 2500
        return min(value, 65000)

    def _append_sample(self, *, single: bool = False) -> None:
        self.seq += 1
        self.t_ms += self.refresh_ms
        absorbance = min(2.4, 0.05 * math.exp(self.seq / 50.0))
        i0 = self._i0()
        raw = max(1, int(i0 * (10.0 ** -absorbance)))
        self._history.append({
            "seq": self.seq,
            "t_ms": self.t_ms,
            "boot_id": self.boot_id,
            "absorbance": round(absorbance, 4),
            "raw": raw,
            "i0": i0,
            "it_ms": self.it_table[self.it_index],
            "pwm_pct": self.pwm_table[self.pwm_index],
            "hd_mode": False,
            "sat": False,
            "single": single,
            "manual": not self.auto_range,
        })
        if len(self._history) > self.HISTORY_CAPACITY:
            del self._history[:-self.HISTORY_CAPACITY]

    def _advance(self) -> None:
        now = time.monotonic()
        if not self._opened or self.state != "measuring":
            self._last_sample_clock = now
            return
        elapsed_ms = int((now - self._last_sample_clock) * 1000)
        count = min(100, elapsed_ms // self.refresh_ms)
        for _ in range(count):
            self._append_sample()
        if count:
            self._last_sample_clock += count * self.refresh_ms / 1000.0

    def get_status(self) -> dict:
        self._advance()
        return {
            "fw": "5.2", "name": "DEMO", "simulated": True,
            "boot_id": self.boot_id,
            "uptime_ms": self.t_ms + int((time.monotonic() - self._boot_clock) * 1000),
            "state": self.state, "seq": self.seq, "blank_done": self.blank_done,
            "hd_mode": False, "refresh_ms": self.refresh_ms,
            "low": self.low, "high": self.high, "opt": self.opt,
            "hub_enabled": self.hub_enabled, "hub_connected": False,
            "hub_ssid": "", "ap_ip": "192.168.7.1", "ap_clients": 0,
            "sta_ip": "", "i2c_errors": self.i2c_errors,
            "saturation_events": self.saturation_events,
            "sensor_resets": self.sensor_resets,
            "failed_searches": self.failed_searches,
            "hist_size": self.HISTORY_CAPACITY,
            "hist_stored": len(self._history), "free_heap": 220000,
            "auto_range": self.auto_range, "it_index": self.it_index,
            "pwm_index": self.pwm_index,
            "it_ms": self.it_table[self.it_index],
            "pwm_pct": self.pwm_table[self.pwm_index],
            "led_duty": self.led_duty, "manual_led": self.led_duty > 0,
            "led_test": self.led_test, "test_period": self.test_period,
            "ema": self.ema, "it_table": list(self.it_table),
            "pwm_table": list(self.pwm_table),
            # v5.0 additions the GUI binds to.
            "min_refresh_ms": self.min_refresh_ms(),
            "led_duty_limit": self.LED_DUTY_LIMIT,
            "boundary_misses": self.boundary_misses,
            # A plausible warm-board figure. The real one is the ESP32 die
            # sensor, which sits well above ambient.
            "soc_temp_c": 54.0,
        }

    def drain_log(self) -> list[str]:
        out, self._log = self._log, []
        return out

    def _probe_period(self) -> None:
        """Emits one result line per IT slot, shaped like the firmware's.

        The real device times two successive conversion boundaries and finds
        the period runs ~9% longer than its nominal label. Reproduced here so
        the GUI's diagnostic path can be exercised without hardware.
        """
        import json
        for i, nominal in enumerate(self.it_table):
            period = nominal * self.REAL_PERIOD_RATIO
            self._log.append(json.dumps({
                "probe": "period", "it_index": i, "nominal_ms": nominal,
                "temp_c": 54.0, "trials": 6, "resolved": 6,
                "period_ms": round(period, 1),
                "min_ms": int(period) - 1, "max_ms": int(period) + 1,
                "ratio": round(self.REAL_PERIOD_RATIO, 3),
                "within_guard": self.REAL_PERIOD_RATIO < self.IT_PERIOD_GUARD,
                "guard": self.IT_PERIOD_GUARD, "poll_ms": 5,
            }, separators=(",", ":")))

    def get_blank_table(self) -> dict:
        rows = []
        for i in range(len(self.it_table)):
            rows.append([min((i + 1) * (j + 1) * 2500, 65535)
                         for j in range(len(self.pwm_table))])
        return {"blank_done": self.blank_done, "timestamp": self.blank_stamp,
                "sweep_duty_pct": self.blank_duty_pct,
                "sweep_ms": self.blank_sweep_ms,
                "it_ms": list(self.it_table), "pwm_pct": list(self.pwm_table),
                "i0": rows}

    def get_history(self, since: int) -> dict:
        self._advance()
        newer = [sample for sample in self._history if sample["seq"] > since]
        batch = newer[:self.HISTORY_PAGE]
        return {
            "boot_id": self.boot_id, "seq": self.seq,
            "first_seq": self._history[0]["seq"] if self._history else 1,
            "samples": batch, "count": len(batch),
            "more": len(newer) > len(batch),
        }

    def send_command(self, payload: dict) -> None:
        self._advance()
        command = payload.get("command", "")
        if command == "start" and self.blank_done:
            self.state = "measuring"
            self._last_sample_clock = time.monotonic()
        elif command == "stop":
            self.state = "idle"
            self.led_test = False
            self.led_duty = 0.0
        elif command == "blank":
            self.state = "idle"
            self.blank_done = True
            self.blank_stamp += 1
            duty = float(payload.get("duty_pct", 0.0))
            self.blank_duty_pct = duty
            # Roughly what the firmware takes: 14.6 s of LED on-time, run
            # back to back or spread out to hold the requested duty.
            self.blank_sweep_ms = int(14600 / (duty / 100.0)) if duty else 24000
        elif command == "clear_history":
            self._history.clear()
        elif command == "read_once" and self.state == "idle":
            self._append_sample(single=True)
        elif command == "auto":
            self.auto_range = True
        elif command == "manual":
            self.auto_range = False
        elif command == "set_gear":
            self.it_index = max(0, min(int(payload.get("it", 0)),
                                       len(self.it_table) - 1))
            self.pwm_index = max(0, min(int(payload.get("pwm", 0)),
                                        len(self.pwm_table) - 1))
        elif command == "led" and self.state == "idle":
            self.led_test = False
            self.led_duty = max(0.0, min(float(payload.get("duty", 0)), 100.0))
        elif command == "led_off":
            self.led_test = False
            self.led_duty = 0.0
        elif command == "test_on" and self.state == "idle":
            self.led_test = True
        elif command == "test_off":
            self.led_test = False
        elif command == "hub_on":
            self.hub_enabled = True
        elif command == "hub_off":
            self.hub_enabled = False
        elif command == "set_pwm":
            index = int(payload.get("index", -1))
            if 0 <= index < len(self.pwm_table):
                self.pwm_table[index] = max(0.0, min(float(payload["value"]), 100.0))
                self.blank_done = False
                self.state = "idle"
        elif command == "set_it":
            index, code = int(payload.get("index", -1)), int(payload.get("code", -1))
            if 0 <= index < len(self.it_table) and 0 <= code < len(self.IT_OPTIONS):
                self.it_table[index] = self.IT_OPTIONS[code]
                self.blank_done = False
                self.state = "idle"
        elif command == "probe_period":
            self._probe_period()
        elif command == "reset_health":
            self.i2c_errors = self.saturation_events = 0
            self.sensor_resets = self.failed_searches = 0
            self.boundary_misses = 0
        elif command == "factory":
            self.low, self.opt, self.high = 10000, 25000, 40000
            self.ema, self.auto_range = 0.8, True
            self.blank_done, self.state = False, "idle"

        if "refresh_ms" in payload:
            # Clamped to the LED thermal floor exactly as the firmware does,
            # so the GUI's interval control can be tested against a device
            # that refuses illegal values instead of one that accepts them.
            want = min(int(payload["refresh_ms"]), 3600000)
            self.refresh_ms = max(self.min_refresh_ms(), want)
            self._last_sample_clock = time.monotonic()
        if "ema" in payload:
            self.ema = max(0.01, min(float(payload["ema"]), 1.0))
        if "test_period" in payload:
            self.test_period = max(5, min(int(payload["test_period"]), 500))
        if "low" in payload:
            self.low = int(payload["low"])
        if "opt" in payload:
            self.opt = int(payload["opt"])
        if "high" in payload:
            self.high = int(payload["high"])
