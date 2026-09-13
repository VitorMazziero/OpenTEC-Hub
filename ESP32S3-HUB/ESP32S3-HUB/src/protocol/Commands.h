// ------------------------------------------------------------------
// handleUSBCommands():
// ------------------------------------------------------------------
// The line buffer persists across calls. A command is only processed once its
// terminating newline has arrived; whatever is in the UART/USB buffer meanwhile
// is appended and kept. The old reader took whatever bytes were available and
// processed them as a whole line: a ~40-byte setpoint fits in one USB packet and
// worked, the ~300-byte calibration command arrived in pieces, and each piece
// failed the "{...}" check and was dropped - so the flowmeter never received a
// curve and the app waited for an ack that could not come.
static const size_t USB_LINE_MAX = 1024;

void handleUSBCommands() {
  static String data;
  static bool reserved = false;
  // Set once a line overflowed USB_LINE_MAX: the tail of that line is thrown away
  // up to its end-of-line instead of being accumulated as the start of a new one,
  // which would hand processCommandData() a fragment that is not a command.
  static bool discarding = false;
  if (!reserved) {
    data.reserve(512);
    reserved = true;
  }

  while (Serial.available() > 0) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') {
      if (discarding) {
        discarding = false;   // the runaway line ended here; the next one starts clean
        data = "";
        continue;
      }
      data.trim();
      if (data.length() > 0) {
        processCommandData(data);
      }
      data = "";
      continue;   // keep draining: more than one line may be queued
    }
    if (discarding) {
      continue;
    }
    if (data.length() >= USB_LINE_MAX) {
      // Runaway input without a newline: discard rather than grow forever, and keep
      // discarding until the line actually ends.
      ESP32_AVISO("Linha serial excedeu " + String(USB_LINE_MAX) + " bytes sem fim de linha; descartada");
      data = "";
      discarding = true;
      continue;
    }
    data += c;
  }
}

// ------------------------------------------------------------------
// processCommandData():
// ------------------------------------------------------------------
String processCommandData(const String &data) {
  if (data.length() == 0) {
      return "Empty command";
  }

  if (data.startsWith("{") && data.endsWith("}")) {
      if (bypassMode) {
        bypassMode = false;
        ESP32_EVT("Modo bypass desativado; retornando ao modo JSON");
      }
      processJsonCommand(data);
      return "OK";
  } else {
      if (!bypassMode) {
        bypassMode = true;
        ESP32_EVT("Modo bypass ativado via USB");
      }
      String response = sendSensorCommand(data, true);
      Serial.print(response);
      return response;
  }
}

// ------------------------------------------------------------------
// computeStateHash():
//   Gera uma assinatura numérica (hash simples) do estado atual 
//   das variáveis do sistema que devem ser salvas na Flash.
// ------------------------------------------------------------------
uint32_t computeStateHash() {
  uint32_t hash = 0;
  // Multiplicadores primos arbitrários para criar entropia simples
  hash += (uint32_t)(tempReference * 100) * 3;
  hash += (uint32_t)(pHReference * 100) * 7;
  hash += (uint32_t)(pHError * 100) * 11;
  hash += (uint32_t)pHCal * 13;
  hash += (uint32_t)pHOperation * 17;
  hash += (uint32_t)pHMix * 19;
  hash += (uint32_t)pHIntensity * 23;
  hash += (uint32_t)motorRPM * 29;
  hash += (uint32_t)nutriOperation * 31;
  hash += (uint32_t)nutriMix * 37;
  hash += (uint32_t)nutriOpCycle * 41;
  hash += (uint32_t)nutriMixCycle * 43;
  hash += (uint32_t)nutriIntensity * 47;
  hash += (uint32_t)antifoamOperation * 53;
  hash += (uint32_t)antifoamMix * 59;
  hash += (uint32_t)antifoamIntensity * 61;
  hash += (uint32_t)pressureReference * 67;
  hash += (uint32_t)(distanceSensorReference * 100) * 71;
  hash += dataDelay * 73;
  
  // Variáveis Booleanas
  hash += (oxyOn ? 1 : 0) * 79;
  hash += (tempOn ? 1 : 0) * 83;
  hash += (phOn ? 1 : 0) * 89;
  hash += (nutrientOn ? 1 : 0) * 97;
  hash += (antifoamOn ? 1 : 0) * 101;
  hash += (pressureOn ? 1 : 0) * 103;
  hash += (agitatorAuto ? 1 : 0) * 107;
  hash += (agitatorReEnablePot ? 1 : 0) * 109;
  hash += (uint32_t)agitatorPercentFoam * 113;
  hash += (uint32_t)agitatorDirFoam * 127;
  hash += (uint32_t)(foamStartDelay_s * 100) * 131;
  hash += (uint32_t)(foamPulse_s * 100) * 137;
  hash += (uint32_t)(foamInterval_s * 100) * 139;
  hash += (biomassCommOn ? 1 : 0) * 149;
  hash += (distanceSensorCommOn ? 1 : 0) * 151;
  hash += (flowmeterControlEnabled ? 1 : 0) * 157;
  hash += (pumpCommOn ? 1 : 0) * 163;
  hash += (servoCommOn ? 1 : 0) * 167;
  hash += (motorControlRoute == MotorControlRoute::Modbus ? 1 : 0) * 173;

  return hash;
}

// ------------------------------------------------------------------
// processJsonCommand():
//   Fully Corrected Version with Temporary Variables to fix
//   the "Double Assignment" bug on Dirty Flags.
// ------------------------------------------------------------------
void processJsonCommand(const String &json) {
  String value;
  bool motorRouteChangedInFrame = false;

  // Diagnostics are a system read: no actuator ownership, NVS mutation or telemetry
  // frame. "all" deliberately emits one bounded line per node for USB robustness.
  if (json.indexOf("\"nodeDiag\"") != -1) {
    printNodeDiagResponse(getValueFromJson(json, "nodeDiag"));
    return;
  }

  // 1. Calcula a assinatura do estado ANTES das mudanças
  uint32_t preHash = computeStateHash();

  // ============ SYSTEM COMMANDS ============
  if (json.indexOf("\"resetVariables\"") != -1) {
    ESP32_EVT("Comando de reset das variáveis recebido");
    tempReference = 0.0f; 
    pHReference = 0.0f; pHError = 0.17f; 
    pHCal = 5; pHOperation = 5; pHMix = 10; pHIntensity = 990;
    motorRPM = 0;
    flagMotorDirty = true;
    nutriOperation = 999; nutriMix = 1; nutriOpCycle = 500;
    nutriMixCycle = 1; nutriIntensity = 99; 
    antifoamOperation = 999; antifoamMix = 1; antifoamIntensity = 99; 
    pressureReference = 100;
    distanceSensorReference = 0.0f; 
    dataDelay = 1000; 
    oxyOn = false; tempOn = false; phOn = false; 
    nutrientOn = false; antifoamOn = false; pressureOn = false; 
    if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
      desiredFlowSetpoint = 0.0f;
      desiredFlowValve1 = 0;
      desiredFlowValve2 = 0;
      desiredFlowValveFlow = 1;
      flowCommandRevision++;
      if (flowCommandRevision == 0) flowCommandRevision = 1;
      flowCommandAwaitingAck = true;
      flowCommandDeliveryCount = 0;
      flowCommandQueuedAt = millis();
      pendingMaxFlow = false;
      pendingReconnectWifi = false;
      pendingA1 = pendingB1 = false;
      pendingK1 = pendingF1 = pendingC1 = false;
      pendingK2 = pendingF2 = pendingC2 = false;
      pendingFlowKp = pendingFlowKi = false;
      pendingFlowFfGain = pendingFlowFfOffset = pendingFlowRampRate = false;
      pendingFlowmeterCommand = buildFlowCommandLocked();
      xSemaphoreGive(cmdMutex);
    }
    flowmeterControlEnabled = false;

    // Drop any outstanding peripheral command: it describes a desired state that the
    // reset has just discarded, and re-delivering it after a reset would resurrect it.
    if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
      biomassBox.awaiting = false;  biomassBox.payload  = "";
      pumpBox.awaiting    = false;  pumpBox.payload     = "";
      agitatorBox.awaiting = false; agitatorBox.payload = "";
      distanceBox.awaiting = false; distanceBox.payload = "";
      xSemaphoreGive(cmdMutex);
    }
    servoDevice.clearCommands();

    saveSettings();
    return;
  }

  if (json.indexOf("\"restart\"") != -1) {
    ESP32_EVT("Comando de reinicialização do ESP32 recebido");
    ESP.restart();
    return;
  }

  if (json.indexOf("\"comTest\"") != -1) {
    if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
      lastSensorJson = "OK";
      ++sampleId;
      xSemaphoreGive(stateMutex);
    }
    Serial.println("OK");
    return;
  }

  // ============ COMM SETTINGS ============
  if (json.indexOf("\"flowmeterComm\"") != -1) {
    bool newVal = (getValueFromJson(json, "flowmeterComm").toInt() != 0);
    if (newVal != flowmeterControlEnabled) {
      ESP32_EVT(String("Comunicação do fluxômetro ") + (newVal ? "ativada" : "desativada"));
    }
    flowmeterControlEnabled = newVal;
  }

  if (json.indexOf("\"biomassComm\"") != -1) {
    bool newVal = (getValueFromJson(json, "biomassComm").toInt() != 0);
    if (newVal != biomassCommOn) {
      ESP32_EVT(String("Comunicação do sensor de biomassa ") + (newVal ? "ativada" : "desativada"));
    }
    biomassCommOn = newVal;
  }

  if (json.indexOf("\"distanceSensorComm\"") != -1) {
    bool newVal = (getValueFromJson(json, "distanceSensorComm").toInt() != 0);
    if (newVal != distanceSensorCommOn) {
      ESP32_EVT(String("Comunicação do sensor de distância ") + (newVal ? "ativada" : "desativada"));
    }
    distanceSensorCommOn = newVal;
  }

  if (json.indexOf("\"pumpComm\"") != -1) {
    bool newVal = (getValueFromJson(json, "pumpComm").toInt() != 0);
    if (newVal != pumpCommOn) {
      ESP32_EVT(String("Comunicação da bomba peristáltica ") + (newVal ? "ativada" : "desativada"));
    }
    pumpCommOn = newVal;
  }

  if (json.indexOf("\"servoComm\"") != -1) {
    bool newVal = false;
    String rawServoComm;
    if (JsonUtils::getRaw(json, "servoComm", rawServoComm) &&
        JsonUtils::parseBool(rawServoComm, newVal)) {
      if (newVal != servoCommOn) {
        ESP32_EVT(String("Comunicação do servo drive ") + (newVal ? "ativada" : "desativada"));
      }
      servoCommOn = newVal;
      servoDevice.setCommEnabled(newVal);
      if (!newVal && motorControlRoute == MotorControlRoute::Modbus) {
        motorRPM = 0;
        flagMotorDirty = false; // setCommEnabled(false) ja revisionou a parada.
      }
    } else {
      ESP32_AVISO("servoComm rejeitado: esperado 0, 1, false ou true");
    }
  }

  // ============ MOTOR COMMAND ROUTE ============
  // 0 = placa tradicional por UART/CN1; 1 = ESP32S3-driver por Modbus.
  // A troca sempre desabilita o motor e ignora qualquer setpoint que tenha
  // vindo no mesmo JSON. Assim um frame de sincronizacao nao religa a rotacao
  // antiga automaticamente na nova via.
  if (json.indexOf("\"motorControlMode\"") != -1) {
    uint32_t requestedMode = 0;
    String rawMode;
    if (JsonUtils::getRaw(json, "motorControlMode", rawMode) &&
        JsonUtils::parseUInt(rawMode, requestedMode) && requestedMode <= 1) {
      const MotorControlRoute requestedRoute = requestedMode == 1
        ? MotorControlRoute::Modbus : MotorControlRoute::UartCn1;
      if (requestedRoute != motorControlRoute) {
        motorControlRoute = requestedRoute;
        motorRPM = 0;
        flagMotorDirty = false;
        flagMotorRouteDirty = true;
        beginMotorRouteTransition(millis());
        motorRouteChangedInFrame = true;
        ESP32_EVT(String("Via do setpoint alterada para ") +
                  (motorControlRoute == MotorControlRoute::Modbus
                    ? "Modbus direto; motor desabilitado"
                    : "UART/CN1; motor desabilitado"));
      }
    } else {
      ESP32_AVISO("motorControlMode rejeitado: esperado 0 (UART/CN1) ou 1 (Modbus)");
    }
  }

  // ============ SERVO (Delta ASDA-B2) ============
  // Reset de energia e poll continuam em FIFO. Velocidade e um estado desejado
  // revisionado separado, pois uma parada nunca pode esperar atras de eventos.
  {
    bool resetEnergy = false;
    bool hasReset = false;
    String raw;
    if (JsonUtils::getRaw(json, "resetServoEnergy", raw)) {
      hasReset = JsonUtils::parseBool(raw, resetEnergy);
      if (!hasReset) ESP32_AVISO("resetServoEnergy rejeitado: valor booleano inválido");
    }

    bool hasPoll = false;
    uint32_t pollMs = 0;
    if (JsonUtils::getRaw(json, "servoPollMs", raw)) {
      hasPoll = JsonUtils::parseUInt(raw, pollMs) &&
                pollMs >= ServoDevice::kMinPollMs && pollMs <= ServoDevice::kMaxPollMs;
      if (!hasPoll) ESP32_AVISO("servoPollMs rejeitado: faixa válida é 250..10000 ms");
    }

    if ((hasReset && resetEnergy) || hasPoll) {
      const ServoEnqueueResult result =
          servoDevice.enqueue(hasReset && resetEnergy, hasPoll, static_cast<uint16_t>(pollMs));
      if (result == ServoEnqueueResult::Full) {
        ESP32_ERRO("Comando Servo rejeitado: fila cheia");
      } else if (result == ServoEnqueueResult::Disabled) {
        ESP32_AVISO("Comando Servo rejeitado: roteamento desabilitado");
      } else if (result == ServoEnqueueResult::Coalesced) {
        ESP32_EVT("servoPollMs atualizado em comando ainda não entregue");
      }
    }
  }

  // Note: distanceSensorReference is updated directly as it is used by internal logic, not sent to UART immediately
  if (json.indexOf("\"distanceSensorReference\"") != -1) {
    float newRef = getValueFromJson(json, "distanceSensorReference").toFloat();
    if (fabs(newRef - distanceSensorReference) > 0.01f) {
        distanceSensorReference = newRef;
        flagDistanceReferenceDirty = true;   // new flag
    }
  }
  
  if (json.indexOf("\"dataDelay\"") != -1) {
    int newDelay = getValueFromJson(json, "dataDelay").toInt();
    dataDelay = (newDelay < 100) ? 100 : newDelay;
  }

  if (json.indexOf("\"oxygenMonitor\"") != -1) {
    oxyOn = (getValueFromJson(json, "oxygenMonitor").toInt() != 0);
  }

  // ============ MOTOR CONTROL ============
  if (json.indexOf("\"motorSetpoint\"") != -1) {
    if (motorRouteChangedInFrame) {
      ESP32_AVISO("motorSetpoint ignorado: troca de via no mesmo frame exige novo comando");
    } else {
      setMotor(getValueFromJson(json, "motorSetpoint").toInt());
    }
  }

  // ============ TEMPERATURE CONTROL ============
  if (json.indexOf("\"tempSetpoint\"") != -1) {
    setTemperature(getValueFromJson(json, "tempSetpoint").toFloat());
  }

  // ============ PH CONTROL (CRITICAL FIX) ============
  // Use temporary variables so we don't update globals before setPH is called
  float tmp_pHRef = pHReference;
  float tmp_pHErr = pHError;
  int tmp_pHOp = pHOperation;
  int tmp_pHMix = pHMix;
  int tmp_pHInt = pHIntensity;
  bool pHUpdated = false;

  if (json.indexOf("\"pHSetpoint\"") != -1) { tmp_pHRef = getValueFromJson(json, "pHSetpoint").toFloat(); pHUpdated = true; }
  if (json.indexOf("\"pHError\"") != -1) { tmp_pHErr = getValueFromJson(json, "pHError").toFloat(); pHUpdated = true; }
  if (json.indexOf("\"pHOperation\"") != -1) { tmp_pHOp = getValueFromJson(json, "pHOperation").toInt(); pHUpdated = true; }
  if (json.indexOf("\"pHMix\"") != -1) { tmp_pHMix = getValueFromJson(json, "pHMix").toInt(); pHUpdated = true; }
  if (json.indexOf("\"pHIntensity\"") != -1) { tmp_pHInt = getValueFromJson(json, "pHIntensity").toInt(); pHUpdated = true; }

  if (pHUpdated) {
    setPH(tmp_pHRef, tmp_pHErr, tmp_pHOp, tmp_pHMix, tmp_pHInt);
  }
  
  // pH Calibration is handled immediately as it triggers a specific W command
  if (json.indexOf("\"pHCal\"") != -1) {
    float pHFloat = getValueFromJson(json, "pHCal").toFloat();
    int newpHCal = (int)round(pHFloat * 100);
    if (newpHCal != pHCal) { 
       setPHCalibration(newpHCal);
    }
  }

  // ============ NUTRIENT PUMP (CRITICAL FIX) ============
  int tmp_nOp = nutriOperation;
  int tmp_nMix = nutriMix;
  int tmp_nOpC = nutriOpCycle;
  int tmp_nMixC = nutriMixCycle;
  int tmp_nInt = nutriIntensity;
  bool nutriUpdated = false;

  if (json.indexOf("\"nutriOperation\"") != -1) { tmp_nOp = getValueFromJson(json, "nutriOperation").toInt(); nutriUpdated = true; }
  if (json.indexOf("\"nutriMix\"") != -1) { tmp_nMix = getValueFromJson(json, "nutriMix").toInt(); nutriUpdated = true; }
  if (json.indexOf("\"nutriOpCycle\"") != -1) { tmp_nOpC = getValueFromJson(json, "nutriOpCycle").toInt(); nutriUpdated = true; }
  if (json.indexOf("\"nutriMixCycle\"") != -1) { tmp_nMixC = getValueFromJson(json, "nutriMixCycle").toInt(); nutriUpdated = true; }
  if (json.indexOf("\"nutriIntensity\"") != -1) { tmp_nInt = getValueFromJson(json, "nutriIntensity").toInt(); nutriUpdated = true; }

  if (nutriUpdated) {
    setNutrient(tmp_nOp, tmp_nMix, tmp_nOpC, tmp_nMixC, tmp_nInt);
  }

  // ============ ANTIFOAM PUMP (CRITICAL FIX) ============
  int tmp_aOp = antifoamOperation;
  int tmp_aMix = antifoamMix;
  int tmp_aInt = antifoamIntensity;
  bool antiUpdated = false;

  if (json.indexOf("\"antifoamOperation\"") != -1) { tmp_aOp = getValueFromJson(json, "antifoamOperation").toInt(); antiUpdated = true; }
  if (json.indexOf("\"antifoamMix\"") != -1) { tmp_aMix = getValueFromJson(json, "antifoamMix").toInt(); antiUpdated = true; }
  if (json.indexOf("\"antifoamIntensity\"") != -1) { tmp_aInt = getValueFromJson(json, "antifoamIntensity").toInt(); antiUpdated = true; }

  if (antiUpdated) {
    setAntifoam(tmp_aOp, tmp_aMix, tmp_aInt);
  }

  // ============ PRESSURE CONTROL ============
  if (json.indexOf("\"pressureReference\"") != -1) {
    setPressure(getValueFromJson(json, "pressureReference").toInt());
  }

  // ============ FLOWMETER (reliable desired-state pass-through) ============
  // Explicit flow commands are always queued, even while telemetry is offline
  // or the UI enable flag is changing. This is required for safe shutdown.
  queueReliableFlowCommandFromJson(json);

  // ============ BIOMASS (Pass-Through) ============
  static String biomassCommand;
  static bool bioReserved = false;
  if (!bioReserved) {
      biomassCommand.reserve(128);
      bioReserved = true;
  }
  biomassCommand = "";
  bool biomassCmdFound = false;

  String startVal = getValueFromJson(json, "start");
  if (startVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"start\":" + startVal; biomassCmdFound = true; }
  String stopVal = getValueFromJson(json, "stop");
  if (stopVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"stop\":" + stopVal; biomassCmdFound = true; }
  String blankVal = getValueFromJson(json, "blank");
  if (blankVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"blank\":" + blankVal; biomassCmdFound = true; }
  String lowVal = getValueFromJson(json, "low");
  if (lowVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"low\":" + lowVal; biomassCmdFound = true; }
  String highVal = getValueFromJson(json, "high");
  if (highVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"high\":" + highVal; biomassCmdFound = true; }
  String optVal = getValueFromJson(json, "opt");
  if (optVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"opt\":" + optVal; biomassCmdFound = true; }

  struct BiomassCmdMap {
    const char* appKey;
    const char* cmdName;
  };
  const BiomassCmdMap bioNewCmds[] = {
    { "biomassIt", "set_it" },
    { "biomassPwm", "set_pwm" },
    { "biomassGear", "set_gear" },
    { "biomassEma", "ema" },
    { "biomassProbePeriodMs", "probe_period" }
  };

  for (const auto& item : bioNewCmds) {
    if (json.indexOf(String("\"") + item.appKey + "\"") != -1) {
      String val = getValueFromJson(json, item.appKey);
      if (val.length() > 0) {
        if (!biomassCmdFound) {
          biomassCommand = "\"command\":\"" + String(item.cmdName) + "\",\"value\":" + val;
          biomassCmdFound = true;
        } else {
          ESP32_EVT(String("Biomass command descartado (um por revisao): ") + item.appKey + "=" + val);
        }
      }
    }
  }

  if (json.indexOf("\"biomassAutoRange\"") != -1) {
    String autoVal = getValueFromJson(json, "biomassAutoRange");
    if (autoVal.length() > 0) {
      if (!biomassCmdFound) {
        bool isAuto = (autoVal == "1" || autoVal == "true" || autoVal == "auto");
        biomassCommand = "\"command\":\"" + String(isAuto ? "auto" : "manual") + "\"";
        biomassCmdFound = true;
      } else {
        ESP32_EVT(String("Biomass command descartado (um por revisao): biomassAutoRange=") + autoVal);
      }
    }
  }

  if (biomassCmdFound && biomassCommOn) {
      queueReliable(biomassBox, biomassCommand, "Biomass");
  }

  // ============ AGITATOR ============
  if (json.indexOf("\"agitatorAuto\"") != -1) {
    agitatorAuto = (getValueFromJson(json, "agitatorAuto").toInt() != 0);
  }
  if (json.indexOf("\"agitatorReEnablePot\"") != -1) {
    agitatorReEnablePot = (getValueFromJson(json, "agitatorReEnablePot").toInt() != 0);
  }
  if (json.indexOf("\"agitatorPercent\"") != -1) {
    int p = getValueFromJson(json, "agitatorPercent").toInt();
    if (p < 0) p = 0; if (p > 100) p = 100;
    agitatorPercentFoam = p;
  }
  if (json.indexOf("\"agitatorDir\"") != -1) {
    agitatorDirFoam = (getValueFromJson(json, "agitatorDir").toInt() != 0) ? 1 : 0;
  }
  
  if (json.indexOf("\"agitatorOn\"") != -1) {
    bool on = (getValueFromJson(json, "agitatorOn").toInt() != 0);
    if (on) {
      queueAgitatorCmd((float)agitatorPercentFoam, agitatorDirFoam, 0);
    } else {
      queueAgitatorCmd(0.0f, agitatorDirFoam, agitatorReEnablePot ? 1 : 0);
    }
  }
  if (json.indexOf("\"foamStartDelay_s\"") != -1) foamStartDelay_s = getValueFromJson(json, "foamStartDelay_s").toFloat();
  if (json.indexOf("\"foamPulse_s\"") != -1) foamPulse_s = getValueFromJson(json, "foamPulse_s").toFloat();
  if (json.indexOf("\"foamInterval_s\"") != -1) foamInterval_s = getValueFromJson(json, "foamInterval_s").toFloat();

  // ============ DISTANCE (config pass-through) ============
  String distInner;
  bool distCmdFound = false;

  if (json.indexOf("\"distanceOffsetMm\"") != -1) {
    String val = getValueFromJson(json, "distanceOffsetMm");
    if (val.length() > 0) {
      float offset = val.toFloat();
      if (offset >= -50.0f && offset <= 200.0f) {
        distInner += "\"offset_mm\":" + val;
        distCmdFound = true;
      } else {
        ESP32_EVT(String("Comando de offset da distancia fora da faixa [-50, 200]: ") + val);
      }
    }
  }

  if (json.indexOf("\"distanceSamplePeriodMs\"") != -1) {
    String val = getValueFromJson(json, "distanceSamplePeriodMs");
    if (val.length() > 0) {
      long period = val.toInt();
      if (period >= 100 && period <= 60000) {
        if (distCmdFound) distInner += ",";
        distInner += "\"sample_period\":" + val;
        distCmdFound = true;
      } else {
        ESP32_EVT(String("Comando de sample_period da distancia fora da faixa [100, 60000]: ") + val);
      }
    }
  }

  if (json.indexOf("\"distanceSendPeriodMs\"") != -1) {
    String val = getValueFromJson(json, "distanceSendPeriodMs");
    if (val.length() > 0) {
      long period = val.toInt();
      if (period >= 100 && period <= 60000) {
        if (distCmdFound) distInner += ",";
        distInner += "\"send_period\":" + val;
        distCmdFound = true;
      } else {
        ESP32_EVT(String("Comando de send_period da distancia fora da faixa [100, 60000]: ") + val);
      }
    }
  }

  if (json.indexOf("\"distanceResetNvs\"") != -1) {
    String val = getValueFromJson(json, "distanceResetNvs");
    // So o valor 1 significa algo para o no; desde D02 (2026-09-13) ele nao confirma um
    // payload sem chave valida, e um "reset_nvs":0 ficaria preso na caixa ate a proxima revisao.
    if (val.length() > 0 && val.toInt() == 1) {
      if (distCmdFound) distInner += ",";
      distInner += "\"reset_nvs\":1";
      distCmdFound = true;
    } else if (val.length() > 0) {
      ESP32_AVISO(String("distanceResetNvs ignorado (so 1 e aceito): ") + val);
    }
  }

  if (distCmdFound && distanceSensorCommOn) {
    queueReliable(distanceBox, distInner, "Distance");
  }

  // ============ PUMP (Pass-Through) ============
  static String pumpCommand;
  static bool pumpReserved = false;
  if (!pumpReserved) {
      pumpCommand.reserve(1536);
      pumpReserved = true;
  }
  pumpCommand = "";
  bool pumpCmdFound = false;

  const char* simpleKeys[] = {
    "pump_command", "mode", "pump_speed", "pump_speed_ms", "pump_pot", "init_t", "final_t",
    "lambda_const", "lambda_linear", "phi_linear", "lambda_exp", "phi_exp",
    "pumpSlope", "pumpIntercept", "pumpPidKp", "pumpPidKi", "pumpPidKd",
    "pumpSlopeLow", "pumpSlopeHigh", "pumpTransitionSpeed", "pumpTransitionFlow",
    "slope_low", "slope_high", "transition_speed", "transition_flow"
  };
  // O firmware da bomba tambem entende save_config, load_config, print_config e clear_nvs.
  // clear_nvs apaga calibracao, perfil e PID e reinicia o no; nenhum deles e operacao de
  // processo. Pelo Hub so passam os tres que o aplicativo e as receitas usam.
  const char* allowedPumpCommands[] = { "reset_volume", "start", "stop" };
  const char* polyKeys[] = {
    "p0", "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p9", "p10",
    "p11", "p12", "p13", "p14", "p15", "p16", "p17", "p18", "p19", "p20"
  };

  for (const char* key : simpleKeys) {
    String val = getValueFromJson(json, key);
    if (val.length() > 0) {
      if (strcmp(key, "pump_command") == 0) {
        bool allowed = false;
        for (const char* ok : allowedPumpCommands) {
          if (val == ok) { allowed = true; break; }
        }
        if (!allowed) {
          ESP32_AVISO(String("pump_command recusado pelo Hub: ") + val);
          continue;
        }
      }
      if (pumpCmdFound) pumpCommand += ",";
      String cleanKey = String(key);
      if (cleanKey == "pumpPidKp") cleanKey = "pid_kp";
      else if (cleanKey == "pumpPidKi") cleanKey = "pid_ki";
      else if (cleanKey == "pumpPidKd") cleanKey = "pid_kd";
      else if (cleanKey == "pumpSlopeLow") cleanKey = "slope_low";
      else if (cleanKey == "pumpSlopeHigh") cleanKey = "slope_high";
      else if (cleanKey == "pumpTransitionSpeed") cleanKey = "transition_speed";
      else if (cleanKey == "pumpTransitionFlow") cleanKey = "transition_flow";
      else if (cleanKey.startsWith("pump_")) cleanKey = cleanKey.substring(5); 
      if (cleanKey == "command") pumpCommand += "\"" + cleanKey + "\":\"" + val + "\"";
      else pumpCommand += "\"" + cleanKey + "\":" + val;
      pumpCmdFound = true;
    }
  }

  for (const char* key : polyKeys) {
    String val = getValueFromJson(json, key);
    if (val.length() > 0) {
      if (pumpCmdFound) pumpCommand += ",";
      pumpCommand += "\"" + String(key) + "\":" + val;
      pumpCmdFound = true;
    }
  }
  
  bool maybePumpSegments =
    json.indexOf("\"pump_command\"") != -1 ||
    json.indexOf("\"num_segments\"") != -1 ||
    json.indexOf("\"t0\"") != -1 ||
    json.indexOf("\"q0\"") != -1;

  if (maybePumpSegments) {
    const int MAX_PUMP_SEGMENTS = 100;
    for (int i = 0; i < MAX_PUMP_SEGMENTS; i++) {
        String t_key = "t" + String(i);
        String t_val = getValueFromJson(json, t_key);
        if (t_val.length() > 0) {
            if (pumpCmdFound) pumpCommand += ",";
            pumpCommand += "\"" + t_key + "\":" + t_val;
            pumpCmdFound = true;
        }

        String q_key = "q" + String(i);
        String q_val = getValueFromJson(json, q_key);
        if (q_val.length() > 0) {
            if (pumpCmdFound) pumpCommand += ",";
            pumpCommand += "\"" + q_key + "\":" + q_val;
            pumpCmdFound = true;
        }
    }
  }

  String numSegVal = getValueFromJson(json, "num_segments");
  if (numSegVal.length() > 0) {
    if (pumpCmdFound) pumpCommand += ",";
    pumpCommand += "\"num_segments\":" + numSegVal;
    pumpCmdFound = true;
  }

  if (pumpCmdFound && pumpCommOn) {
    queueReliable(pumpBox, pumpCommand, "Pump");
  }

  // 2. Compara a assinatura do estado DEPOIS das mudanças
  uint32_t postHash = computeStateHash();

  if (preHash != postHash) {
    // Houve mutação no estado das variáveis.
    // Aciona a flag global de salvamento e reseta o cronômetro do debounce.
    flagPendingSave = true;
    lastSaveTriggerTime = millis();
  }
}

// ------------------------------------------------------------------
