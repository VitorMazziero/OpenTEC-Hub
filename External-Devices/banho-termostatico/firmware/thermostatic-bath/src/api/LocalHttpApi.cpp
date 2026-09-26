#include "LocalHttpApi.h"

#include <Arduino.h>
#include <Update.h>
#include <WiFi.h>
#include <esp_timer.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"
#include "../core/EventLog.h"
#include "../display/DisplayReader.h"
#include "../keypad/KeyPresser.h"
#include "../network/HubLink.h"
#include "../protocol/ConfigCodec.h"
#include "../setpoint/SetpointGuard.h"
#include "../setpoint/SetpointManager.h"

namespace {

const char otaPage[] PROGMEM = R"rawliteral(<!DOCTYPE html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Banho Termostatico OTA</title>
<style>body{font-family:sans-serif;max-width:520px;margin:2em auto;padding:0 1em}progress{width:100%}code{background:#eee;padding:0 .3em}</style>
</head><body><h2>Banho Termostatico &ndash; Firmware Update</h2>
<p>Running: <b>%FW%</b></p>
<p>Selecione a imagem <code>thermostatic-bath.ino.bin</code> (Arduino IDE: <i>Sketch &gt; Export Compiled Binary</i>). Nao envie <code>.merged.bin</code>, <code>.bootloader.bin</code> ou <code>.partitions.bin</code>.</p>
<p>Os reles ficam abertos durante o upload; comandos de setpoint sao recusados ate o reinicio.</p>
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

// Pagina minima para celular: mostra o estado e envia setpoint, ajustes e teclas
// cruas pela mesma rota /command que o aplicativo Python usa.
const char uiPage[] PROGMEM = R"rawliteral(<!DOCTYPE html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Banho Termostatico</title>
<style>body{font-family:sans-serif;max-width:480px;margin:1.5em auto;padding:0 1em}
.big{font-size:2.2em;font-weight:bold}.row{display:flex;gap:.5em;margin:.6em 0;flex-wrap:wrap}
button{padding:.6em 1em;font-size:1em}input{font-size:1.1em;width:6em}pre{background:#f3f3f3;padding:.6em;font-size:.8em;overflow:auto}
.warn{color:#b00}.ok{color:#080}</style></head><body>
<h2>Banho Termostatico</h2>
<div class="warn" id="own"></div>
<div>Setpoint (sombra): <span class="big" id="sp">--</span> <span id="known"></span></div>
<div>Modo: <b id="mode">--</b> <small id="guard"></small> &nbsp; desvio: <b id="dev">--</b>
<button onclick="cmd({mode:'manual'})">Manual</button><button onclick="cmd({mode:'auto'})">Automatico</button></div>
<div>Display PV: <b id="pv">--</b> &nbsp; SP: <b id="dsp">--</b> &nbsp; <small id="dtext"></small></div>
<div>Sequencia: <b id="seq">--</b> <span id="prog"></span> <span class="warn" id="err"></span></div>
<div class="row"><input id="target" type="number" step="0.1" value="30.0"><button onclick="cmd({setpoint:+target.value})">Enviar SP</button>
<button onclick="cmd({abort:1})">Abortar</button></div>
<div class="row"><button onclick="cmd({delta:-1})">-1.0</button><button onclick="cmd({delta:-0.1})">-0.1</button>
<button onclick="cmd({delta:0.1})">+0.1</button><button onclick="cmd({delta:1})">+1.0</button></div>
<div class="row"><input id="sync" type="number" step="0.1" value="30.0"><button onclick="cmd({sync_sp:+sync.value})">Sincronizar sombra</button></div>
<div class="row"><button onclick="cmd({key:'star'})">*</button><button onclick="cmd({key:'up'})">&#9650;</button>
<button onclick="cmd({key:'down'})">&#9660;</button><button onclick="cmd({key:'enter'})">ENTER</button>
<button onclick="cmd({key:'up',hold_ms:2000})">&#9650; 2 s</button><button onclick="cmd({key:'down',hold_ms:2000})">&#9660; 2 s</button></div>
<p><a href="/config">config</a> &middot; <a href="/status">status</a> &middot; <a href="/display">display</a> &middot; <a href="/update">OTA</a></p>
<pre id="log"></pre>
<script>
const $=i=>document.getElementById(i);
function cmd(o){fetch('/command',{method:'POST',body:JSON.stringify(o)}).then(r=>r.text()).then(t=>{$('log').textContent=t+'\n'+$('log').textContent}).catch(e=>{$('log').textContent='erro: '+e})}
function poll(){fetch('/status').then(r=>r.json()).then(s=>{
$('own').textContent=s.hub_owned?'Controlado pelo Hub: somente Abortar esta disponivel.':'';
$('sp').textContent=s.sp_shadow.toFixed(1);$('known').textContent=s.sp_known?'':'(desconhecido)';$('known').className=s.sp_known?'ok':'warn';
$('mode').textContent=s.mode;$('guard').textContent='('+s.guard+(s.guard_corrections?', '+s.guard_corrections+' correcoes':'')+')';
$('dev').textContent=s.deviation_c==null?'--':s.deviation_c.toFixed(1);$('dev').className=s.deviation_c?'warn':'ok';
$('pv').textContent=s.display_pv==null?'--':s.display_pv;$('dsp').textContent=s.display_sp==null?'--':s.display_sp;$('dtext').textContent=s.display_alive?'['+s.display_text+']':'(sem sinal)';
$('seq').textContent=s.seq_state+'/'+s.seq_kind+(s.seq_phase?'/'+s.seq_phase:'');
$('prog').textContent=(s.hold_ms?'hold '+s.hold_ms+' ms, '+s.hold_rate+' toques/s ':'')+(s.presses_total?s.presses_done+'/'+s.presses_total:'')+(s.hold_rounds?' (holds: '+s.hold_rounds+')':'');$('err').textContent=s.seq_error||'';
}).catch(()=>{});}
setInterval(poll,1000);poll();
</script></body></html>)rawliteral";

void sendJson(int code, const String& body) {
  server.send(code, "application/json", body);
}

void handleStatus() {
  sendJson(200, getStatusAsJson());
}

void handleGetConfig() {
  sendJson(200, getConfigAsJson());
}

// POST /config e POST /command compartilham o parser: a diferenca e so o nome.
void handleCommand() {
  if (!server.hasArg("plain")) {
    sendJson(400, "{\"ok\":false,\"error\":\"no_body\"}");
    return;
  }
  const String body = server.arg("plain");
  Serial.printf("[HTTP] %s: %s\n", server.uri().c_str(), body.c_str());
  String reply;
  const bool seen = processCommand(body, reply);
  const bool ok = reply.indexOf("\"ok\":true") >= 0;
  sendJson(ok ? 200 : (seen ? 409 : 400), reply);
}

// Atalho para ferramentas simples: POST /setpoint?sp=31.5 (ou corpo JSON).
void handleSetpoint() {
  String payload;
  if (server.hasArg("sp")) {
    payload = "{\"setpoint\":" + server.arg("sp") + "}";
  } else if (server.hasArg("plain")) {
    payload = server.arg("plain");
  } else {
    sendJson(400, "{\"ok\":false,\"error\":\"no_setpoint\"}");
    return;
  }
  String reply;
  const bool seen = processCommand(payload, reply);
  const bool ok = reply.indexOf("\"ok\":true") >= 0;
  sendJson(ok ? 200 : (seen ? 409 : 400), reply);
}

void handleDisplay() {
  String json = "{\"alive\":";
  json += displayAlive() ? "true" : "false";
  json += ",\"frames\":" + String(displayFrameCount());
  json += ",\"text\":\"" + displayText() + "\"";
  json += ",\"pv\":" + (displayPvValid() ? String(displayPv(), 2) : String("null"));
  json += ",\"sp\":" + (displaySpValid() ? String(displaySp(), 2) : String("null"));
  float live;
  json += ",\"sp_live\":" + (displayLiveSp(live) ? String(live, 2) : String("null"));
  json += ",\"live_frames\":" + String(displayLiveFrameCount());
  json += ",\"mode\":" + String(g_cfg.dispMode);
  json += ",\"leds\":" + String(displayLedSegments());
  json += ",\"raw\":[";
  for (int i = 0; i < BoardConfig::DigitCount; ++i) {
    if (i) json += ",";
    json += String(displayRawSegments(i));
  }
  json += "]}";
  sendJson(200, json);
}

// GET /scope?pin=8&n=2000&us=40 -> osciloscopio de um canal pelo ADC1 (diagnostico da
// etapa 2: niveis reais das linhas do display, que nao sao 0/5 V). So GPIO 1..10 (ADC1;
// o ADC2 disputa com o Wi-Fi). O pino volta a entrada digital no fim: 8..10 sao
// segmentos lidos por registrador, e o 1 (sense de `*`) nao esta ligado.
void handleScope() {
  const long pin = server.hasArg("pin") ? server.arg("pin").toInt() : -1;
  long n = server.hasArg("n") ? server.arg("n").toInt() : 2000;
  long us = server.hasArg("us") ? server.arg("us").toInt() : 40;
  if (pin < 1 || pin > 10 || pin == 3 || n < 10 || n > 4000 || us < 20 || us > 10000 ||
      n * us > 2000000L) {
    sendJson(400, "{\"ok\":false,\"error\":\"range\"}");
    return;
  }
  if (keypadBusy() || setpointBusy()) {
    sendJson(409, "{\"ok\":false,\"error\":\"busy\"}");
    return;
  }
  uint16_t* buf = static_cast<uint16_t*>(malloc(n * sizeof(uint16_t)));
  if (!buf) {
    sendJson(503, "{\"ok\":false,\"error\":\"no_memory\"}");
    return;
  }
  analogSetPinAttenuation(pin, ADC_11db);
  const int64_t start = esp_timer_get_time();
  int64_t next = start;
  for (long k = 0; k < n; ++k) {
    while (esp_timer_get_time() < next) {
    }
    buf[k] = static_cast<uint16_t>(analogReadMilliVolts(pin));
    next += us;
  }
  const uint32_t elapsed = static_cast<uint32_t>(esp_timer_get_time() - start);
  pinMode(pin, INPUT);
  String json;
  json.reserve(120 + n * 5);
  json += "{\"ok\":true,\"pin\":" + String(pin) + ",\"n\":" + String(n) +
          ",\"period_us\":" + String(us) + ",\"elapsed_us\":" + String(elapsed) + ",\"mv\":[";
  for (long k = 0; k < n; ++k) {
    if (k) json += ',';
    json += String(buf[k]);
  }
  json += "]}";
  free(buf);
  sendJson(200, json);
}

// GET /capture?n=2000&us=10 -> analisador logico das linhas do display (diagnostico
// da etapa 2). Limites mantem a captura abaixo de ~1 s e o texto abaixo de ~32 kB.
void handleCapture() {
  long n = server.hasArg("n") ? server.arg("n").toInt() : 2000;
  long us = server.hasArg("us") ? server.arg("us").toInt() : 10;
  if (n < 10 || n > 8000 || us < 2 || us > 1000 || n * us > 1000000L) {
    sendJson(400, "{\"ok\":false,\"error\":\"range\"}");
    return;
  }
  uint16_t* buf = static_cast<uint16_t*>(malloc(n * sizeof(uint16_t)));
  if (!buf) {
    sendJson(503, "{\"ok\":false,\"error\":\"no_memory\"}");
    return;
  }
  const uint32_t elapsed = displayCapture(buf, static_cast<size_t>(n), static_cast<uint32_t>(us));
  String json;
  json.reserve(160 + n * 4);
  json += "{\"ok\":true,\"n\":" + String(n) + ",\"period_us\":" + String(us) +
          ",\"elapsed_us\":" + String(elapsed) +
          ",\"bits\":[\"A\",\"B\",\"C\",\"D\",\"E\",\"F\",\"G\",\"PD\","
          "\"1A\",\"1B\",\"1C\",\"1D\",\"2DISP\",\"1L\"],\"data\":\"";
  char hex[5];
  for (long k = 0; k < n; ++k) {
    snprintf(hex, sizeof(hex), "%04x", buf[k]);
    json += hex;
  }
  json += "\"}";
  free(buf);
  sendJson(200, json);
}

// GET /log -> ultimas linhas do registro de eventos (texto).
void handleLog() {
  static char buf[64 * 146 + 1];
  eventLogText(buf, sizeof(buf));
  server.send(200, "text/plain; charset=utf-8", buf);
}

void handleUi() {
  server.send(200, "text/html", FPSTR(uiPage));
}

void handleNotFound() {
  server.send(404, "text/plain", "Not Found");
}

void handleOtaPage() {
  server.sendHeader("Connection", "close");
  String page = FPSTR(otaPage);
  page.replace("%FW%", BoardConfig::FirmwareTag);
  server.send(200, "text/html", page);
}

void handleOtaUploadDone() {
  server.sendHeader("Connection", "close");
  if (g_otaRejectReason.length() > 0) {
    server.send(400, "text/plain", "Rejected: " + g_otaRejectReason);
  } else if (Update.hasError()) {
    server.send(500, "text/plain", "Flash failed: " + String(Update.errorString()) + ". Running firmware untouched.");
  } else if (!Update.isFinished()) {
    server.send(500, "text/plain", "Upload incomplete. Running firmware untouched.");
  } else {
    server.send(200, "text/plain", "OK: Firmware gravado e validado com sucesso! Reiniciando...");
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
  String json = getStatusAsJson();
  // Acrescenta os campos de rede no mesmo objeto.
  json.remove(json.length() - 1);
  char extra[240];
  snprintf(extra, sizeof(extra),
           ",\"free_heap\":%u,\"ssid\":\"%s\",\"rssi\":%d,\"mac\":\"%s\",\"ap_ip\":\"%s\",\"hub_fail_streak\":%u,\"hub_task_stack_min\":%lu}",
           static_cast<unsigned int>(ESP.getFreeHeap()), WiFi.SSID().c_str(), WiFi.RSSI(),
           WiFi.macAddress().c_str(), WiFi.softAPIP().toString().c_str(), g_hubFailStreak,
           static_cast<unsigned long>(hubLinkMinFreeStackBytes()));
  json += extra;
  sendJson(200, json);
}

}  // namespace

void setupLocalHttpApi() {
  server.on("/", HTTP_GET, handleStatus);
  server.on("/status", HTTP_GET, handleStatus);
  server.on("/diag", HTTP_GET, handleDiag);
  server.on("/config", HTTP_GET, handleGetConfig);
  server.on("/config", HTTP_POST, handleCommand);
  server.on("/command", HTTP_POST, handleCommand);
  server.on("/setpoint", HTTP_POST, handleSetpoint);
  server.on("/display", HTTP_GET, handleDisplay);
  server.on("/capture", HTTP_GET, handleCapture);
  server.on("/scope", HTTP_GET, handleScope);
  server.on("/log", HTTP_GET, handleLog);
  server.on("/ui", HTTP_GET, handleUi);
  server.on("/update", HTTP_GET, handleOtaPage);
  server.on("/update", HTTP_POST, handleOtaUploadDone, handleOtaChunk);
  server.onNotFound(handleNotFound);
  server.begin();
}

void serviceLocalHttpApi() {
  server.handleClient();
}
