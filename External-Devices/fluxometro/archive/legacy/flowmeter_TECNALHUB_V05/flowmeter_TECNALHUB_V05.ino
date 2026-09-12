/***********************************************************************
 * Flowmeter ESP32 - TECNAL Hub V05
 *
 * Reliable command rules:
 * - Local Serial/WebSocket commands remain immediate; V05 app commands ack/retry.
 * - Hub commands carry cmd_id and are idempotent.
 * - Repeated hub delivery is acknowledged but never applied twice.
 * - Telemetry reports the applied cmd_id and actual valve state.
 * - AP+STA stays on channel 6 and never performs disruptive broad scans.
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
#include <EEPROM.h>

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
enum WifiReconnectState { WF_IDLE, WF_CONNECTING };
WifiReconnectState wifiReconnectState = WF_IDLE;

AsyncWebServer server(80);
AsyncWebSocket ws("/ws");

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

#define NUM_SAMPLES 3
float samples[NUM_SAMPLES] = {0};
uint8_t currentSampleIndex = 0;

// Global calibration parameters. The low range uses a quartic so it passes
// through the three measured points and matches curve 2 in value and slope.
float a1, b1, k1, f1, c1;
float k2, f2, c2;

float readFlowVoltage = 0.0;
float readFlowRate = 0.0;
float maxFlowRate = 50.0;

// Variáveis de Controle
float flowSetpoint = 0.0;
float targetFlowSetpoint = 0.0;
float Kp_flow = 0.1; 
float Ki_flow = 0.1; 
float integralError = 0.0;
unsigned long lastControlUpdate = 0;
const unsigned long controlInterval = 200;
// Ki was tuned with the former 10 s cycle. Scale each integral increment so
// increasing the calculation rate does not make integral action 5x stronger.
const float integralIntervalScale = controlInterval / 10000.0f;

bool ledBlinking = false;
unsigned long blinkStartTime = 0;
const unsigned long blinkInterval = 200;
uint8_t blinkCount = 0;
float lowPassPreviousFilteredValue = 0;

char outputMessage[420];
String sensorHubURL = "http://192.168.4.1";
unsigned long lastHTTPDataTime = 0;
unsigned long lastCommandPollTime = 0;
const unsigned long commandPollInterval = 100;
const unsigned long telemetryInterval = 500;
const uint16_t hubConnectTimeoutMs = 1000;
const uint16_t hubRequestTimeoutMs = 1500;

struct CalibrationParams {
  uint32_t magic;
  float a1, b1, k1, f1, c1, k2, f2, c2;
  float kp, ki; 
};
CalibrationParams calParams;
// Schema v2 adds a1/b1. Changing the magic migrates quadratic EEPROM data.
const uint32_t CALIBRATION_MAGIC = 0xCAFEBAC0;

// Forward declarations
void readAndProcessADC();
float lowPassFilter(float newValue, float alpha);
float movingAverageFilter(float newValue);
void readSerialData();
void writeFlowSetpointToDAC(float flowSetpoint);
void startLEDBlinking();
void updateLEDBlinking();
void loadParameters();
void saveParameters();
bool processReceivedData(String data, CommandSource source = COMMAND_DIRECT);
bool extractJsonUint32(const String &json, const char *key, uint32_t &value);
void onWsEvent(AsyncWebSocket *server, AsyncWebSocketClient *client, AwsEventType type, void *arg, uint8_t *data, size_t len);

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
    ads.setDataRate(RATE_ADS1115_8SPS); 
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
  server.begin();
  Serial.println("8. WebServer Started");

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

  // --- Lógica de Controle PI ---
  if (now - lastControlUpdate >= controlInterval) {
    lastControlUpdate = now;
    xSemaphoreTake(commandMutex, portMAX_DELAY);
    
    // Only run PID if we have a target > 0.1
    if (targetFlowSetpoint > 0.1) {
      float error = targetFlowSetpoint - readFlowRate;
      
      // [REQ 3 - FIX] Reduced deadband from 0.05 to 0.01 
      // This allows the Integral term to fix small steady-state errors (the 0.1 offset you saw)
      if (abs(error) > 0.01) {
        
        integralError += error * integralIntervalScale;
        // Constrain integral to prevent massive windup
        integralError = constrain(integralError, -10.0, 10.0); 
        
        float P_term = error * Kp_flow;
        float I_term = integralError * Ki_flow;
        
        // The new output is calculated based on the TARGET + PID corrections
        // (Feedforward approach: output = setpoint + correction)
        float newFlowSetpoint = targetFlowSetpoint + P_term + I_term;
        
        // Safety clamps
        newFlowSetpoint = constrain(newFlowSetpoint, 0.0, maxFlowRate);

        // [REQ 3 - FIX] Reduced output update threshold from 0.02 to 0.005
        // This ensures fine adjustments are actually written to the DAC
        if (abs(newFlowSetpoint - flowSetpoint) > 0.05) {
            flowSetpoint = newFlowSetpoint;
            writeFlowSetpointToDAC(flowSetpoint);
            
            // Uncomment for debugging PID behavior
            // Serial.printf("Tgt:%.2f Act:%.2f Err:%.2f Out:%.2f\n", targetFlowSetpoint, readFlowRate, error, flowSetpoint);
        }
      }
    } else {
        // If target is practically zero, force zero output
        if (flowSetpoint > 0) {
            flowSetpoint = 0;
            writeFlowSetpointToDAC(0);
        }
    }
    xSemaphoreGive(commandMutex);
  }

  // --- Broadcast Loop (1000ms) ---
  if (now - lastLoop >= 1000) { 
    lastLoop += 1000;
    ws.cleanupClients(); 

    float seconds = now / 1000.0;
    float snapTarget;
    uint8_t snapValve1, snapValve2, snapValveFlow;
    uint32_t snapAck;
    uint32_t snapDirectAck;
    uint32_t snapDirectSession;
    unsigned long snapApplyMs;
    String snapSource;
    xSemaphoreTake(commandMutex, portMAX_DELAY);
    snapTarget = targetFlowSetpoint;
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
             ",\"flow_setpoint\":%.6f,\"valve1State\":%d"
             ",\"valve2State\":%d,\"valveFlowState\":%d"
             ",\"ack_cmd_id\":%lu,\"ack_direct_session_id\":%lu"
             ",\"ack_direct_cmd_id\":%lu"
             ",\"last_apply_ms\":%lu,\"command_source\":\"%s\""
             ",\"Kp\":%.2f,\"Ki\":%.2f,\"reconnect_wifi\":%d}",
             seconds, readFlowVoltage, readFlowRate,
             snapTarget, snapValve1, snapValve2, snapValveFlow,
             (unsigned long)snapAck, (unsigned long)snapDirectSession,
             (unsigned long)snapDirectAck,
             snapApplyMs, snapSource.c_str(),
             Kp_flow, Ki_flow, reconnect_Wifi);

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
    readAndProcessADC();
    vTaskDelay(pdMS_TO_TICKS(125)); 
  }
}

void httpTask(void *parameter) {
  Serial.println("[HubCommandTask] Started running at 10 Hz.");
  for (;;) {
    unsigned long now = millis();
    if (WiFi.status() == WL_CONNECTED) {
      // This task remains command-only, but shares the HTTP bus with telemetry
      // so two HTTPClient instances never drive the Wi-Fi stack concurrently.
      if (now - lastCommandPollTime >= commandPollInterval) {
        lastCommandPollTime = now;
        if (xSemaphoreTake(hubHttpMutex, pdMS_TO_TICKS(2000)) == pdTRUE) {
          HTTPClient httpCmd;
          String urlCmd = sensorHubURL + "/flowCommand";
          httpCmd.begin(urlCmd);
          httpCmd.setConnectTimeout(hubConnectTimeoutMs);
          httpCmd.setTimeout(hubRequestTimeoutMs);
          int cmdCode = httpCmd.GET();
          if (cmdCode == 200) {
            String commandPayload = httpCmd.getString();
            commandPayload.trim();
            if (commandPayload.length() > 2) {
              Serial.println("[HubCommandTask] Command received: " + commandPayload);
              processReceivedData(commandPayload, COMMAND_HUB);
            }
          }
          httpCmd.end();
          xSemaphoreGive(hubHttpMutex);
        }
      }

    }
    vTaskDelay(pdMS_TO_TICKS(20));
  }
}

void telemetryTask(void *parameter) {
  Serial.println("[HubTelemetryTask] Started running independently.");
  for (;;) {
    unsigned long now = millis();
    if (WiFi.status() == WL_CONNECTED && now - lastHTTPDataTime >= telemetryInterval) {
      lastHTTPDataTime = now;

      float snapTarget;
      uint8_t snapValve1, snapValve2, snapValveFlow;
      uint32_t snapAck;
      unsigned long snapApplyMs;
      String snapSource;
      xSemaphoreTake(commandMutex, portMAX_DELAY);
      snapTarget = targetFlowSetpoint;
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
                   "&valve1State=" + String(snapValve1) +
                   "&valve2State=" + String(snapValve2) +
                   "&valveFlowState=" + String(snapValveFlow) +
                   "&ack_cmd_id=" + String(snapAck) +
                   "&last_apply_ms=" + String(snapApplyMs) +
                   "&command_source=" + snapSource;
      if (xSemaphoreTake(hubHttpMutex, pdMS_TO_TICKS(2000)) == pdTRUE) {
        HTTPClient http;
        http.begin(url);
        http.setConnectTimeout(hubConnectTimeoutMs);
        http.setTimeout(hubRequestTimeoutMs);
        int telemetryCode = http.GET();
        http.end();
        xSemaphoreGive(hubHttpMutex);

        static uint16_t telemetryFailures = 0;
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

    if (!reconnect_Wifi) {
      wifiReconnectState = WF_IDLE;
      vTaskDelay(pdMS_TO_TICKS(500));
      continue;
    }

    if (WiFi.status() == WL_CONNECTED) {
      wifiReconnectState = WF_IDLE;
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
          Serial.println("[WiFiTask] Connect attempt timed out; trying the other known hub later.");
          WiFi.disconnect(false, false);
          nextHubIndex = (nextHubIndex + 1) % knownHubCount;
          currentSSID = "";
          currentPassword = "";
          wifiReconnectState = WF_IDLE;
          lastReconnectAttempt = now;
        }
        break;
    }

    vTaskDelay(pdMS_TO_TICKS(100));
  }
}

// ----- FUNÇÕES AUXILIARES -----

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

void writeFlowSetpointToDAC(float flowSetpointVal) {
  uint16_t dacValue = (flowSetpointVal / maxFlowRate) * 4095;
  dacValue = constrain(dacValue, 0, 4095);
  if (i2cMutex != NULL && xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(250)) == pdTRUE) {
    mcp.setVoltage(dacValue, false);
    xSemaphoreGive(i2cMutex);
  }
}

// ----- LEITURA DO ADC (Polling Mode) -----
void readAndProcessADC() {
  float sumVolts = 0.0;
  if (i2cMutex == NULL || xSemaphoreTake(i2cMutex, pdMS_TO_TICKS(250)) != pdTRUE) return;
  for (int i = 0; i < 2; i++) {
    int16_t raw = ads.readADC_SingleEnded(3); // A3
    sumVolts += ads.computeVolts(raw);
  }
  xSemaphoreGive(i2cMutex);
  float avgVolts = sumVolts / 2.0;

  float filtered = lowPassFilter(avgVolts, 0.25f);
  float smoothed = movingAverageFilter(filtered);
  readFlowVoltage = smoothed;
  
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
float movingAverageFilter(float newValue) {
  samples[currentSampleIndex] = newValue;
  currentSampleIndex = (currentSampleIndex + 1) % NUM_SAMPLES;
  float sum = 0;
  for (uint8_t i = 0; i < NUM_SAMPLES; i++) { sum += samples[i]; }
  return sum / NUM_SAMPLES;
}
void loadParameters() {
  EEPROM.get(0, calParams);
  if (calParams.magic != CALIBRATION_MAGIC) {
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
}
void saveParameters() {
  EEPROM.put(0, calParams);
  EEPROM.commit();
  Serial.println("Params Saved.");
}
