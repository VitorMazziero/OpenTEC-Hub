#include "MotorDriver.h"

#include "../config/BoardConfig.h"

namespace {
constexpr uint32_t ControlPeriodMs = 10;
constexpr uint16_t MaximumDuty = (1U << BoardConfig::PwmResolutionBits) - 1U;
constexpr uint16_t RampStep =
    (MaximumDuty * ControlPeriodMs + BoardConfig::DirectionRampDurationMs - 1U) /
    BoardConfig::DirectionRampDurationMs;

uint16_t appliedDuty = 0;
bool appliedDirectionRight = true;
uint32_t lastControlMs = 0;

void writeBridge(uint16_t duty12, bool directionRight) {
  // Always clear the inactive leg before energising the requested leg.
  ledcWrite(directionRight ? BoardConfig::LeftPwmPin : BoardConfig::RightPwmPin, 0);
  ledcWrite(directionRight ? BoardConfig::RightPwmPin : BoardConfig::LeftPwmPin, duty12);
}

uint16_t moveTowards(uint16_t current, uint16_t target) {
  if (current < target) {
    return static_cast<uint16_t>(min<uint32_t>(current + RampStep, target));
  }
  if (current > target) {
    return (current - target <= RampStep) ? target : current - RampStep;
  }
  return current;
}
}  // namespace

bool attachPwmPin(int pin) {
  if (!ledcAttach(pin, BoardConfig::PwmFrequencyHz, BoardConfig::PwmResolutionBits)) {
    Serial.printf("LEDC attach failed on GPIO %d\n", pin);
    return false;
  }
  ledcWrite(pin, 0);
  return true;
}

void serviceMotor(uint16_t targetDuty12, bool targetDirRight, uint32_t nowMs) {
  if (nowMs - lastControlMs < ControlPeriodMs) return;
  lastControlMs = nowMs;

  digitalWrite(BoardConfig::RightEnablePin, HIGH);
  digitalWrite(BoardConfig::LeftEnablePin, HIGH);

  // Direction changes are non-blocking: ramp fully to zero, switch the active
  // bridge leg on the next control tick, then ramp to the requested duty.
  if (targetDirRight != appliedDirectionRight) {
    if (appliedDuty > 0) {
      appliedDuty = moveTowards(appliedDuty, 0);
      writeBridge(appliedDuty, appliedDirectionRight);
      return;
    }
    appliedDirectionRight = targetDirRight;
  }

  appliedDuty = moveTowards(appliedDuty, targetDuty12);
  writeBridge(appliedDuty, appliedDirectionRight);
}

void brakeMotor() {
  appliedDuty = 0;
  ledcWrite(BoardConfig::RightPwmPin, 0);
  ledcWrite(BoardConfig::LeftPwmPin, 0);
}

bool getAppliedDirectionRight() {
  return appliedDirectionRight;
}

uint16_t getEffectiveDuty(float percent) {
  constexpr float PwmScale = 40.95f;
  constexpr float OffThreshold = 1.0f;
  constexpr float OnThreshold = 1.5f;
  constexpr float MinimumEffectivePercent = 10.0f;
  constexpr float MaximumEffectivePercent = 100.0f;
  static bool motorOff = false;

  if (percent < OffThreshold && !motorOff) {
    motorOff = true;
    return 0;
  }
  if (percent >= OnThreshold && motorOff) {
    motorOff = false;
  }
  if (motorOff) {
    return 0;
  }

  const float effectivePercent = MinimumEffectivePercent +
                                 percent * (MaximumEffectivePercent - MinimumEffectivePercent) / 100.0f;
  return static_cast<uint16_t>(effectivePercent * PwmScale);
}
