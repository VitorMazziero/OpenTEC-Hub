#!/usr/bin/env python3
"""Hub 10.7 Wi-Fi firmware update (OTA) guardrails."""
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2] / "ESP32S3-HUB"


class HubOtaTests(unittest.TestCase):
    def setUp(self):
        self.ota = (ROOT / "src/network/OtaUpdate.h").read_text(encoding="utf-8")
        self.http = (ROOT / "src/network/HttpServer.h").read_text(encoding="utf-8")
        self.runtime = (ROOT / "src/core/Runtime.h").read_text(encoding="utf-8")
        self.app = (ROOT / "src/core/FirmwareApp.cpp").read_text(encoding="utf-8")

    def test_routes_are_registered_before_the_server_starts(self):
        self.assertIn('server.on("/update", HTTP_GET', self.ota)
        self.assertIn('server.on("/update", HTTP_POST', self.ota)
        self.assertLess(self.http.index("registerOtaRoutes();"), self.http.index("server.begin();"))
        self.assertLess(self.app.index("OtaUpdate.h"), self.app.index("HttpServer.h"))

    def test_image_is_verified_before_the_switch(self):
        self.assertIn("Update.end(true)", self.ota)
        self.assertIn("Update.abort()", self.ota)
        # Only a finished, verified image schedules the reboot.
        finished = self.ota.index("} else if (!Update.isFinished()) {")
        reboot = self.ota.index("hubOtaRebootAtMs = at == 0 ? 1 : at;")
        self.assertLess(finished, reboot)

    def test_refused_while_a_process_is_commanded(self):
        self.assertIn("tempReferenceCommanded", self.ota)
        self.assertIn("motorRPM != 0", self.ota)
        # Checked when the upload starts and again before the image is switched.
        self.assertEqual(self.ota.count("hubOtaBusyReason();"), 3)
        final = self.ota.index("if (final) {")
        self.assertLess(final, self.ota.index("hubOtaReject(busy);", final))

    def test_only_the_hub_app_image_is_accepted(self):
        for token in ('startsWith("ESP32S3-HUB")', '"merged"', '"bootloader"', '"partitions"'):
            self.assertIn(token, self.ota)

    def test_reboot_runs_on_the_main_loop_after_saving(self):
        self.assertIn("serviceHubOtaReboot(now);", self.runtime)
        service = self.ota.index("void serviceHubOtaReboot")
        self.assertLess(self.ota.index("saveSettings();", service), self.ota.index("ESP.restart();", service))


if __name__ == "__main__":
    unittest.main(verbosity=2)
