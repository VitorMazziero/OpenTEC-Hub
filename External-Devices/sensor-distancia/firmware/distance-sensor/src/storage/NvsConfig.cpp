#include "NvsConfig.h"

#include <Arduino.h>
#include <Preferences.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"

namespace {
Preferences g_prefs;
constexpr const char* NVS_NAMESPACE = "dist_cfg";

// Keys (must be <= 15 chars)
constexpr const char* KEY_SAMPLE_MS = "sample_ms";
constexpr const char* KEY_SEND_MS   = "send_ms";
constexpr const char* KEY_CD_SOFT   = "cd_soft";
constexpr const char* KEY_CD_BUS    = "cd_bus";
constexpr const char* KEY_CD_XSHUT  = "cd_xshut";
constexpr const char* KEY_L1_REINIT = "l1_reinit";
constexpr const char* KEY_L2_CLEAR  = "l2_clear";
constexpr const char* KEY_L3_XSHUT  = "l3_xshut";
constexpr const char* KEY_OFFSET_MM = "offset_mm";
}  // namespace

void loadNvsConfig() {
  if (!g_prefs.begin(NVS_NAMESPACE, true)) {
    Serial.println("[NVS] dist_cfg nao encontrado ou erro ao abrir. Usando padroes.");
    return;
  }

  SAMPLE_PERIOD_MS   = g_prefs.getULong(KEY_SAMPLE_MS, SAMPLE_PERIOD_MS);
  SEND_PERIOD_MS     = g_prefs.getULong(KEY_SEND_MS, SEND_PERIOD_MS);
  COOLDOWN_SOFT_MS   = g_prefs.getULong(KEY_CD_SOFT, COOLDOWN_SOFT_MS);
  COOLDOWN_BUS_MS    = g_prefs.getULong(KEY_CD_BUS, COOLDOWN_BUS_MS);
  COOLDOWN_XSHUT_MS  = g_prefs.getULong(KEY_CD_XSHUT, COOLDOWN_XSHUT_MS);
  L1_SOFT_REINIT     = g_prefs.getInt(KEY_L1_REINIT, L1_SOFT_REINIT);
  L2_BUS_CLEAR       = g_prefs.getInt(KEY_L2_CLEAR, L2_BUS_CLEAR);
  L3_XSHUT           = g_prefs.getInt(KEY_L3_XSHUT, L3_XSHUT);
  g_offsetMm         = g_prefs.getFloat(KEY_OFFSET_MM, g_offsetMm);

  g_prefs.end();
  Serial.printf("[NVS] Config carregada: sample=%lums send=%lums offset=%.2fmm\n",
                SAMPLE_PERIOD_MS, SEND_PERIOD_MS, g_offsetMm);
}

void saveNvsConfig() {
  if (!g_prefs.begin(NVS_NAMESPACE, false)) {
    Serial.println("[NVS] Erro ao abrir dist_cfg para escrita!");
    return;
  }

  g_prefs.putULong(KEY_SAMPLE_MS, SAMPLE_PERIOD_MS);
  g_prefs.putULong(KEY_SEND_MS, SEND_PERIOD_MS);
  g_prefs.putULong(KEY_CD_SOFT, COOLDOWN_SOFT_MS);
  g_prefs.putULong(KEY_CD_BUS, COOLDOWN_BUS_MS);
  g_prefs.putULong(KEY_CD_XSHUT, COOLDOWN_XSHUT_MS);
  g_prefs.putInt(KEY_L1_REINIT, L1_SOFT_REINIT);
  g_prefs.putInt(KEY_L2_CLEAR, L2_BUS_CLEAR);
  g_prefs.putInt(KEY_L3_XSHUT, L3_XSHUT);
  g_prefs.putFloat(KEY_OFFSET_MM, g_offsetMm);

  g_prefs.end();
  Serial.println("[NVS] Configuracoes persistidas com sucesso na NVS.");
}

void resetNvsConfig() {
  if (g_prefs.begin(NVS_NAMESPACE, false)) {
    g_prefs.clear();
    g_prefs.end();
    Serial.println("[NVS] dist_cfg limpo com sucesso.");
  }
}
