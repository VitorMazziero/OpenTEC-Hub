#include "KeySense.h"

#include "../config/BoardConfig.h"
#include "../storage/NvsConfig.h"
#include "KeyPresser.h"

namespace {
constexpr unsigned long SAMPLE_MS = 5;
constexpr uint8_t DEBOUNCE_SAMPLES = 6;   // 30 ms estaveis

uint8_t g_activeRun[KEY_COUNT] = {0};
bool g_pressed[KEY_COUNT] = {false};
bool g_manualPress[KEY_COUNT] = {false};
unsigned long g_lastSampleMs = 0;
unsigned long g_arrowsSinceMs = 0;
}  // namespace

void keySenseInit() {
  // INPUT sem pull-up: o divisor 100k/150k e quem define o nivel. Linhas nao
  // ligadas (fora de sense_mask) ficam com pull-up so para nao flutuar; nunca
  // sao lidas.
  for (int i = 0; i < KEY_COUNT; ++i) {
    const bool wired = (g_cfg.senseMask >> i) & 1;
    pinMode(BoardConfig::SensePins[i], wired ? INPUT : INPUT_PULLUP);
  }
}

bool keySenseWired(Key key) {
  return g_cfg.senseEnabled && ((g_cfg.senseMask >> key) & 1);
}

bool keySenseRawActive(Key key) {
  return keySenseWired(key) && digitalRead(BoardConfig::SensePins[key]) == LOW;
}

bool keySensePressed(Key key) {
  return g_pressed[key];
}

unsigned long keySenseArrowsHeldMs(unsigned long now) {
  return g_arrowsSinceMs ? now - g_arrowsSinceMs : 0;
}

void keySenseService(unsigned long now) {
  if (!g_cfg.senseEnabled) return;
  if (now - g_lastSampleMs < SAMPLE_MS) return;
  g_lastSampleMs = now;

  for (int i = 0; i < KEY_COUNT; ++i) {
    const Key key = static_cast<Key>(i);
    if (!keySenseWired(key)) continue;
    const bool active = keySenseRawActive(key);
    if (active) {
      if (g_activeRun[i] < 255) ++g_activeRun[i];
    } else {
      g_activeRun[i] = 0;
    }

    if (!g_pressed[i] && g_activeRun[i] >= DEBOUNCE_SAMPLES) {
      g_pressed[i] = true;
      g_manualPress[i] = !keypadRelayActive(key);
      if (g_manualPress[i]) {
        ++g_manualPressCount;
        g_manualActivityMs = now;
        Serial.printf("[SENSE] Toque manual em %s\n", keyName(key));
        // * e as setas mudam o SP ou o contexto de tela; ENTER sozinho nao altera o
        // valor. So o modo sombra depende da contagem.
        if (g_cfg.spSource == SP_SOURCE_SHADOW && key != KEY_ENTER && g_spKnown) {
          g_spKnown = false;
          saveNvsState(false);
          Serial.println("[SENSE] Setpoint-sombra marcado como desconhecido.");
        }
      }
    } else if (g_pressed[i] && g_activeRun[i] == 0) {
      g_pressed[i] = false;
    }
    // Enquanto uma tecla manual segue pressionada o operador ainda esta agindo:
    // o guarda do modo automatico conta a espera a partir da soltura.
    if (g_pressed[i] && g_manualPress[i]) g_manualActivityMs = now;
  }

  const bool arrows = g_pressed[KEY_UP] && g_manualPress[KEY_UP] &&
                      g_pressed[KEY_DOWN] && g_manualPress[KEY_DOWN];
  if (arrows && g_arrowsSinceMs == 0) g_arrowsSinceMs = now;
  if (!arrows) g_arrowsSinceMs = 0;
}
