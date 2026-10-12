#!/usr/bin/env python3
"""Contratos do Hub 10.9 com a bomba 3.13: repasse da dosagem, recusa, entrega na resposta
do push e publicacao dos campos novos (docs/WIRE_CONTRACT_V9.md, bomba docs/PROTOCOL.md)."""
import pathlib
import re
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "ESP32S3-HUB"


def read(rel: str) -> str:
    return (SRC / rel).read_text(encoding="utf-8")


def get_value_from_json(json: str, key: str) -> str:
    """Modelo de getValueFromJson: a chave casa so entre aspas."""
    m = re.search(r'"' + re.escape(key) + r'"\s*:\s*("([^"]*)"|[^,}\s]+)', json)
    if not m:
        return ""
    return m.group(2) if m.group(2) is not None else m.group(1)


DOSE_KEYS = ["pump_dose", "pump_dose_ml", "pump_dose_period_s", "pump_dose_speed",
             "pump_dose_rate_mlmin", "pump_dose_total_ml", "pump_dose_dead_ms"]


class ReliableBox:
    def __init__(self, base=512_345_000):
        self.revision, self.awaiting, self.payload = base, False, ""

    def queue(self, inner):
        self.revision += 1
        self.awaiting = True
        self.payload = '{"cmd_id":%d,%s}' % (self.revision, inner)
        return self.revision

    def take(self):
        return self.payload if self.awaiting else "{}"

    def ack(self, rid):
        if self.awaiting and rid and rid == self.revision:
            self.awaiting, self.payload = False, ""
            return True
        return False

    reject = ack  # mesma regra de casamento


class DoseForwarding(unittest.TestCase):
    def test_dose_keys_are_forwarded(self):
        cmd = read("src/protocol/Commands.h")
        simple = cmd[cmd.index("const char* simpleKeys[]"):cmd.index("const char* allowedPumpCommands[]")]
        for key in DOSE_KEYS:
            self.assertIn(f'"{key}"', simple)
        # O prefixo pump_ ja e removido para todas as chaves simples.
        self.assertIn('else if (cleanKey.startsWith("pump_")) cleanKey = cleanKey.substring(5);', cmd)

    def test_prefix_strip_model(self):
        for key in DOSE_KEYS:
            self.assertEqual(key[5:], key.replace("pump_", "", 1))
        self.assertEqual("pump_dose"[5:], "dose")

    def test_pump_dose_does_not_match_longer_keys(self):
        frame = '{"pump_dose_ml":0.35,"pump_dose_period_s":300}'
        self.assertEqual(get_value_from_json(frame, "pump_dose"), "")
        self.assertEqual(get_value_from_json(frame, "pump_dose_ml"), "0.35")
        self.assertIn('String searchKey = "\\"" + key + "\\"";', read("src/protocol/Mailboxes.h"))

    def test_nothing_is_queued_without_routing(self):
        self.assertIn("if (pumpCmdFound && pumpCommOn) {", read("src/protocol/Commands.h"))


class Refusal(unittest.TestCase):
    def test_reject_closes_only_the_matching_revision(self):
        box = ReliableBox()
        rid = box.queue('"dose":1')
        self.assertFalse(box.reject(rid - 1))
        self.assertTrue(box.awaiting)
        self.assertTrue(box.reject(rid))
        self.assertFalse(box.awaiting)
        self.assertEqual(box.take(), "{}")

    def test_source_rejects_through_the_mailbox(self):
        mail = read("src/protocol/Mailboxes.h")
        self.assertIn("bool rejectReliable(ReliableMailbox &box, uint32_t rejectedId, const char *label, const char *reason)", mail)
        body = mail[mail.index("bool rejectReliable(ReliableMailbox &box"):mail.index("// True while a command is outstanding")]
        self.assertIn("box.awaiting && rejectedId != 0 && rejectedId == box.revision", body)
        http = read("src/network/HttpServer.h")
        self.assertIn('rejectReliable(pumpBox, rejId, "Pump", rejErr.c_str());', http)

    def test_rej_err_none_means_no_refusal(self):
        self.assertIn('if (err == "none") err = "";', read("src/network/HttpServer.h"))


class PushResponse(unittest.TestCase):
    def test_pump_data_returns_the_pending_command(self):
        http = read("src/network/HttpServer.h")
        handler = http[http.index('server.on("/pumpData"'):http.index('server.on("/agitatorData"') if 'server.on("/agitatorData"' in http else None]
        self.assertIn('request->send(200, "application/json", takeReliable(pumpBox));', handler)
        self.assertNotIn('"Pump data received"', handler)
        # O poll continua: bomba 3.12 ignora o corpo da resposta.
        self.assertIn('request->send(200, "application/json", takeReliable(pumpBox));', http[http.index('"/pumpCommand"'):])

    def test_ack_is_processed_before_the_response(self):
        http = read("src/network/HttpServer.h")
        handler = http[http.index('server.on("/pumpData"'):]
        self.assertLess(handler.index('ackReliable(pumpBox'), handler.index('takeReliable(pumpBox)'))
        self.assertLess(handler.index('rejectReliable(pumpBox'), handler.index('takeReliable(pumpBox)'))


class Publication(unittest.TestCase):
    PUSH_TO_JSON = {
        "drv": "PumpDrive", "sens": "PumpLiquidSensor", "wet": "PumpLiquidOk", "t_min": "PumpProfileMin",
        "up": "PumpUptimeS", "boot": "PumpBootCount", "rst": "PumpResetReason",
        "rej_cmd_id": "PumpRejCmdId", "rej_err": "PumpRejErr",
        "d_act": "PumpDoseActive", "d_pulse": "PumpDosePulse", "d_ml": "PumpDoseMl", "d_per": "PumpDosePeriodS",
        "d_spd": "PumpDoseSpeed", "d_rate": "PumpDoseRate", "d_tot": "PumpDoseTotal", "d_dead": "PumpDoseDeadMs",
        "d_done": "PumpDoseDelivered", "d_n": "PumpDosePulses", "d_next": "PumpDoseNextS", "d_short": "PumpDoseShortfall",
    }

    def test_every_push_field_is_read_and_published(self):
        http = read("src/network/HttpServer.h")
        tel = read("src/sensor/Telemetry.h")
        for push, key in self.PUSH_TO_JSON.items():
            self.assertRegex(http, rf'"{push}"', push)
            self.assertIn(f'\\"{key}\\"', tel, key)

    def test_health_is_published_without_routing_values_only_with_it(self):
        tel = read("src/sensor/Telemetry.h")
        health = tel[tel.index("if (pumpOnline && snapPumpExtSeen) {"):tel.index("if (pumpRouted) {")]
        for key in ("PumpUptimeS", "PumpBootCount", "PumpResetReason", "PumpRejCmdId", "PumpRejErr"):
            self.assertIn(key, health)
        routed = tel[tel.index("if (pumpRouted) {"):]
        for key in ("PumpDrive", "PumpDoseActive", "PumpDoseDelivered", "PumpProfileMin"):
            self.assertIn(key, routed)
            self.assertNotIn(key, health)

    def test_313_fields_stay_out_for_a_312_pump(self):
        http = read("src/network/HttpServer.h")
        self.assertIn('if (request->hasParam("drv") || request->hasParam("d_act")) pumpExtSeen = true;', http)
        tel = read("src/sensor/Telemetry.h")
        self.assertIn("if (pumpExtSeen && (millis() - pumpLastUpdate > PUMP_TIMEOUT)) {", tel)
        self.assertIn("if (snapPumpExtSeen) {", tel)

    def test_version(self):
        self.assertIn('#define HUB_FIRMWARE_VERSION "10.9.0-dev"', read("Config.h"))


if __name__ == "__main__":
    unittest.main()
