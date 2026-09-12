#include "LocalHttpApi.h"

#include <Arduino.h>

#include "../core/AppContext.h"
#include "../protocol/ConfigCodec.h"

namespace {
void handleRoot() {
  const String json = "{\"time\":" + String(g_lastSampleTimeSec, 1) +
                      ",\"distance\":" + String(static_cast<int>(g_lastValidDistance)) + "}";
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
  Serial.println("[HTTP] Body: " + body);
  processConfigUpdate(body);
  server.send(200, "text/plain", "Config Updated");
}

void handleNotFound() {
  server.send(404, "text/plain", "Not Found");
}
}

void setupLocalHttpApi() {
  server.on("/", HTTP_GET, handleRoot);
  server.on("/config", HTTP_GET, handleGetConfig);
  server.on("/config", HTTP_POST, handleConfig);
  server.onNotFound(handleNotFound);
  server.begin();
}

void serviceLocalHttpApi() {
  server.handleClient();
}
