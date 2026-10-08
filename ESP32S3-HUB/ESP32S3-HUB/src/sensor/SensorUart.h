// Funções 'set' (Non-blocking, updates variables and flags)
// ------------------------------------------------------------------

void setMotor(int rpm) {
  // Range Check
  if (rpm < 0) rpm = 0;
  if (rpm > 1000) rpm = 1000;
  if (motorControlRoute == MotorControlRoute::Modbus && !servoCommOn && rpm > 0) {
    ESP32_AVISO("motorSetpoint rejeitado: servoComm esta desabilitado");
    rpm = 0;
  }
  
  // Check change
  if (motorRPM != rpm) {
      motorRPM = rpm;
      flagMotorDirty = true;
  }
}

void setTemperature(float temp, bool exactReference) {
  // Range Check (typical bioreactor range)
  if (temp < 0.0f) temp = 0.0f;
  if (temp > 100.0f) temp = 100.0f;

  const bool changed = fabs(tempReference - temp) > 0.01f;
  const bool exactChanged = exactReference && tempReference != temp;
  if (changed || exactChanged) {
      tempReference = temp;
  }
  // Um novo comando após reboot/troca de via precisa rearmar a UART mesmo se
  // repetir numericamente o último valor armazenado. Na via externa a mesma
  // referência alimenta apenas a cascata e nunca segue diretamente ao C404.
  if (tempControlRoute == TempControlRoute::UartModule &&
      (changed || exactChanged || !tempReferenceCommanded)) {
    flagTempDirty = true;
  }
}

void setPH(float pH, float err, int op, int mix, int intensity) {
  // Range Checks
  if (pH < 0.0f) pH = 0.0f; 
  if (pH > 14.0f) pH = 14.0f;
  if (err < 0.0f) err = 0.0f;
  if (op < 0) op = 0; 
  if (mix < 0) mix = 0; 
  if (intensity < 0) intensity = 0; if (intensity > 999) intensity = 999;

  // Detect ANY change
  bool changed = false;
  if (fabs(pHReference - pH) > 0.01f) changed = true;
  if (fabs(pHError - err) > 0.01f) changed = true;
  if (pHOperation != op) changed = true;
  if (pHMix != mix) changed = true;
  if (pHIntensity != intensity) changed = true;
  
  pHReference = pH;
  pHError = err;
  pHOperation = op;
  pHMix = mix;
  pHIntensity = intensity;
  
  if (changed) flagPhDirty = true;
}

// setPHCalibration remains direct because it is a manual action
void setPHCalibration(int calVal) {
    pHCal = calVal; 
    sendSensorCommand(String(pHCal) + "W", false); 
    vTaskDelay(pdMS_TO_TICKS(10)); 
}

void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity) {
  // Range Checks
  if (op < 0) op = 0; 
  if (mix < 0) mix = 0;
  if (opCycle < 0) opCycle = 0;
  if (mixCycle < 0) mixCycle = 0;
  if (intensity < 0) intensity = 0; if (intensity > 99) intensity = 99;

  // Detect ANY change
  bool changed = false;
  if (nutriOperation != op) changed = true;
  if (nutriMix != mix) changed = true;
  if (nutriOpCycle != opCycle) changed = true;
  if (nutriMixCycle != mixCycle) changed = true;
  if (nutriIntensity != intensity) changed = true;

  nutriOperation = op;
  nutriMix = mix;
  nutriOpCycle = opCycle;
  nutriMixCycle = mixCycle;
  nutriIntensity = intensity;
  
  if (changed) flagNutriDirty = true;
}

void setAntifoam(int op, int mix, int intensity) {
  // Range Checks
  if (op < 0) op = 0; 
  if (mix < 0) mix = 0;
  if (intensity < 0) intensity = 0; if (intensity > 99) intensity = 99;

  bool changed = false;
  if (antifoamOperation != op) changed = true;
  if (antifoamMix != mix) changed = true;
  if (antifoamIntensity != intensity) changed = true;

  antifoamOperation = op;
  antifoamMix = mix;
  antifoamIntensity = intensity;
  
  if (changed) flagAntiDirty = true;
}

void setPressure(int ref) {
  if (ref < 0) ref = 0;
  if (ref > 380) ref = 380; // Assuming 380 is hardware max
  
  if (pressureReference != ref) {
      pressureReference = ref;
      flagPressureDirty = true;
  }
}

// ------------------------------------------------------------------
// sendSensorCommand():
//   Função 'core' de I/O da UART. Protegida por Mutex.
// ------------------------------------------------------------------
String sendSensorCommand(const String &cmd, bool readResponse) {
  if (!uartSensorOK && millis() < uartCooldownUntil) {
    return "";
  }

  if (xSemaphoreTake(sensorSerialMutex, pdMS_TO_TICKS(300)) != pdTRUE) {
    ESP32_ERRO("Timeout ao obter mutex da UART do Módulo TECNAL");
    return "";
  }

  static String response;
  static bool responseReserved = false;
  if (!responseReserved) {
    response.reserve(64);
    responseReserved = true;
  }

  response = "";   // clear stale reply every call
  bool needSyncAfterUnlock = false;

  clearSensorRxLocked();

  sensorSerial.print(cmd);
  sensorSerial.flush();

  if (!readResponse) {
    const uint32_t ackWindowMs = 20;
    const uint32_t t0 = millis();
    while ((millis() - t0) < ackWindowMs) {
      while (sensorSerial.available() > 0) {
        sensorSerial.read();
      }
      vTaskDelay(pdMS_TO_TICKS(1));
    }
    xSemaphoreGive(sensorSerialMutex);
    return "";
  }

  const uint32_t globalTimeoutMs = 250;
  const uint32_t interByteTimeoutMs = 15;
  const uint32_t tStart = millis();
  uint32_t lastByteTime = tStart;
  bool gotAnyByte = false;
  bool lineComplete = false;

  while (!lineComplete && (millis() - tStart) < globalTimeoutMs) {
    while (sensorSerial.available() > 0) {
      char c = sensorSerial.read();
      gotAnyByte = true;
      lastByteTime = millis();

      if (c == '\r' || c == '\n') {
        if (response.length() > 0) {
          lineComplete = true;
          break;
        }
      } else {
        if (response.length() < (MAX_SENSOR_BUFFER_LENGTH - 1)) {
          response += c;
        } else {
          lineComplete = true;
          break;
        }
      }
    }

    if (gotAnyByte && (millis() - lastByteTime > interByteTimeoutMs)) {
      lineComplete = true;
    }

    if (!lineComplete) {
      vTaskDelay(pdMS_TO_TICKS(1));
    }
  }

  response.trim();
  bool valid = gotAnyByte && isValidSensorReply(response);

  if (valid) {
    if (!uartSensorOK) {
      needSyncAfterUnlock = true;
    }
    uartSensorOK = true;
    uartFailureCount = 0;
  } else {
    uartFailureCount++;
    if (uartFailureCount == 1) {
      ESP32_AVISO(String("Falha de leitura UART do Módulo TECNAL após comando '") + cmd + "'");
    }
    if (uartFailureCount >= UART_FAILURE_THRESHOLD) {
      uartFailureCount = UART_FAILURE_THRESHOLD;
      uartSensorOK = false;
      ESP32_AVISO("Limite de falhas UART atingido; reinicializando UART e iniciando cooldown de 5s");
      resetSensorUartLocked();
      uartCooldownUntil = millis() + UART_COOLDOWN_MS;
    }
    response = "";
  }

  xSemaphoreGive(sensorSerialMutex);

  if (needSyncAfterUnlock) {
    ESP32_EVT("Módulo TECNAL voltou a responder; sincronização agendada");
    syncAllSensorSettings();
  }

  if (cmd == "g" && response.length() > 0) {
    float rawValue = response.toFloat();
    float calibrated = a * rawValue + b;
    return bypassMode ? String(calibrated, 1) : String(rawValue, 4);
  }

  return response;
}

// ------------------------------------------------------------------
// removeDecimal():
// ------------------------------------------------------------------
String removeDecimal(String s) {
  s.replace(",", ".");
  s.replace(".", "");
  return s;
}

static void clearSensorRxLocked() {
  while (sensorSerial.available() > 0) {
    sensorSerial.read();
  }
}

static void resetSensorUartLocked() {
  sensorSerial.flush();
  clearSensorRxLocked();
  sensorSerial.end();
  vTaskDelay(pdMS_TO_TICKS(30));
  sensorSerial.begin(9600, SERIAL_8N1, SENSOR_RX_PIN, SENSOR_TX_PIN);
  clearSensorRxLocked();
}

// ------------------------------------------------------------------
// syncAllSensorSettings():
//   Forces the resending of all configurations to the hardware.
//   CORRECTED: Manually raises flags to bypass the "if(changed)" check
//   inside the setters during startup.
// ------------------------------------------------------------------
void syncAllSensorSettings() {
  ESP32_EVT("Sincronização das configurações com o Módulo TECNAL iniciada");
  vTaskDelay(pdMS_TO_TICKS(100));

  setTemperature(tempReference);
  flagTempDirty = true;

  setPH(pHReference, pHError, pHOperation, pHMix, pHIntensity);
  flagPhDirty = true;

  setNutrient(nutriOperation, nutriMix, nutriOpCycle, nutriMixCycle, nutriIntensity);
  flagNutriDirty = true;

  setAntifoam(antifoamOperation, antifoamMix, antifoamIntensity);
  flagAntiDirty = true;

  setPressure(pressureReference);
  flagPressureDirty = true;

  ESP32_INFO("Sincronização agendada; o loop principal enviará os comandos");

  if (biomassCommOn) {
    ESP32_INFO("Comando de início do sensor de biomassa reenfileirado");
    queueReliable(biomassBox, "\"start\":1", "Biomass");
  }
}
