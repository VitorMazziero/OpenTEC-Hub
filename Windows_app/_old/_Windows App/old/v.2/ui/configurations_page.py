#!/usr/bin/env python
# -*- coding: utf-8 -*-
import statistics
import serial
from serial.tools import list_ports
import qdarkstyle
import time
from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QGridLayout, QGroupBox, QLabel, QLineEdit,
    QPushButton, QFormLayout, QComboBox, QPlainTextEdit, QFileDialog,
    QMessageBox, QHBoxLayout, QDialog, QProgressBar, QApplication, QTableWidget, 
    QTableWidgetItem, QListWidget, QListWidgetItem, QCheckBox
)
from PyQt5.QtCore import Qt, QThread, pyqtSignal, QObject, QEventLoop, QStandardPaths, QTimer
import os

from . import UI_CONFIG  # Global UI configuration
from config.preferences import load_preferences, save_preferences, default_preferences
import asyncio
from bleak import BleakScanner

class BluetoothScannerWorker(QThread):
    # Emits a tuple (device, advertisement_data) for each broadcast received.
    device_found = pyqtSignal(object)  

    def __init__(self, timeout=10.0, parent=None):
        super().__init__(parent)
        self.timeout = timeout
        

    def run(self):

        async def scan():
            scanner = BleakScanner()

            def detection_callback(device, advertisement_data):
                # Emit each device (without filtering duplicates)
                self.device_found.emit((device, advertisement_data))
                
            scanner.register_detection_callback(detection_callback)
            await scanner.start()
            await asyncio.sleep(self.timeout)
            await scanner.stop()

        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        loop.run_until_complete(scan())
        loop.close()

class DialogBluetoothScan(QDialog):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Seleção de Dispositivos Bluetooth")
        self.resize(400, 300)
        layout = QVBoxLayout(self)
        self.label = QLabel("Pressione 'Escanear' para procurar dispositivos BLE.")
        layout.addWidget(self.label)
        self.scan_button = QPushButton("Escanear")
        layout.addWidget(self.scan_button)
        self.device_list = QListWidget()
        layout.addWidget(self.device_list)
        self.select_button = QPushButton("Selecionar Dispositivo")
        layout.addWidget(self.select_button)
        self.selected_device = None

        self.scan_button.clicked.connect(self.start_scan)
        self.select_button.clicked.connect(self.select_device)

        self.scanner_worker = None
        # Dictionary mapping address -> QListWidgetItem
        self.device_items = {}

    def start_scan(self):
        self.device_list.clear()
        self.device_items.clear()  # Clear records for new scan
        self.label.setText("Escaneando dispositivos Bluetooth...")
        self.scanner_worker = BluetoothScannerWorker(timeout=10.0)
        self.scanner_worker.device_found.connect(self.add_or_update_device)
        self.scanner_worker.start()

    def add_or_update_device(self, device_info):
        device, ad_data = device_info
        name = device.name or (ad_data.local_name if ad_data and hasattr(ad_data, "local_name") and ad_data.local_name else "Sem nome")
        rssi = ad_data.rssi if ad_data and hasattr(ad_data, "rssi") else device.rssi
        text = f"{name} | {device.address} | RSSI: {rssi}"
        if device.address in self.device_items:
            self.device_items[device.address].setText(text)
        else:
            item = QListWidgetItem(text)
            self.device_list.addItem(item)
            self.device_items[device.address] = item
        self.label.setText("Dispositivos encontrados (atualizando em tempo real)...")

    def select_device(self):
        current_item = self.device_list.currentItem()
        if current_item:
            self.selected_device = current_item.text()
            self.accept()
        else:
            QMessageBox.warning(self, "Seleção Inválida", "Por favor, selecione um dispositivo.")

# --------------------- Main Configurations Page ---------------------
class ConfigurationsPage(QWidget):
    def __init__(self, comm_handler, parent=None):
        super().__init__(parent)
        self.comm_handler = comm_handler

        # Define (or default) the connection parameters as held in the UI.
        self.usb_baud_rate = 115200
        self.usb_data_bits = 8       
        self.usb_stop_bits = 1
        self.usb_parity = serial.PARITY_NONE
        # Wi-Fi and Bluetooth parameters will be taken from the line edits or scan dialog.

        # Create and configure the keep‐alive timer for Bluetooth (3-second interval)
        self.bluetooth_keep_alive_timer = QTimer(self)
        self.bluetooth_keep_alive_timer.setInterval(3000)
        self.bluetooth_keep_alive_timer.timeout.connect(self.send_keep_alive)
        self.comm_handler.set_connection_failed_callback(self.on_connection_failed)

        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(10, 10, 10, 0)
        main_layout.setSpacing(5)
        grid = QGridLayout()
        grid.setSpacing(5)

        # --- Left column: Data Delay, Calibration and Preferences ---
        left_layout = QVBoxLayout()
        # (Wi‑Fi moved out)
        # --- Preferences (moved here) ---
        preferences_group = QGroupBox("Preferências")
        pref_layout = QVBoxLayout()
        self.reset_button       = QPushButton("Resetar Preferências")
        self.reset_module_vars_button = QPushButton("Resetar variáveis do módulo")
        self.restart_comm_button = QPushButton("Reiniciar todas as comunicações")
        # NEW global disconnect button
        self.global_disconnect_button = QPushButton("Desconectar todas as comunicações")
        pref_layout.addWidget(self.reset_button)
        pref_layout.addWidget(self.reset_module_vars_button)
        pref_layout.addWidget(self.restart_comm_button)
        pref_layout.addWidget(self.global_disconnect_button)
        self.theme_combo = QComboBox()
        self.theme_combo.addItems(["Dark", "Light"])
        pref_layout.addWidget(self.theme_combo)
        preferences_group.setLayout(pref_layout)
        left_layout.addWidget(preferences_group)
        self.reset_button.clicked.connect(self.on_reset_preferences)

        data_delay_group = QGroupBox("Leitura de Dados")
        data_delay_layout = QFormLayout()
        self.data_delay_edit = QLineEdit("1000")
        self.dataDelay = int(self.data_delay_edit.text())
        self.data_delay_button = QPushButton("Definir Atraso")
        delay_row_widget = QWidget()
        delay_row_layout = QHBoxLayout(delay_row_widget)
        delay_row_layout.setContentsMargins(0, 0, 0, 0)
        delay_row_layout.addWidget(self.data_delay_edit)
        # The alarm sound checkbox remains.
        self.sound_alarm_checkbox = QCheckBox("Desativar alarme sonoro")
        self.sound_alarm_checkbox.setChecked(False)
        self.sound_alarm_checkbox.stateChanged.connect(lambda state: self.comm_handler._mute_changed(state))
        delay_row_layout.addWidget(self.sound_alarm_checkbox)
        data_delay_layout.addRow("Delay (ms):", delay_row_widget)
        data_delay_layout.addRow(self.data_delay_button)
        self.zerar_tempo_button = QPushButton("Zerar tempo")
        self.zerar_tempo_button.clicked.connect(self.zerar_tempo)
        data_delay_layout.addRow(self.zerar_tempo_button)
        data_delay_group.setLayout(data_delay_layout)
        left_layout.addWidget(data_delay_group)

        calibration_group = QGroupBox("Parâmetros de calibração")
        calibration_layout = QFormLayout()
        self.oxy_cal_a = QLineEdit(str(getattr(self.comm_handler, "oxy_cal_a", "0.0305473419314")))
        self.oxy_cal_b = QLineEdit(str(getattr(self.comm_handler, "oxy_cal_b", "-25.09136520919")))
        calibration_layout.addRow("O₂ slope:", self.oxy_cal_a)
        calibration_layout.addRow("O₂ intercept:", self.oxy_cal_b)
        self.ph_cal_slope = QLineEdit(str(getattr(self.comm_handler, "pH_slope", "1.0")))
        self.ph_cal_intercept = QLineEdit(str(getattr(self.comm_handler, "pH_intercept", "0.0")))
        calibration_layout.addRow("pH slope:", self.ph_cal_slope)
        calibration_layout.addRow("pH intercept:", self.ph_cal_intercept)
        ph_button_layout = QHBoxLayout()
        self.ph_calibrate_button = QPushButton("Calibrar pH (dois pontos)")
        self.ph_calibrate_button.clicked.connect(self.open_ph_calibration_dialog)
        self.ph_calibrate_1_point_button = QPushButton("Calibrar pH (um ponto)")
        self.ph_calibrate_1_point_button.clicked.connect(self.open_ph_1_point_calibration_dialog)
        ph_button_layout.addWidget(self.ph_calibrate_button)
        ph_button_layout.addWidget(self.ph_calibrate_1_point_button)
        ph_button_widget = QWidget()
        ph_button_widget.setLayout(ph_button_layout)
        calibration_layout.addRow(ph_button_widget)
        calibration_group.setLayout(calibration_layout)
        left_layout.addWidget(calibration_group)
        self.oxy_cal_a.editingFinished.connect(self.update_oxy_calibration)
        self.oxy_cal_b.editingFinished.connect(self.update_oxy_calibration)
        self.ph_cal_slope.editingFinished.connect(self.update_ph_calibration)
        self.ph_cal_intercept.editingFinished.connect(self.update_ph_calibration)
        left_widget = QWidget()
        left_widget.setLayout(left_layout)
        grid.addWidget(left_widget, 0, 0)

        # Middle column: USB, Bluetooth configurations and Preferences
        middle_layout = QVBoxLayout()
        usb_group = QGroupBox("Configurações USB")
        usb_layout = QFormLayout()
        self.com_port_combo = QComboBox()
        self.refresh_com_ports()
        self.usb_connect_button = QPushButton("Conectar USB")
        self.usb_stop_button = QPushButton("Parar Conexão USB")
        usb_layout.addRow("Porta COM:", self.com_port_combo)
        usb_layout.addRow(self.usb_connect_button)
        usb_layout.addRow(self.usb_stop_button)
        self.usb_stop_button.setEnabled(False)
        self.refresh_ports_button = QPushButton("Atualizar Portas")
        self.refresh_ports_button.clicked.connect(self.refresh_com_ports)
        usb_layout.addRow(self.refresh_ports_button)
        usb_group.setLayout(usb_layout)

        bluetooth_group = QGroupBox("Configurações Bluetooth")
        bluetooth_layout = QFormLayout()
        self.bluetooth_scan_button = QPushButton("Escanear Bluetooth")
        self.bluetooth_connect_button = QPushButton("Conectar Bluetooth")
        self.bluetooth_stop_button = QPushButton("Parar Conexão Bluetooth")
        bluetooth_layout.addRow(self.bluetooth_scan_button)
        bluetooth_layout.addRow(self.bluetooth_connect_button)
        bluetooth_layout.addRow(self.bluetooth_stop_button)
        self.bluetooth_stop_button.setEnabled(False)
        bluetooth_group.setLayout(bluetooth_layout)

        wifi_group = QGroupBox("Configurações Wi‑Fi")
        wifi_layout = QFormLayout()
        self.ip_edit = QLineEdit("192.168.4.1")
        self.wifi_connect_button = QPushButton("Conectar Wi‑Fi")
        self.wifi_stop_button = QPushButton("Parar Conexão")
        wifi_layout.addRow("Endereço IP:", self.ip_edit)
        wifi_layout.addRow(self.wifi_connect_button)
        wifi_layout.addRow(self.wifi_stop_button)
        self.wifi_stop_button.setEnabled(False)
        wifi_group.setLayout(wifi_layout)
        
        middle_layout.addWidget(usb_group)
        middle_layout.addWidget(wifi_group)
        middle_layout.addWidget(bluetooth_group)
        self.bluetooth_scan_button.clicked.connect(self.open_bluetooth_scan_dialog)
        self.bluetooth_connect_button.clicked.connect(self.connect_bluetooth)
        self.bluetooth_stop_button.clicked.connect(self.stop_connection)
        self.usb_connect_button.clicked.connect(self.connect_usb)
        self.usb_stop_button.clicked.connect(self.stop_connection)
        middle_widget = QWidget()
        middle_widget.setLayout(middle_layout)
        grid.addWidget(middle_widget, 0, 1)

        # Right column: Log
        log_group = QGroupBox("Log")
        log_layout = QVBoxLayout()
        self.log_edit = QPlainTextEdit()
        self.log_edit.setReadOnly(True)
        log_layout.addWidget(self.log_edit)
        log_group.setLayout(log_layout)
        log_group.setMaximumHeight(450)
        grid.addWidget(log_group, 0, 2, 2, 1)
        grid.setColumnStretch(0, 1)
        grid.setColumnStretch(1, 1)
        grid.setColumnStretch(2, 2)
        grid.setRowStretch(0, 1)
        grid.setRowStretch(1, 0)
        main_layout.addLayout(grid)

        # Connect button signals
        self.wifi_connect_button.clicked.connect(self.connect_wifi)
        self.wifi_stop_button.clicked.connect(self.stop_connection)
        self.data_delay_button.clicked.connect(self.set_data_delay)
        self.theme_combo.currentTextChanged.connect(self.change_theme)
        self.setup_field_signals()

        self.global_disconnect_button.clicked.connect(self.disconnect_all)

    def on_connection_failed(self, medium: str):
        if medium == "USB":
            self.usb_connect_button.setEnabled(True)
        elif medium == "WiFi":
            self.wifi_connect_button.setEnabled(True)
        elif medium == "Bluetooth":
            self.bluetooth_connect_button.setEnabled(True)

    def disconnect_all(self):
        """
        Stops keep‑alives, calls handler to disable and disconnect all, logs action,
        and re‑enables connect buttons.
        """
        self.bluetooth_keep_alive_timer.stop()
        self.comm_handler.disable_all_connections()
        self.log_edit.appendPlainText(
            "Todas as comunicações foram desconectadas e desativadas."
        )
        # re‑enable all connect buttons
        self.wifi_connect_button.setEnabled(True)
        self.usb_connect_button.setEnabled(True)
        self.bluetooth_connect_button.setEnabled(True)

    def update_comm_handler_settings(self):
        """
        Updates the comm_handler connection variables from the UI fields.
        This is called before initiating a connection.
        """
        self.comm_handler.usb_port = self.com_port_combo.currentText()
        # If you add editable fields for baud rate, data bits, etc., update here.
        self.comm_handler.usb_baud_rate = self.usb_baud_rate
        self.comm_handler.usb_data_bits = self.usb_data_bits
        self.comm_handler.usb_stop_bits = self.usb_stop_bits
        self.comm_handler.usb_parity = self.usb_parity
        self.comm_handler.ip = self.ip_edit.text()
        # For Bluetooth, the selected device is updated via the scan dialog.

    def open_ph_calibration_dialog(self):
        dialog = PHCalibrationDialog(self.comm_handler, self)
        dialog.exec_()

    def open_ph_1_point_calibration_dialog(self):
        dialog = PHOnePointCalibrationDialog(self.comm_handler, self)
        dialog.exec_()

    def setup_field_signals(self):
        for le in self.findChildren(QLineEdit):
            le.editingFinished.connect(self.on_field_changed)
        for cb in self.findChildren(QComboBox):
            cb.currentTextChanged.connect(self.on_field_changed)

    def on_field_changed(self):
        if self.window() is not None and hasattr(self.window(), "on_editing_finished"):
            self.window().on_editing_finished()

    def on_reset_preferences(self):
        if self.window():
            self.window().reset_preferences()

    def reset_module_variables(self):
        reply = QMessageBox.question(
            self,
            "Resetar Variáveis do Módulo",
            "Você tem certeza que deseja resetar todas as variáveis do módulo para os valores padrões?",
            QMessageBox.Yes | QMessageBox.No
        )
        if reply == QMessageBox.Yes:
            self.comm_handler.send_command({"resetVariables": 1})
            self.log_edit.appendPlainText("Comando de reset das variáveis do módulo enviado.")

    def restart_communications(self):
        reply = QMessageBox.question(
            self,
            "Reiniciar Comunicações",
            "Você deseja reiniciar a comunicação com o módulo? A comunicação será retomada automaticamente se possível.",
            QMessageBox.Yes | QMessageBox.No
        )
        if reply == QMessageBox.Yes:
            self.comm_handler.send_command({"restart": 1})
            self.log_edit.appendPlainText("Comando de reinício das comunicações enviado.")

    def zerar_tempo(self):
        reply = QMessageBox.question(
            self, "Zerar Tempo",
            "Deseja resetar o tempo do programa?",
            QMessageBox.Yes | QMessageBox.No
        )
        if reply == QMessageBox.Yes:
            if hasattr(self.comm_handler, "_raw_time_min"):
                self.comm_handler.timeZeroOffset = self.comm_handler._raw_time_min
            else:
                self.comm_handler.timeZeroOffset = 0
            if self.comm_handler.logger:
                self.comm_handler.logger("Tempo zerado.")

    def prompt_for_log_file(self):
        main_win = self.window()
        if not hasattr(main_win, "log_file") or main_win.log_file is None:
            initial_dir = QStandardPaths.writableLocation(QStandardPaths.DesktopLocation)
            fileName, _ = QFileDialog.getSaveFileName(
                self,
                "Selecione o Arquivo de Log",
                initial_dir,
                "Text Files (*.txt);;All Files (*)"
            )
            if fileName:
                # Check if the file already exists to decide whether to write the header
                file_exists = os.path.exists(fileName)
                
                # Open the file in append mode ("a")
                main_win.log_file = open(fileName, "a")
                
                if not file_exists:
                    # If the file is new, write the header
                    header = ("Time (min)\tTemperature (°C)\tMotor (rpm)\tpH\tAntifoam\t"
                            "Pressure\tOxygen\tFlowmeter\tDistance\tOUR\tConexão\n")
                    main_win.log_file.write(header)
                    main_win.log_file.flush()

    def connect_wifi(self):
        self.prompt_for_log_file()
        self.update_comm_handler_settings()  # Push UI values into comm_handler
        ip = self.ip_edit.text()
        self.comm_handler.connect_wifi(ip)
        self.wifi_connect_button.setEnabled(False)

    def connect_usb(self):
        self.prompt_for_log_file()
        self.update_comm_handler_settings()  # Update comm_handler settings from UI
        port = self.com_port_combo.currentText()
        self.comm_handler.connect_usb(port, int(self.usb_baud_rate), int(self.usb_data_bits),
                                       int(self.usb_stop_bits), self.usb_parity)
        self.usb_connect_button.setEnabled(False)

    def connect_bluetooth(self):
        self.prompt_for_log_file()
        self.update_comm_handler_settings()  
        self.comm_handler.connect_bluetooth()
        if self.comm_handler.is_bluetooth_connected():
            self.bluetooth_keep_alive_timer.start()
        self.bluetooth_connect_button.setEnabled(False)

    def send_keep_alive(self):
        """
        Sends a keep-alive command to the ESP32 via Bluetooth.
        Executed periodically by the timer.
        """
        if (self.comm_handler.bluetooth_ativo and 
            hasattr(self.comm_handler, 'is_bluetooth_connected') and 
            self.comm_handler.is_bluetooth_connected()):
            self.comm_handler.send_command({"keepAlive": True})
            self.log_edit.appendPlainText("Keep alive sent to ESP32.")

    def set_data_delay(self):
        try:
            self.delay = int(self.data_delay_edit.text())
            self.comm_handler.send_command({"dataDelay": self.delay})
            if self.parent() and hasattr(self.parent(), "dataDelay"):
                self.parent().dataDelay = self.delay
            self.log_edit.appendPlainText(f"Atraso de leitura definido para {self.delay} ms")
        except:
            self.log_edit.appendPlainText("Valor inválido para atraso de leitura")

    def stop_connection(self):
        # Stop timers and disconnect communication.
        self.bluetooth_keep_alive_timer.stop()
        self.comm_handler.disconnect()
        self.log_edit.appendPlainText("Connection stopped and timers stopped.")
        sender = self.sender()
        if sender == self.wifi_stop_button:
            self.wifi_connect_button.setEnabled(True)
            self.wifi_stop_button.setEnabled(False)
        elif sender == self.usb_stop_button:
            self.usb_connect_button.setEnabled(True)
            self.usb_stop_button.setEnabled(False)
        elif sender == self.bluetooth_stop_button:
            self.bluetooth_connect_button.setEnabled(True)
            self.bluetooth_stop_button.setEnabled(False)

    def change_theme(self, theme):
        if theme == "Dark":
            QApplication.instance().setStyleSheet(qdarkstyle.load_stylesheet_pyqt5())
        else:
            QApplication.instance().setStyleSheet("")

    def update_oxy_calibration(self):
        try:
            a = float(self.oxy_cal_a.text())
            b = float(self.oxy_cal_b.text())
            self.comm_handler.set_oxygen_calibration(a, b)
        except Exception as e:
            if self.comm_handler.logger:
                self.comm_handler.logger("Erro ao atualizar calibração de oxigênio: " + str(e))
    
    def update_ph_calibration(self):
        try:
            slope = float(self.ph_cal_slope.text())
            intercept = float(self.ph_cal_intercept.text())
            self.comm_handler.set_pH_calibration(slope, intercept)
            if self.comm_handler.logger:
                self.comm_handler.logger(f"Atualizando calibração de pH: slope={slope}, intercept={intercept}")
        except ValueError:
            QMessageBox.warning(self, "Erro", "Valores inválidos para calibração de pH.")
        except Exception as e:
            if self.comm_handler.logger:
                self.comm_handler.logger(f"Erro ao atualizar calibração de pH: {e}")

    def refresh_com_ports(self):
        self.com_port_combo.clear()
        from serial.tools import list_ports
        ports = list_ports.comports()
        for port in ports:
            self.com_port_combo.addItem(port.device)
        if self.com_port_combo.count() == 0:
            self.com_port_combo.addItem("Nenhuma porta disponível")

    def open_bluetooth_scan_dialog(self):
        dialog = DialogBluetoothScan(self)
        if dialog.exec_():
            selected_device = dialog.selected_device
            self.log_edit.appendPlainText(f"Dispositivo Bluetooth selecionado: {selected_device}")
            self.comm_handler.bluetooth_device = selected_device

# --------------------- Two-Point pH Calibration Worker e Dialogs ---------------------
# As classes PHCalibrationWorker, PHOnePointCalibrationWorker, PHCalibrationDialog e
# PHOnePointCalibrationDialog seguem a lógica original, com a alteração de salvar a calibração
# de pH na seção "Configurations" ao invés de "pHCalibration".

class PHCalibrationWorker(QObject):
    finished = pyqtSignal(float, float, float, float, list, list)
    progress = pyqtSignal(int)
    warningRequest = pyqtSignal(float)
    statusUpdate = pyqtSignal(str)

    def __init__(self, comm_handler, ref1_target, ref2_target, delay=1000):
        super().__init__()
        self.comm_handler = comm_handler
        self.ref1_target = ref1_target
        self.ref2_target = ref2_target
        self.delay = delay
        self.cancelled = False
        self._ack_loop = None
        self.comm_handler.send_command({"pHSetpoint": 7})

    def run(self):
        if self.cancelled:
            return
        self.warningRequest.emit(self.ref1_target)
        self._wait_for_ack()
        if self.cancelled:
            return
        ref1_average, ref1_values = self._acquire_calibration_point(progress_start=0, progress_end=45)
        if self.cancelled or ref1_average is None:
            return
        self.warningRequest.emit(self.ref2_target)
        self._wait_for_ack()
        if self.cancelled:
            return
        ref2_average, ref2_values = self._acquire_calibration_point(progress_start=50, progress_end=95)
        if self.cancelled or ref2_average is None:
            return
        if abs(ref1_average - ref2_average) > 1e-6:
            slope = (self.ref1_target - self.ref2_target) / (ref1_average - ref2_average)
        else:
            slope = 1.0
        intercept = self.ref1_target - slope * ref1_average
        self.progress.emit(100)
        self.finished.emit(ref1_average, ref2_average, slope, intercept, ref1_values, ref2_values)

    def _wait_for_ack(self):
        self._ack_loop = QEventLoop()
        self._ack_loop.exec_()
        self._ack_loop = None

    def ack_received(self):
        if self._ack_loop is not None:
            self._ack_loop.quit()

    def _acquire_calibration_point(self, progress_start, progress_end):
        delay_sec = self.delay / 1000.0
        buffer = []
        window_size = 20
        threshold = 5
        while True:
            if self.cancelled:
                return None, []
            reading = self.comm_handler.phRead
            buffer.append(reading)
            if len(buffer) > window_size:
                buffer.pop(0)
            if len(buffer) == window_size:
                std_dev = statistics.stdev(buffer)
                self.statusUpdate.emit(f"Esperando estabilizar (std<=5): {std_dev:.2f}")
                if std_dev < threshold:
                    break
            else:
                self.statusUpdate.emit("Obtendo valores iniciais...")
            time.sleep(delay_sec)
        self.statusUpdate.emit("")
        values = []
        steps = 20
        for i in range(steps):
            if self.cancelled:
                return None, []
            reading = self.comm_handler.phRead
            values.append(reading)
            self.statusUpdate.emit(f"pH read: {reading:.3f}")
            progress = progress_start + int((i + 1) / steps * (progress_end - progress_start))
            self.progress.emit(progress)
            time.sleep(delay_sec)
        average = sum(values) / len(values)
        self.statusUpdate.emit("")
        return average, values

class PHOnePointCalibrationWorker(QObject):
    finished = pyqtSignal(float, float, float, list)
    progress = pyqtSignal(int)
    warningRequest = pyqtSignal(float)
    statusUpdate = pyqtSignal(str)
    
    def __init__(self, comm_handler, target, delay=1000):
        super().__init__()
        self.comm_handler = comm_handler
        self.target = target
        self.delay = delay
        self.cancelled = False
        self._ack_loop = None
        self.comm_handler.send_command({"pHSetpoint": target})
    
    def run(self):
        if self.cancelled:
            return
        self.warningRequest.emit(self.target)
        self._wait_for_ack()
        if self.cancelled:
            return
        average, values = self._acquire_calibration_point(progress_start=0, progress_end=100)
        if self.cancelled or average is None:
            return
        try:
            slope = float(self.comm_handler.pH_slope)
        except:
            slope = 1.0
        intercept = self.target - slope * average
        self.progress.emit(100)
        self.finished.emit(average, slope, intercept, values)
    
    def _wait_for_ack(self):
        self._ack_loop = QEventLoop()
        self._ack_loop.exec_()
        self._ack_loop = None
    
    def ack_received(self):
        if self._ack_loop is not None:
            self._ack_loop.quit()
    
    def _acquire_calibration_point(self, progress_start, progress_end):
        delay_sec = self.delay / 1000.0
        buffer = []
        window_size = 20
        threshold = 5
        while True:
            if self.cancelled:
                return None, []
            reading = self.comm_handler.phRead
            buffer.append(reading)
            if len(buffer) > window_size:
                buffer.pop(0)
            if len(buffer) == window_size:
                std_dev = statistics.stdev(buffer)
                self.statusUpdate.emit(f"Esperando estabilizar (std<=5): {std_dev:.2f}")
                if std_dev < threshold:
                    break
            else:
                self.statusUpdate.emit("Obtendo valores iniciais...")
            time.sleep(delay_sec)
        self.statusUpdate.emit("")
        values = []
        steps = 20
        for i in range(steps):
            if self.cancelled:
                return None, []
            reading = self.comm_handler.phRead
            values.append(reading)
            self.statusUpdate.emit(f"pH read: {reading:.3f}")
            progress = progress_start + int((i + 1) / steps * (progress_end - progress_start))
            self.progress.emit(progress)
            time.sleep(delay_sec)
        average = sum(values) / len(values)
        self.statusUpdate.emit("")
        return average, values

class PHCalibrationDialog(QDialog):
    ackClicked = pyqtSignal()

    def __init__(self, comm_handler, parent=None):
        super().__init__(parent)
        self.comm_handler = comm_handler
        self.setWindowTitle("Calibração do pH")
        self.setModal(True)

        grid = QGridLayout()
        self.ref1_label = QLabel("Referencia 1:")
        self.ref1_entry = QLineEdit("7")
        self.ref2_label = QLabel("Referencia 2:")
        self.ref2_entry = QLineEdit("4")
        grid.addWidget(self.ref1_label, 0, 0)
        grid.addWidget(self.ref1_entry, 0, 1)
        grid.addWidget(self.ref2_label, 1, 0)
        grid.addWidget(self.ref2_entry, 1, 1)

        self.calibrate_button = QPushButton("Calibrar")
        self.cancel_button = QPushButton("Cancelar")
        button_layout = QVBoxLayout()
        button_layout.addWidget(self.calibrate_button)
        button_layout.addWidget(self.cancel_button)
        grid.addLayout(button_layout, 0, 2, 2, 1)

        self.progress_bar = QProgressBar()
        self.progress_bar.setAlignment(Qt.AlignCenter)
        self.status_label = QLabel("")
        self.status_label.setAlignment(Qt.AlignCenter)

        main_layout = QVBoxLayout()
        main_layout.addLayout(grid)
        main_layout.addWidget(self.progress_bar)
        main_layout.addWidget(self.status_label)
        self.setLayout(main_layout)

        self.calibrate_button.clicked.connect(self.start_calibration)
        self.cancel_button.clicked.connect(self.cancel_calibration)
        self.setAttribute(Qt.WA_DeleteOnClose)

        self.thread = None
        self.worker = None

    def closeEvent(self, event):
        self.cancel_calibration()
        event.accept()

    def cancel_calibration(self):
        if self.thread and self.thread.isRunning():
            if self.worker:
                self.worker.cancelled = True
                self.ackClicked.emit()
            self.thread.quit()
            self.thread.wait()
        self.reject()

    def start_calibration(self):
        try:
            ref1 = float(self.ref1_entry.text())
            ref2 = float(self.ref2_entry.text())
        except ValueError:
            QMessageBox.warning(self, "Erro", "Valores de referência inválidos.")
            return

        self.calibrate_button.setEnabled(False)
        self.cancel_button.setEnabled(True)
        self.progress_bar.setValue(0)
        self.status_label.setText("")

        self.thread = QThread()
        self.worker = PHCalibrationWorker(self.comm_handler, ref1, ref2)
        self.worker.moveToThread(self.thread)

        self.ackClicked.connect(self.worker.ack_received)
        self.thread.started.connect(self.worker.run)
        self.worker.progress.connect(self.progress_bar.setValue)
        self.worker.warningRequest.connect(self.show_warning_dialog)
        self.worker.statusUpdate.connect(self.update_status_label)
        self.worker.finished.connect(self.on_calibration_finished)
        self.worker.finished.connect(self.thread.quit)
        self.worker.finished.connect(self.worker.deleteLater)
        self.thread.finished.connect(self.thread.deleteLater)

        self.thread.start()

    def update_status_label(self, text):
        self.status_label.setText(text)

    def show_warning_dialog(self, ref_value):
        msg = f"Começar a calibração com pH {ref_value}: Coloque o sensor em solução"
        QMessageBox.information(self, "Atenção", msg, QMessageBox.Ok)
        self.ackClicked.emit()

    def on_calibration_finished(self, ref1_measured, ref2_measured, slope, intercept, ref1_values, ref2_values):
        if ref1_measured is None or ref2_measured is None:
            return
        resultDialog = QDialog(self)
        resultDialog.setWindowTitle("Calibração Concluída")
        layout = QVBoxLayout(resultDialog)

        eq_label = QLabel(f"Curva: pH = {slope:.4f} * valor lido + {intercept:.4f}")
        layout.addWidget(eq_label)

        table = QTableWidget(2, 7)
        table.setHorizontalHeaderLabels(["Referência", "Valor 1", "Valor 2", "Valor 3", "Valor 4", "Valor 5", "Média"])
        table.setItem(0, 0, QTableWidgetItem("Referencia 1"))
        for i, val in enumerate(ref1_values):
            table.setItem(0, i + 1, QTableWidgetItem(f"{val:.3f}"))
        table.setItem(0, 6, QTableWidgetItem(f"{ref1_measured:.3f}"))

        table.setItem(1, 0, QTableWidgetItem("Referencia 2"))
        for i, val in enumerate(ref2_values):
            table.setItem(1, i + 1, QTableWidgetItem(f"{val:.3f}"))
        table.setItem(1, 6, QTableWidgetItem(f"{ref2_measured:.3f}"))
        layout.addWidget(table)

        okButton = QPushButton("OK")
        okButton.clicked.connect(resultDialog.accept)
        layout.addWidget(okButton)

        resultDialog.exec_()

        self.comm_handler.pH_slope = slope
        self.comm_handler.pH_intercept = intercept
        self.accept()

        if hasattr(self.parent(), "ph_cal_slope"):
            self.parent().ph_cal_slope.setText(f"{slope:.4f}")
        if hasattr(self.parent(), "ph_cal_intercept"):
            self.parent().ph_cal_intercept.setText(f"{intercept:.4f}")
        
        # Salva os valores na seção "Configurations"
        prefs = load_preferences() or default_preferences()
        if "Configurations" not in prefs:
            prefs["Configurations"] = {}
        prefs["Configurations"]["ph_cal_slope"] = str(slope)
        prefs["Configurations"]["ph_cal_intercept"] = str(intercept)
        save_preferences(prefs)

class PHOnePointCalibrationDialog(QDialog):
    ackClicked = pyqtSignal()

    def __init__(self, comm_handler, parent=None):
        super().__init__(parent)
        self.comm_handler = comm_handler
        self.setWindowTitle("Calibração do pH (um ponto)")
        self.setModal(True)

        grid = QGridLayout()
        self.ref_label = QLabel("Referência:")
        self.ref_entry = QLineEdit("7")
        grid.addWidget(self.ref_label, 0, 0)
        grid.addWidget(self.ref_entry, 0, 1)

        self.calibrate_button = QPushButton("Calibrar")
        self.cancel_button = QPushButton("Cancelar")
        button_layout = QVBoxLayout()
        button_layout.addWidget(self.calibrate_button)
        button_layout.addWidget(self.cancel_button)
        grid.addLayout(button_layout, 0, 2)

        self.progress_bar = QProgressBar()
        self.progress_bar.setAlignment(Qt.AlignCenter)
        self.status_label = QLabel("")
        self.status_label.setAlignment(Qt.AlignCenter)

        main_layout = QVBoxLayout()
        main_layout.addLayout(grid)
        main_layout.addWidget(self.progress_bar)
        main_layout.addWidget(self.status_label)
        self.setLayout(main_layout)

        self.calibrate_button.clicked.connect(self.start_calibration)
        self.cancel_button.clicked.connect(self.cancel_calibration)
        self.setAttribute(Qt.WA_DeleteOnClose)

        self.thread = None
        self.worker = None

    def closeEvent(self, event):
        self.cancel_calibration()
        event.accept()

    def cancel_calibration(self):
        if self.thread and self.thread.isRunning():
            if self.worker:
                self.worker.cancelled = True
                self.ackClicked.emit()
            self.thread.quit()
            self.thread.wait()
        self.reject()

    def start_calibration(self):
        try:
            target = float(self.ref_entry.text())
        except ValueError:
            QMessageBox.warning(self, "Erro", "Valor de referência inválido.")
            return

        self.calibrate_button.setEnabled(False)
        self.cancel_button.setEnabled(True)
        self.progress_bar.setValue(0)
        self.status_label.setText("")

        self.thread = QThread()
        self.worker = PHOnePointCalibrationWorker(self.comm_handler, target)
        self.worker.moveToThread(self.thread)

        self.ackClicked.connect(self.worker.ack_received)
        self.thread.started.connect(self.worker.run)
        self.worker.progress.connect(self.progress_bar.setValue)
        self.worker.warningRequest.connect(self.show_warning_dialog)
        self.worker.statusUpdate.connect(self.update_status_label)
        self.worker.finished.connect(self.on_calibration_finished)
        self.worker.finished.connect(self.thread.quit)
        self.worker.finished.connect(self.worker.deleteLater)
        self.thread.finished.connect(self.thread.deleteLater)

        self.thread.start()

    def update_status_label(self, text):
        self.status_label.setText(text)

    def show_warning_dialog(self, target):
        msg = f"Começar a calibração com pH {target}: Coloque o sensor em solução"
        QMessageBox.information(self, "Atenção", msg, QMessageBox.Ok)
        self.ackClicked.emit()

    def on_calibration_finished(self, measured, slope, intercept, values):
        if measured is None:
            return
        resultDialog = QDialog(self)
        resultDialog.setWindowTitle("Calibração Concluída")
        layout = QVBoxLayout(resultDialog)

        eq_label = QLabel(f"Curva: pH = {slope:.4f} * valor lido + {intercept:.4f}")
        layout.addWidget(eq_label)

        table = QTableWidget(1, 7)
        table.setHorizontalHeaderLabels(["Referência", "Valor 1", "Valor 2", "Valor 3", "Valor 4", "Valor 5", "Média"])
        table.setItem(0, 0, QTableWidgetItem("Referência"))
        for i, val in enumerate(values[:5]):
            table.setItem(0, i + 1, QTableWidgetItem(f"{val:.3f}"))
        table.setItem(0, 6, QTableWidgetItem(f"{measured:.3f}"))
        layout.addWidget(table)

        okButton = QPushButton("OK")
        okButton.clicked.connect(resultDialog.accept)
        layout.addWidget(okButton)

        resultDialog.exec_()

        self.comm_handler.pH_slope = slope
        self.comm_handler.pH_intercept = intercept
        self.accept()

        if hasattr(self.parent(), "ph_cal_slope"):
            self.parent().ph_cal_slope.setText(f"{slope:.4f}")
        if hasattr(self.parent(), "ph_cal_intercept"):
            self.parent().ph_cal_intercept.setText(f"{intercept:.4f}")
        
        # Salva os novos valores na seção "Configurations"
        prefs = load_preferences() or default_preferences()
        if "Configurations" not in prefs:
            prefs["Configurations"] = {}
        prefs["Configurations"]["ph_cal_slope"] = str(slope)
        prefs["Configurations"]["ph_cal_intercept"] = str(intercept)
        save_preferences(prefs)
