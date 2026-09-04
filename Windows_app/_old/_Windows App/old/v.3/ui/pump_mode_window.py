#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# pump_mode_window.py - Janela de diálogo para modos da bomba externa
# ATUALIZADO (v3.5): simulação e envio de comandos alinhados ao firmware
# • Toda a lógica usa tempo relativo t' = t - t_init para setpoints
# • O gráfico mostra t de 0 até t_final, com Q(t)=0 no intervalo [0, t_init)
# • Modos 2, 3 e 4 avaliam Q(t') com t' ≥ 0; modo 1 é mascarado antes do início

import numpy as np
from scipy import integrate
from PyQt6.QtWidgets import (
    QDialog, QVBoxLayout, QFormLayout, QLineEdit,
    QPushButton, QDialogButtonBox, QHBoxLayout, QGroupBox,
    QLabel
)
from PyQt6.QtCore import Qt
import pyqtgraph as pg


class PumpModeWindow(QDialog):
    """
    Janela de diálogo para configurar, simular e enviar parâmetros de operação
    da bomba externa, com simulação coerente ao firmware v3.5.
    """
    def __init__(self, mode_id, main_window, parent=None):
        super().__init__(parent)
        self.mode_id = mode_id  # 1: Constante, 2: Linear, 3: Exponencial, 4: Polinomial
        self.main_window = main_window
        self.comm_handler = main_window.comm_handler
        
        mode_names = {
            1: "Constante: Q(t') = λ",
            2: "Linear: Q(t') = λ + φ·t'",
            3: "Exponencial: Q(t') = λ · e^{φ·t'}",
            4: "Polinomial (N): Q(t') = p0 + p1·t' + ... + pN·t'^N"
        }
        self.mode_name = mode_names.get(self.mode_id, "Desconhecido")
        
        self.setWindowTitle(f"Configuração - Modo {self.mode_id} ({self.mode_name})")
        self.setModal(True)
        self.resize(950, 460)
        self.setMinimumSize(600, 420)

        main_layout = QVBoxLayout(self)

        # --- 1. & 2. Layout Superior (Lado a Lado) ---
        top_layout = QHBoxLayout()

        # --- 1. Parâmetros de Tempo (Comum a todos) ---
        time_group = QGroupBox("Parâmetros de Operação")
        time_layout = QFormLayout(time_group)
        self.t_initial_edit = QLineEdit("0")
        self.t_final_edit = QLineEdit("60")
        time_layout.addRow("Tempo Inicial t_init [min]:", self.t_initial_edit)
        time_layout.addRow("Tempo Final t_final [min]:", self.t_final_edit)
        top_layout.addWidget(time_group, 1)

        # --- 2. Parâmetros de Entrada (Dinâmico) ---
        self.params_group = QGroupBox("Parâmetros da Equação em t'")
        self.params_layout = QFormLayout(self.params_group)
        self.param_edits = {}
        self._build_mode_ui()
        top_layout.addWidget(self.params_group, 2)
        
        main_layout.addLayout(top_layout)

        # --- 3. Gráficos de Simulação ---
        graphs_group = QGroupBox("Simulação")
        graphs_layout = QHBoxLayout()
        self.plot_flow = pg.PlotWidget(title="Vazão Simulada Q(t) [mL/min]")
        self.plot_volume = pg.PlotWidget(title="Volume Acumulado V(t) [mL]")
        self.plot_flow.showGrid(x=True, y=True)
        self.plot_volume.showGrid(x=True, y=True)
        self.plot_flow.setLabel('bottom', "Tempo absoluto t [min]")
        self.plot_flow.setLabel('left', "Q(t) [mL/min]")
        self.plot_volume.setLabel('bottom', "Tempo absoluto t [min]")
        self.plot_volume.setLabel('left', "V(t) desde t_init [mL]")

        self.plot_flow.setSizePolicy(pg.QtWidgets.QSizePolicy.Policy.Expanding,
                             pg.QtWidgets.QSizePolicy.Policy.Expanding)
        self.plot_volume.setSizePolicy(pg.QtWidgets.QSizePolicy.Policy.Expanding,
                                    pg.QtWidgets.QSizePolicy.Policy.Expanding)

        # reduce minimum size to avoid clipping when dialog shrinks
        self.plot_flow.setMinimumSize(280, 200)
        self.plot_volume.setMinimumSize(280, 200)
        graphs_layout.addWidget(self.plot_flow)
        graphs_layout.addWidget(self.plot_volume)
        graphs_group.setLayout(graphs_layout)
        main_layout.addWidget(graphs_group)

        

        # --- 4. Botões de Ação ---
        buttons_layout = QHBoxLayout()
        self.sim_button = QPushButton("Simular")
        self.sim_button.setEnabled(False)
        buttons_layout.addWidget(self.sim_button)
        buttons_layout.addStretch()
        self.dialog_buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        self.send_button = self.dialog_buttons.button(QDialogButtonBox.StandardButton.Ok)
        self.send_button.setText("Enviar")
        self.cancel_button = self.dialog_buttons.button(QDialogButtonBox.StandardButton.Cancel)
        self.cancel_button.setText("Cancelar")
        buttons_layout.addWidget(self.dialog_buttons)
        main_layout.addLayout(buttons_layout)
        
        # --- Conexões ---
        self.sim_button.clicked.connect(self.run_simulation)
        self.send_button.clicked.connect(self.send_commands)
        self.cancel_button.clicked.connect(self.reject)
        
        self.t_initial_edit.textChanged.connect(self.check_sim_enable)
        self.t_final_edit.textChanged.connect(self.check_sim_enable)
        for edit in self.param_edits.values():
            edit.textChanged.connect(self.check_sim_enable)

        # Carregar valores salvos
        self.load_from_prefs()

    def _build_mode_ui(self):
        """Constrói a UI de parâmetros específica para o modo selecionado."""
        if self.mode_id == 1:  # Constante: Q(t') = λ
            self.param_edits["A"] = QLineEdit()
            self.params_layout.addRow("λ [mL/min]:", self.param_edits["A"])

        elif self.mode_id == 2:  # Linear: Q(t') = λ + φ·t'
            self.param_edits["A"] = QLineEdit()
            self.param_edits["B"] = QLineEdit()
            self.params_layout.addRow("λ [mL/min]:", self.param_edits["A"])
            self.params_layout.addRow("φ [mL/min²]:", self.param_edits["B"])

        elif self.mode_id == 3:  # Exponencial: Q(t') = λ · e^{φ·t'}
            self.param_edits["A"] = QLineEdit()
            self.param_edits["B"] = QLineEdit()
            self.params_layout.addRow("λ [mL/min]:", self.param_edits["A"])
            self.params_layout.addRow("φ [1/min]:", self.param_edits["B"])

        elif self.mode_id == 4:  # Polinomial: Q(t') = p0 + p1·t' + ...
            self.param_edits["coeffs"] = QLineEdit()
            self.params_layout.addRow(QLabel("Q(t') = p0 + p1·t' + p2·t'^2 + ... + pN·t'^N"))
            self.params_layout.addRow("Coeficientes p0..p20 separados por vírgula:", self.param_edits["coeffs"])
        else:
            self.params_layout.addRow(QLabel("Modo inválido ou não implementado."))

    def check_sim_enable(self):
        """Habilita o botão de simulação se todos os campos tiverem texto."""
        all_filled = bool(self.t_initial_edit.text()) and bool(self.t_final_edit.text())
        for edit in self.param_edits.values():
            if not edit.text():
                all_filled = False
                break
        self.sim_button.setEnabled(all_filled)

    def _get_params_as_floats(self):
        """Lê campos como floats e valida limites."""
        params = {}
        try:
            params["t_initial"] = float(self.t_initial_edit.text().replace(",", "."))
            params["t_final"] = float(self.t_final_edit.text().replace(",", "."))
            
            if params["t_final"] <= 0:
                self.main_window.log_signal.emit("Erro: Tempo Final deve ser maior que zero.")
                return None
            if params["t_final"] <= params["t_initial"]:
                self.main_window.log_signal.emit("Erro: Tempo Final deve ser maior que Tempo Inicial.")
                return None

            if self.mode_id == 4:
                coeffs_str = self.param_edits["coeffs"].text().strip()
                if not coeffs_str:
                    self.main_window.log_signal.emit("Erro: Coeficientes polinomiais não podem estar vazios.")
                    return None
                coeffs_list = []
                for c_str in coeffs_str.replace(" ", "").split(','):
                    if c_str:
                        coeffs_list.append(float(c_str.replace(",", ".")))
                if len(coeffs_list) > 21:
                    self.main_window.log_signal.emit(f"Erro: Máximo de 21 coeficientes (p0..p20). Recebidos {len(coeffs_list)}.")
                    return None
                if not coeffs_list:
                    self.main_window.log_signal.emit("Erro: Nenhum coeficiente válido fornecido.")
                    return None
                params["coeffs"] = coeffs_list
            else:
                if "A" in self.param_edits:
                    params["A"] = float(self.param_edits["A"].text().replace(",", "."))
                if "B" in self.param_edits:
                    params["B"] = float(self.param_edits["B"].text().replace(",", "."))
            return params

        except ValueError:
            self.main_window.log_signal.emit("Erro: Parâmetros inválidos. Use números válidos.")
            return None
        except Exception as e:
            self.main_window.log_signal.emit(f"Erro ao processar parâmetros: {e}")
            return None

    def run_simulation(self):
        """
        Executa a simulação com coerência ao firmware:
        • Eixo do tempo absoluto t vai de 0 até t_final
        • Fluxo Q(t) é zero para 0 ≤ t < t_init
        • Setpoints calculados com t' = max(t - t_init, 0)
        • Volume integra Q(t) ao longo de t absoluto, resultando V(t) que inicia em 0 em t_init
        """
        params = self._get_params_as_floats()
        if params is None:
            return

        t_start_min = params["t_initial"]
        t_stop_min = params["t_final"]

        # Tempo absoluto: de 0 até t_final para refletir o período de espera
        t_sim_min = np.linspace(0.0, t_stop_min, 600)  # maior resolução
        # Tempo relativo: t' = t - t_init, com saturação inferior em 0
        t_relative_sim_min = np.maximum(t_sim_min - t_start_min, 0.0)

        # Inicializa vetor de vazão
        flow_rate_ml_min = np.zeros_like(t_sim_min)
        plot_name = "Q(t) em função de t'"

        try:
            if self.mode_id == 1:
                # Q(t') = λ; porém, antes de t_init a bomba não opera
                A = params['A']
                q_rel = np.full_like(t_relative_sim_min, A)
                # Mascara de operação: somente t >= t_init
                mask_on = t_sim_min >= t_start_min
                flow_rate_ml_min = np.where(mask_on, q_rel, 0.0)
                plot_name = f"Q(t') = {A:.3f}"

            elif self.mode_id == 2:
                # Q(t') = λ + φ·t'
                A, B = params['A'], params['B']
                q_rel = A + B * t_relative_sim_min
                q_rel[q_rel < 0.0] = 0.0
                mask_on = t_sim_min >= t_start_min
                flow_rate_ml_min = np.where(mask_on, q_rel, 0.0)
                plot_name = f"Q(t') = {A:.3f} + {B:.3f}·t'"

            elif self.mode_id == 3:
                # Q(t') = λ · e^{φ·t'}
                A, B = params['A'], params['B']
                q_rel = A * np.exp(B * t_relative_sim_min)
                q_rel[q_rel < 0.0] = 0.0
                mask_on = t_sim_min >= t_start_min
                flow_rate_ml_min = np.where(mask_on, q_rel, 0.0)
                plot_name = f"Q(t') = {A:.3f}·e^({B:.3f}·t')"

            elif self.mode_id == 4:
                # Q(t') = Σ p_i · t'^i, com 0 ≤ i ≤ N, N ≤ 20
                coeffs = params['coeffs']
                # Horner para estabilidade e custo
                q_rel = np.zeros_like(t_relative_sim_min)
                if len(coeffs) > 0:
                    # Preenche faltantes até p20 com zeros
                    coeffs_full = list(coeffs) + [0.0] * (21 - len(coeffs))
                    acc = np.full_like(t_relative_sim_min, coeffs_full[20])
                    for i in range(19, -1, -1):
                        acc = acc * t_relative_sim_min + coeffs_full[i]
                    q_rel = acc
                q_rel[q_rel < 0.0] = 0.0
                mask_on = t_sim_min >= t_start_min
                flow_rate_ml_min = np.where(mask_on, q_rel, 0.0)
                plot_name = f"Q(t') polinomial (p0..p{len(coeffs)-1})"

        except Exception as e:
            self.main_window.log_signal.emit(f"Erro na simulação: {e}")
            return

        # Integra volume sobre t absoluto, começando em 0 no tempo 0
        # Como Q(t)=0 para t<t_init, V(t) crescerá somente após t_init
        t_sim_sec = t_sim_min * 60.0
        flow_rate_ml_sec = flow_rate_ml_min / 60.0
        volume_ml = integrate.cumulative_trapezoid(flow_rate_ml_sec, t_sim_sec, initial=0.0)

        # Plot
        self.plot_flow.plot(t_sim_min, flow_rate_ml_min, clear=True, name=plot_name)
        self.plot_flow.addItem(pg.InfiniteLine(pos=t_start_min, angle=90, movable=False, pen=pg.mkPen(style=Qt.PenStyle.DotLine)))
        self.plot_volume.plot(t_sim_min, volume_ml, clear=True, name="V(t) desde t_init")
        self.plot_volume.addItem(pg.InfiniteLine(pos=t_start_min, angle=90, movable=False, pen=pg.mkPen(style=Qt.PenStyle.DotLine)))

        self.main_window.log_signal.emit(f"Simulação Modo {self.mode_id} concluída.")

    def send_commands(self):
        """
        Salva preferências e envia payload coerente ao firmware main.cpp:
        mode, init_t, final_t e parâmetros específicos por modo.
        """
        params = self._get_params_as_floats()
        if params is None:
            self.main_window.log_signal.emit("Erro: Parâmetros inválidos. Comando não enviado.")
            return

        self.save_to_prefs()
        
        command = {
            "mode": self.mode_id,
            "init_t": params["t_initial"],
            "final_t": params["t_final"]
        }
        
        if self.mode_id == 1:
            command["lambda_const"] = params['A']
        elif self.mode_id == 2:
            command["lambda_linear"] = params['A']
            command["phi_linear"] = params['B']
        elif self.mode_id == 3:
            command["lambda_exp"] = params['A']
            command["phi_exp"] = params['B']
        elif self.mode_id == 4:
            coeffs = params['coeffs']
            for i, coeff in enumerate(coeffs):
                command[f"p{i}"] = coeff  # p0..pN

        self.comm_handler.send_command(command)
        self.main_window.log_signal.emit(f"Modo Bomba {self.mode_id} enviado: {params}")
        self.accept()

    def save_to_prefs(self):
        """Persiste os valores desta janela nas preferências da main_window."""
        mode_key = f"mode_{self.mode_id}"
        prefs = self.main_window.preferences
        
        if "ExternalPump" not in prefs:
            prefs["ExternalPump"] = {"modes": {}}
        if "modes" not in prefs["ExternalPump"]:
            prefs["ExternalPump"]["modes"] = {}
        
        mode_data = {}
        mode_data["t_initial"] = self.t_initial_edit.text()
        mode_data["t_final"] = self.t_final_edit.text()
        for key, edit in self.param_edits.items():
            mode_data[f"param_{key}"] = edit.text()
        prefs["ExternalPump"]["modes"][mode_key] = mode_data
        self.main_window.on_editing_finished()

    def load_from_prefs(self):
        """Carrega valores previamente salvos para este modo."""
        mode_key = f"mode_{self.mode_id}"
        prefs_dict = self.main_window.preferences \
            .get("ExternalPump", {}) \
            .get("modes", {}) \
            .get(mode_key, {})
            
        self.t_initial_edit.setText(prefs_dict.get("t_initial", "0"))
        self.t_final_edit.setText(prefs_dict.get("t_final", "60"))
        for key, edit in self.param_edits.items():
            edit.setText(prefs_dict.get(f"param_{key}", "0"))
        self.check_sim_enable()
