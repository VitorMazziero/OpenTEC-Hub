#!/usr/bin/env python
# -*- coding: utf-8 -*-
import time
from typing import Tuple
import numpy as np
import pyqtgraph as pg
from PyQt5.QtWidgets import QWidget, QVBoxLayout
from collections import deque

# -----------------------------------------------------------------------------
# Signal processing utilities
# -----------------------------------------------------------------------------

def median_filter_centered(x: np.ndarray, window: int) -> np.ndarray:
    """Edge-replicated, centred median filter."""
    # Ensure window is odd
    if window % 2 == 0:
        window += 1
    half = window // 2
    x_padded = np.pad(x, (half, half), mode="edge")
    return np.array([np.median(x_padded[i : i + window]) for i in range(len(x))])

# -----------------------------------------------------------------------------
# Stage 1 – Pre-condition validation
# -----------------------------------------------------------------------------

def _validate_inputs(main_window) -> Tuple[bool, float]:
    """Return *(enabled, oxygen_value)*. *enabled* is *False* if the cascade
    must short-circuit early. All side-effects present in the original
    function (e.g. updating *main_window.current_OUR*) are preserved here.
    """
    if not main_window.parameter_settings_page.oxy_kla_cascade_checkbox.isChecked():
        main_window.current_OUR = -1
        return False, 0.0

    oxy_value = main_window.comm_handler.oxyReadVal
    try:
        oxy_value = float(oxy_value)
        if oxy_value < 0 or oxy_value > 4096:
            return False, 0.0
    except (ValueError, TypeError):
        return False, 0.0

    if main_window.parameter_settings_page.oxy_block.checkbox.isChecked():
        main_window.current_oxygen = oxy_value

    gw = main_window.kla_cascade_page.gradient_widget
    if not hasattr(gw, "kLa_setpoint") or gw.kLa_setpoint is None:
        return False, 0.0

    return True, oxy_value

# -----------------------------------------------------------------------------
# Stage 2 – Data buffering / prediction
# -----------------------------------------------------------------------------

def _update_oxygen_history(main_window, C: float, t: float, cascade_cfg: dict) -> None:
    if not hasattr(main_window, "oxy_hist"):
        main_window.oxy_hist = []
    main_window.oxy_hist.append((t, C))
    
    hist_max_points = int(cascade_cfg.get('history_pts', 20))
    if len(main_window.oxy_hist) > hist_max_points:
        main_window.oxy_hist.pop(0)


def _predict_trend(main_window, cascade_cfg: dict) -> Tuple[float, float]:
    """Return *(C_pred, dC_dt_pred)* based on history or fall back to last point."""
    min_pts_regress = int(cascade_cfg.get('min_pts_regression', 5))
    if len(main_window.oxy_hist) < min_pts_regress:
        C_last = main_window.oxy_hist[-1][1] if main_window.oxy_hist else 0.0
        return C_last, 0.0

    times, values = zip(*main_window.oxy_hist)
    ts = np.asarray(times)
    Cs = np.asarray(values)

    median_win = int(cascade_cfg.get('median_filter_win', 5))
    Cs_smooth = median_filter_centered(Cs, median_win)
    ts_shifted = ts - ts[0]

    slope, intercept = np.polyfit(ts_shifted, Cs_smooth, 1)
    
    pred_horizon = float(cascade_cfg.get('prediction_horizon_s', 30.0))
    C_pred = np.clip(Cs_smooth[-1] + slope * pred_horizon, 0.0, 150.0)
    return float(C_pred), float(slope)

# -----------------------------------------------------------------------------
# Stage 3 – Outer loop (concentration) control
# -----------------------------------------------------------------------------

def _desired_rate(C_pred: float, setpoint: float, cascade_cfg: dict) -> float:
    k_outer = float(cascade_cfg.get('outer_loop_gain', 0.1))
    return k_outer * (setpoint - C_pred)

# -----------------------------------------------------------------------------
# Stage 4 – Inner PID loop
# -----------------------------------------------------------------------------

def _update_pid(main_window, gw, pid_cfg: dict, inner_error: float, dt: float, cascade_cfg: dict) -> float:
    """Return PID output (clamped relative to *gw.kLa_setpoint*)."""
    integral_win = int(cascade_cfg.get('integral_window', 20))
    if not hasattr(main_window, "pid_integral_buffer") or main_window.pid_integral_buffer.maxlen != integral_win:
        main_window.pid_integral_buffer = deque(maxlen=integral_win)

    main_window.pid_integral_buffer.append(inner_error * dt)

    min_integral = float(cascade_cfg.get('min_integral', -500.0))
    max_integral = float(cascade_cfg.get('max_integral', 500.0))
    raw_integral = sum(main_window.pid_integral_buffer)
    bounded_integral = np.clip(raw_integral, min_integral, max_integral)

    if not hasattr(main_window, "last_inner_error"):
        main_window.last_inner_error = inner_error
    if not hasattr(main_window, "filtered_derivative"):
        main_window.filtered_derivative = 0.0

    deriv_tau = float(cascade_cfg.get('derivative_tau_s', 40.0))
    alpha = dt / (deriv_tau + dt) if dt > 0 else 0.0
    raw_derivative = (inner_error - main_window.last_inner_error) / dt if dt > 0 else 0.0
    main_window.filtered_derivative = (
        alpha * raw_derivative + (1.0 - alpha) * main_window.filtered_derivative
    )
    main_window.last_inner_error = inner_error

    P = pid_cfg["Kp"] * inner_error
    I = pid_cfg["Ki"] * bounded_integral
    D = pid_cfg["Kd"] * main_window.filtered_derivative

    output = P + I + D

    min_out = gw.min_kLa - gw.kLa_setpoint
    max_out = gw.max_kLa - gw.kLa_setpoint
    clipped_output = float(np.clip(output, min_out, max_out))
    
    if clipped_output != output and hasattr(main_window, "pid_integral"):
        main_window.pid_integral -= inner_error * dt
        
    return clipped_output

# -----------------------------------------------------------------------------
# Stage 5 – Equipment setpoint interpolation
# -----------------------------------------------------------------------------

def _interpolate_Q_N(gw, kLa_sp: float) -> Tuple[float, float, float]:
    """Interpolate *Q_target*, *N_target*, and return *kLa_target* as well."""
    if not hasattr(gw, "kLa_path_rise") or gw.kLa_path_rise is None:
        raise RuntimeError("kLa path not initialised")

    kLa_rise, Q_rise, N_rise = gw.kLa_path_rise, gw.Q_path_rise, gw.N_path_rise

    idx = None
    for i in range(len(kLa_rise) - 1):
        sp = kLa_sp
        k_lo, k_hi = kLa_rise[i], kLa_rise[i + 1]
        if (k_lo <= sp <= k_hi) or (k_lo >= sp >= k_hi):
            idx = i
            break
    if idx is None:
        idx = 0 if kLa_sp < kLa_rise[0] else len(kLa_rise) - 2
        
    k0, k1 = kLa_rise[idx], kLa_rise[idx + 1]
    Q0, Q1 = Q_rise[idx], Q_rise[idx + 1]
    N0, N1 = N_rise[idx], N_rise[idx + 1]
    
    frac = 0.0 if k1 == k0 else (kLa_sp - k0) / (k1 - k0)
    
    Q_target = Q0 + frac * (Q1 - Q0)
    N_target = N0 + frac * (N1 - N0)
    kLa_target = k0 + frac * (k1 - k0)
    return Q_target, N_target, kLa_target

# -----------------------------------------------------------------------------
# Stage 6 – Hardware communication
# -----------------------------------------------------------------------------

def _clamp_equipment_targets(Q: float, N: float, gw) -> Tuple[float, float]:
    flow_min = gw.Q_min
    flow_max = gw.Q_max
    rpm_min = gw.N_min
    rpm_max = gw.N_max

    return (
        float(np.clip(Q, flow_min, flow_max)),
        float(np.clip(N, rpm_min, rpm_max)),
    )

def _send_hardware(main_window, Q_target: float, N_target: float, oxygen_setpoint: float) -> None:
    now = time.time()
    if now - main_window.last_flow_command_time >= 10.0:
        main_window.comm_handler.send_command(
            {
                "flowSetpoint": Q_target,
                "flowmeterComm": 1,
                "oxygenMonitor": oxygen_setpoint,
                "motorSetpoint": N_target,
            }
        )
        main_window.last_flow_command_time = now
    else:
        main_window.comm_handler.send_command(
            {"oxygenMonitor": oxygen_setpoint, "motorSetpoint": N_target}
        )

# -----------------------------------------------------------------------------
# Stage 7 – OUR computation
# -----------------------------------------------------------------------------
def compute_OUR_mmol_per_L_per_h(C_pct, dCdt_pct_per_s, kLa_per_h, Cstar_pct, Cstar_mg_per_L):
    # Convert kLa to s^-1
    kLa_per_s = kLa_per_h / 3600.0
    # %DO → mg/L scale
    scale = Cstar_mg_per_L / 100.0  # mg/L per 1% DO

    # mg/L/s
    our_mg_per_L_per_s = ( (Cstar_pct - C_pct) * kLa_per_s - dCdt_pct_per_s ) * scale

    # mg/L/s → mmol/L/h (MW_O2 = 32 mg/mmol)
    our_mmol_per_L_per_h = our_mg_per_L_per_s * 3600.0 / 32.0
    return our_mmol_per_L_per_h

def _compute_OUR(C: float, dC_dt_pred: float, kLa_target: float, C_star: float) -> float:
    Cstar_mg_per_L = 6.6 # Default value for Cstar in mg/L (37°C, 1 atm)
    return compute_OUR_mmol_per_L_per_h(C, dC_dt_pred, kLa_target, C_star, Cstar_mg_per_L)

# -----------------------------------------------------------------------------
# Stage 8 – Graph initialisation and update
# -----------------------------------------------------------------------------

def _ensure_our_graph(main_window):
    if "OUR" in main_window.graphs_page.monitored_params:
        return

    main_window.graphs_page.monitored_params["OUR"] = {
        "active": lambda: True,
        "setpoint": lambda: None,
        "unit": "",
        "pen": "w",
        "data_key": "OUR",
    }
    main_window.graphs_page.data_buffers["OUR"] = ([], [])

    for idx, tab_data in enumerate(main_window.graphs_page.tab_widgets_data):
        if "OUR" not in tab_data:
            widget = QWidget()
            vlayout = QVBoxLayout()
            plot = pg.PlotWidget(title="OUR vs Time")
            plot.showGrid(x=True, y=True)
            vlayout.addWidget(plot)
            widget.setLayout(vlayout)
            tab_data["OUR"] = {"widget": widget, "plot": plot, "line": None}
            main_window.graphs_page.tab_widgets[idx].addTab(widget, "OUR")


def _update_our_plot(main_window, t: float, OUR: float):
    buf_t, buf_y = main_window.graphs_page.data_buffers["OUR"]
    buf_t.append(t)
    buf_y.append(OUR)
    for tab_data in main_window.graphs_page.tab_widgets_data:
        if "OUR" in tab_data:
            plot = tab_data["OUR"]["plot"]
            plot.clear()
            plot.plot(buf_t, buf_y, pen="w")

# -----------------------------------------------------------------------------
# Stage 9 – GUI feedback widgets
# -----------------------------------------------------------------------------

def _update_gui_feedback(main_window, gw, Q_target: float, N_target: float):
    gw.update_red_dot(Q_target, N_target)
    main_window.parameter_settings_page.motor_block.line_edit.setText(str(int(N_target)))
    main_window.parameter_settings_page.flow_block.line_edit.setText(f"{Q_target:.2f}")

# -----------------------------------------------------------------------------
# Main orchestration function (called externally)
# -----------------------------------------------------------------------------

def run_kla_cascade_control(main_window):
    """kLa cascade controller (refactored). Public API unchanged."""
    ok, _ = _validate_inputs(main_window)
    if not ok:
        return

    gw = main_window.kla_cascade_page.gradient_widget
    pid_cfg = gw.pid_config or {"C_star": 100, "Kp": 4, "Ki": 0.2, "Kd": 0.01}
    
    # Fetch cascade configuration from the UI
    cascade_cfg = gw.get_cascade_config()

    if main_window.cascade_start_time is None:
        main_window.cascade_start_time = time.time()

    oxygen_setpoint = main_window.parameter_settings_page.get_oxygen_setpoint()
    main_window.comm_handler.send_command({"oxygenMonitor": oxygen_setpoint})

    C = float(main_window.current_oxygen)
    if C <= 0 or C > 150:
        return

    now = time.time()
    _update_oxygen_history(main_window, C, now, cascade_cfg)
    C_pred, dC_dt_pred = _predict_trend(main_window, cascade_cfg)

    desired_dC_dt = _desired_rate(C_pred, oxygen_setpoint, cascade_cfg)

    dt = max(main_window.dataDelay / 1000.0, 1e-6)

    if not hasattr(main_window, "inner_error_buffer"):
        main_window.inner_error_buffer = []
    raw_inner_error = desired_dC_dt - dC_dt_pred
    main_window.inner_error_buffer.append(raw_inner_error)
    if len(main_window.inner_error_buffer) > 5:
        main_window.inner_error_buffer.pop(0)
    inner_error = float(np.mean(main_window.inner_error_buffer))

    pid_output = _update_pid(main_window, gw, pid_cfg, inner_error, dt, cascade_cfg)

    new_kLa_sp = np.clip(gw.kLa_setpoint + pid_output, gw.min_kLa, gw.max_kLa)
    gw.kLa_setpoint = new_kLa_sp

    Q_target, N_target, kLa_target = _interpolate_Q_N(gw, new_kLa_sp)
    Q_target, N_target = _clamp_equipment_targets(Q_target, N_target, gw)

    _send_hardware(main_window, Q_target, N_target, oxygen_setpoint)

    OUR = _compute_OUR(C, dC_dt_pred, kLa_target, pid_cfg["C_star"])
    main_window.current_OUR = OUR

    rel_t = now - main_window.cascade_start_time
    _ensure_our_graph(main_window)
    _update_our_plot(main_window, rel_t, OUR)

    _update_gui_feedback(main_window, gw, Q_target, N_target)
