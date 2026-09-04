#include "ServoDevice.h"

bool ServoDevice::begin(bool commEnabled, uint32_t motorSessionSeed,
                        MotorControlRoute motorRoute) {
  if (mutex_ == nullptr) mutex_ = xSemaphoreCreateMutex();
  commEnabled_ = commEnabled;
  motorCommandId_ = motorSessionSeed == 0 ? 1 : motorSessionSeed;
  motorCommandAck_ = 0;
  motorCommandDeliveries_ = 0;
  motorCommandQueuedAt_ = millis();
  motorRequestedRpm_ = 0;
  motorRoute_ = motorRoute;
  motorEnable_ = false;
  motorCommandPending_ = true;
  return mutex_ != nullptr;
}

void ServoDevice::setCommEnabled(bool enabled) {
  if (mutex_ != nullptr && xSemaphoreTake(mutex_, portMAX_DELAY) == pdTRUE) {
    if (commEnabled_ == enabled) {
      xSemaphoreGive(mutex_);
      return;
    }
    commEnabled_ = enabled;
    if (!enabled && motorRoute_ == MotorControlRoute::Modbus) {
      hasAcceptedSample_ = false;
      lastSampleMs_ = 0;
      motorRequestedRpm_ = 0;
      motorEnable_ = false;
      ++motorCommandId_;
      if (motorCommandId_ == 0) motorCommandId_ = 1;
      motorCommandDeliveries_ = 0;
      motorCommandQueuedAt_ = millis();
      motorCommandPending_ = true;
    }
    xSemaphoreGive(mutex_);
  }
}

bool ServoDevice::commEnabled() const {
  bool value = false;
  if (mutex_ != nullptr && xSemaphoreTake(mutex_, portMAX_DELAY) == pdTRUE) {
    value = commEnabled_;
    xSemaphoreGive(mutex_);
  }
  return value;
}

void ServoDevice::acceptValidPush(const ServoSample &sample, uint32_t nowMs) {
  if (mutex_ != nullptr && xSemaphoreTake(mutex_, portMAX_DELAY) == pdTRUE) {
    lastPresenceMs_ = nowMs;
    // Control status remains observable even when measurement routing is disabled.
    // This is what lets the PC confirm that a stop was actually applied.
    sample_.controlCapable = sample.controlCapable;
    sample_.motorCommandAck = sample.motorCommandAck;
    sample_.motorRouteAck = sample.motorRouteAck;
    sample_.motorAppliedRpm = sample.motorAppliedRpm;
    sample_.motorControlActive = sample.motorControlActive;
    sample_.motorControlFault = sample.motorControlFault;
    motorCommandAck_ = sample.motorCommandAck;
    if (motorCommandAck_ == motorCommandId_ &&
        sample.motorRouteAck == static_cast<int32_t>(motorRoute_)) {
      motorCommandPending_ = false;
    }
    if (commEnabled_) {
      sample_ = sample;
      hasAcceptedSample_ = true;
      lastSampleMs_ = nowMs;
    }
    xSemaphoreGive(mutex_);
  }
}

ServoSnapshot ServoDevice::snapshot(uint32_t nowMs) const {
  ServoSnapshot result;
  if (mutex_ != nullptr && xSemaphoreTake(mutex_, portMAX_DELAY) == pdTRUE) {
    result.sample = sample_;
    result.commEnabled = commEnabled_;
    result.online = lastPresenceMs_ != 0 && (nowMs - lastPresenceMs_ <= kPresenceTimeoutMs);
    result.samplePublishable = result.online && commEnabled_ && hasAcceptedSample_ &&
                               lastSampleMs_ != 0 &&
                               (nowMs - lastSampleMs_ <= kPresenceTimeoutMs);
    result.commandDepth = count_;
    result.motorCommandId = motorCommandId_;
    result.motorCommandAck = motorCommandAck_;
    result.motorRoute = motorRoute_;
    result.motorRouteAck = sample_.motorRouteAck;
    result.motorCommandDeliveries = motorCommandDeliveries_;
    result.motorCommandAgeMs = motorCommandPending_ ? nowMs - motorCommandQueuedAt_ : 0;
    result.motorRequestedRpm = motorRequestedRpm_;
    result.motorAppliedRpm = sample_.motorAppliedRpm;
    result.motorLeaseMs = kMotorLeaseMs;
    result.motorCommandPending = motorCommandPending_;
    result.motorEnable = motorEnable_ && commEnabled_;
    result.motorControlCapable = sample_.controlCapable;
    result.motorControlActive = sample_.motorControlActive;
    result.motorControlFault = sample_.motorControlFault;
    xSemaphoreGive(mutex_);
  }
  return result;
}

ServoEnqueueResult ServoDevice::enqueue(bool resetEnergy, bool hasPollMs, uint16_t pollMs) {
  if (!resetEnergy && !hasPollMs) return ServoEnqueueResult::Invalid;
  if (hasPollMs && (pollMs < kMinPollMs || pollMs > kMaxPollMs)) return ServoEnqueueResult::Invalid;
  if (mutex_ == nullptr || xSemaphoreTake(mutex_, portMAX_DELAY) != pdTRUE) return ServoEnqueueResult::Full;
  if (!commEnabled_) {
    xSemaphoreGive(mutex_);
    return ServoEnqueueResult::Disabled;
  }

  if (!resetEnergy && hasPollMs) {
    for (uint8_t offset = 0; offset < count_; ++offset) {
      const uint8_t reverse = count_ - 1 - offset;
      const uint8_t index = (head_ + reverse) % kCommandCapacity;
      if (!commands_[index].resetEnergy && commands_[index].hasPollMs) {
        commands_[index].pollMs = pollMs;
        xSemaphoreGive(mutex_);
        return ServoEnqueueResult::Coalesced;
      }
    }
  }

  if (count_ == kCommandCapacity) {
    xSemaphoreGive(mutex_);
    return ServoEnqueueResult::Full;
  }
  const uint8_t tail = (head_ + count_) % kCommandCapacity;
  commands_[tail] = {resetEnergy, hasPollMs, pollMs};
  ++count_;
  xSemaphoreGive(mutex_);
  return ServoEnqueueResult::Queued;
}

bool ServoDevice::setMotorDesired(uint16_t rpm, bool enable,
                                  MotorControlRoute route, uint32_t nowMs) {
  if (rpm > kMaxMotorRpm || (enable && rpm == 0)) return false;
  if (mutex_ == nullptr || xSemaphoreTake(mutex_, portMAX_DELAY) != pdTRUE) return false;
  if (!commEnabled_ && enable && route == MotorControlRoute::Modbus) {
    xSemaphoreGive(mutex_);
    return false;
  }

  const uint16_t normalizedRpm = enable ? rpm : 0;
  if (motorRequestedRpm_ != normalizedRpm || motorEnable_ != enable ||
      motorRoute_ != route) {
    motorRequestedRpm_ = normalizedRpm;
    motorEnable_ = enable;
    motorRoute_ = route;
    ++motorCommandId_;
    if (motorCommandId_ == 0) motorCommandId_ = 1;
    motorCommandDeliveries_ = 0;
    motorCommandQueuedAt_ = nowMs;
    motorCommandPending_ = true;
  }
  xSemaphoreGive(mutex_);
  return true;
}

String ServoDevice::takeCommand(uint32_t nowMs) {
  if (mutex_ == nullptr || xSemaphoreTake(mutex_, portMAX_DELAY) != pdTRUE) return "{}";
  Command command;
  bool hasEvent = false;
  if (count_ > 0) {
    command = commands_[head_];
    commands_[head_] = {};
    head_ = (head_ + 1) % kCommandCapacity;
    --count_;
    hasEvent = true;
  }
  const uint32_t motorCommandId = motorCommandId_;
  const bool motorEnable = commEnabled_ && motorEnable_ &&
                           motorRoute_ == MotorControlRoute::Modbus;
  const uint16_t motorRpm = motorEnable ? motorRequestedRpm_ : 0;
  const MotorControlRoute motorRoute = motorRoute_;
  ++motorCommandDeliveries_;
  if (motorCommandPending_ && motorCommandQueuedAt_ == 0) motorCommandQueuedAt_ = nowMs;
  xSemaphoreGive(mutex_);

  String json = "{";
  json += "\"motor_cmd_id\":" + String(motorCommandId);
  json += ",\"motor_rpm\":" + String(motorRpm);
  json += ",\"motor_enable\":" + String(motorEnable ? 1 : 0);
  json += ",\"motor_route\":" + String(static_cast<uint8_t>(motorRoute));
  json += ",\"motor_lease_ms\":" + String(kMotorLeaseMs);
  if (hasEvent && command.resetEnergy) {
    json += ',';
    json += "\"reset_energy\":1";
  }
  if (hasEvent && command.hasPollMs) {
    json += ',';
    json += "\"poll_ms\":" + String(command.pollMs);
  }
  json += '}';
  return json;
}

void ServoDevice::clearCommands() {
  if (mutex_ != nullptr && xSemaphoreTake(mutex_, portMAX_DELAY) == pdTRUE) {
    for (uint8_t i = 0; i < kCommandCapacity; ++i) commands_[i] = {};
    head_ = 0;
    count_ = 0;
    xSemaphoreGive(mutex_);
  }
}
