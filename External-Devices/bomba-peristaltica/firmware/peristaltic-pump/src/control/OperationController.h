void updateOperationState() {
    if (g_opState == OP_WAITING) {
        g_currentFlowRateMlMin = 0.0f;
        if (g_current_t_min >= g_config.init_t_min) {
            Serial.printf("[STATE] init_t (%.2f min) reached. Starting operation.\n", g_config.init_t_min);
            g_opState = OP_RUNNING;
            // Note: We do NOT reset volume here if we are recovering, logic handled in setup
            if (!g_prefs.getBool(NVS_KEY_STATE_ACTIVE, false)) {
                 resetOperationState(); 
            }
        }
    } else if (g_opState == OP_RUNNING) {
        if (g_config.final_t_min > 0.0f && g_current_t_min >= g_config.final_t_min) {
            Serial.printf("[STATE] final_t (%.2f min) reached. Stopping operation.\n", g_config.final_t_min);
            g_opState = OP_IDLE;
            g_config.mode = 0;
            g_configDirty = true;
            resetOperationState();
            clearRuntimeState(); // Operation complete, clear recovery flag
        }
    }
}

void resetOperationState() {
    Serial.println("[STATE] Resetting runtime state (Volume, PID).");
    taskDISABLE_INTERRUPTS();
    g_cumulativeVolumeMl = 0.0f;
    taskENABLE_INTERRUPTS();

    g_pid_error_sum = 0.0f;
    g_pid_last_error = 0.0f;
    g_pid_last_time_ms = millis();
    g_motorOnLatchTimeMs = 0;
    g_latchedSpeed = 0.0f;
}

void runOperationLogic() {
    float t_relative_min = g_current_t_min - g_config.init_t_min;
    if (t_relative_min < 0.0f) t_relative_min = 0.0f;

    float Q_target_mlmin = calculateTargetFlow(t_relative_min);
    float V_target_ml    = calculateTargetVolume(t_relative_min);

    float V_actual_ml;
    taskDISABLE_INTERRUPTS();
    V_actual_ml = g_cumulativeVolumeMl;
    taskENABLE_INTERRUPTS();

    float pid_adj_mlmin = updatePID(V_target_ml, V_actual_ml);

    float Q_final_mlmin = Q_target_mlmin + pid_adj_mlmin;
    if (Q_final_mlmin < 0.0f) Q_final_mlmin = 0.0f;

    g_currentFlowRateMlMin = Q_final_mlmin;
}

float interpolate(float t, float t1, float t2, float q1, float q2) {
    if (t <= t1) return q1;
    if (t >= t2) return q2;
    if (fabsf(t2 - t1) < 1e-6f) return q1; 
    float alpha = (t - t1) / (t2 - t1);
    return q1 + alpha * (q2 - q1);
}

float calculateTargetFlow(float t_relative_min) {
    switch (g_config.mode) {
        case 1: return g_config.lambda_const;
        case 2: return g_config.lambda_linear + g_config.phi_linear * t_relative_min;
        case 3: return g_config.lambda_exp * expf(g_config.phi_exp * t_relative_min);
        case 4: {
            const double t = static_cast<double>(t_relative_min);
            double q = g_config.polyCoeffs[NUM_POLY_COEFFS - 1]; 
            for (int i = NUM_POLY_COEFFS - 2; i >= 0; --i) {
                q = q * t + g_config.polyCoeffs[i];
            }
            return static_cast<float>(q);
        }
        case 5: { // Piecewise linear
            if (g_config.num_segments < 2) return 0.0f; 
            for (int i = 0; i < g_config.num_segments - 1; i++) {
                if (t_relative_min <= g_config.time_points[i + 1]) {
                    return interpolate(
                        t_relative_min,
                        g_config.time_points[i], 
                        g_config.time_points[i + 1],
                        g_config.flow_points[i], 
                        g_config.flow_points[i + 1]
                    );
                }
            }
            return g_config.flow_points[g_config.num_segments - 1];
        }
        default: return 0.0f;
    }
}

float evalPolyIntegral(float t_rel) {
    const double t = static_cast<double>(t_rel);
    if (t == 0.0) return 0.0f;
    double v_prime = 0.0;
    for (int i = NUM_POLY_COEFFS - 1; i >= 0; --i) {
        double c_prime = g_config.polyCoeffs[i] / static_cast<double>(i + 1);
        v_prime = v_prime * t + c_prime;
    }
    return static_cast<float>(v_prime * t);
}

float calculateTargetVolume(float t_relative_min) {
    if (t_relative_min <= 0.0f) return 0.0f;

    switch (g_config.mode) {
        case 1: return g_config.lambda_const * t_relative_min;
        case 2: return g_config.lambda_linear * t_relative_min + 0.5f * g_config.phi_linear * t_relative_min * t_relative_min;
        case 3: 
            if (fabsf(g_config.phi_exp) < 1e-6f) return g_config.lambda_exp * t_relative_min;
            else return (g_config.lambda_exp / g_config.phi_exp) * (expf(g_config.phi_exp * t_relative_min) - 1.0f);
        case 4: return evalPolyIntegral(t_relative_min);
        case 5: { // Piecewise - trapezoidal
            if (g_config.num_segments < 2) return 0.0f;
            float volume = 0.0f;
            for (int i = 0; i < g_config.num_segments - 1; i++) {
                float t1 = g_config.time_points[i];
                float t2 = g_config.time_points[i + 1];
                float q1 = g_config.flow_points[i];
                float q2 = g_config.flow_points[i + 1];
                if (t_relative_min <= t1) break;
                if (t_relative_min >= t2) {
                    float dt = t2 - t1;
                    if (dt > 0) volume += 0.5f * (q1 + q2) * dt; 
                } else {
                    float dt = t_relative_min - t1;
                    float q_current = interpolate(t_relative_min, t1, t2, q1, q2);
                    if (dt > 0) volume += 0.5f * (q1 + q_current) * dt;
                    break; 
                }
            }
            return volume;
        }
        default: return 0.0f;
    }
}

float updatePID(float v_target_ml, float v_actual_ml) {
    unsigned long now = millis();
    float dt = (now - g_pid_last_time_ms) / 60000.0f;
    if (dt <= 0.0f) return 0.0f;

    float error = v_target_ml - v_actual_ml;
    float p_out = g_config.pid_kp * error;

    g_pid_error_sum += error * dt;
    g_pid_error_sum = constrain(g_pid_error_sum, -100.0f, 100.0f);
    float i_out = g_config.pid_ki * g_pid_error_sum;

    float d_error = (error - g_pid_last_error) / dt;
    float d_out = g_config.pid_kd * d_error;

    g_pid_last_error = error;
    g_pid_last_time_ms = now;

    return p_out + i_out + d_out;
}

float calcPotSpeed() {
    static float filtInt  = ADC_CENTER;
    static float filtGain = ADC_CENTER;

    int rawI = analogRead(POT_INT_PIN);
    int rawG = analogRead(POT_GAIN_PIN);

    filtInt  = ADC_LPF_ALPHA  * rawI + (1.0f - ADC_LPF_ALPHA)  * filtInt;
    filtGain = ADC_LPF_ALPHA  * rawG + (1.0f - ADC_LPF_ALPHA)  * filtGain;

    float mag = filtInt / (float)ADC_MAX;
    mag = constrain(mag, 0.0f, 1.0f);

    float dir = (filtGain - ADC_CENTER) / ADC_CENTER;
    dir = constrain(dir, -1.0f, 1.0f);

    float signedMag = dir * mag;
    float out = signedMag * V_MAX;

    return out;
}

void handleSerialInput() {
    static char buf[4096]; 
    static size_t idx = 0;

    while (Serial.available() > 0) {
        int c = Serial.read();
        if (c == '\r') continue;
        if (c == '\n') {
            buf[idx] = '\0';
            idx      = 0;

            String cmd = String(buf);
            cmd.trim();
            if (cmd.length() > 0 && cmd.startsWith("{") && cmd.endsWith("}")) {
                processJsonCommand(cmd);
            } else if (cmd.length() > 0) {
                Serial.println("Error: Command must be in JSON format.");
            }
        } else {
            if (idx < sizeof(buf) - 1) {
                buf[idx++] = static_cast<char>(c);
            } else {
                idx = 0;
            }
        }
    }
}

void processJsonCommand(String json) {
    Serial.println("[CMD] Processing: " + json);

    String cmd = getJsonStringValue(json, "command");
    bool paramChanged = false;
    float fval;

    if (cmd.length() > 0) {
        if (cmd.equals("start")) {
            Serial.println("[CMD] Manual start.");
            g_config.init_t_min  = 0.0f;
            g_config.final_t_min = 0.0f;
            g_configDirty = true;
            g_opState = OP_RUNNING;
            g_opTriggerTimeMs = millis();
            resetOperationState();
            clearRuntimeState(); // New start means clear old recovery
        } else if (cmd.equals("stop")) {
            Serial.println("[CMD] Manual stop.");
            g_opState = OP_IDLE;
            g_config.mode = 0;
            g_configDirty = true;
            resetOperationState();
            clearRuntimeState(); // Stop means we don't recover next time
        } else if (cmd.equals("reset_volume")) {
            Serial.println("[CMD] Resetting cumulative volume.");
            resetOperationState();
            // Don't clear NVS here unless we assume mode 0, 
            // but usually reset_volume implies staying in mode. 
            // We'll let next 60s save update the 0 vol.
        } else if (cmd.equals("save_config")) {
            saveConfig();
        } else if (cmd.equals("load_config")) {
            loadConfig();
        } else if (cmd.equals("print_config")) {
            buildDataJson(true); // Force include arrays
            Serial.println(g_lastDataJson);
        } else if (cmd.equals("clear_nvs")) {
            Serial.println("[CMD] Clearing all preferences...");
            g_prefs.clear();
            delay(1000);
            ESP.restart();
        }
    }

    fval = getJsonFloatValue(json, "mode");
    if (!isnan(fval)) {
        int newMode = (int)fval;
        if (newMode >= 0 && newMode <= 5) {
            // Even if mode is same, receiving it via command implies a "Set" action
            if (newMode != g_config.mode) {
                g_config.mode = newMode;
                g_configDirty = true;
            }
            // Explicit mode command resets the timer/volume
            paramChanged = true; 
        } else {
            Serial.printf("[ERR] Invalid mode %d\n", newMode);
        }
    }
    
    fval = getJsonFloatValue(json, "init_t");
    if (!isnan(fval) && g_config.init_t_min != fval) { g_config.init_t_min = fval; g_configDirty = true; paramChanged = true; }
    fval = getJsonFloatValue(json, "final_t");
    if (!isnan(fval) && g_config.final_t_min != fval) { g_config.final_t_min = fval; g_configDirty = true; paramChanged = true; }

    fval = getJsonFloatValue(json, "speed");
    if (!isnan(fval)) {
        usbSpeedSteps = fval;
        hasUsbSpeed   = true;
        g_opState = OP_IDLE;
        g_config.mode = 0;
    }
    fval = getJsonFloatValue(json, "disablePot");
    if (!isnan(fval)) {
        disablePot = (fval == 1.0f);
    }

    fval = getJsonFloatValue(json, "sensorEnable");
    if (!isnan(fval)) sensorEnable = (fval == 1.0f);
    fval = getJsonFloatValue(json, "sensorBypass");
    if (!isnan(fval)) sensorBypass = (fval == 1.0f);
    fval = getJsonFloatValue(json, "sensorButtonOverride");
    if (!isnan(fval)) sensorButtonOverride = (fval == 1.0f);

    fval = getJsonFloatValue(json, "pumpSlope");
    if (!isnan(fval) && g_config.pumpSlope != fval) { g_config.pumpSlope = fval; g_configDirty = true; }
    fval = getJsonFloatValue(json, "pumpIntercept");
    if (!isnan(fval) && g_config.pumpIntercept != fval) { g_config.pumpIntercept = fval; g_configDirty = true; }
    fval = getJsonFloatValue(json, "pid_kp");
    if (!isnan(fval) && g_config.pid_kp != fval) { g_config.pid_kp = fval; g_configDirty = true; }
    fval = getJsonFloatValue(json, "pid_ki");
    if (!isnan(fval) && g_config.pid_ki != fval) { g_config.pid_ki = fval; g_configDirty = true; }
    fval = getJsonFloatValue(json, "pid_kd");
    if (!isnan(fval) && g_config.pid_kd != fval) { g_config.pid_kd = fval; g_configDirty = true; }

    fval = getJsonFloatValue(json, "lambda_const");
    if (!isnan(fval) && g_config.lambda_const != fval) { g_config.lambda_const = fval; g_configDirty = true; paramChanged = true; }
    fval = getJsonFloatValue(json, "lambda_linear");
    if (!isnan(fval) && g_config.lambda_linear != fval) { g_config.lambda_linear = fval; g_configDirty = true; paramChanged = true; }
    fval = getJsonFloatValue(json, "phi_linear");
    if (!isnan(fval) && g_config.phi_linear != fval) { g_config.phi_linear = fval; g_configDirty = true; paramChanged = true; }
    fval = getJsonFloatValue(json, "lambda_exp");
    if (!isnan(fval) && g_config.lambda_exp != fval) { g_config.lambda_exp = fval; g_configDirty = true; paramChanged = true; }
    fval = getJsonFloatValue(json, "phi_exp");
    if (!isnan(fval) && g_config.phi_exp != fval) { g_config.phi_exp = fval; g_configDirty = true; paramChanged = true; }

    // Mode 4: Polynomial
    bool polyChanged = false;
    for (int i = 0; i < NUM_POLY_COEFFS; i++) {
        char key[16];
        snprintf(key, sizeof(key), "p%d", i);
        double dval = getJsonDoubleValue(json.c_str(), key);
        if (!isnan(dval) && g_config.polyCoeffs[i] != dval) {
            g_config.polyCoeffs[i] = dval;
            g_configDirty = true;
            polyChanged = true;
        }
    }
    if (polyChanged) paramChanged = true;

    // Mode 5: Piecewise
    bool piecewiseChanged = false;
    fval = getJsonFloatValue(json.c_str(), "num_segments");
    if (!isnan(fval)) {
        int n = (int)fval;
        if (n >= 2 && n <= MAX_SEGMENTS) {
            if (g_config.num_segments != n) {
                g_config.num_segments = n;
                g_configDirty = true;
                piecewiseChanged = true;
            }
        }
    }
    
    // Parse time points
    for (int i = 0; i < MAX_SEGMENTS; i++) {
        char key[16];
        snprintf(key, sizeof(key), "t%d", i);
        fval = getJsonFloatValue(json.c_str(), key);
        if (!isnan(fval)) {
            if (g_config.time_points[i] != fval) {
                g_config.time_points[i] = fval;
                g_configDirty = true;
                piecewiseChanged = true;
            }
        }
    }
    
    // Parse flow points
    for (int i = 0; i < MAX_SEGMENTS; i++) {
        char key[16];
        snprintf(key, sizeof(key), "q%d", i);
        fval = getJsonFloatValue(json.c_str(), key);
        if (!isnan(fval)) {
            if (g_config.flow_points[i] != fval) {
                g_config.flow_points[i] = fval;
                g_configDirty = true;
                piecewiseChanged = true;
            }
        }
    }

    if (piecewiseChanged) {
        paramChanged = true;
        if (g_config.mode == 5) {
            // First time point must be 0
            if (g_config.time_points[0] != 0.0f) {
                g_config.time_points[0] = 0.0f; 
            }
        }
    }
    
    // Logic: If parameters changed (or mode command sent), reset the pump cycle
    if (paramChanged && cmd.isEmpty()) {
        Serial.println("[CMD] Parameter change detected, resetting state.");
        resetOperationState();
        clearRuntimeState(); // Clear checkpoint, starting fresh
        g_opTriggerTimeMs = millis();
        
        if (g_config.mode > 0) {
            if (g_config.init_t_min > 0.0f) {
                g_opState = OP_WAITING;
                Serial.printf("[STATE] Waiting for %.2f min.\n", g_config.init_t_min);
            } else {
                g_opState = OP_RUNNING;
                Serial.println("[STATE] Starting immediately.");
            }
        } else {
            g_opState = OP_IDLE;
        }
    } else if (paramChanged && !cmd.isEmpty()) {
        // Handled within specific commands (start/stop)
    }
}

