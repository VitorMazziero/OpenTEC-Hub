void buildDataJson() {
  String json = "{";
  json += "\"seq\":";
  json += String(g_seq);
  json += ",\"t_ms\":";
  json += String(g_lastSampleMs);
  json += ",\"boot_id\":";
  json += String(g_bootId);
  json += ",\"absorbance\":";
  json += String(g_lastAbsorbance, ABSORBANCE_DECIMALS);
  json += ",\"raw\":";
  json += String(g_lastAlsRaw);
  json += ",\"i0\":";
  json += String(g_lastI0);
  json += ",\"it_ms\":";
  json += String(g_config.itDelays[g_currentItIndex]);
  json += ",\"pwm_pct\":";

  // Report 0.0 PWM if IDLE, otherwise report the setting
  if (g_state == IDLE) {
    json += "0.0";
  } else {
    json += String(g_config.pwmSettings[g_currentPwmIndex], 1);
  }

  json += ",\"blank_done\":";
  json += g_blankIsDone ? "true" : "false";
  json += ",\"searching\":";
  json += (g_state == SEARCHING) ? "true" : "false";
  json += ",\"measuring\":";
  json += (g_state == MEASURING) ? "true" : "false";
  json += ",\"blanking\":";
  json += (g_state == BLANKING) ? "true" : "false";
  json += ",\"hd_mode\":";
  json += g_highDensityMode ? "true" : "false";
  json += ",\"sat\":";
  json += g_lastSat ? "true" : "false";
  json += ",\"single\":";
  json += g_lastSingle ? "true" : "false";
  json += ",\"manual\":";
  json += g_autoRange ? "false" : "true";
  json += "}";
  g_lastDataJson = json;
}

bool httpGet(const String& url, int& code, String& body) {
  http.begin(url);
  http.setReuse(false);
  http.setTimeout(1000); // 1 second timeout
  code = http.GET();
  if (code > 0) body = http.getString();
  else          body = String("err=") + code;
  http.end();
  return code >= 200 && code < 300;
}

void sendHubHello() {
  if (WiFi.status() != WL_CONNECTED) return;
  char url[140];
  snprintf(url, sizeof(url), "%s?dev=biomass&ver=v11.1&mac=%s",
           sensorHubHelloURL.c_str(), WiFi.macAddress().c_str());
  int code;
  String body;
  if (httpGet(url, code, body)) {
    g_hubAnnounced = true;
    Serial.printf("[Hub] Hello registrado com sucesso (%d)\n", code);
  } else {
    Serial.printf("[Hub] Hello falhou (%d)\n", code);
  }
}

void sendDataToHub() {
  char url[320];
  float pwmVal = (g_state == IDLE) ? 0.0f : g_config.pwmSettings[g_currentPwmIndex];
  snprintf(url, sizeof(url),
           "%s?absorbance=%.3f&raw=%d&it=%d&pwm=%.1f&hd_mode=%d&ack_cmd_id=%lu&idle=%d&gear=%d&ema=%.3f&probe_ms=%lu",
           sensorHubDataURL.c_str(),
           g_lastAbsorbance,
           g_lastAlsRaw,
           g_config.itDelays[g_currentItIndex],
           pwmVal,
           g_highDensityMode ? 1 : 0,
           static_cast<unsigned long>(g_lastAppliedHubCmdId),
           (g_state == IDLE) ? 1 : 0,
           (g_currentItIndex * g_config.PWM_COUNT + g_currentPwmIndex),
           g_emaAlpha,
           static_cast<unsigned long>(g_config.itRefreshTimes[g_currentItIndex]));

  int    code;
  String body;
  if (httpGet(url, code, body)) {
    if (g_hubFailStreak > 0) {
      Serial.printf("[Hub] Conexao restabelecida apos %u falha(s).\n", g_hubFailStreak);
    }
    g_hubFailStreak = 0;
  } else {
    if (g_hubFailStreak < 255) {
      g_hubFailStreak++;
    }
    Serial.printf("Hub data send FAILED, code %d\n", code);
  }

  lastHubHeartbeatMs = millis();
}

uint32_t extractHubCmdId(const String &json) {
  int k = json.indexOf("\"cmd_id\"");
  if (k < 0) return 0;
  int c = json.indexOf(':', k);
  if (c < 0) return 0;
  return (uint32_t)strtoul(json.c_str() + c + 1, nullptr, 10);
}

void pollHubForCommands() {
  int    code;
  String body;
  if (!httpGet(sensorHubCommandURL, code, body)) {
    if (g_hubFailStreak < 255) {
      g_hubFailStreak++;
    }
    Serial.printf("Hub command poll FAILED, code %d\n", code);
    return;
  }

  if (g_hubFailStreak > 0) {
    Serial.printf("[Hub] Conexao restabelecida apos %u falha(s).\n", g_hubFailStreak);
  }
  g_hubFailStreak = 0;

  if (body.length() == 0 || body == "{}") return;

  const uint32_t id = extractHubCmdId(body);

  if (id != 0 && id == g_lastAppliedHubCmdId) {
    // Already applied. The hub keeps re-delivering until the ack reaches it, so this
    // is the normal case for a poll or two. Re-running it would restart a ~15 s blank
    // or start routine that has already completed.
    return;
  }

  processJsonCommand(body);

  if (id != 0) {
    // Acknowledged whether or not a key was recognised: a payload this node cannot use
    // will not become usable on a retry, and leaving it unacknowledged would pin the
    // hub's mailbox on it indefinitely.
    g_lastAppliedHubCmdId = id;
    Serial.printf("[HubCmd] Applied cmd_id=%lu\n", (unsigned long)id);
  }
}

void checkWifi() {
  if (!g_hubEnabled) return; // Direct-only: never scan, never associate

  const unsigned long now = millis();

  // Link Watchdog: se o streak de falhas consecutivas atingiu o limite, forca queda
  if (g_hubFailStreak >= 8) {
    Serial.printf("[NET] Link zumbi detectado (streak=%u). Forcando queda da associacao...\n", g_hubFailStreak);
    g_hubFailStreak = 0;
    g_hubAnnounced = false;
    WiFi.disconnect(true, false);
    g_wifiState        = WF_IDLE;
    g_wifiNextActionMs = now + 500;
    return;
  }

  if (now < g_wifiNextActionMs) return;

  if (WiFi.status() == WL_CONNECTED) {
    g_wifiState        = WF_IDLE;
    g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
    return;
  }

  switch (g_wifiState) {
    case WF_IDLE:
      if (g_lastKnownSsid != "") {
        Serial.println("[NET] Connecting to known hub: " + g_lastKnownSsid + " on channel 6");
        WiFi.disconnect(false, false);
        WiFi.begin(g_lastKnownSsid.c_str(), g_lastKnownSsid.c_str(), 6);
        g_wifiState        = WF_CONNECTING;
        g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
      } else {
        Serial.println("[NET] Starting async hub scan...");
        WiFi.scanDelete();
        WiFi.scanNetworks(true, true);
        g_wifiState        = WF_SCANNING;
        g_wifiNextActionMs = now + 100;
      }
      break;

    case WF_SCANNING: {
      int n = WiFi.scanComplete();
      if (n == -1) {
        g_wifiNextActionMs = now + 100;
        break;
      }

      String ssidToTry = "";
      if (n > 0) {
        for (int i = 0; i < n; i++) {
          String ssid = WiFi.SSID(i);
          if (ssid == HUB_SSID_A || ssid == HUB_SSID_B) {
            ssidToTry       = ssid;
            g_lastKnownSsid = ssid;
            break;
          }
        }
      }
      WiFi.scanDelete();

      if (ssidToTry != "") {
        Serial.println("[NET] Hub found: " + ssidToTry + ". Connecting STA on channel 6.");
        WiFi.disconnect(false, false);
        WiFi.begin(ssidToTry.c_str(), ssidToTry.c_str(), 6);
        g_wifiState = WF_CONNECTING;
      } else {
        Serial.println("[NET] Hub not found. Keeping local AP active.");
        g_wifiState = WF_IDLE;
      }
      g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
      break;
    }

    case WF_CONNECTING:
      Serial.println("[NET] Connect attempt timed out. Will scan again later.");
      g_lastKnownSsid    = "";
      g_wifiState        = WF_IDLE;
      g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
      break;
  }
}

