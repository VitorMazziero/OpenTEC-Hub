#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# main.py - Main application file
import sys
import time
import os

os.environ["QT_LOGGING_RULES"] = "qt.multimedia.ffmpeg.*=false;qt.multimedia.*=false"
os.environ["QT_MULTIMEDIA_PREFERRED_PLUGINS"] = "windowsmediafoundation"

from PySide6.QtWidgets import (
    QApplication, QMainWindow, QTabWidget, QMessageBox, QLineEdit, QWidget, QGroupBox, QStyle, QVBoxLayout, QLabel
)
from PySide6.QtGui import QFont, QGuiApplication
from PySide6.QtCore import QTimer, Signal, Qt

# Import communication handler
from communication.connection_manager import ConnectionManager
from communication.connection_manager import ConnectionState
# Import preferences functions and defaults
from config.preferences import load_preferences, save_preferences, default_preferences, _to_numeric
# Import UI pages
from ui.configurations_page import ConfigurationsPage
from ui.parameter_settings_page import ParameterSettingsPage
from ui.graphs_page import GraphsPage
# Import control logic
from kLa_methods.kla_cascade_control import run_kla_cascade_control
from kLa_methods.simple_cascade_control import AgitationCascadeControl, AerationCascadeControl

import faulthandler, sys, traceback
# Enable faulthandler to log crashes, redirecting to a file if stderr is not available.
try:
    log_file = open("crash_log.txt", "a")
    log_file.write(f"\n\n--- Session Started: {time.strftime('%Y-%m-%d %H:%M:%S')} ---\n")
    faulthandler.enable(file=log_file, all_threads=True)
    # Note: If sys.stderr is available, faulthandler might still use it for some output.
    # Passing the file ensures that in a headless environment, output has a place to go.
except Exception as e:
    print(f"Warning: Could not enable faulthandler with file logging: {e}")

def _excepthook(exctype, value, tb):
    msg = "".join(traceback.format_exception(exctype, value, tb))
    # Try to find the main window and log to it, otherwise print to stderr.
    try:
        from PySide6.QtWidgets import QApplication
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
    log_signal = Signal(str)

    def __init__(self):
        super().__init__()
        self.setWindowTitle("App TECNAL")
        self.resize(1000, 480)

        self._session_log_file = None
        self._session_log_path = None
        self._open_session_log_file()

        # QDesktopWidget was removed in Qt6; use the primary screen instead
        screen = QGuiApplication.primaryScreen()
        if screen is not None:
            geom = screen.availableGeometry()
            x = geom.center().x() - self.width() // 2 - 125
            y = geom.center().y() - self.height() // 2 - 50
            self.move(x, y)

        # Connect the signal to the slot that will update the UI
        self.log_signal.connect(self.append_log_message)

        # Inicializa o communication handler (onde os dados dos sensores são centralizados)
        self.comm_handler = ConnectionManager(parent=self)
        # Log messages come through a proper Qt signal now
        self.comm_handler.log_message.connect(self.log_signal.emit)

        # Carrega as preferências (ou os defaults se não existirem)
        # load_preferences() agora também lida com a MIGRAÇÃO de formatos antigos
        self.preferences = load_preferences() or default_preferences()

        # Instancia as páginas de UI
        self.configurations_page = ConfigurationsPage(self.comm_handler)
        self.parameter_settings_page = ParameterSettingsPage(self.comm_handler)
        self.parameter_settings_page.main_window = self
        self.graphs_page = GraphsPage(self.comm_handler, self.parameter_settings_page, self.configurations_page)

        self._save_timer = QTimer(self)
        self._save_timer.setSingleShot(True)
        self._save_timer.setInterval(1000)
        self._save_timer.timeout.connect(self._do_save_preferences)

        try:
            # Conecta os campos de gás proporcional para recálculo imediato
            gas_prop_block = self.parameter_settings_page.extern_pump_block
            gas_prop_block.vol_inicial_edit.editingFinished.connect(self.trigger_proportional_flow_recalc)
            gas_prop_block.vvm_edit.editingFinished.connect(self.trigger_proportional_flow_recalc)
        except Exception as e:
            self.comm_handler.log_erro(f"Erro ao conectar sinais de gás proporcional: {e}")

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
        self.graphs_page.refresh_graph_layout()

        # Conexões que não dependem das abas lazy-loaded
        self.configurations_page.theme_combo.currentTextChanged.connect(
            self.parameter_settings_page.update_all_block_styles
        )

        self.cascade_timer = QTimer(self)
        # Conecta o timer à nova função mestre de controle
        self.cascade_timer.timeout.connect(self.run_cascade_controls)
        try:
            self.dataDelay = int(self.configurations_page.data_delay_edit.text())
        except Exception as e:
            self.dataDelay = 1000
            self.comm_handler.log_erro(f"Data delay inválido, usando default 1000ms. Erro: {e}")

        self.connect_parameter_checkbox_signals()
        self.graphs_page.main_window = self
        self.graphs_page.timer.start(self.dataDelay)

        self.define_kLa_cascade_variables()
        
        # Inicializa variáveis de estado para as cascatas simples (necessário para UI)
        try:
            self.simple_cascade_last_rpm = float(self.parameter_settings_page.motor_block.line_edit.text())
        except:
            self.simple_cascade_last_rpm = 200.0
        try:
            self.simple_cascade_last_flow = float(self.parameter_settings_page.flow_block.line_edit.text())
        except:
            self.simple_cascade_last_flow = 1.0
            
        self.comm_handler.set_poll_period_ms(self.dataDelay)
        self.cascade_timer.start(self.dataDelay)
        self.last_gas_prop_time = 0.0
        # 1. Force immediate application of block styles
        self.parameter_settings_page.update_all_block_styles()
        # 2. Set initial state of Graphs tab based on active parameters
        self.update_graphs_tab_state()

    def schedule_preferences_save(self):
        self.preferences = self.collect_preferences()
        self._save_timer.start()

    def _do_save_preferences(self):
        self.persist_and_reload_controllers()

    def _open_session_log_file(self):
        try:
            log_folder = "command_logs"
            os.makedirs(log_folder, exist_ok=True)

            self._session_log_path = os.path.join(
                log_folder,
                time.strftime("command_log_%Y-%m-%d_%H-%M-%S.txt")
            )
            self._session_log_file = open(self._session_log_path, "a", encoding="utf-8")
        except Exception as e:
            self._session_log_file = None
            self._session_log_path = None
            print(f"Failed to open session log file: {e}", file=sys.stderr)

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
            
        if "KlaProfiles" in prefs:
            profiles = prefs["KlaProfiles"]
            selected = prefs.get("SelectedKlaProfile", "Default")
            self.kla_cascade_page.set_profiles(profiles, selected)

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
        self.comm_handler.read_and_parse()
        
        # Atualiza o valor de oxigênio atual
        try:
            self.current_oxygen = float(self.comm_handler.oxyReadVal)
        except (ValueError, TypeError):
            # Mantém o valor antigo se a leitura falhar
            pass 
        
        # --- Controle de Gás Proporcional (Bomba Externa) ---
        current_time = time.time()
        try:
            try:
                gas_prop_interval = float(self.preferences.get("Configurations", {}).get("gas_prop_interval_s", "10.0"))
            except ValueError:
                gas_prop_interval = 10.0
            # Verifica se está ativo e se passaram 10s (para baixa frequência)
            if (self.parameter_settings_page.extern_pump_block.gas_prop_checkbox.isChecked() and
            current_time - self.last_gas_prop_time > gas_prop_interval):
                
                self.last_gas_prop_time = current_time
                self.calculate_and_send_proportional_flow()
        
        except Exception as e:
            # Loga erro, mas não para o loop principal
            self.comm_handler.log_erro(f"Erro no loop de Gás Proporcional: {e}")

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

    def trigger_proportional_flow_recalc(self):
        """
        Slot para ser chamado pelo sinal editingFinished.
        Executa o cálculo e reseta o timer de 10s para evitar envio duplicado.
        """
        # Só executa se o modo proporcional estiver realmente ativo
        if self.parameter_settings_page.extern_pump_block.gas_prop_checkbox.isChecked():
            self.comm_handler.log_aviso("Gás proporcional: recalculando_flow devido à mudança de parâmetro.")
    
            # Reseta o timer para que o loop principal não o execute novamente em breve
            self.last_gas_prop_time = time.time() 
            
            # Executa o cálculo agora
            self.calculate_and_send_proportional_flow()

    def calculate_and_send_proportional_flow(self):
        """
        Calcula a vazão de gás com base no volume da bomba externa e envia para o fluxômetro.
        Fórmula: Vazão [L/min] = (Vol_Inicial [L] + Vol_Bomba [L]) * vvm [L/L.min]
        """
        try:
            param_page = self.parameter_settings_page
            
            # 1. Obter valores da UI (convertendo vírgula para ponto)
            vol_inicial_str = param_page.replace_comma(param_page.extern_pump_block.vol_inicial_edit.text())
            vvm_str = param_page.replace_comma(param_page.extern_pump_block.vvm_edit.text())
            
            vol_inicial_L = float(vol_inicial_str)
            vvm = float(vvm_str)
            
            # 2. Obter leitura da bomba (Assumindo 'pumpVolReadVal' no comm_handler)
            
            # --- ALTERE ESTA LINHA ---
            # O JSON usa "PumpVol", que o comm_handler salva em "pumpVolReadVal"
            pump_vol_L = float(self.comm_handler.pumpVolReadVal) / 1000.0
            # --- FIM DA ALTERAÇÃO ---
            
            # 3. Calcular volume total e vazão
            total_volume_L = vol_inicial_L + pump_vol_L
            calculated_flow_L_min = total_volume_L * vvm
            
            # 4. Obter vazão máxima para limitar o setpoint
            max_flow_str = param_page.replace_comma(param_page.flow_max_flow_edit.text())
            max_flow = float(max_flow_str)
            
            # 5. Limitar o setpoint (clamp)
            setpoint = max(0, min(calculated_flow_L_min, max_flow))
            
            # 6. Atualizar a UI (que está desabilitada para o usuário)
            param_page.flow_block.line_edit.setText(f"{setpoint:.2f}")
            
            # 7. Enviar comando direto para o fluxômetro
            # (Garante que a comunicação e maxFlow sejam enviados)
            command = {
                "flowmeterComm": 1, # O controle está ativo
                "flowSetpoint": setpoint,
                "maxFlow": max_flow,
                "valve_2": 1 if setpoint == 0 else 0, # Lógica do send_flowmeter
            }
            self.comm_handler.send_command(command)

        except AttributeError:
            self.comm_handler.log_erro("'pumpVolReadVal' não encontrado no comm_handler.")
            
            # Desativa o checkbox para evitar erros repetidos
            self.parameter_settings_page.extern_pump_block.gas_prop_checkbox.setChecked(False)
        except ValueError:
            self.comm_handler.log_erro("Valores inválidos para Vol. Inicial, vvm ou Vazão Máx.")
        except Exception as e:
            self.comm_handler.log_erro(f"Erro no cálculo de vazão proporcional: {e}")

    def append_log_message(self, message):
        """Append to GUI and also persist the full session log to disk."""
        try:
            print(message, flush=True)

            if self._session_log_file is not None:
                self._session_log_file.write(message + "\n")
                self._session_log_file.flush()

            if getattr(self, "configurations_page", None) and getattr(self.configurations_page, "log_edit", None):
                self.configurations_page.log_edit.appendPlainText(message)

        except Exception as e:
            print(f"FATAL: Failed to append log to GUI/file: {message}\nError: {e}", file=sys.stderr)

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
            self.comm_handler.log_evt("As preferências foram resetadas para os valores padrões.")

    def collect_preferences(self):
        # Carrega as preferências existentes (ou os defaults se não existirem)
        prefs = self.preferences.copy() # Começa com a cópia atual

        # 1. Coleta Configurações
        conf_page = self.configurations_page

        existing_conf = self.preferences.get("Configurations", {}).copy()
        existing_conf.update({
            "ip_edit":          conf_page.ip_edit.text(),
            "data_delay_edit":  conf_page.data_delay_edit.text(),
            "oxy_cal_a":        conf_page.oxy_cal_a.text(),
            "oxy_cal_b":        conf_page.oxy_cal_b.text(),
            "ph_cal_slope":     conf_page.ph_cal_slope.text(),
            "ph_cal_intercept": conf_page.ph_cal_intercept.text(),
            "com_port_edit":    conf_page.com_port_combo.currentText(),
            "theme_combo":      conf_page.theme_combo.currentText(),
            "baud_rate_edit":   conf_page.usb_baud_rate,
            "data_bits_edit":   conf_page.usb_data_bits,
            "stop_bits_edit":   conf_page.usb_stop_bits,
            "parity_edit":      conf_page.usb_parity,
            "backup_enabled":   conf_page.backup_enable_checkbox.isChecked(),
            "backup_delay_s":   conf_page.backup_delay_edit.text(),
            "log_txt_path":     conf_page.save_path_edit.text(),
        })
        prefs["Configurations"] = existing_conf
        
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
                "low_thresh": param_page.biomass_block.low_thresh_edit.text(),
                "high_thresh": param_page.biomass_block.high_thresh_edit.text(),
                "opt_thresh": param_page.biomass_block.opt_thresh_edit.text()
            },
            "ExternalPump": {
                "comm_on": param_page.extern_pump_block.checkbox.isChecked(),
                "gas_prop_on": param_page.extern_pump_block.gas_prop_checkbox.isChecked(),
                "vol_inicial": param_page.extern_pump_block.vol_inicial_edit.text(),
                "vvm": param_page.extern_pump_block.vvm_edit.text()
            }
        }
        
        # 3. Coleta Gassing-Out
        if self.kla_gassing_out_page is not None:
            prefs["GassingOutConfig"] = self.kla_gassing_out_page.gassing_out_config
        # else: mantém o valor antigo em prefs

        #4. REPLACED: Coleta kLa Pontos Interativos e Config Gráfico
        if self.kla_cascade_page is not None:
            # NEW: Get profiles and selected name
            prefs["KlaProfiles"] = self.kla_cascade_page.get_profiles()
            prefs["SelectedKlaProfile"] = self.kla_cascade_page.get_selected_profile_name()
            prefs["GradientAscend"] = self.kla_cascade_page.gradient_widget.get_graph_config()

        if hasattr(self.parameter_settings_page, "flow_calibration_points"):
             prefs["FlowCalibrationPoints"] = self.parameter_settings_page.flow_calibration_points
        
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
            prefs["ExternalPump"]["gas_prop_on"] = ui_prefs.get("gas_prop_on")
            prefs["ExternalPump"]["vol_inicial"] = ui_prefs.get("vol_inicial")
            prefs["ExternalPump"]["vvm"] = ui_prefs.get("vvm")

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
                self.comm_handler.log_erro(f"Erro ao aplicar calibração de O2: {e}")
            # Atualiza a calibração de pH
            try:
                slope = float(self.configurations_page.ph_cal_slope.text())
                intercept = float(self.configurations_page.ph_cal_intercept.text())
                self.comm_handler.set_pH_calibration(slope, intercept)
            except Exception as e:
                self.comm_handler.log_erro(f"Erro ao aplicar calibração de pH: {e}")

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

        if "FlowCalibrationPoints" in prefs:
             points = prefs["FlowCalibrationPoints"]
             self.parameter_settings_page.flow_calibration_points = points

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
            param_page.biomass_block.checkbox.blockSignals(True)
            param_page.biomass_block.checkbox.setChecked(False)
            param_page.biomass_block.checkbox.blockSignals(False)
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
                gas_prop_state = pump_prefs.get("gas_prop_on", False)
                self.parameter_settings_page.extern_pump_block.gas_prop_checkbox.setChecked(gas_prop_state)
                self.parameter_settings_page.extern_pump_block.vol_inicial_edit.setText(pump_prefs.get("vol_inicial", "1.0"))
                self.parameter_settings_page.extern_pump_block.vvm_edit.setText(pump_prefs.get("vvm", "0.5"))
                
                # Dispara o handler para setar o estado da UI (enabled/disabled)
                self.parameter_settings_page.on_gas_proporcional_toggled(gas_prop_state)
                
                # O carregamento dos modos (param_a, param_b) é feito
                # pela própria PumpModeWindow quando ela é aberta.
            except Exception as e:
                self.comm_handler.log_erro(f"Erro ao aplicar prefs da Bomba Externa: {e}")

    def setup_auto_save(self):
        """
        Conecta QLineEdits ao auto save sem tentar desconectar
        conexões inexistentes. Usa um atributo interno para não
        conectar o mesmo campo mais de uma vez.
        """

        # 1. Campos da janela principal que devem disparar save imediato
        for le in self.findChildren(QLineEdit):
            # Alguns QLineEdits já são tratados por outras páginas,
            # se quiser pode filtrar por parent aqui
            if getattr(le, "_autosave_connected", False):
                continue
            le.editingFinished.connect(self.on_editing_finished)
            le._autosave_connected = True

        # 2. Campos de diálogo de PID não precisam ser tratados aqui,
        # porque ParameterSettingsPage.setup_field_signals já liga
        # editingFinished → on_field_changed → MainWindow.on_editing_finished.
        # Se quiser, pode simplesmente remover o bloco abaixo.
        try:
            all_dialog_edits = [
                *self.parameter_settings_page.agit_pid_edits.values(),
                *self.parameter_settings_page.agit_adv_edits.values(),
                *self.parameter_settings_page.aer_pid_edits.values(),
                *self.parameter_settings_page.aer_adv_edits.values()
            ]
            for edit in all_dialog_edits:
                if getattr(edit, "_autosave_connected", False):
                    continue
                edit.editingFinished.connect(self.on_editing_finished)
                edit._autosave_connected = True
        except Exception as e:
            self.comm_handler.log_erro(f"Erro ao conectar sinais do diálogo de PID: {e}")

    def persist_and_reload_controllers(self):
        try:
            save_preferences(self.preferences)
        except Exception as e:
            self.comm_handler.log_erro(f"Falha ao salvar preferências no disco: {e}")

        try:
            self.comm_handler.reload_filter_settings(self.preferences) 

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
                    self.comm_handler.log_evt("Configurações da Cascata kLa recarregadas.")

            if hasattr(self, 'agit_cascade_control'):
                self.agit_cascade_control.reload_pid_config()
            if hasattr(self, 'aer_cascade_control'):
                self.aer_cascade_control.reload_pid_config()
            if self.kla_gassing_out_page is not None and self._kla_gassing_out_page_loaded:
                if hasattr(self.kla_gassing_out_page, '_apply_config'):
                    self.kla_gassing_out_page._apply_config()
                
        except Exception as e:
            self.comm_handler.log_erro(f"Erro ao recarregar configs do PID: {e}")

    def on_editing_finished(self):
        s = self.sender()
        try:
            if s is self.parameter_settings_page.motor_block.line_edit:
                self.parameter_settings_page.send_motor()
        except Exception as e:
            self.comm_handler.log_erro(f"Erro na validação antes do salvamento: {e}")

        self.schedule_preferences_save()

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
            self.comm_handler.log_evt("Integral do PID (O₂) zerada para todas as cascatas.")
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

    def closeEvent(self, event):
        try:
            self.cascade_timer.stop()
        except Exception:
            pass
        try:
            self.comm_handler.shutdown()
        except Exception:
            pass

        try:
            if self._session_log_file is not None:
                self._session_log_file.flush()
                self._session_log_file.close()
                self._session_log_file = None
        except Exception as e:
            print(f"Failed to close session log: {e}")
        
        super().closeEvent(event)

if __name__ == "__main__":
    app = QApplication(sys.argv)
    app.setFont(QFont("Roboto", 10))
    window = MainWindow()
    window.show()
    sys.exit(app.exec())