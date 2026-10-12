#!/usr/bin/env python3
"""Contract checks for pump 3.12.1: integration fixes with the Hub (wire contract unchanged)."""

from pathlib import Path
import re
import struct
import unittest

ROOT = Path(__file__).resolve().parents[1] / "firmware" / "peristaltic-pump" / "src"


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def all_sources() -> dict:
    return {p.relative_to(ROOT).as_posix(): p.read_text(encoding="utf-8")
            for p in ROOT.rglob("*") if p.suffix in (".h", ".cpp")}


class CmdIdIsAnInteger(unittest.TestCase):
    def test_float_cannot_hold_hub_command_ids(self):
        # Why the fix exists: the Hub seeds ids at >= 1e8 per boot; float32 is exact to 2^24.
        as_float = lambda x: int(struct.unpack("f", struct.pack("f", x))[0])
        self.assertNotEqual(as_float(999_999_001), 999_999_001)
        self.assertEqual(as_float(123_456_001), as_float(123_456_002))

    def test_cmd_id_never_goes_through_a_float(self):
        hub = read("network/HubClient.h")
        self.assertNotIn('getJsonFloatValue(body, "cmd_id")', hub)
        self.assertIn('parseJsonUint32(body.c_str(), "cmd_id", hubCommandId)', hub)
        scan = read("protocol/JsonScan.h")
        self.assertIn("inline bool parseJsonUint32(const char* json, const char* key, uint32_t& out)", scan)
        self.assertIn("strtoull", scan)
        self.assertIn("v > 0xFFFFFFFFULL", scan)

    def test_hello_uses_the_version_macro(self):
        self.assertIn('ver=" PUMP_FW_VERSION "&mac', read("network/HubClient.h"))


class RecoveryCheckpoint(unittest.TestCase):
    def test_speed_that_ends_a_profile_clears_the_checkpoint(self):
        ctl = read("control/OperationController.h")
        speed = ctl[ctl.index('doseCancel("speed")'):ctl.index("usbSpeedSteps = constrain")]
        self.assertIn("if (g_opState != OP_IDLE)", speed)
        self.assertIn("clearRuntimeState();", speed)

    def test_ota_does_not_resume_a_profile_or_dose(self):
        hub = read("network/HubClient.h")
        interlock = hub[hub.index("Intertravamento de seguranca"):hub.index("Update.isRunning()")]
        self.assertIn('doseCancel("ota");', interlock)
        self.assertIn("clearRuntimeState();", interlock)


class SessionVolume(unittest.TestCase):
    def test_session_volume_is_restored_on_every_boot_before_the_motor_task(self):
        setup = read("core/Lifecycle.h")
        restore = setup.index("sessionVolumeRestore();")
        self.assertLess(restore, setup.index("xTaskCreatePinnedToCore(pwmTask"))
        self.assertLess(restore, setup.index("checkAndRecoverState();"))
        self.assertIn("sessionVolumeSave(false);", setup)

    def test_clean_boot_keeps_the_volume(self):
        store = read("storage/RuntimeStateStore.h")
        clean = store[store.index("[BOOT] Clean start"):store.index("void saveRuntimeState()")]
        self.assertIn("resetControllerState();", clean)
        self.assertNotIn("resetOperationState();", clean)

    def test_reset_volume_persists_the_zero(self):
        ctl = read("control/OperationController.h")
        reset = ctl[ctl.index("void resetOperationState()"):ctl.index("void resetControllerState()")]
        self.assertIn("volSet(0.0f);", reset)
        self.assertIn("sessionVolumeSave(true);", reset)

    def test_dose_restore_no_longer_overwrites_the_session(self):
        dose = read("control/DoseController.h")
        restore = dose[dose.index("void doseRestore()"):dose.index("static void doseStartPulse")]
        self.assertNotIn("volSet(", restore)
        self.assertNotIn("g_cumulativeVolumeMl", restore)

    def test_save_is_rate_limited(self):
        store = read("storage/RuntimeStateStore.h")
        self.assertIn('NVS_KEY_SESSION_VOL', store)
        self.assertIn("SESSION_SAVE_MIN_DELTA_ML = 0.05f", store)
        self.assertIn("SESSION_SAVE_MIN_MS       = 30000", store)
        self.assertIn('(char*)"s_sess"', read("core/FirmwareApp.cpp"))


class VolumeCounterIsLocked(unittest.TestCase):
    def test_counter_is_only_touched_through_the_spinlock_helpers(self):
        for path, text in all_sources().items():
            self.assertNotIn("taskDISABLE_INTERRUPTS", text, path)
            if path == "core/FirmwareApp.cpp":
                continue
            self.assertIsNone(re.search(r"\bg_cumulativeVolumeMl\b", text), path)
        app = read("core/FirmwareApp.cpp")
        for helper in ("inline float volRead()", "inline void volAdd(float ml)", "inline void volSet(float ml)"):
            self.assertIn(helper, app)
        self.assertIn("portENTER_CRITICAL(&g_volMux)", app)

    def test_dose_start_stops_integration_before_reading_the_session(self):
        dose = read("control/DoseController.h")
        start = dose[dose.index("Liga a dosagem antes de ler a sessao"):]
        self.assertLess(start.index("g_doseActive = true;"), start.index("g_cycleStartVolumeMl = volRead();"))


class DoseCheckpointRate(unittest.TestCase):
    def test_dose_state_is_saved_at_most_once_a_minute_for_short_periods(self):
        dose = read("control/DoseController.h")
        self.assertIn("DOSE_SAVE_MIN_MS = 60000", dose)
        self.assertIn("g_dose.periodS >= 60.0f || now - g_doseLastSaveMs >= DOSE_SAVE_MIN_MS", dose)


if __name__ == "__main__":
    unittest.main()
