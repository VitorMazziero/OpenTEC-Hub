#include <cassert>
#include <WiFi.h>
#include "../../firmware/thermostatic-bath/src/core/AppContext.h"
#include "../../firmware/thermostatic-bath/src/network/NetworkManager.h"

bool g_serialVerbose = false;
SerialClass Serial;
WiFiClass WiFi;
static uint32_t nowMs = 0;
unsigned long millis() { return nowMs; }

int main() {
  assert(BathConfig{}.hubEnabled == 1);
  WiFi.connectionStatus = 0;
  checkWifi(true);
  assert(WiFi.ssid == "ModuloTECNAL_1" && WiFi.channel == 6);
  nowMs = 9999;
  checkWifi(true);
  assert(WiFi.begins == 1);
  nowMs = 10000;
  checkWifi(true);
  assert(WiFi.ssid == "ModuloTECNAL_2" && WiFi.begins == 2);
  nowMs = 20000;
  checkWifi(true);
  assert(WiFi.ssid == "ModuloTECNAL_1");

  // Nova associacao e perda do Hub: tentar o ultimo Hub sem espera de 10 s.
  WiFi.connectionStatus = WL_CONNECTED;
  checkWifi(true);
  assert(g_lastKnownSsid == "ModuloTECNAL_1");
  g_hubAnnounced = true;
  WiFi.connectionStatus = 0;
  ++nowMs;
  checkWifi(true);
  assert(!g_hubAnnounced && WiFi.begins == 4);

  // Watchdog: manter o radio ativo e dar 500 ms para reassociacao.
  WiFi.connectionStatus = WL_CONNECTED;
  g_hubFailStreak = LINK_WATCHDOG_FAILS;
  checkWifi(true);
  assert(!WiFi.radioOff && g_hubFailStreak == 0);
  const unsigned before = WiFi.begins;
  nowMs += 499;
  checkWifi(true);
  assert(WiFi.begins == before);
  ++nowMs;
  checkWifi(true);
  assert(WiFi.begins == before + 1);

  // OTA por STA: nem watchdog nem desabilitacao podem cortar o upload.
  WiFi.connectionStatus = WL_CONNECTED;
  g_otaInProgress = true;
  g_hubFailStreak = LINK_WATCHDOG_FAILS;
  const unsigned disconnects = WiFi.disconnects;
  checkWifi(true);
  checkWifi(false);
  assert(WiFi.disconnects == disconnects && WiFi.status() == WL_CONNECTED);
  g_otaInProgress = false;
  checkWifi(false);
  assert(WiFi.status() != WL_CONNECTED && g_wifiNextActionMs == 0);
  checkWifi(true);
  assert(WiFi.begins == before + 2);

  // Deadline atravessando o rollover de millis().
  checkWifi(false);
  nowMs = UINT32_MAX - 5000;
  checkWifi(true);
  const unsigned rolloverBegins = WiFi.begins;
  nowMs = 4998;
  checkWifi(true);
  assert(WiFi.begins == rolloverBegins);
  nowMs = 4999;
  checkWifi(true);
  assert(WiFi.begins == rolloverBegins + 1 && !WiFi.radioOff);
  std::puts("NETWORK PASSED: A/B timeout, reconnect, watchdog, OTA, disable/enable, rollover");
}
