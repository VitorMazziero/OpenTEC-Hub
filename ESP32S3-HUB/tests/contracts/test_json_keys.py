#!/usr/bin/env python3
"""Modelo do parser manual do Hub.

Espelha getValueFromJson() (protocol/Mailboxes.h) e JsonUtils::getRaw()
(protocol/JsonUtils.cpp), que compartilham a mesma busca por chave.

O parser manual foi mantido de proposito: e o que rodou em campo na v7 e na v8.
O que estes testes fixam e o unico defeito real dele - indexOf encontrava o
token dentro de um VALOR string, entao {"pump_command":"start","mode":2} fazia
o Hub enxergar um "start" de biomassa e enfileirar um comando fantasma para
outro dispositivo. Um token so vale como chave quando o caractere significativo
anterior e '{' ou ','.
"""
import unittest


def is_key_position(json: str, quote_pos: int) -> bool:
    index = quote_pos - 1
    while index >= 0 and json[index].isspace():
        index -= 1
    return index < 0 or json[index] in "{,"


def get_value(json: str, key: str) -> str:
    token = '"' + key + '"'
    key_pos = json.find(token)
    while key_pos != -1 and not is_key_position(json, key_pos):
        key_pos = json.find(token, key_pos + 1)
    if key_pos == -1:
        return ""
    colon = json.find(":", key_pos + len(key) + 2)
    if colon == -1:
        return ""
    start = colon + 1
    while start < len(json) and json[start].isspace():
        start += 1
    if start >= len(json):
        return ""
    if json[start] == '"':
        start += 1
        end = json.find('"', start)
        if end == -1:
            return ""
    else:
        end = json.find(",", start)
        if end == -1:
            end = json.find("}", start)
            if end == -1:
                end = len(json)
    return json[start:end].strip()


# Frames que o aplicativo realmente emite. Nenhum pode mudar de leitura.
REAL_FRAMES = {
    '{"start":1}': {"start": "1"},
    '{"stop":1}': {"stop": "1"},
    '{"blank":1}': {"blank": "1"},
    '{"low":10,"high":90}': {"low": "10", "high": "90"},
    '{"servoPollMs":2000}': {"servoPollMs": "2000"},
    '{"resetServoEnergy":1}': {"resetServoEnergy": "1"},
    '{"servoComm":0}': {"servoComm": "0"},
    '{"flowSetpoint":12.5,"valve_1":1,"valve_2":0,"v_Flow":0}': {
        "flowSetpoint": "12.5", "valve_1": "1", "valve_2": "0", "v_Flow": "0",
    },
    '{"motorSetpoint":300}': {"motorSetpoint": "300"},
    '{"pHSetpoint":7.0,"pHError":0.17}': {"pHSetpoint": "7.0", "pHError": "0.17"},
    '{"agitatorOn":1,"agitatorPercent":80,"agitatorDir":1}': {
        "agitatorOn": "1", "agitatorPercent": "80", "agitatorDir": "1",
    },
    '{"num_segments":2,"t0":0,"q0":1.5,"t1":10,"q1":2.0}': {
        "num_segments": "2", "t0": "0", "q0": "1.5", "t1": "10", "q1": "2.0",
    },
    '{"servoPollMs" : 1500}': {"servoPollMs": "1500"},
    # Quadro real de calibracao do fluxometro (2026-09-11): a1/b1 em notacao cientifica,
    # InvariantCulture, os oito termos e maxFlow num unico quadro.
    '{"maxFlow":50.0,"a1":-1.2E-05,"b1":0.00034,"k1":2.0,"f1":3.0,"c1":4.0,"k2":0.0,"f2":5.0,"c2":1.0}': {
        "maxFlow": "50.0", "a1": "-1.2E-05", "b1": "0.00034", "k1": "2.0", "f1": "3.0", "c1": "4.0",
        "k2": "0.0", "f2": "5.0", "c2": "1.0",
    },
    # Comandos de configuracao do sensor de distancia (Hub 10.2 / v11)
    '{"distanceOffsetMm":25.5}': {"distanceOffsetMm": "25.5"},
    '{"distanceSamplePeriodMs":200,"distanceSendPeriodMs":1000}': {
        "distanceSamplePeriodMs": "200", "distanceSendPeriodMs": "1000",
    },
    '{"distanceResetNvs":1}': {"distanceResetNvs": "1"},
    # Comandos de sintonia do fluxometro (Hub 10.2 / v11)
    '{"flowKp":0.8,"flowKi":0.05,"flowRampRate":1.5}': {
        "flowKp": "0.8", "flowKi": "0.05", "flowRampRate": "1.5",
    },
    '{"flowFfGain":0.025,"flowFfOffset":-1.2}': {
        "flowFfGain": "0.025", "flowFfOffset": "-1.2",
    },
    # Comandos de calibracao e PID da bomba (Hub 10.2 / 3.9)
    '{"pumpSlope":0.028,"pumpIntercept":1.5}': {
        "pumpSlope": "0.028", "pumpIntercept": "1.5",
    },
    '{"pumpPidKp":1.2,"pumpPidKi":0.05,"pumpPidKd":0.01}': {
        "pumpPidKp": "1.2", "pumpPidKi": "0.05", "pumpPidKd": "0.01",
    },
    # Comandos de configuracao da biomassa (Hub 10.2 / v11)
    '{"biomassIt":2}': {"biomassIt": "2"},
    '{"biomassPwm":45.0}': {"biomassPwm": "45.0"},
    '{"biomassGear":1}': {"biomassGear": "1"},
    '{"biomassEma":0.85}': {"biomassEma": "0.85"},
    '{"biomassProbePeriodMs":500}': {"biomassProbePeriodMs": "500"},
}


class JsonKeyTests(unittest.TestCase):
    def test_real_frames_are_unchanged(self):
        for frame, expected in REAL_FRAMES.items():
            for key, value in expected.items():
                self.assertEqual(value, get_value(frame, key), msg=frame)

    def test_key_name_inside_a_string_value_is_not_a_key(self):
        # O defeito historico: "start" dentro do valor de pump_command fazia o
        # parser devolver o valor do "mode" seguinte, e o Hub enfileirava um
        # comando de biomassa que ninguem pediu.
        frame = '{"pump_command":"start","mode":2}'
        self.assertEqual("", get_value(frame, "start"))
        self.assertEqual("start", get_value(frame, "pump_command"))
        self.assertEqual("2", get_value(frame, "mode"))

        frame = '{"pump_command":"stop","mode":0}'
        self.assertEqual("", get_value(frame, "stop"))
        self.assertEqual("0", get_value(frame, "mode"))

    def test_prefix_keys_do_not_collide(self):
        frame = '{"p1":1.0,"p10":10.0,"t1":5,"t10":50}'
        self.assertEqual("1.0", get_value(frame, "p1"))
        self.assertEqual("10.0", get_value(frame, "p10"))
        self.assertEqual("5", get_value(frame, "t1"))
        self.assertEqual("50", get_value(frame, "t10"))

    def test_absent_key_returns_empty(self):
        self.assertEqual("", get_value('{"motorSetpoint":300}', "servoPollMs"))


class DistanceSourceContractTests(unittest.TestCase):
    def read(self, rel):
        import pathlib
        src_root = pathlib.Path(__file__).resolve().parents[2] / "ESP32S3-HUB"
        return (src_root / rel).read_text(encoding="utf-8")

    def test_distance_handler_calls_ack_and_take_reliable(self):
        http = self.read("src/network/HttpServer.h")
        self.assertIn('ackReliable(distanceBox', http)
        self.assertIn('takeReliable(distanceBox', http)
        self.assertIn('readAckParam(request)', http)

    def test_telemetry_emits_distance_echoes_and_pending(self):
        tel = self.read("src/sensor/Telemetry.h")
        self.assertIn('\\"DistanceOffsetMm\\"', tel)
        self.assertIn('\\"DistanceSamplePeriodMs\\"', tel)
        self.assertIn('\\"DistanceSendPeriodMs\\"', tel)
        self.assertIn('\\"DistanceCommandPending\\"', tel)

    def test_telemetry_emits_flowmeter_echoes(self):
        tel = self.read("src/sensor/Telemetry.h")
        self.assertIn('\\"FlowKp\\"', tel)
        self.assertIn('\\"FlowKi\\"', tel)
        self.assertIn('\\"FlowFfGain\\"', tel)
        self.assertIn('\\"FlowFfOffset\\"', tel)
        self.assertIn('\\"FlowRampRate\\"', tel)
        self.assertIn('\\"FlowOutput\\"', tel)
        self.assertIn('\\"FlowSetpointCorrected\\"', tel)
        self.assertIn('\\"FlowmeterBootId\\"', tel)

    def test_telemetry_emits_pump_echoes(self):
        tel = self.read("src/sensor/Telemetry.h")
        self.assertIn('\\"PumpSlope\\"', tel)
        self.assertIn('\\"PumpIntercept\\"', tel)

    def test_telemetry_emits_biomass_echoes(self):
        tel = self.read("src/sensor/Telemetry.h")
        self.assertIn('\\"BiomassGear\\"', tel)
        self.assertIn('\\"BiomassEma\\"', tel)
        self.assertIn('\\"BiomassProbePeriodMs\\"', tel)


if __name__ == "__main__":
    unittest.main(verbosity=2)
