#include "LocalHttpApi.h"

#include <Arduino.h>
#include <Update.h>
#include <WiFi.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"
#include "../protocol/ConfigCodec.h"

namespace {

const char otaPage[] PROGMEM = R"rawliteral(<!DOCTYPE html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Sensor de Distancia OTA</title>
<style>body{font-family:sans-serif;max-width:520px;margin:2em auto;padding:0 1em}progress{width:100%}code{background:#eee;padding:0 .3em}</style>
</head><body><h2>Sensor de Distancia &ndash; Firmware Update</h2>
<p>Running: <b>DistanceClient r10</b></p>
<p>Selecione a imagem <code>distance-sensor.ino.bin</code> (Arduino IDE: <i>Sketch &gt; Export Compiled Binary</i>). Nao envie <code>.merged.bin</code>, <code>.bootloader.bin</code> ou <code>.partitions.bin</code>.</p>
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

void handleRoot() {
  char json[96];
  snprintf(json, sizeof(json), "{\"time\":%.1f,\"distance\":%d}",
           g_lastSampleTimeSec, static_cast<int>(g_lastValidDistance));
  server.send(200, "application/json", json);
}

void handleGetConfig() {
  Serial.println("[HTTP] GET /config received");
  server.send(200, "application/json", getConfigAsJson());
}

void handleConfig() {
  Serial.println("[HTTP] POST /config received");
  if (!server.hasArg("plain")) {
    server.send(400, "text/plain", "Bad Request - No Body");
    return;
  }

  const String body = server.arg("plain");
  Serial.printf("[HTTP] Body: %s\n", body.c_str());
  processConfigUpdate(body.c_str());
  server.send(200, "text/plain", "Config Updated");
}

void handleNotFound() {
  server.send(404, "text/plain", "Not Found");
}

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

void handleDiag() {
  char json[384];
  snprintf(json, sizeof(json),
           "{\"device\":\"distance-sensor\",\"version\":\"%s\",\"uptime_s\":%lu,"
           "\"free_heap\":%u,\"wifi_status\":%d,\"ssid\":\"%s\",\"rssi\":%d,"
           "\"ip\":\"%s\",\"mac\":\"%s\",\"hub_fail_streak\":%u,\"ota\":%s,"
           "\"distance\":%.0f,\"sample_time\":%.1f,\"offset_mm\":%.2f}",
           BoardConfig::FirmwareTag,
           static_cast<unsigned long>(millis() / 1000),
           static_cast<unsigned int>(ESP.getFreeHeap()),
           WiFi.status(),
           WiFi.SSID().c_str(),
           WiFi.RSSI(),
           WiFi.localIP().toString().c_str(),
           WiFi.macAddress().c_str(),
           g_hubFailStreak,
           g_otaInProgress ? "true" : "false",
           g_lastValidDistance,
           g_lastSampleTimeSec,
           g_offsetMm);
  server.send(200, "application/json", json);
}

}  // namespace

void setupLocalHttpApi() {
  server.on("/", HTTP_GET, handleRoot);
  server.on("/diag", HTTP_GET, handleDiag);
  server.on("/status", HTTP_GET, handleDiag);
  server.on("/config", HTTP_GET, handleGetConfig);
  server.on("/config", HTTP_POST, handleConfig);
  server.on("/update", HTTP_GET, handleOtaPage);
  server.on("/update", HTTP_POST, handleOtaUploadDone, handleOtaChunk);
  server.onNotFound(handleNotFound);
  server.begin();
}


void serviceLocalHttpApi() {
  server.handleClient();
}
