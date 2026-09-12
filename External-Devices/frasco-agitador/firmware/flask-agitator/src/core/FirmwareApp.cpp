#include "FirmwareApp.h"

#include <Arduino.h>
#include <WiFi.h>

#include "../api/LocalHttpApi.h"
#include "../config/BoardConfig.h"
#include "../input/Potentiometer.h"
#include "../motor/MotorDriver.h"
#include "../network/HubClient.h"
#include "../network/NetworkManager.h"
#include "../protocol/CommandCodec.h"
#include "AppContext.h"

void firmwareSetup() {
  Serial.begin(115200);
  analogReadResolution(12);
  analogSetPinAttenuation(BoardConfig::PotentiometerPin, ADC_11db);

  pinMode(BoardConfig::RightEnablePin, OUTPUT);
  pinMode(BoardConfig::LeftEnablePin, OUTPUT);
  digitalWrite(BoardConfig::RightEnablePin, HIGH);
  digitalWrite(BoardConfig::LeftEnablePin, HIGH);

  if (!attachPwmPin(BoardConfig::RightPwmPin)) {
    while (true) {
      delay(1000);
    }
  }
  if (!attachPwmPin(BoardConfig::LeftPwmPin)) {
    while (true) {
      delay(1000);
    }
  }

  WiFi.mode(WIFI_AP_STA);
  WiFi.setSleep(false);
  if (WiFi.softAP(BoardConfig::AccessPointSsid, BoardConfig::AccessPointPassword)) {
    Serial.printf("AP  %s  IP: %s\n",
                  BoardConfig::AccessPointSsid,
                  WiFi.softAPIP().toString().c_str());
  }

  tLastScanKick = 0;
  kickAsyncScanIfDue();
  setupLocalHttpApi();

  applyDuty(BoardConfig::BoostDuty);
  delay(BoardConfig::BoostDurationMs);
  brakeMotor();
  Serial.println("Ready.");
}

void firmwareLoop() {
  serviceLocalHttpApi();
  pollSerialCommand();
  kickAsyncScanIfDue();
  handleScanResultAndMaybeRoam();

  if (WiFi.status() == WL_CONNECTED) {
    hubHello();
    pollHub();
  } else {
    hubAnnounced = false;
  }

  servicePotentiometer();
  if (millis() - tPwmMs >= 10) {
    applyDuty(getEffectiveDuty(targetPercent));
    tPwmMs = millis();
  }

  if (millis() - tTelemetryMs >= 500) {
    char buf[64];
    snprintf(buf, sizeof(buf), "{\"time_s\":%lu,\"duty\":%.1f}", static_cast<unsigned long>(millis() / 1000), targetPercent);
    Serial.println(buf);
    latestTelemetry = String(buf);
    tTelemetryMs = millis();
  }

  // The push has its own timer so two blocking Hub requests do not start back-to-back.
  if (millis() - tHubPushMs >= BoardConfig::HubPushPeriodMs) {
    tHubPushMs = millis();
    pushTelemetryToHub();
  }
}
