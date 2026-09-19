"""Build the window offscreen and check what the operator would actually see.

No hardware and no display: the table is filled from the repository alone, then
a synthetic /nodes answer is pushed in to confirm the address resolution order
and the version comparison, which are the two decisions the interface makes on
the operator's behalf.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

# Must be set before QApplication so the test runs on a machine with no display.
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6 import QtCore, QtWidgets  # noqa: E402

from updater import hub  # noqa: E402
from updater.devices import DEVICES  # noqa: E402
from updater.mainwindow import (  # noqa: E402
    COL_ADDRESS,
    COL_DEVICE,
    COL_NODE_VERSION,
    COL_REPO_VERSION,
    COL_STATE,
    MainWindow,
)

failures: list[str] = []


def check(condition: bool, message: str) -> None:
    if condition:
        print(f"  ok   {message}")
    else:
        print(f"  FAIL {message}")
        failures.append(message)


def cell(window: MainWindow, row: int, column: int) -> str:
    item = window.table.item(row, column)
    return item.text() if item is not None else ""


def main() -> int:
    application = QtWidgets.QApplication.instance() or QtWidgets.QApplication([])
    window = MainWindow()

    print("Montagem da janela")
    check(window.table.rowCount() == len(DEVICES), f"{len(DEVICES)} linhas na tabela")
    check(
        all(
            window.table.item(index, COL_DEVICE).text() == device.name
            for index, device in enumerate(DEVICES)
        ),
        "nomes dos dispositivos na ordem do catalogo",
    )
    check(
        all(
            window.table.item(index, COL_DEVICE).checkState() == QtCore.Qt.Unchecked
            for index in range(window.table.rowCount())
        ),
        "nada marcado ao abrir, nenhuma gravacao por acidente",
    )
    check(not window.cancel_button.isEnabled(), "cancelar desabilitado sem fila")

    print("\nSem consulta ao Hub")
    check(
        cell(window, 0, COL_STATE) == "Nao consultado",
        f"estado inicial: {cell(window, 0, COL_STATE)}",
    )
    check(
        "AP padrao" in cell(window, 0, COL_ADDRESS)
        and DEVICES[0].default_ap_ip in cell(window, 0, COL_ADDRESS),
        f"endereco recai no AP padrao: {cell(window, 0, COL_ADDRESS)}",
    )

    print("\nApos resposta do Hub")
    pump_index = next(i for i, d in enumerate(DEVICES) if d.key == "pump")
    window._on_discovery_ok(
        {
            "pump": hub.NodeStatus(
                dev="pump",
                ip="192.168.4.12",
                mac="AA:BB:CC:DD:EE:01",
                version="3.12",
                online=True,
                registered=True,
                age_ms=500,
            )
        }
    )
    check(
        "192.168.4.12" in cell(window, pump_index, COL_ADDRESS)
        and "(Hub)" in cell(window, pump_index, COL_ADDRESS),
        f"endereco vindo do Hub: {cell(window, pump_index, COL_ADDRESS)}",
    )
    check(
        cell(window, pump_index, COL_STATE).startswith("Online"),
        f"estado da bomba: {cell(window, pump_index, COL_STATE)}",
    )
    check(
        cell(window, pump_index, COL_NODE_VERSION) == "3.12",
        "versao reportada pelo no exibida",
    )
    check(
        cell(window, pump_index, COL_NODE_VERSION) == cell(window, pump_index, COL_REPO_VERSION),
        "versao do no igual a do repositorio neste cenario",
    )

    print("\nPrecedencia do endereco manual")
    row = window._rows["pump"]
    row.ip_override = "192.168.6.1"
    window._refresh_table()
    check(
        "192.168.6.1" in cell(window, pump_index, COL_ADDRESS)
        and "(manual)" in cell(window, pump_index, COL_ADDRESS),
        f"manual tem precedencia sobre o Hub: {cell(window, pump_index, COL_ADDRESS)}",
    )
    row.ip_override = ""
    window._refresh_table()
    check(
        "192.168.4.12" in cell(window, pump_index, COL_ADDRESS),
        "limpar o manual devolve o endereco do Hub",
    )

    print("\nSelecao")
    window._set_all_checked(True)
    check(len(window._checked_rows()) == len(DEVICES), "marcar todos seleciona todos")
    window._set_all_checked(False)
    check(not window._checked_rows(), "desmarcar todos limpa a selecao")

    print("\nHub indisponivel")
    window._on_discovery_failed("teste")
    check(
        cell(window, pump_index, COL_STATE) == "Nao consultado",
        "falha do Hub descarta o estado anterior em vez de mante-lo",
    )
    check(
        DEVICES[pump_index].default_ap_ip in cell(window, pump_index, COL_ADDRESS),
        "endereco volta ao AP padrao apos falha do Hub",
    )

    window.close()
    del window
    application.processEvents()

    print()
    if failures:
        print(f"{len(failures)} verificacao(oes) falharam.")
        return 1
    print("Interface consistente.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
