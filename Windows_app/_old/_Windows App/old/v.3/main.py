#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# main.py - Main application file
import sys
import time
from PyQt6.QtWidgets import (
    QApplication, QMainWindow, QTabWidget, QMessageBox, QLineEdit, QWidget, QGroupBox, QStyle, QVBoxLayout, QLabel
)
from PyQt6.QtGui import QFont, QGuiApplication
from PyQt6.QtCore import QTimer, pyqtSignal, Qt

# Import communication handler
from communication.communication_handler import CommunicationHandler
# Import preferences functions and defaults
from config.preferences import load_preferences, save_preferences, default_preferences, _to_numeric
# Import UI pages
from ui.configurations_page import ConfigurationsPage
from ui.parameter_settings_page import ParameterSettingsPage
from ui.graphs_page import GraphsPage
#from kLa_methods.kla_cascade_page import KlaCascadePage # Lazy loaded
#from kLa_methods.kla_gassing_out_page import KlaGassingOutPage # Lazy loaded
# Import control logic
from kLa_methods.kla_cascade_control import run_kla_cascade_control
from kLa_methods.simple_cascade_control import AgitationCascadeControl, AerationCascadeControl

import faulthandler, sys, traceback
# Enable faulthandler to log crashes, redirecting to a file if stderr is not available.
try:
    log_file = open("crash_log.txt", "w")
    faulthandler.enable(file=log_file, all_threads=True)
    # Note: If sys.stderr is available, faulthandler might still use it for some output.
    # Passing the file ensures that in a headless environment, output has a place to go.
except Exception as e:
    print(f"Warning: Could not enable faulthandler with file logging: {e}")

def _excepthook(exctype, value, tb):
    msg = "".join(traceback.format_exception(exctype, value, tb))
    # Try to find the main window and log to it, otherwise print to stderr.
    try:
        from PyQt6.QtWidgets import QApplication
        # Using a signal is safer here, but for an excepthook, a direct call is often all we can do.
        w = next((w for w in QApplication.topLevelWidgets() if hasattr(w, "append_log_message")), None)
        if w:
            # Since this can be called from any thread, use a QTimer to be safe
            QTimer.singleShot(0, lambda: w.append_log_message(msg))
        else:
             print(msg, file=sys.stderr, flush=True)
    except Exception:
        print(msg, file=sys.stderr, flush=True)

sys.excepthook = _excepthook

class MainWindow(QMainWindow):
    # CORRECTED: Use a signal for thread-safe logging.
    log_signal = pyqtSignal(str)

    def __init__(self):
        super().__init__()
        self.setWindowTitle("App TECNAL")
        self.resize(1000, 480)

        # QDesktopWidget was removed in Qt6; use the primary screen instead
        screen = QGuiApplication.primaryScreen()
        if screen is not None:
            geom = screen.availableGeometry()
            x = geom.center().x() - self.width() // 2 - 125
            y = geom.center().y() - self.height() // 2 - 50
            self.move(x, y)

        # CORRECTED: Connect the signal to the slot that will update the UI
        self.log_signal.connect(self.append_log_message)

        # Inicializa o communication handler (onde os dados dos sensores são centralizados)
        self.comm_handler = CommunicationHandler()
        # CORRECTED: Pass the signal's emit method as the logger callback
        self.comm_handler.logger = self.log_signal.emit

        # Carrega as preferências (ou os defaults se não existirem)
        # load_preferences() agora também lida com a MIGRAÇÃO de formatos antigos
        self.preferences = load_preferences() or default_preferences()

        # Instancia as páginas de UI
        self.configurations_page = ConfigurationsPage(self.comm_handler)
        self.parameter_settings_page = ParameterSettingsPage(self.comm_handler)
        self.parameter_settings_page.main_window = self
        self.graphs_page = GraphsPage(self.comm_handler, self.parameter_settings_page, self.configurations_page)
        
        # ## [LAZY LOADING] Páginas kLa inicializadas como None
        self.kla_cascade_page = None # Será carregada sob demanda
        self._kla_cascade_page_loaded = False # Flag de controle
        self.kla_gassing_out_page = None # Será carregada sob demanda
        self._kla_gassing_out_page_loaded = False # Flag de controle
        
        # Instancia os novos controladores
        self.agit_cascade_control = AgitationCascadeControl(self)
        self.aer_cascade_control = AerationCascadeControl(self)

        # Cria um QTabWidget para conter as páginas
        self.main_tab_widget = QTabWidget()
        self.main_tab_widget.addTab(
            self.parameter_settings_page,
            self.style().standardIcon(QStyle.StandardPixmap.SP_ComputerIcon),
            "Ajustes de Parâmetros"
        )
        self.main_tab_widget.addTab(
            self.graphs_page,
            self.style().standardIcon(QStyle.StandardPixmap.SP_DesktopIcon),
            "Gráficos"
        )
        
        # ## [LAZY LOADING] Adiciona placeholders para as abas
        self.cascade_placeholder = QWidget()
        self.kla_cascade_tab_index = self.main_tab_widget.addTab(
            self.cascade_placeholder,
            self.style().standardIcon(QStyle.StandardPixmap.SP_FileDialogDetailedView),
            "Cascata kLa"
        )
        self.gassing_out_placeholder = QWidget()
        self.kla_gassing_out_tab_index = self.main_tab_widget.addTab(
            self.gassing_out_placeholder,
            self.style().standardIcon(QStyle.StandardPixmap.SP_MediaPlay),
            "kLa gassing-out"
        )
        
        self.main_tab_widget.addTab(
            self.configurations_page,
            self.style().standardIcon(QStyle.StandardPixmap.SP_MessageBoxInformation),
            "Configurações"
        )

        self.setCentralWidget(self.main_tab_widget)
        self.main_tab_widget.currentChanged.connect(self.on_tab_changed)
        
        # Carrega preferências na UI
        # Isso deve vir ANTES do setup_auto_save para que os valores
        # iniciais não disparem um salvamento
        self.apply_preferences(self.preferences)
        self.setup_auto_save() # Configura o salvamento automático

        # Conexões que não dependem das abas lazy-loaded
        self.configurations_page.theme_combo.currentTextChanged.connect(
            self.parameter_settings_page.update_all_block_styles
        )

        self.connect_parameter_checkbox_signals()
        self.graphs_page.main_window = self

        self.define_kLa_cascade_variables()
        # self.define_simple_cascade_variables() # REMOVIDO - agora tratado pelos controladores
        
        # Inicializa variáveis de estado para as cascatas simples (necessário para UI)
        try:
            self.simple_cascade_last_rpm = float(self.parameter_settings_page.motor_block.line_edit.text())
        except:
            self.simple_cascade_last_rpm = 200.0
        try:
            self.simple_cascade_last_flow = float(self.parameter_settings_page.flow_block.line_edit.text())
        except:
            self.simple_cascade_last_flow = 1.0

        
        self.cascade_timer = QTimer(self)
        # Conecta o timer à nova função mestre de controle
        self.cascade_timer.timeout.connect(self.run_cascade_controls)
        try:
            self.dataDelay = int(self.configurations_page.data_delay_edit.text())
        except Exception as e:
            self.dataDelay = 1000
            self.log_signal.emit(f"Data delay inválido, usando default 1000ms. Erro: {e}")
            
        self.comm_handler.set_poll_period_ms(self.dataDelay)
        self.cascade_timer.start(self.dataDelay)

    def on_tab_changed(self, index):
            """Chamado quando o usuário clica em uma aba."""
            
            if index == self.kla_cascade_tab_index and not self._kla_cascade_page_loaded:
                self.load_kla_cascade_tab() 
            
            # Verifica se é a aba "kLa gassing-out" E se ela ainda não foi carregada
            elif index == self.kla_gassing_out_tab_index and not self._kla_gassing_out_page_loaded:
                self.load_kla_gassing_out_tab()

    def load_kla_cascade_tab(self):
        """Importa, instancia e substitui o placeholder da aba Cascata kLa."""
        # 1. Define a flag
        self._kla_cascade_page_loaded = True

        # 2. Mostra "Carregando..."
        loading_widget = QGroupBox("Carregando Módulo")
        loading_layout = QVBoxLayout()
        loading_label = QLabel("Carregando módulo Cascata kLa...")
        loading_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        loading_layout.addWidget(loading_label)
        loading_widget.setLayout(loading_layout)
        
        self.main_tab_widget.removeTab(self.kla_cascade_tab_index)
        self.main_tab_widget.insertTab(
            self.kla_cascade_tab_index,
            loading_widget,
            self.style().standardIcon(QStyle.StandardPixmap.SP_FileDialogDetailedView),
            "Cascata kLa"
        )
        self.main_tab_widget.setCurrentIndex(self.kla_cascade_tab_index)
        QApplication.processEvents()

        # 3. Faz a importação local
        try:
            from kLa_methods.kla_cascade_page import KlaCascadePage
        except ImportError as e:
            loading_label.setText(f"Erro ao importar: {e}\nVerifique as dependências.")
            return

        # 4. Cria a instância real
        self.kla_cascade_page = KlaCascadePage(self.parameter_settings_page)

        # 5. (IMPORTANTE) Aplica as preferências que foram puladas no __init__
        #    Usando a NOVA estrutura 'ControlLoops'
        prefs = self.preferences
        if "ControlLoops" in prefs and "kLa_cascade" in prefs["ControlLoops"]:
            kla_config = prefs["ControlLoops"]["kLa_cascade"]
            self.kla_cascade_page.gradient_widget.set_cascade_config(kla_config.get("advanced", {}))
            self.kla_cascade_page.gradient_widget.set_pid_config(kla_config.get("pid", {}))

        if "GradientAscend" in prefs: # Configurações do gráfico
            self.kla_cascade_page.gradient_widget.set_graph_config(prefs["GradientAscend"])
        if "KlaInteractivePoints" in prefs:
            points = prefs["KlaInteractivePoints"]
            self.kla_cascade_page.interactive_kla_widget.set_points(points)

        # 6. (IMPORTANTE) Reconecta os sinais que removemos do __init__
        self.configurations_page.theme_combo.currentTextChanged.connect(
            self.kla_cascade_page.gradient_widget.update_theme_gradient
        )
        self.kla_cascade_page.gradient_widget.gradientAscentRun.connect(self.enable_kla_cascade)
        
        # Também reconecta o sinal de auto-save
        if hasattr(self, 'on_editing_finished'):
            self.kla_cascade_page.interactive_kla_widget.points_changed.connect(self.on_editing_finished)

        # 7. Substitui o "Carregando..." pela página real
        self.main_tab_widget.removeTab(self.kla_cascade_tab_index)
        self.main_tab_widget.insertTab(
            self.kla_cascade_tab_index,
            self.kla_cascade_page,
            self.style().standardIcon(QStyle.StandardPixmap.SP_FileDialogDetailedView),
            "Cascata kLa"
        )
        self.main_tab_widget.setCurrentIndex(self.kla_cascade_tab_index)

    def load_kla_gassing_out_tab(self):
        """Importa, instancia e substitui o placeholder da aba gassing-out."""
        # 1. Define a flag para não carregar de novo
        self._kla_gassing_out_page_loaded = True

        # 2. Mostra uma mensagem de "Carregando..."
        loading_widget = QGroupBox("Carregando Módulo")
        loading_layout = QVBoxLayout()
        loading_label = QLabel("Carregando módulo kLa Gassing-Out (pode levar um momento)...")
        loading_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        loading_layout.addWidget(loading_label)
        loading_widget.setLayout(loading_layout)
        
        # Substitui o placeholder pelo widget de carregamento
        self.main_tab_widget.removeTab(self.kla_gassing_out_tab_index)
        self.main_tab_widget.insertTab(
            self.kla_gassing_out_tab_index,
            loading_widget,
            self.style().standardIcon(QStyle.StandardPixmap.SP_MediaPlay),
            "kLa gassing-out"
        )
        self.main_tab_widget.setCurrentIndex(self.kla_gassing_out_tab_index)
        QApplication.processEvents() # Força a UI a atualizar e mostrar a mensagem

        # Antes de carregar gassing-out, garante que a cascata kLa (sua dependência) esteja carregada
        if not self._kla_cascade_page_loaded:
            loading_label.setText("Carregando dependência (Cascata kLa) primeiro...")
            QApplication.processEvents()
            self.load_kla_cascade_tab() # Carrega a dependência
            # Agora self.kla_cascade_page está disponível
            loading_label.setText("Carregando módulo kLa Gassing-Out...")
            QApplication.processEvents()

        # 3. Faz a importação pesada (torch, scipy, etc.)
        try:
            from kLa_methods.kla_gassing_out_page import KlaGassingOutPage
        except ImportError as e:
            loading_label.setText(f"Erro ao importar: {e}\nVerifique as dependências.")
            return

        # 4. Cria a instância real (agora é seguro, self.kla_cascade_page existe)
        self.kla_gassing_out_page = KlaGassingOutPage(
            self.comm_handler, self.kla_cascade_page
        )

        # 5. Aplica as preferências (isso carregará o modelo TCN)
        if "GassingOutConfig" in self.preferences:
            self.kla_gassing_out_page.gassing_out_config = self.preferences["GassingOutConfig"]
            self.kla_gassing_out_page._apply_config()

        # 6. Substitui o widget de "Carregando..." pela página real
        self.main_tab_widget.removeTab(self.kla_gassing_out_tab_index)
        self.main_tab_widget.insertTab(
            self.kla_gassing_out_tab_index,
            self.kla_gassing_out_page,
            self.style().standardIcon(QStyle.StandardPixmap.SP_MediaPlay),
            "kLa gassing-out"
        )
        self.main_tab_widget.setCurrentIndex(self.kla_gassing_out_tab_index)

    def define_kLa_cascade_variables(self):
        self.start_time = time.time()
        self.cascade_start_time = None
        self.last_oxygen_value = None
        self.current_oxygen = 0.0
        try:
            self.current_motor_rpm = int(float(self.parameter_settings_page.motor_block.line_edit.text()))
        except:
             self.current_motor_rpm = 100
        self.pid_integral = 0
        self.current_OUR = -1
        self.inner_error_buffer = []
        self.our_time_data = []
        self.our_our_data = []
        self.log_file = None
        self.last_inner_error = 0
        self.last_flow_command_time = 0

        self.pid_terms = {}             # holds latest {"P", "I", "D", "output", ...}
        self.last_pid_log_time = 0.0    # timestamp for throttling log prints

    def run_cascade_controls(self):
        """Função mestre chamada pelo QTimer para executar o controle PID ativo."""
        
        # Lê os dados uma vez
        self.comm_handler.read_and_parse_data()
        
        # Atualiza o valor de oxigênio atual
        try:
            self.current_oxygen = float(self.comm_handler.oxyReadVal)
        except (ValueError, TypeError):
            # Mantém o valor antigo se a leitura falhar
            pass 

        # Executa o controle apropriado
        if self.parameter_settings_page.oxy_kla_cascade_checkbox.isChecked():
            run_kla_cascade_control(self)
        
        elif self.parameter_settings_page.oxy_agit_cascade_checkbox.isChecked():
            self.agit_cascade_control.run_control()
            
        elif self.parameter_settings_page.oxy_aer_cascade_checkbox.isChecked():
            self.aer_cascade_control.run_control()
            
        # Se nenhuma cascata de O2 estiver ativa, zera o OUR
        elif not self.parameter_settings_page.oxy_kla_cascade_checkbox.isChecked():
             self.current_OUR = -1


    def append_log_message(self, message):
        """This is a slot that safely appends a message to the log widget."""
        try:
            # Also print to terminal for debugging and fallback
            print(message, flush=True)
            if getattr(self, "configurations_page", None) and getattr(self.configurations_page, "log_edit", None):
                self.configurations_page.log_edit.appendPlainText(message)
        except Exception as e:
            # This should not happen, but as a safeguard
            print(f"FATAL: Failed to append log to GUI: {message}\nError: {e}", file=sys.stderr)

    def write_log_row(self, row):
        if self.log_file:
            self.log_file.write(row)
            self.log_file.flush()

    def reset_preferences(self):
        reply = QMessageBox.question(
            self,
            "Resetar Preferências",
            "Você tem certeza que deseja resetar todas as preferências para os valores padrões?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
        )
        if reply == QMessageBox.StandardButton.Yes:
            self.preferences = default_preferences()
            self.apply_preferences(self.preferences)
            save_preferences(self.preferences)
            self.log_signal.emit("As preferências foram resetadas para os valores padrões.")

    def collect_preferences(self):
        # Carrega as preferências existentes (ou os defaults se não existirem)
        prefs = self.preferences.copy() # Começa com a cópia atual

        # 1. Coleta Configurações
        conf_page = self.configurations_page
        prefs["Configurations"] = {
            "ip_edit": conf_page.ip_edit.text(),
            "data_delay_edit": conf_page.data_delay_edit.text(),
            "oxy_cal_a": conf_page.oxy_cal_a.text(),
            "oxy_cal_b": conf_page.oxy_cal_b.text(),
            "ph_cal_slope": conf_page.ph_cal_slope.text(),
            "ph_cal_intercept": conf_page.ph_cal_intercept.text(),
            "com_port_edit": conf_page.com_port_combo.currentText(),
            "theme_combo": conf_page.theme_combo.currentText(),
            # Adiciona os parâmetros USB que faltavam
            "baud_rate_edit": conf_page.usb_baud_rate,
            "data_bits_edit": conf_page.usb_data_bits,
            "stop_bits_edit": conf_page.usb_stop_bits,
            "parity_edit": conf_page.usb_parity
        }

        # 2. Coleta ParameterSettings (Setpoints da UI)
        param_page = self.parameter_settings_page
        prefs["ParameterSettings"] = {
            "Temperature": {"line_edit": param_page.temp_block.line_edit.text()},
            "Motor": {
                "line_edit": param_page.motor_block.line_edit.text(),
                "cascade_min": param_page.motor_cascade_min_edit.text(),
                "cascade_max": param_page.motor_cascade_max_edit.text()
            },
            "Pressure": {"line_edit": param_page.pressure_block.line_edit.text()},
            "Oxygen": {"line_edit": param_page.oxy_block.line_edit.text()},
            "Flowmeter": {
                "line_edit": param_page.flow_block.line_edit.text(),
                "Max Flow:": param_page.flow_max_flow_edit.text(),
                "cascade_min": param_page.flow_cascade_min_edit.text(),
                "cascade_max": param_page.flow_cascade_max_edit.text()
            },
            "Distance": {
                "line_edit": param_page.distance_block.line_edit.text(),
                # Adiciona os campos extras
                "Delay Início (s):": param_page.distance_block.extra_edits["Delay Início (s):"].text(),
                "Pulso ON (s):": param_page.distance_block.extra_edits["Pulso ON (s):"].text(),
                "Intervalo (s):": param_page.distance_block.extra_edits["Intervalo (s):"].text()
            },
            "pH": {
                "ph_setpoint": param_page.ph_block.ph_setpoint_edit.text(),
                "ph_error": param_page.ph_block.ph_error_edit.text(),
                "ph_op_time": param_page.ph_block.ph_op_time_edit.text(),
                "ph_disable_time": param_page.ph_block.ph_disable_time_edit.text(),
                "ph_speed": param_page.ph_block.ph_speed_edit.text()
            },
            "Antifoam": {
                "antifoam_op_time": param_page.antifoam_block.line_edit.text(),
                "antifoam_disable_time": param_page.antifoam_block.extra_edits["Tempo Desativado (s):"].text(),
                "antifoam_speed": param_page.antifoam_block.extra_edits["Velocidade (%):"].text()
            },
            "Nutrient": {
                "nutri_op_time": param_page.nutri_block.nutri_op_time_edit.text(),
                "nutri_disable_time": param_page.nutri_block.nutri_disable_time_edit.text(),
                "nutri_op_cycle": param_page.nutri_block.nutri_op_cycle_edit.text(),
                "nutri_disable_cycle": param_page.nutri_block.nutri_disable_cycle_edit.text(),
                "nutri_speed": param_page.nutri_block.nutri_speed_edit.text()
            },
            "Agitator": {
                "use_auto": param_page.agitator_block.checkbox.isChecked(),
                "percent": param_page.agitator_block.agit_percent_edit.text(),
                "re_enable_pot": param_page.agitator_block.agit_repot_checkbox.isChecked()
            },
            "Biomass": {
                "comm_on": param_page.biomass_block.checkbox.isChecked(),
                "low_thresh": param_page.biomass_block.low_thresh_edit.text(),
                "high_thresh": param_page.biomass_block.high_thresh_edit.text(),
                "opt_thresh": param_page.biomass_block.opt_thresh_edit.text()
            },
            "ExternalPump": {
                "comm_on": param_page.extern_pump_block.checkbox.isChecked(),
                "manual_speed": param_page.extern_pump_manual_speed_edit.text()
            }
        }
        
        # 3. Coleta Gassing-Out
        if self.kla_gassing_out_page is not None:
            prefs["GassingOutConfig"] = self.kla_gassing_out_page.gassing_out_config
        # else: mantém o valor antigo em prefs

        # 4. Coleta kLa Pontos Interativos e Config Gráfico
        if self.kla_cascade_page is not None:
            prefs["KlaInteractivePoints"] = self.kla_cascade_page.interactive_kla_widget.get_points()
            prefs["GradientAscend"] = self.kla_cascade_page.gradient_widget.get_graph_config()
        # else: mantém os valores antigos em prefs
        
        # Garante que o container principal existe
        if "ControlLoops" not in prefs:
            prefs["ControlLoops"] = {}

        # a. Coleta kLa (PID + Avançado)
        if self.kla_cascade_page is not None:
            # get_..._config() agora retorna dicts numéricos (após correção)
            prefs["ControlLoops"]["kLa_cascade"] = {
                "pid": self.kla_cascade_page.gradient_widget.get_pid_config(),
                "advanced": self.kla_cascade_page.gradient_widget.get_cascade_config()
            }
        # else: mantém o valor antigo em prefs["ControlLoops"]["kLa_cascade"]

        # b. Coleta Agitação (PID + Avançado)
        # Mapeia dos QLineEdits para o dict numérico
        try:
            prefs["ControlLoops"]["agitation_cascade"] = {
                "pid": {
                    "Kp": _to_numeric(param_page.pid_agit_kp_edit.text(), 0.1),
                    "Ki": _to_numeric(param_page.pid_agit_ki_edit.text(), 0.002),
                    "Kd": _to_numeric(param_page.pid_agit_kd_edit.text(), 0.75)
                },
                "advanced": {
                    "history_pts": int(_to_numeric(param_page.adv_agit_hist_pts_edit.text(), 30)),
                    "median_filter_win": int(_to_numeric(param_page.adv_agit_median_win_edit.text(), 5)),
                    "min_pts_regression": int(_to_numeric(param_page.adv_agit_min_pts_reg_edit.text(), 10)),
                    "prediction_horizon_s": _to_numeric(param_page.adv_agit_pred_horiz_edit.text(), 30.0),
                    "outer_loop_gain": _to_numeric(param_page.adv_agit_outer_gain_edit.text(), 0.1),
                    "derivative_tau_s": _to_numeric(param_page.adv_agit_deriv_tau_edit.text(), 40.0),
                    "max_integral": _to_numeric(param_page.adv_agit_max_int_edit.text(), 200.0),
                    "min_integral": _to_numeric(param_page.adv_agit_min_int_edit.text(), -200.0),
                    "integral_window": int(_to_numeric(param_page.adv_agit_int_win_edit.text(), 60))
                }
            }
        except RuntimeError: # Ocorre se a página for destruída
            print("Aviso: Widgets de PID/Cascata Agitação não acessíveis durante a coleta.")
            if "agitation_cascade" not in prefs["ControlLoops"]:
                 prefs["ControlLoops"]["agitation_cascade"] = default_preferences()["ControlLoops"]["agitation_cascade"]

        # c. Coleta Aeração (PID + Avançado)
        try:
            prefs["ControlLoops"]["aeration_cascade"] = {
                "pid": {
                    "Kp": _to_numeric(param_page.pid_aer_kp_edit.text(), 0.001),
                    "Ki": _to_numeric(param_page.pid_aer_ki_edit.text(), 0.0002),
                    "Kd": _to_numeric(param_page.pid_aer_kd_edit.text(), 0.0075)
                },
                "advanced": {
                    "history_pts": int(_to_numeric(param_page.adv_aer_hist_pts_edit.text(), 30)),
                    "median_filter_win": int(_to_numeric(param_page.adv_aer_median_win_edit.text(), 5)),
                    "min_pts_regression": int(_to_numeric(param_page.adv_aer_min_pts_reg_edit.text(), 10)),
                    "prediction_horizon_s": _to_numeric(param_page.adv_aer_pred_horiz_edit.text(), 30.0),
                    "outer_loop_gain": _to_numeric(param_page.adv_aer_outer_gain_edit.text(), 0.1),
                    "derivative_tau_s": _to_numeric(param_page.adv_aer_deriv_tau_edit.text(), 40.0),
                    "max_integral": _to_numeric(param_page.adv_aer_max_int_edit.text(), 200.0),
                    "min_integral": _to_numeric(param_page.adv_aer_min_int_edit.text(), -200.0),
                    "integral_window": int(_to_numeric(param_page.adv_aer_int_win_edit.text(), 60))
                }
            }
        except RuntimeError:
            print("Aviso: Widgets de PID/Cascata Aeração não acessíveis durante a coleta.")
            if "aeration_cascade" not in prefs["ControlLoops"]:
                 prefs["ControlLoops"]["aeration_cascade"] = default_preferences()["ControlLoops"]["aeration_cascade"]

        # 6. Remove chaves antigas (se a migração falhou ou foi parcial)
        prefs.pop("CascadeConfig", None)
        prefs.pop("PIDConfig", None)
        prefs.pop("pid_agitation", None)
        prefs.pop("pid_aeration", None)

        if "ExternalPump" not in prefs:
            prefs["ExternalPump"] = {}
        saved_modes = self.preferences.get("ExternalPump", {}).get("modes", {})
        prefs["ExternalPump"]["modes"] = saved_modes
        
        if "ExternalPump" in prefs["ParameterSettings"]:
            # Transfere os valores para a chave de nível superior
            ui_prefs = prefs["ParameterSettings"].pop("ExternalPump")
            prefs["ExternalPump"]["comm_on"] = ui_prefs.get("comm_on")
            prefs["ExternalPump"]["manual_speed"] = ui_prefs.get("manual_speed")

        return prefs

    def apply_preferences(self, prefs):
        # 1. Aplica as preferências de configurações
        if "Configurations" in prefs:
            conf = prefs["Configurations"]
            self.configurations_page.ip_edit.setText(conf.get("ip_edit", "192.168.4.1"))
            self.configurations_page.data_delay_edit.setText(conf.get("data_delay_edit", "1000"))
            self.configurations_page.oxy_cal_a.setText(conf.get("oxy_cal_a", "0.0305473419314"))
            self.configurations_page.oxy_cal_b.setText(conf.get("oxy_cal_b", "-25.09136520919"))
            self.configurations_page.ph_cal_slope.setText(conf.get("ph_cal_slope", "1.0"))
            self.configurations_page.ph_cal_intercept.setText(conf.get("ph_cal_intercept", "0.0"))
            
            # Aplica USB
            self.configurations_page.com_port_combo.setCurrentText(conf.get("com_port_edit", "COM5"))
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
            except Exception as e:
                self.log_signal.emit(f"Erro ao aplicar calibração de O2: {e}")
            # Atualiza a calibração de pH
            try:
                slope = float(self.configurations_page.ph_cal_slope.text())
                intercept = float(self.configurations_page.ph_cal_intercept.text())
                self.comm_handler.set_pH_calibration(slope, intercept)
            except Exception as e:
                self.log_signal.emit(f"Erro ao aplicar calibração de pH: {e}")

        # 2. Aplica kLa (se a aba estiver carregada)
        if self.kla_cascade_page is not None:
            if "ControlLoops" in prefs and "kLa_cascade" in prefs["ControlLoops"]:
                kla_config = prefs["ControlLoops"]["kLa_cascade"]
                self.kla_cascade_page.gradient_widget.set_cascade_config(kla_config.get("advanced", {}))
                self.kla_cascade_page.gradient_widget.set_pid_config(kla_config.get("pid", {}))

            if "GradientAscend" in prefs:
                self.kla_cascade_page.gradient_widget.set_graph_config(prefs["GradientAscend"])
            if "KlaInteractivePoints" in prefs:
                points = prefs["KlaInteractivePoints"]
                self.kla_cascade_page.interactive_kla_widget.set_points(points)

        # 3. Aplica PIDs/Avançado da Cascata Simples
        if "ControlLoops" in prefs:
            param_page = self.parameter_settings_page
            
            # a. Agitação
            agit_config = prefs["ControlLoops"].get("agitation_cascade", {})
            agit_pid = agit_config.get("pid", {})
            agit_adv = agit_config.get("advanced", {})

            param_page.pid_agit_kp_edit.setText(str(agit_pid.get("Kp", 0.1)))
            param_page.pid_agit_ki_edit.setText(str(agit_pid.get("Ki", 0.002)))
            param_page.pid_agit_kd_edit.setText(str(agit_pid.get("Kd", 0.75)))
            
            param_page.adv_agit_hist_pts_edit.setText(str(agit_adv.get("history_pts", 30)))
            param_page.adv_agit_median_win_edit.setText(str(agit_adv.get("median_filter_win", 5)))
            param_page.adv_agit_min_pts_reg_edit.setText(str(agit_adv.get("min_pts_regression", 10)))
            param_page.adv_agit_pred_horiz_edit.setText(str(agit_adv.get("prediction_horizon_s", 30.0)))
            param_page.adv_agit_outer_gain_edit.setText(str(agit_adv.get("outer_loop_gain", 0.1)))
            param_page.adv_agit_deriv_tau_edit.setText(str(agit_adv.get("derivative_tau_s", 40.0)))
            param_page.adv_agit_max_int_edit.setText(str(agit_adv.get("max_integral", 200.0)))
            param_page.adv_agit_min_int_edit.setText(str(agit_adv.get("min_integral", -200.0)))
            param_page.adv_agit_int_win_edit.setText(str(agit_adv.get("integral_window", 60)))

            # b. Aeração
            aer_config = prefs["ControlLoops"].get("aeration_cascade", {})
            aer_pid = aer_config.get("pid", {})
            aer_adv = aer_config.get("advanced", {})

            param_page.pid_aer_kp_edit.setText(str(aer_pid.get("Kp", 0.001)))
            param_page.pid_aer_ki_edit.setText(str(aer_pid.get("Ki", 0.0002)))
            param_page.pid_aer_kd_edit.setText(str(aer_pid.get("Kd", 0.0075)))
            
            param_page.adv_aer_hist_pts_edit.setText(str(aer_adv.get("history_pts", 30)))
            param_page.adv_aer_median_win_edit.setText(str(aer_adv.get("median_filter_win", 5)))
            param_page.adv_aer_min_pts_reg_edit.setText(str(aer_adv.get("min_pts_regression", 10)))
            param_page.adv_aer_pred_horiz_edit.setText(str(aer_adv.get("prediction_horizon_s", 30.0)))
            param_page.adv_aer_outer_gain_edit.setText(str(aer_adv.get("outer_loop_gain", 0.1)))
            param_page.adv_aer_deriv_tau_edit.setText(str(aer_adv.get("derivative_tau_s", 40.0)))
            param_page.adv_aer_max_int_edit.setText(str(aer_adv.get("max_integral", 200.0)))
            param_page.adv_aer_min_int_edit.setText(str(aer_adv.get("min_integral", -200.0)))
            param_page.adv_aer_int_win_edit.setText(str(aer_adv.get("integral_window", 60)))

        # 4. Aplica as preferências da ParameterSettingsPage (Setpoints)
        if "ParameterSettings" in prefs:
            par = prefs["ParameterSettings"]
            param_page = self.parameter_settings_page
            
            param_page.temp_block.line_edit.setText(par.get("Temperature", {}).get("line_edit", "25"))
            
            motor_par = par.get("Motor", {})
            param_page.motor_block.line_edit.setText(motor_par.get("line_edit", "100"))
            try:
                self.parameter_settings_page._motor_last_valid = int(float(self.parameter_settings_page.motor_block.line_edit.text()))
            except Exception:
                self.parameter_settings_page._motor_last_valid = 100
            param_page.motor_cascade_min_edit.setText(motor_par.get("cascade_min", "200"))
            param_page.motor_cascade_max_edit.setText(motor_par.get("cascade_max", "1000"))
            
            param_page.pressure_block.line_edit.setText(par.get("Pressure", {}).get("line_edit", "100"))
            param_page.oxy_block.line_edit.setText(par.get("Oxygen", {}).get("line_edit", "50"))
            
            flow_par = par.get("Flowmeter", {})
            param_page.flow_block.line_edit.setText(flow_par.get("line_edit", "5"))
            param_page.flow_max_flow_edit.setText(flow_par.get("Max Flow:", "50"))
            param_page.flow_cascade_min_edit.setText(flow_par.get("cascade_min", "1"))
            param_page.flow_cascade_max_edit.setText(flow_par.get("cascade_max", "20"))
            
            dist_par = par.get("Distance", {})
            param_page.distance_block.line_edit.setText(dist_par.get("line_edit", "100"))
            param_page.distance_block.extra_edits["Delay Início (s):"].setText(dist_par.get("Delay Início (s):", "1"))
            param_page.distance_block.extra_edits["Pulso ON (s):"].setText(dist_par.get("Pulso ON (s):", "1"))
            param_page.distance_block.extra_edits["Intervalo (s):"].setText(dist_par.get("Intervalo (s):", "5"))
            
            ph_par = par.get("pH", {})
            param_page.ph_block.ph_setpoint_edit.setText(ph_par.get("ph_setpoint", "7"))
            param_page.ph_block.ph_error_edit.setText(ph_par.get("ph_error", "0.17"))
            param_page.ph_block.ph_op_time_edit.setText(ph_par.get("ph_op_time", "10"))
            param_page.ph_block.ph_disable_time_edit.setText(ph_par.get("ph_disable_time", "10"))
            param_page.ph_block.ph_speed_edit.setText(ph_par.get("ph_speed", "99"))
            
            antifoam_par = par.get("Antifoam", {})
            param_page.antifoam_block.line_edit.setText(antifoam_par.get("antifoam_op_time", "5"))
            param_page.antifoam_block.extra_edits["Tempo Desativado (s):"].setText(antifoam_par.get("antifoam_disable_time", "20"))
            param_page.antifoam_block.extra_edits["Velocidade (%):"].setText(antifoam_par.get("antifoam_speed", "99"))
            
            biomass_par = par.get("Biomass", {})
            param_page.biomass_block.checkbox.setChecked(bool(biomass_par.get("comm_on", False)))
            param_page.biomass_block.low_thresh_edit.setText(biomass_par.get("low_thresh", "10000"))
            param_page.biomass_block.high_thresh_edit.setText(biomass_par.get("high_thresh", "40000"))
            param_page.biomass_block.opt_thresh_edit.setText(biomass_par.get("opt_thresh", "25000"))

            nutri_par = par.get("Nutrient", {})
            param_page.nutri_block.nutri_op_time_edit.setText(nutri_par.get("nutri_op_time", "999"))
            param_page.nutri_block.nutri_disable_time_edit.setText(nutri_par.get("nutri_disable_time", "1"))
            param_page.nutri_block.nutri_op_cycle_edit.setText(nutri_par.get("nutri_op_cycle", "500"))
            param_page.nutri_block.nutri_disable_cycle_edit.setText(nutri_par.get("nutri_disable_cycle", "1M"))
            param_page.nutri_block.nutri_speed_edit.setText(nutri_par.get("nutri_speed", "99"))
            
            agit_par = par.get("Agitator", {})
            param_page.agitator_block.checkbox.setChecked(bool(agit_par.get("use_auto", True)))
            param_page.agitator_block.agit_percent_edit.setText(str(agit_par.get("percent", "60")))
            param_page.agitator_block.agit_repot_checkbox.setChecked(bool(agit_par.get("re_enable_pot", True)))

        # 5. Aplica Gassing-Out (se carregado)
        if "GassingOutConfig" in prefs:
            if self.kla_gassing_out_page is not None:
                self.kla_gassing_out_page.gassing_out_config = prefs["GassingOutConfig"]
                self.kla_gassing_out_page._apply_config()
            # Se for None, load_kla_gassing_out_tab() cuidará disso quando for clicado
        
        # 6. Aplica Bomba Externa (se a página estiver carregada)
        if "ExternalPump" in prefs and hasattr(self.parameter_settings_page, 'extern_pump_block'):
            try:
                pump_prefs = prefs["ExternalPump"]
                self.parameter_settings_page.extern_pump_block.checkbox.setChecked(pump_prefs.get("comm_on", False))
                self.parameter_settings_page.extern_pump_manual_speed_edit.setText(pump_prefs.get("manual_speed", "50"))
                
                # O carregamento dos modos (param_a, param_b) é feito
                # pela própria PumpModeWindow quando ela é aberta.
            except Exception as e:
                self.log_signal.emit(f"Erro ao aplicar prefs da Bomba Externa: {e}")

    def setup_auto_save(self):
        # Conecta todos os QLineEdits "padrão" (visíveis nas abas)
        for le in self.findChildren(QLineEdit):
            try:
                le.editingFinished.disconnect(self.on_editing_finished)
            except TypeError:
                pass # Não estava conectado
            le.editingFinished.connect(self.on_editing_finished)
        
        # Adiciona manualmente TODOS os QLineEdits do diálogo pop-up
        try:
            all_dialog_edits = [
                *self.parameter_settings_page.agit_pid_edits.values(),
                *self.parameter_settings_page.agit_adv_edits.values(),
                *self.parameter_settings_page.aer_pid_edits.values(),
                *self.parameter_settings_page.aer_adv_edits.values()
            ]
            for edit in all_dialog_edits:
                # Remove conexões antigas para evitar duplicatas (garantia)
                try: edit.editingFinished.disconnect(self.on_editing_finished)
                except TypeError: pass 
                # Conecta
                edit.editingFinished.connect(self.on_editing_finished)
        except Exception as e:
            self.log_signal.emit(f"Erro ao conectar sinais do diálogo de PID: {e}")

        # A conexão para kla_cascade_page.interactive_kla_widget.points_changed
        # é feita dentro de load_kla_cascade_tab() para garantir que a página exista.

    # main.py

    def on_editing_finished(self):
        s = self.sender()
        try:
            # Enforce validation + possible reversion for Motor before saving
            if s is self.parameter_settings_page.motor_block.line_edit:
                self.parameter_settings_page.send_motor()
        except Exception as e:
            self.log_signal.emit(f"Erro na validação antes do salvamento: {e}")

        # Now collect and persist preferences using the post-validation UI state
        prefs = self.collect_preferences()
        self.preferences = prefs
        save_preferences(prefs)

        # Finally notify controllers to reload
        try:
            # Verifica se a página kLa já foi carregada
            if self.kla_cascade_page is not None and self._kla_cascade_page_loaded:
                # Pega as configs kLa recém-salvas
                if "ControlLoops" in self.preferences and "kLa_cascade" in self.preferences["ControlLoops"]:
                    kla_config = self.preferences["ControlLoops"]["kLa_cascade"]
                    
                    # "Empurra" (push) as novas configs para o widget
                    gw = self.kla_cascade_page.gradient_widget
                    gw.set_pid_config(kla_config.get("pid", {}))
                    gw.set_cascade_config(kla_config.get("advanced", {}))
                    
                    # Atualiza o pid_config no controlador principal (para o próximo ciclo)
                    gw.pid_config = kla_config.get("pid", {})
                    
                    self.log_signal.emit("Configurações da Cascata kLa recarregadas.")

            if hasattr(self, 'agit_cascade_control'):
                self.agit_cascade_control.reload_pid_config()
            if hasattr(self, 'aer_cascade_control'):
                self.aer_cascade_control.reload_pid_config()
                
        except Exception as e:
            self.log_signal.emit(f"Erro ao recarregar configs do PID: {e}")

    def enable_kla_cascade(self):
        self.parameter_settings_page.oxy_kla_cascade_checkbox.setEnabled(True)

    def reset_oxygen_integrator(self):
        # 1. Reseta PID kLa
        if hasattr(self, "pid_integral_buffer") and self.pid_integral_buffer:
            try:
                self.pid_integral_buffer.clear()
            except Exception:
                pass
        self.pid_integral = 0
        if hasattr(self, "inner_error_buffer") and self.inner_error_buffer:
            try:
                self.inner_error_buffer.clear()
            except Exception:
                pass
        self.last_inner_error = 0.0
        self.filtered_derivative = 0.0
        if hasattr(self, "oxy_hist") and self.oxy_hist:
            self.oxy_hist.clear()
            
        # 2. Reseta PIDs Simples (AGORA USANDO OS ATRIBUTOS CORRETOS)
        
        # a. Agitação
        if hasattr(self, "pid_agit_integral_buffer") and self.pid_agit_integral_buffer:
            getattr(self, "pid_agit_integral_buffer").clear()
        if hasattr(self, "oxy_hist_agit") and self.oxy_hist_agit:
            getattr(self, "oxy_hist_agit").clear()
        setattr(self, "last_agit_error", 0.0)
        setattr(self, "filtered_deriv_agit", 0.0)

        # b. Aeração
        if hasattr(self, "pid_aer_integral_buffer") and self.pid_aer_integral_buffer:
            getattr(self, "pid_aer_integral_buffer").clear()
        if hasattr(self, "oxy_hist_aera") and self.oxy_hist_aera:
            getattr(self, "oxy_hist_aera").clear()
        setattr(self, "last_aer_error", 0.0)
        setattr(self, "filtered_deriv_aera", 0.0)

        try:
            self.log_signal.emit("Integral do PID (O₂) zerada para todas as cascatas.")
        except Exception:
            pass

    def update_graphs_tab_state(self):
        active = any([
            self.parameter_settings_page.temp_block.checkbox.isChecked(),
            self.parameter_settings_page.motor_block.checkbox.isChecked(),
            self.parameter_settings_page.ph_block.checkbox.isChecked(),
            self.parameter_settings_page.oxy_block.checkbox.isChecked(),
            self.parameter_settings_page.antifoam_block.checkbox.isChecked(),
            self.parameter_settings_page.pressure_block.checkbox.isChecked(),
            self.parameter_settings_page.flow_block.checkbox.isChecked(),
            self.parameter_settings_page.distance_block.checkbox.isChecked(),
            self.parameter_settings_page.biomass_block.checkbox.isChecked(),
            self.parameter_settings_page.extern_pump_block.checkbox.isChecked()
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
            self.parameter_settings_page.distance_block.checkbox,
            self.parameter_settings_page.biomass_block.checkbox,
            self.parameter_settings_page.extern_pump_block.checkbox
        ]
        for cb in checkboxes:
            cb.toggled.connect(self.update_graphs_tab_state)

    def showEvent(self, event):
        self.update_graphs_tab_state()
        self.parameter_settings_page.update_all_block_styles()
        super().showEvent(event)

    def closeEvent(self, event):
        try:
            self.cascade_timer.stop()
        except Exception:
            pass
        try:
            self.comm_handler.shutdown()
        except Exception:
            pass
        super().closeEvent(event)

if __name__ == "__main__":
    app = QApplication(sys.argv)
    app.setFont(QFont("Roboto", 10))
    window = MainWindow()
    window.show()
    sys.exit(app.exec())