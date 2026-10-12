void handleReadData() {
    server.send(200, "application/json", g_lastDataJson);
}

// O Hub guarda ate 511 bytes do /diag de cada no (NodeDiagTask); passar disso corta o JSON.
static const size_t DIAG_MAX_BYTES = 511;

void handleDiag() {
    char json[DIAG_MAX_BYTES + 1];
    const int n = snprintf(json, sizeof(json),
             "{\"device\":\"peristaltic-pump\",\"version\":\"" PUMP_FW_VERSION "\",\"build\":\"" PUMP_FW_BUILD "\","
             "\"uptime_s\":%lu,\"boot\":%lu,\"rst\":%d,\"free_heap\":%u,"
             "\"ssid\":\"%s\",\"rssi\":%d,\"ip\":\"%s\",\"mac\":\"%s\",\"hub_fail_streak\":%u,"
             "\"mode\":%d,\"drv\":%d,\"vol\":%.3f,"
             "\"last_cmd_id\":%lu,\"rej_cmd_id\":%lu,\"rej_err\":\"%s\",\"cal_crc\":\"%08X\","
             "\"dose_active\":%s,\"dose_done\":%.3f,\"push_trunc\":%lu}",
             static_cast<unsigned long>(millis() / 1000),
             static_cast<unsigned long>(g_bootCount),
             g_resetReason,
             static_cast<unsigned int>(ESP.getFreeHeap()),
             WiFi.SSID().c_str(),
             WiFi.RSSI(),
             WiFi.localIP().toString().c_str(),
             WiFi.macAddress().c_str(),
             g_hubFailStreak,
             g_config.mode,
             static_cast<int>(g_drive),
             volRead(),
             static_cast<unsigned long>(g_lastAppliedHubCommandId),
             static_cast<unsigned long>(g_lastRejectedHubCommandId),
             g_lastRejectCode,
             static_cast<unsigned int>(g_pumpCal.crc32),
             g_doseActive ? "true" : "false",
             static_cast<double>(doseDeliveredNow()),
             static_cast<unsigned long>(g_pushTruncations));
    if (n < 0 || static_cast<size_t>(n) > DIAG_MAX_BYTES) {
        server.send(200, "application/json",
                    "{\"device\":\"peristaltic-pump\",\"version\":\"" PUMP_FW_VERSION "\",\"diag_truncated\":true}");
        return;
    }
    server.send(200, "application/json", json);
}

void handleCommand() {
    if (server.hasArg("plain")) {
        String body = server.arg("plain");
        if (processJsonCommand(body, CommandSource::LocalHttp)) {
            server.send(200, "text/plain", "Command processed");
        } else {
            server.send(422, "text/plain", String("Rejected: ") + g_lastRejectCode);
        }
    } else {
        server.send(400, "text/plain", "Bad Request - No Body");
    }
}

const char otaPage[] PROGMEM = R"rawliteral(<!DOCTYPE html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Bomba Peristaltica OTA</title>
<style>body{font-family:sans-serif;max-width:520px;margin:2em auto;padding:0 1em}progress{width:100%}code{background:#eee;padding:0 .3em}</style>
</head><body><h2>Bomba Peristaltica &ndash; Firmware Update</h2>
<p>Running: <b>Peristaltic Pump Controller v)rawliteral" PUMP_FW_VERSION R"rawliteral(</b></p>
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
        // Intertravamento de seguranca: parar a bomba imediatamente. Um perfil ou dosagem
        // interrompido pela gravacao nao retoma sozinho no novo firmware: o operador reinicia.
        doseCancel("ota");
        if (g_opState != OP_IDLE) clearRuntimeState();
        g_opState = OP_IDLE;
        g_dosePulseOn = false;
        g_doseArmOnMs = 0;
        g_doseCutoffMs = 0;
        g_drive = DRV_OTA;
        sessionVolumeSave(true);
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

void sendHubHello() {
    if (WiFi.status() != WL_CONNECTED) return;
    char url[140];
    snprintf(url, sizeof(url), "%s?dev=pump&ver=" PUMP_FW_VERSION "&mac=%s",
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
    const float vol = volRead();
    const int pwm_duty = g_actualPwmDuty;

    float t_rel = g_current_t_min - g_config.init_t_min;
    if (t_rel < 0.0f) t_rel = 0.0f;
    float v_target = calculateTargetVolume(t_rel);

    // Os nove coeficientes da curva saem a cada 10 pushes ou quando o CRC muda (o Hub guarda o
    // ultimo valor); cal_crc vai sempre. Antes eram ~230 caracteres a 1 Hz.
    static uint8_t pushesSinceCal = 255;
    static uint32_t lastSentCrc = 0;
    const bool sendCal = pushesSinceCal >= 9 || g_pumpCal.crc32 != lastSentCrc;

    char url[1024];
    int n = snprintf(url, sizeof(url),
             "%s?mode=%d&pwm=%d&speed=%.1f&flow=%.3f&vol=%.3f&v_tgt=%.3f&active=%d&waiting=%d&ack_cmd_id=%lu"
             "&kp=%.4f&ki=%.4f&kd=%.4f&pot=%d&cyc_vol=%.3f&cal_crc=%08X",
             sensorHubDataURL.c_str(),
             g_config.mode,
             pwm_duty,
             g_cmdSpeed,
             g_currentFlowRateMlMin,
             vol,
             v_target,
             (g_opState == OP_RUNNING) ? 1 : 0,
             (g_opState == OP_WAITING) ? 1 : 0,
             static_cast<unsigned long>(g_lastAppliedHubCommandId),
             g_config.pid_kp,
             g_config.pid_ki,
             g_config.pid_kd,
             (disablePot || hasUsbSpeed || g_doseActive) ? 0 : 1,
             vol - g_cycleStartVolumeMl,
             static_cast<unsigned int>(g_pumpCal.crc32));

    auto append = [&](const char* fmt, auto... args) {
        if (n < 0 || static_cast<size_t>(n) >= sizeof(url)) return;
        n += snprintf(url + n, sizeof(url) - n, fmt, args...);
    };
    if (sendCal) {
        append("&a1=%.9g&b1=%.9g&k1=%.9g&f1=%.9g&c1=%.9g&k2=%.9g&f2=%.9g&c2=%.9g&trans_speed=%.2f",
               g_pumpCal.a1, g_pumpCal.b1, g_pumpCal.k1, g_pumpCal.f1, g_pumpCal.c1,
               g_pumpCal.k2, g_pumpCal.f2, g_pumpCal.c2, g_pumpCal.s_t);
    }
    // 3.13: quem comanda o motor, sensor de liquido, relogio do perfil, reinicios e recusa.
    const bool sensorOn = sensorEnable && !sensorBypass;
    append("&drv=%d&sens=%d&wet=%d&t_min=%.3f&up=%lu&boot=%lu&rst=%d&rej_cmd_id=%lu&rej_err=%s",
           static_cast<int>(g_drive), sensorOn ? 1 : 0, g_motorAllowed ? 1 : 0,
           g_current_t_min, static_cast<unsigned long>(millis() / 1000),
           static_cast<unsigned long>(g_bootCount), g_resetReason,
           static_cast<unsigned long>(g_lastRejectedHubCommandId),
           g_lastRejectCode[0] ? g_lastRejectCode : "none");
    // Dosagem: estado e eco dos parametros, para o app confirmar o que pediu.
    const unsigned long nowMs = millis();
    unsigned long nextS = 0;
    if (g_doseActive && !g_dosePulseOn && static_cast<long>(g_doseNextPulseMs - nowMs) > 0) {
        nextS = (g_doseNextPulseMs - nowMs + 999UL) / 1000UL;
    }
    append("&d_act=%d&d_pulse=%d&d_ml=%.3f&d_per=%.1f&d_spd=%.1f&d_rate=%.3f&d_tot=%.3f&d_dead=%.0f"
           "&d_done=%.3f&d_n=%lu&d_next=%lu&d_short=%.3f",
           g_doseActive ? 1 : 0, g_dosePulseOn ? 1 : 0,
           g_dose.volumeMl, g_dose.periodS, g_dose.speed, g_dose.rateMlMin, g_dose.totalMl, g_dose.deadMs,
           doseDeliveredNow(), static_cast<unsigned long>(g_dosePulses), nextS, g_doseShortMl);

    if (n < 0 || static_cast<size_t>(n) >= sizeof(url)) {
        // Um push cortado mandaria campos pela metade; melhor nao mandar.
        g_pushTruncations++;
        Serial.printf("[Hub] Push maior que o buffer (%d bytes); descartado.\n", n);
        return;
    }
    if (sendCal) {
        pushesSinceCal = 0;
        lastSentCrc = g_pumpCal.crc32;
    } else if (pushesSinceCal < 255) {
        pushesSinceCal++;
    }

    int code;
    String body;
    if (httpGet(url, code, body)) {
        // Hub 10.9 devolve na resposta o comando pendente; o Hub 10.8 responde texto, ignorado.
        if (body.length() > 0 && body[0] == '{') handleHubCommandBody(body);
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

    handleHubCommandBody(body);
}

// Um comando do Hub, venha do poll ou da resposta do push.
void handleHubCommandBody(const String& body) {
    if (body.length() == 0 || body == "{}") return;

    // A hub command carries cmd_id. A local one (serial, or the node's own web UI)
    // does not, and is always applied - the operator standing at the bench outranks
    // a retry that is only still in flight because the link is slow.
    // O Hub numera a partir de uma base aleatoria >= 1e8 por boot. Lido como float, o id
    // perdia os ultimos digitos (float so e exato ate 2^24): o ACK nunca batia com o Hub e
    // ids vizinhos colidiam como "ja aplicado". Inteiro de 32 bits, sempre.
    uint32_t hubCommandId = 0;
    if (parseJsonUint32(body.c_str(), "cmd_id", hubCommandId) && hubCommandId > 0) {
        if (hubCommandId == g_lastAppliedHubCommandId || hubCommandId == g_lastRejectedHubCommandId) {
            // Already applied (acked again on the next push) or already refused (rej_cmd_id
            // stays published). Either way, not re-processed.
            return;
        }
        if (processJsonCommand(body, CommandSource::Hub)) {
            g_lastAppliedHubCommandId = hubCommandId;
            Serial.printf("[HubCmd] Applied cmd_id=%lu\n", (unsigned long)hubCommandId);
        } else {
            // Recusado: sem ACK. O Hub 10.9 encerra o pendente ao ver rej_cmd_id.
            g_lastRejectedHubCommandId = hubCommandId;
            Serial.printf("[HubCmd] Rejected cmd_id=%lu (%s)\n", (unsigned long)hubCommandId, g_lastRejectCode);
        }
        return;
    }

    processJsonCommand(body, CommandSource::Hub);
}

void checkWifi() {
    unsigned long now = millis();

    // Link Watchdog: se o streak de falhas consecutivas atingiu o limite, forca queda
    if (g_hubFailStreak >= 8) {
        Serial.printf("[NET] Link zumbi detectado (streak=%u). Forcando queda da associacao...\n", g_hubFailStreak);
        g_hubFailStreak = 0;
        g_hubAnnounced = false;
        WiFi.disconnect(true, false);
        g_wifiState = WF_IDLE;
        g_wifiNextActionMs = now + 500;
        return;
    }

    if (now < g_wifiNextActionMs) return;

    if (WiFi.status() == WL_CONNECTED) {
        g_wifiState = WF_IDLE;
        g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
        return;
    }

    switch(g_wifiState) {
        case WF_IDLE:
            if (g_lastKnownSsid != "") {
                Serial.printf("[NET] Connecting to known hub: %s on channel 6\n", g_lastKnownSsid.c_str());
                WiFi.disconnect(false, false);
                WiFi.begin(g_lastKnownSsid.c_str(), g_lastKnownSsid.c_str(), 6);
                g_wifiState = WF_CONNECTING;
                g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
            } else {
                Serial.println("[NET] WiFi disconnected. Starting async scan...");
                WiFi.scanDelete();
                WiFi.scanNetworks(true, true);
                g_wifiState = WF_SCANNING;
                g_wifiNextActionMs = now + 100;
            }
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
                    Serial.printf("[NET] Hub found: %s. Connecting on channel 6...\n", ssidToTry.c_str());
                    WiFi.disconnect(false, false);
                    delay(50);
                    WiFi.begin(ssidToTry.c_str(), ssidToTry.c_str(), 6);
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
            g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
            break;
    }
}

bool httpGet(const String& url, int& code, String& body) {
    http.begin(url);
    http.setReuse(false);
    // setTimeout so cobre a leitura; o connect padrao e 5 s. Hello + poll + push no
    // mesmo loop com o Hub mudo passavam do watchdog e reiniciavam a bomba.
    http.setConnectTimeout(1000);
    http.setTimeout(1000);
    code = http.GET();
    if (code > 0) body = http.getString();
    else body = String("err=") + code;
    http.end();
    return code >= 200 && code < 300;
}

String getJsonStringValue(const char* json, const char* key) {
    const char* val = findJsonValueStart(json, key);
    if (!val || *val != '"') return "";
    val++; // skip opening quote
    const char* endQ = strchr(val, '"');
    if (!endQ) return "";
    return String(val).substring(0, endQ - val);
}
