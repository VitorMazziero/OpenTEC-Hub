void buildDataJson(bool includeArrays) {
    float vol;
    int pwm_duty;
    taskDISABLE_INTERRUPTS();
    vol = g_cumulativeVolumeMl;
    pwm_duty = g_actualPwmDuty;
    taskENABLE_INTERRUPTS();

    float t_rel = g_current_t_min - g_config.init_t_min;
    if (t_rel < 0.0f) t_rel = 0.0f;
    float v_target = calculateTargetVolume(t_rel);

    // Optimized JSON building to prevent WDT timeout
    char jsonBuffer[1024]; // Reduced buffer size as we removed massive arrays

    snprintf(jsonBuffer, sizeof(jsonBuffer), 
        "{\"mode\":%d,\"pwm\":%d,\"speed\":%.1f,\"flow_rate_mlmin\":%.3f,"
        "\"cum_volume_ml\":%.3f,\"cycle_volume_ml\":%.3f,\"v_target_ml\":%.3f,\"active\":%s,\"waiting\":%s,"
        "\"current_t_min\":%.3f,\"init_t_min\":%.3f,\"final_t_min\":%.3f,"
        "\"slope\":%.4f,\"intercept\":%.4f,\"pid_kp\":%.4f,\"pid_ki\":%.4f,\"pid_kd\":%.4f,"
        "\"pot\":%d,\"usb_speed\":%d",
        g_config.mode,
        pwm_duty,
        (double)g_cmdSpeed,
        (double)g_currentFlowRateMlMin,
        (double)vol,
        (double)(vol - g_cycleStartVolumeMl),
        (double)v_target,
        (g_opState == OP_RUNNING) ? "true" : "false",
        (g_opState == OP_WAITING) ? "true" : "false",
        (double)g_current_t_min,
        (double)g_config.init_t_min,
        (double)g_config.final_t_min,
        (double)g_config.pumpSlope,
        (double)g_config.pumpIntercept,
        (double)g_config.pid_kp,
        (double)g_config.pid_ki,
        (double)g_config.pid_kd,
        disablePot ? 0 : 1,
        hasUsbSpeed ? 1 : 0
    );

    String json = String(jsonBuffer);
    if (includeArrays) {
        json.reserve(1024);
        char tempBuffer[64];
        if (g_config.mode == 4) {
            json += ",\"poly\":[";
            for (int i = 0; i < NUM_POLY_COEFFS; i++) { 
                snprintf(tempBuffer, sizeof(tempBuffer), "%.12f", g_config.polyCoeffs[i]);
                json += tempBuffer;
                if (i < (NUM_POLY_COEFFS - 1)) json += ",";
            }
            json += "]";
        }

        if (g_config.mode == 5 && g_config.num_segments > 0) {
            snprintf(tempBuffer, sizeof(tempBuffer), ",\"num_segments\":%d", g_config.num_segments);
            json += tempBuffer;

            json += ",\"time_points\":[";
            for (int i = 0; i < g_config.num_segments; i++) {
                snprintf(tempBuffer, sizeof(tempBuffer), "%.3f", g_config.time_points[i]);
                json += tempBuffer;
                if (i < g_config.num_segments - 1) json += ",";
            }
            json += "]";

            json += ",\"flow_points\":[";
            for (int i = 0; i < g_config.num_segments; i++) {
                snprintf(tempBuffer, sizeof(tempBuffer), "%.3f", g_config.flow_points[i]);
                json += tempBuffer;
                if (i < g_config.num_segments - 1) json += ",";
            }
            json += "]";
        }
    }

    json += "}";
    g_lastDataJson = json;
}

