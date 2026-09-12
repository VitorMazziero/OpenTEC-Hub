/***********************************************************************
 * Flowmeter ESP32 - DEBUG VERSION WITH RECONNECT LOGIC
 ***********************************************************************/

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
const char* ap_ssid = "Flowmeter_AP";
const char* ap_password = "flowmeter123";

String currentSSID = "";
String currentPassword = "";

// New Control Variable for Reconnection
bool reconnect_Wifi = true; 
unsigned long lastReconnectAttempt = 0;
const unsigned long wifiReconnectInterval = 10000; // 10 seconds
enum WifiReconnectState { WF_IDLE, WF_SCANNING, WF_CONNECTING };
WifiReconnectState wifiReconnectState = WF_IDLE;

AsyncWebServer server(80);
AsyncWebSocket ws("/ws");

Adafruit_ADS1115 ads;
Adafruit_MCP4725 mcp;

#define VALVE_FLOW_PIN 5
#define VALVE1_PIN 17
#define VALVE2_PIN 16
#define RECEIVER_LED 19

uint8_t valveFlowState = 0;
uint8_t valve1State = 0;
uint8_t valve2State = 0;

#define NUM_SAMPLES 3
float samples[NUM_SAMPLES] = {0};
uint8_t currentSampleIndex = 0;

// Global calibration parameters
float k1, f1, c1;
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
const unsigned long controlInterval = 10000;

bool ledBlinking = false;
unsigned long blinkStartTime = 0;
const unsigned long blinkInterval = 200;
uint8_t blinkCount = 0;
float lowPassPreviousFilteredValue = 0;

char outputMessage[300];
String sensorHubURL = "http://192.168.4.1";
unsigned long lastHTTPDataTime = 0;
unsigned long lastCommandPollTime = 0;
const unsigned long commandPollInterval = 2000;

struct CalibrationParams {
  uint32_t magic;
  float k1, f1, c1, k2, f2, c2;
  float kp, ki; 
};
CalibrationParams calParams;
const uint32_t CALIBRATION_MAGIC = 0xCAFEBABE; 

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
void processReceivedData(String data);
void onWsEvent(AsyncWebSocket *server, AsyncWebSocketClient *client, AwsEventType type, void *arg, uint8_t *data, size_t len);

// Tasks
void sensorTask(void *parameter);
void httpTask(void *parameter);
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
  if (WiFi.mode(WIFI_AP_STA)) Serial.println("OK");
  else Serial.println("FAILED");

  Serial.print("5. Init SoftAP... ");
  // --- CRITICAL FIX: Custom IP for Flowmeter AP ---
  IPAddress local_IP(192, 168, 10, 1);       // Mudamos para 10.1
  IPAddress gateway(192, 168, 10, 1);
  IPAddress subnet(255, 255, 255, 0);
  // Esta linha diz ao ESP32: "Seu IP interno é 10.1, não 4.1"
  WiFi.softAPConfig(local_IP, gateway, subnet);
  if (WiFi.softAP(ap_ssid, ap_password)) {
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

  BaseType_t res2 = xTaskCreatePinnedToCore(httpTask, "HTTPTask", 4096, NULL, 1, NULL, 1);
  if (res2 == pdPASS) Serial.println("   - HTTPTask Created OK");
  else Serial.println("   - HTTPTask FAILED");

  // Aumentei a stack para 8192 pois scans WiFi usam muita memória
  BaseType_t res3 = xTaskCreatePinnedToCore(wifiTask, "WiFiTask", 8192, NULL, 1, NULL, 1);
  if (res3 == pdPASS) Serial.println("   - WiFiTask Created OK");
  else Serial.println("   - WiFiTask FAILED");

  Serial.println("=== SETUP DONE ===\n");
}

static unsigned long lastLoop = 0;

void loop() {
  unsigned long now = millis();

  // --- Lógica de Controle PI ---
  if (now - lastControlUpdate >= controlInterval) {
    lastControlUpdate = now;
    
    // Only run PID if we have a target > 0.1
    if (targetFlowSetpoint > 0.1) {
      float error = targetFlowSetpoint - readFlowRate;
      
      // [REQ 3 - FIX] Reduced deadband from 0.05 to 0.01 
      // This allows the Integral term to fix small steady-state errors (the 0.1 offset you saw)
      if (abs(error) > 0.01) {
        
        integralError += error;
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
  }

  // --- Broadcast Loop (1000ms) ---
  if (now - lastLoop >= 1000) { 
    lastLoop += 1000;
    readSerialData();
    updateLEDBlinking();
    ws.cleanupClients(); 

    float seconds = now / 1000.0;
    snprintf(outputMessage, sizeof(outputMessage),
             "{\"seconds\":%.3f,\"flow_voltage\":%.6f,\"flow_rate\":%.6f"
             ",\"flow_setpoint\":%.6f,\"valve1State\":%d"
             ",\"valve2State\":%d,\"valveFlowState\":%d"
             ",\"Kp\":%.2f,\"Ki\":%.2f,\"reconnect_wifi\":%d}", 
             seconds, readFlowVoltage, readFlowRate,
             targetFlowSetpoint, valve1State, valve2State, valveFlowState,
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
        data[len] = 0; 
        String message = (char*)data;
        processReceivedData(message); 
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
  Serial.println("[HTTPTask] Started running.");
  for (;;) {
    unsigned long now = millis();
    if (WiFi.status() == WL_CONNECTED) {
      HTTPClient http;
      String url = sensorHubURL + "/flowData?seconds=" + String(now / 1000.0, 3) +
                   "&flow_voltage=" + String(readFlowVoltage, 6) +
                   "&flow_rate=" + String(readFlowRate, 6) +
                   "&flow_setpoint=" + String(targetFlowSetpoint, 6) + 
                   "&valve1State=" + String(valve1State) +
                   "&valve2State=" + String(valve2State) +
                   "&valveFlowState=" + String(valveFlowState);
      http.begin(url);
      int httpCode = http.GET();
      http.end();
      
      if (now - lastCommandPollTime >= commandPollInterval) {
        lastCommandPollTime = now;
        HTTPClient httpCmd;
        String urlCmd = sensorHubURL + "/flowCommand";
        httpCmd.begin(urlCmd);
        int cmdCode = httpCmd.GET();
        if (cmdCode == 200) {
          String commandPayload = httpCmd.getString();
          commandPayload.trim();
          if (commandPayload.length() > 2) {
            Serial.println("[HTTPTask] Command received: " + commandPayload);
            processReceivedData(commandPayload);
          }
        }
        httpCmd.end();
      }
    }
    vTaskDelay(pdMS_TO_TICKS(500)); 
  }
}

// ----- Non-blocking AP+STA reconnection logic -----
void wifiTask(void *parameter) {
  Serial.println("[WiFiTask] Started running with async reconnect logic.");
  
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
        if (currentSSID != "") {
          Serial.printf("[WiFiTask] Connecting to known hub: %s\n", currentSSID.c_str());
          WiFi.disconnect(false, false);
          WiFi.begin(currentSSID.c_str(), currentPassword == "" ? currentSSID.c_str() : currentPassword.c_str());
          wifiReconnectState = WF_CONNECTING;
          lastReconnectAttempt = now;
        } else {
          Serial.println("[WiFiTask] Starting async hub scan...");
          WiFi.scanDelete();
          WiFi.scanNetworks(true, true);
          wifiReconnectState = WF_SCANNING;
          lastReconnectAttempt = now;
        }
        break;

      case WF_SCANNING: {
        int n = WiFi.scanComplete();
        if (n == -1) break;

        String ssidToTry = "";
        if (n > 0) {
          for (int i = 0; i < n; ++i) {
            String ssid = WiFi.SSID(i);
            if (ssid == "ModuloTECNAL_1" || ssid == "ModuloTECNAL_2") {
              ssidToTry = ssid;
              currentSSID = ssid;
              currentPassword = ssid;
              break;
            }
          }
        }
        WiFi.scanDelete();

        if (ssidToTry != "") {
          Serial.printf("[WiFiTask] Hub found: %s. Connecting STA without touching AP.\n", ssidToTry.c_str());
          WiFi.disconnect(false, false);
          WiFi.begin(ssidToTry.c_str(), ssidToTry.c_str());
          wifiReconnectState = WF_CONNECTING;
        } else {
          Serial.println("[WiFiTask] Hub not found. Keeping local AP active.");
          wifiReconnectState = WF_IDLE;
        }
        lastReconnectAttempt = now;
        break;
      }

      case WF_CONNECTING:
        Serial.println("[WiFiTask] Connect attempt timed out. Will retry later.");
        currentSSID = "";
        currentPassword = "";
        wifiReconnectState = WF_IDLE;
        lastReconnectAttempt = now;
        break;
    }

    vTaskDelay(pdMS_TO_TICKS(100));
  }
}

// ----- FUNÇÕES AUXILIARES -----

void readSerialData() {
  if (Serial.available()) {
    String data;
    data = Serial.readStringUntil('\n');
    processReceivedData(data);
  }
}

void processReceivedData(String data) {
  data.trim();
  if (data.length() < 2) return;
  if (data.startsWith("{") && data.endsWith("}")) {
    data = data.substring(1, data.length() - 1);
    int start = 0;
    bool calParamsUpdated = false;
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
        if (key == "v_Flow") digitalWrite(VALVE_FLOW_PIN, valveFlowState = value.toInt());
        else if (key == "v1") digitalWrite(VALVE1_PIN, valve1State = value.toInt());
        else if (key == "v2") digitalWrite(VALVE2_PIN, valve2State = value.toInt());
        
        else if (key == "reconnect_wifi") {
           reconnect_Wifi = (value.toInt() == 1);
           Serial.printf("Wifi Reconnect Logic set to: %s\n", reconnect_Wifi ? "TRUE" : "FALSE");
        }
        
        else if (key == "flow_setpoint") {
            float newTarget = value.toFloat();
            
            // [REQ 4] Only update if change is > 0.05 OR if we are turning it OFF (0)
            // We also allow if it was previously 0 (startup)
            if (fabs(newTarget - targetFlowSetpoint) > 0.05 || newTarget == 0.0 || targetFlowSetpoint == 0.0) {
                
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
        else if (key == "kp_flow") { Kp_flow = calParams.kp = value.toFloat(); calParamsUpdated = true; }
        else if (key == "ki_flow") { Ki_flow = calParams.ki = value.toFloat(); calParamsUpdated = true; }

        else if (key == "max_flow") maxFlowRate = value.toFloat();
        else if (key == "k1") { k1 = calParams.k1 = value.toFloat(); calParamsUpdated = true; }
        else if (key == "f1") { f1 = calParams.f1 = value.toFloat(); calParamsUpdated = true; }
        else if (key == "c1") { c1 = calParams.c1 = value.toFloat(); calParamsUpdated = true; }
        else if (key == "k2") { k2 = calParams.k2 = value.toFloat(); calParamsUpdated = true; }
        else if (key == "f2") { f2 = calParams.f2 = value.toFloat(); calParamsUpdated = true; }
        else if (key == "c2") { c2 = calParams.c2 = value.toFloat(); calParamsUpdated = true; }
        
        start = commaIndex + 1;
      } else {
        break;
      }
    }
    if (calParamsUpdated) saveParameters();
    startLEDBlinking();
  }
}

void writeFlowSetpointToDAC(float flowSetpointVal) {
  uint16_t dacValue = (flowSetpointVal / maxFlowRate) * 4095;
  dacValue = constrain(dacValue, 0, 4095);
  mcp.setVoltage(dacValue, false);
}

// ----- LEITURA DO ADC (Polling Mode) -----
void readAndProcessADC() {
  float sumVolts = 0.0;
  for (int i = 0; i < 2; i++) {
    int16_t raw = ads.readADC_SingleEnded(3); // A3
    sumVolts += ads.computeVolts(raw);
  }
  float avgVolts = sumVolts / 2.0;

  float filtered = lowPassFilter(avgVolts, 0.25f);
  float smoothed = movingAverageFilter(filtered);
  readFlowVoltage = smoothed;
  
  if (readFlowVoltage <= 0.0545f) {
    readFlowRate = k1 * sq(readFlowVoltage) + f1 * readFlowVoltage + c1;
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
    calParams.k1 = -139.0570077428; calParams.f1 = 21.9738888302; calParams.c1 = -0.0341880209;
    calParams.k2 = -0.8724324917; calParams.f2 = 10.6573301479; calParams.c2 = 0.1953756879;
    
    calParams.kp = 0.1;
    calParams.ki = 0.1;

    EEPROM.put(0, calParams);
    EEPROM.commit();
    Serial.println("Calibration parameters reset to defaults.");
  } else {
    Serial.println("Calibration parameters loaded.");
  }
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
