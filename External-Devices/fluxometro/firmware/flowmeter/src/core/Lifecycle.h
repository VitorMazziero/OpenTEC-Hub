void firmwareSetup() {
  pinMode(VALVE_FLOW_PIN, OUTPUT);
  digitalWrite(VALVE_FLOW_PIN, HIGH);
  valveFlowState = 1;

  Serial.begin(115200);
  // Espera um pouco para a serial estabilizar
  delay(1000);
  Serial.println("\n\n=== BOOT START ===");
  Serial.println("Firmware " FW_BUILD);

  // Seeded before anything can report telemetry. Never zero: the hub uses 0 as
  // "no boot id seen yet" and must not confuse it with a real session.
  bootSessionId = esp_random();
  if (bootSessionId == 0) bootSessionId = 1;
  Serial.printf("1. Boot session id: %lu\n", (unsigned long)bootSessionId);

  Serial.print("2. Init NVS/EEPROM... ");
  nvs_flash_init();
  if (EEPROM.begin(256)) Serial.println("OK");
  else Serial.println("FAILED");

  Serial.print("3. Init I2C... ");
  if (Wire.begin()) {
      Wire.setClock(100000);
      Serial.println("OK");
  } else Serial.println("FAILED");

  Serial.print("4. Init WiFi Mode... ");
  WiFi.setSleep(false); // Disable sleep for stability
  WiFi.persistent(false);
  WiFi.setAutoReconnect(false); // reconnects are scheduled below, on a fixed channel
  if (WiFi.mode(WIFI_AP_STA)) Serial.println("OK");
  else Serial.println("FAILED");

  Serial.print("5. Init SoftAP... ");
  IPAddress local_IP(192, 168, 10, 1);       // Mudamos para 10.1
  IPAddress gateway(192, 168, 10, 1);
  IPAddress subnet(255, 255, 255, 0);
  // Esta linha diz ao ESP32: "Seu IP interno é 10.1, não 4.1"
  WiFi.softAPConfig(local_IP, gateway, subnet);
  // The hub AP also uses channel 6. Keeping AP and STA on the same channel
  // prevents station reconnect attempts from retuning/dropping direct clients.
  if (WiFi.softAP(ap_ssid, ap_password, wifiRadioChannel, 0, 4)) {
    Serial.print("OK IP: ");
    Serial.println(WiFi.softAPIP());
  } else Serial.println("FAILED");

  // PINS
  pinMode(VALVE_FLOW_PIN, OUTPUT);
  pinMode(VALVE1_PIN, OUTPUT);
  pinMode(VALVE2_PIN, OUTPUT);
  digitalWrite(VALVE_FLOW_PIN, valveFlowState);
  digitalWrite(VALVE1_PIN, valve1State);
  digitalWrite(VALVE2_PIN, valve2State);
  pinMode(RECEIVER_LED, OUTPUT);
  digitalWrite(RECEIVER_LED, LOW);

  commandMutex = xSemaphoreCreateMutex();
  i2cMutex = xSemaphoreCreateMutex();
  hubHttpMutex = xSemaphoreCreateMutex();
  if (commandMutex == NULL || i2cMutex == NULL || hubHttpMutex == NULL) {
    Serial.println("FATAL: failed to create command/I2C/HTTP mutex");
    while (true) delay(1000);
  }

  Serial.print("6. Init ADS1115... ");
  if (ads.begin(0x48)) {
    ads.setGain(GAIN_TWOTHIRDS);
    ads.setDataRate(RATE_ADS1115_128SPS);
    Serial.println("OK");
  } else {
    Serial.println("FAILED (Check wiring!)");
    // Não travamos aqui com while(1) para permitir debug do resto
  }

  Serial.print("7. Init MCP4725... ");
  if (mcp.begin(0x60)) {
    Serial.println("OK");
  } else {
    Serial.println("FAILED (Check wiring!)");
  }

  loadParameters();
  writeFlowSetpointToDAC(flowSetpoint);

  ws.onEvent(onWsEvent);
  server.addHandler(&ws);
  setupOTA();
  server.begin();
  Serial.println("8. WebServer Started (OTA at http://192.168.10.1/update)");

  // CREATE TASKS
  Serial.println("9. Creating Tasks...");

  BaseType_t res1 = xTaskCreatePinnedToCore(sensorTask, "SensorTask", 4096, NULL, 2, NULL, 1);
  if (res1 == pdPASS) Serial.println("   - SensorTask Created OK");
  else Serial.println("   - SensorTask FAILED (Out of memory?)");

  BaseType_t res2 = xTaskCreatePinnedToCore(httpTask, "HubCommandTask", 4096, NULL, 2, NULL, 1);
  if (res2 == pdPASS) Serial.println("   - HubCommandTask Created OK");
  else Serial.println("   - HubCommandTask FAILED");

  // Reconnection has its own stack and never blocks the command executor.
  BaseType_t res3 = xTaskCreatePinnedToCore(wifiTask, "WiFiTask", 8192, NULL, 1, NULL, 1);
  if (res3 == pdPASS) Serial.println("   - WiFiTask Created OK");
  else Serial.println("   - WiFiTask FAILED");

  BaseType_t res4 = xTaskCreatePinnedToCore(telemetryTask, "HubTelemetryTask", 4096, NULL, 1, NULL, 1);
  if (res4 == pdPASS) Serial.println("   - HubTelemetryTask Created OK");
  else Serial.println("   - HubTelemetryTask FAILED");

  Serial.println("=== SETUP DONE ===\n");
}

static unsigned long lastLoop = 0;

void firmwareLoop() {
  unsigned long now = millis();

  // Direct USB commands must not wait for the telemetry broadcast.
  readSerialData();
  updateLEDBlinking();

  // Reboot is deferred here so the completion handler's HTTP response gets out
  // before the TCP stack disappears under it.
  if (otaRebootAtMs && (long)(now - otaRebootAtMs) >= 0) {
    Serial.println("[OTA] Rebooting into new firmware.");
    Serial.flush();
    ESP.restart();
  }
  if (otaInProgress && now - otaLastChunkMs > otaStallTimeoutMs) {
    Serial.printf("[OTA] No data for %lus after %u bytes. Hub link resumes; upload may still continue.\n",
                  otaStallTimeoutMs / 1000, (unsigned)Update.progress());
    otaStalled = true;
    otaInProgress = false;
  }

  if (now - lastControlUpdate >= controlInterval) {
    lastControlUpdate = now;
    xSemaphoreTake(commandMutex, portMAX_DELAY);

    // Slew the reference. Zero is applied at once; with dacHold a zero target
    // leaves rampedTarget where it was, so the restart resumes from that point.
    if (targetFlowSetpoint <= 0.0f) {
      if (!dacHold) rampedTarget = 0.0f;
    } else if (rampRate > 0.0f) {
      float step = rampRate * controlInterval / 1000.0f;
      if (rampedTarget < targetFlowSetpoint) rampedTarget = min(rampedTarget + step, targetFlowSetpoint);
      else if (rampedTarget > targetFlowSetpoint) rampedTarget = max(rampedTarget - step, targetFlowSetpoint);
    } else {
      rampedTarget = targetFlowSetpoint;
    }

    if (targetFlowSetpoint > 0.1f && valveFlowState == 0) {
      float error = rampedTarget - readFlowRate;
      flowFeedforward = feedforwardSetpoint(rampedTarget);

      // Deadband: below one DAC LSB the integral would only chase sensor noise.
      if (abs(error) > 0.01) {
        integralError += error * integralIntervalScale;

        // Anti-windup in output units: the integral may take the output anywhere
        // in 0..maxFlowRate relative to the corrected base, and no further.
        if (Ki_flow > 0.0f) {
          float iMin = -flowFeedforward / Ki_flow;
          float iMax = (maxFlowRate - flowFeedforward) / Ki_flow;
          integralError = constrain(integralError, iMin, iMax);
        } else {
          integralError = 0.0f;
        }

        float P_term = error * Kp_flow;
        float I_term = integralError * Ki_flow;

        // Output = setpoint corrigido (of the ramped reference) + PI trim.
        float newFlowSetpoint = constrain(flowFeedforward + P_term + I_term, 0.0f, maxFlowRate);

        // Commit only once the DAC actually took the value: a timed-out I2C write
        // used to leave flowSetpoint updated and the DAC stale, never retried.
        if (abs(newFlowSetpoint - flowSetpoint) > dacUpdateThreshold) {
          if (writeFlowSetpointToDAC(newFlowSetpoint)) flowSetpoint = newFlowSetpoint;
        }
        if (debugPI) {
          Serial.printf("[PI] t=%lu Tgt:%.3f Ref:%.3f FF:%.3f Act:%.3f Err:%.3f P:%.3f I:%.3f Out:%.3f\n",
                        now, targetFlowSetpoint, rampedTarget, flowFeedforward, readFlowRate,
                        error, P_term, I_term, flowSetpoint);
        }
      }
    } else if (targetFlowSetpoint > 0.1f) {
      // Live target but Valve Off asserted: no flow can exist and the error is not
      // the output's fault. Integral and DAC stay frozen until the valve opens.
    } else if (!dacHold) {
      // Legacy behaviour: a zero target drops the DAC to zero as well.
      flowFeedforward = 0.0f;
      if (flowSetpoint > 0) {
        if (writeFlowSetpointToDAC(0)) flowSetpoint = 0;
      }
    }
    xSemaphoreGive(commandMutex);
  }

  if (now - lastLoop >= 1000) {
    lastLoop += 1000;
    ws.cleanupClients();

    float seconds = now / 1000.0;
    float snapTarget, snapOutput, snapFF;
    uint8_t snapValve1, snapValve2, snapValveFlow;
    uint32_t snapAck;
    uint32_t snapDirectAck;
    uint32_t snapDirectSession;
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
    snapDirectAck = lastAppliedDirectCommandId;
    snapDirectSession = lastAppliedDirectSessionId;
    snapApplyMs = lastCommandApplyMs;
    snapSource = lastCommandSource;
    xSemaphoreGive(commandMutex);
    snprintf(outputMessage, sizeof(outputMessage),
             "{\"seconds\":%.3f,\"flow_voltage\":%.6f,\"flow_rate\":%.6f"
             ",\"flow_setpoint\":%.6f,\"flow_setpoint_corrected\":%.6f,\"flow_output\":%.6f,\"valve1State\":%d"
             ",\"valve2State\":%d,\"valveFlowState\":%d"
             ",\"ack_cmd_id\":%lu,\"ack_direct_session_id\":%lu"
             ",\"ack_direct_cmd_id\":%lu"
             ",\"last_apply_ms\":%lu,\"command_source\":\"%s\""
             ",\"Kp\":%.2f,\"Ki\":%.2f,\"ff_gain\":%.4f,\"ff_offset\":%.4f"
             ",\"ramp_rate\":%.2f,\"dac_hold\":%d,\"reconnect_wifi\":%d}",
             seconds, readFlowVoltage, readFlowRate,
             snapTarget, snapFF, snapOutput, snapValve1, snapValve2, snapValveFlow,
             (unsigned long)snapAck, (unsigned long)snapDirectSession,
             (unsigned long)snapDirectAck,
             snapApplyMs, snapSource.c_str(),
             Kp_flow, Ki_flow, ffGain, ffOffset, rampRate, dacHold ? 1 : 0, reconnect_Wifi);

    // Comentar para limpar o serial se estiver muito poluído
    Serial.println(outputMessage);
    ws.textAll(outputMessage);
  }
  yield();
}

