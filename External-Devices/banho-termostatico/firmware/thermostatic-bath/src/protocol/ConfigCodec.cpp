#include "ConfigCodec.h"

#include <WiFi.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"
#include "../display/DisplayReader.h"
#include "../keypad/KeyPresser.h"
#include "../keypad/KeySense.h"
#include "../setpoint/SetpointGuard.h"
#include "../setpoint/SetpointManager.h"
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
  if (!strncmp(val, "true", 4)) return 1;
  if (!strncmp(val, "false", 5)) return 0;
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

bool getJsonString(const char* json, const char* key, char* out, size_t outLen) {
  const char* val = findJsonValueStart(json, key);
  if (!val || *val != '"' || outLen == 0) return false;
  ++val;
  size_t i = 0;
  while (*val && *val != '"' && i + 1 < outLen) out[i++] = *val++;
  out[i] = '\0';
  return *val == '"';
}

namespace {
constexpr long  PERIOD_MIN_MS = 100;
constexpr long  PERIOD_MAX_MS = 60000;
constexpr float SP_ABS_MIN = -200.0f;   // faixa PT100 do C404
constexpr float SP_ABS_MAX = 900.0f;

bool applyU16(const char* payload, const char* key, uint16_t& target, long minVal, long maxVal, bool& seen) {
  const long value = getJsonValue(payload, key);
  if (!findJsonValueStart(payload, key)) return false;
  if (value < minVal || value > maxVal) { Serial.printf("[CMD] %s=%ld fora da faixa; ignorado.\n", key, value); return false; }
  seen = true;
  if (static_cast<uint16_t>(value) == target) return false;
  target = static_cast<uint16_t>(value);
  Serial.printf("[CMD] %s = %u\n", key, target);
  return true;
}

bool applyU8(const char* payload, const char* key, uint8_t& target, long minVal, long maxVal, bool& seen) {
  if (!findJsonValueStart(payload, key)) return false;
  const long value = getJsonValue(payload, key);
  if (value < minVal || value > maxVal) { Serial.printf("[CMD] %s=%ld fora da faixa; ignorado.\n", key, value); return false; }
  seen = true;
  if (static_cast<uint8_t>(value) == target) return false;
  target = static_cast<uint8_t>(value);
  Serial.printf("[CMD] %s = %u\n", key, target);
  return true;
}

bool applyFloat(const char* payload, const char* key, float& target, float minVal, float maxVal, bool& seen) {
  float value;
  if (!getJsonFloat(payload, key, value)) return false;
  if (value < minVal || value > maxVal) { Serial.printf("[CMD] %s=%.3f fora da faixa; ignorado.\n", key, value); return false; }
  seen = true;
  if (value == target) return false;
  target = value;
  Serial.printf("[CMD] %s = %.3f\n", key, target);
  return true;
}

void replyOk(String& reply, const char* action, const char* extra = nullptr) {
  reply = "{\"ok\":true,\"action\":\"";
  reply += action;
  reply += "\"";
  if (extra) { reply += ","; reply += extra; }
  reply += "}";
}

void replyError(String& reply, const String& err) {
  reply = "{\"ok\":false,\"error\":\"" + err + "\"}";
}

// `presses` e a distancia em toques de seta ate o alvo; `hold` diz se a sequencia
// vai manter a tecla (modo display) em vez de so contar toques.
String setpointReplyExtra() {
  char extra[80];
  snprintf(extra, sizeof(extra), "\"target\":%.2f,\"presses\":%ld,\"hold\":%s", g_spTarget,
           labs(setpointPlannedPresses()), setpointPlannedHold() ? "true" : "false");
  return String(extra);
}

// Executa no maximo uma acao por payload, na ordem de prioridade abaixo. Devolve
// true se alguma chave de acao existia (mesmo que tenha sido recusada).
bool runAction(const char* payload, String& reply, bool& accepted) {
  String err;
  float value;
  char keyBuf[8];

  // Parada do Hub (r3.2): abort + modo manual numa unica revisao confiavel. Depois
  // dela nem a cascata nem a guarda acionam reles; o C404 fica no ultimo SP.
  if (getJsonValue(payload, "stop") == 1) {
    setpointAbort();
    guardSetMode(MODE_MANUAL, "stop");
    accepted = true;
    replyOk(reply, "stop");
    return true;
  }
  if (getJsonValue(payload, "abort") == 1) {
    setpointAbort();
    accepted = true;
    replyOk(reply, "abort");
    return true;
  }
  // Modo: string ("manual"/"auto") ou numero (0/1). Aceito mesmo com sequencia em
  // curso; o guarda so age depois dela.
  if (findJsonValueStart(payload, "mode")) {
    uint8_t mode;
    char modeBuf[12];
    bool parsed = false;
    if (getJsonString(payload, "mode", modeBuf, sizeof(modeBuf))) {
      parsed = modeFromName(modeBuf, mode);
    } else {
      const long v = getJsonValue(payload, "mode");
      parsed = v == MODE_MANUAL || v == MODE_AUTO;
      mode = static_cast<uint8_t>(v);
    }
    if (!parsed) { accepted = false; replyError(reply, "unknown_mode"); return true; }
    accepted = guardSetMode(mode, "comando");
    char extra[32];
    snprintf(extra, sizeof(extra), "\"mode\":\"%s\"", modeName(g_mode));
    replyOk(reply, "mode", extra);
    return true;
  }
  if (getJsonFloat(payload, "sync_sp", value)) {
    accepted = setpointSync(value, err);
    if (accepted) { guardNotifyCommand(); replyOk(reply, "sync_sp"); } else replyError(reply, err);
    return true;
  }
  if (getJsonValue(payload, "home") == 1) {
    const bool hasTarget = getJsonFloat(payload, "setpoint", value);
    accepted = setpointHome(hasTarget, value, err);
    if (accepted) {
      guardNotifyCommand();
      char extra[48];
      snprintf(extra, sizeof(extra), "\"presses\":%lu", static_cast<unsigned long>(keypadPressesTotal()));
      replyOk(reply, "home", extra);
    } else {
      replyError(reply, err);
    }
    return true;
  }
  if (getJsonFloat(payload, "setpoint", value)) {
    accepted = setpointRequestAbsolute(value, err);
    if (accepted) { guardNotifyCommand(); replyOk(reply, "setpoint", setpointReplyExtra().c_str()); } else replyError(reply, err);
    return true;
  }
  if (getJsonFloat(payload, "delta", value)) {
    accepted = setpointRequestDelta(value, err);
    if (accepted) { guardNotifyCommand(); replyOk(reply, "delta", setpointReplyExtra().c_str()); } else replyError(reply, err);
    return true;
  }
  if (getJsonString(payload, "key", keyBuf, sizeof(keyBuf))) {
    Key key;
    if (!keyFromName(keyBuf, key)) { accepted = false; replyError(reply, "unknown_key"); return true; }
    // `hold_ms` mantem a tecla fechada por um tempo fixo (bancada: medir a
    // auto-repeticao); sem ele, `count` toques discretos.
    if (findJsonValueStart(payload, "hold_ms")) {
      const long holdMs = getJsonValue(payload, "hold_ms");
      if (holdMs < 1) { accepted = false; replyError(reply, "range"); return true; }
      accepted = setpointRawHold(key, static_cast<uint32_t>(holdMs), err);
      if (accepted) replyOk(reply, "hold"); else replyError(reply, err);
      return true;
    }
    long count = getJsonValue(payload, "count");
    if (!findJsonValueStart(payload, "count")) count = 1;
    if (count < 1 || count > 65535) { accepted = false; replyError(reply, "range"); return true; }
    accepted = setpointRawPress(key, static_cast<uint16_t>(count), err);
    if (accepted) replyOk(reply, "key"); else replyError(reply, err);
    return true;
  }
  return false;
}
}  // namespace

namespace {
bool processCommandImpl(const char* payload, String& reply, CommandSource source) {
  if (!payload) { replyError(reply, "empty"); return false; }

  // Com a cascata do Hub dona do banho, a API local nao move o C404 nem muda a
  // configuracao. Abort/stop continuam livres: parar nunca depende do Hub.
  if (source == CommandSource::Local && hubOwnershipActive(millis()) &&
      getJsonValue(payload, "abort") != 1 && getJsonValue(payload, "stop") != 1) {
    replyError(reply, "hub_owned");
    return true;
  }

  // Reentrega da mesma revisao (Hub reenvia ate ver o ack). Acoes de tecla nunca
  // podem ser reaplicadas por reentrega: cada toque extra mudaria o SP.
  const long cmdId = getJsonValue(payload, "cmd_id");
  if (cmdId > 0 && static_cast<uint32_t>(cmdId) == g_lastCmdId) {
    Serial.printf("[CMD] cmd_id=%ld ja aplicado; ignorando reentrega.\n", cmdId);
    replyOk(reply, "duplicate");
    return true;
  }

  if (getJsonValue(payload, "reset_nvs") == 1) {
    if (setpointBusy() || keypadBusy()) { replyError(reply, "busy"); return true; }
    resetNvsConfig();
    g_cfg = BathConfig();
    g_spShadow = BoardConfig::InitialSetpointC;
    g_spTarget = g_spShadow;
    g_spKnown = true;
    guardSetMode(MODE_MANUAL, "reset_nvs");
    saveNvsState(false);
    if (cmdId > 0) g_lastCmdId = static_cast<uint32_t>(cmdId);
    Serial.println("[CMD] NVS limpa e padroes restaurados.");
    replyOk(reply, "reset_nvs");
    return true;
  }

  bool accepted = false;
  if (runAction(payload, reply, accepted)) {
    if (accepted && cmdId > 0) g_lastCmdId = static_cast<uint32_t>(cmdId);
    return true;
  }

  // Configuracao. Nao se troca passo, teclas ou fonte com toques em andamento.
  if (setpointBusy() || keypadBusy()) { replyError(reply, "busy"); return true; }

  bool seen = false;
  bool changed = false;
  changed |= applyU16(payload, "press_ms",   g_cfg.pressMs,   20, 2000, seen);
  changed |= applyU16(payload, "gap_ms",     g_cfg.gapMs,     20, 5000, seen);
  changed |= applyU16(payload, "menu_ms",    g_cfg.menuMs,    0, 10000, seen);
  changed |= applyU16(payload, "settle_ms",  g_cfg.settleMs,  0, 60000, seen);
  changed |= applyFloat(payload, "step_c",   g_cfg.stepC,     0.01f, 10.0f, seen);
  changed |= applyFloat(payload, "sp_min",   g_cfg.spMin,     SP_ABS_MIN, SP_ABS_MAX, seen);
  changed |= applyFloat(payload, "sp_max",   g_cfg.spMax,     SP_ABS_MIN, SP_ABS_MAX, seen);
  changed |= applyU8(payload, "enter_key",   g_cfg.enterKey,  0, 2, seen);
  changed |= applyU8(payload, "confirm_key", g_cfg.confirmKey, 0, 2, seen);
  changed |= applyU8(payload, "sp_source",   g_cfg.spSource,  0, 1, seen);
  changed |= applyU8(payload, "sense_enabled", g_cfg.senseEnabled, 0, 1, seen);
  changed |= applyU8(payload, "sense_mask",   g_cfg.senseMask,   0, 15, seen);
  changed |= applyU8(payload, "hub_enabled", g_cfg.hubEnabled, 0, 1, seen);
  changed |= applyU8(payload, "disp_seg_low", g_cfg.dispSegLow, 0, 1, seen);
  changed |= applyU8(payload, "disp_dig_low", g_cfg.dispDigLow, 0, 1, seen);
  changed |= applyU8(payload, "disp_seg_lead", g_cfg.dispSegLead, 0, 1, seen);
  changed |= applyU16(payload, "home_margin", g_cfg.homeMargin, 0, 1000, seen);
  changed |= applyU8(payload, "hold_enabled", g_cfg.holdEnabled, 0, 1, seen);
  changed |= applyU16(payload, "hold_min_steps", g_cfg.holdMinSteps, 1, 1000, seen);
  changed |= applyU16(payload, "hold_stop_steps", g_cfg.holdStopSteps, 0, 100, seen);
  changed |= applyU16(payload, "hold_lag_ms", g_cfg.holdLagMs, 0, 2000, seen);
  changed |= applyU16(payload, "hold_settle_ms", g_cfg.holdSettleMs, 0, 10000, seen);
  changed |= applyU16(payload, "hold_stall_ms", g_cfg.holdStallMs, 200, 20000, seen);
  changed |= applyU16(payload, "mode_hold_ms", g_cfg.modeHoldMs, 0, 20000, seen);
  changed |= applyU16(payload, "guard_delay_ms", g_cfg.guardDelayMs, 1000, 60000, seen);
  {
    const long v = getJsonValue(payload, "send_period");
    if (findJsonValueStart(payload, "send_period") && v >= PERIOD_MIN_MS && v <= PERIOD_MAX_MS) {
      seen = true;
      if (static_cast<uint32_t>(v) != g_cfg.sendPeriodMs) { g_cfg.sendPeriodMs = v; changed = true; }
    }
  }

  if (g_cfg.spMin >= g_cfg.spMax) {
    // Faixa invertida deixaria todo pedido fora de faixa; restaura a anterior.
    Serial.println("[CMD] sp_min >= sp_max; faixa recusada.");
    loadNvsConfig();
    replyError(reply, "sp_range");
    return true;
  }

  if (seen && cmdId > 0) g_lastCmdId = static_cast<uint32_t>(cmdId);

  if (changed) {
    saveNvsConfig();
    keySenseInit();   // sense_mask muda o pinMode das linhas
    replyOk(reply, "config");
  } else if (seen) {
    replyOk(reply, "config_unchanged");
  } else {
    replyError(reply, "no_valid_keys");
  }
  return seen;
}
}  // namespace

bool processCommand(const char* payload, String& reply, CommandSource source) {
  const bool seen = processCommandImpl(payload, reply, source);
  if (source != CommandSource::Hub || !payload) return seen;

  // Recusa de um comando do Hub fica registrada ate ele ser aceito: o Hub le
  // rej_cmd_id/rej_err no push e decide sem esperar o timeout de conclusao.
  const long cmdId = getJsonValue(payload, "cmd_id");
  if (cmdId <= 0) return seen;
  if (reply.indexOf("\"ok\":false") >= 0) {
    String err = "rejected";
    const int start = reply.indexOf("\"error\":\"");
    if (start >= 0) {
      const int from = start + 9;
      const int end = reply.indexOf('"', from);
      if (end > from) err = reply.substring(from, end);
    }
    g_hubRejectCmdId = static_cast<uint32_t>(cmdId);
    snprintf(g_hubRejectErr, sizeof(g_hubRejectErr), "%s", err.c_str());
  } else if (static_cast<uint32_t>(cmdId) == g_hubRejectCmdId) {
    g_hubRejectCmdId = 0;
    g_hubRejectErr[0] = '\0';
  }
  return seen;
}

String getConfigAsJson() {
  char buf[660];
  snprintf(buf, sizeof(buf),
           "{\"press_ms\":%u,\"gap_ms\":%u,\"menu_ms\":%u,\"settle_ms\":%u,\"step_c\":%.3f,"
           "\"sp_min\":%.2f,\"sp_max\":%.2f,\"enter_key\":%u,\"confirm_key\":%u,\"sp_source\":%u,"
           "\"sense_enabled\":%u,\"sense_mask\":%u,\"hub_enabled\":%u,\"disp_seg_low\":%u,\"disp_dig_low\":%u,"
           "\"disp_seg_lead\":%u,\"home_margin\":%u,\"send_period\":%lu,"
           "\"hold_enabled\":%u,\"hold_min_steps\":%u,\"hold_stop_steps\":%u,\"hold_lag_ms\":%u,"
           "\"hold_settle_ms\":%u,\"hold_stall_ms\":%u,\"mode_hold_ms\":%u,\"guard_delay_ms\":%u}",
           g_cfg.pressMs, g_cfg.gapMs, g_cfg.menuMs, g_cfg.settleMs, g_cfg.stepC,
           g_cfg.spMin, g_cfg.spMax, g_cfg.enterKey, g_cfg.confirmKey, g_cfg.spSource,
           g_cfg.senseEnabled, g_cfg.senseMask, g_cfg.hubEnabled, g_cfg.dispSegLow, g_cfg.dispDigLow,
           g_cfg.dispSegLead, g_cfg.homeMargin, static_cast<unsigned long>(g_cfg.sendPeriodMs),
           g_cfg.holdEnabled, g_cfg.holdMinSteps, g_cfg.holdStopSteps, g_cfg.holdLagMs,
           g_cfg.holdSettleMs, g_cfg.holdStallMs, g_cfg.modeHoldMs, g_cfg.guardDelayMs);
  return String(buf);
}

String getStatusAsJson() {
  char buf[980];
  const unsigned long nowMs = millis();
  const bool pvOk = displayPvValid();
  const bool spOk = displaySpValid();
  float deviation;
  const bool devOk = guardDeviation(deviation);
  snprintf(buf, sizeof(buf),
           "{\"device\":\"%s\",\"version\":\"%s\",\"uptime_s\":%lu,"
           "\"mode\":\"%s\",\"guard\":\"%s\",\"deviation_c\":%s,\"guard_corrections\":%lu,\"arrows_held_ms\":%lu,"
           "\"sp_shadow\":%.2f,\"sp_known\":%s,\"sp_target\":%.2f,\"sp_source\":%u,"
           "\"seq_state\":\"%s\",\"seq_kind\":\"%s\",\"seq_phase\":\"%s\",\"seq_error\":\"%s\","
           "\"presses_done\":%lu,\"presses_total\":%lu,\"presses_unconfirmed\":%lu,"
           "\"hold_ms\":%lu,\"hold_rounds\":%u,\"hold_rate\":%.1f,"
           "\"display_alive\":%s,\"display_pv\":%s,\"display_sp\":%s,\"display_text\":\"%s\","
           "\"manual_presses\":%lu,\"manual_age_s\":%ld,"
           "\"wifi_status\":%d,\"ip\":\"%s\",\"ota\":%s,\"last_cmd_id\":%lu,"
           "\"hub_owned\":%s,\"hub_owner_age_ms\":%ld}",
           BoardConfig::DeviceKey, BoardConfig::FirmwareTag,
           static_cast<unsigned long>(millis() / 1000),
           modeName(g_mode), guardStateName(), devOk ? String(deviation, 2).c_str() : "null",
           static_cast<unsigned long>(guardCorrections()), keySenseArrowsHeldMs(millis()),
           g_spShadow, g_spKnown ? "true" : "false", g_spTarget, g_cfg.spSource,
           setpointStateName(), setpointKindName(), setpointPhaseName(), setpointLastError().c_str(),
           static_cast<unsigned long>(keypadPressesDone()),
           static_cast<unsigned long>(keypadPressesTotal()),
           static_cast<unsigned long>(keypadUnconfirmedPresses()),
           keypadHoldMs(millis()), setpointHoldRounds(), setpointHoldRateStepsPerS(),
           displayAlive() ? "true" : "false",
           pvOk ? String(displayPv(), 2).c_str() : "null",
           spOk ? String(displaySp(), 2).c_str() : "null",
           displayText().c_str(),
           static_cast<unsigned long>(g_manualPressCount),
           g_manualActivityMs ? static_cast<long>((millis() - g_manualActivityMs) / 1000) : -1L,
           WiFi.status(), WiFi.localIP().toString().c_str(),
           g_otaInProgress ? "true" : "false",
           static_cast<unsigned long>(g_lastCmdId),
           hubOwnershipActive(nowMs) ? "true" : "false",
           g_hubOwnerSeenMs ? static_cast<long>(nowMs - g_hubOwnerSeenMs) : -1L);
  return String(buf);
}
