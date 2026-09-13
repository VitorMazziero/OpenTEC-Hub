#include "FirmwareApp.h"

#include <Arduino.h>
#include <Wire.h>
#include <Preferences.h>      // For ESP32 Non-Volatile Storage (NVS)
#include <esp_task_wdt.h>     // For Watchdog Timer
#include <algorithm>          // For std::sort (median filter)
#include <cstring>            // For memcpy (median filter)

#include <WiFi.h>
#include <HTTPClient.h>
#include <WebServer.h>
#include <Update.h>

#include "web_ui.h"           // PROGMEM single-page UI

// OTA State
volatile bool g_otaInProgress = false;
unsigned long g_otaLastChunkMs = 0;
const unsigned long OTA_STALL_TIMEOUT_MS = 90000;
unsigned long g_otaRebootAtMs = 0;
String g_otaRejectReason = "";

// Firmware Identity
static const char* FW_VERSION = "v11.1";
static const char* FW_NAME    = "biomass_sensor_analog_v04_direct";

// Sensor Configuration

constexpr int LED_PWM_PIN   = 18;     // GPIO pin for LED PWM control
constexpr int LEDC_FREQ_HZ  = 2000;   // PWM frequency in Hz
constexpr int LEDC_RES_BITS = 8;      // PWM resolution (8-bit = 0-255)

bool     g_ledTestEnable   = false;
float    g_ledTestPct      = 0.0f;
int      g_ledTestDir      = 1;
uint32_t g_ledTestPeriodMs = 50;
uint32_t g_ledTestNextMs   = 0;

constexpr int I2C_SDA_PIN = 8;
constexpr int I2C_SCL_PIN = 9;

constexpr float LED_DUTY_LIMIT = 0.08f;

constexpr uint32_t DEFAULT_REFRESH_MS = 25000;

constexpr uint32_t LED_SETTLE_MS         = 10;
constexpr uint32_t INTEGRATION_MARGIN_MS = 8;

constexpr float    IT_PERIOD_GUARD  = 1.20f;
constexpr uint32_t BOUNDARY_POLL_MS = 5;      // data-register poll interval
constexpr uint16_t BOUNDARY_DELTA   = 32;     // counts that count as "not dark"

inline uint32_t integrationGuardMs(uint32_t itMs) {
  return (uint32_t)(IT_PERIOD_GUARD * (float)itMs) + INTEGRATION_MARGIN_MS;
}

inline uint32_t ledOnMsFor(uint32_t itMs) {
  return LED_SETTLE_MS + 2 * integrationGuardMs(itMs);
}

constexpr uint8_t  VEML7700_ADDR   = 0x10;
constexpr uint8_t  REG_ALS_CONF    = 0x00;
constexpr uint8_t  REG_ALS_DATA_L  = 0x04;   // ALS result, read as one 16-bit word
// 0x05 is the WHITE channel, NOT the high byte of ALS -- vemlRead16(0x04)
// already returns both bytes. Named for what it is so nobody "completes" a
// 16-bit read by reading 0x05 next.
constexpr uint8_t  REG_WHITE_DATA  = 0x05;
constexpr uint16_t VEML_GAIN_2     = (0b01 << 11); // Fix gain at 2x

constexpr int IT_CHOICE_COUNT = 6;
constexpr uint16_t IT_BITS[IT_CHOICE_COUNT] = {
    0b1100, 0b1000, 0b0000, 0b0001, 0b0010, 0b0011};
constexpr uint32_t IT_MS[IT_CHOICE_COUNT] = {25, 50, 100, 200, 400, 800};

uint16_t itRegisterFor(uint32_t ms) {
  for (int i = 0; i < IT_CHOICE_COUNT; i++) {
    if (IT_MS[i] == ms) return (uint16_t)(IT_BITS[i] << 6);
  }
  return 0xFFFF;
}
constexpr uint16_t VEML_ALS_ENABLE   = 0x0000;     // Bit 0 = 0 to enable
// ALS_SD (bit 0) is register-only -- there is no shutdown pin on this part.
// Kept documented because toggling it to restart integration was tried and
// rejected; see takePulsedReading().
constexpr uint16_t SATURATION_RAW  = 65530;        // Sensor saturation threshold

constexpr uint16_t MIN_VALID_BLANK = 500;

// Networking Configuration
static const char* HUB_SSID_A = "ModuloTECNAL_1";
static const char* HUB_SSID_B = "ModuloTECNAL_2";
String sensorHubDataURL    = "http://192.168.4.1/biomassData";
String sensorHubCommandURL = "http://192.168.4.1/biomassCommand";
String sensorHubHelloURL   = "http://192.168.4.1/nodeHello";
bool g_hubAnnounced = false;

static const char* AP_SSID = "BiomassSensor";
IPAddress apIP(192, 168, 7, 1);
IPAddress apGateway(192, 168, 7, 1);
IPAddress apSubnet(255, 255, 255, 0);

WebServer server(80); // Web server for our own AP
HTTPClient http;      // Client for communicating with the hub

String g_lastDataJson  = "{}"; // Latest sample as JSON (serial + /readData)
String g_lastKnownSsid = "";
unsigned long lastWifiCheckMs = 0;
unsigned long WIFI_RECONNECT_PERIOD_MS = 10000; // Check every 10 seconds
unsigned long lastHubPollMs = 0;
unsigned long HUB_POLL_PERIOD_MS = 2000; // Poll hub every 2 seconds
uint8_t g_hubFailStreak = 0;
constexpr unsigned long MAX_HUB_BACKOFF_MS = 15000;

// The hub re-delivers a command until this id comes back on a data push, so the same
// JSON arrives repeatedly by design. 0 means "nothing applied yet"; the hub never
// issues id 0.
uint32_t g_lastAppliedHubCmdId = 0;

// Half the hub's 10 s biomass window, so a single lost push never reads as absent.
unsigned long lastHubHeartbeatMs = 0;
const unsigned long HUB_HEARTBEAT_PERIOD_MS = 5000;
enum WifiReconnectState { WF_IDLE, WF_SCANNING, WF_CONNECTING };
WifiReconnectState g_wifiState = WF_IDLE;
unsigned long g_wifiNextActionMs = 0;

bool g_hubEnabled = true;

// Global Definitions

constexpr int WDT_TIMEOUT_S = 10; // 10-second watchdog

enum SystemState {
  IDLE,
  BLANKING,
  MEASURING,
  SEARCHING // State for finding the best IT/PWM gear
};
SystemState g_state = IDLE;

volatile bool g_abortRequested = false;

bool g_serverStarted  = false;
bool g_wdtReady       = false;
bool g_inHttpHandler  = false;

String g_pendingJson = "";

struct DeviceConfig {
  uint16_t LOW_THRESHOLD_RAW;
  uint16_t HIGH_THRESHOLD_RAW;
  uint16_t OPTIMAL_TARGET_RAW;

  static const int IT_COUNT = 4;
  uint16_t itSettings[IT_COUNT];      // VEML7700 register values for IT
  uint32_t itDelays[IT_COUNT];        // Wait time in ms for each IT
  uint32_t itRefreshTimes[IT_COUNT];  // Poll interval for each IT

  static const int PWM_COUNT = 8;
  float pwmSettings[PWM_COUNT];       // % duty cycle levels

  uint32_t crc32; // Checksum for config integrity
};
DeviceConfig g_config; // The "live" global config

Preferences g_prefs;
constexpr const char* NVS_NAMESPACE  = "biomass_sensor";
constexpr const char* NVS_KEY_CONFIG = "config";
constexpr const char* NVS_KEY_BLANK  = "blanking";
constexpr const char* NVS_KEY_HUB_EN = "hub_en";   // NEW (kept out of DeviceConfig on purpose)
constexpr const char* NVS_KEY_BOOTID = "boot_id";  // NEW
constexpr const char* NVS_KEY_EMA    = "ema";      // NEW (v4.1)
constexpr const char* NVS_KEY_AUTO   = "autorange";// NEW (v4.1)
constexpr const char* NVS_KEY_BLANKFW = "blank_fw";// NEW (v4.6)

constexpr uint32_t BLANK_EPOCH = 3;

struct BlankingData {
  uint16_t blankValues[DeviceConfig::IT_COUNT][DeviceConfig::PWM_COUNT];
  uint32_t timestamp;
  uint32_t crc32;
};
BlankingData g_blankingData; // The "live" global blanking data

constexpr int FILTER_WINDOW_SIZE = 5;
uint16_t g_readingWindow[FILTER_WINDOW_SIZE] = {0};
int      g_readingWindowIndex                = 0;
bool     g_filterIsPrimed                    = false;

float    g_emaFilteredRaw    = 0.0f;
bool     g_emaFilterIsPrimed = false;

volatile float g_targetPct = 0.0f; // Last commanded duty cycle %
uint16_t g_lastAlsRaw      = 0;    // The *doubly filtered* sensor reading
float    g_lastAbsorbance  = 0.0f;
int      g_currentItIndex  = 0;
int      g_currentPwmIndex = 0;
bool     g_blankIsDone     = false;
uint32_t g_nextReadTime    = 0;

uint32_t g_ledOffSinceMs   = 0;
// Reads that could not see a conversion boundary and fell back to the blind
// wait. Should stay at zero; a rising count means the signal is near
// BOUNDARY_DELTA, i.e. the gear is too dim to be trusted.
uint32_t g_boundaryMisses  = 0;

uint32_t g_seq        = 0;  // Increments once per published sample
uint32_t g_bootId     = 0;  // Persisted in NVS, incremented each boot
uint32_t g_lastSampleMs = 0;
uint16_t g_lastI0     = 0;  // The blank actually used for g_lastAbsorbance
bool     g_lastSat    = false;

int g_consecutiveLowReadings  = 0;
int g_consecutiveHighReadings = 0;

int  g_consecutiveGearSearches = 0; // Counts failed searches
bool g_highDensityMode         = false;

bool  g_autoRange    = true;
bool  g_manualLedOn  = false; // LED held on by the operator while IDLE
float g_manualLedPct = 0.0f;  // duty it is held at
float g_emaAlpha     = 0.8f;  // EMA low-pass coefficient (0 < a <= 1)
bool  g_lastSingle   = false; // last published sample was a single shot

uint32_t g_saturationCount = 0;
uint32_t g_i2cErrorCount   = 0;
uint32_t g_sensorResets    = 0;

// 1024 samples x 20 bytes = 20 KB of the S3's 512 KB SRAM.
// At the 5 s refresh interval that is ~85 minutes of backfill, which
// comfortably covers a laptop sleeping or a WiFi drop mid-cultivation.
constexpr int HISTORY_SIZE = 1024;
constexpr int HISTORY_MAX_RESPONSE = 60; // Records per /api/history call

constexpr int ABSORBANCE_DECIMALS = 4;

struct Sample {
  uint32_t seq;
  uint32_t t_ms;
  float    absorbance;
  uint16_t raw;
  uint16_t i0;
  uint8_t  itIdx;
  uint8_t  pwmIdx;
  uint8_t  flags;   // bit0 = HD mode, bit1 = saturated
  uint8_t  _pad;
};
constexpr uint8_t SF_HD_MODE   = 0x01;
constexpr uint8_t SF_SATURATED = 0x02;
//: single-shot reads bypass the median+EMA chain, so they are noisier than
//: the surrounding kinetics. Flagged so analysis can exclude them rather
//: than silently averaging them in with filtered data.
constexpr uint8_t SF_SINGLE    = 0x04;
constexpr uint8_t SF_MANUAL    = 0x08; // taken with auto-ranging disabled

Sample g_history[HISTORY_SIZE];
int      g_historyHead  = 0; // Next write position
uint32_t g_historyCount = 0; // Total samples ever stored (>= HISTORY_SIZE means wrapped)

// Reported with the table so a run's metadata says whether its I0 came from
// the fast back-to-back sweep or a paced one. -1 means the table was loaded
// from NVS and this session did not sweep it, so nothing is known.
float    g_blankSweepDutyPct = -1.0f;  // 0 = unpaced, >0 = paced at that duty
uint32_t g_blankSweepMs      = 0;      // wall time the sweep took

// Forward Declarations
// The .ino auto-prototype pass is fragile once a local header is included,
// so everything used before its definition is declared explicitly.
void     saveConfig();
void     loadConfig();
bool     loadBlankingData();
void     saveBlankingData();
void     buildDataJson();
void     sendDataToHub();
void     sendHubHello();
void     pollHubForCommands();
void     checkWifi();
void     processJsonCommand(String json, bool allowBlocking = true);
void     runBlankingRoutine(float dutyPct = 0.0f);
void     blankCoolDown(uint32_t onMs, float dutyPct);
void     findAndSetOptimalGear();
void     runMeasurementLoop();
void     publishSample(bool single);
void     readOnce();
void     setAutoRange(bool enabled);
void     setManualGear(int itIndex, int pwmIndex);
void     invalidateBlank(const char* reason);
void     applyRecommendedPwmTable();
uint32_t minSafeRefreshMs();
void     enforceRefreshFloor(bool announce);
bool     blankIsValid(int itIndex, int pwmIndex);
bool     findBrightestValidGear(int &itIndex, int &pwmIndex);
void     findOptimalBlankGear(int &bestItIndex, int &bestPwmIndex);
const char* findJsonValueStart(const char* json, const char* key);
long        getJsonValue(const char* json, const char* key);
float       getJsonFloat(const char* json, const char* key, bool &found);
String      getJsonStringValue(const char* json, const char* key);
long        getJsonValue(const String& json, const String& key);
float       getJsonFloat(const String& json, const String& key, bool &found);
String      getJsonStringValue(const String& json, const String& key);
bool     vemlSetConfig(int itIndex);
bool     takePulsedReading(int itIndex, int pwmIndex, uint16_t &out,
                           bool holdLed = false);
uint32_t waitForConversionBoundary(uint16_t baseline, uint32_t timeoutMs,
                                   uint16_t &value);
void     probeConversionPeriod(int pwmIndex);
void     pwmSetDutyPercent(float percent);
void     pwmSetLevel(int pwmIndex);
void     handleI2CError();
bool     resetSensor();
void     resetReadingFilter();
uint16_t getFilteredReading(uint16_t newReading);
void     serviceNetwork();
void     handleSerialInput(bool allowBlocking = true);
uint16_t itRegisterFor(uint32_t ms);
void     delayServiced(uint32_t ms);
void     historyPush(const Sample& s);
void     historyClear();
String   buildStatusJson();
String   buildHistoryJson(uint32_t sinceSeq);
String   buildBlankJson();
void     setHubEnabled(bool enabled);
void     handleOtaPage();
void     handleOtaUploadDone();
void     handleOtaChunk();
void     handleDiag();

// CRC32 Helper
// NOTE: this is a byte sum, not a real CRC32. It is kept exactly as-is
// because changing it would invalidate every already-stored NVS blob.

#include "../storage/Crc.h"
#include "../core/ServiceRuntime.h"
#include "../filtering/SampleFilter.h"
#include "../sensor/Veml7700Driver.h"
#include "../storage/Stores.h"
#include "../history/SampleHistory.h"
#include "../measurement/BlankingAndRange.h"
#include "../measurement/MeasurementPipeline.h"
#include "../protocol/CommandCodec.h"
#include "../protocol/TelemetryAndHub.h"
#include "../api/LocalHttpApi.h"
#include "../core/Lifecycle.h"
