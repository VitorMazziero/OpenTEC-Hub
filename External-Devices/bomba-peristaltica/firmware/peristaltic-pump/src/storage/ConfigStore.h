uint32_t calculateCRC32(const uint8_t *data, size_t length) {
    uint32_t crc = 0;
    for (size_t i = 0; i < length; i++) crc += data[i];
    return crc;
}

void loadConfig() {
    size_t configSize = g_prefs.getBytesLength(NVS_KEY_CONFIG);
    if (configSize == sizeof(PumpConfig)) {
        if (g_prefs.getBytes(NVS_KEY_CONFIG, &g_config, sizeof(PumpConfig)) == sizeof(PumpConfig)) {
            uint32_t crc = calculateCRC32((uint8_t*)&g_config, sizeof(PumpConfig) - sizeof(uint32_t));
            if (crc == g_config.crc32) {
                Serial.println("Loaded valid config from NVS.");
                if (g_config.mode > 5) g_config.mode = 0; 
                return;
            }
        }
    }

    Serial.println("No valid config in NVS. Loading defaults.");
    g_config.mode = 0;
    g_config.init_t_min = 0.0f;
    g_config.final_t_min = 0.0f;
    
    g_config.lambda_const = 0.0f;
    g_config.lambda_linear = 0.0f;
    g_config.phi_linear = 0.0f;
    g_config.lambda_exp = 0.0f;
    g_config.phi_exp = 0.0f;
    
    for (int i = 0; i < NUM_POLY_COEFFS; i++) g_config.polyCoeffs[i] = 0.0;
    
    g_config.num_segments = 0;
    for (int i = 0; i < MAX_SEGMENTS; i++) {
        g_config.time_points[i] = 0.0f;
        g_config.flow_points[i] = 0.0f;
    }

    g_config.pid_kp = 0.5f;
    g_config.pid_ki = 0.05f;
    g_config.pid_kd = 0.001f;

    g_configDirty = true;
}

void saveConfig() {
    g_config.crc32 = calculateCRC32((uint8_t*)&g_config, sizeof(PumpConfig) - sizeof(uint32_t));
    if (g_prefs.putBytes(NVS_KEY_CONFIG, &g_config, sizeof(PumpConfig))) {
        Serial.println("Config saved to NVS.");
    }
    g_configDirty = false;
}

