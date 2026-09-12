void readSerialData() {
  static String inputBuffer;
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == '\n') {
      inputBuffer.trim();
      if (inputBuffer.length() > 0) {
        processReceivedData(inputBuffer, COMMAND_DIRECT);
      }
      inputBuffer = "";
    } else if (c != '\r') {
      if (inputBuffer.length() < 512) {
        inputBuffer += c;
      } else {
        inputBuffer = "";
        Serial.println("[Direct] Command discarded: input longer than 512 bytes");
      }
    }
  }
}

bool extractJsonUint32(const String &json, const char *key, uint32_t &value) {
  String token = "\"" + String(key) + "\"";
  int keyPos = json.indexOf(token);
  if (keyPos < 0) return false;
  int colonPos = json.indexOf(':', keyPos + token.length());
  if (colonPos < 0) return false;
  int start = colonPos + 1;
  while (start < json.length() && isspace(json.charAt(start))) start++;
  int end = start;
  while (end < json.length() && isDigit(json.charAt(end))) end++;
  if (end == start) return false;
  value = (uint32_t)strtoul(json.substring(start, end).c_str(), NULL, 10);
  return true;
}

bool processReceivedData(String data, CommandSource source) {
  data.trim();
  if (data.length() < 2) return false;
  if (data.startsWith("{") && data.endsWith("}")) {
    uint32_t hubCommandId = 0;
    uint32_t directSessionId = 0;
    uint32_t directCommandId = 0;
    bool hasHubCommandId = (source == COMMAND_HUB) &&
                           extractJsonUint32(data, "cmd_id", hubCommandId);
    bool hasDirectCommandId = (source == COMMAND_DIRECT) &&
                              extractJsonUint32(data, "direct_cmd_id", directCommandId);
    bool hasDirectSessionId = (source == COMMAND_DIRECT) &&
                              extractJsonUint32(data, "direct_session_id", directSessionId);

    if (hasHubCommandId) {
      xSemaphoreTake(commandMutex, portMAX_DELAY);
      bool duplicate = (hubCommandId == lastAppliedHubCommandId);
      xSemaphoreGive(commandMutex);
      if (duplicate) {
        // The hub retries until telemetry carries the acknowledgement. A local
        // command issued after this hub command must not be overwritten here.
        return true;
      }
    }

    if (hasDirectCommandId && hasDirectSessionId) {
      xSemaphoreTake(commandMutex, portMAX_DELAY);
      bool sameSession = (directSessionId == lastAppliedDirectSessionId);
      bool duplicateOrStale = sameSession &&
                              ((int32_t)(directCommandId - lastAppliedDirectCommandId) <= 0);
      xSemaphoreGive(commandMutex);
      if (duplicateOrStale) {
        // Direct-app retry after a delayed acknowledgement: acknowledge it,
        // but never actuate the same or an older command twice.
        return true;
      }
    }

    xSemaphoreTake(commandMutex, portMAX_DELAY);
    data = data.substring(1, data.length() - 1);
    int start = 0;
    bool calParamsUpdated = false;
    bool lowQuadraticUpdated = false;
    bool lowHigherOrderUpdated = false;
    bool recognizedCommand = false;
    while (start < data.length()) {
      int colonIndex = data.indexOf(':', start);
      int commaIndex = data.indexOf(',', start);
      if (colonIndex == -1) break;
      if (commaIndex == -1) commaIndex = data.length();

      if (colonIndex < commaIndex) {
        String key = data.substring(start, colonIndex);
        String value = data.substring(colonIndex + 1, commaIndex);
        key.trim();
        value.trim();

        if (key.startsWith("\"") && key.endsWith("\""))
          key = key.substring(1, key.length() - 1);

        if (key == "v_Flow" || key == "valveFlow") {
          valveFlowState = value.toInt() != 0;
          digitalWrite(VALVE_FLOW_PIN, valveFlowState);
          recognizedCommand = true;
        }
        else if (key == "v1" || key == "valve_1") {
          valve1State = value.toInt() != 0;
          digitalWrite(VALVE1_PIN, valve1State);
          recognizedCommand = true;
        }
        else if (key == "v2" || key == "valve_2") {
          valve2State = value.toInt() != 0;
          digitalWrite(VALVE2_PIN, valve2State);
          recognizedCommand = true;
        }

        else if (key == "reconnect_wifi") {
           reconnect_Wifi = (value.toInt() == 1);
           Serial.printf("Wifi Reconnect Logic set to: %s\n", reconnect_Wifi ? "TRUE" : "FALSE");
           recognizedCommand = true;
        }
        else if (key == "debug_pi") {
           debugPI = (value.toInt() == 1);
           Serial.printf("PI debug trace %s\n", debugPI ? "ON" : "OFF");
           recognizedCommand = true;
        }

        else if (key == "flow_setpoint" || key == "flowSetpoint") {
            float newTarget = constrain(value.toFloat(), 0.0f, maxFlowRate);
            recognizedCommand = true;

            // [REQ 4] Only update if change is > 0.05 OR if we are turning it OFF (0)
            // We also allow if it was previously 0 (startup)
            if (fabs(newTarget - targetFlowSetpoint) > 0.001f || newTarget == 0.0f || targetFlowSetpoint == 0.0f) {

                targetFlowSetpoint = newTarget;

                // integralError is never reset: it is the learned gain error of the
                // MFC at this operating point and is the right starting point next time.
                if (targetFlowSetpoint == 0.0) {
                    // A zero target always closes the MFC valve itself (FMA-5400 pin 12).
                    // The hub sends v_Flow=1 alongside, but a direct command may not,
                    // and with dacHold the DAC is still live.
                    valveFlowState = 1;
                    digitalWrite(VALVE_FLOW_PIN, HIGH);
                    if (!dacHold) {
                        flowSetpoint = 0.0;
                        rampedTarget = 0.0f;
                        writeFlowSetpointToDAC(0.0);
                    }
                }

                Serial.printf("New Target Accepted: %.3f\n", targetFlowSetpoint);
            } else {
                Serial.printf("Target Update Ignored (Delta < 0.05): %.3f\n", newTarget);
            }
        }
        else if (key == "kp_flow") { Kp_flow = calParams.kp = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "ki_flow") { Ki_flow = calParams.ki = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "ff_gain") { ffGain = calParams.ff_gain = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "ff_offset") { ffOffset = calParams.ff_offset = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "ramp_rate") { rampRate = calParams.ramp_rate = max(0.0f, value.toFloat()); calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "dac_hold") {
           dacHold = value.toInt() != 0;
           calParams.dac_hold = dacHold ? 1.0f : 0.0f;
           calParamsUpdated = true; recognizedCommand = true;
           Serial.printf("DAC hold across zero setpoint: %s\n", dacHold ? "ON" : "OFF");
        }

        else if (key == "max_flow" || key == "maxFlow") {
          float requestedMax = value.toFloat();
          if (requestedMax > 0.01f) {
            maxFlowRate = requestedMax;
            targetFlowSetpoint = constrain(targetFlowSetpoint, 0.0f, maxFlowRate);
            recognizedCommand = true;
          }
        }
        else if (key == "a1") { a1 = calParams.a1 = value.toFloat(); lowHigherOrderUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "b1") { b1 = calParams.b1 = value.toFloat(); lowHigherOrderUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "k1") { k1 = calParams.k1 = value.toFloat(); lowQuadraticUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "f1") { f1 = calParams.f1 = value.toFloat(); lowQuadraticUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "c1") { c1 = calParams.c1 = value.toFloat(); lowQuadraticUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "k2") { k2 = calParams.k2 = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "f2") { f2 = calParams.f2 = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "c2") { c2 = calParams.c2 = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }

        start = commaIndex + 1;
      } else {
        break;
      }
    }
    // Backward-compatible calibration commands contain only k1/f1/c1 and mean
    // "quadratic". Explicit a1/b1 opt into the quartic low-range model.
    if (lowQuadraticUpdated && !lowHigherOrderUpdated) {
      a1 = calParams.a1 = 0.0f;
      b1 = calParams.b1 = 0.0f;
    }
    if (recognizedCommand) {
      lastCommandApplyMs = millis();
      lastCommandSource = (source == COMMAND_HUB) ? "hub" : "direct";
      if (hasHubCommandId) {
        lastAppliedHubCommandId = hubCommandId;
        Serial.printf("[HubCmd] Applied cmd_id=%lu\n", (unsigned long)hubCommandId);
      }
      if (hasDirectCommandId && hasDirectSessionId) {
        lastAppliedDirectSessionId = directSessionId;
        lastAppliedDirectCommandId = directCommandId;
        Serial.printf("[DirectCmd] Applied session=%lu direct_cmd_id=%lu\n",
                      (unsigned long)directSessionId, (unsigned long)directCommandId);
      }
    }
    if (calParamsUpdated) saveParameters();
    if (recognizedCommand) startLEDBlinking();
    xSemaphoreGive(commandMutex);
    return recognizedCommand;
  }
  return false;
}

// Returns false when the I2C bus could not be taken; the caller keeps its old
// flowSetpoint so the write is retried on the next control cycle.
