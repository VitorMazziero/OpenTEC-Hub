#!/usr/bin/env python
# -*- coding: utf-8 -*-

"""
kla_gassing_out_page.py: A PyQt5 widget for live kLa determination.

This module provides a tab for the application that supports both:
1. Manual gassing-out experiments with live data visualization.
2. A fully automated routine to determine experimental kLa values for
   a predefined set of operational conditions (N and Q), designed to
   populate the data for the kLa cascade control page.
"""

import time
import os
import numpy as np
import torch
import torch.nn as nn
from scipy.ndimage import gaussian_filter1d, binary_closing, binary_opening, label
from collections import deque

from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QGroupBox, QLabel, QPushButton,
    QFileDialog, QMessageBox, QFormLayout, QLineEdit, QSplitter, QDialog,
    QTableWidget, QTableWidgetItem, QHeaderView, QDialogButtonBox, QAbstractItemView
)
from PyQt5.QtCore import QTimer, Qt
import pyqtgraph as pg

# (The TCN_MODEL and HELPER_CLASSES remain unchanged)
# ──────────────────────────────────────────────────────────────────────────────
# TCN MODEL AND HELPER CLASSES
# ──────────────────────────────────────────────────────────────────────────────
EPS_SCALER = 1e-6

class RunningMinMax:
    def __init__(self): self.min = None; self.max = None
    def update(self, x: np.ndarray):
        x = np.asarray(x);
        if x.size == 0: return
        xm, xM = float(x.min()), float(x.max())
        if self.min is None or xm < self.min: self.min = xm
        if self.max is None or xM > self.max: self.max = xM
    def normalise(self, x: np.ndarray) -> np.ndarray:
        x = np.asarray(x, dtype=float)
        if self.min is None or self.max is None or (self.max - self.min) < EPS_SCALER:
            return np.zeros_like(x, dtype=np.float32)
        return np.clip((x - self.min) / (self.max - self.min), 0.0, 1.0).astype(np.float32)

def detect_plateau_segment(t, probs, thr, smooth_sigma=2.0, min_hole=5, min_island=5):
    p = gaussian_filter1d(probs, sigma=smooth_sigma, mode='nearest'); m = p > thr
    m = binary_closing(m, structure=np.ones(min_hole, bool)); m = binary_opening(m, structure=np.ones(min_island, bool))
    labels, nlab = label(m); best_len, best_seg_indices = 0, None
    for seg_label in range(1, nlab + 1):
        idx = np.where(labels == seg_label)[0]
        if idx.size > best_len: best_len, best_seg_indices = idx.size, idx
    if best_seg_indices is None: return None, None
    return float(t[best_seg_indices[0]]), float(t[best_seg_indices[-1]])

class ResBlock(nn.Module):
    def __init__(self, in_ch, out_ch, d, k=3, drop=0.1):
        super().__init__()
        self.conv = nn.Conv1d(in_ch, out_ch, k, padding=d*(k-1)//2, dilation=d)
        self.bn = nn.BatchNorm1d(out_ch); self.relu = nn.ReLU(inplace=True); self.drop = nn.Dropout(drop)
        self.match = (in_ch == out_ch)
    def forward(self, x):
        z = self.relu(self.bn(self.conv(x))); z = self.drop(z)
        return z + x if self.match else z

class DilatedTCN(nn.Module):
    def __init__(self, in_ch=2, layers=7, filters=64, k=3, drop=0.1):
        super().__init__()
        blocks = [ResBlock(in_ch if i==0 else filters, filters, 2**i, k, drop) for i in range(layers)]
        self.encoder = nn.Sequential(*blocks); self.classifier = nn.Conv1d(filters, 1, 1)
    def forward(self, x): return self.classifier(self.encoder(x))

# ──────────────────────────────────────────────────────────────────────────────
# OPTIONS DIALOGS
# ──────────────────────────────────────────────────────────────────────────────
class GassingOutOptionsDialog(QDialog):
    """A dialog for setting the gassing-out calculation parameters."""
    def __init__(self, config, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Gassing-Out Options")
        self.config = config
        self.widgets = {}
        
        layout = QFormLayout(self)
        
        params = {
            "c_sat": ("DO Saturation (%):", "100.0"),
            "stabilization_derivative_threshold": ("Stabilization Threshold (dC/dt):", "0.05"),
            "stabilization_samples": ("Stabilization Samples:", "5"),
            "plateau_thr": ("Plateau Threshold (TCN):", "0.77"),
            "model_path": ("TCN Model Path:", "kLa_TNC_gassing_out.pth"),
            "do_moving_average_window": ("DO Smoothing Window:", "10"),
            "kla_moving_average_window": ("kLa Smoothing Window:", "15"),
            "derivative_window": ("Derivative Calculation Window:", "5") # New parameter
        }
        
        for key, (label, default) in params.items():
            value = self.config.get(key, default)
            widget = QLineEdit(str(value))
            self.widgets[key] = widget
            layout.addRow(label, widget)
            
        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel, Qt.Horizontal, self)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addRow(buttons)

    def get_values(self):
        return {key: widget.text() for key, widget in self.widgets.items()}

class AutomaticKlaDialog(QDialog):
    # This class remains unchanged
    def __init__(self, experimental_points, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Automatic kLa Routine Setup")
        self.experimental_points = experimental_points
        self.save_folder = ""
        main_layout = QVBoxLayout(self)
        warning_text = ("<b>Warning:</b> Ensure Nitrogen and Aeration are connected to the "
                        "50L/min flowmeter's left valve (V1), OR be prepared to manually "
                        "switch gases when prompted by the software status.")
        main_layout.addWidget(QLabel(warning_text))
        self.table = QTableWidget()
        self.table.setColumnCount(3)
        self.table.setHorizontalHeaderLabels(["Status", "Q (L/min)", "N (RPM)"])
        self.table.setRowCount(len(self.experimental_points))
        for i, (q, n, _) in enumerate(self.experimental_points):
            self.table.setItem(i, 0, QTableWidgetItem("Pending"))
            self.table.setItem(i, 1, QTableWidgetItem(f"{q:.2f}"))
            self.table.setItem(i, 2, QTableWidgetItem(f"{n:.0f}"))
        self.table.horizontalHeader().setSectionResizeMode(QHeaderView.Stretch)
        self.table.setEditTriggers(QAbstractItemView.NoEditTriggers)
        main_layout.addWidget(self.table)
        settings_layout = QFormLayout()
        self.replicates_edit = QLineEdit("1")
        self.deoxygenation_rpm_edit = QLineEdit("700")
        settings_layout.addRow("Number of Replicates (1-4):", self.replicates_edit)
        settings_layout.addRow("Deoxygenation RPM:", self.deoxygenation_rpm_edit)
        main_layout.addLayout(settings_layout)
        self.start_button = QPushButton("Select Folder to Save Runs and Start")
        self.start_button.clicked.connect(self.select_folder_and_start)
        button_box = QDialogButtonBox()
        button_box.addButton(self.start_button, QDialogButtonBox.ActionRole)
        main_layout.addWidget(button_box)
    def select_folder_and_start(self):
        folder = QFileDialog.getExistingDirectory(self, "Select Folder to Save Run Data")
        if folder: self.save_folder = folder; self.accept()
    def get_settings(self):
        try:
            replicates = int(self.replicates_edit.text())
            if not 1 <= replicates <= 4: raise ValueError
            rpm = int(self.deoxygenation_rpm_edit.text())
            return {"replicates": replicates, "deoxygenation_rpm": rpm, "save_folder": self.save_folder}
        except ValueError:
            QMessageBox.warning(self, "Invalid Input", "Please enter valid numbers for replicates (1-4) and RPM.")
            return None

# ──────────────────────────────────────────────────────────────────────────────
# MAIN PAGE WIDGET
# ──────────────────────────────────────────────────────────────────────────────
class KlaGassingOutPage(QWidget):
    def __init__(self, comm_handler, kla_cascade_page, parent=None):
        super().__init__(parent)
        self.comm_handler = comm_handler
        self.kla_cascade_page = kla_cascade_page

        # --- Parameters ---
        self.gassing_out_config = {} # Will be loaded from preferences
        self.KLA_CONVERSION_FACTOR = 3600.0
        self.initial_time = time.time()
        self.EPS = 1e-9

        # --- State Variables ---
        self.is_capturing = False; self.is_logging_active = False
        self.waiting_for_stabilization = False; self.output_file = None
        self.capture_start_time = 0.0
        self.auto_routine_active = False; self.auto_routine_state = "IDLE"
        self.auto_settings = {}; self.auto_points_to_test = []
        self.auto_current_run_index = 0; self.auto_current_replicate = 1
        self.auto_replicate_results = []; self.auto_dialog = None

        # --- Data Buffers ---
        self.time_data = deque(maxlen=1000); self.do_raw_data = deque(maxlen=1000)
        self.do_smoothed_data = deque(maxlen=1000); self.kla_inst_raw_data = deque(maxlen=1000)
        self.kla_inst_smoothed_data = deque(maxlen=1000)
        self.do_derivative_buffer = deque(maxlen=5) # Default size, will be updated from config

        # --- TCN Model & Data ---
        self.model = None # Will be loaded after config is set
        self.kla_scaler = RunningMinMax(); self.time_scaler = RunningMinMax()
        self.kla_capture_buffer = []; self.time_capture_buffer = []

        self._init_ui()

        self.update_timer = QTimer(self)
        self.update_timer.timeout.connect(self.update_live_data)
        self.update_timer.start(200)

    def _load_model(self):
        model_path = self.gassing_out_config.get('model_path', 'kLa_TNC_gassing_out.pth')
        try:
            device = torch.device('cpu'); model = DilatedTCN().to(device)
            model.load_state_dict(torch.load(model_path, map_location=device)); model.eval()
            print(f"TCN model loaded from {model_path}.")
            self.model = model
        except Exception as e:
            print(f"Error loading TCN model: {e}")
            QMessageBox.critical(self, "Model Error", f"Could not load TCN model from {model_path}.\nPlateau detection will be disabled.")
            self.model = None

    def _init_ui(self):
        main_layout = QVBoxLayout(self)
        top_controls_layout = QHBoxLayout()

        # Manual Controls
        manual_controls_group = QGroupBox("Control Settings")
        manual_controls_layout = QHBoxLayout(manual_controls_group)
        self.capture_button = QPushButton("Start Data Capture")
        self.capture_button.setCheckable(True)
        self.capture_button.clicked.connect(self.toggle_data_capture)
        manual_controls_layout.addWidget(self.capture_button)
        settings_form = QFormLayout()
        self.min_oxy_edit = QLineEdit("20.0")
        settings_form.addRow("Min DO to Capture Start (%):", self.min_oxy_edit)
        manual_controls_layout.addLayout(settings_form)
        top_controls_layout.addWidget(manual_controls_group)

        # Automatic Routine Controls
        auto_group = QGroupBox("Cascade Experimental kLa")
        auto_layout = QHBoxLayout(auto_group)
        self.auto_kla_button = QPushButton("Automatic kLa")
        self.auto_kla_button.clicked.connect(self.open_auto_kla_dialog)
        self.stop_auto_kla_button = QPushButton("Stop Routine")
        self.stop_auto_kla_button.clicked.connect(self._stop_auto_routine)
        self.stop_auto_kla_button.setEnabled(False)
        auto_layout.addWidget(self.auto_kla_button)
        auto_layout.addWidget(self.stop_auto_kla_button)
        top_controls_layout.addWidget(auto_group)

        # Status
        status_group = QGroupBox("Status")
        status_layout = QHBoxLayout(status_group)
        self.status_label = QLabel("Status: Idle")
        font = self.status_label.font(); font.setBold(True); self.status_label.setFont(font)
        status_layout.addWidget(self.status_label)
        top_controls_layout.addWidget(status_group, stretch=1)

        # Options Button
        options_group = QGroupBox("Advanced")
        options_layout = QHBoxLayout(options_group)
        self.options_button = QPushButton("Options...")
        self.options_button.clicked.connect(self.open_options_dialog)
        options_layout.addWidget(self.options_button)
        top_controls_layout.addWidget(options_group)

        main_layout.addLayout(top_controls_layout)
        
        # Graphs
        splitter = QSplitter(Qt.Horizontal)
        self.do_plot_widget = pg.PlotWidget(); self.do_plot_widget.setLabel('left', 'DO', units='%'); self.do_plot_widget.setLabel('bottom', 'Time', units='s')
        self.do_plot_widget.showGrid(x=True, y=True); self.do_plot_widget.addLegend()
        self.do_raw_curve = self.do_plot_widget.plot(pen=(100, 100, 150), name="DO (Raw)")
        self.do_smoothed_curve = self.do_plot_widget.plot(pen=pg.mkPen('c', width=2), name="DO (Smoothed)")
        self.do_plot_widget.enableAutoRange(axis='y', enable=True)
        splitter.addWidget(self.do_plot_widget)
        
        self.kla_plot_widget = pg.PlotWidget(); self.kla_plot_widget.setLabel('left', 'kLa_inst', units='h⁻¹'); self.kla_plot_widget.setLabel('bottom', 'Time', units='s')
        self.kla_plot_widget.showGrid(x=True, y=True); self.kla_plot_widget.addLegend()
        self.kla_raw_curve = self.kla_plot_widget.plot(pen=(180, 180, 180), name="kLa_inst (Raw)")
        self.kla_smoothed_curve = self.kla_plot_widget.plot(pen=pg.mkPen('m', width=2), name="kLa_inst (Smoothed)")
        self.kla_plot_widget.enableAutoRange(axis='y', enable=True)
        self.prob_view = pg.ViewBox(); self.kla_plot_widget.scene().addItem(self.prob_view)
        self.kla_plot_widget.getAxis('right').linkToView(self.prob_view); self.prob_view.setXLink(self.kla_plot_widget.getViewBox())
        self.kla_plot_widget.getAxis('right').setLabel('P(plateau)', color='#FFD700')
        self.prob_curve = pg.PlotDataItem(pen=pg.mkPen('#FFD700', width=2), name="P(plateau)")
        self.prob_view.setYRange(0, 1.1, padding=0); self.prob_view.addItem(self.prob_curve)
        self.kla_plot_widget.getViewBox().sigResized.connect(lambda: self.prob_view.setGeometry(self.kla_plot_widget.getViewBox().sceneBoundingRect()))
        self.plateau_avg_line = pg.InfiniteLine(angle=0, movable=False, pen=pg.mkPen('g', width=3, style=Qt.DotLine), label='kLa_avg={value:.2f} h⁻¹')
        self.plateau_v_line1 = pg.InfiniteLine(angle=90, movable=False, pen=pg.mkPen('w', style=Qt.DashLine))
        self.plateau_v_line2 = pg.InfiniteLine(angle=90, movable=False, pen=pg.mkPen('w', style=Qt.DashLine))
        for item in [self.plateau_avg_line, self.plateau_v_line1, self.plateau_v_line2]:
            self.kla_plot_widget.addItem(item, ignoreBounds=True); item.hide()
        splitter.addWidget(self.kla_plot_widget)
        
        main_layout.addWidget(splitter)

    def update_live_data(self):
        self.comm_handler.read_and_parse_data()
        current_do_raw = self.comm_handler.oxyReadVal
        if current_do_raw < 0: return

        elapsed_time = time.time() - self.initial_time
        
        self.time_data.append(elapsed_time)
        self.do_raw_data.append(current_do_raw)
        
        do_avg_win = int(self.gassing_out_config.get('do_moving_average_window', 10))
        last_n_do = list(self.do_raw_data)[-do_avg_win:]
        do_smoothed = np.mean(last_n_do) if last_n_do else 0.0
        self.do_smoothed_data.append(do_smoothed)

        dC_dt = 0
        deriv_win = int(self.gassing_out_config.get('derivative_window', 5))
        if len(self.time_data) > deriv_win:
            t_slice = np.array(list(self.time_data)[-deriv_win:])
            do_slice = np.array(list(self.do_smoothed_data)[-deriv_win:])
            # Use linear regression (slope) for a more stable derivative
            try:
                dC_dt, _ = np.polyfit(t_slice, do_slice, 1)
            except np.linalg.LinAlgError:
                dC_dt = 0 # Handle potential errors in polyfit

        kla_inst = 0
        c_sat = float(self.gassing_out_config.get('c_sat', 100.0))
        if abs(c_sat - do_smoothed) > self.EPS:
            kla_inst_s = dC_dt / (c_sat - do_smoothed)
            kla_inst = kla_inst_s * self.KLA_CONVERSION_FACTOR
        
        self.kla_inst_raw_data.append(kla_inst)
        kla_avg_win = int(self.gassing_out_config.get('kla_moving_average_window', 15))
        last_n_kla = list(self.kla_inst_raw_data)[-kla_avg_win:]
        kla_smoothed = np.mean(last_n_kla) if last_n_kla else 0.0
        self.kla_inst_smoothed_data.append(kla_smoothed)
        
        self.do_raw_curve.setData(list(self.time_data), list(self.do_raw_data))
        self.do_smoothed_curve.setData(list(self.time_data), list(self.do_smoothed_data))
        self.kla_raw_curve.setData(list(self.time_data), list(self.kla_inst_raw_data))
        self.kla_smoothed_curve.setData(list(self.time_data), list(self.kla_inst_smoothed_data))

        if self.auto_routine_active:
            self.run_auto_routine_step(do_smoothed, dC_dt, kla_smoothed)
        elif self.is_capturing:
            self.run_manual_capture_step(do_smoothed, dC_dt, kla_smoothed)

    def run_manual_capture_step(self, do_smoothed, dC_dt, kla_smoothed):
        min_oxy_test = float(self.min_oxy_edit.text())
        stabilization_thr = float(self.gassing_out_config.get('stabilization_derivative_threshold', 0.05))
        
        if self.waiting_for_stabilization:
            self.status_label.setText(f"Status: Waiting for DO < {min_oxy_test}% and stabilization...")
            self.do_derivative_buffer.append(dC_dt)
            is_below = do_smoothed < min_oxy_test
            is_stable = all(abs(d) < stabilization_thr for d in self.do_derivative_buffer)
            if is_below and len(self.do_derivative_buffer) == self.do_derivative_buffer.maxlen and is_stable:
                self.waiting_for_stabilization, self.is_logging_active = False, True
                self.capture_start_time = time.time()
                print("Stabilization detected. Starting data logging.")
        
        if self.is_logging_active:
            capture_time = time.time() - self.capture_start_time
            self.status_label.setText("Status: Capturing & Analyzing...")
            if self.output_file:
                self.output_file.write(f"{capture_time:.4f}\t{do_smoothed:.4f}\t{kla_smoothed:.6f}\n")
            if self.model:
                self.run_tcn_inference(capture_time, kla_smoothed)

    def toggle_data_capture(self, checked):
        if self.auto_routine_active:
            QMessageBox.warning(self, "Routine Active", "Cannot start manual capture while automatic routine is running.")
            self.capture_button.setChecked(False); return
        if checked:
            min_oxy_test = float(self.min_oxy_edit.text())
            if self.comm_handler.oxyReadVal > min_oxy_test:
                QMessageBox.warning(self, "High Oxygen Level", f"Current DO is {self.comm_handler.oxyReadVal:.1f}%. Data capture will begin logging only after DO drops and stabilizes below {min_oxy_test}%.")
            file_path, _ = QFileDialog.getSaveFileName(self, "Save Data Capture File", "", "Text Files (*.txt);;All Files (*)")
            if not file_path: self.capture_button.setChecked(False); return
            try:
                self.output_file = open(file_path, 'w')
                self.output_file.write("time (s)\tDO_smoothed (%)\tkLa_inst_smoothed (h-1)\n")
            except IOError as e: QMessageBox.critical(self, "File Error", f"Could not open file: {e}"); self.capture_button.setChecked(False); return
            self._reset_capture_state(); self.is_capturing = True; self.waiting_for_stabilization = True
            self.capture_button.setText("Stop Data Capture")
        else:
            self.is_capturing, self.is_logging_active = False, False
            if self.output_file: self.output_file.close(); self.output_file = None
            self.capture_button.setText("Start Data Capture"); self.status_label.setText("Status: Idle")
            for item in [self.plateau_avg_line, self.plateau_v_line1, self.plateau_v_line2]: item.hide()
            print("Data capture stopped.")

    def _reset_capture_state(self):
        self.kla_scaler, self.time_scaler = RunningMinMax(), RunningMinMax()
        self.kla_capture_buffer, self.time_capture_buffer = [], []
        stabilization_samples = int(self.gassing_out_config.get('stabilization_samples', 5))
        self.do_derivative_buffer = deque(maxlen=stabilization_samples)
        self.prob_curve.clear()
        for item in [self.plateau_avg_line, self.plateau_v_line1, self.plateau_v_line2]: item.hide()

    def run_tcn_inference(self, current_time, current_kla):
        self.time_capture_buffer.append(current_time); self.kla_capture_buffer.append(current_kla)
        if len(self.time_capture_buffer) < 20: return
        self.time_scaler.update(current_time); self.kla_scaler.update(current_kla)
        t_all = np.array(self.time_capture_buffer); kla_all = np.array(self.kla_capture_buffer)
        t_n = self.time_scaler.normalise(t_all); kla_n = self.kla_scaler.normalise(kla_all)
        x = np.stack([kla_n, t_n], axis=0)[None, ...].astype(np.float32)
        with torch.no_grad():
            logits = self.model(torch.from_numpy(x).to('cpu'))
            probs_raw = torch.sigmoid(logits[0,0]).cpu().numpy()
            probs_i = gaussian_filter1d(probs_raw, sigma=2.0, mode='nearest')
        self.prob_curve.setData(t_all, probs_i)
        plateau_thr = float(self.gassing_out_config.get('plateau_thr', 0.77))
        t_start, t_end = detect_plateau_segment(t_all, probs_i, thr=plateau_thr)
        if t_start is not None:
            mask = (t_all >= t_start) & (t_all <= t_end)
            if np.any(mask):
                avg_kla = float(kla_all[mask].mean())
                self.plateau_v_line1.setPos(t_start); self.plateau_v_line2.setPos(t_end)
                self.plateau_avg_line.setPos(avg_kla); self.plateau_avg_line.label.setFormat(f'{avg_kla:.2f}')
                self.plateau_v_line1.show(); self.plateau_v_line2.show(); self.plateau_avg_line.show()
                return avg_kla
        return None

    def open_options_dialog(self):
        dialog = GassingOutOptionsDialog(self.gassing_out_config, self)
        if dialog.exec_() == QDialog.Accepted:
            self.gassing_out_config = dialog.get_values()
            self._apply_config()
            print("Gassing-out options updated.")
            # Trigger auto-save in main window
            if hasattr(self.window(), 'on_editing_finished'):
                self.window().on_editing_finished()

    def _apply_config(self):
        """Applies the configuration settings to the widget's state."""
        stabilization_samples = int(self.gassing_out_config.get('stabilization_samples', 5))
        if self.do_derivative_buffer.maxlen != stabilization_samples:
            self.do_derivative_buffer = deque(maxlen=stabilization_samples)
        self._load_model() # Reload model in case path changed

    # --- (The automatic routine methods are unchanged but will now use the config dictionary) ---
    def open_auto_kla_dialog(self):
        if self.is_capturing or self.auto_routine_active: QMessageBox.warning(self, "Busy", "Cannot start a new routine while another capture is active."); return
        points = self.kla_cascade_page.interactive_kla_widget.get_points()
        if not points: QMessageBox.warning(self, "No Points", "Please add experimental points on the 'kLa Cascade' page first."); return
        self.auto_dialog = AutomaticKlaDialog(points, self)
        if self.auto_dialog.exec_() == QDialog.Accepted:
            settings = self.auto_dialog.get_settings()
            if settings: self._start_auto_routine(settings, points)
    def _start_auto_routine(self, settings, points):
        self.auto_settings = settings; self.auto_points_to_test = points
        self.auto_current_run_index = 0; self.auto_current_replicate = 1
        self.auto_replicate_results = []; self.auto_routine_active = True
        self.auto_kla_button.setEnabled(False); self.stop_auto_kla_button.setEnabled(True)
        self.capture_button.setEnabled(False); print("Starting automatic kLa routine.")
        self._start_deoxygenation()
    def _stop_auto_routine(self):
        print("Stopping automatic kLa routine.")
        self.comm_handler.send_command({"v1": 0, "v_Flow": 1, "flowSetpoint": 0, "motorSetpoint": 0})
        if self.output_file: self.output_file.close(); self.output_file = None
        self.auto_routine_active = False; self.auto_routine_state = "IDLE"
        self.status_label.setText("Status: Routine Stopped by User")
        self.auto_kla_button.setEnabled(True); self.stop_auto_kla_button.setEnabled(False)
        if self.auto_dialog and self.auto_current_run_index < self.auto_dialog.table.rowCount():
             self.auto_dialog.table.item(self.auto_current_run_index, 0).setText("Stopped")
        self.capture_button.setEnabled(True)
    def run_auto_routine_step(self, do_smoothed, dC_dt, kla_smoothed):
        min_oxy_test = float(self.min_oxy_edit.text())
        stabilization_thr = float(self.gassing_out_config.get('stabilization_derivative_threshold', 0.05))
        if self.auto_routine_state == "DEOXYGENATING":
            if do_smoothed < min_oxy_test:
                print("DO threshold reached. Stopping nitrogen and waiting for stabilization.")
                self.comm_handler.send_command({"v1": 0}); self.auto_routine_state = "STABILIZING"
                self.do_derivative_buffer.clear()
        elif self.auto_routine_state == "STABILIZING":
            self.do_derivative_buffer.append(dC_dt)
            is_stable = all(abs(d) < stabilization_thr for d in self.do_derivative_buffer)
            if len(self.do_derivative_buffer) == self.do_derivative_buffer.maxlen and is_stable:
                print("System stabilized. Starting reoxygenation run.")
                self._start_reoxygenation_run()
        elif self.auto_routine_state == "REOXYGENATING":
            capture_time = time.time() - self.capture_start_time
            if self.output_file: self.output_file.write(f"{capture_time:.4f}\t{do_smoothed:.4f}\t{kla_smoothed:.6f}\n")
            avg_kla = None
            if self.model: avg_kla = self.run_tcn_inference(capture_time, kla_smoothed)
            if do_smoothed > 80.0:
                print("Reoxygenation complete.")
                self._finalize_run(avg_kla)
                self._advance_to_next_run()
    def _start_deoxygenation(self):
        self.auto_routine_state = "DEOXYGENATING"
        q, n, _ = self.auto_points_to_test[self.auto_current_run_index]
        reps = self.auto_settings['replicates']; rpm = self.auto_settings['deoxygenation_rpm']
        status_text = f"Run {self.auto_current_run_index + 1}/{len(self.auto_points_to_test)} (Q:{q}, N:{n}), Rep {self.auto_current_replicate}/{reps} - Deoxygenating..."
        self.status_label.setText(status_text)
        self.auto_dialog.table.item(self.auto_current_run_index, 0).setText(f"Running Rep {self.auto_current_replicate}")
        print(f"Starting deoxygenation with {rpm} RPM.")
        self.comm_handler.send_command({"v1": 1, "v_Flow": 1, "motorSetpoint": rpm})
    def _start_reoxygenation_run(self):
        self._reset_capture_state(); self.auto_routine_state = "REOXYGENATING"
        self.capture_start_time = time.time()
        q_target, n_target, _ = self.auto_points_to_test[self.auto_current_run_index]
        filename = f"Run_{self.auto_current_run_index+1}_Q{q_target}_N{n_target}_Rep{self.auto_current_replicate}.txt"
        filepath = os.path.join(self.auto_settings['save_folder'], filename)
        try:
            self.output_file = open(filepath, 'w')
            self.output_file.write("time (s)\tDO_smoothed (%)\tkLa_inst_smoothed (h-1)\n")
        except IOError as e: QMessageBox.critical(self, "File Error", f"Could not create run file: {e}"); self._stop_auto_routine(); return
        print(f"Starting reoxygenation with Q={q_target}, N={n_target}.")
        self.comm_handler.send_command({"flowSetpoint": q_target, "v_Flow": 0, "motorSetpoint": n_target})
    def _finalize_run(self, final_kla):
        if self.output_file: self.output_file.close(); self.output_file = None
        if final_kla is None and self.kla_capture_buffer:
            final_kla = np.mean(self.kla_capture_buffer[-50:])
            print(f"TCN failed to find plateau. Using fallback average kLa: {final_kla:.2f}")
        if final_kla is not None:
            self.auto_replicate_results.append(final_kla)
            print(f"Run finished. Calculated kLa: {final_kla:.2f}")
        else:
            print("Run finished, but no valid kLa could be calculated.")
            self.auto_replicate_results.append(np.nan)
    def _advance_to_next_run(self):
        if self.auto_current_replicate < self.auto_settings['replicates']:
            self.auto_current_replicate += 1; self._start_deoxygenation()
        else:
            valid_results = [r for r in self.auto_replicate_results if not np.isnan(r)]
            if valid_results:
                mean_kla = np.mean(valid_results)
                print(f"Completed all replicates for point {self.auto_current_run_index}. Mean kLa: {mean_kla:.2f}")
                self._update_main_app_data(self.auto_current_run_index, mean_kla)
                self.auto_dialog.table.item(self.auto_current_run_index, 0).setText(f"Done ({mean_kla:.2f})")
            else:
                print(f"Completed all replicates for point {self.auto_current_run_index}, but no valid kLa was found.")
                self.auto_dialog.table.item(self.auto_current_run_index, 0).setText("Failed")
            self.auto_current_run_index += 1; self.auto_current_replicate = 1; self.auto_replicate_results = []
            if self.auto_current_run_index < len(self.auto_points_to_test):
                self._start_deoxygenation()
            else:
                print("All experimental runs are complete.")
                QMessageBox.information(self, "Success", "Automatic kLa routine has finished successfully.")
                self._stop_auto_routine()
    def _update_main_app_data(self, point_index, new_kla):
        try:
            interactive_widget = self.kla_cascade_page.interactive_kla_widget
            interactive_widget.points[point_index][2] = new_kla
            interactive_widget._update_plot(); interactive_widget.points_changed.emit()
            gradient_widget = self.kla_cascade_page.gradient_widget
            gradient_widget.run_gradient_ascent()
            print(f"Updated point {point_index} with kLa={new_kla} and refreshed gradient ascent.")
        except Exception as e:
            print(f"Error updating main application data: {e}")
    def closeEvent(self, event):
        if self.output_file: self.output_file.close()
        if self.auto_routine_active: self._stop_auto_routine()
        event.accept()