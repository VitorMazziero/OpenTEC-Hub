#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# pump_mode_window.py - Janela de diálogo para modos da bomba externa
#
# ATUALIZADO para firmware v3.7:
# - Modo 5 (Piecewise) envia JSON compatível com SensorHub + Pump:
#   * "mode": 5
#   * "init_t", "final_t"
#   * "num_segments"
#   * "t0".."tN" e "q0".."qN" (t' em minutos, Q em mL/min)
# - Modos 1–4 mantidos consistentes com o firmware (λ/φ, polinômio p0..p20).

import numpy as np
from scipy import integrate
from PySide6.QtWidgets import (
    QDialog, QVBoxLayout, QFormLayout, QLineEdit,
    QPushButton, QDialogButtonBox, QHBoxLayout, QGroupBox,
    QLabel, QScrollArea, QWidget
)
from PySide6.QtCore import Qt
import pyqtgraph as pg


class PumpModeWindow(QDialog):
    """
    Janela de diálogo para configurar, simular e enviar parâmetros de operação
    da bomba externa, coerente com o firmware v3.7.

    Convenções:
      t_init, t_final  em minutos (tempo absoluto).
      t' = t - t_init  tempo relativo para Q(t').
    """
    # Alinhado com firmware (p0..p20)
    NUM_POLY_COEFFS = 21
    # Alinhado com firmware SensorHub/Pump (t0/q0..t99/q99)
    MAX_SEGMENTS = 100

    def __init__(self, mode_id, main_window, parent=None):
        super().__init__(parent)
        self.mode_id = mode_id  # 1: Constante, 2: Linear, 3: Exponencial, 4: Polinomial, 5: Piecewise
        self.main_window = main_window
        self.comm_handler = main_window.comm_handler

        mode_names = {
            1: "Constante: Q(t') = λ",
            2: "Linear: Q(t') = λ + φ·t'",
            3: "Exponencial: Q(t') = λ · e^{φ·t'}",
            4: f"Polinomial (N≤{self.NUM_POLY_COEFFS-1}): Q(t') = p0 + ... + pN·t'^N",
            5: f"Piecewise (M≤{self.MAX_SEGMENTS}): Q(t') = interp(t')"
        }
        self.mode_name = mode_names.get(self.mode_id, "Desconhecido")

        self.setWindowTitle(f"Configuração - Modo {self.mode_id} ({self.mode_name})")
        self.setModal(True)
        self.resize(1000, 700)
        self.setMinimumSize(700, 600)

        main_layout = QVBoxLayout(self)

        # ------------------------------------------------------------------
        # 1. + 2. Layout superior
        # ------------------------------------------------------------------
        top_layout = QHBoxLayout()

        # 1. Parâmetros de tempo
        time_group = QGroupBox("Parâmetros de Operação")
        time_layout = QFormLayout(time_group)
        self.t_initial_edit = QLineEdit("0")
        self.t_final_edit = QLineEdit("60")
        time_layout.addRow("Tempo Inicial t_init [min]:", self.t_initial_edit)
        time_layout.addRow("Tempo Final t_final [min]:", self.t_final_edit)
        top_layout.addWidget(time_group, 1)

        # 2. Parâmetros de entrada por modo (em QScrollArea)
        self.params_group = QGroupBox("Parâmetros da Equação em t'")
        self.params_scroll_area = QScrollArea()
        self.params_scroll_area.setWidgetResizable(True)
        self.params_scroll_widget = QWidget()
        self.params_layout = QFormLayout(self.params_scroll_widget)
        self.params_scroll_area.setWidget(self.params_scroll_widget)

        self.params_group.setLayout(QVBoxLayout())
        self.params_group.layout().addWidget(self.params_scroll_area)

        self.param_edits = {}
        self._build_mode_ui()
        top_layout.addWidget(self.params_group, 3)

        main_layout.addLayout(top_layout)

        # ------------------------------------------------------------------
        # 3. Gráficos de simulação
        # ------------------------------------------------------------------
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
        self.plot_flow.setMinimumSize(280, 200)
        self.plot_volume.setMinimumSize(280, 200)

        graphs_layout.addWidget(self.plot_flow)
        graphs_layout.addWidget(self.plot_volume)
        graphs_group.setLayout(graphs_layout)
        main_layout.addWidget(graphs_group)

        # ------------------------------------------------------------------
        # 4. Botões
        # ------------------------------------------------------------------
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

        # Conexões
        self.sim_button.clicked.connect(self.run_simulation)
        self.send_button.clicked.connect(self.send_commands)
        self.cancel_button.clicked.connect(self.reject)

        self.t_initial_edit.textChanged.connect(self.check_sim_enable)
        self.t_final_edit.textChanged.connect(self.check_sim_enable)
        for edit in self.param_edits.values():
            edit.textChanged.connect(self.check_sim_enable)

        # Carregar últimos valores
        self.load_from_prefs()

    # ======================================================================
    # UI específica por modo
    # ======================================================================
    def _build_mode_ui(self):
        if self.mode_id == 1:  # Constante
            self.param_edits["A"] = QLineEdit()
            self.params_layout.addRow("λ [mL/min]:", self.param_edits["A"])

        elif self.mode_id == 2:  # Linear
            self.param_edits["A"] = QLineEdit()
            self.param_edits["B"] = QLineEdit()
            self.params_layout.addRow("λ [mL/min]:", self.param_edits["A"])
            self.params_layout.addRow("φ [mL/min²]:", self.param_edits["B"])

        elif self.mode_id == 3:  # Exponencial
            self.param_edits["A"] = QLineEdit()
            self.param_edits["B"] = QLineEdit()
            self.params_layout.addRow("λ [mL/min]:", self.param_edits["A"])
            self.params_layout.addRow("φ [1/min]:", self.param_edits["B"])

        elif self.mode_id == 4:  # Polinomial p0..p20
            self.param_edits["coeffs"] = QLineEdit()
            self.param_edits["coeffs"].setPlaceholderText("p0, p1, p2, ...")
            self.params_layout.addRow(
                QLabel(
                    f"Q(t') = p0 + p1·t' + ... + p{self.NUM_POLY_COEFFS-1}·t'^{self.NUM_POLY_COEFFS-1}"
                )
            )
            self.params_layout.addRow(
                f"Coeficientes p0..p{self.NUM_POLY_COEFFS-1} (vírgula):",
                self.param_edits["coeffs"]
            )

        elif self.mode_id == 5:  # Piecewise
            self.param_edits["num_segments"] = QLineEdit()
            self.param_edits["num_segments"].setPlaceholderText(
                f"2 a {self.MAX_SEGMENTS}"
            )
            self.param_edits["time_points"] = QLineEdit()
            self.param_edits["time_points"].setPlaceholderText(
                "t0, t1, t2, ... (t0 deve ser 0; t' em minutos)"
            )
            self.param_edits["flow_points"] = QLineEdit()
            self.param_edits["flow_points"].setPlaceholderText(
                "q0, q1, q2, ... (Q em mL/min)"
            )

            self.params_layout.addRow(
                f"Nº de Pontos (2-{self.MAX_SEGMENTS}):",
                self.param_edits["num_segments"]
            )
            self.params_layout.addRow(
                "Pontos de Tempo t' [min] (vírgula):",
                self.param_edits["time_points"]
            )
            self.params_layout.addRow(
                "Pontos de Vazão Q [mL/min] (vírgula):",
                self.param_edits["flow_points"]
            )

        else:
            self.params_layout.addRow(
                QLabel("Modo inválido ou não implementado.")
            )

    # ======================================================================
    # Validação básica para habilitar simulação
    # ======================================================================
    def check_sim_enable(self):
        all_filled = bool(self.t_initial_edit.text()) and bool(self.t_final_edit.text())
        for edit in self.param_edits.values():
            if not edit.text():
                all_filled = False
                break
        self.sim_button.setEnabled(all_filled)

    # ======================================================================
    # Leitura dos parâmetros em float
    # ======================================================================
    def _get_params_as_floats(self):
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

                if len(coeffs_list) > self.NUM_POLY_COEFFS:
                    self.main_window.log_signal.emit(
                        f"Erro: Máximo de {self.NUM_POLY_COEFFS} coeficientes (p0..p{self.NUM_POLY_COEFFS-1}). "
                        f"Recebidos {len(coeffs_list)}."
                    )
                    return None
                if not coeffs_list:
                    self.main_window.log_signal.emit("Erro: Nenhum coeficiente válido fornecido.")
                    return None
                params["coeffs"] = coeffs_list

            elif self.mode_id == 5:
                num_segments_str = self.param_edits["num_segments"].text().strip()
                time_points_str = self.param_edits["time_points"].text().strip()
                flow_points_str = self.param_edits["flow_points"].text().strip()

                if not all([num_segments_str, time_points_str, flow_points_str]):
                    self.main_window.log_signal.emit(
                        "Erro: Todos os campos do Modo 5 devem ser preenchidos."
                    )
                    return None

                num_segments = int(num_segments_str)
                if not (2 <= num_segments <= self.MAX_SEGMENTS):
                    self.main_window.log_signal.emit(
                        f"Erro: Nº de Pontos ({num_segments}) deve estar entre 2 e {self.MAX_SEGMENTS}."
                    )
                    return None

                time_points = [
                    float(t.strip().replace(",", "."))
                    for t in time_points_str.split(',')
                    if t.strip()
                ]
                flow_points = [
                    float(q.strip().replace(",", "."))
                    for q in flow_points_str.split(',')
                    if q.strip()
                ]

                if len(time_points) != num_segments or len(flow_points) != num_segments:
                    self.main_window.log_signal.emit(
                        f"Erro: Nº de Pontos ({num_segments}) não confere com os dados inseridos "
                        f"(Tempos: {len(time_points)}, Vazões: {len(flow_points)})."
                    )
                    return None

                if time_points[0] != 0.0:
                    self.main_window.log_signal.emit(
                        f"Aviso: Primeiro ponto de tempo (t0) é {time_points[0]}, mas deveria ser 0.0."
                    )

                for i in range(num_segments - 1):
                    if time_points[i] >= time_points[i + 1]:
                        self.main_window.log_signal.emit(
                            f"Erro: Pontos de tempo devem ser crescentes. "
                            f"t{i} ({time_points[i]}) >= t{i+1} ({time_points[i+1]})."
                        )
                        return None

                params["num_segments"] = num_segments
                params["time_points"] = time_points
                params["flow_points"] = flow_points

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

    # ======================================================================
    # Simulação (coerente com firmware)
    # ======================================================================
    def run_simulation(self):
        params = self._get_params_as_floats()
        if params is None:
            return

        t_start_min = params["t_initial"]
        t_stop_min = params["t_final"]

        t_sim_min = np.linspace(0.0, t_stop_min, 600)
        t_relative_sim_min = np.maximum(t_sim_min - t_start_min, 0.0)

        flow_rate_ml_min = np.zeros_like(t_sim_min)
        plot_name = "Q(t) em função de t'"

        try:
            if self.mode_id == 1:
                A = params['A']
                q_rel = np.full_like(t_relative_sim_min, A)
                mask_on = t_sim_min >= t_start_min
                flow_rate_ml_min = np.where(mask_on, q_rel, 0.0)
                plot_name = f"Q(t') = {A:.3f}"

            elif self.mode_id == 2:
                A, B = params['A'], params['B']
                q_rel = A + B * t_relative_sim_min
                q_rel[q_rel < 0.0] = 0.0
                mask_on = t_sim_min >= t_start_min
                flow_rate_ml_min = np.where(mask_on, q_rel, 0.0)
                plot_name = f"Q(t') = {A:.3f} + {B:.3f}·t'"

            elif self.mode_id == 3:
                A, B = params['A'], params['B']
                q_rel = A * np.exp(B * t_relative_sim_min)
                q_rel[q_rel < 0.0] = 0.0
                mask_on = t_sim_min >= t_start_min
                flow_rate_ml_min = np.where(mask_on, q_rel, 0.0)
                plot_name = f"Q(t') = {A:.3f}·e^({B:.3f}·t')"

            elif self.mode_id == 4:
                coeffs = params['coeffs']
                q_rel = np.zeros_like(t_relative_sim_min)
                if len(coeffs) > 0:
                    coeffs_full = list(coeffs) + [0.0] * (self.NUM_POLY_COEFFS - len(coeffs))
                    acc = np.full_like(
                        t_relative_sim_min,
                        coeffs_full[self.NUM_POLY_COEFFS - 1]
                    )
                    for i in range(self.NUM_POLY_COEFFS - 2, -1, -1):
                        acc = acc * t_relative_sim_min + coeffs_full[i]
                    q_rel = acc
                q_rel[q_rel < 0.0] = 0.0
                mask_on = t_sim_min >= t_start_min
                flow_rate_ml_min = np.where(mask_on, q_rel, 0.0)
                plot_name = f"Q(t') polinomial (p0..p{len(coeffs)-1})"

            elif self.mode_id == 5:
                q_rel = np.interp(
                    t_relative_sim_min,
                    params["time_points"],
                    params["flow_points"]
                )
                q_rel[q_rel < 0.0] = 0.0
                mask_on = t_sim_min >= t_start_min
                flow_rate_ml_min = np.where(mask_on, q_rel, 0.0)
                plot_name = f"Q(t') piecewise ({params['num_segments']} pontos)"

        except Exception as e:
            self.main_window.log_signal.emit(f"Erro na simulação: {e}")
            return

        t_sim_sec = t_sim_min * 60.0
        flow_rate_ml_sec = flow_rate_ml_min / 60.0
        volume_ml = integrate.cumulative_trapezoid(
            flow_rate_ml_sec, t_sim_sec, initial=0.0
        )

        self.plot_flow.plot(t_sim_min, flow_rate_ml_min, clear=True, name=plot_name)
        self.plot_flow.addItem(
            pg.InfiniteLine(
                pos=t_start_min,
                angle=90,
                movable=False,
                pen=pg.mkPen(style=Qt.PenStyle.DotLine)
            )
        )
        self.plot_volume.plot(
            t_sim_min, volume_ml, clear=True, name="V(t) desde t_init"
        )
        self.plot_volume.addItem(
            pg.InfiniteLine(
                pos=t_start_min,
                angle=90,
                movable=False,
                pen=pg.mkPen(style=Qt.PenStyle.DotLine)
            )
        )

        self.main_window.log_signal.emit(f"Simulação Modo {self.mode_id} concluída.")

    # ======================================================================
    # Envio de comandos JSON para o SensorHub
    # ======================================================================
    def send_commands(self):
        """
        Salva preferências e envia payload JSON compatível com:
          - SensorHub: processJsonCommand (pump section)
          - Pump firmware: processJsonCommand (mode 1–5)
        """
        params = self._get_params_as_floats()
        if params is None:
            self.main_window.log_signal.emit(
                "Erro: Parâmetros inválidos. Comando não enviado."
            )
            return

        self.save_to_prefs()

        # Campos de tempo comuns (nomes devem ser 'init_t' e 'final_t')
        command = {
            "mode": int(self.mode_id),
            "init_t": float(params["t_initial"]),
            "final_t": float(params["t_final"])
        }

        # Modos 1–3: mapeiam diretamente em λ/φ esperados pelo firmware
        if self.mode_id == 1:
            command["lambda_const"] = float(params['A'])

        elif self.mode_id == 2:
            command["lambda_linear"] = float(params['A'])
            command["phi_linear"] = float(params['B'])

        elif self.mode_id == 3:
            command["lambda_exp"] = float(params['A'])
            command["phi_exp"] = float(params['B'])

        # Modo 4: polinômio p0..p20
        elif self.mode_id == 4:
            coeffs = params['coeffs']
            command["mode"] = 4
            for i, coeff in enumerate(coeffs):
                command[f"p{i}"] = float(coeff)

        # Modo 5: piecewise t0..tN, q0..qN, num_segments
        elif self.mode_id == 5:
            command["mode"] = 5
            num_segments = int(params["num_segments"])
            time_points = params["time_points"]
            flow_points = params["flow_points"]

            command["num_segments"] = num_segments

            # Flatten para t0..tN e q0..qN, conforme firmware
            for i in range(num_segments):
                command[f"t{i}"] = float(time_points[i])
                command[f"q{i}"] = float(flow_points[i])

        # Envio via camada de comunicação do main_window
        self.comm_handler.send_command(command)
        self.main_window.log_signal.emit(
            f"Modo Bomba {self.mode_id} enviado: {command}"
        )
        self.accept()

    # ======================================================================
    # Persistência em prefs do main_window
    # ======================================================================
    def save_to_prefs(self):
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
        mode_key = f"mode_{self.mode_id}"
        prefs_dict = (
            self.main_window.preferences
            .get("ExternalPump", {})
            .get("modes", {})
            .get(mode_key, {})
        )

        self.t_initial_edit.setText(prefs_dict.get("t_initial", "0"))
        self.t_final_edit.setText(prefs_dict.get("t_final", "60"))
        for key, edit in self.param_edits.items():
            default_val = "" if self.mode_id == 5 else "0"
            edit.setText(prefs_dict.get(f"param_{key}", default_val))

        if self.mode_id == 5 and not self.param_edits["num_segments"].text():
            self.param_edits["num_segments"].setText("2")

        self.check_sim_enable()
