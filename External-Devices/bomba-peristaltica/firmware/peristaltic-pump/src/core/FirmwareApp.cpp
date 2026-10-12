#include "FirmwareApp.h"

#include <Arduino.h>
#include <math.h>
#include <errno.h>
#include <Preferences.h>
#include <esp_task_wdt.h>
#include <WiFi.h>
#include <HTTPClient.h>
#include <WebServer.h>
#include <Update.h>
#include <esp_system.h>

#include "../protocol/JsonScan.h"
#include "../control/DoseMath.h"
#include "../control/FrameValidate.h"

// Versao unica: banner, hello, /diag e pagina OTA. 3.13 acrescenta ao push o estado de
// acionamento, o sensor de liquido, os reinicios, a dosagem e as recusas (rej_cmd_id/rej_err).
#define PUMP_FW_VERSION "3.13"
#define PUMP_FW_BUILD "3.13"

static const bool     DEBUG_ENABLE      = true;
static const uint32_t DEBUG_INTERVAL_MS = 1000;

#define R_EN_PIN 25
#define L_EN_PIN 26
#define R_PWM_PIN 14
#define L_PWM_PIN 27

#define POT_INT_PIN 34
#define POT_GAIN_PIN 35
#define SENSOR_PIN 15
#define SENSOR_ENABLE_BUTTON_PIN 32
#define SENSOR_STATUS_LED_PIN 33

// Hub STA
static const char* HUB_SSID_A = "ModuloTECNAL_1";
static const char* HUB_SSID_B = "ModuloTECNAL_2";
String sensorHubDataURL = "http://192.168.4.1/pumpData";
String sensorHubCommandURL = "http://192.168.4.1/pumpCommand";
String sensorHubHelloURL = "http://192.168.4.1/nodeHello";
bool g_hubAnnounced = false;

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
uint8_t g_hubFailStreak = 0;
constexpr unsigned long MAX_HUB_BACKOFF_MS = 15000;

uint32_t g_lastAppliedHubCommandId = 0;

// 3.13: quadro recusado nao recebe ACK. O id fica guardado para nao reprocessar reentregas e
// e publicado (rej_cmd_id) com o motivo (rej_err) ate o proximo quadro aceito. Comando local
// recusado publica rej_cmd_id=0.
uint32_t g_lastRejectedHubCommandId = 0;
char g_lastRejectCode[20] = "";

enum class CommandSource { Usb, LocalHttp, Hub };
// "speed" vindo do Hub sem speed_ms ganha este prazo: uma queda do PC ou do Hub com a
// mangueira enchendo nao deixa o motor girando para sempre. USB e HTTP local nao.
const unsigned long HUB_SPEED_DEFAULT_MS = 300000;

// Quem comanda o motor, publicado como drv.
enum DriveState : int {
    DRV_STOPPED = 0, DRV_POTS = 1, DRV_MANUAL_SPEED = 2, DRV_PROFILE_WAITING = 3,
    DRV_PROFILE_RUNNING = 4, DRV_DOSE_IDLE = 5, DRV_DOSE_PULSE = 6, DRV_OTA = 7
};
volatile int g_drive = DRV_STOPPED;
// Motor liberado pelo sensor de liquido (ou sensor desligado), do ultimo ciclo do loop.
volatile bool g_motorAllowed = true;

// Reinicios visiveis ao Hub e ao app.
uint32_t g_bootCount = 0;
int g_resetReason = 0;
constexpr char* NVS_KEY_BOOT_COUNT = (char*)"boot_n";

// Pushes cortados pelo buffer (nunca deveria passar de zero).
uint32_t g_pushTruncations = 0;

// OTA State
volatile bool g_otaInProgress = false;
unsigned long g_otaLastChunkMs = 0;
const unsigned long OTA_STALL_TIMEOUT_MS = 90000;
unsigned long g_otaRebootAtMs = 0;
String g_otaRejectReason = "";


const float     V_MAX                  = 1000.0f;
const float     ENABLE_EPS             = 1.0f;
const uint32_t  MIN_MOTOR_ON_TIME_MS   = 500;
const uint32_t  TASK_DELAY_MS          = 2;
const uint32_t  SENSOR_DEBOUNCE_MS     = 50;
const float     ADC_LPF_ALPHA          = 0.10f;

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

Preferences g_prefs;
constexpr char* NVS_NAMESPACE  = (char*)"feed_pump";
constexpr char* NVS_KEY_CONFIG = (char*)"config";

// Keys for State Persistence
constexpr char* NVS_KEY_STATE_ACTIVE = (char*)"s_active";
constexpr char* NVS_KEY_STATE_VOL    = (char*)"s_vol";
constexpr char* NVS_KEY_STATE_TIME   = (char*)"s_time";
constexpr char* NVS_KEY_STATE_MODE   = (char*)"s_mode";
constexpr char* NVS_KEY_STATE_CVOL   = (char*)"s_cvol";
// 3.12.1: contador de sessao gravado sempre (nao so com perfil rodando), para que um
// reinicio nao o zere. So reset_volume zera.
constexpr char* NVS_KEY_SESSION_VOL  = (char*)"s_sess";

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

    // PID
    float pid_kp;
    float pid_ki;
    float pid_kd;

    uint32_t crc32;
};

#ifndef PUMP_DUAL_RANGE_CAL_DEFINED
#define PUMP_DUAL_RANGE_CAL_DEFINED
struct PumpDualRangeCal {
    uint32_t magic;
    float a1, b1, k1, f1, c1;
    float k2, f2, c2;
    float s_t;
    uint32_t crc32;
};
#endif

PumpConfig g_config;
bool g_configDirty = false;
PumpDualRangeCal g_pumpCal;
bool g_pumpCalibrationReady = false;

TaskHandle_t pwmTaskHandle = nullptr;

// Core1 → Core0
volatile float g_cumulativeVolumeMl = 0.0f;
// O contador e lido e escrito nos dois cores (integracao no core 0, dosagem e comandos no
// core 1). Desligar interrupcoes so protegia o core atual; o spinlock protege os dois.
portMUX_TYPE g_volMux = portMUX_INITIALIZER_UNLOCKED;

inline float volRead() {
    portENTER_CRITICAL(&g_volMux);
    const float v = g_cumulativeVolumeMl;
    portEXIT_CRITICAL(&g_volMux);
    return v;
}

inline void volAdd(float ml) {
    portENTER_CRITICAL(&g_volMux);
    g_cumulativeVolumeMl += ml;
    portEXIT_CRITICAL(&g_volMux);
}

inline void volSet(float ml) {
    portENTER_CRITICAL(&g_volMux);
    g_cumulativeVolumeMl = ml;
    portEXIT_CRITICAL(&g_volMux);
}
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

// 3.10: the cumulative volume is a session counter that only reset_volume clears. Each
// profile cycle remembers where the counter stood when it started, and the volume
// controller closes on (cumulative - cycleStart), so stopping, restarting or changing a
// profile no longer throws the operator's running total away.
float g_cycleStartVolumeMl = 0.0f;

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
// 3.10: optional deadline for a "speed" command (speed_ms). 0 = no deadline. When it
// expires the motor is told 0; the potentiometers stay locked out until "pot":1.
unsigned long  g_usbSpeedUntilMs = 0;

// Dosagem por pulsos. A vazao nessa faixa nao passa pela calibracao polinomial: o operador
// mede mL/min na velocidade fixa e o no liga o motor por volume / vazao (+ atraso de partida)
// a cada periodo. Regras em DoseMath.h; estado na NVS em chaves proprias (d_*).
DoseConfig g_dose = {0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f};
volatile bool g_doseActive = false;
bool g_dosePulseOn = false;
unsigned long g_dosePulseStartMs = 0;
unsigned long g_dosePulseOnMs = 0;
unsigned long g_dosePulseRunMs = 0;
unsigned long g_doseNextPulseMs = 0;
unsigned long g_doseLastTickMs = 0;
float g_doseDeliveredMl = 0.0f;
uint32_t g_dosePulses = 0;
// Fim do pulso aplicado pela tarefa do motor (core 0), que nao depende do loop: com o Hub
// mudo o HTTP bloqueia o loop por ate ~1 s e o pulso passaria do volume. 0 = sem prazo.
volatile uint32_t g_doseCutoffMs = 0;
// 3.13: o prazo e armado no core 0 no instante em que o duty sobe (g_doseArmOnMs > 0 =
// pulso pedido e ainda nao iniciado). Em pulsos de 200 ms os ms entre o loop e o core 0 pesam.
volatile uint32_t g_doseArmOnMs = 0;
portMUX_TYPE g_doseMux = portMUX_INITIALIZER_UNLOCKED;
// Volume que deixou de sair porque o sensor de liquido segurou o motor durante o pulso.
float g_doseShortMl = 0.0f;

// Sensor gate
volatile bool  sensorEnable          = false;
volatile bool  sensorBypass          = false;
volatile bool  sensorButtonOverride = true;
bool           sensorWetState        = false;

void pwmTask(void* pv);

// Setup
void setupADC();
void setupSensorPin();
void loadConfig();
void saveConfig();
void loadPumpCalibration();
void savePumpCalibration();
uint32_t calculateCRC32(const uint8_t *data, size_t length);

// Persistence
void checkAndRecoverState();
void saveRuntimeState();
void clearRuntimeState();
void sessionVolumeRestore();
void sessionVolumeSave(bool force);

// Main Loop helpers
void handleSerialInput();
bool processJsonCommand(String json, CommandSource src = CommandSource::Usb);
void handleHubCommandBody(const String& body);
void checkWifi();
void pollHubForCommands();
float calcPotSpeed();
void updateSensorGate();
void updateOperationState();
void runOperationLogic();
void buildDataJson(bool includeArrays = false); // Modified prototype

// Dosagem por pulsos
void doseHandleCommand(const char* json);
void doseCancel(const char* reason);
void doseRestore();
void doseService(unsigned long now);
float doseDeliveredNow();

// Mode & PID
void resetOperationState();
void resetControllerState();
void startCycle();
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
void sendHubHello();
void handleReadData();
void handleDiag();
void handleCommand();
void handleNotFound();
void handleOtaPage();
void handleOtaUploadDone();
void handleOtaChunk();

// JSON helpers (os de C puro estao em protocol/JsonScan.h)
String getJsonStringValue(const char* json, const char* key);

inline long   getJsonValue(const String& json, const String& key) { return getJsonValue(json.c_str(), key.c_str()); }
inline float  getJsonFloatValue(const String& json, const String& key) { return getJsonFloatValue(json.c_str(), key.c_str()); }
inline double getJsonDoubleValue(const String& json, const String& key) { return getJsonDoubleValue(json.c_str(), key.c_str()); }
inline String getJsonStringValue(const String& json, const String& key) { return getJsonStringValue(json.c_str(), key.c_str()); }


#include "../hardware/PwmRuntime.h"
#include "../storage/CalibrationStore.h"
#include "../core/Lifecycle.h"
#include "../storage/RuntimeStateStore.h"
#include "../control/OperationController.h"
#include "../control/DoseController.h"
#include "../control/SensorAndConversion.h"
#include "../storage/ConfigStore.h"
#include "../protocol/TelemetryCodec.h"
#include "../network/HubClient.h"
