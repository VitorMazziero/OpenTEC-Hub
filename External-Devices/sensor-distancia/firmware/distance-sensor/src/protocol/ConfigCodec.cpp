#include "ConfigCodec.h"

#include <climits>

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

namespace {
// Faixas alinhadas com a validacao do Hub (Commands.h) e com PROTOCOL.md §4.2. O Hub ja
// filtra o que vem por carona; estas checagens cobrem POST /config local e a serial, que
// nao passam por ele, e garantem que os tres caminhos aceitem exatamente o mesmo conjunto.
constexpr long  PERIOD_MIN_MS = 100;
constexpr long  PERIOD_MAX_MS = 60000;
constexpr float OFFSET_MIN_MM = -50.0f;
constexpr float OFFSET_MAX_MM = 200.0f;

// Aplica `value` em `target` so se estiver na faixa e for diferente do atual. Devolve true
// quando houve mudanca real; `seen` marca que a chave existia e era valida, para separar
// "nada para fazer" de "nada reconhecido" no log.
bool applyUlong(const char* payload, const char* key, unsigned long& target,
                long minVal, long maxVal, bool& seen) {
  const long value = getJsonValue(payload, key);
  if (value < minVal || value > maxVal) return false;
  seen = true;
  if (static_cast<unsigned long>(value) == target) return false;
  target = static_cast<unsigned long>(value);
  Serial.printf("Set %s = %lu\n", key, target);
  return true;
}

bool applyInt(const char* payload, const char* key, int& target,
              int minVal, int maxVal, bool& seen) {
  const long value = getJsonValue(payload, key);
  if (value < minVal || value > maxVal) return false;
  seen = true;
  if (static_cast<int>(value) == target) return false;
  target = static_cast<int>(value);
  Serial.printf("Set %s = %d\n", key, target);
  return true;
}
}  // namespace

bool processConfigUpdate(const char* payload) {
  if (!payload) return false;

  // Entrega duplicada da mesma revisao (o Hub reentrega ate ver o ack no push seguinte;
  // apos reboot g_lastCmdId volta a 0 e a reentrega e aplicada de novo, o que e correto).
  // Reaplicar seria inofensivo para os parametros, mas nao para reset_nvs, e cada
  // reaplicacao custaria uma passagem pela NVS.
  long cmdId = getJsonValue(payload, "cmd_id");
  if (cmdId > 0 && static_cast<uint32_t>(cmdId) == g_lastCmdId) {
    Serial.printf("[CMD] cmd_id=%ld ja aplicado; ignorando reentrega.\n", cmdId);
    return true;
  }

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
    if (cmdId > 0) {
      g_lastCmdId = static_cast<uint32_t>(cmdId);
    }
    Serial.println("[CMD] Reset NVS e restaurou parametros padroes.");
    return true;
  }

  bool seen = false;
  bool changed = false;
  changed |= applyUlong(payload, "sample_period", SAMPLE_PERIOD_MS, PERIOD_MIN_MS, PERIOD_MAX_MS, seen);
  changed |= applyUlong(payload, "send_period",   SEND_PERIOD_MS,   PERIOD_MIN_MS, PERIOD_MAX_MS, seen);
  changed |= applyUlong(payload, "cooldown_soft",  COOLDOWN_SOFT_MS,  1, LONG_MAX, seen);
  changed |= applyUlong(payload, "cooldown_bus",   COOLDOWN_BUS_MS,   1, LONG_MAX, seen);
  changed |= applyUlong(payload, "cooldown_xshut", COOLDOWN_XSHUT_MS, 1, LONG_MAX, seen);
  changed |= applyInt(payload, "l1_reinit", L1_SOFT_REINIT, 1, 50,  seen);
  changed |= applyInt(payload, "l2_clear",  L2_BUS_CLEAR,   1, 100, seen);
  changed |= applyInt(payload, "l3_xshut",  L3_XSHUT,       1, 200, seen);

  float offsetVal = 0.0f;
  if (getJsonFloat(payload, "offset_mm", offsetVal)) {
    if (offsetVal >= OFFSET_MIN_MM && offsetVal <= OFFSET_MAX_MM) {
      seen = true;
      if (offsetVal != g_offsetMm) {
        g_offsetMm = offsetVal;
        changed = true;
        Serial.printf("Set g_offsetMm = %.2f\n", offsetVal);
      }
    } else {
      Serial.printf("offset_mm=%.2f fora da faixa [%.0f, %.0f]; ignorado.\n",
                    offsetVal, OFFSET_MIN_MM, OFFSET_MAX_MM);
    }
  }

  // Avanca o ACK somente se ao menos uma chave valida foi reconhecida (D02)
  if (seen && cmdId > 0) {
    g_lastCmdId = static_cast<uint32_t>(cmdId);
  }

  // Grava na flash so quando algo mudou. Um comando que repete os valores vigentes e
  // sucesso (o ack sai no push seguinte de qualquer forma), mas nao custa um ciclo de NVS.
  if (changed) {
    saveNvsConfig();
  } else if (seen) {
    Serial.println("[CMD] Parametros ja vigentes; nada persistido.");
  } else {
    Serial.println("Failed to parse any valid keys from payload.");
  }
  return seen;
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

