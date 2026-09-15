#!/usr/bin/env python3
"""
Test script verifying the algorithmic logic of F07 and F08 remediation.
Author: teamwork_preview_explorer (explorer_remedy_3_it2)
"""

import math
import sys

def parse_json_bool(val_str):
    """Simulates parseJsonBool using strcasecmp and strcmp('1'/'0')."""
    if val_str is None:
        return False, None
    s = val_str.strip()
    if not s:
        return False, None
    
    lower = s.lower()
    if lower == "true" or s == "1":
        return True, True
    if lower == "false" or s == "0":
        return True, False
    return False, None

def parse_bounded_float(val_str, min_val, max_val):
    """Simulates parseBoundedFloat with syntax, finiteness, and range checks."""
    if val_str is None:
        return False, None
    s = val_str.strip()
    if not s:
        return False, None
    try:
        val = float(s)
        if math.isnan(val) or math.isinf(val):
            return False, None
        if val < min_val or val > max_val:
            return False, None
        return True, val
    except ValueError:
        return False, None

def run_tests():
    print("[TEST] Running F08 Boolean Parsing Test Suite...")
    f08_cases = [
        # (input_str, expected_ok, expected_bool)
        ("true", True, True),
        ("false", True, False),
        ("True", True, True),
        ("False", True, False),
        ("TRUE", True, True),
        ("FALSE", True, False),
        ("tRuE", True, True),
        ("fAlSe", True, False),
        ("1", True, True),
        ("0", True, False),
        (" 1 ", True, True),
        (" 0 ", True, False),
        (" true ", True, True),
        (" false ", True, False),
        ("2", False, None),
        ("-1", False, None),
        ("on", False, None),
        ("off", False, None),
        ("null", False, None),
        ("undefined", False, None),
        ("", False, None),
        ("   ", False, None)
    ]
    for s, exp_ok, exp_bool in f08_cases:
        ok, res = parse_json_bool(s)
        assert ok == exp_ok and res == exp_bool, f"F08 failed on '{s}': got ({ok}, {res}), expected ({exp_ok}, {exp_bool})"
    print("       -> All 22 boolean test cases PASSED.")

    print("\n[TEST] Running F07 Calibration Polynomial Float Range Test Suite (+/- 1e7)...")
    factory_coeffs = [
        ("a1", "-1353785.3", -1e7, 1e7, True, -1353785.3),
        ("b1", "246663.69", -1e7, 1e7, True, 246663.69),
        ("k1", "-16473.492", -1e7, 1e7, True, -16473.492),
        ("f1", "484.99466", -1e7, 1e7, True, 484.99466),
        ("c1", "-4.6159464", -1e7, 1e7, True, -4.6159464),
        ("k2", "-0.46260458", -1e7, 1e7, True, -0.46260458),
        ("f2", "10.797299", -1e7, 1e7, True, 10.797299),
        ("c2", "0.28475793", -1e7, 1e7, True, 0.28475793),
        ("v05_a1", "321791.3459", -1e7, 1e7, True, 321791.3459),
        ("v05_b1", "-32589.0731", -1e7, 1e7, True, -32589.0731),
        ("v05_k1", "462.8935", -1e7, 1e7, True, 462.8935),
        ("max_pos", "10000000.0", -1e7, 1e7, True, 10000000.0),
        ("max_neg", "-10000000.0", -1e7, 1e7, True, -10000000.0),
        ("overflow_pos", "10000001.0", -1e7, 1e7, False, None),
        ("overflow_neg", "-10000001.0", -1e7, 1e7, False, None),
        ("huge_overflow", "1e20", -1e7, 1e7, False, None),
        ("nan_case", "NaN", -1e7, 1e7, False, None),
        ("inf_pos", "Inf", -1e7, 1e7, False, None),
        ("inf_neg", "-Infinity", -1e7, 1e7, False, None),
        ("invalid_str", "abc", -1e7, 1e7, False, None),
        ("empty_str", "", -1e7, 1e7, False, None)
    ]
    for name, s, min_v, max_v, exp_ok, exp_val in factory_coeffs:
        ok, res = parse_bounded_float(s, min_v, max_v)
        assert ok == exp_ok, f"F07 failed on {name} '{s}': got ok={ok}, expected {exp_ok}"
        if ok and exp_val is not None:
            assert abs(res - exp_val) < 1e-3, f"F07 value mismatch on {name}: got {res}, expected {exp_val}"
    print("       -> All 21 calibration polynomial test cases PASSED.")

    print("\n[TEST] Running F07 Control Tuning Parameter Range Test Suite...")
    tuning_cases = [
        # (param, s, min_v, max_v, exp_ok)
        ("kp_flow_default", "0.4", 0.0, 100.0, True),
        ("kp_flow_zero", "0.0", 0.0, 100.0, True),
        ("kp_flow_max", "100.0", 0.0, 100.0, True),
        ("kp_flow_over", "100.01", 0.0, 100.0, False),
        ("kp_flow_neg", "-0.01", 0.0, 100.0, False),
        ("ki_flow_default", "2.0", 0.0, 100.0, True),
        ("ki_flow_max", "100.0", 0.0, 100.0, True),
        ("ki_flow_over", "105.0", 0.0, 100.0, False),
        ("ff_gain_default", "0.85", 0.0, 10.0, True),
        ("ff_gain_max", "10.0", 0.0, 10.0, True),
        ("ff_gain_over", "10.1", 0.0, 10.0, False),
        ("ff_offset_default", "-0.05", -5.0, 5.0, True),
        ("ff_offset_neg_bound", "-5.0", -5.0, 5.0, True),
        ("ff_offset_pos_bound", "5.0", -5.0, 5.0, True),
        ("ff_offset_under", "-5.01", -5.0, 5.0, False),
        ("ff_offset_over", "5.01", -5.0, 5.0, False),
        ("ramp_rate_default", "3.0", 0.0, 100.0, True),
        ("ramp_rate_zero", "0.0", 0.0, 100.0, True),
        ("ramp_rate_max", "100.0", 0.0, 100.0, True),
        ("ramp_rate_over", "100.5", 0.0, 100.0, False),
        ("max_flow_default", "50.0", 0.1, 500.0, True),
        ("max_flow_zero_div_risk", "0.0", 0.1, 500.0, False),
        ("max_flow_neg", "-10.0", 0.1, 500.0, False),
        ("max_flow_upper", "500.0", 0.1, 500.0, True),
        ("max_flow_over", "500.1", 0.1, 500.0, False),
    ]
    for name, s, min_v, max_v, exp_ok in tuning_cases:
        ok, res = parse_bounded_float(s, min_v, max_v)
        assert ok == exp_ok, f"Tuning test failed on {name} '{s}': got ok={ok}, expected {exp_ok}"
    print("       -> All 25 control tuning test cases PASSED.")

    print("\n==================================================================")
    print("SUCCESS: All 68 verification cases for F07 and F08 PASSED flawlessly!")
    print("==================================================================")
    return 0

if __name__ == "__main__":
    sys.exit(run_tests())
