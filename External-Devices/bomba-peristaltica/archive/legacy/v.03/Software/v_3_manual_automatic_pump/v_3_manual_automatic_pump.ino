// -----------------------------------------------------------------------------
// Pump stepper control (ESP32) – dual‑core, USB command interface + potentiometers
// -----------------------------------------------------------------------------
// Features
//   • Core 1 task drives AccelStepper::runSpeed() at fixed cadence
//   • Core 0 handles USB commands, ADC sampling, UART link to MKS Servo42C
//   • USB JSON‑messages: {"speed":<val>,"raw":<0|1>,"disablePot":<0|1>,"mStep":<1‑256>}
//       – speed      : desired pump speed
//       – raw = 1    : speed in steps s⁻¹ (direct)
//       – raw = 0    : speed in mL min⁻¹ (converted via slope/intercept)
//       – disablePot : 1 disables potentiometer override, 0 enables
//       – mStep      : micro‑step setting forwarded to Servo42C (e.g. 16,32,64)
//   • Periodic UART polling (100 ms) reads closed‑loop position from Servo42C.
// -----------------------------------------------------------------------------

#include <Arduino.h>
#include <AccelStepper.h>

// ─────────────────────────────────── PINOUT ───────────────────────────────────
#define EN_PIN_PUMP   13   // Enable (LOW = ON)
#define STP_PIN_PUMP  14   // Step pulse
#define DIR_PIN_PUMP  27   // Direction
#define POT_INT_PIN   34   // Intensity potentiometer 
#define POT_GAIN_PIN  35   // Gain potentiometer 
#define UART_TX_PIN   22   // UART2 TX → Servo42C RX
#define UART_RX_PIN   21   // UART2 RX ← Servo42C TX

// ────────────────────────────── CONSTANTS / TUNING ────────────────────────────
const float V_MAX            = 1000.0f;   // steps s⁻¹
const float ALPHA_INT        = 0.05f;     // LPF α – intensity
const float ALPHA_GAIN       = 0.05f;     // LPF α – gain
const float DEAD_ZONE        = 0.05f;     // ±5 % around zero
const uint32_t TASK_DELAY_MS = 1;         // runSpeed cadence (core 1)
const uint32_t POS_POLL_MS   = 100;       // Servo42C position poll interval

// mL min⁻¹ → steps s⁻¹ conversion: speedSteps = mLmin * slope + intercept
float pumpSlope     = 1.0f;   // configurable via USB
float pumpIntercept = 0.0f;

// ────────────────────────────── GLOBAL STATE ──────────────────────────────────
TaskHandle_t stepperTaskHandle = nullptr;
AccelStepper pumpStepper(AccelStepper::DRIVER, STP_PIN_PUMP, DIR_PIN_PUMP);
HardwareSerial Servo42CSerial(2);          // UART2 interface

volatile bool  disablePot      = false;    // 1 ⇒ ignore potentiometers
volatile bool  isRawMode       = true;     // 1 ⇒ speed in steps s⁻¹
volatile float usbSpeedSteps   = 0.0f;     // speed set via USB (steps s⁻¹)
volatile bool  hasUsbSpeed     = false;    // USB speed valid flag
volatile uint16_t microStepVal = 16;       // current micro‑step (default 16)
const uint8_t ADC_RES = 11;                                 // ADC resolution bits
const uint16_t ADC_MAX = (1 << ADC_RES) - 1;                // e.g., 2047 for 11 bits
const float ADC_CENTER = static_cast<float>(ADC_MAX) / 2.0; // e.g., 1023.5 for 11 bits

// last position read from Servo42C (encoder counts)
volatile int32_t motorPosition = 0;

// ──────────────────────────── HELPER PROTOTYPES ───────────────────────────────
void stepperTask(void*);
void setupADC();
float calcPotSpeed();
void readUSBData();
void handleData(const String&);
void updateVariables(const String&, const String&, bool&, float&);
float mlminToSteps(float);
void sendMicrostepCommand(uint16_t);
int32_t queryServo42CPosition();

// ──────────────────────────────── CORE 1 TASK ─────────────────────────────────
void stepperTask(void* pv) {
  digitalWrite(EN_PIN_PUMP, LOW);  // enable driver
  for (;;) {
    pumpStepper.runSpeed();
    vTaskDelay(pdMS_TO_TICKS(TASK_DELAY_MS));
  }
}

// ────────────────────────────────── SETUP ─────────────────────────────────────
void setup() {
  Serial.begin(115200);
  Servo42CSerial.begin(115200, SERIAL_8N1, UART_RX_PIN, UART_TX_PIN);
  setupADC();
  xTaskCreatePinnedToCore(stepperTask, "StepperTask", 2048, nullptr, 1,
                          &stepperTaskHandle, 1);
  sendMicrostepCommand(microStepVal);   // initialise micro‑step on startup
}

void setupADC() {
  analogReadResolution(ADC_RES);     // 0…4095
  pinMode(EN_PIN_PUMP, OUTPUT);
  digitalWrite(EN_PIN_PUMP, LOW);
  pumpStepper.setMaxSpeed(V_MAX);
}

// ────────────────────────────────── LOOP (core 0) ─────────────────────────────
void loop() {
  static uint32_t lastPosPoll = 0;

  // 1 – USB processing
  readUSBData();

  // 2 – Potentiometer processing (unless disabled)
  float potSpeed = 0.0f;
  if (!disablePot) potSpeed = calcPotSpeed();

  // 3 – Arbitration: USB overrides potentiometer when present
  float targetSpeed = hasUsbSpeed ? usbSpeedSteps : potSpeed;
  pumpStepper.setSpeed(constrain(targetSpeed, -V_MAX, V_MAX));

  // 4 – Periodic position polling
  if (millis() - lastPosPoll >= POS_POLL_MS) {
    motorPosition = queryServo42CPosition();
    lastPosPoll = millis();
    Serial.println(motorPosition);
    Serial.println(targetSpeed);
  }
}

// ───────────────────── POTENTIOMETER SAMPLING / FILTER ───────────────────────
float calcPotSpeed() {
  static float filtInt  = analogRead(POT_INT_PIN);
  static float filtGain = analogRead(POT_GAIN_PIN);

  int rawI  = analogRead(POT_INT_PIN);
  int rawG  = analogRead(POT_GAIN_PIN);

  // Low‑pass filters
  filtInt  = ALPHA_INT  * rawI  + (1.0f - ALPHA_INT)  * filtInt;
  filtGain = ALPHA_GAIN * rawG + (1.0f - ALPHA_GAIN) * filtGain;

  // Normalise to –1 … +1 (intensity)
  float intensity = (filtInt - ADC_CENTER) / ADC_CENTER;
  intensity = constrain(intensity, -1.0f, 1.0f);

  // Dead‑band and rescale
  if (fabs(intensity) < DEAD_ZONE) {
    intensity = 0.0f;
  } else {
    float sign = (intensity > 0) ? 1.0f : -1.0f;
    intensity = sign * ((fabs(intensity) - DEAD_ZONE) / (1.0f - DEAD_ZONE));
  }

  // Gain 0 … 1
  float gain = filtGain / static_cast<float>(ADC_MAX);
  gain = constrain(gain, 0.0f, 1.0f);

  return intensity * gain * V_MAX;
}

// ───────────────────────────── USB PARSING (core 0) ───────────────────────────
void readUSBData() {
  if (!Serial.available()) return;
  String data = Serial.readStringUntil('\n');
  data.trim();
  handleData(data);
}

void handleData(const String& dataRaw) {
  if (!dataRaw.startsWith("{") || !dataRaw.endsWith("}")) return;
  String data = dataRaw.substring(1, dataRaw.length() - 1);  // strip braces

  bool  speedPending = false;
  float tempSpeed    = 0.0f;

  int start = 0;
  while (start < data.length()) {
    int colon = data.indexOf(':', start);
    int comma = data.indexOf(',', start);
    if (comma == -1) comma = data.length();
    if (colon == -1 || colon >= comma) break;  // malformed

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
}

// ───────────────────── KEY‑VALUE ACTIONS (core 0, non‑ISR) ────────────────────
void updateVariables(const String& key, const String& value,
                     bool &speedPending, float &tempSpeed) {
  if (key == "speed") {
    tempSpeed   = value.toFloat();
    speedPending = true;
  }
  else if (key == "raw") {
    isRawMode = (value.toInt() == 1);
  }
  else if (key == "disablePot") {
    disablePot = (value.toInt() == 1);
  }
  else if (key == "mStep") {
    uint16_t m = value.toInt();
    if (m >= 1 && m <= 256) {
      microStepVal = m;
      sendMicrostepCommand(m);
    }
  }
  else if (key == "pumpSlope") {
    pumpSlope = value.toFloat();
  }
  else if (key == "pumpIntercept") {
    pumpIntercept = value.toFloat();
  }
  // Extend with additional parameters as needed
}

// ───────────────────── mL min⁻¹ → steps s⁻¹ conversion ────────────────────────
float mlminToSteps(float mlMin) {
  return mlMin * pumpSlope + pumpIntercept;
}

// ───────────────────── Servo42C micro‑step command helper ─────────────────────
void sendMicrostepCommand(uint16_t mStep) {
  // Servo42C expects JSON‑like command {"MS":<value>} followed by \n
  Servo42CSerial.print("{\"MS\":");
  Servo42CSerial.print(mStep);
  Servo42CSerial.println("}\n");
}

// ───────────────────── Servo42C position query helper ─────────────────────────
int32_t queryServo42CPosition() {
  Servo42CSerial.println("{\"GETPOS\":1}\n");
  // simple blocking read with 20 ms timeout
  uint32_t t0 = millis();
  while (Servo42CSerial.available() == 0 && millis() - t0 < 20) {
    delay(1);
  }
  if (Servo42CSerial.available()) {
    String resp = Servo42CSerial.readStringUntil('\n');
    resp.trim();
    // Expecting e.g. {"POS":12345}
    int posIdx = resp.indexOf("POS");
    if (posIdx != -1) {
      int colon = resp.indexOf(':', posIdx);
      int brace = resp.indexOf('}', colon);
      if (colon != -1 && brace != -1) {
        String num = resp.substring(colon + 1, brace);
        return num.toInt();
      }
    }
  }
  return motorPosition;  // fallback: return last known
}
