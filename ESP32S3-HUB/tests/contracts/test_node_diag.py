#!/usr/bin/env python3
"""Contract model and source guards for the Hub 10.2 node diagnostics proxy."""
import json
import pathlib
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "ESP32S3-HUB"
DEVICES = ("distance", "agitator", "pump", "flowmeter", "biomass")


class NodeDiagCacheModel:
    def __init__(self):
        self.rows = {device: {"body": "", "fetched_ms": 0, "code": 0} for device in DEVICES}

    def store(self, device, code, body, fetched_ms):
        encoded = body.encode("utf-8")[:511]
        self.rows[device] = {
            "body": encoded.decode("utf-8", errors="ignore"),
            "fetched_ms": fetched_ms,
            "code": code,
        }

    def entry(self, device, now_ms):
        cached = self.rows[device]
        age = now_ms - cached["fetched_ms"] if cached["fetched_ms"] else 999999
        try:
            diag = json.loads(cached["body"]) if cached["body"] else None
        except json.JSONDecodeError:
            diag = None
        return {"dev": device, "code": cached["code"], "age_ms": age, "diag": diag}


class NodeDiagModelTests(unittest.TestCase):
    def test_never_seen_is_code_zero_and_age_sentinel(self):
        self.assertEqual(
            {"dev": "pump", "code": 0, "age_ms": 999999, "diag": None},
            NodeDiagCacheModel().entry("pump", 1200),
        )

    def test_age_and_exact_diagnostic_object(self):
        model = NodeDiagCacheModel()
        body = '{"rssi":-61,"free_heap":208000,"uptime_s":42}'
        model.store("pump", 200, body, 1000)
        row = model.entry("pump", 1450)
        self.assertEqual(450, row["age_ms"])
        self.assertEqual(-61, row["diag"]["rssi"])

    def test_body_is_bounded_to_511_bytes(self):
        model = NodeDiagCacheModel()
        model.store("distance", 200, "x" * 900, 10)
        self.assertEqual(511, len(model.rows["distance"]["body"].encode("utf-8")))


class NodeDiagSourceTests(unittest.TestCase):
    @staticmethod
    def read(relative):
        return (SRC / relative).read_text(encoding="utf-8")

    def test_cache_and_task_are_bounded_and_independent(self):
        app = self.read("src/core/AppContext.h")
        task = self.read("src/network/NodeDiagTask.h")
        self.assertIn("char body[512]", app)
        self.assertIn("g_nodeDiagCache[DEV_COUNT]", app)
        self.assertIn("http.setTimeout(500)", task)
        self.assertIn('http.begin(String("http://") + ip.toString() + "/diag")', task)
        self.assertIn("sizeof(cache.body) - 1", task)
        self.assertIn("6144", task)
        self.assertIn("NODE_DIAG_INTER_NODE_MS", task)

    def test_http_handler_only_reads_cache(self):
        http = self.read("src/network/HttpServer.h")
        start = http.index('server.on("/nodeDiag"')
        end = http.index('server.on("/agitatorCommand"', start)
        handler = http[start:end]
        self.assertIn("buildNodeDiagDocument", handler)
        self.assertNotIn("http.GET(", handler)
        self.assertNotIn("HTTPClient", handler)

    def test_serial_command_is_non_actuating_and_bounded_per_line(self):
        commands = self.read("src/protocol/Commands.h")
        task = self.read("src/network/NodeDiagTask.h")
        self.assertIn('getValueFromJson(json, "nodeDiag")', commands)
        self.assertIn('requested == "all"', task)
        self.assertIn('Serial.println(String("{\\"NodeDiag\\":")', task)


if __name__ == "__main__":
    unittest.main(verbosity=2)
