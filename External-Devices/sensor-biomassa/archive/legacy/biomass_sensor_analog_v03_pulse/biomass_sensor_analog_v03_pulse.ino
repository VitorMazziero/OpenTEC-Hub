/**
 * @file main.cpp
 * @brief Firmware for a VEML7700-based biomass sensor. (v2.5)
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
 * CHANGELOG (v2.5):
 * - NEW: Implemented "Smart Start". The 'start' command now analyzes blanking
 * data to choose an optimal starting IT/PWM, rather than defaulting to 0/0.
 * - NEW: Implemented "High Density Mode". If 3 consecutive gear searches
 * (due to low signal) fail to find an in-range value, the sensor locks
 * to max IT / max PWM. It automatically exits this mode if the raw
 * signal rises above the optimal target.
 * - CHANGED: Auto-ranging hysteresis increased from 5 to 10 consecutive
 * out-of-range readings.
 * - CHANGED: Serial JSON logging and Hub data push are now synchronized with
 * the measurement loop (every 10s) instead of polling every 2s. This
 * prevents duplicate data logs.
 * - CHANGED: Measurement refresh time (itRefreshTimes) set to 10 seconds.
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
IPAddress apIP(192, 168, 7, 1); // Use a different subnet from the hub (192.168.4.x)
IPAddress apGateway(192, 168, 7, 1);
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
enum WifiReconnectState { WF_IDLE, WF_SCANNING, WF_CONNECTING };
WifiReconnectState g_wifiState = WF_IDLE;
unsigned long g_wifiNextActionMs = 0;

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

// --- NEW: EMA Low-pass filter ---
float    g_emaFilteredRaw = 0.0f;
bool     g_emaFilterIsPrimed = false;


// --- Runtime Globals ---
volatile float g_targetPct     = 0.0f; // Last commanded duty cycle %
uint16_t g_lastAlsRaw          = 0;    // The *doubly filtered* sensor reading
float    g_lastAbsorbance      = 0.0f;
int      g_currentItIndex      = 0;
int      g_currentPwmIndex     = 0;
bool     g_blankIsDone         = false;
uint32_t g_nextReadTime        = 0;

// --- NEW: Auto-ranging hysteresis counters ---
int      g_consecutiveLowReadings = 0;
int      g_consecutiveHighReadings = 0;

// --- NEW: High Density Mode globals ---
int      g_consecutiveGearSearches = 0; // Counts failed searches
bool     g_highDensityMode         = false;


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

/**
 * @brief Resets the median and EMA filters.
 */
void resetReadingFilter() {
  g_filterIsPrimed     = false;
  g_readingWindowIndex = 0;
  for (int i = 0; i < FILTER_WINDOW_SIZE; i++) {
    g_readingWindow[i] = 0;
  }
  // --- NEW: Reset EMA filter as well ---
  g_emaFilterIsPrimed = false;
}

/**
 * @brief Adds a new reading to the filter and returns the median value.
 * @param newReading The raw reading from the sensor.
 * @return The median value from the reading window.
 */
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
// ... (vemlWrite16, vemlRead16 functions are unchanged) ...
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

/**
 * @brief Configures the VEML7700 sensor integration time (IT).
 * --- MODIFIED: Also resets auto-ranging hysteresis counters ---
 */
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
  
  // --- NEW: Reset hysteresis counters on any gear change ---
  g_consecutiveLowReadings = 0;
  g_consecutiveHighReadings = 0;

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
  // resetReadingFilter(); // Reset filter on any "gear change"
  // ^-- This is now called in vemlSetConfig, and we don't want it here
  //     because pwmSetLevel is called *before* the delay/read.
}

// ==========================
// NVS (Storage) Functions
// ==========================

/**
 * @brief Loads the `DeviceConfig` struct from NVS.
 * If NVS is empty or corrupt, loads default values.
 * --- MODIFIED: Sets new 5-second refresh time ---
 */
void loadConfig() {
  if (g_prefs.getBytes(NVS_KEY_CONFIG, &g_config, sizeof(DeviceConfig)) ==
      sizeof(DeviceConfig)) {
    // Calculate CRC
    uint32_t crc = calculateCRC32((uint8_t*)&g_config,
                                  sizeof(DeviceConfig) - sizeof(uint32_t));
    if (crc == g_config.crc32) {
      Serial.println("Loaded config from NVS.");
      // --- FIX: Force 5s refresh time even on loaded configs ---
      // (This is a temporary patch to ensure all devices get the new timing)
      bool needsSave = false;
      for(int i=0; i < g_config.IT_COUNT; i++) {
        if(g_config.itRefreshTimes[i] != 5000) {
          g_config.itRefreshTimes[i] = 5000;
          needsSave = true;
        }
      }
      if(needsSave) {
        Serial.println("Applying 10s refresh time patch to config...");
        saveConfig();
      }
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

  // --- NEW: Set refresh time to 5 seconds for all levels ---
  g_config.itRefreshTimes[0] = 5000; // 10s refresh
  g_config.itRefreshTimes[1] = 5000; // 10s refresh
  g_config.itRefreshTimes[2] = 5000; // 10s refresh
  g_config.itRefreshTimes[3] = 5000; // 10s refresh

  g_config.pwmSettings[0] = 5.0f;
  g_config.pwmSettings[1] = 10.0f;
  g_config.pwmSettings[2] = 15.0f;
  g_config.pwmSettings[3] = 20.0f;
  g_config.pwmSettings[4] = 25.0f;
  g_config.pwmSettings[5] = 50.0f;
  g_config.pwmSettings[6] = 75.0f;
  g_config.pwmSettings[7] = 100.0f;
}

// ... (saveConfig, loadBlankingData, saveBlankingData functions are unchanged) ...
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
                // --- Start Pulsed Read ---
                pwmSetLevel(j); // 1. Turn LED ON
                delay(10); // 1.5. LED stabilization
                delay(g_config.itDelays[i] + 5); // 2. Wait for integration
                
                uint16_t rawReading;
                if (!vemlRead16(REG_ALS_DATA_L, rawReading)) { // 3. Read the sensor
                    pwmSetDutyPercent(0.0f); // Turn off on error
                    handleI2CError();
                    g_state = IDLE;
                    return;
                }
                
                pwmSetDutyPercent(0.0f); // 4. Turn LED OFF immediately

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
    g_blankIsDone = true;
    g_state       = IDLE;

    Serial.println("--- Blanking Complete ---");
    Serial.println("Send JSON '{\"command\":\"start\"}' to begin measurement.");
}

// ==========================
// Auto-Ranging Measurement
// ==========================

/**
 * @brief NEW: Analyzes blanking data to find the best IT/PWM combo.
 * This is used by the 'start' command for a smarter initial gear.
 * @param[out] bestItIndex The index (0-3) of the best IT setting.
 * @param[out] bestPwmIndex The index (0-7) of the best PWM setting.
 */
void findOptimalBlankGear(int &bestItIndex, int &bestPwmIndex) {
  bestItIndex  = 0;
  bestPwmIndex = 0;
  long bestScore  = -1000000;

  for (int it = 0; it < g_config.IT_COUNT; it++) {
    for (int pwm = 0; pwm < g_config.PWM_COUNT; pwm++) {
      uint16_t blankValue = g_blankingData.blankValues[it][pwm];

      // Skip saturated values
      if (blankValue >= SATURATION_RAW) {
        continue;
      }
      
      // Score is the negative absolute difference from optimal
      long score = -abs(g_config.OPTIMAL_TARGET_RAW - blankValue);
      if (score > bestScore) {
        bestScore    = score;
        bestItIndex  = it;
        bestPwmIndex = pwm;
      }
    }
  }

  Serial.print("Smart Start: Found optimal blank gear: IT ");
  Serial.print(g_config.itDelays[bestItIndex]);
  Serial.print("ms, PWM ");
  Serial.print(g_config.pwmSettings[bestPwmIndex]);
  Serial.println("%");
}


/**
 * @brief NEW: Stops measurement to find the optimal gear.
 * This search prioritizes the *lowest* IT/PWM combination that
 * places the reading within the [LOW, HIGH] threshold range.
 *
 * It falls back to the gear *closest* to OPTIMAL_TARGET if no
 * gear is found within the valid range.
 */
/**
 * @brief Finds the best gear by prioritizing the setting that yields a RAW reading 
 * closest to OPTIMAL_TARGET_RAW, while avoiding saturation.
 */
void findAndSetOptimalGear() {
    Serial.println("Signal out of range. Pausing to find optimal new gear...");
    g_state = SEARCHING;
    esp_task_wdt_reset();
    
    // Turn off LED before starting
    pwmSetDutyPercent(0.0f);

    int bestScoreIT = -1;
    int bestScorePWM = -1;
    // Start with a very low score (negative difference is better than -1,000,000)
    long bestScore = -1000000; 

    // --- Search ALL IT/PWM combinations (lowest sensitivity first) ---
    for (int it = 0; it < g_config.IT_COUNT; it++) {
        esp_task_wdt_reset();
        bool isSaturatedThisIT = false;

        for (int pwm = 0; pwm < g_config.PWM_COUNT; pwm++) {
            esp_task_wdt_reset();
            
            // 1. Set config (includes a short delay)
            if (!vemlSetConfig(it)) {
                g_state = IDLE;
                return;
            } 
            
            // 2. Pulse Measurement (Turn ON, Wait, Read, Turn OFF)
            pwmSetLevel(pwm); 
            delay(10); // LED stabilization
            delay(g_config.itDelays[it] + 5); // Wait for integration

            uint16_t newReading;
            if (!vemlRead16(REG_ALS_DATA_L, newReading)) {
                pwmSetDutyPercent(0.0f);
                handleI2CError();
                g_state = IDLE;
                return;
            }
            pwmSetDutyPercent(0.0f); // Turn LED OFF immediately

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

            // --- SCORING: Find the gear closest to OPTIMAL, preferring lower power ---
            // Only consider readings below the HIGH threshold
            if (newReading > g_config.LOW_THRESHOLD_RAW && newReading <= g_config.HIGH_THRESHOLD_RAW) {
                // Score is negative difference from optimal. Closer to zero is better.
                long score = -abs(g_config.OPTIMAL_TARGET_RAW - newReading);

                // If the score is the same, keep the existing one (which was found at a lower IT/PWM)
                if (score > bestScore) {
                    bestScore   = score;
                    bestScoreIT  = it;
                    bestScorePWM = pwm;
                }
            }
        } // end pwm loop
    } // end it loop


    // --- Decision Logic ---
    if (bestScoreIT != -1) {
        // We found a setting that is in range. Jump to the one closest to optimal.
        Serial.print("Jumping to BEST gear (Closest to Optimal Target): IT ");
        Serial.print(g_config.itDelays[bestScoreIT]);
        Serial.print("ms, PWM ");
        Serial.print(g_config.pwmSettings[bestScorePWM]);
        Serial.println("%");
        vemlSetConfig(bestScoreIT);
        pwmSetLevel(bestScorePWM);
    } 
    else {
        // If no non-saturated reading was found above LOW_THRESHOLD, fallback to max sensitivity
        Serial.println("No reading found in target range. Jumping to MAX sensitivity.");
        int it  = g_config.IT_COUNT - 1;
        int pwm = g_config.PWM_COUNT - 1;
        vemlSetConfig(it);
        pwmSetLevel(pwm);
    }

    // Resume measuring
    g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
    g_state        = MEASURING;
}

/**
 * @brief Performs a single measurement cycle.
 * --- MODIFIED for Pulsed Measurement ---
 * --- MODIFIED for EMA filter ---
 * --- MODIFIED for 10-sample auto-range hysteresis ---
 * --- MODIFIED for High Density Mode ---
 * --- MODIFIED for synchronized data output ---
 * --- MODIFIED for 10ms LED stabilization delay ---
 */
/**
 * @brief Performs a single measurement cycle.
 * --- MODIFIED to use new findAndSetOptimalGear() for both LOW and HIGH ---
 */
void runMeasurementLoop() {
  
  // --- Pulsed reading logic ---
  
  // 1. Turn LED ON to the current setting
  pwmSetLevel(g_currentPwmIndex);
  
  // --- NEW: Wait 10ms for the LED to fully stabilize ---
  delay(10); 
  
  // 2. Wait for the *actual* integration time to complete + buffer
  delay(g_config.itDelays[g_currentItIndex] + 5); 

  // 3. Read the sensor
  uint16_t rawReading;
  if (!vemlRead16(REG_ALS_DATA_L, rawReading)) {
    pwmSetDutyPercent(0.0f); // Turn off on error
    handleI2CError();
    return;
  }

  // 4. Turn LED OFF immediately after reading
  pwmSetDutyPercent(0.0f);
  // --- End of pulsed reading logic ---

  // --- High Density Mode Check ---
  if (g_highDensityMode) {
    // In High Density Mode, we only check if the signal has RECOVERED
    if (rawReading > g_config.OPTIMAL_TARGET_RAW) {
      Serial.println("--- High Density Mode Deactivated. Rescanning... ---");
      g_highDensityMode         = false;
      g_consecutiveGearSearches = 0;
      findAndSetOptimalGear(); // <--- UPDATED
      return;
    }
  }


  if (rawReading >= SATURATION_RAW) {
    g_saturationCount++;
  }

  // --- Apply Median Filter ---
  uint16_t medianReading = getFilteredReading(rawReading);

  if (!g_filterIsPrimed) {
    g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
    return;
  }

  // --- Apply EMA Low-Pass Filter ---
  if (!g_emaFilterIsPrimed) {
    g_emaFilteredRaw = (float)medianReading;
    g_emaFilterIsPrimed = true;
  } else {
    // alpha = 0.8
    g_emaFilteredRaw = (0.8f * (float)medianReading) + (0.2f * g_emaFilteredRaw);
  }
  g_lastAlsRaw = (uint16_t)(g_emaFilteredRaw + 0.5f); 


  // --- Auto-Ranging Logic (with 10-sample hysteresis & HDM) ---
  bool configChanged = false; // This is no longer used, but kept for safety

  if (!g_highDensityMode) { // Only run auto-ranging if NOT in HDM
    
    // --- CASE 1: Signal is too LOW ---
    if (g_lastAlsRaw < g_config.LOW_THRESHOLD_RAW && g_lastAlsRaw > 0) {
      g_consecutiveLowReadings++;
      g_consecutiveHighReadings = 0;
      if (g_consecutiveLowReadings >= 10) {
        g_consecutiveGearSearches++; // Increment failed search counter
        if (g_consecutiveGearSearches >= 3) {
          // --- ACTIVATE HIGH DENSITY MODE ---
          Serial.println("--- High Density Mode Activated (3 failed searches) ---");
          g_highDensityMode = true;
          g_consecutiveGearSearches = 0; 
          g_consecutiveLowReadings = 0;
          vemlSetConfig(g_config.IT_COUNT - 1); // Max IT
          pwmSetLevel(g_config.PWM_COUNT - 1);  // Max PWM
          // Set flag to skip calculation this round
          configChanged = true; 
        } else {
          // --- Not at 3 failures yet, just find a new gear ---
          findAndSetOptimalGear(); // <--- UPDATED
          return; // Exit, findAndSetOptimalGear will change state
        }
      }
    } 
    // --- CASE 2: Signal is too HIGH ---
    else if (g_lastAlsRaw > g_config.HIGH_THRESHOLD_RAW) {
      g_consecutiveHighReadings++;
      g_consecutiveLowReadings = 0;
      g_consecutiveGearSearches = 0; // A high reading resets the "too low" counter
      
      if (g_consecutiveHighReadings >= 10) {
        // --- Signal is too high, stop and do a full search ---
        findAndSetOptimalGear(); // <--- THIS IS THE FIX
        return; // Exit, findAndSetOptimalGear will change state
      }
    } 
    // --- CASE 3: Signal is IN RANGE ---
    else {
      g_consecutiveLowReadings = 0;
      g_consecutiveHighReadings = 0;
      g_consecutiveGearSearches = 0; // Reading is good, reset failure counter
    }
  }
  

  if (configChanged) {
    // This block is now only entered when HDM is first activated
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

  // --- Synchronized Data Output ---
  buildDataJson();
  Serial.println(g_lastDataJson);
  
  if (WiFi.status() == WL_CONNECTED) {
    sendDataToHub(); 
  }

  // Schedule the next read
  g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
}

// =================================
// NEW: JSON Parsing/Networking Helpers
// =================================
// ... (getJsonValue, getJsonStringValue functions are unchanged) ...

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
 * --- MODIFIED for Smart Start and HDM reset ---
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
        
        // --- NEW: Smart Start ---
        int startIt, startPwm;
        findOptimalBlankGear(startIt, startPwm); // Find best gear from blank data
        
        g_state        = MEASURING;
        g_nextReadTime = millis(); // Start first read immediately
        vemlSetConfig(startIt); // Set optimal IT
        pwmSetLevel(startPwm);  // Set optimal PWM
        
        // Reset counters
        g_consecutiveGearSearches = 0;
        g_highDensityMode = false;
        // resetReadingFilter(); // Not needed, vemlSetConfig does it
      }
    } else if (cmd.equals("stop")) {
      if (g_state == MEASURING || g_state == SEARCHING) {
        Serial.println("--- Stopping Measurement ---");
        g_state = IDLE;
        pwmSetDutyPercent(0.0f); // Ensure LED is off
        // Reset counters
        g_consecutiveGearSearches = 0;
        g_highDensityMode = false;
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
      Serial.print("Consecutive Low: ");
      Serial.println(g_consecutiveLowReadings);
      Serial.print("Consecutive High: ");
      Serial.println(g_consecutiveHighReadings);
      Serial.print("Failed Gear Searches: ");
      Serial.println(g_consecutiveGearSearches);
      Serial.print("High Density Mode: ");
      Serial.println(g_highDensityMode ? "ACTIVE" : "inactive");
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
      pwmSetDutyPercent(0.0f); // Ensure LED is off
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

// ... (buildDataJson, httpGet, sendDataToHub, pollHubForCommands, checkWifi functions are unchanged) ...

/**
 * @brief Creates the FULL JSON data string from current globals (for serial debug).
 * --- MODIFIED: Reports 0.0% PWM when IDLE ---
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
  
  // --- FIX: Report 0.0 PWM if IDLE, otherwise report the setting ---
  if (g_state == IDLE) {
    json += "0.0";
  } else {
    json += String(g_config.pwmSettings[g_currentPwmIndex], 1);
  }
  // --- END FIX ---

  json += ",\"blank_done\":";
  json += g_blankIsDone ? "true" : "false";
  json += ",\"searching\":";
  json += (g_state == SEARCHING) ? "true" : "false";
  json += ",\"measuring\":";
  json += (g_state == MEASURING) ? "true" : "false";
  // --- NEW: Report High Density Mode ---
  json += ",\"hd_mode\":";
  json += g_highDensityMode ? "true" : "false";
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
  
  // --- FIX: Report 0.0 PWM if IDLE, otherwise report the setting ---
  if (g_state == IDLE) {
    url += "&pwm=0.0";
  } else {
    url += "&pwm=" + String(g_config.pwmSettings[g_currentPwmIndex], 1);
  }
  // --- END FIX ---
  
  // --- NEW: Report High Density Mode ---
  url += "&hd_mode=" + String(g_highDensityMode ? 1 : 0);

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
          if (ssid == HUB_SSID_A || ssid == HUB_SSID_B) {
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

// ==========================
// NEW: Web Server Handlers
// ==========================

/**
 * @brief Handles GET /readData - returns MINIMAL JSON data
 * --- MODIFIED: Sends minimal data as requested ---
 * --- MODIFIED: Reports 0.0% PWM when IDLE ---
 * --- MODIFIED: Reports High Density Mode ---
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
  
  // --- FIX: Report 0.0 PWM if IDLE, otherwise report the setting ---
  if (g_state == IDLE) {
    json += "0.0";
  } else {
    json += String(g_config.pwmSettings[g_currentPwmIndex], 1);
  }
  // --- END FIX ---

  // --- NEW: Report High Density Mode ---
  json += ",\"hd_mode\":";
  json += g_highDensityMode ? "true" : "false";

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
  Serial.println("--- Biomass Sensor Firmware v2.5 (Network Enabled) ---");

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
  // pwmSetLevel(0); // Not needed, LED is off by default
  g_currentPwmIndex = 0; // Set default index

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

  g_wifiNextActionMs = 0;
  checkWifi();

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
  
  // --- NEW: Build an initial JSON for the serial log ---
  buildDataJson();
  Serial.println(g_lastDataJson);

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

  checkWifi();

  // --- NEW: Periodic Command Poll ---
  // (Data send and Serial Log are now in runMeasurementLoop)
  if (now - lastHubPollMs >= HUB_POLL_PERIOD_MS) {
    lastHubPollMs = now;
    
    // 3. If connected, poll for commands
    if (WiFi.status() == WL_CONNECTED) {
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
      // In IDLE, make sure LED is off if test mode is disabled
      if (!g_ledTestEnable && g_targetPct > 0.0f) {
          pwmSetDutyPercent(0.0f);
      }
      break;
    case BLANKING:
    case SEARCHING:
      // These states are event-driven (blocking) and will return to IDLE
      // or MEASURING when complete.
      break;

    case MEASURING:
      // This is the main polling state
      if (now >= g_nextReadTime) {
        // g_nextReadTime is set *inside* runMeasurementLoop
        runMeasurementLoop(); // This performs one pulsed read
      }
      break;
  }
}
