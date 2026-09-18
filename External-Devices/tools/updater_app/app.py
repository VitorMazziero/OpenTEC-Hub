"""
app.py -- entry point for the external-device updater.

Run from this directory:

    python app.py
"""

from __future__ import annotations

import sys
from pathlib import Path

# Allows `python app.py` from any working directory without installing the
# package, which is how the other bench tools in this repository are launched.
sys.path.insert(0, str(Path(__file__).resolve().parent))

from PySide6 import QtWidgets  # noqa: E402

from updater.mainwindow import MainWindow  # noqa: E402


def main() -> int:
    application = QtWidgets.QApplication(sys.argv)
    application.setApplicationName("Atualizador de Dispositivos Externos")
    application.setOrganizationName("Projeto TECNAL")

    window = MainWindow()
    window.show()
    return application.exec()


if __name__ == "__main__":
    raise SystemExit(main())
