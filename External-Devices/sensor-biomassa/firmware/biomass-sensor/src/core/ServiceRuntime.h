void serviceNetwork() {
  if (g_wdtReady) esp_task_wdt_reset();
  if (g_serverStarted && !g_inHttpHandler) server.handleClient();
  handleSerialInput(/*allowBlocking=*/false);
}

void delayServiced(uint32_t ms) {
  const uint32_t start = millis();
  while (millis() - start < ms) {
    serviceNetwork();
    uint32_t remaining = ms - (millis() - start);
    delay(remaining > 10 ? 10 : remaining);
  }
  serviceNetwork();
}

// Median Reading Filter

