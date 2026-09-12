#include "AppContext.h"

#include "../config/BoardConfig.h"

VL53L0X sensor;
WebServer server(80);
String sensorHubURL = BoardConfig::HubUrl;

unsigned long SAMPLE_PERIOD_MS = 1000;
unsigned long SEND_PERIOD_MS = 1000;
unsigned long lastSampleMs = 0;
unsigned long lastSendMs = 0;
unsigned long WIFI_RECONNECT_PERIOD_MS = 10000;

int failStreak = 0;
unsigned long lastRecovery = 0;
unsigned long COOLDOWN_SOFT_MS = 15000;
unsigned long COOLDOWN_BUS_MS = 15000;
unsigned long COOLDOWN_XSHUT_MS = 30000;
int L1_SOFT_REINIT = 5;
int L2_BUS_CLEAR = 10;
int L3_XSHUT = 20;

int lastGoodRawMm = -1;
float g_lastValidDistance = -1.0f;
float g_lastSampleTimeSec = 0.0f;
float g_offsetMm = BoardConfig::OffsetMm;

String g_lastKnownSsid;
WifiReconnectState g_wifiState = WF_IDLE;
unsigned long g_wifiNextActionMs = 0;

volatile bool g_otaInProgress = false;
unsigned long g_otaLastChunkMs = 0;
const unsigned long OTA_STALL_TIMEOUT_MS = 90000;
unsigned long g_otaRebootAtMs = 0;
String g_otaRejectReason = "";

uint8_t g_hubFailStreak = 0;
bool g_hubAnnounced = false;
