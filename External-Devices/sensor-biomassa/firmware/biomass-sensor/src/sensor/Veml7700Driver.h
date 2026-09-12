// Fault Handling
bool resetSensor() {
  g_sensorResets++;
  Serial.println("! FATAL: Attempting I2C sensor reset...");
  Wire.end();
  delay(100);
  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
  Wire.setClock(100000);
  delay(5);

  uint16_t config_val = VEML_GAIN_2 | g_config.itSettings[0] | VEML_ALS_ENABLE;

  Wire.beginTransmission(VEML7700_ADDR);
  Wire.write(REG_ALS_CONF);
  Wire.write(config_val & 0xFF);
  Wire.write((config_val >> 8) & 0xFF);
  if (Wire.endTransmission() == 0) {
    Serial.println("... Sensor reset successful.");
    g_currentItIndex = 0;
    resetReadingFilter();
    return true;
  } else {
    Serial.println("... Sensor reset FAILED.");
    return false;
  }
}

void handleI2CError() {
  g_i2cErrorCount++;
  Serial.print("! I2C Read Error. Total errors: ");
  Serial.println(g_i2cErrorCount);

  if (g_i2cErrorCount % 5 == 0) { // Try to reset sensor every 5 errors
    resetSensor();
  }
  g_state = IDLE; // Drop to idle on error
  Serial.println("Dropping to IDLE state. Send JSON '{\"command\":\"start\"}' to retry.");
}

// VEML7700 Sensor Helpers
bool vemlWrite16(uint8_t reg, uint16_t val) {
  Wire.beginTransmission(VEML7700_ADDR);
  Wire.write(reg);
  Wire.write(val & 0xFF);
  Wire.write((val >> 8) & 0xFF);
  if (Wire.endTransmission() != 0) {
    return false; // I2C Error
  }
  return true;
}

bool vemlRead16(uint8_t reg, uint16_t &value) {
  Wire.beginTransmission(VEML7700_ADDR);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom(VEML7700_ADDR, static_cast<uint8_t>(2)) != 2) return false;
  uint16_t lo = Wire.read();
  uint16_t hi = Wire.read();
  value = static_cast<uint16_t>((hi << 8) | lo);
  return true;
}

bool vemlSetConfig(int itIndex) {
  if (itIndex < 0 || itIndex >= g_config.IT_COUNT) return false;
  g_currentItIndex = itIndex;
  uint16_t config_val =
      VEML_GAIN_2 | g_config.itSettings[itIndex] | VEML_ALS_ENABLE;

  if (!vemlWrite16(REG_ALS_CONF, config_val)) {
    handleI2CError();
    return false;
  }

  resetReadingFilter(); // Reset filter on any "gear change"

  g_consecutiveLowReadings  = 0;
  g_consecutiveHighReadings = 0;

  delayServiced(g_config.itDelays[itIndex] + 5); // Wait for integration to apply
  return true;
}

uint32_t waitForConversionBoundary(uint16_t baseline, uint32_t timeoutMs,
                                   uint16_t &value) {
  const uint32_t t0 = millis();
  while (millis() - t0 < timeoutMs) {
    delayServiced(BOUNDARY_POLL_MS);
    uint16_t v;
    if (!vemlRead16(REG_ALS_DATA_L, v)) return 0;  // caller's read reports it
    int32_t d = (int32_t)v - (int32_t)baseline;
    if (d > (int32_t)BOUNDARY_DELTA || d < -(int32_t)BOUNDARY_DELTA) {
      value = v;
      return millis();
    }
  }
  return 0;
}

bool takePulsedReading(int itIndex, int pwmIndex, uint16_t &out,
                       bool holdLed) {
  if (itIndex < 0 || itIndex >= g_config.IT_COUNT) return false;

  const uint32_t guardMs = integrationGuardMs(g_config.itDelays[itIndex]);

  // (1) A dark conversion must have completed since the LED was last on, or
  // the baseline in (3) is the previous pulse's bright value and no jump is
  // visible. The LED is off for this, so it adds wall time but no heating.
  const uint32_t darkFor = millis() - g_ledOffSinceMs;
  if (darkFor < guardMs) delayServiced(guardMs - darkFor);

  // (2)
  if (pwmIndex >= 0) pwmSetLevel(pwmIndex);
  delayServiced(LED_SETTLE_MS);

  uint16_t baseline = 0;
  if (!vemlRead16(REG_ALS_DATA_L, baseline)) {
    pwmSetDutyPercent(holdLed && g_manualLedOn ? g_manualLedPct : 0.0f);
    handleI2CError();
    return false;
  }

  // (3)
  uint16_t atBoundary = 0;
  const uint32_t tBoundary =
      waitForConversionBoundary(baseline, guardMs, atBoundary);

  // (4)
  if (tBoundary != 0) {
    const uint32_t since = millis() - tBoundary;
    if (since < guardMs) delayServiced(guardMs - since);
  } else {
    // Search already spent one guarded period, so this second one puts the
    // read at 2 x guard after LED-on -- still correct for any real period
    // within the guard, just without the anchor's independence from it.
    g_boundaryMisses++;
    delayServiced(guardMs);
  }

  bool ok = vemlRead16(REG_ALS_DATA_L, out);
  pwmSetDutyPercent(holdLed && g_manualLedOn ? g_manualLedPct : 0.0f);
  if (!ok) {
    handleI2CError();
    return false;
  }
  return true;
}

void probeConversionPeriod(int pwmIndex) {
  constexpr int TRIALS = 6;
  if (pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) {
    pwmIndex = g_currentPwmIndex;
  }
  const int savedIt = g_currentItIndex;

  for (int i = 0; i < g_config.IT_COUNT; i++) {
    const uint32_t nominal = g_config.itDelays[i];
    const uint32_t guardMs = integrationGuardMs(nominal);
    if (!vemlSetConfig(i)) return;

    uint32_t sum = 0;
    int      ok  = 0;
    uint32_t lo  = 0xFFFFFFFF, hi = 0;

    for (int t = 0; t < TRIALS && !g_abortRequested; t++) {
      pwmSetDutyPercent(0.0f);
      delayServiced(guardMs);              // guarantee a dark conversion

      pwmSetLevel(pwmIndex);
      delayServiced(LED_SETTLE_MS);

      uint16_t dark = 0;
      if (!vemlRead16(REG_ALS_DATA_L, dark)) { handleI2CError(); return; }

      uint16_t vPartial = 0;
      uint32_t t1 = waitForConversionBoundary(dark, guardMs, vPartial);
      uint32_t t2 = 0;
      uint16_t vFull = 0;
      if (t1 != 0) {
        // Second step: from the partial conversion to the full one.
        t2 = waitForConversionBoundary(vPartial, guardMs, vFull);
      }
      pwmSetDutyPercent(0.0f);

      if (t1 != 0 && t2 != 0) {
        uint32_t p = t2 - t1;
        sum += p;
        ok++;
        if (p < lo) lo = p;
        if (p > hi) hi = p;
      }
    }

    String json = "{\"probe\":\"period\",\"it_index\":";
    json += String(i);
    json += ",\"nominal_ms\":";
    json += String(nominal);
    // Paired with the measurement, not sampled separately, so a period and
    // the temperature it was measured at can never be mismatched.
    json += ",\"temp_c\":";
    json += String(temperatureRead(), 1);
    json += ",\"trials\":";
    json += String(TRIALS);
    json += ",\"resolved\":";
    json += String(ok);
    if (ok > 0) {
      const float mean = (float)sum / (float)ok;
      json += ",\"period_ms\":";
      json += String(mean, 1);
      json += ",\"min_ms\":";
      json += String(lo);
      json += ",\"max_ms\":";
      json += String(hi);
      json += ",\"ratio\":";
      json += String(mean / (float)nominal, 3);
      json += ",\"within_guard\":";
      json += ((hi <= (uint32_t)(IT_PERIOD_GUARD * (float)nominal)) ? "true"
                                                                    : "false");
    }
    json += ",\"guard\":";
    json += String(IT_PERIOD_GUARD, 2);
    json += ",\"poll_ms\":";
    json += String(BOUNDARY_POLL_MS);
    json += "}";
    Serial.println(json);
  }

  vemlSetConfig(savedIt);
  pwmSetDutyPercent(0.0f);
}

// PWM Helpers
void pwmSetDutyPercent(float percent) {
  if (percent < 0.0f) percent = 0.0f;
  if (percent > 100.0f) percent = 100.0f;

  const uint32_t maxCount = (1u << LEDC_RES_BITS) - 1u;
  const uint32_t duty =
      static_cast<uint32_t>(percent * maxCount / 100.0f + 0.5f);

  if (percent <= 0.0f && g_targetPct > 0.0f) g_ledOffSinceMs = millis();

  analogWrite(LED_PWM_PIN, duty);
  g_targetPct = percent;
}

void pwmSetLevel(int pwmIndex) {
  if (pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) return;
  g_currentPwmIndex = pwmIndex;
  pwmSetDutyPercent(g_config.pwmSettings[pwmIndex]);
  // Filter reset happens in vemlSetConfig(); doing it here would clobber the
  // window between the LED turning on and the read.
}

