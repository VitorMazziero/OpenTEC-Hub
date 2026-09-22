#!/usr/bin/env python3
"""H01 contract checks for the thermostatic bath node."""
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2] / "ESP32S3-HUB"


class BathContractTests(unittest.TestCase):
    def read(self, rel):
        return (ROOT / rel).read_text(encoding="utf-8")

    def test_registry_and_mailbox_are_present(self):
        app = self.read("src/core/AppContext.h")
        boxes = self.read("src/protocol/Mailboxes.h")
        http = self.read("src/network/HttpServer.h")
        self.assertIn("DEV_BATH", app)
        self.assertIn('{ "bath",', app)
        self.assertIn("BathCommandCoordinator bathLink", app)
        self.assertIn("bathLink.seed(bathBase)", boxes)
        self.assertNotIn("bathBox", app + boxes + http)
        self.assertIn('server.on("/bathData"', http)
        self.assertIn('server.on("/bathCommand"', http)

    def test_hello_nodes_and_diag_are_generic_for_bath(self):
        app = self.read("src/core/AppContext.h")
        http = self.read("src/network/HttpServer.h")
        diag = self.read("src/network/NodeDiagTask.h")
        telemetry = self.read("src/sensor/Telemetry.h")
        self.assertIn('devName == "bath"', http)
        self.assertIn("i == DEV_BATH", http)
        self.assertIn('appendNodeIdentity(jsonResponse, "Bath"', telemetry)
        self.assertIn("DEV_COUNT", diag)
        self.assertIn("DEV_BATH", app)

    def test_push_validation_precedes_ack_and_mutation(self):
        http = self.read("src/network/HttpServer.h")
        handler = http[http.index('server.on("/bathData"'):http.index('// Handler de GET /pumpData')]
        self.assertLess(handler.index('request->send(400'), handler.index('bathLink.onReport(report'))
        self.assertLess(handler.index('request->send(400'), handler.index('bathSp = sp'))
        for field in ("sp", "known", "target", "state", "phase", "err", "pv", "pv_ok",
                      "display_sp", "display_sp_ok", "sp_source", "mode", "guard", "dev",
                      "dev_ok", "time", "ack_cmd_id", "rej_cmd_id", "rej_err", "sp_min",
                      "sp_max"):
            self.assertIn(f'hasParam("{field}")', handler)

    def test_push_is_bound_to_registered_compatible_node_and_known_enums(self):
        http = self.read("src/network/HttpServer.h")
        handler = http[http.index('server.on("/bathData"'):http.index('// Handler de GET /pumpData')]
        self.assertIn("registeredBath.registered", handler)
        self.assertIn("registeredBath.ip == remoteIp", handler)
        self.assertIn("bathNodeVersionSupported(registeredBath.version)", handler)
        self.assertIn('request->send(403', handler)
        self.assertIn("bathTelemetryEnumsValid(state, phase, guard)", handler)

    def test_done_is_bound_to_the_setpoint_slot_not_the_last_ack(self):
        coordinator = self.read("src/control/BathCommandCoordinator.cpp")
        # C1: completion follows the setpoint's own id; an operation ACK cannot hide it.
        self.assertIn("completionId_ == r.ackCmdId", coordinator)
        self.assertIn("completionAcked_ = true", coordinator)
        self.assertIn("lastDoneId_ = completionId_", coordinator)

    def test_owner_header_and_prioritized_payload(self):
        http = self.read("src/network/HttpServer.h")
        handler = http[http.index('server.on("/bathData"'):http.index('// Handler de GET /pumpData')]
        self.assertIn('response->addHeader("X-Hub-Owner"', handler)
        self.assertIn("bathTakePayload()", handler)
        coordinator = self.read("src/control/BathCommandCoordinator.cpp")
        self.assertIn("stop_.pending ? &stop_", coordinator)
        self.assertIn(": operation_.pending ? &operation_", coordinator)
        self.assertIn(": setpoint_.pending ? &setpoint_", coordinator)

    def test_aggregate_frame_publishes_bath_presence_and_mailbox(self):
        telemetry = self.read("src/sensor/Telemetry.h")
        for key in ("BathOnline", "BathCommEnabled", "BathCommandPending",
                    "BathCommandId", "BathCommandAck", "BathCommandCompletionPending",
                    "BathCommandLastSentId", "BathCommandLastDoneId",
                    "BathCommandCompletionAgeMs"):
            self.assertIn(f'\\"{key}\\"', telemetry)


if __name__ == "__main__":
    unittest.main(verbosity=2)
