#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# simple_cascade_control.py

import time
import numpy as np
from collections import deque
from typing import Tuple, Dict

# -----------------------------------------------------------------------------
# Função de filtragem (copiada do kLa_cascade_control.py)
# -----------------------------------------------------------------------------

def median_filter_centered(x: np.ndarray, window: int) -> np.ndarray:
    """Edge-replicated, centred median filter."""
    if len(x) == 0:
        return np.array([])
    # Ensure window is odd
    if window % 2 == 0:
        window += 1
    half = window // 2
    x_padded = np.pad(x, (half, half), mode="edge")
    return np.array([np.median(x_padded[i : i + window]) for i in range(len(x))])

# -----------------------------------------------------------------------------
# Classe do Controlador
# -----------------------------------------------------------------------------

class SimplePIDController:
    """
    Controlador de cascata simples (Agit ou Aera) atualizado para
    usar a lógica preditiva do kLa_cascade_control.
    """
    def __init__(self, main_window, pid_prefix, integral_buffer_attr, last_error_attr):
        self.main_window = main_window
        self.pid_prefix = pid_prefix  # 'Agit' ou 'Aera'
        self.integral_buffer_attr = integral_buffer_attr  # ex: 'pid_agit_integral_buffer'
        self.last_error_attr = last_error_attr  # ex: 'last_agit_error'
        self.filtered_deriv_attr = f"filtered_deriv_{self.pid_prefix.lower()}"
        self.oxy_hist_attr = f"oxy_hist_{self.pid_prefix.lower()}" # Histórico de O2 separado
        self.last_run_time_attr = f"last_run_time_{self.pid_prefix.lower()}"

        # Carrega os ganhos PID e configs avançadas
        self.reload_pid_config()
        
        # Inicializa o estado do PID no main_window se não existir
        if not hasattr(self.main_window, self.integral_buffer_attr):
            setattr(self.main_window, self.integral_buffer_attr, deque(maxlen=self.integral_window))
        else:
            # Garante que o maxlen está correto após recarregar
            current_deque = getattr(self.main_window, self.integral_buffer_attr)
            # CORREÇÃO: Recria o deque se o maxlen for diferente, pois 'maxlen' é read-only
            if not current_deque or current_deque.maxlen != self.integral_window:
                setattr(self.main_window, self.integral_buffer_attr, deque(maxlen=self.integral_window))

        if not hasattr(self.main_window, self.last_error_attr):
            setattr(self.main_window, self.last_error_attr, 0.0)
        if not hasattr(self.main_window, self.filtered_deriv_attr):
            setattr(self.main_window, self.filtered_deriv_attr, 0.0)
        if not hasattr(self.main_window, self.oxy_hist_attr):
            setattr(self.main_window, self.oxy_hist_attr, [])
        # Inicializa o estado do throttle
        if not hasattr(self.main_window, self.last_run_time_attr):
            setattr(self.main_window, self.last_run_time_attr, 0.0)

    # simple_cascade_control.py - CÓDIGO CORRIGIDO

    def reload_pid_config(self):
        """Recarrega os ganhos PID e parâmetros avançados das preferências."""
        try:
            # O prefixo é 'Agit' ou 'Aera'
            prefix_lower = self.pid_prefix.lower()  # 'agit' ou 'aera'
            
            # Define o nome base completo para corresponder ao JSON
            if prefix_lower == 'agit':
                base_name = 'agitation'
            elif prefix_lower == 'aera':
                base_name = 'aeration'
            else:
                # Fallback, embora não deva acontecer
                base_name = prefix_lower 
            
            # A chave de config agora é 'agitation_cascade' ou 'aeration_cascade'
            config_key = f"{base_name}_cascade" 
            
            # Obtém o bloco de configuração inteiro, ex: {"pid": {...}, "advanced": {...}}
            loop_config = self.main_window.preferences.get("ControlLoops", {}).get(config_key, {})
            
            if not loop_config:
                # Adiciona um log para sabermos se a chave principal não foi encontrada
                self.main_window.log_signal.emit(f"Aviso: Chave '{config_key}' não encontrada em ControlLoops.")
            
            # Obtém os sub-dicionários
            pid_config = loop_config.get("pid", {})
            adv_config = loop_config.get("advanced", {})
            
            # Carrega Parâmetros PID (agora são numéricos, não strings)
            # Estes defaults SÓ serão usados se as chaves Kp/Ki/Kd não existirem dentro de "pid"
            self.Kp = float(pid_config.get("Kp", 0.1))
            self.Ki = float(pid_config.get("Ki", 0.001))
            self.Kd = float(pid_config.get("Kd", 0.01))

            # Carrega Parâmetros Avançados (agora são numéricos e chaves snake_case)
            self.history_pts = int(adv_config.get("history_pts", 30))
            self.median_filter_win = int(adv_config.get("median_filter_win", 5))
            self.min_pts_regression = int(adv_config.get("min_pts_regression", 10))
            self.prediction_horizon_s = float(adv_config.get("prediction_horizon_s", 30.0))
            self.outer_loop_gain = float(adv_config.get("outer_loop_gain", 0.1))
            self.derivative_tau_s = float(adv_config.get("derivative_tau_s", 40.0))
            self.max_integral = float(adv_config.get("max_integral", 200.0))
            self.min_integral = float(adv_config.get("min_integral", -200.0))
            self.integral_window = int(adv_config.get("integral_window", 60))

            self.control_interval_s = float(adv_config.get("control_interval_s", 10.0))
            if self.control_interval_s < 1: # Garante um limite mínimo
                self.control_interval_s = 1
            
            # Atualiza o maxlen do deque caso tenha mudado
            if hasattr(self.main_window, self.integral_buffer_attr):
                current_deque = getattr(self.main_window, self.integral_buffer_attr)
                # CORREÇÃO: Recria o deque se o maxlen for diferente, pois 'maxlen' é read-only
                if not current_deque or current_deque.maxlen != self.integral_window:
                    setattr(self.main_window, self.integral_buffer_attr, deque(maxlen=self.integral_window))

        except Exception as e:
            self.main_window.log_signal.emit(f"Erro ao carregar PID {self.pid_prefix}: {e}")
            # Definir todos os defaults em caso de falha
            self.Kp, self.Ki, self.Kd = 0.1, 0.001, 0.01
            self.history_pts, self.median_filter_win, self.min_pts_regression = 30, 5, 10
            self.prediction_horizon_s, self.outer_loop_gain, self.derivative_tau_s = 30.0, 0.1, 40.0
            self.max_integral, self.min_integral, self.integral_window = 200.0, -200.0, 60
            self.control_interval_s = 5.0 # Default em caso de falha
            
    def _calculate_pid(self, inner_error, dt):
        """
        Calcula a saída de ajuste do PID (baseado no _update_pid do kLa).
        """
        # --- Integral (com janela e anti-windup na subclasse) ---
        integral_buffer = getattr(self.main_window, self.integral_buffer_attr)
        integral_buffer.append(inner_error * dt) # Adiciona novo termo

        raw_integral = sum(integral_buffer)
        bounded_integral = np.clip(raw_integral, self.min_integral, self.max_integral)
        I = self.Ki * bounded_integral
        
        # --- Derivativo (com filtro) ---
        last_inner_error = getattr(self.main_window, self.last_error_attr, 0.0)
        filtered_derivative = getattr(self.main_window, self.filtered_deriv_attr, 0.0)

        alpha = dt / (self.derivative_tau_s + dt) if (self.derivative_tau_s + dt) > 0 else 0.0
        raw_derivative = (inner_error - last_inner_error) / dt if dt > 0 else 0.0
        
        filtered_derivative = (alpha * raw_derivative) + (1.0 - alpha) * filtered_derivative
        D = self.Kd * filtered_derivative

        # --- Proporcional ---
        P = self.Kp * inner_error

        # Salva o estado de volta no main_window
        setattr(self.main_window, self.last_error_attr, inner_error)
        setattr(self.main_window, self.filtered_deriv_attr, filtered_derivative)
        
        output = P + I + D
        
        # Log
        try:
            log_attr = f"pid_terms_{self.pid_prefix.lower()}"
            setattr(self.main_window, log_attr, {
                "P": P, "I": I, "D": D, 
                "output": output, "integral_raw": raw_integral
            })
        except Exception:
            pass

        return output

    def run_control(self):
        mw = self.main_window
        # --- THROTTLE DO LOOP DE CONTROLE ---
        # Roda este loop de controle em uma frequência menor para evitar spam de comandos
        now = time.time()
        last_run_time = getattr(mw, self.last_run_time_attr, 0.0)
        if (now - last_run_time) < self.control_interval_s:
            return # Ainda não é hora de rodar
        # Atualiza o timestamp *antes* de rodar
        # (Se houver um erro, ainda esperamos o próximo intervalo)
        setattr(mw, self.last_run_time_attr, now)
        # 1. Obter DO atual (já lido no main.py)
        current_oxygen = mw.current_oxygen
        if not (0 <= current_oxygen < 150):
            return  # Leitura inválida
        
        # 2. Obter Setpoint de DO
        oxygen_setpoint = mw.parameter_settings_page.get_oxygen_setpoint()
        
        # 3. Atualizar Histórico e Fazer Predição (lógica do kLa)
        now = time.time()
        oxy_hist = getattr(mw, self.oxy_hist_attr)
        
        # (Copiado de _update_oxygen_history do kLa)
        oxy_hist.append((now, current_oxygen))
        if len(oxy_hist) > self.history_pts:
            oxy_hist.pop(0)
        
        if len(oxy_hist) < self.min_pts_regression:
            C_pred = oxy_hist[-1][1] if oxy_hist else 0.0
            dC_dt_pred = 0.0
        else:
            times, values = zip(*oxy_hist)
            ts = np.asarray(times)
            Cs = np.asarray(values)
            Cs_smooth = median_filter_centered(Cs, self.median_filter_win)
            
            if len(Cs_smooth) > 0:
                ts_shifted = ts - ts[0]
                
                try:
                    # Garante que temos pontos suficientes após filtragem
                    if len(ts_shifted) >= 2 and len(Cs_smooth) >= 2:
                        slope, _ = np.polyfit(ts_shifted, Cs_smooth, 1)
                    else:
                        slope = 0.0
                except (np.linalg.LinAlgError, ValueError):
                    slope = 0.0 # Evita crash se os dados forem ruins
                
                C_pred = np.clip(Cs_smooth[-1] + slope * self.prediction_horizon_s, 0.0, 150.0)
                dC_dt_pred = float(slope)
            else:
                C_pred = current_oxygen
                dC_dt_pred = 0.0


        # 4. Loop Externo (Cálculo do dC/dt desejado)
        # (Copiado de _desired_rate do kLa)
        desired_dC_dt = self.outer_loop_gain * (oxygen_setpoint - C_pred)
        
        # 5. Loop Interno (Erro do PID)
        inner_error = desired_dC_dt - dC_dt_pred
        
        # 6. Obter dt
        dt = max(mw.dataDelay / 1000.0, 1e-6)
        
        # 7. Calcular Saída PID
        pid_output = self._calculate_pid(inner_error, dt)
        
        # 8. Aplicar saída (implementado na subclasse)
        self._apply_output(pid_output)

    def _log_pid(self, mw, prefix):
        """Envia os termos do PID para o log da UI, com throttle."""
        now = time.time()
        log_attr = f"last_pid_log_time_{prefix.lower()}"
        
        # Aplica "throttle" para não inundar o log
        if now - getattr(mw, log_attr, 0) >= 1.0:
            try:
                # 1. Timestamp
                ts = time.strftime("%H:%M:%S")

                # 2. Termos PID (calculados em _calculate_pid)
                terms_attr = f"pid_terms_{prefix.lower()}"
                terms = getattr(mw, terms_attr, {})
                P_val = terms.get('P', 0.0)
                I_val = terms.get('I', 0.0)
                D_val = terms.get('D', 0.0)

                # 3. Valores Chave (Setpoint, O2 Atual)
                setpoint = mw.parameter_settings_page.get_oxygen_setpoint()
                current_o2 = mw.current_oxygen

                # 4. Saída Final (RPM ou Vazão)
                final_output = 0.0
                output_unit = ""
                if prefix == "Agit":
                    final_output = mw.simple_cascade_last_rpm
                    output_unit = "RPM"
                else: # Aera
                    final_output = mw.simple_cascade_last_flow
                    output_unit = "L/min"

                # 5. Erro (o 'inner_error' que foi para o PID)
                error_attr = f"last_{prefix.lower()}_error"
                inner_error = getattr(mw, error_attr, 0.0)

                # 6. Constantes PID (do 'self' do controlador)
                kp_val, ki_val, kd_val = self.Kp, self.Ki, self.Kd

                # --- Formata o Log Completo ---
                # [TS] Cascata Agit: Set=50.0, In=49.8, Out=401.5 RPM
                part1 = f"[{ts}] Cascata {prefix}: " \
                        f"Set={setpoint:.1f}, " \
                        f"In={current_o2:.1f}, " \
                        f"Out={final_output:.1f} {output_unit}"
                
                # | Err=-0.012
                part2 = f"Err={inner_error:.3f}"
                
                # | P=-0.12, I=0.45, D=0.01
                part3 = f"P={P_val:.2f}, I={I_val:.2f}, D={D_val:.2f}"
                
                # | Kp=0.1, Ki=0.002, Kd=0.75
                part4 = f"Kp={kp_val:.3g}, Ki={ki_val:.3g}, Kd={kd_val:.3g}"
                
                final_log_msg = f"{part1} | {part2} | {part3} | {part4}"

                mw.log_signal.emit(final_log_msg)
            
            except Exception as e:
                # Log de fallback em caso de erro
                mw.log_signal.emit(f"[{time.strftime('%H:%M:%S')}] Erro ao formatar log PID {prefix}: {e}")
            
            # Atualiza o timestamp do throttle
            setattr(mw, log_attr, now)

    def _apply_output(self, pid_output):
        raise NotImplementedError


class AgitationCascadeControl(SimplePIDController):
    """Controla o OD ajustando o RPM do motor."""
    
    def __init__(self, main_window):
        super().__init__(main_window, 'Agit', 'pid_agit_integral_buffer', 'last_agit_error')

    def _apply_output(self, pid_output):
        mw = self.main_window
        ps = mw.parameter_settings_page
        
        # Obtém o último RPM comandado
        last_rpm = mw.simple_cascade_last_rpm
        
        # A saída do PID é um *ajuste*
        new_rpm = last_rpm + pid_output
        
        # Obtém o Alcance da Cascata
        try:
            min_rpm = float(ps.motor_cascade_min_edit.text())
            max_rpm = float(ps.motor_cascade_max_edit.text())
        except Exception:
            min_rpm, max_rpm = 200, 1000
        
        # Clampa a saída
        clamped_rpm = np.clip(new_rpm, min_rpm, max_rpm)
        
        # Lógica Anti-Windup: Se a saída foi clampeada,
        # remove o último termo adicionado ao buffer integral.
        if clamped_rpm != new_rpm:
            try:
                integral_buffer = getattr(mw, self.integral_buffer_attr)
                if integral_buffer:
                    integral_buffer.pop() # Remove o termo (error * dt)
            except Exception as e:
                mw.log_signal.emit(f"Erro anti-windup Agit: {e}")
            
        # Envia comando
        mw.comm_handler.send_command({"motorSetpoint": int(clamped_rpm)})
        
        # Atualiza o estado e a UI
        mw.simple_cascade_last_rpm = clamped_rpm
        mw.current_motor_rpm = clamped_rpm  # Atualiza o valor global para logging
        ps.motor_block.line_edit.setText(str(int(clamped_rpm)))
        
        # Loga termos do PID
        self._log_pid(mw, "Agit")


class AerationCascadeControl(SimplePIDController):
    """Controla o OD ajustando a Vazão de Ar (L/min)."""
    
    def __init__(self, main_window):
        super().__init__(main_window, 'Aera', 'pid_aer_integral_buffer', 'last_aer_error')

    def _apply_output(self, pid_output):
        mw = self.main_window
        ps = mw.parameter_settings_page
        
        # Obtém a última vazão comandada
        last_flow = mw.simple_cascade_last_flow
        
        # A saída do PID é um *ajuste*
        new_flow = last_flow + pid_output
        
        # Obtém o Alcance da Cascata
        try:
            min_flow = float(ps.flow_cascade_min_edit.text())
            max_flow = float(ps.flow_cascade_max_edit.text())
        except Exception:
            min_flow, max_flow = 1, 20
        
        # Clampa a saída
        clamped_flow = np.clip(new_flow, min_flow, max_flow)
        
        # Lógica Anti-Windup
        if clamped_flow != new_flow:
            try:
                integral_buffer = getattr(mw, self.integral_buffer_attr)
                if integral_buffer:
                    integral_buffer.pop()
            except Exception as e:
                mw.log_signal.emit(f"Erro anti-windup Aera: {e}")
            
        # Envia comando (força a comunicação do fluxômetro como ativa)
        mw.comm_handler.send_command({
            "flowSetpoint": clamped_flow,
            "flowmeterComm": 1 
        })
        
        # Atualiza o estado e a UI
        mw.simple_cascade_last_flow = clamped_flow
        ps.flow_block.line_edit.setText(f"{clamped_flow:.2f}")
        
        # Loga termos do PID
        self._log_pid(mw, "Aera")