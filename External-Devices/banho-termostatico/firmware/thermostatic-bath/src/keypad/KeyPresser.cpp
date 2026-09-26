#include "KeyPresser.h"

#include "../config/BoardConfig.h"
#include "KeySense.h"

namespace {
constexpr int RELAY_PINS[KEY_COUNT] = {BoardConfig::RelayStarPin, BoardConfig::RelayUpPin,
                                       BoardConfig::RelayDownPin, BoardConfig::RelayEnterPin};
constexpr uint8_t QUEUE_SIZE = 12;

struct Step {
  Key key;
  uint16_t remaining;
  uint16_t extraGapMs;
  uint32_t holdMs;      // > 0: um unico "hold" de ate holdMs em vez de `remaining` toques
};

enum EngineState : uint8_t { ENG_IDLE, ENG_PRESSED, ENG_HELD, ENG_GAP };

Step g_queue[QUEUE_SIZE];
uint8_t g_head = 0;
uint8_t g_count = 0;
EngineState g_state = ENG_IDLE;
unsigned long g_stateSinceMs = 0;
unsigned long g_gapMs = 0;
uint32_t g_holdMaxMs = 0;
bool g_releaseRequested = false;
Key g_activeKey = KEY_STAR;
bool g_activeSeen = false;
uint32_t g_done = 0;
uint32_t g_total = 0;
uint32_t g_unconfirmed = 0;
void (*g_onPress)(Key) = nullptr;

constexpr int RELAY_ON = BoardConfig::RelayActiveLow ? LOW : HIGH;
constexpr int RELAY_OFF = BoardConfig::RelayActiveLow ? HIGH : LOW;

inline void relayWrite(Key key, bool closed) {
  digitalWrite(RELAY_PINS[key], closed ? RELAY_ON : RELAY_OFF);
}

void releaseAll() {
  for (int i = 0; i < KEY_COUNT; ++i) relayWrite(static_cast<Key>(i), false);
}

void popStep() {
  g_head = (g_head + 1) % QUEUE_SIZE;
  --g_count;
}

bool pushStep(const Step& step) {
  if (g_count >= QUEUE_SIZE) return false;
  const uint8_t slot = (g_head + g_count) % QUEUE_SIZE;
  g_queue[slot] = step;
  ++g_count;
  return true;
}

// Abre o rele ativo e agenda a pausa. Comum ao toque e ao hold; so o toque
// avisa o callback, porque so ele tem uma contagem que valha alguma coisa.
void openActiveRelay(unsigned long now, bool notify) {
  relayWrite(g_activeKey, false);
  if (keySenseWired(g_activeKey) && !g_activeSeen) ++g_unconfirmed;
  Step& step = g_queue[g_head];
  g_gapMs = g_cfg.gapMs;
  if (step.holdMs > 0 || --step.remaining == 0) {
    g_gapMs += step.extraGapMs;
    popStep();
  }
  g_state = ENG_GAP;
  g_stateSinceMs = now;
  if (notify && g_onPress) g_onPress(g_activeKey);
}
}  // namespace

void keypadInit() {
  // O registrador de saida do ESP32 parte em 0: com reles ativos em LOW, configurar
  // OUTPUT antes de escrever o nivel de repouso fecharia os quatro por alguns
  // microssegundos a cada boot. A escrita precede o pinMode de proposito.
  for (int i = 0; i < KEY_COUNT; ++i) {
    digitalWrite(RELAY_PINS[i], RELAY_OFF);
    pinMode(RELAY_PINS[i], OUTPUT);
    digitalWrite(RELAY_PINS[i], RELAY_OFF);
  }
}

bool keypadEnqueue(Key key, uint16_t count, uint16_t extraGapMs) {
  if (count == 0) return true;
  if (!pushStep({key, count, extraGapMs, 0})) return false;
  g_total += count;
  return true;
}

bool keypadEnqueueHold(Key key, uint32_t maxMs, uint16_t extraGapMs) {
  if (maxMs == 0) return true;
  return pushStep({key, 1, extraGapMs, maxMs});
}

void keypadRelease() {
  if (g_state == ENG_HELD) g_releaseRequested = true;
}

void keypadClear() {
  releaseAll();
  g_head = 0;
  g_count = 0;
  g_state = ENG_IDLE;
  g_releaseRequested = false;
  keypadResetCounters();
}

void keypadResetCounters() {
  g_done = 0;
  g_total = 0;
}

bool keypadBusy() {
  return g_count > 0 || g_state != ENG_IDLE;
}

bool keypadHolding() {
  return g_state == ENG_HELD;
}

unsigned long keypadHoldMs(unsigned long now) {
  return g_state == ENG_HELD ? now - g_stateSinceMs : 0;
}

bool keypadRelayActive(Key key) {
  return (g_state == ENG_PRESSED || g_state == ENG_HELD) && g_activeKey == key;
}

uint32_t keypadPressesDone() { return g_done; }
uint32_t keypadPressesTotal() { return g_total; }
uint32_t keypadUnconfirmedPresses() { return g_unconfirmed; }

void keypadSetOnPress(void (*callback)(Key)) {
  g_onPress = callback;
}

void keypadService(unsigned long now) {
  switch (g_state) {
    case ENG_IDLE:
      if (g_count == 0) return;
      g_activeKey = g_queue[g_head].key;
      g_activeSeen = false;
      g_releaseRequested = false;
      g_holdMaxMs = g_queue[g_head].holdMs;
      relayWrite(g_activeKey, true);
      g_state = g_holdMaxMs > 0 ? ENG_HELD : ENG_PRESSED;
      g_stateSinceMs = now;
      break;

    case ENG_PRESSED:
      // Com sensoriamento ligado, um toque so conta como confirmado se a linha da
      // tecla foi vista fechada enquanto o rele estava acionado.
      if (keySenseRawActive(g_activeKey)) g_activeSeen = true;
      if (now - g_stateSinceMs < g_cfg.pressMs) return;
      ++g_done;
      openActiveRelay(now, true);
      break;

    case ENG_HELD:
      // O teto e so uma rede de seguranca: quem decide soltar e o SetpointManager,
      // lendo o display. Sem ele um hold nunca ficaria preso por um bug de logica.
      if (keySenseRawActive(g_activeKey)) g_activeSeen = true;
      if (!g_releaseRequested && now - g_stateSinceMs < g_holdMaxMs) return;
      g_releaseRequested = false;
      openActiveRelay(now, false);
      break;

    case ENG_GAP:
      if (now - g_stateSinceMs < g_gapMs) return;
      g_state = ENG_IDLE;
      break;
  }
}
