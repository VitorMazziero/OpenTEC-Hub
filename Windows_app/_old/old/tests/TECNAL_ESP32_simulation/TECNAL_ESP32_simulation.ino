/*************************************************************
 * SensorHub ESP32-S3 Code (Revised – No ArduinoJson)
 * 
 * - WiFi SoftAP: "ModuloTECNAL" (password "ModuloTECNAL")
 * - HTTP server (with WebSocket) for command reception and sensor data reading.
 * - BLE UART service ("Modulo_TECNAL")
 * - Supports two new HTTP endpoints for Flowmeter communication:
 *      • /flowData    : to receive flowmeter data (HTTP GET with query parameters)
 *      • /flowCommand : for the flowmeter to poll for pending commands
 * - Also continues to receive distance sensor updates via /distance.
 *************************************************************/
#include <Arduino.h>
#include <WiFi.h>
#include <ESPAsyncWebServer.h>
#include <AsyncTCP.h>
#include <HTTPClient.h>
#include <NimBLEDevice.h>
#include <vector>

// ============ USER SETTINGS =============
// WiFi AP credentials (for SensorHub)
#define WIFI_SSID       "ModuloTECNAL"
#define WIFI_PASSWORD   "ModuloTECNAL"

// Buffer size for temporary JSON strings (if needed)
#define JSON_BUFFER_LEN 256  

// BLE service and characteristic UUIDs (Nordic UART Service)
#define SERVICE_UUID            "6E400001-B5A3-F393-E0A9-E50E24DCCA9E"
#define CHARACTERISTIC_RX_UUID  "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"  // Write from client
#define CHARACTERISTIC_TX_UUID  "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"  // Notify to client

// ============ GLOBAL OBJECTS ============

// --- BLE Objects ---
NimBLEServer*           pServer           = nullptr;
NimBLECharacteristic*   pTxCharacteristic = nullptr;
NimBLECharacteristic*   pRxCharacteristic = nullptr;
bool                    deviceConnected   = false;

// --- HTTP/AsyncWebServer Objects ---
AsyncWebServer server(80);
AsyncWebSocket ws("/ws");  // WebSocket endpoint for broadcast
String lastSensorJson = ""; // Latest sensor JSON

// ----- Global variable to hold pending flowmeter command (as JSON string) -----
String pendingFlowmeterCommand = "";

// ============ UART CONFIG ============
#define SENSOR_RX_PIN 16  // ESP32-S3 RX pin for sensor (sensor TX -> ESP32-S3 RX)
#define SENSOR_TX_PIN 17  // ESP32-S3 TX pin for sensor (sensor RX -> ESP32-S3 TX)
HardwareSerial sensorSerial(2); // Use UART2 for sensor

// ============ GLOBAL VARIABLES ============
float tempReference = 0.0f;
float pHReference   = 0.0f;
float pHError       = 0.0f;
int   pHOperation   = 0;
int   pHMix         = 0;
int   pHIntensity   = 0;
int   motorRPM      = 0;  // 0 means OFF

int nutriOperation  = 0;
int nutriMix        = 0;
int nutriOpCycle    = 0;
int nutriMixCycle   = 0;
int nutriIntensity  = 0;

int antifoamOperation = 0;
int antifoamMix       = 0;
int antifoamIntensity = 0;

int pressureReference = 0;

bool oxyOn      = false;  // Oxygen sensor enabled if nonzero
bool tempOn     = false;  // Temperature reading ON if tempReference ≠ 0
bool phOn       = false;  // pH reading ON if pHReference ≠ 0
bool nutrientOn = false;  // Nutrient parameters active if any nutrient value is set
bool antifoamOn = false;  // Antifoam active if intensity > 0
bool pressureOn = false;  // Pressure active if reference > 0

// Operating mode flag:
// false = custom mode (JSON commands + periodic sensor reads)
// true  = bypass mode (simply relay commands)
bool bypassMode = false;

unsigned long dataDelay      = 1000;  
unsigned long lastDataMillis = 0;

// Global buffer for sensor input from UART:
String sensorBuffer = "";

// Flowmeter variables – updated when the flowmeter board sends its data via HTTP GET:
bool flowmeterCommOn = false;
float flowmeterTime = 0.0;
float flowmeterVoltage = 0.0;
float flowmeterRate = 0.0;
float flowmeterSetpoint = 0.0;
int   flowmeterValve1 = 0;
int   flowmeterValve2 = 0;

// --- Distance Sensor variables ---
// Updated when the distance sensor board sends an HTTP GET to /distance.
bool distanceSensorCommOn = false;
float distanceSensorTime = 0.0;
float distanceSensorValue = 0.0;

// ============ HELPER FUNCTION FOR MANUAL JSON PARSING ============
String getValueFromJson(const String &json, const String &key) {
  // Look for the key in the form: "key"
  String searchKey = "\"" + key + "\"";
  int keyPos = json.indexOf(searchKey);
  if (keyPos == -1) return "";
  // Find the colon after the key
  int colonPos = json.indexOf(":", keyPos);
  if (colonPos == -1) return "";
  // Skip spaces after the colon
  int start = colonPos + 1;
  while (start < json.length() && isspace(json.charAt(start))) {
    start++;
  }
  // Check if the value is enclosed in quotes
  char firstChar = json.charAt(start);
  int end = -1;
  if (firstChar == '\"') {
    // String value: skip the first quote
    start++;
    end = json.indexOf("\"", start);
    if (end == -1) return "";
  } else {
    // Numeric or boolean value: end at comma or closing brace
    end = json.indexOf(",", start);
    if (end == -1) {
      end = json.indexOf("}", start);
      if (end == -1) end = json.length();
    }
  }
  String value = json.substring(start, end);
  value.trim();
  return value;
}

// ============ FUNCTION DECLARATIONS ============
void handleUSBCommands();
void handleSensorData();
void processCommandData(const String &data); // Process incoming command (JSON or bypass)
void processJsonCommand(const String &json);
void readAndBroadcastSensorData();

// Device command functions
void setMotor(int rpm);
void setTemperature(float t);
void setPH(float pH, float err, int op, int mix, int intensity);
void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity);
void setAntifoam(int op, int mix, int intensity);
void setPressure(int ref);
String sendSensorCommand(const String &cmd, bool readResponse);
String removeDecimal(const String &s);

// BLE helper functions
void blePrint(const String &data);
void blePrintln(const String &data);

// NEW: updateParametersCycle() – sends one command per cycle in round-robin order
void updateParametersCycle();

// ============ BLE CALLBACK CLASSES ============
class MyServerCallbacks : public NimBLEServerCallbacks {
  void onConnect(NimBLEServer* pServer){
    deviceConnected = true;
    Serial.println("BLE client connected");
  }
  void onDisconnect(NimBLEServer* pServer){
    deviceConnected = false;
    Serial.println("BLE client disconnected");
    NimBLEDevice::getAdvertising()->start();  // Restart advertising
  }
};

class MyCallbacks : public NimBLECharacteristicCallbacks {
  void onWrite(NimBLECharacteristic* pCharacteristic, NimBLEConnInfo& connInfo){
    std::string rxValue = pCharacteristic->getValue();
    if (!rxValue.empty()) {
      String data = String(rxValue.c_str());
      Serial.print("Received via BLE: ");
      Serial.println(data);
      processCommandData(data);
    }
  }
};

// --- BLE helper implementations ---
void blePrint(const String &data) {
  if (pTxCharacteristic != nullptr && deviceConnected) {
    pTxCharacteristic->setValue(data.c_str());
    pTxCharacteristic->notify();
  }
}
void blePrintln(const String &data) {
  blePrint(data + "\n");
}

// ============ SETUP ============
void setup() {
  Serial.begin(115200);
  delay(1500);
  Serial.println("ESP32-S3 starting up...");

  // Initialize BLE
  NimBLEDevice::init("Modulo_TECNAL");
  pServer = NimBLEDevice::createServer();
  pServer->setCallbacks(new MyServerCallbacks());
  NimBLEService *pService = pServer->createService(SERVICE_UUID);

  pTxCharacteristic = pService->createCharacteristic(
                        CHARACTERISTIC_TX_UUID,
                        NIMBLE_PROPERTY::NOTIFY
                      );
  pRxCharacteristic = pService->createCharacteristic(
                        CHARACTERISTIC_RX_UUID,
                        NIMBLE_PROPERTY::WRITE
                      );
  pRxCharacteristic->setCallbacks(new MyCallbacks());
  pService->start();
  NimBLEAdvertising* pAdvertising = NimBLEDevice::getAdvertising();
  pAdvertising->addServiceUUID(SERVICE_UUID);
  pAdvertising->start();
  Serial.println("BLE advertising started. Device name: Modulo_TECNAL");

  // Initialize WiFi in AP mode
  WiFi.mode(WIFI_MODE_AP);
  WiFi.softAP(WIFI_SSID, WIFI_PASSWORD);
  Serial.println();
  Serial.print("WiFi SoftAP started. SSID: ");
  Serial.println(WIFI_SSID);
  Serial.print("Password: ");
  Serial.println(WIFI_PASSWORD);
  Serial.print("AP IP address: ");
  Serial.println(WiFi.softAPIP());

  // --- Initialize Async HTTP Server & WebSocket ---
  ws.onEvent([](AsyncWebSocket *server, AsyncWebSocketClient *client, AwsEventType type,
                  void *arg, uint8_t *data, size_t len) {
    if(type == WS_EVT_CONNECT){
      Serial.printf("WebSocket client connected: %u\n", client->id());
    } else if(type == WS_EVT_DISCONNECT){
      Serial.printf("WebSocket client disconnected: %u\n", client->id());
    }
  });
  server.addHandler(&ws);

  // HTTP POST endpoint to receive commands
  server.on("/command", HTTP_POST, [](AsyncWebServerRequest *request) {},
    NULL,
    [](AsyncWebServerRequest *request, uint8_t *data, size_t len, size_t index, size_t total) {
        if (len) {
            String message = String((char *)data).substring(0, len);
            // Extract JSON content manually
            int startIdx = message.indexOf('{');
            int endIdx = message.lastIndexOf('}');
            if (startIdx != -1 && endIdx != -1 && startIdx < endIdx) {
                String jsonContent = message.substring(startIdx, endIdx + 1);
                processCommandData(jsonContent);
                jsonContent.trim();
                request->send(200, "text/plain", "Command received");
            } else {
                request->send(400, "text/plain", "Invalid JSON format");
            }
        } else {
            request->send(400, "text/plain", "No body received");
        }
    });

  // HTTP GET endpoint to return latest sensor data (JSON)
  server.on("/readData", HTTP_GET, [](AsyncWebServerRequest *request){
      request->send(200, "application/json", lastSensorJson);
  });
  
  // ----------------------------------------------------------------
  // HTTP GET endpoint for distance sensor updates.
  // The Distance Sensor board (now a client) sends its sensor data via query parameters.
  // ----------------------------------------------------------------
  server.on("/distance", HTTP_GET, [](AsyncWebServerRequest *request) {
    if (request->hasParam("distance")) {
      String distanceParam = request->getParam("distance")->value();
      distanceSensorValue = distanceParam.toFloat();
      
      // Optionally, also receive a timestamp
      if (request->hasParam("time")) {
        distanceSensorTime = request->getParam("time")->value().toFloat();
      }
      
      distanceSensorCommOn = true;
      
      // Broadcast the update via WebSocket in JSON format (built manually)
      String jsonResponse = "{";
      jsonResponse += "\"Distance\":" + String(distanceSensorValue, 2) + ",";
      jsonResponse += "\"Time\":" + String(distanceSensorTime, 1);
      jsonResponse += "}";
      ws.textAll(jsonResponse);
      
      request->send(200, "text/plain", "Distance sensor update received");
    } else {
      request->send(400, "text/plain", "Missing 'distance' parameter");
    }
  });

  // ----------------------------------------------------------------
  // NEW: HTTP GET endpoint for flowmeter data.
  // The Flowmeter board (as a client) sends its sensor data via query parameters.
  // Expected parameters: seconds, flow_voltage, flow_rate, flow_setpoint, valve1State, valve2State
  // ----------------------------------------------------------------
  server.on("/flowData", HTTP_GET, [](AsyncWebServerRequest *request) {
    if (request->hasParam("seconds") &&
        request->hasParam("flow_voltage") &&
        request->hasParam("flow_rate") &&
        request->hasParam("flow_setpoint") &&
        request->hasParam("valve1State") &&
        request->hasParam("valve2State")) {
      
      flowmeterTime = request->getParam("seconds")->value().toFloat();
      flowmeterVoltage = request->getParam("flow_voltage")->value().toFloat();
      flowmeterRate = request->getParam("flow_rate")->value().toFloat();
      flowmeterSetpoint = request->getParam("flow_setpoint")->value().toFloat();
      flowmeterValve1 = request->getParam("valve1State")->value().toInt();
      flowmeterValve2 = request->getParam("valve2State")->value().toInt();
      
      flowmeterCommOn = true;
      
      // Build JSON response manually for WebSocket broadcast
      String jsonResponse = "{";
      jsonResponse += "\"FlowTime\":" + String(flowmeterTime, 3) + ",";
      jsonResponse += "\"FlowVoltage\":" + String(flowmeterVoltage, 6) + ",";
      jsonResponse += "\"FlowRate\":" + String(flowmeterRate, 6) + ",";
      jsonResponse += "\"FlowSetpoint\":" + String(flowerSetpoint, 6) + ",";
      jsonResponse += "\"Valve1\":" + String(flowmeterValve1) + ",";
      jsonResponse += "\"Valve2\":" + String(flowmeterValve2);
      jsonResponse += "}";
      ws.textAll(jsonResponse);
      
      request->send(200, "text/plain", "Flowmeter data received");
    } else {
      request->send(400, "text/plain", "Missing one or more flowmeter parameters");
    }
  });

  // ----------------------------------------------------------------
  // NEW: HTTP GET endpoint for flowmeter commands.
  // The Flowmeter board polls this endpoint to retrieve any pending command.
  // If a command exists, it is sent (and then cleared); otherwise an empty JSON object is returned.
  // ----------------------------------------------------------------
  server.on("/flowCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
    if (pendingFlowmeterCommand.length() > 0) {
      String commandToSend = pendingFlowmeterCommand;
      pendingFlowmeterCommand = ""; // clear after sending
      request->send(200, "application/json", commandToSend);
    } else {
      request->send(200, "application/json", "{}");
    }
  });

  server.begin();
  Serial.println("HTTP server started.");

  // Initialize UART for sensor/device
  sensorSerial.begin(9600, SERIAL_8N1, SENSOR_RX_PIN, SENSOR_TX_PIN);
  Serial.println("Sensor serial started at 9600 baud.");
}

// ============ LOOP ============
void loop() {
  // Process USB serial commands.
  handleUSBCommands();
  // Process incoming UART data from sensor/device.
  handleSensorData();

  unsigned long now = millis();
  if (!bypassMode && (now - lastDataMillis) >= dataDelay) {
    lastDataMillis = now;
    // In custom mode, update sensor readings via UART and broadcast them.
    readAndBroadcastSensorData();
  }
  // Update cyclic parameters every 2 seconds.
  static unsigned long lastCyclicUpdate = 0;
  if (now - lastCyclicUpdate >= 2000) {
    lastCyclicUpdate = now;
    updateParametersCycle();
  }
  // Let AsyncWebServer and WebSocket do their work (non-blocking)
  delay(5);
}

// ------------------------------------------------------------------
// handleUSBCommands(): Process USB serial commands.
// ------------------------------------------------------------------
void handleUSBCommands() {
  String data = "";
  while (Serial.available() > 0) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') break;
    data += c;
  }
  data.trim();
  if (data.length() > 0) {
    // Optionally, broadcast USB input to all WebSocket clients:
    ws.textAll(data);
    processCommandData(data);
  }
}

// ------------------------------------------------------------------
// handleSensorData(): Process incoming UART data from sensor/device.
// ------------------------------------------------------------------
void handleSensorData() {
  while (sensorSerial.available() > 0) {
    char c = sensorSerial.read();
    sensorBuffer += c;
    if (c == '\n' || c == '\r') {
      sensorBuffer.trim();
      if (sensorBuffer.length() > 0) {
        ws.textAll(sensorBuffer);
        blePrintln(sensorBuffer);
        Serial.println(sensorBuffer);
      }
      sensorBuffer = "";
    }
  }
}

// ------------------------------------------------------------------
// processCommandData(): Process incoming command (JSON or bypass).
// ------------------------------------------------------------------
void processCommandData(const String &data) {
  if (data.length() == 0) {
    return; // Ignore empty commands
  }
  if (data.startsWith("{") && data.endsWith("}")) {
    if (bypassMode) { bypassMode = false; }
    processJsonCommand(data);
  } else {
    if (!bypassMode) { bypassMode = true; }
    String response = sendSensorCommand(data, true);
    ws.textAll(response);
    blePrintln(response);
    Serial.println(response);
  }
}

// ------------------------------------------------------------------
// processJsonCommand(): Manually parse JSON command and update parameters.
// ------------------------------------------------------------------
void processJsonCommand(const String &json) {
  String value;
  // Communication Control for Flowmeter
  if (json.indexOf("\"flowmeterComm\"") != -1) {
    value = getValueFromJson(json, "flowmeterComm");
    int commValue = value.toInt();
    flowmeterCommOn = (commValue != 0);
    Serial.println(String("Flowmeter communication ") + (flowmeterCommOn ? "enabled" : "disabled"));
  }
  // Communication Control for Flowmeter
  if (json.indexOf("\"distanceSensorComm\"") != -1) {
    value = getValueFromJson(json, "distanceSensorComm");
    int commValue = value.toInt();
    flowmeterCommOn = (commValue != 0);
    Serial.println(String("Flowmeter communication ") + (flowmeterCommOn ? "enabled" : "disabled"));
  }
  // Data delay control
  if (json.indexOf("\"dataDelay\"") != -1) {
    value = getValueFromJson(json, "dataDelay");
    int newDelay = value.toInt();
    dataDelay = (newDelay < 100) ? 100 : newDelay;
  }
  // Global Hub Sensor Control Commands
  if (json.indexOf("\"motorSetpoint\"") != -1) {
    value = getValueFromJson(json, "motorSetpoint");
    setMotor(value.toInt());
  }
  if (json.indexOf("\"tempSetpoint\"") != -1) {
    value = getValueFromJson(json, "tempSetpoint");
    setTemperature(value.toFloat());
  }
  if (json.indexOf("\"oxygenMonitor\"") != -1) {
    value = getValueFromJson(json, "oxygenMonitor");
    oxyOn = (value.toInt() != 0);
  }
  bool pHUpdated = false;
  if (json.indexOf("\"pHSetpoint\"") != -1) {
    value = getValueFromJson(json, "pHSetpoint");
    pHReference = value.toFloat();
    pHUpdated = true;
  }
  if (json.indexOf("\"pHError\"") != -1) {
    value = getValueFromJson(json, "pHError");
    pHError = value.toFloat();
    pHUpdated = true;
  }
  if (json.indexOf("\"pHOperation\"") != -1) {
    value = getValueFromJson(json, "pHOperation");
    pHOperation = value.toInt();
    pHUpdated = true;
  }
  if (json.indexOf("\"pHMix\"") != -1) {
    value = getValueFromJson(json, "pHMix");
    pHMix = value.toInt();
    pHUpdated = true;
  }
  if (json.indexOf("\"pHIntensity\"") != -1) {
    value = getValueFromJson(json, "pHIntensity");
    pHIntensity = value.toInt();
    pHUpdated = true;
  }
  if (pHUpdated) {
    setPH(pHReference, pHError, pHOperation, pHMix, pHIntensity);
    Serial.println("pH parameters updated");
  }
  bool nutriUpdated = false;
  if (json.indexOf("\"nutriOperation\"") != -1) {
    value = getValueFromJson(json, "nutriOperation");
    nutriOperation = value.toInt();
    nutriUpdated = true;
  }
  if (json.indexOf("\"nutriMix\"") != -1) {
    value = getValueFromJson(json, "nutriMix");
    nutriMix = value.toInt();
    nutriUpdated = true;
  }
  if (json.indexOf("\"nutriOpCycle\"") != -1) {
    value = getValueFromJson(json, "nutriOpCycle");
    nutriOpCycle = value.toInt();
    nutriUpdated = true;
  }
  if (json.indexOf("\"nutriMixCycle\"") != -1) {
    value = getValueFromJson(json, "nutriMixCycle");
    nutriMixCycle = value.toInt();
    nutriUpdated = true;
  }
  if (json.indexOf("\"nutriIntensity\"") != -1) {
    value = getValueFromJson(json, "nutriIntensity");
    nutriIntensity = value.toInt();
    nutriUpdated = true;
  }
  if (nutriUpdated) {
    setNutrient(nutriOperation, nutriMix, nutriOpCycle, nutriMixCycle, nutriIntensity);
    Serial.println("Nutrient parameters updated");
  }
  bool antifoamUpdated = false;
  if (json.indexOf("\"antifoamOperation\"") != -1) {
    value = getValueFromJson(json, "antifoamOperation");
    antifoamOperation = value.toInt();
    antifoamUpdated = true;
  }
  if (json.indexOf("\"antifoamMix\"") != -1) {
    value = getValueFromJson(json, "antifoamMix");
    antifoamMix = value.toInt();
    antifoamUpdated = true;
  }
  if (json.indexOf("\"antifoamIntensity\"") != -1) {
    value = getValueFromJson(json, "antifoamIntensity");
    antifoamIntensity = value.toInt();
    antifoamUpdated = true;
  }
  if (antifoamUpdated) {
    setAntifoam(antifoamOperation, antifoamMix, antifoamIntensity);
    Serial.println("Antifoam parameters updated");
  }
  if (json.indexOf("\"pressureReference\"") != -1) {
    value = getValueFromJson(json, "pressureReference");
    pressureReference = value.toInt();
    setPressure(pressureReference);
    Serial.println("Pressure updated");
  }
  // Flowmeter-specific Commands:
  // Build a JSON string manually for the flowmeter command.
  String flowmeterCommand = "";
  bool flowmeterCmdFound = false;
  if (json.indexOf("\"flowSetpoint\"") != -1) {
    flowmeterCommand += "\"flow_setpoint\":" + getValueFromJson(json, "flowSetpoint");
    flowmeterSetpoint = getValueFromJson(json, "flowSetpoint")
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"maxFlow\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"max_flow\":" + getValueFromJson(json, "maxFlow");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"k1\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"k1\":" + getValueFromJson(json, "k1");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"f1\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"f1\":" + getValueFromJson(json, "f1");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"c1\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"c1\":" + getValueFromJson(json, "c1");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"k2\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"k2\":" + getValueFromJson(json, "k2");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"f2\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"f2\":" + getValueFromJson(json, "f2");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"c2\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"c2\":" + getValueFromJson(json, "c2");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"valve_1\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"v1_aux\":" + getValueFromJson(json, "valve_1");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"valve_2\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"v2_opt\":" + getValueFromJson(json, "valve_2");
    flowmeterCmdFound = true;
  }
  if (flowmeterCmdFound && flowmeterCommOn) {
    pendingFlowmeterCommand = "{" + flowmeterCommand + "}";
  }
}

// ------------------------------------------------------------------
// readAndBroadcastSensorData(): Read local sensor values and broadcast JSON.
// ------------------------------------------------------------------
void readAndBroadcastSensorData() {
  float temperatureVal = -1.0;
  if (tempOn) {
    String temperatureResp = sendSensorCommand("b", true);
    temperatureVal = temperatureResp.toFloat();
  }
  float pHVal = -1.0;
  if (phOn) {
    String pHResp = sendSensorCommand("d", true);
    pHVal = pHResp.toFloat();
  }
  
  // Simulate oxygen value if oxygen monitoring is enabled
  float oxyVal = -1.0;
  if (oxyOn) {
    static unsigned long oxySimStart = 0;
    if (oxySimStart == 0) {
      oxySimStart = millis();
    }
    float t = (millis() - oxySimStart) / 1000.0;  // time in seconds
    if (t <= 95.0) {
      // Decrease linearly from 100 to 30 over 95 seconds
      oxyVal = 100.0 - (70.0 / 95.0) * t;
    } else if (t <= 190.0) {
      // Increase linearly from 30 to 50 between 95s and 190s
      oxyVal = 30.0 + (20.0 / 95.0) * (t - 95.0);
    } else {
      // Stabilize at 50 after 190 seconds
      oxyVal = 50.0;
    }
  }
  
  // Simulate motor response: the sensor value equals the motor setpoint.
  float motorVal = motorRPM;
  
  // Simulate flowmeter response: the sensor reading equals the flowmeter setpoint.
  float flowRateSimulated = flowmeterSetpoint;
  
  float pressureVal = -1.0;
  if (pressureOn) {
    String pressureResp = sendSensorCommand("c", true);
    pressureVal = pressureResp.toFloat();
  }
  float antifoamVal = -1.0;
  if (antifoamOn) {
    String antifoamResp = sendSensorCommand("e", true);
    antifoamVal = antifoamResp.toFloat();
  }
  float timeSec = millis() / 1000.0;
  
  // Build JSON string manually
  String jsonResponse = "{";
  jsonResponse += "\"Time\":" + String(timeSec, 1);
  jsonResponse += ",\"Tempval\":" + String(temperatureVal, 2);
  jsonResponse += ",\"pHval\":" + String(pHVal, 2);
  jsonResponse += ",\"Oxyval\":" + String(oxyVal, 1);
  jsonResponse += ",\"MotorRPM\":" + String(motorVal, 0);
  if (flowmeterCommOn) {
    jsonResponse += ",\"FlowVoltage\":" + String(flowmeterVoltage, 6);
    jsonResponse += ",\"FlowRate\":" + String(flowRateSimulated, 6);
    jsonResponse += ",\"FlowSetpoint\":" + String(flowmeterSetpoint, 6);
    jsonResponse += ",\"Valve1\":" + String(flowmeterValve1);
    jsonResponse += ",\"Valve2\":" + String(flowmeterValve2);
  }
  jsonResponse += ",\"Antifoam\":" + String(antifoamVal, 0);
  jsonResponse += ",\"Pressure\":" + String(pressureVal, 1);
  if (distanceSensorCommOn) {
    jsonResponse += ",\"Distance\":" + String(distanceSensorValue, 2);
  }
  jsonResponse += "}";
  
  lastSensorJson = jsonResponse;
  ws.textAll(jsonResponse);
  blePrintln(jsonResponse);
  Serial.println(jsonResponse);
}

// ------------------------------------------------------------------
// setMotor(): Send motor control command via sensor command protocol.
// ------------------------------------------------------------------
void setMotor(int rpm) {
  if (rpm < 0) { rpm = 0; }
  if (rpm > 1000) { rpm = 1000; }
  motorRPM = rpm;
  if (rpm == 0) {
    sendSensorCommand("0V", false);
    sendSensorCommand("0A", false);
  } else {
    sendSensorCommand("1V", false);
    sendSensorCommand(String(motorRPM) + "A", false);
  }
}

// ------------------------------------------------------------------
// setTemperature(): Configure temperature sensor command.
// ------------------------------------------------------------------
void setTemperature(float temp) {
  tempReference = temp;
  if (fabs(tempReference) < 0.001) {
    tempOn = false;
    sendSensorCommand("100B", false);
  } else {
    tempOn = true;
    String tStr = String(tempReference, 1);
    String noDot = removeDecimal(tStr);
    sendSensorCommand(noDot + "B", false);
  }
}

// ------------------------------------------------------------------
// setPH(): Configure pH sensor commands.
// ------------------------------------------------------------------
void setPH(float pH, float err, int op, int mix, int intensity) {
  if (pH < 0.0f || pH > 14.0f) { pH = 7.00f; }
  if (err <= 0.0f || err >= 2.0f) { err = 0.17f; }
  if (op < 1 || op >= 1000) { op = 5; }
  if (mix <= 0 || mix >= 1000) { mix = 10; }
  if (intensity <= 0 || intensity >= 100) { intensity = 99; }
  pHReference = pH;
  pHError = err;
  pHOperation = op;
  pHMix = mix;
  pHIntensity = intensity;
  if (fabs(pHReference) < 0.001) {
    phOn = false;
    sendSensorCommand("0F", false);
  } else {
    phOn = true;
    String phStr = String(pHReference, 2);
    String noDot = removeDecimal(phStr);
    sendSensorCommand(noDot + "D", false);
    String cmdErr = removeDecimal(String(pHError, 2)) + "E";
    String cmdOp = String(pHOperation) + "G";
    String cmdMix = String(pHMix) + "H";
    String cmdInt = String(pHIntensity) + "F";
    sendSensorCommand(cmdErr, false);
    sendSensorCommand(cmdOp, false);
    sendSensorCommand(cmdMix, false);
    sendSensorCommand(cmdInt, false);
  }
}

// ------------------------------------------------------------------
// setNutrient(): Configure nutrient pump commands.
// ------------------------------------------------------------------
void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity) {
  if (op < 1 || op >= 1000) { op = 999; }
  if (mix <= 0 || mix >= 1000) { mix = 1; }
  if (opCycle < 1 || opCycle > 500) { opCycle = 500; }
  if (mixCycle < 1 || mixCycle >= 1000) { mixCycle = 1; }
  if (intensity < 0 || intensity >= 100) { intensity = 99; }
  nutriOperation = op;
  nutriMix = mix;
  nutriOpCycle = opCycle;
  nutriMixCycle = mixCycle;
  nutriIntensity = intensity;
  nutrientOn = (nutriIntensity != 0);
  String cmdIntensity = String(nutriIntensity) + "0M";
  String cmdOp = String(nutriOperation) + "N";
  String cmdMix = String(nutriMix) + "O";
  String cmdOpCycle = String(nutriOpCycle) + "P";
  String cmdMixCycle = String(nutriMixCycle) + "Q";
  if (nutriIntensity == 0) {
    sendSensorCommand("0M", false);
  } else {
    sendSensorCommand(cmdIntensity, false);
  }
  sendSensorCommand(cmdOp, false);
  sendSensorCommand(cmdMix, false);
  sendSensorCommand(cmdOpCycle, false);
  sendSensorCommand(cmdMixCycle, false);
}

// ------------------------------------------------------------------
// setAntifoam(): Configure antifoam sensor commands.
// ------------------------------------------------------------------
void setAntifoam(int op, int mix, int intensity) {
  if (op < 1 || op >= 1000) { op = 999; }
  if (mix <= 0 || mix >= 1000) { mix = 1; }
  if (intensity < 0 || intensity >= 100) { intensity = 99; }
  antifoamOperation = op;
  antifoamMix = mix;
  antifoamIntensity = intensity;
  if (antifoamIntensity == 0) {
    antifoamOn = false;
    sendSensorCommand("0I", false);
  } else {
    antifoamOn = true;
    String cmdIntensity = String(antifoamIntensity) + "0I";
    sendSensorCommand(cmdIntensity, false);
  }
  sendSensorCommand(String(antifoamOperation) + "J", false);
  sendSensorCommand(String(antifoamMix) + "L", false);
}

// ------------------------------------------------------------------
// setPressure(): Configure pressure sensor command.
// ------------------------------------------------------------------
void setPressure(int ref) {
  if (ref < 0 || ref > 380) { ref = 100; }
  pressureReference = ref;
  String cmdPressure = String(pressureReference) + "C";
  if (pressureReference == 0) {
    pressureOn = false;
    sendSensorCommand("0C", false);
  } else {
    pressureOn = true;
    sendSensorCommand(cmdPressure, false);
  }
}

// ------------------------------------------------------------------
// sendSensorCommand(): Send command via UART to sensor/device.
// ------------------------------------------------------------------
String sendSensorCommand(const String &cmd, bool readResponse) {
  sensorSerial.print(cmd);
  sensorSerial.print("\r");
  if (readResponse) {
    sensorSerial.setTimeout(50);
    String response = sensorSerial.readStringUntil('\r');
    response.trim();
    delay(10);
    return response;
  }
  return "";
}

// ------------------------------------------------------------------
// removeDecimal(): Remove the decimal point from a numeric string.
// ------------------------------------------------------------------
String removeDecimal(const String &s) {
  String ret = s;
  ret.replace(".", "");
  return ret;
}

// ------------------------------------------------------------------
// updateParametersCycle():
//   Gather one command per active parameter into a vector and send them round-robin.
// ------------------------------------------------------------------
void updateParametersCycle() {
  std::vector<String> cmds;
  if (motorRPM > 0) {
    cmds.push_back("1V");
    cmds.push_back(String(motorRPM) + "A");
  }
  if (tempOn) {
    String tStr = String(tempReference, 1);
    String noDot = removeDecimal(tStr);
    cmds.push_back(noDot + "B");
  }
  if (phOn) {
    String phStr = String(pHReference, 2);
    cmds.push_back(removeDecimal(phStr) + "D");
    String errStr = String(pHError, 2);
    cmds.push_back(removeDecimal(errStr) + "E");
    cmds.push_back(String(pHOperation) + "G");
    cmds.push_back(String(pHMix) + "H");
    cmds.push_back(String(pHIntensity) + "F");
  }
  if (nutrientOn) {
    cmds.push_back(String(nutriIntensity) + "0M");
    cmds.push_back(String(nutriOperation) + "N");
    cmds.push_back(String(nutriMix) + "O");
    cmds.push_back(String(nutriOpCycle) + "P");
    cmds.push_back(String(nutriMixCycle) + "Q");
  }
  if (antifoamOn) {
    cmds.push_back(String(antifoamIntensity) + "0I");
    cmds.push_back(String(antifoamOperation) + "J");
    cmds.push_back(String(antifoamMix) + "L");
  }
  if (pressureOn) {
    cmds.push_back(String(pressureReference) + "C");
  }
  if (cmds.size() == 0) return;
  static size_t currentIndex = 0;
  currentIndex = currentIndex % cmds.size();
  String cmd = cmds[currentIndex];
  sendSensorCommand(cmd, false);
  Serial.println("Periodic update sent: " + cmd);
  currentIndex = (currentIndex + 1) % cmds.size();
}
