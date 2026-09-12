/**
 * @file main.cpp
 * @brief Firmware for an ESP32-based Peristaltic Pump Controller
 *
 * CHANGELOG (v3.2):
 * - CRITICAL FIX (Volume): Corrected a bug in `pwmTask` (Core 1) where
 * `q_actual_ml_per_sec` was not reset to 0.0f each loop, causing
 * `g_cumulativeVolumeMl` to increase even when PWM was 0.
 * - CRITICAL FIX (Latch): Corrected the `MIN_MOTOR_ON_TIME_MS` latch logic.
 * The latch now "freezes" the last active speed command (`g_cmdSpeed`)
 * for the latch duration, forcing the motor to run instead of just being
 * "enabled" with a speed of 0.0.
 * - MOD: Increased `MIN_MOTOR_ON_TIME_MS` to 500ms as requested.
 *
 * CHANGELOG (v3.1):
 * - MOD: Disabled WiFi STA mode. The pump now operates as a standalone AP
 * ("FeedPump") and will no-longer search for or connect to the "ModuloTECNAL"
 * sensor hub.
 * - MOD: Commented out `checkWifi()`, `pollHubForCommands()`, and
 * `sendDataToHub()` calls in the main loop to support standalone mode.
 *
 * CHANGELOG (v3.0):
 * - CRITICAL FIX: Switched Volume Integration Source. The estimated cumulative
 * volume (`g_cumulativeVolumeMl`) is now calculated based on the actual
 * integer **PWM duty cycle** (`g_actualPwmDuty`) being applied, as requested,
 * instead of the float `g_cmdSpeed`.
 * - REFACTOR: Consolidated PWM application logic entirely onto Core 1 (`pwmTask`).
 * - REFACTOR: Removed redundant PWM estimation logic from Core 0's main loop.
 * - NEW: Added `g_actualPwmDuty` (Core 1 writes, Core 0 reads) for accurate reporting.
 *
 * @note The system now uses the calibration model: mlMin = speed * slope + intercept,
 * where PWM is derived from speed, and volume is derived from PWM.
 */

#include <Arduino.h>
#include <math.h>
#include <Preferences.h>      // For ESP32 Non-Volatile Storage (NVS)
#include <esp_task_wdt.h>     // For Watchdog Timer
#include <WiFi.h>
#include <HTTPClient.h>
#include <WebServer.h>        // Using simple WebServer for AP

// ───────────────────────────── DEBUG CONFIG ─────────────────────────────
static const bool     DEBUG_ENABLE      = true;
static const uint32_t DEBUG_INTERVAL_MS = 1000;     // Print period for main loop

// ─────────────────────────────────── PINOUT ───────────────────────────────────
#define R_EN_PIN  25 // Right enable (must be HIGH to run)
#define L_EN_PIN  26 // Left  enable (must be HIGH to run)
#define R_PWM_PIN 14 // RPWM (PWM for forward)
#define L_PWM_PIN 27 // LPWM (PWM for reverse)

#define POT_INT_PIN  34 // Intensity pot (ADC1) - MANUAL MODE
#define POT_GAIN_PIN 35 // Direction  pot (ADC1) - MANUAL MODE
#define SENSOR_PIN   15 // Digital sensor (active LOW)
#define SENSOR_ENABLE_BUTTON_PIN 32 // Button (INPUT_PULLUP)
#define SENSOR_STATUS_LED_PIN    33 // LED

// ────────────────────────── NETWORKING CONFIGURATION ──────────────────────────
// --- Hub (STA) Config ---
static const char* HUB_SSID_A = "ModuloTECNAL_1";
static const char* HUB_SSID_B = "ModuloTECNAL_2";
String sensorHubDataURL = "http://192.168.4.1/pumpData";
String sensorHubCommandURL = "http://192.168.4.1/pumpCommand";

// --- Access Point (AP) Config ---
static const char* AP_SSID = "FeedPump";
IPAddress apIP(192, 168, 6, 1); // Use a different subnet from the hub (192.168.4.x)
IPAddress apGateway(192, 168, 6, 1);
IPAddress apSubnet(255, 255, 255, 0);

WebServer server(80); // Web server for our own AP
HTTPClient http;      // Client for communicating with the hub

// --- Networking Globals ---
String g_lastDataJson = "{}"; // Holds the latest JSON data string
String g_lastKnownSsid = "";

// WiFi State Machine
enum WifiState { WF_IDLE, WF_SCANNING, WF_CONNECTING };
static WifiState g_wifiState = WF_IDLE;
static unsigned long g_wifiNextActionMs = 0;
unsigned long WIFI_RECONNECT_PERIOD_MS = 10000; // Check/retry every 10 seconds

unsigned long lastHubPollMs = 0;
unsigned long HUB_POLL_PERIOD_MS = 2000; // Poll hub every 2 seconds
unsigned long lastDataPushMs = 0;
unsigned long DATA_PUSH_PERIOD_MS = 1000; // Push data to hub every 1s

// ────────────────────────────── CONSTANTS / TUNING ────────────────────────────
const float    V_MAX                  = 1000.0f; // |speed| ≤ V_MAX → 100% duty
const float    ENABLE_EPS             = 1.0f;    // |speed| threshold to enable H-bridge. Min speed unit.
const uint32_t MIN_MOTOR_ON_TIME_MS   = 500;     // (v3.2) Minimum time for motor to run once triggered
const uint32_t TASK_DELAY_MS          = 2;       // core1 PWM/position cadence
const uint32_t SENSOR_DEBOUNCE_MS     = 50;      // digital sensor debounce
const float    ADC_LPF_ALPHA          = 0.10f;   // LPF for pots

// LEDC PWM (ESP32 v3 API)
static const uint8_t  PWM_RES_BITS  = 10; // 10-bit (0..1023)
static const uint16_t PWM_MAX_DUTY  = (1u << PWM_RES_BITS) - 1; // 1023
static const uint16_t PWM_BREAKAWAY = 140; // Min PWM to overcome torque (as 10-bit value)
uint32_t pwmFreqHz = 10000;                  // default 10 kHz

// ADC
const uint8_t  ADC_RES    = 12;
const uint16_t ADC_MAX    = (1 << ADC_RES) - 1; // 4095
const float    ADC_CENTER = static_cast<float>(ADC_MAX) / 2.0f;

// WDT
constexpr int WDT_TIMEOUT_S = 10; // 10-second watchdog

// ────────────────────────── NVS & CONFIG STRUCT ───────────────────────────────
Preferences g_prefs;
constexpr char* NVS_NAMESPACE  = "feed_pump";
constexpr char* NVS_KEY_CONFIG = "config";

struct PumpConfig {
  // Mode & Operation
  int mode; // 0=Idle/Manual, 1=Const, 2=Linear, 3=Exp

  // Timed Operation
  float init_t_min;   // Wait time before starting (minutes)
  float final_t_min; // Stop time (minutes)
                      // Both are relative to command send time

  // Mode 1, 2, 3 parameters
  float lambda_const;
  float lambda_linear;
  float phi_linear;
  float lambda_exp;
  float phi_exp;

  // Calibration: ml/min <-> "speed_units" (0-1000)
  float pumpSlope;     // (speed_units) / (ml/min)
  float pumpIntercept; // (speed_units)

  // PID
  float pid_kp;
  float pid_ki;
  float pid_kd;

  // Checksum
  uint32_t crc32;
};

PumpConfig g_config;
bool g_configDirty = false; // Flag to trigger NVS save

// ────────────────────────────── GLOBAL STATE ──────────────────────────────────
TaskHandle_t pwmTaskHandle = nullptr;

// --- Core 1 (PWM Task) writes, Core 0 (Logic) reads ---
volatile float g_cumulativeVolumeMl = 0.0f; // Integrated by Core 1
volatile int   g_actualPwmDuty = 0;      // (v3.0) Actual PWM value applied (for reporting)

// --- Core 0 (Logic) writes, Core 1 (PWM Task) reads ---
volatile float g_cmdSpeed      = 0.0f;  // Commanded speed (0-V_MAX) for Core 1
volatile bool  g_driverEnabled = false; // H-Bridge enable state

// --- Core 0 (Logic) state ---
enum OperationState { OP_IDLE, OP_WAITING, OP_RUNNING };
static OperationState g_opState = OP_IDLE;
unsigned long g_opTriggerTimeMs = 0;      // Time the *command* was received (t=0)
float g_currentFlowRateMlMin = 0.0f; // Target flow rate (for reporting)
float g_current_t_min = 0.0f;        // Global time since trigger
unsigned long g_motorOnLatchTimeMs = 0; // (v2.9) Latch for minimum run time
float g_latchedSpeed = 0.0f;            // (v3.2) Speed to hold during min-on latch

// PID state
float g_pid_error_sum = 0.0f;
float g_pid_last_error = 0.0f;
unsigned long g_pid_last_time_ms = 0;

// USB/Pot state
volatile bool  disablePot    = false;
volatile float usbSpeedSteps = 0.0f; // Manual speed from USB/WiFi
volatile bool  hasUsbSpeed   = false;

// Sensor gate
volatile bool  sensorEnable         = false;
volatile bool  sensorBypass         = false;
volatile bool  sensorButtonOverride = true;
bool           sensorWetState       = false;

// ──────────────────────────── PROTOTYPES ──────────────────────────────────────
// --- Core 1 Task ---
void pwmTask(void* pv);

// --- Setup ---
void setupADC();
void setupSensorPin();
void loadConfig();
void saveConfig();
uint32_t calculateCRC32(const uint8_t *data, size_t length);

// --- Main Loop (Core 0) ---
void handleSerialInput();
void processJsonCommand(String json);
void checkWifi();
void pollHubForCommands();
float calcPotSpeed();
void updateSensorGate();
void updateOperationState();
void runOperationLogic();
void buildDataJson();

// --- Mode & PID Logic ---
void resetOperationState();
float calculateTargetFlow(float t_min);
float calculateTargetVolume(float t_min, float t_init); // MODIFIED
float updatePID(float v_target_ml, float v_actual_ml);

// --- Conversion & PWM ---
float mlminToSpeedUnits(float mlMin);
float speedUnitsToMlmin(float speedUnits);
void applyDutyFromSpeed(float sAbs, bool dirPositive); // Removed PWM logic from here, now just a placeholder
float pwmDutyToMlmin(int duty); // (v3.0) New: Converts applied PWM duty back to flow rate

// --- Networking ---
bool httpGet(const String& url, int& code, String& body);
void sendDataToHub();
void handleReadData();
void handleCommand();
void handleNotFound();

// --- JSON Helpers ---
long getJsonValue(String json, String key);
float getJsonFloatValue(String json, String key);
String getJsonStringValue(String json, String key);


// ────────────────────────────────── CORE 1 TASK ───────────────────────────────
/**
 * @brief High-frequency task running on Core 1.
 * - Applies PWM signal to the H-bridge based on g_cmdSpeed (Core 0 command).
 * - Integrates the *actual* (estimated) volume pumped using the applied PWM duty.
 */
void pwmTask(void* pv) {
  uint32_t lastUs = micros();

  for (;;) {
    // 1. Read volatile state from Core 0
    float s = g_cmdSpeed; // Commanded speed units (0-V_MAX)
    bool enabled = g_driverEnabled;

    // 2. Apply PWM logic (CONSOLIDATED HERE - v3.0)
    uint32_t duty = 0;
    bool dirPos = true;
    
    if (enabled) {
      float sAbs = fabsf(s);
      sAbs = constrain(sAbs, 0.0f, V_MAX);
      dirPos = (s >= 0.0f);
      
      // Calculate PWM duty based on speed
      if (sAbs < ENABLE_EPS) {
        // Should not happen if g_driverEnabled is true, but safety first
        duty = 0;
      } else {
        // Remap [ENABLE_EPS, V_MAX] -> [PWM_BREAKAWAY, PWM_MAX_DUTY]
        float s_frac = (sAbs - ENABLE_EPS) / (V_MAX - ENABLE_EPS);
        s_frac = constrain(s_frac, 0.0f, 1.0f);
        duty = (uint32_t)lroundf(
            s_frac * (float)(PWM_MAX_DUTY - PWM_BREAKAWAY) + PWM_BREAKAWAY
        );
        duty = constrain(duty, (uint32_t)PWM_BREAKAWAY, (uint32_t)PWM_MAX_DUTY);
      }
    } 
    
    // Apply PWM to pins and update Core 1's reporting global
    g_actualPwmDuty = duty;
    
    // Output stage (Sets the actual voltage/current to the motor)
    if (duty == 0) {
      // Brake
      ledcWrite(R_PWM_PIN, 0);
      ledcWrite(L_PWM_PIN, 0);
    } else {
      if (dirPos) {
        // Forward
        ledcWrite(R_PWM_PIN, duty);
        ledcWrite(L_PWM_PIN, 0);
      } else {
        // Reverse
        ledcWrite(R_PWM_PIN, 0);
        ledcWrite(L_PWM_PIN, duty);
      }
    }

    // 3. Integrate position estimate (actual volume) - NOW USING PWM DUTY (v3.0)
    uint32_t nowUs = micros();
    float dt_sec = (nowUs - lastUs) * 1e-6f;
    lastUs = nowUs;

    // CRITICAL FIX (v3.2): Reset flow rate to 0 *every loop* before checking.
    float q_actual_ml_per_sec = 0.0f;
    
    // Integrate volume ONLY if the PWM value is active (i.e., above breakaway)
    if (duty >= PWM_BREAKAWAY) {
        float q_actual_mlmin = pwmDutyToMlmin(duty); // Use the actual applied PWM duty
        q_actual_ml_per_sec = q_actual_mlmin / 60.0f;
    }

    // Update global cumulative volume (disable interrupts for atomic update)
    // This check is now safe because q_actual_ml_per_sec is guaranteed to be 0
    // if the pump was off during this loop.
    if (q_actual_ml_per_sec != 0.0f) {
      taskDISABLE_INTERRUPTS();
      g_cumulativeVolumeMl += (q_actual_ml_per_sec * (double)dt_sec);
      taskENABLE_INTERRUPTS();
    }

    vTaskDelay(pdMS_TO_TICKS(TASK_DELAY_MS));
  }
}

// ────────────────────────────────── SETUP ─────────────────────────────────────
void setup() {
  Serial.begin(115200);
  delay(100);
  Serial.println("--- Peristaltic Pump Controller v3.2 (Critical Fixes) ---");

  // 1. Load config from NVS
  g_prefs.begin(NVS_NAMESPACE, false);
  loadConfig();

  // 2. Pinout
  pinMode(R_EN_PIN, OUTPUT);
  pinMode(L_EN_PIN, OUTPUT);

  // Enable both half-bridges permanently.
  digitalWrite(R_EN_PIN, HIGH);
  digitalWrite(L_EN_PIN, HIGH);

  pinMode(SENSOR_ENABLE_BUTTON_PIN, INPUT_PULLUP);
  pinMode(SENSOR_STATUS_LED_PIN, OUTPUT);

  // 3. PWM init
  ledcAttach(R_PWM_PIN, pwmFreqHz, PWM_RES_BITS);
  ledcAttach(L_PWM_PIN, pwmFreqHz, PWM_RES_BITS);
  ledcWrite(R_PWM_PIN, 0); // Start off
  ledcWrite(L_PWM_PIN, 0);

  // 4. ADC & Sensor
  setupADC();
  setupSensorPin();

  // 5. WiFi Setup (AP+STA)
  Serial.println("[NET] Setting mode to WIFI_AP (Standalone)...");
  WiFi.mode(WIFI_AP); // v3.1: Changed from WIFI_AP_STA to WIFI_AP

  // Configure and Start AP
  Serial.printf("[NET] Configuring AP on subnet %s\n", apIP.toString().c_str());
  WiFi.softAPConfig(apIP, apGateway, apSubnet);
  if (WiFi.softAP(AP_SSID, "")) { // No password
    Serial.printf("[NET] AP Started: %s (IP: %s)\n", AP_SSID, WiFi.softAPIP().toString().c_str());
  } else {
    Serial.println("[NET] AP Start Failed!");
  }

  // 6. Web Server
  server.on("/readData", HTTP_GET, handleReadData);
  server.on("/command", HTTP_POST, handleCommand);
  server.on("/", HTTP_GET, handleReadData); // Default to data
  server.onNotFound(handleNotFound);
  server.begin();
  Serial.println("[NET] Web server started.");

  // 7. Watchdog Setup
  esp_task_wdt_config_t twdt_config = {
      .timeout_ms   = WDT_TIMEOUT_S * 1000,
      .idle_core_mask = (1 << 1), // Watch core 0, ignore idle core 1
      .trigger_panic  = true
  };
  esp_task_wdt_init(&twdt_config);
  esp_task_wdt_add(NULL); // Add this task (Core 0) to WDT
  Serial.println("Watchdog timer initialized for Core 0.");

  // 8. Start Core 1 Task
  xTaskCreatePinnedToCore(pwmTask, "Pump-PWM-Vol", 4096, nullptr, 1,
                          &pwmTaskHandle, 1);
  Serial.println("Core 1 (PWM & Volume Task) started.");
  
  // 9. Init operation state
  resetOperationState(); // Set initial timestamps and clear volume
  g_opState = OP_IDLE; // Start in idle
  g_wifiNextActionMs = millis(); // Start first WiFi check

  Serial.println("System IDLE. Awaiting commands...");
}

void setupADC() {
  analogReadResolution(ADC_RES);
  analogSetPinAttenuation(POT_INT_PIN,  ADC_11db);
  analogSetPinAttenuation(POT_GAIN_PIN, ADC_11db);
}

void setupSensorPin() {
  pinMode(SENSOR_PIN, INPUT_PULLUP); // active LOW = WET
}

// ────────────────────────────────── LOOP (core 0) ─────────────────────────────
void loop() {
  static uint32_t lastDbg = 0;

  // 1. Feed the watchdog
  esp_task_wdt_reset();

  // 2. Handle Network Clients (AP)
  server.handleClient();

  // 3. Handle Serial Commands
  handleSerialInput();

  // 4. Handle Network (STA) - NON-BLOCKING
  // checkWifi(); // v3.1: Disabled STA mode
  
  uint32_t now = millis();
  /* v3.1: Disabled Hub Polling
  if (WiFi.status() == WL_CONNECTED && (now - lastHubPollMs >= HUB_POLL_PERIOD_MS)) {
    lastHubPollMs = now;
    pollHubForCommands();
  }
  */
  
  // 5. Save config to NVS if it has changed
  if (g_configDirty) {
    saveConfig(); // This also clears the dirty flag
  }

  // 6. Read Manual Inputs (Pots & Sensor Button)
  if (!sensorButtonOverride) {
    sensorEnable = !digitalRead(SENSOR_ENABLE_BUTTON_PIN); // LOW = pressed = enabled
  }
  digitalWrite(SENSOR_STATUS_LED_PIN, sensorEnable ? HIGH : LOW);
  float potSpeed = 0.0f;
  if (!disablePot) potSpeed = calcPotSpeed();

  // 7. Update Global Time
  if (g_opState == OP_IDLE) {
    g_current_t_min = 0.0f;
  } else {
    // This is the global time 't' since the command was triggered
    g_current_t_min = (millis() - g_opTriggerTimeMs) / 60000.0f;
  }

  // 8. Update Timed Operation State Machine
  updateOperationState();

  // 9. Run Main Operation Logic (PID or Manual)
  float requestedSpeed = 0.0f;
  if (g_opState == OP_RUNNING) {
    // --- PID / MODE-BASED LOGIC ---
    runOperationLogic(); // This calculates and sets g_currentFlowRateMlMin
    requestedSpeed = mlminToSpeedUnits(g_currentFlowRateMlMin);
    
    // FIX: Clamp requested speed to 0.0f
    if (requestedSpeed < 0.0f) {
      requestedSpeed = 0.0f;
    }

  } else if (g_opState == OP_IDLE) {
    // --- MANUAL LOGIC ---
    // Arbitration: USB/WiFi overrides potentiometer when present
    requestedSpeed = hasUsbSpeed ? usbSpeedSteps : potSpeed;
    // Update reporting variable (based on the requested speed, which may not be the actual flow)
    g_currentFlowRateMlMin = speedUnitsToMlmin(requestedSpeed);
  }
  // If in OP_WAITING, requestedSpeed remains 0.0, g_currentFlowRateMlMin is 0

  // 10. Sensor Gate
  updateSensorGate();
  bool allowRun = true;
  if (sensorEnable && !sensorBypass) {
    allowRun = sensorWetState; // only run when sensor is WET
  }

  // 11. Apply final command to globals for Core 1 (g_cmdSpeed and g_driverEnabled)
  float finalSpeed = allowRun ? requestedSpeed : 0.0f;
  finalSpeed = constrain(finalSpeed, -V_MAX, V_MAX);

  // (v3.2) CRITICAL FIX for Minimum Run Time Latch
  unsigned long now_ms = millis();
  bool pidWantsOn = (allowRun && finalSpeed >= ENABLE_EPS);

  if (g_motorOnLatchTimeMs == 0) {
      // Latch is NOT active
      if (pidWantsOn) {
          // PID wants to turn ON. Start the latch.
          g_driverEnabled = true;
          g_cmdSpeed = finalSpeed;
          g_latchedSpeed = finalSpeed; // Store the speed we're latching at
          g_motorOnLatchTimeMs = now_ms;
      } else {
          // PID wants OFF. Stay off.
          g_driverEnabled = false;
          g_cmdSpeed = finalSpeed; // (will be 0 or < ENABLE_EPS)
          g_latchedSpeed = 0.0f;
      }
  } else {
      // Latch IS active. Check if it has expired.
      if (now_ms - g_motorOnLatchTimeMs >= MIN_MOTOR_ON_TIME_MS) {
          // Latch expired.
          g_motorOnLatchTimeMs = 0; // Clear the latch
          
          // Now, re-evaluate based on what PID wants *right now*
          if (pidWantsOn) {
              // PID *still* wants on. Keep it on (and start a new latch if it's a new "on" signal)
              g_driverEnabled = true;
              g_cmdSpeed = finalSpeed;
              g_latchedSpeed = 0.0f; // Latch is clear
          } else {
              // PID wants off. Turn it off.
              g_driverEnabled = false;
              g_cmdSpeed = finalSpeed; // (will be 0)
              g_latchedSpeed = 0.0f;
          }
      } else {
          // Latch is still active. Force ON using the latched speed.
          g_driverEnabled = true;
          g_cmdSpeed = g_latchedSpeed; // CRITICAL: Force speed to latched value
      }
  }
  
  // 12. Periodic Debug & Data Push
  now = millis(); // Get time again
  if (now - lastDbg >= DEBUG_INTERVAL_MS) {
    lastDbg = now;
    
    // Build the JSON string for all outputs
    buildDataJson();
    
    Serial.println(g_lastDataJson);

    // Debug
    if (DEBUG_ENABLE && g_opState != OP_IDLE) {
      // Only print debug if not idle
      Serial.printf("[DEBUG] OpState:%d Mode:%d TgtQ:%.2f ActV:%.2f TgtV:%.2f PIDSum:%.2f\n",
        (int)g_opState, g_config.mode, (double)g_currentFlowRateMlMin,
        (double)g_cumulativeVolumeMl, 
        (double)calculateTargetVolume(g_current_t_min, g_config.init_t_min), // Use global time
        (double)g_pid_error_sum);
    }
  }

  // Push data to hub (rate-limited)
  /* v3.1: Disabled Hub Data Push
  if (now - lastDataPushMs >= DATA_PUSH_PERIOD_MS) {
      lastDataPushMs = now;
      if (WiFi.status() == WL_CONNECTED) {
          sendDataToHub();
      }
  }
  */
}

// ───────────────── MAIN OPERATION & PID LOGIC (Core 0) ───────────────────

/**
 * @brief Checks and updates the automatic operation state (IDLE/WAITING/RUNNING).
 */
void updateOperationState() {
  // g_current_t_min is updated in the main loop
  
  // --- State: WAITING (Timer running before start) ---
  if (g_opState == OP_WAITING) {
    g_currentFlowRateMlMin = 0.0; // Ensure pump is reporting 0 flow
    // Check if it's time to start
    if (g_current_t_min >= g_config.init_t_min) {
      Serial.printf("[STATE] init_t (%.2f min) reached. Starting operation.\n", g_config.init_t_min);
      g_opState = OP_RUNNING;
      resetOperationState(); // Reset volume and PID *at the start*
    }
  }
  // --- State: RUNNING (Pumping) ---
  else if (g_opState == OP_RUNNING) {
    // Check if it's time to stop
    // final_t_min <= 0 means run indefinitely (manual start)
    if (g_config.final_t_min > 0.0f && g_current_t_min >= g_config.final_t_min) {
      Serial.printf("[STATE] final_t (%.2f min) reached. Stopping operation.\n", g_config.final_t_min);
      g_opState = OP_IDLE;
      g_config.mode = 0; // Go to idle mode
      g_configDirty = true;
      resetOperationState(); // Clear volume/PID for next run
    }
  }
  // --- State: IDLE (Do nothing) ---
  // (Handled in the main loop)
}


/**
 * @brief Resets only the *runtime* variables (volume, PID) to zero.
 */
void resetOperationState() {
  Serial.println("[STATE] Resetting runtime state (Volume, PID).");
  taskDISABLE_INTERRUPTS();
  g_cumulativeVolumeMl = 0.0f;
  taskENABLE_INTERRUPTS();
  
  g_pid_error_sum = 0.0f;
  g_pid_last_error = 0.0f;
  g_pid_last_time_ms = millis();
  g_motorOnLatchTimeMs = 0; // (v2.9) Clear the motor latch
  g_latchedSpeed = 0.0f;    // (v3.2) Clear the latched speed
}


/**
 * @brief Runs the main PID control loop (only when g_opState == OP_RUNNING).
 */
void runOperationLogic() {
  // We know g_opState == OP_RUNNING if we are here
  // g_current_t_min is the global time
  
  // 1. Get Target Setpoints
  float Q_target_mlmin = calculateTargetFlow(g_current_t_min);
  float V_target_ml = calculateTargetVolume(g_current_t_min, g_config.init_t_min);

  // 2. Get Process Variable (Actual Volume)
  float V_actual_ml;
  taskDISABLE_INTERRUPTS();
  V_actual_ml = g_cumulativeVolumeMl;
  taskENABLE_INTERRUPTS();

  // 3. Calculate PID Correction
  float pid_adj_mlmin = updatePID(V_target_ml, V_actual_ml);

  // 4. Apply Correction
  float Q_final_mlmin = Q_target_mlmin + pid_adj_mlmin;

  // Anti-windup and safety clamp
  if (Q_final_mlmin < 0) Q_final_mlmin = 0;

  // 5. Store result for reporting and main loop
  g_currentFlowRateMlMin = Q_final_mlmin;
}

/**
 * @brief Calculates the target flow rate Q(t) for the current time.
 * @param t_min The time *since command was sent* in minutes.
 */
float calculateTargetFlow(float t_min) {
  // Note: This function calculates the *instantaneous* flow rate at time t,
  // even if t < init_t. The main loop ensures the pump is off.
  switch (g_config.mode) {
    case 1: // Constant
      return g_config.lambda_const;
    
    case 2: // Linear
      // Q(t) = λ + φt
      return g_config.lambda_linear + g_config.phi_linear * t_min;
      
    case 3: // Exponential
      // Q(t) = λe^φt
      return g_config.lambda_exp * expf(g_config.phi_exp * t_min);
      
    default:
      return 0.0f;
  }
}

/**
 * @brief Calculates the target *cumulative volume* V(t).
 * This is the integral from t_init to t_min.
 * @param t_min The *current* time since command was sent.
 * @param t_init The *start* time since command was sent.
 */
float calculateTargetVolume(float t_min, float t_init) {
  float V_target = 0.0f;
  
  // If we haven't reached the start time yet, target is 0
  if (t_min <= t_init) {
    return 0.0f;
  }
  
  switch (g_config.mode) {
    case 1: // Constant
      // V(t) = ∫[t_init, t_min] λ dt = λ * (t_min - t_init)
      V_target = g_config.lambda_const * (t_min - t_init);
      break;
    
    case 2: // Linear
      // V(t) = ∫[t_init, t_min] (λ + φt) dt = [λt + 0.5φt²] from t_init to t_min
      V_target = (g_config.lambda_linear * t_min + 0.5f * g_config.phi_linear * t_min * t_min) -
                 (g_config.lambda_linear * t_init + 0.5f * g_config.phi_linear * t_init * t_init);
      break;
      
    case 3: // Exponential
      // V(t) = ∫[t_init, t_min] (λe^φt) dt = [ (λ/φ)e^φt ] from t_init to t_min
      // V(t) = (λ/φ)(e^(φ*t_min) - e^(φ*t_init))
      if (fabsf(g_config.phi_exp) < 1e-6) {
        // Phi is zero, treat as constant (Mode 1)
        V_target = g_config.lambda_exp * (t_min - t_init);
      } else {
        V_target = (g_config.lambda_exp / g_config.phi_exp) *
                   (expf(g_config.phi_exp * t_min) - expf(g_config.phi_exp * t_init));
      }
      break;
      
    default:
      V_target = 0.0f;
  }
  
  return V_target;
}


/**
 * @brief Simple PID controller.
 * @param v_target_ml The desired cumulative volume.
 * @param v_actual_ml The actual (estimated) cumulative volume.
 * @return The correction value in (mL/min).
 */
float updatePID(float v_target_ml, float v_actual_ml) {
  unsigned long now = millis();
  float dt = (now - g_pid_last_time_ms) / 60000.0f; // PID time delta in minutes
  if (dt <= 0) return 0.0f; // Avoid divide by zero

  float error = v_target_ml - v_actual_ml;

  // Proportional
  float p_out = g_config.pid_kp * error;

  // Integral (with anti-windup)
  g_pid_error_sum += error * dt;
  // Simple anti-windup: clamp integrator
  g_pid_error_sum = constrain(g_pid_error_sum, -100.0f, 100.0f); // Arbitrary clamp
  float i_out = g_config.pid_ki * g_pid_error_sum;

  // Derivative
  float d_error = (error - g_pid_last_error) / dt;
  float d_out = g_config.pid_kd * d_error;

  // Store state
  g_pid_last_error = error;
  g_pid_last_time_ms = now;

  // Total output
  return p_out + i_out + d_out;
}


// ───────────────── POTENTIOMETER SAMPLING (Core 0) ───────────────────────
float calcPotSpeed() {
  static float filtInt  = ADC_CENTER; // Intensity pot
  static float filtGain = ADC_CENTER; // Direction pot

  int rawI = analogRead(POT_INT_PIN);
  int rawG = analogRead(POT_GAIN_PIN);

  // Low-pass
  filtInt  = ADC_LPF_ALPHA  * rawI + (1.0f - ADC_LPF_ALPHA)  * filtInt;
  filtGain = ADC_LPF_ALPHA * rawG + (1.0f - ADC_LPF_ALPHA) * filtGain; // FIX: Was 1.o-

  // Linear normalize
  float mag = filtInt / (float)ADC_MAX;       // 0..1
  mag = constrain(mag, 0.0f, 1.0f);

  float dir = (filtGain - ADC_CENTER) / ADC_CENTER;   // -1..+1
  dir = constrain(dir, -1.0f, 1.0f);

  float signedMag = dir * mag;                // -1..+1
  float out = signedMag * V_MAX;              // -V_MAX..+V_MAX

  return out;
}

// ───────────────────── COMMAND PARSING (Core 0) ───────────────────────────
/**
 * @brief Polls the Serial port and processes incoming JSON commands.
 */
void handleSerialInput() {
  static char buf[512]; // Increased buffer size for larger JSON
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
        Serial.println("Error: Command must be in JSON format. e.g. {\"mode\":1}");
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
 * @brief Processes a JSON command string from any source (Serial, WiFi).
 */
void processJsonCommand(String json) {
  Serial.println("[CMD] Processing: " + json);

  String cmd = getJsonStringValue(json, "command");
  bool paramChanged = false; // Flag to reset state

  if (cmd.length() > 0) {
    if (cmd.equals("start")) {
      // Manual "start" forces an immediate run with no stop time
      Serial.println("[CMD] Manual start.");
      g_config.init_t_min = 0.0f;
      g_config.final_t_min = 0.0f; // 0 means run forever
      g_configDirty = true; // Save this change
      
      g_opState = OP_RUNNING;
      g_opTriggerTimeMs = millis(); // Set global t=0
      resetOperationState(); // Reset vol and PID
      
      // If mode is 0, print warning but allow "run" (at 0 flow)
      if (g_config.mode == 0) {
        Serial.println("[CMD] Warning: Starting with mode 0. Pump will not run.");
      }

    } else if (cmd.equals("stop")) {
      // Manual "stop" forces IDLE
      Serial.println("[CMD] Manual stop.");
      g_opState = OP_IDLE;
      g_config.mode = 0; // Set mode to idle
      g_configDirty = true;
      resetOperationState(); // Reset vol and PID
      
    } else if (cmd.equals("reset_volume")) {
      Serial.println("[CMD] Resetting cumulative volume.");
      resetOperationState();
      
    } else if (cmd.equals("save_config")) {
      saveConfig();
    } else if (cmd.equals("load_config")) {
      loadConfig();
    } else if (cmd.equals("print_config")) {
        buildDataJson(); // Build a JSON with current config
        Serial.println(g_lastDataJson);
    } else if (cmd.equals("clear_nvs")) {
        Serial.println("[CMD] Clearing all preferences from NVS...");
        g_prefs.clear();
        Serial.println("[CMD] NVS cleared. Rebooting to load defaults.");
        delay(1000);
        ESP.restart();
    }
  }

  // --- Check for settings changes ---
  float fval = getJsonFloatValue(json, "mode");
  if (!isnan(fval)) {
    int newMode = (int)fval;
    if (newMode >= 0 && newMode <= 3) {
      if (newMode != g_config.mode) {
        g_config.mode = newMode;
        g_configDirty = true;
        paramChanged = true;
        Serial.printf("[CMD] Mode set to %d\n", g_config.mode);
      }
    } else {
        Serial.printf("[ERR] Invalid mode %d. Must be 0-3.\n", newMode);
    }
  }
  
  // --- Timed Operation ---
  fval = getJsonFloatValue(json, "init_t");
  if (!isnan(fval) && g_config.init_t_min != fval) { g_config.init_t_min = fval; g_configDirty = true; paramChanged = true; }
  fval = getJsonFloatValue(json, "final_t");
  if (!isnan(fval) && g_config.final_t_min != fval) { g_config.final_t_min = fval; g_configDirty = true; paramChanged = true; }


  // --- Manual Commands ---
  fval = getJsonFloatValue(json, "speed"); // Manual speed
  if (!isnan(fval)) {
    usbSpeedSteps = fval;
    hasUsbSpeed   = true;
    g_opState = OP_IDLE; // Manual speed forces IDLE
    g_config.mode = 0;
  }
  fval = getJsonFloatValue(json, "disablePot");
  if (!isnan(fval)) {
    disablePot = (fval == 1.0f);
  }

  // --- Sensor Commands ---
  fval = getJsonFloatValue(json, "sensorEnable");
  if (!isnan(fval)) sensorEnable = (fval == 1.0f);
  fval = getJsonFloatValue(json, "sensorBypass");
  if (!isnan(fval)) sensorBypass = (fval == 1.0f);
  fval = getJsonFloatValue(json, "sensorButtonOverride");
  if (!isnan(fval)) sensorButtonOverride = (fval == 1.0f);

  // --- Calibration & PID ---
  fval = getJsonFloatValue(json, "pumpSlope");
  if (!isnan(fval) && g_config.pumpSlope != fval) { g_config.pumpSlope = fval; g_configDirty = true; }
  fval = getJsonFloatValue(json, "pumpIntercept");
  if (!isnan(fval) && g_config.pumpIntercept != fval) { g_config.pumpIntercept = fval; g_configDirty = true; }
  fval = getJsonFloatValue(json, "pid_kp");
  if (!isnan(fval) && g_config.pid_kp != fval) { g_config.pid_kp = fval; g_configDirty = true; }
  fval = getJsonFloatValue(json, "pid_ki");
  if (!isnan(fval) && g_config.pid_ki != fval) { g_config.pid_ki = fval; g_configDirty = true; }
  fval = getJsonFloatValue(json, "pid_kd");
  if (!isnan(fval) && g_config.pid_kd != fval) { g_config.pid_kd = fval; g_configDirty = true; }

  // --- Mode Parameters ---
  fval = getJsonFloatValue(json, "lambda_const");
  if (!isnan(fval) && g_config.lambda_const != fval) { g_config.lambda_const = fval; g_configDirty = true; paramChanged = true; }
  fval = getJsonFloatValue(json, "lambda_linear");
  if (!isnan(fval) && g_config.lambda_linear != fval) { g_config.lambda_linear = fval; g_configDirty = true; paramChanged = true; }
  fval = getJsonFloatValue(json, "phi_linear");
  if (!isnan(fval) && g_config.phi_linear != fval) { g_config.phi_linear = fval; g_configDirty = true; paramChanged = true; }
  fval = getJsonFloatValue(json, "lambda_exp");
  if (!isnan(fval) && g_config.lambda_exp != fval) { g_config.lambda_exp = fval; g_configDirty = true; paramChanged = true; }
  fval = getJsonFloatValue(json, "phi_exp");
  if (!isnan(fval) && g_config.phi_exp != fval) { g_config.phi_exp = fval; g_configDirty = true; paramChanged = true; }

  
  // If any key parameter changed, reset state and check for auto-start
  if (paramChanged && cmd.isEmpty()) {
      Serial.println("[CMD] Parameter change detected, resetting state.");
      resetOperationState();
      g_opTriggerTimeMs = millis(); // Set global t=0
      
      if (g_config.mode > 0) {
        if (g_config.init_t_min > 0.0f) {
          g_opState = OP_WAITING;
          Serial.printf("[STATE] Mode %d set. Waiting for %.2f min to start.\n", g_config.mode, g_config.init_t_min);
        } else {
          g_opState = OP_RUNNING; // Start immediately
          Serial.printf("[STATE] Mode %d set. Starting immediately (init_t=0).\n", g_config.mode);
        }
      } else {
        g_opState = OP_IDLE; // Mode is 0, so idle
      }
  } else if (paramChanged && !cmd.isEmpty()) {
      // This was a command ("start" or "stop") that *also* contained parameters
      // The command logic at the top already handled the state change.
      Serial.println("[CMD] Command with parameters was applied.");
  }
}

// ───────────────────── SENSOR LOGIC (Core 0) ──────────────────────────
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
    }
  }
}

// ───────────────────── mL/min <-> Speed Units Conversion ────────────────────
/**
 * @brief Converts flow rate (mL/min) to internal "speed units" (0-V_MAX).
 * Implements: speed = (mlMin - intercept) / slope
 */
float mlminToSpeedUnits(float mlMin) {
  // Add a safety check for divide-by-zero
  if (fabsf(g_config.pumpSlope) < 1e-6) {
    if (mlMin > 0.0f) Serial.println("[ERR] pumpSlope is 0. Cannot convert ml/min.");
    return 0.0f;
  }
  return (mlMin - g_config.pumpIntercept) / g_config.pumpSlope;
}

/**
 * @brief Converts internal "speed units" (0-V_MAX) to flow rate (mL/min).
 * Implements: mlMin = speed * slope + intercept
 */
float speedUnitsToMlmin(float speedUnits) {
  return speedUnits * g_config.pumpSlope + g_config.pumpIntercept;
}

/**
 * @brief (v3.0) Converts the ACTUAL integer PWM duty (0-1023) back to flow rate (mL/min).
 * This function is the new source of truth for volume integration.
 */
float pwmDutyToMlmin(int duty) {
  // 1. If PWM is below breakaway, flow is 0.0
  if (duty < PWM_BREAKAWAY) {
    return 0.0f;
  }
  
  // 2. Reverse the PWM duty to find the fractional speed (0.0 to 1.0)
  // Reverses: duty = PWM_BREAKAWAY + s_frac * (PWM_MAX_DUTY - PWM_BREAKAWAY)
  float duty_active_range = (float)(duty - PWM_BREAKAWAY);
  float duty_max_range = (float)(PWM_MAX_DUTY - PWM_BREAKAWAY);
  
  float s_frac = constrain(duty_active_range / duty_max_range, 0.0f, 1.0f);
  
  // 3. Convert fractional speed back to speed units (1.0 to V_MAX)
  // Reverses: speed = ENABLE_EPS + s_frac * (V_MAX - ENABLE_EPS)
  float speed = ENABLE_EPS + s_frac * (V_MAX - ENABLE_EPS);

  // 4. Convert speed units back to ml/min using the calibration
  // mlMin = speed * slope + intercept
  return speedUnitsToMlmin(speed);
}


// ───────────────────── LEDC / PWM (Now CONSOLIDATED to Core 1) ────────────────
/**
 * @brief (REMOVED LOGIC) Placeholder left in case it is called by external libraries.
 */
void applyDutyFromSpeed(float sAbs, bool dirPositive) {
    // Logic moved to pwmTask (Core 1) to consolidate hardware control.
}

// ───────────────────── NVS (STORAGE) FUNCTIONS ──────────────────────────
uint32_t calculateCRC32(const uint8_t *data, size_t length) {
  uint32_t crc = 0;
  for (size_t i = 0; i < length; i++) {
    crc += data[i];
  }
  return crc;
}

void loadConfig() {
  if (g_prefs.getBytes(NVS_KEY_CONFIG, &g_config, sizeof(PumpConfig)) ==
      sizeof(PumpConfig)) {
    // Calculate CRC
    uint32_t crc = calculateCRC32((uint8_t*)&g_config,
                                  sizeof(PumpConfig) - sizeof(uint32_t));
    if (crc == g_config.crc32) {
      Serial.println("Loaded valid config from NVS.");
      // Ensure mode is valid
      if (g_config.mode > 3) g_config.mode = 0;
      return;
    }
  }

  Serial.println("No valid config in NVS. Loading defaults.");
  g_config.mode = 0;
  g_config.init_t_min = 0.0f;
  g_config.final_t_min = 0.0f;
  
  g_config.lambda_const = 0.0f;
  g_config.lambda_linear = 0.0f;
  g_config.phi_linear = 0.0f;
  g_config.lambda_exp = 0.0f;
  g_config.phi_exp = 0.0f;
  
  // NEW: Updated calibration values from user
  g_config.pumpSlope =  0.0280188148f; 
  g_config.pumpIntercept = 1.7601988934f;

  g_config.pid_kp = 0.5f;
  g_config.pid_ki = 0.05f; // Updated
  g_config.pid_kd = 0.001f; // Updated

  g_configDirty = true; // Mark to save defaults
}

void saveConfig() {
  g_config.crc32 = calculateCRC32((uint8_t*)&g_config,
                                  sizeof(PumpConfig) - sizeof(uint32_t));
  if (g_prefs.putBytes(NVS_KEY_CONFIG, &g_config, sizeof(PumpConfig))) {
    Serial.println("Config saved to NVS.");
  } else {
    Serial.println("Error saving config to NVS.");
  }
  g_configDirty = false; // Clear flag
}

// ───────────────────── NETWORKING FUNCTIONS (Core 0) ────────────────────
/**
 * @brief Creates the JSON data string for serial, /readData, and /pumpData
 */
void buildDataJson() {
  float vol;
  int pwm_duty;
  taskDISABLE_INTERRUPTS();
  vol = g_cumulativeVolumeMl;
  pwm_duty = g_actualPwmDuty; // Read actual PWM from Core 1
  taskENABLE_INTERRUPTS();

  // Target volume is calculated based on global time and init time
  float v_target = calculateTargetVolume(g_current_t_min, g_config.init_t_min);

  String json = "{";
  json += "\"mode\":";
  json += String(g_config.mode);
  json += ",\"pwm\":";
  json += String(pwm_duty); // Using actual applied PWM from Core 1
  json += ",\"speed\":"; 
  json += String(g_cmdSpeed, 1);
  json += ",\"flow_rate_mlmin\":";
  json += String(g_currentFlowRateMlMin, 3);
  json += ",\"cum_volume_ml\":";
  json += String(vol, 3);
  json += ",\"v_target_ml\":";
  json += String(v_target, 3);
  json += ",\"active\":";
  json += (g_opState == OP_RUNNING) ? "true" : "false";
  json += ",\"waiting\":";
  json += (g_opState == OP_WAITING) ? "true" : "false";
  json += ",\"current_t_min\":";
  json += String(g_current_t_min, 3);
  json += ",\"init_t_min\":";
  json += String(g_config.init_t_min, 3);
  json += ",\"final_t_min\":";
  json += String(g_config.final_t_min, 3);
  json += "}";
  
  g_lastDataJson = json;
}

/**
 * @brief Handles GET /readData - returns MINIMAL JSON data
 */
void handleReadData() {
  // buildDataJson() is called in the main loop, so g_lastDataJson is fresh
  server.send(200, "application/json", g_lastDataJson);
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

/**
 * @brief Pushes minimal sensor data (as query params) to the Sensor Hub.
 */
void sendDataToHub() {
  float vol;
  int pwm_duty;
  taskDISABLE_INTERRUPTS();
  vol = g_cumulativeVolumeMl;
  pwm_duty = g_actualPwmDuty;
  taskENABLE_INTERRUPTS();

  float v_target = calculateTargetVolume(g_current_t_min, g_config.init_t_min);

  String url = sensorHubDataURL;
  url += "?mode=" + String(g_config.mode);
  url += "&pwm=" + String(pwm_duty);
  url += "&speed=" + String(g_cmdSpeed, 1); // NEW: Add speed
  url += "&flow=" + String(g_currentFlowRateMlMin, 3);
  url += "&vol=" + String(vol, 3);
  url += "&v_tgt=" + String(v_target, 3);
  url += "&active=" + String((g_opState == OP_RUNNING) ? 1 : 0);
  url += "&waiting=" + String((g_opState == OP_WAITING) ? 1 : 0);

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

/**
 * @brief Non-blocking check for WiFi STA connection and reconnects.
 * Runs as a state machine to avoid blocking the main loop.
 */
void checkWifi() {
  unsigned long now = millis();
  if (now < g_wifiNextActionMs) {
    return; // Not time yet
  }

  if (WiFi.status() == WL_CONNECTED) {
    g_wifiState = WF_IDLE; // Connected, do nothing
    g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS; // Check again in 10s
    return;
  }

  // --- If we are NOT connected ---
  
  switch(g_wifiState) {
    case WF_IDLE:
      // Time to start a new scan
      Serial.println("[NET] WiFi disconnected. Starting async scan...");
      WiFi.scanNetworks(true, true); // Start ASYNC scan
      g_wifiState = WF_SCANNING;
      g_wifiNextActionMs = now + 100; // Check scan status soon
      break;
      
    case WF_SCANNING:
    {
      int n = WiFi.scanComplete();
      if (n == -1) {
        // Still scanning...
        g_wifiNextActionMs = now + 100; // Check again soon
      } else if (n > 0) {
        // Scan finished, process results
        Serial.printf("[NET] Scan complete, %d networks found.\n", n);
        String ssidToTry = "";
        for (int i = 0; i < n; i++) {
          String ssid = WiFi.SSID(i);
          if (ssid == HUB_SSID_A || ssid == HUB_SSID_B) {
            ssidToTry = ssid;
            g_lastKnownSsid = ssid; // Store it
            break;
          }
        }
        
        if (ssidToTry != "") {
          Serial.printf("[NET] Hub found: %s. Attempting to connect...\n", ssidToTry.c_str());
          WiFi.disconnect(); // Ensure clean state
          delay(100);
          WiFi.begin(ssidToTry.c_str(), ssidToTry.c_str()); // password == SSID
          g_wifiState = WF_CONNECTING;
          g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS; // Set connect timeout
        } else {
          Serial.println("[NET] Hub not found in scan.");
          g_wifiState = WF_IDLE;
          g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS; // Retry scan after 10s
        }
        WiFi.scanDelete(); // Clear scan results
      } else {
        // n == 0
        Serial.println("[NET] Scan complete, no networks found.");
        g_wifiState = WF_IDLE;
        g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS; // Retry scan after 10s
      }
      break;
    }
      
    case WF_CONNECTING:
      // We are in this state if connect timed out
      Serial.println("[NET] Connect attempt timed out. Retrying scan.");
      g_wifiState = WF_IDLE; // Go back to idle to trigger a new scan
      g_wifiNextActionMs = now; // Retry immediately
      break;
  }
}

/**
 * @brief Simple blocking HTTP GET helper.
 */
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

// ───────────────────── CUSTOM JSON PARSERS ────────────────────────────
/**
 * @brief Simple helper to find a numeric (long) value for a given key.
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
  valueStr.trim();
  
  if (valueStr.length() == 0 || (!isDigit(valueStr.charAt(0)) && valueStr.charAt(0) != '-' && valueStr.charAt(0) != '.')) {
      return -999999;
  }
  
  return atol(valueStr.c_str());
}

/**
 * @brief Simple helper to find a numeric (float) value for a given key.
 * Returns NAN if key not found or value is not numeric.
 */
float getJsonFloatValue(String json, String key) {
  String searchKey = "\"" + key + "\":";
  int keyIndex = json.indexOf(searchKey);
  if (keyIndex == -1) {
    searchKey = "\"" + key + "\" :"; // try with space
    keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) return NAN; // Key not found
  }

  int valueIndex = keyIndex + searchKey.length();
  
  while(valueIndex < json.length() && isspace(json.charAt(valueIndex))) {
    valueIndex++;
  }

  if (json.charAt(valueIndex) == '\"') {
    return NAN;
  }

  int endIndex = json.indexOf(',', valueIndex);
  if (endIndex == -1) {
    endIndex = json.indexOf('}', valueIndex);
  }
  if (endIndex == -1) return NAN;

  String valueStr = json.substring(valueIndex, endIndex);
  valueStr.trim();
  
  if (valueStr.length() == 0 || (!isDigit(valueStr.charAt(0)) && valueStr.charAt(0) != '-' && valueStr.charAt(0) != '.')) {
      return NAN;
  }
  
  return valueStr.toFloat();
}

/**
 * @brief Simple helper to find a string value for a given key.
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