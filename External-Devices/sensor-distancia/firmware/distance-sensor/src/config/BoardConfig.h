#pragma once

#include <Arduino.h>

namespace BoardConfig {
constexpr int I2cSda = 21;
constexpr int I2cScl = 22;
constexpr uint32_t I2cFrequencyHz = 50000;
constexpr uint16_t WireTimeoutMs = 80;
constexpr int SensorXshutPin = 5;

constexpr const char* HubSsidA = "ModuloTECNAL_1";
constexpr const char* HubSsidB = "ModuloTECNAL_2";
constexpr const char* AccessPointSsid = "Distance Sensor";
constexpr const char* HubUrl = "http://192.168.4.1/distance";
constexpr const char* HubHelloUrl = "http://192.168.4.1/nodeHello";
constexpr const char* FirmwareTag = "DistanceClient r11 (AP+STA, Configurable, Non-Blocking)";

constexpr float OffsetMm = 20.0f;
}
