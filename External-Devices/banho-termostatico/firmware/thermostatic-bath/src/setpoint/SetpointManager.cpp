#include "SetpointManager.h"

#include <math.h>

#include "../display/DisplayReader.h"
#include "../keypad/KeyPresser.h"
#include "../storage/NvsConfig.h"

namespace {
// 20 000 toques a 300 ms sao 100 min: cobre um "home" de -199.9 a 850.0 (faixa
// PT100 do C404) e ainda barra um pedido absurdo por erro de unidade.
constexpr long MAX_PRESSES = 20000;
// Rodadas de hold por sequencia. Cada rodada e um hold mais um assentamento; a
// partir da terceira a folga esta claramente mal ajustada e o resto vai a toques.
constexpr uint8_t MAX_HOLD_ROUNDS = 3;
// Correcao automatica apos um sp_mismatch no modo display: reabre a edicao e faz
// a diferenca a toques. Uma so, e so para diferencas pequenas: uma diferenca
// grande depois de uma perna calculada do proprio display e leitura errada ou
// C404 fora do esperado, e o operador precisa ver o erro em vez de o no insistir.
constexpr uint8_t MAX_CORRECTIONS = 1;
constexpr long MAX_CORRECTION_STEPS = 20;
// Display ilegivel durante um hold: solta antes que a auto-repeticao ande as
// cegas. Menor que o atraso tipico ate a primeira repeticao.
constexpr unsigned long HOLD_BLIND_MS = 300;
// Quanto o planejamento espera por uma leitura (estavel para a base, ao vivo
// para um hold) antes de seguir com o que tem.
constexpr unsigned long PLAN_WAIT_MS = 1000;
// Teto de um hold: nunca mais do que os toques discretos que ele substitui
// durariam, mais esta folga. E rede de seguranca; o display decide antes.
constexpr uint32_t HOLD_CAP_EXTRA_MS = 5000;
// Taxa maxima admitida na estimativa: acima disso e leitura errada, nao o C404.
constexpr float HOLD_RATE_CAP_SPS = 500.0f;
constexpr uint32_t RAW_HOLD_MAX_MS = 20000;

SeqState g_state = SEQ_IDLE;
SeqKind g_kind = KIND_NONE;
SeqPhase g_phase = PHASE_NONE;
String g_lastError;
unsigned long g_settleStartMs = 0;
unsigned long g_phaseSinceMs = 0;
bool g_rawTouchedSp = false;

// Plano da sequencia `setpoint` em curso.
long g_plannedPresses = 0;
bool g_planHold = false;
bool g_holdDisabled = false;   // hold deixou de valer nesta sequencia (parado ou as cegas)
uint8_t g_holdRounds = 0;
uint8_t g_corrections = 0;

// Hold em curso.
int8_t g_holdDir = 0;
float g_holdLastSp = 0.0f;
unsigned long g_holdLastChangeMs = 0;
unsigned long g_holdBlindSinceMs = 0;
uint16_t g_holdChanges = 0;    // mudancas vistas no display neste hold
float g_holdRate = 0.0f;       // toques por segundo vistos no display, media movel

inline float quantize(float value) {
  return roundf(value / g_cfg.stepC) * g_cfg.stepC;
}

inline float clampSp(float value) {
  if (value < g_cfg.spMin) return g_cfg.spMin;
  if (value > g_cfg.spMax) return g_cfg.spMax;
  return value;
}

inline long stepsBetween(float from, float to) {
  return lroundf((to - from) / g_cfg.stepC);
}

bool roleToKey(uint8_t role, Key& out) {
  if (role == ROLE_STAR)  { out = KEY_STAR;  return true; }
  if (role == ROLE_ENTER) { out = KEY_ENTER; return true; }
  return false;
}

void resetPlan() {
  g_plannedPresses = 0;
  g_planHold = false;
  g_holdDisabled = false;
  g_holdRounds = 0;
  g_corrections = 0;
  g_holdDir = 0;
  g_holdRate = 0.0f;
}

// Marca a sequencia como em curso na NVS antes do primeiro toque: um reboot no
// meio deixa `busy` gravado, e o proximo boot passa g_spKnown para false.
void beginSequence(SeqKind kind, SeqPhase phase) {
  g_kind = kind;
  g_state = SEQ_RUNNING;
  g_phase = phase;
  g_phaseSinceMs = millis();
  g_lastError = "";
  g_rawTouchedSp = false;
  keypadResetCounters();
  saveNvsState(true);
}

void finishSequence(SeqState finalState, const char* err) {
  g_state = finalState;
  g_phase = PHASE_NONE;
  g_lastError = err ? err : "";
  saveNvsState(false);
  Serial.printf("[SP] Sequencia %s terminou: %s%s%s (sombra=%.2f, conhecido=%d, holds=%u, correcoes=%u)\n",
                setpointKindName(), setpointStateName(),
                g_lastError.length() ? " - " : "", g_lastError.c_str(), g_spShadow, g_spKnown,
                g_holdRounds, g_corrections);
}

void failQueue() {
  keypadClear();
  finishSequence(SEQ_ERROR, "queue_full");
}

bool enqueueRole(uint8_t role, uint16_t extraGapMs) {
  Key key;
  if (!roleToKey(role, key)) return true;
  return keypadEnqueue(key, 1, extraGapMs);
}

bool enqueuePresses(long presses) {
  if (presses > 0 && !keypadEnqueue(KEY_UP, static_cast<uint16_t>(presses))) return false;
  if (presses < 0 && !keypadEnqueue(KEY_DOWN, static_cast<uint16_t>(-presses))) return false;
  return true;
}

// Precondicoes comuns a todo pedido que aciona reles.
bool canStart(String& err) {
  if (g_otaInProgress) { err = "ota_in_progress"; return false; }
  if (setpointBusy() || keypadBusy()) { err = "busy"; return false; }
  return true;
}

bool resolveBase(float& base, String& err) {
  if (g_cfg.spSource == SP_SOURCE_DISPLAY) {
    if (!displaySpValid()) { err = "display_invalid"; return false; }
    base = displaySp();
    return true;
  }
  if (!g_spKnown) { err = "sp_unknown"; return false; }
  base = g_spShadow;
  return true;
}

// Hold so no modo display: sem leitura nao ha como saber quantos toques a
// auto-repeticao do C404 contou.
bool holdApplicable(long distance) {
  return g_cfg.spSource == SP_SOURCE_DISPLAY && g_cfg.holdEnabled && labs(distance) >= g_cfg.holdMinSteps;
}

// Onde o SP esta agora, na melhor fonte disponivel: leitura estavel, depois ao
// vivo, depois a sombra. A estavel guarda o ultimo valor que ficou parado - logo
// apos um hold, o de antes dele - entao so conta como assentada quando a leitura
// ao vivo concorda com ela. Devolve se alguma leitura do display foi usada.
bool currentSp(float& sp, bool& settled) {
  float live;
  const bool liveOk = displayLiveSp(live);
  if (displaySpValid()) {
    const float stable = displaySp();
    if (!liveOk || fabsf(stable - live) <= g_cfg.stepC * 0.5f) { sp = stable; settled = true; return true; }
  }
  settled = false;
  if (liveOk) { sp = live; return true; }
  sp = g_spShadow;
  return false;
}

void onKeyPressed(Key key) {
  if (g_kind == KIND_RAW) {
    if (key == KEY_UP || key == KEY_DOWN || key == KEY_STAR) g_rawTouchedSp = true;
    return;
  }
  if (key == KEY_UP)   g_spShadow = clampSp(quantize(g_spShadow + g_cfg.stepC));
  if (key == KEY_DOWN) g_spShadow = clampSp(quantize(g_spShadow - g_cfg.stepC));
}

void startHold(long remaining, unsigned long now) {
  const Key key = remaining > 0 ? KEY_UP : KEY_DOWN;
  const long distance = labs(remaining);
  const uint32_t cap = static_cast<uint32_t>(distance) * (g_cfg.pressMs + g_cfg.gapMs) + HOLD_CAP_EXTRA_MS;
  if (!keypadEnqueueHold(key, cap)) { failQueue(); return; }
  g_phase = PHASE_HOLD;
  g_phaseSinceMs = now;
  g_holdDir = remaining > 0 ? 1 : -1;
  g_holdLastSp = g_spShadow;
  g_holdLastChangeMs = now;
  g_holdBlindSinceMs = 0;
  g_holdChanges = 0;
  g_holdRate = 0.0f;
  ++g_holdRounds;
  Serial.printf("[SP] Hold %s: faltam %ld toque(s) (rodada %u, teto %lu ms)\n",
                keyName(key), distance, g_holdRounds, static_cast<unsigned long>(cap));
}

void endHold(const char* why, unsigned long now) {
  Serial.printf("[SP] Solta apos %lu ms (%s): sombra=%.2f alvo=%.2f taxa=%.1f toques/s\n",
                keypadHoldMs(now), why, g_spShadow, g_spTarget, g_holdRate);
  keypadRelease();
  g_phase = PHASE_HOLD_SETTLE;
  g_phaseSinceMs = now;
}

// Malha fechada do hold: acompanha o SP ao vivo, mede a taxa de auto-repeticao e
// solta quando o que falta cabe na folga (`hold_stop_steps` mais o que a taxa
// medida anda durante `hold_lag_ms`). O que sobrar vai a toques discretos.
void serviceHold(unsigned long now) {
  if (!keypadHolding()) {
    // Ainda na fila (o motor pega o passo na proxima volta) ou solto pelo teto.
    if (keypadBusy() && now - g_phaseSinceMs < 100) return;
    Serial.println("[SP] Hold encerrado pelo teto do motor.");
    g_phase = PHASE_HOLD_SETTLE;
    g_phaseSinceMs = now;
    return;
  }

  float sp;
  if (displayLiveSp(sp)) {
    g_holdBlindSinceMs = 0;
    if (fabsf(sp - g_holdLastSp) > g_cfg.stepC * 0.5f) {
      // A primeira mudanca e o toque em si, nao uma repeticao: fica fora da taxa.
      const unsigned long dt = now - g_holdLastChangeMs;
      if (++g_holdChanges >= 2 && dt > 0) {
        float inst = fabsf(sp - g_holdLastSp) / g_cfg.stepC * 1000.0f / static_cast<float>(dt);
        if (inst > HOLD_RATE_CAP_SPS) inst = HOLD_RATE_CAP_SPS;
        g_holdRate = g_holdRate > 0.0f ? 0.5f * (g_holdRate + inst) : inst;
      }
      g_holdLastSp = sp;
      g_holdLastChangeMs = now;
      g_spShadow = sp;
    }
    // Toques que ainda faltam no sentido do hold; negativo = ja passou do alvo.
    const float remaining = g_holdDir * (g_spTarget - sp) / g_cfg.stepC;
    const float margin = g_cfg.holdStopSteps + g_holdRate * g_cfg.holdLagMs / 1000.0f;
    if (remaining <= margin) { endHold("perto do alvo", now); return; }
  } else {
    if (g_holdBlindSinceMs == 0) {
      g_holdBlindSinceMs = now;
    } else if (now - g_holdBlindSinceMs >= HOLD_BLIND_MS) {
      g_holdDisabled = true;
      endHold("display ilegivel", now);
      return;
    }
  }

  if (now - g_holdLastChangeMs >= g_cfg.holdStallMs) {
    g_holdDisabled = true;
    endHold("display parado; sem auto-repeticao?", now);
  }
}

// Decide a proxima perna a partir de onde o SP esta: outro hold se ainda esta
// longe, senao os toques que faltam e a confirmacao.
void planNextLeg(unsigned long now) {
  float sp;
  bool settled;
  const bool seen = currentSp(sp, settled);
  if (!settled && now - g_phaseSinceMs < PLAN_WAIT_MS) return;
  if (seen) g_spShadow = sp;

  const long remaining = stepsBetween(g_spShadow, g_spTarget);
  const bool canHold = g_planHold && seen && !g_holdDisabled && g_holdRounds < MAX_HOLD_ROUNDS &&
                       labs(remaining) >= g_cfg.holdMinSteps;
  if (canHold) {
    float live;
    if (displayLiveSp(live)) { startHold(remaining, now); return; }
    if (now - g_phaseSinceMs < PLAN_WAIT_MS) return;
  }

  if (labs(remaining) > MAX_PRESSES) {
    keypadClear();
    finishSequence(SEQ_ERROR, "too_many_presses");
    return;
  }
  if (!enqueuePresses(remaining) || !enqueueRole(g_cfg.confirmKey, 0)) { failQueue(); return; }
  g_phase = PHASE_PRESSES;
  g_phaseSinceMs = now;
  Serial.printf("[SP] Perna a toques: %ld de %s (sombra %.2f -> alvo %.2f)\n",
                labs(remaining), remaining >= 0 ? "up" : "down", g_spShadow, g_spTarget);
}

void finalizeSetpoint(unsigned long now) {
  if (g_cfg.spSource == SP_SOURCE_DISPLAY) {
    if (!displaySpValid()) {
      g_spKnown = false;
      finishSequence(SEQ_ERROR, "display_invalid_after_sequence");
      return;
    }
    g_spShadow = displaySp();
    g_spKnown = true;
    const long residual = stepsBetween(g_spShadow, g_spTarget);
    if (residual != 0) {
      if (g_corrections < MAX_CORRECTIONS && labs(residual) <= MAX_CORRECTION_STEPS) {
        ++g_corrections;
        Serial.printf("[SP] Display em %.2f, alvo %.2f: correcao %u (%ld toque(s))\n",
                      g_spShadow, g_spTarget, g_corrections, residual);
        g_state = SEQ_RUNNING;
        g_phase = PHASE_ENTER;
        g_phaseSinceMs = now;
        if (!enqueueRole(g_cfg.enterKey, g_cfg.menuMs)) failQueue();
        return;
      }
      finishSequence(SEQ_ERROR, "sp_mismatch");
      return;
    }
  } else {
    g_spKnown = true;
  }
  finishSequence(SEQ_DONE, nullptr);
}

void serviceRunning(unsigned long now) {
  switch (g_phase) {
    case PHASE_ENTER:
      if (keypadBusy()) return;
      g_phase = PHASE_PLAN;
      g_phaseSinceMs = now;
      return;

    case PHASE_PLAN:
      planNextLeg(now);
      return;

    case PHASE_HOLD:
      serviceHold(now);
      return;

    case PHASE_HOLD_SETTLE:
      if (keypadBusy() || now - g_phaseSinceMs < g_cfg.holdSettleMs) return;
      g_phase = PHASE_PLAN;
      g_phaseSinceMs = now;
      return;

    default:
      if (keypadBusy()) return;
      g_state = SEQ_SETTLING;
      g_settleStartMs = now;
      return;
  }
}
}  // namespace

void setpointInit() {
  keypadSetOnPress(onKeyPressed);
}

bool setpointRequestAbsolute(float sp, String& err) {
  if (!canStart(err)) return false;
  if (isnan(sp) || sp < g_cfg.spMin || sp > g_cfg.spMax) { err = "range"; return false; }
  float base;
  if (!resolveBase(base, err)) return false;

  const float target = quantize(sp);
  const long presses = stepsBetween(base, target);
  if (labs(presses) > MAX_PRESSES) { err = "too_many_presses"; return false; }

  resetPlan();
  g_spShadow = quantize(base);
  g_spTarget = target;
  if (presses == 0) {
    g_kind = KIND_SETPOINT;
    g_phase = PHASE_NONE;
    g_lastError = "";
    g_state = SEQ_DONE;
    Serial.printf("[SP] Alvo %.2f ja vigente; nada a fazer.\n", target);
    return true;
  }

  g_plannedPresses = presses;
  g_planHold = holdApplicable(presses);
  beginSequence(KIND_SETPOINT, g_planHold ? PHASE_ENTER : PHASE_PRESSES);
  // Com hold, as setas e a confirmacao so entram na fila depois da tecla de
  // entrada, quando o planejamento le o display; sem hold, tudo de uma vez.
  bool ok = enqueueRole(g_cfg.enterKey, g_cfg.menuMs);
  if (ok && !g_planHold) ok = enqueuePresses(presses) && enqueueRole(g_cfg.confirmKey, 0);
  if (!ok) {
    failQueue();
    err = "queue_full";
    return false;
  }
  Serial.printf("[SP] %.2f -> %.2f: %ld toque(s) de %s%s\n", base, target, labs(presses),
                presses > 0 ? "up" : "down", g_planHold ? " (com hold)" : "");
  return true;
}

bool setpointRequestDelta(float delta, String& err) {
  float base;
  if (!resolveBase(base, err)) return false;
  return setpointRequestAbsolute(base + delta, err);
}

bool setpointSync(float sp, String& err) {
  if (setpointBusy() || keypadBusy()) { err = "busy"; return false; }
  if (isnan(sp) || sp < g_cfg.spMin || sp > g_cfg.spMax) { err = "range"; return false; }
  g_spShadow = quantize(sp);
  g_spTarget = g_spShadow;
  g_spKnown = true;
  g_state = SEQ_IDLE;
  g_phase = PHASE_NONE;
  g_lastError = "";
  saveNvsState(false);
  Serial.printf("[SP] Sombra sincronizada em %.2f\n", g_spShadow);
  return true;
}

// "Home": satura o SP em in.L (spMin) com mais toques de ▼ do que a faixa inteira
// exige, o que torna o valor conhecido sem nenhuma leitura. Depende de o C404
// travar em in.L em vez de dar a volta; ver docs/VALIDATION.md.
bool setpointHome(bool hasTarget, float target, String& err) {
  if (!canStart(err)) return false;
  if (hasTarget && (isnan(target) || target < g_cfg.spMin || target > g_cfg.spMax)) { err = "range"; return false; }

  const long span = lroundf((g_cfg.spMax - g_cfg.spMin) / g_cfg.stepC) + g_cfg.homeMargin;
  const long up = hasTarget ? lroundf((quantize(target) - g_cfg.spMin) / g_cfg.stepC) : 0;
  if (span <= 0 || span + up > MAX_PRESSES) { err = "too_many_presses"; return false; }

  // Parte do pior caso (spMax): apos `span` toques o clamp interno chega a spMin,
  // igual ao que o C404 faz.
  resetPlan();
  g_spShadow = g_cfg.spMax;
  g_spTarget = hasTarget ? quantize(target) : g_cfg.spMin;

  beginSequence(KIND_HOME, PHASE_PRESSES);
  bool ok = enqueueRole(g_cfg.enterKey, g_cfg.menuMs);
  // Fatias de ate 65535 toques por passo; span cabe em um unico passo.
  ok = ok && keypadEnqueue(KEY_DOWN, static_cast<uint16_t>(span));
  if (up > 0) ok = ok && keypadEnqueue(KEY_UP, static_cast<uint16_t>(up));
  ok = ok && enqueueRole(g_cfg.confirmKey, 0);
  if (!ok) {
    failQueue();
    err = "queue_full";
    return false;
  }
  Serial.printf("[SP] Home: %ld toques de down, depois %ld de up (alvo %.2f)\n", span, up, g_spTarget);
  return true;
}

// Toques crus para bancada. Nao atualizam a sombra: * e as setas podem ou nao
// mudar o SP conforme a tela do C404, entao no modo sombra a sequencia termina
// com g_spKnown = false se alguma dessas teclas foi usada.
bool setpointRawPress(Key key, uint16_t count, String& err) {
  if (!canStart(err)) return false;
  if (count == 0 || count > MAX_PRESSES) { err = "range"; return false; }
  resetPlan();
  beginSequence(KIND_RAW, PHASE_PRESSES);
  if (!keypadEnqueue(key, count)) {
    failQueue();
    err = "queue_full";
    return false;
  }
  Serial.printf("[SP] Toque cru: %s x%u\n", keyName(key), count);
  return true;
}

// Tecla mantida por um tempo fixo, para medir na bancada o atraso e a taxa da
// auto-repeticao do C404 (e o que o manual chama de tecla mantida em * e ENTER).
bool setpointRawHold(Key key, uint32_t holdMs, String& err) {
  if (!canStart(err)) return false;
  if (holdMs == 0 || holdMs > RAW_HOLD_MAX_MS) { err = "range"; return false; }
  resetPlan();
  beginSequence(KIND_RAW, PHASE_PRESSES);
  // O callback por toque nao dispara em hold; a marca vai aqui.
  if (key == KEY_UP || key == KEY_DOWN || key == KEY_STAR) g_rawTouchedSp = true;
  if (!keypadEnqueueHold(key, holdMs)) {
    failQueue();
    err = "queue_full";
    return false;
  }
  Serial.printf("[SP] Hold cru: %s por %lu ms\n", keyName(key), static_cast<unsigned long>(holdMs));
  return true;
}

void setpointAbort() {
  const bool wasBusy = setpointBusy() || keypadBusy();
  keypadClear();
  if (!wasBusy) return;
  if (g_cfg.spSource == SP_SOURCE_SHADOW) g_spKnown = false;
  finishSequence(SEQ_ABORTED, "aborted");
}

void setpointService(unsigned long now) {
  switch (g_state) {
    case SEQ_RUNNING:
      serviceRunning(now);
      return;

    case SEQ_SETTLING:
      if (now - g_settleStartMs < g_cfg.settleMs) return;
      if (g_kind == KIND_RAW) {
        if (g_cfg.spSource == SP_SOURCE_SHADOW && g_rawTouchedSp) g_spKnown = false;
        if (g_cfg.spSource == SP_SOURCE_DISPLAY && displaySpValid()) { g_spShadow = displaySp(); g_spKnown = true; }
        finishSequence(SEQ_DONE, nullptr);
        return;
      }
      finalizeSetpoint(now);
      return;

    default:
      break;
  }

  // Em repouso e no modo display, a sombra segue o display: intervencao manual e
  // absorvida sem nenhum comando. Grava so quando o valor muda.
  if (g_cfg.spSource == SP_SOURCE_DISPLAY && displaySpValid()) {
    const float seen = displaySp();
    if (!g_spKnown || fabsf(seen - g_spShadow) > g_cfg.stepC * 0.5f) {
      g_spShadow = seen;
      g_spKnown = true;
      saveNvsState(false);
    }
  }
}

SeqState setpointState() { return g_state; }
SeqKind setpointKind() { return g_kind; }
SeqPhase setpointPhase() { return g_phase; }
const String& setpointLastError() { return g_lastError; }
bool setpointBusy() { return g_state == SEQ_RUNNING || g_state == SEQ_SETTLING; }
long setpointPlannedPresses() { return g_plannedPresses; }
bool setpointPlannedHold() { return g_planHold; }
uint8_t setpointHoldRounds() { return g_holdRounds; }
float setpointHoldRateStepsPerS() { return g_holdRate; }

const char* setpointStateName() {
  switch (g_state) {
    case SEQ_IDLE:     return "idle";
    case SEQ_RUNNING:  return "running";
    case SEQ_SETTLING: return "settling";
    case SEQ_DONE:     return "done";
    case SEQ_ERROR:    return "error";
    case SEQ_ABORTED:  return "aborted";
  }
  return "?";
}

const char* setpointKindName() {
  switch (g_kind) {
    case KIND_NONE:     return "none";
    case KIND_SETPOINT: return "setpoint";
    case KIND_HOME:     return "home";
    case KIND_RAW:      return "raw";
  }
  return "?";
}

const char* setpointPhaseName() {
  switch (g_phase) {
    case PHASE_NONE:        return "";
    case PHASE_ENTER:       return "enter";
    case PHASE_PLAN:        return "plan";
    case PHASE_HOLD:        return "hold";
    case PHASE_HOLD_SETTLE: return "hold_settle";
    case PHASE_PRESSES:     return "presses";
  }
  return "?";
}
