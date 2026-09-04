#!/usr/bin/env python
# -*- coding: utf-8 -*-

import sys
import os
import json
import serial  # pyserial for USB communication
import requests
import time
import threading
import concurrent.futures
import numpy as np
from scipy.interpolate import griddata
from matplotlib.backends.backend_qt5agg import FigureCanvasQTAgg as FigureCanvas
from matplotlib.figure import Figure
import matplotlib.pyplot as plt

from PyQt5.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QGridLayout,
    QGroupBox, QLabel, QLineEdit, QPushButton, QTabWidget, QFormLayout,
    QStyle, QSpacerItem, QSizePolicy, QCheckBox, QGraphicsDropShadowEffect,
    QComboBox, QPlainTextEdit, QFileDialog, QMessageBox, QDialog
)
from PyQt5.QtCore import Qt, QPoint, QTimer, QSize, QRect, pyqtSignal, QStandardPaths
from PyQt5.QtGui import QPainter, QFont, QColor, QGuiApplication

import pyqtgraph as pg
import qdarkstyle

# ─────────────────────────────────────────────────────────────
# Global UI configuration variables
# ─────────────────────────────────────────────────────────────
UI_CONFIG = {
    "window_size": QSize(1000, 480),       # The overall window size
    "block_size": QSize(300, 200),         # Default preferred size for blocks (used in Parameter Settings page)
    "margin": 10,
    "spacing": 10,
    "group_spacing": 10,
    "exp_values": {
         "label_width": 80,
         "edit_width": 50,
         "row_height": 30,
         "spacing": 10,
         "square_widget_size": QSize(300, 300),
    }
}

basedir = os.path.dirname(os.path.abspath(sys.argv[0]))
PREFERENCES_FILE = os.path.join(basedir, "preferences.json")

##############################################
# Communication Handler
##############################################
class CommunicationHandler:
    def __init__(self):
        self.mode = None    # "USB" or "WiFi"
        self.ser = None
        self.ip = None
        self.logger = None  # logger callback (set from MainWindow)
        # Create an executor for WiFi operations.
        self.executor = concurrent.futures.ThreadPoolExecutor(max_workers=1)

    def connect_usb(self, port, baudrate, data_bits, stop_bits, parity):
        try:
            self.ser = serial.Serial(
                port=port,
                baudrate=baudrate,
                bytesize=data_bits,
                stopbits=stop_bits,
                parity=parity,
                timeout=2
            )
            self.mode = "USB"
            print("Connected via USB")
            if self.logger:
                self.logger(f"Connected via USB on port {port}.")
        except Exception as e:
            print("USB Connection error:", e)
            if self.logger:
                self.logger(f"USB Connection error: {e}")
            self.ser = None

    def connect_wifi(self, ip):
        self.ip = ip
        self.mode = "WiFi"
        print("Connected via Wi-Fi to", ip)
        if self.logger:
            self.logger(f"Connecting via Wi-Fi to {ip}...")

    def disconnect(self):
        if self.mode == "USB" and self.ser:
            self.ser.close()
        self.mode = None
        print("Disconnected.")
        if self.logger:
            self.logger("Disconnected.")

    def send_command(self, command):
        command_str = json.dumps(command)
        print("Sending command:", command_str)
        if self.logger:
            self.logger("Sending command: " + command_str)
        if self.mode == "USB" and self.ser:
            try:
                self.ser.write(command_str.encode())
            except Exception as e:
                print("USB send error:", e)
                if self.logger:
                    self.logger(f"USB send error: {e}")
        elif self.mode == "WiFi" and self.ip:
            def do_post():
                try:
                    url = f"http://{self.ip}/command"
                    headers = {'Content-Type': 'application/json'}
                    requests.post(url, data=command_str, headers=headers, timeout=0.5)
                except Exception as e:
                    print("WiFi send error:", e)
                    if self.logger:
                        self.logger(f"WiFi send error: {e}")
            threading.Thread(target=do_post, daemon=True).start()
        else:
            print("Not connected.")
            if self.logger:
                self.logger("Not connected when trying to send command.")

    def read_data(self):
        if self.mode == "USB" and self.ser:
            try:
                if self.ser.in_waiting:
                    data = self.ser.readline().decode().strip()
                    return data
            except Exception as e:
                print("USB read error:", e)
                if self.logger:
                    self.logger(f"USB read error: {e}")
        elif self.mode == "WiFi" and self.ip:
            def do_get():
                url = f"http://{self.ip}/readData"
                r = requests.get(url, timeout=1)
                if r.status_code == 200:
                    return r.text
                return None
            future = self.executor.submit(do_get)
            try:
                # Wait a short time for the result.
                return future.result(timeout=0.2)
            except Exception as e:
                print("WiFi read error (async):", e)
                if self.logger:
                    self.logger("WiFi not found, trying again...")
                return None
        return None


##############################################
# Configurations Page
##############################################
class ConfigurationsPage(QWidget):
    def __init__(self, comm_handler, parent=None):
        super().__init__(parent)
        self.comm_handler = comm_handler

        # New grid layout with 3 columns:
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(10,10,10,10)
        main_layout.setSpacing(20)

        grid = QGridLayout()
        grid.setSpacing(20)

        # Column 0: Wi-Fi Settings, Data Delay and Oxygen Calibration (vertical)
        left_layout = QVBoxLayout()
        wifi_group = QGroupBox("Wi-Fi Settings")
        wifi_layout = QFormLayout()
        self.ip_edit = QLineEdit("192.168.4.1")
        self.wifi_connect_button = QPushButton("Connect Wi-Fi")
        self.wifi_stop_button = QPushButton("Stop Connection")
        wifi_layout.addRow("IP Address:", self.ip_edit)
        wifi_layout.addRow(self.wifi_connect_button)
        wifi_layout.addRow(self.wifi_stop_button)
        wifi_group.setLayout(wifi_layout)
        left_layout.addWidget(wifi_group)

        data_delay_group = QGroupBox("Data Delay")
        data_delay_layout = QFormLayout()
        self.data_delay_edit = QLineEdit("1000")  # default 1000 ms
        self.dataDelay = int(self.data_delay_edit.text())
        self.data_delay_button = QPushButton("Set Data Delay")
        data_delay_layout.addRow("Delay (ms):", self.data_delay_edit)
        data_delay_layout.addRow(self.data_delay_button)
        data_delay_group.setLayout(data_delay_layout)
        left_layout.addWidget(data_delay_group)

        oxygen_cal_group = QGroupBox("Oxygen Calibration (A*x+B)")
        oxygen_cal_layout = QFormLayout()
        self.oxy_cal_a = QLineEdit("0.0305473419314")
        self.oxy_cal_b = QLineEdit("-25.09136520919")
        oxygen_cal_layout.addRow("Coefficient A:", self.oxy_cal_a)
        oxygen_cal_layout.addRow("Coefficient B:", self.oxy_cal_b)
        oxygen_cal_group.setLayout(oxygen_cal_layout)
        left_layout.addWidget(oxygen_cal_group)

        left_widget = QWidget()
        left_widget.setLayout(left_layout)
        grid.addWidget(left_widget, 0, 0)

        # Column 1: USB Settings and Preferences (with Reset button)
        usb_group = QGroupBox("USB Settings")
        usb_layout = QFormLayout()
        self.com_port_edit = QLineEdit("COM5")
        self.baud_rate_edit = QLineEdit("115200")
        self.data_bits_edit = QLineEdit("8")
        self.stop_bits_edit = QLineEdit("1")
        self.parity_edit = QLineEdit("None")
        self.usb_connect_button = QPushButton("Connect USB")
        self.usb_stop_button = QPushButton("Stop Connection")
        usb_layout.addRow("COM Port:", self.com_port_edit)
        usb_layout.addRow("Baud Rate:", self.baud_rate_edit)
        usb_layout.addRow("Data Bits:", self.data_bits_edit)
        usb_layout.addRow("Stop Bits:", self.stop_bits_edit)
        usb_layout.addRow("Parity:", self.parity_edit)
        usb_layout.addRow(self.usb_connect_button)
        usb_layout.addRow(self.usb_stop_button)
        usb_group.setLayout(usb_layout)
        middle_layout = QVBoxLayout()
        middle_layout.addWidget(usb_group)
        # New Preferences group
        preferences_group = QGroupBox("Preferences")
        pref_layout = QVBoxLayout()
        self.reset_button = QPushButton("Reset Preferences")
        pref_layout.addWidget(self.reset_button)
        preferences_group.setLayout(pref_layout)
        middle_layout.addWidget(preferences_group)
        # Connect the reset button to trigger a reset in the main window.
        self.reset_button.clicked.connect(self.on_reset_preferences)
        middle_widget = QWidget()
        middle_widget.setLayout(middle_layout)
        grid.addWidget(middle_widget, 0, 1)

        # Column 2: Log block spanning two rows
        log_group = QGroupBox("Log")
        log_layout = QVBoxLayout()
        self.log_edit = QPlainTextEdit()
        self.log_edit.setReadOnly(True)
        log_layout.addWidget(self.log_edit)
        log_group.setLayout(log_layout)
        log_group.setMaximumHeight(500)
        grid.addWidget(log_group, 0, 2, 2, 1)

        grid.setColumnStretch(0, 1)
        grid.setColumnStretch(1, 1)
        grid.setColumnStretch(2, 2)
        grid.setRowStretch(0, 1)
        grid.setRowStretch(1, 0)

        theme_group = QGroupBox("UI Theme Customization")
        theme_layout = QHBoxLayout()
        self.theme_combo = QComboBox()
        self.theme_combo.addItems(["Dark", "Light"])
        theme_layout.addWidget(QLabel("Select Theme:"))
        theme_layout.addWidget(self.theme_combo)
        theme_group.setLayout(theme_layout)
        theme_group.setMaximumHeight(150)
        grid.addWidget(theme_group, 1, 0, 1, 2)

        main_layout.addLayout(grid)

        # Connect signals
        self.wifi_connect_button.clicked.connect(self.connect_wifi)
        self.usb_connect_button.clicked.connect(self.connect_usb)
        self.data_delay_button.clicked.connect(self.set_data_delay)
        self.wifi_stop_button.clicked.connect(self.stop_connection)
        self.usb_stop_button.clicked.connect(self.stop_connection)
        self.theme_combo.currentTextChanged.connect(self.change_theme)

    def on_reset_preferences(self):
        # Call the main window’s reset_preferences method.
        self.window().reset_preferences()

    def prompt_for_log_file(self):
        main_win = self.window()
        if not hasattr(main_win, "log_file") or main_win.log_file is None:
            initial_dir = QStandardPaths.writableLocation(QStandardPaths.DesktopLocation)
            fileName, _ = QFileDialog.getSaveFileName(
                self,
                "Select Log File",
                initial_dir,
                "Text Files (*.txt);;All Files (*)"
            )
            if fileName:
                main_win.log_file = open(fileName, "w")
                header = ("Time (s)\tTemperature (°C)\tMotor (rpm)\tpH\tAntifoam\t"
                "Pressure\tOxygen\tFlowmeter\tDistance\tOUR\n")

                main_win.log_file.write(header)
                main_win.log_file.flush()

    def connect_wifi(self):
        self.prompt_for_log_file()
        ip = self.ip_edit.text()
        self.comm_handler.connect_wifi(ip)

    def connect_usb(self):
        self.prompt_for_log_file()
        port = self.com_port_edit.text()
        try:
            baudrate = int(self.baud_rate_edit.text())
        except:
            baudrate = 115200
        try:
            data_bits = int(self.data_bits_edit.text())
        except:
            data_bits = 8
        try:
            stop_bits = int(self.stop_bits_edit.text())
        except:
            stop_bits = 1
        parity_str = self.parity_edit.text().strip().lower()
        if parity_str == "none":
            parity = serial.PARITY_NONE
        elif parity_str == "even":
            parity = serial.PARITY_EVEN
        elif parity_str == "odd":
            parity = serial.PARITY_ODD
        else:
            parity = serial.PARITY_NONE
        self.comm_handler.connect_usb(port, baudrate, data_bits, stop_bits, parity)

    def set_data_delay(self):
        try:
            delay = int(self.data_delay_edit.text())
            self.comm_handler.send_command({"dataDelay": delay})
            if self.parent() and hasattr(self.parent(), "dataDelay"):
                self.parent().dataDelay = delay
            print("Data delay set to", delay, "ms")
            if hasattr(self, 'log_edit'):
                self.log_edit.appendPlainText(f"Data delay set to {delay} ms")
        except:
            print("Invalid data delay")
            if hasattr(self, 'log_edit'):
                self.log_edit.appendPlainText("Invalid data delay")

    def stop_connection(self):
        self.comm_handler.disconnect()

    def change_theme(self, theme):
        if theme == "Dark":
            QApplication.instance().setStyleSheet(qdarkstyle.load_stylesheet_pyqt5())
        else:
            QApplication.instance().setStyleSheet("")

##############################################
# Parameter Settings Page (with kLa cascade checkbox for Oxygen)
##############################################
class ParameterSettingsPage(QWidget):
    def __init__(self, comm_handler):
        super().__init__()
        self.comm_handler = comm_handler
        main_layout = QVBoxLayout()
        main_layout.setContentsMargins(10, 10, 10, 10)
        main_layout.setSpacing(20)
        grid = QGridLayout()
        grid.setSpacing(20)

        self.temp_block = self.create_block(
            title="Temperature",
            checkbox_text="Temperature Control",
            label_text="Setpoint (°C):",
            default_value="25",
            send_callback=self.send_temperature,
            activation_color="red"
        )
        self.motor_block = self.create_block(
            title="Motor",
            checkbox_text="Motor Control",
            label_text="RPM:",
            default_value="100",
            send_callback=self.send_motor,
            activation_color="green"
        )
        self.pressure_block = self.create_block(
            title="Pressure",
            checkbox_text="Pressure Control",
            label_text="Setpoint (mmHg):",
            default_value="100",
            send_callback=self.send_pressure,
            activation_color="cyan"
        )
        self.oxy_block = self.create_block(
            title="Oxygen",
            checkbox_text="Oxygen Monitoring",
            label_text="Setpoint (%):",
            default_value="50",
            send_callback=self.send_oxygen,
            activation_color="magenta"
        )
        self.oxy_kla_cascade_checkbox = QCheckBox("kLa cascade")
        self.oxy_block.layout().insertWidget(self.oxy_block.layout().count() - 1, self.oxy_kla_cascade_checkbox)
        self.oxy_kla_cascade_checkbox.toggled.connect(self.on_kla_cascade_toggled)
        self.oxy_kla_cascade_checkbox.setEnabled(False)
        
        self.flow_block = self.create_block(
            title="Flowmeter",
            checkbox_text="Flowmeter Control",
            label_text="Setpoint:",
            default_value="1",
            send_callback=self.send_flowmeter,
            activation_color="yellow",
            extra_fields=[("Max Flow:", "50")]
        )
        self.flow_valve_checkbox = QCheckBox("Valve 1 Control")
        self.flow_block.layout().insertWidget(self.flow_block.layout().count() - 1, self.flow_valve_checkbox)
        self.flow_valve_checkbox.toggled.connect(self.on_flow_valve_toggled)

        self.distance_block = self.create_block(
            title="Distance Sensor",
            checkbox_text="Distance Sensor",
            label_text="Min Distance (cm):",
            default_value="100",
            send_callback=self.send_distance_sensor,
            activation_color="white"
        )

        group1 = QVBoxLayout()
        group1.addWidget(self.temp_block)
        group1.addWidget(self.oxy_block)
        group2 = QVBoxLayout()
        group2.addWidget(self.motor_block)
        group2.addWidget(self.flow_block)
        group3 = QVBoxLayout()
        group3.addWidget(self.pressure_block)
        group3.addWidget(self.distance_block)
        group1_widget = QWidget()
        group1_widget.setLayout(group1)
        group2_widget = QWidget()
        group2_widget.setLayout(group2)
        group3_widget = QWidget()
        group3_widget.setLayout(group3)

        grid.addWidget(group1_widget, 0, 0)
        grid.addWidget(group2_widget, 0, 1)
        grid.addWidget(group3_widget, 0, 2)

        self.ph_block = self.create_ph_block(activation_color="blue")
        self.nutri_block = self.create_nutrient_block(activation_color="purple")
        self.antifoam_block = self.create_block(
            title="Antifoam",
            checkbox_text="Antifoam Pump",
            label_text="Op Time (s):",
            default_value="5",
            send_callback=self.send_antifoam,
            activation_color="orange",
            extra_fields=[("Disable Time (s):", "20"), ("Speed (%):", "99")]
        )

        grid.addWidget(self.ph_block, 0, 3)
        grid.addWidget(self.nutri_block, 0, 4)
        grid.addWidget(self.antifoam_block, 0, 5)

        main_layout.addLayout(grid)
        self.setLayout(main_layout)

    def on_kla_cascade_toggled(self, checked):
        print("kLa cascade checkbox toggled:", checked)
        if checked:
            if not self.oxy_block.checkbox.isChecked():
                self.oxy_block.checkbox.setChecked(True)
            if not self.motor_block.checkbox.isChecked():
                self.motor_block.checkbox.setChecked(True)
            if not self.flow_block.checkbox.isChecked():
                self.flow_block.checkbox.setChecked(True)

    def on_flow_valve_toggled(self, checked):
        command = {"valve_1": 1 if checked else 0}
        self.comm_handler.send_command(command)

    def apply_neon_effect(self, widget, color, active):
        if active:
            effect = QGraphicsDropShadowEffect(widget)
            effect.setBlurRadius(20)
            effect.setColor(QColor(color))
            effect.setOffset(0)
            widget.setGraphicsEffect(effect)
        else:
            widget.setGraphicsEffect(None)

    def create_block(self, title, checkbox_text, label_text, default_value, send_callback, activation_color, extra_fields=None, block_size=UI_CONFIG["block_size"]):
        group = QGroupBox(title)
        group.setStyleSheet("QGroupBox::title { font-size: 14pt; font-weight: bold; }"
                            "QGroupBox { padding: 10px; margin: 5px; }")
        group.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        group.sizeHint = lambda: block_size

        layout = QVBoxLayout()
        layout.setSpacing(5)

        checkbox = QCheckBox(checkbox_text)
        layout.addWidget(checkbox)

        layout.addWidget(QLabel(label_text))
        line_edit = QLineEdit(default_value)
        layout.addWidget(line_edit)
        line_edit.returnPressed.connect(send_callback)

        extra_edits = {}
        if extra_fields:
            for lbl, val in extra_fields:
                layout.addWidget(QLabel(lbl))
                le = QLineEdit(val)
                layout.addWidget(le)
                le.returnPressed.connect(send_callback)
                extra_edits[lbl] = le

        layout.addItem(QSpacerItem(20, 40, QSizePolicy.Minimum, QSizePolicy.Expanding))

        group.setLayout(layout)
        group.checkbox = checkbox
        group.line_edit = line_edit
        group.extra_edits = extra_edits

        checkbox.toggled.connect(lambda checked, grp=group, col=activation_color, callback=send_callback: (self.apply_neon_effect(grp, col, checked), callback()))
        return group

    def create_ph_block(self, activation_color, block_size=UI_CONFIG["block_size"]):
        group = QGroupBox("pH Control")
        group.setStyleSheet("QGroupBox::title { font-size: 14pt; font-weight: bold; }"
                            "QGroupBox { padding: 10px; margin: 5px; }")
        group.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        group.sizeHint = lambda: block_size

        layout = QVBoxLayout()
        layout.setSpacing(5)
        group.checkbox = QCheckBox("pH Control")
        layout.addWidget(group.checkbox)
        
        layout.addWidget(QLabel("Setpoint:"))
        group.ph_setpoint_edit = QLineEdit("7")
        layout.addWidget(group.ph_setpoint_edit)
        group.ph_setpoint_edit.returnPressed.connect(self.send_ph)
        
        layout.addWidget(QLabel("Inactive Error:"))
        group.ph_error_edit = QLineEdit("0.17")
        layout.addWidget(group.ph_error_edit)
        group.ph_error_edit.returnPressed.connect(self.send_ph)
        
        layout.addWidget(QLabel("Operation Time (s):"))
        group.ph_op_time_edit = QLineEdit("5")
        layout.addWidget(group.ph_op_time_edit)
        group.ph_op_time_edit.returnPressed.connect(self.send_ph)
        
        layout.addWidget(QLabel("Disable Time (s):"))
        group.ph_disable_time_edit = QLineEdit("20")
        layout.addWidget(group.ph_disable_time_edit)
        group.ph_disable_time_edit.returnPressed.connect(self.send_ph)
        
        layout.addWidget(QLabel("Pump Speed (%):"))
        group.ph_speed_edit = QLineEdit("50")
        layout.addWidget(group.ph_speed_edit)
        group.ph_speed_edit.returnPressed.connect(self.send_ph)
        
        layout.addItem(QSpacerItem(20, 40, QSizePolicy.Minimum, QSizePolicy.Expanding))
        group.setLayout(layout)
        group.checkbox.toggled.connect(lambda checked, grp=group, col=activation_color, callback=self.send_ph: (self.apply_neon_effect(grp, col, checked), callback()))
        return group

    def create_nutrient_block(self, activation_color, block_size=UI_CONFIG["block_size"]):
        group = QGroupBox("Nutrient Pump")
        group.setStyleSheet("QGroupBox::title { font-size: 14pt; font-weight: bold; }"
                            "QGroupBox { padding: 10px; margin: 5px; }")
        group.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        group.sizeHint = lambda: block_size

        layout = QVBoxLayout()
        layout.setSpacing(5)
        group.checkbox = QCheckBox("Nutrient Pump")
        layout.addWidget(group.checkbox)
        
        layout.addWidget(QLabel("Operation Time (s):"))
        group.nutri_op_time_edit = QLineEdit("999")
        layout.addWidget(group.nutri_op_time_edit)
        group.nutri_op_time_edit.returnPressed.connect(self.send_nutrient)
        
        layout.addWidget(QLabel("Disable Time (s):"))
        group.nutri_disable_time_edit = QLineEdit("1")
        layout.addWidget(group.nutri_disable_time_edit)
        group.nutri_disable_time_edit.returnPressed.connect(self.send_nutrient)
        
        layout.addWidget(QLabel("Operation Cycle (s):"))
        group.nutri_op_cycle_edit = QLineEdit("500")
        layout.addWidget(group.nutri_op_cycle_edit)
        group.nutri_op_cycle_edit.returnPressed.connect(self.send_nutrient)
        
        layout.addWidget(QLabel("Disable Cycle (s):"))
        group.nutri_disable_cycle_edit = QLineEdit("1")
        layout.addWidget(group.nutri_disable_cycle_edit)
        group.nutri_disable_cycle_edit.returnPressed.connect(self.send_nutrient)
        
        layout.addWidget(QLabel("Pump Speed (%):"))
        group.nutri_speed_edit = QLineEdit("99")
        layout.addWidget(group.nutri_speed_edit)
        group.nutri_speed_edit.returnPressed.connect(self.send_nutrient)
        
        layout.addItem(QSpacerItem(20, 40, QSizePolicy.Minimum, QSizePolicy.Expanding))
        group.setLayout(layout)
        group.checkbox.toggled.connect(lambda checked, grp=group, col=activation_color, callback=self.send_nutrient: (self.apply_neon_effect(grp, col, checked), callback()))
        return group

    def send_temperature(self):
        text = self.replace_comma(self.temp_block.line_edit.text())
        try:
            value = float(text)
        except ValueError:
            value = 25.0
        if self.temp_block.checkbox.isChecked():
            value = max(15, min(value, 60))
        else:
            value = 0
        command = {"tempSetpoint": value}
        self.comm_handler.send_command(command)

    def send_motor(self):
        text = self.replace_comma(self.motor_block.line_edit.text())
        try:
            value = int(float(text))
        except ValueError:
            value = 100
        if self.motor_block.checkbox.isChecked():
            value = max(50, min(value, 1000))
        else:
            value = 0
        command = {"motorSetpoint": value}
        self.comm_handler.send_command(command)

    def send_ph(self):
        try:
            ph_value = float(self.replace_comma(self.ph_block.ph_setpoint_edit.text()))
        except:
            ph_value = 7
        if not self.ph_block.checkbox.isChecked():
            ph_value = 0
        try:
            error_value = float(self.replace_comma(self.ph_block.ph_error_edit.text()))
        except:
            error_value = 0.17
        try:
            op_time = float(self.replace_comma(self.ph_block.ph_op_time_edit.text()))
        except:
            op_time = 5
        try:
            disable_time = float(self.replace_comma(self.ph_block.ph_disable_time_edit.text()))
        except:
            disable_time = 20
        try:
            speed = float(self.replace_comma(self.ph_block.ph_speed_edit.text()))
        except:
            speed = 50
        if not self.ph_block.checkbox.isChecked():
            speed = 0
        command = {
            "pHSetpoint": ph_value,
            "pHError": error_value,
            "pHOperation": op_time,
            "pHMix": disable_time,
            "pHIntensity": speed
        }
        self.comm_handler.send_command(command)

    def send_oxygen(self):
        text = self.replace_comma(self.oxy_block.line_edit.text())
        try:
            value = float(text)
        except ValueError:
            value = 50
        if self.oxy_block.checkbox.isChecked():
            value = max(1, min(value, 100))
        else:
            value = 0
        command = {"oxygenMonitor": value}
        self.comm_handler.send_command(command)

    def send_nutrient(self):
        try:
            op_time = int(float(self.replace_comma(self.nutri_block.nutri_op_time_edit.text())))
        except:
            op_time = 999
        try:
            disable_time = int(float(self.replace_comma(self.nutri_block.nutri_disable_time_edit.text())))
        except:
            disable_time = 1
        try:
            op_cycle = int(float(self.replace_comma(self.nutri_block.nutri_op_cycle_edit.text())))
        except:
            op_cycle = 500
        try:
            disable_cycle = int(float(self.replace_comma(self.nutri_block.nutri_disable_cycle_edit.text())))
        except:
            disable_cycle = 1
        try:
            speed = int(float(self.replace_comma(self.nutri_block.nutri_speed_edit.text())))
        except:
            speed = 99
        op_time = max(0, min(op_time, 999))
        disable_time = max(1, min(disable_time, 999))
        op_cycle = max(1, min(op_cycle, 500))
        disable_cycle = max(1, min(disable_cycle, 500))
        speed = max(0, min(speed, 99))
        if not self.nutri_block.checkbox.isChecked():
            speed = 0
        command = {
            "nutriOperation": op_time,
            "nutriMix": disable_time,
            "nutriOpCycle": op_cycle,
            "nutriMixCycle": disable_cycle,
            "nutriIntensity": speed
        }
        self.comm_handler.send_command(command)

    def send_antifoam(self):
        try:
            op_value = int(float(self.replace_comma(self.antifoam_block.line_edit.text())))
        except:
            op_value = 5
        disable_text = self.replace_comma(self.antifoam_block.extra_edits["Disable Time (s):"].text())
        try:
            disable_value = int(float(disable_text))
        except:
            disable_value = 20
        speed_text = self.replace_comma(self.antifoam_block.extra_edits["Speed (%):"].text())
        try:
            speed = int(float(speed_text))
        except:
            speed = 99
        if not self.antifoam_block.checkbox.isChecked():
            speed = 0
        op_value = max(0, min(op_value, 999))
        disable_value = max(1, min(disable_value, 999))
        speed = max(0, min(speed, 99))
        command = {
            "antifoamOperation": op_value,
            "antifoamMix": disable_value,
            "antifoamIntensity": speed
        }
        self.comm_handler.send_command(command)

    def send_pressure(self):
        text = self.replace_comma(self.pressure_block.line_edit.text())
        try:
            value = float(text)
        except:
            value = 100
        if self.pressure_block.checkbox.isChecked():
            value = max(1, min(value, 380))
        else:
            value = 0
        command = {"pressureReference": value}
        self.comm_handler.send_command(command)

    def send_flowmeter(self):
        setpoint_text = self.replace_comma(self.flow_block.line_edit.text())
        try:
            setpoint = float(setpoint_text)
        except:
            setpoint = 1
        max_flow_text = self.replace_comma(self.flow_block.extra_edits["Max Flow:"].text())
        try:
            max_flow = float(max_flow_text)
        except:
            max_flow = 50
        if self.flow_block.checkbox.isChecked():
            setpoint = max(0, min(setpoint, max_flow))
            flow_comm = 1
            if setpoint == 0: 
                valve2 = 1
            else: 
                valve2 = 0
        else:
            flow_comm = 0
            valve2 = 1
        command = {
            "flowmeterComm": flow_comm,
            "flowSetpoint": setpoint,
            "maxFlow": max_flow,
            "valve_2": valve2,
        }
        self.comm_handler.send_command(command)

    def send_distance_sensor(self):
        try:
            dist_reference = float(self.replace_comma(self.distance_block.line_edit.text()))
        except ValueError:
            dist_reference = 100.0
        sensor_state = 1 if self.distance_block.checkbox.isChecked() else 0
        command = {"distanceSensorComm": sensor_state, "distanceSensorReference": dist_reference}
        self.comm_handler.send_command(command)

    def replace_comma(self, value_str):
        return value_str.replace(",", ".")

    def get_temperature_setpoint(self):
        try:
            return float(self.replace_comma(self.temp_block.line_edit.text()))
        except:
            return 25.0

    def get_motor_setpoint(self):
        try:
            return int(float(self.replace_comma(self.motor_block.line_edit.text())))
        except:
            return 100

    def get_ph_setpoint(self):
        try:
            return float(self.replace_comma(self.ph_block.ph_setpoint_edit.text()))
        except:
            return 7.0

    def get_oxygen_setpoint(self):
        try:
            return float(self.replace_comma(self.oxy_block.line_edit.text()))
        except:
            return 50.0

    def get_pressure_setpoint(self):
        try:
            return float(self.replace_comma(self.pressure_block.line_edit.text()))
        except:
            return 100.0

    def get_flow_setpoint(self):
        try:
            return float(self.replace_comma(self.flow_block.line_edit.text()))
        except:
            return 1.0

    def get_distance_min(self):
        try:
            return float(self.replace_comma(self.distance_block.line_edit.text()))
        except:
            return 10.0

##############################################
# Graphs Page
##############################################
class GraphsPage(QWidget):
    def __init__(self, comm_handler, param_settings_page, configurations_page):
        super().__init__()
        self.comm_handler = comm_handler
        self.param_settings_page = param_settings_page
        self.configurations_page = configurations_page
        layout = QVBoxLayout()
        self.tab_widget = QTabWidget()
        layout.addWidget(self.tab_widget)
        self.setLayout(layout)
        self.data_buffers = {}
        self.tab_widgets_data = {}
        self.timer = QTimer()
        self.timer.timeout.connect(self.update_data)
        self.timer.start(1000)
        self.VALIDATION_RULES = {
            "Tempval": {"min": 0, "max": 70, "threshold": 5},
            "Motor": {"min": 0, "max": 1500, "threshold": 100},  
            "pHval": {"min": 0, "max": 14, "threshold": 0.5},
            "Oxyval": {"min": 0, "max": 4096, "threshold": 100},
            "Pressureval": {"min": 1, "max": 380, "threshold": 20},
            "FlowRate": {"min": 0, "max": 100, "threshold": 5},
            "Distance": {"min": 0, "max": 1000, "threshold": 10},
            "Antifoam": {"min": 0, "max": 999, "threshold": 5}
        }
        # Dictionary to store the last valid value for each key.
        self.last_valid_values = {}
        self.monitored_params = {
            "Temperature": {"active": lambda: self.param_settings_page.temp_block.checkbox.isChecked(),
                            "setpoint": self.param_settings_page.get_temperature_setpoint,
                            "unit": "°C", "pen": 'r', "data_key": "Tempval"},
            "Motor RPM": {"active": lambda: self.param_settings_page.motor_block.checkbox.isChecked(),
                          "setpoint": self.param_settings_page.get_motor_setpoint,
                          "unit": "RPM", "pen": 'g', "data_key": None},
            "pH": {"active": lambda: self.param_settings_page.ph_block.checkbox.isChecked(),
                   "setpoint": self.param_settings_page.get_ph_setpoint,
                   "unit": "", "pen": 'b', "data_key": "pHval"},
            "Oxygen": {"active": lambda: self.param_settings_page.oxy_block.checkbox.isChecked(),
                       "setpoint": self.param_settings_page.get_oxygen_setpoint,
                       "unit": "%", "pen": 'm', "data_key": "Oxyval"},
            "Antifoam": {"active": lambda: self.param_settings_page.antifoam_block.checkbox.isChecked(),
                         "setpoint": None,
                         "unit": "", "pen": 'orange', "data_key": "Antifoam"},
            "Pressure": {"active": lambda: self.param_settings_page.pressure_block.checkbox.isChecked(),
                         "setpoint": self.param_settings_page.get_pressure_setpoint,
                         "unit": "mmHg", "pen": 'c', "data_key": "Pressureval"},
            "Flowmeter": {"active": lambda: self.param_settings_page.flow_block.checkbox.isChecked(),
                          "setpoint": self.param_settings_page.get_flow_setpoint,
                          "unit": "", "pen": 'y', "data_key": "FlowRate"},
            "Distance": {"active": lambda: self.param_settings_page.distance_block.checkbox.isChecked(),
                         "setpoint": self.param_settings_page.get_distance_min,
                         "unit": "cm", "pen": 'w', "data_key": "Distance"}
        }
        self.main_window = None

    def update_tabs(self):
        for param, config in self.monitored_params.items():
            if config["active"]():
                if param not in self.tab_widgets_data:
                    if config["pen"] is not None:
                        widget = QWidget()
                        vlayout = QVBoxLayout()
                        plot = pg.PlotWidget(title=param)
                        plot.showGrid(x=True, y=True)
                        set_line = None
                        if config["setpoint"] is not None:
                            set_line = pg.InfiniteLine(angle=0, pen=pg.mkPen('w', style=Qt.DashLine))
                            plot.addItem(set_line)
                        vlayout.addWidget(plot)
                        widget.setLayout(vlayout)
                        self.tab_widgets_data[param] = {"widget": widget, "plot": plot, "line": set_line}
                        self.data_buffers[param] = ([], [])
                    else:
                        label = QLabel(f"{param} status")
                        widget = QWidget()
                        hlayout = QHBoxLayout()
                        hlayout.addWidget(label)
                        widget.setLayout(hlayout)
                        self.tab_widgets_data[param] = {"widget": widget, "label": label}
                    self.tab_widget.addTab(self.tab_widgets_data[param]["widget"], param)
            else:
                if param in self.tab_widgets_data:
                    index = self.tab_widget.indexOf(self.tab_widgets_data[param]["widget"])
                    if index != -1:
                        self.tab_widget.removeTab(index)
                    del self.tab_widgets_data[param]
                    if param in self.data_buffers:
                        del self.data_buffers[param]

    def update_data(self):
        self.update_tabs()
        data_str = self.comm_handler.read_data()
        if data_str:
            try:
                data = json.loads(data_str)
                # Validate some critical sensor values first:
                if "Tempval" in data:
                    try:
                        temp_val = float(data["Tempval"])
                        rule = self.VALIDATION_RULES.get("Tempval")
                        self.last_valid_values["Tempval"] = temp_val
                    except Exception as e:
                        print("Temperature conversion error:", e)
                        return
                if "Oxyval" in data:
                    try:
                        if float(data["Oxyval"]) >= 0:
                            oxy_val = float(data["Oxyval"])
                            rule = self.VALIDATION_RULES.get("Oxyval")
                            a = float(self.configurations_page.oxy_cal_a.text())
                            b = float(self.configurations_page.oxy_cal_b.text())
                            calibrated_oxy = a * oxy_val + b
                            self.last_valid_values["Oxyval"] = calibrated_oxy
                            data["Oxyval"] = round(calibrated_oxy, 4)
                        else:
                            print("Oxygen value negative error")
                    except Exception as e:
                        print("Oxygen conversion error:", e)
                        return
                if self.main_window and hasattr(self.main_window, "log_message"):
                    self.main_window.log_message(f"Received data: {json.dumps(data)}")
            except Exception as e:
                print("JSON parse error:", e)
                if self.main_window and hasattr(self.main_window, "log_message"):
                    self.main_window.log_message(f"JSON parse error: {e}")
                return
            current_time = time.time()
        else:
            current_time = time.time()
            data = {}

        # Process each monitored parameter:
        for param, config in self.monitored_params.items():
            if config["active"]() and param in self.tab_widgets_data:
                # Special handling for Motor RPM (not directly in data):
                if param == "Motor RPM":
                    if self.main_window.param_settings_page.oxy_kla_cascade_checkbox.isChecked():
                        value = self.main_window.current_motor_rpm
                    else:
                        value = self.param_settings_page.get_motor_setpoint()
                    rule = self.VALIDATION_RULES.get("Motor")
                    if not (rule["min"] <= value <= rule["max"]):
                        print("Motor RPM out of range; ignoring update.")
                        value = self.last_valid_values.get("Motor", value)
                    else:
                        if "Motor" in self.last_valid_values and abs(value - self.last_valid_values["Motor"]) > rule["threshold"]:
                            print("Motor RPM change too high; ignoring update.")
                            value = self.last_valid_values["Motor"]
                        else:
                            self.last_valid_values["Motor"] = value
                elif config["data_key"]:
                    try:
                        value = float(data.get(config["data_key"], None))
                    except Exception as e:
                        continue
                    rule = self.VALIDATION_RULES.get(config["data_key"])
                    if rule:
                        if not (rule["min"] <= value <= rule["max"]):
                            print(f"{config['data_key']} out of range; ignoring update.")
                            value = self.last_valid_values.get(config["data_key"], value)
                    else:
                        self.last_valid_values[config["data_key"]] = value
                else:
                    value = None

                if value is not None:
                    # Append to the data buffer and update the plot:
                    x_data, y_data = self.data_buffers[param]
                    x_data.append(current_time)
                    y_data.append(value)
                    plot = self.tab_widgets_data[param]["plot"]
                    plot.clear()
                    plot.plot(x_data, y_data, pen=config["pen"])
                    if config["setpoint"] is not None and self.tab_widgets_data[param]["line"] is not None:
                        sp = config["setpoint"]()
                        plot.addItem(pg.InfiniteLine(pos=sp, angle=0, pen=pg.mkPen('w', style=Qt.DashLine)))
                else:
                    label = self.tab_widgets_data[param]["label"]
                    label.setText(f"{param} status: {value}")

        if self.main_window:
            elapsed = current_time - self.main_window.start_time
            temperature = data.get("Tempval", "-1")
            if self.param_settings_page.motor_block.checkbox.isChecked():
                motor = self.main_window.current_motor_rpm
            else:
                motor = -1
            ph = data.get("pHval", "-1")
            antifoam = data.get("Antifoam", "-1")
            pressure = data.get("Pressureval", "-1")
            oxygen = data.get("Oxyval", "-1")
            flowmeter = data.get("FlowRate", "-1")
            distance = data.get("Distance", "-1")
            row = f"{elapsed:.2f}\t{temperature}\t{motor}\t{ph}\t{antifoam}\t{pressure}\t{oxygen}\t{flowmeter}\t{distance}\t{self.main_window.current_OUR}\n"
            self.main_window.write_log_row(row)


##############################################
# Experimental Values Widget (kLa Cascade)
##############################################
class ExperimentalValuesWidget(QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.rpm_max_label = QLabel("   Max", self)
        self.rpm_max = QLineEdit("800", self)
        self.rpm_mid_label = QLabel("   Mid", self)
        self.rpm_mid = QLineEdit("500", self)
        self.rpm_min_label = QLabel("   Min", self)
        self.rpm_min = QLineEdit("200", self)
        
        self.square_widget = SquareWidget(self)

        self.rpm_label = QLabel("    \u2191\n\n  rpm", self)
        self.flow_label = QLabel("          Q (L/min)    \u2192", self)
        
        self.flow_min = QLineEdit("5", self)
        self.flow_min_label = QLabel("Min", self)
        self.flow_mid = QLineEdit("10", self)
        self.flow_mid_label = QLabel("Mid", self)
        self.flow_max = QLineEdit("15", self)
        self.flow_max_label = QLabel("Max", self)

    def resizeEvent(self, event):
        super().resizeEvent(event)
        w = self.width()
        h = self.height()
        left_margin = UI_CONFIG["exp_values"]["spacing"]
        top_margin = UI_CONFIG["exp_values"]["spacing"]
        spacing = UI_CONFIG["exp_values"]["spacing"]
        row_height = UI_CONFIG["exp_values"]["row_height"]
        label_width = UI_CONFIG["exp_values"]["label_width"]
        edit_width = UI_CONFIG["exp_values"]["edit_width"]
        
        rpm_origin = QPoint(left_margin, 15)
        self.rpm_max_label.setGeometry(rpm_origin.x(), rpm_origin.y(), label_width, row_height)
        self.rpm_max.setGeometry(rpm_origin.x() + label_width + spacing, rpm_origin.y(), edit_width, row_height)
        
        rpm_row2_y = rpm_origin.y() + row_height + 77
        self.rpm_mid_label.setGeometry(rpm_origin.x(), rpm_row2_y, label_width, row_height)
        self.rpm_mid.setGeometry(rpm_origin.x() + label_width + spacing, rpm_row2_y, edit_width, row_height)
        
        rpm_row3_y = rpm_row2_y + row_height + 82
        self.rpm_min_label.setGeometry(rpm_origin.x(), rpm_row3_y, label_width, row_height)
        self.rpm_min.setGeometry(rpm_origin.x() + label_width + spacing, rpm_row3_y, edit_width, row_height)
        
        rpm_label_y = rpm_row3_y + row_height + spacing
        self.rpm_label.setGeometry(rpm_origin.x(), rpm_label_y, 60, 80)
        
        flow_label_y = rpm_label_y + row_height + 50
        self.flow_label.setGeometry(rpm_origin.x(), flow_label_y, 180, 30)
        
        square_width = UI_CONFIG["exp_values"]["square_widget_size"].width()
        square_height = UI_CONFIG["exp_values"]["square_widget_size"].height()
        square_block_origin = QPoint(rpm_origin.x() + label_width + spacing + edit_width + 2 * spacing,
                                    top_margin)
        self.square_widget.setGeometry(square_block_origin.x(), square_block_origin.y(),
                                    square_width, square_height)
        self.square_widget.setSquareOrigin(QPoint(20, 20))
        
        flow_edit_width = edit_width
        flow_label_height = 20
        total_flow_width = 3 * flow_edit_width + 2 * spacing
        flow_origin = QPoint((w - total_flow_width) // 2 - 50,
                            h - row_height - flow_label_height - top_margin - 30)
        self.flow_min.setGeometry(flow_origin.x() + 60, flow_origin.y(), flow_edit_width, row_height)
        self.flow_min_label.setGeometry(flow_origin.x() + 65, flow_origin.y() + 50,
                                        flow_edit_width, flow_label_height)
        self.flow_mid.setGeometry(flow_origin.x() + flow_edit_width + spacing + 110, flow_origin.y(),
                                flow_edit_width, row_height)
        self.flow_mid_label.setGeometry(flow_origin.x() + flow_edit_width + spacing + 115, flow_origin.y() + 50,
                                        flow_edit_width, flow_label_height)
        self.flow_max.setGeometry(flow_origin.x() + 2*(flow_edit_width + spacing) + 160, flow_origin.y(),
                                flow_edit_width, row_height)
        self.flow_max_label.setGeometry(flow_origin.x() + 2*(flow_edit_width + spacing) + 165, flow_origin.y() + 50,
                                        100, flow_label_height)

    def paintEvent(self, event):
        super().paintEvent(event)
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        pen = painter.pen()
        pen.setStyle(Qt.DashLine)
        painter.setPen(pen)
        
        inner_rect = self.square_widget.inner_rect
        
        top_left = self.square_widget.mapTo(self, inner_rect.topLeft())
        top_right = self.square_widget.mapTo(self, inner_rect.topRight())
        bottom_left = self.square_widget.mapTo(self, inner_rect.bottomLeft())
        bottom_right = self.square_widget.mapTo(self, inner_rect.bottomRight())
        center = self.square_widget.mapTo(self, inner_rect.center())
        
        rpm_max_right = self.rpm_max.mapTo(self, self.rpm_max.rect().topRight())
        rpm_mid_right = self.rpm_mid.mapTo(self, self.rpm_mid.rect().topRight())
        rpm_min_right = self.rpm_min.mapTo(self, self.rpm_min.rect().topRight())
        
        def get_top_center(widget):
            rect = widget.rect()
            return widget.mapTo(self, QPoint(rect.center().x(), rect.top()))
        
        flow_min_top = get_top_center(self.flow_min)
        flow_mid_top = get_top_center(self.flow_mid)
        flow_max_top = get_top_center(self.flow_max)
        
        painter.drawLine(top_left, QPoint(rpm_max_right.x(), top_left.y()))
        painter.drawLine(bottom_left, QPoint(rpm_min_right.x(), bottom_left.y()))
        painter.drawLine(bottom_left, QPoint(bottom_left.x(), flow_min_top.y()))
        painter.drawLine(bottom_right, QPoint(bottom_right.x(), flow_max_top.y()))
        painter.drawLine(center, QPoint(rpm_mid_right.x(), center.y()))
        painter.drawLine(center, QPoint(center.x(), flow_mid_top.y()))

##############################################
# SquareWidget (Square Block)
##############################################
class SquareWidget(QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.margin = 60
        self.square_origin = QPoint(20, 20)
        
        self.kla_top_left = QLineEdit("100.58", self)
        self.kla_top_right = QLineEdit("117.32", self)
        self.kla_bottom_left = QLineEdit("26.35", self)
        self.kla_bottom_right = QLineEdit("71.85", self)
        self.kla_center = QLineEdit("83.12", self)
        for widget in [self.kla_top_left, self.kla_top_right,
                       self.kla_bottom_left, self.kla_bottom_right, self.kla_center]:
            widget.setFixedSize(60, 30)
            widget.setStyleSheet("background: rgba(255,255,255,200); color: rgb(64, 64, 64);")
            widget.setAlignment(Qt.AlignCenter)

    def setSquareOrigin(self, pos: QPoint):
        self.square_origin = pos
        self.update()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        w = self.width()
        h = self.height()
        m = self.margin
        size = min(w - self.square_origin.x() - m, h - self.square_origin.y() - m)
        inner_rect = QRect(self.square_origin, QSize(size, size))
        self.inner_rect = inner_rect

        offset1 = QPoint(0, -30)
        offset2 = QPoint(0, 0)
        self.kla_top_left.move(inner_rect.topLeft() + offset2)
        self.kla_top_right.move(inner_rect.topRight() + offset2)
        self.kla_bottom_left.move(inner_rect.bottomLeft() + offset1)
        self.kla_bottom_right.move(inner_rect.bottomRight() + offset1)
        self.kla_center.move(inner_rect.center() + offset1)

    def paintEvent(self, event):
        super().paintEvent(event)
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        m = self.margin
        size = min(self.width() - self.square_origin.x() - m,
                   self.height() - self.square_origin.y() - m)
        inner_rect = QRect(self.square_origin, QSize(size, size))
        painter.drawRect(inner_rect)
        dot_radius = 5
        painter.setBrush(Qt.black)
        painter.drawEllipse(inner_rect.topLeft(), dot_radius, dot_radius)
        painter.drawEllipse(inner_rect.topRight(), dot_radius, dot_radius)
        painter.drawEllipse(inner_rect.bottomLeft(), dot_radius, dot_radius)
        painter.drawEllipse(inner_rect.bottomRight(), dot_radius, dot_radius)
        painter.drawEllipse(inner_rect.center(), dot_radius, dot_radius)

##############################################
# Gradient Ascend Widget (with PID config button)
##############################################
class GradientAscendWidget(QWidget):
    gradientAscentRun = pyqtSignal()

    def __init__(self, exp_values_widget, parent=None):
        super().__init__(parent)
        self.exp_values_widget = exp_values_widget

        main_layout = QHBoxLayout(self)
        main_layout.setContentsMargins(5, 5, 5, 5)
        main_layout.setSpacing(10)

        controls_widget = QWidget(self)
        controls_widget.setMaximumWidth(220)
        controls_layout = QFormLayout(controls_widget)
        controls_layout.setSpacing(5)

        self.entry_grid_res = QLineEdit("16", self)
        controls_layout.addRow("Grid Resolution:", self.entry_grid_res)

        self.entry_rising_grid_res = QLineEdit("600", self)
        controls_layout.addRow("Rising Grid Resolution:", self.entry_rising_grid_res)

        self.combo_interp_method = QComboBox(self)
        self.combo_interp_method.addItems(["linear", "cubic"])
        self.combo_interp_method.setCurrentText("cubic")
        controls_layout.addRow("Interpolation Method:", self.combo_interp_method)

        self.entry_step_multiplier = QLineEdit("120", self)
        controls_layout.addRow("Step Size Multiplier:", self.entry_step_multiplier)

        self.entry_min_step = QLineEdit("16", self)
        controls_layout.addRow("Minimum Step Size:", self.entry_min_step)

        self.entry_max_iter = QLineEdit("1000", self)
        controls_layout.addRow("Max Iterations:", self.entry_max_iter)

        self.entry_quiver_scale = QLineEdit("100", self)
        controls_layout.addRow("Quiver Scale:", self.entry_quiver_scale)

        self.entry_quiver_color = QLineEdit("black", self)
        controls_layout.addRow("Quiver Color:", self.entry_quiver_color)

        self.entry_quiver_width = QLineEdit("0.006", self)
        controls_layout.addRow("Quiver Width:", self.entry_quiver_width)

        self.combo_colormap = QComboBox(self)
        self.combo_colormap.addItems(["viridis", "plasma", "inferno", "magma", "cividis"])
        self.combo_colormap.setCurrentText("viridis")
        controls_layout.addRow("Color Map:", self.combo_colormap)
        
        self.start_button = QPushButton("Start Gradient Ascent", self)
        controls_layout.addRow(self.start_button)
        self.start_button.clicked.connect(self.run_gradient_ascent)

        self.pid_config_button = QPushButton("PID config", self)
        self.pid_config_button.setEnabled(False)
        controls_layout.addRow(self.pid_config_button)
        self.pid_config_button.clicked.connect(self.open_pid_config)
        
        main_layout.addWidget(controls_widget)

        self.figure = Figure(figsize=(4, 3), dpi=100) 
        self.figure.patch.set_facecolor("none")
        self.canvas = FigureCanvas(self.figure)
        self.canvas.setStyleSheet("background:transparent;")
        main_layout.addWidget(self.canvas, stretch=1)
        
        self.gradient_ascent_run = False
        self.pid_config = None
        self.red_dot = None

    def open_pid_config(self):
        class PIDConfigDialog(QDialog):
            def __init__(self, parent=None, pid_config=None):
                super().__init__(parent)
                self.setWindowTitle("PID Configuration")
                self.setFixedSize(300, 200)
                layout = QFormLayout(self)
                # Use saved values if available; otherwise, use defaults.
                self.c_star_edit = QLineEdit(str(pid_config.get("C_star", "100")) if pid_config else "100")
                self.kp_edit = QLineEdit(str(pid_config.get("Kp", "0.01")) if pid_config else "0.01")
                self.ki_edit = QLineEdit(str(pid_config.get("Ki", "0")) if pid_config else "0")
                self.kd_edit = QLineEdit(str(pid_config.get("Kd", "0")) if pid_config else "0")
                layout.addRow("C* (Target O2):", self.c_star_edit)
                layout.addRow("Kp:", self.kp_edit)
                layout.addRow("Ki:", self.ki_edit)
                layout.addRow("Kd:", self.kd_edit)
                button_layout = QHBoxLayout()
                self.ok_button = QPushButton("OK")
                self.cancel_button = QPushButton("Cancel")
                button_layout.addWidget(self.ok_button)
                button_layout.addWidget(self.cancel_button)
                layout.addRow(button_layout)
                self.ok_button.clicked.connect(self.accept)
                self.cancel_button.clicked.connect(self.reject)
            def get_values(self):
                try:
                    c_star = float(self.c_star_edit.text())
                except:
                    c_star = 100.0
                try:
                    kp = float(self.kp_edit.text())
                except:
                    kp = 0.01
                try:
                    ki = float(self.ki_edit.text())
                except:
                    ki = 0.0
                try:
                    kd = float(self.kd_edit.text())
                except:
                    kd = 0.0
                return {"C_star": c_star, "Kp": kp, "Ki": ki, "Kd": kd}
        dialog = PIDConfigDialog(self, pid_config=self.pid_config)
        if dialog.exec_() == QDialog.Accepted:
            self.pid_config = dialog.get_values()
            print("PID configuration set to:", self.pid_config)
            main_win = self.window()
            if hasattr(main_win, "log_message"):
                main_win.log_message(f"PID configuration set: {self.pid_config}")


    def run_gradient_ascent(self):
        self.figure.clf()
        try:
            Q_min = float(self.exp_values_widget.flow_min.text())
            Q_mid = float(self.exp_values_widget.flow_mid.text())
            Q_max = float(self.exp_values_widget.flow_max.text())
            N_min = float(self.exp_values_widget.rpm_min.text())
            N_mid = float(self.exp_values_widget.rpm_mid.text())
            N_max = float(self.exp_values_widget.rpm_max.text())
            sw = self.exp_values_widget.square_widget
            kLa_bottom_left = float(sw.kla_bottom_left.text())
            kLa_bottom_right = float(sw.kla_bottom_right.text())
            kLa_top_left = float(sw.kla_top_left.text())
            kLa_top_right = float(sw.kla_top_right.text())
            kLa_mid = float(sw.kla_center.text())
        except Exception as e:
            print("Error reading experimental values:", e)
            return

        points = np.array([
            [Q_min, N_min],
            [Q_min, N_max],
            [Q_max, N_min],
            [Q_max, N_max],
            [Q_mid, N_mid]
        ])
        values = np.array([
            kLa_bottom_left,
            kLa_top_left,
            kLa_bottom_right,
            kLa_top_right,
            kLa_mid
        ])

        try:
            grid_res = int(self.entry_grid_res.text())
            rising_grid_res = int(self.entry_rising_grid_res.text())
            interp_method = self.combo_interp_method.currentText()
            step_multiplier = float(self.entry_step_multiplier.text())
            min_step = float(self.entry_min_step.text())
            max_iter = int(self.entry_max_iter.text())
            quiver_scale = float(self.entry_quiver_scale.text())
            quiver_color = self.entry_quiver_color.text().strip()
            quiver_width = float(self.entry_quiver_width.text())
            selected_cmap = self.combo_colormap.currentText()
        except Exception as e:
            print("Error reading algorithm parameters:", e)
            return

        Q_vals = np.linspace(Q_min, Q_max, grid_res)
        N_vals = np.linspace(N_min, N_max, grid_res)
        Q_grid, N_grid = np.meshgrid(Q_vals, N_vals)
        kLa_grid = griddata(points, values, (Q_grid, N_grid), method=interp_method)

        Q_vals_1 = np.linspace(Q_min, Q_max, rising_grid_res)
        N_vals_1 = np.linspace(N_min, N_max, rising_grid_res)
        Q_grid_1, N_grid_1 = np.meshgrid(Q_vals_1, N_vals_1)
        kLa_grid_1 = griddata(points, values, (Q_grid_1, N_grid_1), method=interp_method)

        kLa_gradient_Q_1 = np.gradient(kLa_grid_1, axis=1)
        kLa_gradient_N_1 = np.gradient(kLa_grid_1, axis=0)

        max_index = np.unravel_index(np.argmax(kLa_grid_1, axis=None), kLa_grid_1.shape)
        min_index = np.unravel_index(np.argmin(kLa_grid_1, axis=None), kLa_grid_1.shape)

        max_Q = Q_grid_1[max_index]
        max_N = N_grid_1[max_index]
        min_Q = Q_grid_1[min_index]
        min_N = N_grid_1[min_index]

        end_point_rise = (max_index[0], max_index[1])
        start_point = (0, 0)
        current_point_rise = np.array(start_point, dtype=float)
        path_rise = [tuple(current_point_rise.astype(int))]
        distance_to_end_prev_rise = -1

        iter_count = 0
        grid_shape = np.array(kLa_grid_1.shape)

        while iter_count < max_iter:
            if not (0 <= current_point_rise[0] < grid_shape[0] and 0 <= current_point_rise[1] < grid_shape[1]):
                break
            grad_rise = np.array([
                kLa_gradient_N_1[int(current_point_rise[0]), int(current_point_rise[1])],
                kLa_gradient_Q_1[int(current_point_rise[0]), int(current_point_rise[1])]
            ])
            norm_rise = np.linalg.norm(grad_rise)
            if norm_rise == 0:
                break
            normalized_grad_rise = grad_rise / norm_rise
            step_rise = max(step_multiplier * norm_rise, min_step)
            next_point_rise = current_point_rise + step_rise * normalized_grad_rise
            next_point_rise = np.clip(np.round(next_point_rise).astype(int), 0, grid_shape - 1)
            distance_rise = np.linalg.norm(np.array(end_point_rise) - next_point_rise)
            path_rise.append(tuple(next_point_rise))
            if distance_rise == distance_to_end_prev_rise:
                break
            distance_to_end_prev_rise = distance_rise
            current_point_rise = next_point_rise.astype(float)
            iter_count += 1

        path_rise = np.array(path_rise)
        Q_path_rise = Q_grid_1[path_rise[:, 0], path_rise[:, 1]]
        N_path_rise = N_grid_1[path_rise[:, 0], path_rise[:, 1]]
        kLa_path_rise = kLa_grid_1[path_rise[:, 0], path_rise[:, 1]]

        self.Q_path_rise = Q_path_rise
        self.N_path_rise = N_path_rise
        self.kLa_path_rise = kLa_path_rise
        self.max_kLa = float(np.max(kLa_path_rise))
        self.min_kLa = float(np.min(kLa_path_rise))
        self.kLa_setpoint = self.min_kLa

        self.gradient_ascent_run = True
        self.pid_config_button.setEnabled(True)
        self.gradientAscentRun.emit()

        main_win = self.window()
        if main_win and hasattr(main_win, "log_message"):
            main_win.log_message("Gradient ascent path calculated.")

        ax = self.figure.add_subplot(111)
        self.figure.patch.set_facecolor("none")
        ax.set_facecolor("none")
        text_color = "black" if self.window().configurations_page.theme_combo.currentText() == "Light" else "white"
        contour = ax.contourf(
            Q_grid, N_grid, kLa_grid, 
            levels=50, cmap=selected_cmap, alpha=1.0
        )
        ax.quiver(
            Q_grid, N_grid,
            np.gradient(kLa_grid, axis=1),
            np.gradient(kLa_grid, axis=0),
            color=quiver_color,
            scale=quiver_scale,
            width=quiver_width
        )
        ax.scatter(points[:, 0], points[:, 1], 
                   color='white', edgecolor='black',
                   s=50, linewidths=1, label='Data Points')
        colors = plt.cm.viridis(np.linspace(0, 1, 10))
        ax.plot(Q_path_rise, N_path_rise, color=colors[1],
                linestyle='-', linewidth=2, label='Rising Path')
        ax.scatter(max_Q, max_N, color=colors[1], marker='x',
                   s=50, linewidths=2, label='Max kLa')
        ax.scatter(min_Q, min_N, color=colors[9], marker='x',
                   s=50, linewidths=2, label='Min kLa')
        ax.set_xlim(Q_min, Q_max)
        ax.set_ylim(N_min, N_max)
        ax.set_xlabel(r'$Q_{G}$ (vvm)', fontsize=9, color=text_color)
        ax.set_ylabel('N (rpm)', fontsize=9, color=text_color)
        ax.set_title("Gradient Ascent of kLa Field", fontsize=10, color=text_color)
        ax.tick_params(labelsize=8, colors=text_color)
        for spine in ax.spines.values():
            spine.set_edgecolor(text_color)
        self.figure.tight_layout()
        self.canvas.draw()

    def update_red_dot(self, Q_target, N_target):
        if not self.figure.axes:
            return
        ax = self.figure.axes[0]
        if self.red_dot is None:
            self.red_dot = ax.scatter([Q_target], [N_target], color="red", s=50, zorder=10)
        else:
            self.red_dot.set_offsets(np.array([[Q_target, N_target]]))
        self.canvas.draw_idle()

##############################################
# kLa Cascade Page
##############################################
class kLaCascadePage(QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.exp_group = QGroupBox("Experimental Values", self)
        self.experimental_values = ExperimentalValuesWidget(self.exp_group)
        self.grad_group = QGroupBox("Gradient Ascent", self)
        self.gradient_widget = GradientAscendWidget(self.experimental_values, self.grad_group)

    def resizeEvent(self, event):
        super().resizeEvent(event)
        w = self.width()
        h = self.height()
        margin = 10

        exp_group_width = 5 * (w - 3 * margin) // 12
        grad_group_width = (w - 3 * margin) - exp_group_width
        group_height = h - 2 * margin

        self.exp_group.setGeometry(margin, margin, exp_group_width, group_height)
        self.experimental_values.setGeometry(
            10, 25, self.exp_group.width() - 20, self.exp_group.height() - 30
        )

        self.grad_group.setGeometry(margin + exp_group_width + margin, margin, grad_group_width, group_height)
        self.gradient_widget.setGeometry(
            10, 25, self.grad_group.width() - 20, self.grad_group.height() - 30
        )

##############################################
# Main Window with Bottom Navigation Tabs and Preferences Management
##############################################
class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.default_size = UI_CONFIG["window_size"]
        self.setWindowTitle("TECNAL Control App")
        self.resize(self.default_size)
        self.center_window()
        self.log_file = None
        self.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)

        self.comm_handler = CommunicationHandler()
        self.comm_handler.logger = self.log_message
        self.configurations_page = ConfigurationsPage(self.comm_handler)
        self.param_settings_page = ParameterSettingsPage(self.comm_handler)
        self.graphs_page = GraphsPage(self.comm_handler, self.param_settings_page, self.configurations_page)
        self.kla_cascade_page = kLaCascadePage()
        
        self.main_tab_widget = QTabWidget()
        self.main_tab_widget.setTabPosition(QTabWidget.South)
        self.main_tab_widget.tabBar().setStyleSheet("QTabBar::tab { font-size: 10pt; }")
        self.main_tab_widget.addTab(self.param_settings_page, self.style().standardIcon(QStyle.SP_ComputerIcon), "Parameters")
        self.main_tab_widget.addTab(self.graphs_page, self.style().standardIcon(QStyle.SP_DesktopIcon), "Graphs")
        self.main_tab_widget.addTab(self.kla_cascade_page, self.style().standardIcon(QStyle.SP_FileDialogDetailedView), "kLa Cascade")
        self.main_tab_widget.addTab(self.configurations_page, self.style().standardIcon(QStyle.SP_MessageBoxInformation), "Configurations")
        self.setCentralWidget(self.main_tab_widget)

        config_index = self.main_tab_widget.indexOf(self.configurations_page)
        self.main_tab_widget.setCurrentIndex(config_index)
        self.resize(self.default_size)

        self.previous_index = config_index
        self.main_tab_widget.currentChanged.connect(self.on_tab_changed)

        for cb in [self.param_settings_page.temp_block.checkbox,
                   self.param_settings_page.motor_block.checkbox,
                   self.param_settings_page.ph_block.checkbox,
                   self.param_settings_page.oxy_block.checkbox,
                   self.param_settings_page.antifoam_block.checkbox,
                   self.param_settings_page.pressure_block.checkbox,
                   self.param_settings_page.flow_block.checkbox,
                   self.param_settings_page.distance_block.checkbox,
                   self.param_settings_page.nutri_block.checkbox]:
            cb.toggled.connect(self.update_graphs_tab_state)
        self.update_graphs_tab_state()

        self.current_motor_rpm = self.param_settings_page.get_motor_setpoint()
        self.current_oxygen = 0.0
        self.last_oxygen_value = None
        self.pid_integral = 0.0
        self.last_pid_error = 0.0
        self.kLa_setpoint = None
        self.our_time_data = []
        self.our_our_data = []
        self.cascade_start_time = None
        self.current_OUR = -1  

        self.cascade_timer = QTimer(self)
        self.cascade_timer.timeout.connect(self.run_kla_cascade_control)
        try:
            self.dataDelay = self.configurations_page.dataDelay
        except Exception as e:
            print("Error reading data delay:", e)
            self.dataDelay = 1000
        self.cascade_timer.start(self.dataDelay)

        self.kla_cascade_page.gradient_widget.gradientAscentRun.connect(self.enable_kla_cascade)

        self.start_time = time.time()
        self.graphs_page.main_window = self

        # Set up auto-save for all QLineEdit fields.
        self.setup_auto_save()
        # Load saved preferences if available.
        self.load_preferences()

    def center_window(self):
        screen = QGuiApplication.primaryScreen().availableGeometry()
        window_geometry = self.frameGeometry()
        window_geometry.moveCenter(screen.center())
        offset = 100
        self.move(window_geometry.topLeft().x() - offset, window_geometry.topLeft().y())

    def log_message(self, message):
        timestamp = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime())
        full_message = f"[{timestamp}] {message}"
        self.configurations_page.log_edit.appendPlainText(full_message)
        print(full_message)

    def write_log_row(self, row):
        if self.log_file:
            self.log_file.write(row)
            self.log_file.flush()

    def closeEvent(self, event):
        if self.log_file:
            self.log_file.close()
        self.save_preferences()
        event.accept()

    def enable_kla_cascade(self):
        self.param_settings_page.oxy_kla_cascade_checkbox.setEnabled(True)

    def on_tab_changed(self, index):
        self.previous_index = index
        self.resize(self.default_size)
        self.update_graphs_tab_state()

    def update_graphs_tab_state(self):
        active = any(cb.isChecked() for cb in [
            self.param_settings_page.temp_block.checkbox,
            self.param_settings_page.motor_block.checkbox,
            self.param_settings_page.ph_block.checkbox,
            self.param_settings_page.oxy_block.checkbox,
            self.param_settings_page.antifoam_block.checkbox,
            self.param_settings_page.pressure_block.checkbox,
            self.param_settings_page.flow_block.checkbox,
            self.param_settings_page.distance_block.checkbox,
            self.param_settings_page.nutri_block.checkbox
        ])
        self.main_tab_widget.setTabEnabled(self.main_tab_widget.indexOf(self.graphs_page), active)

    def run_kla_cascade_control(self):
        if not self.param_settings_page.oxy_kla_cascade_checkbox.isChecked():
            self.current_OUR = -1
            return

        data_str = self.comm_handler.read_data()
        if data_str:
            try:
                data = json.loads(data_str)
                if "Tempval" in data:
                    if float(data["Tempval"]) > 70 or float(data["Tempval"]) < 0:
                        return
                if "Oxyval" in data:
                    if float(data["Oxyval"]) < 0 or float(data["Oxyval"]) > 4096:
                        return
                oxy = data.get("Oxyval", None)
                if oxy is not None:
                    if self.param_settings_page.oxy_block.checkbox.isChecked():
                        try:
                            raw_oxy = float(oxy)
                            a = float(self.configurations_page.oxy_cal_a.text())
                            b = float(self.configurations_page.oxy_cal_b.text())
                            calibrated_oxy = a * raw_oxy + b
                            self.current_oxygen = calibrated_oxy
                        except Exception as e:
                            print("Oxygen calibration error:", e)
                            self.current_oxygen = float(oxy)
                    else:
                        self.current_oxygen = float(oxy)
            except Exception as e:
                print("JSON parse error in run_kla_cascade_control:", e)
                self.log_message(f"JSON parse error in run_kla_cascade_control: {e}")

        gradient_widget = self.kla_cascade_page.gradient_widget
        if not hasattr(gradient_widget, "kLa_setpoint") or gradient_widget.kLa_setpoint is None:
            return

        if not gradient_widget.pid_config:
            pid_config = {"C_star": 100.0, "Kp": 0.05, "Ki": 0.0, "Kd": 0.0}
        else:
            pid_config = gradient_widget.pid_config

        if self.cascade_start_time is None:
            self.cascade_start_time = time.time()

        oxygen_setpoint = self.param_settings_page.get_oxygen_setpoint()
        self.comm_handler.send_command({"oxygenMonitor": oxygen_setpoint})

        C = self.current_oxygen
        if C <= 0 or C > 150:
            return
        print("C", C)

        conc_error = oxygen_setpoint - C
        K_outer = 0.1
        desired_dC_dt = K_outer * conc_error

        dt = self.dataDelay / 1000.0
        if self.last_oxygen_value is None:
            dC_dt = 0.0
        else:
            dC_dt = (C - self.last_oxygen_value) / dt
        self.last_oxygen_value = C

        raw_inner_error = desired_dC_dt - dC_dt
        if not hasattr(self, 'inner_error_buffer'):
            self.inner_error_buffer = []
        self.inner_error_buffer.append(raw_inner_error)
        if len(self.inner_error_buffer) > 5:
            self.inner_error_buffer.pop(0)
        inner_error = sum(self.inner_error_buffer) / len(self.inner_error_buffer)

        max_integral = 1000.0
        min_integral = -1000.0
        self.pid_integral += inner_error * dt
        self.pid_integral = max(min_integral, min(self.pid_integral, max_integral))
        if not hasattr(self, 'last_inner_error'):
            self.last_inner_error = inner_error
        inner_derivative = (inner_error - self.last_inner_error) / dt if dt > 0 else 0.0
        self.last_inner_error = inner_error

        proportional_term = pid_config["Kp"] * inner_error
        integral_term    = pid_config["Ki"] * self.pid_integral
        derivative_term  = pid_config["Kd"] * inner_derivative
        calculated_output = proportional_term + integral_term + derivative_term
        print("calculated_output", calculated_output)

        min_output = 0
        max_output = gradient_widget.max_kLa + gradient_widget.min_kLa
        pid_output = max(min_output, min(calculated_output, max_output))
        print("pid_output", pid_output)

        if pid_output != calculated_output:
            self.pid_integral -= inner_error * dt

        new_setpoint = gradient_widget.kLa_setpoint + pid_output
        if new_setpoint > gradient_widget.max_kLa:
            self.kLa_setpoint = gradient_widget.max_kLa
        elif new_setpoint < gradient_widget.min_kLa:
            self.kLa_setpoint = gradient_widget.min_kLa
        else:
            self.kLa_setpoint = new_setpoint
        print("self.kLa_setpoint", self.kLa_setpoint)

        self.log_message(f"Outer Loop: conc_error={conc_error:.2f}, desired_dC_dt={desired_dC_dt:.2f}; "
                        f"Inner Loop: measured_dC_dt={dC_dt:.2f}, inner_error={inner_error:.2f}, "
                        f"PID output={pid_output:.2f}, new kLa_setpoint={self.kLa_setpoint:.2f}")

        if not hasattr(gradient_widget, "kLa_path_rise") or gradient_widget.kLa_path_rise is None:
            return

        kLa_rise = gradient_widget.kLa_path_rise
        Q_rise = gradient_widget.Q_path_rise
        N_rise = gradient_widget.N_path_rise
        idx = None
        for i in range(len(kLa_rise) - 1):
            if (kLa_rise[i] <= self.kLa_setpoint <= kLa_rise[i+1]) or (kLa_rise[i] >= self.kLa_setpoint >= kLa_rise[i+1]):
                idx = i
                break
        if idx is None:
            if self.kLa_setpoint < kLa_rise[0]:
                idx = 0
                frac = 0.0
            else:
                idx = len(kLa_rise) - 2
                frac = 1.0
        else:
            if kLa_rise[idx+1] != kLa_rise[idx]:
                frac = (self.kLa_setpoint - kLa_rise[idx]) / (kLa_rise[idx+1] - kLa_rise[idx])
            else:
                frac = 0.0
        Q_target = Q_rise[idx] + frac * (Q_rise[idx+1] - Q_rise[idx])
        N_target = N_rise[idx] + frac * (N_rise[idx+1] - N_rise[idx])
        self.current_motor_rpm = N_target
        exp_widget = gradient_widget.exp_values_widget
        try:
            flow_min = float(exp_widget.flow_min.text())
            flow_max = float(exp_widget.flow_max.text())
            rpm_min = float(exp_widget.rpm_min.text())
            rpm_max = float(exp_widget.rpm_max.text())
        except:
            flow_min, flow_max, rpm_min, rpm_max = 0, 100, 0, 1000
        Q_target = max(flow_min, min(Q_target, flow_max))
        N_target = max(rpm_min, min(N_target, rpm_max))
        self.comm_handler.send_command({"flowSetpoint": Q_target, "flowmeterComm": 1, "oxygenMonitor": oxygen_setpoint,"motorSetpoint": N_target})

        kLa_target = kLa_rise[idx] + frac * (kLa_rise[idx+1] - kLa_rise[idx])
        
        # Calculate the OUR
        OUR = -(pid_config["C_star"] - C) * kLa_target / 3600 - dC_dt
        self.current_OUR = OUR

        current_time = time.time()
        relative_time = current_time - self.cascade_start_time

        self.our_time_data.append(relative_time)
        self.our_our_data.append(OUR)

        if "OUR" not in self.graphs_page.monitored_params:
            self.graphs_page.monitored_params["OUR"] = {
                "active": lambda: True,
                "setpoint": lambda: None,
                "unit": "",
                "pen": 'w',
                "data_key": "OUR"
            }
            self.graphs_page.data_buffers["OUR"] = ([], [])
            if "OUR" not in self.graphs_page.tab_widgets_data:
                widget = QWidget()
                vlayout = QVBoxLayout()
                plot = pg.PlotWidget(title="OUR vs Time")
                plot.showGrid(x=True, y=True)
                vlayout.addWidget(plot)
                widget.setLayout(vlayout)
                self.graphs_page.tab_widgets_data["OUR"] = {"widget": widget, "plot": plot, "line": None}
                self.graphs_page.tab_widget.addTab(widget, "OUR")
        buf = self.graphs_page.data_buffers["OUR"]
        buf[0].append(relative_time)
        buf[1].append(OUR)
        plot = self.graphs_page.tab_widgets_data["OUR"]["plot"]
        plot.clear()
        plot.plot(buf[0], buf[1], pen='w')

        gradient_widget.update_red_dot(Q_target, N_target)

    def setup_auto_save(self):
        # Connect all QLineEdit editingFinished signals to save_preferences.
        for le in self.findChildren(QLineEdit):
            le.editingFinished.connect(self.save_preferences)

    def load_preferences(self):
        if os.path.exists(PREFERENCES_FILE):
            try:
                with open(PREFERENCES_FILE, "r") as f:
                    prefs = json.load(f)
                self.apply_preferences(prefs)
            except Exception as e:
                print("Error loading preferences:", e)

    def save_preferences(self):
        prefs = self.collect_preferences()
        try:
            with open(PREFERENCES_FILE, "w") as f:
                json.dump(prefs, f, indent=4)
        except Exception as e:
            print("Error saving preferences:", e)

    def reset_preferences(self):
        reply = QMessageBox.question(self, "Reset Preferences", "Are you sure you want to reset all preferences to default?", QMessageBox.Yes | QMessageBox.No)
        if reply == QMessageBox.Yes:
            if os.path.exists(PREFERENCES_FILE):
                os.remove(PREFERENCES_FILE)
            self.apply_preferences(self.default_preferences())
            self.save_preferences()

    def collect_preferences(self):
        prefs = {}
        cp = self.configurations_page
        prefs["Configurations"] = {
            "ip_edit": cp.ip_edit.text(),
            "data_delay_edit": cp.data_delay_edit.text(),
            "oxy_cal_a": cp.oxy_cal_a.text(),
            "oxy_cal_b": cp.oxy_cal_b.text(),
            "com_port_edit": cp.com_port_edit.text(),
            "baud_rate_edit": cp.baud_rate_edit.text(),
            "data_bits_edit": cp.data_bits_edit.text(),
            "stop_bits_edit": cp.stop_bits_edit.text(),
            "parity_edit": cp.parity_edit.text(),
            "theme_combo": cp.theme_combo.currentText()
        }
        ps = self.param_settings_page
        def collect_block(block):
            d = {"line_edit": block.line_edit.text()}
            for k, le in block.extra_edits.items():
                d[k] = le.text()
            return d
        prefs["ParameterSettings"] = {
            "Temperature": collect_block(ps.temp_block),
            "Motor": collect_block(ps.motor_block),
            "Pressure": collect_block(ps.pressure_block),
            "Oxygen": collect_block(ps.oxy_block),
            "Flowmeter": collect_block(ps.flow_block),
            "Distance": collect_block(ps.distance_block)
        }
        def collect_full_block(block, fields):
            d = {}
            for field in fields:
                d[field] = getattr(block, field + "_edit").text()
            return d
        prefs["ParameterSettings"]["pH"] = collect_full_block(ps.ph_block, ["ph_setpoint", "ph_error", "ph_op_time", "ph_disable_time", "ph_speed"])
        prefs["ParameterSettings"]["Nutrient"] = collect_full_block(ps.nutri_block, ["nutri_op_time", "nutri_disable_time", "nutri_op_cycle", "nutri_disable_cycle", "nutri_speed"])
        prefs["ParameterSettings"]["Antifoam"] = {
            "antifoam_op_time": ps.antifoam_block.line_edit.text(),
            "antifoam_disable_time": ps.antifoam_block.extra_edits["Disable Time (s):"].text(),
            "antifoam_speed": ps.antifoam_block.extra_edits["Speed (%):"].text()
        }
        ev = self.kla_cascade_page.experimental_values
        prefs["ExperimentalValues"] = {
            "rpm_max": ev.rpm_max.text(),
            "rpm_mid": ev.rpm_mid.text(),
            "rpm_min": ev.rpm_min.text(),
            "flow_min": ev.flow_min.text(),
            "flow_mid": ev.flow_mid.text(),
            "flow_max": ev.flow_max.text()
        }
        ga = self.kla_cascade_page.gradient_widget
        prefs["GradientAscend"] = {
            "entry_grid_res": ga.entry_grid_res.text(),
            "entry_rising_grid_res": ga.entry_rising_grid_res.text(),
            "entry_step_multiplier": ga.entry_step_multiplier.text(),
            "entry_min_step": ga.entry_min_step.text(),
            "entry_max_iter": ga.entry_max_iter.text(),
            "entry_quiver_scale": ga.entry_quiver_scale.text(),
            "entry_quiver_color": ga.entry_quiver_color.text(),
            "entry_quiver_width": ga.entry_quiver_width.text(),
            "combo_interp_method": ga.combo_interp_method.currentText(),
            "combo_colormap": ga.combo_colormap.currentText()
        }
        prefs["PIDConfig"] = ga.pid_config if ga.pid_config is not None else {}
        return prefs

    def apply_preferences(self, prefs):
        cp = self.configurations_page
        if "Configurations" in prefs:
            conf = prefs["Configurations"]
            cp.ip_edit.setText(conf.get("ip_edit", "192.168.4.1"))
            cp.data_delay_edit.setText(conf.get("data_delay_edit", "1000"))
            cp.oxy_cal_a.setText(conf.get("oxy_cal_a", "0.0305473419314"))
            cp.oxy_cal_b.setText(conf.get("oxy_cal_b", "-25.09136520919"))
            cp.com_port_edit.setText(conf.get("com_port_edit", "COM5"))
            cp.baud_rate_edit.setText(conf.get("baud_rate_edit", "115200"))
            cp.data_bits_edit.setText(conf.get("data_bits_edit", "8"))
            cp.stop_bits_edit.setText(conf.get("stop_bits_edit", "1"))
            cp.parity_edit.setText(conf.get("parity_edit", "None"))
            theme = conf.get("theme_combo", "Dark")
            cp.theme_combo.setCurrentText(theme)
            cp.change_theme(theme)
        ps = self.param_settings_page
        if "ParameterSettings" in prefs:
            par = prefs["ParameterSettings"]
            def apply_block(block, data):
                block.line_edit.setText(data.get("line_edit", block.line_edit.text()))
                for k, le in block.extra_edits.items():
                    le.setText(data.get(k, le.text()))
            if "Temperature" in par:
                apply_block(ps.temp_block, par["Temperature"])
            if "Motor" in par:
                apply_block(ps.motor_block, par["Motor"])
            if "Pressure" in par:
                apply_block(ps.pressure_block, par["Pressure"])
            if "Oxygen" in par:
                apply_block(ps.oxy_block, par["Oxygen"])
            if "Flowmeter" in par:
                apply_block(ps.flow_block, par["Flowmeter"])
            if "Distance" in par:
                apply_block(ps.distance_block, par["Distance"])
            if "pH" in par:
                d = par["pH"]
                ps.ph_block.ph_setpoint_edit.setText(d.get("ph_setpoint", "7"))
                ps.ph_block.ph_error_edit.setText(d.get("ph_error", "0.17"))
                ps.ph_block.ph_op_time_edit.setText(d.get("ph_op_time", "5"))
                ps.ph_block.ph_disable_time_edit.setText(d.get("ph_disable_time", "20"))
                ps.ph_block.ph_speed_edit.setText(d.get("ph_speed", "50"))
            if "Nutrient" in par:
                d = par["Nutrient"]
                ps.nutri_block.nutri_op_time_edit.setText(d.get("nutri_op_time", "999"))
                ps.nutri_block.nutri_disable_time_edit.setText(d.get("nutri_disable_time", "1"))
                ps.nutri_block.nutri_op_cycle_edit.setText(d.get("nutri_op_cycle", "500"))
                ps.nutri_block.nutri_disable_cycle_edit.setText(d.get("nutri_disable_cycle", "1"))
                ps.nutri_block.nutri_speed_edit.setText(d.get("nutri_speed", "99"))
            if "Antifoam" in par:
                d = par["Antifoam"]
                ps.antifoam_block.line_edit.setText(d.get("antifoam_op_time", "5"))
                ps.antifoam_block.extra_edits["Disable Time (s):"].setText(d.get("antifoam_disable_time", "20"))
                ps.antifoam_block.extra_edits["Speed (%):"].setText(d.get("antifoam_speed", "99"))
        ev = self.kla_cascade_page.experimental_values
        if "ExperimentalValues" in prefs:
            ex = prefs["ExperimentalValues"]
            ev.rpm_max.setText(ex.get("rpm_max", "800"))
            ev.rpm_mid.setText(ex.get("rpm_mid", "500"))
            ev.rpm_min.setText(ex.get("rpm_min", "200"))
            ev.flow_min.setText(ex.get("flow_min", "5"))
            ev.flow_mid.setText(ex.get("flow_mid", "10"))
            ev.flow_max.setText(ex.get("flow_max", "15"))
        ga = self.kla_cascade_page.gradient_widget
        if "GradientAscend" in prefs:
            g = prefs["GradientAscend"]
            ga.entry_grid_res.setText(g.get("entry_grid_res", "16"))
            ga.entry_rising_grid_res.setText(g.get("entry_rising_grid_res", "600"))
            ga.entry_step_multiplier.setText(g.get("entry_step_multiplier", "120"))
            ga.entry_min_step.setText(g.get("entry_min_step", "16"))
            ga.entry_max_iter.setText(g.get("entry_max_iter", "1000"))
            ga.entry_quiver_scale.setText(g.get("entry_quiver_scale", "100"))
            ga.entry_quiver_color.setText(g.get("entry_quiver_color", "black"))
            ga.entry_quiver_width.setText(g.get("entry_quiver_width", "0.006"))
            ga.combo_interp_method.setCurrentText(g.get("combo_interp_method", "cubic"))
            ga.combo_colormap.setCurrentText(g.get("combo_colormap", "viridis"))
        if "PIDConfig" in prefs:
            ga.pid_config = prefs["PIDConfig"]

    def default_preferences(self):
        return {
            "Configurations": {
                "ip_edit": "192.168.4.1",
                "data_delay_edit": "1000",
                "oxy_cal_a": "0.0305473419314",
                "oxy_cal_b": "-25.09136520919",
                "com_port_edit": "COM5",
                "baud_rate_edit": "115200",
                "data_bits_edit": "8",
                "stop_bits_edit": "1",
                "parity_edit": "None",
                "theme_combo": "Dark"
            },
            "ParameterSettings": {
                "Temperature": {"line_edit": "37"},
                "Motor": {"line_edit": "400"},
                "Pressure": {"line_edit": "100"},
                "Oxygen": {"line_edit": "75"},
                "Flowmeter": {"line_edit": "5", "Max Flow:": "50"},
                "Distance": {"line_edit": "100"},
                "pH": {"ph_setpoint": "7", "ph_error": "0.17", "ph_op_time": "10", "ph_disable_time": "10", "ph_speed": "99"},
                "Nutrient": {"nutri_op_time": "999", "nutri_disable_time": "1", "nutri_op_cycle": "500", "nutri_disable_cycle": "1", "nutri_speed": "99"},
                "Antifoam": {"antifoam_op_time": "5", "antifoam_disable_time": "20", "antifoam_speed": "99"}
            },
            "ExperimentalValues": {
                "rpm_max": "800",
                "rpm_mid": "500",
                "rpm_min": "200",
                "flow_min": "5",
                "flow_mid": "10",
                "flow_max": "15"
            },
            "GradientAscend": {
                "entry_grid_res": "16",
                "entry_rising_grid_res": "600",
                "entry_step_multiplier": "120",
                "entry_min_step": "16",
                "entry_max_iter": "1000",
                "entry_quiver_scale": "100",
                "entry_quiver_color": "black",
                "entry_quiver_width": "0.006",
                "combo_interp_method": "cubic",
                "combo_colormap": "viridis"
            },
            "PIDConfig": {"C_star": "100.0", "Kp": "5", "Ki": "0.1", "Kd": "0.1"}
        }

##############################################
# Main entry point
##############################################
if __name__ == "__main__":
    app = QApplication(sys.argv)
    app.setFont(QFont("Roboto", 10))
    app.setStyleSheet(qdarkstyle.load_stylesheet_pyqt5())
    window = MainWindow()
    window.show()
    sys.exit(app.exec_())
