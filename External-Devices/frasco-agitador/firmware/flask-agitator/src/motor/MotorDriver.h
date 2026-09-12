#pragma once

#include <Arduino.h>

bool attachPwmPin(int pin);
void applyDuty(uint16_t duty12);
void brakeMotor();
uint16_t getEffectiveDuty(float percent);
