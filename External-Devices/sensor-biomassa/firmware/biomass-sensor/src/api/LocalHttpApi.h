// Web Server Handlers

void sendJson(const String& json) {
  server.sendHeader("Access-Control-Allow-Origin", "*");
  server.sendHeader("Cache-Control", "no-store");
  server.send(200, "application/json", json);
}

void handleRoot() {
  server.sendHeader("Cache-Control", "no-store");
  server.send_P(200, "text/html", WEB_UI_HTML);
}

void handleReadData() {
  buildDataJson();
  sendJson(g_lastDataJson);
}

void handleStatus() {
  sendJson(buildStatusJson());
}

void handleHistory() {
  uint32_t since = 0;
  if (server.hasArg("since")) {
    long v = server.arg("since").toInt();
    if (v > 0) since = (uint32_t)v;
  }
  sendJson(buildHistoryJson(since));
}

void handleBlankTable() {
  sendJson(buildBlankJson());
}

void handleCommand() {
  if (server.hasArg("plain")) {
    String body = server.arg("plain");
    g_inHttpHandler = true;
    processJsonCommand(body, /*allowBlocking=*/false);
    g_inHttpHandler = false;
    server.sendHeader("Access-Control-Allow-Origin", "*");
    server.send(200, "application/json", buildStatusJson());
  } else {
    server.send(400, "text/plain", "Bad Request - No Body");
  }
}

void handleNotFound() {
  server.send(404, "text/plain", "Not Found");
}

// Arduino Entry Points

