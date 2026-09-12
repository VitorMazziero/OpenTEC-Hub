"""Command-line setup for the Biomass Sensor desktop application.

This module contains only launch configuration. Qt widgets live in
``biomass_gui.py`` and device/protocol code lives in ``biomass_core.py``.
The repository-level ``app.py`` is the normal user entry point.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path
from typing import Optional, Sequence

from biomass_core import DEFAULT_HTTP_HOST, TransportError


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Desktop control, live plots, and CSV recording for the "
                    "Biomass Sensor firmware v5.0 (works with v4.1+)."
    )
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--serial", metavar="PORT",
                        help="connect directly to a USB serial port, e.g. COM5")
    source.add_argument("--http", metavar="HOST", nargs="?",
                        const=DEFAULT_HTTP_HOST,
                        help=f"connect over WiFi (default {DEFAULT_HTTP_HOST})")
    source.add_argument("--auto", action="store_true",
                        help="probe for the sensor instead of showing the connection dialog")
    source.add_argument("--demo", action="store_true",
                        help="run an interactive firmware v5.0 simulation (no hardware)")
    parser.add_argument("--prefer", choices=["serial", "http"], default="serial",
                        help="probe order with --auto (default: serial)")
    parser.add_argument("--baud", type=int, default=115200,
                        help="USB serial baud rate (default: 115200)")
    parser.add_argument("--name", default="run",
                        help="run name used in the recording folder")
    parser.add_argument("--outdir", default="runs", type=Path,
                        help="recording root folder (default: ./runs)")
    parser.add_argument("--interval", type=float, default=1.0,
                        help="PC status polling interval in seconds (default: 1.0)")
    parser.add_argument("--theme", choices=["dark", "light"], default="dark")
    parser.add_argument("--language", choices=["en", "pt-BR"], default="en",
                        help="initial interface language (default: en)")
    parser.add_argument("--autosave", action="store_true",
                        help="begin recording as soon as the device is "
                             "connected, including whatever it still holds in "
                             "its buffer (for unattended logging). Without it "
                             "the window opens in viewing mode and recording "
                             "starts from New experiment")
    parser.add_argument("--no-save", action="store_true",
                        help=argparse.SUPPRESS)   # now the default; kept so
                                                  # existing shortcuts still run
    parser.add_argument("--start", action="store_true",
                        help="start measurement after connecting")
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = build_parser().parse_args(argv)
    # Demo data must never be mistaken for an automatically recorded run.
    # The operator can still start a recording explicitly from the Run tab.
    if args.demo or args.no_save:
        args.autosave = False
    try:
        import biomass_gui
    except ImportError as exc:
        print(
            "The desktop app needs PySide6, pyqtgraph, and numpy "
            f"({exc}).\nInstall them with: pip install -r pc_client/requirements.txt",
            file=sys.stderr,
        )
        return 2

    try:
        return biomass_gui.launch(args)
    except TransportError as exc:
        print(f"Connection error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
