
// OTA routes. Both run on the AsyncTCP task: the upload callback only feeds
// Update, everything slow (the reboot) is handed to loop().
void setupOTA() {
  server.on("/update", HTTP_GET, [](AsyncWebServerRequest *request) {
    request->send(200, "text/html", otaPage);
  });

  server.on("/update", HTTP_POST,
    // Completion: runs once, after the final upload chunk.
    [](AsyncWebServerRequest *request) {
      String msg;
      bool ok = false;
      if (otaRejectReason.length()) {
        msg = "Rejected: " + otaRejectReason;
      } else if (Update.hasError()) {
        msg = "Flash failed: " + String(Update.errorString()) + ". Running firmware untouched.";
      } else if (!Update.isFinished()) {
        msg = "Upload incomplete. Running firmware untouched.";
      } else {
        ok = true;
        msg = "OK: " + String(Update.progress()) + " bytes written. Rebooting into new firmware...";
      }
      Serial.println("[OTA] " + msg);
      AsyncWebServerResponse *response = request->beginResponse(ok ? 200 : 400, "text/plain", msg);
      response->addHeader("Connection", "close");
      request->send(response);
      otaInProgress = false;
      if (ok) otaRebootAtMs = millis() + 500;
    },
    // Chunk handler: called for every piece of the multipart body.
    [](AsyncWebServerRequest *request, String filename, size_t index, uint8_t *data, size_t len, bool final) {
      if (index == 0) {
        otaRejectReason = "";
        // Only the app image belongs in an OTA slot. The other exports start with the
        // same 0xE9 magic, so Update would accept them and the name is the only tell.
        if (filename.indexOf("merged") >= 0 || filename.indexOf("bootloader") >= 0 || filename.indexOf("partitions") >= 0) {
          otaRejectReason = "'" + filename + "' is not the app image, send the plain .ino.bin";
          Serial.println("[OTA] " + otaRejectReason);
          return;
        }
        Serial.printf("[OTA] Upload start: %s\n", filename.c_str());
        if (Update.isRunning()) Update.abort();   // safe here: same task that writes
        otaStalled = false;
        otaNextProgressLog = 0;
        if (!Update.begin(UPDATE_SIZE_UNKNOWN)) Update.printError(Serial);
      }
      if (otaRejectReason.length()) return;
      // Stamped before the flag so loop() never pairs a set flag with a stale time.
      otaLastChunkMs = millis();
      otaInProgress = true;
      if (otaStalled) {
        Serial.printf("[OTA] Data resumed at %u bytes.\n", (unsigned)(index + len));
        otaStalled = false;
      }
      if (index + len >= otaNextProgressLog) {
        Serial.printf("[OTA] %u KB received, t=%lu ms\n", (unsigned)((index + len) / 1024), millis());
        otaNextProgressLog += 131072;
      }
      if (!Update.hasError() && len) {
        if (Update.write(data, len) != len) Update.printError(Serial);
      }
      if (final) {
        if (Update.end(true)) Serial.printf("[OTA] Image verified, %u bytes.\n", (unsigned)(index + len));
        else Update.printError(Serial);
      }
    });
}

