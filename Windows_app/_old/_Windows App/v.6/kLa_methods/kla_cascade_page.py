#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# kLa_cascade_page.py
import numpy as np
import copy
from PySide6.QtWidgets import (
    QWidget, QGroupBox, QVBoxLayout, QHBoxLayout,
    QPushButton, QFormLayout, QLineEdit, QDialog,
    QComboBox, QDialogButtonBox,
    QGridLayout, QStyle, QInputDialog, QLabel, QMessageBox 
)
from PySide6.QtCore import Signal, Qt
from matplotlib.backends.backend_qtagg import FigureCanvasQTAgg as FigureCanvas
from matplotlib.figure import Figure
import matplotlib.pyplot as plt
from config.preferences import default_preferences

# --- Interactive Experimental kLa Widget ---
class InteractiveKlaWidget(QWidget):
    """
    A widget for interactively defining experimental kLa points on a 2D plot.
    Users can add, delete, and drag points. The properties of a selected point
    (Q, N, kLa) can be edited in text fields.
    """
    points_changed = Signal()

    def __init__(self, parent=None):
        super().__init__(parent)
        self.points = []
        self.undo_stack = []
        self.redo_stack = []
        self.selected_point_index = None
        self.dragged_point_index = None
        self._is_first_show = True

        main_layout = QVBoxLayout(self)

        self.figure = Figure(figsize=(5, 5), dpi=100)
        self.canvas = FigureCanvas(self.figure)
        self.canvas.setStyleSheet("background:transparent;")
        self.ax = self.figure.add_subplot(111)
        main_layout.addWidget(self.canvas)

        self.figure.patch.set_alpha(0.0)     # Transparent figure background
        self.ax.set_facecolor('none')        # Transparent axes background
        self.text_color = "gray"
        
        controls_layout = QHBoxLayout()

        buttons_layout = QVBoxLayout()
        self.add_button = QPushButton("Add Point")
        self.delete_button = QPushButton("Delete Selected")
        self.undo_button = QPushButton("Undo")
        self.redo_button = QPushButton("Redo")
        buttons_layout.addWidget(self.add_button)
        buttons_layout.addWidget(self.delete_button)
        buttons_layout.addWidget(self.undo_button)
        buttons_layout.addWidget(self.redo_button)
        buttons_layout.addStretch()
        controls_layout.addLayout(buttons_layout)

        details_group = QGroupBox("Selected Point Details")
        details_layout = QFormLayout(details_group)
        self.q_edit = QLineEdit()
        self.n_edit = QLineEdit()
        self.kla_edit = QLineEdit()
        details_layout.addRow("Q (Flow):", self.q_edit)
        details_layout.addRow("N (RPM):", self.n_edit)
        details_layout.addRow("kLa:", self.kla_edit)
        controls_layout.addWidget(details_group)

        main_layout.addLayout(controls_layout)

        self.add_button.clicked.connect(self.add_point)
        self.delete_button.clicked.connect(self.delete_selected_point)
        self.undo_button.clicked.connect(self.undo)
        self.redo_button.clicked.connect(self.redo)

        self.q_edit.editingFinished.connect(self._update_selected_point_from_text)
        self.n_edit.editingFinished.connect(self._update_selected_point_from_text)
        self.kla_edit.editingFinished.connect(self._update_selected_point_from_text)
        
        self.canvas.mpl_connect('button_press_event', self._on_press)
        self.canvas.mpl_connect('motion_notify_event', self._on_motion)
        self.canvas.mpl_connect('button_release_event', self._on_release)

        self._update_button_states()

    def showEvent(self, event):
        super().showEvent(event)
        if self._is_first_show:
            self._update_plot()
            self._is_first_show = False

    def get_points(self):
        return self.points

    def set_points(self, points_data):
        self._save_state()
        self.points = [list(p) for p in points_data]
        self.selected_point_index = None
        if not self._is_first_show:
            self._update_plot()
        self.points_changed.emit()
        
    def _save_state(self):
        self.undo_stack.append(copy.deepcopy(self.points))
        self.redo_stack.clear()
        if len(self.undo_stack) > 30:
            self.undo_stack.pop(0)
        self._update_button_states()

    def add_point(self):
        self._save_state()
        xlim = self.ax.get_xlim()
        ylim = self.ax.get_ylim()
        new_q = np.mean(xlim)
        new_n = np.mean(ylim)
        new_kla = 50.0
        self.points.append([new_q, new_n, new_kla])
        self.selected_point_index = len(self.points) - 1
        self._update_plot()
        self._update_details_view()
        self.points_changed.emit()

    def delete_selected_point(self):
        if self.selected_point_index is not None:
            self._save_state()
            self.points.pop(self.selected_point_index)
            self.selected_point_index = None
            self._update_plot()
            self._update_details_view()
            self.points_changed.emit()

    def undo(self):
        if not self.undo_stack:
            return
        self.redo_stack.append(copy.deepcopy(self.points))
        self.points = self.undo_stack.pop()
        self.selected_point_index = None
        self._update_plot()
        self._update_button_states()
        self.points_changed.emit()

    def redo(self):
        if not self.redo_stack:
            return
        self.undo_stack.append(copy.deepcopy(self.points))
        self.points = self.redo_stack.pop()
        self.selected_point_index = None
        self._update_plot()
        self._update_button_states()
        self.points_changed.emit()
        
    def _update_button_states(self):
        self.undo_button.setEnabled(bool(self.undo_stack))
        self.redo_button.setEnabled(bool(self.redo_stack))
        self.delete_button.setEnabled(self.selected_point_index is not None)

    def update_theme(self, text_color):
        self.text_color = text_color
        # Redraw the plot to apply the new color
        self._update_plot()

    def _update_plot(self):
        self.ax.clear()
        
        if not self.points:
            self.ax.set_xlim(0, 20)
            self.ax.set_ylim(0, 1000)
        else:
            q_vals = [p[0] for p in self.points]
            n_vals = [p[1] for p in self.points]
            
            self.ax.scatter(q_vals, n_vals, color='blue', label='Experimental Points')
            
            if self.selected_point_index is not None:
                sel_q, sel_n, _ = self.points[self.selected_point_index]
                self.ax.scatter([sel_q], [sel_n], color='red', s=100, edgecolors='black', zorder=5)

            # --- Text positioning logic ---
            for q, n, kla in self.points:
                xlim = self.ax.get_xlim()
                ylim = self.ax.get_ylim()
                
                x_range = xlim[1] - xlim[0]
                y_range = ylim[1] - ylim[0]
                
                x_offset = x_range * 0.015
                y_offset = y_range * 0.015
                
                x_pos = q + x_offset
                y_pos = n + y_offset
                ha = 'left'
                va = 'bottom'

                # Adjust if text goes off right edge
                if x_pos > xlim[1] - x_range * 0.1:
                    ha = 'right'
                    x_pos = q - x_offset
                
                # Adjust if text goes off top edge
                if y_pos > ylim[1] - y_range * 0.05:
                    va = 'top'
                    y_pos = n - y_offset

                self.ax.text(x_pos, y_pos, f'{kla:.1f}', fontsize=9, ha=ha, va=va, color=self.text_color)

        self.ax.set_xlabel('Q (Flow, L/min)', fontsize=9, color=self.text_color)
        self.ax.set_ylabel('N (RPM)', fontsize=9, color=self.text_color)
        self.ax.tick_params(labelsize=8, colors=self.text_color)
        self.ax.grid(True, linestyle='--', alpha=0.6)
        self.figure.tight_layout()
        self.canvas.draw()
        
    def _update_details_view(self):
        if self.selected_point_index is not None:
            q, n, kla = self.points[self.selected_point_index]
            self.q_edit.setText(f"{q:.2f}")
            self.n_edit.setText(f"{n:.0f}")
            self.kla_edit.setText(f"{kla:.2f}")
            self.q_edit.setEnabled(True)
            self.n_edit.setEnabled(True)
            self.kla_edit.setEnabled(True)
        else:
            self.q_edit.clear()
            self.n_edit.clear()
            self.kla_edit.clear()
            self.q_edit.setEnabled(False)
            self.n_edit.setEnabled(False)
            self.kla_edit.setEnabled(False)
        self._update_button_states()

    def _update_selected_point_from_text(self):
        if self.selected_point_index is None:
            return
        try:
            new_q = float(self.q_edit.text())
            new_n = float(self.n_edit.text())
            new_kla = float(self.kla_edit.text())
            
            current_point = self.points[self.selected_point_index]
            if not np.allclose([new_q, new_n, new_kla], current_point):
                self._save_state()
                self.points[self.selected_point_index] = [new_q, new_n, new_kla]
                self._update_plot()
                self.points_changed.emit()
        except (ValueError, TypeError):
            self._update_details_view()

    def _get_point_at_event(self, event):
        if not self.points or event.xdata is None:
            return None
        
        pixel_tolerance = 10
        x_range = self.ax.get_xlim()[1] - self.ax.get_xlim()[0]
        y_range = self.ax.get_ylim()[1] - self.ax.get_ylim()[0]
        x_tol = (x_range / self.ax.get_window_extent().width) * pixel_tolerance
        y_tol = (y_range / self.ax.get_window_extent().height) * pixel_tolerance
        
        for i, (q, n, _) in enumerate(self.points):
            if abs(q - event.xdata) < x_tol and abs(n - event.ydata) < y_tol:
                return i
        return None

    def _on_press(self, event):
        if event.inaxes != self.ax:
            return
        
        point_idx = self._get_point_at_event(event)
        self.selected_point_index = point_idx
        if point_idx is not None:
            self.dragged_point_index = point_idx
            self._save_state()
        
        self._update_plot()
        self._update_details_view()

    def _on_motion(self, event):
        if self.dragged_point_index is None or event.xdata is None:
            return
        
        self.points[self.dragged_point_index][0] = event.xdata
        self.points[self.dragged_point_index][1] = event.ydata
        self._update_plot()
        self._update_details_view()

    def _on_release(self, event):
        if self.dragged_point_index is not None:
            self.dragged_point_index = None
            self.points_changed.emit()

# --- Pop-up Dialog for Graph Configuration ---
class GraphConfigDialog(QDialog):
    # In kLa_cascade_page.py -> class GraphConfigDialog

    def __init__(self, parent=None, config=None):
        super().__init__(parent)
        self.setWindowTitle("Graph Configuration")
        layout = QFormLayout(self)
        self.config = config or {}
        
        # --- NEW: Inputs for Start Point ---
        self.entry_start_q = QLineEdit(self.config.get("start_q", ""))
        self.entry_start_q.setPlaceholderText("Auto (Center)")
        self.entry_start_n = QLineEdit(self.config.get("start_n", ""))
        self.entry_start_n.setPlaceholderText("Auto (Center)")
        # -----------------------------------

        self.entry_grid_res = QLineEdit(self.config.get("grid_res", "16"))
        self.entry_rising_grid_res = QLineEdit(self.config.get("rising_grid_res", "600"))
        self.combo_interp_method = QComboBox()
        self.combo_interp_method.addItems(["linear", "cubic"])
        self.combo_interp_method.setCurrentText(self.config.get("interp_method", "cubic"))
        self.entry_step_multiplier = QLineEdit(self.config.get("step_multiplier", "120"))
        self.entry_min_step = QLineEdit(self.config.get("min_step", "16"))
        self.entry_max_iter = QLineEdit(self.config.get("max_iter", "1000"))
        self.entry_quiver_scale = QLineEdit(self.config.get("quiver_scale", "100"))
        self.entry_quiver_color = QLineEdit(self.config.get("quiver_color", "black"))
        self.entry_quiver_width = QLineEdit(self.config.get("quiver_width", "0.006"))
        self.combo_colormap = QComboBox()
        self.combo_colormap.addItems(["viridis", "plasma", "inferno", "magma", "cividis"])
        self.combo_colormap.setCurrentText(self.config.get("colormap", "viridis"))
        
        # Add rows to layout
        layout.addRow("Start Flow (Q):", self.entry_start_q)       # <--- NEW
        layout.addRow("Start Rotation (N):", self.entry_start_n)   # <--- NEW
        layout.addRow("Grid Resolution:", self.entry_grid_res)
        layout.addRow("Ascent Resolution:", self.entry_rising_grid_res)
        layout.addRow("Interpolation:", self.combo_interp_method)
        layout.addRow("Step Factor:", self.entry_step_multiplier)
        layout.addRow("Min Step:", self.entry_min_step)
        layout.addRow("Max Iterations:", self.entry_max_iter)
        layout.addRow("Arrow Scale:", self.entry_quiver_scale)
        layout.addRow("Arrow Color:", self.entry_quiver_color)
        layout.addRow("Arrow Width:", self.entry_quiver_width)
        layout.addRow("Colormap:", self.combo_colormap)
        
        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel,
            Qt.Orientation.Horizontal,
            self
        )
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addRow(buttons)

    def get_values(self):
        return {
            "start_q": self.entry_start_q.text(),           # <--- NEW
            "start_n": self.entry_start_n.text(),           # <--- NEW
            "grid_res": self.entry_grid_res.text(), 
            "rising_grid_res": self.entry_rising_grid_res.text(),
            "interp_method": self.combo_interp_method.currentText(), 
            "step_multiplier": self.entry_step_multiplier.text(),
            "min_step": self.entry_min_step.text(), 
            "max_iter": self.entry_max_iter.text(),
            "quiver_scale": self.entry_quiver_scale.text(), 
            "quiver_color": self.entry_quiver_color.text(),
            "quiver_width": self.entry_quiver_width.text(), 
            "colormap": self.combo_colormap.currentText(),
        }

# --- Pop-up Dialog for Cascade and PID Configuration ---
class CascadeOptionsDialog(QDialog):
    def __init__(self, parent=None, cascade_config=None, pid_config=None):
        super().__init__(parent)
        self.setWindowTitle("Cascade Options")
        
        # As configs agora são dicts numéricos
        self.cascade_config = cascade_config or {}
        self.pid_config = pid_config or {}
        self.cascade_widgets = {}
        self.pid_widgets = {}

        main_layout = QVBoxLayout(self)

        cascade_group = QGroupBox("Cascade Control Parameters")
        cascade_layout = QFormLayout(cascade_group)
        cascade_params = {
            "History Pts": ("history_pts", 20),
            "Median Filter Win": ("median_filter_win", 5),
            "Min Pts Regression": ("min_pts_regression", 5),
            "Prediction Horizon (s)": ("prediction_horizon_s", 30.0),
            "Outer Loop Gain": ("outer_loop_gain", 0.1),
            "Derivative Tau (s)": ("derivative_tau_s", 40.0),
            "Max Integral": ("max_integral", 500.0),
            "Min Integral": ("min_integral", -500.0),
            "Integral Window": ("integral_window", 20)
        }
        
        for label, (key, default_value) in cascade_params.items():
            value = self.cascade_config.get(key, default_value)
            line_edit = QLineEdit(str(value)) # Converte numérico para str para o QLineEdit
            cascade_layout.addRow(label + ":", line_edit)
            self.cascade_widgets[key] = line_edit
        main_layout.addWidget(cascade_group)

        pid_group = QGroupBox("PID Parameters")
        pid_layout = QFormLayout(pid_group)
        pid_params = {
            "C* (Saturation)": ("C_star", 100.0),
            "Kp": ("Kp", 0.75),
            "Ki": ("Ki", 0.1),
            "Kd": ("Kd", 0.5)
        }
        
        for label, (key, default_value) in pid_params.items():
            value = self.pid_config.get(key, default_value)
            line_edit = QLineEdit(str(value)) # Converte numérico para str para o QLineEdit
            pid_layout.addRow(label + ":", line_edit)
            self.pid_widgets[key] = line_edit
        main_layout.addWidget(pid_group)

        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel,
            Qt.Orientation.Horizontal,
            self
        )
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        main_layout.addWidget(buttons)

    def get_values(self):
        
        def _to_numeric_helper(val_str: str, default_val: float) -> float | int:
            """Converte string para float ou int, com fallback."""
            try:
                f_val = float(val_str.replace(",", "."))
                if f_val.is_integer():
                    return int(f_val)
                return f_val
            except (ValueError, TypeError):
                return default_val

        cascade_values = {}
        # Valores padrão para fallback
        defaults_adv = default_preferences()["ControlLoops"]["kLa_cascade"]["advanced"]
        for key, widget in self.cascade_widgets.items():
            cascade_values[key] = _to_numeric_helper(widget.text(), defaults_adv.get(key, 0.0))

        pid_values = {}
        defaults_pid = default_preferences()["ControlLoops"]["kLa_cascade"]["pid"]
        for key, widget in self.pid_widgets.items():
            pid_values[key] = _to_numeric_helper(widget.text(), defaults_pid.get(key, 0.0))

        return cascade_values, pid_values

# --- Gradient Ascent Widget ---
class GradientAscendWidget(QWidget):
    gradientAscentRun = Signal()
    
    def __init__(self, interactive_kla_widget, parent=None):
        super().__init__(parent)
        self.interactive_kla_widget = interactive_kla_widget
        
        self.graph_config = {}
        self.cascade_config = {}
        self.pid_config = {}
        self._is_first_run = True 

        # --- Main layout and widget structure ---
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        
        graph_and_buttons_layout = QGridLayout()
        
        self.figure = Figure(figsize=(4, 3), dpi=100)
        self.figure.patch.set_facecolor("none")
        self.canvas = FigureCanvas(self.figure)
        self.canvas.setStyleSheet("background:transparent;")
        
        self.graph_config_button = QPushButton(self)
        # Use a standard pixmap enum in Qt6
        self.graph_config_button.setIcon(self.style().standardIcon(QStyle.StandardPixmap.SP_DialogResetButton))
        self.graph_config_button.setFixedSize(24, 24)
        self.graph_config_button.setToolTip("Open Graph Settings")
    
        self.start_button = QPushButton("Run Gradient Ascent")
        self.cascade_options_button = QPushButton("Cascade Options")

        button_layout = QHBoxLayout()
        button_layout.addStretch()
        button_layout.addWidget(self.graph_config_button)
        button_layout.addWidget(self.cascade_options_button)
        button_layout.addWidget(self.start_button)
        button_layout.addStretch()

        graph_and_buttons_layout.addWidget(self.canvas, 0, 0, 1, 2)
        graph_and_buttons_layout.addLayout(button_layout, 1, 0, 1, 2)

        
        main_layout.addLayout(graph_and_buttons_layout)
        
        self.start_button.clicked.connect(self.run_gradient_ascent)
        self.cascade_options_button.clicked.connect(self._open_cascade_options_dialog)
        self.graph_config_button.clicked.connect(self._open_graph_config_dialog)
        
        # --- State variables for calculation results ---
        self.gradient_ascent_run = False; self.red_dot = None
        self.kLa_setpoint = None; self.max_kLa = None; self.min_kLa = None
        self.Q_path_rise = None; self.N_path_rise = None; self.kLa_path_rise = None
        self.Q_min, self.Q_max, self.N_min, self.N_max = 0, 1, 0, 1
        # --- Attributes to store calculation data for replotting ---
        self.points = None; self.Q_grid = None; self.N_grid = None; self.kLa_grid = None
        self.grad_Q = None; self.grad_N = None; self.Q_grid_1 = None; self.N_grid_1 = None
        self.max_index = None; self.min_index = None
    
    def get_cascade_config(self):
        return self.cascade_config

    def set_cascade_config(self, config_dict):
        self.cascade_config = config_dict

    def get_graph_config(self):
        return self.graph_config
    
    def set_graph_config(self, config_dict):
        self.graph_config = config_dict

    def get_pid_config(self):
        return self.pid_config

    def set_pid_config(self, config_dict):
        self.pid_config = config_dict
        # Update internal pid_config used by the controller
        self.pid_config = config_dict

    def showEvent(self, event):
        super().showEvent(event)
        if self._is_first_run:
            self.run_gradient_ascent()
            self._is_first_run = False

    def _open_graph_config_dialog(self):
        dialog = GraphConfigDialog(self, config=self.graph_config)
        if dialog.exec() == QDialog.DialogCode.Accepted:
            self.graph_config = dialog.get_values()
            print("Graph configuration updated.")
            # --- CORRECTED: Re-plot with new settings ---
            self._plot_gradient_field()
            if hasattr(self.window(), 'on_editing_finished'):
                self.window().on_editing_finished()

    def _open_cascade_options_dialog(self):
        dialog = CascadeOptionsDialog(self, cascade_config=self.cascade_config, pid_config=self.pid_config)
        if dialog.exec() == QDialog.DialogCode.Accepted:
            self.cascade_config, self.pid_config = dialog.get_values()
            print("Cascade and PID configurations updated.")
            if hasattr(self.window(), 'on_editing_finished'):
                self.window().on_editing_finished()

    def update_theme_gradient(self):
        self.text_color = "black" if self.window().configurations_page.theme_combo.currentText() == "Light" else "white"
        # Redraw the plot to apply the new color
        self._plot_gradient_field()

    # In kLa_cascade_page.py -> class GradientAscendWidget

    def _plot_gradient_field(self):
        if not self.gradient_ascent_run:
            return

        if self.figure.axes:
            ax = self.figure.axes[0]
            ax.clear()
        else:
            ax = self.figure.add_subplot(111)

        ax.set_facecolor("none")
        
        cfg = self.graph_config
        quiver_scale = float(cfg.get("quiver_scale", 100))
        quiver_color = cfg.get("quiver_color", "black").strip()
        quiver_width = float(cfg.get("quiver_width", 0.006))
        selected_cmap = cfg.get("colormap", "viridis")
        
        text_color = "black" if self.window().configurations_page.theme_combo.currentText() == "Light" else "white"
        self.interactive_kla_widget.update_theme(text_color)
        
        # 1. Contour & Quiver
        ax.contourf(self.Q_grid, self.N_grid, self.kLa_grid, levels=50, cmap=selected_cmap, alpha=1.0)
        ax.quiver(self.Q_grid, self.N_grid, self.grad_Q, self.grad_N,
                  color=quiver_color, scale=quiver_scale, width=quiver_width)
        
        # 2. Original Points (White)
        ax.scatter(self.points[:, 0], self.points[:, 1], color='white', edgecolor='black', s=50, linewidths=1.5, zorder=5, label='Original Points')
        
        colors = plt.cm.viridis(np.linspace(0, 1, 10))
        
        # 3. Path
        ax.plot(self.Q_path_rise, self.N_path_rise, color='darkred', linestyle='-', linewidth=2, label='Path')
        
        # 4. Start Point (RED DOT) - This is the new part
        if hasattr(self, 'start_coords_for_plot'):
            sq, sn = self.start_coords_for_plot
            ax.scatter([sq], [sn], color='red', edgecolor='black', s=80, zorder=6, label='Start Point')

        # 5. Min/Max markers
        ax.scatter(self.Q_grid_1[self.max_index], self.N_grid_1[self.max_index], color=colors[1], marker='x', s=50, linewidths=2, label='kLa Max')
        ax.scatter(self.Q_grid_1[self.min_index], self.N_grid_1[self.min_index], color=colors[9], marker='x', s=50, linewidths=2, label='kLa Min')
        
        ax.set_xlim(self.Q_min, self.Q_max)
        ax.set_ylim(self.N_min, self.N_max)
        ax.set_xlabel(r'$Q_{G}$ (L/min)', fontsize=14, color=text_color)
        ax.set_ylabel('N (rpm)', fontsize=14, color=text_color)
        ax.tick_params(labelsize=14, colors=text_color)
        for label in ax.get_xticklabels() + ax.get_yticklabels():
            label.set_fontname("Times New Roman")
        for spine in ax.spines.values():
            spine.set_edgecolor(text_color)
        
        self.figure.tight_layout()
        self.canvas.draw()

    # In kLa_cascade_page.py -> class GradientAscendWidget

    def _calculate_gradient_path(self):
        """
        Compute a path starting from a DEFINED POINT (or Center).
        - Ascends to Max, Descends to Min.
        - Merges into [Min ... Start ... Max].
        """
        from scipy.interpolate import griddata
        import numpy as np
        from scipy.ndimage import gaussian_filter

        experimental_points = self.interactive_kla_widget.get_points()
        if len(experimental_points) < 3:
            print("Error: At least 3 experimental points are required.")
            self.gradient_ascent_run = False
            return False

        try:
            points_data = np.array(experimental_points, dtype=float)
            self.points = points_data[:, :2]   
            values = points_data[:, 2]         

            self.Q_min, self.N_min = np.min(self.points, axis=0)
            self.Q_max, self.N_max = np.max(self.points, axis=0)

            cfg = self.graph_config
            grid_res = int(float(cfg.get("grid_res", 16)))
            rising_grid_res = int(float(cfg.get("rising_grid_res", 600)))
            interp_method = str(cfg.get("interp_method", "cubic"))
            step_multiplier = float(cfg.get("step_multiplier", 120))
            min_step = float(cfg.get("min_step", 16))
            max_iter = int(float(cfg.get("max_iter", 1000)))
            
            # --- NEW: Get User Start Point ---
            user_start_q = cfg.get("start_q", "").strip()
            user_start_n = cfg.get("start_n", "").strip()
            # ---------------------------------
            
        except Exception as e:
            print("Error reading graph parameters:", e)
            self.gradient_ascent_run = False
            return False

        # Coarse grid (Visualization)
        self.Q_vals = np.linspace(self.Q_min, self.Q_max, grid_res)
        self.N_vals = np.linspace(self.N_min, self.N_max, grid_res)
        self.Q_grid, self.N_grid = np.meshgrid(self.Q_vals, self.N_vals)
        self.kLa_grid = griddata(self.points, values, (self.Q_grid, self.N_grid), method=interp_method)
        self.kLa_grid = np.nan_to_num(self.kLa_grid, nan=np.nanmean(self.kLa_grid) if np.isfinite(self.kLa_grid).any() else 0.0)
        self.grad_Q = np.gradient(self.kLa_grid, axis=1)
        self.grad_N = np.gradient(self.kLa_grid, axis=0)

        # Fine grid (Calculation)
        fine_Q_axis = np.linspace(self.Q_min, self.Q_max, rising_grid_res)
        fine_N_axis = np.linspace(self.N_min, self.N_max, rising_grid_res)
        self.Q_grid_1, self.N_grid_1 = np.meshgrid(fine_Q_axis, fine_N_axis)
        
        kLa_grid_1 = griddata(self.points, values, (self.Q_grid_1, self.N_grid_1), method=interp_method)
        kLa_grid_1 = np.nan_to_num(kLa_grid_1, nan=np.nanmean(kLa_grid_1) if np.isfinite(kLa_grid_1).any() else 0.0)
        kLa_grid_1 = gaussian_filter(kLa_grid_1, sigma=5)

        kLa_gradient_Q_1 = np.gradient(kLa_grid_1, axis=1)
        kLa_gradient_N_1 = np.gradient(kLa_grid_1, axis=0)
        grid_shape = np.array(kLa_grid_1.shape)
        
        # ---- Determine Start Index ----
        try:
            # Try to parse user input
            sq_val = float(user_start_q)
            sn_val = float(user_start_n)
            
            # Clamp to bounds
            sq_val = np.clip(sq_val, self.Q_min, self.Q_max)
            sn_val = np.clip(sn_val, self.N_min, self.N_max)
            
            # Convert physical value to grid index
            # Index = (Value - Min) / (Max - Min) * (Steps - 1)
            c_start = (sq_val - self.Q_min) / (self.Q_max - self.Q_min) * (rising_grid_res - 1)
            r_start = (sn_val - self.N_min) / (self.N_max - self.N_min) * (rising_grid_res - 1)
            start_index = (int(r_start), int(c_start))
            
        except (ValueError, TypeError):
            # Fallback to Geometric Center if empty or invalid
            start_index = (grid_shape[0] // 2, grid_shape[1] // 2)

        # Store for plotting later
        self.start_coords_for_plot = (
            self.Q_grid_1[start_index], 
            self.N_grid_1[start_index]
        )

        # ---- Helper: Bi-directional Walk ----
        def walk_path(start_idx, direction_sign):
            path = [tuple(start_idx)]
            current = np.array(start_idx, dtype=float)
            visited = set(path)
            
            for _ in range(max_iter):
                r, c = int(current[0]), int(current[1])
                if not (0 <= r < grid_shape[0] and 0 <= c < grid_shape[1]): break

                grad = np.array([kLa_gradient_N_1[r, c], kLa_gradient_Q_1[r, c]], dtype=float)
                norm = np.linalg.norm(grad)
                if norm < 1e-9: break

                direction = (direction_sign * grad) / norm
                step = max(step_multiplier * norm, min_step)
                
                next_pos = current + step * direction
                next_idx = np.clip(np.round(next_pos), [0, 0], grid_shape - 1).astype(int)
                next_tuple = tuple(next_idx)

                if next_tuple == path[-1]: break
                if next_tuple in visited: break

                path.append(next_tuple)
                visited.add(next_tuple)
                current = next_idx.astype(float)
            return path

        path_ascent = walk_path(start_index, 1.0)   # +Grad (Uphill)
        path_descent = walk_path(start_index, -1.0) # -Grad (Downhill)

        full_path_idx = path_descent[::-1] + path_ascent[1:]
        path_idx = np.array(full_path_idx, dtype=int)

        if len(path_idx) > 0:
            self.Q_path_rise = self.Q_grid_1[path_idx[:, 0], path_idx[:, 1]]
            self.N_path_rise = self.N_grid_1[path_idx[:, 0], path_idx[:, 1]]
            self.kLa_path_rise = kLa_grid_1[path_idx[:, 0], path_idx[:, 1]]
            
            self.min_index = tuple(path_idx[0])
            self.max_index = tuple(path_idx[-1])
            self.max_kLa = float(self.kLa_path_rise[-1])
            self.min_kLa = float(self.kLa_path_rise[0])
            self.kLa_setpoint = (self.min_kLa + self.max_kLa) / 2.0
        else:
            self.max_kLa = float('nan')
            self.min_kLa = float('nan')
            self.kLa_setpoint = None

        self.gradient_ascent_run = True
        return True

    def run_gradient_ascent(self):
        if self._calculate_gradient_path():
            print("Gradient path calculated.")
            self._plot_gradient_field()
            self.gradientAscentRun.emit()

    def update_red_dot(self, Q_target, N_target):
        if not self.figure.axes: return
        ax = self.figure.axes[0]
        if self.red_dot is None:
            self.red_dot = ax.scatter([Q_target], [N_target], color="red", s=50, zorder=10)
        else:
            self.red_dot.set_offsets([[Q_target, N_target]])
        self.canvas.draw_idle()

# --- Main Page Container ---
class KlaCascadePage(QWidget):
    def __init__(self, parameter_settings_page, parent=None):
        super().__init__(parent)
        
        # Data storage for profiles
        self.profiles = {"Default": []} 
        self.current_profile_name = "Default"

        # Change Main Layout to Vertical to stack Top Bar above Content
        main_layout = QVBoxLayout(self)

        # 1. Top Bar: Profile Selector
        top_bar_layout = QHBoxLayout()
        
        top_bar_layout.addWidget(QLabel("Profile:"))
        
        self.profile_combo = QComboBox()
        self.profile_combo.setMinimumWidth(200)
        self.profile_combo.currentTextChanged.connect(self._on_profile_changed)
        top_bar_layout.addWidget(self.profile_combo)
        
        self.add_profile_btn = QPushButton("+")
        self.add_profile_btn.setFixedSize(30, 30)
        self.add_profile_btn.setToolTip("Create new empty profile")
        self.add_profile_btn.clicked.connect(self._add_new_profile)
        top_bar_layout.addWidget(self.add_profile_btn)
        
        # Add Delete Button (Optional but recommended)
        self.del_profile_btn = QPushButton("-")
        self.del_profile_btn.setFixedSize(30, 30)
        self.del_profile_btn.setToolTip("Delete current profile")
        self.del_profile_btn.clicked.connect(self._delete_current_profile)
        top_bar_layout.addWidget(self.del_profile_btn)

        top_bar_layout.addStretch() # Push everything to the left
        main_layout.addLayout(top_bar_layout)

        # 2. Content Area (Horizontal Split)
        content_layout = QHBoxLayout()

        self.exp_group = QGroupBox("Interactive kLa Experimental Values", self)
        self.exp_group_layout = QVBoxLayout(self.exp_group)
        self.interactive_kla_widget = InteractiveKlaWidget(self.exp_group)
        # Connect internal point changes to local profile update
        self.interactive_kla_widget.points_changed.connect(self._update_current_profile_data)
        self.exp_group_layout.addWidget(self.interactive_kla_widget)
        
        self.grad_group = QGroupBox("Gradient Ascent", self)
        self.grad_group_layout = QVBoxLayout(self.grad_group)
        self.gradient_widget = GradientAscendWidget(self.interactive_kla_widget, self.grad_group)
        self.grad_group_layout.addWidget(self.gradient_widget)

        content_layout.addWidget(self.exp_group, 1)
        content_layout.addWidget(self.grad_group, 2)
        
        main_layout.addLayout(content_layout)

    def set_profiles(self, profiles_dict, selected_name):
        """Called by main.py to load data from preferences."""
        self.profile_combo.blockSignals(True) # Prevent triggering change events during load
        self.profiles = profiles_dict
        self.profile_combo.clear()
        
        if not self.profiles:
            self.profiles = {"Default": []}
            
        self.profile_combo.addItems(list(self.profiles.keys()))
        
        if selected_name in self.profiles:
            self.profile_combo.setCurrentText(selected_name)
            self.current_profile_name = selected_name
        else:
            self.profile_combo.setCurrentIndex(0)
            self.current_profile_name = self.profile_combo.currentText()
            
        self.profile_combo.blockSignals(False)
        
        # Load the points into the widget
        self._load_points_to_widget()

    def get_profiles(self):
        """Called by main.py to save data."""
        # Ensure current widget state is saved to dict before returning
        self._update_current_profile_data()
        return self.profiles

    def get_selected_profile_name(self):
        return self.profile_combo.currentText()

    def _on_profile_changed(self, new_name):
        """Handle user changing selection in ComboBox."""
        if not new_name: return
        self.current_profile_name = new_name
        self._load_points_to_widget()
        # Trigger gradient ascent update since data changed
        self.gradient_widget._is_first_run = True 
        self.gradient_widget.update()
        
        # Trigger auto-save via main window if connected
        if hasattr(self.window(), 'on_editing_finished'):
             self.window().on_editing_finished()

    def _load_points_to_widget(self):
        """Pushes data from self.profiles dict to the interactive widget."""
        points = self.profiles.get(self.current_profile_name, [])
        # We assume points are stored as lists in JSON, InteractiveWidget expects lists
        self.interactive_kla_widget.set_points(points)

    def _update_current_profile_data(self):
        """Pulls data from widget and updates local dict."""
        # This is called whenever points are added/moved/deleted
        current_points = self.interactive_kla_widget.get_points()
        self.profiles[self.current_profile_name] = current_points
        
    def _add_new_profile(self):
        name, ok = QInputDialog.getText(self, "New Profile", "Enter profile name:")
        if ok and name:
            if name in self.profiles:
                QMessageBox.warning(self, "Error", "Profile name already exists.")
                return
            
            # Create new empty entry
            self.profiles[name] = []
            
            # Update UI
            self.profile_combo.addItem(name)
            self.profile_combo.setCurrentText(name) # This triggers _on_profile_changed
            
    def _delete_current_profile(self):
        if len(self.profiles) <= 1:
            QMessageBox.warning(self, "Error", "Cannot delete the last profile.")
            return
            
        name = self.current_profile_name
        confirm = QMessageBox.question(self, "Delete Profile", 
                                     f"Are you sure you want to delete '{name}'?",
                                     QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No)
        
        if confirm == QMessageBox.StandardButton.Yes:
            del self.profiles[name]
            self.profile_combo.removeItem(self.profile_combo.currentIndex())
            # ComboBox automatically selects another item, triggering change event
