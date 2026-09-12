/*************************************************************
 * Distance Sensor Client – Continuous Mode & Dual-SSID Support
 *
 * - Uses Adafruit VL53L0X in continuous mode (200 ms timing budget)
 * - Averages 5 samples per second
 * - Scans for WiFi SSIDs "ModuloTECNAL_1" or "ModuloTECNAL_2"
 *   and uses the SSID as the password
 * - Updates SENSOR_HUB_URL based on connected SSID
 * - Retries sensor initialization on failure or stagnant readings
 *************************************************************/

#include <Arduino.h>
#include <WiFi.h>
#include <HTTPClient.h>
#include <Wire.h>
#include <Adafruit_VL53L0X.h>
#include <math.h>  // for fabs()

// ----- Dynamic WiFi management -----
String currentSSID      = "";
String currentPassword  = "";
String sensorHubURL     = "http://192.168.4.1/distance";  // default hub URL

// ----- Sensor object -----
Adafruit_VL53L0X lox = Adafruit_VL53L0X();

// ----- Timing constants (ms) -----
const unsigned long sampleInterval        = 200;   // 5 Hz sampling
const unsigned long distanceInterval      = 1000;  // 1 s averaging window
const unsigned long sendInterval          = 1000;  // 1 s HTTP send window
const unsigned long wifiReconnectInterval = 5000;  // WiFi retry every 5 s
const int           stagnantThreshold     = 20;    // reinit if 20 identical averages

// ----- Runtime variables -----
unsigned long lastSampleMillis        = 0;
unsigned long lastDistanceMillis      = 0;
unsigned long lastSendMillis          = 0;
unsigned long lastWiFiReconnectMillis = 0;

long sampleSum    = 0;
int  sampleCount  = 0;

static float lastGoodDistance = -1.0;
static int   stagnantCount    = 0;

// Forward declarations
void ensureSensorInitialized();
void connectToWiFi();

void setup() {
  Serial.begin(115200);
  delay(500);
  Serial.println("Distance Sensor Client Starting...");
  
  // Initialize sensor
  ensureSensorInitialized();

  // Configure WiFi
  WiFi.mode(WIFI_STA);
  connectToWiFi();
}

void loop() {
  unsigned long now = millis();

  // 1) Read one sample every 200 ms
  if (now - lastSampleMillis >= sampleInterval) {
    lastSampleMillis = now;
    VL53L0X_RangingMeasurementData_t meas;
    lox.rangingTest(&meas, false);
    sampleSum   += meas.RangeMilliMeter;
    sampleCount += 1;
  }

  // 2) Every 1 s, compute average & handle stagnation
  if (now - lastDistanceMillis >= distanceInterval) {
    lastDistanceMillis = now;

    if (sampleCount > 0) {
      int avgDist      = sampleSum / sampleCount;
      float distValue  = avgDist - 20.0;  // subtract 20 mm offset

      // Check for stagnant readings
      if (lastGoodDistance < 0.0) {
        lastGoodDistance = distValue;
        stagnantCount    = 0;
      } else {
        if (fabs(distValue - lastGoodDistance) < 1.0) {
          stagnantCount++;
          Serial.print("Stagnant count: ");
          Serial.println(stagnantCount);
        } else {
          stagnantCount = 0;
        }
        lastGoodDistance = distValue;
      }

      // Reinitialize if stagnant too long
      if (stagnantCount >= stagnantThreshold) {
        Serial.println("Stagnant readings; reinitializing sensor...");
        ensureSensorInitialized();
        stagnantCount = 0;
      }

      // Output JSON to Serial
      float tSec = now / 1000.0;
      Serial.printf("{\"time\":%.1f,\"distance\":%.0f}\n", tSec, distValue);

      // 3) Send to hub if WiFi connected
      if (WiFi.status() == WL_CONNECTED && now - lastSendMillis >= sendInterval) {
        lastSendMillis = now;
        if (stagnantCount == 0) {
          String url = sensorHubURL + "?distance=" + String((int)distValue)
                       + "&time=" + String(tSec, 1);
          Serial.print("HTTP GET: ");
          Serial.println(url);
          HTTPClient http;
          http.begin(url);
          http.setTimeout(500);
          int code = http.GET();
          Serial.print("Response: ");
          Serial.println(code);
          http.end();
        }
      }
    } else {
      Serial.println("No samples; reinitializing sensor...");
      ensureSensorInitialized();
    }

    // Reset accumulators
    sampleSum   = 0;
    sampleCount = 0;
  }

  // 4) Periodic WiFi reconnection
  if (now - lastWiFiReconnectMillis >= wifiReconnectInterval) {
    lastWiFiReconnectMillis = now;
    if (WiFi.status() != WL_CONNECTED) {
      connectToWiFi();
    }
  }

  delay(1);  // small yield
}


//-----------------------------------------------------------------------------
// Ensures VL53L0X is active in continuous mode with 200 ms timing budget.
// Retries until successful.
//-----------------------------------------------------------------------------
void ensureSensorInitialized() {
  Serial.println("Initializing VL53L0X...");
  while (!lox.begin()) {
    Serial.println("VL53L0X init failed; retrying in 1 s...");
    delay(1000);
  }
  Serial.println("VL53L0X initialized.");
  lox.setMeasurementTimingBudgetMicroSeconds(200000);
  lox.startRangeContinuous();
}


//-----------------------------------------------------------------------------
// Scans for SSID "ModuloTECNAL_1" or "_2", uses SSID as password,
// attempts connection, and updates sensorHubURL accordingly.
//-----------------------------------------------------------------------------
void connectToWiFi() {
  Serial.println("Scanning for target WiFi...");
  int n = WiFi.scanNetworks();
  if (n <= 0) {
    Serial.println("No networks found.");
    return;
  }

  bool found = false;
  for (int i = 0; i < n; i++) {
    String ssid = WiFi.SSID(i);
    if (ssid == "ModuloTECNAL_1" || ssid == "ModuloTECNAL_2") {
      currentSSID     = ssid;
      currentPassword = ssid;  // password = SSID
      found = true;
      break;
    }
  }

  if (!found) {
    Serial.println("Target SSID not detected.");
    return;
  }

  Serial.print("Connecting to ");
  Serial.println(currentSSID);
  WiFi.begin(currentSSID.c_str(), currentPassword.c_str());

  unsigned long start = millis();
  const unsigned long timeout = 5000;
  while (WiFi.status() != WL_CONNECTED && millis() - start < timeout) {
    delay(500);
    Serial.print(".");
  }
  Serial.println();

  if (WiFi.status() == WL_CONNECTED) {
    Serial.print("Connected, IP: ");
    Serial.println(WiFi.localIP());
    // Adjust hub URL per module if needed
    if (currentSSID == "ModuloTECNAL_1") {
      sensorHubURL = "http://192.168.4.1/distance";
    } else {
      sensorHubURL = "http://192.168.4.2/distance";
    }
  } else {
    Serial.println("WiFi connection failed.");
  }
}
