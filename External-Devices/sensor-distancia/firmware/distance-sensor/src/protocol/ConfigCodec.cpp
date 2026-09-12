#include "ConfigCodec.h"

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"
#include "../storage/NvsConfig.h"

const char* findJsonValueStart(const char* json, const char* key) {
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

long getJsonValue(const char* json, const char* key) {
  const char* val = findJsonValueStart(json, key);
  if (!val || *val == '"') return -1;
  char* endPtr = nullptr;
  long result = strtol(val, &endPtr, 10);
  if (endPtr == val) return -1;
  return result;
}

bool getJsonFloat(const char* json, const char* key, float& outVal) {
  const char* val = findJsonValueStart(json, key);
  if (!val || *val == '"') return false;
  char* endPtr = nullptr;
  float result = strtof(val, &endPtr);
  if (endPtr == val) return false;
  outVal = result;
  return true;
}

void processConfigUpdate(const char* payload) {
  if (!payload) return;

  if (getJsonValue(payload, "reset_nvs") == 1) {
    resetNvsConfig();
    SAMPLE_PERIOD_MS = 1000;
    SEND_PERIOD_MS = 1000;
    COOLDOWN_SOFT_MS = 15000;
    COOLDOWN_BUS_MS = 15000;
    COOLDOWN_XSHUT_MS = 30000;
    L1_SOFT_REINIT = 5;
    L2_BUS_CLEAR = 10;
    L3_XSHUT = 20;
    g_offsetMm = BoardConfig::OffsetMm;
    Serial.println("[CMD] Reset NVS e restaurou parametros padroes.");
    return;
  }

  bool updated = false;
  long value = getJsonValue(payload, "sample_period");
  if (value > 0) {
    SAMPLE_PERIOD_MS = value;
    updated = true;
    Serial.printf("Set SAMPLE_PERIOD_MS = %lu\n", value);
  }

  value = getJsonValue(payload, "send_period");
  if (value > 0) {
    SEND_PERIOD_MS = value;
    updated = true;
    Serial.printf("Set SEND_PERIOD_MS = %lu\n", value);
  }

  value = getJsonValue(payload, "cooldown_soft");
  if (value > 0) {
    COOLDOWN_SOFT_MS = value;
    updated = true;
    Serial.printf("Set COOLDOWN_SOFT_MS = %lu\n", value);
  }

  value = getJsonValue(payload, "cooldown_bus");
  if (value > 0) {
    COOLDOWN_BUS_MS = value;
    updated = true;
    Serial.printf("Set COOLDOWN_BUS_MS = %lu\n", value);
  }

  value = getJsonValue(payload, "cooldown_xshut");
  if (value > 0) {
    COOLDOWN_XSHUT_MS = value;
    updated = true;
    Serial.printf("Set COOLDOWN_XSHUT_MS = %lu\n", value);
  }

  value = getJsonValue(payload, "l1_reinit");
  if (value > 0) {
    L1_SOFT_REINIT = value;
    updated = true;
    Serial.printf("Set L1_SOFT_REINIT = %d\n", static_cast<int>(value));
  }

  value = getJsonValue(payload, "l2_clear");
  if (value > 0) {
    L2_BUS_CLEAR = value;
    updated = true;
    Serial.printf("Set L2_BUS_CLEAR = %d\n", static_cast<int>(value));
  }

  value = getJsonValue(payload, "l3_xshut");
  if (value > 0) {
    L3_XSHUT = value;
    updated = true;
    Serial.printf("Set L3_XSHUT = %d\n", static_cast<int>(value));
  }

  float offsetVal = 0.0f;
  if (getJsonFloat(payload, "offset_mm", offsetVal)) {
    if (offsetVal >= 0.0f) {
      g_offsetMm = offsetVal;
      updated = true;
      Serial.printf("Set g_offsetMm = %.2f\n", offsetVal);
    }
  }

  if (updated) {
    saveNvsConfig();
  } else {
    Serial.println("Failed to parse any valid keys from payload.");
  }
}

String getConfigAsJson() {
  char buf[320];
  snprintf(buf, sizeof(buf),
           "{\"sample_period\":%lu,\"send_period\":%lu,\"cooldown_soft\":%lu,"
           "\"cooldown_bus\":%lu,\"cooldown_xshut\":%lu,\"l1_reinit\":%d,"
           "\"l2_clear\":%d,\"l3_xshut\":%d,\"offset_mm\":%.2f}",
           SAMPLE_PERIOD_MS,
           SEND_PERIOD_MS,
           COOLDOWN_SOFT_MS,
           COOLDOWN_BUS_MS,
           COOLDOWN_XSHUT_MS,
           L1_SOFT_REINIT,
           L2_BUS_CLEAR,
           L3_XSHUT,
           g_offsetMm);
  return String(buf);
}

