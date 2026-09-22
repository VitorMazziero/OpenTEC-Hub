#!/usr/bin/env python3
"""Modelo do registro de nos externos (core/AppContext.h) e da sua publicacao (10.1).

Espelha recordDeviceActivity() e appendNodeIdentity(): o IP e sempre publicado
(0.0.0.0 = nunca visto); versao e MAC so aparecem depois de um /nodeHello, e um
/agitatorHello legado renova o hello sem apagar a versao que o no ja informou.
A segunda classe le o proprio fonte para fixar que as quinze chaves existem e que
as duas Strings do quadro reservam o mesmo tamanho.
"""
import json
import pathlib
import re
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "ESP32S3-HUB"
DEVICES = ("distance", "agitator", "pump", "flowmeter", "biomass", "bath")
PREFIXES = ("Distance", "Agitator", "Pump", "Flowmeter", "Biomass", "Bath")


class NodeEntry:
    def __init__(self, name):
        self.name = name
        self.ip = "0.0.0.0"
        self.mac = ""
        self.version = ""
        self.last_hello_ms = 0
        self.last_data_ms = 0
        self.registered = False


class NodeRegistryModel:
    def __init__(self):
        self.entries = {name: NodeEntry(name) for name in DEVICES}

    def record(self, dev, ip, now_ms, is_hello, ver="", mac=""):
        entry = self.entries[dev]
        if ip != "0.0.0.0":
            entry.ip = ip
        if is_hello:
            entry.last_hello_ms = now_ms
            entry.registered = True
            if ver:
                entry.version = ver[:15]
            if mac:
                entry.mac = mac[:17]
        else:
            entry.last_data_ms = now_ms

    def aggregate_keys(self):
        out = {}
        for prefix, name in zip(PREFIXES, DEVICES):
            entry = self.entries[name]
            out[prefix + "IP"] = entry.ip
            if not entry.registered:
                continue
            if entry.version:
                out[prefix + "NodeVer"] = entry.version
            if entry.mac:
                out[prefix + "NodeMac"] = entry.mac
        return out

    def nodes(self, now_ms, only=None):
        rows = []
        for name in DEVICES:
            if only and only != name:
                continue
            entry = self.entries[name]
            last_seen = max(entry.last_hello_ms, entry.last_data_ms)
            age = now_ms - last_seen if last_seen > 0 and now_ms >= last_seen else 999999
            rows.append({
                "dev": name, "ip": entry.ip, "mac": entry.mac, "version": entry.version,
                "age_ms": age, "registered": entry.registered,
                "last_hello_ms": entry.last_hello_ms, "last_data_ms": entry.last_data_ms,
            })
        return {"hub_time_ms": now_ms, "nodes": rows}


class NodeRegistryModelTests(unittest.TestCase):
    def test_ip_is_always_published_and_identity_only_after_hello(self):
        model = NodeRegistryModel()
        keys = model.aggregate_keys()
        self.assertEqual({p + "IP" for p in PREFIXES}, set(keys))
        self.assertTrue(all(v == "0.0.0.0" for v in keys.values()))

        model.record("pump", "192.168.4.3", 1000, is_hello=False)
        keys = model.aggregate_keys()
        self.assertEqual("192.168.4.3", keys["PumpIP"])
        self.assertNotIn("PumpNodeVer", keys)  # data before hello: IP only

        model.record("pump", "192.168.4.3", 2000, is_hello=True, ver="3.8", mac="AA:BB:CC:DD:EE:01")
        keys = model.aggregate_keys()
        self.assertEqual("3.8", keys["PumpNodeVer"])
        self.assertEqual("AA:BB:CC:DD:EE:01", keys["PumpNodeMac"])
        self.assertNotIn("DistanceNodeVer", keys)

    def test_legacy_agitator_hello_keeps_the_reported_version(self):
        model = NodeRegistryModel()
        model.record("agitator", "192.168.4.5", 1000, is_hello=True, ver="v10", mac="AA:BB:CC:DD:EE:05")
        model.record("agitator", "192.168.4.5", 2000, is_hello=True, ver="", mac="")  # /agitatorHello
        entry = model.entries["agitator"]
        self.assertEqual("v10", entry.version)
        self.assertEqual(2000, entry.last_hello_ms)

    def test_dhcp_renumbering_replaces_the_ip(self):
        model = NodeRegistryModel()
        model.record("distance", "192.168.4.2", 1000, is_hello=True, ver="v10", mac="m")
        model.record("distance", "192.168.4.7", 5000, is_hello=False)
        self.assertEqual("192.168.4.7", model.aggregate_keys()["DistanceIP"])

    def test_nodes_reports_freshness_and_filters_by_dev(self):
        model = NodeRegistryModel()
        model.record("flowmeter", "192.168.4.4", 1000, is_hello=True, ver="v10", mac="m")
        model.record("flowmeter", "192.168.4.4", 4000, is_hello=False)
        doc = model.nodes(4500)
        self.assertEqual(4500, doc["hub_time_ms"])
        self.assertEqual(6, len(doc["nodes"]))
        flow = next(n for n in doc["nodes"] if n["dev"] == "flowmeter")
        self.assertEqual(500, flow["age_ms"])
        self.assertTrue(flow["registered"])
        never = next(n for n in doc["nodes"] if n["dev"] == "pump")
        self.assertEqual(999999, never["age_ms"])
        self.assertFalse(never["registered"])
        self.assertEqual(["flowmeter"], [n["dev"] for n in model.nodes(4500, only="flowmeter")["nodes"]])


class NodeRegistrySourceTests(unittest.TestCase):
    def read(self, rel):
        return (SRC / rel).read_text(encoding="utf-8")

    def test_aggregate_frame_emits_the_fifteen_identity_keys(self):
        app = self.read("src/core/AppContext.h")
        tel = self.read("src/sensor/Telemetry.h")
        self.assertIn('"IP\\":\\""', app)
        self.assertIn('"NodeVer\\":\\""', app)
        self.assertIn('"NodeMac\\":\\""', app)
        for prefix in PREFIXES:
            self.assertRegex(tel, r'appendNodeIdentity\(jsonResponse,\s*"%s"' % prefix)

    def test_identity_is_conditional_on_registration(self):
        app = self.read("src/core/AppContext.h")
        helper = app[app.index("inline void appendNodeIdentity"):app.index("inline void recordDeviceActivity")]
        self.assertIn("if (!e.registered) return;", helper)
        self.assertLess(helper.index('IP\\":'), helper.index("if (!e.registered) return;"))

    def test_both_frame_strings_share_one_reserve(self):
        self.assertIn("jsonResponse.reserve(HUB_TELEMETRY_JSON_RESERVE)", self.read("src/sensor/Telemetry.h"))
        self.assertIn("lastSensorJson.reserve(HUB_TELEMETRY_JSON_RESERVE)", self.read("src/core/Runtime.h"))
        self.assertRegex(self.read("src/core/AppContext.h"), r"#define HUB_TELEMETRY_JSON_RESERVE\s+4608")

    def test_nodes_route_publishes_registration_and_timestamps(self):
        http = self.read("src/network/HttpServer.h")
        handler = http[http.index('server.on("/nodes"'):http.index('server.on("/agitatorCommand"')]
        for token in ('\\"hub_time_ms\\"', '\\"registered\\"', '\\"last_hello_ms\\"', '\\"last_data_ms\\"', 'hasParam("dev")'):
            self.assertIn(token, handler)

    def test_legacy_agitator_hello_passes_no_version(self):
        http = self.read("src/network/HttpServer.h")
        handler = http[http.index('server.on("/agitatorHello"'):http.index('server.on("/nodeHello"')]
        self.assertIn('recordDeviceActivity(DEV_AGITATOR, rip, now, true, "", "")', handler)

    def test_firmware_identity_is_10_6_0(self):
        self.assertIn('#define HUB_FIRMWARE_VERSION "10.6.0-dev"', self.read("Config.h"))
        self.assertIn("#define HUB_PROTOCOL_VERSION 10", self.read("Config.h"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
