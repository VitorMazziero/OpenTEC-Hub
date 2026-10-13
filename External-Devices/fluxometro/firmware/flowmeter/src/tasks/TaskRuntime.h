void sensorTask(void *parameter) {
  Serial.println("[SensorTask] Started running.");
  for (;;) {
    readAndProcessADC();          // ~130 ms of conversions
    vTaskDelay(pdMS_TO_TICKS(10)); // yield; ~7 samples/s overall
  }
}

void httpTask(void *parameter) {
  Serial.println("[HubCommandTask] Started running at 2 Hz.");
  static HTTPClient httpCmd;
  httpCmd.setReuse(true);
  uint8_t commandFailures = 0;

  for (;;) {
    unsigned long now = millis();
    if (!otaInProgress && WiFi.status() == WL_CONNECTED) {
      // This task remains command-only, but shares the HTTP bus with telemetry
      // so two HTTPClient instances never drive the Wi-Fi stack concurrently.
      unsigned long currentCmdInterval = commandPollInterval;
      if (commandFailures > 0) {
        uint8_t shift = (commandFailures > 4) ? 4 : commandFailures;
        currentCmdInterval = min(commandPollInterval * (1UL << shift), MAX_HUB_BACKOFF_MS);
      }
      if (now - lastCommandPollTime >= currentCmdInterval) {
        if (xSemaphoreTake(hubHttpMutex, pdMS_TO_TICKS(100)) == pdTRUE) {
          if (otaInProgress || WiFi.status() != WL_CONNECTED) {
            xSemaphoreGive(hubHttpMutex);
            continue;
          }
          // A busy bus is not an HTTP attempt. Retry after the short task yield
          // instead of losing a complete polling period on every collision.
          lastCommandPollTime = millis();
          char cmdUrl[128];
          snprintf(cmdUrl, sizeof(cmdUrl), "%s/flowCommand", sensorHubURL.c_str());
          httpCmd.begin(cmdUrl);
          httpCmd.setConnectTimeout(hubConnectTimeoutMs);
          httpCmd.setTimeout(hubRequestTimeoutMs);
          int cmdCode = httpCmd.GET();
          if (cmdCode > 0) hubLastResponseMs.store(millis());
          String commandPayload;
          if (cmdCode == 200) {
            if (commandFailures > 0) {
              Serial.printf("[HubCommandTask] Link recovered after %u failed request(s).\n", commandFailures);
            }
            commandFailures = 0;
            commandPayload = httpCmd.getString();
            commandPayload.trim();
          } else {
            if (commandFailures < 255) {
              commandFailures++;
            }
            if (commandFailures == 1 || commandFailures % 10 == 0) {
              Serial.printf("[HubCommandTask] /flowCommand failed: HTTP %d (consecutive=%u).\n",
                            cmdCode, commandFailures);
            }
            httpCmd.setReuse(false); // discard a failed/partial response
          }
          httpCmd.end();
          httpCmd.setReuse(true);
          hubCommandFailures.store(commandFailures);
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
  uint16_t transportFailures = 0;

  for (;;) {
    unsigned long now = millis();
    unsigned long currentTelemetryInterval = telemetryInterval;
    if (telemetryFailures > 0) {
      uint8_t shift = (telemetryFailures > 4) ? 4 : telemetryFailures;
      currentTelemetryInterval = min(telemetryInterval * (1UL << shift), MAX_TELEMETRY_BACKOFF_MS);
    }

    static unsigned long lastHelloCheckMs = 0;
    static bool hubAnnounced = false;
    if (telemetryFailures >= telemetryFailuresBeforeRelink) {
      hubAnnounced = false;
    }
    if (!otaInProgress && WiFi.status() == WL_CONNECTED &&
        now - lastHelloCheckMs >= (hubAnnounced ? 30000UL : 5000UL) &&
        now - lastHTTPDataTime < currentTelemetryInterval) {
      char helloUrl[140];
      snprintf(helloUrl, sizeof(helloUrl), "%s/nodeHello?dev=flowmeter&ver=%s&mac=%s",
               sensorHubURL.c_str(), FW_VERSION, WiFi.macAddress().c_str());
      if (xSemaphoreTake(hubHttpMutex, pdMS_TO_TICKS(100)) == pdTRUE) {
        if (otaInProgress || WiFi.status() != WL_CONNECTED) {
          xSemaphoreGive(hubHttpMutex);
          continue;
        }
        lastHelloCheckMs = millis();
        static HTTPClient httpHello;
        httpHello.begin(helloUrl);
        httpHello.setConnectTimeout(hubConnectTimeoutMs);
        httpHello.setTimeout(hubRequestTimeoutMs);
        int helloCode = httpHello.GET();
        if (helloCode > 0) hubLastResponseMs.store(millis());
        // This low-rate request uses a fresh socket; no unread response body
        // may leak into the next announcement.
        httpHello.setReuse(false);
        httpHello.end();
        xSemaphoreGive(hubHttpMutex);
        if (helloCode == 200) {
          hubAnnounced = true;
          Serial.printf("[HubTelemetryTask] Hello registrado com sucesso (%d)\n", helloCode);
        } else {
          Serial.printf("[HubTelemetryTask] Hello falhou (%d)\n", helloCode);
        }
      }
    }

    now = millis(); // an announcement may have waited for the HTTP timeout
    if (!otaInProgress && WiFi.status() == WL_CONNECTED && now - lastHTTPDataTime >= currentTelemetryInterval) {

      float snapTarget, snapOutput, snapFF, snapTransitionV;
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
      snapTransitionV = flowTransitionVoltage;
      xSemaphoreGive(commandMutex);

      uint8_t hwStatus = (adsHealthy ? 1 : 0) | (dacHealthy ? 2 : 0) | (!hardwareFaultLatched ? 4 : 0);
      float reportedFlowRate = hardwareFaultLatched ? -1.0f : readFlowRate;

      char url[640];
      snprintf(url, sizeof(url),
               "%s/flowData?seconds=%.3f&flow_voltage=%.6f&flow_rate=%.6f"
               "&flow_setpoint=%.6f&flow_setpoint_corrected=%.6f&flow_output=%.6f"
               "&ff_gain=%.4f&ff_offset=%.4f&valve1State=%u&valve2State=%u"
               "&valveFlowState=%u&ack_cmd_id=%lu&last_apply_ms=%lu"
               "&command_source=%s&boot_id=%lu&reconnect_wifi=%d"
               "&kp=%.4f&ki=%.4f&ramp=%.3f&transition_v=%.4f&cal_crc=%08X&hw_status=%u",
               sensorHubURL.c_str(),
               now / 1000.0,
               readFlowVoltage,
               reportedFlowRate,
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
               reconnect_Wifi ? 1 : 0,
               Kp_flow,
               Ki_flow,
               rampRate,
               snapTransitionV,
               currentCalCrc,
               hwStatus);
      if (xSemaphoreTake(hubHttpMutex, pdMS_TO_TICKS(100)) == pdTRUE) {
        if (otaInProgress || WiFi.status() != WL_CONNECTED) {
          xSemaphoreGive(hubHttpMutex);
          continue;
        }
        lastHTTPDataTime = millis();
        http.begin(url);
        http.setConnectTimeout(hubConnectTimeoutMs);
        http.setTimeout(hubRequestTimeoutMs);
        int telemetryCode = http.GET();
        if (telemetryCode > 0) hubLastResponseMs.store(millis());
        // Consume the response before end(): keep-alive is safe only once all
        // response bytes have been read. Failed responses get a fresh socket.
        if (telemetryCode == 200) {
          http.getString();
        } else {
          http.setReuse(false);
        }
        http.end();
        http.setReuse(true);
        xSemaphoreGive(hubHttpMutex);

        if (telemetryCode == 200) {
          if (telemetryFailures > 0) {
            Serial.printf("[HubTelemetryTask] Link recovered after %u failed request(s).\n",
                          telemetryFailures);
          }
          telemetryFailures = 0;
          transportFailures = 0;
        } else {
          if (telemetryFailures < UINT16_MAX) telemetryFailures++;
          if (telemetryCode < 0) {
            if (transportFailures < UINT16_MAX) transportFailures++;
          } else {
            // HTTP 4xx/5xx proves a reachable server. Reassociation cannot fix
            // an invalid request or a server-side error.
            transportFailures = 0;
          }
          if (telemetryFailures == 1 || telemetryFailures % 10 == 0) {
            Serial.printf("[HubTelemetryTask] /flowData failed: HTTP %d (consecutive=%u).\n",
                          telemetryCode, telemetryFailures);
          }

          // Link watchdog. WL_CONNECTED only says the station is associated; it says
          // nothing about the path working. Without this the failure count just grew
          // forever and the flowmeter stayed silently mute until someone power-cycled it.
          if (transportFailures >= telemetryFailuresBeforeRelink &&
              millis() - hubLastResponseMs.load() >= hubSilenceBeforeRelinkMs) {
            if (xSemaphoreTake(hubHttpMutex, pdMS_TO_TICKS(100)) == pdTRUE) {
              // A successful command/hello may have arrived while waiting.
              // Never disconnect an OTA upload or an already disconnected STA.
              if (!otaInProgress && reconnect_Wifi && WiFi.status() == WL_CONNECTED &&
                  millis() - hubLastResponseMs.load() >= hubSilenceBeforeRelinkMs) {
                Serial.printf("[HubTelemetryTask] No HTTP response for %lu ms (transport fails=%u); forcing reassociation.\n",
                              millis() - hubLastResponseMs.load(), transportFailures);
                telemetryFailures = 0;
                transportFailures = 0;
                hubAnnounced = false;
                WiFi.disconnect(false, false);
              }
              xSemaphoreGive(hubHttpMutex);
            }
          }
        }
        hubTelemetryFailures.store(telemetryFailures);
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

    // Watchdog de 15 minutos para auto-restaurar reconnect_Wifi se desligado (F15)
    static unsigned long wifiDisabledSinceMs = 0;
    if (!reconnect_Wifi) {
      if (wifiDisabledSinceMs == 0) wifiDisabledSinceMs = now;
      if (now - wifiDisabledSinceMs > 900000UL) {
        Serial.println("[WIFI SAFETY] Auto-restaurando reconnect_Wifi = true apos timeout de 15 min!");
        reconnect_Wifi = true;
        wifiDisabledSinceMs = 0;
      }
    } else {
      wifiDisabledSinceMs = 0;
    }

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

