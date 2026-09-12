#include "ConfigCodec.h"

#include "../core/AppContext.h"

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

void processConfigUpdate(const char* payload) {
  if (!payload) return;
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

  if (!updated) {
    Serial.println("Failed to parse any valid keys from payload.");
  }
}

String getConfigAsJson() {
  char buf[256];
  snprintf(buf, sizeof(buf),
           "{\"sample_period\":%lu,\"send_period\":%lu,\"cooldown_soft\":%lu,"
           "\"cooldown_bus\":%lu,\"cooldown_xshut\":%lu,\"l1_reinit\":%d,"
           "\"l2_clear\":%d,\"l3_xshut\":%d}",
           SAMPLE_PERIOD_MS,
           SEND_PERIOD_MS,
           COOLDOWN_SOFT_MS,
           COOLDOWN_BUS_MS,
           COOLDOWN_XSHUT_MS,
           L1_SOFT_REINIT,
           L2_BUS_CLEAR,
           L3_XSHUT);
  return String(buf);
}
