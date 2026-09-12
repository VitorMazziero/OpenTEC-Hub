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

static const char* findJsonValueStart(const char* json, const char* key) {
  if (!json || !key) return nullptr;
  const size_t klen = strlen(key);
  const char* p = json;
  while ((p = strstr(p, key)) != nullptr) {
    if (p > json && *(p - 1) == '"' && *(p + klen) == '"') {
      const char* afterQuote = p + klen + 1;
      while (*afterQuote && isspace(static_cast<unsigned char>(*afterQuote))) afterQuote++;
      if (*afterQuote == ':') {
        const char* valStart = afterQuote + 1;
        while (*valStart && isspace(static_cast<unsigned char>(*valStart))) valStart++;
        return valStart;
      }
    }
    p += klen;
  }
  return nullptr;
}

bool extractJsonUint32(const char *json, const char *key, uint32_t &value) {
  const char* valStart = findJsonValueStart(json, key);
  if (!valStart || *valStart == '"') return false;
  char* endPtr = nullptr;
  unsigned long val = strtoul(valStart, &endPtr, 10);
  if (endPtr == valStart) return false;
  value = static_cast<uint32_t>(val);
  return true;
}

inline bool extractJsonUint32(const String &json, const char *key, uint32_t &value) {
  return extractJsonUint32(json.c_str(), key, value);
}

bool processReceivedData(const String& rawData, CommandSource source) {
  if (rawData.length() < 2) return false;
  const char* str = rawData.c_str();
  while (*str && isspace(static_cast<unsigned char>(*str))) str++;
  const char* firstBrace = strchr(str, '{');
  const char* lastBrace = strrchr(str, '}');
  if (!firstBrace || !lastBrace || lastBrace <= firstBrace) return false;

  uint32_t hubCommandId = 0;
  uint32_t directSessionId = 0;
  uint32_t directCommandId = 0;
  bool hasHubCommandId = (source == COMMAND_HUB) &&
                         extractJsonUint32(str, "cmd_id", hubCommandId);
  bool hasDirectCommandId = (source == COMMAND_DIRECT) &&
                            extractJsonUint32(str, "direct_cmd_id", directCommandId);
  bool hasDirectSessionId = (source == COMMAND_DIRECT) &&
                            extractJsonUint32(str, "direct_session_id", directSessionId);

  if (hasHubCommandId) {
    xSemaphoreTake(commandMutex, portMAX_DELAY);
    bool duplicate = (hubCommandId == lastAppliedHubCommandId);
    xSemaphoreGive(commandMutex);
    if (duplicate) {
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
      return true;
    }
  }

  xSemaphoreTake(commandMutex, portMAX_DELAY);
  bool calParamsUpdated = false;
  bool lowQuadraticUpdated = false;
  bool lowHigherOrderUpdated = false;
  bool recognizedCommand = false;

  const char* cursor = firstBrace + 1;
  while (cursor < lastBrace) {
    while (cursor < lastBrace && (isspace(static_cast<unsigned char>(*cursor)) || *cursor == ',')) cursor++;
    if (cursor >= lastBrace) break;

    char keyBuf[32];
    size_t kLen = 0;
    if (*cursor == '"') {
      cursor++;
      while (cursor < lastBrace && *cursor != '"' && kLen < sizeof(keyBuf) - 1) {
        keyBuf[kLen++] = *cursor++;
      }
      if (cursor < lastBrace && *cursor == '"') cursor++;
    } else {
      while (cursor < lastBrace && *cursor != ':' && !isspace(static_cast<unsigned char>(*cursor)) && kLen < sizeof(keyBuf) - 1) {
        keyBuf[kLen++] = *cursor++;
      }
    }
    keyBuf[kLen] = '\0';

    while (cursor < lastBrace && *cursor != ':') cursor++;
    if (cursor >= lastBrace) break;
    cursor++; // skip ':'
    while (cursor < lastBrace && isspace(static_cast<unsigned char>(*cursor))) cursor++;

    char valBuf[32];
    size_t vLen = 0;
    if (*cursor == '"') {
      cursor++;
      while (cursor < lastBrace && *cursor != '"' && vLen < sizeof(valBuf) - 1) {
        valBuf[vLen++] = *cursor++;
      }
      if (cursor < lastBrace && *cursor == '"') cursor++;
    } else {
      while (cursor < lastBrace && *cursor != ',' && *cursor != '}' && !isspace(static_cast<unsigned char>(*cursor)) && vLen < sizeof(valBuf) - 1) {
        valBuf[vLen++] = *cursor++;
      }
    }
    valBuf[vLen] = '\0';

    if (strcmp(keyBuf, "v_Flow") == 0 || strcmp(keyBuf, "valveFlow") == 0) {
      valveFlowState = (atoi(valBuf) != 0);
      digitalWrite(VALVE_FLOW_PIN, valveFlowState);
      recognizedCommand = true;
    }
    else if (strcmp(keyBuf, "v1") == 0 || strcmp(keyBuf, "valve_1") == 0) {
      valve1State = (atoi(valBuf) != 0);
      digitalWrite(VALVE1_PIN, valve1State);
      recognizedCommand = true;
    }
    else if (strcmp(keyBuf, "v2") == 0 || strcmp(keyBuf, "valve_2") == 0) {
      valve2State = (atoi(valBuf) != 0);
      digitalWrite(VALVE2_PIN, valve2State);
      recognizedCommand = true;
    }
    else if (strcmp(keyBuf, "reconnect_wifi") == 0) {
      reconnect_Wifi = (atoi(valBuf) == 1);
      Serial.printf("Wifi Reconnect Logic set to: %s\n", reconnect_Wifi ? "TRUE" : "FALSE");
      recognizedCommand = true;
    }
    else if (strcmp(keyBuf, "debug_pi") == 0) {
      debugPI = (atoi(valBuf) == 1);
      Serial.printf("PI debug trace %s\n", debugPI ? "ON" : "OFF");
      recognizedCommand = true;
    }
    else if (strcmp(keyBuf, "flow_setpoint") == 0 || strcmp(keyBuf, "flowSetpoint") == 0) {
      float newTarget = constrain(strtof(valBuf, nullptr), 0.0f, maxFlowRate);
      recognizedCommand = true;

      if (fabs(newTarget - targetFlowSetpoint) > 0.001f || newTarget == 0.0f || targetFlowSetpoint == 0.0f) {
        targetFlowSetpoint = newTarget;
        if (targetFlowSetpoint == 0.0f) {
          valveFlowState = 1;
          digitalWrite(VALVE_FLOW_PIN, HIGH);
          if (!dacHold) {
            flowSetpoint = 0.0f;
            rampedTarget = 0.0f;
            writeFlowSetpointToDAC(0.0f);
          }
        }
        Serial.printf("New Target Accepted: %.3f\n", targetFlowSetpoint);
      } else {
        Serial.printf("Target Update Ignored (Delta < 0.05): %.3f\n", newTarget);
      }
    }
    else if (strcmp(keyBuf, "kp_flow") == 0) { Kp_flow = calParams.kp = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "ki_flow") == 0) { Ki_flow = calParams.ki = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "ff_gain") == 0) { ffGain = calParams.ff_gain = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "ff_offset") == 0) { ffOffset = calParams.ff_offset = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "ramp_rate") == 0) { rampRate = calParams.ramp_rate = max(0.0f, strtof(valBuf, nullptr)); calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "dac_hold") == 0) {
      dacHold = (atoi(valBuf) != 0);
      calParams.dac_hold = dacHold ? 1.0f : 0.0f;
      calParamsUpdated = true; recognizedCommand = true;
      Serial.printf("DAC hold across zero setpoint: %s\n", dacHold ? "ON" : "OFF");
    }
    else if (strcmp(keyBuf, "max_flow") == 0 || strcmp(keyBuf, "maxFlow") == 0) {
      float requestedMax = strtof(valBuf, nullptr);
      if (requestedMax > 0.01f) {
        maxFlowRate = requestedMax;
        targetFlowSetpoint = constrain(targetFlowSetpoint, 0.0f, maxFlowRate);
        recognizedCommand = true;
      }
    }
    else if (strcmp(keyBuf, "a1") == 0) { a1 = calParams.a1 = strtof(valBuf, nullptr); lowHigherOrderUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "b1") == 0) { b1 = calParams.b1 = strtof(valBuf, nullptr); lowHigherOrderUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "k1") == 0) { k1 = calParams.k1 = strtof(valBuf, nullptr); lowQuadraticUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "f1") == 0) { f1 = calParams.f1 = strtof(valBuf, nullptr); lowQuadraticUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "c1") == 0) { c1 = calParams.c1 = strtof(valBuf, nullptr); lowQuadraticUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "k2") == 0) { k2 = calParams.k2 = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "f2") == 0) { f2 = calParams.f2 = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }
    else if (strcmp(keyBuf, "c2") == 0) { c2 = calParams.c2 = strtof(valBuf, nullptr); calParamsUpdated = true; recognizedCommand = true; }

    while (cursor < lastBrace && *cursor != ',') cursor++;
    if (cursor < lastBrace && *cursor == ',') cursor++;
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

// Returns false when the I2C bus could not be taken; the caller keeps its old
// flowSetpoint so the write is retried on the next control cycle.
