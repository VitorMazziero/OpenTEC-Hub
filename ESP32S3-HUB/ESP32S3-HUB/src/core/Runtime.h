// ------------------------------------------------------------------
// startWatchDog():
// ------------------------------------------------------------------
void startWatchDog() {
  // idle_core_mask espelha CONFIG_ESP_TASK_WDT_CHECK_IDLE_TASK_CPU0=y do core:
  // reconfigurar com máscara zero desinscreveria a idle do núcleo 0, que é onde
  // vive a stack de Wi-Fi, e perderíamos justamente o detector de inanição.
  esp_task_wdt_config_t wdt_config = {
    .timeout_ms = WDT_TIMEOUT * 1000,
    .idle_core_mask = (1U << 0),
    .trigger_panic = true
  };

  // O core Arduino-ESP32 3.x já inicializa o TWDT (CONFIG_ESP_TASK_WDT_INIT=y,
  // CONFIG_ESP_TASK_WDT_TIMEOUT_S=5). Nesse caso esp_task_wdt_init() devolve
  // ESP_ERR_INVALID_STATE e o WDT_TIMEOUT desta build nunca chegava a valer: o
  // firmware rodava com os 5 s do core enquanto o código prometia 10 s.
  esp_err_t init_result = esp_task_wdt_init(&wdt_config);
  if (init_result == ESP_ERR_INVALID_STATE) {
    init_result = esp_task_wdt_reconfigure(&wdt_config);
  }
  if (init_result != ESP_OK) {
    ESP32_ERRO(String("Falha ao inicializar o watchdog: ") + esp_err_to_name(init_result) + String(" - Reiniciar Módulo TECNAL"));
  } else {
    ESP32_INFO(String("Watchdog ativo com timeout de ") + WDT_TIMEOUT + String(" s"));
  }

  esp_err_t add_result = esp_task_wdt_add(NULL);
  if (add_result != ESP_OK) {
    ESP32_ERRO(String("Falha ao adicionar a tarefa atual ao watchdog: ") + esp_err_to_name(add_result) + String(" - Reiniciar Módulo TECNAL"));
  } else {
    ESP32_INFO("Tarefa principal registrada no watchdog");
  }
}

// ============ COMMAND PROCESSOR (COMPLETE) ============
// This function runs inside the main loop. It checks "dirty flags"
// and sends ALL parameters for that subsystem to ensure hardware logic works.
static void sendMotorByUart(int rpm) {
  // Sem PI e sem correcao: o numero recebido do app e o mesmo enviado ao
  // modulo TECNAL, para que o visor conheca o setpoint tradicional.
  if (rpm <= 0) {
    // No protocolo da placa, 0V desabilita o servo; 0A mantem o visor coerente.
    sendSensorCommand("0V", false);
    vTaskDelay(pdMS_TO_TICKS(15));
    sendSensorCommand("0A", false);
    return;
  }

  sendSensorCommand("1V", false);
  vTaskDelay(pdMS_TO_TICKS(15));
  sendSensorCommand(String(rpm) + "A", false);
}

static void refreshMotorRouteTransition() {
  if (!motorRouteTransitionPending) return;
  const uint32_t now = millis();
  const ServoSnapshot snapshot = servoDevice.snapshot(now);

  if (snapshot.online && !snapshot.motorCommandPending &&
      snapshot.motorRouteAck == static_cast<int32_t>(motorControlRoute)) {
    motorRouteTransitionPending = false;
    ESP32_EVT(String("Via de rotacao confirmada: ") +
              (motorControlRoute == MotorControlRoute::Modbus
                ? "Modbus direto" : "UART/CN1"));
    return;
  }

  // Sem o no presente nao ha quem possa estar segurando P3-06, logo nao ha o
  // que esperar. Um periferico ausente nunca pode calar a placa original.
  if (!snapshot.online) {
    motorRouteTransitionPending = false;
    ESP32_AVISO("Via de rotacao liberada sem ACK: ESP32S3-driver ausente");
    return;
  }

  // O no esta online mas nao confirma (por exemplo, falha ao escrever P3-06).
  // Sem este prazo o Hub engolia todo setpoint em silencio, para sempre.
  if ((now - motorRouteTransitionStartedMs) >= MOTOR_ROUTE_TRANSITION_TIMEOUT_MS) {
    motorRouteTransitionPending = false;
    ESP32_AVISO(String("Via de rotacao liberada por tempo limite: ") +
                MOTOR_ROUTE_TRANSITION_TIMEOUT_MS +
                String(" ms sem ACK do ESP32S3-driver"));
  }
}

void processOutgoingCommands() {
  refreshMotorRouteTransition();

  // 1. MOTOR ROUTE UPDATE (break-before-make)
  // Primeiro desabilita a placa tradicional com 0V/0A. So depois pede ao
  // ESP32S3-driver que tome ou libere P3-06. A troca nunca reaproveita a
  // rotacao anterior: o operador precisa emitir novo setpoint.
  if (flagMotorRouteDirty) {
    sendMotorByUart(0);
    if (!servoDevice.setMotorDesired(0, false, motorControlRoute, millis())) {
      ESP32_ERRO("Falha ao enfileirar a troca da via de rotacao");
    } else {
      beginMotorRouteTransition(millis());
      ESP32_EVT(String("Troca da via de rotacao solicitada: ") +
                (motorControlRoute == MotorControlRoute::Modbus
                  ? "Modbus direto" : "UART/CN1"));
    }
    flagMotorRouteDirty = false;
    flagMotorDirty = false;
  }

  // 2. MOTOR SETPOINT UPDATE
  if (flagMotorDirty) {
    // So a via UART/CN1 corre o risco de topar com P3-06 ainda em modo
    // software. Na via Modbus o proprio setMotorDesired substitui o comando da
    // troca por uma revisao nova que ja leva via e rotacao juntas, e o no
    // sequencia P4-07/P3-06 sozinho; reter aqui so atrasaria o setpoint.
    if (motorControlRoute == MotorControlRoute::UartCn1 &&
        motorRouteTransitionPending && motorRPM > 0) {
      // Mantem o pedido pendente; ele sera aplicado depois do ACK de P3-06.
    } else if (motorControlRoute == MotorControlRoute::Modbus) {
      if (servoDevice.setMotorDesired(static_cast<uint16_t>(motorRPM),
                                      motorRPM > 0,
                                      MotorControlRoute::Modbus, millis())) {
        ESP32_EVT(String("Rotacao enfileirada: ") + motorRPM +
                  String(" rpm via ESP32S3-driver/Modbus"));
      } else {
        ESP32_ERRO("Rotacao Modbus rejeitada; verifique servoComm e faixa 0..1000 rpm");
      }
      flagMotorDirty = false;
    } else {
      sendMotorByUart(motorRPM);
      ESP32_EVT(String("Rotacao enviada sem PI: ") + motorRPM +
                String(" rpm via UART/CN1"));
      flagMotorDirty = false;
    }
  }

  // 3. TEMPERATURE UPDATE
  if (flagTempDirty) {
    if (tempControlRoute == TempControlRoute::ExternalBath) {
      flagTempDirty = false;
      tempOn = false;
    } else if (fabs(tempReference) < 0.001) {
      tempOn = false;
      sendSensorCommand("100B", false); 
    } else {
      tempOn = true;
      String tStr = String(tempReference, 1);
      String noDot = removeDecimal(tStr);
      sendSensorCommand(noDot + "B", false);
    }
    flagTempDirty = false;
    vTaskDelay(pdMS_TO_TICKS(10));
  }

  // 3. PH UPDATE
  // Original Order: Setpoint(D) -> Error(E) -> Op(G) -> Mix(H) -> Intensity(F)
  if (flagPhDirty) {
    if (fabs(pHReference) < 0.001) { // Check reference, not intensity (matches original)
       phOn = false;
       sendSensorCommand("0F", false);
    } else {
       phOn = true;
       // 1. Setpoint (D)
       String phStr = String(pHReference, 2);
       sendSensorCommand(removeDecimal(phStr) + "D", false);
       vTaskDelay(pdMS_TO_TICKS(20)); // Increased delay to match original
       
       // 2. Error (E)
       String errStr = String(pHError, 2);
       sendSensorCommand(removeDecimal(errStr) + "E", false);
       vTaskDelay(pdMS_TO_TICKS(20));

       // 3. Operation Mode (G)
       sendSensorCommand(String(pHOperation) + "G", false);
       vTaskDelay(pdMS_TO_TICKS(20));

       // 4. Mix Time (H)
       sendSensorCommand(String(pHMix) + "H", false);
       vTaskDelay(pdMS_TO_TICKS(20));

       // 5. Intensity (F)
       sendSensorCommand(String(pHIntensity) + "F", false);
    }
    flagPhDirty = false;
    vTaskDelay(pdMS_TO_TICKS(10));
  }

  // 4. NUTRIENT UPDATE
  // Original Order: Intensity(0M - ON) -> Op(N) -> Mix(O) -> OpCycle(P) -> MixCycle(Q)
  if (flagNutriDirty) {
    if (nutriIntensity == 0) {
      nutrientOn = false;
      sendSensorCommand("0M", false);
    } else {
      nutrientOn = true;
      
      // 1. Intensity (0M) - Turns ON FIRST (Restored Original Logic)
      sendSensorCommand(String(nutriIntensity) + "0M", false);
      vTaskDelay(pdMS_TO_TICKS(20));

      // 2. Operation Mode (N)
      sendSensorCommand(String(nutriOperation) + "N", false);
      vTaskDelay(pdMS_TO_TICKS(20));

      // 3. Mix Time (O)
      sendSensorCommand(String(nutriMix) + "O", false);
      vTaskDelay(pdMS_TO_TICKS(20));

      // 4. Op Cycle (P)
      sendSensorCommand(String(nutriOpCycle) + "P", false);
      vTaskDelay(pdMS_TO_TICKS(20));

      // 5. Mix Cycle (Q)
      sendSensorCommand(String(nutriMixCycle) + "Q", false);
    }
    flagNutriDirty = false;
    vTaskDelay(pdMS_TO_TICKS(10));
  }

  // 5. ANTIFOAM UPDATE
  // Original Order: Intensity(0I - ON) -> Op(J) -> Mix(L)
  if (flagAntiDirty) {
    if (antifoamIntensity == 0) {
      antifoamOn = false;
      sendSensorCommand("0I", false);
    } else {
      antifoamOn = true;
      
      // 1. Intensity (0I) - Turns ON FIRST
      sendSensorCommand(String(antifoamIntensity) + "0I", false);
      vTaskDelay(pdMS_TO_TICKS(20));

      // 2. Operation Mode (J)
      sendSensorCommand(String(antifoamOperation) + "J", false);
      vTaskDelay(pdMS_TO_TICKS(20));

      // 3. Mix Time (L)
      sendSensorCommand(String(antifoamMix) + "L", false);
    }
    flagAntiDirty = false;
    vTaskDelay(pdMS_TO_TICKS(10));
  }

  // 6. PRESSURE UPDATE
  if (flagPressureDirty) {
    if (pressureReference == 0) {
      pressureOn = false;
      sendSensorCommand("0C", false);
    } else {
      pressureOn = true;
      sendSensorCommand(String(pressureReference) + "C", false);
    }
    flagPressureDirty = false;
    vTaskDelay(pdMS_TO_TICKS(10));
  }
}

// First input the cascade is missing, published as BathCascadePausedReason while it
// waits. Order follows what the operator has to fix first.
static const char* bathWaitingReason(bool referenceOk, bool reactorFresh, bool nodeFresh,
                                     bool commOn, uint8_t spSource, uint8_t mode,
                                     const char* guard, bool displaySpOk) {
  if (!referenceOk) return "no_reference";
  if (!reactorFresh) return "reactor_pv_invalid";
  if (!nodeFresh) return "node_offline";
  if (!commOn) return "bath_comm_off";
  if (spSource != 1) return "sp_source_shadow";
  if (mode != 1) return "bath_manual";
  if (strcmp(guard, "suspended") == 0) return "guard_suspended";
  if (!displaySpOk) return "display_sp_invalid";
  return "inputs";
}

void serviceExternalBathCascade(unsigned long now) {
  if (tempControlRoute != TempControlRoute::ExternalBath) {
    bathCascade.reset();
    char discarded[48];
    bathTakeFault(discarded, sizeof(discarded));
    if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
      bathCascadeSnapshot = bathCascade.snapshot();
      bathCommandSetpoint = NAN;
      bathCommandLatestWins = false;
      xSemaphoreGive(stateMutex);
    }
    return;
  }

  // AsyncWebServer updates the node snapshot on another task. Copy the whole
  // shared view under the short state mutex; the PI and command queue run
  // outside it and never hold the lock while formatting or doing I/O.
  bool bathCommSnapshot = false;
  float bathDisplaySpSnapshot = NAN;
  bool bathDisplaySpValidSnapshot = false;
  float bathTargetSnapshot = NAN;
  float nodeSpMinSnapshot = NAN;
  float nodeSpMaxSnapshot = NAN;
  uint8_t bathSpSourceSnapshot = 0;
  uint8_t bathModeSnapshot = 0;
  unsigned long bathLastUpdateSnapshot = 0;
  char bathStateSnapshot[sizeof(bathState)] = "idle";
  char bathGuardSnapshot[sizeof(bathGuard)] = "off";
  if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
    bathCommSnapshot = bathCommOn;
    bathDisplaySpSnapshot = bathDisplaySp;
    bathDisplaySpValidSnapshot = bathDisplaySpValid;
    bathTargetSnapshot = bathTarget;
    nodeSpMinSnapshot = bathNodeSpMin;
    nodeSpMaxSnapshot = bathNodeSpMax;
    bathSpSourceSnapshot = bathSpSource;
    bathModeSnapshot = bathMode;
    bathLastUpdateSnapshot = bathLastUpdate;
    snprintf(bathStateSnapshot, sizeof(bathStateSnapshot), "%s", bathState);
    snprintf(bathGuardSnapshot, sizeof(bathGuardSnapshot), "%s", bathGuard);
    xSemaphoreGive(stateMutex);
  }

  const BathCoordinatorView link = bathLinkView();
  const bool active = tempReferenceCommanded;
  const bool reactorFresh = reactorTempPvFresh(now);
  const bool nodeFresh = bathLastUpdateSnapshot > 0 && elapsedSinceMs(now, bathLastUpdateSnapshot) <= 5000;
  const bool nodeSequenceRunning = strcmp(bathStateSnapshot, "running") == 0 ||
                                   strcmp(bathStateSnapshot, "settling") == 0;
  const bool completionPending = link.completion == BathCompletionState::Pending;

  // D-2 resume: the stop left the node in manual; a new reactor reference re-arms
  // the guard once per activation. An operator choosing manual afterwards is kept.
  if (active && nodeFresh && bathCommSnapshot && bathModeSnapshot != 1 &&
      !bathAutoRequested && !link.stopPending) {
    bathQueueOperation(String("\"mode\":\"auto\""));
    bathAutoRequested = true;
  }

  // Fault events are edges reported by the command path. Seen while the cascade is
  // off they describe nothing the Hub commanded and are dropped.
  char faultReason[48] = "";
  bool fault = bathTakeFault(faultReason, sizeof(faultReason)) && active;
  if (!fault && active && completionPending &&
      link.completionAgeMs >= BATH_COMMAND_COMPLETION_TIMEOUT_MS) {
    fault = true;
    snprintf(faultReason, sizeof(faultReason), "%s", "bath_completion_timeout");
  }

  // C4 safety net: with the local API locked (D-1) and the guard reverting the
  // panel, the node target should equal the last command. If it does not for
  // BATH_TARGET_MISMATCH_MS, send the command again; give up after a few tries.
  const ExternalBathCascadeSnapshot previous = bathCascade.snapshot();
  const bool actuatorBusy = link.anyPending || completionPending || nodeSequenceRunning;
  if (active && nodeFresh && previous.hasCommand && !actuatorBusy &&
      (previous.state == ExternalBathCascadeState::Controlling ||
       previous.state == ExternalBathCascadeState::Approaching) &&
      isfinite(bathTargetSnapshot)) {
    const float expected = bathCascade.lastCommandC();
    if (fabsf(bathTargetSnapshot - expected) >= 0.05f) {
      if (bathTargetMismatchSinceMs == 0) {
        bathTargetMismatchSinceMs = now == 0 ? 1 : now;
      } else if (now - bathTargetMismatchSinceMs >= BATH_TARGET_MISMATCH_MS) {
        bathTargetMismatchSinceMs = 0;
        if (bathTargetResendCount >= BATH_TARGET_MAX_RESENDS) {
          if (!fault) {
            fault = true;
            snprintf(faultReason, sizeof(faultReason), "%s", "target_override");
          }
        } else {
          bathTargetResendCount++;
          ESP32_AVISO(String("Alvo do banho divergente (") + String(bathTargetSnapshot, 1) +
                      " != " + String(expected, 1) + "); reenviando");
          bathQueueSetpoint(expected);
        }
      }
    } else {
      bathTargetMismatchSinceMs = 0;
      bathTargetResendCount = 0;
    }
  } else if (!active) {
    bathTargetMismatchSinceMs = 0;
  }

  const bool referenceOk = active && tempReference > 0.0f;
  ExternalBathCascadeInputs in;
  in.nowMs = now;
  in.enabled = active;
  in.referenceValid = active;
  in.referenceC = tempReference;
  in.reactorPvValid = reactorFresh;
  in.reactorPvC = reactorTempPv;
  in.nodeOnline = nodeFresh;
  in.bathCommEnabled = bathCommSnapshot;
  in.bathSpSourceDisplay = bathSpSourceSnapshot == 1;
  in.bathModeAuto = bathModeSnapshot == 1;
  in.bathGuardHealthy = strcmp(bathGuardSnapshot, "suspended") != 0;
  in.bathSpValid = bathDisplaySpValidSnapshot;
  in.bathSpC = bathDisplaySpSnapshot;
  in.actuatorBusy = link.anyPending || completionPending || nodeSequenceRunning;
  in.pause = active && !reactorFresh;
  in.pauseReason = "reactor_pv_stale";
  in.fault = fault;
  in.faultReason = faultReason;
  in.waitingReason = bathWaitingReason(referenceOk, reactorFresh, nodeFresh, bathCommSnapshot,
                                       bathSpSourceSnapshot, bathModeSnapshot,
                                       bathGuardSnapshot, bathDisplaySpValidSnapshot);
  in.actuatorRangeValid = isfinite(nodeSpMinSnapshot) && isfinite(nodeSpMaxSnapshot) &&
                          nodeSpMinSnapshot < nodeSpMaxSnapshot;
  in.actuatorMinC = nodeSpMinSnapshot;
  in.actuatorMaxC = nodeSpMaxSnapshot;

  bathCascadeLastCalcMs = now;
  const bool ready = bathCascade.update(in);
  ExternalBathCascadeSnapshot updatedSnapshot = bathCascade.snapshot();
  if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
    bathCascadeSnapshot = updatedSnapshot;
    bathCommandSetpoint = updatedSnapshot.state == ExternalBathCascadeState::Off
        ? NAN : updatedSnapshot.commandSetpointC;
    xSemaphoreGive(stateMutex);
  }

  const bool nodeReady = nodeFresh && bathCommSnapshot && !nodeSequenceRunning;
  const bool postDoneCooldown = !link.hasDoneSetpoint ||
                                now - link.lastDoneMs >= bathCascade.config().commandMinMs;
  if (ready && nodeReady && !in.actuatorBusy && postDoneCooldown) {
    const uint32_t revision = bathQueueSetpoint(updatedSnapshot.commandSetpointC);
    if (revision != 0) {
      bathCascade.markCommandSent(updatedSnapshot.commandSetpointC, now);
      if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
        bathCommandLatestWins = false;
        bathCascadeSnapshot = bathCascade.snapshot();
        xSemaphoreGive(stateMutex);
      }
    }
  } else if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
    // latest-wins: a newer output exists but must wait for ACK, done and cooldown.
    bathCommandLatestWins = ready;
    xSemaphoreGive(stateMutex);
  }
}

// ------------------------------------------------------------------
// SETUP
// ------------------------------------------------------------------
void firmwareSetup() {
  Serial.begin(115200);
  ESP32_INFO("Inicialização iniciada");

  lastSensorJson.reserve(HUB_TELEMETRY_JSON_RESERVE);
  preferences.begin("SensorHub", false);
  loadSettings();
  // Setpoint persistido nunca e retomado automaticamente apos reboot. O PC
  // precisa emitir um novo comando de movimento nesta sessao.
  if (motorRPM != 0) {
    ESP32_AVISO("Setpoint de motor persistido descartado no boot por seguranca");
    motorRPM = 0;
    flagPendingSave = true;
    lastSaveTriggerTime = millis();
  }
  // debugSettings();
  startWatchDog();

  sensorSerialMutex = xSemaphoreCreateMutex();
  cmdMutex = xSemaphoreCreateMutex();
  stateMutex = xSemaphoreCreateMutex();
  uint32_t servoMotorSessionSeed = esp_random();
  if (servoMotorSessionSeed == 0) servoMotorSessionSeed = 1;
  if (sensorSerialMutex == NULL || cmdMutex == NULL || stateMutex == NULL ||
      !httpCommandQueue.begin() ||
      !servoDevice.begin(servoCommOn, servoMotorSessionSeed, motorControlRoute)) {
    ESP32_ERRO("Não foi possível criar os mutexes; reiniciando ESP32");
    ESP.restart();
  }

  ESP32_INFO("Mutexes criados com sucesso");
  ESP32_INFO(String("Servo motor command session seed: ") + servoMotorSessionSeed);

  // Start every hub boot in a different command-ID region. If the hub reboots
  // while the flowmeter remains powered, a new command cannot be mistaken for
  // an already-applied command from the previous hub session.
  flowCommandRevision = esp_random();
  if (flowCommandRevision == 0) flowCommandRevision = 1;
  flowCommandAck = flowCommandRevision;
  ESP32_INFO(String("Flow command session seed: ") + flowCommandRevision);

  // Mesma regra para as quatro caixas confiáveis. Sem isso cada boot do Hub recomeça em
  // cmd_id=1 enquanto o nó, que continuou ligado, ainda ecoa ack_cmd_id=1 da sessão
  // anterior: o primeiro comando depois do reboot era dado como confirmado antes de ser
  // entregue (ackReliable roda antes de takeReliable no push) e se perdia em silêncio.
  // Base múltipla de 1000 mantém os ids legíveis no log e garante que dois boots só
  // colidam se sortearem a mesma base (1 em 900 000).
  seedReliableMailboxes();

  sensorSerial.begin(9600, SERIAL_8N1, SENSOR_RX_PIN, SENSOR_TX_PIN);
  ESP32_INFO("UART do Módulo TECNAL iniciada em 9600 baud");
  if (tempControlRoute == TempControlRoute::ExternalBath) {
    // The persisted route is restored, but no persisted setpoint is resumed.
    // Release the original module actuator before accepting a new bath command.
    sendSensorCommand("100B", false);
    tempOn = false;
    tempReferenceCommanded = false;
  }

  // O modulo original precisa estar desabilitado antes de o no poder assumir
  // P3-06. Isso tambem garante um boot seguro quando a via persistida e UART.
  sendMotorByUart(0);
  beginMotorRouteTransition(millis());

  startWiFi();
  startNodeDiagTask();
  syncAllSensorSettings();

  ESP32_EVT("Inicialização concluída");
}

// ------------------------------------------------------------------
// LOOP
// ------------------------------------------------------------------
void firmwareLoop() {
  // 1. Process USB Debug Commands
  handleUSBCommands();

  // HTTP callbacks only enqueue complete frames. All mutations, NVS scheduling and
  // device-mailbox changes happen here on the main Arduino task.
  String queuedHttpCommand;
  while (httpCommandQueue.dequeue(queuedHttpCommand)) {
    processCommandData(queuedHttpCommand);
  }

  unsigned long now = millis();
  
  // 2. PRIORITY: Process Outgoing UART Commands (The "Mailbox")
  // We check this BEFORE reading sensors. If Python sent a command,
  // we send it to the hardware now.
  if (!bypassMode) {
     processOutgoingCommands();
  }

  // 3. PERIODIC: Read Sensors
  // We only read if enough time has passed.
  if (!bypassMode && (now - lastDataMillis) >= dataDelay) {
    lastDataMillis = now;
    
    // Safety check for Foam
    if (distanceSensorCommOn){
      checkDistanceSensorReference(); 
    }
    
    // Now it is safe to read because processOutgoingCommands() has finished.
    readAndBroadcastSensorData(); 
  }

  serviceExternalBathCascade(now);
  serviceHubOtaReboot(now);

  // NVS FLASH SAVE (Debounced)
  // Só grava na memória física se houve alteração e já se passaram 5 segundos sem novos comandos
  if (flagPendingSave && (now - lastSaveTriggerTime >= SAVE_DEBOUNCE_MS)) {
    saveSettings();
    flagPendingSave = false;
    ESP32_INFO("Parametros salvos na NVS (Flash)");
  }

  // Feed Watchdog
  esp_task_wdt_reset();
  vTaskDelay(pdMS_TO_TICKS(10)); 

  // Low-frequency heap log
  static unsigned long lastHeapLog = 0;
  if (millis() - lastHeapLog >= 60000) {
    lastHeapLog = millis();
    ESP32_INFO(String("Heap livre: ") + heap_caps_get_free_size(MALLOC_CAP_DEFAULT));
  }
}

// ------------------------------------------------------------------
