// ------------------------------------------------------------------
// readAndBroadcastSensorData():
// ------------------------------------------------------------------
void readAndBroadcastSensorData() {
  // 1. LEITURA DOS SENSORES
  // All sensor readings are unconditional: the *On flags gate closed-loop
  // actuation only (pump, heater, controller), NOT the acquisition of the
  // process variable. The PV must be visible on the dashboard regardless of
  // whether the control loop is active. See temperature below as the reference
  // pattern — it was already correct; pH, O₂, pressure and antifoam now follow
  // the same rule.
  vTaskDelay(pdMS_TO_TICKS(10));
  float temperatureVal = -1.0;
  String temperatureResp = sendSensorCommand("b", true);
  if (temperatureResp.length() > 0) {
    temperatureVal = temperatureResp.toFloat();
    if (temperatureVal < 0.0f || temperatureVal > 100.0f) temperatureVal = -1.0f;
  }
  vTaskDelay(pdMS_TO_TICKS(10));
  float pHVal = -1.0;
  {
    // Measurement always active. phOn controls acid/base dosing, not reading.
    String pHResp = sendSensorCommand("k", true);
    if (pHResp.length() > 0) pHVal = pHResp.toFloat();
  }
  vTaskDelay(pdMS_TO_TICKS(10));
  float oxyVal = -1.0;
  {
    // Measurement always active. oxyOn controls DO acquisition mode, not reading.
    String oxyResp = sendSensorCommand("g", true);
    if (oxyResp.length() > 0) {
      oxyVal = oxyResp.toFloat();
      if (oxyVal < 0.0f || oxyVal > 4095.0f) oxyVal = -1.0f;
    }
  }
  vTaskDelay(pdMS_TO_TICKS(10));
  float pressureVal = -1.0;
  {
    // Measurement always active. pressureOn controls the reference setpoint, not reading.
    String pressureResp = sendSensorCommand("c", true);
    pressureVal = pressureResp.toFloat();
  }
  vTaskDelay(pdMS_TO_TICKS(10));
  float antifoamVal = -1.0;
  {
    // Measurement always active. antifoamOn controls the dosing pump, not reading.
    String antifoamResp = sendSensorCommand("e", true);
    antifoamVal = antifoamResp.toFloat();
  }

  float timeSec = millis() / 1000.0;

  // Copy callback-owned device state under one short critical section. JSON assembly
  // below uses only these locals and therefore never holds stateMutex while allocating.
  bool snapDistanceComm = false, snapBiomassComm = false, snapPumpComm = false;
  float snapDistanceValue = -1.0f, snapBiomassAbs = 0.0f, snapBiomassPwm = 0.0f;
  float snapPumpSpeed = 0.0f, snapPumpFlow = 0.0f, snapPumpVolume = 0.0f, snapPumpTarget = 0.0f;
  int snapBiomassRaw = 0, snapBiomassIt = 0, snapPumpMode = 0, snapPumpPwm = 0;
  bool snapPumpActive = false, snapPumpWaiting = false, snapAgitatorPot = true;
  float snapAgitatorPercent = 0.0f;
  int snapAgitatorDir = 1;
  String snapAgitatorSource = "unknown";
  unsigned long snapDistanceUpdate = 0, snapBiomassUpdate = 0, snapBiomassSampleUpdate = 0;
  unsigned long snapPumpUpdate = 0, snapAgitatorUpdate = 0;
  float snapDistanceOffsetMm = NAN;
  uint32_t snapDistanceSamplePeriodMs = 0;
  uint32_t snapDistanceSendPeriodMs = 0;
  bool snapDistanceEchoSeen = false;
  int snapBiomassGear = -1;
  float snapBiomassEma = NAN;
  uint32_t snapBiomassProbePeriodMs = 0;
  bool snapBiomassEchoSeen = false;
  float snapPumpSlope = NAN;
  float snapPumpIntercept = NAN;
  bool snapPumpEchoSeen = false;
  float snapPumpPidKp = NAN, snapPumpPidKi = NAN, snapPumpPidKd = NAN;
  int snapPumpPotEnabled = -1;
  float snapPumpCycleVolume = NAN;
  // 10.1: the node registry is copied whole (IP always; version/MAC only once the
  // node has said hello) so the identity keys are assembled outside the mutex too.
  DeviceNodeEntry snapNodes[DEV_COUNT];
  if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
    for (int i = 0; i < DEV_COUNT; i++) snapNodes[i] = g_deviceRegistry[i];
    if (distanceEchoSeen && (millis() - distanceSensorLastUpdate > distancePresenceWindowMs(distanceSendPeriodMs))) {
      distanceEchoSeen = false;
    }
    snapDistanceEchoSeen = distanceEchoSeen;
    snapDistanceOffsetMm = distanceOffsetMm;
    snapDistanceSamplePeriodMs = distanceSamplePeriodMs;
    snapDistanceSendPeriodMs = distanceSendPeriodMs;
    snapDistanceComm = distanceSensorCommOn;
    snapDistanceValue = distanceSensorValue;
    snapDistanceUpdate = distanceSensorLastUpdate;
    snapBiomassProbePeriodMs = biomassProbePeriodMs;
    unsigned long bioWin = biomassPresenceWindowMs(snapBiomassProbePeriodMs);
    if (biomassEchoSeen && (millis() - biomassLastUpdate > bioWin)) {
      biomassEchoSeen = false;
    }
    snapBiomassEchoSeen = biomassEchoSeen;
    snapBiomassGear = biomassGear;
    snapBiomassEma = biomassEma;
    snapBiomassComm = biomassCommOn;
    snapBiomassAbs = biomassAbsorbance;
    snapBiomassRaw = biomassRaw;
    snapBiomassIt = biomassIt;
    snapBiomassPwm = biomassPwm;
    snapBiomassUpdate = biomassLastUpdate;
    snapBiomassSampleUpdate = biomassSampleLastUpdate;
    if (pumpEchoSeen && (millis() - pumpLastUpdate > PUMP_TIMEOUT)) {
      pumpEchoSeen = false;
    }
    snapPumpEchoSeen = pumpEchoSeen;
    snapPumpSlope = pumpSlope;
    snapPumpIntercept = pumpIntercept;
    snapPumpPidKp = pumpPidKp;
    snapPumpPidKi = pumpPidKi;
    snapPumpPidKd = pumpPidKd;
    snapPumpPotEnabled = pumpPotEnabled;
    snapPumpCycleVolume = pumpCycleVolume;
    snapPumpComm = pumpCommOn;
    snapPumpMode = pumpMode;
    snapPumpPwm = pumpPwm;
    snapPumpSpeed = pumpSpeed;
    snapPumpFlow = pumpFlowRate;
    snapPumpVolume = pumpVolume;
    snapPumpTarget = pumpTargetVolume;
    snapPumpActive = pumpActive;
    snapPumpWaiting = pumpWaiting;
    snapPumpUpdate = pumpLastUpdate;
    snapAgitatorPercent = agitatorActualPercent;
    snapAgitatorDir = agitatorActualDir;
    snapAgitatorPot = agitatorPotActive;
    snapAgitatorSource = agitatorSource;
    snapAgitatorUpdate = agitatorLastUpdate;
    xSemaphoreGive(stateMutex);
  }

  // Validação de Distância
  // Uses the display window, not the foam-interlock one - see DISTANCE_PRESENCE_TIMEOUT.
  //
  // Presença e leitura são coisas distintas. distanceOnline diz que o nó empurrou dentro
  // da janela (a mesma expressão de /nodes); validDistance exige além disso que o próprio
  // nó tenha declarado a leitura válida (distance >= 0; ele empurra -1 quando o VL53L0X
  // falha). Um nó com sensor em falha continua online e ecoando a sua configuração,
  // apenas sem valor publicável. Amarrar a presença à leitura fazia o PC acusar "nó não
  // responde" com o nó respondendo a cada segundo.
  bool distanceOnline = snapDistanceComm &&
                        (millis() - snapDistanceUpdate <= distancePresenceWindowMs(snapDistanceSendPeriodMs));
  bool validDistance = distanceOnline && snapDistanceValue >= 0.0f;

  // Validação da Bomba e do Agitador
  bool pumpOnline = snapPumpComm && snapPumpUpdate > 0 &&
                    (millis() - snapPumpUpdate <= PUMP_TIMEOUT);
  bool agitatorOnline = snapAgitatorUpdate > 0 &&
                        (millis() - snapAgitatorUpdate <= AGITATOR_TIMEOUT);

  bool biomassPending  = mailboxPending(biomassBox);
  bool pumpPending     = mailboxPending(pumpBox);
  bool agitatorPending = mailboxPending(agitatorBox);
  bool distancePending = mailboxPending(distanceBox);
  const ServoSnapshot servoSnapshot = servoDevice.snapshot(millis());

  // Validação de Biomassa
  //
  // biomassOnline: the node is answering at all. Deliberately NOT gated on
  // biomassCommOn - "the sensor is there and you have routing switched off" is a real
  // state the operator needs to see, and it is exactly the one that silently swallows
  // every blank/start/threshold command.
  //
  // validBiomass: there is a current reading. A node idling with routing on is online
  // with no sample, and the PC shows a dash rather than the last measurement.
  unsigned long bioWin = biomassPresenceWindowMs(snapBiomassProbePeriodMs);
  bool biomassOnline = snapBiomassUpdate > 0 &&
                       (millis() - snapBiomassUpdate <= bioWin);
  bool validBiomass = false;
  if (snapBiomassComm && snapBiomassSampleUpdate > 0) {
      unsigned long age = millis() - snapBiomassSampleUpdate;
      if (age <= bioWin) validBiomass = true;
  }

  // 2. CONSTRUÇÃO DO JSON
  static String jsonResponse;
  static bool jsonReserved = false;
  if (!jsonReserved) {
      // v8: os campos Servo* acrescentam ~250 bytes ao pior caso.
      // v8.1: presença/roteamento/pendência dos quatro dispositivos e o bloco do
      // agitador acrescentam ~330 bytes. Pior caso medido ~1550; a folga é deliberada,
      // porque um realloc no meio da montagem fragmenta o heap a cada quadro.
      // 10.1: IP dos cinco nós (~140 bytes) e, só para nós registrados, versão e MAC
      // (~50 bytes por nó). Pior caso estimado ~2050. Mesma reserva de lastSensorJson
      // (Runtime.h), porque a cópia realoca se a origem for maior.
      // 10.2: Ecos de configuracao dos nós (distância ~80 B, fluxômetro ~160 B, bomba ~45 B,
      // biomassa ~60 B). Pior caso estimado ~2400 B; a reserva de 3072 B comporta com folga.
      jsonResponse.reserve(HUB_TELEMETRY_JSON_RESERVE);
      jsonReserved = true;
  }
  
  bool snapFlowOnline = false;
  bool snapFlowPending = false;
  bool snapFlowControlEnabled = false;
  float snapFlowVoltage = 0.0f, snapFlowRate = 0.0f, snapFlowSetpoint = 0.0f;
  int snapValve1 = 0, snapValve2 = 0, snapValveFlow = 0;
  uint32_t snapFlowRevision = 0, snapFlowAck = 0, snapDeliveryCount = 0;
  unsigned long snapQueuedAt = 0;
  String snapFlowSource = "unknown";
  bool snapFlowReconnect = true;
  float snapFlowmeterKp = NAN, snapFlowmeterKi = NAN;
  float snapFlowmeterFfGain = NAN, snapFlowmeterFfOffset = NAN, snapFlowmeterRampRate = NAN;
  float snapFlowmeterOutput = NAN, snapFlowmeterSetpointCorrected = NAN;
  uint8_t snapFlowmeterHwStatus = 7;
  uint32_t snapFlowmeterCalCrc = 0;
  uint32_t snapFlowmeterBootId = 0;
  bool snapFlowEchoSeen = false;
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
    if (flowmeterCommOn && (millis() - flowmeterLastUpdate > FLOWMETER_TIMEOUT)) {
      flowmeterCommOn = false;
    }
    if (flowmeterEchoSeen && !flowmeterCommOn) {
      flowmeterEchoSeen = false;
    }
    snapFlowEchoSeen = flowmeterEchoSeen;
    snapFlowmeterKp = flowmeterKp;
    snapFlowmeterKi = flowmeterKi;
    snapFlowmeterFfGain = flowmeterFfGain;
    snapFlowmeterFfOffset = flowmeterFfOffset;
    snapFlowmeterRampRate = flowmeterRampRate;
    snapFlowmeterOutput = flowmeterOutput;
    snapFlowmeterSetpointCorrected = flowmeterSetpointCorrected;
    snapFlowmeterHwStatus = flowmeterHwStatus;
    snapFlowmeterCalCrc = flowmeterCalCrc;
    snapFlowmeterBootId = flowmeterBootId;
    snapFlowOnline = flowmeterCommOn;
    snapFlowPending = flowCommandAwaitingAck;
    snapFlowControlEnabled = flowmeterControlEnabled;
    snapFlowVoltage = flowmeterVoltage;
    snapFlowRate = flowmeterRate;
    snapFlowSetpoint = flowmeterSetpoint;
    snapValve1 = flowmeterValve1;
    snapValve2 = flowmeterValve2;
    snapValveFlow = flowmeterValveFlow;
    snapFlowRevision = flowCommandRevision;
    snapFlowAck = flowCommandAck;
    snapDeliveryCount = flowCommandDeliveryCount;
    snapQueuedAt = flowCommandQueuedAt;
    snapFlowSource = flowmeterLastCommandSource;
    snapFlowReconnect = flowmeterReconnectWifi;
    xSemaphoreGive(cmdMutex);
  }

  jsonResponse = "{";

  jsonResponse += "\"HubFirmwareVersion\":\"" HUB_FIRMWARE_VERSION "\"";
  jsonResponse += ",\"HubProtocolVersion\":" + String(HUB_PROTOCOL_VERSION);
  jsonResponse += ",\"Time\":" + String(timeSec, 1);
  jsonResponse += ",\"Tempval\":" + String(temperatureVal, 2);
  jsonResponse += ",\"pHval\":" + String(pHVal, 2);
  jsonResponse += ",\"Oxyval\":" + String(oxyVal, 1);
  jsonResponse += ",\"Antifoam\":" + String(antifoamVal, 0);
  jsonResponse += ",\"Pressure\":" + String(pressureVal, 1);
  jsonResponse += ",\"HubStations\":" + String(WiFi.softAPgetStationNum());
  
  jsonResponse += ",\"FlowmeterOnline\":" + String(snapFlowOnline ? "true" : "false");
  jsonResponse += ",\"FlowControlEnabled\":" + String(snapFlowControlEnabled ? "true" : "false");
  jsonResponse += ",\"FlowCommandPending\":" + String(snapFlowPending ? "true" : "false");
  jsonResponse += ",\"FlowCommandId\":" + String(snapFlowRevision);
  jsonResponse += ",\"FlowCommandAck\":" + String(snapFlowAck);
  jsonResponse += ",\"FlowCommandDeliveries\":" + String(snapDeliveryCount);
  jsonResponse += ",\"FlowCommandAgeMs\":" + String(snapFlowPending ? millis() - snapQueuedAt : 0);
  jsonResponse += ",\"FlowCommandSource\":\"" + snapFlowSource + "\"";
  if (snapFlowOnline) {
    jsonResponse += ",\"FlowVoltage\":" + String(snapFlowVoltage, 4);
    jsonResponse += ",\"FlowRate\":" + String(snapFlowRate, 4);
    jsonResponse += ",\"FlowSetpoint\":" + String(snapFlowSetpoint, 4);
    jsonResponse += ",\"Valve1\":" + String(snapValve1);
    jsonResponse += ",\"Valve2\":" + String(snapValve2);
    jsonResponse += ",\"ValveFlow\":" + String(snapValveFlow);
    // Surfaces the node's own reconnect switch. A flowmeter with it off looks exactly
    // like one that is merely absent, and used to have no way back except a cable.
    jsonResponse += ",\"FlowmeterReconnectWifi\":" + String(snapFlowReconnect ? "true" : "false");
    if (snapFlowEchoSeen) {
      if (!isnan(snapFlowmeterKp)) jsonResponse += ",\"FlowKp\":" + String(snapFlowmeterKp, 4);
      if (!isnan(snapFlowmeterKi)) jsonResponse += ",\"FlowKi\":" + String(snapFlowmeterKi, 4);
      if (!isnan(snapFlowmeterFfGain)) jsonResponse += ",\"FlowFfGain\":" + String(snapFlowmeterFfGain, 4);
      if (!isnan(snapFlowmeterFfOffset)) jsonResponse += ",\"FlowFfOffset\":" + String(snapFlowmeterFfOffset, 4);
      if (!isnan(snapFlowmeterRampRate)) jsonResponse += ",\"FlowRampRate\":" + String(snapFlowmeterRampRate, 3);
      if (!isnan(snapFlowmeterOutput)) jsonResponse += ",\"FlowOutput\":" + String(snapFlowmeterOutput, 4);
      if (!isnan(snapFlowmeterSetpointCorrected)) jsonResponse += ",\"FlowSetpointCorrected\":" + String(snapFlowmeterSetpointCorrected, 4);
      if (snapFlowmeterCalCrc != 0) jsonResponse += ",\"FlowmeterCalCrc\":" + String(snapFlowmeterCalCrc);
      jsonResponse += ",\"FlowmeterHwStatus\":" + String(snapFlowmeterHwStatus);
      if (snapFlowmeterBootId != 0) jsonResponse += ",\"FlowmeterBootId\":" + String(snapFlowmeterBootId);
    }
  }

  // Presença, roteamento e pendência de TODO dispositivo externo, sempre emitidos.
  //
  // Sempre, e não só quando verdadeiros: a ausência de uma destas chaves significa "este
  // Hub é anterior a ela", que não é a mesma coisa que "false". Sem essa distinção o PC
  // não consegue separar um Hub desatualizado de um nó que caiu, e o único caminho
  // honesto seria tratar todo dispositivo como falho.
  //
  // *CommEnabled é o eco do flag que este Hub persiste na NVS. O PC guarda o dele em
  // disco; depois de um reboot os dois podem divergir, e a partir daí o Hub descarta
  // todo sub-comando daquele dispositivo em silêncio.
  jsonResponse += ",\"BiomassOnline\":" + String(biomassOnline ? "true" : "false");
  jsonResponse += ",\"BiomassCommEnabled\":" + String(snapBiomassComm ? "true" : "false");
  jsonResponse += ",\"BiomassCommandPending\":" + String(biomassPending ? "true" : "false");
  if (validBiomass) {
      jsonResponse += ",\"BiomassAbs\":" + String(snapBiomassAbs, 3);
      jsonResponse += ",\"BiomassRaw\":" + String(snapBiomassRaw);
      jsonResponse += ",\"BiomassIT\":" + String(snapBiomassIt);
      jsonResponse += ",\"BiomassPWM\":" + String(snapBiomassPwm, 1);
  }
  if (biomassOnline && snapBiomassEchoSeen) {
    if (snapBiomassGear >= 0) jsonResponse += ",\"BiomassGear\":" + String(snapBiomassGear);
    if (!isnan(snapBiomassEma)) jsonResponse += ",\"BiomassEma\":" + String(snapBiomassEma, 3);
    if (snapBiomassProbePeriodMs > 0) jsonResponse += ",\"BiomassProbePeriodMs\":" + String(snapBiomassProbePeriodMs);
  }

  jsonResponse += ",\"DistanceOnline\":" + String(distanceOnline ? "true" : "false");
  jsonResponse += ",\"DistanceCommEnabled\":" + String(snapDistanceComm ? "true" : "false");
  jsonResponse += ",\"DistanceCommandPending\":" + String(distancePending ? "true" : "false");
  if (validDistance) {
    jsonResponse += ",\"Distance\":" + String(snapDistanceValue, 2);
  }
  // Ecos seguem a presença, não a leitura: o offset aplicado não deixa de valer porque o
  // sensor óptico está em falha.
  if (distanceOnline && snapDistanceEchoSeen) {
    jsonResponse += ",\"DistanceOffsetMm\":" + String(snapDistanceOffsetMm, 2);
    jsonResponse += ",\"DistanceSamplePeriodMs\":" + String(snapDistanceSamplePeriodMs);
    jsonResponse += ",\"DistanceSendPeriodMs\":" + String(snapDistanceSendPeriodMs);
  }

  jsonResponse += ",\"PumpOnline\":" + String(pumpOnline ? "true" : "false");
  jsonResponse += ",\"PumpCommEnabled\":" + String(snapPumpComm ? "true" : "false");
  jsonResponse += ",\"PumpCommandPending\":" + String(pumpPending ? "true" : "false");
  if (pumpOnline) {
    jsonResponse += ",\"PumpMode\":" + String(snapPumpMode);
    jsonResponse += ",\"PumpPWM\":" + String(snapPumpPwm);
    jsonResponse += ",\"PumpSpeed\":" + String(snapPumpSpeed, 1);
    jsonResponse += ",\"PumpFlow\":" + String(snapPumpFlow, 3);
    jsonResponse += ",\"PumpVol\":" + String(snapPumpVolume, 3);
    jsonResponse += ",\"PumpTargetVol\":" + String(snapPumpTarget, 3);
    jsonResponse += ",\"PumpActive\":" + String(snapPumpActive ? "true" : "false");
    jsonResponse += ",\"PumpWaiting\":" + String(snapPumpWaiting ? "true" : "false");
    if (snapPumpEchoSeen) {
      if (!isnan(snapPumpSlope)) jsonResponse += ",\"PumpSlope\":" + String(snapPumpSlope, 4);
      if (!isnan(snapPumpIntercept)) jsonResponse += ",\"PumpIntercept\":" + String(snapPumpIntercept, 4);
      // 3.10 only; a 3.9 pump never sets these and the keys stay out of the frame.
      if (!isnan(snapPumpPidKp)) jsonResponse += ",\"PumpPidKp\":" + String(snapPumpPidKp, 4);
      if (!isnan(snapPumpPidKi)) jsonResponse += ",\"PumpPidKi\":" + String(snapPumpPidKi, 4);
      if (!isnan(snapPumpPidKd)) jsonResponse += ",\"PumpPidKd\":" + String(snapPumpPidKd, 4);
      if (snapPumpPotEnabled >= 0) jsonResponse += ",\"PumpPotEnabled\":" + String(snapPumpPotEnabled ? "true" : "false");
      if (!isnan(snapPumpCycleVolume)) jsonResponse += ",\"PumpCycleVol\":" + String(snapPumpCycleVolume, 3);
    }
  }

  jsonResponse += ",\"AgitatorOnline\":" + String(agitatorOnline ? "true" : "false");
  jsonResponse += ",\"AgitatorCommandPending\":" + String(agitatorPending ? "true" : "false");
  if (agitatorOnline) {
    jsonResponse += ",\"AgitatorPercent\":" + String(snapAgitatorPercent, 1);
    jsonResponse += ",\"AgitatorDir\":" + String(snapAgitatorDir);
    jsonResponse += ",\"AgitatorPotActive\":" + String(snapAgitatorPot ? "true" : "false");
    jsonResponse += ",\"AgitatorSource\":\"" + snapAgitatorSource + "\"";
  }

  // Presence is independent from routing. Motor stopped at 0 rpm remains a valid
  // sample; values are only published while routing is enabled.
  jsonResponse += ",\"ServoOnline\":" + String(servoSnapshot.online ? "true" : "false");
  jsonResponse += ",\"ServoCommEnabled\":" + String(servoSnapshot.commEnabled ? "true" : "false");
  jsonResponse += ",\"ServoCommandPending\":" + String((servoSnapshot.commandDepth > 0 || servoSnapshot.motorCommandPending) ? "true" : "false");
  jsonResponse += ",\"ServoCommandQueueDepth\":" + String(servoSnapshot.commandDepth);
  jsonResponse += ",\"MotorControlViaModbus\":" + String(motorControlRoute == MotorControlRoute::Modbus ? "true" : "false");
  jsonResponse += ",\"ServoControlCapable\":" + String(servoSnapshot.motorControlCapable ? "true" : "false");
  jsonResponse += ",\"ServoMotorCommandId\":" + String(servoSnapshot.motorCommandId);
  jsonResponse += ",\"ServoMotorCommandAck\":" + String(servoSnapshot.motorCommandAck);
  jsonResponse += ",\"ServoMotorRouteAck\":" + String(servoSnapshot.motorRouteAck);
  jsonResponse += ",\"ServoMotorCommandPending\":" + String(servoSnapshot.motorCommandPending ? "true" : "false");
  jsonResponse += ",\"ServoMotorCommandDeliveries\":" + String(servoSnapshot.motorCommandDeliveries);
  jsonResponse += ",\"ServoMotorCommandAgeMs\":" + String(servoSnapshot.motorCommandAgeMs);
  jsonResponse += ",\"ServoMotorRequestedRpm\":" + String(servoSnapshot.motorRequestedRpm);
  jsonResponse += ",\"ServoMotorAppliedRpm\":" + String(servoSnapshot.motorAppliedRpm);
  jsonResponse += ",\"ServoMotorLeaseMs\":" + String(servoSnapshot.motorLeaseMs);
  jsonResponse += ",\"ServoMotorEnabled\":" + String(servoSnapshot.motorEnable ? "true" : "false");
  jsonResponse += ",\"ServoMotorControlActive\":" + String(servoSnapshot.motorControlActive ? "true" : "false");
  jsonResponse += ",\"ServoMotorControlFault\":" + String(servoSnapshot.motorControlFault);
  if (servoSnapshot.samplePublishable) {
    jsonResponse += ",\"ServoRpm\":" + String(servoSnapshot.sample.rpm, 1);
    jsonResponse += ",\"ServoTorquePct\":" + String(servoSnapshot.sample.torquePct, 1);
    jsonResponse += ",\"ServoTorqueNm\":" + String(servoSnapshot.sample.torqueNm, 4);
    jsonResponse += ",\"ServoLoadPct\":" + String(servoSnapshot.sample.loadPct, 1);
    jsonResponse += ",\"ServoPowerW\":" + String(servoSnapshot.sample.powerW, 2);
    jsonResponse += ",\"ServoEnergyWh\":" + String(servoSnapshot.sample.energyWh, 6);
    jsonResponse += ",\"ServoState\":" + String(servoSnapshot.sample.state);
    jsonResponse += ",\"ServoAlarm\":" + String(servoSnapshot.sample.alarm);
    jsonResponse += ",\"ServoCommOk\":" + String(servoSnapshot.sample.commOk);
    jsonResponse += ",\"ServoCommErr\":" + String(servoSnapshot.sample.commErr);
  }

  // External-node identity (10.1). *IP is unconditional (0.0.0.0 = never seen);
  // *NodeVer/*NodeMac appear only once the node has registered through /nodeHello,
  // so an unregistered node costs the frame nothing. Additive: protocol stays 10.
  appendNodeIdentity(jsonResponse, "Distance",  snapNodes[DEV_DISTANCE]);
  appendNodeIdentity(jsonResponse, "Agitator",  snapNodes[DEV_AGITATOR]);
  appendNodeIdentity(jsonResponse, "Pump",      snapNodes[DEV_PUMP]);
  appendNodeIdentity(jsonResponse, "Flowmeter", snapNodes[DEV_FLOWMETER]);
  appendNodeIdentity(jsonResponse, "Biomass",   snapNodes[DEV_BIOMASS]);

  jsonResponse += ",\"SensorCommOK\":" + String(uartSensorOK ? "true" : "false");
  jsonResponse += "}";

  // Marca d'água do quadro agregado. Só fala quando cresce em degraus de 64 B (a
  // contagem de dígitos de Time e dos volumes oscila o tamanho em poucos bytes) e avisa
  // quando o máximo passa da reserva: a partir daí cada ciclo realoca e fragmenta o heap.
  // É este o número que a medição de bancada do Content-Length precisa (PONTOS §1.2).
  {
    static size_t frameHighWater = 0;
    const size_t frameLen = jsonResponse.length();
    if (frameLen >= frameHighWater + 64 ||
        (frameLen > HUB_TELEMETRY_JSON_RESERVE && frameHighWater <= HUB_TELEMETRY_JSON_RESERVE)) {
      frameHighWater = frameLen;
      if (frameLen > HUB_TELEMETRY_JSON_RESERVE) {
        ESP32_AVISO("Quadro agregado com " + String(frameLen) + " bytes excede a reserva de " +
                    String(HUB_TELEMETRY_JSON_RESERVE) + " bytes; realocacao a cada ciclo");
      } else {
        ESP32_EVT("Quadro agregado: novo maximo de " + String(frameLen) + " bytes (reserva " +
                  String(HUB_TELEMETRY_JSON_RESERVE) + ")");
      }
    }
  }

  if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
    lastSensorJson = jsonResponse;
    sampleId++;
    xSemaphoreGive(stateMutex);
  }
  
  Serial.println(jsonResponse); 

  lastReadBroadcastTime = millis();
}

// ------------------------------------------------------------------
