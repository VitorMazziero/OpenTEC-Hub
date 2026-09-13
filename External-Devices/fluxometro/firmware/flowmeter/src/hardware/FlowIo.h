bool writeFlowSetpointToDAC(float flowSetpointVal) {
  if (hardwareFaultLatched || !dacHealthy) return false;

  if (isnan(flowSetpointVal) || flowSetpointVal <= 0.0f) {
    flowSetpointVal = 0.0f;
  }
  float effectiveMax = maxFlowRate;
  if (isnan(effectiveMax) || effectiveMax <= 0.01f) {
    effectiveMax = 50.0f; // Safe fallback para evitar divisao por zero
    Serial.println("[DAC] Aviso: maxFlowRate invalido <= 0.01, adotado fallback 50.0 L/min");
  }

  // Normalizacao estrita no dominio float [0.0, 1.0] antes de conversao para DAC 12 bits
  float fraction = flowSetpointVal / effectiveMax;
  fraction = constrain(fraction, 0.0f, 1.0f);
  uint16_t dacValue = (uint16_t)constrain((fraction * 4095.0f) + 0.5f, 0.0f, 4095.0f);

  if (i2cMutex != NULL && xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(250)) == pdTRUE) {
    Wire.beginTransmission(0x60);
    byte err = Wire.endTransmission();
    if (err != 0) {
      Serial.printf("[DAC] Falha de comunicacao I2C (%d). Latched!\n", err);
      dacHealthy = false;
      hardwareFaultLatched = true;
      xSemaphoreGive(i2cMutex);
      return false;
    }
    mcp.setVoltage(dacValue, false);
    xSemaphoreGive(i2cMutex);
    return true;
  }
  Serial.println("[DAC] I2C busy, write deferred");
  return false;
}

void readAndProcessADC() {
  if (i2cMutex == NULL) return;
  float sumVolts = 0.0;
  uint8_t got = 0;
  // The mutex is taken per conversion (~8 ms), never for the whole burst: the DAC
  // write waits at most one conversion instead of the 250 ms it used to lose.
  for (uint8_t i = 0; i < ADC_SAMPLES_PER_CYCLE; i++) {
    if (xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(50)) != pdTRUE) continue;
    int16_t raw = ads.readADC_SingleEnded(3); // A3
    xSemaphoreGive(i2cMutex);
    sumVolts += ads.computeVolts(raw);
    got++;
  }
  if (got == 0) return;
  float avgVolts = sumVolts / got;

  readFlowVoltage = lowPassFilter(avgVolts, flowFilterAlpha);

  if (readFlowVoltage <= flowTransitionVoltage) {
    // Horner form reduces floating-point cancellation at millivolt inputs.
    readFlowRate = ((((a1 * readFlowVoltage + b1) * readFlowVoltage + k1)
                    * readFlowVoltage + f1) * readFlowVoltage + c1);
  } else {
    readFlowRate = k2 * sq(readFlowVoltage) + f2 * readFlowVoltage + c2;
  }
  if (readFlowRate < 0.0f) readFlowRate = 0.0f;
}

void startLEDBlinking() {
  ledBlinking = true;
  blinkStartTime = millis();
  blinkCount = 0;
  digitalWrite(RECEIVER_LED, HIGH);
}
void updateLEDBlinking() {
  if (ledBlinking) {
    if (millis() - blinkStartTime >= blinkInterval) {
      blinkStartTime = millis();
      digitalWrite(RECEIVER_LED, !digitalRead(RECEIVER_LED));
      blinkCount++;
      if (blinkCount >= 4) {
        ledBlinking = false;
        digitalWrite(RECEIVER_LED, LOW);
      }
    }
  }
}
float lowPassFilter(float newValue, float alpha) {
  float filteredValue = alpha * newValue + (1 - alpha) * lowPassPreviousFilteredValue;
  lowPassPreviousFilteredValue = filteredValue;
  return filteredValue;
}
