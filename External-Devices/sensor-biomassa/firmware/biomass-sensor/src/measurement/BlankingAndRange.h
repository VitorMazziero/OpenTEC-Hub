// Blanking Routine
void blankCoolDown(uint32_t onMs, float dutyPct) {
  if (dutyPct <= 0.0f || dutyPct >= 100.0f) return;
  const uint32_t idleMs = (uint32_t)((float)onMs * (100.0f / dutyPct - 1.0f));
  const uint32_t start  = millis();
  while (millis() - start < idleMs) {
    if (g_abortRequested) return;
    const uint32_t left = idleMs - (millis() - start);
    delayServiced(left > 200 ? 200 : left);
  }
}

void runBlankingRoutine(float dutyPct) {
  g_state = BLANKING;
  g_abortRequested = false;
  const uint32_t sweepStartedMs = millis();
  const bool     paced          = (dutyPct > 0.0f);
  Serial.println("--- Starting Blanking Routine ---");
  if (paced) {
    Serial.print("Paced sweep at ");
    Serial.print(dutyPct, 1);
    Serial.println("% LED duty: slower, but every cell is measured under the "
                   "same thermal load.");
  }
  Serial.println("Ensure clear media is circulating. This will take a moment...");
  serviceNetwork();

  for (int i = 0; i < g_config.IT_COUNT; i++) {
    if (g_abortRequested) break;
    if (!vemlSetConfig(i)) {
      g_state = IDLE;
      return;
    } // I2C Error
    Serial.print("Setting IT: ");
    Serial.print(g_config.itDelays[i]);
    Serial.println("ms");
    serviceNetwork();
    bool isSaturated = false;

    for (int j = 0; j < g_config.PWM_COUNT; j++) {
      if (g_abortRequested) break;
      serviceNetwork();
      uint16_t reading = 0;
      if (isSaturated) {
        reading = 65535; // If saturated, mark all subsequent PWMs as saturated
      } else {
        uint16_t rawReading;
        if (!takePulsedReading(i, j, rawReading)) {
          g_state = IDLE;
          return;
        }

        reading = rawReading;

        if (reading >= SATURATION_RAW) {
          isSaturated = true;
          reading     = 65535;
        }

        // Only a cell that actually pulsed the LED owes a cool-down; the
        // skipped ones past saturation cost nothing to begin with.
        blankCoolDown(ledOnMsFor(g_config.itDelays[i]), dutyPct);
      }

      g_blankingData.blankValues[i][j] = reading;
      Serial.print("  PWM ");
      Serial.print(g_config.pwmSettings[j]);
      Serial.print("%: RAW = ");
      Serial.println(reading);
    }
  }

  pwmSetDutyPercent(0.0f);

  if (g_abortRequested) {
    // Abort leaves the previously stored blank untouched: a half-swept table
    // would silently corrupt every absorbance computed afterwards.
    Serial.println("--- Blanking ABORTED (previous blank data kept) ---");
    g_abortRequested = false;
    loadBlankingData(); // Restore whatever was last committed
    vemlSetConfig(0);
    g_state = IDLE;
    return;
  }

  saveBlankingData(); // Save to NVS

  g_blankSweepDutyPct = paced ? dutyPct : 0.0f;
  g_blankSweepMs      = millis() - sweepStartedMs;

  vemlSetConfig(0);
  g_blankIsDone = true;
  g_state       = IDLE;

  Serial.print("--- Blanking Complete (");
  Serial.print(g_blankSweepMs / 1000.0f, 1);
  Serial.println(" s) ---");
  Serial.println("Send JSON '{\"command\":\"start\"}' to begin measurement.");
}

// Auto-Ranging Measurement

uint32_t minSafeRefreshMs() {
  uint32_t itMs = 0;
  if (g_autoRange) {
    for (int i = 0; i < g_config.IT_COUNT; i++) {
      if (g_config.itDelays[i] > itMs) itMs = g_config.itDelays[i];
    }
  } else {
    itMs = g_config.itDelays[g_currentItIndex];
  }
  return (uint32_t)(ledOnMsFor(itMs) / LED_DUTY_LIMIT);
}

void enforceRefreshFloor(bool announce) {
  uint32_t floorMs = minSafeRefreshMs();
  bool changed = false;
  for (int i = 0; i < g_config.IT_COUNT; i++) {
    if (g_config.itRefreshTimes[i] < floorMs) {
      g_config.itRefreshTimes[i] = floorMs;
      changed = true;
    }
  }
  if (changed && announce) {
    Serial.print("Sampling interval raised to ");
    Serial.print(floorMs);
    Serial.println(" ms to keep LED duty within the thermal limit.");
  }
}

bool blankIsValid(int itIndex, int pwmIndex) {
  if (!g_blankIsDone) return false;
  uint16_t v = g_blankingData.blankValues[itIndex][pwmIndex];
  // The lower bound is not cosmetic. "v > 0" let cells of 9..109 counts pass
  // as usable gears -- the signature a pre-v4.6 sweep leaves behind -- and
  // the auto-ranger would then divide readings by an I_0 of 9.
  return (v >= MIN_VALID_BLANK && v < SATURATION_RAW);
}

bool findBrightestValidGear(int &itIndex, int &pwmIndex) {
  itIndex  = -1;
  pwmIndex = -1;
  uint16_t best = 0;
  for (int it = 0; it < g_config.IT_COUNT; it++) {
    for (int pwm = 0; pwm < g_config.PWM_COUNT; pwm++) {
      if (!blankIsValid(it, pwm)) continue;
      uint16_t v = g_blankingData.blankValues[it][pwm];
      if (v > best) {
        best     = v;
        itIndex  = it;
        pwmIndex = pwm;
      }
    }
  }
  return (itIndex >= 0);
}

void findOptimalBlankGear(int &bestItIndex, int &bestPwmIndex) {
  bestItIndex  = -1;
  bestPwmIndex = -1;
  uint16_t best = 0;

  // Preferred: brightest gear whose blank is inside the accepted band.
  for (int it = 0; it < g_config.IT_COUNT; it++) {
    for (int pwm = 0; pwm < g_config.PWM_COUNT; pwm++) {
      if (!blankIsValid(it, pwm)) continue;
      uint16_t v = g_blankingData.blankValues[it][pwm];
      if (v > g_config.HIGH_THRESHOLD_RAW) continue;
      if (v > best) {
        best         = v;
        bestItIndex  = it;
        bestPwmIndex = pwm;
      }
    }
  }

  // Every valid gear is above HIGH: the optics are brighter than the band
  // allows. Take the dimmest valid gear so we at least start unsaturated.
  if (bestItIndex < 0) {
    uint16_t lowest = 0xFFFF;
    for (int it = 0; it < g_config.IT_COUNT; it++) {
      for (int pwm = 0; pwm < g_config.PWM_COUNT; pwm++) {
        if (!blankIsValid(it, pwm)) continue;
        uint16_t v = g_blankingData.blankValues[it][pwm];
        if (v < lowest) {
          lowest       = v;
          bestItIndex  = it;
          bestPwmIndex = pwm;
        }
      }
    }
    if (bestItIndex >= 0) {
      Serial.println("Smart Start: no gear inside the threshold band; "
                     "using the dimmest valid gear.");
    }
  }

  if (bestItIndex < 0) {
    Serial.println("Smart Start: NO valid blank in any gear. Re-run 'blank'.");
    bestItIndex  = 0;
    bestPwmIndex = 0;
    return;
  }

  Serial.print("Smart Start: IT ");
  Serial.print(g_config.itDelays[bestItIndex]);
  Serial.print("ms, PWM ");
  Serial.print(g_config.pwmSettings[bestPwmIndex]);
  Serial.print("% (blank ");
  Serial.print(g_blankingData.blankValues[bestItIndex][bestPwmIndex]);
  Serial.print(", headroom ");
  Serial.print(log10f((float)g_blankingData.blankValues[bestItIndex][bestPwmIndex] /
                      (float)g_config.LOW_THRESHOLD_RAW), 2);
  Serial.println(" AU before first gear change)");
}

void findAndSetOptimalGear() {
  Serial.println("Signal out of range. Pausing to find optimal new gear...");
  g_state = SEARCHING;
  g_abortRequested = false;
  serviceNetwork();

  pwmSetDutyPercent(0.0f);

  int  bestScoreIT  = -1;
  int  bestScorePWM = -1;
  long bestScore    = -1000000;

  for (int it = 0; it < g_config.IT_COUNT; it++) {
    if (g_abortRequested) break;
    serviceNetwork();

    for (int pwm = 0; pwm < g_config.PWM_COUNT; pwm++) {
      if (g_abortRequested) break;
      serviceNetwork();

      // Skip gears we could never report absorbance for. Besides being
      // wrong to select, testing them wastes an integration period each,
      // so this also shortens the search.
      if (!blankIsValid(it, pwm)) continue;

      // 1. Set config (includes a serviced delay)
      if (!vemlSetConfig(it)) {
        g_state = IDLE;
        return;
      }

      // 2. Pulse Measurement (LED on, restart integration, wait, read, off)
      uint16_t newReading;
      if (!takePulsedReading(it, pwm, newReading)) {
        g_state = IDLE;
        return;
      }

      Serial.print("  Testing IT ");
      Serial.print(g_config.itDelays[it]);
      Serial.print("ms, PWM ");
      Serial.print(g_config.pwmSettings[pwm]);
      Serial.print("%: RAW = ");
      Serial.println(newReading);

      if (newReading >= SATURATION_RAW) {
        Serial.println("  Saturated. Skipping rest of this IT level.");
        g_saturationCount++;
        break; // Stop iterating PWMs for this IT
      }

      // Find the gear closest to OPTIMAL, preferring lower power.
      if (newReading > g_config.LOW_THRESHOLD_RAW &&
          newReading <= g_config.HIGH_THRESHOLD_RAW) {
        long score = -abs(g_config.OPTIMAL_TARGET_RAW - newReading);
        if (score > bestScore) {
          bestScore    = score;
          bestScoreIT  = it;
          bestScorePWM = pwm;
        }
      }
    } // end pwm loop
  } // end it loop

  if (g_abortRequested) {
    Serial.println("--- Gear search ABORTED ---");
    g_abortRequested = false;
    pwmSetDutyPercent(0.0f);
    g_state = IDLE;
    return;
  }

  if (bestScoreIT != -1) {
    Serial.print("Jumping to BEST gear (Closest to Optimal Target): IT ");
    Serial.print(g_config.itDelays[bestScoreIT]);
    Serial.print("ms, PWM ");
    Serial.print(g_config.pwmSettings[bestScorePWM]);
    Serial.println("%");
    vemlSetConfig(bestScoreIT);
    pwmSetLevel(bestScorePWM);
  } else {
    // Nothing landed in the band. Go as bright as we can while still being
    // able to compute absorbance -- NOT the absolute max gear, whose blank
    // is almost certainly saturated and would yield -99.
    int it, pwm;
    if (findBrightestValidGear(it, pwm)) {
      Serial.print("No reading in target range. Using brightest valid gear: IT ");
      Serial.print(g_config.itDelays[it]);
      Serial.print("ms, PWM ");
      Serial.print(g_config.pwmSettings[pwm]);
      Serial.println("%");
      vemlSetConfig(it);
      pwmSetLevel(pwm);
    } else {
      Serial.println("No valid blank in any gear. Re-run 'blank'.");
      vemlSetConfig(g_config.IT_COUNT - 1);
      pwmSetLevel(g_config.PWM_COUNT - 1);
    }
  }

  g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
  g_state        = MEASURING;
}

