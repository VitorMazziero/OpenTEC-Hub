/**
 * @file biomass_sensor_analog.cpp
 * @brief Firmware for a VEML7700-based biomass sensor.
 *
 * This firmware controls an LED emitter via PWM and reads an ALS (Ambient Light
 * Sensor) to calculate absorbance. It features auto-ranging for both sensor
 * integration time (IT) and LED PWM duty cycle to maintain an optimal
 * signal-to-noise ratio.
 *
 * All settings and blanking calibration data are stored in Non-Volatile
 * Storage (NVS) on the ESP32.
 */

#include <Arduino.h>
#include <Wire.h>
#include <Preferences.h>      // For ESP32 Non-Volatile Storage (NVS)
#include <esp_task_wdt.h>   // For Watchdog Timer
#include <algorithm>        // For std::sort (median filter)
#include <cstring>          // For memcpy (median filter)

// ==========================
// Sensor Configuration
// ==========================

// --- PWM ---
constexpr int LED_PWM_PIN   = 18;     // GPIO pin for LED PWM control
constexpr int LEDC_FREQ_HZ  = 2000;   // PWM frequency in Hz
constexpr int LEDC_RES_BITS = 8;     // PWM resolution (10-bit = 0-1023)

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
constexpr uint16_t VEML_ALS_ENABLE = 0x0000;       // Bit 0 = 0 to enable
constexpr uint16_t SATURATION_RAW = 65530;       // Sensor saturation threshold

// ==========================
// Global Definitions
// ==========================

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

/**
 * @struct DeviceConfig
 * @brief Holds all runtime-configurable settings, saved to NVS.
 */
struct DeviceConfig {
  uint16_t LOW_THRESHOLD_RAW;
  uint16_t HIGH_THRESHOLD_RAW;
  uint16_t OPTIMAL_TARGET_RAW;

  static const int IT_COUNT = 4;
  uint16_t itSettings[IT_COUNT];     // VEML7700 register values for IT
  uint32_t itDelays[IT_COUNT];       // Wait time in ms for each IT
  uint32_t itRefreshTimes[IT_COUNT]; // Poll interval for each IT

  static const int PWM_COUNT = 8;
  float pwmSettings[PWM_COUNT];      // % duty cycle levels

  uint32_t crc32; // Checksum for config integrity
};
DeviceConfig g_config; // The "live" global config

// --- NVS (Non-Volatile Storage) ---
Preferences g_prefs;
constexpr char* NVS_NAMESPACE  = "biomass_sensor";
constexpr char* NVS_KEY_CONFIG = "config";
constexpr char* NVS_KEY_BLANK  = "blanking";

/**
 * @struct BlankingData
 * @brief Holds the calibration (I_0) values, saved to NVS.
 */
struct BlankingData {
  uint16_t blankValues[DeviceConfig::IT_COUNT][DeviceConfig::PWM_COUNT];
  uint32_t timestamp;
  uint32_t crc32;
};
BlankingData g_blankingData; // The "live" global blanking data

// --- Smoothing Filter ---
constexpr int FILTER_WINDOW_SIZE = 5;
uint16_t g_readingWindow[FILTER_WINDOW_SIZE] = {0};
int      g_readingWindowIndex                = 0;
bool     g_filterIsPrimed                    = false;

// --- Runtime Globals ---
volatile float g_targetPct      = 0.0f; // Last commanded duty cycle %
uint16_t g_lastAlsRaw           = 0;    // The *filtered* sensor reading
float    g_lastAbsorbance       = 0.0f;
int      g_currentItIndex       = 0;
int      g_currentPwmIndex      = 0;
bool     g_blankIsDone          = false;
uint32_t g_nextReadTime         = 0;

// --- Health Metrics ---
uint32_t g_saturationCount = 0;
uint32_t g_i2cErrorCount   = 0;
uint32_t g_sensorResets    = 0;

// ==========================
// CRC32 Helper
// ==========================

/**
 * @brief Calculates a simple checksum for data integrity.
 * @note For production, replace with a proper CRC32 library.
 */
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
 * @brief Resets the median filter window.
 */
void resetReadingFilter() {
  g_filterIsPrimed     = false;
  g_readingWindowIndex = 0;
  for (int i = 0; i < FILTER_WINDOW_SIZE; i++) {
    g_readingWindow[i] = 0;
  }
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
  g_readingWindowIndex                  = (g_readingWindowIndex + 1) % FILTER_WINDOW_SIZE;

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

/**
 * @brief Attempts to reset the VEML7700 sensor on the I2C bus.
 * @return True on success, false on failure.
 */
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

/**
 * @brief Handles an I2C communication error.
 */
void handleI2CError() {
  g_i2cErrorCount++;
  Serial.print("! I2C Read Error. Total errors: ");
  Serial.println(g_i2cErrorCount);

  if (g_i2cErrorCount % 5 == 0) { // Try to reset sensor every 5 errors
    resetSensor();
  }
  g_state = IDLE; // Drop to idle on error
  Serial.println("Dropping to IDLE state. Type 'start' to retry.");
}

// ==========================
// VEML7700 Sensor Helpers
// ==========================

/**
 * @brief Writes a 16-bit value to a VEML7700 register.
 * @return True on success, false on I2C failure.
 */
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

/**
 * @brief Reads a 16-bit value from a VEML7700 register.
 * @return True on success, false on I2C failure.
 */
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
 * @param itIndex The index of the IT setting from `g_config.itSettings`.
 * @return True on success, false on failure.
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
  delay(g_config.itDelays[itIndex] + 5); // Wait for integration to apply
  return true;
}

// ==========================
// PWM Helpers
// ==========================

/**
 * @brief Sets the LED PWM duty cycle to a specific percentage.
 * Uses the Arduino `analogWrite` function.
 * @param percent A value from 0.0 to 100.0.
 */
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

/**
 * @brief Sets the PWM level from the predefined array in `g_config`.
 * @param pwmIndex The index of the PWM setting.
 */
void pwmSetLevel(int pwmIndex) {
  if (pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) return;
  g_currentPwmIndex = pwmIndex;
  pwmSetDutyPercent(g_config.pwmSettings[pwmIndex]);
  resetReadingFilter(); // Reset filter on any "gear change"
}

// ==========================
// NVS (Storage) Functions
// ==========================

/**
 * @brief Loads the `DeviceConfig` struct from NVS.
 * If NVS is empty or corrupt, loads default values.
 */
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

/**
 * @brief Saves the current `g_config` struct to NVS.
 */
void saveConfig() {
  g_config.crc32 = calculateCRC32((uint8_t*)&g_config,
                                  sizeof(DeviceConfig) - sizeof(uint32_t));
  if (g_prefs.putBytes(NVS_KEY_CONFIG, &g_config, sizeof(DeviceConfig))) {
    Serial.println("Config saved to NVS.");
  } else {
    Serial.println("Error saving config to NVS.");
  }
}

/**
 * @brief Loads the `BlankingData` struct from NVS.
 * @return True if data was loaded, false otherwise.
 */
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

/**
 * @brief Saves the current `g_blankingData` struct to NVS.
 */
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

/**
 * @brief Runs the full calibration routine for blanking (I_0).
 * This iterates through every IT/PWM combination and stores the
 * sensor reading for clear media.
 */
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
        pwmSetLevel(j);                       // Set PWM
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
  Serial.println("Type 'start' to begin measurement.");
}

// ==========================
// Auto-Ranging Measurement
// ==========================

/**
 * @brief Scans for a new IT/PWM "gear" when the signal is too low.
 * This function pauses measurements, finds the best combination
 * that brings the reading closest to `OPTIMAL_TARGET_RAW`,
 * and then resumes measurements.
 */
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
 * logic, and calculates/prints the final absorbance.
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
    return; // Eit, findAndSetBestNewGear will change state
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

  // --- Calculate and Report Absorbance ---
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

  Serial.print("Absorbance: ");
  Serial.print(g_lastAbsorbance, 3);
  Serial.print(", RAW_filt: ");
  Serial.print(g_lastAlsRaw);
  Serial.print(", I_0: ");
  Serial.print(current_I0);
  Serial.print(", IT: ");
  Serial.print(g_config.itDelays[g_currentItIndex]);
  Serial.print("ms, PWM: ");
  Serial.print(g_config.pwmSettings[g_currentPwmIndex]);
  Serial.println("%");

  g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
}

// ==========================
// Serial Command Parser
// ==========================

/**
 * @brief Polls the Serial port and processes incoming commands.
 */
void handleSerialInput() {
  static char buf[64];
  static size_t idx = 0;

  while (Serial.available() > 0) {
    int c = Serial.read();
    if (c == '\r') continue;
    if (c == '\n') {
      buf[idx] = '\0';
      idx      = 0;

      String cmd = String(buf);
      cmd.trim();
      if (cmd.length() == 0) continue;

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
      } else if (cmd.startsWith("set low ")) {
        g_config.LOW_THRESHOLD_RAW = cmd.substring(8).toInt();
        Serial.print("Live config: LOW_THRESHOLD_RAW set to ");
        Serial.println(g_config.LOW_THRESHOLD_RAW);
      } else if (cmd.startsWith("set high ")) {
        g_config.HIGH_THRESHOLD_RAW = cmd.substring(9).toInt();
        Serial.print("Live config: HIGH_THRESHOLD_RAW set to ");
        Serial.println(g_config.HIGH_THRESHOLD_RAW);
      } else if (cmd.startsWith("set opt ")) {
        g_config.OPTIMAL_TARGET_RAW = cmd.substring(8).toInt();
        Serial.print("Live config: OPTIMAL_TARGET_RAW set to ");
        Serial.println(g_config.OPTIMAL_TARGET_RAW);
      } else if (cmd.equals("test on")) {
        if (g_state != IDLE) {
          Serial.println("Error: test allowed only in IDLE");
        } else {
          g_ledTestEnable = true;
          g_ledTestPct    = 0.0f;
          g_ledTestDir    = 1;
          g_ledTestNextMs = millis();
          Serial.println("LED test enabled");
        }
      } else if (cmd.equals("test off")) {
        g_ledTestEnable = false;
        Serial.println("LED test disabled");
      } else if (cmd.startsWith("test period ")) {
        long ms = cmd.substring(12).toInt();
        if (ms < 5) ms = 5;
        g_ledTestPeriodMs = static_cast<uint32_t>(ms);
        Serial.print("LED test period set to ");
        Serial.print(g_ledTestPeriodMs);
        Serial.println(" ms");
      } else {
        Serial.println(
            "Unknown command. Try: blank, start, stop, print_blank, "
            "print_config, print_health, save_config, load_config, set low "
            "<val>, set high <val>, set opt <val>");
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

// ==========================
// Arduino Entry Points
// ==========================

void setup() {
  Serial.begin(115200);
  delay(50);
  Serial.println("--- Biomass Sensor Firmware v2.0 ---");

  // Load config and blanking data from storage
  g_prefs.begin(NVS_NAMESPACE, false);
  loadConfig();
  loadBlankingData();

  // ===== PWM Setup (using analogWrite) =====
  // Set the frequency and resolution for the analogWrite function
  // This ensures our 0-100% duty cycle calculation is correct
  // ===== PWM Setup (using analogWrite) =====
  // Set the pin as an output first
  pinMode(LED_PWM_PIN, OUTPUT); 
  
  // Now, set the frequency and resolution *for that specific pin*
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
  if (g_blankIsDone)
    Serial.println("Type 'start' to begin measurement.");
  else
    Serial.println("Type 'blank' to calibrate.");
}

void loop() {
  esp_task_wdt_reset(); // Feed the watchdog

  handleSerialInput(); // Handle commands from serial

  // Non-blocking state machine
  uint32_t now = millis();

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
    Serial.print("[TEST] Duty ");
    Serial.print(g_ledTestPct, 1);
    Serial.println(" percent");
  }

  switch (g_state) {
    case IDLE:
    case BLANKING:
    case SEARCHING:
      // These states are event-driven (by serial or function calls)
      // No periodic action required in the loop.
      break;

    case MEASURING:
      // This is the main polling state
      if (now >= g_nextReadTime) {
        runMeasurementLoop();
      }
      break;
  }
}