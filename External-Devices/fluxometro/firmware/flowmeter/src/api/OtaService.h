
// OTA routes. Both run on the AsyncTCP task: the upload callback only feeds
// Update, everything slow (the reboot) is handed to loop().
void setupOTA() {
  server.on("/update", HTTP_GET, [](AsyncWebServerRequest *request) {
    request->send(200, "text/html", otaPage);
  });

  server.on("/diag", HTTP_GET, [](AsyncWebServerRequest *request) {
    char json[320];
    snprintf(json, sizeof(json),
             "{\"device\":\"flowmeter\",\"version\":\"%s\",\"uptime_s\":%lu,"
             "\"free_heap\":%u,\"wifi_status\":%d,\"ssid\":\"%s\",\"rssi\":%d,"
             "\"ip\":\"%s\",\"mac\":\"%s\",\"hub_fail_streak\":%u,\"ota\":%s,"
             "\"flow_rate\":%.4f,\"flow_sp\":%.4f}",
             FW_VERSION,
             static_cast<unsigned long>(millis() / 1000),
             static_cast<unsigned int>(ESP.getFreeHeap()),
             WiFi.status(),
             WiFi.SSID().c_str(),
             WiFi.RSSI(),
             WiFi.localIP().toString().c_str(),
             WiFi.macAddress().c_str(),
             g_hubFailStreak,
             otaInProgress ? "true" : "false",
             readFlowRate,
             flowSetpoint);
    request->send(200, "application/json", json);
  });

  server.on("/status", HTTP_GET, [](AsyncWebServerRequest *request) {
    char json[320];
    snprintf(json, sizeof(json),
             "{\"device\":\"flowmeter\",\"version\":\"%s\",\"uptime_s\":%lu,"
             "\"free_heap\":%u,\"wifi_status\":%d,\"ssid\":\"%s\",\"rssi\":%d,"
             "\"ip\":\"%s\",\"mac\":\"%s\",\"hub_fail_streak\":%u,\"ota\":%s,"
             "\"flow_rate\":%.4f,\"flow_sp\":%.4f}",
             FW_VERSION,
             static_cast<unsigned long>(millis() / 1000),
             static_cast<unsigned int>(ESP.getFreeHeap()),
             WiFi.status(),
             WiFi.SSID().c_str(),
             WiFi.RSSI(),
             WiFi.localIP().toString().c_str(),
             WiFi.macAddress().c_str(),
             g_hubFailStreak,
             otaInProgress ? "true" : "false",
             readFlowRate,
             flowSetpoint);
    request->send(200, "application/json", json);
  });

  // F11: Endpoint dedicado para leitura e auditoria de coeficientes em EEPROM
  server.on("/calibration", HTTP_GET, [](AsyncWebServerRequest *request) {
    char buf[448];
    snprintf(buf, sizeof(buf),
      "{\"a1\":%.6e,\"b1\":%.6e,\"k1\":%.6e,\"f1\":%.6e,\"c1\":%.6e,"
      "\"k2\":%.6e,\"f2\":%.6e,\"c2\":%.6e,\"transition_v\":%.4f,"
      "\"max_flow\":%.2f,\"crc\":\"%08X\"}",
      calParams.a1, calParams.b1, calParams.k1, calParams.f1, calParams.c1,
      calParams.k2, calParams.f2, calParams.c2, calParams.transition_v,
      maxFlowRate, currentCalCrc);
    request->send(200, "application/json", buf);
  });

  server.on("/update", HTTP_POST,
    // Completion: runs once, after the final upload chunk.
    [](AsyncWebServerRequest *request) {
      String msg;
      bool ok = false;
      if (otaRejectReason.length()) {
        msg = "Rejected: " + otaRejectReason;
      } else if (Update.hasError()) {
        msg = "Flash failed: " + String(Update.errorString()) + ". Running firmware untouched.";
      } else if (!Update.isFinished()) {
        msg = "Upload incomplete. Running firmware untouched.";
      } else {
        ok = true;
        msg = "OK: Firmware gravado e validado com sucesso! Reiniciando...";
      }
      Serial.println("[OTA] " + msg);
      AsyncWebServerResponse *response = request->beginResponse(ok ? 200 : 400, "text/plain", msg);
      response->addHeader("Connection", "close");
      request->send(response);
      otaInProgress = false;
      if (ok) otaRebootAtMs = millis() + 500;
    },
    // Chunk handler: called for every piece of the multipart body.
    [](AsyncWebServerRequest *request, String filename, size_t index, uint8_t *data, size_t len, bool final) {
      if (index == 0) {
        otaRejectReason = "";
        // Only the app image belongs in an OTA slot. The other exports start with the
        // same 0xE9 magic, so Update would accept them and the name is the only tell.
        if (filename.indexOf("merged") >= 0 || filename.indexOf("bootloader") >= 0 || filename.indexOf("partitions") >= 0) {
          otaRejectReason = "'" + filename + "' is not the app image, send the plain .ino.bin";
          Serial.println("[OTA] " + otaRejectReason);
          return;
        }
        Serial.printf("[OTA] Upload start: %s\n", filename.c_str());

        // --- F14: Safe Stop Atomico Protegido por commandMutex ---
        if (commandMutex != NULL && xSemaphoreTake(commandMutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
          valveFlowState = 1;
          digitalWrite(VALVE_FLOW_PIN, HIGH); // Corte geral fechado
          valve1State = 0;
          digitalWrite(VALVE1_PIN, LOW);       // Rota 1 fechada
          valve2State = 0;
          digitalWrite(VALVE2_PIN, LOW);       // Rota 2 fechada

          targetFlowSetpoint = 0.0f;
          rampedTarget = 0.0f;
          flowSetpoint = 0.0f;
          integralError = 0.0f;
          flowFeedforward = 0.0f;

          writeFlowSetpointToDAC(0.0f);        // Zero V no DAC

          xSemaphoreGive(commandMutex);
          Serial.println("[OTA SAFETY] Safe Stop executado com sucesso sob commandMutex!");
        } else {
          // Fallback de emergencia caso haja timeout no mutex
          digitalWrite(VALVE_FLOW_PIN, HIGH);
          digitalWrite(VALVE1_PIN, LOW);
          digitalWrite(VALVE2_PIN, LOW);
          writeFlowSetpointToDAC(0.0f);
          Serial.println("[OTA SAFETY] Timeout de mutex: pinos de hardware forcados para estado seguro!");
        }

        if (Update.isRunning()) Update.abort();   // safe here: same task that writes
        otaStalled = false;
        otaSafeLatch = false;
        otaNextProgressLog = 0;
        if (!Update.begin(UPDATE_SIZE_UNKNOWN)) Update.printError(Serial);
      }
      if (otaRejectReason.length()) return;
      // Stamped before the flag so loop() never pairs a set flag with a stale time.
      otaLastChunkMs = millis();
      otaInProgress = true;
      if (otaStalled) {
        Serial.printf("[OTA] Data resumed at %u bytes.\n", (unsigned)(index + len));
        otaStalled = false;
      }
      if (index + len >= otaNextProgressLog) {
        Serial.printf("[OTA] %u KB received, t=%lu ms\n", (unsigned)((index + len) / 1024), millis());
        otaNextProgressLog += 131072;
      }
      if (!Update.hasError() && len) {
        if (Update.write(data, len) != len) Update.printError(Serial);
      }
      if (final) {
        if (Update.end(true)) Serial.printf("[OTA] Image verified, %u bytes.\n", (unsigned)(index + len));
        else Update.printError(Serial);
      }
    });
}

