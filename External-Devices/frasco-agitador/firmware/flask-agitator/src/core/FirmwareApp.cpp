#include "FirmwareApp.h"

#include <Arduino.h>
#include <Update.h>
#include <WiFi.h>
#include <esp_task_wdt.h>

#include "../api/LocalHttpApi.h"
#include "../config/BoardConfig.h"
#include "../input/Potentiometer.h"
#include "../motor/MotorDriver.h"
#include "../network/HubClient.h"
#include "../network/NetworkManager.h"
#include "../protocol/CommandCodec.h"
#include "AppContext.h"

namespace {
constexpr uint32_t WDT_TIMEOUT_S = 15;
}

void firmwareSetup() {
  Serial.begin(115200);
  analogReadResolution(12);
  analogSetPinAttenuation(BoardConfig::PotentiometerPin, ADC_11db);

  pinMode(BoardConfig::RightEnablePin, OUTPUT);
  pinMode(BoardConfig::LeftEnablePin, OUTPUT);
  digitalWrite(BoardConfig::RightEnablePin, HIGH);
  digitalWrite(BoardConfig::LeftEnablePin, HIGH);

  if (!attachPwmPin(BoardConfig::RightPwmPin)) {
    while (true) {
      delay(1000);
    }
  }
  if (!attachPwmPin(BoardConfig::LeftPwmPin)) {
    while (true) {
      delay(1000);
    }
  }

  WiFi.mode(WIFI_AP_STA);
  WiFi.setSleep(false);
  if (WiFi.softAP(BoardConfig::AccessPointSsid, BoardConfig::AccessPointPassword)) {
    Serial.printf("AP  %s  IP: %s\n",
                  BoardConfig::AccessPointSsid,
                  WiFi.softAPIP().toString().c_str());
  }

  tLastScanKick = 0;
  kickAsyncScanIfDue();
  setupLocalHttpApi();

  applyDuty(BoardConfig::BoostDuty);
  delay(BoardConfig::BoostDurationMs);
  brakeMotor();
  Serial.println("Ready.");

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
    if (now - g_otaLastChunkMs > OTA_STALL_TIMEOUT_MS) {
      Serial.println("[OTA] Watchdog disparado: upload estagnou.");
      Update.abort();
      g_otaInProgress = false;
    }
    delay(1);
    return;
  }

  pollSerialCommand();
  kickAsyncScanIfDue();
  handleScanResultAndMaybeRoam();

  if (WiFi.status() == WL_CONNECTED) {
    hubHello();
    pollHub();
  } else {
    hubAnnounced = false;
  }

  servicePotentiometer();
  if (millis() - tPwmMs >= 10) {
    applyDuty(getEffectiveDuty(targetPercent));
    tPwmMs = millis();
  }

  if (millis() - tTelemetryMs >= 500) {
    char buf[64];
    snprintf(buf, sizeof(buf), "{\"time_s\":%lu,\"duty\":%.1f}", static_cast<unsigned long>(millis() / 1000), targetPercent);
    Serial.println(buf);
    latestTelemetry = String(buf);
    tTelemetryMs = millis();
  }

  // The push has its own timer so two blocking Hub requests do not start back-to-back.
  if (millis() - tHubPushMs >= BoardConfig::HubPushPeriodMs) {
    tHubPushMs = millis();
    pushTelemetryToHub();
  }
}
