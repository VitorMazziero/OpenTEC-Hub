#pragma once

#include <Arduino.h>

#include "../core/AppContext.h"

uint32_t extractCmdId(const char* payload);
inline uint32_t extractCmdId(const String& payload) { return extractCmdId(payload.c_str()); }
const char* srcName(Source source);
bool parseAndApplyJson(const char* payload, Source source);
inline bool parseAndApplyJson(const String& payload, Source source) { return parseAndApplyJson(payload.c_str(), source); }
void pollSerialCommand();
