#include "Potentiometer.h"

#include <Arduino.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"

void servicePotentiometer() {
  static float smoothedRaw = 0.0f;
  static uint16_t lastRaw = 0;
  constexpr float Alpha = 0.5f;
  constexpr uint16_t ClipThreshold = 4080;

  if (!potEnabled) {
    return;
  }

  const uint16_t sample = analogRead(BoardConfig::PotentiometerPin);
  if (sample < ClipThreshold) {
    smoothedRaw = Alpha * sample + (1.0f - Alpha) * smoothedRaw;
  }
  const uint16_t raw = static_cast<uint16_t>(smoothedRaw);
  if (abs(static_cast<int>(raw) - static_cast<int>(lastRaw)) <=
      4095 * BoardConfig::PotentiometerDeadbandPercent / 100.0f) {
    return;
  }

  lastRaw = raw;
  targetPercent = raw * 100.0f / 4095.0f;
  lastSource = Source::POT;
  Serial.printf("[Pot ] Cmd: %.1f %%\n", targetPercent);
}
