// Web Server Handlers

void sendJson(const String& json) {
  server.sendHeader("Access-Control-Allow-Origin", "*");
  server.sendHeader("Cache-Control", "no-store");
  server.send(200, "application/json", json);
}

void handleRoot() {
  server.sendHeader("Cache-Control", "no-store");
  server.send_P(200, "text/html", WEB_UI_HTML);
}

void handleReadData() {
  buildDataJson();
  sendJson(g_lastDataJson);
}

void handleStatus() {
  sendJson(buildStatusJson());
}

void handleDiag() {
  char json[320];
  snprintf(json, sizeof(json),
           "{\"device\":\"biomass-sensor\",\"version\":\"%s\",\"uptime_s\":%lu,"
           "\"free_heap\":%u,\"wifi_status\":%d,\"ssid\":\"%s\",\"rssi\":%d,"
           "\"ip\":\"%s\",\"mac\":\"%s\",\"hub_fail_streak\":%u,\"ota\":%s,"
           "\"state\":%d,\"absorbance\":%.3f,\"raw\":%u}",
           FW_VERSION,
           static_cast<unsigned long>(millis() / 1000),
           static_cast<unsigned int>(ESP.getFreeHeap()),
           WiFi.status(),
           WiFi.SSID().c_str(),
           WiFi.RSSI(),
           WiFi.localIP().toString().c_str(),
           WiFi.macAddress().c_str(),
           g_hubFailStreak,
           g_otaInProgress ? "true" : "false",
           static_cast<int>(g_state),
           g_lastAbsorbance,
           g_lastAlsRaw);
  sendJson(json);
}

void handleHistory() {
  uint32_t since = 0;
  if (server.hasArg("since")) {
    long v = server.arg("since").toInt();
    if (v > 0) since = (uint32_t)v;
  }
  sendJson(buildHistoryJson(since));
}

void handleBlankTable() {
  sendJson(buildBlankJson());
}

void handleCommand() {
  if (server.hasArg("plain")) {
    String body = server.arg("plain");
    g_inHttpHandler = true;
    processJsonCommand(body, /*allowBlocking=*/false);
    g_inHttpHandler = false;
    server.sendHeader("Access-Control-Allow-Origin", "*");
    server.send(200, "application/json", buildStatusJson());
  } else {
    server.send(400, "text/plain", "Bad Request - No Body");
  }
}

void handleNotFound() {
  server.send(404, "text/plain", "Not Found");
}

const char otaPage[] PROGMEM = R"rawliteral(<!DOCTYPE html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Sensor de Biomassa OTA</title>
<style>body{font-family:sans-serif;max-width:520px;margin:2em auto;padding:0 1em}progress{width:100%}code{background:#eee;padding:0 .3em}</style>
</head><body><h2>Sensor de Biomassa &ndash; Firmware Update</h2>
<p>Running: <b>Biomass Sensor Firmware v5.3</b></p>
<p>Selecione a imagem <code>biomass-sensor.ino.bin</code> (Arduino IDE: <i>Sketch &gt; Export Compiled Binary</i>). Nao envie <code>.merged.bin</code>, <code>.bootloader.bin</code> ou <code>.partitions.bin</code>.</p>
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
    // Intertravamento de seguranca: desligar emissor optico e parar medicoes
    pwmSetDutyPercent(0.0f);
    g_manualLedOn = false;
    g_ledTestEnable = false;
    g_state = IDLE;

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


// Arduino Entry Points

