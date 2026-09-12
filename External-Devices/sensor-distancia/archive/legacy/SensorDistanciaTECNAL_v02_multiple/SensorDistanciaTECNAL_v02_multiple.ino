/*************************************************************
 * Distance Sensor Client – steady 1 Hz + gentle recovery
 * - ESP32 Dev Module (SDA=21, SCL=22 by default)
 * - Pololu VL53L0X lib, single-shot 1 sample/second
 * - Progressive recovery with cool-downs (no thrash)
 * - Sends every second; -1 when invalid; clamps negatives to 0
 * - Hub: http://192.168.4.1/distance
 *************************************************************/

#include <Arduino.h>
#include <WiFi.h>
#include <HTTPClient.h>
#include <Wire.h>
#include <VL53L0X.h>   // Pololu library

// ====== CONFIG (adjust to your wiring) ======
#define I2C_SDA             21
#define I2C_SCL             22
#define I2C_HZ          50000UL   // 50 kHz is very forgiving
#define WIRE_TIMEOUT_MS       80  // I2C transaction timeout

// If you can, wire XSHUT to a GPIO (active LOW). Set -1 if not wired.
#define VL53_XSHUT_PIN        5   // use -1 if not connected

// Wi-Fi SSIDs (password = SSID)
static const char* SSID_A = "ModuloTECNAL_1";
static const char* SSID_B = "ModuloTECNAL_2";

static const float OFFSET_MM = 20.0f; // subtract 20 mm, then clamp at 0
// ===========================================

#define FW_TAG "DistanceClient r6 (1Hz, singleshot, calm-recovery)"

VL53L0X sensor;
String sensorHubURL = "http://192.168.4.1/distance";

// ---- Timing ----
const unsigned long SAMPLE_PERIOD_MS   = 1000;   // 1 Hz sample
const unsigned long SEND_PERIOD_MS     = 1000;   // send every 1 s
unsigned long lastSampleMs = 0;
unsigned long lastSendMs   = 0;

// ---- Recovery policy ----
int  failStreak            = 0;      // consecutive failed reads
unsigned long lastRecovery = 0;      // last time we attempted a recovery
const unsigned long COOLDOWN_SOFT_MS  = 15000;   // min between soft re-inits
const unsigned long COOLDOWN_BUS_MS   = 15000;   // min between bus clears
const unsigned long COOLDOWN_XSHUT_MS = 30000;   // min between power cycles

// escalate thresholds (seconds of failure since we read once per second)
const int L1_SOFT_REINIT = 5;   // ≥5 sec fails -> soft re-init
const int L2_BUS_CLEAR   = 10;  // ≥10 sec fails -> bus clear + re-init
const int L3_XSHUT       = 20;  // ≥20 sec fails -> XSHUT + re-init

// ---- Accumulator for the current 1 s window (single-shot -> one sample) ----
int  lastGoodRawMm = -1;   // remember last good raw mm (optional)

// ---------- Forward declarations ----------
void i2cInit();
bool i2cBusClear();
void sensorPowerCycle();     // XSHUT or just bus clear if not wired
bool sensorInit();           // init Pololu sensor (single-shot settings)
bool readSingleShot(int &mm);
void maybeRecover();         // progressive recovery with cooldowns

void connectToWiFi();
bool httpGet(const String& url, int& code, String& body);
void i2cScanOnce(const char* tag);

// ===================== SETUP =====================
void setup() {
  Serial.begin(115200);
  delay(300);
  Serial.println();
  Serial.println(FW_TAG);

  i2cInit();
  i2cScanOnce("[BOOT]");

  if (!sensorInit()) {
    Serial.println("[BOOT] Sensor init failed; will try again after cooldown.");
  }

  WiFi.mode(WIFI_STA);
  connectToWiFi();
}

// ====================== LOOP =====================
void loop() {
  const unsigned long now = millis();

  // 1) Take one measurement per second (single-shot)
  if (now - lastSampleMs >= SAMPLE_PERIOD_MS) {
    lastSampleMs = now;

    int mm = -1;
    if (readSingleShot(mm)) {
      failStreak = 0;
      if (mm > 0 && mm < 4000) lastGoodRawMm = mm;
    } else {
      failStreak++;
      // do NOT spam recovery; we escalate gently in maybeRecover()
      maybeRecover();
    }

    // 2) Format value (apply offset, clamp >= 0, or -1 if invalid)
    float dist = -1.0f;
    if (mm > 0) {
      dist = (float)mm - OFFSET_MM;
      if (dist < 0) dist = 0;
    }

    float tSec = now / 1000.0f;
    Serial.printf("{\"time\":%.1f,\"distance\":%.0f}\n", tSec, dist);

    // 3) Send once per second (regardless of validity)
    if (WiFi.status() == WL_CONNECTED && now - lastSendMs >= SEND_PERIOD_MS) {
      lastSendMs = now;
      String url = sensorHubURL + "?distance=" + String((int)dist) +
                   "&time=" + String(tSec, 1);
      Serial.print("HTTP GET: "); Serial.println(url);
      int code; String body;
      if (httpGet(url, code, body)) {
        Serial.printf("Response: %d\n", code);
      } else {
        Serial.printf("HTTP error: %d \"%s\"\n", code, body.c_str());
      }
    }
  }

  delay(1);
}

// =============== I2C / Sensor helpers ===============
void i2cInit() {
  if (VL53_XSHUT_PIN >= 0) {
    pinMode(VL53_XSHUT_PIN, OUTPUT);
    digitalWrite(VL53_XSHUT_PIN, HIGH); // sensor ON
    delay(5);
  }
  pinMode(I2C_SDA, INPUT_PULLUP);
  pinMode(I2C_SCL, INPUT_PULLUP);
  delay(2);

  Wire.end();
  delay(2);
  Wire.begin(I2C_SDA, I2C_SCL, I2C_HZ);
  Wire.setTimeOut(WIRE_TIMEOUT_MS);

  Serial.printf("[I2C] SDA=%d SCL=%d @ %lu Hz, tmo=%ums\n",
                I2C_SDA, I2C_SCL, (unsigned long)I2C_HZ, (unsigned)WIRE_TIMEOUT_MS);
}

bool sensorInit() {
  // Try a modest number of attempts; don't loop forever
  for (int attempt = 0; attempt < 3; ++attempt) {
    Serial.printf("[VL53] init attempt %d\n", attempt + 1);
    if (sensor.init()) {
      Serial.println("[VL53] init OK");
      sensor.setTimeout(WIRE_TIMEOUT_MS);          // library-side timeout
      sensor.setMeasurementTimingBudget(200000);   // 200 ms budget
      // Single-shot: we'll call readRangeSingleMillimeters() each second
      return true;
    }
    delay(20);
  }
  Serial.println("[VL53] init FAILED");
  return false;
}

bool readSingleShot(int &mm) {
  // Pololu’s single-shot helper
  uint16_t r = sensor.readRangeSingleMillimeters();
  bool timeout = sensor.timeoutOccurred();
  bool ok = (!timeout && r > 0 && r < 4000 && r < 8190);

  if (ok) {
    mm = (int)r;
    return true;
  } else {
    mm = -1;
    return false;
  }
}

void maybeRecover() {
  const unsigned long now = millis();

  // Stage L1: Soft re-init (no bus clear) after >=5 consecutive fails, 15s cooldown
  if (failStreak >= L1_SOFT_REINIT && (now - lastRecovery) >= COOLDOWN_SOFT_MS) {
    Serial.println("[RECOVER] soft re-init");
    sensorInit();
    lastRecovery = now;
    return;
  }

  // Stage L2: Bus clear + re-init after >=10 fails, 15s cooldown
  if (failStreak >= L2_BUS_CLEAR && (now - lastRecovery) >= COOLDOWN_BUS_MS) {
    Serial.println("[RECOVER] bus clear + re-init");
    i2cBusClear();
    Wire.end(); delay(2);
    Wire.begin(I2C_SDA, I2C_SCL, I2C_HZ);
    Wire.setTimeOut(WIRE_TIMEOUT_MS);
    sensorInit();
    lastRecovery = now;
    return;
  }

  // Stage L3: XSHUT power cycle + re-init after >=20 fails, 30s cooldown
  if (failStreak >= L3_XSHUT && (now - lastRecovery) >= COOLDOWN_XSHUT_MS) {
    Serial.println("[RECOVER] XSHUT power-cycle + re-init");
    if (VL53_XSHUT_PIN >= 0) {
      digitalWrite(VL53_XSHUT_PIN, LOW);
      delay(10);
      digitalWrite(VL53_XSHUT_PIN, HIGH);
      delay(10);
    } else {
      i2cBusClear();
    }
    Wire.end(); delay(2);
    Wire.begin(I2C_SDA, I2C_SCL, I2C_HZ);
    Wire.setTimeOut(WIRE_TIMEOUT_MS);
    sensorInit();
    lastRecovery = now;
    // keep failStreak; only successful read will reset it
  }
}

bool i2cBusClear() {
  Serial.println("[I2C] bus clear...");
  pinMode(I2C_SDA, INPUT_PULLUP);
  pinMode(I2C_SCL, INPUT_PULLUP);
  delay(2);
  if (digitalRead(I2C_SDA) == HIGH) {
    Serial.println("[I2C] SDA already high");
    return true;
  }
  pinMode(I2C_SCL, OUTPUT);
  for (int i = 0; i < 16; ++i) {
    digitalWrite(I2C_SCL, LOW);  delayMicroseconds(5);
    digitalWrite(I2C_SCL, HIGH); delayMicroseconds(5);
    if (digitalRead(I2C_SDA) == HIGH) {
      Serial.printf("[I2C] SDA released after %d pulses\n", i + 1);
      break;
    }
  }
  // STOP
  pinMode(I2C_SDA, OUTPUT);
  digitalWrite(I2C_SDA, LOW);
  delayMicroseconds(5);
  digitalWrite(I2C_SCL, HIGH);
  delayMicroseconds(5);
  digitalWrite(I2C_SDA, HIGH);
  delayMicroseconds(5);
  bool ok = (digitalRead(I2C_SDA) == HIGH);
  Serial.printf("[I2C] clear %s\n", ok ? "OK" : "FAILED");
  return ok;
}

// ==================== WiFi / HTTP ====================
void connectToWiFi() {
  Serial.println("Scanning for target WiFi...");
  int n = WiFi.scanNetworks(false, true);
  if (n <= 0) {
    Serial.println("No networks found.");
    return;
  }
  String chosen = "";
  for (int i = 0; i < n; i++) {
    String ssid = WiFi.SSID(i);
    if (ssid == SSID_A || ssid == SSID_B) { chosen = ssid; break; }
  }
  if (chosen == "") { Serial.println("Target SSID not detected."); return; }

  Serial.printf("Connecting to %s\n", chosen.c_str());
  WiFi.disconnect(true, true);
  delay(50);
  WiFi.begin(chosen.c_str(), chosen.c_str()); // password == SSID

  unsigned long start = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - start < 9000) {
    delay(400); Serial.print(".");
  }
  Serial.println();
  if (WiFi.status() == WL_CONNECTED) {
    Serial.printf("[NET] STA IP: %s  HUB: %s\n",
                  WiFi.localIP().toString().c_str(), sensorHubURL.c_str());
  } else {
    Serial.println("WiFi connection failed.");
  }
}

bool httpGet(const String& url, int& code, String& body) {
  HTTPClient http;
  http.begin(url);
  http.setReuse(false);
  http.setTimeout(2500);
#if defined(HTTPC_STRICT_FOLLOW_REDIRECTS)
  http.setFollowRedirects(HTTPC_STRICT_FOLLOW_REDIRECTS);
#endif
  code = http.GET();
  if (code > 0) body = http.getString();
  else          body = String("err=") + code;
  http.end();
  return code >= 200 && code < 300;
}

// ======================= Debug =======================
void i2cScanOnce(const char* tag) {
  Serial.printf("%s I2C scan...\n", tag);
  uint8_t count = 0;
  for (uint8_t a = 1; a < 127; a++) {
    Wire.beginTransmission(a);
    if (Wire.endTransmission() == 0) { Serial.printf(" - 0x%02X\n", a); count++; }
    delay(2);
  }
  if (count == 0) Serial.println(" - no devices");
}
