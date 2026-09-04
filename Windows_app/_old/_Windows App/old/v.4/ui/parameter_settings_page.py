#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# parameter_settings_page.py

from PySide6.QtWidgets import (
    QWidget, QVBoxLayout, QGridLayout, QGroupBox, QLabel, QLineEdit,
    QCheckBox, QSpacerItem, QSizePolicy, QPushButton, QHBoxLayout,
    QDialog, QFormLayout, QDialogButtonBox, QStyle, QMessageBox
)
from PySide6.QtGui import QIntValidator
from PySide6.QtCore import Qt
from . import UI_CONFIG  # Import global UI configuration
import time
try:
    from .pump_mode_window import PumpModeWindow
except ImportError:
    print("Aviso: Não foi possível importar PumpModeWindow. Os modos da bomba não funcionarão.")
    PumpModeWindow = None # Define como None para evitar crash

try:
    from .flow_calibration_dialog import FlowCalibrationDialog
except ImportError:
    print("Aviso: flow_calibration_dialog não encontrado.")
    FlowCalibrationDialog = None

# --- Diálogo para Configurações da Cascata Simples ---
class SimpleCascadeSettingsDialog(QDialog):
    """
    Um diálogo pop-up para configurar os parâmetros avançados e PID
    para as cascatas simples de Agitação e Aeração.
    Layout 2x2.
    """
    def __init__(self, parent=None, 
                 original_agit_pid_edits=None, original_agit_adv_edits=None,
                 original_aer_pid_edits=None, original_aer_adv_edits=None):
        super().__init__(parent)
        self.setWindowTitle("Parâmetros da Cascata Simples")
        self.setModal(True)

        # 1. Armazena referências aos QLineEdits *originais*
        self.original_edits = {
            "agit_pid": original_agit_pid_edits, "agit_adv": original_agit_adv_edits,
            "aer_pid": original_aer_pid_edits, "aer_adv": original_aer_adv_edits
        }
        
        # Dicionário para guardar os edits *temporários* do diálogo
        self.temp_edits = {}

        main_layout = QVBoxLayout(self)
        grid_layout = QGridLayout()
        grid_layout.setSpacing(10)

        # --- Bloco de Agitação (Esquerda) ---
        adv_agit_group = self._create_group(
            "Cascata Agitação", 
            original_agit_adv_edits, 
            "agit_adv"
        )
        pid_agit_group = self._create_group(
            "PID Agitação", 
            original_agit_pid_edits, 
            "agit_pid"
        )
        
        grid_layout.addWidget(adv_agit_group, 0, 0)
        grid_layout.addWidget(pid_agit_group, 1, 0)

        # --- Bloco de Aeração (Direita) ---
        adv_aer_group = self._create_group(
            "Cascata Aeração", 
            original_aer_adv_edits, 
            "aer_adv"
        )
        pid_aer_group = self._create_group(
            "PID Aeração", 
            original_aer_pid_edits, 
            "aer_pid"
        )
        
        grid_layout.addWidget(adv_aer_group, 0, 1)
        grid_layout.addWidget(pid_aer_group, 1, 1)

        main_layout.addLayout(grid_layout)

        # --- Botões OK/Cancelar ---
        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel,
            Qt.Orientation.Horizontal,
            self
        )
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        main_layout.addWidget(buttons)

    def _create_group(self, title, original_edits_dict, temp_key_prefix):
        """Helper para criar um QGroupBox (PID ou Avançado)."""
        group = QGroupBox(title)
        layout = QFormLayout(group)
        self.temp_edits[temp_key_prefix] = {}

        if original_edits_dict:
            # Garante a ordem dos campos
            for key in original_edits_dict.keys():
                original_edit = original_edits_dict[key]
                temp_edit = QLineEdit()
                temp_edit.setText(original_edit.text()) # Copia valor
                
                layout.addRow(f"{key}:", temp_edit)
                self.temp_edits[temp_key_prefix][key] = temp_edit # Salva temp
        return group

    def accept(self):
        """Copia os valores de volta para os QLineEdits originais."""
        try:
            # Copia de volta
            for group_key in self.original_edits: # "agit_pid", "agit_adv", ...
                if self.original_edits[group_key]:
                    for field_key in self.original_edits[group_key]: # "Kp", "History Pts", ...
                        temp_val = self.temp_edits[group_key][field_key].text()
                        self.original_edits[group_key][field_key].setText(temp_val)
        except Exception as e:
            # Isso pode ser mostrado em um QMessageBox se preferir
            print(f"Erro ao salvar configurações do diálogo: {e}")
        
        super().accept()

class ParameterSettingsPage(QWidget):
    def __init__(self, comm_handler):
        super().__init__()
        self.comm_handler = comm_handler
        self.main_window = None  # Referência ao MainWindow será atribuída externamente
        self.flow_calibration_points = []

        # Layouts raiz
        main_layout = QVBoxLayout()
        main_layout.setContentsMargins(10, 10, 10, 10)
        main_layout.setSpacing(5)

        grid = QGridLayout()
        grid.setSpacing(5)

        # --- Conjuntos de QLineEdits para PIDs e Avançados (Agitação e Aeração) ---
        # (O código para self.pid_agit_... self.agit_pid_edits, etc. permanece idêntico)
        # --- Conjuntos de QLineEdits para PIDs e Avançados: Agitação ---
        self.pid_agit_kp_edit = QLineEdit("0.1")
        self.pid_agit_ki_edit = QLineEdit("0.002")
        self.pid_agit_kd_edit = QLineEdit("0.75")

        self.adv_agit_hist_pts_edit = QLineEdit("30")
        self.adv_agit_median_win_edit = QLineEdit("5")
        self.adv_agit_min_pts_reg_edit = QLineEdit("10")
        self.adv_agit_pred_horiz_edit = QLineEdit("30.0")
        self.adv_agit_outer_gain_edit = QLineEdit("0.1")
        self.adv_agit_deriv_tau_edit = QLineEdit("40.0")
        self.adv_agit_max_int_edit = QLineEdit("200.0")
        self.adv_agit_min_int_edit = QLineEdit("-200.0")
        self.adv_agit_int_win_edit = QLineEdit("60")

        self.agit_pid_edits = {
            "Kp": self.pid_agit_kp_edit,
            "Ki": self.pid_agit_ki_edit,
            "Kd": self.pid_agit_kd_edit,
        }
        self.agit_adv_edits = {
            "History Pts": self.adv_agit_hist_pts_edit,
            "Median Filter Win": self.adv_agit_median_win_edit,
            "Min Pts Regression": self.adv_agit_min_pts_reg_edit,
            "Prediction Horizon (s)": self.adv_agit_pred_horiz_edit,
            "Outer Loop Gain": self.adv_agit_outer_gain_edit,
            "Derivative Tau (s)": self.adv_agit_deriv_tau_edit,
            "Max Integral": self.adv_agit_max_int_edit,
            "Min Integral": self.adv_agit_min_int_edit,
            "Integral Window": self.adv_agit_int_win_edit,
        }

        # --- Conjuntos de QLineEdits para PIDs e Avançados: Aeração ---
        self.pid_aer_kp_edit = QLineEdit("0.001")
        self.pid_aer_ki_edit = QLineEdit("0.0002")
        self.pid_aer_kd_edit = QLineEdit("0.0075")

        self.adv_aer_hist_pts_edit = QLineEdit("30")
        self.adv_aer_median_win_edit = QLineEdit("5")
        self.adv_aer_min_pts_reg_edit = QLineEdit("10")
        self.adv_aer_pred_horiz_edit = QLineEdit("30.0")
        self.adv_aer_outer_gain_edit = QLineEdit("0.1")
        self.adv_aer_deriv_tau_edit = QLineEdit("40.0")
        self.adv_aer_max_int_edit = QLineEdit("200.0")
        self.adv_aer_min_int_edit = QLineEdit("-200.0")
        self.adv_aer_int_win_edit = QLineEdit("60")

        self.aer_pid_edits = {
            "Kp": self.pid_aer_kp_edit,
            "Ki": self.pid_aer_ki_edit,
            "Kd": self.pid_aer_kd_edit,
        }
        self.aer_adv_edits = {
            "History Pts": self.adv_aer_hist_pts_edit,
            "Median Filter Win": self.adv_aer_median_win_edit,
            "Min Pts Regression": self.adv_aer_min_pts_reg_edit,
            "Prediction Horizon (s)": self.adv_aer_pred_horiz_edit,
            "Outer Loop Gain": self.adv_aer_outer_gain_edit,
            "Derivative Tau (s)": self.adv_aer_deriv_tau_edit,
            "Max Integral": self.adv_aer_max_int_edit,
            "Min Integral": self.adv_aer_min_int_edit,
            "Integral Window": self.adv_aer_int_win_edit,
        }

        # --- Blocos padrão usando create_block ---

        # a. Bloco de Temperatura (linha única)
        self.temp_block = self.create_block(
            title="Temperatura",
            checkbox_text="Controle de Temperatura",
            label_text="Setpoint (°C):",
            default_value="25",
            send_callback=self.send_temperature,
            activation_color="red",
            setpoint_horizontal=True  # MUDANÇA
        )
        
        # b. Bloco de Pressão (linha única)
        self.pressure_block = self.create_block(
            title="Pressão",
            checkbox_text="Controle de Pressão",
            label_text="Setpoint (mmHg):",
            default_value="100",
            send_callback=self.send_pressure,
            activation_color="cyan",
            setpoint_horizontal=True  # MUDANÇA
        )

        self.motor_block = self.create_block(
            title="Motor",
            checkbox_text="Controle do Motor",
            label_text="Setpoint (RPM):",
            default_value="100",
            send_callback=self.send_motor,
            activation_color="green",
        )

        self._motor_last_valid = 100
        self.motor_block.line_edit.setValidator(QIntValidator(50, 1000, self))

        # --- Bloco de Oxigênio (Layout inalterado) ---
        self.oxy_block = self.create_block(
            title="Oxigênio",
            checkbox_text="Monitoramento de O2",
            label_text="Setpoint (%):",
            default_value="50",
            send_callback=self.send_oxygen,
            activation_color="magenta",
        )
        oxy_layout = self.oxy_block.layout()
        self.oxy_kla_cascade_checkbox = QCheckBox("Cascata kLa")
        self.oxy_kla_cascade_checkbox.setEnabled(False)
        oxy_layout.insertWidget(oxy_layout.count() - 1, self.oxy_kla_cascade_checkbox)
        self.oxy_agit_cascade_checkbox = QCheckBox("Cascata Agitação")
        oxy_layout.insertWidget(oxy_layout.count() - 1, self.oxy_agit_cascade_checkbox)
        self.oxy_aer_cascade_checkbox = QCheckBox("Cascata Aeração")
        oxy_layout.insertWidget(oxy_layout.count() - 1, self.oxy_aer_cascade_checkbox)
        self.oxy_reset_i_btn = QPushButton("Zerar Integral (O₂)")
        self.oxy_reset_i_btn.setToolTip("Zera o termo integral do PID do controle de O₂.")
        self.oxy_reset_i_btn.setEnabled(False)
        self.simple_cascade_settings_btn = QPushButton()
        try:
            icon = self.style().standardIcon(QStyle.StandardPixmap.SP_FileDialogDetailedView)
        except Exception:
            icon = self.style().standardIcon(QStyle.StandardPixmap.SP_DialogOkButton)
        self.simple_cascade_settings_btn.setIcon(icon)
        self.simple_cascade_settings_btn.setFixedSize(45, 20)
        self.simple_cascade_settings_btn.setToolTip("Abrir Parâmetros da Cascata Simples")
        self.simple_cascade_settings_btn.clicked.connect(self.open_simple_cascade_settings)
        oxy_final_row = QHBoxLayout()
        oxy_final_row.addWidget(self.oxy_reset_i_btn)
        oxy_final_row.addWidget(self.simple_cascade_settings_btn)
        oxy_final_row.addStretch()
        oxy_layout.insertLayout(oxy_layout.count() - 1, oxy_final_row)
        self.oxy_kla_cascade_checkbox.toggled.connect(self.on_kla_cascade_toggled)
        self.oxy_reset_i_btn.clicked.connect(self.on_reset_oxygen_integral_clicked)
        self.oxy_kla_cascade_checkbox.toggled.connect(self.oxy_reset_i_btn.setEnabled)
        self.oxy_agit_cascade_checkbox.toggled.connect(self.on_agit_cascade_toggled)
        self.oxy_aer_cascade_checkbox.toggled.connect(self.on_aer_cascade_toggled)

        self.flow_block = self.create_block(
            title="Fluxômetro",
            checkbox_text="Controle da Vazão de Ar",
            label_text="Setpoint (L/min):",
            default_value="1",
            send_callback=self.send_flowmeter,
            activation_color="yellow",
            extra_fields=[],
            setpoint_horizontal=True # Keep setpoint on same line as label
        )
        flow_layout = self.flow_block.layout()

        # 1. Row for Max Flow
        max_flow_row = QHBoxLayout()
        max_flow_label = QLabel("Vazão Máxima:")
        self.flow_max_flow_edit = QLineEdit("50")
        self.flow_max_flow_edit.returnPressed.connect(self.send_flowmeter)
        self.flow_block.extra_edits = {"Max Flow:": self.flow_max_flow_edit}
        max_flow_row.addWidget(max_flow_label)
        max_flow_row.addWidget(self.flow_max_flow_edit)
        flow_layout.insertLayout(flow_layout.count() - 1, max_flow_row)

        # 2. Row for Valves (Valve 1 and Valve 2)
        self.flow_valve_checkbox = QCheckBox("Válvula 1")
        self.flow_valve_checkbox.toggled.connect(self.on_flow_valve_toggled)
        
        self.flow_valve2_checkbox = QCheckBox("Válvula 2") # NEW Checkbox
        self.flow_valve2_checkbox.toggled.connect(self.on_flow_valve2_toggled)

        valve_row = QHBoxLayout()
        valve_row.addWidget(self.flow_valve_checkbox)
        valve_row.addWidget(self.flow_valve2_checkbox) 
        valve_row.addStretch()
        flow_layout.insertLayout(flow_layout.count() - 1, valve_row)

        # 3. Row for Cascade Range (Unchanged)
        flow_cascade_label_row = QHBoxLayout()
        flow_cascade_label_row.addWidget(QLabel("Alcance Cascata:"))
        flow_cascade_label_row.addStretch()
        flow_layout.insertLayout(flow_layout.count() - 1, flow_cascade_label_row)
        flow_cascade_inputs_row = QHBoxLayout()
        self.flow_cascade_min_edit = QLineEdit("1")
        self.flow_cascade_max_edit = QLineEdit("20")
        flow_cascade_inputs_row.addWidget(self.flow_cascade_min_edit)
        flow_cascade_inputs_row.addWidget(QLabel("-"))
        flow_cascade_inputs_row.addWidget(self.flow_cascade_max_edit)
        flow_layout.insertLayout(flow_layout.count() - 1, flow_cascade_inputs_row)

        # 4. Row for Calibration Button (NEW)
        calib_layout = QHBoxLayout()
        calib_label = QLabel("Calibração:")
        self.calib_btn = QPushButton()
        try:
             # Use a generic icon like "Detailed View"
             icon_cal = self.style().standardIcon(QStyle.StandardPixmap.SP_FileDialogDetailedView)
             self.calib_btn.setIcon(icon_cal)
        except:
             self.calib_btn.setText("Calib")
        
        self.calib_btn.setFixedSize(40, 25)
        self.calib_btn.setToolTip("Abrir Tabela de Calibração")
        self.calib_btn.clicked.connect(self.open_flow_calibration)
        
        calib_layout.addWidget(calib_label)
        calib_layout.addWidget(self.calib_btn)
        calib_layout.addStretch()
        flow_layout.insertLayout(flow_layout.count() - 1, calib_layout)

        # --- Alcance Cascata do Motor (Layout inalterado) ---
        motor_cascade_label_row = QHBoxLayout()
        motor_cascade_label_row.addWidget(QLabel("Alcance Cascata:"))
        motor_cascade_label_row.addStretch()
        self.motor_block.layout().insertLayout(self.motor_block.layout().count() - 1, motor_cascade_label_row)
        motor_cascade_inputs_row = QHBoxLayout()
        self.motor_cascade_min_edit = QLineEdit("200")
        self.motor_cascade_max_edit = QLineEdit("1000")
        motor_cascade_inputs_row.addWidget(self.motor_cascade_min_edit)
        motor_cascade_inputs_row.addWidget(QLabel("-"))
        motor_cascade_inputs_row.addWidget(self.motor_cascade_max_edit)
        self.motor_block.layout().insertLayout(self.motor_block.layout().count() - 1, motor_cascade_inputs_row)

        # --- Demais blocos padrão ---
        self.distance_block = self.create_block(
            title="Sensor de Distância",
            checkbox_text="Ativar Sensor",
            label_text="Min Distância (cm):",
            default_value="100",
            send_callback=self.send_distance_sensor,
            activation_color="black",
            extra_fields=[("Delay Início (s):", "1"), ("Pulso ON (s):", "1"), ("Intervalo (s):", "5")],
            setpoint_horizontal=True 
        )
        
        # a. Blocos de pH e Nutriente (agora com layout horizontal)
        self.ph_block = self.create_ph_block(activation_color="blue")
        self.nutri_block = self.create_nutrient_block(activation_color="purple")
        
        self.antifoam_block = self.create_block(
            title="Antiespumante",
            checkbox_text="Bomba de Antiespuma",
            label_text="Tempo Operando (s):",
            default_value="5",
            send_callback=self.send_antifoam,
            activation_color="orange",
            extra_fields=[("Tempo Desativado (s):", "20"), ("Velocidade (%):", "99")],
            setpoint_horizontal=True  # make label and input share the same line
        )

        self.agitator_block = self.create_agitator_block(activation_color="teal")

        # d. Bloco de Biomassa
        self.biomass_block = self.create_biomass_block(activation_color="#00796B") # Teal 700

        # --- Agrupamento em colunas para o grid (REORGANIZADO) ---
        
        # Coluna 1: Temp, Pressão, Oxigênio
        col1 = QVBoxLayout()
        col1.setSpacing(5)
        col1.setContentsMargins(0, 0, 0, 0)
        col1.addWidget(self.temp_block)
        col1.addWidget(self.pressure_block)
        col1.addWidget(self.oxy_block)
        col1w = QWidget(); col1w.setLayout(col1)

        # Coluna 2: Motor, Fluxômetro
        col2 = QVBoxLayout()
        col2.setSpacing(5)
        col2.setContentsMargins(0, 0, 0, 0)
        col2.addWidget(self.motor_block)
        col2.addWidget(self.flow_block)
        col2w = QWidget(); col2w.setLayout(col2)
        
        # Coluna 3: pH, Nutriente, Distância
        col3 = QVBoxLayout()
        col3.setSpacing(5)
        col3.setContentsMargins(0, 0, 0, 0)
        col3.addWidget(self.ph_block)
        col3.addWidget(self.nutri_block)
        col3w = QWidget(); col3w.setLayout(col3)

        # Coluna 4: Antiespumante, Frasco Agitador
        col4 = QVBoxLayout()
        col4.setSpacing(5)
        col4.setContentsMargins(0, 0, 0, 0)
        col4.addWidget(self.antifoam_block)
        col4.addWidget(self.agitator_block)
        col4.addWidget(self.distance_block)
        col4w = QWidget(); col4w.setLayout(col4)

        # Coluna 5: Biomassa, Bomba Externa (Placeholder)
        col5 = QVBoxLayout()
        col5.setSpacing(5)
        col5.setContentsMargins(0, 0, 0, 0)
        col5.addWidget(self.biomass_block)
        
        # Criar placeholder
        self.extern_pump_block = self.create_extern_pump_block(
            activation_color="#FFB300" # Laranja/Ambar
        )
        col5.addWidget(self.extern_pump_block)
        self.extern_pump_block.gas_prop_checkbox.toggled.connect(self.on_gas_proporcional_toggled)
        col5w = QWidget(); col5w.setLayout(col5)

        # Adiciona colunas ao grid
        grid.addWidget(col1w, 0, 0)
        grid.addWidget(col2w, 0, 1)
        grid.addWidget(col3w, 0, 2)
        grid.addWidget(col4w, 0, 3)
        grid.addWidget(col5w, 0, 4)

        main_layout.addLayout(grid)
        self.setLayout(main_layout)

        # Conexões automáticas para atualização de preferências
        self.setup_field_signals()

    def open_simple_cascade_settings(self):
        """
        Abre o diálogo de configurações da cascata simples,
        passando as *referências* aos QLineEdits originais.
        """
        dialog = SimpleCascadeSettingsDialog(
            self,
            original_agit_pid_edits=self.agit_pid_edits,
            original_agit_adv_edits=self.agit_adv_edits,
            original_aer_pid_edits=self.aer_pid_edits,
            original_aer_adv_edits=self.aer_adv_edits
        )
        
        if dialog.exec() == QDialog.DialogCode.Accepted:
            # Os valores foram copiados de volta para os QLineEdits originais.
            # Apenas dispara o on_field_changed para salvar as preferências.
            self.on_field_changed()
            if self.main_window and hasattr(self.main_window, "agit_cascade_control"):
                # Força os controladores a recarregar os valores de PID
                self.main_window.agit_cascade_control.reload_pid_config()
                self.main_window.aer_cascade_control.reload_pid_config()

    def on_reset_oxygen_integral_clicked(self):
        if self.main_window is not None and hasattr(self.main_window, "reset_oxygen_integrator"):
            self.main_window.reset_oxygen_integrator()

    def setup_field_signals(self):
        # Adiciona todos os QLineEdits padrão (que estão em layouts)
        for le in self.findChildren(QLineEdit):
            le.editingFinished.connect(self.on_field_changed)
        
        all_dialog_edits = [
            *self.agit_pid_edits.values(), *self.agit_adv_edits.values(),
            *self.aer_pid_edits.values(), *self.aer_adv_edits.values()
        ]
        for edit in all_dialog_edits:
            edit.editingFinished.connect(self.on_field_changed)

        for cb in self.findChildren(QCheckBox):
            cb.stateChanged.connect(self.on_field_changed)

    def on_field_changed(self):
        if self.window() is not None and hasattr(self.window(), "on_editing_finished"):
            self.window().on_editing_finished()

    def on_gas_proporcional_toggled(self, checked):
        """
        Controla a UI do fluxômetro quando o gás proporcional é ativado.
        """
        if checked:
            # Força a ativação do controle do fluxômetro
            if not self.flow_block.checkbox.isChecked():
                self.flow_block.checkbox.setChecked(True)
            
            # Desabilita a entrada manual do setpoint
            self.flow_block.line_edit.setEnabled(False)
            self._log("Controle de vazão proporcional (Bomba Externa) ativado.")
            command = {"pumpComm": 1}
            self.comm_handler.send_command(command)
        else:
            # Habilita a entrada manual do setpoint
            self.flow_block.line_edit.setEnabled(True)
            self._log("Controle de vazão proporcional desativado.")
            command = {"pumpComm": 0}
            self.comm_handler.send_command(command)

    # --- Lógica de Exclusão Mútua e Ativação/Desativação ---
    def on_kla_cascade_toggled(self, checked):
        if checked:
            # Desativa as outras cascatas
            if self.oxy_agit_cascade_checkbox.isChecked():
                self.oxy_agit_cascade_checkbox.setChecked(False)
            if self.oxy_aer_cascade_checkbox.isChecked():
                self.oxy_aer_cascade_checkbox.setChecked(False)

            # Ativa os controles necessários
            if not self.oxy_block.checkbox.isChecked():
                self.oxy_block.checkbox.setChecked(True)
            if not self.motor_block.checkbox.isChecked():
                self.motor_block.checkbox.setChecked(True)
            if not self.flow_block.checkbox.isChecked():
                self.flow_block.checkbox.setChecked(True)
        
        # Se kLa for ativada, desativa as entradas de setpoint
        self.motor_block.line_edit.setEnabled(not checked)
        self.flow_block.line_edit.setEnabled(not checked)
        self._log("Cascata kLa ativada.") if checked else self._log("Cascata kLa desativada.")

    def on_agit_cascade_toggled(self, checked):
        # 1d. Desativa a entrada de RPM
        self.motor_block.line_edit.setEnabled(not checked)
        
        if checked:
            # Ativa o "Controle do motor"
            if not self.motor_block.checkbox.isChecked():
                self.motor_block.checkbox.setChecked(True)
            # Ativa o "Monitoramento de O2"
            if not self.oxy_block.checkbox.isChecked():
                self.oxy_block.checkbox.setChecked(True)
                
            # Desativa as outras cascatas
            if self.oxy_kla_cascade_checkbox.isChecked():
                self.oxy_kla_cascade_checkbox.setChecked(False)
            if self.oxy_aer_cascade_checkbox.isChecked():
                self.oxy_aer_cascade_checkbox.setChecked(False)
                
            # Re-ativa a entrada de fluxo (caso a cascata de aeração a tenha desativado)
            if not self.flow_block.line_edit.isEnabled():
                 self.flow_block.line_edit.setEnabled(True)
            
            self._log("Cascata de agitação ativada.") if checked else self._log("Cascata de agitação desativada.")

    def on_aer_cascade_toggled(self, checked):
        # 1d. Desativa a entrada de L/min
        self.flow_block.line_edit.setEnabled(not checked)
        
        if checked:
            # Ativa o "Controle da vazão de ar"
            if not self.flow_block.checkbox.isChecked():
                self.flow_block.checkbox.setChecked(True)
            # Ativa o "Monitoramento de O2"
            if not self.oxy_block.checkbox.isChecked():
                self.oxy_block.checkbox.setChecked(True)
                
            # Desativa as outras cascatas
            if self.oxy_kla_cascade_checkbox.isChecked():
                self.oxy_kla_cascade_checkbox.setChecked(False)
            if self.oxy_agit_cascade_checkbox.isChecked():
                self.oxy_agit_cascade_checkbox.setChecked(False)
                
            # Re-ativa a entrada de RPM (caso a cascata de agitação a tenha desativado)
            if not self.motor_block.line_edit.isEnabled():
                self.motor_block.line_edit.setEnabled(True)

            self._log("Cascata de aeração ativada.") if checked else self._log("Cascata de aeração desativada.")

    def on_flow_valve_toggled(self, checked):
        command = {"valve_1": 1 if checked else 0}
        self.comm_handler.send_command(command)
    
    def open_flow_calibration(self):
        """Opens the flow calibration dialog with saved points."""
        if FlowCalibrationDialog is None:
            QMessageBox.critical(self, "Erro", "Módulo de calibração não carregado.")
            return
            
        # Pass the current stored points to the dialog
        dialog = FlowCalibrationDialog(self.comm_handler, initial_data=self.flow_calibration_points, parent=self)
        
        if dialog.exec() == QDialog.DialogCode.Accepted:
            # When user clicks Save/Send, we update our local storage
            # NOTE: We need to ensure FlowCalibrationDialog has a get_data() method
            if hasattr(dialog, 'get_data'):
                self.flow_calibration_points = dialog.get_data()
                self.on_field_changed() # Trigger auto-save to preferences
            else:
                print("Warning: FlowCalibrationDialog missing get_data method")

    def on_flow_valve2_toggled(self, checked):
        # Sends command for Valve 2 manually
        command = {"valve_2": 1 if checked else 0}
        self.comm_handler.send_command(command)
    
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
            self.antifoam_block,
            self.agitator_block,
            self.biomass_block,
            self.extern_pump_block
        ]
        for block in blocks:
            activation_color = getattr(block, "activation_color", "#cccccc")
            
            if block == self.agitator_block:
                active = block.agit_on_checkbox.isChecked() # Usar o checkbox correto
            else:
                active = block.checkbox.isChecked() # Padrão
                
            self.update_block_style(block, active, activation_color)

    def create_block(self, title, checkbox_text, label_text, default_value, send_callback,
                     activation_color, extra_fields=None, block_size=UI_CONFIG["block_size"],
                     setpoint_horizontal=False): # Novo parâmetro
        """
        Cria um bloco de parâmetros padrão.
        Se setpoint_horizontal=True, o label e o line_edit ficam na mesma linha.
        """
        group = QGroupBox(title)
        group.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        group.sizeHint = lambda: block_size
        group.activation_color = activation_color
        
        layout = QVBoxLayout()
        layout.setSpacing(5)

        checkbox = QCheckBox(checkbox_text)
        layout.addWidget(checkbox)

        line_edit = QLineEdit(default_value)
        line_edit.returnPressed.connect(send_callback)
        line_edit.editingFinished.connect(send_callback)

        if setpoint_horizontal:
            # Layout horizontal para label e setpoint
            setpoint_layout = QHBoxLayout()
            setpoint_layout.addWidget(QLabel(label_text))
            setpoint_layout.addWidget(line_edit)
            layout.addLayout(setpoint_layout)
        else:
            # Layout vertical padrão
            layout.addWidget(QLabel(label_text))
            layout.addWidget(line_edit)

        extra_edits = {}
        if extra_fields:
            # Usa QFormLayout para campos extras, é mais limpo
            form_layout = QFormLayout()
            form_layout.setSpacing(5)
            for lbl, val in extra_fields:
                le = QLineEdit(val)
                le.returnPressed.connect(send_callback)
                form_layout.addRow(QLabel(lbl), le)
                extra_edits[lbl] = le
            layout.addLayout(form_layout)

        layout.addItem(QSpacerItem(10, 10, QSizePolicy.Policy.Minimum, QSizePolicy.Policy.Expanding))

        group.setLayout(layout)
        group.checkbox = checkbox
        group.line_edit = line_edit
        group.extra_edits = extra_edits

        checkbox.toggled.connect(lambda checked, grp=group, col=activation_color, callback=send_callback:
                                     (self.update_block_style(grp, checked, col), callback()))
        return group

    def create_ph_block(self, activation_color, block_size=UI_CONFIG["block_size"]):
        group = QGroupBox("Controle de pH")
        group.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        group.sizeHint = lambda: block_size
        group.activation_color = activation_color

        layout = QVBoxLayout()
        layout.setSpacing(5)
        group.checkbox = QCheckBox("Controle de pH")
        layout.addWidget(group.checkbox)
        
        # Usa QFormLayout para alinhamento automático de label: entry
        form_layout = QFormLayout()
        form_layout.setSpacing(5)

        group.ph_setpoint_edit = QLineEdit("7")
        group.ph_setpoint_edit.returnPressed.connect(self.send_ph)
        form_layout.addRow("Setpoint:", group.ph_setpoint_edit)
        
        group.ph_error_edit = QLineEdit("0.17")
        group.ph_error_edit.returnPressed.connect(self.send_ph)
        form_layout.addRow("Erro Inativo:", group.ph_error_edit)
        
        group.ph_op_time_edit = QLineEdit("5")
        group.ph_op_time_edit.returnPressed.connect(self.send_ph)
        form_layout.addRow("Tempo Operando (s):", group.ph_op_time_edit)
        
        group.ph_disable_time_edit = QLineEdit("20")
        group.ph_disable_time_edit.returnPressed.connect(self.send_ph)
        form_layout.addRow("Tempo Desativada (s):", group.ph_disable_time_edit)
        
        group.ph_speed_edit = QLineEdit("50")
        group.ph_speed_edit.returnPressed.connect(self.send_ph)
        form_layout.addRow("Velocidade da Bomba (%):", group.ph_speed_edit)
        
        layout.addLayout(form_layout)
        
        layout.addItem(QSpacerItem(10, 10, QSizePolicy.Policy.Minimum, QSizePolicy.Policy.Expanding))
        group.setLayout(layout)
        group.checkbox.toggled.connect(lambda checked, grp=group, col=activation_color, callback=self.send_ph:
                                            (self.update_block_style(grp, checked, col), callback()))
        return group

    def create_nutrient_block(self, activation_color, block_size=UI_CONFIG["block_size"]):
        group = QGroupBox("Bomba de Nutriente")
        group.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        group.sizeHint = lambda: block_size
        group.activation_color = activation_color

        layout = QVBoxLayout()
        layout.setSpacing(5)
        group.checkbox = QCheckBox("Ativar Nutriente")
        layout.addWidget(group.checkbox)
        
        # Usa QFormLayout para alinhamento automático de label: entry
        form_layout = QFormLayout()
        form_layout.setSpacing(5)

        group.nutri_op_time_edit = QLineEdit("999")
        group.nutri_op_time_edit.returnPressed.connect(self.send_nutrient)
        form_layout.addRow("Tempo Operando (s):", group.nutri_op_time_edit)
        
        group.nutri_disable_time_edit = QLineEdit("1")
        group.nutri_disable_time_edit.returnPressed.connect(self.send_nutrient)
        form_layout.addRow("Tempo Desativada (s):", group.nutri_disable_time_edit)
        
        group.nutri_op_cycle_edit = QLineEdit("500")
        group.nutri_op_cycle_edit.returnPressed.connect(self.send_nutrient)
        form_layout.addRow("Ciclo Operando (min):", group.nutri_op_cycle_edit)
        
        group.nutri_disable_cycle_edit = QLineEdit("1")
        group.nutri_disable_cycle_edit.returnPressed.connect(self.send_nutrient)
        form_layout.addRow("Ciclo Desativada (min):", group.nutri_disable_cycle_edit)
        
        group.nutri_speed_edit = QLineEdit("99")
        group.nutri_speed_edit.returnPressed.connect(self.send_nutrient)
        form_layout.addRow("Velocidade (%):", group.nutri_speed_edit)
        
        layout.addLayout(form_layout)
        
        layout.addItem(QSpacerItem(10, 10, QSizePolicy.Policy.Minimum, QSizePolicy.Policy.Expanding))
        group.setLayout(layout)
        group.checkbox.toggled.connect(lambda checked, grp=group, col=activation_color, callback=self.send_nutrient:
                                            (self.update_block_style(grp, checked, col), callback()))
        return group

    def create_biomass_block(self, activation_color, block_size=UI_CONFIG["block_size"]):
        group = QGroupBox("Biomassa")
        group.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        group.sizeHint = lambda: block_size
        group.activation_color = activation_color

        layout = QVBoxLayout()
        layout.setSpacing(5)
        
        # 1. Checkbox
        group.checkbox = QCheckBox("Monitorar Biomassa")
        layout.addWidget(group.checkbox)
        
        # 2. New QFormLayout for thresholds
        form_layout = QFormLayout()
        form_layout.setSpacing(5)
        
        group.low_thresh_edit = QLineEdit("10000")
        group.high_thresh_edit = QLineEdit("40000")
        group.opt_thresh_edit = QLineEdit("25000")
        
        form_layout.addRow("Min Alcance:", group.low_thresh_edit)
        form_layout.addRow("Max Alcance:", group.high_thresh_edit)
        form_layout.addRow("Optimal Raw:", group.opt_thresh_edit)
        
        layout.addLayout(form_layout)

        # 3. Icon Buttons (as requested)
        # Helper to create a labeled icon button
        def create_icon_button(text, icon_pixmap):
            layout = QHBoxLayout()
            layout.setContentsMargins(0, 0, 0, 0)
            
            icon_btn = QPushButton()
            try:
                icon = self.style().standardIcon(icon_pixmap)
                icon_btn.setIcon(icon)
            except Exception as e:
                print(f"Icon error: {e}")
                icon_btn.setText("?")
                
            icon_btn.setFixedSize(45, 20)
            icon_btn.setToolTip(text)
            
            layout.addWidget(icon_btn)
            return layout, icon_btn

        # Create the buttons
        blank_layout, group.blank_button = create_icon_button(
            "Branco", QStyle.StandardPixmap.SP_DialogResetButton
        )
        start_layout, group.start_button = create_icon_button(
            "Iniciar", QStyle.StandardPixmap.SP_MediaPlay
        )
        stop_layout, group.stop_button = create_icon_button(
            "Parar", QStyle.StandardPixmap.SP_MediaStop
        )

        # Main horizontal layout for button row
        button_row = QHBoxLayout()
        button_row.setContentsMargins(0, 10, 0, 0)  # left, top, right, bottom

        # Branco group on the left
        button_row.addLayout(blank_layout)
        button_row.addStretch(1)

        # Start group centered
        button_row.addLayout(start_layout)
        button_row.addStretch(1)

        # Stop group on the right
        button_row.addLayout(stop_layout)

        # Add the button row to the main layout
        layout.addLayout(button_row)


        # 4. Connections
        group.checkbox.toggled.connect(
            lambda checked, grp=group, col=activation_color:
                (self.update_block_style(grp, checked, col), 
                 self.send_biomass_comm(checked))
        )
        
        # Connect new fields to the new config sender
        group.low_thresh_edit.returnPressed.connect(self.send_biomass_config)
        group.high_thresh_edit.returnPressed.connect(self.send_biomass_config)
        group.opt_thresh_edit.returnPressed.connect(self.send_biomass_config)
        
        # Connect buttons to their simple commands
        group.blank_button.clicked.connect(self.send_biomass_blank)
        group.start_button.clicked.connect(self.send_biomass_start)
        group.stop_button.clicked.connect(self.send_biomass_stop)
        
        layout.addItem(QSpacerItem(10, 10, QSizePolicy.Policy.Minimum, QSizePolicy.Policy.Expanding))
        group.setLayout(layout)
        return group
    
    def create_agitator_block(self, activation_color, block_size=UI_CONFIG["block_size"]):
        group = QGroupBox("Frasco Agitador")
        group.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        group.sizeHint = lambda: block_size
        group.activation_color = activation_color

        layout = QVBoxLayout()
        layout.setSpacing(5)

        # Manual ON/OFF (immediate run using current config)
        group.agit_on_checkbox = QCheckBox("Agitador")
        layout.addWidget(group.agit_on_checkbox)
        group.agit_on_checkbox.toggled.connect(
            lambda checked, grp=group, col=activation_color, cb=self.send_agitator:
                (self.update_block_style(grp, checked, col), cb())
        )

        rpm_layout = QHBoxLayout()
        rpm_label = QLabel("RPM (%)")
        group.agit_percent_edit = QLineEdit("80")
        group.agit_percent_edit.setValidator(QIntValidator(-100, 100, group))
        group.agit_percent_edit.setPlaceholderText("ex.: 80 CW, -80 CCW")
        group.agit_percent_edit.returnPressed.connect(self.send_agitator)
        rpm_layout.addWidget(rpm_label)
        rpm_layout.addWidget(group.agit_percent_edit, 1)
        layout.addLayout(rpm_layout)
        group._last_dir = 1  # horário

        row = QHBoxLayout()
        row.setContentsMargins(0, 0, 0, 0)

        group.checkbox = QCheckBox("Anti-espuma")
        group.agit_repot_checkbox = QCheckBox("Manual")
        group.agit_repot_checkbox.setChecked(True)

        row.addWidget(group.checkbox)
        row.addStretch()  # optional, keeps space between them
        row.addWidget(group.agit_repot_checkbox)

        layout.addLayout(row)
        group.agit_repot_checkbox.toggled.connect(lambda _: self.send_agitator())

        layout.addItem(QSpacerItem(10, 10, QSizePolicy.Policy.Minimum, QSizePolicy.Policy.Expanding))
        group.setLayout(layout)
        return group

    def create_extern_pump_block(self, activation_color, block_size=UI_CONFIG["block_size"]):
        """
        Cria o bloco de controle customizado para a Bomba Externa.
        """
        group = QGroupBox("Bomba Externa")
        group.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        group.sizeHint = lambda: block_size
        group.activation_color = activation_color
        
        layout = QVBoxLayout()
        layout.setSpacing(5)

        # 1. Checkbox de Ativação Principal
        group.checkbox = QCheckBox("Ativar Comunicação")
        layout.addWidget(group.checkbox)
        
        # 2. Botões de Modo (Grid 2x2)
        modes_group = QGroupBox("Modos de Operação")
        modes_layout = QGridLayout()
        
        self.pump_mode_1_btn = QPushButton("Constante")
        self.pump_mode_2_btn = QPushButton("Linear")
        self.pump_mode_3_btn = QPushButton("Exponencial")
        self.pump_mode_4_btn = QPushButton("Polinomial")
        self.pump_mode_5_btn = QPushButton("Piecewise")  
        
        self.pump_mode_1_btn.clicked.connect(self.open_pump_mode_1_window)
        self.pump_mode_2_btn.clicked.connect(self.open_pump_mode_2_window)
        self.pump_mode_3_btn.clicked.connect(self.open_pump_mode_3_window)
        self.pump_mode_4_btn.clicked.connect(self.open_pump_mode_4_window)
        self.pump_mode_5_btn.clicked.connect(self.open_pump_mode_5_window)

        modes_layout.addWidget(self.pump_mode_1_btn, 0, 0)
        modes_layout.addWidget(self.pump_mode_2_btn, 0, 1)
        modes_layout.addWidget(self.pump_mode_3_btn, 1, 0)
        modes_layout.addWidget(self.pump_mode_4_btn, 1, 1)
        modes_layout.addWidget(self.pump_mode_5_btn, 2, 0)
        
        modes_group.setLayout(modes_layout)
        layout.addWidget(modes_group)

        # 3. Novos Controles: Gás Proporcional (Substitui o controle manual)
        group.gas_prop_checkbox = QCheckBox("Gas Proporcional")
        layout.addWidget(group.gas_prop_checkbox)
        
        gas_form_layout = QFormLayout()
        gas_form_layout.setSpacing(5)
        
        group.vol_inicial_edit = QLineEdit("1.0") # Valor padrão
        group.vvm_edit = QLineEdit("0.5")         # Valor padrão
        
        gas_form_layout.addRow("Vol Inicial (L):", group.vol_inicial_edit)
        gas_form_layout.addRow("vvm:", group.vvm_edit)
        
        layout.addLayout(gas_form_layout)

        layout.addItem(QSpacerItem(10, 10, QSizePolicy.Policy.Minimum, QSizePolicy.Policy.Expanding))
        group.setLayout(layout)

        # Conexão do Checkbox
        group.checkbox.toggled.connect(
            lambda checked, grp=group, col=activation_color:
                (self.update_block_style(grp, checked, col), self.send_extern_pump_comm(checked))
        )
        
        # Desabilitar controles se o checkbox não estiver marcado
        group.checkbox.toggled.connect(modes_group.setEnabled)
        
        # Conexões para os novos widgets
        group.checkbox.toggled.connect(group.gas_prop_checkbox.setEnabled)
        group.checkbox.toggled.connect(group.vol_inicial_edit.setEnabled)
        group.checkbox.toggled.connect(group.vvm_edit.setEnabled)
        
        # Estado inicial
        modes_group.setEnabled(False)
        
        # Estado inicial para os novos widgets
        group.gas_prop_checkbox.setEnabled(False)
        group.vol_inicial_edit.setEnabled(False)
        group.vvm_edit.setEnabled(False)

        return group

    def send_extern_pump_comm(self, checked):
        """Envia o comando de ligar/desligar a comunicação da bomba."""
        
        # FIXED LOGIC: Send command for BOTH states (ON and OFF)
        command = {
            "pumpComm": 1 if checked else 0  # Tell Hub to enable/disable pump routing
        }

        # If turning OFF, force the pump to stop and go to IDLE
        if not checked:
            command["mode"] = 0
            command["speed"] = 0  # CHANGED: 'pump_speed' to 'speed' (see item 2 below)

        self.comm_handler.send_command(command)
        self.on_field_changed() # Salva o estado do checkbox

    def _open_pump_mode_window(self, mode_id):
        """Helper genérico para abrir a janela de modo."""
        if PumpModeWindow is None:
            self._log("Erro: Módulo PumpModeWindow não foi carregado.")
            return
            
        if self.main_window is None:
            self._log("Erro: Referência à MainWindow não definida.")
            return

        # Passa a main_window para a janela de diálogo
        dialog = PumpModeWindow(mode_id=mode_id, main_window=self.main_window, parent=self)
        dialog.exec()  # Abre como um diálogo modal

    def open_pump_mode_1_window(self):
        self._open_pump_mode_window(1)

    def open_pump_mode_2_window(self):
        self._open_pump_mode_window(2)

    def open_pump_mode_3_window(self):
        self._open_pump_mode_window(3)

    def open_pump_mode_4_window(self):
        self._open_pump_mode_window(4)

    def open_pump_mode_5_window(self):
        self._open_pump_mode_window(5)

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
        """
        Enforce 50..1000 RPM inclusive. If the current entry is invalid,
        revert the QLineEdit to the last valid value.
        Sends the valid setpoint if the checkbox is checked, or 0 if unchecked.
        """
        # 1. Get text and attempt to parse
        raw_text = self.replace_comma(self.motor_block.line_edit.text()).strip()
        is_valid = False
        try:
            requested = int(float(raw_text))

            # 2. Validate the number against the domain [50, 1000]
            if 50 <= requested <= 1000:
                is_valid = True

        except ValueError:
            # Not a number
            requested = self._motor_last_valid # Value to be used for revert
            is_valid = False

        # 3. Update UI and last valid value based on validation
        if is_valid:
            self._motor_last_valid = requested
            # Normalize UI text (e.g., if user typed "070.0")
            if self.motor_block.line_edit.text() != str(requested):
                    self.motor_block.line_edit.setText(str(requested))
            value_to_send = requested
        else:
            # Revert UI to last valid value
            self.motor_block.line_edit.setText(str(self._motor_last_valid))
            # The value to send (if the box is checked) should be the one we reverted to
            value_to_send = self._motor_last_valid 
            self._log(f"Valor RPM '{raw_text}' fora do limite (50-1000) ou inválido. Revertendo para {self._motor_last_valid}.")

        # 4. Decide what command to send based on checkbox state
        if self.motor_block.checkbox.isChecked():
            # Control is ON, send the (now validated) setpoint
            final_setpoint = value_to_send
        else:
            # Control is OFF, send 0
            final_setpoint = 0

        command = {"motorSetpoint": final_setpoint}
        self.comm_handler.send_command(command)

        if self.main_window is not None:
            self.main_window.current_motor_rpm = final_setpoint

    def _log(self, text: str) -> None:
        if self.main_window:
            ts = time.strftime("%H:%M:%S")
            self.main_window.log_signal.emit(f"[{ts}] {text}")

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
        disable_text = self.replace_comma(self.antifoam_block.extra_edits["Tempo Desativado (s):"].text())
        try:
            disable_value = int(float(disable_text))
        except:
            disable_value = 20
        speed_text = self.replace_comma(self.antifoam_block.extra_edits["Velocidade (%):"].text())
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
        
        # Usa o QLineEdit correto para Max Flow
        max_flow_text = self.replace_comma(self.flow_max_flow_edit.text())
        try:
            max_flow = float(max_flow_text)
        except:
            max_flow = 50

        if self.flow_block.checkbox.isChecked():
            setpoint = max(0, min(setpoint, max_flow))
            flow_comm = 1
            v_Flow = 1 if setpoint == 0 else 0
        else:
            flow_comm = 0
            v_Flow = 1
        
        valve1_state = 1 if self.flow_valve_checkbox.isChecked() else 0
        valve2_state = 1 if self.flow_valve2_checkbox.isChecked() else 0

        command = {
            "flowmeterComm": flow_comm,
            "flowSetpoint": setpoint,
            "maxFlow": max_flow,
            "valve_1": valve1_state, 
            "valve_2": valve2_state,
            "v_Flow": v_Flow 
        }
        self.comm_handler.send_command(command)

    def send_distance_sensor(self):
        try:
            dist_reference = float(self.replace_comma(self.distance_block.line_edit.text()))
        except:
            dist_reference = 100.0
        try:
            delay_s = float(self.replace_comma(self.distance_block.extra_edits["Delay Início (s):"].text()))
        except:
            delay_s = 1.0
        
        try:
            pulse_s = float(self.replace_comma(self.distance_block.extra_edits["Pulso ON (s):"].text()))
        except:
            pulse_s = 1.0
            
        try:
            interval_s = float(self.replace_comma(self.distance_block.extra_edits["Intervalo (s):"].text()))
        except:
            interval_s = 5.0
        sensor_state = 1 if self.distance_block.checkbox.isChecked() else 0
        command = {
            "distanceSensorComm": sensor_state, 
            "distanceSensorReference": dist_reference,
            "foamStartDelay_s": delay_s,
            "foamPulse_s": pulse_s,
            "foamInterval_s": interval_s
        }
        self.comm_handler.send_command(command)
    
    def send_agitator(self):
        # signed percent encodes direction: >=0 horário, <0 anti-horário
        try:
            raw = int(float(self.replace_comma(self.agitator_block.agit_percent_edit.text())))
        except Exception:
            raw = 80
        # clamp to [-100, 100]
        raw = max(-100, min(raw, 100))
        if raw != 0:
            self.agitator_block._last_dir = 1 if raw >= 0 else 0
        dir_val = self.agitator_block._last_dir

        pct = abs(raw)  # magnitude 0..100
        dir_val = 1 if raw >= 0 else 0  # 1 horário, 0 anti-horário
        repot = 1 if self.agitator_block.agit_repot_checkbox.isChecked() else 0
        auto = 1 if self.agitator_block.checkbox.isChecked() else 0
        onoff = 1 if self.agitator_block.agit_on_checkbox.isChecked() else 0

        command = {
            # automatic use inside the anti-foam routine
            "agitatorAuto": auto,
            "agitatorReEnablePot": repot,
            "agitatorPercent": pct,
            "agitatorDir": dir_val,
            "agitatorOn": onoff
        }
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
    
    def _send_biomass_command(self, command: dict, log_msg: str):
        """Centralized helper for sending simple biomass commands."""
        self.comm_handler.send_command(command)
        self._log(log_msg)

    def send_biomass_comm(self, checked):
        """Envia o comando de ativação/desativação do sensor de biomassa."""
        command = {"biomassComm": 1 if checked else 0}
        self.comm_handler.send_command(command)
        self._log(f"Monitoramento de Biomassa {'ativado' if checked else 'desativado'}.")

    def send_biomass_config(self):
        """Envia os novos parâmetros de threshold (low, high, opt) em um único comando."""
        try:
            # 1. Tenta ler e converter todos os valores primeiro
            low_val = int(self.biomass_block.low_thresh_edit.text())
            high_val = int(self.biomass_block.high_thresh_edit.text())
            opt_val = int(self.biomass_block.opt_thresh_edit.text())

            # 2. Constrói o dicionário de comando combinado
            combined_command = {
                "low": low_val,
                "high": high_val,
                "opt": opt_val
            }

            # 3. Envia o comando único
            self.comm_handler.send_command(combined_command)
            
            # 4. Loga o sucesso do envio de todos os valores
            self._log(f"Biomassa: Thresholds atualizados -> low: {low_val}, high: {high_val}, opt: {opt_val}.")

        except ValueError:
            # Se qualquer um dos valores for inválido, nada é enviado
            self._log("ERRO: Um dos valores de threshold da biomassa é inválido. Nenhum comando foi enviado.")

    def send_biomass_blank(self):
        """Envia o comando 'blank' para o sensor de biomassa."""
        self._send_biomass_command({"blank": 1}, "Comando 'Branco' enviado para Biomassa.")

    def send_biomass_start(self):
        """Envia o comando 'start' para o sensor de biomassa."""
        self._send_biomass_command({"start": 1}, "Comando 'Iniciar' enviado para Biomassa.")

    def send_biomass_stop(self):
        """Envia o comando 'stop' para o sensor de biomassa."""
        self._send_biomass_command({"stop": 1}, "Comando 'Parar' enviado para Biomassa.")
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
    
    def _send_biomass_command(self, command: dict, log_msg: str):
        """Centralized helper for sending simple biomass commands."""
        self.comm_handler.send_command(command)
        self._log(log_msg)

    def send_biomass_comm(self, checked):
        """Envia o comando de ativação/desativação do sensor de biomassa."""
        command = {"biomassComm": 1 if checked else 0}
        self.comm_handler.send_command(command)
        self._log(f"Monitoramento de Biomassa {'ativado' if checked else 'desativado'}.")

    def send_biomass_config(self):
        """Envia os novos parâmetros de threshold (low, high, opt) em um único comando."""
        try:
            # 1. Tenta ler e converter todos os valores primeiro
            low_val = int(self.biomass_block.low_thresh_edit.text())
            high_val = int(self.biomass_block.high_thresh_edit.text())
            opt_val = int(self.biomass_block.opt_thresh_edit.text())

            # 2. Constrói o dicionário de comando combinado
            combined_command = {
                "low": low_val,
                "high": high_val,
                "opt": opt_val
            }

            # 3. Envia o comando único
            self.comm_handler.send_command(combined_command)
            
            # 4. Loga o sucesso do envio de todos os valores
            self._log(f"Biomassa: Thresholds atualizados -> low: {low_val}, high: {high_val}, opt: {opt_val}.")

        except ValueError:
            # Se qualquer um dos valores for inválido, nada é enviado
            self._log("ERRO: Um dos valores de threshold da biomassa é inválido. Nenhum comando foi enviado.")

    def send_biomass_blank(self):
        """Envia o comando 'blank' para o sensor de biomassa."""
        self._send_biomass_command({"blank": 1}, "Comando 'Branco' enviado para Biomassa.")

    def send_biomass_start(self):
        """Envia o comando 'start' para o sensor de biomassa."""
        self._send_biomass_command({"start": 1}, "Comando 'Iniciar' enviado para Biomassa.")

    def send_biomass_stop(self):
        """Envia o comando 'stop' para o sensor de biomassa."""
        self._send_biomass_command({"stop": 1}, "Comando 'Parar' enviado para Biomassa.")