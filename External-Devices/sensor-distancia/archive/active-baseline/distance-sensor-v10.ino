/*************************************************************
 * Distance Sensor Client – AP+STA, Non-Blocking, Configurable
 * - ESP32 Dev Module (SDA=21, SCL=22)
 * - Pololu VL53L0X lib, single-shot 1 sample/second
 * - Serves JSON data on its own AP (192.168.5.1)
 * - Forwards data to hub (192.168.4.1) when connected
 * - Non-blocking STA reconnect logic (scans once, retries)
 * - Runtime configurable via Serial or POST to /config
 *
 * - v10: Solved IP conflict, non-blocking scan, runtime config
 *************************************************************/

#include <Arduino.h>
#include <WiFi.h>
#include <HTTPClient.h>
#include <Wire.h>
#include <VL53L0X.h>   // Pololu library
#include <WebServer.h>  // For serving data on the AP

// ====== CONFIG (adjust to your wiring) ======
#define I2C_SDA          21
#define I2C_SCL          22
#define I2C_HZ        50000UL   // 50 kHz is very forgiving
#define WIRE_TIMEOUT_MS      80   // I2C transaction timeout

// If you can, wire XSHUT to a GPIO (active LOW). Set -1 if not wired.
#define VL53_XSHUT_PIN       5   // use -1 if not connected

// Wi-Fi SSIDs (password = SSID)
static const char* SSID_A = "ModuloTECNAL_1";
static const char* SSID_B = "ModuloTECNAL_2";

static const float OFFSET_MM = 20.0f; // subtract 20 mm, then clamp at 0

// --- Access Point Config ---
static const char* AP_SSID = "Distance Sensor";
// --- NEW: Define a separate subnet for our AP to avoid IP conflicts ---
IPAddress apIP(192, 168, 5, 1);
IPAddress apGateway(192, 168, 5, 1);
IPAddress apSubnet(255, 255, 255, 0);
// ===========================================

#define FW_TAG "DistanceClient r10 (AP+STA, Configurable, Non-Blocking)"

VL53L0X sensor;
String sensorHubURL = "http://192.168.4.1/distance";
WebServer server(80); // Create a web server on port 80

// ===================================================================
// --- NEW: Runtime-configurable variables (were const) ---
// ===================================================================
// ---- Timing ----
unsigned long SAMPLE_PERIOD_MS   = 1000;   // 1 Hz sample
unsigned long SEND_PERIOD_MS     = 1000;   // send every 1 s
unsigned long lastSampleMs = 0;
unsigned long lastSendMs   = 0;

// ---- WiFi Reconnect Timing ----
unsigned long lastWifiCheckMs = 0;
unsigned long WIFI_RECONNECT_PERIOD_MS = 10000; // Check every 10 seconds

// ---- Recovery policy ----
int   failStreak          = 0;     // consecutive failed reads
unsigned long lastRecovery = 0;     // last time we attempted a recovery
unsigned long COOLDOWN_SOFT_MS  = 15000;   // min between soft re-inits
unsigned long COOLDOWN_BUS_MS   = 15000;   // min between bus clears
unsigned long COOLDOWN_XSHUT_MS = 30000;   // min between power cycles

// escalate thresholds (seconds of failure since we read once per second)
int L1_SOFT_REINIT = 5;   // ≥5 sec fails -> soft re-init
int L2_BUS_CLEAR   = 10;  // ≥10 sec fails -> bus clear + re-init
int L3_XSHUT       = 20;  // ≥20 sec fails -> XSHUT + re-init
// ===================================================================

// ---- Accumulator for the current 1 s window (single-shot -> one sample) ----
int   lastGoodRawMm = -1;   // remember last good raw mm (optional)

// --- Global variables to hold the latest data for the web server ---
float g_lastValidDistance = -1.0f;
float g_lastSampleTimeSec = 0.0f;

// --- NEW: Global for non-blocking reconnect ---
String g_lastKnownSsid = "";
enum WifiReconnectState { WF_IDLE, WF_SCANNING, WF_CONNECTING };
WifiReconnectState g_wifiState = WF_IDLE;
unsigned long g_wifiNextActionMs = 0;

// ---------- Forward declarations ----------
void i2cInit();
bool i2cBusClear();
void sensorPowerCycle();     // XSHUT or just bus clear if not wired
bool sensorInit();           // init Pololu sensor (single-shot settings)
bool readSingleShot(int &mm);
void maybeRecover();         // progressive recovery with cooldowns

void checkWifi(); // Non-blocking reconnect
bool httpGet(const String& url, int& code, String& body);
void i2cScanOnce(const char* tag);

// --- NEW: Configuration update functions ---
void processConfigUpdate(String payload);
String getConfigAsJson();
long getJsonValue(String json, String key);

// --- Web server handler functions ---
void handleRoot() {
  // Create the same JSON string you send to serial
  String json = "{\"time\":" + String(g_lastSampleTimeSec, 1) + 
                ",\"distance\":" + String((int)g_lastValidDistance) + "}";
  server.send(200, "application/json", json);
}

// --- NEW: Handler for GET /config ---
void handleGetConfig() {
  Serial.println("[HTTP] GET /config received");
  server.send(200, "application/json", getConfigAsJson());
}

// --- NEW: Handler for POST /config ---
void handleConfig() {
  Serial.println("[HTTP] POST /config received");
  if (server.hasArg("plain")) {
    String body = server.arg("plain");
    Serial.println("[HTTP] Body: " + body);
    processConfigUpdate(body);
    server.send(200, "text/plain", "Config Updated");
  } else {
    server.send(400, "text/plain", "Bad Request - No Body");
  }
}

void handleNotFound() {
  server.send(404, "text/plain", "Not Found");
}

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

  // --- Set mode to AP + STA ---
  Serial.println("[NET] Setting mode to WIFI_AP_STA...");
  WiFi.mode(WIFI_AP_STA);

  // --- NEW: Configure our AP's static IP to avoid conflicts ---
  Serial.printf("[NET] Configuring AP on subnet %s\n", apIP.toString().c_str());
  WiFi.softAPConfig(apIP, apGateway, apSubnet);
  
  // --- Start the Access Point ---
  Serial.printf("[NET] Starting AP: %s\n", AP_SSID);
  if (WiFi.softAP(AP_SSID)) { // Using open network as per last file
    Serial.printf("[NET] AP IP: %s\n", WiFi.softAPIP().toString().c_str());
  } else {
    Serial.println("[NET] AP Start Failed!");
  }

  // --- Configure and start the web server ---
  server.on("/", HTTP_GET, handleRoot);       // Serve sensor data
  server.on("/config", HTTP_GET, handleGetConfig);  // View current config
  server.on("/config", HTTP_POST, handleConfig); // Update config
  server.onNotFound(handleNotFound);
  server.begin();
  Serial.println("[NET] Web server started. AP: 192.168.5.1");

  g_wifiNextActionMs = 0;
  checkWifi();
}

// ====================== LOOP =====================
void loop() {
  const unsigned long now = millis();

  // --- Must call server.handleClient() every loop ---
  // This is non-blocking and processes incoming HTTP requests on the AP
  server.handleClient();

  // --- NEW: Check for serial commands ---
  if (Serial.available() > 0) {
    String cmd = Serial.readStringUntil('\n');
    cmd.trim();
    if (cmd.length() > 0 && cmd.startsWith("{")) {
      Serial.println("[CMD] Received: " + cmd);
      processConfigUpdate(cmd);
    }
  }

  checkWifi();


  // 1) Take one measurement per second (single-shot)
  if (now - lastSampleMs >= SAMPLE_PERIOD_MS) {
    lastSampleMs = now;

    int mm = -1;
    if (readSingleShot(mm)) {
      failStreak = 0;
      if (mm > 0 && mm < 4000) lastGoodRawMm = mm;
    } else {
      failStreak++;
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

    // --- Update global variables for the web server ---
    g_lastValidDistance = dist;
    g_lastSampleTimeSec = tSec;

    // 3) Send once per second (regardless of validity)
    //    This block will just be skipped if WiFi is not connected.
    if (WiFi.status() == WL_CONNECTED && now - lastSendMs >= SEND_PERIOD_MS) {
      lastSendMs = now;
      String url = sensorHubURL + "?distance=" + String((int)dist) +
                   "&time=" + String(tSec, 1);
      Serial.print("HTTP GET: "); Serial.println(url);
      int code; String body;
      if (httpGet(url, code, body)) {
        Serial.printf("Response: %d\n", code);
      } else {
        // This will now correctly show hub errors, not our own 404s
        Serial.printf("HTTP error: %d \"%s\"\n", code, body.c_str());
      }
    }
  }

  delay(1);
}

// =============== I2C / Sensor helpers ===============
void i2cInit() {
// ... (existing code, no changes) ...
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
// ... (existing code, no changes) ...
  // Try a modest number of attempts; don't loop forever
  for (int attempt = 0; attempt < 3; ++attempt) {
    Serial.printf("[VL53] init attempt %d\n", attempt + 1);
    if (sensor.init()) {
      Serial.println("[VL53] init OK");
      sensor.setTimeout(WIRE_TIMEOUT_MS);      // library-side timeout
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
// ... (existing code, no changes) ...
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
// ... (existing code, no changes) ...
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
// ... (existing code, no changes) ...
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

void checkWifi() {
  unsigned long now = millis();
  if (now < g_wifiNextActionMs) return;

  if (WiFi.status() == WL_CONNECTED) {
    g_wifiState = WF_IDLE;
    g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
    return;
  }

  switch (g_wifiState) {
    case WF_IDLE:
      if (g_lastKnownSsid != "") {
        Serial.println("[NET] Connecting to known hub: " + g_lastKnownSsid);
        WiFi.disconnect(false, false);
        WiFi.begin(g_lastKnownSsid.c_str(), g_lastKnownSsid.c_str());
        g_wifiState = WF_CONNECTING;
        g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
      } else {
        Serial.println("[NET] Starting async hub scan...");
        WiFi.scanDelete();
        WiFi.scanNetworks(true, true);
        g_wifiState = WF_SCANNING;
        g_wifiNextActionMs = now + 100;
      }
      break;

    case WF_SCANNING: {
      int n = WiFi.scanComplete();
      if (n == -1) {
        g_wifiNextActionMs = now + 100;
        break;
      }

      String ssidToTry = "";
      if (n > 0) {
        for (int i = 0; i < n; i++) {
          String ssid = WiFi.SSID(i);
          if (ssid == SSID_A || ssid == SSID_B) {
            ssidToTry = ssid;
            g_lastKnownSsid = ssid;
            break;
          }
        }
      }
      WiFi.scanDelete();

      if (ssidToTry != "") {
        Serial.println("[NET] Hub found: " + ssidToTry + ". Connecting STA.");
        WiFi.disconnect(false, false);
        WiFi.begin(ssidToTry.c_str(), ssidToTry.c_str());
        g_wifiState = WF_CONNECTING;
      } else {
        Serial.println("[NET] Hub not found. Keeping local AP active.");
        g_wifiState = WF_IDLE;
      }
      g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
      break;
    }

    case WF_CONNECTING:
      Serial.println("[NET] Connect attempt timed out. Will scan again later.");
      g_lastKnownSsid = "";
      g_wifiState = WF_IDLE;
      g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
      break;
  }
}

bool httpGet(const String& url, int& code, String& body) {
// ... (existing code, no changes) ...
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


// ======================= NEW: Config Helpers =======================

/**
 * @brief Simple helper to find a numeric value for a given key in a JSON string.
 * Example: getJsonValue("{\"foo\": 123}", "foo") -> 123
 */
long getJsonValue(String json, String key) {
  String searchKey = "\"" + key + "\":";
  int keyIndex = json.indexOf(searchKey);
  if (keyIndex == -1) {
    searchKey = "\"" + key + "\" :"; // try with space
    keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) return -1; // Key not found
  }

  int valueIndex = keyIndex + searchKey.length();
  
  // Find the end of the number (comma or closing brace)
  int endIndex = json.indexOf(',', valueIndex);
  if (endIndex == -1) {
    endIndex = json.indexOf('}', valueIndex);
  }
  if (endIndex == -1) return -1; // Malformed

  String valueStr = json.substring(valueIndex, endIndex);
  valueStr.trim(); // Remove whitespace
  
  return valueStr.toInt(); // Convert to long
}

/**
 * @brief Parses a JSON string and updates global config variables.
 */
void processConfigUpdate(String payload) {
  // We use this flag to see if any value was successfully parsed
  bool updated = false;
  long val = 0;

  val = getJsonValue(payload, "sample_period");
  if (val > 0) { SAMPLE_PERIOD_MS = val; updated = true; Serial.printf("Set SAMPLE_PERIOD_MS = %lu\n", val); }

  val = getJsonValue(payload, "send_period");
  if (val > 0) { SEND_PERIOD_MS = val; updated = true; Serial.printf("Set SEND_PERIOD_MS = %lu\n", val); }

  val = getJsonValue(payload, "cooldown_soft");
  if (val > 0) { COOLDOWN_SOFT_MS = val; updated = true; Serial.printf("Set COOLDOWN_SOFT_MS = %lu\n", val); }

  val = getJsonValue(payload, "cooldown_bus");
  if (val > 0) { COOLDOWN_BUS_MS = val; updated = true; Serial.printf("Set COOLDOWN_BUS_MS = %lu\n", val); }

  val = getJsonValue(payload, "cooldown_xshut");
  if (val > 0) { COOLDOWN_XSHUT_MS = val; updated = true; Serial.printf("Set COOLDOWN_XSHUT_MS = %lu\n", val); }

  val = getJsonValue(payload, "l1_reinit");
  if (val > 0) { L1_SOFT_REINIT = val; updated = true; Serial.printf("Set L1_SOFT_REINIT = %d\n", (int)val); }

  val = getJsonValue(payload, "l2_clear");
  if (val > 0) { L2_BUS_CLEAR = val; updated = true; Serial.printf("Set L2_BUS_CLEAR = %d\n", (int)val); }

  val = getJsonValue(payload, "l3_xshut");
  if (val > 0) { L3_XSHUT = val; updated = true; Serial.printf("Set L3_XSHUT = %d\n", (int)val); }

  if (!updated) {
    Serial.println("Failed to parse any valid keys from payload.");
  }
}

/**
 * @brief Returns a JSON string of the current configuration.
 */
String getConfigAsJson() {
  String json = "{";
  json += "\"sample_period\":" + String(SAMPLE_PERIOD_MS);
  json += ",\"send_period\":" + String(SEND_PERIOD_MS);
  json += ",\"cooldown_soft\":" + String(COOLDOWN_SOFT_MS);
  json += ",\"cooldown_bus\":" + String(COOLDOWN_BUS_MS);
  json += ",\"cooldown_xshut\":" + String(COOLDOWN_XSHUT_MS);
  json += ",\"l1_reinit\":" + String(L1_SOFT_REINIT);
  json += ",\"l2_clear\":" + String(L2_BUS_CLEAR);
  json += ",\"l3_xshut\":" + String(L3_XSHUT);
  json += "}";
  return json;
}

// ======================= Debug =======================
void i2cScanOnce(const char* tag) {
// ... (existing code, no changes) ...
  Serial.printf("%s I2C scan...\n", tag);
  uint8_t count = 0;
  for (uint8_t a = 1; a < 127; a++) {
    Wire.beginTransmission(a);
    if (Wire.endTransmission() == 0) { Serial.printf(" - 0x%02X\n", a); count++; }
    delay(2);
  }
  if (count == 0) Serial.println(" - no devices");
}
