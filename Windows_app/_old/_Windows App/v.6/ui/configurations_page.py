#!/usr/bin/env python
# -*- coding: utf-8 -*-
import statistics
import serial
from serial.tools import list_ports
import qdarkstyle
import time
import os

from PySide6.QtWidgets import (
    QWidget, QVBoxLayout, QGridLayout, QGroupBox, QLineEdit,
    QPushButton, QFormLayout, QComboBox, QPlainTextEdit, QFileDialog,
    QMessageBox, QHBoxLayout, QDialog, QProgressBar, QApplication, QTableWidget,
    QTableWidgetItem, QCheckBox, QStyle, QLabel
)
from PySide6.QtCore import (
    Qt, QThread, Signal, QObject, QEventLoop, QStandardPaths, QTimer,
)

from config.preferences import load_preferences, save_preferences, default_preferences
from ui.configurations_page_integration import ConfigurationsPageConnectionMixin

class FilterSettingsDialog(QDialog):
    """Um pop-up para configurar os parâmetros do filtro de spike."""
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Configurações dos Filtros de Spike")
        self.setModal(True)
        main_win = self.parent().window()
        self.prefs = main_win.preferences # Get live prefs

        layout = QFormLayout(self)

        # Mapeamento dos campos para facilitar o acesso e salvamento
        self.fields = {
            "spike_abs_threshold_ph": ("Filtro pH:", "1000.0"),
            "spike_abs_threshold_oxy": ("Filtro O₂:", "200.0"),
            "follow_tolerance_ph": ("Tol seguinte pH:", "200.0"),
            "follow_tolerance_oxy": ("Tol seguinte O₂:", "100.0"),
            "spike_confirm_runs": ("Confirmações de Spike:", "3"),
        }

        self.line_edits = {}
        for key, (label, default_val) in self.fields.items():
            value = self.prefs.get("Filters", {}).get(key, default_val)
            self.line_edits[key] = QLineEdit(str(value))
            layout.addRow(label, self.line_edits[key])

        close_button = QPushButton("Fechar")
        close_button.clicked.connect(self.save_and_close)
        layout.addRow(close_button)

    def save_and_close(self):
        """Salva as configurações e fecha o diálogo."""
        self.save_filter_settings()
        self.accept()

    def save_filter_settings(self):

        """
        Salva as configurações dos filtros DIRETAMENTE no objeto de 
        preferências da MainWindow. O on_editing_finished da MainWindow
        (conectado no __init__) cuidará de persistir no arquivo.
        """
        try:
            main_win = self.parent().window()
            prefs = main_win.preferences

            if "Filters" not in prefs:
                prefs["Filters"] = {}

            for key, line_edit in self.line_edits.items():
                prefs["Filters"][key] = line_edit.text()

            # Notifica o communication_handler para recarregar as configurações
            if hasattr(self.parent(), "comm_handler"):
                self.parent().comm_handler.reload_filter_settings()

        except Exception as e:
            # Adiciona um log de erro para depuração
            if hasattr(self, 'parent') and hasattr(self.parent(), 'log_edit'):
                self.parent().comm_handler.log_erro(f"Erro ao salvar filtros na memória: {e}")
            else:
                print(f"Erro ao salvar filtros na memória: {e}")

# ADD THIS ENTIRE NEW CLASS
class OtherSettingsDialog(QDialog):
    """
    Um pop-up para "Outras Configurações" (Filtros, Intervalos,
    Parâmetros Físicos, Gassing-Out).
    """
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Outras Configurações")
        self.setModal(True)
        self.config_page = parent
        self.main_win = self.config_page.window()
        self.prefs = self.main_win.preferences

        # O layout principal do QGroupBox "Outras Configurações"
        layout = QGridLayout()

        # --- Sub-bloco Filtros e Gráficos ---
        filter_graph_group = QGroupBox("Filtros e Gráficos")
        form1 = QFormLayout(filter_graph_group)

        # Botão Filtros
        self.filters_button = QPushButton("Configurar Filtros de Spike")
        # Conecta ao método do PARENT (ConfigurationsPage)
        self.filters_button.clicked.connect(self.config_page.open_filter_settings) 
        form1.addRow(self.filters_button)

        # QLineEdit para graph_max_points
        self.graph_max_points_edit = QLineEdit()
        form1.addRow("Pontos máx. gráfico principal:", self.graph_max_points_edit)

        layout.addWidget(filter_graph_group, 0, 0)

        # --- Sub-bloco Intervalos ---
        intervals_group = QGroupBox("Intervalos de Controle")
        form2 = QFormLayout(intervals_group)

        self.gas_prop_interval_s_edit = QLineEdit()
        form2.addRow("Intervalo Gás Prop. (s):", self.gas_prop_interval_s_edit)

        self.flow_cmd_interval_s_edit = QLineEdit()
        form2.addRow("Intervalo Cmd Fluxo (kLa) (s):", self.flow_cmd_interval_s_edit)

        layout.addWidget(intervals_group, 0, 1)

        # --- Sub-bloco Parâmetros Físicos ---
        phys_group = QGroupBox("Parâmetros Físicos e Calibração")
        form3 = QFormLayout(phys_group)

        self.c_star_mg_per_l_edit = QLineEdit()
        form3.addRow("C* O₂ (mg/L, 37°C):", self.c_star_mg_per_l_edit)

        self.ph_cal_stable_window_edit = QLineEdit()
        form3.addRow("Janela Estabil. pH (amostras):", self.ph_cal_stable_window_edit)

        self.ph_cal_stable_thresh_edit = QLineEdit()
        form3.addRow("Threshold Estabil. pH (std):", self.ph_cal_stable_thresh_edit)

        self.ph_cal_avg_steps_edit = QLineEdit()
        form3.addRow("Amostras Média Calib. pH:", self.ph_cal_avg_steps_edit)

        layout.addWidget(phys_group, 1, 0)

        # --- Sub-bloco Gassing-Out ---
        gassing_out_group = QGroupBox("Gassing-Out (Rotina Auto)")
        form4 = QFormLayout(gassing_out_group)

        self.gassing_out_min_reoxy_s_edit = QLineEdit()
        form4.addRow("Tempo mín. reoxigenação (s):", self.gassing_out_min_reoxy_s_edit)

        self.gassing_out_confirm_start_pct_edit = QLineEdit()
        form4.addRow("Início Confirmação kLa (%):", self.gassing_out_confirm_start_pct_edit)

        self.gassing_out_confirm_end_pct_edit = QLineEdit()
        form4.addRow("Fim Confirmação kLa (%):", self.gassing_out_confirm_end_pct_edit)

        layout.addWidget(gassing_out_group, 1, 1)

        # Agrupa os QLineEdits para save/load
        self.other_config_edits = {
            "graph_max_points": self.graph_max_points_edit,
            "gas_prop_interval_s": self.gas_prop_interval_s_edit,
            "flow_cmd_interval_s": self.flow_cmd_interval_s_edit,
            "c_star_mg_per_l": self.c_star_mg_per_l_edit,
            "ph_cal_stable_window": self.ph_cal_stable_window_edit,
            "ph_cal_stable_thresh": self.ph_cal_stable_thresh_edit,
            "ph_cal_avg_steps": self.ph_cal_avg_steps_edit,
            "gassing_out_min_reoxy_s": self.gassing_out_min_reoxy_s_edit,
            "gassing_out_confirm_start_pct": self.gassing_out_confirm_start_pct_edit,
            "gassing_out_confirm_end_pct": self.gassing_out_confirm_end_pct_edit
        }

        # --- Botões Salvar/Cancelar ---
        button_layout = QHBoxLayout()
        self.save_button = QPushButton("Salvar e Fechar")
        self.cancel_button = QPushButton("Cancelar")
        button_layout.addStretch()
        button_layout.addWidget(self.save_button)
        button_layout.addWidget(self.cancel_button)

        # --- Layout Principal do Diálogo ---
        main_dialog_layout = QVBoxLayout(self)
        main_dialog_layout.addLayout(layout)
        main_dialog_layout.addLayout(button_layout)

        # --- Conexões e Carga ---
        self.save_button.clicked.connect(self.save_and_close)
        self.cancel_button.clicked.connect(self.reject)
        
        self.load_settings()

    def load_settings(self):
        """Carrega as configurações do objeto de preferências da MainWindow."""
        try:
            defaults = default_preferences().get("Configurations", {})
            conf = self.prefs.get("Configurations", {})
            
            for key, widget in self.other_config_edits.items():
                default_val = defaults.get(key, "")
                widget.setText(str(conf.get(key, default_val)))
        except Exception as e:
            print(f"Erro ao carregar 'Outras Configurações': {e}")

    def save_settings(self):
        """Salva as configurações deste diálogo no objeto de preferências da MainWindow."""
        try:
            conf = self.prefs.setdefault("Configurations", {})
            for key, widget in self.other_config_edits.items():
                conf[key] = widget.text()
            self.config_page.log_evt("Configurações salvas")
        except Exception as e:
            print(f"Erro ao salvar 'Outras Configurações': {e}")

    def save_and_close(self):
        """Salva as configurações, notifica a MainWindow e fecha."""
        self.save_settings()
        # Chama a função de persistência/reload da MainWindow
        if hasattr(self.main_win, 'persist_and_reload_controllers'):
            self.main_win.persist_and_reload_controllers() 
        self.accept()

class ConfigurationsPage(ConfigurationsPageConnectionMixin, QWidget):
    """
    Merged USB/Wi-Fi connection block + new "Opções de Conexão" (backup).
    Includes restored pH calibration buttons.
    """
    def __init__(self, comm_handler, parent=None):
        super().__init__(parent)
        self.comm_handler = comm_handler

        # Defaults (UI-visible cache)
        self.usb_baud_rate = 115200
        self.usb_data_bits = 8
        self.usb_stop_bits = 1
        self.usb_parity = serial.PARITY_NONE

        # Hook handler callbacks
        self.comm_handler.connection_state_changed.connect(self.on_state_changed)

        # --------------- Layout root
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(10, 10, 10, 0)
        main_layout.setSpacing(6)
        grid = QGridLayout()
        grid.setSpacing(8)

        # --------------- Preferences group
        preferences_group = QGroupBox("Preferências")
        pref_layout = QVBoxLayout(preferences_group)
        self.reset_button = QPushButton("Resetar Preferências")
        self.reset_module_vars_button = QPushButton("Resetar variáveis do módulo")
        self.restart_comm_button = QPushButton("Reiniciar todas as comunicações")
        self.global_disconnect_button = QPushButton("Desconectar todas as comunicações")
        self.theme_combo = QComboBox()
        self.theme_combo.addItems(["Dark", "Light"])
        pref_layout.addWidget(self.reset_button)
        pref_layout.addWidget(self.reset_module_vars_button)
        pref_layout.addWidget(self.restart_comm_button)
        pref_layout.addWidget(self.global_disconnect_button)
        
        # --- Theme and Settings Button Row ---
        theme_row_layout = QHBoxLayout()
        theme_row_layout.addWidget(self.theme_combo, 1) # Dá prioridade de expansão

        self.other_settings_button = QPushButton()
        # Tenta usar um ícone de "configurações", se não, usa um ícone de "detalhes"
        try:
            icon = self.style().standardIcon(QStyle.StandardPixmap.SP_SettingsIcon)
        except AttributeError:
            icon = self.style().standardIcon(QStyle.StandardPixmap.SP_FileDialogDetailedView)
            
        self.other_settings_button.setIcon(icon)
        self.other_settings_button.setToolTip("Abrir outras configurações (filtros, intervalos, etc.)")
        # Tenta deixar o botão quadrado
        try:
            btn_size = self.theme_combo.sizeHint().height()
            self.other_settings_button.setFixedSize(btn_size, btn_size)
        except Exception:
            pass # Continua se a sizeHint falhar

        theme_row_layout.addWidget(self.other_settings_button, 0) # Não expande
        
        pref_layout.addLayout(theme_row_layout) # Adiciona a QHBoxLayout à QVBoxLayout

        self.reset_button.clicked.connect(self.on_reset_preferences)
        self.reset_module_vars_button.clicked.connect(self.reset_module_variables)
        self.restart_comm_button.clicked.connect(self.restart_communications)
        self.global_disconnect_button.clicked.connect(self.disconnect_all)
        self.theme_combo.currentTextChanged.connect(self.change_theme)
        self.other_settings_button.clicked.connect(self.open_other_settings_dialog) # ADD THIS LINE

        grid.addWidget(preferences_group, 0, 0, 1, 1)

        # --------------- Calibration (pH/O2) + RESTORED pH buttons
        calib_group = QGroupBox("Parâmetros de calibração")
        calib_form = QFormLayout(calib_group)
        self.oxy_cal_a = QLineEdit(str(getattr(self.comm_handler, "oxy_cal_a", "0.0305473419314")))
        self.oxy_cal_b = QLineEdit(str(getattr(self.comm_handler, "oxy_cal_b", "-25.09136520919")))
        self.ph_cal_slope = QLineEdit(str(getattr(self.comm_handler, "pH_slope", "1.0")))
        self.ph_cal_intercept = QLineEdit(str(getattr(self.comm_handler, "pH_intercept", "0.0")))
        calib_form.addRow("O₂ slope:", self.oxy_cal_a)
        calib_form.addRow("O₂ intercept:", self.oxy_cal_b)
        calib_form.addRow("pH slope:", self.ph_cal_slope)
        calib_form.addRow("pH intercept:", self.ph_cal_intercept) 

        # --- pH calibration buttons (restored)
        ph_btn_row_widget = QWidget()
        ph_btn_row = QHBoxLayout(ph_btn_row_widget)
        ph_btn_row.setContentsMargins(0, 0, 0, 0)
        self.ph_calibrate_button = QPushButton("Calibrar pH (dois pontos)")
        self.ph_calibrate_1_point_button = QPushButton("Calibrar pH (um ponto)")
        ph_btn_row.addWidget(self.ph_calibrate_button)
        ph_btn_row.addWidget(self.ph_calibrate_1_point_button)
        calib_form.addRow(ph_btn_row_widget)
        self.ph_calibrate_button.clicked.connect(self.open_ph_calibration_dialog)
        self.ph_calibrate_1_point_button.clicked.connect(self.open_ph_1_point_calibration_dialog)

        self.oxy_cal_a.editingFinished.connect(self.update_oxy_calibration)
        self.oxy_cal_b.editingFinished.connect(self.update_oxy_calibration)
        self.ph_cal_slope.editingFinished.connect(self.update_ph_calibration)
        self.ph_cal_intercept.editingFinished.connect(self.update_ph_calibration)
        grid.addWidget(calib_group, 1, 0, 1, 1)

        # --------------- MERGED CONNECTION BLOCK
        conn_group = QGroupBox("Conexão (USB / Wi-Fi)")
        conn_form = QFormLayout(conn_group)

        # Transport selector
        self.medium_combo = QComboBox()
        self.medium_combo.addItems(["USB", "WiFi"])
        conn_form.addRow("Meio:", self.medium_combo)

        # USB area
        usb_row = QWidget(); usb_layout = QHBoxLayout(usb_row)
        usb_layout.setContentsMargins(0, 0, 0, 0)
        self.com_port_combo = QComboBox()
        self.refresh_ports_button = QPushButton("Atualizar Portas")
        self.refresh_ports_button.clicked.connect(self.refresh_com_ports)
        usb_layout.addWidget(self.com_port_combo)
        usb_layout.addWidget(self.refresh_ports_button)
        conn_form.addRow("Porta COM:", usb_row)

        # Wi-Fi area
        self.ip_edit = QLineEdit("192.168.4.1")
        conn_form.addRow("Endereço IP:", self.ip_edit)

        # Buttons
        btn_row = QWidget(); btn_layout = QHBoxLayout(btn_row)
        btn_layout.setContentsMargins(0, 0, 0, 0)
        self.connect_button = QPushButton("Conectar")
        self.stop_button = QPushButton("Parar Conexão")
        self.stop_button.setEnabled(False)
        btn_layout.addWidget(self.connect_button)
        btn_layout.addWidget(self.stop_button)
        conn_form.addRow(btn_row)

        # wiring
        self.refresh_ports_button.clicked.connect(self.refresh_com_ports)
        self.connect_button.clicked.connect(self.connect_clicked)
        self.stop_button.clicked.connect(self.stop_connection)
        self.refresh_com_ports()

        grid.addWidget(conn_group, 0, 1, 1, 1)

        # --------------- NEW: Connection Options (backup) + (merged) Leitura de Dados
        opt_group = QGroupBox("Opções de Conexão")
        opt_form = QFormLayout(opt_group)

        # -- backup
        backup_row = QHBoxLayout()
        self.backup_enable_checkbox = QCheckBox("Habilitar backup automático")
        self.backup_enable_checkbox.setChecked(True)
        
        backup_row.addWidget(self.backup_enable_checkbox)
        
        self.backup_delay_edit = QLineEdit("5.0")
        opt_form.addRow(backup_row) # Adiciona a linha com checkbox e botão
        opt_form.addRow("Atraso do backup (s):", self.backup_delay_edit)

        # -- Leitura de Dados
        self.data_delay_edit = QLineEdit("1000")
        self.data_delay_button = QPushButton("Definir Atraso")
        delay_row = QWidget()
        delay_row_l = QHBoxLayout(delay_row)
        delay_row_l.setContentsMargins(0, 0, 0, 0)
        delay_row_l.addWidget(self.data_delay_edit)
        self.sound_alarm_checkbox = QCheckBox("Desativar alarme sonoro")
        self.sound_alarm_checkbox.stateChanged.connect(lambda s: self.comm_handler._mute_changed(s))
        delay_row_l.addWidget(self.sound_alarm_checkbox)
        self.zerar_tempo_button = QPushButton("Zerar tempo")
        self.zerar_tempo_button.clicked.connect(self.zerar_tempo)

        opt_form.addRow("Atraso de leitura (ms):", delay_row)
        opt_form.addRow(self.data_delay_button)
        opt_form.addRow(self.zerar_tempo_button)

        self.data_delay_button.clicked.connect(self.set_data_delay)
        self.backup_enable_checkbox.stateChanged.connect(self._apply_backup_options)
        self.backup_delay_edit.editingFinished.connect(self._apply_backup_options)

        grid.addWidget(opt_group, 1, 1, 1, 1)

        # --------------- Log
        log_group = QGroupBox("Log")
        log_v = QVBoxLayout(log_group)
        self.log_edit = QPlainTextEdit()
        self.log_edit.setReadOnly(True)
        self.log_edit.setMaximumBlockCount(250)
        log_v.addWidget(self.log_edit)

        grid.addWidget(log_group, 0, 2, 3, 1)
        # place below both left (calib) and middle (opções) columns
        self.init_save_path_block(grid, row=2, col=0, colspan=2)

        grid.setColumnStretch(0, 1)
        grid.setColumnStretch(1, 1)
        grid.setColumnStretch(2, 2)
        main_layout.addLayout(grid)

        # Load prefs into UI + handler
        self.apply_preferences(load_preferences() or default_preferences())
        self._apply_backup_options()

    def log_info(self, msg: str) -> None:
        self.comm_handler.log_info(msg)

    def log_evt(self, msg: str) -> None:
        self.comm_handler.log_evt(msg)

    def log_aviso(self, msg: str) -> None:
        self.comm_handler.log_aviso(msg)

    def log_erro(self, msg: str) -> None:
        self.comm_handler.log_erro(msg)

    def open_filter_settings(self):
        """Abre o diálogo de configuração dos filtros."""
        dialog = FilterSettingsDialog(self)
        dialog.exec()

    def open_other_settings_dialog(self):
        """Abre o diálogo de 'Outras Configurações'."""
        dialog = OtherSettingsDialog(self)
        dialog.exec()

    def init_save_path_block(self, parent_grid, row=3, col=0, colspan=2):
        """
        Creates the 'Caminho para salvar' block and wires up a helper that
        opens/creates the log file ONLY if the path in the entry is valid.
        Place this below both 'Opções de Conexão' and 'Parâmetros de calibração'.

        """

        # --- UI ---
        grp = QGroupBox("Caminho para salvar")
        form = QFormLayout(grp)

        self.save_path_edit = QLineEdit()
        self.save_path_edit.setPlaceholderText("Ex.: C:/dados/experimento.txt (ou escolha abaixo)")

        # Try to show current path from prefs (if any)
        prefs = load_preferences() or default_preferences()
        cur_path = (prefs.get("Configurations", {}) or {}).get("log_txt_path", "")
        if isinstance(cur_path, str) and cur_path.strip():
            self.save_path_edit.setText(cur_path.strip())

        row_w = QWidget()
        row_l = QHBoxLayout(row_w)
        row_l.setContentsMargins(0, 0, 0, 0)

        self.create_file_btn = QPushButton("Criar Novo")
        self.browse_file_btn = QPushButton("Selecionar…") 

        row_l.addWidget(self.save_path_edit, 1)
        row_l.addWidget(self.create_file_btn)
        row_l.addWidget(self.browse_file_btn) 
        form.addRow("Arquivo .txt:", row_w)

        parent_grid.addWidget(grp, row, col, 1, colspan)

        # --- Helpers ---
        def _save_prefs_with_path(path_text: str):
            p = load_preferences() or default_preferences()
            conf = p.setdefault("Configurations", {})
            conf["log_txt_path"] = path_text
            save_preferences(p)

        def _write_header_if_new(fh):
            header = ("Time (min)\tTemperature (°C)\tMotor (rpm)\tpH\tAntifoam\t"
                    "Pressure\tOxygen\tFlowmeter\tDistance\tOUR\tBiomass\tPump Volume\tPump Flow\tConexão\n")
            try:
                if fh.tell() == 0:
                    fh.write(header)
                    fh.flush()
            except Exception:
                pass

        # Public helper used by prompt_for_log_file()
        def ensure_log_file_open():
            """
            Checks the line-edit path. If it’s usable, opens/creates the file
            silently and returns True. If not usable, returns False so caller
            can show the file dialog.
            """
            import os
            path = self.save_path_edit.text().strip()
            if not path:
                return False

            # Normalize and ensure ".txt"
            base, ext = os.path.splitext(path)
            if not ext:
                path = path + ".txt"

            # Directory must exist (we do NOT create directories silently)
            dir_path = os.path.dirname(path) or os.getcwd()
            if not os.path.isdir(dir_path):
                return False

            # Try opening the file
            try:
                file_exists = os.path.exists(path)
                main_win = self.window()
                # Close previous file if any (safety)
                if hasattr(main_win, "log_file") and main_win.log_file:
                    try:
                        main_win.log_file.close()
                    except Exception:
                        pass

                fh = open(path, "a", encoding="utf-8")
                main_win.log_file = fh
                if not file_exists:
                    _write_header_if_new(fh)

                # Persist path
                self.save_path_edit.setText(path)
                _save_prefs_with_path(path)
                return True
            except Exception:
                return False

        # Expose helper as an instance method
        self.ensure_log_file_open = ensure_log_file_open

        # --- Signals ---
        def _on_create_new_clicked():
            """
            Tenta criar/abrir o arquivo exatamente como escrito
            na caixa de texto. NÃO abre um diálogo de arquivo.
            """
            if self.ensure_log_file_open():
                self.log_info(f"Arquivo de log aberto/criado: {self.save_path_edit.text()}")
            else:
                # Falhou
                QMessageBox.warning(self, "Erro ao Criar Arquivo",
                                  "Não foi possível criar/abrir o arquivo.\n"
                                  "Verifique se o caminho e as permissões estão corretos.\n"
                                  "O diretório deve existir.")
        
        def _on_browse_new_clicked():
            """
            SEMPRE abre o diálogo de arquivo para selecionar um arquivo.
            """
            initial_dir = QStandardPaths.writableLocation(QStandardPaths.StandardLocation.DesktopLocation)
            
            # Usa o texto atual como sugestão
            current_path = self.save_path_edit.text().strip()
            if current_path:
                initial_dir = os.path.dirname(current_path) or initial_dir
            
            path, _ = QFileDialog.getSaveFileName(
                self, "Selecione ou Crie um Arquivo de Log", initial_dir, "Text Files (*.txt);;All Files (*)"
            )
            if not path:
                return  # Usuário cancelou
            
            # Normaliza a extensão
            base, ext = os.path.splitext(path)
            if not ext:
                path = path + ".txt"

            # Define o texto e tenta abrir o arquivo
            self.save_path_edit.setText(path)
            if not self.ensure_log_file_open():
                QMessageBox.warning(self, "Erro", "Não foi possível usar o caminho informado.")
            else:
                self.log_info(f"Arquivo de log alterado para: {path}")

        self.create_file_btn.clicked.connect(_on_create_new_clicked)
        self.browse_file_btn.clicked.connect(_on_browse_new_clicked)

        # Also persist when the user types a new path manually
        def _on_edit_finished():
            txt = self.save_path_edit.text().strip()
            _save_prefs_with_path(txt)

        self.save_path_edit.editingFinished.connect(_on_edit_finished)

    # -------------------- Handlers & helpers -------------------------
    def apply_preferences(self, prefs):
        conf = prefs.get("Configurations", {})
        # theme
        theme = conf.get("theme_combo", "Dark")
        self.theme_combo.setCurrentText(theme)
        self.change_theme(theme)

        # timings
        data_delay = conf.get("data_delay_edit", "1000")
        self.data_delay_edit.setText(str(data_delay))

        # conn fields
        self.ip_edit.setText(conf.get("ip_edit", "192.168.4.1"))
        # USB params (store on self for MainWindow compatibility)
        self.usb_baud_rate = int(conf.get("baud_rate_edit", "115200"))
        self.usb_data_bits = int(conf.get("data_bits_edit", "8"))
        self.usb_stop_bits = int(conf.get("stop_bits_edit", "1"))
        parity_text = conf.get("parity_edit", "None").lower()
        self.usb_parity = serial.PARITY_NONE if parity_text == "none" else parity_text

        # backup options
        self.backup_enable_checkbox.setChecked(bool(conf.get("backup_enabled", True)))
        self.backup_delay_edit.setText(str(conf.get("backup_delay_s", "5.0")))

        # cal
        self.oxy_cal_a.setText(conf.get("oxy_cal_a", "0.0305473419314"))
        self.oxy_cal_b.setText(conf.get("oxy_cal_b", "-25.09136520919"))
        self.ph_cal_slope.setText(conf.get("ph_cal_slope", "1.0"))
        self.ph_cal_intercept.setText(conf.get("ph_cal_intercept", "0.0"))
        
        self.save_path_edit.setText(conf.get("log_txt_path", ""))

        # push to handler
        self.set_data_delay()

    def prompt_for_log_file(self):
        # If user-provided path is valid/usable, open silently and return.
        if hasattr(self, "ensure_log_file_open") and self.ensure_log_file_open():
            return

        # Fallback to prompting (only when path is missing/invalid)
        initial_dir = QStandardPaths.writableLocation(QStandardPaths.StandardLocation.DesktopLocation)
        fileName, _ = QFileDialog.getSaveFileName(
            self,
            "Selecione o Arquivo de Log",
            initial_dir,
            "Text Files (*.txt);;All Files (*)"
        )
        if fileName:
            # Ensure .txt
            import os
            base, ext = os.path.splitext(fileName)
            if not ext:
                fileName = fileName + ".txt"

            main_win = self.window()
            file_exists = os.path.exists(fileName)
            main_win.log_file = open(fileName, "a", encoding="utf-8")
            if not file_exists:
                header = ("Time (min)\tTemperature (°C)\tMotor (rpm)\tpH\tAntifoam\t"
                        "Pressure\tOxygen\tFlowmeter\tDistance\tOUR\tBiomass\tPump Volume\tPump Flow\tConexão\n")
                main_win.log_file.write(header)
                main_win.log_file.flush()

            # Reflect and persist chosen path so future runs are silent
            if hasattr(self, "save_path_edit"):
                self.save_path_edit.setText(fileName)
            prefs = load_preferences() or default_preferences()
            prefs.setdefault("Configurations", {})["log_txt_path"] = fileName
            save_preferences(prefs)

    # -------- Buttons
    def connect_clicked(self):
        self.prompt_for_log_file()

        medium = self.medium_combo.currentText()
        if medium == "USB":
            port = self.com_port_combo.currentText().strip()
            if not port or "Nenhuma porta" in port:
                self.log_aviso("nenhuma porta USB disponível para conectar")
                return   # connect_usb() itself transitions to ERROR if port is invalid
            self.comm_handler.connect_usb(
                port,
                int(self.usb_baud_rate),
                int(self.usb_data_bits),
                int(self.usb_stop_bits),
                self.usb_parity
            )
        else:
            self.comm_handler.connect_wifi(self.ip_edit.text().strip())

    def stop_connection(self):
        self.comm_handler.disconnect()
        self.log_evt("Conexão parada pelo usuário")

    def disconnect_all(self):
        self.comm_handler.disconnect()
        self.log_evt("Todas as comunicações foram desconectadas")
        self.refresh_com_ports()

    # -------- Misc UI actions
    def set_data_delay(self):
        try:
            delay_ms = int(self.data_delay_edit.text())
        except Exception:
            self.log_aviso("Valor inválido para atraso de leitura")
            return
        self.comm_handler.send_command({"dataDelay": delay_ms})
        self.comm_handler.set_poll_period_ms(delay_ms)
        win = self.window()
        
        if hasattr(win, "dataDelay"):
            win.dataDelay = delay_ms
        if hasattr(win, "cascade_timer"):
            win.cascade_timer.setInterval(delay_ms)
        if hasattr(getattr(win, "graphs_page", None), "timer"):
            win.graphs_page.timer.setInterval(delay_ms)
        if hasattr(getattr(win, "kla_gassing_out_page", None), "update_timer"):
            win.kla_gassing_out_page.update_timer.setInterval(delay_ms)

        if hasattr(win, "schedule_preferences_save"):
            win.schedule_preferences_save()

        self.log_info(f"Atraso de leitura definido: {delay_ms/1000} s")

    def change_theme(self, theme):
        if theme == "Dark":
            try:
                QApplication.instance().setStyleSheet(qdarkstyle.load_stylesheet(qt_api="PySide6"))
            except Exception:
                QApplication.instance().setStyleSheet(qdarkstyle.load_stylesheet())
        else:
            QApplication.instance().setStyleSheet("")

    def _apply_backup_options(self):
        enabled = self.backup_enable_checkbox.isChecked()
        try:
            delay_s = float(self.backup_delay_edit.text())
        except Exception:
            delay_s = 5.0

        self.comm_handler.set_backup_options(enabled, delay_s)

        main_win = self.window()
        if hasattr(main_win, "schedule_preferences_save"):
            main_win.schedule_preferences_save()

    def on_reset_preferences(self):
        reply = QMessageBox.question(
            self, "Resetar Preferências",
            "Você tem certeza que deseja resetar todas as preferências para os valores padrões?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
        )
        if reply == QMessageBox.StandardButton.Yes:
            prefs = default_preferences()
            save_preferences(prefs)
            self.apply_preferences(prefs)
            self.log_evt("Preferências resetadas")

    def reset_module_variables(self):
        if QMessageBox.question(
            self, "Resetar Variáveis do Módulo",
            "Deseja resetar todas as variáveis do módulo?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
        ) == QMessageBox.StandardButton.Yes:
            self.comm_handler.send_command({"resetVariables": 1})
            self.log_evt("Comando de reset das variáveis do módulo enviado")

    def restart_communications(self):
        if QMessageBox.question(
            self, "Reiniciar Comunicações",
            "Deseja reiniciar a comunicação com o módulo?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
        ) == QMessageBox.StandardButton.Yes:
            self.comm_handler.send_command({"restart": 1})
            self.log_evt("Comando de reinicialização enviado ao ESP32")

    def zerar_tempo(self):
        if QMessageBox.question(
            self, "Zerar Tempo",
            "Deseja resetar o tempo do programa?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
        ) == QMessageBox.StandardButton.Yes:
            if hasattr(self.comm_handler, "_raw_time_min"):
                self.comm_handler.timeZeroOffset = self.comm_handler._raw_time_min
            else:
                self.comm_handler.timeZeroOffset = 0
            self.comm_handler.timeZeroOffset = self.comm_handler.readings.time_raw_s / 60.0

    def update_oxy_calibration(self):
        try:
            a = float(self.oxy_cal_a.text())
            b = float(self.oxy_cal_b.text())
            self.comm_handler.set_oxygen_calibration(a, b)
        except Exception as e:
            self.log_erro(f"Erro ao atualizar calibração de O₂: {e}")

    def update_ph_calibration(self):
        try:
            slope = float(self.ph_cal_slope.text())
            intercept = float(self.ph_cal_intercept.text())
            self.comm_handler.set_pH_calibration(slope, intercept)
        except ValueError:
            QMessageBox.warning(self, "Erro", "Valores inválidos para calibração de pH.")

    def refresh_com_ports(self):
        self.com_port_combo.clear()
        ports = list_ports.comports()
        for port in ports:
            self.com_port_combo.addItem(port.device)
        if self.com_port_combo.count() == 0:
            self.com_port_combo.addItem("Nenhuma porta disponível")

    # ---- Open dialogs for pH calibration ----
    def open_ph_calibration_dialog(self):
        dlg = PHCalibrationDialog(self.comm_handler, self)
        dlg.exec()

    def open_ph_1_point_calibration_dialog(self):
        dlg = PHOnePointCalibrationDialog(self.comm_handler, self)
        dlg.exec()

# --------------------- pH Calibration (unchanged) ---------------------
class PHCalibrationWorker(QObject):
    finished = Signal(float, float, float, float, list, list)
    progress = Signal(int)
    warningRequest = Signal(float)
    statusUpdate = Signal(str)

    def __init__(self, comm_handler, ref1_target, ref2_target, delay=1000, config_prefs=None):
        super().__init__()
        self.comm_handler = comm_handler
        self.ref1_target = ref1_target
        self.ref2_target = ref2_target
        self.delay = delay
        self.cancelled = False
        self._ack_loop = None

        if config_prefs is None:
            config_prefs = {}
        self.config_prefs = config_prefs

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
        self._ack_loop.exec()
        self._ack_loop = None

    def ack_received(self):
        if self._ack_loop is not None:
            self._ack_loop.quit()

    def _acquire_calibration_point(self, progress_start, progress_end):
        delay_sec = self.delay / 1000.0
        buffer = []
        window_size = int(float(self.config_prefs.get("ph_cal_stable_window", "20")))
        threshold = float(self.config_prefs.get("ph_cal_stable_thresh", "5"))
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
        steps = int(float(self.config_prefs.get("ph_cal_avg_steps", "20")))
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
    finished = Signal(float, float, float, list)
    progress = Signal(int)
    warningRequest = Signal(float)
    statusUpdate = Signal(str)

    def __init__(self, comm_handler, target, delay=1000, config_prefs=None):
        super().__init__()
        self.comm_handler = comm_handler
        self.target = target
        self.delay = delay
        self.cancelled = False
        self._ack_loop = None
        if config_prefs is None:
            config_prefs = {}
        self.config_prefs = config_prefs
        self.config_prefs = config_prefs
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
            cfg = self.comm_handler._parser._cfg
            slope = cfg.ph_slope
        except Exception:
            slope = 1.0
        intercept = self.target - slope * average
        self.progress.emit(100)
        self.finished.emit(average, slope, intercept, values)

    def _wait_for_ack(self):
        self._ack_loop = QEventLoop()
        self._ack_loop.exec()
        self._ack_loop = None

    def ack_received(self):
        if self._ack_loop is not None:
            self._ack_loop.quit()

    def _acquire_calibration_point(self, progress_start, progress_end):
        delay_sec = self.delay / 1000.0
        buffer = []
        window_size = int(float(self.config_prefs.get("ph_cal_stable_window", "20")))
        threshold = float(self.config_prefs.get("ph_cal_stable_thresh", "5"))
        while True:
            if self.cancelled:
                return None, []
            reading = self.comm_handler.phRead
            buffer.append(reading)
            if len(buffer) > window_size:
                buffer.pop(0)
            if len(buffer) == window_size:
                std_dev = statistics.stdev(buffer)
                self.statusUpdate.emit(f"Esperando estabilizar (std<={threshold:.1f}): {std_dev:.2f}")
                if std_dev < threshold:
                    break
            else:
                self.statusUpdate.emit("Obtendo valores iniciais...")
            time.sleep(delay_sec)
        self.statusUpdate.emit("")
        values = []
        steps = int(float(self.config_prefs.get("ph_cal_avg_steps", "20")))
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
    ackClicked = Signal()

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
        self.progress_bar.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.status_label = QLabel("")
        self.status_label.setAlignment(Qt.AlignmentFlag.AlignCenter)

        main_layout = QVBoxLayout()
        main_layout.addLayout(grid)
        main_layout.addWidget(self.progress_bar)
        main_layout.addWidget(self.status_label)
        self.setLayout(main_layout)

        self.calibrate_button.clicked.connect(self.start_calibration)
        self.cancel_button.clicked.connect(self.cancel_calibration)
        self.setAttribute(Qt.WidgetAttribute.WA_DeleteOnClose)

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
        config_prefs = self.parent().window().preferences.get("Configurations", {})
        self.worker = PHCalibrationWorker(self.comm_handler, ref1, ref2, config_prefs=config_prefs)
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
        QMessageBox.information(self, "Atenção", msg, QMessageBox.StandardButton.Ok)
        self.ackClicked.emit()

    def on_calibration_finished(self, ref1_measured, ref2_measured, slope, intercept, ref1_values, ref2_values):
        if ref1_measured is None or ref2_measured is None:
            return
        resultDialog = QDialog(self)
        resultDialog.setWindowTitle("Calibração Concluída")
        layout = QVBoxLayout(resultDialog)

        eq_label = QLabel(f"Curva: pH = {slope:.13f} * valor lido + {intercept:.12f}")
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

        resultDialog.exec()
        self.comm_handler.set_pH_calibration(slope, intercept)
        self.accept()

        if hasattr(self.parent(), "ph_cal_slope"):
            self.parent().ph_cal_slope.setText(f"{slope:.13f}")
        if hasattr(self.parent(), "ph_cal_intercept"):
            self.parent().ph_cal_intercept.setText(f"{intercept:.12f}")

        # Save in "Configurations"
        prefs = load_preferences() or default_preferences()
        if "Configurations" not in prefs:
            prefs["Configurations"] = {}
        prefs["Configurations"]["ph_cal_slope"] = str(slope)
        prefs["Configurations"]["ph_cal_intercept"] = str(intercept)
        save_preferences(prefs)


class PHOnePointCalibrationDialog(QDialog):
    ackClicked = Signal()

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
        self.progress_bar.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.status_label = QLabel("")
        self.status_label.setAlignment(Qt.AlignmentFlag.AlignCenter)

        main_layout = QVBoxLayout()
        main_layout.addLayout(grid)
        main_layout.addWidget(self.progress_bar)
        main_layout.addWidget(self.status_label)
        self.setLayout(main_layout)

        self.calibrate_button.clicked.connect(self.start_calibration)
        self.cancel_button.clicked.connect(self.cancel_calibration)
        self.setAttribute(Qt.WidgetAttribute.WA_DeleteOnClose)

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
        config_prefs = self.parent().window().preferences.get("Configurations", {})
        self.worker = PHOnePointCalibrationWorker(self.comm_handler, target, config_prefs=config_prefs)
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
        QMessageBox.information(self, "Atenção", msg, QMessageBox.StandardButton.Ok)
        self.ackClicked.emit()

    def on_calibration_finished(self, measured, slope, intercept, values):
        if measured is None:
            return
        resultDialog = QDialog(self)
        resultDialog.setWindowTitle("Calibração Concluída")
        layout = QVBoxLayout(resultDialog)

        eq_label = QLabel(f"Curva: pH = {slope:.6f} * valor lido + {intercept:.6f}")
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

        resultDialog.exec()
        self.comm_handler.set_pH_calibration(slope, intercept)
        self.accept()

        if hasattr(self.parent(), "ph_cal_slope"):
            self.parent().ph_cal_slope.setText(f"{slope:.12f}")
        if hasattr(self.parent(), "ph_cal_intercept"):
            self.parent().ph_cal_intercept.setText(f"{intercept:.13f}")

        # Save in "Configurations"
        prefs = load_preferences() or default_preferences()
        if "Configurations" not in prefs:
            prefs["Configurations"] = {}
        prefs["Configurations"]["ph_cal_slope"] = str(slope)
        prefs["Configurations"]["ph_cal_intercept"] = str(intercept)
        save_preferences(prefs)
