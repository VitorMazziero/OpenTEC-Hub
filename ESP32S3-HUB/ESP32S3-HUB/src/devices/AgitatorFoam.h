// ------------------------------------------------------------------
// queueAgitatorCmd():
// ------------------------------------------------------------------
void queueAgitatorCmd(float pct, int dir, int activePot)
{
  if (pct < 0.0f) pct = 0.0f;
  if (pct > 100.0f) pct = 100.0f;
  dir = (dir != 0) ? 1 : 0;
  activePot = (activePot != 0) ? 1 : 0;
  char buf[96];
  snprintf(buf, sizeof(buf),
           "\"RPM_percent\":%.1f,\"Dir\":%d,\"ActivePot\":%d",
           pct, dir, activePot);
  // Retained until the node acknowledges the revision. A newer desired state supersedes
  // an unacknowledged one, which is correct: the box carries state, not events.
  queueReliable(agitatorBox, String(buf), "Agitator");
}

// ------------------------------------------------------------------
// checkDistanceSensorReference():
// (Lógica de espuma de zona dupla: Normal vs Emergência)
// ------------------------------------------------------------------
void checkDistanceSensorReference()
{
  static bool   foam           = false;
  static bool   mixerOn        = false;
  static bool   dosingOn       = false;
  static unsigned long lastMs = 0;      
  static unsigned long normal_sumOnMs = 0;
  static unsigned long normal_nextDoseCumMs = 0;
  static unsigned long normal_doseStartMs = 0;
  static unsigned long emergency_doseStartMs = 0;
  static unsigned long emergency_lastDoseTimestamp = 0;
  const float SQUIRT_ZONE_LIMIT = 10.0f; 
  const unsigned long EMERGENCY_DOSE_INTERVAL_MS = 600000; // 10 min
  const unsigned long EMERGENCY_DOSE_PULSE_MS = 1000;      // 1 seg
  const unsigned long now = millis();
  static bool lastDataStale = false;
  static bool emergencyMode = false;

  // --- NEW: reset all static state when reference was changed mid-run ---
  if (flagDistanceReferenceDirty) {
    flagDistanceReferenceDirty = false;

    // Stop any active dosing or mixing immediately
    if (dosingOn) {
      sendSensorCommand("0M", false);
      dosingOn = false;
    }
    if (mixerOn && agitatorAuto) {
      queueAgitatorCmd(0.0f, agitatorDirFoam, agitatorReEnablePot ? 1 : 0);
      mixerOn = false;
    }

    // Reset all timers and state flags
    foam                         = false;
    normal_sumOnMs               = 0;
    normal_nextDoseCumMs         = 0;
    normal_doseStartMs           = 0;
    emergency_doseStartMs        = 0;
    emergency_lastDoseTimestamp  = 0;
    lastMs                       = 0;   // forces re-anchor on next tick
    lastDataStale                = false;
    emergencyMode                = false;

    ESP32_EVT(String("Referencia do sensor de distancia alterada para ")
              + distanceSensorReference
              + String("; estado da logica de espuma reiniciado"));
  }

  if (lastMs == 0) lastMs = now;

  // Helpers de ação (chamam sendSensorCommand, que é 'thread-safe')
  auto doseOff = [&]() { sendSensorCommand("0M", false); dosingOn = false; };
  auto doseOn = [&](int intensity) {
    if (intensity < 0) intensity = 0; if (intensity > 99) intensity = 99;
    sendSensorCommand(String(intensity) + "0M", false); dosingOn = (intensity > 0);
  };
  auto stopMixer = [&]() {
    if (mixerOn && agitatorAuto) {
      queueAgitatorCmd(0.0f, agitatorDirFoam, agitatorReEnablePot ? 1 : 0);
      mixerOn = false;
    }
  };
  auto startMixer = [&]() {
    if (!mixerOn && agitatorAuto) {
      queueAgitatorCmd((float)agitatorPercentFoam, agitatorDirFoam, 0);
      mixerOn = true;
    }
  };
  auto resetNormalTimers = [&]() {
    normal_sumOnMs = 0; normal_nextDoseCumMs = 0; normal_doseStartMs = 0;
  };
  auto resetEmergencyTimers = [&]() {
    emergency_doseStartMs = 0; emergency_lastDoseTimestamp = 0;
  };
  auto allStop = [&]() {
    if (dosingOn) doseOff();
    stopMixer();
    foam = false;
    resetNormalTimers();
    resetEmergencyTimers();
    lastMs = now;
  };

  // Se a referência for 0, desliga tudo
  if (distanceSensorReference <= 0.0f) {
    if (foam || mixerOn || dosingOn) {
      ESP32_EVT("Lógica de espuma desativada: referência do sensor de distância zerada");
    }
    allStop();
    emergencyMode = false;
    lastDataStale = false;
    return;
  }

  // Se os dados estiverem velhos, desliga tudo.
  //
  // O intertravamento le sob stateMutex porque o handler /distance passou a
  // escrever esses tres campos sob o mesmo mutex na v9. Ler fora dele deixaria
  // a decisao que aciona bomba e agitador apoiada num trio possivelmente
  // inconsistente: valor novo com carimbo de tempo velho, ou o contrario.
  bool snapComm = false;
  float snapValue = -1.0f;
  unsigned long snapUpdate = 0;
  if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
    snapComm = distanceSensorCommOn;
    snapValue = distanceSensorValue;
    snapUpdate = distanceSensorLastUpdate;
    xSemaphoreGive(stateMutex);
  }

  const bool dataStale = !snapComm ||
                        (now - snapUpdate > DISTANCE_TIMEOUT) ||
                        (snapValue < 0.0f);

  if (dataStale) {
    if (!lastDataStale) {
      ESP32_AVISO("Lógica de espuma pausada: dados do sensor de distância inválidos ou expirados");
    }
    lastDataStale = true;
    allStop();
    emergencyMode = false;
    return;
  }
  lastDataStale = false;

  // Lógica de histerese
  const float h = 0.5f;
  const float foamThreshold_lower = distanceSensorReference - h;
  const float foamThreshold_upper = distanceSensorReference + h;
  const float d = snapValue;
  bool currentFoamFlag;
  if (d > 0.0f && d <= foamThreshold_lower)   currentFoamFlag = true;
  else if (d > foamThreshold_upper)          currentFoamFlag = false;
  else                                       currentFoamFlag = foam;
  
  // Acumula tempo de espuma
  const unsigned long dt = (now - lastMs);
  if (currentFoamFlag) normal_sumOnMs += dt;
  lastMs = now;

  // Transições de estado
  if (currentFoamFlag && !foam) { // Espuma acabou de aparecer
    ESP32_EVT("Espuma detectada; iniciando resposta automática");
    startMixer();
    resetNormalTimers();
    resetEmergencyTimers();
    normal_nextDoseCumMs = (unsigned long)(foamStartDelay_s * 1000.0f);
    if (dosingOn) doseOff();
  }
  if (!currentFoamFlag && foam) { // Espuma acabou de sumir
    ESP32_EVT("Espuma normalizada; desligando resposta automática");
    allStop();
    emergencyMode = false;
  }
  foam = currentFoamFlag;

  bool nowEmergency = (foam && d <= SQUIRT_ZONE_LIMIT);

  if (nowEmergency && !emergencyMode) {
    ESP32_AVISO("Modo de emergência de espuma ativado");
  }
  if (!nowEmergency && emergencyMode) {
    ESP32_EVT("Modo de emergência de espuma desativado");
  }
  emergencyMode = nowEmergency;

  // Máquina de estados (só roda se houver espuma)
  if (foam) {
    if (d <= SQUIRT_ZONE_LIMIT) {
      // ESTADO DE EMERGÊNCIA (Esguicho/Obstrução)
      if (dosingOn && normal_doseStartMs > 0) {
        doseOff(); // Para qualquer pulso normal
        normal_doseStartMs = 0;
      }
      if (!dosingOn) {
        if (now - emergency_lastDoseTimestamp >= EMERGENCY_DOSE_INTERVAL_MS) {
          doseOn(99); // Pulso de emergência
          emergency_doseStartMs = now;
          emergency_lastDoseTimestamp = now;
        }
      } else {
        if (emergency_doseStartMs > 0 && (now - emergency_doseStartMs >= EMERGENCY_DOSE_PULSE_MS)) {
          doseOff(); // Termina o pulso de emergência
          emergency_doseStartMs = 0;
        }
      }
    } else {
      // ESTADO NORMAL (Espuma Real)
      if (dosingOn && emergency_doseStartMs > 0) {
        doseOff(); // Para qualquer pulso de emergência
        emergency_doseStartMs = 0;
      }
      if (!dosingOn) {
        if (normal_sumOnMs >= normal_nextDoseCumMs) {
          doseOn(99); // Pulso normal (Y)
          normal_doseStartMs = now;
          normal_nextDoseCumMs += (unsigned long)(foamInterval_s * 1000.0f); // Agenda próximo (Z)
        }
      } else {
        if (normal_doseStartMs > 0 && (now - normal_doseStartMs >= (unsigned long)(foamPulse_s * 1000.0f))) {
          doseOff(); // Termina o pulso normal (Y)
          normal_doseStartMs = 0;
        }
      }
    }
  }
}

// ------------------------------------------------------------------
// Funções 'set' (Non-blocking, updates variables and flags)
