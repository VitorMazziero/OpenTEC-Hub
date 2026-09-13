#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OLD = ROOT / "old" if (ROOT / "old").exists() else ROOT / "_old"
V9 = ROOT / "ESP32S3-HUB"

ENDPOINTS = {
    "/command", "/readData", "/ping", "/distance", "/flowData",
    "/biomassData", "/pumpData", "/agitatorData", "/servoData",
    "/flowCommand", "/biomassCommand", "/agitatorHello",
    "/agitatorCommand", "/pumpCommand", "/servoCommand",
}

V8_KEYS = {
    "Time", "Tempval", "pHval", "Oxyval", "Antifoam", "Pressure", "HubStations",
    "FlowmeterOnline", "FlowControlEnabled", "FlowCommandPending", "FlowCommandId",
    "FlowCommandAck", "FlowCommandDeliveries", "FlowCommandAgeMs", "FlowCommandSource",
    "FlowVoltage", "FlowRate", "FlowSetpoint", "Valve1", "Valve2", "ValveFlow",
    "BiomassOnline", "BiomassCommEnabled", "BiomassCommandPending", "BiomassAbs",
    "BiomassRaw", "BiomassIT", "BiomassPWM", "DistanceOnline", "DistanceCommEnabled",
    "Distance", "PumpOnline", "PumpCommEnabled", "PumpCommandPending", "PumpMode",
    "PumpPWM", "PumpSpeed", "PumpFlow", "PumpVol", "PumpTargetVol", "PumpActive",
    "PumpWaiting", "AgitatorOnline", "AgitatorCommandPending", "AgitatorPercent",
    "AgitatorDir", "AgitatorPotActive", "AgitatorSource", "ServoOnline", "ServoRpm",
    "ServoTorquePct", "ServoTorqueNm", "ServoLoadPct", "ServoPowerW", "ServoEnergyWh",
    "ServoState", "ServoAlarm", "ServoCommOk", "ServoCommErr", "SensorCommOK",
}

V10_KEYS = {
    "HubFirmwareVersion", "HubProtocolVersion", "ServoCommEnabled",
    "ServoCommandPending", "ServoCommandQueueDepth",
    "ServoControlCapable", "ServoMotorCommandId", "ServoMotorCommandAck",
    "ServoMotorCommandPending", "ServoMotorCommandDeliveries",
    "ServoMotorCommandAgeMs", "ServoMotorRequestedRpm", "ServoMotorAppliedRpm",
    "ServoMotorLeaseMs", "ServoMotorEnabled", "ServoMotorControlActive",
    "ServoMotorControlFault", "ServoMotorRouteAck", "MotorControlViaModbus",
    "FlowmeterReconnectWifi",
    "FlowTransitionVoltage",
    "PumpSlopeLow", "PumpSlopeHigh", "PumpTransitionSpeed", "PumpTransitionFlow", "PumpCalCrc",
}


def verify_legacy() -> None:
    manifest = OLD / "LEGACY_SHA256.txt"
    checked = 0
    for line in manifest.read_text(encoding="utf-8").splitlines():
        if not line or line.startswith("#"):
            continue
        size_text, digest, relative = re.split(r"\s+", line, maxsplit=2)
        path = OLD / relative
        data = path.read_bytes()
        assert len(data) == int(size_text), f"tamanho divergente: {relative}"
        assert hashlib.sha256(data).hexdigest().upper() == digest, f"hash divergente: {relative}"
        checked += 1
    assert checked == 10, f"esperados 10 sketches legados, encontrados {checked}"


def source_text() -> str:
    extensions = {".ino", ".h", ".cpp"}
    return "\n".join(
        path.read_text(encoding="utf-8", errors="strict")
        for path in sorted(V9.rglob("*"))
        if path.is_file() and path.suffix in extensions
    )


def verify_static_contract() -> None:
    source = source_text()
    missing_endpoints = sorted(endpoint for endpoint in ENDPOINTS if f'"{endpoint}"' not in source)
    missing_keys = sorted(key for key in V8_KEYS | V10_KEYS if f'\\"{key}\\"' not in source)
    assert not missing_endpoints, f"endpoints ausentes: {missing_endpoints}"
    assert not missing_keys, f"chaves ausentes: {missing_keys}"

    entry = (V9 / "ESP32S3-HUB.ino").read_text(encoding="utf-8")
    assert len(entry.splitlines()) <= 12, "o .ino voltou a concentrar implementação"
    assert "firmwareSetup();" in entry and "firmwareLoop();" in entry
    assert "server.on" not in entry and "Preferences" not in entry

    required_rules = {
        "finite floats": "parseFiniteFloat",
        "servo state range": "sample.state > 3",
        "poll lower bound": "kMinPollMs = 250",
        "poll upper bound": "kMaxPollMs = 10000",
        "servo FIFO capacity": "kCommandCapacity = 8",
        "HTTP queue": "httpCommandQueue.enqueue",
        "cache mutex": "cachedSampleId = sampleId",
        "motor desired state": "setMotorDesired(",
        "motor command id": "motor_cmd_id",
        "motor command lease": "motor_lease_ms",
        "motor route": "motor_route",
        "break-before-make": "sendMotorByUart(0)",
        "motor applied ack": "motorCommandAck_ == motorCommandId_",
        # A flowmeter that silently restarts must not have its zeroed state adopted as
        # the operator's request, and its reconnect switch must be reachable from here.
        "flowmeter boot id": "flowmeterBootId",
        "flowmeter reboot re-assert": "rebootDetected",
        "flowmeter reconnect command": "reconnectWifi",
    }
    missing_rules = [name for name, token in required_rules.items() if token not in source]
    assert not missing_rules, f"regras v10 ausentes: {missing_rules}"
    assert "motorSpeedPi" not in source, "laco PI obsoleto ainda esta no firmware"
    assert "motorSpeedPi" not in source, "laco PI obsoleto ainda esta no firmware"


def run_fixtures() -> None:
    result = subprocess.run(
        [sys.executable, "-m", "unittest", "discover", "-s", "tests/contracts", "-p", "test_*.py", "-v"],
        cwd=ROOT,
        check=False,
    )
    assert result.returncode == 0, "fixtures de contrato falharam"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--legacy-only", action="store_true")
    args = parser.parse_args()
    verify_legacy()
    print("OK: 10 hashes legados")
    if not args.legacy_only:
        verify_static_contract()
        print("OK: endpoints, chaves e regras estáticas v10")
        run_fixtures()
        print("OK: fixtures HTTP, presença e fila Servo")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
