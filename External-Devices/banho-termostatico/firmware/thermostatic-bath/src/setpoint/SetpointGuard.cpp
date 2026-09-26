#include "SetpointGuard.h"

#include "../core/EventLog.h"

#include <math.h>

#include "../config/BoardConfig.h"
#include "../display/DisplayReader.h"
#include "../keypad/KeyPresser.h"
#include "../keypad/KeySense.h"
#include "../storage/NvsConfig.h"
#include "SetpointManager.h"

namespace {
// Correcoes seguidas que falharam (erro ou recusa) antes de o guarda se suspender:
// uma leitura errada do display faria o no apertar teclas sem parar. Um abort do
// operador suspende na hora. Um comando de setpoint ou uma troca de modo rearmam.
constexpr uint8_t MAX_FAILS = 3;

GuardState g_state = GUARD_OFF;
float g_pendingSp = 0.0f;
unsigned long g_pendingSinceMs = 0;
uint8_t g_fails = 0;
uint32_t g_corrections = 0;
bool g_gestureFired = false;
unsigned long g_buttonSinceMs = 0;
bool g_buttonFired = false;
unsigned long g_lastCheckMs = 0;
bool g_checked = false;
// O guarda so reverte um desvio depois de ver um toque manual em ▲/▼ (sensoriamento).
// Sem toque, um valor diferente no display e tratado como leitura ruim: so e contado.
bool g_armed = false;
uint32_t g_seenManualCount = 0;
uint32_t g_ignored = 0;
float g_lastIgnoredSp = NAN;

void disarm() {
  g_armed = false;
  g_seenManualCount = g_manualPressCount;
}

inline long stepsBetween(float from, float to) {
  return lroundf((to - from) / g_cfg.stepC);
}

void setState(GuardState state) {
  if (state == g_state) return;
  g_state = state;
  logPrintf("[GUARD] %s\n", guardStateName());
}

void showMode() {
  if (BoardConfig::ModeLedPin >= 0) digitalWrite(BoardConfig::ModeLedPin, g_mode == MODE_AUTO ? HIGH : LOW);
}

// Cada gesto vale uma troca por pressionamento: o operador solta antes de poder
// trocar de novo. Dois gestos: ▲+▼ no painel (sensoriamento) e o botao da caixa.
void serviceGesture(unsigned long now) {
  if (g_cfg.modeHoldMs == 0) return;

  if (g_cfg.senseEnabled) {
    const unsigned long held = keySenseArrowsHeldMs(now);
    if (held == 0) {
      g_gestureFired = false;
    } else if (!g_gestureFired && held >= g_cfg.modeHoldMs) {
      g_gestureFired = true;
      guardSetMode(g_mode == MODE_AUTO ? MODE_MANUAL : MODE_AUTO, "teclas");
    }
  }

  if (BoardConfig::ModeButtonPin >= 0) {
    const bool down = digitalRead(BoardConfig::ModeButtonPin) == LOW;
    if (!down) {
      g_buttonSinceMs = 0;
      g_buttonFired = false;
    } else {
      if (g_buttonSinceMs == 0) g_buttonSinceMs = now;
      if (!g_buttonFired && now - g_buttonSinceMs >= g_cfg.modeHoldMs) {
        g_buttonFired = true;
        guardSetMode(g_mode == MODE_AUTO ? MODE_MANUAL : MODE_AUTO, "botao");
      }
    }
  }
}

void serviceCorrecting(unsigned long now) {
  if (setpointBusy() || keypadBusy()) return;
  const SeqState result = setpointState();
  if (result == SEQ_DONE) {
    g_fails = 0;
    disarm();
    setState(GUARD_WATCH);
    return;
  }
  if (result == SEQ_ABORTED) {
    disarm();
    logPrintln("[GUARD] Correcao abortada pelo operador; guarda suspenso.");
    setState(GUARD_SUSPENDED);
    return;
  }
  if (++g_fails >= MAX_FAILS) {
    logPrintf("[GUARD] %u correcoes seguidas falharam; guarda suspenso.\n", g_fails);
    setState(GUARD_SUSPENDED);
    return;
  }
  setState(GUARD_WATCH);
}
}  // namespace

void guardInit() {
  if (BoardConfig::ModeButtonPin >= 0) pinMode(BoardConfig::ModeButtonPin, INPUT_PULLUP);
  if (BoardConfig::ModeLedPin >= 0) pinMode(BoardConfig::ModeLedPin, OUTPUT);
  g_state = g_mode == MODE_AUTO ? GUARD_WATCH : GUARD_OFF;
  showMode();
}

void guardService(unsigned long now) {
  serviceGesture(now);

  if (g_mode != MODE_AUTO) { setState(GUARD_OFF); return; }
  if (g_state == GUARD_OFF) setState(GUARD_WATCH);
  if (g_state == GUARD_SUSPENDED) return;
  if (g_state == GUARD_CORRECTING) { serviceCorrecting(now); return; }

  // Sequencia de outra origem (comando, home, toque cru) ou OTA: so observa.
  if (g_otaInProgress || setpointBusy() || keypadBusy()) { setState(GUARD_WATCH); return; }

  // Arma com um toque manual novo em ▲/▼ (contado pelo sensoriamento, que nao conta os
  // toques dos reles). Sem sensoriamento o guarda nunca arma.
  if (g_manualPressCount != g_seenManualCount) {
    g_seenManualCount = g_manualPressCount;
    if (!g_armed) logPrintf("[GUARD] Toque manual visto: armado.\n");
    g_armed = true;
  }

  // Avaliacao espacada por `guard_check_ms`.
  if (g_checked && now - g_lastCheckMs < g_cfg.guardCheckMs) return;
  g_checked = true;
  g_lastCheckMs = now;
  if (g_cfg.spSource != SP_SOURCE_DISPLAY || !displaySpValid()) { setState(GUARD_WATCH); return; }

  const float seen = displaySp();
  // Com uma tecla ainda ativa (ou solta ha pouco) o display pode nao ter mudado ainda:
  // "no alvo" so desarma depois de guard_delay_ms sem toque.
  const bool keysQuiet = !g_manualActivityMs || now - g_manualActivityMs >= g_cfg.guardDelayMs;
  if (stepsBetween(seen, g_spTarget) == 0) {
    if (g_armed && !keysQuiet) { setState(GUARD_WATCH); return; }
    if (g_armed) logPrintf("[GUARD] Display de volta ao alvo %.2f: desarmado.\n", g_spTarget);
    disarm();
    g_lastIgnoredSp = NAN;
    setState(GUARD_WATCH);
    return;
  }

  if (!g_armed) {
    // Desvio sem toque: leitura ruim do display (ou mudanca que o sensoriamento nao ve).
    // Nao se aperta tecla por isso; conta uma vez por valor distinto.
    if (isnan(g_lastIgnoredSp) || fabsf(seen - g_lastIgnoredSp) > g_cfg.stepC * 0.5f) {
      ++g_ignored;
      g_lastIgnoredSp = seen;
      logPrintf("[GUARD] Display em %.2f (alvo %.2f) sem toque manual: ignorado (%lu).\n",
                seen, g_spTarget, static_cast<unsigned long>(g_ignored));
    }
    setState(GUARD_WATCH);
    return;
  }

  // Armado. Espera as teclas ficarem soltas por guard_delay_ms e o mesmo valor em duas
  // avaliacoes seguidas (uma leitura isolada nao basta para apertar tecla).
  if (!keysQuiet) {
    setState(GUARD_PENDING);
    g_pendingSp = seen;
    g_pendingSinceMs = now;
    return;
  }
  if (g_state != GUARD_PENDING || fabsf(seen - g_pendingSp) > g_cfg.stepC * 0.5f) {
    setState(GUARD_PENDING);
    g_pendingSp = seen;
    g_pendingSinceMs = now;
    return;
  }

  String err;
  if (setpointRequestAbsolute(g_spTarget, err)) {
    ++g_corrections;
    logPrintf("[GUARD] Display em %.2f, alvo %.2f: revertendo (correcao %lu)\n",
              seen, g_spTarget, static_cast<unsigned long>(g_corrections));
    setState(GUARD_CORRECTING);
    return;
  }
  logPrintf("[GUARD] Correcao recusada: %s\n", err.c_str());
  if (++g_fails >= MAX_FAILS) { setState(GUARD_SUSPENDED); return; }
}

bool guardSetMode(uint8_t mode, const char* source) {
  if (mode > MODE_AUTO) return false;
  const bool changed = mode != g_mode;
  g_mode = mode;
  g_fails = 0;
  disarm();
  // Entrar no automatico pelo gesto ▲+▼ e um toque no painel: se o par mexeu no SP,
  // o desvio e revertido depois que as teclas forem soltas.
  if (g_mode == MODE_AUTO && !strcmp(source, "teclas")) g_armed = true;
  g_state = g_mode == MODE_AUTO ? GUARD_WATCH : GUARD_OFF;
  showMode();
  if (changed) saveNvsState(setpointBusy());
  logPrintf("[GUARD] Modo %s (%s)%s\n", modeName(g_mode), source, changed ? "" : " - inalterado");
  return true;
}

void guardNotifyCommand() {
  g_fails = 0;
  disarm();
  if (g_state == GUARD_SUSPENDED || g_state == GUARD_PENDING) setState(GUARD_WATCH);
}

GuardState guardState() { return g_state; }
uint32_t guardCorrections() { return g_corrections; }
bool guardArmed() { return g_armed; }
uint32_t guardIgnored() { return g_ignored; }

const char* guardStateName() {
  switch (g_state) {
    case GUARD_OFF:        return "off";
    case GUARD_WATCH:      return "watch";
    case GUARD_PENDING:    return "pending";
    case GUARD_CORRECTING: return "correcting";
    case GUARD_SUSPENDED:  return "suspended";
  }
  return "?";
}

const char* modeName(uint8_t mode) {
  return mode == MODE_AUTO ? "auto" : "manual";
}

bool modeFromName(const char* name, uint8_t& out) {
  if (!name) return false;
  if (!strcmp(name, "auto") || !strcmp(name, "automatic") || !strcmp(name, "1")) { out = MODE_AUTO; return true; }
  if (!strcmp(name, "manual") || !strcmp(name, "0")) { out = MODE_MANUAL; return true; }
  return false;
}

bool guardDeviation(float& outC) {
  if (!displaySpValid()) return false;
  outC = displaySp() - g_spTarget;
  return true;
}
