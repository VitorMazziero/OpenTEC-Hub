"""
Empirical test suite for Biomass Sensor Firmware Milestone 2 edge cases.
Challenger M2_2 verification harness.

Tests:
1. Version string verification across LocalHttpApi.h:80, FirmwareApp.cpp, TelemetryAndHub.h,
   NodeFirmwareCatalog.cs, and documentation.
2. Boundary checks and potential arithmetic/timer overflow in currentRefreshFloor() and its call site.
3. NVS putBool failure modes, silent failure handling, and corruption risk analysis.
"""

import re
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
BIOMASS_ROOT = REPO_ROOT / "External-Devices" / "sensor-biomassa"
FW_ROOT = BIOMASS_ROOT / "firmware" / "biomass-sensor"
SRC_ROOT = FW_ROOT / "src"

LOCAL_HTTP_API_H = SRC_ROOT / "api" / "LocalHttpApi.h"
COMMAND_CODEC_H = SRC_ROOT / "protocol" / "CommandCodec.h"
FIRMWARE_APP_CPP = SRC_ROOT / "core" / "FirmwareApp.cpp"
TELEMETRY_AND_HUB_H = SRC_ROOT / "protocol" / "TelemetryAndHub.h"
LIFECYCLE_H = SRC_ROOT / "core" / "Lifecycle.h"
STORES_H = SRC_ROOT / "storage" / "Stores.h"
BLANKING_AND_RANGE_H = SRC_ROOT / "measurement" / "BlankingAndRange.h"
NODE_CATALOG_CS = REPO_ROOT / "Windows_app" / "src" / "OpenTECHub" / "Services" / "Communication" / "NodeFirmwareCatalog.cs"
PLAN_BIOMASSA_MD = REPO_ROOT / "IMPLEMENTATION_PLAN_BIOMASSA.md"
COMANDOS_MD = REPO_ROOT / "External-Devices" / "docs" / "COMANDOS_DISPOSITIVOS_EXTERNOS.md"


class TestMilestone2EdgeCases(unittest.TestCase):

    # =========================================================================
    # Edge Case 1: LocalHttpApi.h:80 Version String Matching
    # =========================================================================

    def test_edge_case_1_version_string_exact_match(self):
        """Verify whether LocalHttpApi.h:80 matches the release version exactly."""
        self.assertTrue(LOCAL_HTTP_API_H.exists(), f"Missing {LOCAL_HTTP_API_H}")
        lines = LOCAL_HTTP_API_H.read_text(encoding="utf-8").splitlines()
        
        # Line 80 is 1-indexed (index 79 in 0-indexed list)
        line_80 = lines[79] if len(lines) >= 80 else ""
        match = re.search(r"<p>Running:\s*<b>Biomass Sensor Firmware (v[\d\.]+)</b></p>", line_80)
        self.assertIsNotNone(match, f"Line 80 does not contain expected version paragraph: '{line_80}'")
        ota_page_version = match.group(1)

        # Extract FW_VERSION from FirmwareApp.cpp
        fw_app_content = FIRMWARE_APP_CPP.read_text(encoding="utf-8")
        fw_ver_match = re.search(r'static const char\*\s+FW_VERSION\s*=\s*"([^"]+)";', fw_app_content)
        self.assertIsNotNone(fw_ver_match, "FW_VERSION not found in FirmwareApp.cpp")
        cpp_fw_version = fw_ver_match.group(1)

        # Extract hello version from TelemetryAndHub.h
        hub_content = TELEMETRY_AND_HUB_H.read_text(encoding="utf-8")
        hub_ver_match = re.search(r'dev=biomass&ver=([^&"]+)', hub_content)
        self.assertIsNotNone(hub_ver_match, "ver= not found in TelemetryAndHub.h sendHubHello()")
        wire_hello_version = hub_ver_match.group(1)

        # Extract catalog version from NodeFirmwareCatalog.cs
        catalog_content = NODE_CATALOG_CS.read_text(encoding="utf-8")
        # Look for [Biomass] = new(...) { "v11" }
        cat_match = re.search(r'\[Biomass\]\s*=\s*new\([^)]*\)\s*\{\s*([^}]+)\s*\}', catalog_content)
        self.assertIsNotNone(cat_match, "[Biomass] entry not found in NodeFirmwareCatalog.cs")
        catalog_versions = [v.strip().strip('"') for v in cat_match.group(1).split(",")]

        # Record findings
        print("\n--- Edge Case 1: Version String Analysis ---")
        print(f"LocalHttpApi.h:80 string:         '{ota_page_version}'")
        print(f"FirmwareApp.cpp FW_VERSION:       '{cpp_fw_version}'")
        print(f"TelemetryAndHub.h /nodeHello ver: '{wire_hello_version}'")
        print(f"NodeFirmwareCatalog.cs Biomass:   {catalog_versions}")

        # Check exact equality
        matches_cpp = (ota_page_version == cpp_fw_version)
        matches_hello = (ota_page_version == wire_hello_version)
        in_catalog = (ota_page_version in catalog_versions)

        print(f"Matches FirmwareApp.cpp exactly:  {matches_cpp}")
        print(f"Matches /nodeHello wire ver:      {matches_hello}")
        print(f"Accepted by NodeFirmwareCatalog:  {in_catalog}")

        # The release version in code is "v11", but LocalHttpApi.h:80 has "v11.0"
        if not matches_cpp:
            print(f"DISCREPANCY CONFIRMED: LocalHttpApi.h:80 has '{ota_page_version}' while FW_VERSION is '{cpp_fw_version}'!")
        if not in_catalog:
            print(f"CATALOG MISMATCH CONFIRMED: NodeFirmwareCatalog rejects '{ota_page_version}' for biomass (only {catalog_versions} allowed)!")

        # Note: We assert the observed state to document the exact facts empirically
        self.assertEqual(ota_page_version, "v11.0")
        self.assertEqual(cpp_fw_version, "v11")
        self.assertNotEqual(ota_page_version, cpp_fw_version, "Discrepancy: v11.0 vs v11")


    # =========================================================================
    # Edge Case 2: currentRefreshFloor() Boundary Checks & Overflow
    # =========================================================================

    def test_edge_case_2_current_refresh_floor_boundary_and_overflow(self):
        """Empirically test currentRefreshFloor() for bounds checking and overflow."""
        codec_content = COMMAND_CODEC_H.read_text(encoding="utf-8")

        # Extract currentRefreshFloor implementation
        func_match = re.search(r'inline\s+uint32_t\s+currentRefreshFloor\(\)\s*\{([^}]+)\}', codec_content)
        self.assertIsNotNone(func_match, "currentRefreshFloor() not found in CommandCodec.h")
        func_body = func_match.group(1).strip()

        print("\n--- Edge Case 2: currentRefreshFloor() Implementation ---")
        print(func_body)

        # 1. Boundary check evaluation
        has_lower_bound_check = "g_currentItIndex >= 0" in func_body or "g_currentItIndex >" in func_body or "0 <=" in func_body
        has_upper_bound_check = "IT_COUNT" in func_body or "g_currentItIndex <" in func_body

        print(f"Has lower bound check (g_currentItIndex >= 0): {has_lower_bound_check}")
        print(f"Has upper bound check (g_currentItIndex < IT_COUNT): {has_upper_bound_check}")

        self.assertFalse(has_lower_bound_check, "currentRefreshFloor() lacks lower bound check on g_currentItIndex")
        self.assertFalse(has_upper_bound_check, "currentRefreshFloor() lacks upper bound check on g_currentItIndex")

        # Simulate out-of-bounds index impact
        # In DeviceConfig struct:
        # struct DeviceConfig {
        #   uint16_t LOW_THRESHOLD_RAW; // 2 bytes
        #   uint16_t HIGH_THRESHOLD_RAW; // 2 bytes
        #   uint16_t OPTIMAL_TARGET_RAW; // 2 bytes
        #   uint16_t itSettings[4]; // 8 bytes
        #   uint32_t itDelays[4]; // 16 bytes
        #   uint32_t itRefreshTimes[4]; // 16 bytes
        #   float pwmSettings[8]; // 32 bytes
        #   uint32_t crc32; // 4 bytes
        # };
        # If g_currentItIndex == 4, itRefreshTimes[4] aliases with pwmSettings[0] reinterpreted as uint32_t!
        import struct
        pwm_100_as_float = 100.0
        float_bytes = struct.pack('<f', pwm_100_as_float)
        aliased_uint32 = struct.unpack('<I', float_bytes)[0]
        print(f"Simulated aliasing: if g_currentItIndex=4, pwmSettings[0]=100.0f reinterpreted as uint32_t ms = {aliased_uint32} ms (~{aliased_uint32/1000/86400:.1f} days)!")

        # 2. Timer rollover / overflow evaluation
        # Extract setManualGear
        start_idx = codec_content.find("void setManualGear(")
        self.assertNotEqual(start_idx, -1, "setManualGear not found")
        end_idx = codec_content.find("\nvoid invalidateBlank(", start_idx)
        set_manual_body = codec_content[start_idx:end_idx]

        call_match = re.search(r'g_nextReadTime\s*=\s*millis\(\)\s*\+\s*currentRefreshFloor\(\);', set_manual_body)
        self.assertIsNotNone(call_match, "g_nextReadTime = millis() + currentRefreshFloor() call not found")

        # Check Lifecycle.h loop comparison: if (now >= g_nextReadTime)
        lifecycle_content = LIFECYCLE_H.read_text(encoding="utf-8")
        has_unsafe_rollover = "if (now >= g_nextReadTime)" in lifecycle_content
        print(f"Unsafe unsigned timer rollover comparison 'if (now >= g_nextReadTime)': {has_unsafe_rollover}")

        # Simulate millis() rollover
        millis_near_max = 0xFFFFFFFF - 5000  # 5 seconds before 49.7-day rollover
        interval = 24375  # floor for 800ms IT
        g_nextReadTime_wrapped = (millis_near_max + interval) & 0xFFFFFFFF
        
        # Immediate evaluation under (now >= g_nextReadTime)
        now = millis_near_max
        immediate_fire = (now >= g_nextReadTime_wrapped)
        print(f"Simulated rollover at now={now}: g_nextReadTime wrapped to {g_nextReadTime_wrapped}.")
        print(f"Does 'now >= g_nextReadTime' evaluate to True immediately without cooldown? {immediate_fire}")
        self.assertTrue(immediate_fire, "Rollover causes premature firing due to unsigned >= comparison without elapsed subtraction")


    # =========================================================================
    # Edge Case 3: NVS putBool Failure and Corruption Risks
    # =========================================================================

    def test_edge_case_3_nvs_putbool_failure_and_corruption(self):
        """Analyze failure modes, return value handling, and corruption risks of putBool(NVS_KEY_AUTO, false)."""
        codec_content = COMMAND_CODEC_H.read_text(encoding="utf-8")

        # Find setManualGear
        start_idx = codec_content.find("void setManualGear(")
        self.assertNotEqual(start_idx, -1, "setManualGear not found")
        end_idx = codec_content.find("\nvoid invalidateBlank(", start_idx)
        set_manual_body = codec_content[start_idx:end_idx]

        # Check if putBool return value is checked or captured
        putbool_line_match = re.search(r'([^\n]*g_prefs\.putBool\(NVS_KEY_AUTO,\s*false\);)', set_manual_body)
        self.assertIsNotNone(putbool_line_match, "putBool(NVS_KEY_AUTO, false) not found in setManualGear")
        putbool_line = putbool_line_match.group(1).strip()
        print("\n--- Edge Case 3: NVS putBool Analysis ---")
        print(f"Exact line: '{putbool_line}'")

        # Check for error checking
        has_error_check = (
            "if (!g_prefs.putBool" in set_manual_body or
            "if (g_prefs.putBool" in set_manual_body or
            "size_t res = g_prefs.putBool" in set_manual_body or
            "bool ok = g_prefs.putBool" in set_manual_body
        )
        print(f"Has return code check: {has_error_check}")
        self.assertFalse(has_error_check, "Return code of g_prefs.putBool is ignored")

        # Check for state check before write (flash wear protection / wear leveling)
        # e.g., "if (g_autoRange) { g_prefs.putBool(NVS_KEY_AUTO, false); }"
        lines_before_putbool = set_manual_body[:set_manual_body.find("g_prefs.putBool(NVS_KEY_AUTO, false);")].splitlines()
        has_state_guard = any("if (g_autoRange" in l for l in lines_before_putbool)
        print(f"Has flash wear guard (if (g_autoRange)): {has_state_guard}")

        # Document conclusion regarding corruption vs failure:
        # 1. Hardware/Partition corruption: ESP-IDF NVS is power-fail safe (two-phase commit).
        #    putBool CANNOT corrupt the NVS filesystem structure.
        # 2. Functional failure: If NVS is full or fails, putBool fails silently (returns 0).
        #    RAM state (g_autoRange=false) desynchronizes from NVS (still true).
        # 3. Flash wear: Lack of `if (g_autoRange)` issues redundant write/commit attempts.
        self.assertFalse(has_state_guard, "setManualGear writes to NVS without checking if g_autoRange is already false")

    # =========================================================================
    # Edge Case 4: Stress Simulation of currentRefreshFloor under extreme parameters
    # =========================================================================

    def test_edge_case_4_stress_simulation_refresh_floor(self):
        """Simulate all combinations of IT index, config refresh intervals, and duty limits."""
        IT_DELAYS = [100, 200, 400, 800]
        DUTY_LIMIT = 0.08
        MARGIN = 10
        SETTLE = 10

        def calc_floor(it_ms: int) -> int:
            guard = int(1.20 * it_ms) + MARGIN
            led_on = SETTLE + 2 * guard
            return int(led_on / DUTY_LIMIT)

        floors = [calc_floor(d) for d in IT_DELAYS]
        print("\n--- Edge Case 4: Thermal Safety Floor Reference ---")
        for i, (d, f) in enumerate(zip(IT_DELAYS, floors)):
            print(f"IT slot {i} ({d} ms): safe refresh floor = {f} ms")

        # Verify max floor is 24375 ms (for 800ms IT)
        self.assertEqual(floors[3], 24375)
        # Verify min floor is 3375 ms (for 100ms IT)
        self.assertEqual(floors[0], 3375)

        # Test boundary behavior when itRefreshTimes contains edge values:
        # e.g., 0, 1000, 3375, 25000, 2147483647 (max int32), 4294967295 (max uint32)
        test_intervals = [0, 1000, 3375, 5000, 24375, 30000, 0x7FFFFFFF, 0xFFFFFFFF]
        for it_idx in range(4):
            floor_for_it = floors[it_idx]
            for val in test_intervals:
                # helper logic: (itRefreshTimes[it_idx] > floorMs) ? itRefreshTimes[it_idx] : floorMs
                effective = val if val > floor_for_it else floor_for_it
                self.assertGreaterEqual(effective, floor_for_it)
                self.assertGreaterEqual(effective, 3375)



if __name__ == '__main__':
    unittest.main()
