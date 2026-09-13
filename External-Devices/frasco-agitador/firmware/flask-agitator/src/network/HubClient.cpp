#include "HubClient.h"

#include <HTTPClient.h>
#include <WiFi.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"
#include "../protocol/CommandCodec.h"

void hubHello() {
  static unsigned long lastHelloCheckMs = 0;
  const unsigned long now = millis();
  if (WiFi.status() != WL_CONNECTED) {
    return;
  }
  if (hubAnnounced && (now - lastHelloCheckMs < 30000)) {
    return;
  }
  lastHelloCheckMs = now;

  HTTPClient http;
  char url[140];
  snprintf(url, sizeof(url), "http://%s/nodeHello?dev=agitator&ver=%s&mac=%s",
           hubIp.toString().c_str(), BoardConfig::FirmwareVersion, WiFi.macAddress().c_str());
  http.begin(url);
  http.setTimeout(800);
  int responseCode = http.GET();
  if (responseCode == 404) {
    http.end();
    snprintf(url, sizeof(url), "http://%s/agitatorHello", hubIp.toString().c_str());
    http.begin(url);
    http.setTimeout(800);
    responseCode = http.GET();
  }
  if (responseCode > 0 && responseCode < 300) {
    Serial.printf("Hub hello OK (%d)\n", responseCode);
    hubAnnounced = true;
    g_hubFailStreak = 0;
  } else {
    if (g_hubFailStreak < 255) g_hubFailStreak++;
    Serial.printf("Hub hello failed (%d, streak=%u)\n", responseCode, g_hubFailStreak);
  }
  http.end();
}

void pollHub() {
  unsigned long pollInterval = 500;
  if (g_hubFailStreak > 0) {
    uint8_t shift = (g_hubFailStreak > 4) ? 4 : g_hubFailStreak;
    pollInterval = min(500UL * (1UL << shift), MAX_HUB_BACKOFF_MS);
  }

  if (millis() - tHubPollMs < pollInterval) {
    return;
  }
  tHubPollMs = millis();
  if (WiFi.status() != WL_CONNECTED) {
    return;
  }

  HTTPClient http;
  char url[64];
  snprintf(url, sizeof(url), "http://%s/agitatorCommand", hubIp.toString().c_str());
  http.begin(url);
  http.setTimeout(700);
  int httpCode = http.GET();
  if (httpCode == 200) {
    g_hubFailStreak = 0;
    String body = http.getString();
    body.trim();
    if (body.length() > 2 && body != "{}") {
      const uint32_t commandId = extractCmdId(body);
      if (commandId == 0 || commandId != lastAppliedHubCmdId) {
        const bool applied = parseAndApplyJson(body, Source::HUB);
        if (commandId != 0) {
          // Unsupported payloads are acknowledged because retry cannot make them usable.
          lastAppliedHubCmdId = commandId;
          Serial.printf(applied ? "[HubCmd] Applied cmd_id=%lu\n" :
                                  "[HubCmd] cmd_id=%lu had no usable key\n",
                        static_cast<unsigned long>(commandId));
        }
      }
    }
  } else {
    if (g_hubFailStreak < 255) g_hubFailStreak++;
  }
  http.end();
}

void pushTelemetryToHub() {
  if (WiFi.status() != WL_CONNECTED) {
    return;
  }

  HTTPClient http;
  char url[192];
  snprintf(url, sizeof(url),
           "http://%s/agitatorData?pct=%.1f&dir=%d&pot=%d&src=%s&secs=%lu&ack_cmd_id=%lu",
           hubIp.toString().c_str(),
           targetPercent,
           dirRight ? 1 : 0,
           potEnabled ? 1 : 0,
           srcName(lastSource),
           static_cast<unsigned long>(millis() / 1000),
           static_cast<unsigned long>(lastAppliedHubCmdId));
  http.begin(url);
  http.setTimeout(400);
  int code = http.GET();
  if (code >= 200 && code < 300) {
    g_hubFailStreak = 0;
  } else {
    if (g_hubFailStreak < 255) g_hubFailStreak++;
  }
  http.end();
}
