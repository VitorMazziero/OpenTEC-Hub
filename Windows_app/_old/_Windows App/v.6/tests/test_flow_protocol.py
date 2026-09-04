import json
import sys
import threading
import unittest
from pathlib import Path


APP_ROOT = Path(__file__).resolve().parents[1]
if str(APP_ROOT) not in sys.path:
    sys.path.insert(0, str(APP_ROOT))

from communication.data_parser import DataParser, ParserConfig
from communication.connection_manager import ConnectionManager, _FlushCommandsRequest
from communication.transport import WiFiConfig, WiFiTransport


class _Response:
    def __init__(self, status_code, text):
        self.status_code = status_code
        self.text = text


class _Session:
    def __init__(self, response):
        self.response = response
        self.posts = []

    def post(self, url, **kwargs):
        self.posts.append((url, kwargs))
        return self.response


class FlowProtocolTests(unittest.TestCase):
    def test_send_command_wakes_worker_without_waiting_for_sensor_poll(self):
        class _ManagerStub:
            def __init__(self):
                self._cmd_lock = threading.Lock()
                self._cmd_buffer = {}
                self.requests = []

            def _post_request(self, request):
                self.requests.append(request)

        manager = _ManagerStub()
        ConnectionManager.send_command(manager, {"valve_1": 0, "flowSetpoint": 0})

        self.assertEqual(manager._cmd_buffer, {"valve_1": 0, "flowSetpoint": 0})
        self.assertEqual(len(manager.requests), 1)
        self.assertIsInstance(manager.requests[0], _FlushCommandsRequest)

    def test_hub_flow_status_is_parsed_for_kla_guard(self):
        parser = DataParser(ParserConfig())
        payload = {
            "Time": 12.0,
            "FlowmeterOnline": True,
            "FlowControlEnabled": True,
            "FlowCommandPending": False,
            "FlowCommandId": 17,
            "FlowCommandAck": 17,
            "FlowCommandDeliveries": 1,
            "FlowCommandAgeMs": 0,
            "FlowCommandSource": "hub",
            "FlowSetpoint": 2.5,
            "Valve1": 0,
            "Valve2": 0,
            "ValveFlow": 0,
            "HubStations": 3,
        }

        self.assertTrue(parser.parse(json.dumps(payload)))
        readings = parser.readings
        self.assertTrue(readings.flowmeter_online)
        self.assertFalse(readings.flow_command_pending)
        self.assertEqual(readings.flow_command_id, 17)
        self.assertEqual(readings.flow_command_ack, 17)
        self.assertEqual(readings.flow_valve_1, 0)
        self.assertEqual(readings.flow_valve_main, 0)
        self.assertAlmostEqual(readings.flow_setpoint, 2.5)
        self.assertEqual(readings.hub_stations, 3)

    def test_wifi_write_requires_http_200_and_ok_body(self):
        transport = WiFiTransport(WiFiConfig(ip="192.168.4.1"))

        transport._session = _Session(_Response(500, "OK"))
        self.assertFalse(transport.write('{"valve_1":0}'))

        transport._session = _Session(_Response(200, "not accepted"))
        self.assertFalse(transport.write('{"valve_1":0}'))

        session = _Session(_Response(200, "OK"))
        transport._session = session
        self.assertTrue(transport.write('{"valve_1":0}'))
        self.assertEqual(len(session.posts), 1)


if __name__ == "__main__":
    unittest.main()
