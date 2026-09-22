#!/usr/bin/env python3
"""H03 source and numerical guardrails for the pure bath cascade controller."""
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2] / "ESP32S3-HUB"


class BathCascadeTests(unittest.TestCase):
    def setUp(self):
        self.header = (ROOT / "src/control/ExternalBathCascade.h").read_text(encoding="utf-8")
        self.source = (ROOT / "src/control/ExternalBathCascade.cpp").read_text(encoding="utf-8")

    def test_states_and_pure_boundary_are_defined(self):
        for state in ("Off", "WaitingInputs", "Initializing", "Controlling",
                      "ActuatorBusy", "Paused", "Fault"):
            self.assertIn(state, self.header)
        self.assertNotIn("HTTP", self.header + self.source)
        self.assertNotIn("Preferences", self.header + self.source)
        self.assertNotIn("GPIO", self.header + self.source)

    def test_defaults_match_hub_plan(self):
        for token in ("kp = 0.5f", "tiS = 600.0f", "biasC = 0.6f",
                      "periodMs = 10000", "filterS = 20.0f", "commandMinMs = 30000",
                      "commandBandC = 0.1f", "slewCMin = 0.5f",
                      "offsetHighC = 5.0f", "offsetLowC = 5.0f"):
            self.assertIn(token, self.header)

    def test_controller_contains_required_safety_terms(self):
        for token in ("filterS", "candidateIntegral", "Conditional anti-windup",
                      "offsetLowC", "offsetHighC", "commandBandC", "maxStep",
                      "commandMinMs", "in.nowMs - lastInputMs_", "faultLatched_"):
            self.assertIn(token, self.source)

    def test_fault_is_latched_until_explicit_reset(self):
        fault_latch = self.source.index("faultLatched_ = true")
        latched_gate = self.source.index("if (faultLatched_)", fault_latch)
        reset_clear = self.source.index("faultLatched_ = false")
        self.assertLess(reset_clear, fault_latch)
        self.assertLess(fault_latch, latched_gate)
        self.assertIn("lastInputMs_ = in.nowMs", self.source[latched_gate:])

    def test_configuration_is_transactional(self):
        self.assertIn("if (!finitePositive(candidate.kp)", self.source)
        self.assertIn("config_ = candidate;", self.source)
        self.assertLess(self.source.index("if (!finitePositive(candidate.kp)"),
                        self.source.index("config_ = candidate;"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
