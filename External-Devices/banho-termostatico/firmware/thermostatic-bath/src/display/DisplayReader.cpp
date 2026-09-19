#include "DisplayReader.h"

#include <soc/gpio_reg.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"

namespace {
constexpr unsigned long DECODE_PERIOD_MS = 100;
constexpr uint8_t STABLE_FRAMES = 3;
constexpr unsigned long ALIVE_TIMEOUT_MS = 500;

// Bits: A=1 B=2 C=4 D=8 E=16 F=32 G=64 PD=128.
constexpr uint8_t SEG_DP = 0x80;
constexpr uint8_t DIGIT_PATTERNS[10] = {0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F};
constexpr uint8_t PATTERN_MINUS = 0x40;

struct PinMask {
  uint8_t bank;    // 0 -> GPIO_IN_REG, 1 -> GPIO_IN1_REG
  uint32_t mask;
};

PinMask g_segMask[8];
PinMask g_digMask[BoardConfig::DigitLineCount];
PinMask g_selMask;
bool g_hasSelect = false;

volatile uint8_t g_raw[BoardConfig::DigitCount];
volatile uint32_t g_frames = 0;
// Quadro coerente: copia de g_raw feita no fim da varredura, so quando a varredura
// inteira repetiu a anterior. Uma atualizacao do C404 no meio de uma varredura
// mistura digitos velhos e novos; exigir duas varreduras iguais descarta essa
// mistura sem esperar os 300 ms do filtro de estabilidade.
uint8_t g_prevScan[BoardConfig::DigitCount];
volatile uint8_t g_live[BoardConfig::DigitCount];
volatile uint32_t g_liveFrames = 0;
volatile int8_t g_prevDigit = -1;
volatile uint8_t g_segLow = 1;
volatile uint8_t g_digLow = 1;
volatile uint8_t g_segLead = 0;

uint8_t g_stable[BoardConfig::DigitCount];
uint8_t g_candidate[BoardConfig::DigitCount];
uint8_t g_candidateRun = 0;
uint32_t g_lastFrames = 0;
unsigned long g_lastFrameChangeMs = 0;
unsigned long g_lastDecodeMs = 0;
bool g_alive = false;
bool g_pvValid = false;
bool g_spValid = false;
float g_pv = 0.0f;
float g_sp = 0.0f;

PinMask maskFor(int pin) {
  PinMask m;
  m.bank = pin >= 32 ? 1 : 0;
  m.mask = 1UL << (pin & 31);
  return m;
}

inline bool IRAM_ATTR level(const PinMask& m, uint32_t in0, uint32_t in1) {
  return ((m.bank ? in1 : in0) & m.mask) != 0;
}

// Amostra na transicao das linhas de digito. O quadro lido pertence ao digito que
// estava ativo ate agora (drivers que trocam os segmentos depois de desligar o
// digito) ou ao que acaba de ativar (`dispSegLead`). A escolha e calibrada em
// bancada; ver docs/VALIDATION.md.
void IRAM_ATTR onDigitEdge() {
  const uint32_t in0 = REG_READ(GPIO_IN_REG);
  const uint32_t in1 = REG_READ(GPIO_IN1_REG);

  uint8_t seg = 0;
  for (int i = 0; i < 8; ++i) {
    const bool lit = level(g_segMask[i], in0, in1) != (g_segLow != 0);
    if (lit) seg |= (1 << i);
  }

  int8_t active = -1;
  for (int i = 0; i < BoardConfig::DigitLineCount; ++i) {
    const bool on = level(g_digMask[i], in0, in1) != (g_digLow != 0);
    if (on) { active = i; break; }
  }
  if (active >= 0 && g_hasSelect && level(g_selMask, in0, in1)) {
    active += BoardConfig::DigitLineCount;
  }

  const int8_t target = g_segLead ? active : g_prevDigit;
  if (target >= 0 && target < BoardConfig::DigitCount) {
    g_raw[target] = seg;
    if (target == BoardConfig::DigitCount - 1) {
      ++g_frames;
      bool same = true;
      for (int i = 0; i < BoardConfig::DigitCount; ++i) {
        if (g_raw[i] != g_prevScan[i]) { same = false; break; }
      }
      if (same) {
        for (int i = 0; i < BoardConfig::DigitCount; ++i) g_live[i] = g_raw[i];
        ++g_liveFrames;
      }
      for (int i = 0; i < BoardConfig::DigitCount; ++i) g_prevScan[i] = g_raw[i];
    }
  }
  g_prevDigit = active;
}

// Converte quatro padroes (esquerda -> direita) em numero. Aceita branco a
// esquerda, sinal '-' e um unico ponto decimal.
bool decodeNumber(const uint8_t* patterns, float& out) {
  long value = 0;
  int decimals = -1;
  bool negative = false;
  bool anyDigit = false;
  for (int i = 0; i < 4; ++i) {
    const uint8_t p = patterns[i];
    const uint8_t body = p & ~SEG_DP;
    if (p & SEG_DP) {
      if (decimals >= 0) return false;
      decimals = 3 - i;
    }
    if (body == 0) {
      if (anyDigit) return false;
      continue;
    }
    if (body == PATTERN_MINUS) {
      if (anyDigit || negative) return false;
      negative = true;
      continue;
    }
    int d = -1;
    for (int k = 0; k < 10; ++k) {
      if (DIGIT_PATTERNS[k] == body) { d = k; break; }
    }
    if (d < 0) return false;
    anyDigit = true;
    value = value * 10 + d;
  }
  if (!anyDigit) return false;
  float f = static_cast<float>(value);
  for (int i = 0; i < decimals; ++i) f /= 10.0f;
  out = negative ? -f : f;
  return true;
}

char patternChar(uint8_t p) {
  const uint8_t body = p & ~SEG_DP;
  if (body == 0) return ' ';
  if (body == PATTERN_MINUS) return '-';
  for (int k = 0; k < 10; ++k) {
    if (DIGIT_PATTERNS[k] == body) return static_cast<char>('0' + k);
  }
  return '?';
}
}  // namespace

void displayInit() {
  for (int i = 0; i < 8; ++i) {
    pinMode(BoardConfig::SegmentPins[i], INPUT);
    g_segMask[i] = maskFor(BoardConfig::SegmentPins[i]);
  }
  for (int i = 0; i < BoardConfig::DigitLineCount; ++i) {
    pinMode(BoardConfig::DigitPins[i], INPUT);
    g_digMask[i] = maskFor(BoardConfig::DigitPins[i]);
  }
  g_hasSelect = BoardConfig::DisplaySelectPin >= 0;
  if (g_hasSelect) {
    pinMode(BoardConfig::DisplaySelectPin, INPUT);
    g_selMask = maskFor(BoardConfig::DisplaySelectPin);
  }
  for (int i = 0; i < BoardConfig::DigitCount; ++i) {
    g_raw[i] = 0;
    g_prevScan[i] = 0;
    g_live[i] = 0;
    g_stable[i] = 0;
    g_candidate[i] = 0;
  }

  g_segLow = g_cfg.dispSegLow;
  g_digLow = g_cfg.dispDigLow;
  g_segLead = g_cfg.dispSegLead;

  // As interrupcoes ficam sempre armadas: sem fiacao as linhas nao mudam e o custo
  // e zero; com fiacao o leitor funciona mesmo em modo sombra e serve de
  // diagnostico (/display).
  for (int i = 0; i < BoardConfig::DigitLineCount; ++i) {
    attachInterrupt(digitalPinToInterrupt(BoardConfig::DigitPins[i]), onDigitEdge, CHANGE);
  }
  if (g_hasSelect) {
    attachInterrupt(digitalPinToInterrupt(BoardConfig::DisplaySelectPin), onDigitEdge, CHANGE);
  }
}

void displayService(unsigned long now) {
  g_segLow = g_cfg.dispSegLow;
  g_digLow = g_cfg.dispDigLow;
  g_segLead = g_cfg.dispSegLead;

  if (now - g_lastDecodeMs < DECODE_PERIOD_MS) return;
  g_lastDecodeMs = now;

  const uint32_t frames = g_frames;
  if (frames != g_lastFrames) {
    g_lastFrames = frames;
    g_lastFrameChangeMs = now;
  }
  g_alive = (now - g_lastFrameChangeMs) <= ALIVE_TIMEOUT_MS && frames > 0;
  if (!g_alive) {
    g_pvValid = false;
    g_spValid = false;
    g_candidateRun = 0;
    return;
  }

  uint8_t snapshot[BoardConfig::DigitCount];
  noInterrupts();
  for (int i = 0; i < BoardConfig::DigitCount; ++i) snapshot[i] = g_raw[i];
  interrupts();

  bool same = true;
  for (int i = 0; i < BoardConfig::DigitCount; ++i) {
    if (snapshot[i] != g_candidate[i]) { same = false; break; }
  }
  if (same) {
    if (g_candidateRun < 255) ++g_candidateRun;
  } else {
    memcpy(g_candidate, snapshot, sizeof(snapshot));
    g_candidateRun = 1;
  }
  if (g_candidateRun < STABLE_FRAMES) return;
  memcpy(g_stable, g_candidate, sizeof(g_stable));

  uint8_t pv[4], sp[4];
  for (int i = 0; i < 4; ++i) {
    pv[i] = g_stable[BoardConfig::PvDigits[i]];
    sp[i] = g_stable[BoardConfig::SpDigits[i]];
  }
  g_pvValid = decodeNumber(pv, g_pv);
  g_spValid = decodeNumber(sp, g_sp);
}

bool displayPvValid() { return g_alive && g_pvValid; }
float displayPv() { return g_pv; }
bool displaySpValid() { return g_alive && g_spValid; }
float displaySp() { return g_sp; }
bool displayAlive() { return g_alive; }
uint32_t displayFrameCount() { return g_frames; }
uint32_t displayLiveFrameCount() { return g_liveFrames; }

bool displayLiveSp(float& out) {
  if (!g_alive) return false;
  uint8_t sp[4];
  noInterrupts();
  for (int i = 0; i < 4; ++i) sp[i] = g_live[BoardConfig::SpDigits[i]];
  interrupts();
  return decodeNumber(sp, out);
}

uint8_t displayRawSegments(uint8_t digit) {
  return digit < BoardConfig::DigitCount ? g_stable[digit] : 0;
}

String displayText() {
  String text;
  text.reserve(BoardConfig::DigitCount * 2 + 1);
  for (int i = 0; i < BoardConfig::DigitCount; ++i) {
    text += patternChar(g_stable[i]);
    if (g_stable[i] & SEG_DP) text += '.';
  }
  return text;
}
