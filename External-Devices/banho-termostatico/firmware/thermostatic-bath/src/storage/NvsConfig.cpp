#include "NvsConfig.h"

#include <Arduino.h>
#include <Preferences.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"

namespace {
Preferences g_prefs;
constexpr const char* NS_CONFIG = "bath_cfg";
constexpr const char* NS_STATE  = "bath_st";

// Chaves com no maximo 15 caracteres.
constexpr const char* KEY_PRESS_MS   = "press_ms";
constexpr const char* KEY_GAP_MS     = "gap_ms";
constexpr const char* KEY_MENU_MS    = "menu_ms";
constexpr const char* KEY_SETTLE_MS  = "settle_ms";
constexpr const char* KEY_STEP_C     = "step_c";
constexpr const char* KEY_SP_MIN     = "sp_min";
constexpr const char* KEY_SP_MAX     = "sp_max";
constexpr const char* KEY_ENTER_KEY  = "enter_key";
constexpr const char* KEY_CONFIRM    = "confirm_key";
constexpr const char* KEY_SP_SOURCE  = "sp_source";
constexpr const char* KEY_SENSE      = "sense_en";
constexpr const char* KEY_SENSE_MASK = "sense_mask";
constexpr const char* KEY_HUB        = "hub_en";
constexpr const char* KEY_SEG_LOW    = "disp_seg_low";
constexpr const char* KEY_DIG_LOW    = "disp_dig_low";
constexpr const char* KEY_SEG_LEAD   = "disp_seg_lead";
constexpr const char* KEY_HOME_MARG  = "home_margin";
constexpr const char* KEY_SEND_MS    = "send_ms";
constexpr const char* KEY_HOLD_EN    = "hold_en";
constexpr const char* KEY_HOLD_MIN   = "hold_min";
constexpr const char* KEY_HOLD_STOP  = "hold_stop";
constexpr const char* KEY_HOLD_LAG   = "hold_lag_ms";
constexpr const char* KEY_HOLD_SETTLE = "hold_settle";
constexpr const char* KEY_HOLD_STALL = "hold_stall";
constexpr const char* KEY_MODE_HOLD  = "mode_hold_ms";
constexpr const char* KEY_GUARD_MS   = "guard_ms";

constexpr const char* KEY_SP_SHADOW  = "sp_shadow";
constexpr const char* KEY_SP_KNOWN   = "sp_known";
constexpr const char* KEY_SEQ_BUSY   = "seq_busy";
constexpr const char* KEY_SP_TARGET  = "sp_target";
constexpr const char* KEY_MODE       = "mode";
}  // namespace

void loadNvsConfig() {
  if (!g_prefs.begin(NS_CONFIG, true)) {
    Serial.println("[NVS] bath_cfg ausente; usando padroes.");
    return;
  }
  g_cfg.pressMs      = g_prefs.getUShort(KEY_PRESS_MS, g_cfg.pressMs);
  g_cfg.gapMs        = g_prefs.getUShort(KEY_GAP_MS, g_cfg.gapMs);
  g_cfg.menuMs       = g_prefs.getUShort(KEY_MENU_MS, g_cfg.menuMs);
  g_cfg.settleMs     = g_prefs.getUShort(KEY_SETTLE_MS, g_cfg.settleMs);
  g_cfg.stepC        = g_prefs.getFloat(KEY_STEP_C, g_cfg.stepC);
  g_cfg.spMin        = g_prefs.getFloat(KEY_SP_MIN, g_cfg.spMin);
  g_cfg.spMax        = g_prefs.getFloat(KEY_SP_MAX, g_cfg.spMax);
  g_cfg.enterKey     = g_prefs.getUChar(KEY_ENTER_KEY, g_cfg.enterKey);
  g_cfg.confirmKey   = g_prefs.getUChar(KEY_CONFIRM, g_cfg.confirmKey);
  g_cfg.spSource     = g_prefs.getUChar(KEY_SP_SOURCE, g_cfg.spSource);
  g_cfg.senseEnabled = g_prefs.getUChar(KEY_SENSE, g_cfg.senseEnabled);
  g_cfg.senseMask    = g_prefs.getUChar(KEY_SENSE_MASK, g_cfg.senseMask);
  g_cfg.hubEnabled   = g_prefs.getUChar(KEY_HUB, g_cfg.hubEnabled);
  g_cfg.dispSegLow   = g_prefs.getUChar(KEY_SEG_LOW, g_cfg.dispSegLow);
  g_cfg.dispDigLow   = g_prefs.getUChar(KEY_DIG_LOW, g_cfg.dispDigLow);
  g_cfg.dispSegLead  = g_prefs.getUChar(KEY_SEG_LEAD, g_cfg.dispSegLead);
  g_cfg.homeMargin   = g_prefs.getUShort(KEY_HOME_MARG, g_cfg.homeMargin);
  g_cfg.sendPeriodMs = g_prefs.getULong(KEY_SEND_MS, g_cfg.sendPeriodMs);
  g_cfg.holdEnabled   = g_prefs.getUChar(KEY_HOLD_EN, g_cfg.holdEnabled);
  g_cfg.holdMinSteps  = g_prefs.getUShort(KEY_HOLD_MIN, g_cfg.holdMinSteps);
  g_cfg.holdStopSteps = g_prefs.getUShort(KEY_HOLD_STOP, g_cfg.holdStopSteps);
  g_cfg.holdLagMs     = g_prefs.getUShort(KEY_HOLD_LAG, g_cfg.holdLagMs);
  g_cfg.holdSettleMs  = g_prefs.getUShort(KEY_HOLD_SETTLE, g_cfg.holdSettleMs);
  g_cfg.holdStallMs   = g_prefs.getUShort(KEY_HOLD_STALL, g_cfg.holdStallMs);
  g_cfg.modeHoldMs    = g_prefs.getUShort(KEY_MODE_HOLD, g_cfg.modeHoldMs);
  g_cfg.guardDelayMs  = g_prefs.getUShort(KEY_GUARD_MS, g_cfg.guardDelayMs);
  g_prefs.end();
  Serial.printf("[NVS] Config: press=%ums gap=%ums step=%.2f faixa=[%.1f,%.1f] enter=%u confirm=%u fonte=%u hold=%u\n",
                g_cfg.pressMs, g_cfg.gapMs, g_cfg.stepC, g_cfg.spMin, g_cfg.spMax,
                g_cfg.enterKey, g_cfg.confirmKey, g_cfg.spSource, g_cfg.holdEnabled);
}

void saveNvsConfig() {
  if (!g_prefs.begin(NS_CONFIG, false)) {
    Serial.println("[NVS] Erro ao abrir bath_cfg para escrita!");
    return;
  }
  g_prefs.putUShort(KEY_PRESS_MS, g_cfg.pressMs);
  g_prefs.putUShort(KEY_GAP_MS, g_cfg.gapMs);
  g_prefs.putUShort(KEY_MENU_MS, g_cfg.menuMs);
  g_prefs.putUShort(KEY_SETTLE_MS, g_cfg.settleMs);
  g_prefs.putFloat(KEY_STEP_C, g_cfg.stepC);
  g_prefs.putFloat(KEY_SP_MIN, g_cfg.spMin);
  g_prefs.putFloat(KEY_SP_MAX, g_cfg.spMax);
  g_prefs.putUChar(KEY_ENTER_KEY, g_cfg.enterKey);
  g_prefs.putUChar(KEY_CONFIRM, g_cfg.confirmKey);
  g_prefs.putUChar(KEY_SP_SOURCE, g_cfg.spSource);
  g_prefs.putUChar(KEY_SENSE, g_cfg.senseEnabled);
  g_prefs.putUChar(KEY_SENSE_MASK, g_cfg.senseMask);
  g_prefs.putUChar(KEY_HUB, g_cfg.hubEnabled);
  g_prefs.putUChar(KEY_SEG_LOW, g_cfg.dispSegLow);
  g_prefs.putUChar(KEY_DIG_LOW, g_cfg.dispDigLow);
  g_prefs.putUChar(KEY_SEG_LEAD, g_cfg.dispSegLead);
  g_prefs.putUShort(KEY_HOME_MARG, g_cfg.homeMargin);
  g_prefs.putULong(KEY_SEND_MS, g_cfg.sendPeriodMs);
  g_prefs.putUChar(KEY_HOLD_EN, g_cfg.holdEnabled);
  g_prefs.putUShort(KEY_HOLD_MIN, g_cfg.holdMinSteps);
  g_prefs.putUShort(KEY_HOLD_STOP, g_cfg.holdStopSteps);
  g_prefs.putUShort(KEY_HOLD_LAG, g_cfg.holdLagMs);
  g_prefs.putUShort(KEY_HOLD_SETTLE, g_cfg.holdSettleMs);
  g_prefs.putUShort(KEY_HOLD_STALL, g_cfg.holdStallMs);
  g_prefs.putUShort(KEY_MODE_HOLD, g_cfg.modeHoldMs);
  g_prefs.putUShort(KEY_GUARD_MS, g_cfg.guardDelayMs);
  g_prefs.end();
  Serial.println("[NVS] Configuracao persistida.");
}

void resetNvsConfig() {
  if (g_prefs.begin(NS_CONFIG, false)) {
    g_prefs.clear();
    g_prefs.end();
  }
  if (g_prefs.begin(NS_STATE, false)) {
    g_prefs.clear();
    g_prefs.end();
  }
  Serial.println("[NVS] bath_cfg e bath_st limpos.");
}

void loadNvsState() {
  if (!g_prefs.begin(NS_STATE, true)) {
    Serial.printf("[NVS] bath_st ausente; sombra parte de %.1f.\n", g_spShadow);
    return;
  }
  g_spShadow = g_prefs.getFloat(KEY_SP_SHADOW, g_spShadow);
  g_spKnown  = g_prefs.getUChar(KEY_SP_KNOWN, g_spKnown ? 1 : 0) != 0;
  const bool busy = g_prefs.getUChar(KEY_SEQ_BUSY, 0) != 0;
  // O alvo e persistido a parte: no modo display a sombra segue o painel, e um
  // reboot no meio de uma mudanca manual nao pode transformar essa mudanca em alvo.
  g_spTarget = g_prefs.getFloat(KEY_SP_TARGET, g_spShadow);
  g_mode = g_prefs.getUChar(KEY_MODE, g_mode);
  if (g_mode > MODE_AUTO) g_mode = MODE_MANUAL;
  g_prefs.end();
  if (busy) {
    // Reboot com sequencia em curso: nao se sabe quantos toques chegaram ao C404.
    g_spKnown = false;
    Serial.println("[NVS] Sequencia interrompida por reboot; sombra marcada como desconhecida.");
    saveNvsState(false);
  }
  Serial.printf("[NVS] Estado: sombra=%.2f conhecido=%d alvo=%.2f modo=%u\n", g_spShadow, g_spKnown, g_spTarget, g_mode);
}

void saveNvsState(bool sequenceBusy) {
  if (!g_prefs.begin(NS_STATE, false)) {
    Serial.println("[NVS] Erro ao abrir bath_st para escrita!");
    return;
  }
  g_prefs.putFloat(KEY_SP_SHADOW, g_spShadow);
  g_prefs.putUChar(KEY_SP_KNOWN, g_spKnown ? 1 : 0);
  g_prefs.putUChar(KEY_SEQ_BUSY, sequenceBusy ? 1 : 0);
  g_prefs.putFloat(KEY_SP_TARGET, g_spTarget);
  g_prefs.putUChar(KEY_MODE, g_mode);
  g_prefs.end();
}
