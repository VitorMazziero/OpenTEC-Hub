#pragma once

#include <Arduino.h>

struct ServoSample {
  float rpm = 0.0f;
  float torquePct = 0.0f;
  float torqueNm = 0.0f;
  float loadPct = 0.0f;
  float powerW = 0.0f;
  float energyWh = 0.0f;
  int32_t state = 0;
  int32_t alarm = 0;
  uint32_t commOk = 0;
  uint32_t commErr = 0;
  bool controlCapable = false;
  uint32_t motorCommandAck = 0;
  int32_t motorRouteAck = -1;
  uint16_t motorAppliedRpm = 0;
  bool motorControlActive = false;
  int32_t motorControlFault = 0;
};

enum class MotorControlRoute : uint8_t {
  UartCn1 = 0,
  Modbus = 1
};

struct ServoSnapshot {
  ServoSample sample;
  bool online = false;
  bool commEnabled = true;
  bool samplePublishable = false;
  uint8_t commandDepth = 0;
  uint32_t motorCommandId = 0;
  uint32_t motorCommandAck = 0;
  MotorControlRoute motorRoute = MotorControlRoute::Modbus;
  int32_t motorRouteAck = -1;
  uint32_t motorCommandDeliveries = 0;
  uint32_t motorCommandAgeMs = 0;
  uint16_t motorRequestedRpm = 0;
  uint16_t motorAppliedRpm = 0;
  uint16_t motorLeaseMs = 0;
  bool motorCommandPending = false;
  bool motorEnable = false;
  bool motorControlCapable = false;
  bool motorControlActive = false;
  int32_t motorControlFault = 0;
};

enum class ServoEnqueueResult : uint8_t {
  Queued,
  Coalesced,
  Full,
  Invalid,
  Disabled
};

class ServoDevice {
 public:
  static constexpr uint8_t kCommandCapacity = 8;
  static constexpr uint16_t kMinPollMs = 250;
  static constexpr uint16_t kMaxPollMs = 10000;
  static constexpr uint32_t kPresenceTimeoutMs = 6000;
  static constexpr uint16_t kMaxMotorRpm = 1000;
  static constexpr uint16_t kMotorLeaseMs = 3000;

  bool begin(bool commEnabled, uint32_t motorSessionSeed, MotorControlRoute motorRoute);
  void setCommEnabled(bool enabled);
  bool commEnabled() const;
  void acceptValidPush(const ServoSample &sample, uint32_t nowMs);
  ServoSnapshot snapshot(uint32_t nowMs) const;
  ServoEnqueueResult enqueue(bool resetEnergy, bool hasPollMs, uint16_t pollMs);
  bool setMotorDesired(uint16_t rpm, bool enable, MotorControlRoute route, uint32_t nowMs);
  String takeCommand(uint32_t nowMs);
  void clearCommands();

 private:
  struct Command {
    bool resetEnergy = false;
    bool hasPollMs = false;
    uint16_t pollMs = 0;
  };

  mutable SemaphoreHandle_t mutex_ = nullptr;
  ServoSample sample_;
  bool commEnabled_ = true;
  bool hasAcceptedSample_ = false;
  uint32_t lastPresenceMs_ = 0;
  uint32_t lastSampleMs_ = 0;
  uint32_t motorCommandId_ = 0;
  uint32_t motorCommandAck_ = 0;
  uint32_t motorCommandDeliveries_ = 0;
  uint32_t motorCommandQueuedAt_ = 0;
  uint16_t motorRequestedRpm_ = 0;
  MotorControlRoute motorRoute_ = MotorControlRoute::Modbus;
  bool motorEnable_ = false;
  bool motorCommandPending_ = false;
  Command commands_[kCommandCapacity];
  uint8_t head_ = 0;
  uint8_t count_ = 0;
};
