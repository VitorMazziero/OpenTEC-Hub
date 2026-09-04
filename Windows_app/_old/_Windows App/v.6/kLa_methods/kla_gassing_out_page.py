#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
kla_gassing_out_page.py: A PySide6 widget for live kLa determination.

This module provides a tab for the application that supports both:
1. Manual gassing-out experiments with live data visualization.
2. A fully automated routine to determine experimental kLa values for
   a predefined set of operational conditions (N and Q), designed to
   populate the data for the kLa cascade control page.

Updates in this version:
- Enforced minimum reoxygenation ("Air Addition") acquisition time of 30 s.
- Clear stage definitions and behavior:
  * Deoxygenation → **Nitrogen Addition**: graph cleared, TCN disabled.
  * Reoxygenation → **Air Addition**: graph cleared, TCN enabled.
- Added interactive post-run dialog "Confirm the kLa region" that shows kLa and
  TCN guide; user can drag vertical lines (t_i, t_f) to select plateau and confirm.
  Selected region average is saved and used for the run.
- ✨ New: "Open Table" button in the "Cascade Experimental kLa" block to view the
  experimental points table currently in use.
- ✨ New: Status label now shows compact run info, e.g. "Nitrogen - 1/10 - 200 RPM 2.00 L/min",
  updating automatically per run and stage.
- ✨ New: "Stop and calculate kLa" button (enabled only during Air Addition) to stop
  the run immediately and open the region-selection dialog.
- ✨ New: Options now include "Reoxygenation End DO (%)" to configure the end-DO
  target instead of the previously fixed 80%.
"""

import os
import time
from collections import deque

import numpy as np
import torch
import torch.nn as nn
from scipy.ndimage import gaussian_filter1d, binary_closing, binary_opening, label
from scipy.optimize import minimize_scalar

from PySide6.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QGroupBox, QLabel, QPushButton,
    QFileDialog, QMessageBox, QFormLayout, QLineEdit, QSplitter, QDialog,
    QTableWidget, QTableWidgetItem, QHeaderView, QDialogButtonBox, QAbstractItemView, QCheckBox, QFrame
)
from PySide6.QtCore import QTimer, Qt
import pyqtgraph as pg

# ──────────────────────────────────────────────────────────────────────────────
# TCN MODEL AND HELPER CLASSES
# ──────────────────────────────────────────────────────────────────────────────
EPS_SCALER = 1e-6


class RunningMinMax:
    def __init__(self):
        self.min = None
        self.max = None

    def update(self, x):
        x = np.asarray(x)
        if x.size == 0:
            return
        xm, xM = float(np.min(x)), float(np.max(x))
        if self.min is None or xm < self.min:
            self.min = xm
        if self.max is None or xM > self.max:
            self.max = xM

    def normalise(self, x: np.ndarray) -> np.ndarray:
        x = np.asarray(x, dtype=float)
        if (
            self.min is None
            or self.max is None
            or (self.max - self.min) < EPS_SCALER
        ):
            return np.zeros_like(x, dtype=np.float32)
        return np.clip(
            (x - self.min) / (self.max - self.min), 0.0, 1.0
        ).astype(np.float32)


def detect_plateau_segment(t, probs, thr, smooth_sigma=2.0, min_hole=5, min_island=5):
    p = gaussian_filter1d(probs, sigma=smooth_sigma, mode="nearest")
    m = p > thr
    m = binary_closing(m, structure=np.ones(min_hole, bool))
    m = binary_opening(m, structure=np.ones(min_island, bool))
    labels, nlab = label(m)
    best_len, best_seg_indices = 0, None
    for seg_label in range(1, nlab + 1):
        idx = np.where(labels == seg_label)[0]
        if idx.size > best_len:
            best_len, best_seg_indices = idx.size, idx
    if best_seg_indices is None:
        return None, None
    return float(t[best_seg_indices[0]]), float(t[best_seg_indices[-1]])


class ResBlock(nn.Module):
    def __init__(self, in_ch, out_ch, d, k=3, drop=0.1):
        super().__init__()
        self.conv = nn.Conv1d(in_ch, out_ch, k, padding=d * (k - 1) // 2, dilation=d)
        self.bn = nn.BatchNorm1d(out_ch)
        self.relu = nn.ReLU(inplace=True)
        self.drop = nn.Dropout(drop)
        self.match = in_ch == out_ch

    def forward(self, x):
        z = self.relu(self.bn(self.conv(x)))
        z = self.drop(z)
        return z + x if self.match else z


class DilatedTCN(nn.Module):
    def __init__(self, in_ch=2, layers=7, filters=64, k=3, drop=0.1):
        super().__init__()
        blocks = [
            ResBlock(in_ch if i == 0 else filters, filters, 2**i, k, drop)
            for i in range(layers)
        ]
        self.encoder = nn.Sequential(*blocks)
        self.classifier = nn.Conv1d(filters, 1, 1)

    def forward(self, x):
        return self.classifier(self.encoder(x))


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

        # --- Automatic C* Section ---
        self.auto_csat_cb = QCheckBox("Enable Automatic C* Optimization")
        is_auto = str(self.config.get("auto_csat_enabled", "False")) == "True"
        self.auto_csat_cb.setChecked(is_auto)
        layout.addRow(self.auto_csat_cb)

        # Standard parameters
        params = {
            "c_sat": ("DO Saturation (%):", "100.0"),
            "stabilization_derivative_threshold": (
                "Stabilization Threshold (dC/dt):",
                "0.05",
            ),
            "stabilization_samples": ("Stabilization Samples:", "5"),
            "plateau_thr": ("Plateau Threshold (TCN):", "0.77"),
            "model_path": ("TCN Model Path:", "kLa_methods/kLa_TNC_gassing_out.pth"),
            "kla_moving_average_window": ("kLa Smoothing Window:", "15"),
            "reoxygenation_end_percent": ("Reoxygenation End DO (%):", "80.0"),
            "do_tau_s": ("DO τ (s) [EWMA]:", "2.0"),
            "deriv_span_s": ("Derivative span (s):", "6.0"),
            "kla_tau_s": ("kLa τ (s) [EWMA]:", "3.0"),
            "deriv_poly_order": ("Derivative poly order (1-3):", "2"),
            "denom_guard_pct": ("Min (c_sat - DO) %:", "0.5"),
        }
        
        # New Auto C* Params
        auto_params = {
            "auto_csat_period": ("Optimization Period (s):", "5.0"),
            "auto_csat_min_c": ("Trigger Min DO (%):", "30.0"),
            "auto_csat_max_c": ("Trigger Max DO (%):", "70.0"),
            "auto_csat_min_t": ("Trigger Min Run Time (s):", "5.0"),
        }

        # Create widgets for standard params
        for key, (label_text, default) in params.items():
            value = self.config.get(key, default)
            widget = QLineEdit(str(value))
            self.widgets[key] = widget
            layout.addRow(label_text, widget)

        # Separator
        layout.addRow(QLabel("<b>Automatic C* Settings:</b>"))

        # Create widgets for auto params
        for key, (label_text, default) in auto_params.items():
            value = self.config.get(key, default)
            widget = QLineEdit(str(value))
            self.widgets[key] = widget
            layout.addRow(label_text, widget)

        # Logic: Disable Manual C* if Auto is checked
        self.auto_csat_cb.toggled.connect(self._toggle_manual_entry)
        self._toggle_manual_entry(is_auto)

        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok
            | QDialogButtonBox.StandardButton.Cancel,
            Qt.Orientation.Horizontal,
            self,
        )
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addRow(buttons)

    def _toggle_manual_entry(self, checked):
        if "c_sat" in self.widgets:
            self.widgets["c_sat"].setEnabled(not checked)

    def get_values(self):
        values = {key: widget.text() for key, widget in self.widgets.items()}
        values["auto_csat_enabled"] = str(self.auto_csat_cb.isChecked())
        return values

class AutomaticKlaDialog(QDialog):
    # This class remains largely unchanged, with small guardrails
    def __init__(self, experimental_points, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Automatic kLa Routine Setup")
        self.experimental_points = experimental_points
        self.save_folder = ""
        main_layout = QVBoxLayout(self)
        warning_text = (
            "<b>Warning:</b> Ensure Nitrogen and Aeration are connected to the "
            "50L/min flowmeter's left valve (V1), OR be prepared to manually "
            "switch gases when prompted by the software status."
        )
        main_layout.addWidget(QLabel(warning_text))
        self.table = QTableWidget()
        self.table.setColumnCount(3)
        self.table.setHorizontalHeaderLabels(["Status", "Q (L/min)", "N (RPM)"])
        self.table.setRowCount(len(self.experimental_points))
        for i, (q, n, _) in enumerate(self.experimental_points):
            self.table.setItem(i, 0, QTableWidgetItem("Pending"))
            self.table.setItem(i, 1, QTableWidgetItem(f"{float(q):.2f}"))
            self.table.setItem(i, 2, QTableWidgetItem(f"{float(n):.0f}"))
        self.table.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeMode.Stretch)
        self.table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
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
        button_box.addButton(self.start_button, QDialogButtonBox.ButtonRole.ActionRole)
        main_layout.addWidget(button_box)

    def select_folder_and_start(self):
        folder = QFileDialog.getExistingDirectory(self, "Select Folder to Save Run Data")
        if folder:
            self.save_folder = folder
            self.accept()

    def get_settings(self):
        try:
            replicates = int(self.replicates_edit.text())
            if not 1 <= replicates <= 4:
                raise ValueError
            rpm = int(self.deoxygenation_rpm_edit.text())
            return {
                "replicates": replicates,
                "deoxygenation_rpm": rpm,
                "save_folder": self.save_folder,
            }
        except ValueError:
            QMessageBox.warning(
                self,
                "Invalid Input",
                "Please enter valid numbers for replicates (1-4) and RPM.",
            )
            return None


# ──────────────────────────────────────────────────────────────────────────────
# SIMPLE VIEW-ONLY DIALOG TO SHOW CURRENT EXPERIMENTAL POINTS
# ──────────────────────────────────────────────────────────────────────────────
class PointsTableDialog(QDialog):
    """Read-only view of the current experimental points used for the cascade runs."""

    def __init__(self, points, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Experimental Points (Q, N, kLa)")
        main = QVBoxLayout(self)
        table = QTableWidget(self)
        table.setColumnCount(3)
        table.setHorizontalHeaderLabels(["Q (L/min)", "N (RPM)", "kLa (h⁻¹)"])
        table.setRowCount(len(points))
        for i, (q, n, kla) in enumerate(points):
            table.setItem(i, 0, QTableWidgetItem(f"{float(q):.2f}"))
            table.setItem(i, 1, QTableWidgetItem(f"{float(n):.0f}"))
            if kla is None or (isinstance(kla, float) and np.isnan(kla)):
                kla_text = "—"
            else:
                kla_text = f"{float(kla):.2f}"
            table.setItem(i, 2, QTableWidgetItem(kla_text))
        table.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeMode.Stretch)
        table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        main.addWidget(table)
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Close)
        buttons.rejected.connect(self.reject)
        buttons.accepted.connect(self.accept)
        main.addWidget(buttons)


# ──────────────────────────────────────────────────────────────────────────────
# INTERACTIVE CONFIRMATION DIALOG
# ──────────────────────────────────────────────────────────────────────────────
class ConfirmKlaDialog(QDialog):
    def __init__(self, t_data, kla_data, do_data=None, dcdt_data=None, 
                 probs=None, suggested_region=(None, None), 
                 config_prefs=None, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Confirm kLa Plateau")
        self.resize(950, 600)
        
        # --- Data Storage ---
        self.t_data = np.array(t_data)
        self.kla_data = np.array(kla_data)
        
        # Data for recalculation
        self.do_data = np.array(do_data) if do_data is not None else np.zeros_like(self.t_data)
        self.dcdt_data = np.array(dcdt_data) if dcdt_data is not None else np.zeros_like(self.t_data)
        
        self.config = config_prefs or {}
        self.current_c_star = float(self.config.get("c_sat", 100.0))

        # Result placeholders
        self.result_kla = None
        self.result_t_start = None
        self.result_t_end = None

        self._init_ui()
        
        # Plot initial data
        self.kla_curve.setData(self.t_data, self.kla_data)
        
        if probs is not None and len(probs) == len(self.t_data):
            max_kla = np.nanmax(self.kla_data) if len(self.kla_data) > 0 else 1.0
            scaled_probs = probs * (max_kla * 0.2) 
            self.prob_curve.setData(self.t_data, scaled_probs)

        # Region Selection
        t_i, t_f = suggested_region
        if t_i is None or t_f is None:
            if len(self.t_data) > 2:
                t_i = self.t_data[int(len(self.t_data) * 0.8)]
                t_f = self.t_data[-1]
            else:
                t_i, t_f = 0, 1
        
        self.region = pg.LinearRegionItem([t_i, t_f], brush=pg.mkBrush(0, 255, 0, 50))
        self.region.setZValue(10)
        self.region.sigRegionChanged.connect(self.update_stats)
        self.plot_widget.addItem(self.region)
        
        self.update_stats()

    def _init_ui(self):
        layout = QVBoxLayout(self)

        # --- Plot ---
        self.plot_widget = pg.PlotWidget(title="Select Plateau Region")
        self.plot_widget.setLabel("left", "kLa", "h-1")
        self.plot_widget.setLabel("bottom", "Time", "s")
        self.plot_widget.showGrid(x=True, y=True)
        self.kla_curve = self.plot_widget.plot(pen=pg.mkPen('y', width=2), name="kLa")
        self.prob_curve = self.plot_widget.plot(pen=pg.mkPen(color=(255, 255, 255, 100), width=1, style=Qt.PenStyle.DashLine), name="Prob Guide")
        self.avg_line = pg.InfiniteLine(angle=0, pen=pg.mkPen('c', width=2, style=Qt.PenStyle.DashLine))
        self.plot_widget.addItem(self.avg_line)
        layout.addWidget(self.plot_widget)

        # --- Controls Area ---
        controls_group = QGroupBox("C* Optimization & Adjustments")
        controls_layout = QHBoxLayout(controls_group)

        # 1. Optimizer Controls
        self.chk_auto_opt = QCheckBox("Use Optimizer")
        self.chk_auto_opt.setChecked(False)
        self.chk_auto_opt.toggled.connect(self.toggle_optimizer_mode)
        controls_layout.addWidget(self.chk_auto_opt)

        self.btn_run_opt = QPushButton("Find Best C*")
        self.btn_run_opt.setEnabled(False) # Disabled by default
        self.btn_run_opt.clicked.connect(self.run_static_optimization)
        controls_layout.addWidget(self.btn_run_opt)

        # Vertical Separator
        line = QFrame()
        line.setFrameShape(QFrame.VLine)
        line.setFrameShadow(QFrame.Sunken)
        controls_layout.addWidget(line)

        # 2. Manual Controls
        controls_layout.addWidget(QLabel("C* Value:"))
        self.c_star_edit = QLineEdit(str(self.current_c_star))
        self.c_star_edit.setFixedWidth(70)
        controls_layout.addWidget(self.c_star_edit)
        
        self.btn_recalc = QPushButton("Recalc Curve")
        self.btn_recalc.clicked.connect(self.recalculate_curve)
        controls_layout.addWidget(self.btn_recalc)
        
        controls_layout.addStretch()

        # 3. Stats Display
        self.stats_label = QLabel("Selected Avg: --")
        font = self.stats_label.font()
        font.setPointSize(12)
        font.setBold(True)
        self.stats_label.setFont(font)
        controls_layout.addWidget(self.stats_label)

        layout.addWidget(controls_group)

        # --- Dialog Buttons ---
        btn_box = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        btn_box.accepted.connect(self.accept_selection)
        btn_box.rejected.connect(self.reject)
        layout.addWidget(btn_box)

    def toggle_optimizer_mode(self, checked):
        """Enables/Disables inputs based on checkbox."""
        self.btn_run_opt.setEnabled(checked)
        self.c_star_edit.setEnabled(not checked)
        # We don't disable btn_recalc because user might want to manually tweak the optimized value

    def run_static_optimization(self):
        """Runs the Normalized Slope minimization on the static dataset."""
        # Get Thresholds
        try:
            min_c = float(self.config.get("auto_csat_min_c", 30.0))
            max_c = float(self.config.get("auto_csat_max_c", 70.0))
        except ValueError:
            min_c, max_c = 30.0, 70.0

        # Filter Data
        mask = (self.do_data >= min_c) & (self.do_data <= max_c) & (self.dcdt_data > 0.001)
        
        if np.sum(mask) < 10:
            QMessageBox.warning(self, "Optimization Error", 
                                f"Not enough data points between {min_c}% and {max_c}% DO.")
            return

        do_sub = self.do_data[mask]
        dcdt_sub = self.dcdt_data[mask]
        t_sub = self.t_data[mask]
        max_observed_do = np.max(do_sub)

        # Objective Function (Same as Online)
        def objective_function(c_sat_cand):
            if c_sat_cand <= max_observed_do + 0.1: return 1e9
            denom = c_sat_cand - do_sub
            kla_est = (dcdt_sub / denom) * 3600.0
            if len(kla_est) < 2: return 1e9
            slope, _ = np.polyfit(t_sub, kla_est, 1)
            avg_kla = np.mean(kla_est)
            if avg_kla < 1e-6: return 1e9
            return abs(slope) / abs(avg_kla)

        # Run Scipy Optimize
        lower = max_observed_do + 0.5
        upper = 150.0
        
        try:
            res = minimize_scalar(
                objective_function, bounds=(lower, upper), 
                method='bounded', options={'xatol': 0.01}
            )

            if res.success:
                optimized_c = res.x
                # Update UI
                self.c_star_edit.setText(f"{optimized_c:.2f}")
                print(f"Static Optimization Result: {optimized_c:.2f}")
                # Trigger Recalculation
                self.recalculate_curve()
                QMessageBox.information(self, "Success", f"Found optimal C*: {optimized_c:.2f}")
            else:
                QMessageBox.warning(self, "Failed", "Optimizer could not converge.")
        except Exception as e:
            QMessageBox.critical(self, "Error", f"Optimizer crashed: {e}")

    def recalculate_curve(self):
        """Re-computes kLa based on the new C* value in the text box."""
        try:
            new_c_star = float(self.c_star_edit.text())
        except ValueError:
            QMessageBox.warning(self, "Invalid Input", "C* must be a valid number.")
            return

        max_do = np.max(self.do_data) if len(self.do_data) > 0 else 0
        if new_c_star <= max_do:
             QMessageBox.warning(self, "Invalid C*", f"C* ({new_c_star}) must be > max DO ({max_do:.2f}).")
             return

        # Math
        denom = new_c_star - self.do_data
        denom[denom < 0.01] = 0.01 
        new_kla_raw = (self.dcdt_data / denom) * 3600.0
        
        # Smoothing
        window_size = int(self.config.get("kla_moving_average_window", 15))
        if window_size > 1:
            kernel = np.ones(window_size) / window_size
            new_kla_smoothed = np.convolve(new_kla_raw, kernel, mode='same')
            self.kla_data = new_kla_smoothed
        else:
            self.kla_data = new_kla_raw

        # Update Graph
        self.kla_curve.setData(self.t_data, self.kla_data)
        self.update_stats()
        
        # Save to config temporarily
        self.config["c_sat"] = str(new_c_star)

    def update_stats(self):
        min_x, max_x = self.region.getRegion()
        mask = (self.t_data >= min_x) & (self.t_data <= max_x)
        if np.any(mask):
            avg = np.mean(self.kla_data[mask])
            self.stats_label.setText(f"Selected Avg: {avg:.2f} h⁻¹")
            self.avg_line.setValue(avg)
            self.avg_line.show()
        else:
            self.stats_label.setText("Selected Avg: --")
            self.avg_line.hide()

    def accept_selection(self):
        min_x, max_x = self.region.getRegion()
        mask = (self.t_data >= min_x) & (self.t_data <= max_x)
        if np.any(mask):
            self.result_kla = np.mean(self.kla_data[mask])
            self.result_t_start = min_x
            self.result_t_end = max_x
            self.accept()
        else:
            QMessageBox.warning(self, "Selection Error", "Please select a valid time range.")

    def get_result(self):
        return self.result_kla, self.result_t_start, self.result_t_end

# ──────────────────────────────────────────────────────────────────────────────
# MAIN PAGE WIDGET
# ──────────────────────────────────────────────────────────────────────────────
class KlaGassingOutPage(QWidget):
    def __init__(self, comm_handler, kla_cascade_page, parent=None):
        super().__init__(parent)
        self.comm_handler = comm_handler
        self.kla_cascade_page = kla_cascade_page

        # --- Parameters ---
        self.gassing_out_config = {}  # Will be loaded from preferences
        self.KLA_CONVERSION_FACTOR = 3600.0
        self.initial_time = time.time()
        self.EPS = 1e-9
        # Enforced minimum acquisition time during Air Addition
        self.MIN_REOXY_CAPTURE_SEC = 30.0

        # --- State Variables ---
        self.is_capturing = False
        self.is_logging_active = False
        self.waiting_for_stabilization = False
        self.output_file = None
        self.capture_start_time = 0.0

        # Auto C* State
        self.last_opt_time = 0.0

        self.auto_routine_active = False
        self.auto_routine_state = "IDLE"
        self.auto_settings = {}
        self.auto_points_to_test = []
        self.auto_current_run_index = 0
        self.auto_current_replicate = 1
        self.auto_replicate_results = []
        self.auto_dialog = None
        self._last_run_filepath = None

        # Flow-command confirmation guard. Command dispatch is immediate and
        # independent of the sensor/cascade timer; these timers only govern
        # confirmation and safe retry.
        self.FLOW_ACK_RETRY_SEC = 3.0
        self.FLOW_ACK_MAX_RETRIES = 4
        self._pending_flow_command = None
        self._pending_flow_expected = None
        self._pending_flow_success = None
        self._pending_flow_last_send = 0.0
        self._pending_flow_retries = 0

        # --- Data Buffers ---
        self.time_data = deque(maxlen=1000)
        self.do_raw_data = deque(maxlen=1000)
        self.do_smoothed_data = deque(maxlen=1000)
        self.dcdt_data = deque(maxlen=1000)  # Stores dC/dt for recalculation
        self.kla_inst_raw_data = deque(maxlen=1000)
        self.kla_inst_smoothed_data = deque(maxlen=1000)
        self.do_derivative_buffer = deque(maxlen=5)

        # --- TCN Model & Data ---
        self.model = None
        self.kla_scaler = RunningMinMax()
        self.time_scaler = RunningMinMax()
        self.kla_capture_buffer = []
        self.time_capture_buffer = []
        self.tcn_enabled = False
        self.last_probs = None
        self.last_t_all = None
        self.last_pred_region = (None, None)

        self._init_ui()

        # Timer to periodically update
        self.update_timer = QTimer(self)
        self.update_timer.timeout.connect(self.update_live_data)
        if hasattr(self, "main_window"):
            self.update_timer.start(self.main_window.dataDelay)
        else:
            self.update_timer.start(1000)

    def _load_model(self):
        model_path = self.gassing_out_config.get(
            "model_path", "kLa_methods/kLa_TNC_gassing_out.pth"
        )
        try:
            device = torch.device("cpu")
            model = DilatedTCN().to(device)
            state = torch.load(model_path, map_location=device)
            if isinstance(state, dict) and "state_dict" in state:
                state = state["state_dict"]
            model.load_state_dict(state)
            model.eval()
            print(f"TCN model loaded from {model_path}.")
            self.model = model
        except Exception as e:
            print(f"Error loading TCN model: {e}")
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
        settings_form.addRow("Capture Start (DO%):", self.min_oxy_edit)
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
        # NEW buttons
        self.open_table_button = QPushButton("Open Table")
        self.open_table_button.clicked.connect(self.open_points_table_dialog)
        self.stop_and_calc_button = QPushButton("Stop and Calc")
        self.stop_and_calc_button.setEnabled(False)
        self.stop_and_calc_button.clicked.connect(self._stop_and_calculate_kla)
        # Add to layout
        auto_layout.addWidget(self.auto_kla_button)
        auto_layout.addWidget(self.stop_auto_kla_button)
        auto_layout.addWidget(self.open_table_button)
        auto_layout.addWidget(self.stop_and_calc_button)
        top_controls_layout.addWidget(auto_group)

        # Status
        status_group = QGroupBox("Status")
        status_layout = QHBoxLayout(status_group)
        self.status_label = QLabel("Status: Idle")
        font = self.status_label.font()
        font.setBold(True)
        self.status_label.setFont(font)
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
        splitter = QSplitter(Qt.Orientation.Horizontal)

        # DO plot
        self.do_plot_widget = pg.PlotWidget()
        self.do_plot_widget.setLabel("left", "DO", units="%")
        self.do_plot_widget.setLabel("bottom", "Time", units="s")
        self.do_plot_widget.showGrid(x=True, y=True)
        self.do_plot_widget.addLegend()
        self.do_raw_curve = self.do_plot_widget.plot(pen=(100, 100, 150), name="DO (Raw)")
        self.do_smoothed_curve = self.do_plot_widget.plot(
            pen=pg.mkPen("c", width=2), name="DO (Smoothed)"
        )
        self.do_plot_widget.enableAutoRange(axis="y", enable=True)
        splitter.addWidget(self.do_plot_widget)

        # kLa plot
        self.kla_plot_widget = pg.PlotWidget()
        self.kla_plot_widget.setLabel("left", "kLa_inst", units="h⁻¹")
        self.kla_plot_widget.setLabel("bottom", "Time", units="s")
        self.kla_plot_widget.showGrid(x=True, y=True)
        self.kla_plot_widget.addLegend()
        self.kla_raw_curve = self.kla_plot_widget.plot(
            pen=(180, 180, 180), name="kLa_inst (Raw)"
        )
        self.kla_smoothed_curve = self.kla_plot_widget.plot(
            pen=pg.mkPen("m", width=2), name="kLa_inst (Smoothed)"
        )
        self.kla_plot_widget.enableAutoRange(axis="y", enable=True)

        # Right axis for P(plateau)
        self.prob_view = pg.ViewBox()
        self.kla_plot_widget.scene().addItem(self.prob_view)
        self.kla_plot_widget.getAxis("right").linkToView(self.prob_view)
        self.prob_view.setXLink(self.kla_plot_widget.getViewBox())
        self.kla_plot_widget.getAxis("right").setLabel("P(plateau)", color="#FFD700")
        self.prob_curve = pg.PlotDataItem(pen=pg.mkPen("#FFD700", width=2), name="P(plateau)")
        self.prob_view.setYRange(0, 1.1, padding=0)
        self.prob_view.addItem(self.prob_curve)
        self.kla_plot_widget.getViewBox().sigResized.connect(
            lambda: self.prob_view.setGeometry(
                self.kla_plot_widget.getViewBox().sceneBoundingRect()
            )
        )

        # Plateau guide lines + label
        self.plateau_avg_line = pg.InfiniteLine(
            angle=0, movable=False, pen=pg.mkPen("g", width=3, style=Qt.PenStyle.DotLine)
        )
        self.plateau_avg_label = pg.LabelItem(text="kLa_avg: — h⁻¹")
        self.plateau_v_line1 = pg.InfiniteLine(
            angle=90, movable=False, pen=pg.mkPen("w", style=Qt.PenStyle.DashLine)
        )
        self.plateau_v_line2 = pg.InfiniteLine(
            angle=90, movable=False, pen=pg.mkPen("w", style=Qt.PenStyle.DashLine)
        )
        for item in [self.plateau_avg_line, self.plateau_v_line1, self.plateau_v_line2, self.plateau_avg_label]:
            self.kla_plot_widget.addItem(item, ignoreBounds=True)
            item.hide()

        splitter.addWidget(self.kla_plot_widget)
        main_layout.addWidget(splitter)

    # ──────────────────────────────────────────────────────────────────────
    # Utility helpers
    # ──────────────────────────────────────────────────────────────────────
    def _clear_graphs(self):
        """Clear all plots and in-memory buffers; restart time origin."""
        self.time_data.clear()
        self.do_raw_data.clear()
        self.do_smoothed_data.clear()
        self.dcdt_data.clear()  # Clear derivatives
        self.kla_inst_raw_data.clear()
        self.kla_inst_smoothed_data.clear()
        self.kla_capture_buffer = []
        self.time_capture_buffer = []
        self.do_raw_curve.clear()
        self.do_smoothed_curve.clear()
        self.kla_raw_curve.clear()
        self.kla_smoothed_curve.clear()
        self.prob_curve.clear()
        for item in [self.plateau_avg_line, self.plateau_v_line1, self.plateau_v_line2]:
            item.hide()
        self.initial_time = time.time()
        self.last_probs = None
        self.last_t_all = None
        self.last_pred_region = (None, None)
        self.last_opt_time = 0.0  # Reset optimization timer

    def _optimize_csat_routine(self):
        """
        Calculates best C* to minimize the NORMALIZED slope of kLa vs Time.
        Uses only data between the trigger thresholds (min_c and max_c).
        """
        # 1. Get thresholds from config
        try:
            min_c = float(self.gassing_out_config.get("auto_csat_min_c", 30.0))
            max_c = float(self.gassing_out_config.get("auto_csat_max_c", 70.0))
        except ValueError:
            return # Config invalid

        # 2. Convert buffers to numpy arrays
        if len(self.do_smoothed_data) < 10:
            return # Not enough data

        t_arr = np.array(self.time_data)
        do_arr = np.array(self.do_smoothed_data)
        dcdt_arr = np.array(self.dcdt_data)

        # 3. Create a strict mask: Only points BETWEEN the triggers
        # We also ensure dC/dt is positive to avoid noise at the very start/end
        mask = (do_arr >= min_c) & (do_arr <= max_c) & (dcdt_arr > 0.001)
        
        # Validation: We need enough points to calculate a slope
        if np.sum(mask) < 10:
            return 

        t_sub = t_arr[mask]
        do_sub = do_arr[mask]
        dcdt_sub = dcdt_arr[mask]

        # 4. Define the Cost Function
        # We define the lower bound slightly above the max observed DO in the range
        # to strictly avoid division by zero.
        max_observed_do = np.max(do_sub)
        
        def objective_function(c_sat_cand):
            # Safety: C* must be higher than any DO value in the subset
            if c_sat_cand <= max_observed_do + 0.1:
                return 1e9 # Huge penalty for invalid C*

            # Calculate kLa profile for this candidate C*
            # kLa = (dC/dt) / (C* - C)
            denom = c_sat_cand - do_sub
            kla_est = (dcdt_sub / denom) * 3600.0 # Convert to h-1

            # Calculate the linear slope of kLa vs Time
            # Polyfit returns [slope, intercept]
            if len(kla_est) < 2: return 1e9
            slope, intercept = np.polyfit(t_sub, kla_est, 1)

            # --- CRITICAL FIX ---
            # We minimize the RELATIVE slope (Slope / Average Value).
            # This prevents the optimizer from choosing huge C* just to make kLa small.
            avg_kla = np.mean(kla_est)
            if avg_kla < 1e-6: return 1e9 # Avoid division by zero
            
            normalized_slope = abs(slope) / abs(avg_kla)
            
            return normalized_slope

        # 5. Run Optimization
        # Bounds: Lower = Max Observed DO + 0.5% buffer. Upper = 150% (Reasonable physical limit)
        lower_bound = max_observed_do + 0.5
        upper_bound = 150.0 
        
        res = minimize_scalar(
            objective_function, 
            bounds=(lower_bound, upper_bound), 
            method='bounded',
            options={'xatol': 0.01} # Tolerance
        )

        if res.success:
            new_csat = res.x
            
            # Update Config
            self.gassing_out_config["c_sat"] = f"{new_csat:.2f}"
            
            # 6. Recalculate ALL data (for the graphs) using the new C*
            c_sat_val = float(new_csat)
            
            # Calculate denom for the WHOLE dataset, guarding against negatives
            denom_full = c_sat_val - do_arr
            # Guard: prevent division by zero or negative kLa if raw data spiked above C*
            denom_full[denom_full < 0.1] = 0.1 
            
            kla_inst_full = (dcdt_arr / denom_full) * 3600.0
            
            # Update Raw Data
            self.kla_inst_raw_data = deque(kla_inst_full, maxlen=1000)
            
            # Update Smoothed Data
            kla_avg_win = int(self.gassing_out_config.get("kla_moving_average_window", 15))
            smoothed = []
            temp_q = deque(maxlen=kla_avg_win)
            for val in kla_inst_full:
                temp_q.append(val)
                smoothed.append(np.mean(temp_q))
            
            self.kla_inst_smoothed_data = deque(smoothed, maxlen=1000)
            
            # Update AI Buffer (Important for TCN)
            self.time_capture_buffer = list(t_arr)
            self.kla_capture_buffer = list(smoothed)

            print(f"Auto-optimized C*: {new_csat:.2f} (Norm. Slope: {res.fun:.6f})")

    # ──────────────────────────────────────────────────────────────────────
    # Main update loop
    # ──────────────────────────────────────────────────────────────────────
    def update_live_data(self):
        if not (self.is_capturing or self.auto_routine_active):
            return
        self.comm_handler.read_and_parse_data()
        current_do_raw = self.comm_handler.oxyReadVal
        if current_do_raw < 0:
            return

        elapsed_time = time.time() - self.initial_time

        self.time_data.append(elapsed_time)
        self.do_raw_data.append(current_do_raw)

        # --- DO smoothing & robust derivative ---
        t_arr = np.array(self.time_data, dtype=float)
        do_arr = np.array(self.do_raw_data, dtype=float)

        tau_do = float(self.gassing_out_config.get("do_tau_s", 2.0))
        do_smooth_arr = self.ewma_irregular(t_arr, do_arr, tau_do)
        do_smoothed = float(do_smooth_arr[-1])
        self.do_smoothed_data.append(do_smoothed)

        # Robust local polynomial derivative
        span_s = float(self.gassing_out_config.get("deriv_span_s", 6.0))
        poly_ord = int(self.gassing_out_config.get("deriv_poly_order", 2))
        if t_arr.size >= max(10, poly_ord + 2):
            dCdt_arr = self.local_poly_derivative(
                t_arr,
                do_smooth_arr,
                span_s=span_s,
                poly_order=poly_ord,
                robust=True,
                iters=2,
            )
            dC_dt = float(dCdt_arr[-1])
        else:
            dC_dt = 0.0

        self.dcdt_data.append(dC_dt)  # Store derivative for auto-opt

        # --- Auto C* Optimization Logic ---
        # Only run if enabled, during Air Addition (logging active), and periodic trigger met
        is_auto = str(self.gassing_out_config.get("auto_csat_enabled", "False")) == "True"
        if is_auto and self.is_logging_active:
            period = float(self.gassing_out_config.get("auto_csat_period", 5.0))

            # Trigger conditions
            min_c = float(self.gassing_out_config.get("auto_csat_min_c", 30.0))
            max_c = float(self.gassing_out_config.get("auto_csat_max_c", 70.0))
            min_t = float(self.gassing_out_config.get("auto_csat_min_t", 5.0))

            # Capture time for trigger check
            run_time = time.time() - self.capture_start_time

            if (run_time > min_t and
                    do_smoothed > min_c and
                    do_smoothed < max_c and
                    (time.time() - self.last_opt_time) > period):
                self._optimize_csat_routine()
                self.last_opt_time = time.time()

        # kLa calculation (Standard step, will use potentially updated C* from above)
        kla_inst = 0.0
        c_sat = float(self.gassing_out_config.get("c_sat", 100.0))
        if abs(c_sat - do_smoothed) > self.EPS:
            kla_inst_s = dC_dt / (c_sat - do_smoothed)
            kla_inst = kla_inst_s * self.KLA_CONVERSION_FACTOR

        self.kla_inst_raw_data.append(kla_inst)
        kla_avg_win = int(self.gassing_out_config.get("kla_moving_average_window", 15))
        last_n_kla = list(self.kla_inst_raw_data)[-kla_avg_win:]
        kla_smoothed = float(np.mean(last_n_kla)) if last_n_kla else 0.0
        self.kla_inst_smoothed_data.append(kla_smoothed)

        # Update plots
        self.do_raw_curve.setData(list(self.time_data), list(self.do_raw_data))
        self.do_smoothed_curve.setData(list(self.time_data), list(self.do_smoothed_data))
        self.kla_raw_curve.setData(list(self.time_data), list(self.kla_inst_raw_data))
        self.kla_smoothed_curve.setData(
            list(self.time_data), list(self.kla_inst_smoothed_data)
        )

        if self.auto_routine_active:
            self.run_auto_routine_step(do_smoothed, dC_dt, kla_smoothed)
        elif self.is_capturing:
            self.run_manual_capture_step(do_smoothed, dC_dt, kla_smoothed)

    def run_manual_capture_step(self, do_smoothed, dC_dt, kla_smoothed):
        min_oxy_to_start = float(self.min_oxy_edit.text())
        stabilization_thr = float(
            self.gassing_out_config.get("stabilization_derivative_threshold", 0.05)
        )

        if self.waiting_for_stabilization:
            self.status_label.setText(
                f"Status: Waiting for DO < {min_oxy_to_start}% and stabilization…"
            )
            self.do_derivative_buffer.append(dC_dt)
            is_below = do_smoothed < min_oxy_to_start
            is_stable = all(abs(d) < stabilization_thr for d in self.do_derivative_buffer)
            if (
                is_below
                and len(self.do_derivative_buffer) == self.do_derivative_buffer.maxlen
                and is_stable
            ):
                self.waiting_for_stabilization, self.is_logging_active = False, True
                self.capture_start_time = time.time()
                print("Stabilization detected. Starting data logging.")

        if self.is_logging_active:
            capture_time = time.time() - self.capture_start_time
            self.status_label.setText("Status: Capturing & Analyzing…")
            if self.output_file:
                self.output_file.write(
                    f"{capture_time:.4f}\t{do_smoothed:.4f}\t{kla_smoothed:.6f}\n"
                )
            if self.model and self.tcn_enabled:
                self.run_tcn_inference(capture_time, kla_smoothed)

    def toggle_data_capture(self, checked):
        if self.auto_routine_active:
            QMessageBox.warning(
                self,
                "Routine Active",
                "Cannot start manual capture while automatic routine is running.",
            )
            self.capture_button.setChecked(False)
            return
        if checked:
            min_oxy_test = float(self.min_oxy_edit.text())
            if self.comm_handler.oxyReadVal > min_oxy_test:
                QMessageBox.warning(
                    self,
                    "High Oxygen Level",
                    (
                        f"Current DO is {self.comm_handler.oxyReadVal:.1f}%. "
                        f"Data capture will begin logging only after DO drops and stabilizes below {min_oxy_test}%."
                    ),
                )
            file_path, _ = QFileDialog.getSaveFileName(
                self,
                "Save Data Capture File",
                "",
                "Text Files (*.txt);;All Files (*)",
            )
            if not file_path:
                self.capture_button.setChecked(False)
                return
            try:
                self.output_file = open(file_path, "w")
                self.output_file.write(
                    "time (s)\tDO_smoothed (%)\tkLa_inst_smoothed (h-1)\n"
                )
            except IOError as e:
                QMessageBox.critical(self, "File Error", f"Could not open file: {e}")
                self.capture_button.setChecked(False)
                return
            self._reset_capture_state()
            self.is_capturing = True
            self.waiting_for_stabilization = True
            self.tcn_enabled = True  # manual capture: allow TCN guidance
            self.capture_button.setText("Stop Data Capture")
        else:
            self.is_capturing, self.is_logging_active = False, False
            if self.output_file:
                self.output_file.close()
                self.output_file = None
            self.capture_button.setText("Start Data Capture")
            self.status_label.setText("Status: Idle")
            for item in [self.plateau_avg_line, self.plateau_v_line1, self.plateau_v_line2]:
                item.hide()
            print("Data capture stopped.")

    def _reset_capture_state(self):
        self.kla_scaler, self.time_scaler = RunningMinMax(), RunningMinMax()
        self.kla_capture_buffer, self.time_capture_buffer = [], []
        stabilization_samples = int(self.gassing_out_config.get("stabilization_samples", 5))
        self.do_derivative_buffer = deque(maxlen=stabilization_samples)
        self.prob_curve.clear()
        for item in [self.plateau_avg_line, self.plateau_v_line1, self.plateau_v_line2]:
            item.hide()

    def run_tcn_inference(self, current_time, current_kla):
        # Guard: TCN is only a guide and can be enabled/disabled by stage
        if not (self.model and self.tcn_enabled):
            return None
        self.time_capture_buffer.append(current_time)
        self.kla_capture_buffer.append(current_kla)
        if len(self.time_capture_buffer) < 20:
            return None
        self.time_scaler.update(self.time_capture_buffer)
        self.kla_scaler.update(self.kla_capture_buffer)
        t_all = np.array(self.time_capture_buffer, dtype=float)
        kla_all = np.array(self.kla_capture_buffer, dtype=float)
        t_n = self.time_scaler.normalise(t_all)
        kla_n = self.kla_scaler.normalise(kla_all)
        x = np.stack([kla_n, t_n], axis=0)[None, ...].astype(np.float32)
        with torch.no_grad():
            logits = self.model(torch.from_numpy(x).to("cpu"))
            probs_raw = torch.sigmoid(logits[0, 0]).cpu().numpy()
            probs_i = gaussian_filter1d(probs_raw, sigma=2.0, mode="nearest")
        self.last_t_all = t_all
        self.last_probs = probs_i
        self.prob_curve.setData(t_all, probs_i)
        plateau_thr = float(self.gassing_out_config.get("plateau_thr", 0.77))
        t_start, t_end = detect_plateau_segment(t_all, probs_i, thr=plateau_thr)
        self.last_pred_region = (t_start, t_end)
        if t_start is not None:
            mask = (t_all >= t_start) & (t_all <= t_end)
            if np.any(mask):
                avg_kla = float(kla_all[mask].mean())
                self.plateau_v_line1.setPos(t_start)
                self.plateau_v_line2.setPos(t_end)
                self.plateau_avg_line.setPos(avg_kla)
                self.plateau_avg_label.setText(f"kLa_avg: {avg_kla:.2f} h⁻¹")
                self.plateau_v_line1.show()
                self.plateau_v_line2.show()
                self.plateau_avg_line.show()
                return avg_kla
        return None

    @staticmethod
    def ewma_irregular(t, x, tau_s):
        """Time-constant EWMA that handles irregular dt cleanly."""
        t = np.asarray(t, dtype=float)
        x = np.asarray(x, dtype=float)
        if x.size == 0:
            return x
        y = np.empty_like(x)
        y[0] = x[0]
        for i in range(1, x.size):
            dt = max(1e-9, t[i] - t[i - 1])
            alpha = 1.0 - np.exp(-dt / max(1e-6, tau_s))
            y[i] = y[i - 1] + alpha * (x[i] - y[i - 1])
        return y

    @staticmethod
    def local_poly_derivative(t, y, span_s=3.0, poly_order=2, robust=True, iters=2):
        """LOESS-style derivative on uneven samples."""
        def _tricube(u):
            w = np.clip(1 - np.abs(u) ** 3, 0.0, 1.0) ** 3
            return w

        t = np.asarray(t, dtype=float)
        y = np.asarray(y, dtype=float)
        n = t.size
        dy = np.full(n, np.nan)

        if n < poly_order + 2:
            return np.gradient(y, t)

        for i in range(n):
            t0 = t[i]
            mask = np.abs(t - t0) <= span_s
            if np.count_nonzero(mask) < (poly_order + 2):
                k = max(poly_order + 2, 7)
                lo = max(0, i - k // 2)
                hi = min(n, lo + k)
                mask = np.zeros(n, bool)
                mask[lo:hi] = True

            ti = t[mask]
            yi = y[mask]
            u = (ti - t0) / max(1e-9, span_s)
            w = _tricube(u)

            X = np.vstack([(ti - t0) ** p for p in range(poly_order + 1)]).T

            if robust:
                r_w = np.ones_like(w)
                for _ in range(iters):
                    WX = (w * r_w)[:, None] * X
                    Wy = (w * r_w) * yi
                    try:
                        beta, *_ = np.linalg.lstsq(WX, Wy, rcond=None)
                    except np.linalg.LinAlgError:
                        break
                    resid = yi - X @ beta
                    s = 1.4826 * np.median(np.abs(resid)) + 1e-12
                    c = 1.345 * s
                    r = resid / max(1e-12, c)
                    r_w = 1.0 / np.maximum(1.0, np.abs(r))
            else:
                WX = (w)[:, None] * X
                Wy = (w) * yi
                try:
                    beta, *_ = np.linalg.lstsq(WX, Wy, rcond=None)
                except np.linalg.LinAlgError:
                    continue

            dy[i] = beta[1] if poly_order >= 1 else np.nan

        if np.isnan(dy[0]) and n >= 2:
            dy[0] = (y[1] - y[0]) / max(1e-9, t[1] - t[0])
        if np.isnan(dy[-1]) and n >= 2:
            dy[-1] = (y[-1] - y[-2]) / max(1e-9, t[-1] - t[-2])

        if np.any(np.isnan(dy)):
            idx = np.arange(n)
            ok = ~np.isnan(dy)
            dy = np.interp(idx, idx[ok], dy[ok])
        return dy

    def open_options_dialog(self):
        dialog = GassingOutOptionsDialog(self.gassing_out_config, self)
        if dialog.exec() == QDialog.DialogCode.Accepted:
            self.gassing_out_config = dialog.get_values()
            self._apply_config()
            print("Gassing-out options updated.")
            if hasattr(self.window(), "on_editing_finished"):
                self.window().on_editing_finished()

    def _apply_config(self):
        try:
            main_prefs = self.window().preferences
            self.MIN_REOXY_CAPTURE_SEC = float(main_prefs.get("Configurations", {}).get("gassing_out_min_reoxy_s", "30.0"))
        except Exception:
            self.MIN_REOXY_CAPTURE_SEC = 30.0
        stabilization_samples = int(self.gassing_out_config.get("stabilization_samples", 5))
        if self.do_derivative_buffer.maxlen != stabilization_samples:
            self.do_derivative_buffer = deque(maxlen=stabilization_samples)
        self._load_model()

    def open_auto_kla_dialog(self):
        if self.is_capturing or self.auto_routine_active:
            QMessageBox.warning(
                self, "Busy", "Cannot start a new routine while another capture is active."
            )
            return
        points = self.kla_cascade_page.interactive_kla_widget.get_points()
        if not points:
            QMessageBox.warning(
                self,
                "No Points",
                "Please add experimental points on the 'kLa Cascade' page first.",
            )
            return
        self.auto_dialog = AutomaticKlaDialog(points, self)
        if self.auto_dialog.exec() == QDialog.DialogCode.Accepted:
            settings = self.auto_dialog.get_settings()
            if settings:
                self._start_auto_routine(settings, points)

    def open_points_table_dialog(self):
        """Opens the table that is being used for the automatic routine."""
        try:
            if self.auto_dialog is not None:
                self.auto_dialog.setModal(False)
                self.auto_dialog.show()
                self.auto_dialog.activateWindow()
                if hasattr(self.auto_dialog, "raise_"):
                    self.auto_dialog.raise_()
            else:
                points = self.kla_cascade_page.interactive_kla_widget.get_points()
                dlg = PointsTableDialog(points, self)
                dlg.exec()
        except Exception as e:
            QMessageBox.warning(self, "Open Table", f"Could not open the points table: {e}")

    def _start_auto_routine(self, settings, points):
        self.auto_settings = settings
        self.auto_points_to_test = points
        self.auto_current_run_index = 0
        self.auto_current_replicate = 1
        self.auto_replicate_results = []
        self.auto_routine_active = True
        self.auto_kla_button.setEnabled(False)
        self.stop_auto_kla_button.setEnabled(True)
        self.capture_button.setEnabled(False)
        print("Starting automatic kLa routine.")
        self._start_deoxygenation()

    def _stop_auto_routine(self):
        print("Stopping automatic kLa routine.")
        try:
            self.comm_handler.send_command(
                {
                    "flowmeterComm": 1,
                    "valve_1": 0,
                    "valve_2": 0,
                    "v_Flow": 1,
                    "flowSetpoint": 0,
                    "motorSetpoint": 0,
                }
            )
        except Exception:
            pass
        if self.output_file:
            self.output_file.close()
            self.output_file = None
        self.auto_routine_active = False
        self.auto_routine_state = "IDLE"
        self._pending_flow_command = None
        self._pending_flow_expected = None
        self._pending_flow_success = None
        self.status_label.setText("Status: Routine Stopped by User")
        self.auto_kla_button.setEnabled(True)
        self.stop_auto_kla_button.setEnabled(False)
        self.stop_and_calc_button.setEnabled(False)
        if self.auto_dialog and self.auto_current_run_index < self.auto_dialog.table.rowCount():
            self.auto_dialog.table.item(self.auto_current_run_index, 0).setText("Stopped")
        self.capture_button.setEnabled(True)

    def _stop_and_calculate_kla(self):
        """User-triggered stop during Air Addition to immediately calculate kLa."""
        if not (self.auto_routine_active and self.auto_routine_state == "AIR_ADDITION"):
            return
        try:
            self.comm_handler.send_command(
                {
                    "flowmeterComm": 1,
                    "valve_1": 0,
                    "valve_2": 0,
                    "v_Flow": 1,
                    "flowSetpoint": 0,
                }
            )
        except Exception:
            pass

        final_kla_from_tcn = None
        if self.model and self.tcn_enabled and self.time_capture_buffer:
            last_t = self.time_capture_buffer[-1]
            last_kla = self.kla_capture_buffer[-1] if self.kla_capture_buffer else 0.0
            final_kla_from_tcn = self.run_tcn_inference(last_t, last_kla)

        self._finalize_run(final_kla_from_tcn)
        self.stop_and_calc_button.setEnabled(False)
        self._advance_to_next_run()

    def run_auto_routine_step(self, do_smoothed, dC_dt, kla_smoothed):
        if self.auto_routine_state.startswith("WAIT_"):
            self._service_flow_state_guard()
            return

        stab_deriv_thr = float(
            self.gassing_out_config.get("stabilization_derivative_threshold", 0.05)
        )

        if self.auto_routine_state == "NITROGEN_ADDITION":
            target_do_to_start = float(self.min_oxy_edit.text())
            q_target, n_target, _ = self.auto_points_to_test[self.auto_current_run_index]
            self._set_stage_status(
                "Nitrogen", q_target, self.auto_settings.get("deoxygenation_rpm", n_target)
            )
            if do_smoothed < target_do_to_start:
                print("DO threshold reached. Stopping nitrogen and waiting for stabilization.")
                self._request_flow_state(
                    command={
                        "flowmeterComm": 1,
                        "valve_1": 0,
                        "valve_2": 0,
                        "v_Flow": 1,
                        "flowSetpoint": 0,
                    },
                    expected={
                        "valve_1": 0,
                        "valve_2": 0,
                        "v_Flow": 1,
                        "flowSetpoint": 0.0,
                    },
                    wait_state="WAIT_NITROGEN_CLOSE",
                    success="STABILIZING",
                    status="Closing nitrogen valve",
                )

        elif self.auto_routine_state == "STABILIZING":
            q_target, n_target, _ = self.auto_points_to_test[self.auto_current_run_index]
            self._set_stage_status(
                "Stabilizing", q_target, self.auto_settings.get("deoxygenation_rpm", n_target)
            )
            self.do_derivative_buffer.append(dC_dt)
            is_stable = all(abs(d) < stab_deriv_thr for d in self.do_derivative_buffer)
            if len(self.do_derivative_buffer) == self.do_derivative_buffer.maxlen and is_stable:
                print("System stabilized. Starting Air Addition (reoxygenation) run.")
                self._start_reoxygenation_run()

        elif self.auto_routine_state == "AIR_ADDITION":
            capture_time = time.time() - self.capture_start_time
            q_target, n_target, _ = self.auto_points_to_test[self.auto_current_run_index]
            self._set_stage_status("Air", q_target, n_target)
            if self.output_file:
                self.output_file.write(
                    f"{capture_time:.4f}\t{do_smoothed:.4f}\t{kla_smoothed:.6f}\n"
                )

            avg_kla = None
            if self.model and self.tcn_enabled:
                avg_kla = self.run_tcn_inference(capture_time, kla_smoothed)

            end_do = float(self.gassing_out_config.get("reoxygenation_end_percent", 80.0))
            if (do_smoothed > end_do) and (capture_time >= self.MIN_REOXY_CAPTURE_SEC):
                print("Reoxygenation complete (time ≥ 30 s condition met).")
                self._finalize_run(avg_kla)
                self._advance_to_next_run()

    def _request_flow_state(self, command, expected, wait_state, success, status):
        """Send immediately, then hold the scientific state machine for ack."""
        self._pending_flow_command = dict(command)
        self._pending_flow_expected = dict(expected)
        self._pending_flow_success = success
        self._pending_flow_retries = 0
        self._pending_flow_last_send = time.monotonic()
        self.auto_routine_state = wait_state
        self.status_label.setText(f"Status: {status} (awaiting confirmation)")
        self.comm_handler.send_command(self._pending_flow_command)

    def _flow_state_matches(self, expected):
        readings = self.comm_handler.readings
        if not readings.flowmeter_online or readings.flow_command_pending:
            return False
        if readings.flow_command_id and readings.flow_command_ack != readings.flow_command_id:
            return False

        if "valve_1" in expected and readings.flow_valve_1 != int(expected["valve_1"]):
            return False
        if "valve_2" in expected and readings.flow_valve_2 != int(expected["valve_2"]):
            return False
        if "v_Flow" in expected and readings.flow_valve_main != int(expected["v_Flow"]):
            return False
        if "flowSetpoint" in expected:
            if readings.flow_setpoint < 0:
                return False
            if abs(readings.flow_setpoint - float(expected["flowSetpoint"])) > 0.05:
                return False
        return True

    def _service_flow_state_guard(self):
        if not self._pending_flow_command or not self._pending_flow_expected:
            return

        if self._flow_state_matches(self._pending_flow_expected):
            success = self._pending_flow_success
            self._pending_flow_command = None
            self._pending_flow_expected = None
            self._pending_flow_success = None

            if success == "NITROGEN_ADDITION":
                self.auto_routine_state = "NITROGEN_ADDITION"
                q, _, _ = self.auto_points_to_test[self.auto_current_run_index]
                rpm = int(self.auto_settings["deoxygenation_rpm"])
                self._set_stage_status("Nitrogen", q, rpm)
            elif success == "STABILIZING":
                self.auto_routine_state = "STABILIZING"
                self.do_derivative_buffer.clear()
            elif success == "BEGIN_AIR_CAPTURE":
                self._begin_reoxygenation_capture()
            return

        now = time.monotonic()
        if now - self._pending_flow_last_send < self.FLOW_ACK_RETRY_SEC:
            return

        if self._pending_flow_retries >= self.FLOW_ACK_MAX_RETRIES:
            reason = "Flowmeter did not confirm the required safe valve state. Routine aborted."
            print(reason)
            self._stop_auto_routine()
            self.status_label.setText("Status: Aborted - valve confirmation failed")
            QMessageBox.critical(self, "Flowmeter Safety", reason)
            return

        self._pending_flow_retries += 1
        self._pending_flow_last_send = now
        print(f"Retrying flow command ({self._pending_flow_retries}/{self.FLOW_ACK_MAX_RETRIES}).")
        self.comm_handler.send_command(self._pending_flow_command)

    def _set_stage_status(self, stage_name: str, q_target: float, n_value: float):
        cur, tot = self._current_run_progress()
        try:
            text = (
                f"Status: {stage_name} - {cur}/{tot} - {float(n_value):.0f} RPM "
                f"{float(q_target):.2f} L/min"
            )
        except Exception:
            text = f"Status: {stage_name}"
        self.status_label.setText(text)

    def _current_run_progress(self):
        if not self.auto_points_to_test or not self.auto_settings:
            return 0, 0
        total = len(self.auto_points_to_test) * int(self.auto_settings.get("replicates", 1))
        current = (self.auto_current_run_index * int(self.auto_settings.get("replicates", 1))) + self.auto_current_replicate
        return current, total

    def _start_deoxygenation(self):
        self._clear_graphs()
        self.tcn_enabled = False
        q, n, _ = self.auto_points_to_test[self.auto_current_run_index]
        reps = int(self.auto_settings["replicates"])
        rpm = int(self.auto_settings["deoxygenation_rpm"])
        self._set_stage_status("Nitrogen", q, rpm)
        if self.auto_dialog:
            self.auto_dialog.table.item(self.auto_current_run_index, 0).setText(
                f"Running Rep {self.auto_current_replicate}/{reps}"
            )
        print(f"Starting Nitrogen Addition with {rpm} RPM.")
        self.stop_and_calc_button.setEnabled(False)
        self._request_flow_state(
            command={
                "flowmeterComm": 1,
                "valve_1": 1,
                "valve_2": 0,
                "v_Flow": 1,
                "flowSetpoint": 0,
                "motorSetpoint": rpm,
            },
            expected={
                "valve_1": 1,
                "valve_2": 0,
                "v_Flow": 1,
                "flowSetpoint": 0.0,
            },
            wait_state="WAIT_NITROGEN_OPEN",
            success="NITROGEN_ADDITION",
            status="Opening nitrogen valve",
        )

    def _start_reoxygenation_run(self):
        q_target, n_target, _ = self.auto_points_to_test[self.auto_current_run_index]
        print(f"Requesting Air Addition with Q={q_target}, N={n_target}.")
        self._request_flow_state(
            command={
                "flowmeterComm": 1,
                "valve_1": 0,
                "valve_2": 0,
                "v_Flow": 0,
                "flowSetpoint": q_target,
                "motorSetpoint": n_target,
            },
            expected={
                "valve_1": 0,
                "valve_2": 0,
                "v_Flow": 0,
                "flowSetpoint": q_target,
            },
            wait_state="WAIT_AIR_OPEN",
            success="BEGIN_AIR_CAPTURE",
            status="Opening air flow and confirming nitrogen closure",
        )

    def _begin_reoxygenation_capture(self):
        self._reset_capture_state()
        self._clear_graphs()
        self.auto_routine_state = "AIR_ADDITION"
        self.tcn_enabled = True
        self.capture_start_time = time.time()
        q_target, n_target, _ = self.auto_points_to_test[self.auto_current_run_index]
        filename = (
            f"Run_{self.auto_current_run_index+1}_Q{q_target}_N{n_target}_"
            f"Rep{self.auto_current_replicate}.txt"
        )
        filepath = os.path.join(self.auto_settings["save_folder"], filename)
        self._last_run_filepath = filepath
        try:
            self.output_file = open(filepath, "w")
            self.output_file.write("time (s)\tDO_smoothed (%)\tkLa_inst_smoothed (h-1)\n")
        except IOError as e:
            QMessageBox.critical(self, "File Error", f"Could not create run file: {e}")
            self._stop_auto_routine()
            return
        print(f"Starting Air Addition with Q={q_target}, N={n_target}.")
        self._set_stage_status("Air", q_target, n_target)
        self.stop_and_calc_button.setEnabled(True)

    def _finalize_run(self, final_kla_from_tcn):
        # Stop gas flow immediately; do not leave air flowing while the operator
        # confirms the selected kLa interval.
        self.auto_routine_state = "CONFIRMING"
        try:
            self.comm_handler.send_command(
                {
                    "flowmeterComm": 1,
                    "valve_1": 0,
                    "valve_2": 0,
                    "v_Flow": 1,
                    "flowSetpoint": 0,
                }
            )
        except Exception as exc:
            print(f"Warning: could not queue final flow stop: {exc}")

        if self.output_file:
            self.output_file.close()
            self.output_file = None

        t_all = np.array(self.time_data, dtype=float)
        kla_all = np.array(self.kla_inst_smoothed_data, dtype=float)
        do_all = np.array(self.do_smoothed_data, dtype=float)
        dcdt_all = np.array(self.dcdt_data, dtype=float)

        probs = (
            self.last_probs
            if (
                self.last_probs is not None
                and self.last_t_all is not None
                and len(self.last_t_all) == len(t_all)
            )
            else None
        )
        suggested = (
            self.last_pred_region if all(v is not None for v in self.last_pred_region) else (None, None)
        )

        while True:
            main_prefs = self.window().preferences.get("Configurations", {})
            dlg = ConfirmKlaDialog(
                t_all, kla_all,
                do_data=do_all, dcdt_data=dcdt_all,
                probs=probs, suggested_region=suggested,
                config_prefs=self.gassing_out_config,
                parent=self
            )
            if dlg.exec() == QDialog.DialogCode.Accepted:
                kla_avg, t_i, t_f = dlg.get_result()
                if kla_avg is None:
                    if final_kla_from_tcn is not None:
                        kla_avg = float(final_kla_from_tcn)
                        t_i, t_f = suggested
                    else:
                        kla_avg = float(np.mean(kla_all[-50:])) if kla_all.size else float("nan")

                self._save_run_result(self._last_run_filepath, kla_avg, t_i, t_f)
                self.auto_replicate_results.append(kla_avg)
                print(f"Run finished. Confirmed kLa: {kla_avg:.2f}")
                break
            else:
                QMessageBox.information(
                    self,
                    "Confirmation Required",
                    "Please confirm the kLa region to proceed to the next replicate.",
                )

    def _advance_to_next_run(self):
        self.stop_and_calc_button.setEnabled(False)
        if self.auto_current_replicate < int(self.auto_settings["replicates"]):
            self.auto_current_replicate += 1
            self._start_deoxygenation()
        else:
            valid_results = [
                r for r in self.auto_replicate_results if r is not None and not np.isnan(r)
            ]
            if valid_results:
                mean_kla = float(np.mean(valid_results))
                print(
                    f"Completed all replicates for point {self.auto_current_run_index}. "
                    f"Mean kLa: {mean_kla:.2f}"
                )
                self._update_main_app_data(self.auto_current_run_index, mean_kla)
                if self.auto_dialog:
                    self.auto_dialog.table.item(self.auto_current_run_index, 0).setText(
                        f"Done ({mean_kla:.2f})"
                    )
            else:
                print(
                    f"Completed all replicates for point {self.auto_current_run_index}, "
                    "but no valid kLa was found."
                )
                if self.auto_dialog:
                    self.auto_dialog.table.item(self.auto_current_run_index, 0).setText(
                        "Failed"
                    )
            self.auto_current_run_index += 1
            self.auto_current_replicate = 1
            self.auto_replicate_results = []
            if self.auto_current_run_index < len(self.auto_points_to_test):
                self._start_deoxygenation()
            else:
                print("All experimental runs are complete.")
                QMessageBox.information(
                    self, "Success", "Automatic kLa routine has finished successfully."
                )
                self._stop_auto_routine()

    def _update_main_app_data(self, point_index, new_kla):
        try:
            interactive_widget = self.kla_cascade_page.interactive_kla_widget
            interactive_widget.points[point_index][2] = new_kla
            interactive_widget._update_plot()
            interactive_widget.points_changed.emit()
            gradient_widget = self.kla_cascade_page.gradient_widget
            gradient_widget.run_gradient_ascent()
            print(
                f"Updated point {point_index} with kLa={new_kla} and refreshed gradient ascent."
            )
        except Exception as e:
            print(f"Error updating main application data: {e}")

    def _save_run_result(self, filepath, kla_avg, t_i, t_f):
        base, _ = os.path.splitext(filepath or "run")
        meta_path = base + "_result.txt"
        try:
            with open(meta_path, "w") as f:
                f.write(
                    "kLa_avg(h-1)\t{:.6f}\n".format(
                        kla_avg if kla_avg is not None else float("nan")
                    )
                )
                f.write(
                    "t_i(s)\t{:.4f}\n".format(t_i if t_i is not None else float("nan"))
                )
                f.write(
                    "t_f(s)\t{:.4f}\n".format(t_f if t_f is not None else float("nan"))
                )
            print(f"Saved run result to {meta_path}")
        except Exception as e:
            print(f"Failed to save run result: {e}")

    def closeEvent(self, event):
        try:
            if self.output_file:
                self.output_file.close()
            if self.auto_routine_active:
                self._stop_auto_routine()
        finally:
            event.accept()
