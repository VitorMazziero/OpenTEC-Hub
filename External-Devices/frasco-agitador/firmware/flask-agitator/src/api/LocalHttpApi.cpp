#include "LocalHttpApi.h"

#include "../core/AppContext.h"
#include "../protocol/CommandCodec.h"

namespace {
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
}

void setupLocalHttpApi() {
  server.on("/cmd", HTTP_POST, handleCommand);
  server.on("/read", HTTP_GET, handleRead);
  server.begin();
}

void serviceLocalHttpApi() {
  server.handleClient();
}
