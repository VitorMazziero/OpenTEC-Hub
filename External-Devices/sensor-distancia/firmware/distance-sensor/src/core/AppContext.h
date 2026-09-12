#pragma once

#include <Arduino.h>
#include <VL53L0X.h>
#include <WebServer.h>

enum WifiReconnectState { WF_IDLE, WF_SCANNING, WF_CONNECTING };

extern VL53L0X sensor;
extern WebServer server;
extern String sensorHubURL;

extern unsigned long SAMPLE_PERIOD_MS;
extern unsigned long SEND_PERIOD_MS;
extern unsigned long lastSampleMs;
extern unsigned long lastSendMs;
extern unsigned long WIFI_RECONNECT_PERIOD_MS;

extern int failStreak;
extern unsigned long lastRecovery;
extern unsigned long COOLDOWN_SOFT_MS;
extern unsigned long COOLDOWN_BUS_MS;
extern unsigned long COOLDOWN_XSHUT_MS;
extern int L1_SOFT_REINIT;
extern int L2_BUS_CLEAR;
extern int L3_XSHUT;

extern int lastGoodRawMm;
extern float g_lastValidDistance;
extern float g_lastSampleTimeSec;

extern String g_lastKnownSsid;
extern WifiReconnectState g_wifiState;
extern unsigned long g_wifiNextActionMs;
