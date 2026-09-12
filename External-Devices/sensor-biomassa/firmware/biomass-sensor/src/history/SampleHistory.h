
void historyPush(const Sample& s) {
  g_history[g_historyHead] = s;
  g_historyHead = (g_historyHead + 1) % HISTORY_SIZE;
  g_historyCount++;
}

void historyClear() {
  g_historyHead  = 0;
  g_historyCount = 0;
}

String buildHistoryJson(uint32_t sinceSeq) {
  // Oldest sample still in the buffer. Once wrapped, the write head points
  // at it; before that the buffer is simply filled from index 0.
  const int readPos = (g_historyCount > (uint32_t)HISTORY_SIZE)
                          ? g_historyHead
                          : 0;

  const uint32_t stored = (g_historyCount > (uint32_t)HISTORY_SIZE)
                              ? (uint32_t)HISTORY_SIZE
                              : g_historyCount;

  // One reserve up front instead of ~60 reallocs; this runs on every poll,
  // and repeated grow-and-copy of a multi-kB String fragments the heap.
  String json;
  json.reserve(8192);
  json += "{\"boot_id\":";
  json += String(g_bootId);
  json += ",\"seq\":";
  json += String(g_seq);
  json += ",\"first_seq\":";
  json += String(stored ? g_history[readPos].seq : g_seq + 1);
  json += ",\"samples\":[";

  int emitted = 0;

  for (uint32_t i = 0; i < stored; i++) {
    const Sample& s = g_history[(readPos + i) % HISTORY_SIZE];
    if (s.seq <= sinceSeq) continue;
    if (emitted >= HISTORY_MAX_RESPONSE) break;

    if (emitted > 0) json += ",";
    json += "{\"seq\":";
    json += String(s.seq);
    json += ",\"t_ms\":";
    json += String(s.t_ms);
    json += ",\"absorbance\":";
    json += String(s.absorbance, ABSORBANCE_DECIMALS);
    json += ",\"raw\":";
    json += String(s.raw);
    json += ",\"i0\":";
    json += String(s.i0);
    json += ",\"it_ms\":";
    json += String(g_config.itDelays[s.itIdx]);
    json += ",\"pwm_pct\":";
    json += String(g_config.pwmSettings[s.pwmIdx], 1);
    json += ",\"hd_mode\":";
    json += (s.flags & SF_HD_MODE) ? "true" : "false";
    json += ",\"sat\":";
    json += (s.flags & SF_SATURATED) ? "true" : "false";
    json += ",\"single\":";
    json += (s.flags & SF_SINGLE) ? "true" : "false";
    json += ",\"manual\":";
    json += (s.flags & SF_MANUAL) ? "true" : "false";
    json += "}";
    emitted++;
  }

  json += "],\"count\":";
  json += String(emitted);
  json += ",\"more\":";
  // "more" is true if we stopped early and newer samples remain.
  bool more = false;
  if (emitted >= HISTORY_MAX_RESPONSE) {
    const Sample& last = g_history[(g_historyHead + HISTORY_SIZE - 1) % HISTORY_SIZE];
    more = (last.seq > sinceSeq + (uint32_t)emitted);
  }
  json += more ? "true" : "false";
  json += "}";
  return json;
}

String buildBlankJson() {
  String json = "{\"blank_done\":";
  json += g_blankIsDone ? "true" : "false";
  json += ",\"timestamp\":";
  json += String(g_blankingData.timestamp);
  json += ",\"sweep_duty_pct\":";
  json += String(g_blankSweepDutyPct, 1);
  json += ",\"sweep_ms\":";
  json += String(g_blankSweepMs);
  json += ",\"it_ms\":[";
  for (int i = 0; i < g_config.IT_COUNT; i++) {
    if (i) json += ",";
    json += String(g_config.itDelays[i]);
  }
  json += "],\"pwm_pct\":[";
  for (int j = 0; j < g_config.PWM_COUNT; j++) {
    if (j) json += ",";
    json += String(g_config.pwmSettings[j], 1);
  }
  json += "],\"i0\":[";
  for (int i = 0; i < g_config.IT_COUNT; i++) {
    if (i) json += ",";
    json += "[";
    for (int j = 0; j < g_config.PWM_COUNT; j++) {
      if (j) json += ",";
      json += String(g_blankingData.blankValues[i][j]);
    }
    json += "]";
  }
  json += "]}";
  return json;
}

String buildStatusJson() {
  const char* stateStr = "idle";
  switch (g_state) {
    case IDLE:      stateStr = "idle";      break;
    case BLANKING:  stateStr = "blanking";  break;
    case MEASURING: stateStr = "measuring"; break;
    case SEARCHING: stateStr = "searching"; break;
  }

  String json = "{\"fw\":\"";
  json += FW_VERSION;
  json += "\",\"name\":\"";
  json += FW_NAME;
  json += "\",\"boot_id\":";
  json += String(g_bootId);
  json += ",\"uptime_ms\":";
  json += String(millis());
  json += ",\"state\":\"";
  json += stateStr;
  json += "\",\"seq\":";
  json += String(g_seq);
  json += ",\"blank_done\":";
  json += g_blankIsDone ? "true" : "false";
  json += ",\"hd_mode\":";
  json += g_highDensityMode ? "true" : "false";
  json += ",\"refresh_ms\":";
  json += String(g_config.itRefreshTimes[g_currentItIndex]);
  // Lets the UI bound its interval control to what the hardware will accept
  // instead of offering settings the device would silently clamp.
  json += ",\"min_refresh_ms\":";
  json += String(minSafeRefreshMs());
  json += ",\"led_duty_limit\":";
  json += String(LED_DUTY_LIMIT, 3);

  json += ",\"auto_range\":";
  json += g_autoRange ? "true" : "false";
  json += ",\"it_index\":";
  json += String(g_currentItIndex);
  json += ",\"pwm_index\":";
  json += String(g_currentPwmIndex);
  json += ",\"it_ms\":";
  json += String(g_config.itDelays[g_currentItIndex]);
  json += ",\"pwm_pct\":";
  json += String(g_config.pwmSettings[g_currentPwmIndex], 1);
  json += ",\"led_duty\":";
  json += String(g_targetPct, 1);
  json += ",\"manual_led\":";
  json += g_manualLedOn ? "true" : "false";
  json += ",\"led_test\":";
  json += g_ledTestEnable ? "true" : "false";
  json += ",\"test_period\":";
  json += String(g_ledTestPeriodMs);
  json += ",\"ema\":";
  json += String(g_emaAlpha, 2);
  json += ",\"it_table\":[";
  for (int i = 0; i < g_config.IT_COUNT; i++) {
    if (i) json += ",";
    json += String(g_config.itDelays[i]);
  }
  json += "],\"pwm_table\":[";
  for (int j = 0; j < g_config.PWM_COUNT; j++) {
    if (j) json += ",";
    json += String(g_config.pwmSettings[j], 1);
  }
  json += "]";
  json += ",\"low\":";
  json += String(g_config.LOW_THRESHOLD_RAW);
  json += ",\"high\":";
  json += String(g_config.HIGH_THRESHOLD_RAW);
  json += ",\"opt\":";
  json += String(g_config.OPTIMAL_TARGET_RAW);
  json += ",\"hub_enabled\":";
  json += g_hubEnabled ? "true" : "false";
  json += ",\"hub_connected\":";
  json += (WiFi.status() == WL_CONNECTED) ? "true" : "false";
  json += ",\"hub_ssid\":\"";
  json += g_lastKnownSsid;
  json += "\",\"ap_ip\":\"";
  json += WiFi.softAPIP().toString();
  json += "\",\"ap_clients\":";
  json += String(WiFi.softAPgetStationNum());
  json += ",\"sta_ip\":\"";
  json += (WiFi.status() == WL_CONNECTED) ? WiFi.localIP().toString() : String("");
  json += "\",\"i2c_errors\":";
  json += String(g_i2cErrorCount);
  json += ",\"saturation_events\":";
  json += String(g_saturationCount);
  json += ",\"sensor_resets\":";
  json += String(g_sensorResets);
  json += ",\"soc_temp_c\":";
  json += String(temperatureRead(), 1);
  // Reads that fell back to the blind wait because no conversion boundary was
  // visible. Non-zero means a gear too dim to anchor on -- treat its readings
  // with suspicion.
  json += ",\"boundary_misses\":";
  json += String(g_boundaryMisses);
  json += ",\"failed_searches\":";
  json += String(g_consecutiveGearSearches);
  json += ",\"hist_size\":";
  json += String(HISTORY_SIZE);
  json += ",\"hist_stored\":";
  json += String(g_historyCount > (uint32_t)HISTORY_SIZE ? (uint32_t)HISTORY_SIZE
                                                         : g_historyCount);
  json += ",\"free_heap\":";
  json += String(ESP.getFreeHeap());
  json += "}";
  return json;
}

