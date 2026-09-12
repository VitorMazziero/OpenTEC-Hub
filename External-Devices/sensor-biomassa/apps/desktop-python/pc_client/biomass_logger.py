"""
biomass_logger.py -- entry point for the biomass sensor client.

Launches the desktop window by default. If no transport is given on the
command line the window opens a connection dialog, so it works as a
double-clickable app with no arguments at all.

    python biomass_logger.py                    # window + connection dialog
    python biomass_logger.py --serial COM5 --name ec01
    python biomass_logger.py --http 192.168.7.1
    python biomass_logger.py --auto --headless --start   # unattended logging
    python biomass_logger.py --list-ports

The window lives in biomass_gui.py; headless mode is here because it must
run without PySide6 installed.
"""

from __future__ import annotations

import argparse
import sys
import time
from datetime import datetime
from pathlib import Path

from biomass_core import (
    DEFAULT_HTTP_HOST,
    HttpTransport,
    Recorder,
    Sample,
    SensorClient,
    SerialTransport,
    Transport,
    TransportError,
    discover,
    list_candidate_ports,
)


def open_transport(args) -> Transport:
    """Opens the transport the arguments ask for, probing if unspecified."""
    if args.serial:
        t: Transport = SerialTransport(args.serial, baud=args.baud)
    elif args.http:
        t = HttpTransport(args.http)
    else:
        return discover(prefer=args.prefer, http_host=DEFAULT_HTTP_HOST)
    t.open()
    return t


def run_headless(args) -> int:
    t = open_transport(args)
    rec = Recorder(Path(args.outdir), args.name) if not args.no_save else None
    if rec:
        print(f"recording to {rec.csv_path}")

    def on_sample(s: Sample) -> None:
        flags = "".join(["H" if s.hd_mode else "", "S" if s.sat else "",
                         "1" if s.single else "", "M" if s.manual else ""])
        abs_txt = f"{s.absorbance:8.3f}" if s.valid else "     ERR"
        print(f"{datetime.fromtimestamp(s.wall_epoch):%H:%M:%S}  "
              f"seq {s.seq:<6d} A {abs_txt}  raw {s.raw:5d}  I0 {s.i0:5d}  "
              f"{s.it_ms:3d}ms {s.pwm_pct:5.1f}%  {flags}")

    client = SensorClient(t, recorder=rec, on_sample=on_sample,
                          on_log=lambda m: print(f"  . {m}"))
    try:
        client.prime()
        if args.start:
            client.command("start")
        print("logging; Ctrl-C to stop")
        while True:
            client.poll()
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print("\nstopping")
    finally:
        if rec:
            rec.close()
            print(f"wrote {rec.count} samples to {rec.csv_path}")
        t.close()
    return 0


def run_gui(args) -> int:
    try:
        import biomass_gui
    except ImportError as exc:
        print(f"The window needs PySide6, pyqtgraph and numpy ({exc}).\n"
              "    pip install PySide6 pyqtgraph numpy\n"
              "Or run without a GUI:  --headless", file=sys.stderr)
        return 2
    return biomass_gui.launch(args)


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Desktop control, live plot and CSV logger for the "
                    "VEML7700 biomass sensor.")
    src = ap.add_mutually_exclusive_group()
    src.add_argument("--serial", metavar="PORT", help="serial port, e.g. COM5")
    src.add_argument("--http", metavar="HOST", nargs="?", const=DEFAULT_HTTP_HOST,
                     help=f"device HTTP host (default {DEFAULT_HTTP_HOST})")
    src.add_argument("--auto", action="store_true",
                     help="probe serial ports then the AP instead of asking")
    ap.add_argument("--prefer", choices=["serial", "http"], default="serial",
                    help="probe order for --auto (default: serial)")
    ap.add_argument("--baud", type=int, default=115200)
    ap.add_argument("--name", default="run", help="run name, used in the folder name")
    ap.add_argument("--outdir", default="runs", help="where run folders are created")
    ap.add_argument("--interval", type=float, default=1.0,
                    help="client poll interval in seconds (default 1.0). This "
                         "is how often the PC asks; the device's own sampling "
                         "interval is set in the window.")
    ap.add_argument("--theme", choices=["dark", "light"], default="dark")
    ap.add_argument("--headless", action="store_true", help="no window, log only")
    ap.add_argument("--autosave", action="store_true",
                    help="window: start recording on connect instead of "
                         "waiting for New experiment. Headless mode always "
                         "records unless --no-save is given")
    ap.add_argument("--no-save", action="store_true",
                    help="do not start recording automatically")
    ap.add_argument("--start", action="store_true",
                    help="send the start command once connected")
    ap.add_argument("--list-ports", action="store_true")
    args = ap.parse_args()

    if args.list_ports:
        ports = list_candidate_ports()
        print("\n".join(ports) if ports else "no serial ports found")
        return 0

    try:
        return run_headless(args) if args.headless else run_gui(args)
    except TransportError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
