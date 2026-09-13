#pragma once

#include <Arduino.h>

namespace BoardConfig {
constexpr const char* FirmwareVersion = "v10";
constexpr const char* AccessPointSsid = "MotorBoeco";
constexpr const char* AccessPointPassword = "MotorBoeco";
constexpr const char* HubSsids[2] = {"ModuloTECNAL_1", "ModuloTECNAL_2"};
constexpr const char* HubPasswords[2] = {"ModuloTECNAL_1", "ModuloTECNAL_2"};

constexpr int RightPwmPin = 25;
constexpr int LeftPwmPin = 26;
constexpr int RightEnablePin = 27;
constexpr int LeftEnablePin = 13;
constexpr int PotentiometerPin = 36;

constexpr uint32_t PwmFrequencyHz = 15000;
constexpr uint8_t PwmResolutionBits = 12;
constexpr float PotentiometerDeadbandPercent = 2.0f;
constexpr uint16_t DirectionRampDurationMs = 200;

constexpr uint32_t ScanIntervalMs = 10000;
constexpr int SwitchDeltaDb = 10;
constexpr uint32_t ScanChannelMs = 80;
constexpr uint32_t HubPushPeriodMs = 1000;
}
