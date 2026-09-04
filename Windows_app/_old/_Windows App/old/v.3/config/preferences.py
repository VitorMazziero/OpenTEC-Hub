#!/usr/bin/env python
# -*- coding: utf-8 -*-
# preferences.py

import os
import json
from typing import Dict, Any

# Define o caminho do arquivo de preferências (localizado na raiz do projeto)
PREFERENCES_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "preferences.json")

def _to_numeric(val_str: Any, default_val: float) -> float | int:
    """Converte um valor (provavelmente string) para float ou int."""
    if isinstance(val_str, (int, float)):
        return val_str
    try:
        f_val = float(str(val_str).replace(",", "."))
        if f_val.is_integer():
            return int(f_val)
        return f_val
    except (ValueError, TypeError):
        return default_val

def _migrate_preferences(prefs: Dict[str, Any]) -> Dict[str, Any]:
    """
    Verifica se as preferências estão no formato antigo e as migra
    para a nova estrutura centralizada 'ControlLoops'.
    """
    # Se 'ControlLoops' já existe, asumimos que a migração foi feita.
    if "ControlLoops" in prefs:
        return prefs
        
    print("MIGRATION: Detectadas preferências de formato antigo. Migrando para 'ControlLoops'...")
    
    # 1. Crie o novo contêiner
    control_loops = {
        "kLa_cascade": {"pid": {}, "advanced": {}},
        "agitation_cascade": {"pid": {}, "advanced": {}},
        "aeration_cascade": {"pid": {}, "advanced": {}}
    }
    
    # 2. Migrar kLa PID (antigo 'PIDConfig')
    if "PIDConfig" in prefs:
        old_pid = prefs.get("PIDConfig", {})
        control_loops["kLa_cascade"]["pid"] = {
            "C_star": _to_numeric(old_pid.get("C_star", 100.0), 100.0),
            "Kp": _to_numeric(old_pid.get("Kp", 0.75), 0.75),
            "Ki": _to_numeric(old_pid.get("Ki", 0.1), 0.1),
            "Kd": _to_numeric(old_pid.get("Kd", 0.5), 0.5)
        }
        del prefs["PIDConfig"]

    # 3. Migrar kLa Avançado (antigo 'CascadeConfig')
    if "CascadeConfig" in prefs:
        old_adv = prefs.get("CascadeConfig", {})
        control_loops["kLa_cascade"]["advanced"] = {
            "history_pts": int(_to_numeric(old_adv.get("history_pts", 20), 20)),
            "median_filter_win": int(_to_numeric(old_adv.get("median_filter_win", 5), 5)),
            "min_pts_regression": int(_to_numeric(old_adv.get("min_pts_regression", 5), 5)),
            "prediction_horizon_s": _to_numeric(old_adv.get("prediction_horizon_s", 30.0), 30.0),
            "outer_loop_gain": _to_numeric(old_adv.get("outer_loop_gain", 0.1), 0.1),
            "derivative_tau_s": _to_numeric(old_adv.get("derivative_tau_s", 40.0), 40.0),
            "max_integral": _to_numeric(old_adv.get("max_integral", 500.0), 500.0),
            "min_integral": _to_numeric(old_adv.get("min_integral", -500.0), -500.0),
            "integral_window": int(_to_numeric(old_adv.get("integral_window", 20), 20))
        }
        del prefs["CascadeConfig"]

    # 4. Migrar Agitação (antigo 'pid_agitation')
    if "pid_agitation" in prefs:
        old_agit = prefs.get("pid_agitation", {})
        control_loops["agitation_cascade"]["pid"] = {
            "Kp": _to_numeric(old_agit.get("Kp", 0.1), 0.1),
            "Ki": _to_numeric(old_agit.get("Ki", 0.002), 0.002),
            "Kd": _to_numeric(old_agit.get("Kd", 0.75), 0.75)
        }
        control_loops["agitation_cascade"]["advanced"] = {
            "history_pts": int(_to_numeric(old_agit.get("History Pts", 30), 30)),
            "median_filter_win": int(_to_numeric(old_agit.get("Median Filter Win", 5), 5)),
            "min_pts_regression": int(_to_numeric(old_agit.get("Min Pts Regression", 10), 10)),
            "prediction_horizon_s": _to_numeric(old_agit.get("Prediction Horizon (s)", 30.0), 30.0),
            "outer_loop_gain": _to_numeric(old_agit.get("Outer Loop Gain", 0.1), 0.1),
            "derivative_tau_s": _to_numeric(old_agit.get("Derivative Tau (s)", 40.0), 40.0),
            "max_integral": _to_numeric(old_agit.get("Max Integral", 200.0), 200.0),
            "min_integral": _to_numeric(old_agit.get("Min Integral", -200.0), -200.0),
            "integral_window": int(_to_numeric(old_agit.get("Integral Window", 60), 60))
        }
        del prefs["pid_agitation"]

    # 5. Migrar Aeração (antigo 'pid_aeration')
    if "pid_aeration" in prefs:
        old_aer = prefs.get("pid_aeration", {})
        control_loops["aeration_cascade"]["pid"] = {
            "Kp": _to_numeric(old_aer.get("Kp", 0.001), 0.001),
            "Ki": _to_numeric(old_aer.get("Ki", 0.0002), 0.0002),
            "Kd": _to_numeric(old_aer.get("Kd", 0.0075), 0.0075)
        }
        control_loops["aeration_cascade"]["advanced"] = {
            "history_pts": int(_to_numeric(old_aer.get("History Pts", 30), 30)),
            "median_filter_win": int(_to_numeric(old_aer.get("Median Filter Win", 5), 5)),
            "min_pts_regression": int(_to_numeric(old_aer.get("Min Pts Regression", 10), 10)),
            "prediction_horizon_s": _to_numeric(old_aer.get("Prediction Horizon (s)", 30.0), 30.0),
            "outer_loop_gain": _to_numeric(old_aer.get("Outer Loop Gain", 0.1), 0.1),
            "derivative_tau_s": _to_numeric(old_aer.get("Derivative Tau (s)", 40.0), 40.0),
            "max_integral": _to_numeric(old_aer.get("Max Integral", 200.0), 200.0),
            "min_integral": _to_numeric(old_aer.get("Min Integral", -200.0), -200.0),
            "integral_window": int(_to_numeric(old_aer.get("Integral Window", 60), 60))
        }
        del prefs["pid_aeration"]
        
    # 6. Adiciona a nova estrutura ao objeto de preferências
    prefs["ControlLoops"] = control_loops
    
    # 7. (Opcional) Migra chaves de string para snake_case em outros configs
    if "GassingOutConfig" in prefs:
        old_gassing = prefs.get("GassingOutConfig", {})
        prefs["GassingOutConfig"] = {
            "c_sat": old_gassing.get("c_sat", "100.0"),
            "stabilization_derivative_threshold": old_gassing.get("stabilization_derivative_threshold", "0.05"),
            "stabilization_samples": old_gassing.get("stabilization_samples", "5"),
            "plateau_thr": old_gassing.get("plateau_thr", "0.77"),
            "model_path": old_gassing.get("model_path", "kLa_TNC_gassing_out.pth"),
            "do_moving_average_window": old_gassing.get("do_moving_average_window", "10"),
            "kla_moving_average_window": old_gassing.get("kla_moving_average_window", "15"),
            "derivative_window": old_gassing.get("derivative_window", "5")
        }

    if "GradientAscend" in prefs:
        old_grad = prefs.get("GradientAscend", {})
        prefs["GradientAscend"] = {
            "grid_res": old_grad.get("entry_grid_res", "16"),
            "rising_grid_res": old_grad.get("entry_rising_grid_res", "600"),
            "step_multiplier": old_grad.get("entry_step_multiplier", "120"),
            "min_step": old_grad.get("entry_min_step", "16"),
            "max_iter": old_grad.get("entry_max_iter", "1000"),
            "quiver_scale": old_grad.get("entry_quiver_scale", "100"),
            "quiver_color": old_grad.get("entry_quiver_color", "black"),
            "quiver_width": old_grad.get("entry_quiver_width", "0.006"),
            "interp_method": old_grad.get("combo_interp_method", "cubic"),
            "colormap": old_grad.get("combo_colormap", "viridis")
        }
        
    print("MIGRATION: Migração concluída.")
    return prefs

def load_preferences():
    """Carrega as preferências salvas do arquivo JSON, se existir."""
    if os.path.exists(PREFERENCES_FILE):
        try:
            with open(PREFERENCES_FILE, "r") as f:
                prefs = json.load(f)
            
            # Executa a migração após carregar
            prefs = _migrate_preferences(prefs)
            
            return prefs
        except Exception as e:
            print(f"Erro ao carregar preferências: {e}. Usando defaults.")
            try:
                # Tenta salvar um backup do arquivo corrompido
                corrupt_file = PREFERENCES_FILE + ".corrupt"
                os.rename(PREFERENCES_FILE, corrupt_file)
                print(f"Arquivo de preferências corrompido salvo como: {corrupt_file}")
            except Exception as e_rename:
                print(f"Não foi possível renomear o arquivo corrompido: {e_rename}")
            return None
    return None

def save_preferences(prefs):
    """Salva as preferências no arquivo JSON."""
    try:
        with open(PREFERENCES_FILE, "w") as f:
            json.dump(prefs, f, indent=4)
    except Exception as e:
        print(f"Erro ao salvar preferências: {e}")

def default_preferences():
    """Retorna um dicionário com as preferências padrão (NOVA ESTRUTURA)."""
    return {
        "Configurations": {
            "ip_edit": "192.168.4.1",
            "data_delay_edit": "1000",
            "oxy_cal_a": "0.0305473419314",
            "oxy_cal_b": "-25.09136520919",
            "ph_cal_slope": "1.0",
            "ph_cal_intercept": "0.0",
            "com_port_edit": "COM5",
            "baud_rate_edit": "115200",
            "data_bits_edit": "8",
            "stop_bits_edit": "1",
            "parity_edit": "None",
            "theme_combo": "Dark"
        },
        "ParameterSettings": {
            "Temperature": {"line_edit": "37"},
            "Motor": {
                "line_edit": "400",
                "cascade_min": "200",
                "cascade_max": "1000"
            },
            "Pressure": {"line_edit": "100"},
            "Oxygen": {"line_edit": "75"},
            "Flowmeter": {
                "line_edit": "5",
                "Max Flow:": "50",
                "cascade_min": "1",
                "cascade_max": "20"
            },
            "Distance": {
                "line_edit": "100",
                "Delay Início (s):": "1",
                "Pulso ON (s):": "1",
                "Intervalo (s):": "5"
            },
            "pH": {
                "ph_setpoint": "7",
                "ph_error": "0.17",
                "ph_op_time": "10",
                "ph_disable_time": "10",
                "ph_speed": "99"
            },
            "Nutrient": {
                "nutri_op_time": "999",
                "nutri_disable_time": "1",
                "nutri_op_cycle": "500",
                "nutri_disable_cycle": "1",
                "nutri_speed": "99"
            },
            "Antifoam": {
                "antifoam_op_time": "5",
                "antifoam_disable_time": "20",
                "antifoam_speed": "99"
            },
            "Agitator": {
                "use_auto": True,
                "percent": "60",
                "re_enable_pot": True
            },
            "Biomass": {
                "comm_on": False,
                "low_thresh": "10000",
                "high_thresh": "40000",
                "opt_thresh": "25000"
            }
        },
        "ExternalPump": {
            "comm_on": False,
            "manual_speed": "50",
            "modes": {
                "mode_1": {
                    "t_initial": "0",
                    "t_final": "60",
                    "param_A": "10"
                },
                "mode_2": {
                    "t_initial": "0",
                    "t_final": "60",
                    "param_A": "1",
                    "param_B": "0.1"
                },
                "mode_3": {
                    "t_initial": "0",
                    "t_final": "60",
                    "param_A": "1",
                    "param_B": "0.05"
                },
                "mode_4": {
                    "t_initial": "0",
                    "t_final": "60",
                    "param_coeffs": "1, 0.1, 0.01"
                }
            }
        },
        "KlaInteractivePoints": [
            [5.0, 200.0, 26.35],
            [15.0, 200.0, 71.85],
            [5.0, 800.0, 100.58],
            [15.0, 800.0, 117.32],
            [10.0, 500.0, 83.12]
        ],
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
            "grid_res": "16",
            "rising_grid_res": "600",
            "step_multiplier": "120",
            "min_step": "16",
            "max_iter": "1000",
            "quiver_scale": "100",
            "quiver_color": "black",
            "quiver_width": "0.006",
            "interp_method": "cubic",
            "colormap": "viridis"
        },
        "ControlLoops": {
            "kLa_cascade": {
                "pid": {
                    "C_star": 100.0,
                    "Kp": 0.75,
                    "Ki": 0.1,
                    "Kd": 0.5
                },
                "advanced": {
                    "history_pts": 20,
                    "median_filter_win": 5,
                    "min_pts_regression": 5,
                    "prediction_horizon_s": 30.0,
                    "outer_loop_gain": 0.1,
                    "derivative_tau_s": 40.0,
                    "max_integral": 500.0,
                    "min_integral": -500.0,
                    "integral_window": 20
                }
            },
            "agitation_cascade": {
                "pid": {
                    "Kp": 0.1,
                    "Ki": 0.002,
                    "Kd": 0.75
                },
                "advanced": {
                    "history_pts": 30,
                    "median_filter_win": 5,
                    "min_pts_regression": 10,
                    "prediction_horizon_s": 30.0,
                    "outer_loop_gain": 0.1,
                    "derivative_tau_s": 40.0,
                    "max_integral": 200.0,
                    "min_integral": -200.0,
                    "integral_window": 60
                }
            },
            "aeration_cascade": {
                "pid": {
                    "Kp": 0.001,
                    "Ki": 0.0002,
                    "Kd": 0.0075
                },
                "advanced": {
                    "history_pts": 30,
                    "median_filter_win": 5,
                    "min_pts_regression": 10,
                    "prediction_horizon_s": 30.0,
                    "outer_loop_gain": 0.1,
                    "derivative_tau_s": 40.0,
                    "max_integral": 200.0,
                    "min_integral": -200.0,
                    "integral_window": 60
                }
            }
        }
    }