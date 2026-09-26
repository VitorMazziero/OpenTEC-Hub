#include "DisplayReader.h"

#include <esp_timer.h>
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
PinMask g_ledMask;
bool g_hasLedLine = false;

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

// ---- Modo 1: janelas contadas a partir de 2DISP -------------------------------
// Um timer de hardware amostra os segmentos a cada SLOT_TICK_US; a ISR de 2DISP marca
// o inicio de cada banco. A janela k de um banco comeca em k * dispSlotUs depois da
// borda; amostras a menos de SLOT_GUARD_US das divisas nao contam (a borda de 2DISP e
// o apagamento entre janelas, ~20-60 us, caem ai). Cada segmento da janela e decidido
// por maioria das amostras restantes.
constexpr uint32_t SLOT_TICK_US = 100;
constexpr uint32_t SLOT_GUARD_US = 180;
constexpr uint8_t MAX_SLOTS = 6;
hw_timer_t* g_slotTimer = nullptr;
volatile uint8_t g_activeMode = 0xFF;
volatile uint8_t g_bank = 0;
volatile int64_t g_bankStartUs = 0;
volatile bool g_bankValid = false;
volatile uint32_t g_slotUs = 1023;
volatile uint8_t g_spBank = 0;
volatile uint8_t g_accCount[MAX_SLOTS][8];
volatile uint8_t g_accSamples[MAX_SLOTS];
volatile uint8_t g_banksDone = 0;
volatile uint8_t g_leds = 0;          // janela 4 (LEDs de sinalizacao), diagnostico
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

void IRAM_ATTR commitScan();

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
    if (target == BoardConfig::DigitCount - 1) commitScan();
  }
  g_prevDigit = active;
}

void IRAM_ATTR commitScan() {
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

// Fecha o banco que acabou: padrao de cada janela por maioria, janelas 0..3 da direita
// para a esquerda (medido: " 23.6" varre 6, 3, 2, branco), janela 4 = LEDs.
void IRAM_ATTR finishBank(uint8_t bank) {
  const uint8_t base = bank == g_spBank ? BoardConfig::DigitLineCount : 0;
  for (uint8_t k = 0; k < MAX_SLOTS; ++k) {
    uint8_t pattern = 0;
    const uint8_t n = g_accSamples[k];
    if (n > 0) {
      for (int i = 0; i < 8; ++i) {
        if (g_accCount[k][i] * 2 > n) pattern |= 1 << i;
      }
    }
    if (k < BoardConfig::DigitLineCount) {
      g_raw[base + (BoardConfig::DigitLineCount - 1 - k)] = pattern;
    } else if (k == BoardConfig::DigitLineCount && n > 0) {
      g_leds = pattern;
    }
    g_accSamples[k] = 0;
    for (int i = 0; i < 8; ++i) g_accCount[k][i] = 0;
  }
  if (++g_banksDone >= 2) {
    g_banksDone = 0;
    commitScan();
  }
}

void IRAM_ATTR onSelectEdge() {
  const int64_t now = esp_timer_get_time();
  const uint32_t in0 = REG_READ(GPIO_IN_REG);
  const uint32_t in1 = REG_READ(GPIO_IN1_REG);
  if (g_bankValid) finishBank(g_bank);
  g_bank = level(g_selMask, in0, in1) ? 1 : 0;
  g_bankStartUs = now;
  g_bankValid = true;
}

void IRAM_ATTR onSlotTick() {
  if (!g_bankValid) return;
  const uint32_t dt = static_cast<uint32_t>(esp_timer_get_time() - g_bankStartUs);
  const uint32_t slot = g_slotUs;
  const uint32_t k = dt / slot;
  if (k >= MAX_SLOTS) return;
  const uint32_t off = dt - k * slot;
  if (off < SLOT_GUARD_US || off + SLOT_GUARD_US > slot) return;
  const uint32_t in0 = REG_READ(GPIO_IN_REG);
  const uint32_t in1 = REG_READ(GPIO_IN1_REG);
  if (g_accSamples[k] == 255) return;
  ++g_accSamples[k];
  for (int i = 0; i < 8; ++i) {
    if (level(g_segMask[i], in0, in1) != (g_segLow != 0)) ++g_accCount[k][i];
  }
}

// Liga o leitor no modo pedido. Modo 0: ISR nas linhas de digito e em 2DISP. Modo 1:
// ISR so em 2DISP e timer de amostragem; as linhas de digito ficam com pull-down
// porque, nesse modo, normalmente nao estao ligadas.
void applyMode(uint8_t mode) {
  if (mode == g_activeMode) return;
  for (int i = 0; i < BoardConfig::DigitLineCount; ++i) {
    detachInterrupt(digitalPinToInterrupt(BoardConfig::DigitPins[i]));
  }
  if (g_hasSelect) detachInterrupt(digitalPinToInterrupt(BoardConfig::DisplaySelectPin));
  if (g_slotTimer) {
    timerEnd(g_slotTimer);
    g_slotTimer = nullptr;
  }
  g_bankValid = false;
  g_banksDone = 0;
  g_prevDigit = -1;

  if (mode == 1 && g_hasSelect) {
    for (int i = 0; i < BoardConfig::DigitLineCount; ++i) {
      pinMode(BoardConfig::DigitPins[i], INPUT_PULLDOWN);
    }
    for (uint8_t k = 0; k < MAX_SLOTS; ++k) {
      g_accSamples[k] = 0;
      for (int i = 0; i < 8; ++i) g_accCount[k][i] = 0;
    }
    attachInterrupt(digitalPinToInterrupt(BoardConfig::DisplaySelectPin), onSelectEdge, CHANGE);
    g_slotTimer = timerBegin(1000000);
    timerAttachInterrupt(g_slotTimer, &onSlotTick);
    timerAlarm(g_slotTimer, SLOT_TICK_US, true, 0);
  } else {
    for (int i = 0; i < BoardConfig::DigitLineCount; ++i) {
      pinMode(BoardConfig::DigitPins[i], INPUT);
      attachInterrupt(digitalPinToInterrupt(BoardConfig::DigitPins[i]), onDigitEdge, CHANGE);
    }
    if (g_hasSelect) {
      attachInterrupt(digitalPinToInterrupt(BoardConfig::DisplaySelectPin), onDigitEdge, CHANGE);
    }
  }
  g_activeMode = mode;
  Serial.printf("[DISP] Leitor no modo %u\n", mode);
}

// Converte quatro padroes (esquerda -> direita) em numero. Aceita branco a
// esquerda, sinal '-' e um unico ponto decimal, que tem de estar na casa que o C404
// usa (`disp_decimals`): sem essa exigencia, um ponto nao lido multiplica por 10.
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
  if ((decimals < 0 ? 0 : decimals) != g_cfg.dispDecimals) return false;
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
  g_hasLedLine = BoardConfig::DisplayLedLinePin >= 0;
  if (g_hasLedLine) {
    pinMode(BoardConfig::DisplayLedLinePin, INPUT);
    g_ledMask = maskFor(BoardConfig::DisplayLedLinePin);
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
  g_slotUs = g_cfg.dispSlotUs;
  g_spBank = g_cfg.dispSpBank;

  // As interrupcoes ficam sempre armadas: sem fiacao as linhas nao mudam e o custo
  // e zero; com fiacao o leitor funciona mesmo em modo sombra e serve de
  // diagnostico (/display).
  applyMode(g_cfg.dispMode);
}

void displayService(unsigned long now) {
  g_segLow = g_cfg.dispSegLow;
  g_digLow = g_cfg.dispDigLow;
  g_segLead = g_cfg.dispSegLead;
  g_slotUs = g_cfg.dispSlotUs;
  g_spBank = g_cfg.dispSpBank;
  applyMode(g_cfg.dispMode);

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
  // SP fora da faixa configurada e leitura errada, nao setpoint: nenhuma sequencia pode
  // partir dele (234 no lugar de 23.4 disparou 2100 toques de descida).
  g_spValid = decodeNumber(sp, g_sp) && g_sp >= g_cfg.spMin && g_sp <= g_cfg.spMax;
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
  return decodeNumber(sp, out) && out >= g_cfg.spMin && out <= g_cfg.spMax;
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

uint32_t displayCapture(uint16_t* out, size_t n, uint32_t periodUs) {
  const int64_t start = esp_timer_get_time();
  int64_t next = start;
  for (size_t k = 0; k < n; ++k) {
    while (esp_timer_get_time() < next) {
    }
    const uint32_t in0 = REG_READ(GPIO_IN_REG);
    const uint32_t in1 = REG_READ(GPIO_IN1_REG);
    uint16_t v = 0;
    for (int i = 0; i < 8; ++i) {
      if (level(g_segMask[i], in0, in1)) v |= 1u << i;
    }
    for (int i = 0; i < BoardConfig::DigitLineCount; ++i) {
      if (level(g_digMask[i], in0, in1)) v |= 1u << (8 + i);
    }
    if (g_hasSelect && level(g_selMask, in0, in1)) v |= 1u << 12;
    if (g_hasLedLine && level(g_ledMask, in0, in1)) v |= 1u << 13;
    out[k] = v;
    next += periodUs;
  }
  return static_cast<uint32_t>(esp_timer_get_time() - start);
}

uint8_t displayLedSegments() { return g_leds; }
