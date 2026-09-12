#include "AppContext.h"

WebServer server(80);
IPAddress hubIp(192, 168, 4, 1);

volatile Source lastSource = Source::POT;
volatile float targetPercent = 0.0f;
volatile bool dirRight = true;
volatile bool potEnabled = true;
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
