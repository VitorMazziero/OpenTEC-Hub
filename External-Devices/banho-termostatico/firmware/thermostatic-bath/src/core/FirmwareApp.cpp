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
#include "../network/HubLink.h"
#include "../protocol/ConfigCodec.h"
#include "../setpoint/SetpointGuard.h"
#include "../setpoint/SetpointManager.h"
#include "../storage/NvsConfig.h"
#include "AppContext.h"

constexpr uint32_t WDT_TIMEOUT_S = 15;

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

  hubLinkPublishSnapshot(millis());
  hubLinkInit();
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
    hubLinkPublishSnapshot(now);
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

  // O parser e o SetpointManager continuam exclusivamente no loop principal.
  // A tarefa HubLink só entrega aqui corpos já recebidos, sem tocar nos relés.
  hubLinkServiceMainLoop();

  keypadService(now);
  keySenseService(now);
  displayService(now);
  setpointService(now);
  guardService(now);

  hubLinkPublishSnapshot(now);

  delay(1);
}
