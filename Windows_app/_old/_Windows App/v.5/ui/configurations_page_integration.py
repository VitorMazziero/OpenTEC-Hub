#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
ui/configurations_page_integration.py
~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
Demonstrates how ConfigurationsPage (and any other UI widget) should
connect to ConnectionManager.connection_state_changed to drive button
enabled/disabled state without any race conditions.

DROP-IN replacement for the connection-control section of ConfigurationsPage.
"""
from __future__ import annotations

from typing import Optional

from PySide6.QtCore import Qt, QTimer, Slot
from PySide6.QtWidgets import (
    QWidget, QGroupBox, QFormLayout, QHBoxLayout,
    QPushButton, QComboBox, QLineEdit, QLabel,
)

from communication.connection_manager import ConnectionManager, ConnectionState


# ---------------------------------------------------------------------------
# Mapping: which buttons are enabled for each state
# ---------------------------------------------------------------------------
#
#   State          | Connect btn | Stop btn | Medium/IP/Port fields
#   ---------------+-------------+----------+---------------------
#   DISCONNECTED   |     ON      |   OFF    |         ON
#   CONNECTING     |     OFF     |   ON     |         OFF
#   CONNECTED      |     OFF     |   ON     |         OFF
#   RECONNECTING   |     OFF     |   ON     |         OFF
#   ERROR          |     ON      |   OFF    |         ON
#
_BUTTON_MATRIX: dict[ConnectionState, tuple[bool, bool, bool]] = {
    #                                     connect  stop    fields
    ConnectionState.DISCONNECTED: (True,  False,  True),
    ConnectionState.CONNECTING:   (False, True,   False),
    ConnectionState.CONNECTED:    (False, True,   False),
    ConnectionState.RECONNECTING: (False, True,   False),
    ConnectionState.ERROR:        (True,  False,  True),
}


class ConnectionControlBlock(QGroupBox):
    """
    Self-contained widget that owns the Connect/Stop buttons and the
    medium selector (USB port / IP).

    It listens to ``ConnectionManager.connection_state_changed`` and updates
    all interactive elements automatically – the rest of the application
    never needs to touch button enabled state.
    """

    def __init__(
        self,
        comm: ConnectionManager,
        parent: Optional[QWidget] = None,
    ) -> None:
        super().__init__("Conexão (USB / Wi-Fi)", parent)
        self._comm = comm

        self._build_ui()
        self._wire_signals()

        # Set initial UI state based on current manager state
        # (manager starts DISCONNECTED so this is a clean slate)
        self._apply_state(ConnectionState.DISCONNECTED, "")

    # ------------------------------------------------------------------
    # UI construction
    # ------------------------------------------------------------------

    def _build_ui(self) -> None:
        form = QFormLayout(self)

        # Transport selector
        self.medium_combo = QComboBox()
        self.medium_combo.addItems(["USB", "WiFi"])
        form.addRow("Meio:", self.medium_combo)

        # USB port row
        usb_row = QWidget()
        usb_layout = QHBoxLayout(usb_row)
        usb_layout.setContentsMargins(0, 0, 0, 0)
        self.com_port_combo = QComboBox()
        self.refresh_ports_btn = QPushButton("Atualizar")
        self.refresh_ports_btn.setMaximumWidth(80)
        usb_layout.addWidget(self.com_port_combo)
        usb_layout.addWidget(self.refresh_ports_btn)
        form.addRow("Porta COM:", usb_row)

        # Wi-Fi IP row
        self.ip_edit = QLineEdit("192.168.4.1")
        form.addRow("Endereço IP:", self.ip_edit)

        # Status indicator label
        self.status_label = QLabel("Desconectado")
        self.status_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        form.addRow("Status:", self.status_label)

        # Connect / Stop buttons
        btn_row = QWidget()
        btn_layout = QHBoxLayout(btn_row)
        btn_layout.setContentsMargins(0, 0, 0, 0)
        self.connect_btn = QPushButton("Conectar")
        self.stop_btn    = QPushButton("Parar Conexão")
        btn_layout.addWidget(self.connect_btn)
        btn_layout.addWidget(self.stop_btn)
        form.addRow(btn_row)

        # Fields that should be locked while a connection is active
        self._field_widgets: list[QWidget] = [
            self.medium_combo,
            self.com_port_combo,
            self.refresh_ports_btn,
            self.ip_edit,
        ]

    # ------------------------------------------------------------------
    # Signal wiring
    # ------------------------------------------------------------------

    def _wire_signals(self) -> None:
        # React to state machine transitions
        # Signal is emitted from any thread – Qt delivers it on the UI thread
        self._comm.connection_state_changed.connect(self._on_state_changed)

        # Button actions
        self.connect_btn.clicked.connect(self._on_connect_clicked)
        self.stop_btn.clicked.connect(self._on_stop_clicked)
        self.refresh_ports_btn.clicked.connect(self._refresh_com_ports)

        # Also populate ports on startup
        self._refresh_com_ports()

    # ------------------------------------------------------------------
    # Slots
    # ------------------------------------------------------------------

    @Slot(ConnectionState, str)
    def _on_state_changed(self, state: ConnectionState, medium: str) -> None:
        """
        This slot is the SINGLE place that updates connect/stop button state.
        It is connected to connection_state_changed, which is emitted by the
        ConnectionManager worker thread → Qt automatically queues it to run
        on the UI thread, so no QTimer.singleShot hack needed.
        """
        self._apply_state(state, medium)

    @Slot()
    def _on_connect_clicked(self) -> None:
        medium = self.medium_combo.currentText()
        if medium == "USB":
            port = self.com_port_combo.currentText().strip()
            if not port or "Nenhuma porta" in port:
                self.status_label.setText("Nenhuma porta USB disponível")
                return
            # Retrieve stored USB params from parent ConfigurationsPage
            conf_page = self._find_configurations_page()
            baud       = int(getattr(conf_page, "usb_baud_rate", 115200))
            data_bits  = int(getattr(conf_page, "usb_data_bits", 8))
            stop_bits  = int(getattr(conf_page, "usb_stop_bits", 1))
            parity     = getattr(conf_page, "usb_parity", "None")
            self._comm.connect_usb(port, baud, data_bits, stop_bits, parity)
        else:
            self._comm.connect_wifi(self.ip_edit.text().strip())

    @Slot()
    def _on_stop_clicked(self) -> None:
        self._comm.disconnect()

    @Slot()
    def _refresh_com_ports(self) -> None:
        from serial.tools import list_ports
        self.com_port_combo.clear()
        ports = list_ports.comports()
        for p in ports:
            self.com_port_combo.addItem(p.device)
        if not ports:
            self.com_port_combo.addItem("Nenhuma porta disponível")

    # ------------------------------------------------------------------
    # State → UI mapper
    # ------------------------------------------------------------------

    _STATE_LABEL: dict[ConnectionState, str] = {
        ConnectionState.DISCONNECTED: "Desconectado",
        ConnectionState.CONNECTING:   "Conectando…",
        ConnectionState.CONNECTED:    "Conectado",
        ConnectionState.RECONNECTING: "Reconectando (backup)…",
        ConnectionState.ERROR:        "Erro de conexão",
    }

    def _apply_state(self, state: ConnectionState, medium: str) -> None:
        connect_on, stop_on, fields_on = _BUTTON_MATRIX.get(
            state, (True, False, True)
        )
        self.connect_btn.setEnabled(connect_on)
        self.stop_btn.setEnabled(stop_on)

        for w in self._field_widgets:
            w.setEnabled(fields_on)

        label = self._STATE_LABEL.get(state, "Desconhecido")
        if medium:
            label = f"{label} [{medium}]"
        self.status_label.setText(label)

    # ------------------------------------------------------------------

    def _find_configurations_page(self):
        """Walk up the widget tree to find the ConfigurationsPage."""
        p = self.parent()
        while p is not None:
            if hasattr(p, "usb_baud_rate"):
                return p
            p = p.parent() if hasattr(p, "parent") else None
        return self


# ---------------------------------------------------------------------------
# Example: patching the existing ConfigurationsPage (minimal changes needed)
# ---------------------------------------------------------------------------

class ConfigurationsPageConnectionMixin:
    """
    Mixin to replace the ad-hoc ``on_connection_failed`` /
    ``on_connection_changed`` callbacks in the original ConfigurationsPage
    with a single clean slot wired to connection_state_changed.

    Usage in ConfigurationsPage.__init__::

        # OLD:
        self.comm_handler.set_connection_failed_callback(self.on_connection_failed)
        self.comm_handler.set_connection_changed_callback(self.on_connection_changed)

        # NEW (after adding this mixin to the class hierarchy):
        self.comm_handler.connection_state_changed.connect(self.on_state_changed)
    """

    @Slot(ConnectionState, str)
    def on_state_changed(self, state: ConnectionState, medium: str) -> None:
        """
        Atualiza apenas o estado visual dos botões.
        O log fica centralizado no ConnectionManager.
        """
        connect_on, stop_on, _ = _BUTTON_MATRIX.get(state, (True, False, True))

        if hasattr(self, "connect_button"):
            self.connect_button.setEnabled(connect_on)
        if hasattr(self, "stop_button"):
            self.stop_button.setEnabled(stop_on)

    def on_connection_failed(self, medium: str) -> None:
        pass   # handled by on_state_changed

    def on_connection_changed(self, connected: bool, medium: Optional[str]) -> None:
        pass   # handled by on_state_changed


# ---------------------------------------------------------------------------
# Minimal integration test / usage example
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    """
    Quick smoke test: create a ConnectionManager and ConnectionControlBlock,
    show the window, and verify buttons toggle correctly when connect is clicked.
    """
    import sys
    from PySide6.QtWidgets import QApplication, QMainWindow, QVBoxLayout

    app = QApplication(sys.argv)

    # Stub alarm manager so we don't need the real file
    import types, sys as _sys
    fake_alarm = types.ModuleType("alarm_manager")
    fake_alarm.AlarmThread = type("AlarmThread", (), {"__init__": lambda *a, **k: None, "start": lambda *a: None, "is_alive": lambda *a: False})
    _sys.modules.setdefault("alarm_manager", fake_alarm)

    comm = ConnectionManager()
    comm.log_message.connect(lambda m: print(m))

    win = QMainWindow()
    central = QWidget()
    layout = QVBoxLayout(central)
    block = ConnectionControlBlock(comm, central)
    layout.addWidget(block)
    win.setCentralWidget(central)
    win.resize(400, 300)
    win.setWindowTitle("Connection Control – State Machine Demo")
    win.show()

    sys.exit(app.exec())