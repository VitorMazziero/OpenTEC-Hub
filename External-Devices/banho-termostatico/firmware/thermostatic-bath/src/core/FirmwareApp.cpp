#include "FirmwareApp.h"

#include <Arduino.h>
#include <Update.h>
#include <WiFi.h>
#include <esp_task_wdt.h>

#include "../api/LocalHttpApi.h"
#include "../config/BoardConfig.h"
#include "../display/DisplayReader.h"
#include "../keypad/KeyPresser.h"
#include "../keypad/KeySense.h"
#include "../network/NetworkManager.h"
#include "../protocol/ConfigCodec.h"
#include "../setpoint/SetpointGuard.h"
#include "../setpoint/SetpointManager.h"
#include "../storage/NvsConfig.h"
#include "AppContext.h"

namespace {
constexpr uint32_t WDT_TIMEOUT_S = 15;
constexpr unsigned long MAX_HUB_BACKOFF_MS = 15000;
unsigned long g_lastSendMs = 0;
unsigned long g_lastHelloMs = 0;

void sendHubHello() {
  char url[140];
  snprintf(url, sizeof(url), "%s?dev=%s&ver=v1&mac=%s",
           BoardConfig::HubHelloUrl, BoardConfig::DeviceKey, WiFi.macAddress().c_str());
  int code;
  String body;
  if (httpGet(url, code, body)) {
    g_hubAnnounced = true;
    Serial.printf("[Hub] Hello registrado (%d)\n", code);
  } else {
    Serial.printf("[Hub] Hello falhou (%d)\n", code);
  }
}

// Push periodico ao Hub. A rota /bath ainda nao existe no Hub; o formato segue o
// dos outros nos (eco de estado + ack_cmd_id, comando por carona na resposta).
void pushToHub(unsigned long now) {
  unsigned long interval = g_cfg.sendPeriodMs;
  if (g_hubFailStreak > 0) {
    const uint8_t shift = (g_hubFailStreak > 4) ? 4 : g_hubFailStreak;
    interval = min(g_cfg.sendPeriodMs * (1UL << shift), MAX_HUB_BACKOFF_MS);
  }
  if (now - g_lastSendMs < interval) return;
  g_lastSendMs = now;

  float deviation;
  const bool devOk = guardDeviation(deviation);
  char url[300];
  snprintf(url, sizeof(url),
           "%s?sp=%.2f&known=%d&target=%.2f&state=%s&pv=%.2f&pv_ok=%d&mode=%u&dev=%.2f&dev_ok=%d&time=%.1f&ack_cmd_id=%lu",
           BoardConfig::HubUrl, g_spShadow, g_spKnown ? 1 : 0, g_spTarget, setpointStateName(),
           displayPvValid() ? displayPv() : -1.0f, displayPvValid() ? 1 : 0,
           g_mode, devOk ? deviation : 0.0f, devOk ? 1 : 0, now / 1000.0f,
           static_cast<unsigned long>(g_lastCmdId));
  int code;
  String body;
  if (httpGet(url, code, body)) {
    g_hubFailStreak = 0;
    if (code == 200 && body.length() > 1 && body[0] == '{') {
      String reply;
      processCommand(body, reply);
      Serial.printf("[Hub] Comando por carona: %s -> %s\n", body.c_str(), reply.c_str());
    }
  } else {
    if (g_hubFailStreak < 255) g_hubFailStreak++;
    Serial.printf("[Hub] HTTP error: %d (streak=%u)\n", code, g_hubFailStreak);
  }
}
}  // namespace

void firmwareSetup() {
  // Reles abertos antes de qualquer outra coisa: e a unica saida fisica do no.
  keypadInit();

  Serial.begin(115200);
  delay(300);
  Serial.println();
  Serial.println(BoardConfig::FirmwareTag);

  loadNvsConfig();
  loadNvsState();
  setpointInit();
  guardInit();
  keySenseInit();
  displayInit();

  WiFi.mode(WIFI_AP_STA);
  const IPAddress apIp(192, 168, 8, 1);
  WiFi.softAPConfig(apIp, apIp, IPAddress(255, 255, 255, 0));
  if (WiFi.softAP(BoardConfig::AccessPointSsid, "", 6)) {
    Serial.printf("[NET] AP %s em %s (canal 6)\n", BoardConfig::AccessPointSsid, WiFi.softAPIP().toString().c_str());
  } else {
    Serial.println("[NET] AP Start Failed!");
  }

  setupLocalHttpApi();
  Serial.println("[NET] Web server started. UI at http://192.168.8.1/ui");
  g_wifiNextActionMs = 0;
  checkWifi();

  esp_task_wdt_config_t twdt_config = {
      .timeout_ms     = WDT_TIMEOUT_S * 1000,
      .idle_core_mask = 0,
      .trigger_panic  = true
  };
  esp_err_t err = esp_task_wdt_init(&twdt_config);
  if (err == ESP_ERR_INVALID_STATE) {
    esp_task_wdt_reconfigure(&twdt_config);
  }
  esp_task_wdt_add(NULL);
  Serial.println("[WDT] Task Watchdog inicializado (15s).");
}

void firmwareLoop() {
  esp_task_wdt_reset();
  const unsigned long now = millis();
  serviceLocalHttpApi();

  if (g_otaRebootAtMs > 0 && now >= g_otaRebootAtMs) {
    Serial.println("[OTA] Reiniciando no novo firmware...");
    delay(100);
    ESP.restart();
  }

  if (g_otaInProgress) {
    // Nenhum rele pode ficar fechado enquanto o laco so atende o upload.
    if (keypadBusy()) setpointAbort();
    if (now - g_otaLastChunkMs > OTA_STALL_TIMEOUT_MS) {
      Serial.println("[OTA] Watchdog disparado: upload estagnou.");
      Update.abort();
      g_otaInProgress = false;
    }
    delay(1);
    return;
  }

  if (Serial.available() > 0) {
    String command = Serial.readStringUntil('\n');
    command.trim();
    if (!command.isEmpty() && command.startsWith("{")) {
      String reply;
      processCommand(command, reply);
      Serial.println(reply);
    } else if (command == "status") {
      Serial.println(getStatusAsJson());
    } else if (command == "config") {
      Serial.println(getConfigAsJson());
    }
  }

  keypadService(now);
  keySenseService(now);
  displayService(now);
  setpointService(now);
  guardService(now);

  // O httpGet bloqueia ate 2,5 s; com um rele fechado isso viraria um toque longo
  // (o C404 interpreta tecla mantida como "voltar a tela principal"). Nada de
  // trafego com o Hub enquanto ha toques em andamento.
  checkWifi();
  if (g_cfg.hubEnabled && WiFi.status() == WL_CONNECTED && !keypadBusy()) {
    if (!g_hubAnnounced || now - g_lastHelloMs >= 30000) {
      g_lastHelloMs = now;
      sendHubHello();
    }
    pushToHub(now);
  }

  delay(1);
}
