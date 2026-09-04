#pragma once

#include <Arduino.h>

namespace JsonUtils {

bool getRaw(const String &json, const char *key, String &value);
bool containsKey(const String &json, const char *key);
bool parseBool(const String &raw, bool &value);
bool parseInt(const String &raw, int32_t &value);
bool parseUInt(const String &raw, uint32_t &value);
bool parseFiniteFloat(const String &raw, float &value);

}  // namespace JsonUtils

