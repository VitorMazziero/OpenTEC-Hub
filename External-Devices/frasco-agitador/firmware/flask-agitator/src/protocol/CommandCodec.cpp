#include "CommandCodec.h"

static bool extractJsonFloat(const String& payload, const char* key, float& outVal) {
  String searchKey = "\"" + String(key) + "\"";
  int keyIndex = payload.indexOf(searchKey);
  if (keyIndex < 0) return false;
  int colon = payload.indexOf(':', keyIndex + searchKey.length());
  if (colon < 0) return false;
  int valueIndex = colon + 1;
  while (valueIndex < static_cast<int>(payload.length()) && isspace(payload.charAt(valueIndex))) {
    valueIndex++;
  }
  if (valueIndex >= static_cast<int>(payload.length())) return false;
  char* endPtr = nullptr;
  float val = strtof(payload.c_str() + valueIndex, &endPtr);
  if (endPtr == payload.c_str() + valueIndex) return false;
  outVal = val;
  return true;
}

static bool extractJsonInt(const String& payload, const char* key, int& outVal) {
  String searchKey = "\"" + String(key) + "\"";
  int keyIndex = payload.indexOf(searchKey);
  if (keyIndex < 0) return false;
  int colon = payload.indexOf(':', keyIndex + searchKey.length());
  if (colon < 0) return false;
  int valueIndex = colon + 1;
  while (valueIndex < static_cast<int>(payload.length()) && isspace(payload.charAt(valueIndex))) {
    valueIndex++;
  }
  if (valueIndex >= static_cast<int>(payload.length())) return false;
  char* endPtr = nullptr;
  long val = strtol(payload.c_str() + valueIndex, &endPtr, 10);
  if (endPtr == payload.c_str() + valueIndex) return false;
  outVal = static_cast<int>(val);
  return true;
}

uint32_t extractCmdId(const String& payload) {
  const int key = payload.indexOf("\"cmd_id\"");
  if (key < 0) {
    return 0;
  }
  const int colon = payload.indexOf(':', key + 8);
  if (colon < 0) {
    return 0;
  }
  const char* p = payload.c_str() + colon + 1;
  while (*p && isspace(*p)) p++;
  return static_cast<uint32_t>(strtoul(p, nullptr, 10));
}

const char* srcName(Source source) {
  switch (source) {
    case Source::WIFI:
      return "Wi-Fi";
    case Source::USB:
      return "USB";
    case Source::HUB:
      return "Hub";
    default:
      return "Pot";
  }
}

bool parseAndApplyJson(const String& payload, Source source) {
  int firstBrace = payload.indexOf('{');
  int lastBrace = payload.lastIndexOf('}');
  if (firstBrace < 0 || lastBrace <= firstBrace) {
    return false;
  }

  bool valid = false;
  float percent = 0.0f;
  if (extractJsonFloat(payload, "RPM_percent", percent)) {
    if (percent >= 0.0f && percent <= 100.0f) {
      targetPercent = percent;
      valid = true;
    } else {
      Serial.printf("[%s] RPM_percent out of range\n", srcName(source));
    }
  }

  int direction = 0;
  if (extractJsonInt(payload, "Dir", direction)) {
    if (direction == 0 || direction == 1) {
      dirRight = (direction == 1);
      valid = true;
    } else {
      Serial.printf("[%s] Dir must be 0 or 1\n", srcName(source));
    }
  }

  int activePot = 0;
  if (extractJsonInt(payload, "ActivePot", activePot)) {
    if (activePot == 0 || activePot == 1) {
      potEnabled = (activePot == 1);
      valid = true;
      Serial.printf("[%s] Potentiometer %s\n", srcName(source), potEnabled ? "enabled" : "disabled");
    } else {
      Serial.printf("[%s] ActivePot must be 0 or 1\n", srcName(source));
    }
  }

  if (valid) {
    lastSource = source;
    Serial.printf("[%s] Cmd: %.1f %%  Dir:%s  Pot:%s\n",
                  srcName(source),
                  targetPercent,
                  dirRight ? "Right" : "Left",
                  potEnabled ? "ON" : "OFF");
  }
  return valid;
}

void pollSerialCommand() {
  static String buffer;
  while (Serial.available()) {
    const char character = static_cast<char>(Serial.read());
    if (character == '\n' || character == '\r') {
      if (buffer.length()) {
        parseAndApplyJson(buffer, Source::USB);
      }
      buffer.clear();
    } else if (buffer.length() < 180) {
      buffer += character;
    }
  }
}
