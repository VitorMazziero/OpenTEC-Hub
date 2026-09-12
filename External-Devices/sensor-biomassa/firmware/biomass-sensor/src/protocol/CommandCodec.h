// JSON Parsing / Networking Helpers

const char* findJsonValueStart(const char* json, const char* key) {
  if (!json || !key) return nullptr;
  const size_t klen = strlen(key);
  const char* p = json;
  while ((p = strstr(p, key)) != nullptr) {
    if (p > json && *(p - 1) == '"' && *(p + klen) == '"') {
      const char* afterQuote = p + klen + 1;
      while (*afterQuote && isspace(static_cast<unsigned char>(*afterQuote))) afterQuote++;
      if (*afterQuote == ':') {
        const char* valStart = afterQuote + 1;
        while (*valStart && isspace(static_cast<unsigned char>(*valStart))) valStart++;
        return valStart;
      }
    }
    p += klen;
  }
  return nullptr;
}

long getJsonValue(const char* json, const char* key) {
  const char* val = findJsonValueStart(json, key);
  if (!val || *val == '"') return -999999;
  char* endPtr = nullptr;
  long result = strtol(val, &endPtr, 10);
  if (endPtr == val) return -999999;
  return result;
}

float getJsonFloat(const char* json, const char* key, bool &found) {
  found = false;
  const char* val = findJsonValueStart(json, key);
  if (!val || *val == '"') return 0.0f;
  char* endPtr = nullptr;
  float result = strtof(val, &endPtr);
  if (endPtr == val) return 0.0f;
  found = true;
  return result;
}

String getJsonStringValue(const char* json, const char* key) {
  const char* val = findJsonValueStart(json, key);
  if (!val || *val != '"') return "";
  val++; // skip quote
  const char* endQ = strchr(val, '"');
  if (!endQ) return "";
  return String(val).substring(0, endQ - val);
}

inline long   getJsonValue(const String& json, const String& key) { return getJsonValue(json.c_str(), key.c_str()); }
inline float  getJsonFloat(const String& json, const String& key, bool &found) { return getJsonFloat(json.c_str(), key.c_str(), found); }
inline String getJsonStringValue(const String& json, const String& key) { return getJsonStringValue(json.c_str(), key.c_str()); }


void setAutoRange(bool enabled) {
  g_autoRange = enabled;
  g_prefs.putBool(NVS_KEY_AUTO, enabled);
  if (enabled) {
    Serial.println("Auto-ranging ENABLED.");
    g_consecutiveLowReadings  = 0;
    g_consecutiveHighReadings = 0;
    g_consecutiveGearSearches = 0;
  } else {
    Serial.println("Auto-ranging DISABLED (manual gear lock).");
    g_highDensityMode = false;
  }
  // Re-enabling auto-ranging puts the long integration times back in play,
  // so an interval that was safe under a locked short IT may no longer be.
  enforceRefreshFloor(true);
}

void setManualGear(int itIndex, int pwmIndex) {
  if (itIndex < 0 || itIndex >= g_config.IT_COUNT ||
      pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) {
    Serial.println("Error: gear index out of range.");
    return;
  }
  vemlSetConfig(itIndex);
  pwmSetLevel(pwmIndex);
  if (g_state == IDLE) pwmSetDutyPercent(g_manualLedOn ? g_manualLedPct : 0.0f);
  Serial.print("Manual gear set: IT ");
  Serial.print(g_config.itDelays[itIndex]);
  Serial.print("ms, PWM ");
  Serial.print(g_config.pwmSettings[pwmIndex]);
  Serial.println("%");
}

void invalidateBlank(const char* reason) {
  g_blankIsDone = false;
  g_prefs.remove(NVS_KEY_BLANK);
  g_prefs.remove(NVS_KEY_BLANKFW);
  memset(&g_blankingData, 0, sizeof(g_blankingData));
  if (g_state == MEASURING || g_state == SEARCHING) {
    g_state = IDLE;
    pwmSetDutyPercent(0.0f);
  }
  Serial.print("!! BLANK INVALIDATED: ");
  Serial.println(reason);
  Serial.println("!! Run 'blank' again before measuring.");
}


void processJsonCommand(String json, bool allowBlocking) {
  Serial.println("[NET] Processing command: " + json);

  String cmd = getJsonStringValue(json, "command");

  // Also accept the {"key":1} shorthand form.
  if (cmd.length() == 0) {
    if (getJsonValue(json, "blank") == 1) cmd = "blank";
    else if (getJsonValue(json, "start") == 1) cmd = "start";
    else if (getJsonValue(json, "stop") == 1) cmd = "stop";
    else if (getJsonValue(json, "print_blank") == 1) cmd = "print_blank";
    else if (getJsonValue(json, "print_config") == 1) cmd = "print_config";
    else if (getJsonValue(json, "print_health") == 1) cmd = "print_health";
    else if (getJsonValue(json, "save_config") == 1) cmd = "save_config";
    else if (getJsonValue(json, "load_config") == 1) cmd = "load_config";
    else if (getJsonValue(json, "test_on") == 1) cmd = "test_on";
    else if (getJsonValue(json, "test_off") == 1) cmd = "test_off";
    else if (getJsonValue(json, "status") == 1) cmd = "status";
    else if (getJsonValue(json, "hub_on") == 1) cmd = "hub_on";
    else if (getJsonValue(json, "hub_off") == 1) cmd = "hub_off";
    else if (getJsonValue(json, "auto") == 1) cmd = "auto";
    else if (getJsonValue(json, "manual") == 1) cmd = "manual";
    else if (getJsonValue(json, "read_once") == 1) cmd = "read_once";
    else if (getJsonValue(json, "probe_period") == 1) cmd = "probe_period";
    else if (getJsonValue(json, "led_off") == 1) cmd = "led_off";
    else if (getJsonValue(json, "reset_health") == 1) cmd = "reset_health";
    else if (getJsonValue(json, "clear_history") == 1) cmd = "clear_history";
  }

  const bool busy = (g_state == BLANKING || g_state == SEARCHING);

  if (cmd.length() > 0) {
    if (cmd.equals("stop")) {
      if (busy) {
        Serial.println("--- Abort requested; unwinding current routine ---");
        g_abortRequested = true;
      } else if (g_state == MEASURING) {
        Serial.println("--- Stopping Measurement ---");
        g_state = IDLE;
        pwmSetDutyPercent(0.0f); // Ensure LED is off
        g_consecutiveGearSearches = 0;
        g_highDensityMode         = false;
      }
      return;
    }

    if (cmd.equals("status")) {
      Serial.println(buildStatusJson());
      return;
    }

    if (cmd.equals("history")) {
      long since = getJsonValue(json, "since");
      if (since < 0) since = 0;
      Serial.println(buildHistoryJson((uint32_t)since));
      return;
    }

    if (cmd.equals("clear_history")) {
      uint32_t dropped = (g_historyCount > (uint32_t)HISTORY_SIZE)
                             ? (uint32_t)HISTORY_SIZE
                             : g_historyCount;
      historyClear();
      Serial.print("--- Sample history cleared (");
      Serial.print(dropped);
      Serial.println(" sample(s) dropped) ---");
      Serial.println(buildStatusJson());
      return;
    }

    if (busy && (cmd.equals("blank") || cmd.equals("start") ||
                 cmd.equals("test_on") || cmd.equals("probe_period"))) {
      Serial.println("Error: Busy (blanking or searching). Send 'stop' first.");
      return;
    }

    // Anything that calls delayServiced() belongs here: those pump the web
    // server, which must not happen underneath an open request.
    if (!allowBlocking && (cmd.equals("blank") || cmd.equals("start") ||
                           cmd.equals("read_once") || cmd.equals("set_gear") ||
                           cmd.equals("probe_period"))) {
      // One slot. Overwriting it would silently drop a command the operator
      // sent -- refuse instead, so the caller sees what happened.
      if (g_pendingJson.length() > 0) {
        Serial.println("Error: a command is already queued. Try again.");
        return;
      }
      g_pendingJson = json;
      Serial.println("[NET] Deferred '" + cmd + "' to main loop.");
      return;
    }

    if (cmd.equals("blank")) {
      if (g_state == IDLE) {
        // No "duty_pct" is the pre-v5.2 back-to-back sweep, so an old client
        // gets exactly the behaviour it was written against.
        bool  found;
        float duty = getJsonFloat(json, "duty_pct", found);
        if (!found) {
          duty = 0.0f;
        } else if (duty < 1.0f) {
          duty = 1.0f;      // below this a sweep takes over 20 minutes
        } else if (duty > 60.0f) {
          duty = 60.0f;     // above this the pacing gains nothing: an
                            // unpaced sweep already runs at about 60 %
        }
        runBlankingRoutine(duty);
      } else {
        Serial.println("Error: Busy");
      }
    } else if (cmd.equals("start")) {
      if (!g_blankIsDone)
        Serial.println("Error: Please run 'blank' first.");
      else if (g_state == IDLE) {
        Serial.println("--- Starting Measurement ---");

        int startIt, startPwm;
        findOptimalBlankGear(startIt, startPwm); // Smart Start

        g_state        = MEASURING;
        g_nextReadTime = millis(); // Start first read immediately
        vemlSetConfig(startIt);
        pwmSetLevel(startPwm);

        g_consecutiveGearSearches = 0;
        g_highDensityMode         = false;
      }
    } else if (cmd.equals("hub_on")) {
      setHubEnabled(true);
    } else if (cmd.equals("hub_off")) {
      setHubEnabled(false);

    } else if (cmd.equals("auto")) {
      setAutoRange(true);
    } else if (cmd.equals("manual")) {
      setAutoRange(false);
    } else if (cmd.equals("set_gear")) {
      long it  = getJsonValue(json, "it");
      long pwm = getJsonValue(json, "pwm");
      if (it == -999999 || pwm == -999999) {
        Serial.println("Error: set_gear needs \"it\" and \"pwm\" indices.");
      } else {
        setManualGear((int)it, (int)pwm);
      }
    } else if (cmd.equals("read_once")) {
      if (g_state == IDLE) {
        readOnce();
      } else {
        Serial.println("Error: read_once allowed only in IDLE.");
      }
    } else if (cmd.equals("probe_period")) {
      // Diagnostic: measures what the sensor's conversion period really is,
      // rather than trusting the nominal integration time. Needs the LED, so
      // IDLE only.
      if (g_state != IDLE) {
        Serial.println("Error: probe_period allowed only in IDLE.");
      } else {
        long pwm = getJsonValue(json, "pwm");
        g_abortRequested = false;
        probeConversionPeriod(pwm == -999999 ? -1 : (int)pwm);
      }
    } else if (cmd.equals("led")) {
      if (g_state != IDLE) {
        Serial.println("Error: manual LED allowed only in IDLE.");
      } else {
        bool found;
        float duty = getJsonFloat(json, "duty", found);
        if (!found) {
          Serial.println("Error: led needs \"duty\" (0-100).");
        } else {
          if (duty < 0.0f) duty = 0.0f;
          if (duty > 100.0f) duty = 100.0f;
          g_ledTestEnable = false;     // manual duty overrides the sweep
          g_manualLedOn   = (duty > 0.0f);
          g_manualLedPct  = duty;
          pwmSetDutyPercent(duty);
          Serial.print("Manual LED duty: ");
          Serial.print(duty, 1);
          Serial.println("%");
        }
      }
    } else if (cmd.equals("led_off")) {
      g_manualLedOn  = false;
      g_manualLedPct = 0.0f;
      pwmSetDutyPercent(0.0f);
      Serial.println("Manual LED off.");
    } else if (cmd.equals("set_pwm")) {
      long idx = getJsonValue(json, "index");
      bool found;
      float v = getJsonFloat(json, "value", found);
      if (idx < 0 || idx >= g_config.PWM_COUNT || !found) {
        Serial.println("Error: set_pwm needs \"index\" (0-7) and \"value\" (0-100).");
      } else if (v < 0.0f || v > 100.0f) {
        Serial.println("Error: PWM value must be 0-100.");
      } else {
        g_config.pwmSettings[idx] = v;
        saveConfig();
        invalidateBlank("PWM level table changed");
      }
    } else if (cmd.equals("set_it")) {
      long idx  = getJsonValue(json, "index");
      long code = getJsonValue(json, "code"); // 0..5 -> 25,50,100,200,400,800 ms
      if (idx < 0 || idx >= g_config.IT_COUNT || code < 0 || code > 5) {
        Serial.println("Error: set_it needs \"index\" (0-3) and \"code\" (0-5).");
      } else {
        g_config.itSettings[idx] = (uint16_t)(IT_BITS[code] << 6);
        g_config.itDelays[idx]   = IT_MS[code];
        saveConfig();
        invalidateBlank("integration time table changed");
      }
    } else if (cmd.equals("pwm_preset")) {
      // One shot, one save, one blank invalidation -- setting the eight
      // levels individually would erase the blank eight times over.
      applyRecommendedPwmTable();
      saveConfig();
      invalidateBlank("LED level table reset to the recommended ladder");
    } else if (cmd.equals("reset_health")) {
      g_i2cErrorCount   = 0;
      g_saturationCount = 0;
      g_sensorResets    = 0;
      g_boundaryMisses  = 0;
      Serial.println("Health counters cleared.");
    } else if (cmd.equals("factory")) {
      g_prefs.remove(NVS_KEY_CONFIG);
      loadConfig();
      saveConfig();
      invalidateBlank("factory reset");
      setAutoRange(true);
      g_emaAlpha = 0.8f;
      g_prefs.putFloat(NVS_KEY_EMA, g_emaAlpha);
      Serial.println("Factory defaults restored.");
    } else if (cmd.equals("print_blank")) {
      Serial.println(buildBlankJson());
    } else if (cmd.equals("print_config")) {
      Serial.println(buildStatusJson());
    } else if (cmd.equals("print_health")) {
      Serial.println(buildStatusJson());
    } else if (cmd.equals("save_config")) {
      saveConfig();
    } else if (cmd.equals("load_config")) {
      loadConfig();
    } else if (cmd.equals("test_on")) {
      if (g_state != IDLE) {
        Serial.println("Error: test allowed only in IDLE");
      } else {
        g_ledTestEnable = true;
        g_ledTestPct    = 0.0f;
        g_ledTestDir    = 1;
        g_ledTestNextMs = millis();
        Serial.println("LED test enabled");
      }
    } else if (cmd.equals("test_off")) {
      g_ledTestEnable = false;
      pwmSetDutyPercent(0.0f); // Ensure LED is off
      Serial.println("LED test disabled");
    }
  } // end if(cmd)

  // Check for numeric settings
  long val = getJsonValue(json, "low");
  if (val != -999999) {
    g_config.LOW_THRESHOLD_RAW = (uint16_t)val;
    Serial.print("Live config: LOW_THRESHOLD_RAW set to ");
    Serial.println(g_config.LOW_THRESHOLD_RAW);
  }

  val = getJsonValue(json, "high");
  if (val != -999999) {
    g_config.HIGH_THRESHOLD_RAW = (uint16_t)val;
    Serial.print("Live config: HIGH_THRESHOLD_RAW set to ");
    Serial.println(g_config.HIGH_THRESHOLD_RAW);
  }

  val = getJsonValue(json, "opt");
  if (val != -999999) {
    g_config.OPTIMAL_TARGET_RAW = (uint16_t)val;
    Serial.print("Live config: OPTIMAL_TARGET_RAW set to ");
    Serial.println(g_config.OPTIMAL_TARGET_RAW);
  }

  val = getJsonValue(json, "test_period");
  if (val != -999999) {
    if (val < 5) val = 5;
    g_ledTestPeriodMs = static_cast<uint32_t>(val);
    Serial.print("LED test period set to ");
    Serial.print(g_ledTestPeriodMs);
    Serial.println(" ms");
  }

  val = getJsonValue(json, "refresh_ms");
  if (val != -999999) {
    if (val > 3600000L) val = 3600000L;
    long floorMs = (long)minSafeRefreshMs();
    if (val < floorMs) {
      Serial.print("Requested interval ");
      Serial.print(val);
      Serial.print(" ms exceeds the LED thermal duty limit; clamped to ");
      Serial.print(floorMs);
      Serial.println(" ms.");
      val = floorMs;
    }
    for (int i = 0; i < g_config.IT_COUNT; i++) {
      g_config.itRefreshTimes[i] = (uint32_t)val;
    }
    Serial.print("Sampling interval set to ");
    Serial.print(val);
    Serial.println(" ms");
  }

  bool foundF;
  float f = getJsonFloat(json, "ema", foundF);
  if (foundF) {
    if (f <= 0.0f) f = 0.01f;
    if (f > 1.0f)  f = 1.0f;
    g_emaAlpha = f;
    g_prefs.putFloat(NVS_KEY_EMA, g_emaAlpha);
    Serial.print("EMA alpha set to ");
    Serial.println(g_emaAlpha, 2);
  }
}

void handleSerialInput(bool allowBlocking) {
  static char   buf[256];
  static size_t idx = 0;
  static bool   inPump = false;

  if (inPump) return;
  inPump = true;

  while (Serial.available() > 0) {
    int c = Serial.read();
    if (c == '\r') continue;
    if (c == '\n') {
      buf[idx] = '\0';
      idx      = 0;

      String cmd = String(buf);
      cmd.trim();
      if (cmd.length() > 0 && cmd.startsWith("{") && cmd.endsWith("}")) {
        processJsonCommand(cmd, allowBlocking);
      } else if (cmd.length() > 0) {
        Serial.println("Error: Command must be in JSON format. e.g. {\"command\":\"start\"}");
      }
    } else {
      if (idx < sizeof(buf) - 1) {
        buf[idx++] = static_cast<char>(c);
      } else {
        idx = 0; // overflow guard
      }
    }
  }
  inPump = false;
}

