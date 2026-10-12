void buildDataJson(bool includeArrays) {
    const float vol = volRead();
    const int pwm_duty = g_actualPwmDuty;

    float t_rel = g_current_t_min - g_config.init_t_min;
    if (t_rel < 0.0f) t_rel = 0.0f;
    float v_target = calculateTargetVolume(t_rel);

    // Optimized JSON building to prevent WDT timeout
    char jsonBuffer[1024]; // Reduced buffer size as we removed massive arrays

    snprintf(jsonBuffer, sizeof(jsonBuffer), 
        "{\"mode\":%d,\"pwm\":%d,\"speed\":%.1f,\"flow_rate_mlmin\":%.3f,"
        "\"cum_volume_ml\":%.3f,\"cycle_volume_ml\":%.3f,\"v_target_ml\":%.3f,\"active\":%s,\"waiting\":%s,"
        "\"current_t_min\":%.3f,\"init_t_min\":%.3f,\"final_t_min\":%.3f,"
        "\"pid_kp\":%.4f,\"pid_ki\":%.4f,\"pid_kd\":%.4f,"
        "\"pumpA1\":%.9g,\"pumpB1\":%.9g,\"pumpK1\":%.9g,\"pumpF1\":%.9g,\"pumpC1\":%.9g,"
        "\"pumpK2\":%.9g,\"pumpF2\":%.9g,\"pumpC2\":%.9g,\"pumpTransitionSpeed\":%.2f,\"pumpCalCrc\":\"%08X\","
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
        (double)g_config.pid_kp,
        (double)g_config.pid_ki,
        (double)g_config.pid_kd,
        (double)g_pumpCal.a1, (double)g_pumpCal.b1, (double)g_pumpCal.k1,
        (double)g_pumpCal.f1, (double)g_pumpCal.c1,
        (double)g_pumpCal.k2, (double)g_pumpCal.f2, (double)g_pumpCal.c2,
        (double)g_pumpCal.s_t,
        g_pumpCal.crc32,
        disablePot ? 0 : 1,
        hasUsbSpeed ? 1 : 0
    );

    String json = String(jsonBuffer);

    // Dosagem por pulsos e estado 3.13, em buffer proprio para nao apertar o quadro acima.
    char doseBuffer[640];
    const unsigned long nowMs = millis();
    unsigned long nextS = 0;
    if (g_doseActive && !g_dosePulseOn && static_cast<long>(g_doseNextPulseMs - nowMs) > 0) {
        nextS = (g_doseNextPulseMs - nowMs + 999UL) / 1000UL;
    }
    snprintf(doseBuffer, sizeof(doseBuffer),
        ",\"dose_active\":%s,\"dose_pulse\":%s,\"dose_ml\":%.3f,\"dose_period_s\":%.1f,"
        "\"dose_speed\":%.1f,\"dose_rate_mlmin\":%.3f,\"dose_total_ml\":%.3f,"
        "\"dose_delivered_ml\":%.3f,\"dose_pulses\":%lu,\"dose_next_s\":%lu,\"dose_dead_ms\":%.0f,"
        "\"dose_short_ml\":%.3f,\"drv\":%d,\"liquid_sensor\":%s,\"motor_allowed\":%s,\"boot\":%lu,"
        "\"reset_reason\":%d,\"rej_cmd_id\":%lu,\"rej_err\":\"%s\",\"fw_build\":\"%s\"",
        g_doseActive ? "true" : "false",
        g_dosePulseOn ? "true" : "false",
        (double)g_dose.volumeMl, (double)g_dose.periodS,
        (double)g_dose.speed, (double)g_dose.rateMlMin, (double)g_dose.totalMl,
        (double)doseDeliveredNow(),
        static_cast<unsigned long>(g_dosePulses),
        nextS,
        (double)g_dose.deadMs,
        (double)g_doseShortMl,
        static_cast<int>(g_drive),
        (sensorEnable && !sensorBypass) ? "true" : "false",
        g_motorAllowed ? "true" : "false",
        static_cast<unsigned long>(g_bootCount),
        g_resetReason,
        static_cast<unsigned long>(g_lastRejectedHubCommandId),
        g_lastRejectCode,
        PUMP_FW_BUILD);
    json += doseBuffer;

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

