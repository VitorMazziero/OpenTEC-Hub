#include "HubLink.h"

#include <WiFi.h>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include <freertos/task.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"
#include "../display/DisplayReader.h"
#include "../protocol/ConfigCodec.h"
#include "../setpoint/SetpointGuard.h"
#include "../setpoint/SetpointManager.h"
#include "NetworkManager.h"

namespace {
constexpr unsigned long HELLO_PERIOD_MS = 30000;
constexpr unsigned long MAX_HUB_BACKOFF_MS = 15000;
constexpr uint32_t HUB_TASK_STACK_BYTES = 6144;
constexpr size_t COMMAND_PAYLOAD_BYTES = 384;

struct HubSnapshot {
  bool hubEnabled;
  bool otaInProgress;
  uint32_t sendPeriodMs;
  unsigned long nowMs;
  float sp;
  bool known;
  float target;
  char state[12];
  char phase[16];
  char error[48];
  float pv;
  bool pvOk;
  float displaySp;
  bool displaySpOk;
  uint8_t spSource;
  uint8_t mode;
  char guard[16];
  float deviation;
  bool deviationOk;
  uint32_t ackCmdId;
};

struct HubCommandMessage {
  char payload[COMMAND_PAYLOAD_BYTES];
};

portMUX_TYPE g_snapshotMux = portMUX_INITIALIZER_UNLOCKED;
HubSnapshot g_snapshot{};
volatile bool g_snapshotReady = false;
QueueHandle_t g_commandQueue = nullptr;
TaskHandle_t g_hubTask = nullptr;
volatile uint32_t g_minFreeStackBytes = 0;

HubSnapshot copySnapshot() {
  HubSnapshot copy{};
  portENTER_CRITICAL(&g_snapshotMux);
  copy = g_snapshot;
  portEXIT_CRITICAL(&g_snapshotMux);
  return copy;
}

void sendHubHello() {
  char url[180];
  snprintf(url, sizeof(url), "%s?dev=%s&ver=%s&mac=%s",
           BoardConfig::HubHelloUrl, BoardConfig::DeviceKey,
           BoardConfig::FirmwareVersion, WiFi.macAddress().c_str());
  int code = 0;
  String body;
  if (httpGet(url, code, body)) {
    g_hubAnnounced = true;
    Serial.printf("[HubLink] Hello registrado (%d)\n", code);
  } else {
    g_hubAnnounced = false;
    Serial.printf("[HubLink] Hello falhou (%d)\n", code);
  }
}

void enqueueCommand(const String& body) {
  if (!g_commandQueue || body.length() < 2 || body[0] != '{') return;
  if (body.length() >= COMMAND_PAYLOAD_BYTES) {
    Serial.printf("[HubLink] Comando descartado: %u bytes excedem o limite.\n",
                  static_cast<unsigned>(body.length()));
    return;
  }
  HubCommandMessage message{};
  snprintf(message.payload, sizeof(message.payload), "%s", body.c_str());
  if (xQueueSend(g_commandQueue, &message, 0) != pdTRUE) {
    // Sem ACK novo, o Hub conserva a revisão e a reentrega no próximo push.
    Serial.println("[HubLink] Fila de comandos cheia; aguardando reentrega confiável.");
  }
}

bool pushToHub(const HubSnapshot& s, unsigned long scheduleNow) {
  static unsigned long lastSendMs = 0;
  unsigned long interval = s.sendPeriodMs;
  if (g_hubFailStreak > 0) {
    const uint8_t shift = g_hubFailStreak > 4 ? 4 : g_hubFailStreak;
    interval = min(s.sendPeriodMs * (1UL << shift), MAX_HUB_BACKOFF_MS);
  }
  if (scheduleNow - lastSendMs < interval) return false;
  lastSendMs = scheduleNow;

  char url[640];
  snprintf(url, sizeof(url),
           "%s?sp=%.2f&known=%d&target=%.2f&state=%s&phase=%s&err=%s"
           "&pv=%.2f&pv_ok=%d&display_sp=%.2f&display_sp_ok=%d&sp_source=%u"
           "&mode=%u&guard=%s&dev=%.2f&dev_ok=%d&time=%.1f&ack_cmd_id=%lu",
           BoardConfig::HubUrl, s.sp, s.known ? 1 : 0, s.target, s.state, s.phase, s.error,
           s.pvOk ? s.pv : -1.0f, s.pvOk ? 1 : 0,
           s.displaySpOk ? s.displaySp : -1.0f, s.displaySpOk ? 1 : 0, s.spSource,
           s.mode, s.guard, s.deviationOk ? s.deviation : 0.0f, s.deviationOk ? 1 : 0,
           s.nowMs / 1000.0f, static_cast<unsigned long>(s.ackCmdId));

  int code = 0;
  String body;
  if (httpGet(url, code, body)) {
    g_hubFailStreak = 0;
    if (code == 200) enqueueCommand(body);
    return true;
  }

  if (g_hubFailStreak < 255) g_hubFailStreak++;
  Serial.printf("[HubLink] HTTP error: %d (streak=%u)\n", code, g_hubFailStreak);
  return true;
}

void hubTask(void*) {
  unsigned long lastHelloMs = 0;
  unsigned long lastStackLogMs = 0;

  for (;;) {
    if (!g_snapshotReady) {
      vTaskDelay(pdMS_TO_TICKS(20));
      continue;
    }

    const HubSnapshot s = copySnapshot();
    if (!s.hubEnabled || s.otaInProgress) {
      checkWifi(false);
      vTaskDelay(pdMS_TO_TICKS(100));
      continue;
    }

    checkWifi(true);
    const unsigned long now = millis();
    if (WiFi.status() == WL_CONNECTED) {
      if (!g_hubAnnounced || now - lastHelloMs >= HELLO_PERIOD_MS) {
        lastHelloMs = now;
        sendHubHello();
      }
      pushToHub(s, now);
    }

    if (now - lastStackLogMs >= 30000) {
      lastStackLogMs = now;
      const uint32_t freeBytes = static_cast<uint32_t>(uxTaskGetStackHighWaterMark(nullptr));
      g_minFreeStackBytes = freeBytes;
      Serial.printf("[HubLink] Pilha livre mínima: %lu bytes\n",
                    static_cast<unsigned long>(freeBytes));
    }
    vTaskDelay(pdMS_TO_TICKS(20));
  }
}
}  // namespace

void hubLinkInit() {
  if (!g_commandQueue) g_commandQueue = xQueueCreate(2, sizeof(HubCommandMessage));
  if (!g_commandQueue) {
    Serial.println("[HubLink] Falha ao criar fila de comandos.");
    return;
  }
  if (!g_hubTask) {
    const BaseType_t created = xTaskCreatePinnedToCore(
        hubTask, "BathHubLink", HUB_TASK_STACK_BYTES, nullptr, 1, &g_hubTask, 0);
    if (created != pdPASS) {
      g_hubTask = nullptr;
      Serial.println("[HubLink] Falha ao criar tarefa de rede.");
    }
  }
}

void hubLinkPublishSnapshot(unsigned long now) {
  HubSnapshot next{};
  next.hubEnabled = g_cfg.hubEnabled != 0;
  next.otaInProgress = g_otaInProgress;
  next.sendPeriodMs = g_cfg.sendPeriodMs;
  next.nowMs = now;
  next.sp = g_spShadow;
  next.known = g_spKnown;
  next.target = g_spTarget;
  snprintf(next.state, sizeof(next.state), "%s", setpointStateName());
  snprintf(next.phase, sizeof(next.phase), "%s", setpointPhaseName());
  snprintf(next.error, sizeof(next.error), "%s", setpointLastError().c_str());
  next.pvOk = displayPvValid();
  next.pv = next.pvOk ? displayPv() : -1.0f;
  next.displaySpOk = displaySpValid();
  next.displaySp = next.displaySpOk ? displaySp() : -1.0f;
  next.spSource = g_cfg.spSource;
  next.mode = g_mode;
  snprintf(next.guard, sizeof(next.guard), "%s", guardStateName());
  next.deviationOk = guardDeviation(next.deviation);
  next.ackCmdId = g_lastCmdId;

  portENTER_CRITICAL(&g_snapshotMux);
  g_snapshot = next;
  g_snapshotReady = true;
  portEXIT_CRITICAL(&g_snapshotMux);
}

void hubLinkServiceMainLoop() {
  if (!g_commandQueue || g_otaInProgress) return;
  HubCommandMessage message{};
  if (xQueueReceive(g_commandQueue, &message, 0) != pdTRUE) return;

  String reply;
  processCommand(message.payload, reply);
  Serial.printf("[HubLink] Comando por carona: %s -> %s\n", message.payload, reply.c_str());
}

uint32_t hubLinkMinFreeStackBytes() {
  return g_minFreeStackBytes;
}
