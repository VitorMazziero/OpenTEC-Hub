/***********************************************************************
 * Flowmeter ESP32 - OpenTECHub V08
 *
 * Reliable command rules (unchanged from V05):
 * - Local Serial/WebSocket commands remain immediate; app commands ack/retry.
 * - Hub commands carry cmd_id and are idempotent.
 * - Repeated hub delivery is acknowledged but never applied twice.
 * - Telemetry reports the applied cmd_id and actual valve state.
 * - AP+STA stays on channel 6 and never performs disruptive broad scans.
 *
 * V06 - link reliability. The protocol did not change; transport and recovery did.
 * - HTTP keep-alive on both hub clients. V05 opened and tore down a TCP connection
 *   for every request: ~12 per second into the hub's lwIP, which is where socket
 *   exhaustion showed up as "lost" commands whenever RF degraded at all.
 * - Command polling drops from 10 Hz to 4 Hz. A gas setpoint moves on the scale of
 *   seconds, so the extra 6 Hz bought no response time and cost real airtime.
 * - Link watchdog: WL_CONNECTED is not proof the path works. Ten consecutive failed
 *   telemetry posts now tear the association down instead of counting forever.
 * - Reconnection no longer flips to the other hub on the first timeout, which used
 *   to send the next attempt to a module that is not even powered.
 * - Telemetry carries a per-power-on boot_id. The hub adopts the reported state when
 *   nothing is pending, so a silent reboot used to hand it a zero setpoint as if the
 *   operator had asked for it; the hub now re-asserts its own desired state instead.
 * - Telemetry also carries reconnect_wifi, and the hub can now set it. Switching it
 *   off used to be a one-way trip that only a cable could undo.
 * - OTA firmware update at http://192.168.10.1/update over the flowmeter's own AP.
 *   The case is sealed and this board needs BOOT held for a serial flash; from V06
 *   on, uploads go over Wi-Fi (see setupOTA()).
 *
 * V07 - control authority and measurement latency. Protocol unchanged; one field added.
 * - The integral term was clamped to +/-10 error-units, which with Ki=0.1 capped the
 *   whole correction at +/-1 flow unit. A target of 2.0 sat at 3.36 with the DAC
 *   pinned at 0.86 and nowhere lower to go. Anti-windup now bounds the *output* to
 *   0..maxFlowRate, so the loop has full authority against MFC gain error.
 * - Measurement lag was ~2 s: 8 SPS conversions (125 ms each, two per cycle), a 375 ms
 *   task period, alpha=0.25 low-pass and a 3-sample moving average. Now 128 SPS with
 *   16 conversions averaged (same 125 ms mains-rejection window), no moving average,
 *   alpha=0.5, one sample every ~140 ms. PI period 200 -> 100 ms; Ki keeps its meaning.
 * - sensorTask held the I2C mutex for the whole 250 ms burst while the DAC write gave
 *   up after 250 ms, so some writes were silently dropped after flowSetpoint had
 *   already been updated. Mutex is now per conversion and a failed write is retried.
 * - DAC update threshold 0.05 -> 0.005 (one LSB is 0.012), as the V05 comment intended.
 * - Actual controller output is reported as flow_output next to the target, on the
 *   WebSocket and in hub telemetry. The plateau above was invisible without it.
 * - debug_pi=1 prints a step-response line per control cycle for tuning.
 * - OTA stall watchdog only releases the hub link; it never aborts Update from
 *   loop(), which freed the write buffer under the AsyncTCP task (StoreProhibited).
 *
 * V08 - setpoint feedforward. Protocol unchanged; one telemetry field added.
 * - The MFC delivers more than it is asked for: 1 -> 1.4, 5 -> 6, 12 -> 14.8 L/min,
 *   and the PI then needed up to a minute to pull it back. Two setpoints now exist:
 *   the real one (targetFlowSetpoint, what the hub asked for, the PI's target) and
 *   the corrected one (flowFeedforward = ff_gain * real + ff_offset, what is sent to
 *   the MFC as the base). The PI only trims on top of the corrected value, so the
 *   first response lands near the target and the integral has little left to do.
 * - ff_gain/ff_offset default to the least-squares inverse of the three points above
 *   (0.8175, -0.05); settable by command and stored in EEPROM. The EEPROM schema is
 *   migrated in place: an old record keeps its calibration and gets the defaults.
 * - Telemetry and WebSocket carry flow_setpoint_corrected next to flow_setpoint and
 *   flow_output; the WebSocket JSON also reports ff_gain/ff_offset with Kp/Ki.
 ***********************************************************************/

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

#define FW_VERSION "V08"
#define FW_BUILD FW_VERSION " (" __DATE__ " " __TIME__ ")"

// ----- WiFi Credentials & Settings -----
const char* ap_ssid = "Floxometro_AP";
const char* ap_password = NULL; // Rede aberta (sem senha)

String currentSSID = "";
String currentPassword = "";

// New Control Variable for Reconnection
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

// ----- OTA firmware update -----
// GET /update serves a one-field form; POSTing the exported app image there
// (Arduino IDE: Sketch > Export Compiled Binary -> build/esp32.esp32.esp32/*.ino.bin)
// streams it into the inactive OTA slot and reboots into it. The boot pointer only
// moves after Update.end() verifies the image, so a bad or interrupted upload leaves
// the running firmware untouched. No login: the AP is open and so is this page.
// Hub polling, telemetry and reconnects stand down while a transfer is in flight so
// the Wi-Fi stack serves the upload alone. Cleared on completion, failure, or stall.
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
// Setpoint corrigido: the value actually handed to the MFC as the base of the PI
// output. The MFC over-delivers (1 -> 1.4, 5 -> 6, 12 -> 14.8), so the base is
// ff_gain * real + ff_offset, the least-squares inverse of those points. The PI
// only trims the residual on top of it.
float flowFeedforward = 0.0;
float ffGain = 0.85f;
float ffOffset = -0.05f;
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

// Link watchdog. WiFi.status() reporting WL_CONNECTED is not proof the path works:
// a lost DHCP lease, a full AP or a wedged socket all leave the station "connected"
// and mute. After this many consecutive failed telemetry posts (~5 s at 2 Hz) the
// association is torn down so wifiTask rebuilds it from scratch.
const uint16_t telemetryFailuresBeforeRelink = 10;

// Identifies this power-on to the hub. The hub adopts the flowmeter's reported state
// whenever no command is pending, so a silent reboot used to hand it a zero setpoint
// as if the operator had asked for it. A changed id tells the hub to re-assert its
// own desired state instead. Seeded in setup() from the hardware RNG.
uint32_t bootSessionId = 0;

struct CalibrationParams {
  uint32_t magic;
  float a1, b1, k1, f1, c1, k2, f2, c2;
  float kp, ki;
  float ff_gain, ff_offset;   // V08 (schema v3), appended so v2 records migrate in place
};
CalibrationParams calParams;
// Schema v2 added a1/b1 and reset everything. v3 appends the feedforward pair and
// migrates: a v2 record keeps its calibration and only receives the FF defaults.
const uint32_t CALIBRATION_MAGIC_V2 = 0xCAFEBAC0;
const uint32_t CALIBRATION_MAGIC = 0xCAFEBAC1;
const float FF_GAIN_DEFAULT = 0.8175f;
const float FF_OFFSET_DEFAULT = -0.05f;

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
bool processReceivedData(String data, CommandSource source = COMMAND_DIRECT);
bool extractJsonUint32(const String &json, const char *key, uint32_t &value);
void onWsEvent(AsyncWebSocket *server, AsyncWebSocketClient *client, AwsEventType type, void *arg, uint8_t *data, size_t len);
void setupOTA();

// Tasks
void sensorTask(void *parameter);
void httpTask(void *parameter);
void telemetryTask(void *parameter);
void wifiTask(void *parameter);

void setup() {
  Serial.begin(115200);
  // Espera um pouco para a serial estabilizar
  delay(1000);
  Serial.println("\n\n=== BOOT START ===");
  Serial.println("Firmware " FW_BUILD);

  // Seeded before anything can report telemetry. Never zero: the hub uses 0 as
  // "no boot id seen yet" and must not confuse it with a real session.
  bootSessionId = esp_random();
  if (bootSessionId == 0) bootSessionId = 1;
  Serial.printf("1. Boot session id: %lu\n", (unsigned long)bootSessionId);

  Serial.print("2. Init NVS/EEPROM... ");
  nvs_flash_init();
  if (EEPROM.begin(256)) Serial.println("OK");
  else Serial.println("FAILED");

  Serial.print("3. Init I2C... ");
  if (Wire.begin()) {
      Wire.setClock(100000);
      Serial.println("OK");
  } else Serial.println("FAILED");

  Serial.print("4. Init WiFi Mode... ");
  WiFi.setSleep(false); // Disable sleep for stability
  WiFi.persistent(false);
  WiFi.setAutoReconnect(false); // reconnects are scheduled below, on a fixed channel
  if (WiFi.mode(WIFI_AP_STA)) Serial.println("OK");
  else Serial.println("FAILED");

  Serial.print("5. Init SoftAP... ");
  // --- CRITICAL FIX: Custom IP for Flowmeter AP ---
  IPAddress local_IP(192, 168, 10, 1);       // Mudamos para 10.1
  IPAddress gateway(192, 168, 10, 1);
  IPAddress subnet(255, 255, 255, 0);
  // Esta linha diz ao ESP32: "Seu IP interno é 10.1, não 4.1"
  WiFi.softAPConfig(local_IP, gateway, subnet);
  // The hub AP also uses channel 6. Keeping AP and STA on the same channel
  // prevents station reconnect attempts from retuning/dropping direct clients.
  if (WiFi.softAP(ap_ssid, ap_password, wifiRadioChannel, 0, 4)) {
    Serial.print("OK IP: ");
    Serial.println(WiFi.softAPIP());
  } else Serial.println("FAILED");

  // PINS
  pinMode(VALVE_FLOW_PIN, OUTPUT);
  pinMode(VALVE1_PIN, OUTPUT);
  pinMode(VALVE2_PIN, OUTPUT);
  digitalWrite(VALVE_FLOW_PIN, valveFlowState);
  digitalWrite(VALVE1_PIN, valve1State);
  digitalWrite(VALVE2_PIN, valve2State);
  pinMode(RECEIVER_LED, OUTPUT);
  digitalWrite(RECEIVER_LED, LOW);

  commandMutex = xSemaphoreCreateMutex();
  i2cMutex = xSemaphoreCreateMutex();
  hubHttpMutex = xSemaphoreCreateMutex();
  if (commandMutex == NULL || i2cMutex == NULL || hubHttpMutex == NULL) {
    Serial.println("FATAL: failed to create command/I2C/HTTP mutex");
    while (true) delay(1000);
  }

  Serial.print("6. Init ADS1115... ");
  if (ads.begin(0x48)) {
    ads.setGain(GAIN_TWOTHIRDS);
    ads.setDataRate(RATE_ADS1115_128SPS);
    Serial.println("OK");
  } else {
    Serial.println("FAILED (Check wiring!)");
    // Não travamos aqui com while(1) para permitir debug do resto
  }

  Serial.print("7. Init MCP4725... ");
  if (mcp.begin(0x60)) {
    Serial.println("OK");
  } else {
    Serial.println("FAILED (Check wiring!)");
  }

  loadParameters();
  writeFlowSetpointToDAC(flowSetpoint);

  ws.onEvent(onWsEvent);
  server.addHandler(&ws);
  setupOTA();
  server.begin();
  Serial.println("8. WebServer Started (OTA at http://192.168.10.1/update)");

  // CREATE TASKS
  Serial.println("9. Creating Tasks...");

  BaseType_t res1 = xTaskCreatePinnedToCore(sensorTask, "SensorTask", 4096, NULL, 2, NULL, 1);
  if (res1 == pdPASS) Serial.println("   - SensorTask Created OK");
  else Serial.println("   - SensorTask FAILED (Out of memory?)");

  BaseType_t res2 = xTaskCreatePinnedToCore(httpTask, "HubCommandTask", 4096, NULL, 2, NULL, 1);
  if (res2 == pdPASS) Serial.println("   - HubCommandTask Created OK");
  else Serial.println("   - HubCommandTask FAILED");

  // Reconnection has its own stack and never blocks the command executor.
  BaseType_t res3 = xTaskCreatePinnedToCore(wifiTask, "WiFiTask", 8192, NULL, 1, NULL, 1);
  if (res3 == pdPASS) Serial.println("   - WiFiTask Created OK");
  else Serial.println("   - WiFiTask FAILED");

  BaseType_t res4 = xTaskCreatePinnedToCore(telemetryTask, "HubTelemetryTask", 4096, NULL, 1, NULL, 1);
  if (res4 == pdPASS) Serial.println("   - HubTelemetryTask Created OK");
  else Serial.println("   - HubTelemetryTask FAILED");

  Serial.println("=== SETUP DONE ===\n");
}

static unsigned long lastLoop = 0;

void loop() {
  unsigned long now = millis();

  // Direct USB commands must not wait for the telemetry broadcast.
  readSerialData();
  updateLEDBlinking();

  // --- OTA housekeeping ---
  // Reboot is deferred here so the completion handler's HTTP response gets out
  // before the TCP stack disappears under it.
  if (otaRebootAtMs && (long)(now - otaRebootAtMs) >= 0) {
    Serial.println("[OTA] Rebooting into new firmware.");
    Serial.flush();
    ESP.restart();
  }
  // A client that vanishes mid-upload never reaches the completion handler. Without
  // this the hub tasks would stay parked on otaInProgress forever. Only the flag is
  // touched here: Update is owned by the AsyncTCP task, and aborting it from this
  // task while a write is in flight frees the buffer under it (StoreProhibited).
  // If data does resume, the chunk handler re-arms the flag and the upload goes on.
  if (otaInProgress && now - otaLastChunkMs > otaStallTimeoutMs) {
    Serial.printf("[OTA] No data for %lus after %u bytes. Hub link resumes; upload may still continue.\n",
                  otaStallTimeoutMs / 1000, (unsigned)Update.progress());
    otaStalled = true;
    otaInProgress = false;
  }

  // --- Lógica de Controle PI ---
  if (now - lastControlUpdate >= controlInterval) {
    lastControlUpdate = now;
    xSemaphoreTake(commandMutex, portMAX_DELAY);

    // Only run PID if we have a target > 0.1
    if (targetFlowSetpoint > 0.1) {
      float error = targetFlowSetpoint - readFlowRate;
      flowFeedforward = feedforwardSetpoint(targetFlowSetpoint);

      // Deadband: below one DAC LSB the integral would only chase sensor noise.
      if (abs(error) > 0.01) {
        integralError += error * integralIntervalScale;

        // Anti-windup in output units. The old +/-10 clamp on integralError
        // capped the I term at +/-1.0 flow unit with Ki=0.1: against an MFC whose
        // real gain differs from nominal the loop simply ran out of authority
        // (target 2.0 stuck at 3.36 with the DAC pinned at 0.86). The integral
        // may now take the output anywhere in 0..maxFlowRate, and no further.
        // Bounds are relative to the corrected base, which is what the I term sits on.
        if (Ki_flow > 0.0f) {
          float iMin = -flowFeedforward / Ki_flow;
          float iMax = (maxFlowRate - flowFeedforward) / Ki_flow;
          integralError = constrain(integralError, iMin, iMax);
        } else {
          integralError = 0.0f;
        }

        float P_term = error * Kp_flow;
        float I_term = integralError * Ki_flow;

        // Output = setpoint corrigido + PI trim. The PI's error is still measured
        // against the real setpoint, so the flow settles on what was asked for.
        float newFlowSetpoint = constrain(flowFeedforward + P_term + I_term, 0.0f, maxFlowRate);

        // Commit only once the DAC actually took the value: a timed-out I2C write
        // used to leave flowSetpoint updated and the DAC stale, never retried.
        if (abs(newFlowSetpoint - flowSetpoint) > dacUpdateThreshold) {
          if (writeFlowSetpointToDAC(newFlowSetpoint)) flowSetpoint = newFlowSetpoint;
        }
        if (debugPI) {
          Serial.printf("[PI] t=%lu Tgt:%.3f FF:%.3f Act:%.3f Err:%.3f P:%.3f I:%.3f Out:%.3f\n",
                        now, targetFlowSetpoint, flowFeedforward, readFlowRate, error, P_term, I_term, flowSetpoint);
        }
      }
    } else {
      // If target is practically zero, force zero output
      flowFeedforward = 0.0f;
      if (flowSetpoint > 0) {
        if (writeFlowSetpointToDAC(0)) flowSetpoint = 0;
      }
    }
    xSemaphoreGive(commandMutex);
  }

  // --- Broadcast Loop (1000ms) ---
  if (now - lastLoop >= 1000) {
    lastLoop += 1000;
    ws.cleanupClients();

    float seconds = now / 1000.0;
    float snapTarget, snapOutput, snapFF;
    uint8_t snapValve1, snapValve2, snapValveFlow;
    uint32_t snapAck;
    uint32_t snapDirectAck;
    uint32_t snapDirectSession;
    unsigned long snapApplyMs;
    String snapSource;
    xSemaphoreTake(commandMutex, portMAX_DELAY);
    snapTarget = targetFlowSetpoint;
    snapOutput = flowSetpoint;
    snapFF = flowFeedforward;
    snapValve1 = valve1State;
    snapValve2 = valve2State;
    snapValveFlow = valveFlowState;
    snapAck = lastAppliedHubCommandId;
    snapDirectAck = lastAppliedDirectCommandId;
    snapDirectSession = lastAppliedDirectSessionId;
    snapApplyMs = lastCommandApplyMs;
    snapSource = lastCommandSource;
    xSemaphoreGive(commandMutex);
    snprintf(outputMessage, sizeof(outputMessage),
             "{\"seconds\":%.3f,\"flow_voltage\":%.6f,\"flow_rate\":%.6f"
             ",\"flow_setpoint\":%.6f,\"flow_setpoint_corrected\":%.6f,\"flow_output\":%.6f,\"valve1State\":%d"
             ",\"valve2State\":%d,\"valveFlowState\":%d"
             ",\"ack_cmd_id\":%lu,\"ack_direct_session_id\":%lu"
             ",\"ack_direct_cmd_id\":%lu"
             ",\"last_apply_ms\":%lu,\"command_source\":\"%s\""
             ",\"Kp\":%.2f,\"Ki\":%.2f,\"ff_gain\":%.4f,\"ff_offset\":%.4f,\"reconnect_wifi\":%d}",
             seconds, readFlowVoltage, readFlowRate,
             snapTarget, snapFF, snapOutput, snapValve1, snapValve2, snapValveFlow,
             (unsigned long)snapAck, (unsigned long)snapDirectSession,
             (unsigned long)snapDirectAck,
             snapApplyMs, snapSource.c_str(),
             Kp_flow, Ki_flow, ffGain, ffOffset, reconnect_Wifi);

    // Comentar para limpar o serial se estiver muito poluído
    Serial.println(outputMessage);
    ws.textAll(outputMessage);
  }
  yield();
}

void onWsEvent(AsyncWebSocket *server, AsyncWebSocketClient *client, AwsEventType type, void *arg, uint8_t *data, size_t len) {
  if (type == WS_EVT_CONNECT) {
    Serial.printf("WS Client #%u connected\n", client->id());
  } else if (type == WS_EVT_DISCONNECT) {
    Serial.printf("WS Client #%u disconnected\n", client->id());
  } else if (type == WS_EVT_DATA) {
    AwsFrameInfo *info = (AwsFrameInfo*)arg;
    if (info->final && info->index == 0 && info->len == len) {
      if (info->opcode == WS_TEXT) {
        String message((char*)data, len);
        bool accepted = processReceivedData(message, COMMAND_DIRECT);
        float snapTarget;
        uint8_t snapValve1, snapValve2, snapValveFlow;
        uint32_t snapDirectAck;
        uint32_t snapDirectSession;
        unsigned long snapApplyMs;
        xSemaphoreTake(commandMutex, portMAX_DELAY);
        snapTarget = targetFlowSetpoint;
        snapValve1 = valve1State;
        snapValve2 = valve2State;
        snapValveFlow = valveFlowState;
        snapDirectAck = lastAppliedDirectCommandId;
        snapDirectSession = lastAppliedDirectSessionId;
        snapApplyMs = lastCommandApplyMs;
        xSemaphoreGive(commandMutex);

        char ackMessage[280];
        snprintf(ackMessage, sizeof(ackMessage),
                 "{\"command_ack\":%s,\"ack_direct_session_id\":%lu"
                 ",\"ack_direct_cmd_id\":%lu"
                 ",\"last_apply_ms\":%lu,\"command_source\":\"direct\""
                 ",\"flow_setpoint\":%.6f,\"valve1State\":%d"
                 ",\"valve2State\":%d,\"valveFlowState\":%d}",
                 accepted ? "true" : "false", (unsigned long)snapDirectSession,
                 (unsigned long)snapDirectAck,
                 snapApplyMs, snapTarget, snapValve1, snapValve2, snapValveFlow);
        client->text(ackMessage);
      }
    }
  }
}

// ----- TASKS -----

void sensorTask(void *parameter) {
  Serial.println("[SensorTask] Started running.");
  for (;;) {
    readAndProcessADC();          // ~130 ms of conversions
    vTaskDelay(pdMS_TO_TICKS(10)); // yield; ~7 samples/s overall
  }
}

void httpTask(void *parameter) {
  Serial.println("[HubCommandTask] Started running at 4 Hz.");
  // One client for the life of the task, with keep-alive enabled once. Each poll still
  // does begin()/GET()/end(), but end() now hands the socket back instead of tearing it
  // down: rebuilding it per request meant a TCP handshake and teardown every 100 ms.
  // Together with telemetry that was ~12 connections/s piling into TIME_WAIT on the
  // hub's lwIP, which is where socket exhaustion showed up as "lost" commands.
  static HTTPClient httpCmd;
  httpCmd.setReuse(true);

  for (;;) {
    unsigned long now = millis();
    if (!otaInProgress && WiFi.status() == WL_CONNECTED) {
      // This task remains command-only, but shares the HTTP bus with telemetry
      // so two HTTPClient instances never drive the Wi-Fi stack concurrently.
      if (now - lastCommandPollTime >= commandPollInterval) {
        lastCommandPollTime = now;
        if (xSemaphoreTake(hubHttpMutex, pdMS_TO_TICKS(2000)) == pdTRUE) {
          httpCmd.begin(sensorHubURL + "/flowCommand");
          httpCmd.setConnectTimeout(hubConnectTimeoutMs);
          httpCmd.setTimeout(hubRequestTimeoutMs);
          int cmdCode = httpCmd.GET();
          String commandPayload;
          if (cmdCode == 200) {
            commandPayload = httpCmd.getString();
            commandPayload.trim();
          }
          httpCmd.end();
          xSemaphoreGive(hubHttpMutex);

          // Parsed outside the bus mutex: applying a command touches the DAC and the
          // I2C mutex, and must never hold the HTTP bus while it does.
          if (commandPayload.length() > 2) {
            Serial.println("[HubCommandTask] Command received: " + commandPayload);
            processReceivedData(commandPayload, COMMAND_HUB);
          }
        }
      }

    }
    vTaskDelay(pdMS_TO_TICKS(20));
  }
}

void telemetryTask(void *parameter) {
  Serial.println("[HubTelemetryTask] Started running independently.");
  static HTTPClient http;
  http.setReuse(true);
  uint16_t telemetryFailures = 0;

  for (;;) {
    unsigned long now = millis();
    if (!otaInProgress && WiFi.status() == WL_CONNECTED && now - lastHTTPDataTime >= telemetryInterval) {
      lastHTTPDataTime = now;

      float snapTarget, snapOutput, snapFF;
      uint8_t snapValve1, snapValve2, snapValveFlow;
      uint32_t snapAck;
      unsigned long snapApplyMs;
      String snapSource;
      xSemaphoreTake(commandMutex, portMAX_DELAY);
      snapTarget = targetFlowSetpoint;
      snapOutput = flowSetpoint;
      snapFF = flowFeedforward;
      snapValve1 = valve1State;
      snapValve2 = valve2State;
      snapValveFlow = valveFlowState;
      snapAck = lastAppliedHubCommandId;
      snapApplyMs = lastCommandApplyMs;
      snapSource = lastCommandSource;
      xSemaphoreGive(commandMutex);

      String url = sensorHubURL + "/flowData?seconds=" + String(now / 1000.0, 3) +
                   "&flow_voltage=" + String(readFlowVoltage, 6) +
                   "&flow_rate=" + String(readFlowRate, 6) +
                   "&flow_setpoint=" + String(snapTarget, 6) +
                   "&flow_setpoint_corrected=" + String(snapFF, 6) +
                   "&flow_output=" + String(snapOutput, 6) +
                   "&valve1State=" + String(snapValve1) +
                   "&valve2State=" + String(snapValve2) +
                   "&valveFlowState=" + String(snapValveFlow) +
                   "&ack_cmd_id=" + String(snapAck) +
                   "&last_apply_ms=" + String(snapApplyMs) +
                   "&command_source=" + snapSource +
                   "&boot_id=" + String(bootSessionId) +
                   "&reconnect_wifi=" + String(reconnect_Wifi ? 1 : 0);
      if (xSemaphoreTake(hubHttpMutex, pdMS_TO_TICKS(2000)) == pdTRUE) {
        http.begin(url);
        http.setConnectTimeout(hubConnectTimeoutMs);
        http.setTimeout(hubRequestTimeoutMs);
        int telemetryCode = http.GET();
        http.end();
        xSemaphoreGive(hubHttpMutex);

        if (telemetryCode == 200) {
          if (telemetryFailures > 0) {
            Serial.printf("[HubTelemetryTask] Link recovered after %u failed request(s).\n",
                          telemetryFailures);
          }
          telemetryFailures = 0;
        } else {
          telemetryFailures++;
          if (telemetryFailures == 1 || telemetryFailures % 10 == 0) {
            Serial.printf("[HubTelemetryTask] /flowData failed: HTTP %d (consecutive=%u).\n",
                          telemetryCode, telemetryFailures);
          }

          // Link watchdog. WL_CONNECTED only says the station is associated; it says
          // nothing about the path working. Without this the failure count just grew
          // forever and the flowmeter stayed silently mute until someone power-cycled it.
          if (telemetryFailures >= telemetryFailuresBeforeRelink) {
            Serial.println("[HubTelemetryTask] Link is associated but mute; forcing reassociation.");
            telemetryFailures = 0;
            if (xSemaphoreTake(hubHttpMutex, pdMS_TO_TICKS(2000)) == pdTRUE) {
              http.end();
              xSemaphoreGive(hubHttpMutex);
            }
            // wifiTask owns reconnection; dropping the association is enough to wake it.
            WiFi.disconnect(false, false);
          }
        }
      }
    }
    vTaskDelay(pdMS_TO_TICKS(20));
  }
}

// ----- Non-blocking AP+STA reconnection logic -----
// No broad WiFi scan is used here. ESP32 AP+STA has one physical radio; scans
// can stall the direct Flowmeter_AP/WebSocket link. Both known TECNAL hubs use
// channel 6, so association is attempted directly on that channel.
void wifiTask(void *parameter) {
  Serial.println("[WiFiTask] Started fixed-channel reconnect logic (no scans).");

  lastReconnectAttempt = 0;

  for (;;) {
    unsigned long now = millis();

    // No association attempts while an OTA image is streaming in over the AP.
    if (!reconnect_Wifi || otaInProgress) {
      wifiReconnectState = WF_IDLE;
      vTaskDelay(pdMS_TO_TICKS(500));
      continue;
    }

    if (WiFi.status() == WL_CONNECTED) {
      wifiReconnectState = WF_IDLE;
      failedAttemptsOnCurrentSSID = 0;
      lastReconnectAttempt = now;
      vTaskDelay(pdMS_TO_TICKS(500));
      continue;
    }

    if (now - lastReconnectAttempt < wifiReconnectInterval && wifiReconnectState == WF_IDLE) {
      vTaskDelay(pdMS_TO_TICKS(250));
      continue;
    }

    switch (wifiReconnectState) {
      case WF_IDLE:
        if (currentSSID == "") {
          currentSSID = knownHubSSIDs[nextHubIndex];
          currentPassword = currentSSID;
        }
        Serial.printf("[WiFiTask] Connecting to %s on channel %u.\n",
                      currentSSID.c_str(), wifiRadioChannel);
        WiFi.disconnect(false, false);
        WiFi.begin(currentSSID.c_str(), currentPassword.c_str(), wifiRadioChannel);
        wifiReconnectState = WF_CONNECTING;
        lastReconnectAttempt = now;
        break;

      case WF_CONNECTING:
        if (now - lastReconnectAttempt >= wifiConnectTimeout) {
          WiFi.disconnect(false, false);
          // Do not flip hubs on the first timeout. The usual cause is the right hub
          // being momentarily busy, and flipping sent the next attempt to a module
          // that is not even powered - roughly 13 s wasted per flip, so a hub that
          // never went away took ~26 s to come back instead of ~13 s.
          failedAttemptsOnCurrentSSID++;
          if (failedAttemptsOnCurrentSSID >= attemptsBeforeTryingOtherHub) {
            Serial.printf("[WiFiTask] %s failed %u times; trying the other known hub.\n",
                          currentSSID.c_str(), failedAttemptsOnCurrentSSID);
            failedAttemptsOnCurrentSSID = 0;
            nextHubIndex = (nextHubIndex + 1) % knownHubCount;
            currentSSID = "";
            currentPassword = "";
          } else {
            Serial.printf("[WiFiTask] %s timed out (%u/%u); retrying the same hub.\n",
                          currentSSID.c_str(), failedAttemptsOnCurrentSSID,
                          attemptsBeforeTryingOtherHub);
          }
          wifiReconnectState = WF_IDLE;
          lastReconnectAttempt = now;
        }
        break;
    }

    vTaskDelay(pdMS_TO_TICKS(100));
  }
}

// ----- FUNÇÕES AUXILIARES -----

// OTA routes. Both run on the AsyncTCP task: the upload callback only feeds
// Update, everything slow (the reboot) is handed to loop().
void setupOTA() {
  server.on("/update", HTTP_GET, [](AsyncWebServerRequest *request) {
    request->send(200, "text/html", otaPage);
  });

  server.on("/update", HTTP_POST,
    // Completion: runs once, after the final upload chunk.
    [](AsyncWebServerRequest *request) {
      String msg;
      bool ok = false;
      if (otaRejectReason.length()) {
        msg = "Rejected: " + otaRejectReason;
      } else if (Update.hasError()) {
        msg = "Flash failed: " + String(Update.errorString()) + ". Running firmware untouched.";
      } else if (!Update.isFinished()) {
        msg = "Upload incomplete. Running firmware untouched.";
      } else {
        ok = true;
        msg = "OK: " + String(Update.progress()) + " bytes written. Rebooting into new firmware...";
      }
      Serial.println("[OTA] " + msg);
      AsyncWebServerResponse *response = request->beginResponse(ok ? 200 : 400, "text/plain", msg);
      response->addHeader("Connection", "close");
      request->send(response);
      otaInProgress = false;
      if (ok) otaRebootAtMs = millis() + 500;
    },
    // Chunk handler: called for every piece of the multipart body.
    [](AsyncWebServerRequest *request, String filename, size_t index, uint8_t *data, size_t len, bool final) {
      if (index == 0) {
        otaRejectReason = "";
        // Only the app image belongs in an OTA slot. The other exports start with the
        // same 0xE9 magic, so Update would accept them and the name is the only tell.
        if (filename.indexOf("merged") >= 0 || filename.indexOf("bootloader") >= 0 || filename.indexOf("partitions") >= 0) {
          otaRejectReason = "'" + filename + "' is not the app image, send the plain .ino.bin";
          Serial.println("[OTA] " + otaRejectReason);
          return;
        }
        Serial.printf("[OTA] Upload start: %s\n", filename.c_str());
        if (Update.isRunning()) Update.abort();   // safe here: same task that writes
        otaStalled = false;
        otaNextProgressLog = 0;
        if (!Update.begin(UPDATE_SIZE_UNKNOWN)) Update.printError(Serial);
      }
      if (otaRejectReason.length()) return;
      // Stamped before the flag so loop() never pairs a set flag with a stale time.
      otaLastChunkMs = millis();
      otaInProgress = true;
      if (otaStalled) {
        Serial.printf("[OTA] Data resumed at %u bytes.\n", (unsigned)(index + len));
        otaStalled = false;
      }
      if (index + len >= otaNextProgressLog) {
        Serial.printf("[OTA] %u KB received, t=%lu ms\n", (unsigned)((index + len) / 1024), millis());
        otaNextProgressLog += 131072;
      }
      if (!Update.hasError() && len) {
        if (Update.write(data, len) != len) Update.printError(Serial);
      }
      if (final) {
        if (Update.end(true)) Serial.printf("[OTA] Image verified, %u bytes.\n", (unsigned)(index + len));
        else Update.printError(Serial);
      }
    });
}

void readSerialData() {
  static String inputBuffer;
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == '\n') {
      inputBuffer.trim();
      if (inputBuffer.length() > 0) {
        processReceivedData(inputBuffer, COMMAND_DIRECT);
      }
      inputBuffer = "";
    } else if (c != '\r') {
      if (inputBuffer.length() < 512) {
        inputBuffer += c;
      } else {
        inputBuffer = "";
        Serial.println("[Direct] Command discarded: input longer than 512 bytes");
      }
    }
  }
}

bool extractJsonUint32(const String &json, const char *key, uint32_t &value) {
  String token = "\"" + String(key) + "\"";
  int keyPos = json.indexOf(token);
  if (keyPos < 0) return false;
  int colonPos = json.indexOf(':', keyPos + token.length());
  if (colonPos < 0) return false;
  int start = colonPos + 1;
  while (start < json.length() && isspace(json.charAt(start))) start++;
  int end = start;
  while (end < json.length() && isDigit(json.charAt(end))) end++;
  if (end == start) return false;
  value = (uint32_t)strtoul(json.substring(start, end).c_str(), NULL, 10);
  return true;
}

bool processReceivedData(String data, CommandSource source) {
  data.trim();
  if (data.length() < 2) return false;
  if (data.startsWith("{") && data.endsWith("}")) {
    uint32_t hubCommandId = 0;
    uint32_t directSessionId = 0;
    uint32_t directCommandId = 0;
    bool hasHubCommandId = (source == COMMAND_HUB) &&
                           extractJsonUint32(data, "cmd_id", hubCommandId);
    bool hasDirectCommandId = (source == COMMAND_DIRECT) &&
                              extractJsonUint32(data, "direct_cmd_id", directCommandId);
    bool hasDirectSessionId = (source == COMMAND_DIRECT) &&
                              extractJsonUint32(data, "direct_session_id", directSessionId);

    if (hasHubCommandId) {
      xSemaphoreTake(commandMutex, portMAX_DELAY);
      bool duplicate = (hubCommandId == lastAppliedHubCommandId);
      xSemaphoreGive(commandMutex);
      if (duplicate) {
        // The hub retries until telemetry carries the acknowledgement. A local
        // command issued after this hub command must not be overwritten here.
        return true;
      }
    }

    if (hasDirectCommandId && hasDirectSessionId) {
      xSemaphoreTake(commandMutex, portMAX_DELAY);
      bool sameSession = (directSessionId == lastAppliedDirectSessionId);
      bool duplicateOrStale = sameSession &&
                              ((int32_t)(directCommandId - lastAppliedDirectCommandId) <= 0);
      xSemaphoreGive(commandMutex);
      if (duplicateOrStale) {
        // Direct-app retry after a delayed acknowledgement: acknowledge it,
        // but never actuate the same or an older command twice.
        return true;
      }
    }

    xSemaphoreTake(commandMutex, portMAX_DELAY);
    data = data.substring(1, data.length() - 1);
    int start = 0;
    bool calParamsUpdated = false;
    bool lowQuadraticUpdated = false;
    bool lowHigherOrderUpdated = false;
    bool recognizedCommand = false;
    while (start < data.length()) {
      int colonIndex = data.indexOf(':', start);
      int commaIndex = data.indexOf(',', start);
      if (colonIndex == -1) break;
      if (commaIndex == -1) commaIndex = data.length();

      if (colonIndex < commaIndex) {
        String key = data.substring(start, colonIndex);
        String value = data.substring(colonIndex + 1, commaIndex);
        key.trim();
        value.trim();

        if (key.startsWith("\"") && key.endsWith("\""))
          key = key.substring(1, key.length() - 1);

        // --- Process Keys ---
        if (key == "v_Flow" || key == "valveFlow") {
          valveFlowState = value.toInt() != 0;
          digitalWrite(VALVE_FLOW_PIN, valveFlowState);
          recognizedCommand = true;
        }
        else if (key == "v1" || key == "valve_1") {
          valve1State = value.toInt() != 0;
          digitalWrite(VALVE1_PIN, valve1State);
          recognizedCommand = true;
        }
        else if (key == "v2" || key == "valve_2") {
          valve2State = value.toInt() != 0;
          digitalWrite(VALVE2_PIN, valve2State);
          recognizedCommand = true;
        }

        else if (key == "reconnect_wifi") {
           reconnect_Wifi = (value.toInt() == 1);
           Serial.printf("Wifi Reconnect Logic set to: %s\n", reconnect_Wifi ? "TRUE" : "FALSE");
           recognizedCommand = true;
        }
        else if (key == "debug_pi") {
           debugPI = (value.toInt() == 1);
           Serial.printf("PI debug trace %s\n", debugPI ? "ON" : "OFF");
           recognizedCommand = true;
        }

        else if (key == "flow_setpoint" || key == "flowSetpoint") {
            float newTarget = constrain(value.toFloat(), 0.0f, maxFlowRate);
            recognizedCommand = true;

            // [REQ 4] Only update if change is > 0.05 OR if we are turning it OFF (0)
            // We also allow if it was previously 0 (startup)
            if (fabs(newTarget - targetFlowSetpoint) > 0.001f || newTarget == 0.0f || targetFlowSetpoint == 0.0f) {

                targetFlowSetpoint = newTarget;

                // [REQ 1 & 2 - FIX] DO NOT reset integralError here.
                // DO NOT force flowSetpoint = targetFlowSetpoint.
                // We only reset integral if we are shutting down completely.
                if (targetFlowSetpoint == 0.0) {
                    integralError = 0.0;
                    flowSetpoint = 0.0;
                    writeFlowSetpointToDAC(0.0);
                }

                Serial.printf("New Target Accepted: %.3f\n", targetFlowSetpoint);
            } else {
                Serial.printf("Target Update Ignored (Delta < 0.05): %.3f\n", newTarget);
            }
        }
        else if (key == "kp_flow") { Kp_flow = calParams.kp = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "ki_flow") { Ki_flow = calParams.ki = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "ff_gain") { ffGain = calParams.ff_gain = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "ff_offset") { ffOffset = calParams.ff_offset = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }

        else if (key == "max_flow" || key == "maxFlow") {
          float requestedMax = value.toFloat();
          if (requestedMax > 0.01f) {
            maxFlowRate = requestedMax;
            targetFlowSetpoint = constrain(targetFlowSetpoint, 0.0f, maxFlowRate);
            recognizedCommand = true;
          }
        }
        else if (key == "a1") { a1 = calParams.a1 = value.toFloat(); lowHigherOrderUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "b1") { b1 = calParams.b1 = value.toFloat(); lowHigherOrderUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "k1") { k1 = calParams.k1 = value.toFloat(); lowQuadraticUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "f1") { f1 = calParams.f1 = value.toFloat(); lowQuadraticUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "c1") { c1 = calParams.c1 = value.toFloat(); lowQuadraticUpdated = true; calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "k2") { k2 = calParams.k2 = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "f2") { f2 = calParams.f2 = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }
        else if (key == "c2") { c2 = calParams.c2 = value.toFloat(); calParamsUpdated = true; recognizedCommand = true; }

        start = commaIndex + 1;
      } else {
        break;
      }
    }
    // Backward-compatible calibration commands contain only k1/f1/c1 and mean
    // "quadratic". Explicit a1/b1 opt into the quartic low-range model.
    if (lowQuadraticUpdated && !lowHigherOrderUpdated) {
      a1 = calParams.a1 = 0.0f;
      b1 = calParams.b1 = 0.0f;
    }
    if (recognizedCommand) {
      lastCommandApplyMs = millis();
      lastCommandSource = (source == COMMAND_HUB) ? "hub" : "direct";
      if (hasHubCommandId) {
        lastAppliedHubCommandId = hubCommandId;
        Serial.printf("[HubCmd] Applied cmd_id=%lu\n", (unsigned long)hubCommandId);
      }
      if (hasDirectCommandId && hasDirectSessionId) {
        lastAppliedDirectSessionId = directSessionId;
        lastAppliedDirectCommandId = directCommandId;
        Serial.printf("[DirectCmd] Applied session=%lu direct_cmd_id=%lu\n",
                      (unsigned long)directSessionId, (unsigned long)directCommandId);
      }
    }
    if (calParamsUpdated) saveParameters();
    if (recognizedCommand) startLEDBlinking();
    xSemaphoreGive(commandMutex);
    return recognizedCommand;
  }
  return false;
}

// Returns false when the I2C bus could not be taken; the caller keeps its old
// flowSetpoint so the write is retried on the next control cycle.
bool writeFlowSetpointToDAC(float flowSetpointVal) {
  uint16_t dacValue = (flowSetpointVal / maxFlowRate) * 4095;
  dacValue = constrain(dacValue, 0, 4095);
  if (i2cMutex != NULL && xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(250)) == pdTRUE) {
    mcp.setVoltage(dacValue, false);
    xSemaphoreGive(i2cMutex);
    return true;
  }
  Serial.println("[DAC] I2C busy, write deferred");
  return false;
}

// ----- LEITURA DO ADC (Polling Mode) -----
void readAndProcessADC() {
  if (i2cMutex == NULL) return;
  float sumVolts = 0.0;
  uint8_t got = 0;
  // The mutex is taken per conversion (~8 ms), never for the whole burst: the DAC
  // write waits at most one conversion instead of the 250 ms it used to lose.
  for (uint8_t i = 0; i < ADC_SAMPLES_PER_CYCLE; i++) {
    if (xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(50)) != pdTRUE) continue;
    int16_t raw = ads.readADC_SingleEnded(3); // A3
    xSemaphoreGive(i2cMutex);
    sumVolts += ads.computeVolts(raw);
    got++;
  }
  if (got == 0) return;
  float avgVolts = sumVolts / got;

  readFlowVoltage = lowPassFilter(avgVolts, flowFilterAlpha);

  if (readFlowVoltage <= 0.0545f) {
    // Horner form reduces floating-point cancellation at millivolt inputs.
    readFlowRate = ((((a1 * readFlowVoltage + b1) * readFlowVoltage + k1)
                    * readFlowVoltage + f1) * readFlowVoltage + c1);
  } else {
    readFlowRate = k2 * sq(readFlowVoltage) + f2 * readFlowVoltage + c2;
  }
  if (readFlowRate < 0.0f) readFlowRate = 0.0f;
}

void startLEDBlinking() {
  ledBlinking = true;
  blinkStartTime = millis();
  blinkCount = 0;
  digitalWrite(RECEIVER_LED, HIGH);
}
void updateLEDBlinking() {
  if (ledBlinking) {
    if (millis() - blinkStartTime >= blinkInterval) {
      blinkStartTime = millis();
      digitalWrite(RECEIVER_LED, !digitalRead(RECEIVER_LED));
      blinkCount++;
      if (blinkCount >= 4) {
        ledBlinking = false;
        digitalWrite(RECEIVER_LED, LOW);
      }
    }
  }
}
float lowPassFilter(float newValue, float alpha) {
  float filteredValue = alpha * newValue + (1 - alpha) * lowPassPreviousFilteredValue;
  lowPassPreviousFilteredValue = filteredValue;
  return filteredValue;
}
void loadParameters() {
  EEPROM.get(0, calParams);
  if (calParams.magic == CALIBRATION_MAGIC_V2) {
    // v2 layout is a prefix of v3: calibration and PI gains are already valid.
    calParams.magic = CALIBRATION_MAGIC;
    calParams.ff_gain = FF_GAIN_DEFAULT;
    calParams.ff_offset = FF_OFFSET_DEFAULT;
    EEPROM.put(0, calParams);
    EEPROM.commit();
    Serial.println("Calibration record migrated to v3 (feedforward defaults added).");
  } else if (calParams.magic != CALIBRATION_MAGIC) {
    calParams.magic = CALIBRATION_MAGIC;
    // Low curve: exact interpolation of (V, L/min) = (0.010330,0),
    // (0.024090,0.5), (0.040640,0.75), with value AND slope matched to curve 2
    // at 0.0545 V. The result is monotonic throughout the calibrated low range.
    calParams.a1 = 321791.345936369;
    calParams.b1 = -32589.073104291;
    calParams.k1 = 462.893536740;
    calParams.f1 = 43.294432104;
    calParams.c1 = -0.464367483;
    // Preserve the high-range curve from the successful calibration.
    calParams.k2 = -0.854551899;
    calParams.f2 = 11.814453070;
    calParams.c2 = 0.192231954;

    calParams.kp = 0.2;
    calParams.ki = 0.02;
    calParams.ff_gain = FF_GAIN_DEFAULT;
    calParams.ff_offset = FF_OFFSET_DEFAULT;

    EEPROM.put(0, calParams);
    EEPROM.commit();
    Serial.println("Calibration parameters reset to defaults.");
  } else {
    Serial.println("Calibration parameters loaded.");
  }
  a1 = calParams.a1; b1 = calParams.b1;
  k1 = calParams.k1; f1 = calParams.f1; c1 = calParams.c1;
  k2 = calParams.k2; f2 = calParams.f2; c2 = calParams.c2;

  Kp_flow = calParams.kp;
  Ki_flow = calParams.ki;
  ffGain = calParams.ff_gain;
  ffOffset = calParams.ff_offset;
  Serial.printf("Feedforward: corrected = %.4f * real + %.4f\n", ffGain, ffOffset);
}

// Setpoint corrigido for a given setpoint real. Zero stays zero: the MFC must be
// fully closed when nothing is requested, whatever the offset says.
float feedforwardSetpoint(float target) {
  if (target <= 0.0f) return 0.0f;
  return constrain(ffGain * target + ffOffset, 0.0f, maxFlowRate);
}
void saveParameters() {
  EEPROM.put(0, calParams);
  EEPROM.commit();
  Serial.println("Params Saved.");
}
