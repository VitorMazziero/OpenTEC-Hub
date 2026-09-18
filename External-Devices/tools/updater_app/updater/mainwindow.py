"""
mainwindow.py -- the updater window: one table, one queue, one log.

The table is the whole state of the bench: for each device, where it is, what
it is running, what the repository declares and which image would be sent. The
operator ticks the devices to update and the queue runs them in order.

Two facts drive most of the interface. A device answers OTA either through the
network of the Hub or as its own access point, so an address is resolved per
device and can always be overridden by hand. And the version a node reports is
only comparable to the version declared in the firmware source, which is what a
fresh build would install -- the comparison is shown, never acted on
automatically, because during a bench run the operator decides what gets
flashed.
"""

from __future__ import annotations

from pathlib import Path
from typing import Optional

from PySide6 import QtCore, QtGui, QtWidgets

from . import firmware, hub
from .devices import DEFAULT_LIBRARIES, DEVICES, HUB_DEFAULT_IP, Device
from .jobs import DiscoveryWorker, Job, JobRunner

COL_DEVICE = 0
COL_STATE = 1
COL_ADDRESS = 2
COL_NODE_VERSION = 3
COL_REPO_VERSION = 4
COL_IMAGE = 5
COL_BUILT = 6
COLUMN_COUNT = 7

# Two palettes on purpose. The log is light-on-dark, so it uses the bright
# values; the table sits on the window background, where those same values wash
# out. The version mismatch is the signal an operator most needs to catch at a
# glance, so it gets a colour with real contrast rather than a matching one.
LOG_COLORS = {
    "info": "#d4d4d4",
    "ok": "#4ec9b0",
    "warn": "#dcdcaa",
    "error": "#f48771",
    "muted": "#808080",
}

TABLE_OK = "#1a7f5a"
TABLE_WARN = "#9a5b00"
TABLE_ERROR = "#b32218"
TABLE_MUTED = "#707070"
TABLE_BUSY = "#1c5fa8"


class DeviceRow:
    """Per-device interface state: what the Hub said and what the operator chose."""

    def __init__(self, device: Device) -> None:
        self.device = device
        self.node: Optional[hub.NodeStatus] = None
        self.ip_override: str = ""
        self.binary_override: Optional[Path] = None
        self.phase: str = ""

    @property
    def address(self) -> str:
        """Where an upload would be sent, in the order the publisher resolves it."""
        if self.ip_override:
            return self.ip_override
        if self.node is not None and self.node.has_address:
            return self.node.ip
        return self.device.default_ap_ip

    @property
    def address_source(self) -> str:
        if self.ip_override:
            return "manual"
        if self.node is not None and self.node.has_address:
            return "Hub"
        return "AP padrao"

    @property
    def binary(self) -> Optional[firmware.BinaryInfo]:
        if self.binary_override is not None:
            if self.binary_override.is_file():
                return firmware.describe(self.binary_override)
            return None
        return firmware.latest_binary(self.device)


class MainWindow(QtWidgets.QMainWindow):
    def __init__(self) -> None:
        super().__init__()
        self.setWindowTitle("Atualizador de Dispositivos Externos - Projeto TECNAL")
        self.resize(1180, 780)

        self._rows = {device.key: DeviceRow(device) for device in DEVICES}
        self._runner: Optional[JobRunner] = None
        self._discovery: Optional[DiscoveryWorker] = None

        self._build_ui()
        self._refresh_table()
        self._log("Pronto. Atualize o estado para localizar os nos pelo Hub.", "muted")

    # ------------------------------------------------------------------
    # Construction
    # ------------------------------------------------------------------

    def _build_ui(self) -> None:
        central = QtWidgets.QWidget()
        layout = QtWidgets.QVBoxLayout(central)
        layout.setContentsMargins(12, 12, 12, 12)
        layout.setSpacing(10)

        layout.addWidget(self._build_connection_box())
        layout.addWidget(self._build_table(), stretch=3)
        layout.addWidget(self._build_actions())
        layout.addWidget(self._build_progress())
        layout.addWidget(self._build_log(), stretch=2)

        self.setCentralWidget(central)
        self.statusBar().showMessage("Ocioso")

    def _build_connection_box(self) -> QtWidgets.QWidget:
        box = QtWidgets.QGroupBox("Conexao")
        grid = QtWidgets.QGridLayout(box)

        grid.addWidget(QtWidgets.QLabel("IP do Hub:"), 0, 0)
        self.hub_ip_edit = QtWidgets.QLineEdit(HUB_DEFAULT_IP)
        self.hub_ip_edit.setMaximumWidth(160)
        self.hub_ip_edit.setToolTip(
            "Endereco do ESP32S3-HUB. O aplicativo le /nodes para descobrir onde "
            "cada dispositivo esta. Sem o Hub, usa o AP padrao de cada um."
        )
        grid.addWidget(self.hub_ip_edit, 0, 1)

        self.refresh_button = QtWidgets.QPushButton("Atualizar estado")
        self.refresh_button.clicked.connect(self._start_discovery)
        grid.addWidget(self.refresh_button, 0, 2)

        self.hub_status_label = QtWidgets.QLabel("Estado do Hub: nao consultado")
        self.hub_status_label.setStyleSheet("color: #808080;")
        grid.addWidget(self.hub_status_label, 0, 3)

        grid.addWidget(QtWidgets.QLabel("Bibliotecas Arduino:"), 1, 0)
        self.libraries_edit = QtWidgets.QLineEdit(str(DEFAULT_LIBRARIES))
        self.libraries_edit.setToolTip(
            "Passado ao arduino-cli como --libraries. As bibliotecas compartilhadas "
            "ficam fora do repositorio; sem este caminho a compilacao falha."
        )
        grid.addWidget(self.libraries_edit, 1, 1, 1, 3)

        browse = QtWidgets.QPushButton("Procurar...")
        browse.clicked.connect(self._choose_libraries)
        grid.addWidget(browse, 1, 4)

        grid.setColumnStretch(3, 1)
        return box

    def _build_table(self) -> QtWidgets.QWidget:
        self.table = QtWidgets.QTableWidget(len(DEVICES), COLUMN_COUNT)
        self.table.setHorizontalHeaderLabels(
            [
                "Dispositivo",
                "Estado",
                "Endereco",
                "Versao no dispositivo",
                "Versao no repositorio",
                "Imagem a enviar",
                "Compilada em",
            ]
        )
        self.table.verticalHeader().setVisible(False)
        self.table.setAlternatingRowColors(True)
        self.table.setSelectionBehavior(QtWidgets.QAbstractItemView.SelectRows)
        self.table.setSelectionMode(QtWidgets.QAbstractItemView.SingleSelection)
        self.table.setEditTriggers(QtWidgets.QAbstractItemView.NoEditTriggers)
        self.table.setContextMenuPolicy(QtCore.Qt.CustomContextMenu)
        self.table.customContextMenuRequested.connect(self._show_row_menu)

        header = self.table.horizontalHeader()
        header.setSectionResizeMode(COL_DEVICE, QtWidgets.QHeaderView.ResizeToContents)
        header.setSectionResizeMode(COL_IMAGE, QtWidgets.QHeaderView.Stretch)
        for column in (COL_STATE, COL_ADDRESS, COL_NODE_VERSION, COL_REPO_VERSION, COL_BUILT):
            header.setSectionResizeMode(column, QtWidgets.QHeaderView.ResizeToContents)

        for index, device in enumerate(DEVICES):
            item = QtWidgets.QTableWidgetItem(device.name)
            item.setFlags(item.flags() | QtCore.Qt.ItemIsUserCheckable)
            item.setCheckState(QtCore.Qt.Unchecked)
            item.setData(QtCore.Qt.UserRole, device.key)
            item.setToolTip(f"{device.key} - {device.sketch_dir}")
            self.table.setItem(index, COL_DEVICE, item)
            for column in range(1, COLUMN_COUNT):
                self.table.setItem(index, column, QtWidgets.QTableWidgetItem(""))

        return self.table

    def _build_actions(self) -> QtWidgets.QWidget:
        widget = QtWidgets.QWidget()
        row = QtWidgets.QHBoxLayout(widget)
        row.setContentsMargins(0, 0, 0, 0)

        select_all = QtWidgets.QPushButton("Marcar todos")
        select_all.clicked.connect(lambda: self._set_all_checked(True))
        row.addWidget(select_all)

        clear_all = QtWidgets.QPushButton("Desmarcar todos")
        clear_all.clicked.connect(lambda: self._set_all_checked(False))
        row.addWidget(clear_all)

        row.addSpacing(24)

        self.compile_button = QtWidgets.QPushButton("Compilar")
        self.compile_button.setToolTip("Compila o firmware dos dispositivos marcados, sem enviar.")
        self.compile_button.clicked.connect(lambda: self._start_queue(True, False))
        row.addWidget(self.compile_button)

        self.upload_button = QtWidgets.QPushButton("Enviar OTA")
        self.upload_button.setToolTip("Envia a imagem ja compilada para os dispositivos marcados.")
        self.upload_button.clicked.connect(lambda: self._start_queue(False, True))
        row.addWidget(self.upload_button)

        self.both_button = QtWidgets.QPushButton("Compilar e enviar")
        self.both_button.setDefault(True)
        self.both_button.clicked.connect(lambda: self._start_queue(True, True))
        row.addWidget(self.both_button)

        row.addStretch(1)

        self.cancel_button = QtWidgets.QPushButton("Cancelar")
        self.cancel_button.setEnabled(False)
        self.cancel_button.clicked.connect(self._cancel_queue)
        row.addWidget(self.cancel_button)

        return widget

    def _build_progress(self) -> QtWidgets.QWidget:
        self.progress = QtWidgets.QProgressBar()
        self.progress.setRange(0, 100)
        self.progress.setValue(0)
        self.progress.setFormat("Ocioso")
        return self.progress

    def _build_log(self) -> QtWidgets.QWidget:
        box = QtWidgets.QGroupBox("Registro")
        layout = QtWidgets.QVBoxLayout(box)

        self.log_view = QtWidgets.QPlainTextEdit()
        self.log_view.setReadOnly(True)
        self.log_view.setMaximumBlockCount(5000)
        font = QtGui.QFontDatabase.systemFont(QtGui.QFontDatabase.FixedFont)
        font.setPointSize(9)
        self.log_view.setFont(font)
        # Qualified by class and paired with the palette: under the native
        # Windows style an unqualified rule leaves the viewport white, and the
        # light text of the log becomes unreadable against it.
        self.log_view.setStyleSheet(
            "QPlainTextEdit { background-color: #1e1e1e; color: #d4d4d4; "
            "border: 1px solid #3c3c3c; }"
        )
        palette = self.log_view.palette()
        palette.setColor(QtGui.QPalette.Base, QtGui.QColor("#1e1e1e"))
        palette.setColor(QtGui.QPalette.Text, QtGui.QColor("#d4d4d4"))
        self.log_view.setPalette(palette)
        layout.addWidget(self.log_view)

        buttons = QtWidgets.QHBoxLayout()
        buttons.addStretch(1)
        clear = QtWidgets.QPushButton("Limpar registro")
        clear.clicked.connect(self.log_view.clear)
        buttons.addWidget(clear)
        save = QtWidgets.QPushButton("Salvar registro...")
        save.clicked.connect(self._save_log)
        buttons.addWidget(save)
        layout.addLayout(buttons)

        return box

    # ------------------------------------------------------------------
    # Table state
    # ------------------------------------------------------------------

    def _selected_row(self) -> Optional[DeviceRow]:
        index = self.table.currentRow()
        if index < 0:
            return None
        item = self.table.item(index, COL_DEVICE)
        if item is None:
            return None
        return self._rows[item.data(QtCore.Qt.UserRole)]

    def _checked_rows(self) -> list[DeviceRow]:
        checked = []
        for index in range(self.table.rowCount()):
            item = self.table.item(index, COL_DEVICE)
            if item is not None and item.checkState() == QtCore.Qt.Checked:
                checked.append(self._rows[item.data(QtCore.Qt.UserRole)])
        return checked

    def _set_all_checked(self, checked: bool) -> None:
        state = QtCore.Qt.Checked if checked else QtCore.Qt.Unchecked
        for index in range(self.table.rowCount()):
            item = self.table.item(index, COL_DEVICE)
            if item is not None:
                item.setCheckState(state)

    def _refresh_table(self) -> None:
        for index, device in enumerate(DEVICES):
            row = self._rows[device.key]
            node = row.node

            if row.phase:
                state, color = row.phase, TABLE_BUSY
            elif node is None:
                state, color = "Nao consultado", TABLE_MUTED
            elif node.online:
                state, color = f"Online ({node.age_text})", TABLE_OK
            elif node.registered:
                state, color = "Registrado, sem dados", TABLE_WARN
            else:
                state, color = "Offline", TABLE_ERROR
            self._set_cell(index, COL_STATE, state, color)

            self._set_cell(
                index,
                COL_ADDRESS,
                f"{row.address}  ({row.address_source})",
                tooltip=f"Destino do envio: http://{row.address}/update",
            )

            node_version = node.version if node is not None and node.version else "--"
            repo_version = firmware.repo_version(device) or "--"
            version_color = None
            version_tip = f"Declarada em {device.version_file}"
            if node_version != "--" and repo_version != "--":
                if node_version == repo_version:
                    version_color = TABLE_OK
                else:
                    version_color = TABLE_WARN
                    version_tip = (
                        f"O dispositivo roda {node_version} e o repositorio declara "
                        f"{repo_version}. Compile e envie para igualar.\n{version_tip}"
                    )
            self._set_cell(index, COL_NODE_VERSION, node_version, version_color, version_tip)
            self._set_cell(index, COL_REPO_VERSION, repo_version, version_color, version_tip)

            info = row.binary
            if info is None:
                image_text = "nenhuma imagem encontrada"
                image_color = TABLE_ERROR
                built_text = "--"
                image_tip = "Compile o firmware ou escolha um .bin pelo menu do botao direito."
            else:
                manual = " [manual]" if row.binary_override is not None else ""
                image_text = info.summary + manual
                image_color = None
                built_text = info.modified_text
                image_tip = str(info.path)
            self._set_cell(index, COL_IMAGE, image_text, image_color, tooltip=image_tip)
            self._set_cell(index, COL_BUILT, built_text)

    def _set_cell(
        self,
        row_index: int,
        column: int,
        text: str,
        color: Optional[str] = None,
        tooltip: str = "",
    ) -> None:
        item = self.table.item(row_index, column)
        if item is None:
            item = QtWidgets.QTableWidgetItem()
            self.table.setItem(row_index, column, item)
        item.setText(text)
        item.setForeground(QtGui.QBrush(QtGui.QColor(color)) if color else QtGui.QBrush())
        item.setToolTip(tooltip)

    # ------------------------------------------------------------------
    # Row menu
    # ------------------------------------------------------------------

    def _show_row_menu(self, position: QtCore.QPoint) -> None:
        row = self._selected_row()
        if row is None:
            return

        menu = QtWidgets.QMenu(self)
        menu.addAction("Escolher imagem .bin...", lambda: self._choose_binary(row))
        if row.binary_override is not None:
            menu.addAction("Usar a imagem compilada mais recente", lambda: self._clear_binary(row))
        menu.addSeparator()
        menu.addAction("Definir endereco manualmente...", lambda: self._set_manual_ip(row))
        if row.ip_override:
            menu.addAction("Voltar ao endereco descoberto", lambda: self._clear_manual_ip(row))
        menu.addSeparator()
        menu.addAction("Testar rota /update", lambda: self._test_route(row))
        menu.exec(self.table.viewport().mapToGlobal(position))

    def _choose_binary(self, row: DeviceRow) -> None:
        start = row.device.build_dir if row.device.build_dir.is_dir() else row.device.sketch_dir
        path, _ = QtWidgets.QFileDialog.getOpenFileName(
            self, f"Imagem para {row.device.name}", str(start), "Imagem de firmware (*.bin)"
        )
        if not path:
            return
        candidate = Path(path)
        if not firmware.is_application_image(candidate):
            QtWidgets.QMessageBox.warning(
                self,
                "Imagem invalida",
                f"{candidate.name} e uma imagem merged, bootloader ou de particoes.\n\n"
                "O firmware recusa esse arquivo durante o upload. Envie o .ino.bin "
                "da aplicacao.",
            )
            return
        row.binary_override = candidate
        self._log(f"[{row.device.name}] Imagem manual: {candidate}", "info")
        self._refresh_table()

    def _clear_binary(self, row: DeviceRow) -> None:
        row.binary_override = None
        self._log(f"[{row.device.name}] Voltando a imagem compilada mais recente.", "info")
        self._refresh_table()

    def _set_manual_ip(self, row: DeviceRow) -> None:
        text, ok = QtWidgets.QInputDialog.getText(
            self,
            f"Endereco de {row.device.name}",
            "IP do dispositivo:",
            text=row.ip_override or row.address,
        )
        if ok:
            row.ip_override = text.strip()
            self._refresh_table()

    def _clear_manual_ip(self, row: DeviceRow) -> None:
        row.ip_override = ""
        self._refresh_table()

    def _test_route(self, row: DeviceRow) -> None:
        address = row.address
        QtWidgets.QApplication.setOverrideCursor(QtCore.Qt.WaitCursor)
        try:
            reachable = hub.probe_update_route(address)
        finally:
            QtWidgets.QApplication.restoreOverrideCursor()
        if reachable:
            self._log(f"[{row.device.name}] http://{address}/update respondeu.", "ok")
        else:
            self._log(
                f"[{row.device.name}] http://{address}/update nao respondeu. "
                "Verifique a rede Wi-Fi do Hub ou do dispositivo.",
                "warn",
            )

    # ------------------------------------------------------------------
    # Discovery
    # ------------------------------------------------------------------

    def _start_discovery(self) -> None:
        if self._discovery is not None and self._discovery.isRunning():
            return
        hub_ip = self.hub_ip_edit.text().strip() or HUB_DEFAULT_IP
        self.refresh_button.setEnabled(False)
        self.hub_status_label.setText("Estado do Hub: consultando...")
        self.hub_status_label.setStyleSheet("color: #569cd6;")
        self._log(f"Consultando http://{hub_ip}/nodes ...", "muted")

        self._discovery = DiscoveryWorker(hub_ip, self)
        self._discovery.finished_ok.connect(self._on_discovery_ok)
        self._discovery.failed.connect(self._on_discovery_failed)
        self._discovery.finished.connect(lambda: self.refresh_button.setEnabled(True))
        self._discovery.start()

    def _on_discovery_ok(self, statuses: dict) -> None:
        for key, row in self._rows.items():
            row.node = statuses.get(key)
        online = sum(1 for status in statuses.values() if status.online)
        self.hub_status_label.setText(f"Estado do Hub: {len(statuses)} nos, {online} online")
        self.hub_status_label.setStyleSheet("color: #4ec9b0;")
        self._log(f"Hub respondeu: {len(statuses)} nos no diretorio, {online} online.", "ok")
        self._refresh_table()

    def _on_discovery_failed(self, message: str) -> None:
        for row in self._rows.values():
            row.node = None
        self.hub_status_label.setText("Estado do Hub: indisponivel")
        self.hub_status_label.setStyleSheet("color: #dcdcaa;")
        self._log(f"Hub indisponivel ({message}).", "warn")
        self._log(
            "Os enderecos voltam ao AP padrao de cada dispositivo. Conecte-se ao "
            "Wi-Fi do dispositivo para atualizar por ali.",
            "muted",
        )
        self._refresh_table()

    # ------------------------------------------------------------------
    # Queue
    # ------------------------------------------------------------------

    def _start_queue(self, do_compile: bool, do_upload: bool) -> None:
        if self._runner is not None and self._runner.isRunning():
            return

        rows = self._checked_rows()
        if not rows:
            QtWidgets.QMessageBox.information(
                self, "Nenhum dispositivo", "Marque ao menos um dispositivo na tabela."
            )
            return

        libraries = Path(self.libraries_edit.text().strip())
        if do_compile and not libraries.is_dir():
            QtWidgets.QMessageBox.warning(
                self,
                "Bibliotecas nao encontradas",
                f"O caminho de bibliotecas nao existe:\n{libraries}",
            )
            return

        if do_upload and not self._confirm_upload(rows):
            return

        jobs = [
            Job(
                device=row.device,
                ip=row.address,
                do_compile=do_compile,
                do_upload=do_upload,
                binary_path=row.binary_override,
            )
            for row in rows
        ]

        for row in rows:
            row.phase = "Na fila"
        self._refresh_table()

        self._runner = JobRunner(jobs, libraries, self)
        self._runner.log.connect(self._log)
        self._runner.job_started.connect(self._on_job_started)
        self._runner.job_finished.connect(self._on_job_finished)
        self._runner.upload_progress.connect(self._on_upload_progress)
        self._runner.all_finished.connect(self._on_queue_finished)

        self._set_busy(True)
        names = ", ".join(row.device.name for row in rows)
        if do_compile and do_upload:
            action = "Compilar e enviar"
        elif do_compile:
            action = "Compilar"
        else:
            action = "Enviar"
        self._log(f"=== {action}: {names} ===", "info")
        self._runner.start()

    def _confirm_upload(self, rows: list[DeviceRow]) -> bool:
        lines = []
        for row in rows:
            info = row.binary
            image = info.summary if info is not None else "sera resolvida apos compilar"
            lines.append(f"  - {row.device.name}  ->  {row.address}   [{image}]")
        detail = "\n".join(lines)
        answer = QtWidgets.QMessageBox.question(
            self,
            "Confirmar gravacao",
            "As imagens serao gravadas nos dispositivos abaixo, que reiniciam ao "
            f"final:\n\n{detail}\n\nProsseguir?",
            QtWidgets.QMessageBox.Yes | QtWidgets.QMessageBox.No,
            QtWidgets.QMessageBox.No,
        )
        return answer == QtWidgets.QMessageBox.Yes

    def _cancel_queue(self) -> None:
        if self._runner is not None and self._runner.isRunning():
            self._runner.cancel()
            self.cancel_button.setEnabled(False)
            self._log("Cancelamento solicitado; aguardando a etapa atual parar.", "warn")

    def _on_job_started(self, key: str, phase: str) -> None:
        row = self._rows[key]
        row.phase = phase
        self.statusBar().showMessage(f"{phase} {row.device.name}...")
        if phase == "Enviando":
            self.progress.setRange(0, 100)
            self.progress.setValue(0)
            self.progress.setFormat(f"{row.device.name}: %p%")
        else:
            # Indeterminate: arduino-cli gives no percentage to report.
            self.progress.setRange(0, 0)
            self.progress.setFormat(f"{row.device.name}: compilando")
        self._refresh_table()

    def _on_upload_progress(self, key: str, sent: int, total: int) -> None:
        percent = int(sent * 100 / total) if total else 0
        self.progress.setValue(percent)
        self.progress.setFormat(
            f"{self._rows[key].device.name}: {sent / 1024:.0f} / {total / 1024:.0f} KB (%p%)"
        )

    def _on_job_finished(self, key: str, ok: bool, detail: str) -> None:
        self._rows[key].phase = ""
        self._refresh_table()

    def _on_queue_finished(self, succeeded: int, failed: int) -> None:
        self.progress.setRange(0, 100)
        self.progress.setValue(0)
        self.progress.setFormat("Ocioso")
        for row in self._rows.values():
            row.phase = ""
        self._set_busy(False)

        level = "ok" if failed == 0 else "warn"
        self._log(f"=== Fila concluida: {succeeded} com sucesso, {failed} com falha ===", level)
        self.statusBar().showMessage(f"Concluido: {succeeded} ok, {failed} falha(s)")
        self._refresh_table()

        if succeeded:
            self._log(
                "Aguarde o reinicio e atualize o estado para confirmar a versao "
                "reportada por cada no.",
                "muted",
            )

    def _set_busy(self, busy: bool) -> None:
        for widget in (
            self.compile_button,
            self.upload_button,
            self.both_button,
            self.refresh_button,
        ):
            widget.setEnabled(not busy)
        self.cancel_button.setEnabled(busy)
        self.table.setEnabled(not busy)

    # ------------------------------------------------------------------
    # Log
    # ------------------------------------------------------------------

    def _log(self, message: str, level: str = "info") -> None:
        stamp = QtCore.QTime.currentTime().toString("HH:mm:ss")
        color = LOG_COLORS.get(level, LOG_COLORS["info"])
        escaped = message.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
        self.log_view.appendHtml(
            f'<span style="color:#606060;">{stamp}</span> '
            f'<span style="color:{color};">{escaped}</span>'
        )
        scrollbar = self.log_view.verticalScrollBar()
        scrollbar.setValue(scrollbar.maximum())

    def _save_log(self) -> None:
        path, _ = QtWidgets.QFileDialog.getSaveFileName(
            self, "Salvar registro", "atualizacao.log", "Registro (*.log *.txt)"
        )
        if not path:
            return
        try:
            Path(path).write_text(self.log_view.toPlainText(), encoding="utf-8")
        except OSError as exc:
            QtWidgets.QMessageBox.warning(self, "Falha ao salvar", str(exc))
            return
        self._log(f"Registro salvo em {path}", "ok")

    # ------------------------------------------------------------------

    def _choose_libraries(self) -> None:
        path = QtWidgets.QFileDialog.getExistingDirectory(
            self, "Diretorio de bibliotecas Arduino", self.libraries_edit.text()
        )
        if path:
            self.libraries_edit.setText(path)

    def closeEvent(self, event: QtGui.QCloseEvent) -> None:
        if self._runner is not None and self._runner.isRunning():
            answer = QtWidgets.QMessageBox.question(
                self,
                "Operacao em andamento",
                "Uma gravacao esta em andamento. Fechar agora interrompe o envio e "
                "pode deixar o dispositivo com a imagem incompleta.\n\n"
                "Fechar mesmo assim?",
                QtWidgets.QMessageBox.Yes | QtWidgets.QMessageBox.No,
                QtWidgets.QMessageBox.No,
            )
            if answer != QtWidgets.QMessageBox.Yes:
                event.ignore()
                return
            self._runner.cancel()
            self._runner.wait(5000)
        event.accept()
