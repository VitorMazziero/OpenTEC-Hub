#!/usr/bin/env python3
"""Static contract checks for pump firmware 3.12 polynomial calibration."""

from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1] / "firmware" / "peristaltic-pump" / "src"


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


class PumpFirmwareV312Contract(unittest.TestCase):
    def test_version_is_consistent(self):
        self.assertIn('#define PUMP_FW_VERSION "3.12"', read("core/FirmwareApp.cpp"))
        self.assertIn('ver=3.12', read("network/HubClient.h"))
        self.assertIn('v3.12', read("core/Lifecycle.h"))

    def test_record_contains_flowmeter_equation_family(self):
        source = read("storage/CalibrationStore.h")
        for field in ("a1", "b1", "k1", "f1", "c1", "k2", "f2", "c2", "s_t"):
            self.assertIn(field, source)
        self.assertIn("PUMP_CAL_MAGIC_V3", source)
        self.assertIn("9 * sizeof(float)", source)

    def test_old_records_are_migrated_without_reusing_the_old_blob(self):
        source = read("storage/CalibrationStore.h")
        self.assertIn("PumpDualRangeCalV2", source)
        self.assertIn("NVS_KEY_PUMP_CAL_V2", source)
        self.assertIn("NVS_KEY_PUMP_POLY_CAL", source)
        self.assertIn("setLinearPumpCalibration(old.m_low", source)

    def test_persisted_record_is_mathematically_validated_before_activation(self):
        source = read("storage/CalibrationStore.h")
        self.assertIn("isPumpCalibrationValid(candidate)", source)
        self.assertIn("lowAtTransition", source)
        self.assertIn("lowDerivative", source)
        self.assertIn("current < previous", source)

    def test_forward_conversion_uses_horner_polynomials(self):
        source = read("control/SensorAndConversion.h")
        self.assertIn("g_pumpCal.a1 * speedUnits", source)
        self.assertIn("g_pumpCal.k2 * speedUnits", source)
        self.assertNotIn("g_pumpCal.q_t", source)

    def test_inverse_uses_bounded_bisection(self):
        source = read("control/SensorAndConversion.h")
        self.assertIn("for (uint8_t i = 0; i < 32; i++)", source)
        self.assertIn("lower", source)
        self.assertIn("upper", source)

    def test_command_is_atomic_and_blocked_while_running(self):
        source = read("control/OperationController.h")
        self.assertIn("hasAllPumpCoefficients", source)
        self.assertIn("nove campos no mesmo quadro", source)
        self.assertIn("g_opState == OP_RUNNING || g_opState == OP_WAITING", source)

    def test_command_validates_c0_c1_and_monotonicity(self):
        source = read("control/OperationController.h")
        self.assertIn("lowValue-highValue", source)
        self.assertIn("lowSlope-highSlope", source)
        self.assertIn("monotonic", source)
        self.assertIn("g_pumpCal = previous", source)

    def test_usb_telemetry_echoes_all_coefficients_and_crc(self):
        source = read("protocol/TelemetryCodec.h")
        for key in ("pumpA1", "pumpB1", "pumpK1", "pumpF1", "pumpC1",
                    "pumpK2", "pumpF2", "pumpC2", "pumpTransitionSpeed", "pumpCalCrc"):
            self.assertIn(key, source)

    def test_hub_push_echoes_native_coefficients_and_crc(self):
        source = read("network/HubClient.h")
        for key in ("a1=", "b1=", "k1=", "f1=", "c1=", "k2=", "f2=", "c2=",
                    "trans_speed=", "cal_crc="):
            self.assertIn(key, source)

    def test_legacy_pump_config_layout_remains_separate(self):
        source = read("core/FirmwareApp.cpp")
        pump_config = source[source.index("struct PumpConfig"):source.index("#ifndef PUMP_DUAL_RANGE_CAL_DEFINED")]
        self.assertNotIn("a1", pump_config)
        self.assertIn("float pumpSlope", pump_config)


if __name__ == "__main__":
    unittest.main()
