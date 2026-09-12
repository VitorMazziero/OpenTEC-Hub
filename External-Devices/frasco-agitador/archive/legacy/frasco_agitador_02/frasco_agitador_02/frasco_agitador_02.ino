/****************************************************************************************
 *  Frasco Agitador IBT-2 (BTS7960) script
 *  ESP32 ▸ IBT-2 (BTS7960) 12 V DC‑motor controller – Rev. E 
 *  ─────────────────────────────────────────────────────────────────────────────────────
 *  Added in this revision
 *    1. Potentiometer enable / disable command  {"ActivePot":1|0}
 *    2. Telemetry JSON {"time_s":<sec>,"duty":<float>} every 500 ms on USB‑CDC
 *    3. HTTP GET /read returns latest telemetry JSON (soft‑AP)
 *
 *  Retained and earlier features (see Rev. D header for details)
 *    • Direction command  {"Dir":1|0}
 *    • Manual speed with 10 kΩ pot (when ActivePot = 1 and moved > ±2 %)
 *    • Wi‑Fi Soft‑AP + HTTP POST /cmd
 *    • USB‑CDC JSON command on the virtual COM port
 *    • Priority: newest JSON (Wi‑Fi or USB)  >  pot movement (if enabled)
 *    • Current telemetry via R_IS / L_IS (internal use only)
 *
 *  Dependencies
 *    • Arduino‑ESP32 core ≥ 3.0.0
 *    • ArduinoJson v6.x   (install via Library Manager)
 ****************************************************************************************/

#include <Arduino.h>
#include <WiFi.h>
#include <WebServer.h>
#include <ArduinoJson.h>

/* ───── Wi‑Fi Soft‑AP credentials ──────────────────────────────────────────── */
constexpr const char* AP_SSID = "MotorBoeco";
constexpr const char* AP_PASS = "MotorBoeco";

/* ───── Hardware mapping ──────────────────────────────────────────────────── */
constexpr int RPWM = 25;      // Right / forward PWM
constexpr int LPWM = 26;      // Left  / reverse PWM
constexpr int REN  = 27;      // Enable right half‑bridge
constexpr int LEN  = 13;      // Enable left  half‑bridge

constexpr int POT_PIN = 36;   // Potentiometer wiper  → ADC1_4
constexpr int RIS_PIN = 34;   // R_IS  → ADC1_6
constexpr int LIS_PIN = 35;   // L_IS  → ADC1_7

/* ───── PWM parameters ────────────────────────────────────────────────────── */
constexpr uint32_t PWM_FREQ = 15000;   // 15 kHz
constexpr uint8_t  PWM_RES  = 12;      // 12‑bit

/* ───── Current‑sense calibration ─────────────────────────────────────────── */
constexpr float VREF   = 3.3f;
constexpr int   ADC_FS = 4095;         // 12‑bit ADC
constexpr float RIS_R  = 1000.0f;
constexpr float ILIS_K = 19500.0f;

/* ───── Control constants ─────────────────────────────────────────────────── */
constexpr float    PERCENT_EPS = 2.0f;        // ±2 % pot dead‑band
constexpr uint16_t BOOST_DUTY  = 4095;        // 100 % at 12 bits
constexpr uint16_t BOOST_MS    = 200;

/* ───── Global state ──────────────────────────────────────────────────────── */
WebServer server(80);

enum class Source : uint8_t { POT, WIFI, USB };
volatile Source lastSource        = Source::POT;
volatile float  targetPercent     = 0.0f;      // 0…100
volatile bool   dirRight          = true;      // true = right / forward
volatile bool   potEnabled        = true;      // true = pot can override

/* Latest telemetry JSON sent by /read (updated every 500 ms) */
String latestTelemetry;

/* ───── LEDC helper ───────────────────────────────────────────────────────── */
static bool attachPwmPin(int pin)
{
    if (!ledcAttach(pin, PWM_FREQ, PWM_RES)) {
        Serial.printf("LEDC attach failed on GPIO %d\n", pin);
        return false;
    }
    ledcWrite(pin, 0);
    return true;
}

/* ───── Motor drive ───────────────────────────────────────────────────────── */
void applyDuty(uint16_t duty12)
{
    digitalWrite(REN, HIGH);
    digitalWrite(LEN, HIGH);

    if (dirRight) {
        ledcWrite(RPWM, duty12);
        ledcWrite(LPWM, 0);
    } else {
        ledcWrite(RPWM, 0);
        ledcWrite(LPWM, duty12);
    }
}

void brakeMotor()
{
    ledcWrite(RPWM, 0);
    ledcWrite(LPWM, 0);
}

/* ───── Potentiometer helpers ─────────────────────────────────────────────── */
static uint16_t readPotRaw()               { return analogRead(POT_PIN); }
static float    rawToPercent(uint16_t r)   { return r * 100.0f / ADC_FS; }

/* ───── JSON command handler (shared by Wi‑Fi & USB) ──────────────────────── */
static bool parseAndApplyJson(const String& s, Source src)
{
    StaticJsonDocument<192> doc;
    if (deserializeJson(doc, s)) return false;

    bool valid = false;

    /* RPM_percent ---------------------------------------------------------- */
    if (doc.containsKey("RPM_percent")) {
        float pct = doc["RPM_percent"];
        if (pct >= 0.0f && pct <= 100.0f) {
            targetPercent = pct;
            valid = true;
        } else {
            Serial.printf("[%s] RPM_percent out of range\n",
                          (src == Source::WIFI ? "Wi‑Fi" : "USB "));
        }
    }

    /* Dir ------------------------------------------------------------------ */
    if (doc.containsKey("Dir")) {
        int d = doc["Dir"];
        if (d == 0 || d == 1) {
            dirRight = (d == 1);
            valid = true;
        } else {
            Serial.printf("[%s] Dir must be 0 or 1\n",
                          (src == Source::WIFI ? "Wi‑Fi" : "USB "));
        }
    }

    /* ActivePot ------------------------------------------------------------ */
    if (doc.containsKey("ActivePot")) {
        int ap = doc["ActivePot"];
        if (ap == 0 || ap == 1) {
            potEnabled = (ap == 1);
            valid = true;
            Serial.printf("[%s] Potentiometer %s\n",
                          (src == Source::WIFI ? "Wi‑Fi" : "USB "),
                          potEnabled ? "enabled" : "disabled");
        } else {
            Serial.printf("[%s] ActivePot must be 0 or 1\n",
                          (src == Source::WIFI ? "Wi‑Fi" : "USB "));
        }
    }

    if (valid) {
        lastSource = src;
        Serial.printf("[%s] Cmd: %.1f %%  Dir:%s  Pot:%s\n",
                      (src == Source::WIFI ? "Wi‑Fi" : "USB "),
                      targetPercent, dirRight ? "Right" : "Left",
                      potEnabled ? "ON" : "OFF");
    }
    return valid;
}

/* ───── HTTP handlers ─────────────────────────────────────────────────────── */
static void handleCmd()
{
    if (server.method() != HTTP_POST) { server.send(405); return; }

    if (!parseAndApplyJson(server.arg("plain"), Source::WIFI)) {
        server.send(422, "text/plain",
                    "JSON: {\"RPM_percent\":0-100, \"Dir\":0|1, \"ActivePot\":0|1}");
        return;
    }
    server.send(200, "text/plain", "OK");
}

static void handleRead()
{
    /* Always build fresh telemetry to guarantee up‑to‑date timestamp */
    StaticJsonDocument<64> doc;
    doc["time_s"] = millis() / 1000;
    doc["duty"]   = targetPercent;

    String payload;
    serializeJson(doc, payload);
    latestTelemetry = payload;            // update global copy for USB print

    server.send(200, "application/json", payload);
}

/* ───── USB‑CDC command poller ─────────────────────────────────────────────── */
static void pollSerialCmd()
{
    static String buf;
    while (Serial.available()) {
        char c = char(Serial.read());
        if (c == '\n' || c == '\r') {
            if (buf.length()) parseAndApplyJson(buf, Source::USB);
            buf.clear();
        } else if (buf.length() < 180) buf += c;
    }
}

/* ───── Forward declaration ───────────────────────────────────────────────── */
uint16_t getEffectiveDuty(float pct);

/* ───── Setup ─────────────────────────────────────────────────────────────── */
void setup() {
    Serial.begin(115200);
    analogReadResolution(12);
    analogSetPinAttenuation(POT_PIN, ADC_11db);

    pinMode(REN, OUTPUT);
    pinMode(LEN, OUTPUT);
    digitalWrite(REN, HIGH);
    digitalWrite(LEN, HIGH);

    /* --- LEDC initialisation ------------------------------------ */
    if (!attachPwmPin(RPWM)) while (true) delay(1000);
    if (!attachPwmPin(LPWM)) while (true) delay(1000);

    /* --- Wi‑Fi soft‑AP and HTTP server -------------------------- */
    WiFi.mode(WIFI_AP);
    WiFi.softAP(AP_SSID, AP_PASS);
    Serial.printf("AP  %s  IP: %s\n",
                  AP_SSID, WiFi.softAPIP().toString().c_str());

    server.on("/cmd", HTTP_POST, handleCmd);
    server.on("/read", HTTP_GET,  handleRead);
    server.begin();

    applyDuty(BOOST_DUTY);
    delay(BOOST_MS);
    brakeMotor();
    Serial.println("Ready.");
}

/* ───── Main loop ─────────────────────────────────────────────────────────── */
void loop()
{
    server.handleClient();
    pollSerialCmd();

    /* ─── Potentiometer with exponential smoothing ──────────────────────── */
    static float    smoothedPotRaw = 0.0f;
    static uint16_t lastPotRaw     = 0;
    constexpr float ALPHA         = 0.5f;    // smoothing factor 0<α<1
    constexpr uint16_t CLIP_THRESH = 4080;    // reject spurious 4095

    if (potEnabled) {
        uint16_t sample = analogRead(POT_PIN);
        if (sample < CLIP_THRESH) {
            smoothedPotRaw = ALPHA * sample + (1.0f - ALPHA) * smoothedPotRaw;
        }

        uint16_t potRaw = uint16_t(smoothedPotRaw);

        if (abs(int(potRaw) - int(lastPotRaw)) > (ADC_FS * PERCENT_EPS / 100.0f)) {
            lastPotRaw    = potRaw;
            targetPercent = rawToPercent(potRaw);
            lastSource    = Source::POT;
            Serial.printf("[Pot ] Cmd: %.1f %%\n", targetPercent);
        }
    }

    /* ─── PWM update every 10 ms ────────────────────────────────────────── */
    static uint32_t tLast = 0;
    if (millis() - tLast >= 10) {
        uint16_t duty = getEffectiveDuty(targetPercent);
        applyDuty(duty);
        tLast = millis();
    }

    /* ─── Telemetry every 500 ms (USB) ──────────────────────────────────── */
    static uint32_t tTel = 0;
    if (millis() - tTel >= 500) {
        StaticJsonDocument<64> doc;
        doc["time_s"] = millis() / 1000;
        doc["duty"]   = targetPercent;

        serializeJson(doc, Serial);
        Serial.println();

        serializeJson(doc, latestTelemetry);  // store for reference
        tTel = millis();
    }
}

/* ───── Duty helper with hysteresis and minimum drive ─────────────────────── */
uint16_t getEffectiveDuty(float pct)
{
    constexpr float PWM_SCALE   = 40.95f;  // 100 % → 4095
    constexpr float OFF_THRESH  = 1.0f;    // % below which output goes to 0
    constexpr float ON_THRESH   = 1.5f;    // hysteresis restore
    constexpr float MIN_EFF_PCT = 10.0f;   // minimum effective duty %
    constexpr float MAX_EFF_PCT = 100.0f;

    static bool motorOff = false;

    if (pct < OFF_THRESH && !motorOff) {
        motorOff = true;
        return 0;
    }
    if (pct >= ON_THRESH && motorOff) {
        motorOff = false;
    }

    if (motorOff) return 0;

    /* Linear scaling: 0–100 % input → 10–100 % effective duty */
    float effPct = MIN_EFF_PCT + pct * (MAX_EFF_PCT - MIN_EFF_PCT) / 100.0f;

    return uint16_t(effPct * PWM_SCALE);
}

