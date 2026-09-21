#!/usr/bin/env python3
"""H05 persistence, validation and safe-reboot contract checks."""
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2] / "ESP32S3-HUB"


class BathSettingsTests(unittest.TestCase):
    def read(self, rel):
        return (ROOT / rel).read_text(encoding="utf-8")

    def test_all_configuration_keys_are_parsed_and_persisted(self):
        commands = self.read("src/protocol/Commands.h")
        settings = self.read("src/storage/Settings.h")
        keys = [
            "bathCascadeKp", "bathCascadeTiS", "bathCascadeBiasC",
            "bathCascadePeriodMs", "bathCascadeFilterS",
            "bathCascadeCommandMinMs", "bathCascadeCommandBandC",
            "bathCascadeSlewCMin", "bathCascadeOffsetHighC",
            "bathCascadeOffsetLowC", "bathCascadeOutputMinC",
            "bathCascadeOutputMaxC",
        ]
        for key in keys:
            self.assertIn(key, commands)
        for key in [
            '"bathKp"', '"bathTi"', '"bathBias"', '"bathPeriod"',
            '"bathFilter"', '"bathCmdMin"', '"bathBand"', '"bathSlew"',
            '"bathOffHigh"', '"bathOffLow"', '"bathOutMin"', '"bathOutMax"',
            '"bathRoute"', '"bathComm"',
        ]:
            self.assertIn(key, settings)

    def test_candidate_is_atomic_and_complete_config_is_validated(self):
        commands = self.read("src/protocol/Commands.h")
        cascade = self.read("src/control/ExternalBathCascade.cpp")
        self.assertIn("ExternalBathCascadeConfig bathCandidate = bathCascadeConfig", commands)
        self.assertIn("bathConfigSyntaxValid", commands)
        self.assertIn("validateConfig(bathCandidate)", commands)
        self.assertIn("bathCascadeConfig = bathCandidate", commands)
        self.assertIn("static bool validateConfig", self.read("src/control/ExternalBathCascade.h"))
        self.assertIn("candidate.outputMinC >= candidate.outputMaxC", cascade)
        self.assertIn("candidate.periodMs < 100", cascade)
        self.assertIn("bathCascadeReset", commands)
        self.assertIn("routeOrSetpointSameFrame", commands)

    def test_reboot_does_not_resume_process_and_restores_100b_guard(self):
        settings = self.read("src/storage/Settings.h")
        runtime = self.read("src/core/Runtime.h")
        self.assertIn("tempReferenceCommanded = false", settings)
        self.assertIn("tempRouteTransitionPending = tempControlRoute == TempControlRoute::ExternalBath", settings)
        self.assertIn('sendSensorCommand("100B", false)', runtime)
        self.assertIn("if (tempControlRoute == TempControlRoute::ExternalBath)", runtime)

    def test_hash_and_debounce_cover_new_persistent_state(self):
        commands = self.read("src/protocol/Commands.h")
        runtime = self.read("src/core/Runtime.h")
        self.assertIn("bathCascadeConfig.periodMs * 199", commands)
        self.assertIn("bathCommOn ? 1 : 0", commands)
        self.assertIn("flagPendingSave && (now - lastSaveTriggerTime >= SAVE_DEBOUNCE_MS)", runtime)

    def test_control_waits_for_confirmed_display_setpoint_and_period(self):
        cascade = self.read("src/control/ExternalBathCascade.cpp")
        self.assertIn("in.bathSpValid", cascade)
        self.assertIn("in.nowMs - lastInputMs_ < config_.periodMs", cascade)
        self.assertIn("lastInputMs_ = in.nowMs", cascade)
        self.assertIn("postDoneCooldown", self.read("src/core/Runtime.h"))
        self.assertIn("xSemaphoreTake(stateMutex", self.read("src/core/Runtime.h"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
