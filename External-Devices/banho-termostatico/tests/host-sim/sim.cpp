// Host simulation: the real SetpointManager + SetpointGuard + KeyPresser + KeySense
// + AppContext driving a modelled Contemp C404 (auto-repeat with initial delay,
// optional acceleration, dropped presses, unreadable-display windows, operator
// pressing keys) through a modelled DisplayReader (frame-coherent live value with
// latency; stable value that lags 350 ms and holds the previous stable value while
// the digits are moving). Key-sense lines are driven from relay state + operator.
#include <Arduino.h>

#include <deque>
#include <functional>
#include <utility>
#include <vector>

#include "config/BoardConfig.h"
#include "core/AppContext.h"
#include "display/DisplayReader.h"
#include "keypad/KeyPresser.h"
#include "keypad/KeySense.h"
#include "setpoint/SetpointGuard.h"
#include "setpoint/SetpointManager.h"
#include "storage/NvsConfig.h"

// ---------------------------------------------------------------- Arduino stubs
bool g_serialVerbose = false;
SerialClass Serial;
static unsigned long g_now = 0;
static int g_pinLevel[64];
unsigned long millis() { return g_now; }
void digitalWrite(int pin, int level) { g_pinLevel[pin] = level; }
int digitalRead(int pin) { return g_pinLevel[pin]; }
void pinMode(int, int) {}
void noInterrupts() {}
void interrupts() {}

// ---------------------------------------------------------------- C404 model
struct C404 {
  float sp = 30.0f;
  float step = 0.1f;
  float lo = 5.0f, hi = 90.0f;
  bool autoRepeat = true;
  unsigned long debounceMs = 20;         // key down -> first increment
  unsigned long repeatDelayMs = 600;     // hold -> repeats begin
  unsigned long repeatPeriodMs = 100;    // 10 steps/s
  unsigned long accelAfterMs = 0;        // 0 = no acceleration
  unsigned long fastPeriodMs = 100;
  float fastStep = 0.1f;
  unsigned long releaseTailMs = 0;       // repeats keep firing this long after release
  int dropEveryNth = 0;                  // every Nth discrete press is ignored
  unsigned long blindFrom = 0, blindTo = 0;   // window where SP digits are unreadable
  bool userUp = false, userDown = false, userStar = false;   // operator's fingers

  bool wasClosed = false;
  int dir = 0;
  unsigned long closedSince = 0, releasedAt = 0, lastRepeat = 0;
  bool pressRegistered = false;
  bool repeatsStarted = false;
  int pressCount = 0;
  int minSteps = 0, maxSteps = 0;        // excursion tracking (in steps from start)
  float startSp = 0;
  unsigned long lastChangeMs = 0;
  std::deque<std::pair<unsigned long, float>> history;

  bool readable(unsigned long now) const { return !(blindTo > blindFrom && now >= blindFrom && now < blindTo); }

  void apply(float d, unsigned long now) {
    float n = sp + d;
    if (n < lo) n = lo;
    if (n > hi) n = hi;
    n = roundf(n / step) * step;
    if (fabsf(n - sp) < step * 0.5f) return;
    sp = n;
    lastChangeMs = now;
    history.push_back({now, sp});
    const int s = lroundf((sp - startSp) / step);
    if (s < minSteps) minSteps = s;
    if (s > maxSteps) maxSteps = s;
  }

  void tick(unsigned long now) {
    const bool up = g_pinLevel[BoardConfig::RelayUpPin] == LOW || userUp;
    const bool down = g_pinLevel[BoardConfig::RelayDownPin] == LOW || userDown;
    const bool closed = up || down;
    if (closed && !wasClosed) {
      closedSince = now;
      pressRegistered = false;
      repeatsStarted = false;
      lastRepeat = 0;
      dir = up ? 1 : -1;
    }
    if (!closed && wasClosed) releasedAt = now;
    // A late release detection only prolongs a repeat stream that already started.
    const bool repeating = closed || (releaseTailMs && repeatsStarted && wasClosedRecently(now));
    if (closed && !pressRegistered && now - closedSince >= debounceMs) {
      pressRegistered = true;
      ++pressCount;
      if (!(dropEveryNth && pressCount % dropEveryNth == 0)) apply(dir * step, now);
      lastRepeat = now;
    } else if (repeating && pressRegistered && autoRepeat && now - closedSince >= repeatDelayMs) {
      const bool fast = accelAfterMs && now - closedSince >= accelAfterMs;
      const unsigned long period = fast ? fastPeriodMs : repeatPeriodMs;
      if (now - lastRepeat >= period) {
        apply(dir * (fast ? fastStep : step), now);
        lastRepeat = now;
        repeatsStarted = true;
      }
    }
    wasClosed = closed;
  }
  bool wasClosedRecently(unsigned long now) const { return releasedAt && now - releasedAt < releaseTailMs; }

  float spAt(unsigned long t) const {
    float v = startSp;
    for (const auto& h : history) { if (h.first <= t) v = h.second; else break; }
    return v;
  }
};
static C404* g_c404 = nullptr;
static unsigned long g_liveLatencyMs = 20;   // two coherent scans + decode
static unsigned long g_stableLagMs = 350;    // 3 decodes at 100 ms, worst phase

// ---------------------------------------------------------------- DisplayReader stub
static float g_stableSp = 0;
static bool g_stableValid = false;
void displayInit() {}
void displayService(unsigned long now) {
  // Stable filter: takes the current value once it has sat unchanged for the lag,
  // and (like the real reader) keeps the previous stable value until then.
  if (!g_c404->readable(now)) { g_stableValid = false; return; }
  if (now - g_c404->lastChangeMs >= g_stableLagMs) { g_stableSp = g_c404->sp; g_stableValid = true; }
}
bool displayPvValid() { return false; }
float displayPv() { return 0; }
bool displaySpValid() { return g_stableValid; }
float displaySp() { return g_stableSp; }
bool displayAlive() { return true; }
uint32_t displayFrameCount() { return 0; }
uint32_t displayLiveFrameCount() { return 0; }
uint8_t displayRawSegments(uint8_t) { return 0; }
String displayText() { return String(); }
bool displayLiveSp(float& out) {
  if (!g_c404->readable(g_now)) return false;
  out = g_c404->spAt(g_now >= g_liveLatencyMs ? g_now - g_liveLatencyMs : 0);
  return true;
}

// ---------------------------------------------------------------- NVS stubs
static int g_nvsBusyWrites = 0, g_nvsWrites = 0;
static uint32_t g_corrBase = 0;
static uint32_t corrections() { return guardCorrections() - g_corrBase; }
void loadNvsConfig() {}
void saveNvsConfig() {}
void resetNvsConfig() {}
void loadNvsState() {}
void saveNvsState(bool busy) { ++g_nvsWrites; if (busy) ++g_nvsBusyWrites; }

// Key-sense lines: LOW when the relay or the operator closes the key.
static void driveSenseLines(const C404& c) {
  g_pinLevel[BoardConfig::SensePins[KEY_STAR]]  = (g_pinLevel[BoardConfig::RelayStarPin] == LOW || c.userStar) ? LOW : HIGH;
  g_pinLevel[BoardConfig::SensePins[KEY_UP]]    = (g_pinLevel[BoardConfig::RelayUpPin] == LOW || c.userUp) ? LOW : HIGH;
  g_pinLevel[BoardConfig::SensePins[KEY_DOWN]]  = (g_pinLevel[BoardConfig::RelayDownPin] == LOW || c.userDown) ? LOW : HIGH;
  g_pinLevel[BoardConfig::SensePins[KEY_ENTER]] = g_pinLevel[BoardConfig::RelayEnterPin] == LOW ? LOW : HIGH;
}

// ---------------------------------------------------------------- harness
static int g_failures = 0;
#define CHECK(cond, ...) do { if (!(cond)) { ++g_failures; printf("    FAIL: %s  ", #cond); printf(__VA_ARGS__); printf("\n"); } } while (0)

struct Outcome {
  SeqState state;
  String err;
  unsigned long elapsedMs;
  float sp;
  int holds;
  unsigned long maxRelayClosedMs;
  int relayCloses;
};

static void resetWorld(C404& c, float startSp) {
  g_now = 1000;
  for (int i = 0; i < 64; ++i) g_pinLevel[i] = HIGH;
  keypadInit();
  keypadClear();
  g_cfg = BathConfig();
  g_cfg.enterKey = ROLE_NONE;      // manual §7.1: arrows act on the main screen
  g_cfg.confirmKey = ROLE_NONE;
  g_cfg.spSource = SP_SOURCE_DISPLAY;
  c.sp = startSp; c.startSp = startSp; c.minSteps = c.maxSteps = 0; c.history.clear();
  c.lastChangeMs = 0; c.wasClosed = false; c.pressCount = 0; c.releasedAt = 0;
  g_c404 = &c;
  g_stableSp = startSp; g_stableValid = true;
  g_spShadow = startSp; g_spKnown = true; g_spTarget = startSp;
  g_nvsBusyWrites = g_nvsWrites = 0;
  g_manualPressCount = 0; g_manualActivityMs = 0;
  c.userUp = c.userDown = c.userStar = false;
  String err;
  setpointSync(startSp, err);
  guardSetMode(MODE_MANUAL, "sim");
  keySenseInit();
  g_corrBase = guardCorrections();
}

// Runs the loop for a fixed time (guard scenarios do not end on their own).
static void runFor(C404& c, unsigned long ms, std::function<void(unsigned long)> hook = nullptr) {
  const unsigned long t0 = g_now;
  while (g_now - t0 < ms) {
    ++g_now;
    if (hook) hook(g_now - t0);
    keypadService(g_now);
    c.tick(g_now);
    driveSenseLines(c);
    keySenseService(g_now);
    displayService(g_now);
    setpointService(g_now);
    guardService(g_now);
    int nClosed = (g_pinLevel[4] == LOW) + (g_pinLevel[5] == LOW) + (g_pinLevel[6] == LOW) + (g_pinLevel[7] == LOW);
    CHECK(nClosed <= 1, "two relays closed at once");
  }
}

static Outcome runUntilIdle(C404& c, unsigned long maxMs, std::function<void(unsigned long)> hook = nullptr) {
  const unsigned long t0 = g_now;
  Outcome o{};
  bool closed = false;
  unsigned long closedSince = 0;
  while (g_now - t0 < maxMs) {
    ++g_now;
    if (hook) hook(g_now - t0);
    keypadService(g_now);
    c.tick(g_now);
    driveSenseLines(c);
    keySenseService(g_now);
    displayService(g_now);
    setpointService(g_now);
    guardService(g_now);
    const bool anyClosed = g_pinLevel[4] == LOW || g_pinLevel[5] == LOW || g_pinLevel[6] == LOW || g_pinLevel[7] == LOW;
    int nClosed = (g_pinLevel[4] == LOW) + (g_pinLevel[5] == LOW) + (g_pinLevel[6] == LOW) + (g_pinLevel[7] == LOW);
    CHECK(nClosed <= 1, "two relays closed at once");
    if (anyClosed && !closed) { closed = true; closedSince = g_now; ++o.relayCloses; }
    if (!anyClosed && closed) { closed = false; if (g_now - closedSince > o.maxRelayClosedMs) o.maxRelayClosedMs = g_now - closedSince; }
    if (!setpointBusy() && !keypadBusy()) break;
  }
  o.state = setpointState();
  o.err = setpointLastError();
  o.elapsedMs = g_now - t0;
  o.sp = c.sp;
  o.holds = setpointHoldRounds();
  return o;
}

static const char* stateName(SeqState s) {
  switch (s) { case SEQ_IDLE: return "idle"; case SEQ_RUNNING: return "running"; case SEQ_SETTLING: return "settling";
               case SEQ_DONE: return "done"; case SEQ_ERROR: return "error"; case SEQ_ABORTED: return "aborted"; }
  return "?";
}

static void report(const char* name, const Outcome& o, const C404& c) {
  printf("  %-52s %-8s %-30s sp=%.1f t=%6lu ms holds=%d excursion=[%d,%d] longest relay=%lu ms closes=%d\n",
         name, stateName(o.state), o.err.c_str(), o.sp, o.elapsedMs, o.holds, c.minSteps, c.maxSteps,
         o.maxRelayClosedMs, o.relayCloses);
}

static unsigned long discreteEstimateMs(long steps) {
  return static_cast<unsigned long>(labs(steps)) * (g_cfg.pressMs + g_cfg.gapMs) + g_cfg.settleMs;
}

int main(int argc, char** argv) {
  g_serialVerbose = argc > 1 && strcmp(argv[1], "-v") == 0;
  setpointInit();
  String err;
  C404 c;

  printf("== A. hold, +15 steps, C404 10 steps/s after 600 ms\n");
  resetWorld(c, 30.0f);
  CHECK(setpointRequestAbsolute(31.5f, err), "%s", err.c_str());
  CHECK(setpointPlannedHold(), "plan should use hold");
  { Outcome o = runUntilIdle(c, 60000); report("A", o, c);
    CHECK(o.state == SEQ_DONE, "state=%s err=%s", stateName(o.state), o.err.c_str());
    CHECK(fabsf(o.sp - 31.5f) < 0.01f, "sp=%.2f", o.sp);
    CHECK(o.holds == 1, "holds=%d", o.holds);
    CHECK(o.elapsedMs < discreteEstimateMs(15), "hold slower than %lu ms discrete", discreteEstimateMs(15));
    CHECK(c.maxSteps <= 15, "overshoot to %d", c.maxSteps);
    CHECK(fabsf(g_spShadow - 31.5f) < 0.01f && g_spKnown, "shadow=%.2f known=%d", g_spShadow, g_spKnown); }

  printf("== B. hold, +300 steps (30 -> 60), 10 steps/s\n");
  resetWorld(c, 30.0f);
  CHECK(setpointRequestAbsolute(60.0f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 200000); report("B", o, c);
    CHECK(o.state == SEQ_DONE, "state=%s err=%s", stateName(o.state), o.err.c_str());
    CHECK(fabsf(o.sp - 60.0f) < 0.01f, "sp=%.2f", o.sp);
    CHECK(o.elapsedMs < 40000, "took %lu ms", o.elapsedMs);
    CHECK(c.maxSteps <= 300, "overshoot to %d", c.maxSteps); }

  printf("== C. hold, +300 steps, accelerates to 50 steps/s after 2 s\n");
  resetWorld(c, 30.0f);
  c.accelAfterMs = 2000; c.fastPeriodMs = 20;
  CHECK(setpointRequestAbsolute(60.0f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 200000); report("C", o, c);
    CHECK(o.state == SEQ_DONE, "state=%s err=%s", stateName(o.state), o.err.c_str());
    CHECK(fabsf(o.sp - 60.0f) < 0.01f, "sp=%.2f", o.sp);
    CHECK(o.elapsedMs < 15000, "took %lu ms", o.elapsedMs); }
  c.accelAfterMs = 0; c.fastPeriodMs = 100;

  printf("== D. hold, +300 steps, step grows to 1.0 C every 100 ms after 2 s (100 steps/s)\n");
  resetWorld(c, 30.0f);
  c.accelAfterMs = 2000; c.fastPeriodMs = 100; c.fastStep = 1.0f;
  CHECK(setpointRequestAbsolute(60.0f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 200000); report("D", o, c);
    CHECK(o.state == SEQ_DONE, "state=%s err=%s", stateName(o.state), o.err.c_str());
    CHECK(fabsf(o.sp - 60.0f) < 0.01f, "sp=%.2f", o.sp);
    CHECK(c.maxSteps <= 300 + 12, "overshoot to %d", c.maxSteps); }
  c.accelAfterMs = 0; c.fastStep = 0.1f;

  printf("== E. no auto-repeat: stall, then discrete\n");
  resetWorld(c, 30.0f);
  c.autoRepeat = false;
  CHECK(setpointRequestAbsolute(31.5f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("E", o, c);
    CHECK(o.state == SEQ_DONE, "state=%s err=%s", stateName(o.state), o.err.c_str());
    CHECK(fabsf(o.sp - 31.5f) < 0.01f, "sp=%.2f", o.sp);
    CHECK(o.holds == 1, "holds=%d (should give up after one stalled hold)", o.holds); }
  c.autoRepeat = true;

  printf("== F. shadow mode: discrete exactly as before\n");
  resetWorld(c, 30.0f);
  g_cfg.spSource = SP_SOURCE_SHADOW; g_cfg.enterKey = ROLE_STAR; g_cfg.confirmKey = ROLE_ENTER;
  CHECK(setpointRequestAbsolute(31.5f, err), "%s", err.c_str());
  CHECK(!setpointPlannedHold(), "no hold in shadow mode");
  CHECK(keypadPressesTotal() == 17, "queued %lu presses (want 15 + * + ENTER)", (unsigned long)keypadPressesTotal());
  { Outcome o = runUntilIdle(c, 60000); report("F", o, c);
    CHECK(o.state == SEQ_DONE, "state=%s err=%s", stateName(o.state), o.err.c_str());
    CHECK(fabsf(o.sp - 31.5f) < 0.01f, "sp=%.2f", o.sp);
    CHECK(o.holds == 0 && o.relayCloses == 17, "holds=%d closes=%d", o.holds, o.relayCloses);
    CHECK(o.maxRelayClosedMs <= static_cast<unsigned long>(g_cfg.pressMs) + 1, "longest press %lu ms", o.maxRelayClosedMs);
    CHECK(fabsf(g_spShadow - 31.5f) < 0.01f && g_spKnown, "shadow=%.2f", g_spShadow); }

  printf("== G. display mode, hold disabled: discrete\n");
  resetWorld(c, 30.0f);
  g_cfg.holdEnabled = 0;
  CHECK(setpointRequestAbsolute(31.5f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("G", o, c);
    CHECK(o.state == SEQ_DONE && o.holds == 0 && o.relayCloses == 15, "state=%s holds=%d closes=%d", stateName(o.state), o.holds, o.relayCloses); }

  printf("== H. display goes unreadable 1.0-4.0 s into the hold\n");
  resetWorld(c, 30.0f);
  c.blindFrom = g_now + 1000; c.blindTo = g_now + 4000;
  CHECK(setpointRequestAbsolute(35.0f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("H", o, c);
    CHECK(o.state == SEQ_DONE, "state=%s err=%s", stateName(o.state), o.err.c_str());
    CHECK(fabsf(o.sp - 35.0f) < 0.01f, "sp=%.2f", o.sp);
    CHECK(o.holds == 1, "holds=%d (blind hold must not be retried)", o.holds); }
  c.blindFrom = c.blindTo = 0;

  printf("== I. C404 drops every 7th discrete press: mismatch -> one correction\n");
  resetWorld(c, 30.0f);
  c.dropEveryNth = 7; g_cfg.holdEnabled = 0;
  CHECK(setpointRequestAbsolute(31.5f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("I", o, c);
    CHECK(o.state == SEQ_DONE, "state=%s err=%s", stateName(o.state), o.err.c_str());
    CHECK(fabsf(o.sp - 31.5f) < 0.01f, "sp=%.2f", o.sp); }
  c.dropEveryNth = 0;

  printf("== I2. every press dropped: correction fails -> sp_mismatch, not endless\n");
  resetWorld(c, 30.0f);
  c.dropEveryNth = 1; g_cfg.holdEnabled = 0;
  CHECK(setpointRequestAbsolute(30.5f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("I2", o, c);
    CHECK(o.state == SEQ_ERROR && o.err == "sp_mismatch", "state=%s err=%s", stateName(o.state), o.err.c_str());
    CHECK(o.relayCloses == 10, "closes=%d (5 + 5 correction)", o.relayCloses); }
  c.dropEveryNth = 0;

  printf("== J. abort in the middle of a hold\n");
  resetWorld(c, 30.0f);
  CHECK(setpointRequestAbsolute(60.0f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000, [](unsigned long t) { if (t == 2000) setpointAbort(); }); report("J", o, c);
    CHECK(o.state == SEQ_ABORTED, "state=%s", stateName(o.state));
    CHECK(o.elapsedMs <= 2001, "relays not opened promptly: %lu", o.elapsedMs);
    CHECK(g_pinLevel[5] == HIGH && g_pinLevel[6] == HIGH, "relay still closed after abort");
    CHECK(g_spKnown, "display mode keeps sp_known after abort"); }

  printf("== K. raw hold up 2000 ms\n");
  resetWorld(c, 30.0f);
  CHECK(setpointRawHold(KEY_UP, 2000, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("K", o, c);
    CHECK(o.state == SEQ_DONE, "state=%s", stateName(o.state));
    CHECK(o.maxRelayClosedMs >= 2000 && o.maxRelayClosedMs <= 2002, "held %lu ms", o.maxRelayClosedMs);
    CHECK(c.maxSteps >= 14 && c.maxSteps <= 16, "C404 model moved %d steps (1 + ~14 repeats)", c.maxSteps); }

  printf("== L. downward -40 steps (30 -> 26)\n");
  resetWorld(c, 30.0f);
  CHECK(setpointRequestAbsolute(26.0f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("L", o, c);
    CHECK(o.state == SEQ_DONE && fabsf(o.sp - 26.0f) < 0.01f, "state=%s sp=%.2f", stateName(o.state), o.sp);
    CHECK(c.minSteps >= -40, "undershoot to %d", c.minSteps); }

  printf("== M. hold into the in.H clamp (88 -> 90)\n");
  resetWorld(c, 88.0f);
  CHECK(setpointRequestAbsolute(90.0f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("M", o, c);
    CHECK(o.state == SEQ_DONE && fabsf(o.sp - 90.0f) < 0.01f, "state=%s sp=%.2f", stateName(o.state), o.sp); }

  printf("== N. enter_key=* and confirm_key=ENTER with hold\n");
  resetWorld(c, 30.0f);
  g_cfg.enterKey = ROLE_STAR; g_cfg.confirmKey = ROLE_ENTER;
  CHECK(setpointRequestAbsolute(35.0f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("N", o, c);
    CHECK(o.state == SEQ_DONE && fabsf(o.sp - 35.0f) < 0.01f, "state=%s sp=%.2f", stateName(o.state), o.sp);
    CHECK(g_pinLevel[4] == HIGH && g_pinLevel[7] == HIGH, "relays open"); }

  printf("== O. C404 keeps repeating 250 ms after release (slow release detection)\n");
  resetWorld(c, 30.0f);
  c.releaseTailMs = 250;
  CHECK(setpointRequestAbsolute(40.0f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("O", o, c);
    CHECK(o.state == SEQ_DONE && fabsf(o.sp - 40.0f) < 0.01f, "state=%s sp=%.2f err=%s", stateName(o.state), o.sp, o.err.c_str()); }
  c.releaseTailMs = 0;

  printf("== O2. release tail 500 ms (longer than hold_settle_ms): stale stable value must not be used\n");
  resetWorld(c, 30.0f);
  c.releaseTailMs = 500;
  CHECK(setpointRequestAbsolute(40.0f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("O2", o, c);
    CHECK(o.state == SEQ_DONE && fabsf(o.sp - 40.0f) < 0.01f, "state=%s sp=%.2f err=%s", stateName(o.state), o.sp, o.err.c_str());
    CHECK(o.relayCloses <= 1 + 8, "closes=%d (a correction leg was needed)", o.relayCloses); }
  c.releaseTailMs = 0;

  printf("== P. fast C404 (25 steps/s, no delay) with lag: overshoot handled\n");
  resetWorld(c, 30.0f);
  c.repeatDelayMs = 100; c.repeatPeriodMs = 40; g_liveLatencyMs = 120;
  g_cfg.pressMs = 60;   // G2: press_ms below the repeat delay, or every discrete press repeats
  CHECK(setpointRequestAbsolute(45.0f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("P", o, c);
    CHECK(o.state == SEQ_DONE && fabsf(o.sp - 45.0f) < 0.01f, "state=%s sp=%.2f err=%s", stateName(o.state), o.sp, o.err.c_str());
    CHECK(c.maxSteps <= 150 + 6, "overshoot to %d", c.maxSteps); }
  c.repeatDelayMs = 600; c.repeatPeriodMs = 100; g_liveLatencyMs = 20;

  printf("== P2. same fast C404 with press_ms left at 150 (misconfigured): reports sp_mismatch, never loops\n");
  resetWorld(c, 30.0f);
  c.repeatDelayMs = 100; c.repeatPeriodMs = 40; g_liveLatencyMs = 120;
  CHECK(setpointRequestAbsolute(45.0f, err), "%s", err.c_str());
  { Outcome o = runUntilIdle(c, 60000); report("P2", o, c);
    CHECK(o.state == SEQ_ERROR && o.err == "sp_mismatch", "state=%s err=%s", stateName(o.state), o.err.c_str());
    CHECK(o.elapsedMs < 15000, "took %lu ms", o.elapsedMs); }
  c.repeatDelayMs = 600; c.repeatPeriodMs = 100; g_liveLatencyMs = 20;

  printf("== Q. below hold_min_steps: discrete, no hold\n");
  resetWorld(c, 30.0f);
  CHECK(setpointRequestAbsolute(31.0f, err), "%s", err.c_str());
  CHECK(!setpointPlannedHold(), "10 steps < hold_min_steps=15 must not hold");
  { Outcome o = runUntilIdle(c, 60000); report("Q", o, c);
    CHECK(o.state == SEQ_DONE && o.holds == 0 && o.relayCloses == 10, "state=%s holds=%d closes=%d", stateName(o.state), o.holds, o.relayCloses); }

  printf("== R. home in shadow mode still counts every press\n");
  resetWorld(c, 30.0f);
  g_cfg.spSource = SP_SOURCE_SHADOW; g_cfg.spMin = 28.0f; g_cfg.spMax = 32.0f; g_cfg.homeMargin = 2; c.lo = 28.0f; c.hi = 32.0f;
  CHECK(setpointHome(true, 29.0f, err), "%s", err.c_str());
  CHECK(keypadPressesTotal() == 40 + 2 + 10, "queued %lu", (unsigned long)keypadPressesTotal());
  { Outcome o = runUntilIdle(c, 120000); report("R", o, c);
    CHECK(o.state == SEQ_DONE && fabsf(o.sp - 29.0f) < 0.01f, "state=%s sp=%.2f", stateName(o.state), o.sp);
    CHECK(fabsf(g_spShadow - 29.0f) < 0.01f && g_spKnown, "shadow=%.2f", g_spShadow); }
  c.lo = 5.0f; c.hi = 90.0f;

  printf("== S. busy: second request during a hold is refused\n");
  resetWorld(c, 30.0f);
  CHECK(setpointRequestAbsolute(60.0f, err), "%s", err.c_str());
  { bool refused = false; String e2;
    Outcome o = runUntilIdle(c, 200000, [&](unsigned long t) { if (t == 3000) { refused = !setpointRequestAbsolute(31.0f, e2); } });
    report("S", o, c);
    CHECK(refused && e2 == "busy", "refused=%d err=%s", refused, e2.c_str());
    CHECK(o.state == SEQ_DONE && fabsf(o.sp - 60.0f) < 0.01f, "state=%s sp=%.2f", stateName(o.state), o.sp); }

  // ------------------------------------------------------------ modes / guard
  // Operator taps ▲ n times (150 ms down, 250 ms up) starting at t0 (hook time).
  auto taps = [](C404& c, int n, unsigned long t0) {
    return [&c, n, t0](unsigned long t) {
      if (t < t0) return;
      const unsigned long k = (t - t0) / 400, ph = (t - t0) % 400;
      c.userUp = k < static_cast<unsigned long>(n) && ph < 150;
    };
  };

  printf("== T1. manual mode: operator moves SP +0.5, node only reports\n");
  resetWorld(c, 30.0f);
  g_cfg.senseEnabled = 1;
  { int closes = 0; bool wasClosed = false;
    runFor(c, 15000, [&](unsigned long t) { taps(c, 5, 500)(t); const bool cl = g_pinLevel[5] == LOW || g_pinLevel[6] == LOW; if (cl && !wasClosed) ++closes; wasClosed = cl; });
    float dev = 0; const bool devOk = guardDeviation(dev);
    printf("  T1: sp=%.1f mode=%s guard=%s deviation=%.2f manual_presses=%lu closes=%d\n", c.sp, modeName(g_mode), guardStateName(), dev, (unsigned long)g_manualPressCount, closes);
    CHECK(fabsf(c.sp - 30.5f) < 0.01f, "sp=%.2f", c.sp);
    CHECK(closes == 0, "relays acted in manual mode");
    CHECK(devOk && fabsf(dev - 0.5f) < 0.01f, "deviation=%.2f ok=%d", dev, devOk);
    CHECK(g_manualPressCount == 5, "manual_presses=%lu", (unsigned long)g_manualPressCount);
    CHECK(guardState() == GUARD_OFF, "guard=%s", guardStateName()); }

  printf("== T2. auto mode: same +0.5 is reverted after guard_delay_ms\n");
  resetWorld(c, 30.0f);
  g_cfg.senseEnabled = 1;
  guardSetMode(MODE_AUTO, "sim");
  { unsigned long firstRelayAt = 0;
    runFor(c, 20000, [&](unsigned long t) { taps(c, 5, 500)(t); if (!firstRelayAt && (g_pinLevel[5] == LOW || g_pinLevel[6] == LOW)) firstRelayAt = t; });
    printf("  T2: sp=%.1f guard=%s corrections=%lu first relay at %lu ms\n", c.sp, guardStateName(), (unsigned long)corrections(), firstRelayAt);
    CHECK(fabsf(c.sp - 30.0f) < 0.01f, "sp=%.2f", c.sp);
    CHECK(corrections() == 1, "corrections=%lu", (unsigned long)corrections());
    // last tap released at 500 + 4*400 + 150 = 2250 ms; delay 5000 -> not before ~7250
    CHECK(firstRelayAt >= 7200 && firstRelayAt < 9000, "correction started at %lu ms", firstRelayAt);
    CHECK(guardState() == GUARD_WATCH, "guard=%s", guardStateName()); }

  printf("== T3. auto mode: operator holds ▲ 3 s (auto-repeat), reverted with a hold\n");
  resetWorld(c, 30.0f);
  g_cfg.senseEnabled = 1;
  guardSetMode(MODE_AUTO, "sim");
  { runFor(c, 30000, [&](unsigned long t) { c.userUp = t >= 500 && t < 3500; });
    printf("  T3: sp=%.1f max excursion=%d guard=%s corrections=%lu holds=%u\n", c.sp, c.maxSteps, guardStateName(), (unsigned long)corrections(), setpointHoldRounds());
    CHECK(c.maxSteps >= 20, "operator hold moved only %d steps", c.maxSteps);
    CHECK(fabsf(c.sp - 30.0f) < 0.01f, "sp=%.2f", c.sp);
    CHECK(corrections() == 1, "corrections=%lu", (unsigned long)corrections()); }

  printf("== T4. gesture: ▲+▼ held 3 s toggles manual->auto once; again -> manual\n");
  resetWorld(c, 30.0f);
  g_cfg.senseEnabled = 1;
  { const char* modeAt4s = ""; const char* modeAt7s = "";
    runFor(c, 30000, [&](unsigned long t) {
      const bool both = (t >= 500 && t < 7000) || (t >= 12000 && t < 15600);
      c.userUp = both; c.userDown = both;
      if (t == 4000) modeAt4s = modeName(g_mode);
      if (t == 7000) modeAt7s = modeName(g_mode);
    });
    printf("  T4: at 4 s=%s, at 7 s=%s, end=%s sp=%.1f led=%d\n", modeAt4s, modeAt7s, modeName(g_mode), c.sp, g_pinLevel[BoardConfig::ModeLedPin]);
    CHECK(BoardConfig::ModeLedPin >= 0 && g_pinLevel[BoardConfig::ModeLedPin] == LOW, "LED must be off in manual mode");
    CHECK(!strcmp(modeAt4s, "auto"), "mode after 3.5 s held = %s", modeAt4s);
    CHECK(!strcmp(modeAt7s, "auto"), "held longer must not toggle back (%s)", modeAt7s);
    CHECK(g_mode == MODE_MANUAL, "second gesture should give manual, got %s", modeName(g_mode)); }

  printf("== T4b. gesture during auto mode with SP drift is reverted before the toggle back\n");
  resetWorld(c, 30.0f);
  g_cfg.senseEnabled = 1;
  { runFor(c, 30000, [&](unsigned long t) { const bool both = t >= 500 && t < 4000; c.userUp = both; c.userDown = both; });
    printf("  T4b: mode=%s sp=%.1f corrections=%lu led=%d\n", modeName(g_mode), c.sp, (unsigned long)corrections(), g_pinLevel[BoardConfig::ModeLedPin]);
    CHECK(g_mode == MODE_AUTO, "mode=%s", modeName(g_mode));
    CHECK(g_pinLevel[BoardConfig::ModeLedPin] == HIGH, "LED must be on in auto mode");
    CHECK(fabsf(c.sp - 30.0f) < 0.01f, "SP moved by the pair (model: up wins) and was not reverted: %.2f", c.sp); }

  printf("== T5. auto mode in shadow mode: nothing to see, nothing done\n");
  resetWorld(c, 30.0f);
  g_cfg.senseEnabled = 1; g_cfg.spSource = SP_SOURCE_SHADOW;
  guardSetMode(MODE_AUTO, "sim");
  { runFor(c, 15000, taps(c, 5, 500));
    printf("  T5: sp=%.1f guard=%s corrections=%lu sp_known=%d\n", c.sp, guardStateName(), (unsigned long)corrections(), g_spKnown);
    CHECK(corrections() == 0 && fabsf(c.sp - 30.5f) < 0.01f, "sp=%.2f corrections=%lu", c.sp, (unsigned long)corrections());
    CHECK(!g_spKnown, "manual press in shadow mode must drop sp_known"); }

  printf("== T6. auto mode, C404 ignores every press: 3 failed corrections -> suspended; command re-arms\n");
  resetWorld(c, 30.0f);
  g_cfg.senseEnabled = 1; g_cfg.holdEnabled = 0;
  guardSetMode(MODE_AUTO, "sim");
  { runFor(c, 2000, taps(c, 3, 200));
    c.dropEveryNth = 1;
    runFor(c, 90000);
    printf("  T6: guard=%s corrections=%lu sp=%.1f\n", guardStateName(), (unsigned long)corrections(), c.sp);
    CHECK(guardState() == GUARD_SUSPENDED, "guard=%s", guardStateName());
    CHECK(corrections() == 3, "corrections=%lu", (unsigned long)corrections());
    c.dropEveryNth = 0;
    String e; CHECK(setpointRequestAbsolute(30.0f, e), "%s", e.c_str()); guardNotifyCommand();
    runFor(c, 15000);
    CHECK(guardState() == GUARD_WATCH && fabsf(c.sp - 30.0f) < 0.01f, "guard=%s sp=%.2f", guardStateName(), c.sp); }

  printf("== T7. auto mode, operator aborts a correction -> suspended\n");
  resetWorld(c, 30.0f);
  g_cfg.senseEnabled = 1;
  guardSetMode(MODE_AUTO, "sim");
  { bool aborted = false;
    runFor(c, 20000, [&](unsigned long t) { taps(c, 5, 500)(t); if (!aborted && guardState() == GUARD_CORRECTING && setpointBusy()) { setpointAbort(); aborted = true; } });
    printf("  T7: guard=%s aborted=%d\n", guardStateName(), aborted);
    CHECK(aborted && guardState() == GUARD_SUSPENDED, "guard=%s", guardStateName()); }

  printf("== T8. sense_mask: * line floating LOW is ignored when unwired\n");
  resetWorld(c, 30.0f);
  g_cfg.senseEnabled = 1;
  { runFor(c, 3000, [&](unsigned long t) { c.userStar = t > 100; });
    printf("  T8: manual_presses=%lu\n", (unsigned long)g_manualPressCount);
    CHECK(g_manualPressCount == 0, "unwired * counted as manual press");
    g_cfg.senseMask = 15; keySenseInit();
    runFor(c, 3000, [&](unsigned long t) { c.userStar = t > 100; });
    CHECK(g_manualPressCount == 1, "wired * not counted (%lu)", (unsigned long)g_manualPressCount); }

  printf("== T9. auto mode: a commanded setpoint moves the target; guard keeps the new one\n");
  resetWorld(c, 30.0f);
  guardSetMode(MODE_AUTO, "sim");
  { String e; CHECK(setpointRequestAbsolute(32.0f, e), "%s", e.c_str()); guardNotifyCommand();
    runFor(c, 20000, taps(c, 3, 12000));
    printf("  T9: sp=%.1f target=%.1f corrections=%lu\n", c.sp, g_spTarget, (unsigned long)corrections());
    CHECK(fabsf(c.sp - 32.0f) < 0.01f && fabsf(g_spTarget - 32.0f) < 0.01f, "sp=%.2f target=%.2f", c.sp, g_spTarget);
    CHECK(corrections() == 1, "corrections=%lu", (unsigned long)corrections()); }

  printf("\n%s (%d failure(s))\n", g_failures ? "FAILED" : "ALL PASSED", g_failures);
  return g_failures ? 1 : 0;
}
