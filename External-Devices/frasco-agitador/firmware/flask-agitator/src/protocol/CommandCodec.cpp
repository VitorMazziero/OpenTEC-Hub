#include "CommandCodec.h"

static const char* findJsonValueStart(const char* json, const char* key) {
  if (!json || !key) return nullptr;
  const size_t klen = strlen(key);
  const char* p = json;
  while ((p = strstr(p, key)) != nullptr) {
    if (p > json && *(p - 1) == '"' && *(p + klen) == '"') {
      const char* afterQuote = p + klen + 1;
      while (*afterQuote && isspace(static_cast<unsigned char>(*afterQuote))) afterQuote++;
      if (*afterQuote == ':') {
        const char* valStart = afterQuote + 1;
        while (*valStart && isspace(static_cast<unsigned char>(*valStart))) valStart++;
        return valStart;
      }
    }
    p += klen;
  }
  return nullptr;
}

static bool extractJsonFloat(const char* payload, const char* key, float& outVal) {
  const char* valStart = findJsonValueStart(payload, key);
  if (!valStart || *valStart == '"') return false;
  char* endPtr = nullptr;
  float val = strtof(valStart, &endPtr);
  if (endPtr == valStart) return false;
  outVal = val;
  return true;
}

static bool extractJsonInt(const char* payload, const char* key, int& outVal) {
  const char* valStart = findJsonValueStart(payload, key);
  if (!valStart || *valStart == '"') return false;
  char* endPtr = nullptr;
  long val = strtol(valStart, &endPtr, 10);
  if (endPtr == valStart) return false;
  outVal = static_cast<int>(val);
  return true;
}

uint32_t extractCmdId(const char* payload) {
  const char* valStart = findJsonValueStart(payload, "cmd_id");
  if (!valStart || *valStart == '"') return 0;
  char* endPtr = nullptr;
  unsigned long val = strtoul(valStart, &endPtr, 10);
  if (endPtr == valStart) return 0;
  return static_cast<uint32_t>(val);
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

bool parseAndApplyJson(const char* payload, Source source) {
  if (!payload) return false;
  const char* firstBrace = strchr(payload, '{');
  const char* lastBrace = strrchr(payload, '}');
  if (!firstBrace || !lastBrace || lastBrace <= firstBrace) {
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
