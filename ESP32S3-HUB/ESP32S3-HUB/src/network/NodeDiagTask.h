#pragma once

// Background proxy for the five external nodes' local GET /diag endpoints.
// This header is included into the Hub's deliberate single translation unit.

static constexpr unsigned long NODE_DIAG_PERIOD_MS = 30000;
static constexpr unsigned long NODE_DIAG_INTER_NODE_MS = 200;
static constexpr unsigned long NODE_DIAG_NEVER_AGE_MS = 999999;

inline int nodeDiagDeviceIndex(const String& name) {
  for (int i = 0; i < DEV_COUNT; ++i) {
    if (name == g_deviceRegistry[i].name) return i;
  }
  return -1;
}

inline String buildNodeDiagEntry(int index, unsigned long now) {
  if (index < 0 || index >= DEV_COUNT) return "{}";

  NodeDiagCache cached = {};
  if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
    cached = g_nodeDiagCache[index];
    xSemaphoreGive(stateMutex);
  }

  const unsigned long age = cached.fetchedMs > 0 && now >= cached.fetchedMs
      ? now - cached.fetchedMs
      : NODE_DIAG_NEVER_AGE_MS;
  String entry;
  entry.reserve(700);
  entry += "{\"dev\":\"";
  entry += g_deviceRegistry[index].name;
  entry += "\",\"code\":" + String(cached.code);
  entry += ",\"age_ms\":" + String(age);
  entry += ",\"diag\":";
  const size_t bodyLen = strnlen(cached.body, sizeof(cached.body));
  if (bodyLen >= 2 && cached.body[0] == '{' && cached.body[bodyLen - 1] == '}') {
    entry += cached.body;
  } else {
    entry += "null";
  }
  entry += "}";
  return entry;
}

inline String buildNodeDiagDocument(const String& only = "") {
  String response;
  response.reserve(3072);
  const unsigned long now = millis();
  response += "{\"hub_time_ms\":" + String(now) + ",\"nodes\":[";
  bool first = true;
  for (int i = 0; i < DEV_COUNT; ++i) {
    if (only.length() > 0 && only != g_deviceRegistry[i].name) continue;
    if (!first) response += ',';
    response += buildNodeDiagEntry(i, now);
    first = false;
  }
  response += "]}";
  return response;
}

inline void printNodeDiagResponse(const String& requested) {
  const unsigned long now = millis();
  if (requested == "all") {
    for (int i = 0; i < DEV_COUNT; ++i) {
      Serial.println(String("{\"NodeDiag\":") + buildNodeDiagEntry(i, now) + "}");
    }
    return;
  }

  const int index = nodeDiagDeviceIndex(requested);
  if (index >= 0) {
    Serial.println(String("{\"NodeDiag\":") + buildNodeDiagEntry(index, now) + "}");
  } else {
    Serial.println(String("{\"NodeDiag\":{\"dev\":\"") + requested +
                   "\",\"code\":404,\"age_ms\":999999,\"diag\":null}}");
  }
}

inline void nodeDiagTask(void*) {
  // Let /nodeHello traffic populate the registry before the first sweep.
  vTaskDelay(pdMS_TO_TICKS(2000));
  for (;;) {
    for (int i = 0; i < DEV_COUNT; ++i) {
      IPAddress ip(0, 0, 0, 0);
      bool registered = false;
      if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
        registered = g_deviceRegistry[i].registered;
        ip = g_deviceRegistry[i].ip;
        xSemaphoreGive(stateMutex);
      }

      if (!registered || ip == IPAddress(0, 0, 0, 0)) continue;

      HTTPClient http;
      http.setTimeout(500);
      http.begin(String("http://") + ip.toString() + "/diag");
      const int code = http.GET();
      String body = code == 200 ? http.getString() : "";
      http.end();

      if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
        NodeDiagCache& cache = g_nodeDiagCache[i];
        cache.code = code;
        cache.fetchedMs = millis();
        const size_t copyLen = body.length() < sizeof(cache.body) - 1
            ? body.length()
            : sizeof(cache.body) - 1;
        memcpy(cache.body, body.c_str(), copyLen);
        cache.body[copyLen] = '\0';
        xSemaphoreGive(stateMutex);
      }
      vTaskDelay(pdMS_TO_TICKS(NODE_DIAG_INTER_NODE_MS));
    }
    vTaskDelay(pdMS_TO_TICKS(NODE_DIAG_PERIOD_MS));
  }
}

inline void startNodeDiagTask() {
  const uint32_t before = ESP.getFreeHeap();
  const BaseType_t created = xTaskCreatePinnedToCore(
      nodeDiagTask, "NodeDiag", 6144, nullptr, 1, &g_nodeDiagTaskHandle, 1);
  if (created != pdPASS) {
    g_nodeDiagTaskHandle = nullptr;
    ESP32_ERRO("Falha ao criar tarefa NodeDiag");
    return;
  }
  ESP32_INFO(String("NodeDiag task criada; heap antes=") + before +
             String(" depois=") + ESP.getFreeHeap());
}
