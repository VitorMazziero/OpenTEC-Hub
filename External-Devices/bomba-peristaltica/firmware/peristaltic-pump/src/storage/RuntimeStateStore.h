void checkAndRecoverState() {
    bool isActive = g_prefs.getBool(NVS_KEY_STATE_ACTIVE, false);
    
    if (isActive) {
        int savedMode = g_prefs.getInt(NVS_KEY_STATE_MODE, 0);
        float savedVol = g_prefs.getFloat(NVS_KEY_STATE_VOL, 0.0f);
        float savedTime = g_prefs.getFloat(NVS_KEY_STATE_TIME, 0.0f);
        float savedCycleVol = g_prefs.getFloat(NVS_KEY_STATE_CVOL, 0.0f);

        Serial.println(">>> DETECTED UNEXPECTED RESET! RECOVERING STATE <<<");
        Serial.printf("Recovering: Mode %d at %.2f min with %.2f mL\n", savedMode, savedTime, savedVol);

        // Restore Volume (session counter and where this cycle started)
        taskDISABLE_INTERRUPTS();
        g_cumulativeVolumeMl = savedVol;
        taskENABLE_INTERRUPTS();
        g_cycleStartVolumeMl = savedCycleVol;

        // Restore Mode (ensure config matches saved state if possible, though config is usually persistent)
        if (g_config.mode != savedMode) {
             g_config.mode = savedMode;
             // We don't mark dirty here to avoid immediate re-write, relies on config key match
        }

        g_opTriggerTimeMs = millis() - (unsigned long)(savedTime * 60000.0f);

        g_opState = OP_RUNNING;
        
        // Reset PID
        g_pid_error_sum = 0.0f;
        g_pid_last_error = 0.0f;
        g_pid_last_time_ms = millis();

    } else {
        Serial.println("[BOOT] Clean start (No active state found).");
        resetOperationState();
        g_opState = OP_IDLE;
    }
}

void saveRuntimeState() {
    float vol;
    taskDISABLE_INTERRUPTS();
    vol = g_cumulativeVolumeMl;
    taskENABLE_INTERRUPTS();

    // Only save if we are actually doing something interesting
    if (g_config.mode != 0) {
        g_prefs.putBool(NVS_KEY_STATE_ACTIVE, true);
        g_prefs.putFloat(NVS_KEY_STATE_VOL, vol);
        g_prefs.putFloat(NVS_KEY_STATE_TIME, g_current_t_min);
        g_prefs.putInt(NVS_KEY_STATE_MODE, g_config.mode);
        g_prefs.putFloat(NVS_KEY_STATE_CVOL, g_cycleStartVolumeMl);
        Serial.println("[NVS] Checkpoint saved.");
    }
}

void clearRuntimeState() {
    // We only set Active to false, no need to wipe values
    g_prefs.putBool(NVS_KEY_STATE_ACTIVE, false);
    Serial.println("[NVS] Checkpoint cleared (Clean Stop).");
}

