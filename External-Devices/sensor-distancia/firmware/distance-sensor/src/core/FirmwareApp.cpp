#include "FirmwareApp.h"

#include <Arduino.h>
#include <Update.h>
#include <WiFi.h>
#include <esp_task_wdt.h>

#include "../api/LocalHttpApi.h"
#include "../config/BoardConfig.h"
#include "../network/NetworkManager.h"
#include "../protocol/ConfigCodec.h"
#include "../sensor/DistanceSensor.h"
#include "AppContext.h"

namespace {
constexpr uint32_t WDT_TIMEOUT_S = 15;
constexpr unsigned long MAX_HUB_BACKOFF_MS = 15000;
}

void firmwareSetup() {
  Serial.begin(115200);
  delay(300);
  Serial.println();
  Serial.println(BoardConfig::FirmwareTag);

  i2cInit();
  i2cScanOnce("[BOOT]");
  if (!sensorInit()) {
    Serial.println("[BOOT] Sensor init failed; will try again after cooldown.");
  }

  Serial.println("[NET] Setting mode to WIFI_AP_STA...");
  WiFi.mode(WIFI_AP_STA);
  const IPAddress apIp(192, 168, 5, 1);
  Serial.printf("[NET] Configuring AP on subnet %s (Channel 6)\n", apIp.toString().c_str());
  WiFi.softAPConfig(apIp, apIp, IPAddress(255, 255, 255, 0));
  Serial.printf("[NET] Starting AP: %s on channel 6\n", BoardConfig::AccessPointSsid);
  if (WiFi.softAP(BoardConfig::AccessPointSsid, "", 6)) {
    Serial.printf("[NET] AP IP: %s\n", WiFi.softAPIP().toString().c_str());
  } else {
    Serial.println("[NET] AP Start Failed!");
  }

  setupLocalHttpApi();
  Serial.println("[NET] Web server started. AP: 192.168.5.1");
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
      Serial.println("[CMD] Received: " + command);
      processConfigUpdate(command);
    }
  }

  checkWifi();
  if (now - lastSampleMs >= SAMPLE_PERIOD_MS) {
    lastSampleMs = now;

    int mm = -1;
    if (readSingleShot(mm)) {
      failStreak = 0;
      if (mm > 0 && mm < 4000) {
        lastGoodRawMm = mm;
      }
    } else {
      ++failStreak;
      maybeRecover();
    }

    float distance = -1.0f;
    if (mm > 0) {
      distance = static_cast<float>(mm) - BoardConfig::OffsetMm;
      if (distance < 0) {
        distance = 0;
      }
    }

    const float seconds = now / 1000.0f;
    Serial.printf("{\"time\":%.1f,\"distance\":%.0f}\n", seconds, distance);
    g_lastValidDistance = distance;
    g_lastSampleTimeSec = seconds;

    unsigned long currentSendInterval = SEND_PERIOD_MS;
    if (g_hubFailStreak > 0) {
      uint8_t shift = (g_hubFailStreak > 4) ? 4 : g_hubFailStreak;
      currentSendInterval = min(SEND_PERIOD_MS * (1UL << shift), MAX_HUB_BACKOFF_MS);
    }

    if (WiFi.status() == WL_CONNECTED && now - lastSendMs >= currentSendInterval) {
      lastSendMs = now;
      char url[128];
      snprintf(url, sizeof(url), "%s?distance=%d&time=%.1f",
               sensorHubURL.c_str(), static_cast<int>(distance), seconds);
      Serial.print("HTTP GET: ");
      Serial.println(url);
      int code;
      String body;
      if (httpGet(url, code, body)) {
        if (g_hubFailStreak > 0) {
          Serial.printf("[Hub] Conexao restabelecida apos %u falha(s).\n", g_hubFailStreak);
        }
        g_hubFailStreak = 0;
        Serial.printf("Response: %d\n", code);
      } else {
        if (g_hubFailStreak < 255) g_hubFailStreak++;
        Serial.printf("HTTP error: %d \"%s\" (streak=%u, backoff=%lu ms)\n",
                      code, body.c_str(), g_hubFailStreak, currentSendInterval);
      }
    }
  }

  delay(1);
}

