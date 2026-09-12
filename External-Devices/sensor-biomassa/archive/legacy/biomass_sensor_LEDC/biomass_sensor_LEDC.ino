#include <Arduino.h>
#include <Wire.h>
#include <Preferences.h>      // For ESP32 Non-Volatile Storage (NVS)
#include <esp_task_wdt.h>   // For Watchdog Timer
#include <algorithm>          // For std::sort (median filter)
#include <cstring>            // For memcpy (median filter)

// ==========================
// User configuration
// ==========================
// *** Using analogWrite ***
constexpr int LED_PIN = 18; // Pin for analogWrite

// LED test sweep
bool      g_ledTestEnable   = false;
float     g_ledTestPct      = 0.0f; // Still use 0-100% conceptually
int       g_ledTestDir      = 1;          // +1 up, −1 down
uint32_t  g_ledTestPeriodMs = 50;         // step interval
uint32_t  g_ledTestNextMs   = 0;

// I2C pins for ESP32-S3
constexpr int I2C_SDA_PIN = 8;
constexpr int I2C_SCL_PIN = 9;

// VEML7700 I2C address and registers
constexpr uint8_t VEML7700_ADDR = 0x10;
constexpr uint8_t REG_ALS_CONF = 0x00;
constexpr uint8_t REG_ALS_DATA_L = 0x04;
constexpr uint8_t REG_ALS_DATA_H = 0x05;

// VEML7700 Configuration bits
constexpr uint16_t VEML_GAIN_2 = (0b01 << 11); // Fix gain at 2x
constexpr uint16_t VEML_ALS_ENABLE = 0x0000; // Bit 0 = 0 to enable
constexpr uint16_t SATURATION_RAW = 65530;    // Sensor is saturated

// ==========================
// Firmware settings
// ==========================

// --- Watchdog Timer ---
constexpr int WDT_TIMEOUT_S = 10; // 10-second watchdog

// --- State Machine ---
enum SystemState { IDLE, BLANKING, MEASURING, SEARCHING };
SystemState g_state = IDLE;

// --- Config Struct (for NVS) ---
struct DeviceConfig {
  uint16_t LOW_THRESHOLD_RAW;
  uint16_t HIGH_THRESHOLD_RAW;
  uint16_t OPTIMAL_TARGET_RAW;
  static const int IT_COUNT = 4;
  uint16_t itSettings[IT_COUNT];
  uint32_t itDelays[IT_COUNT];
  uint32_t itRefreshTimes[IT_COUNT];
  // *** Now using 8-bit analogWrite (0-255), but store percentages for consistency ***
  static const int PWM_COUNT = 8;
  float pwmSettings[PWM_COUNT]; // Still store as percentages (5.0, 10.0, ...)
  uint32_t crc32;
};
DeviceConfig g_config;

// --- NVS ---
Preferences g_prefs;
constexpr char* NVS_NAMESPACE = "biomass_sensor";
constexpr char* NVS_KEY_CONFIG = "config";
constexpr char* NVS_KEY_BLANK = "blanking";

// --- Blanking Data Struct (for NVS) ---
struct BlankingData {
  uint16_t blankValues[DeviceConfig::IT_COUNT][DeviceConfig::PWM_COUNT];
  uint32_t timestamp;
  uint32_t crc32;
};
BlankingData g_blankingData;

// --- Smoothing Filter ---
constexpr int FILTER_WINDOW_SIZE = 5;
uint16_t g_readingWindow[FILTER_WINDOW_SIZE] = {0};
int g_readingWindowIndex = 0;
bool g_filterIsPrimed = false;

// --- Globals ---
volatile float g_targetPct = 0.0f; // Still store last commanded percentage
uint16_t g_lastAlsRaw = 0;
float    g_lastAbsorbance = 0.0f;
int      g_currentItIndex = 0;
int      g_currentPwmIndex = 0; // Index into the g_config.pwmSettings array
bool     g_blankIsDone = false;
uint32_t g_nextReadTime = 0;

// --- Health Metrics ---
uint32_t g_saturationCount = 0;
uint32_t g_i2cErrorCount = 0;
uint32_t g_wdtResetCount = 0;
uint32_t g_sensorResets = 0;

// ==========================
// CRC32 Helper
// ==========================
uint32_t calculateCRC32(const uint8_t *data, size_t length) {
  uint32_t crc = 0;
  for (size_t i = 0; i < length; i++) { crc += data[i]; } // Simple checksum
  return crc;
}

// ==========================
// Reading Filter
// ==========================
void resetReadingFilter() { /* ... (no changes needed) ... */
  g_filterIsPrimed = false;
  g_readingWindowIndex = 0;
  memset(g_readingWindow, 0, sizeof(g_readingWindow));
}
uint16_t getFilteredReading(uint16_t newReading) { /* ... (no changes needed) ... */
  g_readingWindow[g_readingWindowIndex] = newReading;
  g_readingWindowIndex = (g_readingWindowIndex + 1) % FILTER_WINDOW_SIZE;

  if (!g_filterIsPrimed && g_readingWindowIndex == 0) { g_filterIsPrimed = true; }
  if (!g_filterIsPrimed) { return newReading; }

  uint16_t sortedWindow[FILTER_WINDOW_SIZE];
  memcpy(sortedWindow, g_readingWindow, sizeof(g_readingWindow));
  std::sort(sortedWindow, sortedWindow + FILTER_WINDOW_SIZE);
  return sortedWindow[FILTER_WINDOW_SIZE / 2];
}

// ==========================
// Fault Handling
// ==========================
bool resetSensor() { /* ... (no changes needed) ... */
  g_sensorResets++;
  Serial.println("! Attempting I2C sensor reset...");
  Wire.end();
  delay(100);
  if (!Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN)) {
      Serial.println("!!! Wire.begin failed after reset! Halting.");
      while(1) { delay(100); esp_task_wdt_reset(); }
  }
  Wire.setClock(100000);
  delay(10);
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
    Serial.println("... Sensor reset FAILED (endTransmission failed).");
    return false;
  }
}
void handleI2CError(const char* context = "") { /* ... (no changes needed) ... */
  g_i2cErrorCount++;
  Serial.print("! I2C Error");
  if (strlen(context) > 0) { Serial.print(" in "); Serial.print(context); }
  Serial.print(". Total errors: "); Serial.println(g_i2cErrorCount);
  resetSensor();
  g_state = IDLE;
  // pwmSetDutyPercent(0.0f); // Turn off LED - done below using analogWrite
  analogWrite(LED_PIN, 0); // Explicitly turn off LED
  Serial.println("Dropping to IDLE state due to I2C error. Type 'start' to retry.");
}

// ==========================
// VEML7700 helpers
// ==========================
bool vemlWrite16(uint8_t reg, uint16_t val) { /* ... (no changes needed) ... */
  Wire.beginTransmission(VEML7700_ADDR);
  Wire.write(reg);
  Wire.write(val & 0xFF);
  Wire.write((val >> 8) & 0xFF);
  uint8_t error = Wire.endTransmission();
  if (error != 0) {
    Serial.print("! vemlWrite16 failed, error code: "); Serial.println(error);
    return false;
  }
  return true;
}
bool vemlRead16(uint8_t reg, uint16_t &value) { /* ... (no changes needed) ... */
  Wire.beginTransmission(VEML7700_ADDR);
  Wire.write(reg);
  uint8_t error = Wire.endTransmission(false);
  if (error != 0) {
    Serial.print("! vemlRead16 failed (endTransmission), error code: "); Serial.println(error);
    return false;
  }
  uint8_t bytesRead = Wire.requestFrom(VEML7700_ADDR, (uint8_t)2);
  if (bytesRead != 2) {
      Serial.print("! vemlRead16 failed (requestFrom returned "); Serial.print(bytesRead); Serial.println(" bytes)");
      while(Wire.available()) Wire.read();
      return false;
  }
  if (Wire.available() < 2) {
       Serial.println("! vemlRead16 failed (not enough bytes available)");
       while(Wire.available()) Wire.read();
       return false;
  }
  uint16_t lo = Wire.read();
  uint16_t hi = Wire.read();
  value = (hi << 8) | lo;
  return true;
}
bool vemlSetConfig(int itIndex) { /* ... (no changes needed) ... */
  if (itIndex < 0 || itIndex >= g_config.IT_COUNT) return false;
  g_currentItIndex = itIndex;
  uint16_t config_val = VEML_GAIN_2 | g_config.itSettings[itIndex] | VEML_ALS_ENABLE;
  if (!vemlWrite16(REG_ALS_CONF, config_val)) { return false; }
  resetReadingFilter();
  delay(g_config.itDelays[itIndex] + 5);
  return true;
}

// ==========================
// *** PWM helpers - Using analogWrite ***
// ==========================
void pwmSetDutyPercent(float percent) {
  percent = max(0.0f, min(100.0f, percent)); // Clamp 0-100
  // Map 0-100% to 0-255 for 8-bit analogWrite
  uint8_t duty = static_cast<uint8_t>(percent * 255.0f / 100.0f + 0.5f);
  analogWrite(LED_PIN, duty);
  g_targetPct = percent; // Store the percentage
}

// Sets the PWM level based on the index into the percentage array
void pwmSetLevel(int pwmIndex) {
  if (pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) return;
  g_currentPwmIndex = pwmIndex;
  // Get the percentage from the config and set it
  pwmSetDutyPercent(g_config.pwmSettings[pwmIndex]);
  resetReadingFilter(); // Reset filter on gear change
}

// ==========================
// NVS Functions
// ==========================
void loadConfig() { /* ... (no changes needed) ... */
  Serial.println("Attempting to load config from NVS..."); delay(10);
  size_t required_size;
  if (!g_prefs.getBytesLength(NVS_KEY_CONFIG, required_size) || required_size != sizeof(DeviceConfig)) {
      Serial.println("NVS config not found or size mismatch."); delay(10);
  } else if (g_prefs.getBytes(NVS_KEY_CONFIG, &g_config, sizeof(DeviceConfig)) == sizeof(DeviceConfig)) {
    uint32_t crc = calculateCRC32((uint8_t*)&g_config, sizeof(DeviceConfig) - sizeof(uint32_t));
    if (crc == g_config.crc32) {
      Serial.println("Loaded config from NVS."); delay(10); return;
    } else { Serial.println("NVS config CRC mismatch."); delay(10); }
  } else { Serial.println("Error reading config bytes from NVS."); delay(10); }

  Serial.println("Loading default config."); delay(10);
  g_config.LOW_THRESHOLD_RAW = 10000;
  g_config.HIGH_THRESHOLD_RAW = 40000;
  g_config.OPTIMAL_TARGET_RAW = 25000;
  g_config.itSettings[0]=(0b00000<<6); g_config.itDelays[0]=100; g_config.itRefreshTimes[0]=600;
  g_config.itSettings[1]=(0b00100<<6); g_config.itDelays[1]=200; g_config.itRefreshTimes[1]=700;
  g_config.itSettings[2]=(0b01000<<6); g_config.itDelays[2]=400; g_config.itRefreshTimes[2]=900;
  g_config.itSettings[3]=(0b01100<<6); g_config.itDelays[3]=800; g_config.itRefreshTimes[3]=1300;
  g_config.pwmSettings[0]=5.0f; g_config.pwmSettings[1]=10.0f; g_config.pwmSettings[2]=15.0f;
  g_config.pwmSettings[3]=20.0f; g_config.pwmSettings[4]=25.0f; g_config.pwmSettings[5]=50.0f;
  g_config.pwmSettings[6]=75.0f; g_config.pwmSettings[7]=100.0f;
}
void saveConfig() { /* ... (no changes needed) ... */
  g_config.crc32 = calculateCRC32((uint8_t*)&g_config, sizeof(DeviceConfig) - sizeof(uint32_t));
  if (g_prefs.putBytes(NVS_KEY_CONFIG, &g_config, sizeof(DeviceConfig))) {
    Serial.println("Config saved to NVS.");
  } else {
    Serial.println("Error saving config to NVS.");
  }
}
bool loadBlankingData() { /* ... (no changes needed) ... */
  Serial.println("Attempting to load blanking data from NVS..."); delay(10);
  size_t required_size;
  if (!g_prefs.getBytesLength(NVS_KEY_BLANK, required_size) || required_size != sizeof(BlankingData)) {
       Serial.println("NVS blanking data not found or size mismatch."); delay(10);
  } else if (g_prefs.getBytes(NVS_KEY_BLANK, &g_blankingData, sizeof(BlankingData)) == sizeof(BlankingData)) {
    uint32_t crc = calculateCRC32((uint8_t*)&g_blankingData, sizeof(BlankingData) - sizeof(uint32_t));
    if (crc == g_blankingData.crc32) {
      Serial.print("Loaded blanking data from NVS (Timestamp: "); Serial.print(g_blankingData.timestamp); Serial.println(")"); delay(10);
      g_blankIsDone = true;
      return true;
    } else { Serial.println("NVS blanking data CRC mismatch."); delay(10); }
  } else { Serial.println("Error reading blanking bytes from NVS."); delay(10); }
  Serial.println("No valid blanking data found."); delay(10);
  g_blankIsDone = false;
  return false;
}
void saveBlankingData() { /* ... (no changes needed) ... */
  g_blankingData.timestamp = millis();
  g_blankingData.crc32 = calculateCRC32((uint8_t*)&g_blankingData, sizeof(BlankingData) - sizeof(uint32_t));
  if (g_prefs.putBytes(NVS_KEY_BLANK, &g_blankingData, sizeof(BlankingData))) {
    Serial.println("Blanking data saved to NVS.");
  } else {
    Serial.println("Error saving blanking data to NVS.");
  }
}

// ==========================
// BLANKING ROUTINE
// ==========================
void runBlankingRoutine() {
  g_state = BLANKING;
  Serial.println("--- Starting Blanking Routine ---");
  Serial.println("Ensure clear media is circulating. This will take several seconds...");
  esp_task_wdt_reset(); // Feed WDT before starting

  for (int i = 0; i < g_config.IT_COUNT; i++) {
    esp_task_wdt_reset(); // Feed WDT at start of outer loop
    if (!vemlSetConfig(i)) { handleI2CError("Blanking->vemlSetConfig"); g_state = IDLE; return; }
    Serial.print("Blanking - Setting IT: "); Serial.print(g_config.itDelays[i]); Serial.println("ms");
    bool isSaturated = false;

    for (int j = 0; j < g_config.PWM_COUNT; j++) {
       esp_task_wdt_reset(); // *** Feed WDT inside inner loop ***
      uint16_t reading = 0;
      if (isSaturated) {
        reading = 65535;
      } else {
        pwmSetLevel(j);
        delay(g_config.itRefreshTimes[i] + 50); // Delay includes sensor refresh time

        uint16_t rawReading;
        if (!vemlRead16(REG_ALS_DATA_L, rawReading)) {
          handleI2CError("Blanking->vemlRead16");
          g_state = IDLE;
          return;
        }
        reading = rawReading;

        if (reading >= SATURATION_RAW) {
          isSaturated = true;
          reading = 65535;
        }
      }
      g_blankingData.blankValues[i][j] = reading;
      Serial.print("  PWM "); Serial.print(g_config.pwmSettings[j]); Serial.print("%: RAW = "); Serial.println(reading);
    } // End PWM loop
  } // End IT loop
  
  saveBlankingData();
  
  // pwmSetDutyPercent(0.0f); // Use analogWrite
  analogWrite(LED_PIN, 0); // Turn LED off
  vemlSetConfig(0); // Attempt to set back to default
  pwmSetLevel(0);
  g_blankIsDone = true;
  g_state = IDLE;
  
  Serial.println("--- Blanking Complete ---");
  Serial.println("Type 'start' to begin measurement.");
}

// ==========================
// Auto-Ranging Measurement
// ==========================

void findAndSetBestNewGear() {
  Serial.println("Searching for optimal gear...");
  g_state = SEARCHING;
  esp_task_wdt_reset(); // Feed WDT before starting search

  int bestGearIT = -1;
  int bestGearPWM = -1;
  long bestScore = -2000000000; 

  for (int it = g_currentItIndex; it < g_config.IT_COUNT; it++) {
     esp_task_wdt_reset(); // Feed WDT at start of outer loop
    bool isSaturatedThisIT = false;
    int startPwm = (it == g_currentItIndex) ? g_currentPwmIndex + 1 : 0;
    
    if (startPwm >= g_config.PWM_COUNT) continue; 

    for (int pwm = startPwm; pwm < g_config.PWM_COUNT; pwm++) {
       esp_task_wdt_reset(); // *** Feed WDT inside inner loop ***
      if (!vemlSetConfig(it)) { handleI2CError("Search->vemlSetConfig"); g_state = IDLE; return; }
      pwmSetLevel(pwm);
      delay(g_config.itRefreshTimes[it] + 50); 

      uint16_t newReading;
      if (!vemlRead16(REG_ALS_DATA_L, newReading)) {
        handleI2CError("Search->vemlRead16");
        g_state = IDLE;
        return;
      }

      Serial.print("  Testing IT "); Serial.print(g_config.itDelays[it]); Serial.print("ms, PWM ");
      Serial.print(g_config.pwmSettings[pwm]); Serial.print("%: RAW = "); Serial.println(newReading);

      if (newReading >= SATURATION_RAW) {
        Serial.println("  Saturated. Skipping rest of this IT level.");
        g_saturationCount++;
        isSaturatedThisIT = true;
        break; 
      }

      if (newReading >= g_config.LOW_THRESHOLD_RAW) { 
        long score = -abs((long)g_config.OPTIMAL_TARGET_RAW - (long)newReading);
        if (score > bestScore) {
          bestScore = score;
          bestGearIT = it;
          bestGearPWM = pwm;
        }
      } 
    } // End PWM loop
    
    if (bestGearIT != -1) { break; } // Found best gear
    
  } // End IT loop

  if (bestGearIT != -1) {
    Serial.print("Jumping to new gear: IT "); Serial.print(g_config.itDelays[bestGearIT]);
    Serial.print("ms, PWM "); Serial.print(g_config.pwmSettings[bestGearPWM]); Serial.println("%");
    if (vemlSetConfig(bestGearIT)) { pwmSetLevel(bestGearPWM); } 
    else { handleI2CError("Search->Final SetConfig"); g_state = IDLE; return; }
  } else {
    Serial.println("!!! At max sensitivity, but reading is still below threshold or saturated.");
    if (vemlSetConfig(g_config.IT_COUNT - 1)) { pwmSetLevel(g_config.PWM_COUNT - 1); } 
    else { handleI2CError("Search->Final SetConfig Max"); g_state = IDLE; return; }
  }

  g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
  g_state = MEASURING;
}


void runMeasurementLoop() {
  uint16_t rawReading;
  if (!vemlRead16(REG_ALS_DATA_L, rawReading)) {
    handleI2CError("Measure->vemlRead16"); return;
  }
  
  bool wasSaturated = (rawReading >= SATURATION_RAW);
  if (wasSaturated) { g_saturationCount++; }
  
  g_lastAlsRaw = getFilteredReading(rawReading);

  if (!g_filterIsPrimed) {
    g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
    return;
  }
  
  bool configChanged = false;
   
  if (g_lastAlsRaw < g_config.LOW_THRESHOLD_RAW && g_lastAlsRaw > 0) {
      if (!wasSaturated) { findAndSetBestNewGear(); } 
      else { 
         Serial.println("Raw saturated, filter low - delaying search.");
         g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
      }
    return; 
  } else if (g_lastAlsRaw > g_config.HIGH_THRESHOLD_RAW || (wasSaturated && g_currentItIndex == 0 && g_currentPwmIndex == 0) ) { 
    if (g_currentPwmIndex > 0) {
      pwmSetLevel(g_currentPwmIndex - 1);
      configChanged = true;
    } else if (g_currentItIndex > 0) {
      if (vemlSetConfig(g_currentItIndex - 1)) {
          pwmSetLevel(g_config.PWM_COUNT - 1); // Go to max PWM of the lower IT
          configChanged = true;
      } else { handleI2CError("Measure->vemlSetConfig Down"); return; }
    } else {
        Serial.println("!!! At minimum sensitivity but reading is too high/saturated!");
        g_lastAbsorbance = -99.1f; 
    }
  }
  
  if (configChanged) {
    g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
    return; 
  }
  
  if (g_lastAbsorbance != -99.1f) { 
      uint16_t current_I0 = g_blankingData.blankValues[g_currentItIndex][g_currentPwmIndex];
      uint16_t current_I = g_lastAlsRaw;
      
      if (current_I0 == 0 || current_I0 == 65535) { g_lastAbsorbance = -99.0f; } 
      else {
        if (current_I > current_I0) { current_I = current_I0; }
        if (current_I <= 0) { g_lastAbsorbance = 9.9f; } 
        else { g_lastAbsorbance = -log10((float)current_I / (float)current_I0); }
      }
  } 

  Serial.print("Absorbance: "); Serial.print(g_lastAbsorbance, 3);
  Serial.print(", RAW_filt: "); Serial.print(g_lastAlsRaw);
  Serial.print(", RAW_inst: "); Serial.print(rawReading); 
  Serial.print(", I_0: "); Serial.print(g_blankingData.blankValues[g_currentItIndex][g_currentPwmIndex]);
  Serial.print(", IT: "); Serial.print(g_config.itDelays[g_currentItIndex]);
  Serial.print("ms, PWM: "); Serial.print(g_config.pwmSettings[g_currentPwmIndex]);
  Serial.println("%");
  
  g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
}


// ==========================
// Serial parsing (Expanded)
// ==========================
void handleSerialInput() { /* ... (no changes needed) ... */ 
  static char buf[64];
  static size_t idx = 0;

  while (Serial.available() > 0) {
    int c = Serial.read();
    if (c == '\r') continue;
    if (c == '\n') {
      buf[idx] = '\0';
      idx = 0;
      
      String cmd = String(buf);
      cmd.trim();
      if (cmd.length() == 0) continue;

      if (cmd.equals("blank")) {
        if (g_state == IDLE) runBlankingRoutine();
        else Serial.println("Error: Busy");
      } else if (cmd.equals("start")) {
        if (!g_blankIsDone) Serial.println("Error: Please run 'blank' first.");
        else if (g_state == IDLE) {
          Serial.println("--- Starting Measurement ---");
          g_state = MEASURING;
          g_nextReadTime = millis(); 
          if (vemlSetConfig(0)) { 
              pwmSetLevel(0);
              resetReadingFilter();
          } else {
               handleI2CError("Start->vemlSetConfig"); 
               g_state = IDLE; 
          }
        }
      } else if (cmd.equals("stop")) {
        if (g_state == MEASURING || g_state == SEARCHING) {
          Serial.println("--- Stopping Measurement ---");
          g_state = IDLE;
          // pwmSetDutyPercent(0.0f); // Use analogWrite
          analogWrite(LED_PIN, 0); // Turn off LED
        }
      } else if (cmd.equals("print_blank")) {
         if (!g_blankIsDone) { Serial.println("No blanking data."); continue; }
        Serial.println("--- Stored Blanking Data (I_0) ---");
        for (int i = 0; i < g_config.IT_COUNT; i++) {
          Serial.print("IT "); Serial.print(g_config.itDelays[i]); Serial.println("ms:");
          for (int j = 0; j < g_config.PWM_COUNT; j++) {
            Serial.print("  PWM "); Serial.print(g_config.pwmSettings[j]);
            Serial.print("%: \t"); Serial.println(g_blankingData.blankValues[i][j]);
          }
        }
      } else if (cmd.equals("print_config")) {
        Serial.println("--- Current Device Config ---");
        Serial.print("Low Thresh: "); Serial.println(g_config.LOW_THRESHOLD_RAW);
        Serial.print("High Thresh: "); Serial.println(g_config.HIGH_THRESHOLD_RAW);
        Serial.print("Opt. Target: "); Serial.println(g_config.OPTIMAL_TARGET_RAW);
      } else if (cmd.equals("print_health")) {
        Serial.println("--- System Health Metrics ---");
        Serial.print("I2C Errors: "); Serial.println(g_i2cErrorCount);
        Serial.print("Saturation Events: "); Serial.println(g_saturationCount);
        Serial.print("Sensor Resets: "); Serial.println(g_sensorResets);
      } else if (cmd.equals("save_config")) {
        saveConfig();
      } else if (cmd.equals("load_config")) {
        loadConfig();
      } else if (cmd.startsWith("set low ")) {
         int val = cmd.substring(8).toInt();
         if (val > 0 && val < g_config.HIGH_THRESHOLD_RAW) {
            g_config.LOW_THRESHOLD_RAW = val;
            Serial.print("Live config: LOW_THRESHOLD_RAW set to "); Serial.println(g_config.LOW_THRESHOLD_RAW);
         } else { Serial.println("Invalid value."); }
      } else if (cmd.startsWith("set high ")) {
         int val = cmd.substring(9).toInt();
          if (val > g_config.LOW_THRESHOLD_RAW && val < SATURATION_RAW) {
            g_config.HIGH_THRESHOLD_RAW = val;
            Serial.print("Live config: HIGH_THRESHOLD_RAW set to "); Serial.println(g_config.HIGH_THRESHOLD_RAW);
         } else { Serial.println("Invalid value."); }
      } else if (cmd.startsWith("set opt ")) {
        int val = cmd.substring(8).toInt();
         if (val > g_config.LOW_THRESHOLD_RAW && val < g_config.HIGH_THRESHOLD_RAW) {
            g_config.OPTIMAL_TARGET_RAW = val;
            Serial.print("Live config: OPTIMAL_TARGET_RAW set to "); Serial.println(g_config.OPTIMAL_TARGET_RAW);
         } else { Serial.println("Invalid value."); }
      } else if (cmd.equals("test on")) {
          if (g_state != IDLE) { Serial.println("Error: test only in IDLE"); }
          else { g_ledTestEnable = true; g_ledTestPct = 0.0f; g_ledTestDir = 1; g_ledTestNextMs = millis(); Serial.println("LED test enabled"); }
      } else if (cmd.equals("test off")) {
        g_ledTestEnable = false;
         if (g_state == IDLE) analogWrite(LED_PIN, 0); // Use analogWrite
        Serial.println("LED test disabled");
      } else if (cmd.startsWith("test period ")) {
        long ms = cmd.substring(12).toInt();
        if (ms < 5) ms = 5;
        g_ledTestPeriodMs = static_cast<uint32_t>(ms);
        Serial.print("LED test period set to "); Serial.print(g_ledTestPeriodMs); Serial.println(" ms");
      } else {
        Serial.println("Unknown command.");
      }
    } else {
      if (idx < sizeof(buf) - 1) { buf[idx++] = static_cast<char>(c); } 
      else { idx = 0; } // overflow
    }
  }
}

// ==========================
// Arduino entry points
// ==========================
void setup() {
  Serial.begin(115200);
  delay(1000); 
  Serial.println("\n--- Biomass Sensor Firmware v2.2 (analogWrite) ---"); 

  Serial.println("DEBUG: Setup start."); delay(100);

  Serial.println("DEBUG: Initializing NVS..."); delay(100);
  if (!g_prefs.begin(NVS_NAMESPACE, false)) {
      Serial.println("!!! CRITICAL: Failed to initialize NVS. Halting."); delay(100);
      while(1) delay(1000); 
  }
  loadConfig(); 
  loadBlankingData(); 
  Serial.println("DEBUG: NVS Init complete."); delay(100);

  // *** Setup analogWrite ***
  Serial.println("DEBUG: Setting up analogWrite..."); delay(100);
  pinMode(LED_PIN, OUTPUT);       // Set pin as output
  analogWrite(LED_PIN, 0);        // Start with LED off
  Serial.println("DEBUG: analogWrite setup complete."); delay(100);

  // *** Remove LEDC setup ***
  // int assignedChannel = ledcAttach(LED_PWM_PIN, LEDC_FREQ_HZ, LEDC_RES_BITS);
  // ... (removed LEDC init code) ...
  // pwmSetDutyPercent(0.0f); // Now handled by analogWrite above

  Serial.println("DEBUG: Initializing I2C..."); delay(100);
  if (!Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN)) {
      Serial.println("!!! CRITICAL: Failed to initialize I2C (Wire.begin failed). Check pins/wiring. Halting."); delay(100);
      while(1) delay(1000); 
  }
  Wire.setClock(100000);
  delay(10); 
  Serial.println("DEBUG: I2C Initialized."); delay(100);

  Serial.println("DEBUG: Configuring initial sensor state..."); delay(100);
  if (!vemlSetConfig(0)) {
    Serial.println("!!! CRITICAL: Initial vemlSetConfig FAILED during setup. Check sensor wiring/power. Halting."); delay(100);
    while(1) { delay(1000); esp_task_wdt_reset(); } 
  } else {
    Serial.println("DEBUG: Initial sensor config OK."); delay(100);
  }
  // pwmSetLevel(0); // Set initial PWM level - now done by analogWrite(LED_PIN, 0)


  Serial.println("DEBUG: Initializing Watchdog..."); delay(100);
  esp_task_wdt_config_t twdt_config = {
    .timeout_ms   = WDT_TIMEOUT_S * 1000,
    .idle_core_mask = (1 << CONFIG_FREERTOS_NUMBER_OF_CORES) - 1, 
    .trigger_panic  = true
  };
  esp_err_t err = esp_task_wdt_init(&twdt_config);
  if (err != ESP_OK) {
    Serial.print("!!! WARNING: esp_task_wdt_init failed, code "); Serial.println((int)err); delay(100);
  } else {
    Serial.println("DEBUG: Watchdog timer initialized."); delay(100);
     err = esp_task_wdt_add(NULL); 
     if (err != ESP_OK) {
       Serial.print("!!! WARNING: esp_task_wdt_add failed, code "); Serial.println((int)err); delay(100);
     } else {
       Serial.println("DEBUG: Main task added to Watchdog."); delay(100);
     }
  }

  Serial.println("--- Setup complete ---"); delay(100); 
  Serial.println("System IDLE.");
  if (g_blankIsDone) Serial.println("Type 'start' to begin measurement.");
  else Serial.println("Type 'blank' to calibrate.");
}

void loop() {
  esp_task_wdt_reset(); 
  
  handleSerialInput();
  
  uint32_t now = millis();

  if (g_ledTestEnable && g_state == IDLE && now >= g_ledTestNextMs) {
    g_ledTestNextMs = now + g_ledTestPeriodMs;
    g_ledTestPct += g_ledTestDir * 2.0f;
    g_ledTestPct = max(0.0f, min(100.0f, g_ledTestPct)); 
    if (g_ledTestPct >= 100.0f) g_ledTestDir = -1; 
    if (g_ledTestPct <= 0.0f)   g_ledTestDir =  1; 
    pwmSetDutyPercent(g_ledTestPct); // This now calls analogWrite
    // Serial output removed for potentially smoother test
  }
  
  switch (g_state) {
    case IDLE:
      delay(10); 
      break;
    case BLANKING:
      // Blocking function, WDT fed inside
      break;
    case SEARCHING:
      // Blocking function, WDT fed inside
      break;
      
    case MEASURING:
      if (now >= g_nextReadTime) {
        runMeasurementLoop(); 
      } else {
         delay(1); 
      }
      break;
  }
}
