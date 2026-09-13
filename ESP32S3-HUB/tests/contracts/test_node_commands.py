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
        # Mirrors Commands.h: only the value 1 is forwarded (D02, 2026-09-13).
        try:
            if int(data["distanceResetNvs"]) == 1:
                inner_parts.append('"reset_nvs":1')
                found = True
        except (ValueError, TypeError):
            pass

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

    def test_reset_nvs_zero_is_not_forwarded(self):
        inner, ok = translate_distance_command('{"distanceResetNvs":0}')
        self.assertFalse(ok)
        self.assertIsNone(inner)
        src = (SRC_ROOT / "src/protocol/Commands.h").read_text(encoding="utf-8")
        self.assertIn('val.toInt() == 1', src)
        self.assertIn('"reset_nvs\\":1', src)

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
        self.assertIn("if (distanceEchoSeen && (millis() - distanceSensorLastUpdate > distancePresenceWindowMs(distanceSendPeriodMs)))", tel)
        self.assertIn("distanceEchoSeen = false;", tel)

    def test_distance_handler_has_no_stagnation_filter(self):
        # PONTOS §1.1: a validade da leitura vem do nó (distance=-1 em falha do VL53L0X).
        # O filtro de cinco amostras iguais herdado do v1 pausava a lógica de espuma com
        # nível parado e não pode voltar.
        http = self.read("src/network/HttpServer.h")
        self.assertNotIn("stagnated", http)
        self.assertNotIn("lastAccepted", http)
        self.assertIn("distanceSensorValue = (newDistance >= 0.0f) ? newDistance : -1.0f;", http)

    def test_telemetry_distance_presence_is_independent_of_reading(self):
        # PONTOS §1.1: DistanceOnline e os ecos seguem a janela de presença; só a chave
        # Distance depende da leitura declarada válida pelo nó (distanceSensorValue >= 0).
        tel = self.read("src/sensor/Telemetry.h")
        self.assertIn(
            "bool distanceOnline = snapDistanceComm &&" + chr(10) +
            "                        (millis() - snapDistanceUpdate <= distancePresenceWindowMs(snapDistanceSendPeriodMs));",
            tel,
        )
        self.assertIn("bool validDistance = distanceOnline && snapDistanceValue >= 0.0f;", tel)

        self.assertIn('\\"DistanceOnline\\":" + String(distanceOnline ? "true" : "false")', tel)
        self.assertIn("if (distanceOnline && snapDistanceEchoSeen) {", tel)
        # A chave Distance continua condicionada à leitura válida, e os ecos não ficam
        # aninhados dentro desse bloco.
        distance_block = tel[tel.index("if (validDistance) {"):tel.index("if (distanceOnline && snapDistanceEchoSeen) {")]
        self.assertIn('\\"Distance\\":', distance_block)
        self.assertNotIn("DistanceOffsetMm", distance_block)

    def test_distance_presence_window_follows_send_period(self):
        # D03 (2026-09-13): 2.5 x send_ms, floor 3 s; interlock window untouched.
        app = self.read("src/core/AppContext.h")
        self.assertIn("inline unsigned long distancePresenceWindowMs(uint32_t sendPeriodMs)", app)
        self.assertIn("(unsigned long)(sendPeriodMs * 2.5f)", app)
        self.assertIn("dyn > DISTANCE_PRESENCE_TIMEOUT ? dyn : DISTANCE_PRESENCE_TIMEOUT", app)
        self.assertIn("const unsigned long DISTANCE_TIMEOUT = 1200;", app)
        http = self.read("src/network/HttpServer.h")
        self.assertIn("now - distanceSensorLastUpdate <= distancePresenceWindowMs(distanceSendPeriodMs)", http)
        for src in (app, self.read("src/sensor/Telemetry.h"), http):
            self.assertNotIn("<= DISTANCE_PRESENCE_TIMEOUT)", src)

    def test_reliable_mailboxes_are_seeded_per_hub_boot(self):
        # Um no que ficou ligado durante o reboot do Hub continua ecoando o ack_cmd_id da
        # sessao anterior. Se o contador recomecasse em 1, o primeiro comando novo seria
        # dado como confirmado antes da entrega (ackReliable roda antes de takeReliable).
        mail = self.read("src/protocol/Mailboxes.h")
        runtime = self.read("src/core/Runtime.h")
        self.assertIn("void seedReliableMailboxes()", mail)
        self.assertIn("&distanceBox, &biomassBox, &pumpBox, &agitatorBox", mail)
        self.assertIn("esp_random()", mail[mail.index("void seedReliableMailboxes()"):])
        self.assertIn("seedReliableMailboxes();", runtime)
        # A semeadura precisa vir depois da criacao de cmdMutex e antes do Wi-Fi subir.
        self.assertLess(runtime.index("cmdMutex = xSemaphoreCreateMutex();"), runtime.index("seedReliableMailboxes();"))
        self.assertLess(runtime.index("seedReliableMailboxes();"), runtime.index("startWiFi();"))

    def test_telemetry_reports_frame_high_water_mark(self):
        # PONTOS §1.2: a marca d'água do quadro agregado sai na serial para a medição de
        # bancada, e avisa quando ultrapassa a reserva (realocação a cada ciclo).
        tel = self.read("src/sensor/Telemetry.h")
        self.assertIn("static size_t frameHighWater = 0;", tel)
        self.assertIn("if (frameLen > HUB_TELEMETRY_JSON_RESERVE) {", tel)
        self.assertIn('ESP32_AVISO("Quadro agregado com "', tel)


def translate_pump_command(json_str: str, comm_on: bool = True):
    """Espelha o bloco de comando da bomba em Commands.h."""
    try:
        data = json.loads(json_str)
    except Exception:
        return None, False

    simple_keys = [
        "pump_command", "mode", "pump_speed", "pump_speed_ms", "pump_pot", "init_t", "final_t",
        "lambda_const", "lambda_linear", "phi_linear", "lambda_exp", "phi_exp",
        "pumpSlope", "pumpIntercept", "pumpPidKp", "pumpPidKi", "pumpPidKd",
        "pumpSlopeLow", "pumpSlopeHigh", "pumpTransitionSpeed", "pumpTransitionFlow",
        "slope_low", "slope_high", "transition_speed", "transition_flow",
    ]
    allowed_commands = ("reset_volume", "start", "stop")
    parts = []
    found = False
    for k in simple_keys:
        if k in data:
            val = str(data[k])
            if k == "pump_command" and val not in allowed_commands:
                continue
            clean_k = k
            if clean_k == "pumpPidKp":
                clean_k = "pid_kp"
            elif clean_k == "pumpPidKi":
                clean_k = "pid_ki"
            elif clean_k == "pumpPidKd":
                clean_k = "pid_kd"
            elif clean_k == "pumpSlopeLow":
                clean_k = "slope_low"
            elif clean_k == "pumpSlopeHigh":
                clean_k = "slope_high"
            elif clean_k == "pumpTransitionSpeed":
                clean_k = "transition_speed"
            elif clean_k == "pumpTransitionFlow":
                clean_k = "transition_flow"
            elif clean_k.startswith("pump_"):
                clean_k = clean_k[5:]
            if clean_k == "command":
                parts.append(f'"{clean_k}":"{val}"')
            else:
                parts.append(f'"{clean_k}":{val}')
            found = True

    if not found or not comm_on:
        return None, False
    return ",".join(parts), True


def translate_biomass_command(json_str: str, comm_on: bool = True):
    """Espelha o bloco de comando da biomassa em Commands.h."""
    try:
        data = json.loads(json_str)
    except Exception:
        return None, False, []

    cmd_parts = []
    discarded = []
    found = False

    for k in ["start", "stop", "blank", "low", "high", "opt"]:
        if k in data:
            cmd_parts.append(f'"{k}":{data[k]}')
            found = True

    new_cmds = [
        ("biomassIt", "set_it"),
        ("biomassPwm", "set_pwm"),
        ("biomassGear", "set_gear"),
        ("biomassEma", "ema"),
        ("biomassProbePeriodMs", "probe_period"),
    ]
    for app_key, cmd_name in new_cmds:
        if app_key in data:
            val = data[app_key]
            if not found:
                cmd_parts.append(f'"command":"{cmd_name}","value":{val}')
                found = True
            else:
                discarded.append(app_key)

    if "biomassAutoRange" in data:
        auto_val = str(data["biomassAutoRange"]).lower()
        if not found:
            is_auto = auto_val in ("1", "true", "auto")
            cmd_parts.append(f'"command":"{"auto" if is_auto else "manual"}"')
            found = True
        else:
            discarded.append("biomassAutoRange")

    if not found or not comm_on:
        return None, False, discarded
    return ",".join(cmd_parts), True, discarded


class PumpCommandTests(unittest.TestCase):
    def read(self, rel: str) -> str:
        return (SRC_ROOT / rel).read_text(encoding="utf-8")

    def test_calibration_and_pid_translations(self):
        cmd = '{"pumpSlope":0.028,"pumpIntercept":1.5,"pumpPidKp":1.2,"pumpPidKi":0.05,"pumpPidKd":0.01}'
        inner, ok = translate_pump_command(cmd)
        self.assertTrue(ok)
        self.assertEqual('"pumpSlope":0.028,"pumpIntercept":1.5,"pid_kp":1.2,"pid_ki":0.05,"pid_kd":0.01', inner)

    def test_dual_range_calibration_routing(self):
        cmd = '{"pumpSlopeLow":0.025,"pumpSlopeHigh":0.035,"pumpTransitionSpeed":150.0,"pumpTransitionFlow":20.0}'
        inner, ok = translate_pump_command(cmd)
        self.assertTrue(ok)
        self.assertEqual('"slope_low":0.025,"slope_high":0.035,"transition_speed":150.0,"transition_flow":20.0', inner)

    def test_pump_command_whitelist_blocks_clear_nvs_and_config_verbs(self):
        # COMANDOS_DISPOSITIVOS_EXTERNOS §1.10: clear_nvs apaga calibracao e reinicia o no;
        # save/load/print_config nao sao operacao. Pelo Hub so passam os tres do app.
        for verb in ("clear_nvs", "save_config", "load_config", "print_config"):
            inner, ok = translate_pump_command('{"pump_command":"%s"}' % verb)
            self.assertFalse(ok, verb)
        for verb in ("reset_volume", "start", "stop"):
            inner, ok = translate_pump_command('{"pump_command":"%s"}' % verb)
            self.assertTrue(ok, verb)
            self.assertEqual('"command":"%s"' % verb, inner)
        cmd = self.read("src/protocol/Commands.h")
        self.assertIn('allowedPumpCommands[] = { "reset_volume", "start", "stop" };', cmd)
        self.assertIn('pump_command recusado pelo Hub', cmd)

    def test_pump_speed_ms_and_pot_are_forwarded_without_prefix(self):
        inner, ok = translate_pump_command('{"pump_speed":500,"pump_speed_ms":63000}')
        self.assertTrue(ok)
        self.assertEqual('"speed":500,"speed_ms":63000', inner)
        inner, ok = translate_pump_command('{"pump_pot":1}')
        self.assertTrue(ok)
        self.assertEqual('"pot":1', inner)
        cmd = self.read("src/protocol/Commands.h")
        self.assertIn('"pump_speed_ms", "pump_pot"', cmd)

    def test_hub_echoes_pump_pid_pot_and_cycle_volume(self):
        http = self.read("src/network/HttpServer.h")
        tel = self.read("src/sensor/Telemetry.h")
        for param in ("kp", "ki", "kd", "pot", "cyc_vol"):
            self.assertIn('hasParam("%s")' % param, http)
        for key in ("PumpPidKp", "PumpPidKi", "PumpPidKd", "PumpPotEnabled", "PumpCycleVol"):
            self.assertIn('\\"%s\\":' % key, tel)

    def test_speed_alone_without_pump_prefix_is_rejected(self):
        cmd = '{"speed":100.0}'
        inner, ok = translate_pump_command(cmd)
        self.assertFalse(ok)
        self.assertIsNone(inner)

    def test_pump_command_string_value_quoted(self):
        cmd = '{"pump_command":"reset_volume"}'
        inner, ok = translate_pump_command(cmd)
        self.assertTrue(ok)
        self.assertEqual('"command":"reset_volume"', inner)


class BiomassCommandTests(unittest.TestCase):
    def test_individual_biomass_commands(self):
        for app_key, cmd_name, val in [
            ("biomassIt", "set_it", 2),
            ("biomassPwm", "set_pwm", 45.0),
            ("biomassGear", "set_gear", 1),
            ("biomassEma", "ema", 0.85),
            ("biomassProbePeriodMs", "probe_period", 500),
        ]:
            cmd = json.dumps({app_key: val})
            inner, ok, discarded = translate_biomass_command(cmd)
            self.assertTrue(ok)
            self.assertEqual(f'"command":"{cmd_name}","value":{val}', inner)
            self.assertEqual([], discarded)

    def test_biomass_autorange_routing(self):
        # B03: auto / 1 / true -> {"command":"auto"}
        for val in ["auto", 1, True, "true"]:
            cmd = json.dumps({"biomassAutoRange": val})
            inner, ok, discarded = translate_biomass_command(cmd)
            self.assertTrue(ok, f"biomassAutoRange with {val} failed")
            self.assertEqual('"command":"auto"', inner)
            self.assertEqual([], discarded)

        # B03: manual / 0 / false -> {"command":"manual"}
        for val in ["manual", 0, False, "false"]:
            cmd = json.dumps({"biomassAutoRange": val})
            inner, ok, discarded = translate_biomass_command(cmd)
            self.assertTrue(ok, f"biomassAutoRange with {val} failed")
            self.assertEqual('"command":"manual"', inner)
            self.assertEqual([], discarded)

    def test_test_period_is_not_routed(self):
        # B12: test_period key removed from Hub Commands.h
        cmd = '{"test_period":100}'
        inner, ok, discarded = translate_biomass_command(cmd)
        self.assertFalse(ok)
        self.assertIsNone(inner)

    def test_one_command_per_revision_enforced(self):
        cmd = '{"biomassIt":2,"biomassPwm":45.0,"biomassGear":1}'
        inner, ok, discarded = translate_biomass_command(cmd)
        self.assertTrue(ok)
        self.assertEqual('"command":"set_it","value":2', inner)
        self.assertEqual(["biomassPwm", "biomassGear"], discarded)

        # biomassAutoRange is also discarded if a preceding command was already found
        cmd = '{"biomassIt":2,"biomassAutoRange":"auto"}'
        inner, ok, discarded = translate_biomass_command(cmd)
        self.assertTrue(ok)
        self.assertEqual('"command":"set_it","value":2', inner)
        self.assertEqual(["biomassAutoRange"], discarded)


class NodeCommandSourceContractTests(unittest.TestCase):
    def read(self, rel: str) -> str:
        return (SRC_ROOT / rel).read_text(encoding="utf-8")

    def test_appcontext_flow_pump_biomass_echo_variables(self):
        app = self.read("src/core/AppContext.h")
        self.assertIn("float flowmeterKp = NAN;", app)
        self.assertIn("float flowmeterKi = NAN;", app)
        self.assertIn("float flowmeterFfGain = NAN;", app)
        self.assertIn("float flowmeterFfOffset = NAN;", app)
        self.assertIn("float flowmeterRampRate = NAN;", app)
        self.assertIn("float flowmeterOutput = NAN;", app)
        self.assertIn("float flowmeterSetpointCorrected = NAN;", app)
        self.assertIn("bool  pendingFlowTransitionVoltage = false;", app)
        self.assertIn("float desiredFlowTransitionVoltage = 0.0545f;", app)
        self.assertIn("float flowmeterTransitionVoltage = NAN;", app)
        self.assertIn("bool  flowmeterEchoSeen = false;", app)
        self.assertIn("float pumpSlope = NAN;", app)
        self.assertIn("float pumpIntercept = NAN;", app)
        self.assertIn("float pumpSlopeLow = NAN;", app)
        self.assertIn("float pumpSlopeHigh = NAN;", app)
        self.assertIn("float pumpTransitionSpeed = NAN;", app)
        self.assertIn("float pumpTransitionFlow = NAN;", app)
        self.assertIn("uint32_t pumpCalCrc = 0;", app)
        self.assertIn("bool  pumpEchoSeen = false;", app)
        self.assertIn("int      biomassGear = -1;", app)
        self.assertIn("float    biomassEma = NAN;", app)
        self.assertIn("uint32_t biomassProbePeriodMs = 0;", app)
        self.assertIn("bool     biomassEchoSeen = false;", app)

    def test_mailboxes_flow_tuning_serialization_and_queueing(self):
        mb = self.read("src/protocol/Mailboxes.h")
        self.assertIn('\\"kp_flow\\":', mb)
        self.assertIn('\\"ki_flow\\":', mb)
        self.assertIn('\\"ff_gain\\":', mb)
        self.assertIn('\\"ff_offset\\":', mb)
        self.assertIn('\\"ramp_rate\\":', mb)
        self.assertIn('\\"transition_v\\":', mb)
        self.assertIn('\\"flowKp\\"', mb)
        self.assertIn('\\"flowKi\\"', mb)
        self.assertIn('\\"flowFfGain\\"', mb)
        self.assertIn('\\"flowFfOffset\\"', mb)
        self.assertIn('\\"flowRampRate\\"', mb)
        self.assertIn('\\"flowTransitionVoltage\\"', mb)

    def test_commands_pump_and_biomass_whitelists(self):
        cmd = self.read("src/protocol/Commands.h")
        self.assertIn('"pumpSlope"', cmd)
        self.assertIn('"pumpIntercept"', cmd)
        self.assertIn('"pumpPidKp"', cmd)
        self.assertIn('"pumpPidKi"', cmd)
        self.assertIn('"pumpPidKd"', cmd)
        self.assertIn('"pumpSlopeLow"', cmd)
        self.assertIn('"pumpSlopeHigh"', cmd)
        self.assertIn('"pumpTransitionSpeed"', cmd)
        self.assertIn('"pumpTransitionFlow"', cmd)
        self.assertIn('cleanKey == "pumpPidKp"', cmd)
        self.assertIn('cleanKey = "pid_kp"', cmd)
        self.assertIn('cleanKey == "pumpSlopeLow"', cmd)
        self.assertIn('cleanKey = "slope_low"', cmd)
        self.assertIn('"biomassIt"', cmd)
        self.assertIn('"biomassPwm"', cmd)
        self.assertIn('"biomassGear"', cmd)
        self.assertIn('"biomassEma"', cmd)
        self.assertIn('"biomassProbePeriodMs"', cmd)
        self.assertIn('"biomassAutoRange"', cmd)
        self.assertNotIn('"test_period"', cmd)

    def test_biomass_presence_window_and_echo_timeout(self):
        # B01: Dynamic presence window follows probe_ms (2.5x probe_ms, floor 10 s)
        app = self.read("src/core/AppContext.h")
        self.assertIn("inline unsigned long biomassPresenceWindowMs(int probePeriodMs)", app)
        self.assertIn("(unsigned long)(probePeriodMs * 2.5f)", app)
        self.assertIn("const unsigned long BIOMASS_TIMEOUT = 10000;", app)

        tel = self.read("src/sensor/Telemetry.h")
        self.assertIn("unsigned long bioWin = biomassPresenceWindowMs(snapBiomassProbePeriodMs);", tel)
        self.assertIn("if (biomassEchoSeen && (millis() - biomassLastUpdate > bioWin))", tel)
        self.assertIn("millis() - snapBiomassUpdate <= bioWin", tel)
        self.assertIn("age <= bioWin", tel)

        http = self.read("src/network/HttpServer.h")
        self.assertIn("now - biomassLastUpdate <= biomassPresenceWindowMs(biomassProbePeriodMs)", http)

    def test_httpserver_reads_flow_pump_biomass_parameters(self):
        http = self.read("src/network/HttpServer.h")
        self.assertIn('hasParam("kp")', http)
        self.assertIn('hasParam("ki")', http)
        self.assertIn('hasParam("ramp")', http)
        self.assertIn('hasParam("ff_gain")', http)
        self.assertIn('hasParam("ff_offset")', http)
        self.assertIn('hasParam("flow_output")', http)
        self.assertIn('hasParam("flow_setpoint_corrected")', http)
        self.assertIn('hasParam("transition_v")', http)
        self.assertIn('hasParam("slope")', http)
        self.assertIn('hasParam("intercept")', http)
        self.assertIn('hasParam("slope_low")', http)
        self.assertIn('hasParam("slope_high")', http)
        self.assertIn('hasParam("trans_speed")', http)
        self.assertIn('hasParam("trans_flow")', http)
        self.assertIn('hasParam("cal_crc")', http)
        self.assertIn('hasParam("gear")', http)
        self.assertIn('hasParam("ema")', http)
        self.assertIn('hasParam("probe_ms")', http)

    def test_telemetry_emits_node_echo_keys(self):
        tel = self.read("src/sensor/Telemetry.h")
        self.assertIn('\\"FlowKp\\"', tel)
        self.assertIn('\\"FlowKi\\"', tel)
        self.assertIn('\\"FlowFfGain\\"', tel)
        self.assertIn('\\"FlowFfOffset\\"', tel)
        self.assertIn('\\"FlowRampRate\\"', tel)
        self.assertIn('\\"FlowOutput\\"', tel)
        self.assertIn('\\"FlowSetpointCorrected\\"', tel)
        self.assertIn('\\"FlowmeterBootId\\"', tel)
        self.assertIn('\\"FlowTransitionVoltage\\"', tel)
        self.assertIn('\\"PumpSlope\\"', tel)
        self.assertIn('\\"PumpIntercept\\"', tel)
        self.assertIn('\\"PumpSlopeLow\\"', tel)
        self.assertIn('\\"PumpSlopeHigh\\"', tel)
        self.assertIn('\\"PumpTransitionSpeed\\"', tel)
        self.assertIn('\\"PumpTransitionFlow\\"', tel)
        self.assertIn('\\"PumpCalCrc\\"', tel)
        self.assertIn('\\"BiomassGear\\"', tel)
        self.assertIn('\\"BiomassEma\\"', tel)
        self.assertIn('\\"BiomassProbePeriodMs\\"', tel)


if __name__ == "__main__":
    unittest.main(verbosity=2)
