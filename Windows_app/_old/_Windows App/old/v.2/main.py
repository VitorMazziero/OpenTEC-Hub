#!/usr/bin/env python
# -*- coding: utf-8 -*-
import sys
import time
from PyQt5.QtWidgets import QApplication, QMainWindow, QTabWidget, QMessageBox, QLineEdit, QWidget, QGroupBox, QStyle
from PyQt5.QtGui import QFont
from PyQt5.QtCore import QTimer

# Import communication handler
from communication.communication_handler import CommunicationHandler
# Import preferences functions and defaults
from config.preferences import load_preferences, save_preferences, default_preferences
# Import UI pages
from ui.configurations_page import ConfigurationsPage
from ui.parameter_settings_page import ParameterSettingsPage
from ui.graphs_page import GraphsPage
from ui.kla_cascade_page import KlaCascadePage
from ui.kla_gassing_out_page import KlaGassingOutPage
# Import o método de controle em cascata
from kla_cascade_control import run_kla_cascade_control

class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("App TECNAL")
        self.resize(1000, 480)
        screen = QApplication.desktop().screenGeometry()
        x = screen.center().x() - self.width() // 2 - 125 
        y = screen.center().y() - self.height() // 2 - 50  
        self.move(x, y)
        
        # Inicializa o communication handler (onde os dados dos sensores são centralizados)
        self.comm_handler = CommunicationHandler()
        self.comm_handler.logger = self.log_message
        
        # Carrega as preferências (ou os defaults se não existirem)
        self.preferences = load_preferences() or default_preferences()
        print("Preferences loaded:", self.preferences)
        
        # Instancia as páginas de UI
        self.configurations_page = ConfigurationsPage(self.comm_handler)
        self.parameter_settings_page = ParameterSettingsPage(self.comm_handler)
        self.parameter_settings_page.main_window = self
        self.graphs_page = GraphsPage(self.comm_handler, self.parameter_settings_page, self.configurations_page)
        self.kla_cascade_page = KlaCascadePage(self.parameter_settings_page)
        self.kla_gassing_out_page = KlaGassingOutPage(self.comm_handler, self.kla_cascade_page)
        
        # Cria um QTabWidget para conter as páginas
        self.main_tab_widget = QTabWidget()
        self.main_tab_widget.addTab(
            self.parameter_settings_page,
            self.style().standardIcon(QStyle.SP_ComputerIcon),
            "Ajustes de Parâmetros"
        )
        self.main_tab_widget.addTab(
            self.graphs_page,
            self.style().standardIcon(QStyle.SP_DesktopIcon),
            "Gráficos"
        )
        self.main_tab_widget.addTab(
            self.kla_cascade_page,
            self.style().standardIcon(QStyle.SP_FileDialogDetailedView),
            "Cascata kLa"
        )
        self.main_tab_widget.addTab(
            self.kla_gassing_out_page,
            self.style().standardIcon(QStyle.SP_MediaPlay), # An icon for running an experiment
            "kLa gassing-out"
        )
        self.main_tab_widget.addTab(
            self.configurations_page,
            self.style().standardIcon(QStyle.SP_MessageBoxInformation),
            "Configurações"
        )
        
        self.setCentralWidget(self.main_tab_widget)
        
        self.setup_auto_save()
        self.apply_preferences(self.preferences)
        
        self.configurations_page.theme_combo.currentTextChanged.connect(
            self.parameter_settings_page.update_all_block_styles
        )
        
        self.configurations_page.theme_combo.currentTextChanged.connect(
            self.kla_cascade_page.gradient_widget.update_theme_gradient
        )

        self.kla_cascade_page.gradient_widget.gradientAscentRun.connect(self.enable_kla_cascade)
        
        self.connect_parameter_checkbox_signals()
        self.graphs_page.main_window = self
        
        self.define_kLa_cascade_variables()
        self.cascade_timer = QTimer(self)
        self.cascade_timer.timeout.connect(lambda: run_kla_cascade_control(self))
        try:
            self.dataDelay = int(self.configurations_page.data_delay_edit.text())
        except Exception as e:
            print("Error reading data delay:", e)
            self.dataDelay = 1000
        self.cascade_timer.start(self.dataDelay)

    def define_kLa_cascade_variables(self):
        self.start_time = time.time()
        self.cascade_start_time = None
        self.last_oxygen_value = None
        self.current_oxygen = 0.0
        self.current_motor_rpm = int(float(self.parameter_settings_page.motor_block.line_edit.text()))
        self.pid_integral = 0
        self.current_OUR = -1
        self.inner_error_buffer = []
        self.our_time_data = []
        self.our_our_data = []
        self.log_file = None
        self.last_inner_error = 0
        self.last_flow_command_time = 0

    def log_message(self, message):
        if hasattr(self, "configurations_page") and hasattr(self.configurations_page, "log_edit"):
            self.configurations_page.log_edit.appendPlainText(message)
        print(message)
        
    def write_log_row(self, row):
        if self.log_file:
            self.log_file.write(row)
            self.log_file.flush()
    
    def reset_preferences(self):
        reply = QMessageBox.question(
            self, "Resetar Preferências",
            "Você tem certeza que deseja resetar todas as preferências para os valores padrões?",
            QMessageBox.Yes | QMessageBox.No
        )
        if reply == QMessageBox.Yes:
            self.preferences = default_preferences()
            self.apply_preferences(self.preferences)
            save_preferences(self.preferences)
            self.log_message("As preferências foram resetadas para os valores padrões.")
        
    def collect_preferences(self):
        # Carrega as preferências existentes (ou os defaults se não existirem)
        prefs = load_preferences() or default_preferences()

        # Atualiza as preferências de configurações a partir da ConfigurationsPage.
        prefs["Configurations"]["ip_edit"] = self.configurations_page.ip_edit.text()
        prefs["Configurations"]["data_delay_edit"] = self.configurations_page.data_delay_edit.text()
        prefs["Configurations"]["oxy_cal_a"] = self.configurations_page.oxy_cal_a.text()
        prefs["Configurations"]["oxy_cal_b"] = self.configurations_page.oxy_cal_b.text()
        # Aqui os parâmetros de pH são salvos na seção "Configurations"
        prefs["Configurations"]["ph_cal_slope"] = self.configurations_page.ph_cal_slope.text()
        prefs["Configurations"]["ph_cal_intercept"] = self.configurations_page.ph_cal_intercept.text()
        prefs["Configurations"]["com_port_edit"] = self.configurations_page.com_port_combo.currentText()
        prefs["Configurations"]["theme_combo"] = self.configurations_page.theme_combo.currentText()

        # Atualiza as preferências da ParameterSettingsPage.
        par = prefs.get("ParameterSettings", {})
        par["Temperature"] = {"line_edit": self.parameter_settings_page.temp_block.line_edit.text()}
        par["Motor"] = {"line_edit": self.parameter_settings_page.motor_block.line_edit.text()}
        par["Pressure"] = {"line_edit": self.parameter_settings_page.pressure_block.line_edit.text()}
        par["Oxygen"] = {"line_edit": self.parameter_settings_page.oxy_block.line_edit.text()}
        par["Flowmeter"] = {"line_edit": self.parameter_settings_page.flow_block.line_edit.text()}
        par["Distance"] = {"line_edit": self.parameter_settings_page.distance_block.line_edit.text()}
        par["pH"] = {
            "ph_setpoint": self.parameter_settings_page.ph_block.ph_setpoint_edit.text(),
            "ph_error": self.parameter_settings_page.ph_block.ph_error_edit.text(),
            "ph_op_time": self.parameter_settings_page.ph_block.ph_op_time_edit.text(),
            "ph_disable_time": self.parameter_settings_page.ph_block.ph_disable_time_edit.text(),
            "ph_speed": self.parameter_settings_page.ph_block.ph_speed_edit.text()
        }
        par["Antifoam"] = {
            "antifoam_op_time": self.parameter_settings_page.antifoam_block.line_edit.text(),
            "antifoam_disable_time": self.parameter_settings_page.antifoam_block.extra_edits["Disable Time (s):"].text(),
            "antifoam_speed": self.parameter_settings_page.antifoam_block.extra_edits["Speed (%):"].text()
        }
        prefs["ParameterSettings"] = par
        prefs["GassingOutConfig"] = self.kla_gassing_out_page.gassing_out_config

        # Collect Cascade and Graph configurations
        prefs["CascadeConfig"] = self.kla_cascade_page.gradient_widget.get_cascade_config()
        prefs["GraphConfig"] = self.kla_cascade_page.gradient_widget.get_graph_config()
        prefs["KlaInteractivePoints"] = self.kla_cascade_page.interactive_kla_widget.get_points()

        # Save PIDConfig from GradientAscendWidget, if available
        pid_cfg = getattr(self.kla_cascade_page.gradient_widget, "pid_config", None)
        if pid_cfg:
            # Force values to float before saving
            prefs["PIDConfig"] = {
                "C_star": float(pid_cfg.get("C_star", 100.0)),
                "Kp": float(pid_cfg.get("Kp", 0.01)),
                "Ki": float(pid_cfg.get("Ki", 0.0)),
                "Kd": float(pid_cfg.get("Kd", 0.0))
            }

        return prefs

    def apply_preferences(self, prefs):
        # Aplica as preferências de configurações
        if "Configurations" in prefs:
            conf = prefs["Configurations"]
            self.configurations_page.ip_edit.setText(conf.get("ip_edit", "192.168.4.1"))
            self.configurations_page.data_delay_edit.setText(conf.get("data_delay_edit", "1000"))
            self.configurations_page.oxy_cal_a.setText(conf.get("oxy_cal_a", "0.0305473419314"))
            self.configurations_page.oxy_cal_b.setText(conf.get("oxy_cal_b", "-25.09136520919"))
            # Aqui a interface é atualizada com os valores de calibração de pH salvos em "Configurations"
            self.configurations_page.ph_cal_slope.setText(conf.get("ph_cal_slope", "1.0"))
            self.configurations_page.ph_cal_intercept.setText(conf.get("ph_cal_intercept", "0.0"))
            self.configurations_page.usb_baud_rate = conf.get("baud_rate_edit", "115200")
            self.configurations_page.usb_data_bits = conf.get("data_bits_edit", "8")
            self.configurations_page.usb_stop_bits = conf.get("stop_bits_edit", "1")
            self.configurations_page.usb_parity = conf.get("parity_edit", "None")
            theme = conf.get("theme_combo", "Dark")
            self.configurations_page.theme_combo.setCurrentText(theme)
            self.configurations_page.change_theme(theme)
            
            # Atualiza a calibração de oxigênio
            try:
                a = float(self.configurations_page.oxy_cal_a.text())
                b = float(self.configurations_page.oxy_cal_b.text())
                self.comm_handler.set_oxygen_calibration(a, b)
            except:
                pass
            # Atualiza a calibração de pH usando os valores unificados
            try:
                slope = float(self.configurations_page.ph_cal_slope.text())
                intercept = float(self.configurations_page.ph_cal_intercept.text())
                self.comm_handler.set_pH_calibration(slope, intercept)
            except:
                pass

        # Apply Cascade and Graph configurations
        if "CascadeConfig" in prefs:
            self.kla_cascade_page.gradient_widget.set_cascade_config(prefs["CascadeConfig"])
        if "GraphConfig" in prefs:
            self.kla_cascade_page.gradient_widget.set_graph_config(prefs["GraphConfig"])
        if "KlaInteractivePoints" in prefs:
            points = prefs["KlaInteractivePoints"]
            self.kla_cascade_page.interactive_kla_widget.set_points(points)

        # Apply PIDConfig preferences
        if "PIDConfig" in self.preferences:
            # This loop is for the main_window's internal copy, it can remain
            for k in ["C_star", "Kp", "Ki", "Kd"]:
                try:
                    self.preferences["PIDConfig"][k] = float(self.preferences["PIDConfig"][k])
                except:
                    self.preferences["PIDConfig"][k] = 0.0

        self.kla_cascade_page.gradient_widget.set_pid_config(self.preferences["PIDConfig"])

        # Aplica as preferências da ParameterSettingsPage
        if "ParameterSettings" in prefs:
            par = prefs["ParameterSettings"]
            if "Temperature" in par:
                self.parameter_settings_page.temp_block.line_edit.setText(par["Temperature"].get("line_edit", "25"))
            if "Motor" in par:
                self.parameter_settings_page.motor_block.line_edit.setText(par["Motor"].get("line_edit", "100"))
            if "Pressure" in par:
                self.parameter_settings_page.pressure_block.line_edit.setText(par["Pressure"].get("line_edit", "100"))
            if "Oxygen" in par:
                self.parameter_settings_page.oxy_block.line_edit.setText(par["Oxygen"].get("line_edit", "50"))
            if "Flowmeter" in par:
                self.parameter_settings_page.flow_block.line_edit.setText(par["Flowmeter"].get("line_edit", "5"))
            if "Distance" in par:
                self.parameter_settings_page.distance_block.line_edit.setText(par["Distance"].get("line_edit", "100"))
            if "pH" in par:
                ph = par["pH"]
                self.parameter_settings_page.ph_block.ph_setpoint_edit.setText(ph.get("ph_setpoint", "7"))
                self.parameter_settings_page.ph_block.ph_error_edit.setText(ph.get("ph_error", "0.17"))
                self.parameter_settings_page.ph_block.ph_op_time_edit.setText(ph.get("ph_op_time", "10"))
                self.parameter_settings_page.ph_block.ph_disable_time_edit.setText(ph.get("ph_disable_time", "10"))
                self.parameter_settings_page.ph_block.ph_speed_edit.setText(ph.get("ph_speed", "99"))
            if "Antifoam" in par:
                antifoam = par["Antifoam"]
                self.parameter_settings_page.antifoam_block.line_edit.setText(antifoam.get("antifoam_op_time", "5"))
                self.parameter_settings_page.antifoam_block.extra_edits["Disable Time (s):"].setText(antifoam.get("antifoam_disable_time", "20"))
                self.parameter_settings_page.antifoam_block.extra_edits["Speed (%):"].setText(antifoam.get("antifoam_speed", "99"))
        
        if "GassingOutConfig" in prefs:
            self.kla_gassing_out_page.gassing_out_config = prefs["GassingOutConfig"]
            self.kla_gassing_out_page._apply_config() # Apply the loaded settings
                
    def setup_auto_save(self):
        for le in self.findChildren(QLineEdit):
            le.editingFinished.connect(self.on_editing_finished)
        if hasattr(self, 'kla_cascade_page'):
            self.kla_cascade_page.interactive_kla_widget.points_changed.connect(self.on_editing_finished)
            
    def on_editing_finished(self):
        prefs = self.collect_preferences()
        save_preferences(prefs)
    
    def enable_kla_cascade(self):
        self.parameter_settings_page.oxy_kla_cascade_checkbox.setEnabled(True)
    
    def update_graphs_tab_state(self):
        active = any([
            self.parameter_settings_page.temp_block.checkbox.isChecked(),
            self.parameter_settings_page.motor_block.checkbox.isChecked(),
            self.parameter_settings_page.ph_block.checkbox.isChecked(),
            self.parameter_settings_page.oxy_block.checkbox.isChecked(),
            self.parameter_settings_page.antifoam_block.checkbox.isChecked(),
            self.parameter_settings_page.pressure_block.checkbox.isChecked(),
            self.parameter_settings_page.flow_block.checkbox.isChecked(),
            self.parameter_settings_page.distance_block.checkbox.isChecked()
        ])
        graphs_index = self.main_tab_widget.indexOf(self.graphs_page)
        self.main_tab_widget.setTabEnabled(graphs_index, active)
    
    def connect_parameter_checkbox_signals(self):
        checkboxes = [
            self.parameter_settings_page.temp_block.checkbox,
            self.parameter_settings_page.motor_block.checkbox,
            self.parameter_settings_page.ph_block.checkbox,
            self.parameter_settings_page.oxy_block.checkbox,
            self.parameter_settings_page.antifoam_block.checkbox,
            self.parameter_settings_page.pressure_block.checkbox,
            self.parameter_settings_page.flow_block.checkbox,
            self.parameter_settings_page.distance_block.checkbox
        ]
        for cb in checkboxes:
            cb.toggled.connect(self.update_graphs_tab_state)
    
    def showEvent(self, event):
        self.update_graphs_tab_state()
        self.parameter_settings_page.update_all_block_styles()
        super().showEvent(event)

if __name__ == "__main__":
    app = QApplication(sys.argv)
    app.setFont(QFont("Roboto", 10))
    window = MainWindow()
    window.show()
    sys.exit(app.exec_())
