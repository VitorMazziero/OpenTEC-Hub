#!/usr/bin/env python3
"""Static contract checks for the pump pulse-dosing build (3.12+dose2)."""

from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1] / "firmware" / "peristaltic-pump" / "src"


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


class PumpDoseContract(unittest.TestCase):
    def test_wire_version_is_unchanged_and_build_is_distinct(self):
        app = read("core/FirmwareApp.cpp")
        self.assertIn('#define PUMP_FW_VERSION "3.12"', app)
        self.assertIn('#define PUMP_FW_BUILD "3.12+dose2"', app)
        self.assertIn("ver=3.12", read("network/HubClient.h"))

    def test_dose_state_does_not_touch_pump_config_layout(self):
        app = read("core/FirmwareApp.cpp")
        pump_config = app[app.index("struct PumpConfig"):app.index("#ifndef PUMP_DUAL_RANGE_CAL_DEFINED")]
        self.assertNotIn("dose", pump_config)
        dose = read("control/DoseController.h")
        for key in ("d_vol", "d_per", "d_spd", "d_rate", "d_tot", "d_act", "d_done", "d_n"):
            self.assertIn(f'"{key}"', dose)
            self.assertLessEqual(len(key), 15)  # NVS key limit

    def test_parameters_are_validated_before_use(self):
        dose = read("control/DoseController.h")
        for rule in ("dose_ml fora de", "dose_period_s fora de", "dose_speed fora de",
                     "dose_rate_mlmin ausente", "pulso menor que 0,5 s", "pulso nao cabe no periodo"):
            self.assertIn(rule, dose)
        self.assertIn("MIN_MOTOR_ON_TIME_MS", dose)

    def test_pulse_length_is_volume_over_measured_rate_not_calibration(self):
        dose = read("control/DoseController.h")
        self.assertIn("volume / g_dose.rateMlMin * 60000.0f", dose)
        self.assertNotIn("mlminToSpeedUnits", dose)
        self.assertNotIn("speedUnitsToMlmin", dose)

    def test_pulse_end_is_enforced_on_core0_independent_of_the_loop(self):
        pwm = read("hardware/PwmRuntime.h")
        self.assertIn("g_doseCutoffMs", pwm)
        self.assertIn("enabled = false", pwm)
        self.assertIn("volatile uint32_t g_doseCutoffMs", read("core/FirmwareApp.cpp"))

    def test_dose_drives_the_motor_and_blocks_pot_between_pulses(self):
        loop = read("core/Lifecycle.h")
        self.assertIn("doseService(now);", loop)
        self.assertIn("requestedSpeed = g_dosePulseOn ? g_dose.speed : 0.0f;", loop)
        self.assertIn("doseRestore();", loop)

    def test_operator_commands_cancel_dosing(self):
        ctl = read("control/OperationController.h")
        for reason in ('doseCancel("start")', 'doseCancel("stop")',
                       'doseCancel("mode")', 'doseCancel("speed")'):
            self.assertIn(reason, ctl)
        # The dose handler runs last so a same-frame dose:1 survives a mode/speed reset.
        self.assertTrue(ctl.rstrip().endswith("doseHandleCommand(json.c_str());\n}"))

    def test_ota_interlock_stops_the_pulse(self):
        ota = read("network/HubClient.h")
        interlock = ota[ota.index("Intertravamento de seguranca"):ota.index("Update.isRunning()")]
        self.assertIn("g_dosePulseOn = false", interlock)
        self.assertIn("g_doseCutoffMs = 0", interlock)

    def test_telemetry_exposes_dose_state_and_build(self):
        tel = read("protocol/TelemetryCodec.h")
        for key in ("dose_active", "dose_pulse", "dose_ml", "dose_period_s", "dose_speed",
                    "dose_rate_mlmin", "dose_total_ml", "dose_delivered_ml", "dose_pulses",
                    "dose_next_s", "fw_build"):
            self.assertIn(key, tel)

    def test_dose_volume_reaches_the_session_counter_the_app_reads(self):
        dose = read("control/DoseController.h")
        self.assertIn("doseAddToSession(ml);", dose)
        self.assertIn("g_cumulativeVolumeMl += ml", dose)
        # No double counting: the polynomial integration is off while dosing.
        self.assertIn("!g_doseActive", read("hardware/PwmRuntime.h"))
        self.assertIn("volatile bool g_doseActive", read("core/FirmwareApp.cpp"))
        # The Hub push sends the session counter as vol.
        self.assertIn("vol = g_cumulativeVolumeMl;", read("network/HubClient.h"))

    def test_dose_persists_and_resumes_after_reset(self):
        dose = read("control/DoseController.h")
        self.assertIn("void doseRestore()", dose)
        self.assertIn("retomada apos reinicio", dose)
        self.assertIn("doseSaveState();", dose)


if __name__ == "__main__":
    unittest.main()
