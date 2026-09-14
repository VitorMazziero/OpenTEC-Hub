// ============ CAIXA DE COMANDO CONFIÁVEL ============

// Every Hub boot starts each box in its own cmd_id region (see Runtime.h). The node keeps
// its last applied id across a Hub reboot and echoes it on every push; a fresh counter
// restarting at 1 would collide with that echo and lose the first command silently.
void seedReliableMailboxes() {
  ReliableMailbox* boxes[] = { &distanceBox, &biomassBox, &pumpBox, &agitatorBox };
  for (ReliableMailbox* box : boxes) {
    const uint32_t base = ((esp_random() % 900000UL) + 100000UL) * 1000UL;
    box->revision = base;
    box->ack = 0;
    box->awaiting = false;
    box->payload = "";
  }
  ESP32_INFO(String("Reliable mailbox seeds: distance=") + distanceBox.revision +
             " biomass=" + biomassBox.revision + " pump=" + pumpBox.revision +
             " agitator=" + agitatorBox.revision);
}

// Queues one command for a node, retained until it acknowledges this revision.
// innerJson carries the key/value pairs WITHOUT the surrounding braces.
uint32_t queueReliable(ReliableMailbox &box, const String &innerJson, const char *label) {
  uint32_t revision = 0;
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
    box.revision++;
    if (box.revision == 0) box.revision = 1;   // 0 is the "never acknowledged" sentinel
    box.awaiting = true;
    box.deliveries = 0;
    box.queuedAt = millis();
    box.payload = "{\"cmd_id\":" + String(box.revision) + "," + innerJson + "}";
    revision = box.revision;
    xSemaphoreGive(cmdMutex);
  }
  if (revision != 0) {
    ESP32_EVT(String(label) + " command queued cmd_id=" + revision);
  }
  return revision;
}

// Unlike takePending(), reading never clears the box. The command stays available until
// the node's own data push carries the matching ack_cmd_id.
String takeReliable(ReliableMailbox &box) {
  String out = "{}";
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
    if (box.awaiting && box.payload.length() > 0) {
      out = box.payload;
      box.deliveries++;
    }
    xSemaphoreGive(cmdMutex);
  }
  return out;
}

// Clears the box when the node reports the revision it applied. A stale or unknown ack
// is recorded but never clears an outstanding command.
bool ackReliable(ReliableMailbox &box, uint32_t reportedAck, const char *label) {
  bool cleared = false;
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
    box.ack = reportedAck;
    if (box.awaiting && reportedAck != 0 && reportedAck == box.revision) {
      box.awaiting = false;
      box.payload = "";
      cleared = true;
    }
    xSemaphoreGive(cmdMutex);
  }
  if (cleared) {
    ESP32_EVT(String(label) + " command acknowledged cmd_id=" + reportedAck);
  }
  return cleared;
}

// True while a command is outstanding, for the telemetry frame.
bool mailboxPending(ReliableMailbox &box) {
  bool pending = false;
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
    pending = box.awaiting;
    xSemaphoreGive(cmdMutex);
  }
  return pending;
}

// Reads the ack_cmd_id query parameter a node appends to its data push, or 0.
uint32_t readAckParam(AsyncWebServerRequest *request) {
  if (!request->hasParam("ack_cmd_id")) return 0;
  return (uint32_t)strtoul(request->getParam("ack_cmd_id")->value().c_str(), NULL, 10);
}

// ============ HELPERS DE SINCRONIZAÇÃO ============

// Define um valor em uma fila 'pending' de forma segura (thread-safe)
inline void setPending(String& dst, const String& val) {
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) { 
    dst = val; 
    xSemaphoreGive(cmdMutex); 
  }
}

// Obtém e limpa um valor de uma fila 'pending' de forma segura (thread-safe)
inline String takePending(String& src) {
  String out = "{}";
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) { 
    if (src.length() > 0) { 
      out = src; 
      src = ""; 
    } 
    xSemaphoreGive(cmdMutex); 
  }
  return out;
}

// Build the current flowmeter message while cmdMutex is held. Actuator state
// is always complete; calibration fields are included only while pending ack.
String buildFlowCommandLocked() {
  String cmd;
  cmd.reserve(512);

  cmd = "{\"cmd_id\":" + String(flowCommandRevision);

  // ---------------------------------------------------------
  // Normal operational command fields
  // These can be sent independently of calibration.
  // ---------------------------------------------------------
  cmd += ",\"flow_setpoint\":" + String(desiredFlowSetpoint, 6);
  cmd += ",\"v1\":" + String(desiredFlowValve1);
  cmd += ",\"v2\":" + String(desiredFlowValve2);
  cmd += ",\"v_Flow\":" + String(desiredFlowValveFlow);

  if (pendingMaxFlow) {
    cmd += ",\"max_flow\":" + String(desiredMaxFlow, 6);
  }

  if (pendingReconnectWifi) {
    cmd += ",\"reconnect_wifi\":" + String(desiredReconnectWifi);
  }

  // ---------------------------------------------------------
  // Calibration
  //
  // Flowmeter v12 requires the complete calibration model in
  // the SAME frame when transition_v is present.
  //
  // Never send a partial calibration block.
  // ---------------------------------------------------------

  const bool fullCalibrationPending =
      pendingA1 &&
      pendingB1 &&
      pendingK1 &&
      pendingF1 &&
      pendingC1 &&
      pendingK2 &&
      pendingF2 &&
      pendingC2 &&
      pendingFlowTransitionVoltage;

  if (fullCalibrationPending) {

    // Segment 1
    // a1/b1 first so the node sees the quartic opt-in before
    // k1/f1/c1.
    cmd += ",\"a1\":" + String(desiredA1, 9);
    cmd += ",\"b1\":" + String(desiredB1, 9);
    cmd += ",\"k1\":" + String(desiredK1, 9);
    cmd += ",\"f1\":" + String(desiredF1, 9);
    cmd += ",\"c1\":" + String(desiredC1, 9);

    // Segment 2
    cmd += ",\"k2\":" + String(desiredK2, 9);
    cmd += ",\"f2\":" + String(desiredF2, 9);
    cmd += ",\"c2\":" + String(desiredC2, 9);

    // transition_v is part of the calibration transaction.
    // It must not be sent independently.
    cmd += ",\"transition_v\":" +
           String(desiredFlowTransitionVoltage, 4);
  }

  // ---------------------------------------------------------
  // Controller tuning parameters
  // These are independent from the sensor calibration model.
  // ---------------------------------------------------------

  if (pendingFlowKp) {
    cmd += ",\"kp_flow\":" + String(desiredFlowKp, 4);
  }

  if (pendingFlowKi) {
    cmd += ",\"ki_flow\":" + String(desiredFlowKi, 4);
  }

  if (pendingFlowFfGain) {
    cmd += ",\"ff_gain\":" + String(desiredFlowFfGain, 4);
  }

  if (pendingFlowFfOffset) {
    cmd += ",\"ff_offset\":" + String(desiredFlowFfOffset, 4);
  }

  if (pendingFlowRampRate) {
    cmd += ",\"ramp_rate\":" + String(desiredFlowRampRate, 3);
  }

  cmd += "}";

  return cmd;
}

// Unlike the v6 mailbox, reading never clears the flowmeter command. It is
// retained until /flowData acknowledges its cmd_id.
String getReliableFlowCommand() {
  String out = "{}";
  bool firstDelivery = false;
  uint32_t revision = 0;
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
    if (flowCommandAwaitingAck && pendingFlowmeterCommand.length() > 0) {
      out = pendingFlowmeterCommand;
      flowCommandDeliveryCount++;
      firstDelivery = (flowCommandDeliveryCount == 1);
      revision = flowCommandRevision;
    }
    xSemaphoreGive(cmdMutex);
  }
  if (firstDelivery) {
    ESP32_EVT(String("Flow command first delivery cmd_id=") + revision);
  }
  return out;
}

// ============ HELPER DE PARSING JSON ============

// indexOf tambem encontra o token dentro de um VALOR string. Em
// {"pump_command":"start","mode":2} a busca por "start" casava dentro do valor,
// o parser seguia ate o proximo ':' e devolvia o 2 do "mode" - ou seja, um
// comando de bomba enfileirava um "start" fantasma no no de biomassa. Um token
// so e chave quando o caractere significativo anterior e '{' ou ','.
static bool isJsonKeyPosition(const String &json, int quotePos) {
  int index = quotePos - 1;
  while (index >= 0 && isspace(json.charAt(index))) --index;
  return index < 0 || json.charAt(index) == '{' || json.charAt(index) == ',';
}

String getValueFromJson(const String &json, const String &key) {
  String searchKey = "\"" + key + "\"";
  int keyPos = json.indexOf(searchKey);
  while (keyPos != -1 && !isJsonKeyPosition(json, keyPos)) {
    keyPos = json.indexOf(searchKey, keyPos + 1);
  }
  if (keyPos == -1) return "";
  int colonPos = json.indexOf(':', keyPos + key.length() + 2);
  if (colonPos == -1) return "";
  int start = colonPos + 1;
  while (start < json.length() && isspace(json.charAt(start))) {
    start++;
  }
  char firstChar = json.charAt(start);
  int end = -1;
  if (firstChar == '\"') {
    start++;
    end = json.indexOf("\"", start);
    if (end == -1) return "";
  } else {
    end = json.indexOf(",", start);
    if (end == -1) {
      end = json.indexOf("}", start);
      if (end == -1) end = json.length();
    }
  }
  String value = json.substring(start, end);
  value.trim();
  return value;
}

uint32_t queueReliableFlowCommandFromJson(const String &json) {
  bool hasFlow = json.indexOf("\"flowSetpoint\"") != -1;
  bool hasV1 = json.indexOf("\"valve_1\"") != -1;
  bool hasV2 = json.indexOf("\"valve_2\"") != -1;
  bool hasVFlow = json.indexOf("\"v_Flow\"") != -1;
  bool hasMax = json.indexOf("\"maxFlow\"") != -1 || json.indexOf("\"max_flow\"") != -1;
  // The node's own "keep looking for a hub" switch. It can be turned off over the
  // flowmeter's USB serial or its private AP, and nothing here could turn it back on,
  // so a flowmeter parked that way never returned without someone walking to it.
  bool hasReconnect = json.indexOf("\"reconnectWifi\"") != -1;
  bool hasA1 = json.indexOf("\"a1\"") != -1;
  bool hasB1 = json.indexOf("\"b1\"") != -1;
  bool hasK1 = json.indexOf("\"k1\"") != -1;
  bool hasF1 = json.indexOf("\"f1\"") != -1;
  bool hasC1 = json.indexOf("\"c1\"") != -1;
  bool hasK2 = json.indexOf("\"k2\"") != -1;
  bool hasF2 = json.indexOf("\"f2\"") != -1;
  bool hasC2 = json.indexOf("\"c2\"") != -1;
  bool hasKp = json.indexOf("\"flowKp\"") != -1;
  bool hasKi = json.indexOf("\"flowKi\"") != -1;
  bool hasFfGain = json.indexOf("\"flowFfGain\"") != -1;
  bool hasFfOffset = json.indexOf("\"flowFfOffset\"") != -1;
  bool hasRamp = json.indexOf("\"flowRampRate\"") != -1;
  bool hasTransV = json.indexOf("\"flowTransitionVoltage\"") != -1 || json.indexOf("\"transition_v\"") != -1;

  if (!(hasFlow || hasV1 || hasV2 || hasVFlow || hasMax || hasReconnect ||
        hasA1 || hasB1 || hasK1 || hasF1 || hasC1 || hasK2 || hasF2 || hasC2 ||
        hasKp || hasKi || hasFfGain || hasFfOffset || hasRamp || hasTransV)) {
    return 0;
  }

  uint32_t revision = 0;
  String payload;
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
    if (hasFlow) {
      desiredFlowSetpoint = max(0.0f, getValueFromJson(json, "flowSetpoint").toFloat());
      // A setpoint-only command is common in cascade control. Infer the main
      // shutoff valve only when the sender did not specify it explicitly.
      if (!hasVFlow) desiredFlowValveFlow = (desiredFlowSetpoint > 0.0f) ? 0 : 1;
    }
    if (hasV1) desiredFlowValve1 = getValueFromJson(json, "valve_1").toInt() != 0;
    if (hasV2) desiredFlowValve2 = getValueFromJson(json, "valve_2").toInt() != 0;
    if (hasVFlow) desiredFlowValveFlow = getValueFromJson(json, "v_Flow").toInt() != 0;

    if (hasMax) {
      String mfVal = getValueFromJson(json, "maxFlow");
      if (mfVal.length() == 0) mfVal = getValueFromJson(json, "max_flow");
      if (mfVal.length() > 0) {
        desiredMaxFlow = mfVal.toFloat();
        pendingMaxFlow = true;
      }
    }
    if (hasReconnect) {
      desiredReconnectWifi = getValueFromJson(json, "reconnectWifi").toInt() != 0 ? 1 : 0;
      pendingReconnectWifi = true;
    }
    if (hasA1) { desiredA1 = getValueFromJson(json, "a1").toFloat(); pendingA1 = true; }
    if (hasB1) { desiredB1 = getValueFromJson(json, "b1").toFloat(); pendingB1 = true; }
    if (hasK1) { desiredK1 = getValueFromJson(json, "k1").toFloat(); pendingK1 = true; }
    if (hasF1) { desiredF1 = getValueFromJson(json, "f1").toFloat(); pendingF1 = true; }
    if (hasC1) { desiredC1 = getValueFromJson(json, "c1").toFloat(); pendingC1 = true; }
    if (hasK2) { desiredK2 = getValueFromJson(json, "k2").toFloat(); pendingK2 = true; }
    if (hasF2) { desiredF2 = getValueFromJson(json, "f2").toFloat(); pendingF2 = true; }
    if (hasC2) { desiredC2 = getValueFromJson(json, "c2").toFloat(); pendingC2 = true; }
    if (hasKp) { desiredFlowKp = getValueFromJson(json, "flowKp").toFloat(); pendingFlowKp = true; }
    if (hasKi) { desiredFlowKi = getValueFromJson(json, "flowKi").toFloat(); pendingFlowKi = true; }
    if (hasFfGain) { desiredFlowFfGain = getValueFromJson(json, "flowFfGain").toFloat(); pendingFlowFfGain = true; }
    if (hasFfOffset) { desiredFlowFfOffset = getValueFromJson(json, "flowFfOffset").toFloat(); pendingFlowFfOffset = true; }
    if (hasRamp) { desiredFlowRampRate = max(0.0f, getValueFromJson(json, "flowRampRate").toFloat()); pendingFlowRampRate = true; }
    if (hasTransV) {
      String tvVal = getValueFromJson(json, "flowTransitionVoltage");
      if (tvVal.length() == 0) tvVal = getValueFromJson(json, "transition_v");
      if (tvVal.length() > 0) {
        desiredFlowTransitionVoltage = tvVal.toFloat();
        pendingFlowTransitionVoltage = true;
      }
    }

    flowCommandRevision++;
    if (flowCommandRevision == 0) flowCommandRevision = 1;
    flowCommandAwaitingAck = true;
    flowCommandDeliveryCount = 0;
    flowCommandQueuedAt = millis();
    pendingFlowmeterCommand = buildFlowCommandLocked();
    revision = flowCommandRevision;
    payload = pendingFlowmeterCommand;
    xSemaphoreGive(cmdMutex);
  }

  if (revision != 0) {
    ESP32_EVT(String("Flow command queued cmd_id=") + revision + " payload=" + payload);
  }
  return revision;
}

// ============ FUNCTION DECLARATIONS ============
void startWiFi();
void startWatchDog();
void handleUSBCommands();
String processCommandData(const String &data);
void processJsonCommand(const String &json);
void processOutgoingCommands();
void readAndBroadcastSensorData();
void setMotor(int rpm);
void setTemperature(float t);
void setPH(float pH, float err, int op, int mix, int intensity);
void setPHCalibration(int calVal);
void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity);
void setAntifoam(int op, int mix, int intensity);
void setPressure(int ref);
String sendSensorCommand(const String &cmd, bool readResponse);
String removeDecimal(String s);
void updateParametersCycle();
void checkDistanceSensorReference();
void syncAllSensorSettings();
static void clearSensorRxLocked();
static void resetSensorUartLocked();
uint32_t computeStateHash();
void queueAgitatorCmd(float pct, int dir, int activePot);
String buildFlowCommandLocked();
String getReliableFlowCommand();
uint32_t queueReliableFlowCommandFromJson(const String &json);
uint32_t queueReliable(ReliableMailbox &box, const String &innerJson, const char *label);
String takeReliable(ReliableMailbox &box);
bool ackReliable(ReliableMailbox &box, uint32_t reportedAck, const char *label);
bool mailboxPending(ReliableMailbox &box);

// Helper de validação de resposta da UART (portável, sem range-based for)
static inline bool isValidSensorReply(const String& r) {
  if (r.length() == 0) return false;
  char c0 = r[0];
  // Permite números, negativos, decimais
  if (isDigit(c0) || c0 == '-' || c0 == '+' || c0 == '.') return true;
  // Permite respostas alfanuméricas (comandos de status, etc)
  for (size_t i = 0; i < r.length(); ++i) {
    if (isAlphaNumeric(r[i])) return true;
  }
  return false;
}

