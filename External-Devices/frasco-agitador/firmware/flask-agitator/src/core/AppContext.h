#pragma once

#include <Arduino.h>
#include <WebServer.h>

enum class Source : uint8_t { POT, WIFI, USB, HUB };

extern WebServer server;
extern IPAddress hubIp;

extern volatile Source lastSource;
extern volatile float targetPercent;
extern volatile bool dirRight;
extern volatile bool potEnabled;
extern String latestTelemetry;

extern bool hubAnnounced;
extern uint32_t tHubPollMs;
extern uint32_t tTelemetryMs;
extern uint32_t tPwmMs;
extern uint32_t tHubPushMs;
extern uint32_t lastAppliedHubCmdId;

extern uint32_t tLastScanKick;
extern int lastSeenRssi[2];
extern int currentHubIndex;
extern int desiredHubIndex;
