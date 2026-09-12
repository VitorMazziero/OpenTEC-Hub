"""Checks the public desktop entry point without opening a window."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path


PC_CLIENT = Path(__file__).resolve().parents[1]
SOFTWARE_ROOT = PC_CLIENT.parent
sys.path.insert(0, str(PC_CLIENT))

from biomass_desktop import build_parser


fails = []


def check(cond, msg):
    print(("  PASS  " if cond else "  FAIL  ") + msg)
    if not cond:
        fails.append(msg)


def main() -> int:
    print("[desktop defaults]")
    args = build_parser().parse_args([])
    check(args.serial is None and args.http is None and not args.auto and not args.demo,
          "no arguments opens the connection dialog")
    check(args.name == "run", "default run name is stable")
    check(args.outdir == Path("runs"), "recordings default to ./runs")
    check(args.theme == "dark", "dark theme is the default")
    # Opening the app must not create a run folder. Unattended logging asks
    # for itself; a window nobody asked to record does not.
    check(not args.autosave, "the window opens without recording by default")

    print("\n[connection options]")
    auto = build_parser().parse_args(["--auto", "--prefer", "http"])
    check(auto.auto and auto.prefer == "http", "automatic discovery order parses")
    wifi = build_parser().parse_args(["--http"])
    check(wifi.http == "192.168.7.1", "bare --http selects the firmware AP")
    usb = build_parser().parse_args(["--serial", "COM7", "--autosave"])
    check(usb.serial == "COM7" and usb.autosave,
          "explicit USB with unattended recording parses")
    # --no-save is now the default. Kept accepted so an existing shortcut or
    # lab script does not fail to launch.
    legacy = build_parser().parse_args(["--serial", "COM7", "--no-save"])
    check(legacy.no_save, "the retired --no-save flag still parses")
    demo = build_parser().parse_args(["--demo"])
    check(demo.demo and demo.serial is None and demo.http is None,
          "hardware-free demo mode parses as a connection source")
    portuguese = build_parser().parse_args(["--demo", "--language", "pt-BR"])
    check(portuguese.language == "pt-BR",
          "Brazilian Portuguese can be selected at launch")

    print("\n[repository entry point]")
    result = subprocess.run(
        [sys.executable, str(SOFTWARE_ROOT / "app.py"), "--help"],
        cwd=SOFTWARE_ROOT, capture_output=True, text=True, timeout=20,
    )
    check(result.returncode == 0, "python app.py --help exits successfully")
    check("Desktop control" in result.stdout and "--serial" in result.stdout,
          "public help describes the UI and connection options")

    binding = subprocess.run(
        [sys.executable, "-c",
         "import sys; "
         f"sys.path.insert(0, {str(PC_CLIENT)!r}); "
         "import biomass_gui; "
         "from pyqtgraph.Qt import QT_LIB; print(QT_LIB)"],
        cwd=SOFTWARE_ROOT, capture_output=True, text=True, timeout=20,
    )
    check(binding.returncode == 0 and binding.stdout.strip() == "PySide6",
          f"normal app import pins pyqtgraph to PySide6 ({binding.stdout.strip()})")

    print("\n" + ("ALL PASS" if not fails else f"{len(fails)} FAILURES:"))
    for failure in fails:
        print("  - " + failure)
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
