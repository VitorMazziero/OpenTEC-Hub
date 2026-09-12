/**
 * @file main.cpp
 * @brief Firmware for a VEML7700-based biomass sensor. (v2.2)
 *
 * This firmware controls an LED emitter via PWM and reads an ALS (Ambient Light
 * Sensor) to calculate absorbance. It features auto-ranging for both sensor
 * integration time (IT) and LED PWM duty cycle to maintain an optimal
 * signal-to-noise ratio.
 *
 * All settings and blanking calibration data are stored in Non-Volatile
 * Storage (NVS) on the ESP32.
 *
 * NETWORKING:
 * - Runs in AP+STA mode.
 * - AP: "BiomassSensor" (no password) on 192.168.6.1.
 * - Serves data on GET /readData (Minimal JSON)
 * - Accepts commands on POST /command
 * - STA: Connects to Sensor Hub ("ModuloTECNAL_1" or "ModuloTECNAL_2").
 * - Pushes data to http://192.168.4.1/biomassData (Minimal parameters)
 * - Pulls commands from http://192.168.4.1/biomassCommand
 *
 * CHANGELOG (v2.2):
 * - FIXED: `processJsonCommand` now correctly parses "key":value commands
 * (e.g., {"blank":1}) in addition to {"command":"blank"}. This
 * fixes the bug where commands were ignored and readings were zero.
 * - CHANGED: `sendDataToHub` no longer sends blank, searching, or
 * measuring parameters to reduce network traffic.
 * - CHANGED: `handleReadData` (for AP clients) now sends a minimal
 * JSON object, matching the data sent to the hub. The main loop's
 * `Serial.println` still prints the full debug JSON.
 */

#include <Arduino.h>
#include <Wire.h>
#include <Preferences.h>      // For ESP32 Non-Volatile Storage (NVS)
#include <esp_task_wdt.h>     // For Watchdog Timer
#include <algorithm>          // For std::sort (median filter)
#include <cstring>            // For memcpy (median filter)

// --- NEW: Networking Includes ---
#include <WiFi.h>
#include <HTTPClient.h>
#include <WebServer.h>        // Using simple WebServer for AP

// ==========================
// Sensor Configuration
// ==========================

// --- PWM ---
constexpr int LED_PWM_PIN   = 18;     // GPIO pin for LED PWM control
constexpr int LEDC_FREQ_HZ  = 2000;   // PWM frequency in Hz
constexpr int LEDC_RES_BITS = 8;      // PWM resolution (8-bit = 0-255)

// --- LED Test Sweep ---
bool     g_ledTestEnable   = false;
float    g_ledTestPct      = 0.0f;
int      g_ledTestDir      = 1;
uint32_t g_ledTestPeriodMs = 50;
uint32_t g_ledTestNextMs   = 0;

// --- I2C ---
constexpr int I2C_SDA_PIN = 8;
constexpr int I2C_SCL_PIN = 9;

// --- VEML7700 Sensor ---
constexpr uint8_t VEML7700_ADDR   = 0x10;
constexpr uint8_t REG_ALS_CONF    = 0x00;
constexpr uint8_t REG_ALS_DATA_L  = 0x04;
constexpr uint8_t REG_ALS_DATA_H  = 0x05;
constexpr uint16_t VEML_GAIN_2    = (0b01 << 11); // Fix gain at 2x
constexpr uint16_t VEML_ALS_ENABLE = 0x0000;      // Bit 0 = 0 to enable
constexpr uint16_t SATURATION_RAW = 65530;      // Sensor saturation threshold

// ==========================
// NEW: Networking Configuration
// ==========================
// --- Hub (STA) Config ---
static const char* HUB_SSID_A = "ModuloTECNAL_1";
static const char* HUB_SSID_B = "ModuloTECNAL_2";
String sensorHubDataURL = "http://192.168.4.1/biomassData";
String sensorHubCommandURL = "http://192.168.4.1/biomassCommand";

// --- Access Point (AP) Config ---
static const char* AP_SSID = "BiomassSensor";
IPAddress apIP(192, 168, 6, 1); // Use a different subnet from the hub (192.168.4.x)
IPAddress apGateway(192, 168, 6, 1);
IPAddress apSubnet(255, 255, 255, 0);

WebServer server(80); // Web server for our own AP
HTTPClient http;      // Client for communicating with the hub

// --- Networking Globals ---
String g_lastDataJson = "{}"; // Holds the latest FULL JSON data string (for serial debug)
String g_lastKnownSsid = "";
unsigned long lastWifiCheckMs = 0;
unsigned long WIFI_RECONNECT_PERIOD_MS = 10000; // Check every 10 seconds
unsigned long lastHubPollMs = 0;
unsigned long HUB_POLL_PERIOD_MS = 2000; // Poll hub every 2 seconds

// ==========================
// Global Definitions
// ==========================
// ... (rest of the existing globals are unchanged) ...
// --- Watchdog Timer ---
constexpr int WDT_TIMEOUT_S = 10; // 10-second watchdog

// --- State Machine ---
enum SystemState {
  IDLE,
  BLANKING,
  MEASURING,
  SEARCHING // State for finding the best IT/PWM gear
};
SystemState g_state = IDLE;

// ... (DeviceConfig struct is unchanged) ...
struct DeviceConfig {
  uint16_t LOW_THRESHOLD_RAW;
  uint16_t HIGH_THRESHOLD_RAW;
  uint16_t OPTIMAL_TARGET_RAW;

  static const int IT_COUNT = 4;
  uint16_t itSettings[IT_COUNT];      // VEML7700 register values for IT
  uint32_t itDelays[IT_COUNT];        // Wait time in ms for each IT
  uint32_t itRefreshTimes[IT_COUNT]; // Poll interval for each IT

  static const int PWM_COUNT = 8;
  float pwmSettings[PWM_COUNT];       // % duty cycle levels

  uint32_t crc32; // Checksum for config integrity
};
DeviceConfig g_config; // The "live" global config

// --- NVS (Non-Volatile Storage) ---
Preferences g_prefs;
constexpr char* NVS_NAMESPACE  = "biomass_sensor";
constexpr char* NVS_KEY_CONFIG = "config";
constexpr char* NVS_KEY_BLANK  = "blanking";

// ... (BlankingData struct is unchanged) ...
struct BlankingData {
  uint16_t blankValues[DeviceConfig::IT_COUNT][DeviceConfig::PWM_COUNT];
  uint32_t timestamp;
  uint32_t crc32;
};
BlankingData g_blankingData; // The "live" global blanking data

// ... (Filter and Runtime Globals are unchanged) ...
// --- Smoothing Filter ---
constexpr int FILTER_WINDOW_SIZE = 5;
uint16_t g_readingWindow[FILTER_WINDOW_SIZE] = {0};
int      g_readingWindowIndex                = 0;
bool     g_filterIsPrimed                    = false;

// --- Runtime Globals ---
volatile float g_targetPct     = 0.0f; // Last commanded duty cycle %
uint16_t g_lastAlsRaw          = 0;    // The *filtered* sensor reading
float    g_lastAbsorbance      = 0.0f;
int      g_currentItIndex      = 0;
int      g_currentPwmIndex     = 0;
bool     g_blankIsDone         = false;
uint32_t g_nextReadTime        = 0;

// --- Health Metrics ---
uint32_t g_saturationCount = 0;
uint32_t g_i2cErrorCount   = 0;
uint32_t g_sensorResets    = 0;


// ==========================
// CRC32 Helper
// ==========================
// ... (calculateCRC32 function is unchanged) ...
uint32_t calculateCRC32(const uint8_t *data, size_t length) {
  uint32_t crc = 0;
  for (size_t i = 0; i < length; i++) {
    crc += data[i];
  }
  return crc;
}

// ==========================
// Median Reading Filter
// ==========================
// ... (resetReadingFilter and getFilteredReading functions are unchanged) ...
void resetReadingFilter() {
  g_filterIsPrimed     = false;
  g_readingWindowIndex = 0;
  for (int i = 0; i < FILTER_WINDOW_SIZE; i++) {
    g_readingWindow[i] = 0;
  }
}
uint16_t getFilteredReading(uint16_t newReading) {
  if (!g_filterIsPrimed) {
    // Prime the filter with the first reading
    for (int i = 0; i < FILTER_WINDOW_SIZE; i++) {
      g_readingWindow[i] = newReading;
    }
    g_filterIsPrimed = true;
    return newReading;
  }

  // Add new reading to the circular buffer
  g_readingWindow[g_readingWindowIndex] = newReading;
  g_readingWindowIndex                = (g_readingWindowIndex + 1) % FILTER_WINDOW_SIZE;

  // Create a temporary copy for sorting
  uint16_t sortedWindow[FILTER_WINDOW_SIZE];
  memcpy(sortedWindow, g_readingWindow, sizeof(g_readingWindow));

  // Sort the temporary array
  std::sort(sortedWindow, sortedWindow + FILTER_WINDOW_SIZE);

  // Return the median value
  return sortedWindow[FILTER_WINDOW_SIZE / 2];
}

// ==========================
// Fault Handling
// ==========================
// ... (resetSensor and handleI2CError functions are unchanged) ...
bool resetSensor() {
  g_sensorResets++;
  Serial.println("! FATAL: Attempting I2C sensor reset...");
  Wire.end();
  delay(100);
  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
  Wire.setClock(100000);
  delay(5);

  // Set to default state
  uint16_t config_val = VEML_GAIN_2 | g_config.itSettings[0] | VEML_ALS_ENABLE;

  Wire.beginTransmission(VEML7700_ADDR);
  Wire.write(REG_ALS_CONF);
  Wire.write(config_val & 0xFF);
  Wire.write((config_val >> 8) & 0xFF);
  if (Wire.endTransmission() == 0) {
    Serial.println("... Sensor reset successful.");
    g_currentItIndex = 0;
    resetReadingFilter();
    return true;
  } else {
    Serial.println("... Sensor reset FAILED.");
    return false;
  }
}
void handleI2CError() {
  g_i2cErrorCount++;
  Serial.print("! I2C Read Error. Total errors: ");
  Serial.println(g_i2cErrorCount);

  if (g_i2cErrorCount % 5 == 0) { // Try to reset sensor every 5 errors
    resetSensor();
  }
  g_state = IDLE; // Drop to idle on error
  Serial.println("Dropping to IDLE state. Send JSON '{\"command\":\"start\"}' to retry.");
}

// ==========================
// VEML7700 Sensor Helpers
// ==========================
// ... (vemlWrite16, vemlRead16, vemlSetConfig functions are unchanged) ...
bool vemlWrite16(uint8_t reg, uint16_t val) {
  Wire.beginTransmission(VEML7700_ADDR);
  Wire.write(reg);
  Wire.write(val & 0xFF);
  Wire.write((val >> 8) & 0xFF);
  if (Wire.endTransmission() != 0) {
    return false; // I2C Error
  }
  return true;
}
bool vemlRead16(uint8_t reg, uint16_t &value) {
  Wire.beginTransmission(VEML7700_ADDR);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return false; // I2C Error
  if (Wire.requestFrom(VEML7700_ADDR, static_cast<uint8_t>(2)) != 2) return false; // I2C Error
  uint16_t lo = Wire.read();
  uint16_t hi = Wire.read();
  value       = static_cast<uint16_t>((hi << 8) | lo);
  return true;
}
bool vemlSetConfig(int itIndex) {
  if (itIndex < 0 || itIndex >= g_config.IT_COUNT) return false;
  g_currentItIndex = itIndex;
  uint16_t config_val =
      VEML_GAIN_2 | g_config.itSettings[itIndex] | VEML_ALS_ENABLE;

  if (!vemlWrite16(REG_ALS_CONF, config_val)) {
    handleI2CError();
    return false;
  }

  resetReadingFilter(); // Reset filter on any "gear change"
  delay(g_config.itDelays[itIndex] + 5); // Wait for integration to apply
  return true;
}

// ==========================
// PWM Helpers
// ==========================
// ... (pwmSetDutyPercent and pwmSetLevel functions are unchanged) ...
void pwmSetDutyPercent(float percent) {
  if (percent < 0.0f) percent = 0.0f;
  if (percent > 100.0f) percent = 100.0f;

  // Calculate duty value based on the configured resolution
  const uint32_t maxCount = (1u << LEDC_RES_BITS) - 1u;
  const uint32_t duty =
      static_cast<uint32_t>(percent * maxCount / 100.0f + 0.5f);

  // Write the value to the pin
  analogWrite(LED_PWM_PIN, duty);
  g_targetPct = percent;
}
void pwmSetLevel(int pwmIndex) {
  if (pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) return;
  g_currentPwmIndex = pwmIndex;
  pwmSetDutyPercent(g_config.pwmSettings[pwmIndex]);
  resetReadingFilter(); // Reset filter on any "gear change"
}

// ==========================
// NVS (Storage) Functions
// ==========================
// ... (loadConfig, saveConfig, loadBlankingData, saveBlankingData functions are unchanged) ...
void loadConfig() {
  if (g_prefs.getBytes(NVS_KEY_CONFIG, &g_config, sizeof(DeviceConfig)) ==
      sizeof(DeviceConfig)) {
    // Calculate CRC
    uint32_t crc = calculateCRC32((uint8_t*)&g_config,
                                  sizeof(DeviceConfig) - sizeof(uint32_t));
    if (crc == g_config.crc32) {
      Serial.println("Loaded config from NVS.");
      return;
    }
  }

  Serial.println("No valid config in NVS. Loading defaults.");
  g_config.LOW_THRESHOLD_RAW  = 10000;
  g_config.HIGH_THRESHOLD_RAW = 40000;
  g_config.OPTIMAL_TARGET_RAW = 25000;

  g_config.itSettings[0] = (0b00000 << 6); // 100ms
  g_config.itSettings[1] = (0b00100 << 6); // 200ms
  g_config.itSettings[2] = (0b01000 << 6); // 400ms
  g_config.itSettings[3] = (0b01100 << 6); // 800ms

  g_config.itDelays[0] = 100;
  g_config.itDelays[1] = 200;
  g_config.itDelays[2] = 400;
  g_config.itDelays[3] = 800;

  g_config.itRefreshTimes[0] = 600;
  g_config.itRefreshTimes[1] = 700;
  g_config.itRefreshTimes[2] = 900;
  g_config.itRefreshTimes[3] = 1300;

  g_config.pwmSettings[0] = 5.0f;
  g_config.pwmSettings[1] = 10.0f;
  g_config.pwmSettings[2] = 15.0f;
  g_config.pwmSettings[3] = 20.0f;
  g_config.pwmSettings[4] = 25.0f;
  g_config.pwmSettings[5] = 50.0f;
  g_config.pwmSettings[6] = 75.0f;
  g_config.pwmSettings[7] = 100.0f;
}
void saveConfig() {
  g_config.crc32 = calculateCRC32((uint8_t*)&g_config,
                                  sizeof(DeviceConfig) - sizeof(uint32_t));
  if (g_prefs.putBytes(NVS_KEY_CONFIG, &g_config, sizeof(DeviceConfig))) {
    Serial.println("Config saved to NVS.");
  } else {
    Serial.println("Error saving config to NVS.");
  }
}
bool loadBlankingData() {
  if (g_prefs.getBytes(NVS_KEY_BLANK, &g_blankingData, sizeof(BlankingData)) ==
      sizeof(BlankingData)) {
    uint32_t crc = calculateCRC32(
        (uint8_t*)&g_blankingData, sizeof(BlankingData) - sizeof(uint32_t));
    if (crc == g_blankingData.crc32) {
      Serial.print("Loaded blanking data from NVS (Timestamp: ");
      Serial.print(g_blankingData.timestamp);
      Serial.println(")");
      g_blankIsDone = true;
      return true;
    }
  }
  Serial.println("No valid blanking data in NVS.");
  g_blankIsDone = false;
  return false;
}
void saveBlankingData() {
  g_blankingData.timestamp = millis(); // Simple timestamp
  g_blankingData.crc32 = calculateCRC32(
      (uint8_t*)&g_blankingData, sizeof(BlankingData) - sizeof(uint32_t));
  if (g_prefs.putBytes(NVS_KEY_BLANK, &g_blankingData, sizeof(BlankingData))) {
    Serial.println("Blanking data saved to NVS.");
  } else {
    Serial.println("Error saving blanking data to NVS.");
  }
}

// ==========================
// Blanking Routine
// ==========================
// ... (runBlankingRoutine function is unchanged, though its Serial.printlns are updated) ...
void runBlankingRoutine() {
  g_state = BLANKING;
  Serial.println("--- Starting Blanking Routine ---");
  Serial.println("Ensure clear media is circulating. This will take a moment...");
  esp_task_wdt_reset();

  for (int i = 0; i < g_config.IT_COUNT; i++) {
    if (!vemlSetConfig(i)) {
      g_state = IDLE;
      return;
    } // I2C Error
    Serial.print("Setting IT: ");
    Serial.print(g_config.itDelays[i]);
    Serial.println("ms");
    esp_task_wdt_reset();
    bool isSaturated = false;

    for (int j = 0; j < g_config.PWM_COUNT; j++) {
      esp_task_wdt_reset();
      uint16_t reading = 0;
      if (isSaturated) {
        reading = 65535; // If saturated, mark all subsequent PWMs as saturated
      } else {
        pwmSetLevel(j);                    // Set PWM
        delay(g_config.itRefreshTimes[i]); // Wait

        uint16_t rawReading;
        if (!vemlRead16(REG_ALS_DATA_L, rawReading)) {
          handleI2CError();
          g_state = IDLE;
          return;
        }
        reading = rawReading;

        if (reading >= SATURATION_RAW) {
          isSaturated = true;
          reading     = 65535;
        }
      }

      g_blankingData.blankValues[i][j] = reading;
      Serial.print("  PWM ");
      Serial.print(g_config.pwmSettings[j]);
      Serial.print("%: RAW = ");
      Serial.println(reading);
    }
  }

  saveBlankingData(); // Save to NVS

  // Restore default state
  pwmSetDutyPercent(0.0f);
  vemlSetConfig(0);
  pwmSetLevel(0);
  g_blankIsDone = true;
  g_state       = IDLE;

  Serial.println("--- Blanking Complete ---");
  Serial.println("Send JSON '{\"command\":\"start\"}' to begin measurement.");
}

// ==========================
// Auto-Ranging Measurement
// ==========================
// ... (findAndSetBestNewGear function is unchanged) ...
void findAndSetBestNewGear() {
  Serial.println("Reading low. Pausing to find optimal new gear...");
  g_state = SEARCHING;
  esp_task_wdt_reset();

  int bestGearIT  = -1;
  int bestGearPWM = -1;
  long bestScore  = -1000000;

  for (int it = g_currentItIndex; it < g_config.IT_COUNT; it++) {
    esp_task_wdt_reset();
    bool isSaturatedThisIT = false;
    int startPwm = 0;
    if (it == g_currentItIndex) {
      startPwm = g_currentPwmIndex + 1;
    }
    if (startPwm >= g_config.PWM_COUNT) continue;

    for (int pwm = startPwm; pwm < g_config.PWM_COUNT; pwm++) {
      esp_task_wdt_reset();
      if (!vemlSetConfig(it)) {
        g_state = IDLE;
        return;
      } // I2C Error
      pwmSetLevel(pwm);
      delay(g_config.itRefreshTimes[it]);

      uint16_t newReading;
      if (!vemlRead16(REG_ALS_DATA_L, newReading)) {
        handleI2CError();
        g_state = IDLE;
        return;
      }

      Serial.print("  Testing IT ");
      Serial.print(g_config.itDelays[it]);
      Serial.print("ms, PWM ");
      Serial.print(g_config.pwmSettings[pwm]);
      Serial.print("%: RAW = ");
      Serial.println(newReading);

      if (newReading >= SATURATION_RAW) {
        Serial.println("  Saturated. Skipping rest of this IT level.");
        g_saturationCount++;
        isSaturatedThisIT = true;
        break; // Stop iterating PWMs for this IT
      }

      if (newReading > g_config.LOW_THRESHOLD_RAW) {
        // Score is the negative absolute difference from optimal
        long score = -abs(g_config.OPTIMAL_TARGET_RAW - newReading);
        if (score > bestScore) {
          bestScore   = score;
          bestGearIT  = it;
          bestGearPWM = pwm;
        }
      }
    }

    if (isSaturatedThisIT && bestGearIT == -1) {
      // If we saturated and *still* haven't found a good gear
      continue;
    }
  }

  if (bestGearIT != -1) {
    Serial.print("Jumping to new gear: IT ");
    Serial.print(g_config.itDelays[bestGearIT]);
    Serial.print("ms, PWM ");
    Serial.print(g_config.pwmSettings[bestGearPWM]);
    Serial.println("%");
    vemlSetConfig(bestGearIT);
    pwmSetLevel(bestGearPWM);
  } else {
    // We searched everything and all readings are still low
    Serial.println("At max sensitivity, but reading is still low.");
    int it  = g_config.IT_COUNT - 1;
    int pwm = g_config.PWM_COUNT - 1;
    vemlSetConfig(it);
    pwmSetLevel(pwm);
  }

  g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
  g_state        = MEASURING;
}

/**
 * @brief Performs a single measurement cycle.
 * Reads the sensor, applies the median filter, runs auto-ranging
 * logic, and calculates absorbance.
 *
 * NOTE: This function NO LONGER prints to serial. It just updates
 * globals. The main loop's periodic sender will handle logging/sending.
 */
void runMeasurementLoop() {
  uint16_t rawReading;
  if (!vemlRead16(REG_ALS_DATA_L, rawReading)) {
    handleI2CError();
    return;
  }

  if (rawReading >= SATURATION_RAW) {
    g_saturationCount++;
  }

  // Get the filtered (median) reading
  g_lastAlsRaw = getFilteredReading(rawReading);

  // Wait for the filter to prime before doing calculations
  if (!g_filterIsPrimed) {
    g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
    return;
  }

  // --- Auto-Ranging Logic (uses filtered g_lastAlsRaw) ---
  bool configChanged = false;

  if (g_lastAlsRaw < g_config.LOW_THRESHOLD_RAW && g_lastAlsRaw > 0) {
    // Signal is too low, find a better (more sensitive) gear
    findAndSetBestNewGear();
    return; // Exit, findAndSetBestNewGear will change state
  } else if (g_lastAlsRaw > g_config.HIGH_THRESHOLD_RAW) {
    // Signal is too high (near saturation), shift down
    if (g_currentPwmIndex > 0) {
      pwmSetLevel(g_currentPwmIndex - 1);
      configChanged = true;
    } else if (g_currentItIndex > 0) {
      vemlSetConfig(g_currentItIndex - 1);
      pwmSetLevel(g_config.PWM_COUNT - 1); // Set to highest PWM at new IT
      configChanged = true;
    }
  }

  if (configChanged) {
    // If we changed gear, wait for a new reading before calculating
    g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
    return;
  }

  // --- Calculate and Store Absorbance ---
  uint16_t current_I0 =
      g_blankingData.blankValues[g_currentItIndex][g_currentPwmIndex];
  uint16_t current_I = g_lastAlsRaw;

  if (current_I0 == 0 || current_I0 == 65535) {
    g_lastAbsorbance = -99.0f; // Error: Blank is 0 or Saturated
  } else {
    if (current_I > current_I0) {
      current_I = current_I0; // Cap reading at blank value
    }
    if (current_I == 0) {
      g_lastAbsorbance = 9.9f; // Error: True zero reading
    } else {
      // Absorbance = -log10( I / I_0 )
      g_lastAbsorbance = -log10((float)current_I / (float)current_I0);
    }
  }

  // Schedule the next read
  g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
}

// =================================
// NEW: JSON Parsing/Networking Helpers
// =================================

/**
 * @brief Simple helper to find a numeric value for a given key in a JSON string.
 * Example: getJsonValue("{\"foo\": 123}", "foo") -> 123
 * Returns -999999 if key not found or value is not numeric.
 */
long getJsonValue(String json, String key) {
  String searchKey = "\"" + key + "\":";
  int keyIndex = json.indexOf(searchKey);
  if (keyIndex == -1) {
    searchKey = "\"" + key + "\" :"; // try with space
    keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) return -999999; // Key not found
  }

  int valueIndex = keyIndex + searchKey.length();
  
  // Skip whitespace
  while(valueIndex < json.length() && isspace(json.charAt(valueIndex))) {
    valueIndex++;
  }

  // Check if value is a string (starts with ")
  if (json.charAt(valueIndex) == '\"') {
    return -999999; // It's a string, not a number
  }

  // Find the end of the number (comma or closing brace)
  int endIndex = json.indexOf(',', valueIndex);
  if (endIndex == -1) {
    endIndex = json.indexOf('}', valueIndex);
  }
  if (endIndex == -1) return -999999; // Malformed

  String valueStr = json.substring(valueIndex, endIndex);
  valueStr.trim(); // Remove whitespace
  
  // Basic check if it's a number
  if (valueStr.length() == 0 || (!isDigit(valueStr.charAt(0)) && valueStr.charAt(0) != '-')) {
      return -999999;
  }
  
  return atol(valueStr.c_str()); // Use atol for long
}

/**
 * @brief Simple helper to find a string value for a given key in a JSON string.
 * Example: getJsonStringValue("{\"foo\": \"bar\"}", "foo") -> "bar"
 */
String getJsonStringValue(String json, String key) {
  String searchKey = "\"" + key + "\":\"";
  int keyIndex = json.indexOf(searchKey);
  if (keyIndex == -1) {
    searchKey = "\"" + key + "\" : \""; // try with spaces
    keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) return ""; // Key not found
  }

  int valueIndex = keyIndex + searchKey.length();
  int endIndex = json.indexOf('"', valueIndex);
  if (endIndex == -1) return ""; // Malformed

  return json.substring(valueIndex, endIndex);
}

/**
 * @brief Re-implementation of the serial handler logic, but using JSON.
 */
void processJsonCommand(String json) {
  Serial.println("[NET] Processing command: " + json);

  String cmd = getJsonStringValue(json, "command");
  
  // ---!!! FIX !!!---
  // If the {"command":"..."} format fails, check for the {"key":1} format.
  if (cmd.length() == 0) {
      if (getJsonValue(json, "blank") == 1) cmd = "blank";
      else if (getJsonValue(json, "start") == 1) cmd = "start";
      else if (getJsonValue(json, "stop") == 1) cmd = "stop";
      else if (getJsonValue(json, "print_blank") == 1) cmd = "print_blank";
      else if (getJsonValue(json, "print_config") == 1) cmd = "print_config";
      else if (getJsonValue(json, "print_health") == 1) cmd = "print_health";
      else if (getJsonValue(json, "save_config") == 1) cmd = "save_config";
      else if (getJsonValue(json, "load_config") == 1) cmd = "load_config";
      else if (getJsonValue(json, "test_on") == 1) cmd = "test_on";
      else if (getJsonValue(json, "test_off") == 1) cmd = "test_off";
  }
  // ---!!! END FIX !!!---


  if (cmd.length() > 0) {
    if (cmd.equals("blank")) {
      if (g_state == IDLE)
        runBlankingRoutine();
      else
        Serial.println("Error: Busy");
    } else if (cmd.equals("start")) {
      if (!g_blankIsDone)
        Serial.println("Error: Please run 'blank' first.");
      else if (g_state == IDLE) {
        Serial.println("--- Starting Measurement ---");
        g_state        = MEASURING;
        g_nextReadTime = millis();
        vemlSetConfig(0);
        pwmSetLevel(0);
        resetReadingFilter();
      }
    } else if (cmd.equals("stop")) {
      if (g_state == MEASURING || g_state == SEARCHING) {
        Serial.println("--- Stopping Measurement ---");
        g_state = IDLE;
        pwmSetDutyPercent(0.0f);
      }
    } else if (cmd.equals("print_blank")) {
      Serial.println("--- Stored Blanking Data (I_0) ---");
      for (int i = 0; i < g_config.IT_COUNT; i++) {
        Serial.print("IT ");
        Serial.print(g_config.itDelays[i]);
        Serial.println("ms:");
        for (int j = 0; j < g_config.PWM_COUNT; j++) {
          Serial.print("  PWM ");
          Serial.print(g_config.pwmSettings[j]);
          Serial.print("%: \t");
          Serial.println(g_blankingData.blankValues[i][j]);
        }
      }
    } else if (cmd.equals("print_config")) {
      Serial.println("--- Current Device Config ---");
      Serial.print("Low Thresh: ");
      Serial.println(g_config.LOW_THRESHOLD_RAW);
      Serial.print("High Thresh: ");
      Serial.println(g_config.HIGH_THRESHOLD_RAW);
      Serial.print("Opt. Target: ");
      Serial.println(g_config.OPTIMAL_TARGET_RAW);
    } else if (cmd.equals("print_health")) {
      Serial.println("--- System Health Metrics ---");
      Serial.print("I2C Errors: ");
      Serial.println(g_i2cErrorCount);
      Serial.print("Saturation Events: ");
      Serial.println(g_saturationCount);
      Serial.print("Sensor Resets: ");
      Serial.println(g_sensorResets);
    } else if (cmd.equals("save_config")) {
      saveConfig();
    } else if (cmd.equals("load_config")) {
      loadConfig();
    } else if (cmd.equals("test_on")) {
      if (g_state != IDLE) {
        Serial.println("Error: test allowed only in IDLE");
      } else {
        g_ledTestEnable = true;
        g_ledTestPct    = 0.0f;
        g_ledTestDir    = 1;
        g_ledTestNextMs = millis();
        Serial.println("LED test enabled");
      }
    } else if (cmd.equals("test_off")) {
      g_ledTestEnable = false;
      Serial.println("LED test disabled");
    }
  } // end if(cmd)

  // Check for numeric settings
  long val = getJsonValue(json, "low");
  if (val != -999999) {
    g_config.LOW_THRESHOLD_RAW = (uint16_t)val;
    Serial.print("Live config: LOW_THRESHOLD_RAW set to ");
    Serial.println(g_config.LOW_THRESHOLD_RAW);
  }

  val = getJsonValue(json, "high");
  if (val != -999999) {
    g_config.HIGH_THRESHOLD_RAW = (uint16_t)val;
    Serial.print("Live config: HIGH_THRESHOLD_RAW set to ");
    Serial.println(g_config.HIGH_THRESHOLD_RAW);
  }

  val = getJsonValue(json, "opt");
  if (val != -999999) {
    g_config.OPTIMAL_TARGET_RAW = (uint16_t)val;
    Serial.print("Live config: OPTIMAL_TARGET_RAW set to ");
    Serial.println(g_config.OPTIMAL_TARGET_RAW);
  }

  val = getJsonValue(json, "test_period");
  if (val != -999999) {
    if (val < 5) val = 5;
    g_ledTestPeriodMs = static_cast<uint32_t>(val);
    Serial.print("LED test period set to ");
    Serial.print(g_ledTestPeriodMs);
    Serial.println(" ms");
  }
}

/**
 * @brief Polls the Serial port and processes incoming JSON commands.
 */
void handleSerialInput() {
  static char buf[256];
  static size_t idx = 0;

  while (Serial.available() > 0) {
    int c = Serial.read();
    if (c == '\r') continue;
    if (c == '\n') {
      buf[idx] = '\0';
      idx      = 0;

      String cmd = String(buf);
      cmd.trim();
      if (cmd.length() > 0 && cmd.startsWith("{") && cmd.endsWith("}")) {
        processJsonCommand(cmd);
      } else if (cmd.length() > 0) {
        Serial.println("Error: Command must be in JSON format. e.g. {\"command\":\"start\"}");
      }
    } else {
      if (idx < sizeof(buf) - 1) {
        buf[idx++] = static_cast<char>(c);
      } else {
        idx = 0; // overflow guard
      }
    }
  }
}

/**
 * @brief Creates the FULL JSON data string from current globals (for serial debug).
 */
void buildDataJson() {
  String json = "{";
  json += "\"absorbance\":";
  json += String(g_lastAbsorbance, 3);
  json += ",\"raw\":";
  json += String(g_lastAlsRaw);
  json += ",\"it_ms\":";
  json += String(g_config.itDelays[g_currentItIndex]);
  json += ",\"pwm_pct\":";
  json += String(g_config.pwmSettings[g_currentPwmIndex], 1);
  json += ",\"blank_done\":";
  json += g_blankIsDone ? "true" : "false";
  json += ",\"searching\":";
  json += (g_state == SEARCHING) ? "true" : "false";
  json += ",\"measuring\":";
  json += (g_state == MEASURING) ? "true" : "false";
  json += "}";
  g_lastDataJson = json;
}

// --- NEW: HTTP GET helper (from distance sensor) ---
bool httpGet(const String& url, int& code, String& body) {
  http.begin(url);
  http.setReuse(false);
  http.setTimeout(1000); // 1 second timeout
  code = http.GET();
  if (code > 0) body = http.getString();
  else            body = String("err=") + code;
  http.end();
  return code >= 200 && code < 300;
}

/**
 * @brief Pushes the latest sensor data (as JSON) to the Sensor Hub.
 * --- MODIFIED: Sends minimal data as requested ---
 */
void sendDataToHub() {
  String url = sensorHubDataURL;
  url += "?absorbance=" + String(g_lastAbsorbance, 3);
  url += "&raw=" + String(g_lastAlsRaw);
  url += "&it=" + String(g_config.itDelays[g_currentItIndex]);
  url += "&pwm=" + String(g_config.pwmSettings[g_currentPwmIndex], 1);
  // REMOVED per user request:
  // url += "&blank=" + String(g_blankIsDone ? 1 : 0);
  // url += "&searching=" + String((g_state == SEARCHING) ? 1 : 0);
  // url += "&measuring=" + String((g_state == MEASURING) ? 1 : 0);

  int code;
  String body;
  if (httpGet(url, code, body)) {
    // Serial.println("Hub data send OK");
  } else {
    Serial.printf("Hub data send FAILED, code %d\n", code);
  }
}

/**
 * @brief Polls the Sensor Hub for pending commands.
 */
void pollHubForCommands() {
  int code;
  String body;
  if (httpGet(sensorHubCommandURL, code, body)) {
    if (body.length() > 0 && body != "{}") {
      processJsonCommand(body);
    }
  } else {
    Serial.printf("Hub command poll FAILED, code %d\n", code);
  }
}

// --- NEW: Non-blocking STA connect (from distance sensor) ---
void startStaConnectionAttempt() {
  String ssidToTry = g_lastKnownSsid;

  // If we don't have a known SSID, scan to find one.
  if (ssidToTry == "") {
    Serial.println("Scanning for target Hub WiFi (STA)...");
    int n = WiFi.scanNetworks(false, true);
    if (n <= 0) {
      Serial.println("No networks found (STA).");
      return;
    }
    for (int i = 0; i < n; i++) {
      String ssid = WiFi.SSID(i);
      if (ssid == HUB_SSID_A || ssid == HUB_SSID_B) {
        ssidToTry = ssid;
        g_lastKnownSsid = ssid; // Store it
        Serial.println("Found target network: " + ssidToTry);
        break;
      }
    }
  }
  if (ssidToTry == "") {
    Serial.println("Target Hub SSID (STA) not detected.");
    return;
  }
  Serial.printf("Attempting to connect to %s (STA)...\n", ssidToTry.c_str());
  WiFi.disconnect();
  delay(100);
  WiFi.begin(ssidToTry.c_str(), ssidToTry.c_str()); // password == SSID
}

// ==========================
// NEW: Web Server Handlers
// ==========================

/**
 * @brief Handles GET /readData - returns MINIMAL JSON data
 * --- MODIFIED: Sends minimal data as requested ---
 */
void handleReadData() {
  // Build the minimal JSON string for clients
  String json = "{";
  json += "\"absorbance\":";
  json += String(g_lastAbsorbance, 3);
  json += ",\"raw\":";
  json += String(g_lastAlsRaw);
  json += ",\"it_ms\":";
  json += String(g_config.itDelays[g_currentItIndex]);
  json += ",\"pwm_pct\":";
  json += String(g_config.pwmSettings[g_currentPwmIndex], 1);
  json += "}";
  
  server.send(200, "application/json", json); // Send the minimal JSON
}

/**
 * @brief Handles POST /command - accepts JSON commands
 */
void handleCommand() {
  if (server.hasArg("plain")) {
    String body = server.arg("plain");
    processJsonCommand(body);
    server.send(200, "text/plain", "Command processed");
  } else {
    server.send(400, "text/plain", "Bad Request - No Body");
  }
}

void handleNotFound() {
  server.send(404, "text/plain", "Not Found");
}


// ==========================
// Arduino Entry Points
// ==========================

void setup() {
  Serial.begin(115200);
  delay(50);
  Serial.println("--- Biomass Sensor Firmware v2.2 (Network Enabled) ---");

  // Load config and blanking data from storage
  g_prefs.begin(NVS_NAMESPACE, false);
  loadConfig();
  loadBlankingData();

  // ===== PWM Setup (using analogWrite) =====
  pinMode(LED_PWM_PIN, OUTPUT);
  analogWriteFrequency(LED_PWM_PIN, LEDC_FREQ_HZ);
  analogWriteResolution(LED_PWM_PIN, LEDC_RES_BITS);

  Serial.print("PWM configured: Pin ");
  Serial.print(LED_PWM_PIN);
  Serial.print(", Freq: ");
  Serial.print(LEDC_FREQ_HZ);
  Serial.print(" Hz, Res: ");
  Serial.print(LEDC_RES_BITS);
  Serial.println(" bits");

  pwmSetDutyPercent(0.0f); // Set initial duty to 0%

  // ===== I2C Setup =====
  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
  Wire.setClock(100000);
  delay(5);

  if (!vemlSetConfig(0)) {
    Serial.println("VEML7700 init warning.");
  }
  pwmSetLevel(0); // Set to default 0% PWM level

  // ===== NEW: WiFi Setup =====
  Serial.println("[NET] Setting mode to WIFI_AP_STA...");
  WiFi.mode(WIFI_AP_STA);

  // Configure and Start AP
  Serial.printf("[NET] Configuring AP on subnet %s\n", apIP.toString().c_str());
  WiFi.softAPConfig(apIP, apGateway, apSubnet);
  if (WiFi.softAP(AP_SSID, "")) { // No password
    Serial.printf("[NET] AP Started: %s (IP: %s)\n", AP_SSID, WiFi.softAPIP().toString().c_str());
  } else {
    Serial.println("[NET] AP Start Failed!");
  }

  // Configure and start Web Server
  server.on("/readData", HTTP_GET, handleReadData);
  server.on("/command", HTTP_POST, handleCommand);
  server.on("/", HTTP_GET, handleReadData); // Default to data
  server.onNotFound(handleNotFound);
  server.begin();
  Serial.println("[NET] Web server started.");

  // Start STA connection attempt
  startStaConnectionAttempt();

  // ===== Watchdog Setup =====
  esp_task_wdt_config_t twdt_config = {
      .timeout_ms   = WDT_TIMEOUT_S * 1000,
      .idle_core_mask = 0, // do not watch idle tasks
      .trigger_panic  = true // panic on timeout
  };
  esp_err_t err = esp_task_wdt_init(&twdt_config);
  if (err != ESP_OK) {
    Serial.print("esp_task_wdt_init failed, code ");
    Serial.println((int)err);
  } else {
    Serial.println("Watchdog timer initialized.");
  }
  err = esp_task_wdt_add(NULL); // Add this task to WDT
  if (err != ESP_OK) {
    Serial.print("esp_task_wdt_add failed, code ");
    Serial.println((int)err);
  } else {
    Serial.println("Main task added to Watchdog.");
  }

  Serial.println("System IDLE.");
  Serial.println("Send JSON commands via Serial, POST /command, or Sensor Hub.");
  if (g_blankIsDone)
    Serial.println("e.g. {\"command\":\"start\"} or {\"start\":1}");
  else
    Serial.println("e.g. {\"command\":\"blank\"} or {\"blank\":1}");
}

void loop() {
  esp_task_wdt_reset(); // Feed the watchdog

  server.handleClient(); // Handle incoming HTTP requests on our AP

  handleSerialInput(); // Handle commands from serial

  // Non-blocking state machine
  uint32_t now = millis();

  // --- NEW: WiFi Reconnect Logic ---
  if (now - lastWifiCheckMs >= WIFI_RECONNECT_PERIOD_MS) {
    lastWifiCheckMs = now;
    if (WiFi.status() != WL_CONNECTED) {
      Serial.println("[NET] WiFi STA disconnected. Attempting reconnect...");
      startStaConnectionAttempt();
    }
  }

  // --- NEW: Periodic Data Send & Command Poll ---
  if (now - lastHubPollMs >= HUB_POLL_PERIOD_MS) {
    lastHubPollMs = now;
    
    // 1. Update the global JSON string with current data (full debug version)
    buildDataJson();
    
    // 2. Log the FULL JSON to serial (as requested)
    Serial.println(g_lastDataJson);
    
    // 3. If connected, send MINIMAL data to hub and poll for commands
    if (WiFi.status() == WL_CONNECTED) {
      sendDataToHub(); // This now sends minimal data
      pollHubForCommands();
    }
  }


  // Non-blocking LED test sweep (only runs in IDLE state)
  if (g_ledTestEnable && g_state == IDLE && now >= g_ledTestNextMs) {
    g_ledTestNextMs = now + g_ledTestPeriodMs;
    g_ledTestPct += g_ledTestDir * 2.0f; // 2 percent per step
    if (g_ledTestPct >= 100.0f) {
      g_ledTestPct = 100.0f;
      g_ledTestDir = -1;
    }
    if (g_ledTestPct <= 0.0f) {
      g_ledTestPct = 0.0f;
      g_ledTestDir = 1;
    }
    pwmSetDutyPercent(g_ledTestPct);
    // Serial.print("[TEST] Duty ");
    // Serial.print(g_ledTestPct, 1);
    // Serial.println(" percent");
  }

  switch (g_state) {
    case IDLE:
    case BLANKING:
    case SEARCHING:
      // These states are event-driven
      break;

    case MEASURING:
      // This is the main polling state
      if (now >= g_nextReadTime) {
        runMeasurementLoop(); // This updates globals, sender handles the rest
      }
      break;
  }
}