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

const char otaPage[] PROGMEM = R"rawliteral(<!DOCTYPE html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Bomba Peristaltica OTA</title>
<style>body{font-family:sans-serif;max-width:520px;margin:2em auto;padding:0 1em}progress{width:100%}code{background:#eee;padding:0 .3em}</style>
</head><body><h2>Bomba Peristaltica &ndash; Firmware Update</h2>
<p>Running: <b>Peristaltic Pump Controller v3.8</b></p>
<p>Selecione a imagem <code>peristaltic-pump.ino.bin</code> (Arduino IDE: <i>Sketch &gt; Export Compiled Binary</i>). Nao envie <code>.merged.bin</code>, <code>.bootloader.bin</code> ou <code>.partitions.bin</code>.</p>
<form id="f"><input type="file" name="firmware" accept=".bin" required> <input type="submit" value="Flash"></form>
<progress id="p" value="0" max="100" hidden></progress><p id="s"></p>
<script>
const f=document.getElementById('f'),p=document.getElementById('p'),s=document.getElementById('s');
f.onsubmit=e=>{e.preventDefault();const x=new XMLHttpRequest();x.open('POST','/update');
x.upload.onprogress=v=>{p.hidden=false;p.value=Math.round(100*v.loaded/v.total);s.textContent='Uploading '+p.value+'%'};
x.onload=()=>{s.textContent=x.responseText;if(x.status==200)setTimeout(()=>location.reload(),8000)};
x.onerror=()=>{s.textContent='Conexao perdida. Se atingiu 100%, a placa esta reiniciando; recarregue em alguns segundos.'};
x.send(new FormData(f))};
</script></body></html>)rawliteral";

void handleOtaPage() {
    server.sendHeader("Connection", "close");
    server.send(200, "text/html", otaPage);
}

void handleOtaUploadDone() {
    server.sendHeader("Connection", "close");
    if (g_otaRejectReason.length() > 0) {
        String msg = "Rejected: " + g_otaRejectReason;
        server.send(400, "text/plain", msg);
    } else if (Update.hasError()) {
        String msg = "Flash failed: " + String(Update.errorString()) + ". Running firmware untouched.";
        server.send(500, "text/plain", msg);
    } else if (!Update.isFinished()) {
        server.send(500, "text/plain", "Upload incomplete. Running firmware untouched.");
    } else {
        String msg = "OK: Firmware gravado e validado com sucesso! Reiniciando...";
        server.send(200, "text/plain", msg);
        g_otaRebootAtMs = millis() + 500;
    }
    Serial.println("[OTA] Finalizado requisicao POST /update");
    g_otaInProgress = false;
}

void handleOtaChunk() {
    static size_t nextProgressLog = 0;
    HTTPUpload& upload = server.upload();

    if (upload.status == UPLOAD_FILE_START) {
        g_otaRejectReason = "";
        if (upload.filename.indexOf("merged") >= 0 || upload.filename.indexOf("bootloader") >= 0 || upload.filename.indexOf("partitions") >= 0) {
            g_otaRejectReason = "'" + upload.filename + "' nao e a imagem do app, envie o .ino.bin";
            Serial.printf("[OTA] Rejeitado: %s\n", g_otaRejectReason.c_str());
            return;
        }
        Serial.printf("[OTA] Upload start: %s\n", upload.filename.c_str());
        // Intertravamento de seguranca: parar a bomba imediatamente
        g_opState = OP_IDLE;
        g_driverEnabled = false;
        g_cmdSpeed = 0.0f;
        g_actualPwmDuty = 0;
        ledcWrite(R_PWM_PIN, 0);
        ledcWrite(L_PWM_PIN, 0);
        digitalWrite(R_EN_PIN, LOW);
        digitalWrite(L_EN_PIN, LOW);

        if (Update.isRunning()) Update.abort();
        nextProgressLog = 0;
        if (!Update.begin(UPDATE_SIZE_UNKNOWN)) {
            Update.printError(Serial);
        }
    }

    if (g_otaRejectReason.length() > 0) return;

    g_otaLastChunkMs = millis();
    g_otaInProgress = true;

    if (upload.status == UPLOAD_FILE_WRITE) {
        if (upload.totalSize >= nextProgressLog) {
            Serial.printf("[OTA] %u KB recebidos\n", static_cast<unsigned>(upload.totalSize / 1024));
            nextProgressLog += 131072;
        }
        if (!Update.hasError() && upload.currentSize > 0) {
            if (Update.write(upload.buf, upload.currentSize) != upload.currentSize) {
                Update.printError(Serial);
            }
        }
    } else if (upload.status == UPLOAD_FILE_END) {
        if (Update.end(true)) {
            Serial.printf("[OTA] Imagem verificada com sucesso: %u bytes.\n", static_cast<unsigned>(upload.totalSize));
        } else {
            Update.printError(Serial);
        }
    } else if (upload.status == UPLOAD_FILE_ABORTED) {
        Update.end();
        g_otaInProgress = false;
        Serial.println("[OTA] Upload abortado.");
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
    if (httpGet(url, code, body)) {
        if (g_hubFailStreak > 0) {
            Serial.printf("[Hub] Conexao restabelecida apos %u falha(s).\n", g_hubFailStreak);
        }
        g_hubFailStreak = 0;
    } else {
        if (g_hubFailStreak < 255) {
            g_hubFailStreak++;
        }
        // Serial.printf("Hub data send FAILED, code %d\n", code);
    }
}

void pollHubForCommands() {
    int code;
    String body;
    if (!httpGet(sensorHubCommandURL, code, body)) {
        if (g_hubFailStreak < 255) {
            g_hubFailStreak++;
        }
        return;
    }

    if (g_hubFailStreak > 0) {
        Serial.printf("[Hub] Conexao restabelecida apos %u falha(s).\n", g_hubFailStreak);
    }
    g_hubFailStreak = 0;

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

float getJsonFloatValue(const char* json, const char* key) {
    const char* val = findJsonValueStart(json, key);
    if (!val || *val == '"') return NAN;
    char* endPtr = nullptr;
    float result = strtof(val, &endPtr);
    if (endPtr == val) return NAN;
    return result;
}

double getJsonDoubleValue(const char* json, const char* key) {
    const char* val = findJsonValueStart(json, key);
    if (!val || *val == '"') return NAN;
    char* endPtr = nullptr;
    double result = strtod(val, &endPtr);
    if (endPtr == val) return NAN;
    return result;
}

String getJsonStringValue(const char* json, const char* key) {
    const char* val = findJsonValueStart(json, key);
    if (!val || *val != '"') return "";
    val++; // skip opening quote
    const char* endQ = strchr(val, '"');
    if (!endQ) return "";
    return String(val).substring(0, endQ - val);
}
