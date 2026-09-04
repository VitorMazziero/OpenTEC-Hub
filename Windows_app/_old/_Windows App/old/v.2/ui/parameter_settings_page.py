#!/usr/bin/env python
# -*- coding: utf-8 -*-
from PyQt5.QtWidgets import QWidget, QVBoxLayout, QGridLayout, QGroupBox, QLabel, QLineEdit, QCheckBox, QSpacerItem, QSizePolicy
from . import UI_CONFIG  # Import global UI configuration

class ParameterSettingsPage(QWidget):
    def __init__(self, comm_handler):
        super().__init__()
        self.comm_handler = comm_handler
        # Reference to MainWindow (set later by MainWindow)
        self.main_window = None

        main_layout = QVBoxLayout()
        main_layout.setContentsMargins(10, 10, 10, 10)
        main_layout.setSpacing(5)
        grid = QGridLayout()
        grid.setSpacing(5)

        # Create blocks with modern style
        self.temp_block = self.create_block(
            title="Temperatura",
            checkbox_text="Controle de Temperatura",
            label_text="Setpoint (°C):",
            default_value="25",
            send_callback=self.send_temperature,
            activation_color="red"
        )
        self.motor_block = self.create_block(
            title="Motor",
            checkbox_text="Controle do Motor",
            label_text="RPM:",
            default_value="100",
            send_callback=self.send_motor,
            activation_color="green"
        )
        self.pressure_block = self.create_block(
            title="Pressão",
            checkbox_text="Controle de Pressão",
            label_text="Setpoint (mmHg):",
            default_value="100",
            send_callback=self.send_pressure,
            activation_color="cyan"
        )
        self.oxy_block = self.create_block(
            title="Oxigênio",
            checkbox_text="Monitoramento de O2",
            label_text="Setpoint (%):",
            default_value="50",
            send_callback=self.send_oxygen,
            activation_color="magenta"
        )
        self.oxy_kla_cascade_checkbox = QCheckBox("Cascata kLa")
        self.oxy_block.layout().insertWidget(self.oxy_block.layout().count() - 1, self.oxy_kla_cascade_checkbox)
        self.oxy_kla_cascade_checkbox.toggled.connect(self.on_kla_cascade_toggled)
        self.oxy_kla_cascade_checkbox.setEnabled(False)
        
        self.flow_block = self.create_block(
            title="Fluxômetro",
            checkbox_text="Controle de vazão de ar",
            label_text="Setpoint:",
            default_value="1",
            send_callback=self.send_flowmeter,
            activation_color="yellow",
            extra_fields=[("Max Flow:", "50")]
        )
        self.flow_valve_checkbox = QCheckBox("Controle da Válvula 1")
        self.flow_block.layout().insertWidget(self.flow_block.layout().count() - 1, self.flow_valve_checkbox)
        self.flow_valve_checkbox.toggled.connect(self.on_flow_valve_toggled)

        self.distance_block = self.create_block(
            title="Sensor de Distância",
            checkbox_text="Ativar Sensor",
            label_text="Distância Mínima (cm):",
            default_value="100",
            send_callback=self.send_distance_sensor,
            activation_color="black"
        )

        # Organize blocks into groups
        group1 = QVBoxLayout()
        group1.setSpacing(5)
        group1.setContentsMargins(0, 0, 0, 0) 
        group1.addWidget(self.temp_block)
        group1.addWidget(self.oxy_block)

        group2 = QVBoxLayout()
        group2.setSpacing(5)
        group2.setContentsMargins(0, 0, 0, 0) 
        group2.addWidget(self.motor_block)
        group2.addWidget(self.flow_block)

        group3 = QVBoxLayout()
        group3.setSpacing(5)
        group3.setContentsMargins(0, 0, 0, 0) 
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
            title="Antiespumante",
            checkbox_text="Bomba de Antiespuma",
            label_text="Tempo de Operação (s):",
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

        # Connect signals for automatic field updates to preferences
        self.setup_field_signals()

    def setup_field_signals(self):
        for le in self.findChildren(QLineEdit):
            le.editingFinished.connect(self.on_field_changed)
        for cb in self.findChildren(QCheckBox):
            cb.stateChanged.connect(self.on_field_changed)

    def on_field_changed(self):
        if self.window() is not None and hasattr(self.window(), "on_editing_finished"):
            self.window().on_editing_finished()

    def on_kla_cascade_toggled(self, checked):
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
    
    def send_motor(self):
        # Updated: update the motor value instantly
        text = self.replace_comma(self.motor_block.line_edit.text())
        try:
            value = int(float(text))
        except ValueError:
            value = 100
        command = {"motorSetpoint": value}
        self.comm_handler.send_command(command)
        self.main_window.current_motor_rpm = value

    def get_current_theme(self):
        if self.main_window is not None and hasattr(self.main_window, "configurations_page"):
            return self.main_window.configurations_page.theme_combo.currentText()
        else:
            return "Light"

    def get_block_style(self, active, activation_color):
        theme = self.get_current_theme()
        if theme == "Dark":
            border_color = activation_color if active else "#555555"
            style = f"""
                QGroupBox {{
                    background-color: transparent;
                    color: #f0f0f0;
                    border: 2px solid {border_color};
                    border-radius: 10px;
                    padding: 5px;
                    margin: 5px;
                }}
                QGroupBox::title {{
                    subcontrol-origin: margin;
                    subcontrol-position: top left;
                    padding: 0 5px;
                    font-size: 14pt;
                    font-weight: bold;
                    color: #f0f0f0;
                }}
            """
        else:
            border_color = activation_color if active else "#cccccc"
            style = f"""
                QGroupBox {{
                    background-color: #f9f9f9;
                    color: #000000;
                    border: 2px solid {border_color};
                    border-radius: 10px;
                    padding: 5px;
                    margin: 5px;
                }}
                QGroupBox::title {{
                    subcontrol-origin: margin;
                    subcontrol-position: top left;
                    padding: 0 5px;
                    font-size: 14pt;
                    font-weight: bold;
                    color: #000000;
                }}
            """
        return style

    def update_block_style(self, group, active, activation_color):
        group.setStyleSheet(self.get_block_style(active, activation_color))

    def update_all_block_styles(self):
        blocks = [
            self.temp_block,
            self.motor_block,
            self.pressure_block,
            self.oxy_block,
            self.flow_block,
            self.distance_block,
            self.ph_block,
            self.nutri_block,
            self.antifoam_block
        ]
        for block in blocks:
            activation_color = getattr(block, "activation_color", "#cccccc")
            active = block.checkbox.isChecked()
            self.update_block_style(block, active, activation_color)

    def create_block(self, title, checkbox_text, label_text, default_value, send_callback,
                     activation_color, extra_fields=None, block_size=UI_CONFIG["block_size"]):
        group = QGroupBox(title)
        group.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        group.sizeHint = lambda: block_size
        group.activation_color = activation_color
        
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

        layout.addItem(QSpacerItem(10, 10, QSizePolicy.Minimum, QSizePolicy.Expanding))

        group.setLayout(layout)
        group.checkbox = checkbox
        group.line_edit = line_edit
        group.extra_edits = extra_edits

        checkbox.toggled.connect(lambda checked, grp=group, col=activation_color, callback=send_callback:
                                   (self.update_block_style(grp, checked, col), callback()))
        return group

    def create_ph_block(self, activation_color, block_size=UI_CONFIG["block_size"]):
        group = QGroupBox("Controle de pH")
        group.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        group.sizeHint = lambda: block_size
        group.activation_color = activation_color

        layout = QVBoxLayout()
        layout.setSpacing(5)
        group.checkbox = QCheckBox("Controle de pH")
        layout.addWidget(group.checkbox)
        
        layout.addWidget(QLabel("Setpoint:"))
        group.ph_setpoint_edit = QLineEdit("7")
        layout.addWidget(group.ph_setpoint_edit)
        group.ph_setpoint_edit.returnPressed.connect(self.send_ph)
        
        layout.addWidget(QLabel("Erro Inativo:"))
        group.ph_error_edit = QLineEdit("0.17")
        layout.addWidget(group.ph_error_edit)
        group.ph_error_edit.returnPressed.connect(self.send_ph)
        
        layout.addWidget(QLabel("Tempo de Operação (s):"))
        group.ph_op_time_edit = QLineEdit("5")
        layout.addWidget(group.ph_op_time_edit)
        group.ph_op_time_edit.returnPressed.connect(self.send_ph)
        
        layout.addWidget(QLabel("Tempo de Desativação (s):"))
        group.ph_disable_time_edit = QLineEdit("20")
        layout.addWidget(group.ph_disable_time_edit)
        group.ph_disable_time_edit.returnPressed.connect(self.send_ph)
        
        layout.addWidget(QLabel("Velocidade da Bomba (%):"))
        group.ph_speed_edit = QLineEdit("50")
        layout.addWidget(group.ph_speed_edit)
        group.ph_speed_edit.returnPressed.connect(self.send_ph)
        
        layout.addItem(QSpacerItem(10, 10, QSizePolicy.Minimum, QSizePolicy.Expanding))
        group.setLayout(layout)
        group.checkbox.toggled.connect(lambda checked, grp=group, col=activation_color, callback=self.send_ph:
                                       (self.update_block_style(grp, checked, col), callback()))
        return group

    def create_nutrient_block(self, activation_color, block_size=UI_CONFIG["block_size"]):
        group = QGroupBox("Bomba de Nutriente")
        group.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        group.sizeHint = lambda: block_size
        group.activation_color = activation_color

        layout = QVBoxLayout()
        layout.setSpacing(5)
        group.checkbox = QCheckBox("Ativar Nutriente")
        layout.addWidget(group.checkbox)
        
        layout.addWidget(QLabel("Tempo de Operação (s):"))
        group.nutri_op_time_edit = QLineEdit("999")
        layout.addWidget(group.nutri_op_time_edit)
        group.nutri_op_time_edit.returnPressed.connect(self.send_nutrient)
        
        layout.addWidget(QLabel("Tempo de Desativação (s):"))
        group.nutri_disable_time_edit = QLineEdit("1")
        layout.addWidget(group.nutri_disable_time_edit)
        group.nutri_disable_time_edit.returnPressed.connect(self.send_nutrient)
        
        layout.addWidget(QLabel("Ciclo de Operação (min):"))
        group.nutri_op_cycle_edit = QLineEdit("500")
        layout.addWidget(group.nutri_op_cycle_edit)
        group.nutri_op_cycle_edit.returnPressed.connect(self.send_nutrient)
        
        layout.addWidget(QLabel("Ciclo de Desativação (min):"))
        group.nutri_disable_cycle_edit = QLineEdit("1")
        layout.addWidget(group.nutri_disable_cycle_edit)
        group.nutri_disable_cycle_edit.returnPressed.connect(self.send_nutrient)
        
        layout.addWidget(QLabel("Velocidade da Bomba (%):"))
        group.nutri_speed_edit = QLineEdit("99")
        layout.addWidget(group.nutri_speed_edit)
        group.nutri_speed_edit.returnPressed.connect(self.send_nutrient)
        
        layout.addItem(QSpacerItem(10, 10, QSizePolicy.Minimum, QSizePolicy.Expanding))
        group.setLayout(layout)
        group.checkbox.toggled.connect(lambda checked, grp=group, col=activation_color, callback=self.send_nutrient:
                                       (self.update_block_style(grp, checked, col), callback()))
        return group

    # Command sending methods (unchanged except for motor, which is updated immediately)
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
            value = max(50, min(value, 1100))
        else:
            value = 0
        command = {"motorSetpoint": value}
        self.comm_handler.send_command(command)
        self.main_window.current_motor_rpm = int(float(self.motor_block.line_edit.text()))

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
            disable_time = 5
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
            "pHIntensity": speed * 10
        }
        self.comm_handler.send_command(command)

    def send_oxygen(self):
        text = self.replace_comma(self.oxy_block.line_edit.text())
        try:
            value = float(text)
        except ValueError:
            value = 50
        if self.oxy_block.checkbox.isChecked():
            value = max(1, min(value, 150))
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
            valve2 = 1 if setpoint == 0 else 0
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
        except:
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
