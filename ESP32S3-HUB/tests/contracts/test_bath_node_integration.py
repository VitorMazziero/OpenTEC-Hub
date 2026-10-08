#!/usr/bin/env python3
"""Cross-firmware guards required by the Hub external-bath control path."""
import pathlib
import re
import unittest


REPO = pathlib.Path(__file__).resolve().parents[3]
BATH = REPO / "External-Devices/banho-termostatico/firmware/thermostatic-bath/src"


class BathNodeIntegrationTests(unittest.TestCase):
    def read(self, rel):
        return (BATH / rel).read_text(encoding="utf-8")

    def test_node_version_matches_hub_minimum(self):
        board = self.read("config/BoardConfig.h")
        hub_http = (REPO / "ESP32S3-HUB/ESP32S3-HUB/src/network/HttpServer.h").read_text(encoding="utf-8")
        coordinator = (REPO / "ESP32S3-HUB/ESP32S3-HUB/src/control/BathCommandCoordinator.cpp").read_text(encoding="utf-8")
        version = re.search(r'FirmwareVersion = "r(\d+)\.(\d+)"', board)
        self.assertIsNotNone(version)
        self.assertEqual(int(version[1]), 3)
        self.assertGreaterEqual(int(version[2]), 2)
        self.assertIn("bathNodeVersionSupported(registeredBath.version)", hub_http)
        self.assertIn("return minor >= 2;", coordinator)

    def test_node_publishes_rejection_range_and_honours_ownership(self):
        link = self.read("network/HubLink.cpp")
        codec = self.read("protocol/ConfigCodec.cpp")
        for token in ("&rej_cmd_id=%lu&rej_err=%s&sp_min=%.2f&sp_max=%.2f", 'g_hubOwnerFlag = owner == "1"',
                      "CommandSource::Hub"):
            self.assertIn(token, link)
        self.assertIn('replyError(reply, "hub_owned")', codec)
        self.assertIn('getJsonValue(payload, "stop") == 1', codec)
        self.assertIn('guardSetMode(MODE_MANUAL, "stop")', codec)
        network = self.read("network/NetworkManager.cpp")
        self.assertIn('"X-Hub-Owner"', network)

    def test_integrated_push_period_stays_inside_hub_freshness_window(self):
        link = self.read("network/HubLink.cpp")
        self.assertIn("HUB_CONTROL_MAX_PERIOD_MS = 2000", link)
        self.assertIn("next.hubEnabled", link)
        self.assertIn("min(g_cfg.sendPeriodMs, HUB_CONTROL_MAX_PERIOD_MS)", link)

    def test_rollover_safe_deadline_checks(self):
        network = self.read("network/NetworkManager.cpp")
        app = self.read("core/FirmwareApp.cpp")
        self.assertIn("static_cast<int32_t>(now - g_wifiNextActionMs) < 0", network)
        self.assertIn("static_cast<int32_t>(now - g_otaRebootAtMs) >= 0", app)

    def test_reconnect_requires_new_hello_for_ip_binding(self):
        network = self.read("network/NetworkManager.cpp")
        connected = network.index("if (WiFi.status() == WL_CONNECTED) {")
        deadline = network.index("static_cast<int32_t>(now - g_wifiNextActionMs)")
        self.assertIn("g_hubAnnounced = false;", network[connected:deadline])
        link = self.read("network/HubLink.cpp")
        self.assertIn("if (g_hubAnnounced && !g_otaInProgress) pushToHub", link)


if __name__ == "__main__":
    unittest.main(verbosity=2)
