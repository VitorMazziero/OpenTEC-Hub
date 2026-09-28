// ------------------------------------------------------------------
// Atualizacao do firmware do Hub pela rede Wi-Fi do proprio modulo (OTA).
// ------------------------------------------------------------------
// GET /update mostra a pagina de envio; POST /update recebe o ESP32S3-HUB.ino.bin e o
// grava na particao OTA inativa. A troca de imagem so acontece depois de Update.end(true)
// verificar a imagem inteira; ate la o firmware em execucao continua intacto e um envio
// interrompido nao muda nada. Exige o esquema de particoes padrao (duas particoes de app).
//
// Recusado enquanto o Hub comanda um processo (cascata do banho com referencia do reator,
// motor com rotacao): o reinicio interromperia o controle. O operador para o processo e
// envia de novo. A mesma verificacao roda no fim do envio, antes de trocar a imagem.
#include <Update.h>
#include <esp_ota_ops.h>

const char hubOtaPage[] PROGMEM = R"rawliteral(<!DOCTYPE html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Hub TECNAL OTA</title>
<style>body{font-family:sans-serif;max-width:560px;margin:2em auto;padding:0 1em}progress{width:100%}code{background:#eee;padding:0 .3em}</style>
</head><body><h2>Hub TECNAL &ndash; atualizar firmware</h2>
<p>Rodando: <b>%FW%</b> (%SSID%, parti&ccedil;&atilde;o %PART%, at&eacute; %MAX% KB).</p>
<p>%BUSY%</p>
<p>Envie <code>ESP32S3-HUB.ino.bin</code> (Arduino IDE: <i>Sketch &gt; Export Compiled Binary</i>,
ou <code>tools\ota_upload.ps1</code>). N&atilde;o envie <code>.merged.bin</code>, <code>.bootloader.bin</code>
ou <code>.partitions.bin</code>. O Hub reinicia sozinho ao final; os n&oacute;s reconectam em alguns segundos.</p>
<form id="f"><input type="file" name="firmware" accept=".bin" required> <input type="submit" value="Gravar"></form>
<progress id="p" value="0" max="100" hidden></progress><p id="s"></p>
<script>
const f=document.getElementById('f'),p=document.getElementById('p'),s=document.getElementById('s');
f.onsubmit=e=>{e.preventDefault();const x=new XMLHttpRequest();x.open('POST','/update');
x.upload.onprogress=v=>{p.hidden=false;p.value=Math.round(100*v.loaded/v.total);s.textContent='Enviando '+p.value+'%'};
x.onload=()=>{s.textContent=x.responseText;if(x.status==200)setTimeout(()=>location.reload(),15000)};
x.onerror=()=>{s.textContent='Conexao perdida. Se chegou a 100%, o Hub esta reiniciando; recarregue em alguns segundos.'};
x.send(new FormData(f))};
</script></body></html>)rawliteral";

struct HubOtaSession {
  AsyncWebServerRequest* owner = nullptr;
  bool rejected = false;
  char reason[96] = "";
  size_t nextLogBytes = 0;
};
HubOtaSession hubOta;
// Escrito pela tarefa do AsyncWebServer, lido pelo loop principal.
volatile uint32_t hubOtaRebootAtMs = 0;

// Motivo para recusar o OTA, ou "" com o Hub livre.
const char* hubOtaBusyReason() {
  bool bathActive = false;
  if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
    bathActive = tempControlRoute == TempControlRoute::ExternalBath && tempReferenceCommanded;
    xSemaphoreGive(stateMutex);
  }
  if (bathActive) return "cascata do banho ativa; zere o setpoint do reator antes";
  // Leitura de int alinhado: atomica; so o loop principal escreve.
  if (motorRPM != 0) return "motor com rotacao; pare o motor antes";
  return "";
}

void hubOtaReject(const char* reason) {
  hubOta.rejected = true;
  snprintf(hubOta.reason, sizeof(hubOta.reason), "%s", reason);
  if (Update.isRunning()) Update.abort();
  ESP32_AVISO(String("OTA recusado: ") + hubOta.reason);
}

void hubOtaPageHandler(AsyncWebServerRequest* request) {
  String page = FPSTR(hubOtaPage);
  const esp_partition_t* running = esp_ota_get_running_partition();
  const char* busy = hubOtaBusyReason();
  page.replace("%FW%", HUB_FIRMWARE_VERSION);
  page.replace("%SSID%", WIFI_SSID);
  page.replace("%PART%", running ? running->label : "?");
  page.replace("%MAX%", String(ESP.getFreeSketchSpace() / 1024));
  page.replace("%BUSY%", busy[0] ? String("<b>Bloqueado agora:</b> ") + busy + "." : String(""));
  request->send(200, "text/html", page);
}

void hubOtaUploadHandler(AsyncWebServerRequest* request, const String& filename, size_t index,
                         uint8_t* data, size_t len, bool final) {
  if (index == 0) {
    // Um envio por vez; o segundo recebe 409 no fim do corpo.
    if (hubOta.owner != nullptr) return;
    hubOta = HubOtaSession{};
    hubOta.owner = request;
    request->onDisconnect([request]() {
      if (hubOta.owner != request) return;
      if (Update.isRunning()) Update.abort();
      hubOta.owner = nullptr;
      ESP32_AVISO("OTA interrompido; firmware atual mantido");
    });
    ESP32_INFO(String("OTA iniciado: ") + filename);
    const char* busy = hubOtaBusyReason();
    if (busy[0]) {
      hubOtaReject(busy);
    } else if (!filename.startsWith("ESP32S3-HUB") || !filename.endsWith(".bin") ||
               filename.indexOf("merged") >= 0 || filename.indexOf("bootloader") >= 0 ||
               filename.indexOf("partitions") >= 0) {
      // Nome do export do Arduino: evita gravar por engano a imagem de um no (mesmo chip).
      hubOtaReject("envie a imagem do app do Hub (ESP32S3-HUB.ino.bin)");
    } else {
      if (Update.isRunning()) Update.abort();
      if (!Update.begin(UPDATE_SIZE_UNKNOWN, U_FLASH)) {
        hubOtaReject(Update.errorString());
      }
    }
  }
  if (hubOta.owner != request || hubOta.rejected) return;

  if (len > 0 && Update.write(data, len) != len) {
    hubOtaReject(Update.errorString());
    return;
  }
  const size_t received = index + len;
  if (received >= hubOta.nextLogBytes) {
    ESP32_INFO(String("OTA: ") + (received / 1024) + " KB recebidos");
    hubOta.nextLogBytes += 262144;
  }
  if (final) {
    const char* busy = hubOtaBusyReason();
    if (busy[0]) {
      // Processo iniciado durante o envio: nao trocar a imagem por baixo dele.
      hubOtaReject(busy);
    } else if (!Update.end(true)) {
      hubOtaReject(Update.errorString());
    } else {
      ESP32_EVT(String("OTA: imagem verificada, ") + received + " bytes");
    }
  }
}

void hubOtaDoneHandler(AsyncWebServerRequest* request) {
  if (hubOta.owner == nullptr) {
    request->send(400, "text/plain", "Nenhum arquivo recebido.");
    return;
  }
  if (hubOta.owner != request) {
    request->send(409, "text/plain", "Outro envio de firmware em andamento.");
    return;
  }
  AsyncWebServerResponse* response;
  if (hubOta.rejected) {
    response = request->beginResponse(400, "text/plain",
        String("Recusado: ") + hubOta.reason + ". O firmware atual continua rodando.");
  } else if (!Update.isFinished()) {
    response = request->beginResponse(500, "text/plain",
        "Envio incompleto. O firmware atual continua rodando.");
  } else {
    response = request->beginResponse(200, "text/plain",
        "OK: firmware gravado e verificado. O Hub reinicia em instantes.");
    const uint32_t at = millis() + 1500;
    hubOtaRebootAtMs = at == 0 ? 1 : at;
  }
  response->addHeader("Connection", "close");
  request->send(response);
  hubOta.owner = nullptr;
}

void registerOtaRoutes() {
  server.on("/update", HTTP_GET, hubOtaPageHandler);
  server.on("/update", HTTP_POST, hubOtaDoneHandler, hubOtaUploadHandler);
}

// Chamado pelo loop principal: reinicia depois que a resposta do POST saiu.
void serviceHubOtaReboot(unsigned long now) {
  const uint32_t at = hubOtaRebootAtMs;
  if (at == 0 || static_cast<int32_t>(now - at) < 0) return;
  if (flagPendingSave) {
    saveSettings();
    flagPendingSave = false;
  }
  ESP32_AVISO("Reiniciando no firmware novo (OTA)");
  Serial.flush();
  ESP.restart();
}
