"""Run every hardware-free check from one command."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path


TEST_DIR = Path(__file__).resolve().parent
PC_CLIENT = TEST_DIR.parent
TESTS = [
    "test_app_entry.py",
    "test_demo.py",
    "test_client.py",
    "test_ringbuffer.py",
    "test_serial.py",
    "check_firmware.py",
    "test_gui.py",
]


def main() -> int:
    failed = []
    for name in TESTS:
        print(f"\n{'=' * 72}\n{name}\n{'=' * 72}", flush=True)
        result = subprocess.run([sys.executable, str(TEST_DIR / name)],
                                cwd=PC_CLIENT)
        if result.returncode:
            failed.append(name)

    if failed:
        print("\nFAILED: " + ", ".join(failed))
        return 1
    print(f"\nALL {len(TESTS)} TEST PROGRAMS PASSED")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
