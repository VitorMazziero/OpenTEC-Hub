void checkAndRecoverState() {
    bool isActive = g_prefs.getBool(NVS_KEY_STATE_ACTIVE, false);
    
    if (isActive) {
        int savedMode = g_prefs.getInt(NVS_KEY_STATE_MODE, 0);
        float savedVol = g_prefs.getFloat(NVS_KEY_STATE_VOL, 0.0f);
        float savedTime = g_prefs.getFloat(NVS_KEY_STATE_TIME, 0.0f);
        float savedCycleVol = g_prefs.getFloat(NVS_KEY_STATE_CVOL, 0.0f);

        Serial.println(">>> DETECTED UNEXPECTED RESET! RECOVERING STATE <<<");
        Serial.printf("Recovering: Mode %d at %.2f min with %.2f mL\n", savedMode, savedTime, savedVol);

        // The session counter was already restored from s_sess (newer than this 60 s
        // checkpoint). s_vol is only the fallback for a node upgraded from 3.12.
        if (!g_prefs.isKey(NVS_KEY_SESSION_VOL)) volSet(savedVol);
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
        Serial.printf("[BOOT] Clean start (No active state found); session volume %.3f mL kept.\n",
                      (double)volRead());
        resetControllerState();
        g_opState = OP_IDLE;
    }
}

void saveRuntimeState() {
    const float vol = volRead();

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


// ---- Contador de sessao (3.12.1) --------------------------------------------------
// Gravado em qualquer estado, nao so com perfil rodando: antes, todo reinicio fora de
// perfil zerava o volume, e o Hub e o app viam um degrau falso (o gas proporcional caia
// junto). Limite de frequencia: variacao >= 0,05 mL e >= 30 s desde a ultima gravacao;
// as transicoes (stop, fim de perfil, reset_volume, fim da dosagem, OTA) forcam.
static const float         SESSION_SAVE_MIN_DELTA_ML = 0.05f;
static const unsigned long SESSION_SAVE_MIN_MS       = 30000;
static float               g_sessionSavedMl          = 0.0f;
static unsigned long       g_sessionSavedAtMs        = 0;

void sessionVolumeRestore() {
    if (g_prefs.isKey(NVS_KEY_SESSION_VOL)) {
        const float saved = g_prefs.getFloat(NVS_KEY_SESSION_VOL, 0.0f);
        volSet(isfinite(saved) ? saved : 0.0f);
        Serial.printf("[NVS] Session volume restored: %.3f mL\n", (double)volRead());
    }
    g_sessionSavedMl = volRead();
    g_sessionSavedAtMs = millis();
}

void sessionVolumeSave(bool force) {
    const float vol = volRead();
    const unsigned long now = millis();
    if (!force) {
        if (fabsf(vol - g_sessionSavedMl) < SESSION_SAVE_MIN_DELTA_ML) return;
        if (now - g_sessionSavedAtMs < SESSION_SAVE_MIN_MS) return;
    }
    g_prefs.putFloat(NVS_KEY_SESSION_VOL, vol);
    g_sessionSavedMl = vol;
    g_sessionSavedAtMs = now;
}
