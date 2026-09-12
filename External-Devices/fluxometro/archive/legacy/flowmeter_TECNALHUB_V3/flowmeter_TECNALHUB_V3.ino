/***********************************************************************
 * Flowmeter ESP32 Code (Revised with Auto-Correction Control Loop)
 ***********************************************************************/

#include <AsyncTCP.h>
#include <ESPAsyncWebServer.h>

#include <Adafruit_MCP4725.h>
#include <Adafruit_ADS1X15.h>
#include <BluetoothSerial.h>
#include <WiFi.h>
#include <Wire.h>
#include <HTTPClient.h>
#include <nvs_flash.h>
#include <EEPROM.h>

// ----- WiFi Credentials -----
const char* ap_ssid = "Flowmeter_AP";
const char* ap_password = "flowmeter123";

String currentSSID = "";
String currentPassword = "";

unsigned long lastWiFiCheckTime = 0;
const unsigned long wifiCheckInterval = 5000;

AsyncWebServer server(80);
AsyncWebSocket ws("/ws");

Adafruit_ADS1115 ads;
Adafruit_MCP4725 mcp;
BluetoothSerial SerialBT;

#define READY_PIN 4
#ifndef IRAM_ATTR
#define IRAM_ATTR
#endif
volatile bool new_data_available = false;
void IRAM_ATTR NewDataReadyISR() {
  new_data_available = true;
}

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

// Flow meter variables
float readFlowVoltage = 0.0;
float readFlowRate = 0.0;
float maxFlowRate = 50.0;

// --- NOVO: Variáveis de Controle ---
float flowSetpoint = 0.0;        // Este é o valor enviado ao DAC (Setpoint Interno)
float targetFlowSetpoint = 0.0;  // Este é o valor que o usuário QUER (Setpoint Alvo)
unsigned long lastControlUpdate = 0;
const unsigned long controlInterval = 10000; // 10 segundos
float Kp_flow = 0.5; // Ganho do controlador. 0.5 significa que corrige 50% do erro a cada 10s
// -----------------------------------

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
};
CalibrationParams calParams;
const uint32_t CALIBRATION_MAGIC = 0xDEADBEEF;

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
  SerialBT.begin("Flowmeter 50L/min");

  nvs_flash_init();
  EEPROM.begin(64);

  Wire.begin();
  Wire.setClock(100000);

  WiFi.mode(WIFI_AP_STA);
  WiFi.softAP(ap_ssid, ap_password);
  Serial.println("\n--- Flowmeter Access Point ---");
  Serial.println(WiFi.softAPIP());

  Serial.println("Scanning for Sensor Hub WiFi network...");

  pinMode(VALVE_FLOW_PIN, OUTPUT);
  pinMode(VALVE1_PIN, OUTPUT);
  pinMode(VALVE2_PIN, OUTPUT);
  digitalWrite(VALVE_FLOW_PIN, valveFlowState);
  digitalWrite(VALVE1_PIN, valve1State);
  digitalWrite(VALVE2_PIN, valve2State);
  pinMode(RECEIVER_LED, OUTPUT);
  digitalWrite(RECEIVER_LED, LOW);
  
  pinMode(READY_PIN, INPUT);

  if (!ads.begin(0x48)) {
    Serial.println(F("Failed to initialize ADS1115."));
    while (1);
  } else {
    Serial.println(F("ADC ADS1115 initialized."));
  }
  
  ads.setGain(GAIN_TWOTHIRDS);
  ads.setDataRate(RATE_ADS1115_8SPS);
  attachInterrupt(digitalPinToInterrupt(READY_PIN), NewDataReadyISR, FALLING);
  ads.startADCReading(ADS1X15_REG_CONFIG_MUX_SINGLE_3, true);
  
  if (!mcp.begin(0x60)) {
    Serial.println(F("Failed to find MCP4725 chip."));
  } else {
    Serial.println(F("DAC MCP4725 initialized."));
  }
  
  delay(50);
  loadParameters();
  writeFlowSetpointToDAC(flowSetpoint);

  ws.onEvent(onWsEvent);
  server.addHandler(&ws);
  server.begin();

  xTaskCreatePinnedToCore(sensorTask, "SensorTask", 4096, NULL, 2, NULL, 1);
  xTaskCreatePinnedToCore(httpTask, "HTTPTask", 4096, NULL, 1, NULL, 1);
  xTaskCreatePinnedToCore(wifiTask, "WiFiTask", 4096, NULL, 1, NULL, 1);
}

static unsigned long lastLoop = 0;

void loop() {
  unsigned long now = millis();

  // --- NOVO: Lógica do Controlador Proporcional/Integral ---
  if (now - lastControlUpdate >= controlInterval) {
    lastControlUpdate = now;
    
    // Só atua se o usuário pediu uma vazão maior que um mínimo (vazão ativa)
    // e se o controle automático estiver "ligado" (implícito pelo setpoint > 0.1)
    if (targetFlowSetpoint > 0.1) {
      
      // 1. Calcula o erro (O que eu quero - O que eu tenho)
      float error = targetFlowSetpoint - readFlowRate;
      
      // 2. Aplica Deadband (opcional) para evitar oscilação em torno do setpoint
      if (abs(error) > 0.05) {
        
        // 3. Calcula ajuste (Ação Integral Discreta)
        // Se erro > 0 (quero 4, tenho 3.8), error = 0.2. Adjustment positivo. Aumenta DAC.
        // Se erro < 0 (quero 4, tenho 4.2), error = -0.2. Adjustment negativo. Diminui DAC.
        float adjustment = error * Kp_flow;
        
        // 4. Atualiza o Setpoint Interno (DAC)
        flowSetpoint += adjustment;
        
        // 5. Proteções de limite
        flowSetpoint = constrain(flowSetpoint, 0.0, maxFlowRate);
        
        // 6. Envia para o hardware
        writeFlowSetpointToDAC(flowSetpoint);
        
        Serial.printf("Auto-Control: Target=%.2f, Actual=%.2f, Err=%.2f, New_DAC_SP=%.2f\n", 
                      targetFlowSetpoint, readFlowRate, error, flowSetpoint);
      }
    }
  }
  // ---------------------------------------------------------

  if (now - lastLoop >= 250) {
    lastLoop += 250;
    readSerialData();
    updateLEDBlinking();
    ws.cleanupClients(); 

    // Nota: enviamos 'targetFlowSetpoint' no JSON como 'flow_setpoint' 
    // para que o App veja o que o usuário pediu, não o valor interno corrigido.
    // Se preferir ver o valor interno corrigido, mude para 'flowSetpoint'.
    float seconds = now / 1000.0;
    snprintf(outputMessage, sizeof(outputMessage),
             "{\"seconds\":%.3f,\"flow_voltage\":%.6f,\"flow_rate\":%.6f"
             ",\"flow_setpoint\":%.6f,\"valve1State\":%d"
             ",\"valve2State\":%d,\"valveFlowState\":%d}",
             seconds, readFlowVoltage, readFlowRate,
             targetFlowSetpoint, valve1State, valve2State, valveFlowState); // Usando targetFlowSetpoint aqui

    Serial.println(outputMessage);      
    SerialBT.println(outputMessage);    
    ws.textAll(outputMessage);          
  }

  yield();
}

void onWsEvent(AsyncWebSocket *server, AsyncWebSocketClient *client, AwsEventType type, void *arg, uint8_t *data, size_t len) {
  if (type == WS_EVT_CONNECT) {
    Serial.printf("WebSocket client #%u connected\n", client->id());
  } else if (type == WS_EVT_DISCONNECT) {
    Serial.printf("WebSocket client #%u disconnected\n", client->id());
  } else if (type == WS_EVT_DATA) {
    AwsFrameInfo *info = (AwsFrameInfo*)arg;
    if (info->final && info->index == 0 && info->len == len) {
      if (info->opcode == WS_TEXT) {
        data[len] = 0; 
        String message = (char*)data;
        Serial.printf("Received WS: %s\n", message.c_str());
        processReceivedData(message); 
      }
    }
  }
}

void sensorTask(void *parameter) {
  for (;;) {
    if (new_data_available) {
      readAndProcessADC();
    }
    vTaskDelay(pdMS_TO_TICKS(10));
  }
}

void httpTask(void *parameter) {
  for (;;) {
    unsigned long now = millis();
    if (WiFi.status() == WL_CONNECTED) {
      HTTPClient http;
      // Enviando targetFlowSetpoint para o Hub manter consistência com o que o usuário pediu
      String url = sensorHubURL + "/flowData?seconds=" + String(now / 1000.0, 3) +
                   "&flow_voltage=" + String(readFlowVoltage, 6) +
                   "&flow_rate=" + String(readFlowRate, 6) +
                   "&flow_setpoint=" + String(targetFlowSetpoint, 6) + 
                   "&valve1State=" + String(valve1State) +
                   "&valve2State=" + String(valve2State) +
                   "&valveFlowState=" + String(valveFlowState);
      http.begin(url);
      http.GET();
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
            Serial.println("Received flowmeter command from Hub: " + commandPayload);
            processReceivedData(commandPayload);
          }
        }
        httpCmd.end();
      }
    }
    vTaskDelay(pdMS_TO_TICKS(500)); 
  }
}

void wifiTask(void *parameter) {
  // (Mantido igual ao seu código original)
  for (;;) {
    if (WiFi.status() != WL_CONNECTED) {
      if (millis() - lastWiFiCheckTime >= wifiCheckInterval) {
        int n = WiFi.scanComplete();
        if (n == -1) { 
        } else if (n == -2) { 
          Serial.println("WiFi scan failed. Rescanning...");
          WiFi.scanNetworks(true); 
        } else if (n >= 0) { 
          bool targetFound = false;
          Serial.printf("WiFi scan: %d networks.\n", n);
          for (int i = 0; i < n; i++) {
            String ssid = WiFi.SSID(i);
            if (ssid == "ModuloTECNAL_1" || ssid == "ModuloTECNAL_2") {
              currentSSID = ssid;
              currentPassword = ssid; 
              targetFound = true;
              break;
            }
          }
          WiFi.scanDelete(); 
          if (targetFound) {
            Serial.println("Found Hub: " + currentSSID + ". Connecting...");
            WiFi.begin(currentSSID.c_str(), currentPassword.c_str());
          } else {
            Serial.println("Hub not found. Rescanning.");
            WiFi.scanNetworks(true); 
          }
        }
        lastWiFiCheckTime = millis();
      }
    }
    vTaskDelay(pdMS_TO_TICKS(1000));
  }
}

void readSerialData() {
  if (SerialBT.available() || Serial.available()) {
    String data;
    if (SerialBT.available())
      data = SerialBT.readStringUntil('\n');
    else
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
        
        if (key == "v_Flow") digitalWrite(VALVE_FLOW_PIN, valveFlowState = value.toInt());
        else if (key == "v1") digitalWrite(VALVE1_PIN, valve1State = value.toInt());
        else if (key == "v2") digitalWrite(VALVE2_PIN, valve2State = value.toInt());
        
        // --- MODIFICADO: Lógica de Setpoint ---
        else if (key == "flow_setpoint") {
            // 1. Atualiza o que o usuário QUER
            targetFlowSetpoint = value.toFloat();
            
            // 2. Reseta o setpoint interno para o valor do usuário (início rápido)
            // Isso garante que ele vá direto para a tensão "normal" calibrada
            flowSetpoint = targetFlowSetpoint;
            writeFlowSetpointToDAC(flowSetpoint);
            
            // 3. Reseta o timer de controle para esperar 10s antes da primeira correção
            lastControlUpdate = millis();
        }
        // --------------------------------------

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
  // Usa variável local para não confundir nomes
  uint16_t dacValue = (flowSetpointVal / maxFlowRate) * 4095;
  dacValue = constrain(dacValue, 0, 4095);
  // Serial.print("Setting DAC value: "); Serial.println(dacValue);
  mcp.setVoltage(dacValue, false);
}

void readAndProcessADC() {
  noInterrupts();
  new_data_available = false;
  interrupts();
  int16_t raw = ads.getLastConversionResults();
  float newVolts = ads.computeVolts(raw);
  float filtered = lowPassFilter(newVolts, 0.25f);
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
    EEPROM.put(0, calParams);
    EEPROM.commit();
    Serial.println("Calibration parameters not found, using defaults.");
  } else {
    Serial.println("Calibration parameters loaded from EEPROM.");
  }
  k1 = calParams.k1; f1 = calParams.f1; c1 = calParams.c1;
  k2 = calParams.k2; f2 = calParams.f2; c2 = calParams.c2;
}
void saveParameters() {
  EEPROM.put(0, calParams);
  EEPROM.commit();
  Serial.println("Calibration parameters updated and saved to EEPROM.");
}