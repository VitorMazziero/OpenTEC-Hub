#pragma once

#include <Arduino.h>

const char* findJsonValueStart(const char* json, const char* key);
long getJsonValue(const char* json, const char* key);
inline long getJsonValue(const String& json, const char* key) {
  return getJsonValue(json.c_str(), key);
}
bool getJsonFloat(const char* json, const char* key, float& outVal);
inline bool getJsonFloat(const String& json, const char* key, float& outVal) {
  return getJsonFloat(json.c_str(), key, outVal);
}
void processConfigUpdate(const char* payload);
inline void processConfigUpdate(const String& payload) {
  processConfigUpdate(payload.c_str());
}
String getConfigAsJson();
