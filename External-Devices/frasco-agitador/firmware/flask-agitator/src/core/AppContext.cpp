#include "AppContext.h"

WebServer server(80);
IPAddress hubIp(192, 168, 4, 1);

volatile Source lastSource = Source::POT;
volatile float targetPercent = 0.0f;
volatile bool dirRight = true;
// Safe after every reset: the local knob only regains authority through an
// explicit ActivePot:1 command from the Hub, local HTTP, or USB.
volatile bool potEnabled = false;
String latestTelemetry;

bool hubAnnounced = false;
uint32_t tHubPollMs = 0;
uint32_t tTelemetryMs = 0;
uint32_t tPwmMs = 0;
uint32_t tHubPushMs = 0;
uint32_t lastAppliedHubCmdId = 0;

uint32_t tLastScanKick = 0;
int lastSeenRssi[2] = {-999, -999};
int currentHubIndex = -1;
int desiredHubIndex = -1;

volatile bool g_otaInProgress = false;
unsigned long g_otaLastChunkMs = 0;
const unsigned long OTA_STALL_TIMEOUT_MS = 90000;
unsigned long g_otaRebootAtMs = 0;
String g_otaRejectReason = "";

uint8_t g_hubFailStreak = 0;
const unsigned long MAX_HUB_BACKOFF_MS = 15000;
