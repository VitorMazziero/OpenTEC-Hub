#include "HubClient.h"

#include <HTTPClient.h>
#include <WiFi.h>

#include "../core/AppContext.h"
#include "../protocol/CommandCodec.h"

void hubHello() {
  if (hubAnnounced || WiFi.status() != WL_CONNECTED) {
    return;
  }

  HTTPClient http;
  char url[64];
  snprintf(url, sizeof(url), "http://%s/agitatorHello", hubIp.toString().c_str());
  http.begin(url);
  http.setTimeout(500);
  const int responseCode = http.GET();
  if (responseCode > 0) {
    Serial.printf("Hub hello OK (%d)\n", responseCode);
    hubAnnounced = true;
  } else {
    Serial.printf("Hub hello failed (%d)\n", responseCode);
  }
  http.end();
}

void pollHub() {
  if (millis() - tHubPollMs < 500) {
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
  if (http.GET() == 200) {
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
  http.GET();
  http.end();
}
