#!/usr/bin/env python3
"""Contract checks for pump 3.13: integration contract with the Hub (see docs/PROTOCOL.md)."""

from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1] / "firmware" / "peristaltic-pump" / "src"


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


class PureHeaders(unittest.TestCase):
    def test_headers_compiled_on_the_pc_do_not_include_arduino(self):
        for path in ("protocol/JsonScan.h", "control/DoseMath.h", "control/FrameValidate.h"):
            text = read(path)
            self.assertNotIn("Arduino.h", text, path)
            self.assertIsNone(re.search(r"String", text), path)
            self.assertNotIn("Serial", text, path)

    def test_json_helpers_live_only_in_jsonscan(self):
        hub = read("network/HubClient.h")
        self.assertNotIn("const char* findJsonValueStart(const char* json, const char* key) {", hub)
        self.assertIn('#include "../protocol/JsonScan.h"', read("core/FirmwareApp.cpp"))


class Refusals(unittest.TestCase):
    def test_whole_frame_is_validated_before_anything_is_applied(self):
        ctl = read("control/OperationController.h")
        body = ctl[ctl.index("bool processJsonCommand(String json, CommandSource src)"):]
        self.assertLess(body.index("validateFrame(json.c_str(), ctx)"), body.index('getJsonStringValue(json, "command")'))
        self.assertIn("return false;", body[:body.index('getJsonStringValue(json, "command")')])

    def test_refused_hub_command_gets_no_ack_and_is_not_reprocessed(self):
        hub = read("network/HubClient.h")
        handler = hub[hub.index("void handleHubCommandBody(const String& body)"):]
        self.assertIn("hubCommandId == g_lastRejectedHubCommandId", handler)
        rejected = handler[handler.index("} else {"):handler.index("return;\n    }\n\n    processJsonCommand")]
        self.assertIn("g_lastRejectedHubCommandId = hubCommandId;", rejected)
        self.assertNotIn("g_lastAppliedHubCommandId", rejected)

    def test_reject_codes_match_the_protocol(self):
        validate = read("control/FrameValidate.h") + read("control/DoseMath.h")
        for code in ("mode_range", "seg_range", "cal_busy", "cal_incomplete", "cal_st_range", "cal_shape",
                     "dose_ml", "dose_period", "dose_speed", "dose_rate", "dose_total", "dose_short",
                     "dose_fit", "dose_busy", "dose_dead", "dose_range"):
            self.assertIn(f'"{code}"', validate)
        protocol = (ROOT.parents[2] / "docs" / "PROTOCOL.md").read_text(encoding="utf-8")
        for code in ("mode_range", "cal_shape", "dose_fit", "dose_busy", "dose_range"):
            self.assertIn(code, protocol)

    def test_local_http_reports_a_refusal(self):
        self.assertIn('server.send(422, "text/plain", String("Rejected: ") + g_lastRejectCode);',
                      read("network/HubClient.h"))


class PushFields(unittest.TestCase):
    def test_push_carries_the_313_fields(self):
        hub = read("network/HubClient.h")
        push = hub[hub.index("void sendDataToHub()"):hub.index("void pollHubForCommands()")]
        for key in ("drv", "sens", "wet", "t_min", "up", "boot", "rst", "rej_cmd_id", "rej_err",
                    "d_act", "d_pulse", "d_ml", "d_per", "d_spd", "d_rate", "d_tot", "d_dead",
                    "d_done", "d_n", "d_next", "d_short", "cal_crc", "ack_cmd_id", "vol", "cyc_vol"):
            self.assertRegex(push, rf"[?&]{key}=", key)

    def test_push_is_never_sent_truncated(self):
        hub = read("network/HubClient.h")
        self.assertIn("char url[1024];", hub)
        self.assertIn("g_pushTruncations++;", hub)

    def test_calibration_echo_is_sent_on_change_or_every_tenth_push(self):
        hub = read("network/HubClient.h")
        self.assertIn("pushesSinceCal >= 9 || g_pumpCal.crc32 != lastSentCrc", hub)
        self.assertIn("if (sendCal) {", hub)

    def test_pot_echo_is_off_while_dosing(self):
        self.assertIn("(disablePot || hasUsbSpeed || g_doseActive) ? 0 : 1", read("network/HubClient.h"))

    def test_push_response_is_handled_as_a_command(self):
        hub = read("network/HubClient.h")
        self.assertIn("if (body.length() > 0 && body[0] == '{') handleHubCommandBody(body);", hub)


class DriveAndReboots(unittest.TestCase):
    def test_drive_states(self):
        app = read("core/FirmwareApp.cpp")
        for name, value in (("DRV_STOPPED", 0), ("DRV_POTS", 1), ("DRV_MANUAL_SPEED", 2),
                            ("DRV_PROFILE_WAITING", 3), ("DRV_PROFILE_RUNNING", 4),
                            ("DRV_DOSE_IDLE", 5), ("DRV_DOSE_PULSE", 6), ("DRV_OTA", 7)):
            self.assertRegex(app, rf"{name} = {value}\b")
        self.assertIn("g_drive = g_dosePulseOn ? DRV_DOSE_PULSE : DRV_DOSE_IDLE;", read("core/Lifecycle.h"))
        self.assertIn("g_motorAllowed = allowRun;", read("core/Lifecycle.h"))

    def test_boot_count_and_reset_reason(self):
        setup = read("core/Lifecycle.h")
        self.assertIn("g_prefs.putUInt(NVS_KEY_BOOT_COUNT, g_bootCount);", setup)
        self.assertIn("esp_reset_reason()", setup)


class HubSpeedDeadline(unittest.TestCase):
    def test_hub_speed_without_deadline_gets_the_default(self):
        ctl = read("control/OperationController.h")
        self.assertIn("src == CommandSource::Hub", ctl)
        self.assertIn("HUB_SPEED_DEFAULT_MS", ctl)
        self.assertIn("const unsigned long HUB_SPEED_DEFAULT_MS = 300000;", read("core/FirmwareApp.cpp"))


class Diag(unittest.TestCase):
    def test_diag_respects_the_hub_cache(self):
        hub = read("network/HubClient.h")
        self.assertIn("static const size_t DIAG_MAX_BYTES = 511;", hub)
        diag = hub[hub.index("void handleDiag()"):hub.index("void handleCommand()")]
        template = "".join(re.findall(r'"((?:[^"\\]|\\.)*)"', diag[:diag.index("static_cast<unsigned long>(millis()")]))
        # Format string with generous widths substituted must stay under the Hub's cache.
        worst = (template.replace("\\\"", '"')
                 .replace("%lu", "4294967295").replace("%u", "4294967295").replace("%d", "-2147483648")
                 .replace("%.3f", "-99999.999").replace("%08X", "FFFFFFFF").replace("%s", "x" * 20))
        worst = worst.replace("PUMP_FW_VERSION", "3.13").replace("PUMP_FW_BUILD", "3.13")
        self.assertLess(len(worst), 512, len(worst))


class DosePulseTiming(unittest.TestCase):
    def test_pulse_deadline_is_armed_on_core0_when_the_motor_starts(self):
        pwm = read("hardware/PwmRuntime.h")
        self.assertIn("g_doseArmOnMs != 0 && enabled && fabsf(s) >= ENABLE_EPS", pwm)
        self.assertIn("portENTER_CRITICAL(&g_doseMux);", pwm)
        dose = read("control/DoseController.h")
        self.assertIn("doseSetPulseTiming(static_cast<uint32_t>(onMs), 0);", dose)

    def test_dead_time_is_persisted_and_counted(self):
        dose = read("control/DoseController.h")
        self.assertIn('DOSE_KEY_DEAD = "d_dead"', dose)
        self.assertIn("doseCountedMl(g_dose.rateMlMin, static_cast<float>(g_dosePulseRunMs), g_dose.deadMs)", dose)
        self.assertIn("g_doseShortMl +=", dose)


if __name__ == "__main__":
    unittest.main()
