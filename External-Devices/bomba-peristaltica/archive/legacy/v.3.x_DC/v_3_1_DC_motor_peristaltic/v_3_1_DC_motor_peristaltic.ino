// -----------------------------------------------------------------------------
// Pump DC motor control (ESP32 + BTS7960) – CORRECTED VERSION
// Dual-core, USB command interface + potentiometers with sensor gate.
//
// CORRECTION: Applies "Parallel Enable" logic required for the BTS7960.
// Both R_EN and L_EN are held HIGH. Direction is controlled by which
// PWM pin (RPWM or LPWM) receives the duty cycle signal.
// -----------------------------------------------------------------------------

#include <Arduino.h>
#include <math.h>

// ───────────────────────────── DEBUG CONFIG ─────────────────────────────
static const bool     DEBUG_ENABLE      = true;
static const uint32_t DEBUG_INTERVAL_MS = 250;      // Print period
static const float    ENABLE_EPS        = 1.0f;     // |speed| threshold to enable H-bridge

// ───────────────────────────── POT MAPPING ─────────────────────────────
#define POT_DIRECTION_IS_GAIN 1  // 1 = Gain gives direction, Intensity is magnitude
const float DEAD_ZONE_DIR     = 0.02f; // 2% center deadband for direction only

// ─────────────────────────────────── PINOUT ───────────────────────────────────
#define R_EN_PIN  25 // Right enable (must be HIGH to run)
#define L_EN_PIN  26 // Left  enable (must be HIGH to run)
#define R_PWM_PIN 14 // RPWM (PWM for forward)
#define L_PWM_PIN 27 // LPWM (PWM for reverse)

#define POT_INT_PIN  34 // Intensity pot (ADC1)
#define POT_GAIN_PIN 35 // Direction  pot (ADC1)
#define SENSOR_PIN   15 // Digital sensor (active LOW)
#define SENSOR_ENABLE_BUTTON_PIN 32 // Button (INPUT_PULLUP)
#define SENSOR_STATUS_LED_PIN    33 // LED

// ────────────────────────────── CONSTANTS / TUNING ────────────────────────────
const float    V_MAX                = 1000.0f; // |speed| ≤ V_MAX → 100% duty
const float    ALPHA_INT            = 0.10f;   // LPF α – intensity (snappier)
const float    ALPHA_GAIN           = 0.10f;   // LPF α – direction
const uint32_t TASK_DELAY_MS        = 1;       // core1 PWM/position cadence
const uint32_t POS_POLL_MS          = 200;     // telemetry interval
const uint32_t SENSOR_DEBOUNCE_MS   = 50;      // digital sensor debounce

// mL min⁻¹ → "speed units" conversion
float pumpSlope     = 1.0f;
float pumpIntercept = 0.0f;

// LEDC PWM (ESP32 v3 API)
static const uint8_t  PWM_RES_BITS   = 10; // 12-bit (0..4095)
static const uint16_t PWM_MAX_DUTY   = (1u << PWM_RES_BITS) - 1;
uint32_t pwmFreqHz = 10000;                  // default 15 kHz

// ────────────────────────────── GLOBAL STATE ──────────────────────────────────
TaskHandle_t pwmTaskHandle = nullptr;

volatile bool  disablePot    = false;
volatile bool  isRawMode     = true;
volatile float usbSpeedSteps = 0.0f;
volatile bool  hasUsbSpeed   = false;

volatile bool driverEnabled  = false; // core0 decides; pwmTask uses it
volatile float cmdSpeed      = 0.0f;  // commanded speed seen by core1 task

const uint8_t  ADC_RES    = 12;
const uint16_t ADC_MAX    = (1 << ADC_RES) - 1; // 4095
const float    ADC_CENTER = static_cast<float>(ADC_MAX) / 2.0f;

volatile int32_t motorPosition = 0;

// Sensor gate
volatile bool sensorEnable         = false;
volatile bool sensorBypass         = false;
volatile bool sensorButtonOverride = false;
bool          sensorWetState       = false;

// ──────────────────────────── PROTOTYPES ──────────────────────────────────────
void pwmTask(void*);
void setupADC();
float calcPotSpeed();
void readUSBData();
void handleData(const String&);
void updateVariables(const String&, const String&, bool&, float&);
float mlminToSteps(float);
void setupSensorPin();
void updateSensorGate();
void configurePwm(uint32_t freqHz);
void applyDutyFromSpeed(float sAbs, bool dirPositive);

// ────────────────────────────────── CORE 1 TASK ───────────────────────────────
void pwmTask(void* pv) {
  uint32_t lastUs = micros();
  double   posAccum = 0.0;
  uint32_t lastDbg = 0;

  for (;;) {
    float s = cmdSpeed; // volatile read

    // Apply PWM logic on every tick
    if (driverEnabled) {
      float sAbs = fabsf(s);
      sAbs = constrain(sAbs, 0.0f, V_MAX);
      bool dirPos = (s >= 0.0f);
      applyDutyFromSpeed(sAbs, dirPos);
    } else {
      // Brake the motor
      applyDutyFromSpeed(0.0f, true);
    }

    // Integrate position estimate
    uint32_t nowUs = micros();
    float dt = (nowUs - lastUs) * 1e-6f;
    lastUs = nowUs;
    posAccum += (double)s * (double)dt;
    motorPosition = (int32_t)posAccum;

    // Debug (task-level)
    if (DEBUG_ENABLE) {
      uint32_t now = millis();
      if (now - lastDbg >= DEBUG_INTERVAL_MS) {
        Serial.printf("[PWM] enabled=%d cmd=%.2f\n", driverEnabled, (double)cmdSpeed);
        lastDbg = now;
      }
    }

    vTaskDelay(pdMS_TO_TICKS(TASK_DELAY_MS));
  }
}

// ────────────────────────────────── SETUP ─────────────────────────────────────
void setup() {
  Serial.begin(115200);

  pinMode(R_EN_PIN, OUTPUT);
  pinMode(L_EN_PIN, OUTPUT);

  // CORRECTED LOGIC: Enable both half-bridges permanently.
  // Direction will be controlled by which PWM pin gets the signal.
  digitalWrite(R_EN_PIN, HIGH);
  digitalWrite(L_EN_PIN, HIGH);

  // PWM init (v3 API)
  ledcAttach(R_PWM_PIN, pwmFreqHz, PWM_RES_BITS);
  ledcAttach(L_PWM_PIN, pwmFreqHz, PWM_RES_BITS);
  ledcWrite(R_PWM_PIN, 0);
  ledcWrite(L_PWM_PIN, 0);

  pinMode(SENSOR_ENABLE_BUTTON_PIN, INPUT_PULLUP);
  pinMode(SENSOR_STATUS_LED_PIN, OUTPUT);

  setupADC();       // 11 dB attenuation for 0..3.3V pots
  setupSensorPin();

  xTaskCreatePinnedToCore(pwmTask, "BTS7960-PWM", 4096, nullptr, 1,
                          &pwmTaskHandle, 1);

  if (DEBUG_ENABLE) {
    Serial.println("[SETUP] Ready. BTS7960 Parallel Enable Logic.");
  }
}

void setupADC() {
  analogReadResolution(ADC_RES);
  // Full-scale ~3.3V. Without this, readings saturate near 1.1V → instant max.
  analogSetPinAttenuation(POT_INT_PIN,  ADC_11db);
  analogSetPinAttenuation(POT_GAIN_PIN, ADC_11db);
}

void setupSensorPin() {
  pinMode(SENSOR_PIN, INPUT_PULLUP); // active LOW = WET
}

// ────────────────────────────────── LOOP (core 0) ─────────────────────────────
void loop() {
  static uint32_t lastPosPoll = 0;
  static uint32_t lastDbg     = 0;

  // 1 – USB processing
  readUSBData();

  // 2 – Sensor-enabled state (physical button unless overridden)
  if (!sensorButtonOverride) {
    sensorEnable = !digitalRead(SENSOR_ENABLE_BUTTON_PIN); // LOW = pressed = enabled
  }
  digitalWrite(SENSOR_STATUS_LED_PIN, sensorEnable ? HIGH : LOW);

  // 3 – Potentiometer processing (unless disabled)
  float potSpeed = 0.0f;
  if (!disablePot) potSpeed = calcPotSpeed();

  // 4 – Arbitration: USB overrides potentiometer when present
  float requestedSpeed = hasUsbSpeed ? usbSpeedSteps : potSpeed;

  // 5 – Sensor gate update (read digital pin + debounce)
  updateSensorGate();

  bool allowRun = true;
  if (sensorEnable && !sensorBypass) {
    allowRun = sensorWetState; // only run when sensor is WET
  }

  // 6 – Apply final command
  float finalSpeed = allowRun ? requestedSpeed : 0.0f;
  finalSpeed = constrain(finalSpeed, -V_MAX, V_MAX);
  cmdSpeed = finalSpeed;               // seen by core1 PWM task
  driverEnabled = (allowRun && fabsf(finalSpeed) > ENABLE_EPS);

  // 7 – Periodic telemetry
  if (millis() - lastPosPoll >= POS_POLL_MS) {
    lastPosPoll = millis();
    Serial.printf("{\"pos\":%ld,\"sensorState\":%d}\n",
                  (long)motorPosition, sensorWetState ? 1 : 0);
  }

  // 8 – Debug (arbitration)
  if (DEBUG_ENABLE) {
    uint32_t now = millis();
    if (now - lastDbg >= DEBUG_INTERVAL_MS) {
      Serial.printf("[ARB] hasUsb=%d disablePot=%d allowRun=%d req=%.2f final=%.2f wet=%d\n",
                    hasUsbSpeed, disablePot, allowRun, (double)requestedSpeed,
                    (double)finalSpeed, sensorWetState ? 1 : 0);
      lastDbg = now;
    }
  }
}

// ───────────────── POTENTIOMETER SAMPLING / LINEAR MAP + DEBUG ───────────────
float calcPotSpeed() {
  static float filtInt  = ADC_CENTER; // Intensity pot
  static float filtGain = ADC_CENTER; // Direction pot

  int rawI = analogRead(POT_INT_PIN);
  int rawG = analogRead(POT_GAIN_PIN);

  // Low-pass
  filtInt  = ALPHA_INT  * rawI + (1.0f - ALPHA_INT)  * filtInt;
  filtGain = ALPHA_GAIN * rawG + (1.0f - ALPHA_GAIN) * filtGain;

  // Linear normalize
  float mag = filtInt / (float)ADC_MAX;         // 0..1
  mag = constrain(mag, 0.0f, 1.0f);

  float dir = (filtGain - ADC_CENTER) / ADC_CENTER;   // -1..+1
  dir = constrain(dir, -1.0f, 1.0f);
  if (fabsf(dir) < DEAD_ZONE_DIR) dir = 0.0f;        // small center deadband

  float signedMag = dir * mag;                      // -1..+1
  float out = signedMag * V_MAX;                    // -V_MAX..+V_MAX

  // Debug (pot-level)
  if (DEBUG_ENABLE) {
    static uint32_t lastDbg = 0;
    uint32_t now = millis();
    if (now - lastDbg >= DEBUG_INTERVAL_MS) {
      Serial.printf("[POTS] rawI=%4d rawG=%4d  filtI=%.1f filtG=%.1f  mag=%.3f dir=%.3f out=%.1f\n",
                    rawI, rawG, (double)filtInt, (double)filtGain,
                    (double)mag, (double)dir, (double)out);
      lastDbg = now;
    }
  }

  return out;
}

// ───────────────────────────── USB PARSING (core 0) ───────────────────────────
void readUSBData() {
  if (!Serial.available()) return;
  String data = Serial.readStringUntil('\n');
  data.trim();
  handleData(data);
}

void handleData(const String& dataRaw) {
  if (!dataRaw.startsWith("{") || !dataRaw.endsWith("}")) return;
  String data = dataRaw.substring(1, dataRaw.length() - 1); // strip braces

  bool  speedPending = false;
  float tempSpeed    = 0.0f;

  int start = 0;
  while (start < data.length()) {
    int colon = data.indexOf(':', start);
    int comma = data.indexOf(',', start);
    if (comma == -1) comma = data.length();
    if (colon == -1 || colon >= comma) break;

    String key = data.substring(start, colon);
    String val = data.substring(colon + 1, comma);
    key.trim(); val.trim();

    if (key.startsWith("\"") && key.endsWith("\""))
      key = key.substring(1, key.length() - 1);

    updateVariables(key, val, speedPending, tempSpeed);
    start = comma + 1;
  }

  if (speedPending) {
    usbSpeedSteps = isRawMode ? tempSpeed : mlminToSteps(tempSpeed);
    hasUsbSpeed   = true;
  }

  if (DEBUG_ENABLE) {
    Serial.printf("[USB] isRaw=%d speedPending=%d usbSpeed=%.2f\n",
                  isRawMode, speedPending, (double)usbSpeedSteps);
  }
}

// ───────────────────── KEY-VALUE ACTIONS (core 0) ─────────────────────────────
void updateVariables(const String& key, const String& value,
                     bool &speedPending, float &tempSpeed) {
  if (key == "speed") {
    tempSpeed  = value.toFloat();
    speedPending = true;
  }
  else if (key == "raw") {
    isRawMode = (value.toInt() == 1);
  }
  else if (key == "disablePot") {
    disablePot = (value.toInt() == 1);
  }
  else if (key == "mStep") {
    uint32_t f = (uint32_t)value.toInt();
    f = constrain(f, (uint32_t)100, (uint32_t)30000);
    pwmFreqHz = f;
    configurePwm(pwmFreqHz);
    if (DEBUG_ENABLE) Serial.printf("[PWM] Freq -> %u Hz\n", (unsigned)pwmFreqHz);
  }
  else if (key == "pumpSlope") {
    pumpSlope = value.toFloat();
  }
  else if (key == "pumpIntercept") {
    pumpIntercept = value.toFloat();
  }
  else if (key == "sensorEnable") {
    sensorEnable = (value.toInt() == 1);
  }
  else if (key == "sensorBypass") {
    sensorBypass = (value.toInt() == 1);
  }
  else if (key == "sensorButtonOverride") {
    sensorButtonOverride = (value.toInt() == 1);
  }
}

// ───────────── Sensor read + debounce logic (core 0) ──────────────────────────
void updateSensorGate() {
  static int lastSteadyState = HIGH;
  static int lastFlickerState = HIGH;
  static unsigned long lastDebounceTime = 0;

  int currentState = digitalRead(SENSOR_PIN);

  if (currentState != lastFlickerState) {
    lastDebounceTime = millis();
    lastFlickerState = currentState;
  }

  if ((millis() - lastDebounceTime) > SENSOR_DEBOUNCE_MS) {
    if (currentState != lastSteadyState) {
      lastSteadyState = currentState;
      sensorWetState = (lastSteadyState == LOW); // active LOW = WET
      if (DEBUG_ENABLE) Serial.printf("[SENSOR] Wet=%d\n", sensorWetState ? 1 : 0);
    }
  }
}

// ───────────────────── mL min⁻¹ → "speed units" conversion ────────────────────
float mlminToSteps(float mlMin) {
  return mlMin * pumpSlope + pumpIntercept;
}

// ───────────────────── LEDC helpers (v3 API) ──────────────────────────────────
void configurePwm(uint32_t freqHz) {
  ledcChangeFrequency(R_PWM_PIN, freqHz, PWM_RES_BITS);
  ledcChangeFrequency(L_PWM_PIN, freqHz, PWM_RES_BITS);
}

// CORRECTED H-BRIDGE LOGIC with kick-start:
// Both EN pins are held HIGH in setup().
// Direction is controlled by which PWM pin receives the duty signal.
// This version adds a short kick to overcome breakaway torque on start.
void applyDutyFromSpeed(float sAbs, bool dirPositive) {
  // Tunable parameters
  static const float    KICK_MIN_FRAC = 0.5f;   // minimum fraction of V_MAX during kick
  static const uint32_t KICK_MS       = 100;     // kick duration in milliseconds

  // State for start detection and kick timing
  static bool     lastWasZero = true;
  static bool     kickActive  = false;
  static uint32_t kickEndMs   = 0;

  uint32_t now = millis();

  // Detect start event: previously zero, now nonzero above enable epsilon
  bool starting = lastWasZero && (sAbs > ENABLE_EPS);
  if (starting) {
    kickActive = true;
    kickEndMs  = now + KICK_MS;
  }

  // End kick when time elapses
  if (kickActive && (int32_t)(now - kickEndMs) >= 0) {
    kickActive = false;
  }

  // During kick, enforce a minimum effective command magnitude
  float sEff = sAbs;
  if (kickActive) {
    float kickUnits = KICK_MIN_FRAC * V_MAX;
    if (sEff < kickUnits) sEff = kickUnits;
  }

  // Convert to duty
  uint32_t duty = (uint32_t)lroundf((sEff / V_MAX) * PWM_MAX_DUTY);
  duty = constrain(duty, (uint32_t)0, (uint32_t)PWM_MAX_DUTY);

  // Output stage
  if (duty == 0) {
    // Brake
    ledcWrite(R_PWM_PIN, 0);
    ledcWrite(L_PWM_PIN, 0);
    lastWasZero = true;
    kickActive  = false;  // ensure no stale kick
  } else {
    if (dirPositive) {
      // Forward
      ledcWrite(R_PWM_PIN, duty);
      ledcWrite(L_PWM_PIN, 0);
    } else {
      // Reverse
      ledcWrite(R_PWM_PIN, 0);
      ledcWrite(L_PWM_PIN, duty);
    }
    lastWasZero = false;
  }

  // Debug
  if (DEBUG_ENABLE) {
    static uint32_t lastDbg = 0;
    if (now - lastDbg >= DEBUG_INTERVAL_MS) {
      if (duty == 0) {
        Serial.printf("[LEDC] BRAKE  duty=0    EN(R=1,L=1)\n");
      } else if (dirPositive) {
        Serial.printf("[LEDC] FWD    duty=%-4u EN(R=1,L=1)%s\n",
                      (unsigned)duty, kickActive ? "  KICK" : "");
      } else {
        Serial.printf("[LEDC] REV    duty=%-4u EN(R=1,L=1)%s\n",
                      (unsigned)duty, kickActive ? "  KICK" : "");
      }
      lastDbg = now;
    }
  }
}
