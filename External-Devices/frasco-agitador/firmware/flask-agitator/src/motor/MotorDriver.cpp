#include "MotorDriver.h"

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"

bool attachPwmPin(int pin) {
  if (!ledcAttach(pin, BoardConfig::PwmFrequencyHz, BoardConfig::PwmResolutionBits)) {
    Serial.printf("LEDC attach failed on GPIO %d\n", pin);
    return false;
  }
  ledcWrite(pin, 0);
  return true;
}

void applyDuty(uint16_t duty12) {
  digitalWrite(BoardConfig::RightEnablePin, HIGH);
  digitalWrite(BoardConfig::LeftEnablePin, HIGH);
  if (dirRight) {
    ledcWrite(BoardConfig::RightPwmPin, duty12);
    ledcWrite(BoardConfig::LeftPwmPin, 0);
  } else {
    ledcWrite(BoardConfig::RightPwmPin, 0);
    ledcWrite(BoardConfig::LeftPwmPin, duty12);
  }
}

void brakeMotor() {
  ledcWrite(BoardConfig::RightPwmPin, 0);
  ledcWrite(BoardConfig::LeftPwmPin, 0);
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
