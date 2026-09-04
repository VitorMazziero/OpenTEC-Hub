#!/usr/bin/env python3
import math
import unittest


class ServoPresenceModel:
    TIMEOUT_MS = 6000

    def __init__(self, enabled=True):
        self.enabled = enabled
        self.last_presence = None
        self.sample = None

    def push(self, now_ms, **sample):
        required = (
            "rpm", "torque_pct", "power_w", "state", "control_capable",
            "motor_ack", "motor_route_ack", "motor_applied_rpm", "motor_control_active",
            "motor_control_fault",
        )
        if any(key not in sample for key in required):
            return False
        floats = [sample[key] for key in ("rpm", "torque_pct", "power_w")]
        if not all(isinstance(value, (int, float)) and math.isfinite(value) for value in floats):
            return False
        if not isinstance(sample["state"], int) or not 0 <= sample["state"] <= 3:
            return False
        self.last_presence = now_ms
        if self.enabled:
            self.sample = dict(sample)
        return True

    def snapshot(self, now_ms):
        online = self.last_presence is not None and now_ms - self.last_presence <= self.TIMEOUT_MS
        return online, self.enabled and online and self.sample is not None

    def set_enabled(self, enabled):
        self.enabled = enabled
        if not enabled:
            self.sample = None


class ServoPresenceTests(unittest.TestCase):
    @staticmethod
    def sample(**overrides):
        result = dict(
            rpm=0.0, torque_pct=0.0, power_w=0.0, state=0,
            control_capable=True, motor_ack=1, motor_applied_rpm=0,
            motor_route_ack=1,
            motor_control_active=False, motor_control_fault=0,
        )
        result.update(overrides)
        return result

    def test_presence_is_independent_from_routing(self):
        servo = ServoPresenceModel(enabled=False)
        self.assertTrue(servo.push(1000, **self.sample(rpm=10.0, torque_pct=2.0, power_w=3.0, state=1)))
        self.assertEqual((True, False), servo.snapshot(2000))
        self.assertIsNone(servo.sample)

    def test_invalid_sample_does_not_refresh_presence(self):
        servo = ServoPresenceModel()
        self.assertFalse(servo.push(1000, **self.sample(rpm=float("nan"), torque_pct=2.0, power_w=3.0, state=1)))
        self.assertEqual((False, False), servo.snapshot(1001))
        self.assertFalse(servo.push(2000, **self.sample(state=4)))

    def test_zero_rpm_is_valid_and_timeout_is_six_seconds(self):
        servo = ServoPresenceModel()
        self.assertTrue(servo.push(1000, **self.sample()))
        self.assertEqual((True, True), servo.snapshot(7000))
        self.assertEqual((False, False), servo.snapshot(7001))

    def test_reenable_requires_a_new_accepted_sample(self):
        servo = ServoPresenceModel()
        servo.push(1000, **self.sample(rpm=10.0, torque_pct=1.0, power_w=2.0, state=1))
        servo.set_enabled(False)
        servo.push(2000, **self.sample(rpm=20.0, torque_pct=2.0, power_w=4.0, state=1))
        servo.set_enabled(True)
        self.assertEqual((True, False), servo.snapshot(2001))
        servo.push(2002, **self.sample(rpm=20.0, torque_pct=2.0, power_w=4.0, state=1))
        self.assertEqual((True, True), servo.snapshot(2003))


if __name__ == "__main__":
    unittest.main(verbosity=2)
