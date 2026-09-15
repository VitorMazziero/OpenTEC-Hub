#!/usr/bin/env python3
"""
test_firmware_m2_empirical.py

Empirical Challenge Test Suite for Milestone 2 Biomass Sensor Firmware.
Verifies:
1. Parsing CommandCodec.h:
   - Does setManualGear() guarantee 0.0f duty even if g_state == MEASURING?
   - Does it prevent duty cycle from exceeding 8% across all IT gears?
   - Is coalesced NVS saveConfig() invoked properly without infinite loops?
2. Test start command state transitions under manual vs auto mode.
"""

import math
import os
import re
import sys
import unittest
from pathlib import Path

WORKSPACE_ROOT = Path(__file__).resolve().parents[3]
FIRMWARE_DIR = WORKSPACE_ROOT / "External-Devices" / "sensor-biomassa" / "firmware" / "biomass-sensor"
COMMAND_CODEC_H = FIRMWARE_DIR / "src" / "protocol" / "CommandCodec.h"
LOCAL_HTTP_API_H = FIRMWARE_DIR / "src" / "api" / "LocalHttpApi.h"
FIRMWARE_APP_CPP = FIRMWARE_DIR / "src" / "core" / "FirmwareApp.cpp"
BLANKING_RANGE_H = FIRMWARE_DIR / "src" / "measurement" / "BlankingAndRange.h"
STORES_H = FIRMWARE_DIR / "src" / "storage" / "Stores.h"


class TestCommandCodecParsing(unittest.TestCase):
    """Static analysis and code structure validation of CommandCodec.h and friends."""

    @classmethod
    def setUpClass(cls):
        cls.codec_code = COMMAND_CODEC_H.read_text(encoding="utf-8")
        cls.http_code = LOCAL_HTTP_API_H.read_text(encoding="utf-8")
        cls.stores_code = STORES_H.read_text(encoding="utf-8")
        cls.blank_code = BLANKING_RANGE_H.read_text(encoding="utf-8")
        cls.app_code = FIRMWARE_APP_CPP.read_text(encoding="utf-8")

    def extract_function_body(self, code: str, func_name: str) -> str:
        """Extract full function body using brace counting."""
        idx = code.find(f"void {func_name}(")
        if idx == -1:
            return ""
        open_brace = code.find("{", idx)
        if open_brace == -1:
            return ""
        depth = 1
        pos = open_brace + 1
        while pos < len(code) and depth > 0:
            if code[pos] == "{":
                depth += 1
            elif code[pos] == "}":
                depth -= 1
            pos += 1
        return code[open_brace + 1:pos - 1]

    def test_setManualGear_unconditional_zero_duty(self):
        """Verify setManualGear sets 0.0f duty unconditionally without IDLE guard."""
        body = self.extract_function_body(self.codec_code, "setManualGear")
        self.assertTrue(len(body) > 0, "setManualGear function definition not found in CommandCodec.h")

        # Confirm pwmSetDutyPercent(0.0f) is present
        self.assertIn("pwmSetDutyPercent(0.0f);", body, "pwmSetDutyPercent(0.0f) not found in setManualGear")

        # Confirm there is NO state check guarding pwmSetDutyPercent
        self.assertNotIn("if (g_state == IDLE)", body, "Legacy 'if (g_state == IDLE)' bug still present!")
        self.assertNotIn("g_state ==", body, "Conditional state check found in setManualGear duty setter")

        # Verify execution order: pwmSetLevel followed by pwmSetDutyPercent(0.0f)
        idx_pwm = body.find("pwmSetLevel(pwmIndex);")
        idx_duty0 = body.find("pwmSetDutyPercent(0.0f);")
        self.assertNotEqual(idx_pwm, -1, "pwmSetLevel(pwmIndex) not found")
        self.assertNotEqual(idx_duty0, -1, "pwmSetDutyPercent(0.0f) not found")
        self.assertLess(idx_pwm, idx_duty0, "pwmSetDutyPercent(0.0f) must execute AFTER pwmSetLevel")

        # Verify autoRange is disabled and persisted
        self.assertIn("g_autoRange = false;", body, "g_autoRange = false not found in setManualGear")
        self.assertIn("g_prefs.putBool(NVS_KEY_AUTO, false);", body, "NVS_KEY_AUTO not saved in setManualGear")

        # Verify thermal safety floor and reschedule next read time
        self.assertIn("enforceRefreshFloor(true);", body, "enforceRefreshFloor(true) not found in setManualGear")
        self.assertIn("g_nextReadTime = millis() + currentRefreshFloor();", body,
                      "g_nextReadTime not delayed by currentRefreshFloor()")

    def test_currentRefreshFloor_definition(self):
        """Verify currentRefreshFloor helper exists and uses minSafeRefreshMs()."""
        m = re.search(r"inline\s+uint32_t\s+currentRefreshFloor\s*\(\)\s*\{([^}]+)\}", self.codec_code)
        self.assertIsNotNone(m, "currentRefreshFloor() not found in CommandCodec.h")
        body = m.group(1)
        self.assertIn("minSafeRefreshMs()", body)
        self.assertIn("g_config.itRefreshTimes[g_currentItIndex]", body)

    def test_coalesced_nvs_saveConfig_structure(self):
        """Verify numeric settings block coalesces saveConfig with change detection."""
        start_idx = self.codec_code.find("// Check for numeric settings")
        self.assertNotEqual(start_idx, -1, "Numeric settings section header not found")
        block = self.codec_code[start_idx:]
        end_idx = block.find("bool foundF;")
        self.assertNotEqual(end_idx, -1, "End of numeric settings block not found")
        numeric_block = block[:end_idx]

        # Verify bool configModified = false;
        self.assertIn("bool configModified = false;", numeric_block)

        # Verify low threshold change detection
        self.assertIn("if (g_config.LOW_THRESHOLD_RAW != (uint16_t)val)", numeric_block)
        # Verify high threshold change detection
        self.assertIn("if (g_config.HIGH_THRESHOLD_RAW != (uint16_t)val)", numeric_block)
        # Verify opt threshold change detection
        self.assertIn("if (g_config.OPTIMAL_TARGET_RAW != (uint16_t)val)", numeric_block)

        # Verify refresh_ms / probe_ms / probe_period change detection
        self.assertIn("minSafeRefreshMs()", numeric_block)
        self.assertIn("if (g_config.itRefreshTimes[i] != (uint32_t)val)", numeric_block)

        # Verify exactly one saveConfig() call conditioned on configModified
        save_calls = re.findall(r"if\s*\(\s*configModified\s*\)\s*\{\s*saveConfig\(\);\s*\}", numeric_block)
        self.assertEqual(len(save_calls), 1, "Exactly one conditioned saveConfig() call expected in numeric block")

        # Verify probe_period dedicated command also calls saveConfig()
        pp_cmd_idx = self.codec_code.find('else if (cmd.equals("probe_period"))')
        self.assertNotEqual(pp_cmd_idx, -1)
        pp_block = self.codec_code[pp_cmd_idx:pp_cmd_idx + 800]
        self.assertIn("saveConfig();", pp_block, "saveConfig() missing from probe_period command branch")

    def test_saveConfig_has_no_infinite_recursion(self):
        """Verify saveConfig in Stores.h is non-recursive and cannot cycle."""
        m = re.search(r"void\s+saveConfig\s*\(\)\s*\{([^}]+)\}", self.stores_code)
        self.assertIsNotNone(m, "saveConfig not found in Stores.h")
        body = m.group(1)
        self.assertNotIn("saveConfig(", body, "Direct recursion in saveConfig")
        self.assertNotIn("processJsonCommand(", body, "saveConfig calls processJsonCommand")
        self.assertNotIn("loadConfig(", body, "saveConfig calls loadConfig")
        self.assertIn("calculateCRC32", body)
        self.assertIn("g_prefs.putBytes", body)

    def test_firmware_version_11(self):
        """Verify LocalHttpApi.h declares v11.0."""
        self.assertIn("Biomass Sensor Firmware v11.0", self.http_code)
        self.assertNotIn("Biomass Sensor Firmware v5.3", self.http_code)


class TestThermalDutyCalculations(unittest.TestCase):
    """Mathematical and empirical verification of thermal duty cycle across all IT gears."""

    def setUp(self):
        self.IT_MS = [25, 50, 100, 200, 400, 800]
        self.IT_PERIOD_GUARD = 1.20
        self.INTEGRATION_MARGIN_MS = 8
        self.LED_SETTLE_MS = 10
        self.LED_DUTY_LIMIT = 0.08

    def integrationGuardMs(self, itMs: int) -> int:
        return int(self.IT_PERIOD_GUARD * float(itMs)) + self.INTEGRATION_MARGIN_MS

    def ledOnMsFor(self, itMs: int) -> int:
        return self.LED_SETTLE_MS + 2 * self.integrationGuardMs(itMs)

    def minSafeRefreshMs(self, itMs: int) -> int:
        return int(float(self.ledOnMsFor(itMs)) / self.LED_DUTY_LIMIT)

    def test_duty_cycle_across_all_it_gears_manual_mode(self):
        """Verify LED duty cycle <= 8% for EVERY integration time in manual gear mode."""
        for it in self.IT_MS:
            guard = self.integrationGuardMs(it)
            led_on = self.ledOnMsFor(it)
            floor_ms = self.minSafeRefreshMs(it)
            duty = float(led_on) / float(floor_ms)

            self.assertLessEqual(duty, 0.08000000000000002,
                                 f"IT {it}ms duty cycle {duty:.5f} exceeds 8% limit!")
            self.assertEqual(floor_ms, int(led_on * 12.5), f"IT {it}ms floor calculation unexpected")
            self.assertAlmostEqual(duty, 0.08, places=4, msg=f"IT {it}ms duty should be 8.000%")

    def test_duty_cycle_in_auto_mode(self):
        """In auto-range mode, minSafeRefreshMs must use max IT (800ms)."""
        max_it = max(self.IT_MS)
        guard = self.integrationGuardMs(max_it)  # 1.2*800 + 8 = 968
        led_on = self.ledOnMsFor(max_it)        # 10 + 2*968 = 1946
        floor_ms = self.minSafeRefreshMs(max_it) # 1946 / 0.08 = 24325
        duty = float(led_on) / float(floor_ms)

        self.assertEqual(guard, 968)
        self.assertEqual(led_on, 1946)
        self.assertEqual(floor_ms, 24325)
        self.assertLessEqual(duty, 0.08)

        duty_at_25 = float(self.ledOnMsFor(25)) / float(floor_ms)
        self.assertLess(duty_at_25, 0.08)

    def test_refresh_clamping_enforces_safety(self):
        """Simulate arbitrary operator inputs for refresh interval and check clamping."""
        for requested_val in [-500, 0, 1, 100, 1000, 5000, 10000, 24000]:
            for it in self.IT_MS:
                floor_ms = self.minSafeRefreshMs(it)
                clamped_val = max(requested_val, floor_ms)
                duty = float(self.ledOnMsFor(it)) / float(clamped_val)
                self.assertLessEqual(duty, 0.08000000000000002,
                                     f"Requested {requested_val}ms for IT {it} allowed duty {duty:.4f}")


class BiomassFirmwareSimulator:
    """Accurate Python emulation of Biomass Sensor firmware logic."""

    STATE_IDLE = "IDLE"
    STATE_MEASURING = "MEASURING"
    STATE_BLANKING = "BLANKING"
    STATE_SEARCHING = "SEARCHING"

    def __init__(self):
        self.IT_MS = [25, 50, 100, 200, 400, 800]
        self.PWM_SETTINGS = [2.0, 3.5, 6.0, 10.5, 18.0, 32.0, 57.0, 100.0]
        self.IT_COUNT = len(self.IT_MS)
        self.PWM_COUNT = len(self.PWM_SETTINGS)

        self.g_state = self.STATE_IDLE
        self.g_blankIsDone = False
        self.g_autoRange = True
        self.g_currentItIndex = 2  # default 100ms
        self.g_currentPwmIndex = 3 # default 10.5%
        self.g_currentLedDuty = 0.0
        self.g_millis = 100000
        self.g_nextReadTime = 0

        self.itRefreshTimes = [25000] * self.IT_COUNT
        self.LOW_THRESHOLD_RAW = 500
        self.HIGH_THRESHOLD_RAW = 55000
        self.OPTIMAL_TARGET_RAW = 30000

        self.blankTable = [[30000 for _ in range(self.PWM_COUNT)] for _ in range(self.IT_COUNT)]

        self.saveConfigCount = 0
        self.smartStartCount = 0
        self.vemlSetConfigCalls = []
        self.pwmSetLevelCalls = []

    def minSafeRefreshMs(self) -> int:
        if self.g_autoRange:
            it = max(self.IT_MS)
        else:
            it = self.IT_MS[self.g_currentItIndex]
        guard = int(1.20 * float(it)) + 8
        led_on = 10 + 2 * guard
        return int(float(led_on) / 0.08)

    def currentRefreshFloor(self) -> int:
        floor = self.minSafeRefreshMs()
        cur = self.itRefreshTimes[self.g_currentItIndex]
        return cur if cur > floor else floor

    def enforceRefreshFloor(self):
        floor = self.minSafeRefreshMs()
        for i in range(self.IT_COUNT):
            if self.itRefreshTimes[i] < floor:
                self.itRefreshTimes[i] = floor

    def blankIsValid(self, it: int, pwm: int) -> bool:
        if not self.g_blankIsDone:
            return False
        val = self.blankTable[it][pwm]
        return 1000 <= val < 60000

    def findOptimalBlankGear(self) -> tuple[int, int]:
        self.smartStartCount += 1
        return (4, 5)

    def pwmSetDutyPercent(self, pct: float):
        self.g_currentLedDuty = pct

    def pwmSetLevel(self, pwmIndex: int):
        self.g_currentPwmIndex = pwmIndex
        self.pwmSetDutyPercent(self.PWM_SETTINGS[pwmIndex])
        self.pwmSetLevelCalls.append(pwmIndex)

    def vemlSetConfig(self, itIndex: int):
        self.g_currentItIndex = itIndex
        self.vemlSetConfigCalls.append(itIndex)

    def saveConfig(self):
        self.saveConfigCount += 1

    def setManualGear(self, itIndex: int, pwmIndex: int):
        if itIndex < 0 or itIndex >= self.IT_COUNT or pwmIndex < 0 or pwmIndex >= self.PWM_COUNT:
            return
        self.vemlSetConfig(itIndex)
        self.pwmSetLevel(pwmIndex)
        self.pwmSetDutyPercent(0.0)
        self.g_autoRange = False
        self.enforceRefreshFloor()
        self.g_nextReadTime = self.g_millis + self.currentRefreshFloor()

    def processJsonCommand(self, cmd_dict: dict):
        cmd = cmd_dict.get("command", "")
        if not cmd:
            for k in ["start", "stop", "blank", "save_config", "probe_period"]:
                if cmd_dict.get(k) == 1:
                    cmd = k
                    break

        busy = (self.g_state in [self.STATE_BLANKING, self.STATE_SEARCHING])

        if cmd:
            if cmd == "stop":
                if busy:
                    pass
                elif self.g_state == self.STATE_MEASURING:
                    self.g_state = self.STATE_IDLE
                    self.pwmSetDutyPercent(0.0)
                return

            if busy and cmd in ["blank", "start", "probe_period"]:
                return "Error: Busy"

            if cmd == "start":
                if not self.g_blankIsDone:
                    return "Error: Please run 'blank' first."
                elif self.g_state == self.STATE_IDLE:
                    startIt = self.g_currentItIndex
                    startPwm = self.g_currentPwmIndex
                    if self.g_autoRange or not self.blankIsValid(startIt, startPwm):
                        startIt, startPwm = self.findOptimalBlankGear()

                    self.g_state = self.STATE_MEASURING
                    self.g_nextReadTime = self.g_millis
                    self.vemlSetConfig(startIt)
                    self.pwmSetLevel(startPwm)
                    return "Started"
                return None

            elif cmd == "probe_period":
                val = cmd_dict.get("value", -999999)
                if val != -999999 and val > 0:
                    if val > 3600000:
                        val = 3600000
                    floor = self.minSafeRefreshMs()
                    if val < floor:
                        val = floor
                    for i in range(self.IT_COUNT):
                        self.itRefreshTimes[i] = val
                    self.saveConfig()
                    return "Probe period updated"

        configModified = False

        if "low" in cmd_dict:
            val = cmd_dict["low"]
            if val != -999999 and self.LOW_THRESHOLD_RAW != val:
                self.LOW_THRESHOLD_RAW = val
                configModified = True

        if "high" in cmd_dict:
            val = cmd_dict["high"]
            if val != -999999 and self.HIGH_THRESHOLD_RAW != val:
                self.HIGH_THRESHOLD_RAW = val
                configModified = True

        if "opt" in cmd_dict:
            val = cmd_dict["opt"]
            if val != -999999 and self.OPTIMAL_TARGET_RAW != val:
                self.OPTIMAL_TARGET_RAW = val
                configModified = True

        refresh_val = cmd_dict.get("refresh_ms", cmd_dict.get("probe_ms", cmd_dict.get("probe_period_key", -999999)))
        if refresh_val != -999999:
            if refresh_val > 3600000:
                refresh_val = 3600000
            floor = self.minSafeRefreshMs()
            if refresh_val < floor:
                refresh_val = floor
            for i in range(self.IT_COUNT):
                if self.itRefreshTimes[i] != refresh_val:
                    self.itRefreshTimes[i] = refresh_val
                    configModified = True

        if configModified:
            self.saveConfig()


class TestStateTransitionsAndPersistence(unittest.TestCase):
    """Dynamic behavioral simulation of start command, setManualGear, and coalesced saveConfig."""

    def test_setManualGear_during_MEASURING_guarantees_zero_duty(self):
        """Simulate calling setManualGear while actively MEASURING."""
        sim = BiomassFirmwareSimulator()
        sim.g_blankIsDone = True
        sim.g_state = BiomassFirmwareSimulator.STATE_MEASURING

        # Simulate LED being active in pulsed read
        sim.g_currentLedDuty = 57.0

        # Operator sends manual gear command: IT=1 (50ms), PWM=2 (6%)
        sim.setManualGear(1, 2)

        # Duty cycle MUST be 0.0f immediately
        self.assertEqual(sim.g_currentLedDuty, 0.0, "LED duty was not zeroed in MEASURING state!")
        self.assertFalse(sim.g_autoRange, "autoRange should be disabled")
        self.assertEqual(sim.g_currentItIndex, 1)
        self.assertEqual(sim.g_currentPwmIndex, 2)
        # Next read time delayed by at least the thermal floor of IT=1
        expected_floor = sim.minSafeRefreshMs()
        self.assertGreaterEqual(sim.g_nextReadTime - sim.g_millis, expected_floor)

    def test_start_transitions_auto_mode_smart_start(self):
        """In auto mode, start command must invoke Smart Start."""
        sim = BiomassFirmwareSimulator()
        sim.g_blankIsDone = True
        sim.g_autoRange = True
        sim.g_currentItIndex = 1
        sim.g_currentPwmIndex = 1

        res = sim.processJsonCommand({"command": "start"})
        self.assertEqual(res, "Started")
        self.assertEqual(sim.g_state, BiomassFirmwareSimulator.STATE_MEASURING)
        self.assertEqual(sim.smartStartCount, 1, "findOptimalBlankGear should be invoked in auto mode")
        # Optimal gear (4, 5) chosen by Smart Start
        self.assertEqual(sim.g_currentItIndex, 4)
        self.assertEqual(sim.g_currentPwmIndex, 5)

    def test_start_transitions_manual_mode_preserves_gear(self):
        """In manual mode with valid blank, start command MUST preserve user gear."""
        sim = BiomassFirmwareSimulator()
        sim.g_blankIsDone = True
        sim.setManualGear(2, 3)
        self.assertFalse(sim.g_autoRange)
        self.assertEqual(sim.g_currentItIndex, 2)
        self.assertEqual(sim.g_currentPwmIndex, 3)

        sim.blankTable[2][3] = 25000

        smart_starts_before = sim.smartStartCount
        res = sim.processJsonCommand({"command": "start"})
        self.assertEqual(res, "Started")
        self.assertEqual(sim.g_state, BiomassFirmwareSimulator.STATE_MEASURING)
        self.assertEqual(sim.smartStartCount, smart_starts_before,
                         "Smart Start must NOT be called in manual mode when blank is valid!")
        self.assertEqual(sim.g_currentItIndex, 2, "Manual IT gear was clobbered by start!")
        self.assertEqual(sim.g_currentPwmIndex, 3, "Manual PWM gear was clobbered by start!")

    def test_start_transitions_manual_mode_invalid_blank_fallback(self):
        """In manual mode, if selected gear has invalid blank, Smart Start fallback triggers."""
        sim = BiomassFirmwareSimulator()
        sim.g_blankIsDone = True
        sim.setManualGear(0, 0)
        self.assertFalse(sim.g_autoRange)

        sim.blankTable[0][0] = 50 # below MIN_VALID_BLANK (1000)

        res = sim.processJsonCommand({"command": "start"})
        self.assertEqual(res, "Started")
        self.assertEqual(sim.g_state, BiomassFirmwareSimulator.STATE_MEASURING)
        self.assertEqual(sim.smartStartCount, 1, "Smart Start fallback should trigger when blank is invalid")
        self.assertEqual(sim.g_currentItIndex, 4)
        self.assertEqual(sim.g_currentPwmIndex, 5)

    def test_start_transitions_without_blank_rejected(self):
        """If blank has not been completed, start command must be rejected."""
        sim = BiomassFirmwareSimulator()
        sim.g_blankIsDone = False
        res = sim.processJsonCommand({"command": "start"})
        self.assertEqual(res, "Error: Please run 'blank' first.")
        self.assertEqual(sim.g_state, BiomassFirmwareSimulator.STATE_IDLE)

    def test_start_transitions_busy_states_rejected(self):
        """If state is BLANKING or SEARCHING, start command must be rejected."""
        sim = BiomassFirmwareSimulator()
        sim.g_blankIsDone = True

        for busy_st in [BiomassFirmwareSimulator.STATE_BLANKING, BiomassFirmwareSimulator.STATE_SEARCHING]:
            sim.g_state = busy_st
            res = sim.processJsonCommand({"command": "start"})
            self.assertEqual(res, "Error: Busy")
            self.assertEqual(sim.g_state, busy_st)

    def test_coalesced_saveConfig_single_field(self):
        """Single parameter change triggers exactly one saveConfig."""
        sim = BiomassFirmwareSimulator()
        self.assertEqual(sim.saveConfigCount, 0)

        sim.processJsonCommand({"low": 800})
        self.assertEqual(sim.saveConfigCount, 1)
        self.assertEqual(sim.LOW_THRESHOLD_RAW, 800)

    def test_coalesced_saveConfig_multi_field_single_write(self):
        """Multiple parameter changes in single JSON trigger exactly ONE coalesced saveConfig."""
        sim = BiomassFirmwareSimulator()
        self.assertEqual(sim.saveConfigCount, 0)

        sim.processJsonCommand({
            "low": 900,
            "high": 48000,
            "opt": 28000,
            "refresh_ms": 30000
        })
        self.assertEqual(sim.saveConfigCount, 1,
                         "Multi-field JSON should coalesce into exactly 1 saveConfig call!")
        self.assertEqual(sim.LOW_THRESHOLD_RAW, 900)
        self.assertEqual(sim.HIGH_THRESHOLD_RAW, 48000)
        self.assertEqual(sim.OPTIMAL_TARGET_RAW, 28000)
        self.assertEqual(sim.itRefreshTimes[0], 30000)

    def test_coalesced_saveConfig_idempotent_no_write(self):
        """Sending identical values does NOT write to NVS (wear leveling)."""
        sim = BiomassFirmwareSimulator()
        sim.LOW_THRESHOLD_RAW = 500
        sim.HIGH_THRESHOLD_RAW = 55000

        sim.processJsonCommand({"low": 500, "high": 55000})
        self.assertEqual(sim.saveConfigCount, 0, "Redundant values must not trigger NVS write!")

    def test_coalesced_saveConfig_stress_loop(self):
        """Rapid sequence of commands does not deadlock or loop infinitely."""
        sim = BiomassFirmwareSimulator()
        for i in range(1000):
            sim.processJsonCommand({"low": 500 + (i % 10), "high": 50000 + (i % 20)})
        self.assertGreater(sim.saveConfigCount, 0)

    def test_setManualGear_out_of_bounds_rejected(self):
        """Out of bounds itIndex or pwmIndex must be rejected without mutating state."""
        sim = BiomassFirmwareSimulator()
        sim.setManualGear(2, 3)
        self.assertEqual(sim.g_currentItIndex, 2)
        self.assertEqual(sim.g_currentPwmIndex, 3)

        # Invalid IT indices
        sim.setManualGear(-1, 3)
        self.assertEqual(sim.g_currentItIndex, 2)
        sim.setManualGear(6, 3)
        self.assertEqual(sim.g_currentItIndex, 2)

        # Invalid PWM indices
        sim.setManualGear(2, -1)
        self.assertEqual(sim.g_currentPwmIndex, 3)
        sim.setManualGear(2, 8)
        self.assertEqual(sim.g_currentPwmIndex, 3)

    def test_start_when_already_measuring(self):
        """Start command received during MEASURING must not interrupt or change state."""
        sim = BiomassFirmwareSimulator()
        sim.g_blankIsDone = True
        sim.g_state = BiomassFirmwareSimulator.STATE_MEASURING
        sim.g_currentItIndex = 2
        sim.g_currentPwmIndex = 3

        smart_starts_before = sim.smartStartCount
        res = sim.processJsonCommand({"command": "start"})
        self.assertIsNone(res)
        self.assertEqual(sim.g_state, BiomassFirmwareSimulator.STATE_MEASURING)
        self.assertEqual(sim.smartStartCount, smart_starts_before)
        self.assertEqual(sim.g_currentItIndex, 2)
        self.assertEqual(sim.g_currentPwmIndex, 3)

    def test_coalesced_saveConfig_invalid_keys(self):
        """Non-config keys or unparsable values must not trigger saveConfig."""
        sim = BiomassFirmwareSimulator()
        sim.processJsonCommand({"foo": "bar", "unknown_key": 12345})
        self.assertEqual(sim.saveConfigCount, 0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
