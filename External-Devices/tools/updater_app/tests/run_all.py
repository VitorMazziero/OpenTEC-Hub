"""Run every hardware-free check from one command."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

TEST_DIR = Path(__file__).resolve().parent
APP_DIR = TEST_DIR.parent
TESTS = [
    "test_catalog.py",
    "test_hub.py",
    "test_ota.py",
    "test_gui.py",
]


def main() -> int:
    failed = []
    for name in TESTS:
        print(f"\n{'=' * 72}\n{name}\n{'=' * 72}", flush=True)
        result = subprocess.run([sys.executable, str(TEST_DIR / name)], cwd=APP_DIR)
        if result.returncode:
            failed.append(name)

    print(f"\n{'=' * 72}")
    if failed:
        print(f"Falharam: {', '.join(failed)}")
        return 1
    print(f"Todos os {len(TESTS)} conjuntos passaram.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
