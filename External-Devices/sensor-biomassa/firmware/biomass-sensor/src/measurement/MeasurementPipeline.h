void runMeasurementLoop() {

  uint16_t rawReading;
  if (!takePulsedReading(g_currentItIndex, g_currentPwmIndex, rawReading)) {
    return;
  }

  if (g_highDensityMode && g_autoRange) {
    // In High Density Mode, we only check if the signal has RECOVERED
    if (rawReading > g_config.OPTIMAL_TARGET_RAW) {
      Serial.println("--- High Density Mode Deactivated. Rescanning... ---");
      g_highDensityMode         = false;
      g_consecutiveGearSearches = 0;
      findAndSetOptimalGear();
      return;
    }
  }

  g_lastSat = (rawReading >= SATURATION_RAW);
  if (g_lastSat) {
    g_saturationCount++;
  }

  uint16_t medianReading = getFilteredReading(rawReading);

  if (!g_filterIsPrimed) {
    g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
    return;
  }

  if (!g_emaFilterIsPrimed) {
    g_emaFilteredRaw    = (float)medianReading;
    g_emaFilterIsPrimed = true;
  } else {
    // alpha is operator-adjustable; 1.0 disables the low-pass entirely
    g_emaFilteredRaw = (g_emaAlpha * (float)medianReading) +
                       ((1.0f - g_emaAlpha) * g_emaFilteredRaw);
  }
  g_lastAlsRaw = (uint16_t)(g_emaFilteredRaw + 0.5f);

  bool configChanged = false;

  // Manual mode locks the gear: no searches, no High Density Mode.
  if (g_autoRange && !g_highDensityMode) {

    if (g_lastAlsRaw < g_config.LOW_THRESHOLD_RAW && g_lastAlsRaw > 0) {
      g_consecutiveLowReadings++;
      g_consecutiveHighReadings = 0;
      if (g_consecutiveLowReadings >= 10) {
        g_consecutiveGearSearches++; // Increment failed search counter
        if (g_consecutiveGearSearches >= 3) {
          Serial.println("--- High Density Mode Activated (3 failed searches) ---");
          g_highDensityMode         = true;
          g_consecutiveGearSearches = 0;
          g_consecutiveLowReadings  = 0;
          // Brightest gear we can still compute absorbance at. The absolute
          // max gear used previously has a saturated blank in any normal
          // calibration, so HDM used to silently turn every reading into -99.
          int hdIt, hdPwm;
          if (findBrightestValidGear(hdIt, hdPwm)) {
            vemlSetConfig(hdIt);
            pwmSetLevel(hdPwm);
          } else {
            vemlSetConfig(g_config.IT_COUNT - 1);
            pwmSetLevel(g_config.PWM_COUNT - 1);
          }
          configChanged = true;
        } else {
          findAndSetOptimalGear();
          return; // findAndSetOptimalGear will change state
        }
      }
    }
    else if (g_lastAlsRaw > g_config.HIGH_THRESHOLD_RAW) {
      g_consecutiveHighReadings++;
      g_consecutiveLowReadings  = 0;
      g_consecutiveGearSearches = 0;

      if (g_consecutiveHighReadings >= 10) {
        findAndSetOptimalGear();
        return;
      }
    }
    else {
      g_consecutiveLowReadings  = 0;
      g_consecutiveHighReadings = 0;
      g_consecutiveGearSearches = 0;
    }
  }

  if (configChanged) {
    // Only entered when HDM is first activated
    g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
    return;
  }

  publishSample(/*single=*/false);

  // Schedule the next read
  g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
}

void publishSample(bool single) {
  uint16_t current_I0 =
      g_blankingData.blankValues[g_currentItIndex][g_currentPwmIndex];
  uint16_t current_I = g_lastAlsRaw;

  g_lastI0 = current_I0;

  if (current_I0 == 0 || current_I0 == 65535) {
    g_lastAbsorbance = -99.0f; // Error: Blank is 0 or Saturated
  } else {
    if (current_I > current_I0) {
      current_I = current_I0; // Cap reading at blank value
    }
    if (current_I == 0) {
      g_lastAbsorbance = 9.9f; // Error: True zero reading
    } else {
      // Absorbance = -log10( I / I_0 )
      g_lastAbsorbance = -log10((float)current_I / (float)current_I0);
    }
  }

  g_seq++;
  g_lastSampleMs = millis();
  g_lastSingle   = single;

  Sample s;
  s.seq        = g_seq;
  s.t_ms       = g_lastSampleMs;
  s.absorbance = g_lastAbsorbance;
  s.raw        = g_lastAlsRaw;
  s.i0         = g_lastI0;
  s.itIdx      = (uint8_t)g_currentItIndex;
  s.pwmIdx     = (uint8_t)g_currentPwmIndex;
  s.flags      = (uint8_t)((g_highDensityMode ? SF_HD_MODE : 0) |
                           (g_lastSat ? SF_SATURATED : 0) |
                           (single ? SF_SINGLE : 0) |
                           (g_autoRange ? 0 : SF_MANUAL));
  s._pad       = 0;
  historyPush(s);

  buildDataJson();
  Serial.println(g_lastDataJson);

  if (g_hubEnabled && WiFi.status() == WL_CONNECTED) {
    sendDataToHub();
  }
}

void readOnce() {
  uint16_t rawReading;
  if (!takePulsedReading(g_currentItIndex, g_currentPwmIndex, rawReading,
                         /*holdLed=*/true)) {
    return;
  }

  g_lastSat    = (rawReading >= SATURATION_RAW);
  if (g_lastSat) g_saturationCount++;
  g_lastAlsRaw = rawReading;

  publishSample(/*single=*/true);
}

