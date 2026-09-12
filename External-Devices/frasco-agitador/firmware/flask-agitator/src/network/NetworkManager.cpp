#include "NetworkManager.h"

#include <Arduino.h>
#include <WiFi.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"

namespace {
int hubIndexFromSsid(const String& ssid) {
  if (ssid == BoardConfig::HubSsids[0]) {
    return 0;
  }
  if (ssid == BoardConfig::HubSsids[1]) {
    return 1;
  }
  return -1;
}
}

void kickAsyncScanIfDue() {
  if (g_hubFailStreak >= 8) {
    Serial.printf("[NET] Link zumbi detectado (streak=%u). Forcando queda da associacao...\n", g_hubFailStreak);
    g_hubFailStreak = 0;
    WiFi.disconnect(true, false);
    currentHubIndex = -1;
    hubAnnounced = false;
    tLastScanKick = 0;
  }

  if (WiFi.scanComplete() == WIFI_SCAN_RUNNING) {
    return;
  }
  const uint32_t now = millis();
  if (now - tLastScanKick < BoardConfig::ScanIntervalMs) {
    return;
  }

  WiFi.scanDelete();
  WiFi.scanNetworks(true, false, false, BoardConfig::ScanChannelMs);
  tLastScanKick = now;
}

void handleScanResultAndMaybeRoam() {
  const int networkCount = WiFi.scanComplete();
  if (networkCount < 0) {
    return;
  }

  lastSeenRssi[0] = lastSeenRssi[1] = -999;
  for (int i = 0; i < networkCount; ++i) {
    const int index = hubIndexFromSsid(WiFi.SSID(i));
    if (index >= 0) {
      lastSeenRssi[index] = WiFi.RSSI(i);
    }
  }
  WiFi.scanDelete();

  int bestIndex = -1;
  int bestRssi = -999;
  for (int i = 0; i < 2; ++i) {
    if (lastSeenRssi[i] > bestRssi) {
      bestRssi = lastSeenRssi[i];
      bestIndex = i;
    }
  }
  desiredHubIndex = bestIndex;

  if (WiFi.status() != WL_CONNECTED) {
    if (desiredHubIndex >= 0) {
      Serial.printf("Connecting to nearest hub: %s (RSSI %d dBm) on channel 6\n",
                    BoardConfig::HubSsids[desiredHubIndex],
                    bestRssi);
      WiFi.disconnect(true, false);
      WiFi.begin(BoardConfig::HubSsids[desiredHubIndex], BoardConfig::HubPasswords[desiredHubIndex], 6);
      currentHubIndex = desiredHubIndex;
      hubAnnounced = false;
    } else if (desiredHubIndex < 0) {
      Serial.println("No SensorHub APs found.");
    }
    return;
  }

  const int connectedIndex = hubIndexFromSsid(WiFi.SSID());
  if (connectedIndex >= 0) {
    currentHubIndex = connectedIndex;
  }
  if (desiredHubIndex < 0 || connectedIndex < 0 || desiredHubIndex == connectedIndex) {
    return;
  }

  const int otherRssi = lastSeenRssi[desiredHubIndex];
  const int currentRssi = WiFi.RSSI();
  if (otherRssi != -999 && otherRssi - currentRssi >= BoardConfig::SwitchDeltaDb) {
    Serial.printf("Roaming: switching %s (RSSI %d) → %s (RSSI %d)\n",
                  BoardConfig::HubSsids[connectedIndex],
                  currentRssi,
                  BoardConfig::HubSsids[desiredHubIndex],
                  otherRssi);
    WiFi.disconnect(true, false);
    delay(50);
    WiFi.begin(BoardConfig::HubSsids[desiredHubIndex], BoardConfig::HubPasswords[desiredHubIndex], 6);
    currentHubIndex = desiredHubIndex;
    hubAnnounced = false;
  }
}
