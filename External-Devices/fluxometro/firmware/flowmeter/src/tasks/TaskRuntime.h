void sensorTask(void *parameter) {
  Serial.println("[SensorTask] Started running.");
  for (;;) {
    readAndProcessADC();          // ~130 ms of conversions
    vTaskDelay(pdMS_TO_TICKS(10)); // yield; ~7 samples/s overall
  }
}

void httpTask(void *parameter) {
  Serial.println("[HubCommandTask] Started running at 4 Hz.");
  static HTTPClient httpCmd;
  httpCmd.setReuse(true);

  for (;;) {
    unsigned long now = millis();
    if (!otaInProgress && WiFi.status() == WL_CONNECTED) {
      // This task remains command-only, but shares the HTTP bus with telemetry
      // so two HTTPClient instances never drive the Wi-Fi stack concurrently.
      if (now - lastCommandPollTime >= commandPollInterval) {
        lastCommandPollTime = now;
        if (xSemaphoreTake(hubHttpMutex, pdMS_TO_TICKS(2000)) == pdTRUE) {
          char cmdUrl[128];
          snprintf(cmdUrl, sizeof(cmdUrl), "%s/flowCommand", sensorHubURL.c_str());
          httpCmd.begin(cmdUrl);
          httpCmd.setConnectTimeout(hubConnectTimeoutMs);
          httpCmd.setTimeout(hubRequestTimeoutMs);
          int cmdCode = httpCmd.GET();
          String commandPayload;
          if (cmdCode == 200) {
            commandPayload = httpCmd.getString();
            commandPayload.trim();
          }
          httpCmd.end();
          xSemaphoreGive(hubHttpMutex);

          // Parsed outside the bus mutex: applying a command touches the DAC and the
          // I2C mutex, and must never hold the HTTP bus while it does.
          if (commandPayload.length() > 2) {
            Serial.println("[HubCommandTask] Command received: " + commandPayload);
            processReceivedData(commandPayload, COMMAND_HUB);
          }
        }
      }

    }
    vTaskDelay(pdMS_TO_TICKS(20));
  }
}

void telemetryTask(void *parameter) {
  Serial.println("[HubTelemetryTask] Started running independently.");
  static HTTPClient http;
  http.setReuse(true);
  uint16_t telemetryFailures = 0;

  for (;;) {
    unsigned long now = millis();
    if (!otaInProgress && WiFi.status() == WL_CONNECTED && now - lastHTTPDataTime >= telemetryInterval) {
      lastHTTPDataTime = now;

      float snapTarget, snapOutput, snapFF;
      uint8_t snapValve1, snapValve2, snapValveFlow;
      uint32_t snapAck;
      unsigned long snapApplyMs;
      String snapSource;
      xSemaphoreTake(commandMutex, portMAX_DELAY);
      snapTarget = targetFlowSetpoint;
      snapOutput = flowSetpoint;
      snapFF = flowFeedforward;
      snapValve1 = valve1State;
      snapValve2 = valve2State;
      snapValveFlow = valveFlowState;
      snapAck = lastAppliedHubCommandId;
      snapApplyMs = lastCommandApplyMs;
      snapSource = lastCommandSource;
      xSemaphoreGive(commandMutex);

      char url[384];
      snprintf(url, sizeof(url),
               "%s/flowData?seconds=%.3f&flow_voltage=%.6f&flow_rate=%.6f"
               "&flow_setpoint=%.6f&flow_setpoint_corrected=%.6f&flow_output=%.6f"
               "&ff_gain=%.4f&ff_offset=%.4f&valve1State=%u&valve2State=%u"
               "&valveFlowState=%u&ack_cmd_id=%lu&last_apply_ms=%lu"
               "&command_source=%s&boot_id=%lu&reconnect_wifi=%d",
               sensorHubURL.c_str(),
               now / 1000.0,
               readFlowVoltage,
               readFlowRate,
               snapTarget,
               snapFF,
               snapOutput,
               ffGain,
               ffOffset,
               snapValve1,
               snapValve2,
               snapValveFlow,
               static_cast<unsigned long>(snapAck),
               snapApplyMs,
               snapSource.c_str(),
               static_cast<unsigned long>(bootSessionId),
               reconnect_Wifi ? 1 : 0);
      if (xSemaphoreTake(hubHttpMutex, pdMS_TO_TICKS(2000)) == pdTRUE) {
        http.begin(url);
        http.setConnectTimeout(hubConnectTimeoutMs);
        http.setTimeout(hubRequestTimeoutMs);
        int telemetryCode = http.GET();
        http.end();
        xSemaphoreGive(hubHttpMutex);

        if (telemetryCode == 200) {
          if (telemetryFailures > 0) {
            Serial.printf("[HubTelemetryTask] Link recovered after %u failed request(s).\n",
                          telemetryFailures);
          }
          telemetryFailures = 0;
        } else {
          telemetryFailures++;
          if (telemetryFailures == 1 || telemetryFailures % 10 == 0) {
            Serial.printf("[HubTelemetryTask] /flowData failed: HTTP %d (consecutive=%u).\n",
                          telemetryCode, telemetryFailures);
          }

          // Link watchdog. WL_CONNECTED only says the station is associated; it says
          // nothing about the path working. Without this the failure count just grew
          // forever and the flowmeter stayed silently mute until someone power-cycled it.
          if (telemetryFailures >= telemetryFailuresBeforeRelink) {
            Serial.println("[HubTelemetryTask] Link is associated but mute; forcing reassociation.");
            telemetryFailures = 0;
            if (xSemaphoreTake(hubHttpMutex, pdMS_TO_TICKS(2000)) == pdTRUE) {
              http.end();
              xSemaphoreGive(hubHttpMutex);
            }
            // wifiTask owns reconnection; dropping the association is enough to wake it.
            WiFi.disconnect(false, false);
          }
        }
      }
    }
    vTaskDelay(pdMS_TO_TICKS(20));
  }
}

// No broad WiFi scan is used here. ESP32 AP+STA has one physical radio; scans
// can stall the direct Flowmeter_AP/WebSocket link. Both known TECNAL hubs use
// channel 6, so association is attempted directly on that channel.
void wifiTask(void *parameter) {
  Serial.println("[WiFiTask] Started fixed-channel reconnect logic (no scans).");

  lastReconnectAttempt = 0;

  for (;;) {
    unsigned long now = millis();

    // No association attempts while an OTA image is streaming in over the AP.
    if (!reconnect_Wifi || otaInProgress) {
      wifiReconnectState = WF_IDLE;
      vTaskDelay(pdMS_TO_TICKS(500));
      continue;
    }

    if (WiFi.status() == WL_CONNECTED) {
      wifiReconnectState = WF_IDLE;
      failedAttemptsOnCurrentSSID = 0;
      lastReconnectAttempt = now;
      vTaskDelay(pdMS_TO_TICKS(500));
      continue;
    }

    if (now - lastReconnectAttempt < wifiReconnectInterval && wifiReconnectState == WF_IDLE) {
      vTaskDelay(pdMS_TO_TICKS(250));
      continue;
    }

    switch (wifiReconnectState) {
      case WF_IDLE:
        if (currentSSID == "") {
          currentSSID = knownHubSSIDs[nextHubIndex];
          currentPassword = currentSSID;
        }
        Serial.printf("[WiFiTask] Connecting to %s on channel %u.\n",
                      currentSSID.c_str(), wifiRadioChannel);
        WiFi.disconnect(false, false);
        WiFi.begin(currentSSID.c_str(), currentPassword.c_str(), wifiRadioChannel);
        wifiReconnectState = WF_CONNECTING;
        lastReconnectAttempt = now;
        break;

      case WF_CONNECTING:
        if (now - lastReconnectAttempt >= wifiConnectTimeout) {
          WiFi.disconnect(false, false);
          failedAttemptsOnCurrentSSID++;
          if (failedAttemptsOnCurrentSSID >= attemptsBeforeTryingOtherHub) {
            Serial.printf("[WiFiTask] %s failed %u times; trying the other known hub.\n",
                          currentSSID.c_str(), failedAttemptsOnCurrentSSID);
            failedAttemptsOnCurrentSSID = 0;
            nextHubIndex = (nextHubIndex + 1) % knownHubCount;
            currentSSID = "";
            currentPassword = "";
          } else {
            Serial.printf("[WiFiTask] %s timed out (%u/%u); retrying the same hub.\n",
                          currentSSID.c_str(), failedAttemptsOnCurrentSSID,
                          attemptsBeforeTryingOtherHub);
          }
          wifiReconnectState = WF_IDLE;
          lastReconnectAttempt = now;
        }
        break;
    }

    vTaskDelay(pdMS_TO_TICKS(100));
  }
}

