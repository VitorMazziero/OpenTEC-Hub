#include "NetworkManager.h"

#include <HTTPClient.h>
#include <WiFi.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"

void checkWifi() {
  const unsigned long now = millis();

  // Link Watchdog: se o streak de falhas atingiu o limite, o enlace esta mudo/zumbi
  if (g_hubFailStreak >= LINK_WATCHDOG_FAILS) {
    Serial.printf("[NET] Link zumbi detectado (streak=%u). Forcando queda da associacao...\n", g_hubFailStreak);
    g_hubFailStreak = 0;
    g_hubAnnounced = false;
    WiFi.disconnect(true, false);
    g_wifiState = WF_IDLE;
    g_wifiNextActionMs = now + 500;
    return;
  }

  if (now < g_wifiNextActionMs) {
    return;
  }

  if (WiFi.status() == WL_CONNECTED) {
    g_wifiState = WF_IDLE;
    g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
    return;
  }

  switch (g_wifiState) {
    case WF_IDLE:
      if (!g_lastKnownSsid.isEmpty()) {
        Serial.println("[NET] Connecting to known hub: " + g_lastKnownSsid + " on channel 6");
        WiFi.disconnect(false, false);
        WiFi.begin(g_lastKnownSsid.c_str(), g_lastKnownSsid.c_str(), 6);
        g_wifiState = WF_CONNECTING;
        g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
      } else {
        Serial.println("[NET] Starting async hub scan...");
        WiFi.scanDelete();
        WiFi.scanNetworks(true, true);
        g_wifiState = WF_SCANNING;
        g_wifiNextActionMs = now + 100;
      }
      break;

    case WF_SCANNING: {
      const int networkCount = WiFi.scanComplete();
      if (networkCount == -1) {
        g_wifiNextActionMs = now + 100;
        break;
      }

      String ssidToTry;
      for (int i = 0; i < networkCount; ++i) {
        const String ssid = WiFi.SSID(i);
        if (ssid == BoardConfig::HubSsidA || ssid == BoardConfig::HubSsidB) {
          ssidToTry = ssid;
          g_lastKnownSsid = ssid;
          break;
        }
      }
      WiFi.scanDelete();

      if (!ssidToTry.isEmpty()) {
        Serial.println("[NET] Hub found: " + ssidToTry + ". Connecting STA on channel 6.");
        WiFi.disconnect(false, false);
        WiFi.begin(ssidToTry.c_str(), ssidToTry.c_str(), 6);
        g_wifiState = WF_CONNECTING;
      } else {
        Serial.println("[NET] Hub not found. Keeping local AP active.");
        g_wifiState = WF_IDLE;
      }
      g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
      break;
    }

    case WF_CONNECTING:
      Serial.println("[NET] Connect attempt timed out. Will scan again later.");
      g_lastKnownSsid = "";
      g_wifiState = WF_IDLE;
      g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
      break;
  }
}

bool httpGet(const String& url, int& code, String& body) {
  HTTPClient http;
  http.begin(url);
  http.setReuse(false);
  http.setTimeout(2500);
#if defined(HTTPC_STRICT_FOLLOW_REDIRECTS)
  http.setFollowRedirects(HTTPC_STRICT_FOLLOW_REDIRECTS);
#endif
  code = http.GET();
  body = code > 0 ? http.getString() : String("err=") + code;
  http.end();
  return code >= 200 && code < 300;
}
