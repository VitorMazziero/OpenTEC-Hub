#include "SetpointGuard.h"

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

inline long stepsBetween(float from, float to) {
  return lroundf((to - from) / g_cfg.stepC);
}

void setState(GuardState state) {
  if (state == g_state) return;
  g_state = state;
  Serial.printf("[GUARD] %s\n", guardStateName());
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
    setState(GUARD_WATCH);
    return;
  }
  if (result == SEQ_ABORTED) {
    Serial.println("[GUARD] Correcao abortada pelo operador; guarda suspenso.");
    setState(GUARD_SUSPENDED);
    return;
  }
  if (++g_fails >= MAX_FAILS) {
    Serial.printf("[GUARD] %u correcoes seguidas falharam; guarda suspenso.\n", g_fails);
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
  if (g_cfg.spSource != SP_SOURCE_DISPLAY || !displaySpValid()) { setState(GUARD_WATCH); return; }

  const float seen = displaySp();
  if (stepsBetween(seen, g_spTarget) == 0) { setState(GUARD_WATCH); return; }

  // Desvio: espera o painel parar. Cada valor novo reinicia a contagem, e uma
  // tecla manual ainda pressionada tambem (g_manualActivityMs segue a soltura).
  if (g_state != GUARD_PENDING || fabsf(seen - g_pendingSp) > g_cfg.stepC * 0.5f) {
    setState(GUARD_PENDING);
    g_pendingSp = seen;
    g_pendingSinceMs = now;
    return;
  }
  if (now - g_pendingSinceMs < g_cfg.guardDelayMs) return;
  if (g_cfg.senseEnabled && g_manualActivityMs && now - g_manualActivityMs < g_cfg.guardDelayMs) return;

  String err;
  if (setpointRequestAbsolute(g_spTarget, err)) {
    ++g_corrections;
    Serial.printf("[GUARD] Display em %.2f, alvo %.2f: revertendo (correcao %lu)\n",
                  seen, g_spTarget, static_cast<unsigned long>(g_corrections));
    setState(GUARD_CORRECTING);
    return;
  }
  Serial.printf("[GUARD] Correcao recusada: %s\n", err.c_str());
  if (++g_fails >= MAX_FAILS) { setState(GUARD_SUSPENDED); return; }
  g_pendingSinceMs = now;   // tenta de novo apos mais uma espera
}

bool guardSetMode(uint8_t mode, const char* source) {
  if (mode > MODE_AUTO) return false;
  const bool changed = mode != g_mode;
  g_mode = mode;
  g_fails = 0;
  g_state = g_mode == MODE_AUTO ? GUARD_WATCH : GUARD_OFF;
  showMode();
  if (changed) saveNvsState(setpointBusy());
  Serial.printf("[GUARD] Modo %s (%s)%s\n", modeName(g_mode), source, changed ? "" : " - inalterado");
  return true;
}

void guardNotifyCommand() {
  g_fails = 0;
  if (g_state == GUARD_SUSPENDED || g_state == GUARD_PENDING) setState(GUARD_WATCH);
}

GuardState guardState() { return g_state; }
uint32_t guardCorrections() { return g_corrections; }

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
