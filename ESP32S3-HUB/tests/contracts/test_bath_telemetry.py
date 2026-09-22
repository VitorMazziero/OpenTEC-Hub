#!/usr/bin/env python3
"""H06 aggregate telemetry and snapshot-budget contract checks."""
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2] / "ESP32S3-HUB"


class BathTelemetryTests(unittest.TestCase):
    def read(self, rel):
        return (ROOT / rel).read_text(encoding="utf-8")

    def test_firmware_identity_and_full_bath_field_set(self):
        config = self.read("Config.h")
        telemetry = self.read("src/sensor/Telemetry.h")
        context = self.read("src/core/AppContext.h")
        self.assertIn('#define HUB_FIRMWARE_VERSION "10.6.0-dev"', config)
        fields = [
            "TempSetpoint", "BathSp", "BathTarget", "BathPv", "BathDisplaySp",
            "BathState", "BathPhase", "BathError", "BathMode", "BathGuard",
            "BathDeviation", "BathSpSource", "BathCascadeError",
            "BathCascadePvFiltered", "BathCascadeP", "BathCascadeI",
            "BathCascadeSaturated", "BathCascadePausedReason", "BathCascadeLastUpdateMs",
            "BathOwned", "BathCascadeActive", "BathCascadeFaultReason", "BathStopPending",
            "BathCommandCompletion", "BathOperationError", "BathNodeRejectId",
            "BathNodeRejectError", "BathNodeSpMin", "BathNodeSpMax", "TempSetpointCommanded",
            "TempModuleActuatorOn", "BathCascadeKp", "BathCascadeTiS", "BathCascadeBiasC",
            "BathCascadePeriodMs", "BathCascadeFilterS", "BathCascadeCommandMinMs",
            "BathCascadeCommandBandC", "BathCascadeSlewCMin", "BathCascadeOffsetHighC",
            "BathCascadeOffsetLowC", "BathCascadeOutputMinC", "BathCascadeOutputMaxC",
            "BathCascadeConfigError",
        ]
        for field in fields:
            self.assertIn(field, telemetry + context)
        self.assertIn('appendNodeIdentity(jsonResponse, "Bath"', telemetry)
        self.assertIn('json += "IP', context)
        self.assertIn('json += "NodeVer', context)
        self.assertIn('json += "NodeMac', context)

    def test_invalid_numeric_snapshot_is_null_and_strings_are_bounded(self):
        telemetry = self.read("src/sensor/Telemetry.h")
        self.assertIn('else jsonResponse += "null"', telemetry)
        self.assertIn("snprintf(snapBathState, sizeof(snapBathState)", telemetry)
        self.assertIn("snprintf(snapBathError, sizeof(snapBathError)", telemetry)
        self.assertIn("xSemaphoreTake(stateMutex", telemetry)

    def test_reserve_budget_has_room_for_full_diagnostic_frame(self):
        context = self.read("src/core/AppContext.h")
        reserve = int(context.split("#define HUB_TELEMETRY_JSON_RESERVE ", 1)[1].splitlines()[0])
        # Conservative worst-case estimate for the H06 additions: long bounded
        # strings, finite numbers and node identity fields, excluding legacy keys.
        bath_payload = 2600
        self.assertGreaterEqual(reserve, 4608)
        self.assertLess(bath_payload, reserve)

    def test_identity_is_appended_from_registry_snapshot(self):
        telemetry = self.read("src/sensor/Telemetry.h")
        self.assertIn('appendNodeIdentity(jsonResponse, "Bath",', telemetry)
        self.assertIn("snapNodes[DEV_BATH]", telemetry)


if __name__ == "__main__":
    unittest.main(verbosity=2)
