void handleReadData() {
    server.send(200, "application/json", g_lastDataJson);
}

void handleCommand() {
    if (server.hasArg("plain")) {
        String body = server.arg("plain");
        processJsonCommand(body);
        server.send(200, "text/plain", "Command processed");
    } else {
        server.send(400, "text/plain", "Bad Request - No Body");
    }
}

void handleNotFound() {
    server.send(404, "text/plain", "Not Found");
}

void sendDataToHub() {
    float vol;
    int pwm_duty;
    taskDISABLE_INTERRUPTS();
    vol = g_cumulativeVolumeMl;
    pwm_duty = g_actualPwmDuty;
    taskENABLE_INTERRUPTS();

    float t_rel = g_current_t_min - g_config.init_t_min;
    if (t_rel < 0.0f) t_rel = 0.0f;
    float v_target = calculateTargetVolume(t_rel);

    char url[256];
    snprintf(url, sizeof(url),
             "%s?mode=%d&pwm=%d&speed=%.1f&flow=%.3f&vol=%.3f&v_tgt=%.3f&active=%d&waiting=%d&ack_cmd_id=%lu",
             sensorHubDataURL.c_str(),
             g_config.mode,
             pwm_duty,
             g_cmdSpeed,
             g_currentFlowRateMlMin,
             vol,
             v_target,
             (g_opState == OP_RUNNING) ? 1 : 0,
             (g_opState == OP_WAITING) ? 1 : 0,
             static_cast<unsigned long>(g_lastAppliedHubCommandId));

    int code;
    String body;
    if (!httpGet(url, code, body)) {
        // Serial.printf("Hub data send FAILED, code %d\n", code);
    }
}

void pollHubForCommands() {
    int code;
    String body;
    if (!httpGet(sensorHubCommandURL, code, body)) return;
    if (body.length() == 0 || body == "{}") return;

    // A hub command carries cmd_id. A local one (serial, or the node's own web UI)
    // does not, and is always applied - the operator standing at the bench outranks
    // a retry that is only still in flight because the link is slow.
    float idVal = getJsonFloatValue(body, "cmd_id");
    if (!isnan(idVal) && idVal > 0.0f) {
        uint32_t hubCommandId = (uint32_t)idVal;
        if (hubCommandId == g_lastAppliedHubCommandId) {
            // Already applied. Acknowledged again on the next push; not re-applied.
            return;
        }
        processJsonCommand(body);
        g_lastAppliedHubCommandId = hubCommandId;
        Serial.printf("[HubCmd] Applied cmd_id=%lu\n", (unsigned long)hubCommandId);
        return;
    }

    processJsonCommand(body);
}

void checkWifi() {
    unsigned long now = millis();
    if (now < g_wifiNextActionMs) return;

    if (WiFi.status() == WL_CONNECTED) {
        g_wifiState = WF_IDLE;
        g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
        return;
    }

    switch(g_wifiState) {
        case WF_IDLE:
            Serial.println("[NET] WiFi disconnected. Starting async scan...");
            WiFi.scanNetworks(true, true);
            g_wifiState = WF_SCANNING;
            g_wifiNextActionMs = now + 100;
            break;
        case WF_SCANNING: {
            int n = WiFi.scanComplete();
            if (n == -1) {
                g_wifiNextActionMs = now + 100;
            } else if (n > 0) {
                String ssidToTry = "";
                for (int i = 0; i < n; i++) {
                    String ssid = WiFi.SSID(i);
                    if (ssid == HUB_SSID_A || ssid == HUB_SSID_B) {
                        ssidToTry = ssid;
                        g_lastKnownSsid = ssid;
                        break;
                    }
                }
                if (ssidToTry != "") {
                    Serial.printf("[NET] Hub found: %s. Connecting...\n", ssidToTry.c_str());
                    WiFi.disconnect();
                    delay(100);
                    WiFi.begin(ssidToTry.c_str(), ssidToTry.c_str());
                    g_wifiState = WF_CONNECTING;
                    g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
                } else {
                    g_wifiState = WF_IDLE;
                    g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
                }
                WiFi.scanDelete();
            } else {
                g_wifiState = WF_IDLE;
                g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
            }
            break;
        }
        case WF_CONNECTING:
            Serial.println("[NET] Connect attempt timed out.");
            g_wifiState = WF_IDLE;
            g_wifiNextActionMs = now;
            break;
    }
}

bool httpGet(const String& url, int& code, String& body) {
    http.begin(url);
    http.setReuse(false);
    http.setTimeout(1000);
    code = http.GET();
    if (code > 0) body = http.getString();
    else body = String("err=") + code;
    http.end();
    return code >= 200 && code < 300;
}

long getJsonValue(String json, String key) {
    String searchKey = "\"" + key + "\":";
    int keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) {
        searchKey = "\"" + key + "\" :";
        keyIndex = json.indexOf(searchKey);
        if (keyIndex == -1) return -999999;
    }
    int valueIndex = keyIndex + searchKey.length();
    while(valueIndex < json.length() && isspace(json.charAt(valueIndex))) valueIndex++;
    if (json.charAt(valueIndex) == '\"') return -999999;
    int endIndex = json.indexOf(',', valueIndex);
    if (endIndex == -1) endIndex = json.indexOf('}', valueIndex);
    if (endIndex == -1) return -999999;
    String valueStr = json.substring(valueIndex, endIndex);
    valueStr.trim();
    if (valueStr.length() == 0 || (!isDigit(valueStr.charAt(0)) && valueStr.charAt(0) != '-' && valueStr.charAt(0) != '.')) {
        return -999999;
    }
    return atol(valueStr.c_str());
}

float getJsonFloatValue(String json, String key) {
    String searchKey = "\"" + key + "\":";
    int keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) {
        searchKey = "\"" + key + "\" :";
        keyIndex = json.indexOf(searchKey);
        if (keyIndex == -1) return NAN;
    }
    int valueIndex = keyIndex + searchKey.length();
    while(valueIndex < json.length() && isspace(json.charAt(valueIndex))) valueIndex++;
    if (json.charAt(valueIndex) == '\"') return NAN;
    int endIndex = json.indexOf(',', valueIndex);
    if (endIndex == -1) endIndex = json.indexOf('}', valueIndex);
    if (endIndex == -1) return NAN;
    String valueStr = json.substring(valueIndex, endIndex);
    valueStr.trim();
    if (valueStr.length() == 0 || (!isDigit(valueStr.charAt(0)) && valueStr.charAt(0) != '-' && valueStr.charAt(0) != '.')) {
        return NAN;
    }
    return valueStr.toFloat();
}

String getJsonStringValue(String json, String key) {
    String searchKey = "\"" + key + "\":\"";
    int keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) {
        searchKey = "\"" + key + "\" : \"";
        keyIndex = json.indexOf(searchKey);
        if (keyIndex == -1) return "";
    }
    int valueIndex = keyIndex + searchKey.length();
    int endIndex = json.indexOf('\"', valueIndex);
    if (endIndex == -1) return "";
    return json.substring(valueIndex, endIndex);
}

double getJsonDoubleValue(String json, String key) {
    String searchKey = "\"" + key + "\":";
    int keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) {
        searchKey = "\"" + key + "\" :";
        keyIndex = json.indexOf(searchKey);
        if (keyIndex == -1) return NAN;
    }
    int valueIndex = keyIndex + searchKey.length();
    while (valueIndex < json.length() && isspace(json.charAt(valueIndex))) valueIndex++;
    if (json.charAt(valueIndex) == '\"') return NAN;
    int endIndex = json.indexOf(',', valueIndex);
    if (endIndex == -1) endIndex = json.indexOf('}', valueIndex);
    if (endIndex == -1) return NAN;
    String valueStr = json.substring(valueIndex, endIndex);
    valueStr.trim();
    if (valueStr.length() == 0) return NAN;
    char *endp = nullptr;
    double val = strtod(valueStr.c_str(), &endp);
    if (endp == valueStr.c_str()) return NAN;
    return val;
}
