void firmwareSetup() {
  Serial.begin(115200);
  delay(50);
  Serial.print("--- Biomass Sensor Firmware v");
  Serial.print(FW_VERSION);
  Serial.println(" (Direct Control) ---");

  g_prefs.begin(NVS_NAMESPACE, false);

  g_hubEnabled = g_prefs.getBool(NVS_KEY_HUB_EN, true); // default ON = v2.5 behaviour
  g_bootId     = g_prefs.getUInt(NVS_KEY_BOOTID, 0) + 1;
  g_prefs.putUInt(NVS_KEY_BOOTID, g_bootId);
  g_emaAlpha   = g_prefs.getFloat(NVS_KEY_EMA, 0.8f);
  if (g_emaAlpha <= 0.0f || g_emaAlpha > 1.0f) g_emaAlpha = 0.8f;
  g_autoRange  = g_prefs.getBool(NVS_KEY_AUTO, true);

  // Load config and blanking data from storage
  loadConfig();
  loadBlankingData();

  Serial.print("Auto-ranging: ");
  Serial.print(g_autoRange ? "ON" : "OFF (manual)");
  Serial.print(" | EMA alpha: ");
  Serial.println(g_emaAlpha, 2);
  Serial.print("Boot ID: ");
  Serial.print(g_bootId);
  Serial.print(" | Hub mode: ");
  Serial.println(g_hubEnabled ? "ENABLED" : "DISABLED (direct only)");

  pinMode(LED_PWM_PIN, OUTPUT);
  analogWriteFrequency(LED_PWM_PIN, LEDC_FREQ_HZ);
  analogWriteResolution(LED_PWM_PIN, LEDC_RES_BITS);

  Serial.print("PWM configured: Pin ");
  Serial.print(LED_PWM_PIN);
  Serial.print(", Freq: ");
  Serial.print(LEDC_FREQ_HZ);
  Serial.print(" Hz, Res: ");
  Serial.print(LEDC_RES_BITS);
  Serial.println(" bits");

  pwmSetDutyPercent(0.0f); // Set initial duty to 0%

  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
  Wire.setClock(100000);
  delay(5);

  if (!vemlSetConfig(0)) {
    Serial.println("VEML7700 init warning.");
  }
  g_currentPwmIndex = 0;

  Serial.println("[NET] Setting mode to WIFI_AP_STA...");
  WiFi.mode(WIFI_AP_STA);

  Serial.printf("[NET] Configuring AP on subnet %s\n", apIP.toString().c_str());
  WiFi.softAPConfig(apIP, apGateway, apSubnet);
  if (WiFi.softAP(AP_SSID, "")) { // No password
    Serial.printf("[NET] AP Started: %s (IP: %s)\n", AP_SSID,
                  WiFi.softAPIP().toString().c_str());
  } else {
    Serial.println("[NET] AP Start Failed!");
  }

  // Configure and start Web Server
  server.on("/",             HTTP_GET,  handleRoot);
  server.on("/readData",     HTTP_GET,  handleReadData);
  server.on("/api/data",     HTTP_GET,  handleReadData);
  server.on("/api/status",   HTTP_GET,  handleStatus);
  server.on("/api/history",  HTTP_GET,  handleHistory);
  server.on("/api/blank",    HTTP_GET,  handleBlankTable);
  server.on("/api/command",  HTTP_POST, handleCommand);
  server.on("/command",      HTTP_POST, handleCommand); // v2.5 alias
  server.onNotFound(handleNotFound);
  server.begin();
  g_serverStarted = true;
  Serial.println("[NET] Web server started. UI at http://192.168.7.1/");

  g_wifiNextActionMs = 0;
  checkWifi();

  esp_task_wdt_config_t twdt_config = {
      .timeout_ms     = WDT_TIMEOUT_S * 1000,
      .idle_core_mask = 0,    // do not watch idle tasks
      .trigger_panic  = true  // panic on timeout
  };
  esp_err_t err = esp_task_wdt_init(&twdt_config);
  if (err != ESP_OK) {
    Serial.print("esp_task_wdt_init failed, code ");
    Serial.println((int)err);
  } else {
    Serial.println("Watchdog timer initialized.");
  }
  err = esp_task_wdt_add(NULL); // Add this task to WDT
  if (err != ESP_OK) {
    Serial.print("esp_task_wdt_add failed, code ");
    Serial.println((int)err);
  } else {
    g_wdtReady = true;
    Serial.println("Main task added to Watchdog.");
  }

  buildDataJson();
  Serial.println(g_lastDataJson);

  Serial.println("System IDLE.");
  Serial.println("Send JSON commands via Serial, POST /api/command, or Sensor Hub.");
  if (g_blankIsDone)
    Serial.println("e.g. {\"command\":\"start\"} or {\"start\":1}");
  else
    Serial.println("e.g. {\"command\":\"blank\"} or {\"blank\":1}");
}

void firmwareLoop() {
  esp_task_wdt_reset(); // Feed the watchdog

  server.handleClient(); // Handle incoming HTTP requests on our AP

  handleSerialInput(/*allowBlocking=*/false); // Commands from serial

  // Run any blocking command a handler parked for us. Doing it here, outside
  // both handleSerialInput() and handleClient(), is what lets blank/start
  // pump BOTH transports while they run.
  if (g_pendingJson.length() > 0) {
    String pending = g_pendingJson;
    g_pendingJson = "";
    processJsonCommand(pending, /*allowBlocking=*/true);
  }

  uint32_t now = millis();

  checkWifi();

  if (g_hubEnabled && now - lastHubPollMs >= HUB_POLL_PERIOD_MS) {
    lastHubPollMs = now;
    if (WiFi.status() == WL_CONNECTED) {
      pollHubForCommands();
    }
  }

  if (g_hubEnabled && g_state != MEASURING &&
      now - lastHubHeartbeatMs >= HUB_HEARTBEAT_PERIOD_MS) {
    if (WiFi.status() == WL_CONNECTED) {
      sendDataToHub();   // updates lastHubHeartbeatMs itself
    } else {
      lastHubHeartbeatMs = now;
    }
  }

  // Non-blocking LED test sweep (only runs in IDLE state)
  if (g_ledTestEnable && g_state == IDLE && now >= g_ledTestNextMs) {
    g_ledTestNextMs = now + g_ledTestPeriodMs;
    g_ledTestPct += g_ledTestDir * 2.0f; // 2 percent per step
    if (g_ledTestPct >= 100.0f) {
      g_ledTestPct = 100.0f;
      g_ledTestDir = -1;
    }
    if (g_ledTestPct <= 0.0f) {
      g_ledTestPct = 0.0f;
      g_ledTestDir = 1;
    }
    pwmSetDutyPercent(g_ledTestPct);
  }

  switch (g_state) {
    case IDLE:
      // In IDLE the LED stays off unless the operator is deliberately
      // holding it on (manual duty) or running the test sweep.
      if (!g_ledTestEnable && !g_manualLedOn && g_targetPct > 0.0f) {
        pwmSetDutyPercent(0.0f);
      }
      break;
    case BLANKING:
    case SEARCHING:
      // Event-driven (blocking) states; they return to IDLE or MEASURING
      // when complete and service the web server while they run.
      break;

    case MEASURING:
      if (now >= g_nextReadTime) {
        runMeasurementLoop(); // This performs one pulsed read
      }
      break;
  }
}
