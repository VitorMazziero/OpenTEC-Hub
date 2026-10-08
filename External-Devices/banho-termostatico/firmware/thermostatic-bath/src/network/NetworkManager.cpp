#include "NetworkManager.h"

#include <HTTPClient.h>
#include <WiFi.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"

void checkWifi(bool hubEnabled) {
  // Nunca derrubar o caminho que esta transportando o upload OTA.
  if (g_otaInProgress) return;
  static bool tryHubB = false;
  const unsigned long now = millis();

  if (!hubEnabled) {
    if (WiFi.status() == WL_CONNECTED || g_wifiState != WF_IDLE) {
      WiFi.disconnect(false, false);
      g_wifiState = WF_IDLE;
    }
    g_hubAnnounced = false;
    g_hubFailStreak = 0;
    g_wifiNextActionMs = 0;
    return;
  }

  // Link Watchdog: streak de falhas no limite significa enlace mudo/zumbi.
  if (g_hubFailStreak >= LINK_WATCHDOG_FAILS) {
    Serial.printf("[NET] Link zumbi detectado (streak=%u). Forcando queda da associacao...\n", g_hubFailStreak);
    g_hubFailStreak = 0;
    g_hubAnnounced = false;
    WiFi.disconnect(false, false);
    g_wifiState = WF_IDLE;
    g_wifiNextActionMs = now + 500;
    return;
  }

  // Uma nova associação pode receber outro DHCP. Forçar novo hello antes do
  // próximo push mantém o vínculo IP que o Hub 10.5.1 exige.
  if (WiFi.status() == WL_CONNECTED) {
    g_lastKnownSsid = WiFi.SSID();
    tryHubB = g_lastKnownSsid == BoardConfig::HubSsidB;
    g_wifiState = WF_IDLE;
    g_wifiNextActionMs = 0;
    return;
  }
  g_hubAnnounced = false;

  if (g_wifiNextActionMs != 0 &&
      static_cast<int32_t>(now - g_wifiNextActionMs) < 0) return;

  if (g_wifiState == WF_CONNECTING) {
    Serial.println("[NET] Connect attempt timed out. Trying the other hub.");
    tryHubB = !tryHubB;
  }

  // AP+STA compartilha um radio: tentativas diretas no canal do Hub, sem scan.
  const char* ssid = tryHubB ? BoardConfig::HubSsidB : BoardConfig::HubSsidA;
  Serial.printf("[NET] Connecting to %s on channel %d\n", ssid, BoardConfig::HubWifiChannel);
  WiFi.disconnect(false, false);
  WiFi.begin(ssid, ssid, BoardConfig::HubWifiChannel);
  g_wifiState = WF_CONNECTING;
  g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
}

bool httpGet(const String& url, int& code, String& body, String* hubOwner) {
  HTTPClient http;
  http.begin(url);
  http.setReuse(false);
  http.setTimeout(2500);
#if defined(HTTPC_STRICT_FOLLOW_REDIRECTS)
  http.setFollowRedirects(HTTPC_STRICT_FOLLOW_REDIRECTS);
#endif
  if (hubOwner) {
    static const char* headerKeys[] = { "X-Hub-Owner" };
    http.collectHeaders(headerKeys, 1);
  }
  code = http.GET();
  body = code > 0 ? http.getString() : String("err=") + code;
  if (hubOwner) *hubOwner = code > 0 ? http.header("X-Hub-Owner") : String();
  http.end();
  return code >= 200 && code < 300;
}
