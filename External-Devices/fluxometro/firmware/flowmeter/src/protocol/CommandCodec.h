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

// F08: Parser booleano robusto case-insensitive compativel com JSON ("true", "false", "True", "False", "1", "0")
static inline bool parseJsonBool(const char* str, bool* outVal) {
  if (!str || !outVal) return false;
  while (*str && isspace(static_cast<unsigned char>(*str))) str++;
  if (*str == '\0') return false;

  if (strcasecmp(str, "true") == 0 || strcmp(str, "1") == 0) {
    *outVal = true;
    return true;
  }
  if (strcasecmp(str, "false") == 0 || strcmp(str, "0") == 0) {
    *outVal = false;
    return true;
  }
  return false;
}

// F07: Parser e validador numerico estrito (rejeicao de NaN, Inf, caracteres espurios e fora de faixa)
static inline bool parseBoundedFloat(const char* str, float minVal, float maxVal, float* outVal) {
  if (!str || !outVal) return false;
  while (*str && isspace(static_cast<unsigned char>(*str))) str++;
  if (*str == '\0') return false;
  char* endPtr = nullptr;
  float val = strtof(str, &endPtr);
  if (endPtr == str) return false;
  while (*endPtr && isspace(static_cast<unsigned char>(*endPtr))) endPtr++;
  if (*endPtr != '\0') return false; // Rejeita trailing caracteres
  if (isnan(val) || isinf(val)) return false;
  if (val < minVal || val > maxVal) return false;
  *outVal = val;
  return true;
}

struct StagedCommands {
  bool hasVFlow = false;
  uint8_t stagedVFlow = 0;

  bool hasV1 = false;
  uint8_t stagedV1 = 0;

  bool hasV2 = false;
  uint8_t stagedV2 = 0;

  bool hasReconnectWifi = false;
  bool stagedReconnectWifi = false;

  bool hasDebugPi = false;
  bool stagedDebugPi = false;

  bool hasFlowSetpoint = false;
  float stagedFlowSetpoint = 0.0f;

  bool hasMaxFlow = false;
  float stagedMaxFlow = 0.0f;

  bool hasKp = false;
  float stagedKp = 0.0f;

  bool hasKi = false;
  float stagedKi = 0.0f;

  bool hasFfGain = false;
  float stagedFfGain = 0.0f;

  bool hasFfOffset = false;
  float stagedFfOffset = 0.0f;

  bool hasRampRate = false;
  float stagedRampRate = 0.0f;

  bool hasDacHold = false;
  bool stagedDacHold = false;

  bool hasA1 = false; float stagedA1 = 0.0f;
  bool hasB1 = false; float stagedB1 = 0.0f;
  bool hasK1 = false; float stagedK1 = 0.0f;
  bool hasF1 = false; float stagedF1 = 0.0f;
  bool hasC1 = false; float stagedC1 = 0.0f;
  bool hasK2 = false; float stagedK2 = 0.0f;
  bool hasF2 = false; float stagedF2 = 0.0f;
  bool hasC2 = false; float stagedC2 = 0.0f;

  bool frameHasErrors = false;
  bool recognizedAny = false;
};

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

  // =========================================================================
  // FASE 1: Parsing e Validacao em Staging Area (Sem efeitos colaterais em RAM)
  // =========================================================================
  StagedCommands staged;

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

    char valBuf[64];
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

    bool bVal = false;
    float fVal = 0.0f;

    if (strcmp(keyBuf, "v_Flow") == 0 || strcmp(keyBuf, "valveFlow") == 0) {
      if (parseJsonBool(valBuf, &bVal)) {
        staged.hasVFlow = true; staged.stagedVFlow = bVal ? 1 : 0; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] v_Flow booleano invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "v1") == 0 || strcmp(keyBuf, "valve_1") == 0) {
      if (parseJsonBool(valBuf, &bVal)) {
        staged.hasV1 = true; staged.stagedV1 = bVal ? 1 : 0; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] v1 booleano invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "v2") == 0 || strcmp(keyBuf, "valve_2") == 0) {
      if (parseJsonBool(valBuf, &bVal)) {
        staged.hasV2 = true; staged.stagedV2 = bVal ? 1 : 0; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] v2 booleano invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "reconnect_wifi") == 0) {
      if (parseJsonBool(valBuf, &bVal)) {
        staged.hasReconnectWifi = true; staged.stagedReconnectWifi = bVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] reconnect_wifi booleano invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "debug_pi") == 0) {
      if (parseJsonBool(valBuf, &bVal)) {
        staged.hasDebugPi = true; staged.stagedDebugPi = bVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] debug_pi booleano invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "flow_setpoint") == 0 || strcmp(keyBuf, "flowSetpoint") == 0) {
      if (parseBoundedFloat(valBuf, 0.0f, 500.0f, &fVal)) {
        staged.hasFlowSetpoint = true; staged.stagedFlowSetpoint = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] flow_setpoint fora de faixa ou invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "max_flow") == 0 || strcmp(keyBuf, "maxFlow") == 0) {
      if (parseBoundedFloat(valBuf, 0.1f, 500.0f, &fVal)) {
        staged.hasMaxFlow = true; staged.stagedMaxFlow = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] max_flow fora de faixa (0.1..500): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "kp_flow") == 0) {
      if (parseBoundedFloat(valBuf, 0.0f, 100.0f, &fVal)) {
        staged.hasKp = true; staged.stagedKp = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] kp_flow fora de faixa (0..100): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "ki_flow") == 0) {
      if (parseBoundedFloat(valBuf, 0.0f, 100.0f, &fVal)) {
        staged.hasKi = true; staged.stagedKi = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] ki_flow fora de faixa (0..100): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "ff_gain") == 0) {
      if (parseBoundedFloat(valBuf, 0.0f, 10.0f, &fVal)) {
        staged.hasFfGain = true; staged.stagedFfGain = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] ff_gain fora de faixa (0..10): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "ff_offset") == 0) {
      if (parseBoundedFloat(valBuf, -5.0f, 5.0f, &fVal)) {
        staged.hasFfOffset = true; staged.stagedFfOffset = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] ff_offset fora de faixa (-5..5): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "ramp_rate") == 0) {
      if (parseBoundedFloat(valBuf, 0.0f, 100.0f, &fVal)) {
        staged.hasRampRate = true; staged.stagedRampRate = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] ramp_rate fora de faixa (0..100): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "dac_hold") == 0) {
      if (parseJsonBool(valBuf, &bVal)) {
        staged.hasDacHold = true; staged.stagedDacHold = bVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] dac_hold booleano invalido: %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "a1") == 0) {
      if (parseBoundedFloat(valBuf, -1.0e7f, 1.0e7f, &fVal)) {
        staged.hasA1 = true; staged.stagedA1 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] a1 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "b1") == 0) {
      if (parseBoundedFloat(valBuf, -1.0e7f, 1.0e7f, &fVal)) {
        staged.hasB1 = true; staged.stagedB1 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] b1 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "k1") == 0) {
      if (parseBoundedFloat(valBuf, -1.0e7f, 1.0e7f, &fVal)) {
        staged.hasK1 = true; staged.stagedK1 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] k1 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "f1") == 0) {
      if (parseBoundedFloat(valBuf, -1.0e7f, 1.0e7f, &fVal)) {
        staged.hasF1 = true; staged.stagedF1 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] f1 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "c1") == 0) {
      if (parseBoundedFloat(valBuf, -1.0e7f, 1.0e7f, &fVal)) {
        staged.hasC1 = true; staged.stagedC1 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] c1 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "k2") == 0) {
      if (parseBoundedFloat(valBuf, -1.0e7f, 1.0e7f, &fVal)) {
        staged.hasK2 = true; staged.stagedK2 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] k2 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "f2") == 0) {
      if (parseBoundedFloat(valBuf, -1.0e7f, 1.0e7f, &fVal)) {
        staged.hasF2 = true; staged.stagedF2 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] f2 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }
    else if (strcmp(keyBuf, "c2") == 0) {
      if (parseBoundedFloat(valBuf, -1.0e7f, 1.0e7f, &fVal)) {
        staged.hasC2 = true; staged.stagedC2 = fVal; staged.recognizedAny = true;
      } else {
        Serial.printf("[REJECT] c2 fora de faixa (+/-1e7): %s\n", valBuf);
        staged.frameHasErrors = true;
      }
    }

    while (cursor < lastBrace && *cursor != ',') cursor++;
    if (cursor < lastBrace && *cursor == ',') cursor++;
  }

  // =========================================================================
  // FASE 2: Commit Atomico Transacional (Sob Protecao de Mutex)
  // =========================================================================
  if (staged.frameHasErrors || !staged.recognizedAny) {
    if (staged.frameHasErrors) {
      Serial.println("[REJECT] Quadro rejeitado por parametros invalidos ou fora de faixa; nenhuma alteracao aplicada.");
    }
    return false;
  }

  xSemaphoreTake(commandMutex, portMAX_DELAY);
  bool calParamsUpdated = false;

  // 1. Max flow (F09)
  if (staged.hasMaxFlow) {
    maxFlowRate = calParams.max_flow = staged.stagedMaxFlow;
    targetFlowSetpoint = constrain(targetFlowSetpoint, 0.0f, maxFlowRate);
    calParamsUpdated = true;
    Serial.printf("[CMD] Atualizado e persistido max_flow: %.2f L/min\n", maxFlowRate);
  }

  // 2. Valvulas (F05)
  uint8_t effectiveV1 = staged.hasV1 ? staged.stagedV1 : valve1State;
  uint8_t effectiveV2 = staged.hasV2 ? staged.stagedV2 : valve2State;
  uint8_t effectiveVFlow = staged.hasVFlow ? staged.stagedVFlow : valveFlowState;
  bool valveStateChanged = staged.hasV1 || staged.hasV2 || staged.hasVFlow;

  // F05: Se recebeu setpoint positivo e v_Flow NAO foi especificado no frame, abre v_Flow
  if (staged.hasFlowSetpoint && !staged.hasVFlow && staged.stagedFlowSetpoint > MIN_FLOW_CUTOFF_THRESHOLD) {
    effectiveVFlow = 0; // 0 = Aberto / Fluxo liberado
    valveStateChanged = true;
  }

  // 3. Setpoint de Fluxo (F04, F12, F14)
  if (staged.hasFlowSetpoint) {
    float newTarget = constrain(staged.stagedFlowSetpoint, 0.0f, maxFlowRate);

    // Bloqueio se hardware em falha ou se OTA safe latch estiver ativo
    if ((hardwareFaultLatched || !adsHealthy || !dacHealthy || otaSafeLatch) && newTarget > 0.0f) {
      Serial.println("[SAFETY] Setpoint rejeitado: falha de hardware ou OTA safe latch ativo!");
      newTarget = 0.0f;
    }

    if (newTarget <= MIN_FLOW_CUTOFF_THRESHOLD) {
      targetFlowSetpoint = 0.0f;
      effectiveVFlow = 1; // Corte mecanico acionado (GPIO 5 HIGH)
      valveStateChanged = true;
      if (!dacHold) {
        flowSetpoint = 0.0f;
        rampedTarget = 0.0f;
        integralError = 0.0f;
        flowFeedforward = 0.0f;
        writeFlowSetpointToDAC(0.0f);
      }
      Serial.printf("[CMD] Setpoint <= %.2f: Corte acionado (dacHold=%s)\n",
                    MIN_FLOW_CUTOFF_THRESHOLD, dacHold ? "ON" : "OFF");
    } else {
      targetFlowSetpoint = newTarget;
      Serial.printf("[CMD] Novo Setpoint Aceito: %.3f L/min\n", targetFlowSetpoint);
    }
  }

  // Aplicacao no hardware das valvulas
  if (valveStateChanged) {
    valve1State = effectiveV1;
    digitalWrite(VALVE1_PIN, valve1State ? HIGH : LOW);

    valve2State = effectiveV2;
    digitalWrite(VALVE2_PIN, valve2State ? HIGH : LOW);

    valveFlowState = effectiveVFlow;
    digitalWrite(VALVE_FLOW_PIN, valveFlowState ? HIGH : LOW);
  }

  // 4. Flags e parametros de controle
  if (staged.hasReconnectWifi) {
    reconnect_Wifi = staged.stagedReconnectWifi;
    Serial.printf("[CMD] Wifi Reconnect Logic set to: %s\n", reconnect_Wifi ? "TRUE" : "FALSE");
  }
  if (staged.hasDebugPi) {
    debugPI = staged.stagedDebugPi;
    Serial.printf("[CMD] PI debug trace %s\n", debugPI ? "ON" : "OFF");
  }
  if (staged.hasDacHold) {
    dacHold = staged.stagedDacHold;
    calParams.dac_hold = dacHold ? 1.0f : 0.0f;
    calParamsUpdated = true;
    Serial.printf("[CMD] DAC hold across zero setpoint: %s\n", dacHold ? "ON" : "OFF");
  }
  if (staged.hasKp) { Kp_flow = calParams.kp = staged.stagedKp; calParamsUpdated = true; }
  if (staged.hasKi) { Ki_flow = calParams.ki = staged.stagedKi; calParamsUpdated = true; }
  if (staged.hasFfGain) { ffGain = calParams.ff_gain = staged.stagedFfGain; calParamsUpdated = true; }
  if (staged.hasFfOffset) { ffOffset = calParams.ff_offset = staged.stagedFfOffset; calParamsUpdated = true; }
  if (staged.hasRampRate) { rampRate = calParams.ramp_rate = staged.stagedRampRate; calParamsUpdated = true; }

  // Coeficientes de calibracao (F10: NAO zerar a1/b1 quando k1/f1/c1 forem atualizados isoladamente!)
  if (staged.hasA1) { a1 = calParams.a1 = staged.stagedA1; calParamsUpdated = true; }
  if (staged.hasB1) { b1 = calParams.b1 = staged.stagedB1; calParamsUpdated = true; }
  if (staged.hasK1) { k1 = calParams.k1 = staged.stagedK1; calParamsUpdated = true; }
  if (staged.hasF1) { f1 = calParams.f1 = staged.stagedF1; calParamsUpdated = true; }
  if (staged.hasC1) { c1 = calParams.c1 = staged.stagedC1; calParamsUpdated = true; }
  if (staged.hasK2) { k2 = calParams.k2 = staged.stagedK2; calParamsUpdated = true; }
  if (staged.hasF2) { f2 = calParams.f2 = staged.stagedF2; calParamsUpdated = true; }
  if (staged.hasC2) { c2 = calParams.c2 = staged.stagedC2; calParamsUpdated = true; }

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

  if (calParamsUpdated) saveParameters();
  startLEDBlinking();

  xSemaphoreGive(commandMutex);
  return true;
}

// Returns false when the I2C bus could not be taken; the caller keeps its old
// flowSetpoint so the write is retried on the next control cycle.
