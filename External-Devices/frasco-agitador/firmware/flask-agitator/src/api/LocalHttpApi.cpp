#include "LocalHttpApi.h"

#include <Arduino.h>
#include <Update.h>

#include "../core/AppContext.h"
#include "../motor/MotorDriver.h"
#include "../protocol/CommandCodec.h"

namespace {

const char otaPage[] PROGMEM = R"rawliteral(<!DOCTYPE html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Frasco Agitador OTA</title>
<style>body{font-family:sans-serif;max-width:520px;margin:2em auto;padding:0 1em}progress{width:100%}code{background:#eee;padding:0 .3em}</style>
</head><body><h2>Frasco Agitador &ndash; Firmware Update</h2>
<p>Running: <b>Frasco Agitador Firmware</b></p>
<p>Selecione a imagem <code>flask-agitator.ino.bin</code> (Arduino IDE: <i>Sketch &gt; Export Compiled Binary</i>). Nao envie <code>.merged.bin</code>, <code>.bootloader.bin</code> ou <code>.partitions.bin</code>.</p>
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

void handleCommand() {
  if (server.method() != HTTP_POST) {
    server.send(405);
    return;
  }
  if (!parseAndApplyJson(server.arg("plain"), Source::WIFI)) {
    server.send(422, "text/plain", "JSON: {\"RPM_percent\":0-100, \"Dir\":0|1, \"ActivePot\":0|1}");
    return;
  }
  server.send(200, "text/plain", "OK");
}

void handleRead() {
  char buf[64];
  snprintf(buf, sizeof(buf), "{\"time_s\":%lu,\"duty\":%.1f}", static_cast<unsigned long>(millis() / 1000), targetPercent);
  latestTelemetry = String(buf);
  server.send(200, "application/json", latestTelemetry);
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
    // Intertravamento de segurança: desacelerar/parar o motor
    targetPercent = 0.0f;
    brakeMotor();
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

}  // namespace

void setupLocalHttpApi() {
  server.on("/cmd", HTTP_POST, handleCommand);
  server.on("/read", HTTP_GET, handleRead);
  server.on("/update", HTTP_GET, handleOtaPage);
  server.on("/update", HTTP_POST, handleOtaUploadDone, handleOtaChunk);
  server.begin();
}


void serviceLocalHttpApi() {
  server.handleClient();
}
