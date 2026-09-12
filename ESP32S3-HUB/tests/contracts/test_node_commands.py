#!/usr/bin/env python3
"""Modelo e testes de contrato dos comandos de nós externos e caixa confiável.

Foca na caixa confiável por carona no push (piggyback) do sensor de distância (Hub 10.2),
sua tradução de chaves, limites de validação no Hub, máquina de entrega/ack e testes
de código fonte dos handlers e geradores de telemetria.
"""
import json
import math
import pathlib
import unittest

SRC_ROOT = pathlib.Path(__file__).resolve().parents[2] / "ESP32S3-HUB"


class ReliableMailboxModel:
    def __init__(self):
        self.payload = ""
        self.revision = 0
        self.ack = 0
        self.awaiting = False
        self.deliveries = 0

    def queue_reliable(self, inner_json: str, label: str = "Distance") -> int:
        self.revision += 1
        if self.revision == 0:
            self.revision = 1
        self.awaiting = True
        self.deliveries = 0
        self.payload = f'{{"cmd_id":{self.revision},{inner_json}}}'
        return self.revision

    def take_reliable(self) -> str:
        if self.awaiting and len(self.payload) > 0:
            self.deliveries += 1
            return self.payload
        return "{}"

    def ack_reliable(self, reported_ack: int, label: str = "Distance") -> bool:
        self.ack = reported_ack
        if self.awaiting and reported_ack != 0 and reported_ack == self.revision:
            self.awaiting = False
            self.payload = ""
            return True
        return False

    def mailbox_pending(self) -> bool:
        return self.awaiting


def translate_distance_command(json_str: str, comm_on: bool = True):
    """Espelha o bloco de comando da distancia em Commands.h."""
    try:
        data = json.loads(json_str)
    except Exception:
        return None, False

    inner_parts = []
    found = False

    if "distanceOffsetMm" in data:
        try:
            val = float(data["distanceOffsetMm"])
            if -50.0 <= val <= 200.0:
                inner_parts.append(f'"offset_mm":{data["distanceOffsetMm"]}')
                found = True
        except (ValueError, TypeError):
            pass

    if "distanceSamplePeriodMs" in data:
        try:
            val = int(data["distanceSamplePeriodMs"])
            if 100 <= val <= 60000:
                inner_parts.append(f'"sample_period":{data["distanceSamplePeriodMs"]}')
                found = True
        except (ValueError, TypeError):
            pass

    if "distanceSendPeriodMs" in data:
        try:
            val = int(data["distanceSendPeriodMs"])
            if 100 <= val <= 60000:
                inner_parts.append(f'"send_period":{data["distanceSendPeriodMs"]}')
                found = True
        except (ValueError, TypeError):
            pass

    if "distanceResetNvs" in data:
        inner_parts.append(f'"reset_nvs":{data["distanceResetNvs"]}')
        found = True

    if not found or not comm_on:
        return None, False

    return ",".join(inner_parts), True


class PiggybackDistanceProtocolTests(unittest.TestCase):
    def test_queue_and_take_reliable_delivers_payload_with_cmd_id(self):
        box = ReliableMailboxModel()
        self.assertFalse(box.mailbox_pending())
        self.assertEqual("{}", box.take_reliable())

        rev = box.queue_reliable('"offset_mm":25.5')
        self.assertEqual(1, rev)
        self.assertTrue(box.mailbox_pending())

        first = box.take_reliable()
        self.assertEqual('{"cmd_id":1,"offset_mm":25.5}', first)
        self.assertEqual(1, box.deliveries)

        # Redelivery without ack keeps delivering the same payload
        second = box.take_reliable()
        self.assertEqual(first, second)
        self.assertEqual(2, box.deliveries)

    def test_matching_ack_clears_box(self):
        box = ReliableMailboxModel()
        box.queue_reliable('"offset_mm":25.5')
        self.assertTrue(box.mailbox_pending())

        cleared = box.ack_reliable(1)
        self.assertTrue(cleared)
        self.assertFalse(box.mailbox_pending())
        self.assertEqual("{}", box.take_reliable())

    def test_different_or_zero_ack_does_not_clear(self):
        box = ReliableMailboxModel()
        box.queue_reliable('"offset_mm":25.5')

        # ack 0 is sentinel for "no ack reported"
        self.assertFalse(box.ack_reliable(0))
        self.assertTrue(box.mailbox_pending())

        # stale ack from earlier command
        self.assertFalse(box.ack_reliable(99))
        self.assertTrue(box.mailbox_pending())
        self.assertNotEqual("{}", box.take_reliable())

    def test_piggyback_cycle_with_distance_push(self):
        box = ReliableMailboxModel()

        # 1. Idle node push without pending command -> response is text
        pending = box.take_reliable()
        resp = pending if pending != "{}" else "Distance data received"
        self.assertEqual("Distance data received", resp)

        # 2. Operator sends command to hub
        inner, ok = translate_distance_command('{"distanceOffsetMm":25.5}', comm_on=True)
        self.assertTrue(ok)
        cmd_id = box.queue_reliable(inner)
        self.assertEqual(1, cmd_id)

        # 3. Node next push arrives (no ack yet, node has not seen command)
        box.ack_reliable(0)
        pending = box.take_reliable()
        resp = pending if pending != "{}" else "Distance data received"
        self.assertEqual('{"cmd_id":1,"offset_mm":25.5}', resp)

        # 4. Node applies config and pushes again with &ack_cmd_id=1
        cleared = box.ack_reliable(1)
        self.assertTrue(cleared)
        pending = box.take_reliable()
        resp = pending if pending != "{}" else "Distance data received"
        self.assertEqual("Distance data received", resp)


class DistanceCommandValidationTests(unittest.TestCase):
    def test_offset_range_limits(self):
        # Valid range [-50, 200]
        inner, ok = translate_distance_command('{"distanceOffsetMm":-50.0}')
        self.assertTrue(ok)
        self.assertEqual('"offset_mm":-50.0', inner)

        inner, ok = translate_distance_command('{"distanceOffsetMm":200.0}')
        self.assertTrue(ok)
        self.assertEqual('"offset_mm":200.0', inner)

        inner, ok = translate_distance_command('{"distanceOffsetMm":25.5}')
        self.assertTrue(ok)
        self.assertEqual('"offset_mm":25.5', inner)

        # Out of range
        _, ok = translate_distance_command('{"distanceOffsetMm":-50.1}')
        self.assertFalse(ok)

        _, ok = translate_distance_command('{"distanceOffsetMm":200.1}')
        self.assertFalse(ok)

    def test_period_range_limits(self):
        # Valid range [100, 60000]
        inner, ok = translate_distance_command('{"distanceSamplePeriodMs":100}')
        self.assertTrue(ok)
        self.assertEqual('"sample_period":100', inner)

        inner, ok = translate_distance_command('{"distanceSendPeriodMs":60000}')
        self.assertTrue(ok)
        self.assertEqual('"send_period":60000', inner)

        # Out of range
        _, ok = translate_distance_command('{"distanceSamplePeriodMs":99}')
        self.assertFalse(ok)

        _, ok = translate_distance_command('{"distanceSendPeriodMs":60001}')
        self.assertFalse(ok)

    def test_multiple_keys_combined(self):
        cmd = '{"distanceOffsetMm":15.0,"distanceSamplePeriodMs":500,"distanceSendPeriodMs":1000}'
        inner, ok = translate_distance_command(cmd)
        self.assertTrue(ok)
        self.assertEqual('"offset_mm":15.0,"sample_period":500,"send_period":1000', inner)

    def test_reset_nvs_command(self):
        inner, ok = translate_distance_command('{"distanceResetNvs":1}')
        self.assertTrue(ok)
        self.assertEqual('"reset_nvs":1', inner)

    def test_comm_disabled_discards_command(self):
        inner, ok = translate_distance_command('{"distanceOffsetMm":25.5}', comm_on=False)
        self.assertFalse(ok)
        self.assertIsNone(inner)


class DistanceHubSourceContractTests(unittest.TestCase):
    def read(self, rel: str) -> str:
        return (SRC_ROOT / rel).read_text(encoding="utf-8")

    def test_appcontext_has_mailbox_and_echo_variables(self):
        app = self.read("src/core/AppContext.h")
        self.assertIn("ReliableMailbox distanceBox;", app)
        self.assertIn("float    distanceOffsetMm = NAN;", app)
        self.assertIn("uint32_t distanceSamplePeriodMs = 0;", app)
        self.assertIn("uint32_t distanceSendPeriodMs = 0;", app)
        self.assertIn("bool     distanceEchoSeen = false;", app)

    def test_commands_translates_and_validates_distance(self):
        cmd = self.read("src/protocol/Commands.h")
        self.assertIn("distanceOffsetMm", cmd)
        self.assertIn('\\"offset_mm\\":', cmd)
        self.assertIn("distanceSamplePeriodMs", cmd)
        self.assertIn('\\"sample_period\\":', cmd)
        self.assertIn("distanceSendPeriodMs", cmd)
        self.assertIn('\\"send_period\\":', cmd)
        self.assertIn("distanceResetNvs", cmd)
        self.assertIn('\\"reset_nvs\\":', cmd)
        self.assertIn("-50.0f", cmd)
        self.assertIn("200.0f", cmd)
        self.assertIn("queueReliable(distanceBox", cmd)
        # resetVariables clears distanceBox
        self.assertIn('distanceBox.awaiting = false; distanceBox.payload = "";', cmd)

    def test_http_server_handles_distance_piggyback(self):
        http = self.read("src/network/HttpServer.h")
        self.assertIn('hasParam("offset")', http)
        self.assertIn('hasParam("sample_ms")', http)
        self.assertIn('hasParam("send_ms")', http)
        self.assertIn('ackReliable(distanceBox, readAckParam(request), "Distance");', http)
        self.assertIn("String pending = takeReliable(distanceBox);", http)
        self.assertIn('request->send(200, "application/json", pending);', http)
        self.assertIn('request->send(200, "text/plain", "Distance data received");', http)

    def test_telemetry_emits_distance_echoes_conditionally_and_pending_always(self):
        tel = self.read("src/sensor/Telemetry.h")
        self.assertIn("bool distancePending = mailboxPending(distanceBox);", tel)
        self.assertIn('\\"DistanceCommandPending\\":', tel)
        self.assertIn('\\"DistanceOffsetMm\\":', tel)
        self.assertIn('\\"DistanceSamplePeriodMs\\":', tel)
        self.assertIn('\\"DistanceSendPeriodMs\\":', tel)
        # Check echo seen expiry when leaving presence window
        self.assertIn("if (distanceEchoSeen && (millis() - distanceSensorLastUpdate > DISTANCE_PRESENCE_TIMEOUT))", tel)
        self.assertIn("distanceEchoSeen = false;", tel)


if __name__ == "__main__":
    unittest.main(verbosity=2)
