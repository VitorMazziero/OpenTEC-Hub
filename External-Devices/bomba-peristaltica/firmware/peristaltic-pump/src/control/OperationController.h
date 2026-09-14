void updateOperationState() {
    if (g_opState == OP_WAITING) {
        g_currentFlowRateMlMin = 0.0f;
        if (g_current_t_min >= g_config.init_t_min) {
            Serial.printf("[STATE] init_t (%.2f min) reached. Starting operation.\n", g_config.init_t_min);
            g_opState = OP_RUNNING;
            // Note: on recovery the cycle start was restored in setup; do not move it.
            if (!g_prefs.getBool(NVS_KEY_STATE_ACTIVE, false)) {
                 startCycle();
            }
        }
    } else if (g_opState == OP_RUNNING) {
        if (g_config.final_t_min > 0.0f && g_current_t_min >= g_config.final_t_min) {
            Serial.printf("[STATE] final_t (%.2f min) reached. Stopping operation.\n", g_config.final_t_min);
            g_opState = OP_IDLE;
            g_config.mode = 0;
            g_configDirty = true;
            startCycle();        // controller state only; the session volume is kept
            clearRuntimeState(); // Operation complete, clear recovery flag
        }
    }
}

// Only reset_volume calls this: the session counter goes to zero and the running cycle
// (if any) starts counting from there.
void resetOperationState() {
    Serial.println("[STATE] Resetting runtime state (Volume, PID).");
    taskDISABLE_INTERRUPTS();
    g_cumulativeVolumeMl = 0.0f;
    taskENABLE_INTERRUPTS();
    g_cycleStartVolumeMl = 0.0f;

    g_pid_error_sum = 0.0f;
    g_pid_last_error = 0.0f;
    g_pid_last_time_ms = millis();
    g_motorOnLatchTimeMs = 0;
    g_latchedSpeed = 0.0f;
}

// Everything a new profile cycle needs, without touching the session counter.
void startCycle() {
    float vol;
    taskDISABLE_INTERRUPTS();
    vol = g_cumulativeVolumeMl;
    taskENABLE_INTERRUPTS();
    g_cycleStartVolumeMl = vol;

    g_pid_error_sum = 0.0f;
    g_pid_last_error = 0.0f;
    g_pid_last_time_ms = millis();
    g_motorOnLatchTimeMs = 0;
    g_latchedSpeed = 0.0f;
    Serial.printf("[STATE] Cycle start at %.3f mL (session counter kept).\n", (double)vol);
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
    V_actual_ml -= g_cycleStartVolumeMl;   // this cycle's delivery, not the session total

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
            startCycle();
            clearRuntimeState(); // New start means clear old recovery
        } else if (cmd.equals("stop")) {
            Serial.println("[CMD] Manual stop (session volume kept).");
            g_opState = OP_IDLE;
            g_config.mode = 0;
            g_configDirty = true;
            startCycle();
            clearRuntimeState(); // Stop means we don't recover next time
        } else if (cmd.equals("reset_volume")) {
            Serial.println("[CMD] Resetting cumulative volume.");
            resetOperationState();
            // The 60 s checkpoint picks the zero up; mode and state are untouched.
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
        usbSpeedSteps = constrain(fval, -V_MAX, V_MAX);
        hasUsbSpeed   = true;
        g_opState = OP_IDLE;
        g_config.mode = 0;
        // Optional deadline. Without it the speed holds until the next command, as before.
        float msVal = getJsonFloatValue(json, "speed_ms");
        if (!isnan(msVal) && msVal > 0.0f && usbSpeedSteps != 0.0f) {
            g_usbSpeedUntilMs = millis() + (unsigned long)msVal;
            if (g_usbSpeedUntilMs == 0) g_usbSpeedUntilMs = 1;
            Serial.printf("[CMD] speed %.1f for %lu ms\n", (double)usbSpeedSteps, (unsigned long)msVal);
        } else {
            g_usbSpeedUntilMs = 0;
        }
    }
    // "pot":1 hands the motor back to the bench potentiometers (and forgets any "speed");
    // "pot":0 locks them out. "disablePot" is the 3.9 spelling, kept for local clients.
    fval = getJsonFloatValue(json, "pot");
    if (!isnan(fval)) {
        if (fval == 1.0f) {
            disablePot = false;
            hasUsbSpeed = false;
            usbSpeedSteps = 0.0f;
            g_usbSpeedUntilMs = 0;
            Serial.println("[CMD] Potentiometers enabled.");
        } else {
            disablePot = true;
            Serial.println("[CMD] Potentiometers disabled.");
        }
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

    // Contrato atomico v3.12: as mesmas equacoes quartica/quadratica do fluxometro.
    const char* pumpKeys[] = { "pumpA1", "pumpB1", "pumpK1", "pumpF1", "pumpC1",
                               "pumpK2", "pumpF2", "pumpC2", "pumpTransitionSpeed" };
    const char* nativePumpKeys[] = { "a1", "b1", "k1", "f1", "c1", "k2", "f2", "c2", "transition_speed" };
    float pumpValues[9];
    bool hasAnyPumpCoefficient = false;
    bool hasAllPumpCoefficients = true;
    for (uint8_t i = 0; i < 9; i++) {
        pumpValues[i] = getJsonFloatValue(json, pumpKeys[i]);
        if (isnan(pumpValues[i])) pumpValues[i] = getJsonFloatValue(json, nativePumpKeys[i]);
        hasAnyPumpCoefficient |= !isnan(pumpValues[i]);
        hasAllPumpCoefficients &= !isnan(pumpValues[i]) && isfinite(pumpValues[i]);
    }

    if (hasAnyPumpCoefficient) {
        if (g_opState == OP_RUNNING || g_opState == OP_WAITING) {
            Serial.println("[REJECT] Calibracao nao pode mudar durante operacao ativa.");
        } else if (!hasAllPumpCoefficients) {
            Serial.println("[REJECT] Calibracao polinomial incompleta: exige nove campos no mesmo quadro.");
        } else if (pumpValues[8] <= 0.0f || pumpValues[8] >= 1000.0f) {
            Serial.println("[REJECT] pumpTransitionSpeed fora de (0,1000).");
        } else {
            PumpDualRangeCal previous = g_pumpCal;
            g_pumpCal.a1=pumpValues[0]; g_pumpCal.b1=pumpValues[1]; g_pumpCal.k1=pumpValues[2];
            g_pumpCal.f1=pumpValues[3]; g_pumpCal.c1=pumpValues[4]; g_pumpCal.k2=pumpValues[5];
            g_pumpCal.f2=pumpValues[6]; g_pumpCal.c2=pumpValues[7]; g_pumpCal.s_t=pumpValues[8];
            float st = g_pumpCal.s_t;
            float lowValue = ((((g_pumpCal.a1*st)+g_pumpCal.b1)*st+g_pumpCal.k1)*st+g_pumpCal.f1)*st+g_pumpCal.c1;
            float highValue = (g_pumpCal.k2*st+g_pumpCal.f2)*st+g_pumpCal.c2;
            float lowSlope = (((4*g_pumpCal.a1*st)+(3*g_pumpCal.b1))*st+(2*g_pumpCal.k1))*st+g_pumpCal.f1;
            float highSlope = 2*g_pumpCal.k2*st+g_pumpCal.f2;
            bool monotonic = speedUnitsToMlmin(0) >= -1e-5f;
            float prior = speedUnitsToMlmin(0);
            for (uint8_t i=1; i<=100 && monotonic; i++) {
                float current = speedUnitsToMlmin(i*10.0f);
                monotonic = isfinite(current) && current >= prior-1e-5f;
                prior = current;
            }
            if (fabsf(lowValue-highValue) > 1e-3f || fabsf(lowSlope-highSlope) > 1e-3f || !monotonic) {
                g_pumpCal = previous;
                Serial.println("[REJECT] Curva deve ser C0+C1, nao negativa e monotonica.");
            } else {
                savePumpCalibration();
                Serial.printf("[CMD] Calibracao polinomial aplicada (CRC %08X).\n", g_pumpCal.crc32);
            }
        }
    }
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
        Serial.println("[CMD] Parameter change detected, starting a new cycle.");
        startCycle();
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

