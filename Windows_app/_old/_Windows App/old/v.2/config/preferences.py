#!/usr/bin/env python
# -*- coding: utf-8 -*-

import os
import json

# Define o caminho do arquivo de preferências (localizado na raiz do projeto)
PREFERENCES_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "preferences.json")

def load_preferences():
    """Carrega as preferências salvas do arquivo JSON, se existir."""
    if os.path.exists(PREFERENCES_FILE):
        try:
            with open(PREFERENCES_FILE, "r") as f:
                prefs = json.load(f)
            return prefs
        except Exception as e:
            print("Erro ao carregar preferências:", e)
    return None

def save_preferences(prefs):
    """Salva as preferências no arquivo JSON."""
    try:
        with open(PREFERENCES_FILE, "w") as f:
            json.dump(prefs, f, indent=4)
    except Exception as e:
        print("Erro ao salvar preferências:", e)

def default_preferences():
    """Retorna um dicionário com as preferências padrão."""
    return {
        "Configurations": {
            "ip_edit": "192.168.4.1",
            "data_delay_edit": "1000",
            "oxy_cal_a": "0.0305473419314",
            "oxy_cal_b": "-25.09136520919",
            "com_port_edit": "COM5",
            "baud_rate_edit": "115200",
            "data_bits_edit": "8",
            "stop_bits_edit": "1",
            "parity_edit": "None",
            "theme_combo": "Dark"
        },
        "ParameterSettings": {
            "Temperature": {"line_edit": "37"},
            "Motor": {"line_edit": "400"},
            "Pressure": {"line_edit": "100"},
            "Oxygen": {"line_edit": "75"},
            "Flowmeter": {"line_edit": "5", "Max Flow:": "50"},
            "Distance": {"line_edit": "100"},
            "pH": {"ph_setpoint": "7", "ph_error": "0.17", "ph_op_time": "10", "ph_disable_time": "10", "ph_speed": "99"},
            "Nutrient": {"nutri_op_time": "999", "nutri_disable_time": "1", "nutri_op_cycle": "500", "nutri_disable_cycle": "1", "nutri_speed": "99"},
            "Antifoam": {"antifoam_op_time": "5", "antifoam_disable_time": "20", "antifoam_speed": "99"}
        },
        "KlaInteractivePoints": [
            [5.0, 200.0, 26.35],
            [15.0, 200.0, 71.85],
            [5.0, 800.0, 100.58],
            [15.0, 800.0, 117.32],
            [10.0, 500.0, 83.12]
        ],
        "CascadeConfig": {
            "history_pts": "20",
            "median_filter_win": "5",
            "min_pts_regression": "5",
            "prediction_horizon_s": "30.0",
            "outer_loop_gain": "0.1",
            "derivative_tau_s": "40.0",
            "max_integral": "500.0",
            "min_integral": "-500.0",
            "integral_window": "20"
        },
        "GassingOutConfig": {
            "c_sat": "100.0",
            "stabilization_derivative_threshold": "0.05",
            "stabilization_samples": "5",
            "plateau_thr": "0.77",
            "model_path": "kLa_TNC_gassing_out.pth",
            "do_moving_average_window": "10",
            "kla_moving_average_window": "15",
            "derivative_window": "5"
        },
        "GradientAscend": {
            "entry_grid_res": "16",
            "entry_rising_grid_res": "600",
            "entry_step_multiplier": "120",
            "entry_min_step": "16",
            "entry_max_iter": "1000",
            "entry_quiver_scale": "100",
            "entry_quiver_color": "black",
            "entry_quiver_width": "0.006",
            "combo_interp_method": "cubic",
            "combo_colormap": "viridis"
        },
        "PIDConfig": {"C_star": "100.0", "Kp": "0.75", "Ki": "0.1", "Kd": "0.5"},
        "pHCalibration": {
            "slope": "1",
            "intercept": "0"
        }
    }
