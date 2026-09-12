/**
 * @file main.cpp
 * @brief Firmware for an ESP32-based Peristaltic Pump Controller
 *
 * CHANGELOG (v4 - Hub command acknowledgement):
 * - ADDED: hub commands carry cmd_id and are applied at most once. The hub now
 *   retains a command until this node echoes the id it applied, instead of the
 *   old consume-on-read mailbox where a dropped HTTP response lost the command
 *   silently and a newer one overwrote an unread one.
 * - ADDED: &ack_cmd_id= on the /pumpData push. This is the whole acknowledgement
 *   channel: the hub clears its mailbox on it, and the PC shows the pending chip
 *   until it arrives.
 * - NOTE: the PC now disables this pump with TWO ordered frames, {"mode":0,
 *   "speed":0} and then {"pumpComm":0}. The old single frame did not stop the
 *   pump at all - the hub cleared its own routing flag while parsing it and then
 *   dropped the mode:0 travelling beside it, so this node kept dosing and only
 *   its telemetry went quiet. Nothing changes here for that; it is recorded
 *   because it explains why the disable sequence looks different on the wire.
 *
 * CHANGELOG (v3.8 - Robust Persistence & Optimization):
 * - ADDED: State persistence (Volume, Time, Mode) saved to NVS every 60s.
 * - ADDED: Automatic crash recovery (resumes profile on boot if previously active).
 * - FIXED: WDT Crash caused by large JSON serialization in the main loop.
 * - OPTIMIZED: Removed time_points/flow_points from periodic Serial/WiFi output.
 * - LOGIC: Receiving a new Mode command resets state/timer (fresh start).
 */

#include <Arduino.h>
#include <math.h>
#include <Preferences.h>
#include <esp_task_wdt.h>
#include <WiFi.h>
#include <HTTPClient.h>
#include <WebServer.h>

// ───────────────────────────── DEBUG CONFIG ─────────────────────────────
static const bool     DEBUG_ENABLE      = true;
static const uint32_t DEBUG_INTERVAL_MS = 1000;

// ─────────────────────────────────── PINOUT ───────────────────────────────────
#define R_EN_PIN 25
#define L_EN_PIN 26
#define R_PWM_PIN 14
#define L_PWM_PIN 27

#define POT_INT_PIN 34
#define POT_GAIN_PIN 35
#define SENSOR_PIN 15
#define SENSOR_ENABLE_BUTTON_PIN 32
#define SENSOR_STATUS_LED_PIN 33

// ────────────────────────── NETWORKING CONFIGURATION ──────────────────────────
// Hub STA
static const char* HUB_SSID_A = "ModuloTECNAL_1";
static const char* HUB_SSID_B = "ModuloTECNAL_2";
String sensorHubDataURL = "http://192.168.4.1/pumpData";
String sensorHubCommandURL = "http://192.168.4.1/pumpCommand";

// AP
static const char* AP_SSID = "FeedPump";
IPAddress apIP(192, 168, 6, 1);
IPAddress apGateway(192, 168, 6, 1);
IPAddress apSubnet(255, 255, 255, 0);

WebServer server(80);
HTTPClient http;

String g_lastDataJson = "{}";
String g_lastKnownSsid = "";

// WiFi SM
enum WifiState { WF_IDLE, WF_SCANNING, WF_CONNECTING };
static WifiState g_wifiState = WF_IDLE;
static unsigned long g_wifiNextActionMs = 0;
unsigned long WIFI_RECONNECT_PERIOD_MS = 10000;

unsigned long lastHubPollMs = 0;
unsigned long HUB_POLL_PERIOD_MS = 2000;
unsigned long lastDataPushMs = 0;
unsigned long DATA_PUSH_PERIOD_MS = 1000;

// Hub command acknowledgement (v4).
//
// The hub keeps re-delivering a command until it sees this id come back on the data
// push, so the same JSON arrives several times by design. Applying it twice would be
// wrong for anything that resets the profile clock - mode, init_t and final_t all call
// resetOperationState() - so a repeat is acknowledged and then ignored.
//
// 0 means "nothing applied yet"; the hub never issues id 0.
uint32_t g_lastAppliedHubCommandId = 0;


// ────────────────────────────── CONSTANTS / TUNING ────────────────────────────
const float     V_MAX                  = 1000.0f;
const float     ENABLE_EPS             = 1.0f;
const uint32_t  MIN_MOTOR_ON_TIME_MS   = 500;
const uint32_t  TASK_DELAY_MS          = 2;
const uint32_t  SENSOR_DEBOUNCE_MS     = 50;
const float     ADC_LPF_ALPHA          = 0.10f;

// --- FLEXIBILITY CONSTANTS ---
const uint8_t NUM_POLY_COEFFS = 21; 
const uint8_t MAX_SEGMENTS = 100;

// LEDC PWM
static const uint8_t  PWM_RES_BITS  = 10;
static const uint16_t PWM_MAX_DUTY  = (1u << PWM_RES_BITS) - 1;
static const uint16_t PWM_BREAKAWAY = 155;
uint32_t pwmFreqHz = 7500;

// ADC
const uint8_t  ADC_RES    = 12;
const uint16_t ADC_MAX    = (1 << ADC_RES) - 1;
const float    ADC_CENTER = static_cast<float>(ADC_MAX) / 2.0f;

// WDT
constexpr int WDT_TIMEOUT_S = 15; // Increased slightly for safety

// ────────────────────────── NVS & CONFIG STRUCT ───────────────────────────────
Preferences g_prefs;
constexpr char* NVS_NAMESPACE  = (char*)"feed_pump";
constexpr char* NVS_KEY_CONFIG = (char*)"config";

// Keys for State Persistence
constexpr char* NVS_KEY_STATE_ACTIVE = (char*)"s_active";
constexpr char* NVS_KEY_STATE_VOL    = (char*)"s_vol";
constexpr char* NVS_KEY_STATE_TIME   = (char*)"s_time";
constexpr char* NVS_KEY_STATE_MODE   = (char*)"s_mode";

struct PumpConfig {
    int mode; // 0 idle, 1 const, 2 linear, 3 exp, 4 poly, 5 piecewise

    float init_t_min;
    float final_t_min;

    // Mode 1-3 params
    float lambda_const;
    float lambda_linear;
    float phi_linear;
    float lambda_exp;
    float phi_exp;

    // Mode 4: Polynomial
    double polyCoeffs[NUM_POLY_COEFFS]; 

    // Mode 5: Piecewise Linear
    int num_segments; 
    float time_points[MAX_SEGMENTS]; 
    float flow_points[MAX_SEGMENTS]; 

    // Calibration & PID
    float pumpSlope;
    float pumpIntercept;

    float pid_kp;
    float pid_ki;
    float pid_kd;

    uint32_t crc32;
};

PumpConfig g_config;
bool g_configDirty = false;

// ────────────────────────────── GLOBAL STATE ──────────────────────────────────
TaskHandle_t pwmTaskHandle = nullptr;

// Core1 → Core0
volatile float g_cumulativeVolumeMl = 0.0f;
volatile int   g_actualPwmDuty = 0;

// Core0 → Core1
volatile float g_cmdSpeed      = 0.0f;
volatile bool  g_driverEnabled = false;

// Core0 state
enum OperationState { OP_IDLE, OP_WAITING, OP_RUNNING };
static OperationState g_opState = OP_IDLE;
unsigned long g_opTriggerTimeMs = 0;
float g_currentFlowRateMlMin = 0.0f;
float g_current_t_min = 0.0f;
unsigned long g_motorOnLatchTimeMs = 0;
float g_latchedSpeed = 0.0f;

// PID state
float g_pid_error_sum = 0.0f;
float g_pid_last_error = 0.0f;
unsigned long g_pid_last_time_ms = 0;

// Persistence State
unsigned long g_lastStateSaveMs = 0;
const unsigned long STATE_SAVE_INTERVAL_MS = 60000; // Save every 60s

// Manual
volatile bool  disablePot      = false;
volatile float usbSpeedSteps = 0.0f;
volatile bool  hasUsbSpeed   = false;

// Sensor gate
volatile bool  sensorEnable          = false;
volatile bool  sensorBypass          = false;
volatile bool  sensorButtonOverride = true;
bool           sensorWetState        = false;

// ──────────────────────────── PROTOTYPES ──────────────────────────────────────
void pwmTask(void* pv);

// Setup
void setupADC();
void setupSensorPin();
void loadConfig();
void saveConfig();
uint32_t calculateCRC32(const uint8_t *data, size_t length);

// Persistence
void checkAndRecoverState();
void saveRuntimeState();
void clearRuntimeState();

// Main Loop helpers
void handleSerialInput();
void processJsonCommand(String json);
void checkWifi();
void pollHubForCommands();
float calcPotSpeed();
void updateSensorGate();
void updateOperationState();
void runOperationLogic();
void buildDataJson(bool includeArrays = false); // Modified prototype

// Mode & PID
void resetOperationState();
float calculateTargetFlow(float t_rel_min);
float calculateTargetVolume(float t_rel_min);
float evalPolyIntegral(float t_rel);
float updatePID(float v_target_ml, float v_actual_ml);
float interpolate(float t, float t1, float t2, float q1, float q2);

// Conversion & PWM
float mlminToSpeedUnits(float mlMin);
float speedUnitsToMlmin(float speedUnits);
void  applyDutyFromSpeed(float sAbs, bool dirPositive);
float pwmDutyToMlmin(int duty);

// Networking
bool httpGet(const String& url, int& code, String& body);
void sendDataToHub();
void handleReadData();
void handleCommand();
void handleNotFound();

// JSON helpers
long  getJsonValue(String json, String key);
float getJsonFloatValue(String json, String key);
String getJsonStringValue(String json, String key);
double getJsonDoubleValue(String json, String key);

// ────────────────────────────────── CORE 0 TASK ───────────────────────────────
void pwmTask(void* pv) {
    uint32_t lastUs = micros();

    for (;;) {
        float s = g_cmdSpeed;
        bool enabled = g_driverEnabled;

        uint32_t duty = 0;
        bool dirPos = true;
        
        if (enabled) {
            float sAbs = fabsf(s);
            sAbs = constrain(sAbs, 0.0f, V_MAX);
            dirPos = (s >= 0.0f);
            if (sAbs < ENABLE_EPS) {
                duty = 0;
            } else {
                float s_frac = (sAbs - ENABLE_EPS) / (V_MAX - ENABLE_EPS);
                s_frac = constrain(s_frac, 0.0f, 1.0f);
                duty = (uint32_t)lroundf(
                    s_frac * (float)(PWM_MAX_DUTY - PWM_BREAKAWAY) + PWM_BREAKAWAY
                );
                duty = constrain(duty, (uint32_t)PWM_BREAKAWAY, (uint32_t)PWM_MAX_DUTY);
            }
        }

        g_actualPwmDuty = duty;

        if (duty == 0) {
            ledcWrite(R_PWM_PIN, 0);
            ledcWrite(L_PWM_PIN, 0);
        } else {
            if (dirPos) {
                ledcWrite(R_PWM_PIN, duty);
                ledcWrite(L_PWM_PIN, 0);
            } else {
                ledcWrite(R_PWM_PIN, 0);
                ledcWrite(L_PWM_PIN, duty);
            }
        }

        uint32_t nowUs = micros();
        float dt_sec = (nowUs - lastUs) * 1e-6f;
        lastUs = nowUs;

        float q_actual_ml_per_sec = 0.0f;
        if (duty >= PWM_BREAKAWAY) {
            float q_actual_mlmin = pwmDutyToMlmin(duty);
            q_actual_ml_per_sec = q_actual_mlmin / 60.0f;
        }

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
    Serial.println("--- Peristaltic Pump Controller v3.8 (Robust Recovery) ---");

    g_prefs.begin(NVS_NAMESPACE, false);
    loadConfig();

    pinMode(R_EN_PIN, OUTPUT);
    pinMode(L_EN_PIN, OUTPUT);
    digitalWrite(R_EN_PIN, HIGH);
    digitalWrite(L_EN_PIN, HIGH);

    pinMode(SENSOR_ENABLE_BUTTON_PIN, INPUT_PULLUP);
    pinMode(SENSOR_STATUS_LED_PIN, OUTPUT);

    ledcAttach(R_PWM_PIN, pwmFreqHz, PWM_RES_BITS);
    ledcAttach(L_PWM_PIN, pwmFreqHz, PWM_RES_BITS);
    ledcWrite(R_PWM_PIN, 0);
    ledcWrite(L_PWM_PIN, 0);

    setupADC();
    setupSensorPin();

    Serial.println("[NET] Setting mode to WIFI_AP_STA...");
    WiFi.mode(WIFI_AP_STA);

    Serial.printf("[NET] Configuring AP on subnet %s\n", apIP.toString().c_str());
    WiFi.softAPConfig(apIP, apGateway, apSubnet);
    if (WiFi.softAP(AP_SSID, "")) {
        Serial.printf("[NET] AP Started: %s (IP: %s)\n", AP_SSID, WiFi.softAPIP().toString().c_str());
    } else {
        Serial.println("[NET] AP Start Failed!");
    }

    server.on("/readData", HTTP_GET, handleReadData);
    server.on("/command", HTTP_POST, handleCommand);
    server.on("/", HTTP_GET, handleReadData);
    server.onNotFound(handleNotFound);
    server.begin();
    Serial.println("[NET] Web server started.");

    // Configure WDT for Core 1 (where loop() runs)
    esp_task_wdt_config_t twdt_config = {
        .timeout_ms     = WDT_TIMEOUT_S * 1000,
        .idle_core_mask = (1 << 0), // Watchdog can ignore Core 0
        .trigger_panic  = true
    };
    esp_task_wdt_init(&twdt_config);
    esp_task_wdt_add(NULL); // Add loopTask to WDT
    Serial.println("Watchdog timer initialized for Core 1.");

    // Pin the real-time PWM task to Core 0
    xTaskCreatePinnedToCore(pwmTask, "Pump-PWM-Vol", 4096, nullptr, 1, &pwmTaskHandle, 0);
    Serial.println("Core 0 (PWM & Volume Task) started.");

    // --- RECOVERY LOGIC ---
    checkAndRecoverState();

    g_wifiNextActionMs = millis();
}

void setupADC() {
    analogReadResolution(ADC_RES);
    analogSetPinAttenuation(POT_INT_PIN,  ADC_11db);
    analogSetPinAttenuation(POT_GAIN_PIN, ADC_11db);
}

void setupSensorPin() {
    pinMode(SENSOR_PIN, INPUT_PULLUP);
}

// ────────────────────────────────── LOOP (core 1) ─────────────────────────────
void loop() {
    static uint32_t lastDbg = 0;

    esp_task_wdt_reset(); // Reset Core 1 WDT

    server.handleClient();

    handleSerialInput();

    checkWifi();
    
    uint32_t now = millis();
    if (WiFi.status() == WL_CONNECTED && (now - lastHubPollMs >= HUB_POLL_PERIOD_MS)) {
        lastHubPollMs = now;
        pollHubForCommands();
    }
    
    if (g_configDirty) {
        saveConfig();
    }

    // --- PERSISTENCE: Save state every 60s if running ---
    if (g_opState == OP_RUNNING && (now - g_lastStateSaveMs >= STATE_SAVE_INTERVAL_MS)) {
        saveRuntimeState();
        g_lastStateSaveMs = now;
    }

    if (!sensorButtonOverride) {
        sensorEnable = !digitalRead(SENSOR_ENABLE_BUTTON_PIN);
    }
    digitalWrite(SENSOR_STATUS_LED_PIN, sensorEnable ? HIGH : LOW);
    float potSpeed = 0.0f;
    if (!disablePot) potSpeed = calcPotSpeed();

    if (g_opState == OP_IDLE) {
        g_current_t_min = 0.0f;
    } else {
        // Calculate current time based on the trigger time calculated at start or recovery
        g_current_t_min = (millis() - g_opTriggerTimeMs) / 60000.0f;
    }

    updateOperationState();

    float requestedSpeed = 0.0f;
    if (g_opState == OP_RUNNING) {
        runOperationLogic();
        requestedSpeed = mlminToSpeedUnits(g_currentFlowRateMlMin);
        if (requestedSpeed < 0.0f) requestedSpeed = 0.0f;
    } else if (g_opState == OP_IDLE) {
        requestedSpeed = hasUsbSpeed ? usbSpeedSteps : potSpeed;
        g_currentFlowRateMlMin = speedUnitsToMlmin(requestedSpeed);
    }

    updateSensorGate();
    bool allowRun = true;
    if (sensorEnable && !sensorBypass) {
        allowRun = sensorWetState;
    }

    float finalSpeed = allowRun ? requestedSpeed : 0.0f;
    finalSpeed = constrain(finalSpeed, -V_MAX, V_MAX);

    unsigned long now_ms = millis();
    bool pidWantsOn = (allowRun && fabsf(finalSpeed) >= ENABLE_EPS);

    if (g_motorOnLatchTimeMs == 0) {
        if (pidWantsOn) {
            g_driverEnabled = true;
            g_cmdSpeed = finalSpeed;
            g_latchedSpeed = finalSpeed;
            g_motorOnLatchTimeMs = now_ms;
        } else {
            g_driverEnabled = false;
            g_cmdSpeed = finalSpeed;
            g_latchedSpeed = 0.0f;
        }
    } else {
        if (now_ms - g_motorOnLatchTimeMs >= MIN_MOTOR_ON_TIME_MS) {
            g_motorOnLatchTimeMs = 0;
            if (pidWantsOn) {
                g_driverEnabled = true;
                g_cmdSpeed = finalSpeed;
                g_latchedSpeed = 0.0f;
            } else {
                g_driverEnabled = false;
                g_cmdSpeed = finalSpeed;
                g_latchedSpeed = 0.0f;
            }
        } else {
            g_driverEnabled = true;
            g_cmdSpeed = g_latchedSpeed;
        }
    }
    
    now = millis();
    if (now - lastDbg >= DEBUG_INTERVAL_MS) {
        lastDbg = now;

        // OPTIMIZATION: Do NOT include large arrays in periodic output to prevent WDT crash
        buildDataJson(false); 
        Serial.println(g_lastDataJson);

        if (DEBUG_ENABLE && g_opState != OP_IDLE) {
            float t_rel_dbg = g_current_t_min - g_config.init_t_min;
            if (t_rel_dbg < 0.0f) t_rel_dbg = 0.0f;
            Serial.printf("[DEBUG] OpState:%d Mode:%d TgtQ:%.2f ActV:%.2f TgtV:%.2f PIDSum:%.2f\n",
                (int)g_opState, g_config.mode, (double)g_currentFlowRateMlMin,
                (double)g_cumulativeVolumeMl, 
                (double)calculateTargetVolume(t_rel_dbg),
                (double)g_pid_error_sum);
        }
    }

    if (now - lastDataPushMs >= DATA_PUSH_PERIOD_MS) {
        lastDataPushMs = now;
        if (WiFi.status() == WL_CONNECTED) {
            sendDataToHub();
        }
    }
}

// ───────────────── PERSISTENCE & RECOVERY (Core 1) ────────────────────
void checkAndRecoverState() {
    bool isActive = g_prefs.getBool(NVS_KEY_STATE_ACTIVE, false);
    
    if (isActive) {
        int savedMode = g_prefs.getInt(NVS_KEY_STATE_MODE, 0);
        float savedVol = g_prefs.getFloat(NVS_KEY_STATE_VOL, 0.0f);
        float savedTime = g_prefs.getFloat(NVS_KEY_STATE_TIME, 0.0f);

        Serial.println(">>> DETECTED UNEXPECTED RESET! RECOVERING STATE <<<");
        Serial.printf("Recovering: Mode %d at %.2f min with %.2f mL\n", savedMode, savedTime, savedVol);

        // Restore Volume
        taskDISABLE_INTERRUPTS();
        g_cumulativeVolumeMl = savedVol;
        taskENABLE_INTERRUPTS();

        // Restore Mode (ensure config matches saved state if possible, though config is usually persistent)
        if (g_config.mode != savedMode) {
             g_config.mode = savedMode;
             // We don't mark dirty here to avoid immediate re-write, relies on config key match
        }

        // Calculate Trigger Time
        // current_t_min = (millis() - trigger) / 60000
        // trigger = millis() - (current_t_min * 60000)
        // We assume 'savedTime' is the time we were at.
        g_opTriggerTimeMs = millis() - (unsigned long)(savedTime * 60000.0f);

        g_opState = OP_RUNNING;
        
        // Reset PID
        g_pid_error_sum = 0.0f;
        g_pid_last_error = 0.0f;
        g_pid_last_time_ms = millis();

    } else {
        Serial.println("[BOOT] Clean start (No active state found).");
        resetOperationState();
        g_opState = OP_IDLE;
    }
}

void saveRuntimeState() {
    float vol;
    taskDISABLE_INTERRUPTS();
    vol = g_cumulativeVolumeMl;
    taskENABLE_INTERRUPTS();

    // Only save if we are actually doing something interesting
    if (g_config.mode != 0) {
        g_prefs.putBool(NVS_KEY_STATE_ACTIVE, true);
        g_prefs.putFloat(NVS_KEY_STATE_VOL, vol);
        g_prefs.putFloat(NVS_KEY_STATE_TIME, g_current_t_min);
        g_prefs.putInt(NVS_KEY_STATE_MODE, g_config.mode);
        Serial.println("[NVS] Checkpoint saved.");
    }
}

void clearRuntimeState() {
    // We only set Active to false, no need to wipe values
    g_prefs.putBool(NVS_KEY_STATE_ACTIVE, false);
    Serial.println("[NVS] Checkpoint cleared (Clean Stop).");
}

// ───────────────── MAIN OPERATION & PID LOGIC (Core 1) ───────────────────
void updateOperationState() {
    if (g_opState == OP_WAITING) {
        g_currentFlowRateMlMin = 0.0f;
        if (g_current_t_min >= g_config.init_t_min) {
            Serial.printf("[STATE] init_t (%.2f min) reached. Starting operation.\n", g_config.init_t_min);
            g_opState = OP_RUNNING;
            // Note: We do NOT reset volume here if we are recovering, logic handled in setup
            if (!g_prefs.getBool(NVS_KEY_STATE_ACTIVE, false)) {
                 resetOperationState(); 
            }
        }
    } else if (g_opState == OP_RUNNING) {
        if (g_config.final_t_min > 0.0f && g_current_t_min >= g_config.final_t_min) {
            Serial.printf("[STATE] final_t (%.2f min) reached. Stopping operation.\n", g_config.final_t_min);
            g_opState = OP_IDLE;
            g_config.mode = 0;
            g_configDirty = true;
            resetOperationState();
            clearRuntimeState(); // Operation complete, clear recovery flag
        }
    }
}

void resetOperationState() {
    Serial.println("[STATE] Resetting runtime state (Volume, PID).");
    taskDISABLE_INTERRUPTS();
    g_cumulativeVolumeMl = 0.0f;
    taskENABLE_INTERRUPTS();

    g_pid_error_sum = 0.0f;
    g_pid_last_error = 0.0f;
    g_pid_last_time_ms = millis();
    g_motorOnLatchTimeMs = 0;
    g_latchedSpeed = 0.0f;
}

void runOperationLogic() {
    float t_relative_min = g_current_t_min - g_config.init_t_min;
    if (t_relative_min < 0.0f) t_relative_min = 0.0f;

    float Q_target_mlmin = calculateTargetFlow(t_relative_min);
    float V_target_ml    = calculateTargetVolume(t_relative_min);

    float V_actual_ml;
    taskDISABLE_INTERRUPTS();
    V_actual_ml = g_cumulativeVolumeMl;
    taskENABLE_INTERRUPTS();

    float pid_adj_mlmin = updatePID(V_target_ml, V_actual_ml);

    float Q_final_mlmin = Q_target_mlmin + pid_adj_mlmin;
    if (Q_final_mlmin < 0.0f) Q_final_mlmin = 0.0f;

    g_currentFlowRateMlMin = Q_final_mlmin;
}

// ───────────────── CALCULATION HELPERS ───────────────────
float interpolate(float t, float t1, float t2, float q1, float q2) {
    if (t <= t1) return q1;
    if (t >= t2) return q2;
    if (fabsf(t2 - t1) < 1e-6f) return q1; 
    float alpha = (t - t1) / (t2 - t1);
    return q1 + alpha * (q2 - q1);
}

float calculateTargetFlow(float t_relative_min) {
    switch (g_config.mode) {
        case 1: return g_config.lambda_const;
        case 2: return g_config.lambda_linear + g_config.phi_linear * t_relative_min;
        case 3: return g_config.lambda_exp * expf(g_config.phi_exp * t_relative_min);
        case 4: {
            const double t = static_cast<double>(t_relative_min);
            double q = g_config.polyCoeffs[NUM_POLY_COEFFS - 1]; 
            for (int i = NUM_POLY_COEFFS - 2; i >= 0; --i) {
                q = q * t + g_config.polyCoeffs[i];
            }
            return static_cast<float>(q);
        }
        case 5: { // Piecewise linear
            if (g_config.num_segments < 2) return 0.0f; 
            for (int i = 0; i < g_config.num_segments - 1; i++) {
                if (t_relative_min <= g_config.time_points[i + 1]) {
                    return interpolate(
                        t_relative_min,
                        g_config.time_points[i], 
                        g_config.time_points[i + 1],
                        g_config.flow_points[i], 
                        g_config.flow_points[i + 1]
                    );
                }
            }
            return g_config.flow_points[g_config.num_segments - 1];
        }
        default: return 0.0f;
    }
}

float evalPolyIntegral(float t_rel) {
    const double t = static_cast<double>(t_rel);
    if (t == 0.0) return 0.0f;
    double v_prime = 0.0;
    for (int i = NUM_POLY_COEFFS - 1; i >= 0; --i) {
        double c_prime = g_config.polyCoeffs[i] / static_cast<double>(i + 1);
        v_prime = v_prime * t + c_prime;
    }
    return static_cast<float>(v_prime * t);
}

float calculateTargetVolume(float t_relative_min) {
    if (t_relative_min <= 0.0f) return 0.0f;

    switch (g_config.mode) {
        case 1: return g_config.lambda_const * t_relative_min;
        case 2: return g_config.lambda_linear * t_relative_min + 0.5f * g_config.phi_linear * t_relative_min * t_relative_min;
        case 3: 
            if (fabsf(g_config.phi_exp) < 1e-6f) return g_config.lambda_exp * t_relative_min;
            else return (g_config.lambda_exp / g_config.phi_exp) * (expf(g_config.phi_exp * t_relative_min) - 1.0f);
        case 4: return evalPolyIntegral(t_relative_min);
        case 5: { // Piecewise - trapezoidal
            if (g_config.num_segments < 2) return 0.0f;
            float volume = 0.0f;
            for (int i = 0; i < g_config.num_segments - 1; i++) {
                float t1 = g_config.time_points[i];
                float t2 = g_config.time_points[i + 1];
                float q1 = g_config.flow_points[i];
                float q2 = g_config.flow_points[i + 1];
                if (t_relative_min <= t1) break;
                if (t_relative_min >= t2) {
                    float dt = t2 - t1;
                    if (dt > 0) volume += 0.5f * (q1 + q2) * dt; 
                } else {
                    float dt = t_relative_min - t1;
                    float q_current = interpolate(t_relative_min, t1, t2, q1, q2);
                    if (dt > 0) volume += 0.5f * (q1 + q_current) * dt;
                    break; 
                }
            }
            return volume;
        }
        default: return 0.0f;
    }
}

float updatePID(float v_target_ml, float v_actual_ml) {
    unsigned long now = millis();
    float dt = (now - g_pid_last_time_ms) / 60000.0f;
    if (dt <= 0.0f) return 0.0f;

    float error = v_target_ml - v_actual_ml;
    float p_out = g_config.pid_kp * error;

    g_pid_error_sum += error * dt;
    g_pid_error_sum = constrain(g_pid_error_sum, -100.0f, 100.0f);
    float i_out = g_config.pid_ki * g_pid_error_sum;

    float d_error = (error - g_pid_last_error) / dt;
    float d_out = g_config.pid_kd * d_error;

    g_pid_last_error = error;
    g_pid_last_time_ms = now;

    return p_out + i_out + d_out;
}

// ───────────────── POTENTIOMETER SAMPLING (Core 1) ───────────────────────
float calcPotSpeed() {
    static float filtInt  = ADC_CENTER;
    static float filtGain = ADC_CENTER;

    int rawI = analogRead(POT_INT_PIN);
    int rawG = analogRead(POT_GAIN_PIN);

    filtInt  = ADC_LPF_ALPHA  * rawI + (1.0f - ADC_LPF_ALPHA)  * filtInt;
    filtGain = ADC_LPF_ALPHA  * rawG + (1.0f - ADC_LPF_ALPHA)  * filtGain;

    float mag = filtInt / (float)ADC_MAX;
    mag = constrain(mag, 0.0f, 1.0f);

    float dir = (filtGain - ADC_CENTER) / ADC_CENTER;
    dir = constrain(dir, -1.0f, 1.0f);

    float signedMag = dir * mag;
    float out = signedMag * V_MAX;

    return out;
}

// ───────────────────── COMMAND PARSING (Core 1) ───────────────────────────
void handleSerialInput() {
    static char buf[4096]; 
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
                Serial.println("Error: Command must be in JSON format.");
            }
        } else {
            if (idx < sizeof(buf) - 1) {
                buf[idx++] = static_cast<char>(c);
            } else {
                idx = 0;
            }
        }
    }
}

void processJsonCommand(String json) {
    Serial.println("[CMD] Processing: " + json);

    String cmd = getJsonStringValue(json, "command");
    bool paramChanged = false;
    float fval;

    if (cmd.length() > 0) {
        if (cmd.equals("start")) {
            Serial.println("[CMD] Manual start.");
            g_config.init_t_min  = 0.0f;
            g_config.final_t_min = 0.0f;
            g_configDirty = true;
            g_opState = OP_RUNNING;
            g_opTriggerTimeMs = millis();
            resetOperationState();
            clearRuntimeState(); // New start means clear old recovery
        } else if (cmd.equals("stop")) {
            Serial.println("[CMD] Manual stop.");
            g_opState = OP_IDLE;
            g_config.mode = 0;
            g_configDirty = true;
            resetOperationState();
            clearRuntimeState(); // Stop means we don't recover next time
        } else if (cmd.equals("reset_volume")) {
            Serial.println("[CMD] Resetting cumulative volume.");
            resetOperationState();
            // Don't clear NVS here unless we assume mode 0, 
            // but usually reset_volume implies staying in mode. 
            // We'll let next 60s save update the 0 vol.
        } else if (cmd.equals("save_config")) {
            saveConfig();
        } else if (cmd.equals("load_config")) {
            loadConfig();
        } else if (cmd.equals("print_config")) {
            buildDataJson(true); // Force include arrays
            Serial.println(g_lastDataJson);
        } else if (cmd.equals("clear_nvs")) {
            Serial.println("[CMD] Clearing all preferences...");
            g_prefs.clear();
            delay(1000);
            ESP.restart();
        }
    }

    fval = getJsonFloatValue(json, "mode");
    if (!isnan(fval)) {
        int newMode = (int)fval;
        if (newMode >= 0 && newMode <= 5) {
            // Even if mode is same, receiving it via command implies a "Set" action
            if (newMode != g_config.mode) {
                g_config.mode = newMode;
                g_configDirty = true;
            }
            // Explicit mode command resets the timer/volume
            paramChanged = true; 
        } else {
            Serial.printf("[ERR] Invalid mode %d\n", newMode);
        }
    }
    
    fval = getJsonFloatValue(json, "init_t");
    if (!isnan(fval) && g_config.init_t_min != fval) { g_config.init_t_min = fval; g_configDirty = true; paramChanged = true; }
    fval = getJsonFloatValue(json, "final_t");
    if (!isnan(fval) && g_config.final_t_min != fval) { g_config.final_t_min = fval; g_configDirty = true; paramChanged = true; }

    fval = getJsonFloatValue(json, "speed");
    if (!isnan(fval)) {
        usbSpeedSteps = fval;
        hasUsbSpeed   = true;
        g_opState = OP_IDLE;
        g_config.mode = 0;
    }
    fval = getJsonFloatValue(json, "disablePot");
    if (!isnan(fval)) {
        disablePot = (fval == 1.0f);
    }

    fval = getJsonFloatValue(json, "sensorEnable");
    if (!isnan(fval)) sensorEnable = (fval == 1.0f);
    fval = getJsonFloatValue(json, "sensorBypass");
    if (!isnan(fval)) sensorBypass = (fval == 1.0f);
    fval = getJsonFloatValue(json, "sensorButtonOverride");
    if (!isnan(fval)) sensorButtonOverride = (fval == 1.0f);

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

    // Mode 4: Polynomial
    bool polyChanged = false;
    for (int i = 0; i < NUM_POLY_COEFFS; i++) {
        String key = "p" + String(i);
        double dval = getJsonDoubleValue(json, key);
        if (!isnan(dval) && g_config.polyCoeffs[i] != dval) {
            g_config.polyCoeffs[i] = dval;
            g_configDirty = true;
            polyChanged = true;
        }
    }
    if (polyChanged) paramChanged = true;

    // Mode 5: Piecewise
    bool piecewiseChanged = false;
    fval = getJsonFloatValue(json, "num_segments");
    if (!isnan(fval)) {
        int n = (int)fval;
        if (n >= 2 && n <= MAX_SEGMENTS) {
            if (g_config.num_segments != n) {
                g_config.num_segments = n;
                g_configDirty = true;
                piecewiseChanged = true;
            }
        }
    }
    
    // Parse time points
    for (int i = 0; i < MAX_SEGMENTS; i++) {
        String key = "t" + String(i);
        fval = getJsonFloatValue(json, key);
        if (!isnan(fval)) {
            if (g_config.time_points[i] != fval) {
                g_config.time_points[i] = fval;
                g_configDirty = true;
                piecewiseChanged = true;
            }
        }
    }
    
    // Parse flow points
    for (int i = 0; i < MAX_SEGMENTS; i++) {
        String key = "q" + String(i);
        fval = getJsonFloatValue(json, key);
        if (!isnan(fval)) {
            if (g_config.flow_points[i] != fval) {
                g_config.flow_points[i] = fval;
                g_configDirty = true;
                piecewiseChanged = true;
            }
        }
    }

    if (piecewiseChanged) {
        paramChanged = true;
        if (g_config.mode == 5) {
            // First time point must be 0
            if (g_config.time_points[0] != 0.0f) {
                g_config.time_points[0] = 0.0f; 
            }
        }
    }
    
    // Logic: If parameters changed (or mode command sent), reset the pump cycle
    if (paramChanged && cmd.isEmpty()) {
        Serial.println("[CMD] Parameter change detected, resetting state.");
        resetOperationState();
        clearRuntimeState(); // Clear checkpoint, starting fresh
        g_opTriggerTimeMs = millis();
        
        if (g_config.mode > 0) {
            if (g_config.init_t_min > 0.0f) {
                g_opState = OP_WAITING;
                Serial.printf("[STATE] Waiting for %.2f min.\n", g_config.init_t_min);
            } else {
                g_opState = OP_RUNNING;
                Serial.println("[STATE] Starting immediately.");
            }
        } else {
            g_opState = OP_IDLE;
        }
    } else if (paramChanged && !cmd.isEmpty()) {
        // Handled within specific commands (start/stop)
    }
}

// ───────────────────── SENSOR LOGIC (Core 1) ──────────────────────────
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
            sensorWetState = (lastSteadyState == LOW);
        }
    }
}

// ───────────────────── mL/min <-> Speed Units Conversion ────────────────────
float mlminToSpeedUnits(float mlMin) {
    if (fabsf(g_config.pumpSlope) < 1e-6f) return 0.0f;
    return (mlMin - g_config.pumpIntercept) / g_config.pumpSlope;
}

float speedUnitsToMlmin(float speedUnits) {
    return speedUnits * g_config.pumpSlope + g_config.pumpIntercept;
}

float pwmDutyToMlmin(int duty) {
    if (duty < PWM_BREAKAWAY) return 0.0f;
    float duty_active_range = (float)(duty - PWM_BREAKAWAY);
    float duty_max_range = (float)(PWM_MAX_DUTY - PWM_BREAKAWAY);
    float s_frac = constrain(duty_active_range / duty_max_range, 0.0f, 1.0f);
    float speed = ENABLE_EPS + s_frac * (V_MAX - ENABLE_EPS);
    return speedUnitsToMlmin(speed);
}

// ───────────────────── LEDC / PWM placeholder ────────────────────────────────
void applyDutyFromSpeed(float, bool) {}

// ───────────────────── NVS (STORAGE) FUNCTIONS ──────────────────────────
uint32_t calculateCRC32(const uint8_t *data, size_t length) {
    uint32_t crc = 0;
    for (size_t i = 0; i < length; i++) crc += data[i];
    return crc;
}

void loadConfig() {
    size_t configSize = g_prefs.getBytesLength(NVS_KEY_CONFIG);
    if (configSize == sizeof(PumpConfig)) {
        if (g_prefs.getBytes(NVS_KEY_CONFIG, &g_config, sizeof(PumpConfig)) == sizeof(PumpConfig)) {
            uint32_t crc = calculateCRC32((uint8_t*)&g_config, sizeof(PumpConfig) - sizeof(uint32_t));
            if (crc == g_config.crc32) {
                Serial.println("Loaded valid config from NVS.");
                if (g_config.mode > 5) g_config.mode = 0; 
                return;
            }
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
    
    for (int i = 0; i < NUM_POLY_COEFFS; i++) g_config.polyCoeffs[i] = 0.0;
    
    g_config.num_segments = 0;
    for (int i = 0; i < MAX_SEGMENTS; i++) {
        g_config.time_points[i] = 0.0f;
        g_config.flow_points[i] = 0.0f;
    }

    g_config.pumpSlope     = 0.0280188148f; 
    g_config.pumpIntercept = 1.7601988934f;

    g_config.pid_kp = 0.5f;
    g_config.pid_ki = 0.05f;
    g_config.pid_kd = 0.001f;

    g_configDirty = true;
}

void saveConfig() {
    g_config.crc32 = calculateCRC32((uint8_t*)&g_config, sizeof(PumpConfig) - sizeof(uint32_t));
    if (g_prefs.putBytes(NVS_KEY_CONFIG, &g_config, sizeof(PumpConfig))) {
        Serial.println("Config saved to NVS.");
    }
    g_configDirty = false;
}

// ───────────────────── NETWORKING FUNCTIONS (Core 1) ────────────────────
void buildDataJson(bool includeArrays) {
    float vol;
    int pwm_duty;
    taskDISABLE_INTERRUPTS();
    vol = g_cumulativeVolumeMl;
    pwm_duty = g_actualPwmDuty;
    taskENABLE_INTERRUPTS();

    float t_rel = g_current_t_min - g_config.init_t_min;
    if (t_rel < 0.0f) t_rel = 0.0f;
    float v_target = calculateTargetVolume(t_rel);

    // Optimized JSON building to prevent WDT timeout
    char jsonBuffer[1024]; // Reduced buffer size as we removed massive arrays

    snprintf(jsonBuffer, sizeof(jsonBuffer), 
        "{\"mode\":%d,\"pwm\":%d,\"speed\":%.1f,\"flow_rate_mlmin\":%.3f,"
        "\"cum_volume_ml\":%.3f,\"v_target_ml\":%.3f,\"active\":%s,\"waiting\":%s,"
        "\"current_t_min\":%.3f,\"init_t_min\":%.3f,\"final_t_min\":%.3f",
        g_config.mode,
        pwm_duty,
        (double)g_cmdSpeed,
        (double)g_currentFlowRateMlMin,
        (double)vol,
        (double)v_target,
        (g_opState == OP_RUNNING) ? "true" : "false",
        (g_opState == OP_WAITING) ? "true" : "false",
        (double)g_current_t_min,
        (double)g_config.init_t_min,
        (double)g_config.final_t_min
    );

    String json = String(jsonBuffer);

    // Only include arrays if explicitly requested (fixes WDT crash)
    if (includeArrays) {
        char tempBuffer[64];
        if (g_config.mode == 4) {
            json += ",\"poly\":[";
            for (int i = 0; i < NUM_POLY_COEFFS; i++) { 
                snprintf(tempBuffer, sizeof(tempBuffer), "%.12f", g_config.polyCoeffs[i]);
                json += tempBuffer;
                if (i < (NUM_POLY_COEFFS - 1)) json += ",";
            }
            json += "]";
        }

        if (g_config.mode == 5 && g_config.num_segments > 0) {
            snprintf(tempBuffer, sizeof(tempBuffer), ",\"num_segments\":%d", g_config.num_segments);
            json += tempBuffer;

            json += ",\"time_points\":[";
            for (int i = 0; i < g_config.num_segments; i++) {
                snprintf(tempBuffer, sizeof(tempBuffer), "%.3f", g_config.time_points[i]);
                json += tempBuffer;
                if (i < g_config.num_segments - 1) json += ",";
            }
            json += "]";

            json += ",\"flow_points\":[";
            for (int i = 0; i < g_config.num_segments; i++) {
                snprintf(tempBuffer, sizeof(tempBuffer), "%.3f", g_config.flow_points[i]);
                json += tempBuffer;
                if (i < g_config.num_segments - 1) json += ",";
            }
            json += "]";
        }
    }

    json += "}";
    g_lastDataJson = json;
}

void handleReadData() {
    server.send(200, "application/json", g_lastDataJson);
}

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

void sendDataToHub() {
    float vol;
    int pwm_duty;
    taskDISABLE_INTERRUPTS();
    vol = g_cumulativeVolumeMl;
    pwm_duty = g_actualPwmDuty;
    taskENABLE_INTERRUPTS();

    float t_rel = g_current_t_min - g_config.init_t_min;
    if (t_rel < 0.0f) t_rel = 0.0f;
    float v_target = calculateTargetVolume(t_rel);

    String url = sensorHubDataURL;
    url += "?mode=" + String(g_config.mode);
    url += "&pwm=" + String(pwm_duty);
    url += "&speed=" + String(g_cmdSpeed, 1);
    url += "&flow=" + String(g_currentFlowRateMlMin, 3);
    url += "&vol=" + String(vol, 3);
    url += "&v_tgt=" + String(v_target, 3);
    url += "&active=" + String((g_opState == OP_RUNNING) ? 1 : 0);
    url += "&waiting=" + String((g_opState == OP_WAITING) ? 1 : 0);
    // The acknowledgement channel. Sent every push, not only after a command: the hub
    // clears its mailbox on a matching id, and a push that omitted it would leave a
    // command retained forever if the one push right after applying it were lost.
    url += "&ack_cmd_id=" + String((unsigned long)g_lastAppliedHubCommandId);

    int code;
    String body;
    if (!httpGet(url, code, body)) {
        // Serial.printf("Hub data send FAILED, code %d\n", code);
    }
}

void pollHubForCommands() {
    int code;
    String body;
    if (!httpGet(sensorHubCommandURL, code, body)) return;
    if (body.length() == 0 || body == "{}") return;

    // A hub command carries cmd_id. A local one (serial, or the node's own web UI)
    // does not, and is always applied - the operator standing at the bench outranks
    // a retry that is only still in flight because the link is slow.
    float idVal = getJsonFloatValue(body, "cmd_id");
    if (!isnan(idVal) && idVal > 0.0f) {
        uint32_t hubCommandId = (uint32_t)idVal;
        if (hubCommandId == g_lastAppliedHubCommandId) {
            // Already applied. Acknowledged again on the next push; not re-applied.
            return;
        }
        processJsonCommand(body);
        g_lastAppliedHubCommandId = hubCommandId;
        Serial.printf("[HubCmd] Applied cmd_id=%lu\n", (unsigned long)hubCommandId);
        return;
    }

    processJsonCommand(body);
}

void checkWifi() {
    unsigned long now = millis();
    if (now < g_wifiNextActionMs) return;

    if (WiFi.status() == WL_CONNECTED) {
        g_wifiState = WF_IDLE;
        g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
        return;
    }

    switch(g_wifiState) {
        case WF_IDLE:
            Serial.println("[NET] WiFi disconnected. Starting async scan...");
            WiFi.scanNetworks(true, true);
            g_wifiState = WF_SCANNING;
            g_wifiNextActionMs = now + 100;
            break;
        case WF_SCANNING: {
            int n = WiFi.scanComplete();
            if (n == -1) {
                g_wifiNextActionMs = now + 100;
            } else if (n > 0) {
                String ssidToTry = "";
                for (int i = 0; i < n; i++) {
                    String ssid = WiFi.SSID(i);
                    if (ssid == HUB_SSID_A || ssid == HUB_SSID_B) {
                        ssidToTry = ssid;
                        g_lastKnownSsid = ssid;
                        break;
                    }
                }
                if (ssidToTry != "") {
                    Serial.printf("[NET] Hub found: %s. Connecting...\n", ssidToTry.c_str());
                    WiFi.disconnect();
                    delay(100);
                    WiFi.begin(ssidToTry.c_str(), ssidToTry.c_str());
                    g_wifiState = WF_CONNECTING;
                    g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
                } else {
                    g_wifiState = WF_IDLE;
                    g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
                }
                WiFi.scanDelete();
            } else {
                g_wifiState = WF_IDLE;
                g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
            }
            break;
        }
        case WF_CONNECTING:
            Serial.println("[NET] Connect attempt timed out.");
            g_wifiState = WF_IDLE;
            g_wifiNextActionMs = now;
            break;
    }
}

bool httpGet(const String& url, int& code, String& body) {
    http.begin(url);
    http.setReuse(false);
    http.setTimeout(1000);
    code = http.GET();
    if (code > 0) body = http.getString();
    else body = String("err=") + code;
    http.end();
    return code >= 200 && code < 300;
}

// ───────────────────── CUSTOM JSON PARSERS ────────────────────────────
long getJsonValue(String json, String key) {
    String searchKey = "\"" + key + "\":";
    int keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) {
        searchKey = "\"" + key + "\" :";
        keyIndex = json.indexOf(searchKey);
        if (keyIndex == -1) return -999999;
    }
    int valueIndex = keyIndex + searchKey.length();
    while(valueIndex < json.length() && isspace(json.charAt(valueIndex))) valueIndex++;
    if (json.charAt(valueIndex) == '\"') return -999999;
    int endIndex = json.indexOf(',', valueIndex);
    if (endIndex == -1) endIndex = json.indexOf('}', valueIndex);
    if (endIndex == -1) return -999999;
    String valueStr = json.substring(valueIndex, endIndex);
    valueStr.trim();
    if (valueStr.length() == 0 || (!isDigit(valueStr.charAt(0)) && valueStr.charAt(0) != '-' && valueStr.charAt(0) != '.')) {
        return -999999;
    }
    return atol(valueStr.c_str());
}

float getJsonFloatValue(String json, String key) {
    String searchKey = "\"" + key + "\":";
    int keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) {
        searchKey = "\"" + key + "\" :";
        keyIndex = json.indexOf(searchKey);
        if (keyIndex == -1) return NAN;
    }
    int valueIndex = keyIndex + searchKey.length();
    while(valueIndex < json.length() && isspace(json.charAt(valueIndex))) valueIndex++;
    if (json.charAt(valueIndex) == '\"') return NAN;
    int endIndex = json.indexOf(',', valueIndex);
    if (endIndex == -1) endIndex = json.indexOf('}', valueIndex);
    if (endIndex == -1) return NAN;
    String valueStr = json.substring(valueIndex, endIndex);
    valueStr.trim();
    if (valueStr.length() == 0 || (!isDigit(valueStr.charAt(0)) && valueStr.charAt(0) != '-' && valueStr.charAt(0) != '.')) {
        return NAN;
    }
    return valueStr.toFloat();
}

String getJsonStringValue(String json, String key) {
    String searchKey = "\"" + key + "\":\"";
    int keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) {
        searchKey = "\"" + key + "\" : \"";
        keyIndex = json.indexOf(searchKey);
        if (keyIndex == -1) return "";
    }
    int valueIndex = keyIndex + searchKey.length();
    int endIndex = json.indexOf('\"', valueIndex);
    if (endIndex == -1) return "";
    return json.substring(valueIndex, endIndex);
}

double getJsonDoubleValue(String json, String key) {
    String searchKey = "\"" + key + "\":";
    int keyIndex = json.indexOf(searchKey);
    if (keyIndex == -1) {
        searchKey = "\"" + key + "\" :";
        keyIndex = json.indexOf(searchKey);
        if (keyIndex == -1) return NAN;
    }
    int valueIndex = keyIndex + searchKey.length();
    while (valueIndex < json.length() && isspace(json.charAt(valueIndex))) valueIndex++;
    if (json.charAt(valueIndex) == '\"') return NAN;
    int endIndex = json.indexOf(',', valueIndex);
    if (endIndex == -1) endIndex = json.indexOf('}', valueIndex);
    if (endIndex == -1) return NAN;
    String valueStr = json.substring(valueIndex, endIndex);
    valueStr.trim();
    if (valueStr.length() == 0) return NAN;
    char *endp = nullptr;
    double val = strtod(valueStr.c_str(), &endp);
    if (endp == valueStr.c_str()) return NAN;
    return val;
}