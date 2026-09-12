#include "FirmwareApp.h"

#include <Arduino.h>
#include <AsyncTCP.h>
#include <ESPAsyncWebServer.h>
#include <Adafruit_MCP4725.h>
#include <Adafruit_ADS1X15.h>
#include <WiFi.h>
#include <Wire.h>
#include <HTTPClient.h>
#include <nvs_flash.h>
#include <esp_random.h>   // esp_random() for the boot session id
#include <EEPROM.h>
#include <Update.h>      // OTA: writes the inactive app slot, see setupOTA()

#define FW_VERSION "V10"
#define FW_BUILD FW_VERSION " (" __DATE__ " " __TIME__ ")"

const char* ap_ssid = "Floxometro_AP";
const char* ap_password = NULL; // Rede aberta (sem senha)

String currentSSID = "";
String currentPassword = "";

bool reconnect_Wifi = true;
unsigned long lastReconnectAttempt = 0;
const unsigned long wifiReconnectInterval = 5000;
const unsigned long wifiConnectTimeout = 8000;
const uint8_t wifiRadioChannel = 6;
const char* knownHubSSIDs[] = {"ModuloTECNAL_1", "ModuloTECNAL_2"};
const uint8_t knownHubCount = sizeof(knownHubSSIDs) / sizeof(knownHubSSIDs[0]);
uint8_t nextHubIndex = 0;
// Consecutive association timeouts on the SSID currently being tried, and how many
// are tolerated before assuming this really is the wrong hub and switching.
uint8_t failedAttemptsOnCurrentSSID = 0;
const uint8_t attemptsBeforeTryingOtherHub = 3;
enum WifiReconnectState { WF_IDLE, WF_CONNECTING };
WifiReconnectState wifiReconnectState = WF_IDLE;

AsyncWebServer server(80);
AsyncWebSocket ws("/ws");

volatile bool otaInProgress = false;
volatile bool otaStalled = false;    // watchdog fired; next chunk logs the resume
unsigned long otaLastChunkMs = 0;
// Generous on purpose: block erases stall the Wi-Fi driver and the PC may roam
// off a no-internet AP for a while. TCP rides that out; the watchdog must too.
const unsigned long otaStallTimeoutMs = 90000;
// Set by the completion handler; loop() reboots once the HTTP response has left.
unsigned long otaRebootAtMs = 0;
String otaRejectReason = "";
size_t otaNextProgressLog = 0;     // serial progress marker, every 128 KB

const char otaPage[] PROGMEM = R"rawliteral(<!DOCTYPE html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Fluxometro OTA</title>
<style>body{font-family:sans-serif;max-width:520px;margin:2em auto;padding:0 1em}progress{width:100%}code{background:#eee;padding:0 .3em}</style>
</head><body><h2>Fluxometro &ndash; firmware update</h2>
<p>Running: <b>)rawliteral" FW_BUILD R"rawliteral(</b></p>
<p>Select the exported app image <code>flowmeter_*.ino.bin</code> (Arduino IDE: <i>Sketch &gt; Export Compiled Binary</i>,
folder <code>build/esp32.esp32.esp32/</code>). Do <b>not</b> send the <code>.merged.bin</code>, <code>.bootloader.bin</code> or <code>.partitions.bin</code>.</p>
<form id="f"><input type="file" name="firmware" accept=".bin" required> <input type="submit" value="Flash"></form>
<progress id="p" value="0" max="100" hidden></progress><p id="s"></p>
<script>
const f=document.getElementById('f'),p=document.getElementById('p'),s=document.getElementById('s');
f.onsubmit=e=>{e.preventDefault();const x=new XMLHttpRequest();x.open('POST','/update');
x.upload.onprogress=v=>{p.hidden=false;p.value=Math.round(100*v.loaded/v.total);s.textContent='Uploading '+p.value+'%'};
x.onload=()=>{s.textContent=x.responseText;if(x.status==200)setTimeout(()=>location.reload(),8000)};
x.onerror=()=>{s.textContent='Connection lost. If the upload had reached 100% the device is rebooting; reload in a few seconds.'};
x.send(new FormData(f))};
</script></body></html>)rawliteral";

Adafruit_ADS1115 ads;
Adafruit_MCP4725 mcp;
SemaphoreHandle_t commandMutex = NULL;
SemaphoreHandle_t i2cMutex = NULL;
// ESP-IDF's Wi-Fi stack must not be driven by two HTTPClient transactions at
// the same time. Command polling and telemetry share this bus mutex.
SemaphoreHandle_t hubHttpMutex = NULL;

#define VALVE_FLOW_PIN 5
#define VALVE1_PIN 17
#define VALVE2_PIN 16
#define RECEIVER_LED 19

uint8_t valveFlowState = 0;
uint8_t valve1State = 0;
uint8_t valve2State = 0;

enum CommandSource : uint8_t { COMMAND_DIRECT = 0, COMMAND_HUB = 1 };
uint32_t lastAppliedHubCommandId = 0;
uint32_t lastAppliedDirectSessionId = 0;
uint32_t lastAppliedDirectCommandId = 0;
unsigned long lastCommandApplyMs = 0;
String lastCommandSource = "boot";

// ADS1115 at 128 SPS (7.8 ms/conversion). 16 conversions span 125 ms, the same
// integration window one 8 SPS conversion had, so 50/60 Hz rejection is unchanged
// while averaging lowers the noise. The sample rate goes from 2.7 Hz to ~7 Hz.
#define ADC_SAMPLES_PER_CYCLE 16

// Global calibration parameters. The low range uses a quartic so it passes
// through the three measured points and matches curve 2 in value and slope.
float a1, b1, k1, f1, c1;
float k2, f2, c2;

float readFlowVoltage = 0.0;
float readFlowRate = 0.0;
float maxFlowRate = 50.0;

// Variáveis de Controle
float flowSetpoint = 0.0;
float targetFlowSetpoint = 0.0;   // setpoint real: what was asked for, the PI's target
float flowFeedforward = 0.0;
float ffGain = 0.85f;
float ffOffset = -0.05f;
// The reference the PI actually tracks. Slewed toward targetFlowSetpoint at
// rampRate L/min per second so a step never reaches the MFC as a step. Zero is
// never ramped; with dacHold the last value is kept through a zero setpoint.
float rampedTarget = 0.0f;
float rampRate = 3.0f;      // ramp_rate command, persisted. 0 disables the ramp.
// Omega FMA-5400 manual 5.5: keep the setpoint, toggle Valve Off. With dacHold a
// zero setpoint asserts Valve Off and leaves the DAC and PI state untouched, so
// the restart is the last operating point and not a step into a closed valve.
bool dacHold = true;        // dac_hold command, persisted.
float Kp_flow = 0.1;
float Ki_flow = 0.1;
float integralError = 0.0;
unsigned long lastControlUpdate = 0;
const unsigned long controlInterval = 100;
// Ki was tuned with the former 10 s cycle. Scale each integral increment so
// changing the calculation rate does not change integral strength.
const float integralIntervalScale = controlInterval / 10000.0f;
// One DAC LSB is maxFlowRate/4095 = 0.012, so anything above 0.005 is a real change.
const float dacUpdateThreshold = 0.005f;
// Low-pass on the ~140 ms sample stream: alpha=0.5 is a ~140 ms time constant.
const float flowFilterAlpha = 0.5f;
// Set with debug_pi=1: one Tgt/Act/Out/I line per control cycle for step tests.
bool debugPI = false;

bool ledBlinking = false;
unsigned long blinkStartTime = 0;
const unsigned long blinkInterval = 200;
uint8_t blinkCount = 0;
float lowPassPreviousFilteredValue = 0;

char outputMessage[512];   // V07: flow_output added, keep headroom
String sensorHubURL = "http://192.168.4.1";
unsigned long lastHTTPDataTime = 0;
unsigned long lastCommandPollTime = 0;
// 250 ms, not the former 100 ms. A gas setpoint changes on the scale of seconds,
// so polling at 10 Hz bought no response time and cost 10 TCP handshakes/s on a hub
// that also serves the PC, biomass, agitator, pump and servo node.
const unsigned long commandPollInterval = 250;
const unsigned long telemetryInterval = 500;
const uint16_t hubConnectTimeoutMs = 1000;
const uint16_t hubRequestTimeoutMs = 1500;

const uint16_t telemetryFailuresBeforeRelink = 10;

uint32_t bootSessionId = 0;

struct CalibrationParams {
  uint32_t magic;
  float a1, b1, k1, f1, c1, k2, f2, c2;
  float kp, ki;
  float ff_gain, ff_offset;   // V08 (schema v3), appended so v2 records migrate in place
  float ramp_rate, dac_hold;  // V10 (schema v5); dac_hold stored as 0/1 in a float
};
CalibrationParams calParams;
const uint32_t CALIBRATION_MAGIC_V2 = 0xCAFEBAC0;
const uint32_t CALIBRATION_MAGIC_V3 = 0xCAFEBAC1;
const uint32_t CALIBRATION_MAGIC_V4 = 0xCAFEBAC2;
const uint32_t CALIBRATION_MAGIC = 0xCAFEBAC3;
const float FF_GAIN_DEFAULT = 0.85f;
const float FF_OFFSET_DEFAULT = -0.05f;
const float KP_DEFAULT = 0.4f;
const float KI_DEFAULT = 2.0f;
const float RAMP_RATE_DEFAULT = 3.0f;
const float DAC_HOLD_DEFAULT = 1.0f;

const float FACTORY_A1 = -1353785.3f;
const float FACTORY_B1 = 246663.69f;
const float FACTORY_K1 = -16473.492f;
const float FACTORY_F1 = 484.99466f;
const float FACTORY_C1 = -4.6159464f;
const float FACTORY_K2 = -0.46260458f;
const float FACTORY_F2 = 10.797299f;
const float FACTORY_C2 = 0.28475793f;

static void applyFactoryCurve(CalibrationParams &cp) {
  cp.a1 = FACTORY_A1; cp.b1 = FACTORY_B1; cp.k1 = FACTORY_K1;
  cp.f1 = FACTORY_F1; cp.c1 = FACTORY_C1;
  cp.k2 = FACTORY_K2; cp.f2 = FACTORY_F2; cp.c2 = FACTORY_C2;
}

// Forward declarations
void readAndProcessADC();
float lowPassFilter(float newValue, float alpha);
void readSerialData();
bool writeFlowSetpointToDAC(float flowSetpoint);
void startLEDBlinking();
void updateLEDBlinking();
void loadParameters();
void saveParameters();
float feedforwardSetpoint(float target);
bool processReceivedData(const String &data, CommandSource source = COMMAND_DIRECT);
bool extractJsonUint32(const char *json, const char *key, uint32_t &value);
inline bool extractJsonUint32(const String &json, const char *key, uint32_t &value);
void onWsEvent(AsyncWebSocket *server, AsyncWebSocketClient *client, AwsEventType type, void *arg, uint8_t *data, size_t len);
void setupOTA();

// Tasks
void sensorTask(void *parameter);
void httpTask(void *parameter);
void telemetryTask(void *parameter);
void wifiTask(void *parameter);


#include "../core/Lifecycle.h"
#include "../api/WebSocketApi.h"
#include "../tasks/TaskRuntime.h"
#include "../api/OtaService.h"
#include "../protocol/CommandCodec.h"
#include "../hardware/FlowIo.h"
#include "../storage/CalibrationStore.h"
