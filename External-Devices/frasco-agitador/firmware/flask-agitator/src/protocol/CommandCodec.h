#pragma once

#include <Arduino.h>

#include "../core/AppContext.h"

uint32_t extractCmdId(const String& payload);
const char* srcName(Source source);
bool parseAndApplyJson(const String& payload, Source source);
void pollSerialCommand();
