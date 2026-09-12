void firmwareSetup() {
    Serial.begin(115200);
    delay(100);
    Serial.println("--- Peristaltic Pump Controller v3.8 (Robust Recovery) ---");

    g_prefs.begin(NVS_NAMESPACE, false);
    loadConfig();

    pinMode(R_EN_PIN, OUTPUT);
    pinMode(L_EN_PIN, OUTPUT);
    digitalWrite(R_EN_PIN, HIGH);
    digitalWrite(L_EN_PIN, HIGH);

    pinMode(SENSOR_ENABLE_BUTTON_PIN, INPUT_PULLUP);
    pinMode(SENSOR_STATUS_LED_PIN, OUTPUT);

    ledcAttach(R_PWM_PIN, pwmFreqHz, PWM_RES_BITS);
    ledcAttach(L_PWM_PIN, pwmFreqHz, PWM_RES_BITS);
    ledcWrite(R_PWM_PIN, 0);
    ledcWrite(L_PWM_PIN, 0);

    setupADC();
    setupSensorPin();

    Serial.println("[NET] Setting mode to WIFI_AP_STA...");
    WiFi.mode(WIFI_AP_STA);

    Serial.printf("[NET] Configuring AP on subnet %s (Channel 6)\n", apIP.toString().c_str());
    WiFi.softAPConfig(apIP, apGateway, apSubnet);
    if (WiFi.softAP(AP_SSID, "", 6)) {
        Serial.printf("[NET] AP Started: %s (IP: %s) on channel 6\n", AP_SSID, WiFi.softAPIP().toString().c_str());
    } else {
        Serial.println("[NET] AP Start Failed!");
    }

    server.on("/readData", HTTP_GET, handleReadData);
    server.on("/diag", HTTP_GET, handleDiag);
    server.on("/status", HTTP_GET, handleDiag);
    server.on("/command", HTTP_POST, handleCommand);
    server.on("/", HTTP_GET, handleReadData);
    server.on("/update", HTTP_GET, handleOtaPage);
    server.on("/update", HTTP_POST, handleOtaUploadDone, handleOtaChunk);
    server.onNotFound(handleNotFound);
    server.begin();
    Serial.println("[NET] Web server started.");

    // Configure WDT for Core 1 (where loop() runs)
    esp_task_wdt_config_t twdt_config = {
        .timeout_ms     = WDT_TIMEOUT_S * 1000,
        .idle_core_mask = (1 << 0), // Watchdog can ignore Core 0
        .trigger_panic  = true
    };
    esp_task_wdt_init(&twdt_config);
    esp_task_wdt_add(NULL); // Add loopTask to WDT
    Serial.println("Watchdog timer initialized for Core 1.");

    // Pin the real-time PWM task to Core 0
    xTaskCreatePinnedToCore(pwmTask, "Pump-PWM-Vol", 4096, nullptr, 1, &pwmTaskHandle, 0);
    Serial.println("Core 0 (PWM & Volume Task) started.");

    checkAndRecoverState();

    g_wifiNextActionMs = millis();
}

void setupADC() {
    analogReadResolution(ADC_RES);
    analogSetPinAttenuation(POT_INT_PIN,  ADC_11db);
    analogSetPinAttenuation(POT_GAIN_PIN, ADC_11db);
}

void setupSensorPin() {
    pinMode(SENSOR_PIN, INPUT_PULLUP);
}

void firmwareLoop() {
    static uint32_t lastDbg = 0;

    esp_task_wdt_reset(); // Reset Core 1 WDT

    server.handleClient();

    uint32_t now = millis();

    if (g_otaRebootAtMs > 0 && now >= g_otaRebootAtMs) {
        Serial.println("[OTA] Reiniciando no novo firmware...");
        delay(100);
        ESP.restart();
    }

    if (g_otaInProgress) {
        if (now - g_otaLastChunkMs > OTA_STALL_TIMEOUT_MS) {
            Serial.println("[OTA] Watchdog disparado: upload estagnou.");
            Update.abort();
            g_otaInProgress = false;
        }
        delay(1);
        return;
    }

    handleSerialInput();

    checkWifi();

    static unsigned long lastHelloCheckMs = 0;
    if (WiFi.status() == WL_CONNECTED && (!g_hubAnnounced || now - lastHelloCheckMs >= 30000)) {
        lastHelloCheckMs = now;
        sendHubHello();
    }
    
    unsigned long pollInterval = HUB_POLL_PERIOD_MS;
    if (g_hubFailStreak > 0) {
        uint8_t shift = (g_hubFailStreak > 4) ? 4 : g_hubFailStreak;
        pollInterval = min(HUB_POLL_PERIOD_MS * (1UL << shift), MAX_HUB_BACKOFF_MS);
    }
    if (WiFi.status() == WL_CONNECTED && (now - lastHubPollMs >= pollInterval)) {
        lastHubPollMs = now;
        pollHubForCommands();
    }
    
    if (g_configDirty) {
        saveConfig();
    }

    if (g_opState == OP_RUNNING && (now - g_lastStateSaveMs >= STATE_SAVE_INTERVAL_MS)) {
        saveRuntimeState();
        g_lastStateSaveMs = now;
    }

    if (!sensorButtonOverride) {
        sensorEnable = !digitalRead(SENSOR_ENABLE_BUTTON_PIN);
    }
    digitalWrite(SENSOR_STATUS_LED_PIN, sensorEnable ? HIGH : LOW);
    float potSpeed = 0.0f;
    if (!disablePot) potSpeed = calcPotSpeed();

    if (g_opState == OP_IDLE) {
        g_current_t_min = 0.0f;
    } else {
        // Calculate current time based on the trigger time calculated at start or recovery
        g_current_t_min = (millis() - g_opTriggerTimeMs) / 60000.0f;
    }

    updateOperationState();

    float requestedSpeed = 0.0f;
    if (g_opState == OP_RUNNING) {
        runOperationLogic();
        requestedSpeed = mlminToSpeedUnits(g_currentFlowRateMlMin);
        if (requestedSpeed < 0.0f) requestedSpeed = 0.0f;
    } else if (g_opState == OP_IDLE) {
        requestedSpeed = hasUsbSpeed ? usbSpeedSteps : potSpeed;
        g_currentFlowRateMlMin = speedUnitsToMlmin(requestedSpeed);
    }

    updateSensorGate();
    bool allowRun = true;
    if (sensorEnable && !sensorBypass) {
        allowRun = sensorWetState;
    }

    float finalSpeed = allowRun ? requestedSpeed : 0.0f;
    finalSpeed = constrain(finalSpeed, -V_MAX, V_MAX);

    unsigned long now_ms = millis();
    bool pidWantsOn = (allowRun && fabsf(finalSpeed) >= ENABLE_EPS);

    if (g_motorOnLatchTimeMs == 0) {
        if (pidWantsOn) {
            g_driverEnabled = true;
            g_cmdSpeed = finalSpeed;
            g_latchedSpeed = finalSpeed;
            g_motorOnLatchTimeMs = now_ms;
        } else {
            g_driverEnabled = false;
            g_cmdSpeed = finalSpeed;
            g_latchedSpeed = 0.0f;
        }
    } else {
        if (now_ms - g_motorOnLatchTimeMs >= MIN_MOTOR_ON_TIME_MS) {
            g_motorOnLatchTimeMs = 0;
            if (pidWantsOn) {
                g_driverEnabled = true;
                g_cmdSpeed = finalSpeed;
                g_latchedSpeed = 0.0f;
            } else {
                g_driverEnabled = false;
                g_cmdSpeed = finalSpeed;
                g_latchedSpeed = 0.0f;
            }
        } else {
            g_driverEnabled = true;
            g_cmdSpeed = g_latchedSpeed;
        }
    }
    
    now = millis();
    if (now - lastDbg >= DEBUG_INTERVAL_MS) {
        lastDbg = now;

        buildDataJson(false); 
        Serial.println(g_lastDataJson);

        if (DEBUG_ENABLE && g_opState != OP_IDLE) {
            float t_rel_dbg = g_current_t_min - g_config.init_t_min;
            if (t_rel_dbg < 0.0f) t_rel_dbg = 0.0f;
            Serial.printf("[DEBUG] OpState:%d Mode:%d TgtQ:%.2f ActV:%.2f TgtV:%.2f PIDSum:%.2f\n",
                (int)g_opState, g_config.mode, (double)g_currentFlowRateMlMin,
                (double)g_cumulativeVolumeMl, 
                (double)calculateTargetVolume(t_rel_dbg),
                (double)g_pid_error_sum);
        }
    }

    unsigned long pushInterval = DATA_PUSH_PERIOD_MS;
    if (g_hubFailStreak > 0) {
        uint8_t shift = (g_hubFailStreak > 4) ? 4 : g_hubFailStreak;
        pushInterval = min(DATA_PUSH_PERIOD_MS * (1UL << shift), MAX_HUB_BACKOFF_MS);
    }

    if (now - lastDataPushMs >= pushInterval) {
        lastDataPushMs = now;
        if (WiFi.status() == WL_CONNECTED) {
            sendDataToHub();
        }
    }
}

