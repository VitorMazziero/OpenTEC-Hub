"""User entry point for the Biomass Sensor desktop application.

Run this file from the ``software`` folder:

    python app.py

The implementation stays under ``pc_client`` so the firmware, desktop app,
and tests remain separate at the repository root.
"""

from __future__ import annotations

import sys
from pathlib import Path


PC_CLIENT = Path(__file__).resolve().parent / "pc_client"
if str(PC_CLIENT) not in sys.path:
    sys.path.insert(0, str(PC_CLIENT))

from biomass_desktop import main


if __name__ == "__main__":
    raise SystemExit(main())
