// NVS (Storage) Functions

void loadConfig() {
  if (g_prefs.getBytes(NVS_KEY_CONFIG, &g_config, sizeof(DeviceConfig)) ==
      sizeof(DeviceConfig)) {
    uint32_t crc = calculateCRC32((uint8_t*)&g_config,
                                  sizeof(DeviceConfig) - sizeof(uint32_t));
    if (crc == g_config.crc32) {
      Serial.println("Loaded config from NVS.");

      bool itRepaired = false;
      for (int i = 0; i < g_config.IT_COUNT; i++) {
        uint16_t want = itRegisterFor(g_config.itDelays[i]);
        if (want == 0xFFFF) {
          // Stored delay is not a value the sensor supports at all.
          g_config.itDelays[i]   = IT_MS[i + 2 < IT_CHOICE_COUNT ? i + 2 : i];
          want = itRegisterFor(g_config.itDelays[i]);
        }
        if (g_config.itSettings[i] != want) {
          Serial.printf("  IT slot %d: register 0x%04X -> 0x%04X (%lu ms)\n",
                        i, g_config.itSettings[i], want,
                        (unsigned long)g_config.itDelays[i]);
          g_config.itSettings[i] = want;
          itRepaired = true;
        }
      }
      if (itRepaired) {
        Serial.println("!! Integration-time codes repaired (v2.5..v4.1 bug).");
        Serial.println("!! Stored blank discarded -- it was taken at the "
                       "wrong integration times. Re-run 'blank'.");
        g_prefs.remove(NVS_KEY_BLANK);
        g_prefs.remove(NVS_KEY_BLANKFW);
        saveConfig();
      }
      uint32_t floorMs = minSafeRefreshMs();
      bool needsSave = false;
      for (int i = 0; i < g_config.IT_COUNT; i++) {
        if (g_config.itRefreshTimes[i] < floorMs) {
          g_config.itRefreshTimes[i] = floorMs;
          needsSave = true;
        }
      }
      if (needsSave) {
        Serial.print("Stored sampling interval raised to ");
        Serial.print(floorMs);
        Serial.println(" ms (LED thermal duty limit).");
        saveConfig();
      }
      return;
    }
  }

  Serial.println("No valid config in NVS. Loading defaults.");
  g_config.LOW_THRESHOLD_RAW  = 10000;
  g_config.HIGH_THRESHOLD_RAW = 40000;
  g_config.OPTIMAL_TARGET_RAW = 25000;

  g_config.itDelays[0] = 100;
  g_config.itDelays[1] = 200;
  g_config.itDelays[2] = 400;
  g_config.itDelays[3] = 800;
  for (int i = 0; i < g_config.IT_COUNT; i++) {
    g_config.itSettings[i] = itRegisterFor(g_config.itDelays[i]);
  }

  // DEFAULT_REFRESH_MS, or the thermal floor if that is somehow higher (a
  // hand-edited IT table could push it there). Never below the floor.
  {
    uint32_t floorMs = minSafeRefreshMs();
    uint32_t startMs = (DEFAULT_REFRESH_MS > floorMs) ? DEFAULT_REFRESH_MS
                                                      : floorMs;
    for (int i = 0; i < g_config.IT_COUNT; i++) {
      g_config.itRefreshTimes[i] = startMs;
    }
  }

  applyRecommendedPwmTable();
}

void applyRecommendedPwmTable() {
  static const float LADDER[DeviceConfig::PWM_COUNT] = {
      2.0f, 3.5f, 6.0f, 10.5f, 18.0f, 32.0f, 57.0f, 100.0f};
  for (int i = 0; i < g_config.PWM_COUNT; i++) {
    g_config.pwmSettings[i] = LADDER[i];
  }
}

void saveConfig() {
  g_config.crc32 = calculateCRC32((uint8_t*)&g_config,
                                  sizeof(DeviceConfig) - sizeof(uint32_t));
  if (g_prefs.putBytes(NVS_KEY_CONFIG, &g_config, sizeof(DeviceConfig))) {
    Serial.println("Config saved to NVS.");
  } else {
    Serial.println("Error saving config to NVS.");
  }
}

bool loadBlankingData() {
  if (g_prefs.getBytes(NVS_KEY_BLANK, &g_blankingData, sizeof(BlankingData)) ==
      sizeof(BlankingData)) {
    uint32_t crc = calculateCRC32(
        (uint8_t*)&g_blankingData, sizeof(BlankingData) - sizeof(uint32_t));
    if (crc == g_blankingData.crc32) {
      // A stored blank passes CRC no matter which firmware measured it, so
      // CRC alone cannot tell a good table from one taken by a measurement
      // path that has since been corrected. The epoch can.
      uint32_t epoch = g_prefs.getUInt(NVS_KEY_BLANKFW, 0);
      if (epoch != BLANK_EPOCH) {
        Serial.print("!! Stored blank was taken by measurement epoch ");
        Serial.print(epoch);
        Serial.print(", this firmware is epoch ");
        Serial.println(BLANK_EPOCH);
        Serial.println("!! Discarded -- its readings are not comparable with "
                       "what this build measures. Re-run 'blank'.");
        g_prefs.remove(NVS_KEY_BLANK);
        memset(&g_blankingData, 0, sizeof(g_blankingData));
        g_blankIsDone = false;
        return false;
      }
      Serial.print("Loaded blanking data from NVS (Timestamp: ");
      Serial.print(g_blankingData.timestamp);
      Serial.println(")");
      g_blankIsDone = true;
      return true;
    }
  }
  Serial.println("No valid blanking data in NVS.");
  g_blankIsDone = false;
  return false;
}

void saveBlankingData() {
  g_blankingData.timestamp = millis(); // Simple timestamp
  g_blankingData.crc32 = calculateCRC32(
      (uint8_t*)&g_blankingData, sizeof(BlankingData) - sizeof(uint32_t));
  if (g_prefs.putBytes(NVS_KEY_BLANK, &g_blankingData, sizeof(BlankingData))) {
    g_prefs.putUInt(NVS_KEY_BLANKFW, BLANK_EPOCH);
    Serial.println("Blanking data saved to NVS.");
  } else {
    Serial.println("Error saving blanking data to NVS.");
  }
}

void setHubEnabled(bool enabled) {
  g_hubEnabled = enabled;
  g_prefs.putBool(NVS_KEY_HUB_EN, enabled);
  if (enabled) {
    Serial.println("[NET] Hub mode ENABLED (STA scan/push/poll active).");
    g_wifiState        = WF_IDLE;
    g_wifiNextActionMs = 0;
  } else {
    Serial.println("[NET] Hub mode DISABLED (direct control only).");
    WiFi.disconnect(false, false);
    g_lastKnownSsid = "";
    g_wifiState     = WF_IDLE;
  }
}

