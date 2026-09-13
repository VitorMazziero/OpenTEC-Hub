#pragma once

#include <Arduino.h>

bool attachPwmPin(int pin);
void serviceMotor(uint16_t targetDuty12, bool targetDirRight, uint32_t nowMs);
void brakeMotor();
uint16_t getEffectiveDuty(float percent);
