#include "ConfigCodec.h"

#include "../core/AppContext.h"

long getJsonValue(String json, String key) {
  String searchKey = "\"" + key + "\":";
  int keyIndex = json.indexOf(searchKey);
  if (keyIndex == -1) {
    searchKey = "\"" + key + "\" :";
    keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) {
      return -1;
    }
  }

  const int valueIndex = keyIndex + searchKey.length();
  int endIndex = json.indexOf(',', valueIndex);
  if (endIndex == -1) {
    endIndex = json.indexOf('}', valueIndex);
  }
  if (endIndex == -1) {
    return -1;
  }

  String value = json.substring(valueIndex, endIndex);
  value.trim();
  return value.toInt();
}

void processConfigUpdate(String payload) {
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
  String json = "{";
  json += "\"sample_period\":" + String(SAMPLE_PERIOD_MS);
  json += ",\"send_period\":" + String(SEND_PERIOD_MS);
  json += ",\"cooldown_soft\":" + String(COOLDOWN_SOFT_MS);
  json += ",\"cooldown_bus\":" + String(COOLDOWN_BUS_MS);
  json += ",\"cooldown_xshut\":" + String(COOLDOWN_XSHUT_MS);
  json += ",\"l1_reinit\":" + String(L1_SOFT_REINIT);
  json += ",\"l2_clear\":" + String(L2_BUS_CLEAR);
  json += ",\"l3_xshut\":" + String(L3_XSHUT);
  json += "}";
  return json;
}
