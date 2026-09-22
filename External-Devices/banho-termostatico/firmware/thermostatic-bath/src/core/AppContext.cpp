#include "AppContext.h"

#include "../config/BoardConfig.h"

WebServer server(80);
BathConfig g_cfg;

float g_spShadow = BoardConfig::InitialSetpointC;
bool  g_spKnown  = true;
float g_spTarget = BoardConfig::InitialSetpointC;
uint32_t g_lastCmdId = 0;
uint8_t g_mode = MODE_MANUAL;

uint32_t g_manualPressCount = 0;
unsigned long g_manualActivityMs = 0;

String g_lastKnownSsid;
WifiReconnectState g_wifiState = WF_IDLE;
unsigned long g_wifiNextActionMs = 0;
unsigned long WIFI_RECONNECT_PERIOD_MS = 10000;

volatile bool g_otaInProgress = false;
unsigned long g_otaLastChunkMs = 0;
const unsigned long OTA_STALL_TIMEOUT_MS = 90000;
unsigned long g_otaRebootAtMs = 0;
String g_otaRejectReason = "";

volatile uint8_t g_hubFailStreak = 0;
volatile bool g_hubAnnounced = false;

volatile bool g_hubOwnerFlag = false;
volatile unsigned long g_hubOwnerSeenMs = 0;
uint32_t g_hubRejectCmdId = 0;
char g_hubRejectErr[32] = "";

bool hubOwnershipActive(unsigned long now) {
  if (!g_cfg.hubEnabled || !g_hubOwnerFlag || g_hubOwnerSeenMs == 0) return false;
  return now - g_hubOwnerSeenMs <= HUB_OWNERSHIP_TIMEOUT_MS;
}

const char* keyName(Key k) {
  switch (k) {
    case KEY_STAR:  return "star";
    case KEY_UP:    return "up";
    case KEY_DOWN:  return "down";
    case KEY_ENTER: return "enter";
    default:        return "?";
  }
}

bool keyFromName(const char* name, Key& out) {
  if (!name) return false;
  if (!strcmp(name, "star") || !strcmp(name, "*"))     { out = KEY_STAR;  return true; }
  if (!strcmp(name, "up") || !strcmp(name, "inc"))     { out = KEY_UP;    return true; }
  if (!strcmp(name, "down") || !strcmp(name, "dec"))   { out = KEY_DOWN;  return true; }
  if (!strcmp(name, "enter") || !strcmp(name, "ok"))   { out = KEY_ENTER; return true; }
  return false;
}
