"""Cross-language telemetry contract for autonomous pH/pressure ramp references.

Source contract only: does not qualify UART write completion or physical actuation.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
HUB = ROOT / "ESP32S3-HUB" / "ESP32S3-HUB" / "src"
WINDOWS = ROOT / "Windows_app" / "src" / "OpenTECHub.Protocol"


class RecipeRampReferenceContractTests(unittest.TestCase):
    def test_exact_temperature_reference_is_opt_in_and_preserves_ordinary_commands(self):
        sensor = (HUB / "sensor" / "SensorUart.h").read_text(encoding="utf-8")
        commands = (HUB / "protocol" / "Commands.h").read_text(encoding="utf-8")
        keys = (WINDOWS / "CommandKeys.cs").read_text(encoding="utf-8")
        self.assertIn('TempSetpointExact = "tempSetpointExact"', keys)
        declarations = (HUB / "protocol" / "Mailboxes.h").read_text(encoding="utf-8")
        self.assertIn("bool exactReference = false", declarations)
        self.assertIn("exactReference && tempReference != temp", sensor)
        self.assertIn("changed || exactChanged || !tempReferenceCommanded", sensor)
        self.assertIn('getRaw(json, "tempSetpointExact", exactRaw)', commands)
        self.assertIn("if (exactReference) setTemperature(requested, true);", commands)
        self.assertIn("else setTemperature(requested);", commands)

    def test_temperature_off_echo_survives_cleared_bath_command_state(self):
        telemetry = (HUB / "sensor" / "Telemetry.h").read_text(encoding="utf-8")
        self.assertIn("snapTempReference >= 0.0f", telemetry)
        self.assertIn("snapTempReference == 0.0f || !snapBathViaBath || snapTempCommanded", telemetry)

    def test_hub_and_windows_agree_on_reference_dispatch_state_keys(self):
        telemetry = (HUB / "sensor" / "Telemetry.h").read_text(encoding="utf-8")
        keys = (WINDOWS / "CommandKeys.cs").read_text(encoding="utf-8")
        for key in ("PHSetpoint", "PHError", "PHControlActive", "PHCommandPending",
                    "PressureReference", "PressureControlActive", "PressureCommandPending"):
            self.assertIn(f'public const string {key} = "{key}";', keys)
            self.assertIn(f'\\"{key}\\"', telemetry)

    def test_reference_precision_matches_native_module_command_representation(self):
        telemetry = (HUB / "sensor" / "Telemetry.h").read_text(encoding="utf-8")
        runtime = (HUB / "core" / "Runtime.h").read_text(encoding="utf-8")
        commands = (HUB / "protocol" / "Commands.h").read_text(encoding="utf-8")
        self.assertIn("String(pHReference, 2)", runtime)
        self.assertIn("String(snapPhReference, 2)", telemetry)
        self.assertIn("String(pHError, 2)", runtime)
        self.assertIn("String(snapPhError, 2)", telemetry)
        self.assertIn('getValueFromJson(json, "pressureReference").toInt()', commands)
        self.assertIn("String(snapPressureReference)", telemetry)
        self.assertIn("isfinite(snapPhReference)", telemetry)


if __name__ == "__main__":
    unittest.main()
