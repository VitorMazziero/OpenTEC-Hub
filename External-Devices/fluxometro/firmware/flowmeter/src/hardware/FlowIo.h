bool writeFlowSetpointToDAC(float flowSetpointVal) {
  uint16_t dacValue = (flowSetpointVal / maxFlowRate) * 4095;
  dacValue = constrain(dacValue, 0, 4095);
  if (i2cMutex != NULL && xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(250)) == pdTRUE) {
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

  if (readFlowVoltage <= 0.0545f) {
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
