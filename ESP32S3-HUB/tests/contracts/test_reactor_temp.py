#!/usr/bin/env python3
"""H02 contract checks for the reactor temperature PV."""
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2] / "ESP32S3-HUB"


class ReactorPvModel:
    def __init__(self):
        self.valid = False
        self.value = None
        self.updated_ms = 0
        self.data_delay_ms = 1000

    @property
    def timeout_ms(self):
        return min(60000, max(5000, 3 * max(100, self.data_delay_ms)))

    def accept(self, value, now_ms):
        if isinstance(value, (int, float)) and value == value and 0 <= value <= 100:
            self.value = value
            self.valid = True
            self.updated_ms = now_ms
        else:
            self.value = None
            self.valid = False

    def fresh(self, now_ms):
        # uint32 arithmetic, as on the ESP32; a stamp later than now has age zero.
        diff = (now_ms - self.updated_ms) & 0xFFFFFFFF
        age = 0 if diff >= 0x80000000 else diff
        return self.valid and self.updated_ms > 0 and age <= self.timeout_ms


class ReactorTemperatureTests(unittest.TestCase):
    def test_valid_sample_is_fresh_and_invalid_does_not_refresh_timestamp(self):
        pv = ReactorPvModel()
        pv.accept(30.5, 1000)
        self.assertTrue(pv.fresh(5999))
        pv.accept(float("nan"), 5000)
        self.assertFalse(pv.fresh(6000))
        self.assertEqual(1000, pv.updated_ms)

    def test_sample_stamped_after_the_loop_read_now_is_fresh(self):
        # 2026-10-09: the loop reads now, then the sensor read stamps the PV a few hundred ms
        # later; unsigned now - stamp wrapped, the cascade paused on every read and its slope
        # window never filled, so the bath PI never engaged.
        pv = ReactorPvModel()
        pv.accept(31.5, 10_300)
        self.assertTrue(pv.fresh(10_000))

    def test_freshness_checks_use_the_wrap_safe_age(self):
        app = (ROOT / "src/core/AppContext.h").read_text(encoding="utf-8")
        runtime = (ROOT / "src/core/Runtime.h").read_text(encoding="utf-8")
        self.assertIn("elapsedSinceMs(nowMs, reactorTempPvUpdatedMs)", app)
        self.assertIn("elapsedSinceMs(now, bathLastUpdateSnapshot)", runtime)

    def test_timeout_is_three_delays_with_floor_and_ceiling(self):
        pv = ReactorPvModel()
        self.assertEqual(5000, pv.timeout_ms)
        pv.data_delay_ms = 3000
        self.assertEqual(9000, pv.timeout_ms)
        pv.data_delay_ms = 60000
        self.assertEqual(60000, pv.timeout_ms)

    def test_source_uses_uart_pv_and_explicit_validity(self):
        telemetry = (ROOT / "src/sensor/Telemetry.h").read_text(encoding="utf-8")
        app = (ROOT / "src/core/AppContext.h").read_text(encoding="utf-8")
        self.assertIn('sendSensorCommand("b", true)', telemetry)
        self.assertIn("parseFiniteFloat(temperatureResp, temperatureVal)", telemetry)
        self.assertIn("reactorTempPvUpdatedMs", telemetry)
        self.assertIn("reactorTempPvFresh", app)
        self.assertIn('\\"TempvalValid\\"', telemetry)
        self.assertIn('\\"TempvalAgeMs\\"', telemetry)


if __name__ == "__main__":
    unittest.main(verbosity=2)
