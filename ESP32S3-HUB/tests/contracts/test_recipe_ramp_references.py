"""Cross-language telemetry contract for autonomous pH/pressure ramp references.

Source contract only: does not qualify UART write completion or physical actuation.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
HUB = ROOT / "ESP32S3-HUB" / "ESP32S3-HUB" / "src"
WINDOWS = ROOT / "Windows_app" / "src" / "OpenTECHub.Protocol"


class RecipeRampReferenceContractTests(unittest.TestCase):
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
