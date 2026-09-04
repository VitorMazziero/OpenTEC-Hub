/*************************************************************
 * Optimized ESP32-S3 code implementing:
 *  - WiFi SoftAP: "ModuloTECNAL" (password "ModuloTECNAL")
 *  - TCP server on port 23 (telnet-like)
 *  - Bluetooth Low Energy (BLE) UART service ("Modulo_TECNAL")
 *  - Two operating modes: Bypass mode and Custom JSON mode
 *  - Communication with a sensor/device via UART (pins 16 RX, 17 TX)
 *  - Periodic sensor readings broadcasted in JSON (custom mode)
 *  - Accepting commands via WiFi, BLE, and USB
 *************************************************************/

#include <Arduino.h>
#include <WiFi.h>
#include <WiFiClient.h>
#include <NimBLEDevice.h>
#include <ArduinoJson.h>   // Install ArduinoJson library!

// ============ USER SETTINGS =============
// WiFi AP credentials
#define WIFI_SSID       "ModuloTECNAL"
#define WIFI_PASSWORD   "ModuloTECNAL"
#define WIFI_PORT       23      // Telnet-like TCP port

// JSON buffer length (max bytes per incoming JSON line)
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

// --- WiFi Objects ---
WiFiServer  wifiServer(WIFI_PORT);
WiFiClient  wifiClient; // track a single connected client

// ============ UART CONFIG ============
// Define the pins for the sensor's UART communication.
#define SENSOR_RX_PIN 16  // ESP32-S3 RX pin for sensor (sensor TX -> ESP32-S3 RX)
#define SENSOR_TX_PIN 17  // ESP32-S3 TX pin for sensor (sensor RX -> ESP32-S3 TX)

// Use UART2 on the ESP32-S3 for sensor communication.
HardwareSerial sensorSerial(2);

// ============ GLOBAL VARIABLES ============
// Variables for setpoints & state (custom mode)
float tempReference = 0.0f;
float pHReference   = 0.0f;
float pHError       = 0.0f;
int   pHOperation   = 0;
int   pHMix         = 0;
int   pHIntensity   = 0;
int   motorRPM      = 0;  // 0 means OFF

// Nutrient control variables
int nutriOperation  = 0;
int nutriMix        = 0;
int nutriOpCycle    = 0;
int nutriMixCycle   = 0;
int nutriIntensity  = 0;

// Antifoam control variables
int antifoamOperation = 0;
int antifoamMix       = 0;
int antifoamIntensity = 0;

// Pressure control variable
int pressureReference = 0;

// Booleans to decide if a sensor reading should be requested:
bool oxyOn      = false;  // Oxygen sensor enabled if nonzero
bool tempOn     = false;  // Temperature reading ON if tempReference ≠ 0
bool phOn       = false;  // pH reading ON if pHReference ≠ 0
bool antifoamOn = false;  // Antifoam reading active if intensity > 0
bool pressureOn = false;  // Pressure reading active if reference > 0

// Operating mode flag:
// false = custom mode (JSON commands + periodic sensor reads)
// true  = bypass mode (simply relay commands)
bool bypassMode = false;

// The user-specified delay (ms) for sensor data reading (custom mode)
unsigned long dataDelay      = 1000;  
unsigned long lastDataMillis = 0;

// ============ FUNCTION DECLARATIONS ============
// Communication handlers
void handleWiFiClient();
void handleUSBCommands();
void handleSensorData();
void processCommandData(const String &data); // Process incoming command (JSON or bypass)

// Device command functions
void setMotor(int rpm);
void setTemperature(float t);
void setPH(float pH, float err, int op, int mix, int intensity);
void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity);
void setAntifoam(int op, int mix, int intensity);
void setPressure(int ref);
String sendSensorCommand(const String &cmd, bool readResponse);

// Sensor reading and broadcasting
void readAndBroadcastSensorData();

// JSON command processing using ArduinoJson
void processJsonCommand(const String &json);

// Utility functions
String removeDecimal(const String &s);

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

// Helper functions for BLE printing
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
  // Start Serial (USB) for debugging and command input
  Serial.begin(115200);
  delay(1500);
  Serial.println("ESP32-S3 starting up...");

  // --- Initialize BLE ---
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

  // --- Initialize WiFi in AP mode ---
  WiFi.mode(WIFI_MODE_AP);
  WiFi.softAP(WIFI_SSID, WIFI_PASSWORD);
  Serial.println();
  Serial.print("WiFi SoftAP started. SSID: ");
  Serial.println(WIFI_SSID);
  Serial.print("Password: ");
  Serial.println(WIFI_PASSWORD);
  Serial.print("AP IP address: ");
  Serial.println(WiFi.softAPIP());
  wifiServer.begin();
  Serial.print("WiFi server started on port ");
  Serial.println(WIFI_PORT);

  // --- Initialize UART for the sensor/device ---
  sensorSerial.begin(9600, SERIAL_8N1, SENSOR_RX_PIN, SENSOR_TX_PIN);
  Serial.println("Sensor serial started at 9600 baud.");
}

// ============ LOOP ============
void loop() {
  // 1) Process incoming data from the sensor/device (UART)
  handleSensorData();

  // 2) Process WiFi client commands (TCP)
  handleWiFiClient();

  // 3) Process USB (Serial) commands
  handleUSBCommands();

  // 4) In custom mode, periodically request sensor readings and broadcast them
  unsigned long now = millis();
  if (!bypassMode && (now - lastDataMillis) >= dataDelay) {
    lastDataMillis = now;
    readAndBroadcastSensorData();
  }
}

// ------------------------------------------------------------------
// handleSensorData():
// Read data arriving from the sensor/device via UART and relay it.
// ------------------------------------------------------------------
void handleSensorData() {
  while (sensorSerial.available() > 0) {
    String data = sensorSerial.readStringUntil('\n');
    data.trim();
    if (data.length() > 0) {
      if (wifiClient && wifiClient.connected()) {
        wifiClient.println(data);
      }
      blePrintln(data);
    }
  }
}

// ------------------------------------------------------------------
// handleWiFiClient():
// Check and process incoming commands from a WiFi client.
// ------------------------------------------------------------------
void handleWiFiClient() {
  if (!wifiClient || !wifiClient.connected()) {
    wifiClient = wifiServer.available();
  } else {
    while (wifiClient.available()) {
      String data = wifiClient.readStringUntil('\n');
      data.trim();
      if (data.length() > 0) {
        processCommandData(data);
      }
    }
  }
}

// ------------------------------------------------------------------
// handleUSBCommands():
// Read and process commands coming via USB Serial.
// ------------------------------------------------------------------
void handleUSBCommands() {
  String data = "";
  while (Serial.available() > 0) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') {
      break;
    }
    data += c;
  }
  data.trim();  // Remove any extraneous whitespace
  if (data.length() > 0) {
    if (wifiClient && wifiClient.connected()) {
        wifiClient.println(data);
      }
    processCommandData(data);
  }
}

// ------------------------------------------------------------------
// processCommandData():
// If the command is a JSON object, parse and update parameters.
// Otherwise, treat it as a bypass command.
// ------------------------------------------------------------------
void processCommandData(const String &data) {
  // Check if the command is in JSON format (starts with '{' and ends with '}')
  if (data.startsWith("{") && data.endsWith("}")) {
    // Exit bypass mode (if we were in it)
    if (bypassMode) {
      bypassMode = false;
      Serial.setTimeout(50);
    }
    processJsonCommand(data);
  } else {
    // If not JSON, treat as bypass command:
    if (!bypassMode) {
      bypassMode = true;
      Serial.setTimeout(100);
    }
    // Simply relay the command to the sensor and broadcast the response
    String response = sendSensorCommand(data, true);
    if (wifiClient && wifiClient.connected()) {
      wifiClient.println(response);
    }
    blePrintln(response);
    Serial.println(response);
  }
}

// ------------------------------------------------------------------
// processJsonCommand():
// Parse incoming JSON (using ArduinoJson) and update global parameters.
// ------------------------------------------------------------------
void processJsonCommand(const String &json) {
  DynamicJsonDocument doc(JSON_BUFFER_LEN);
  DeserializationError error = deserializeJson(doc, json);
  if (error) {
    Serial.print("JSON parse error: ");
    Serial.println(error.c_str());
    return;
  }

  // dataDelay (minimum 100 ms)
  if (doc.containsKey("dataDelay")) {
    int newDelay = doc["dataDelay"].as<int>();
    dataDelay = (newDelay < 100) ? 100 : newDelay;
  }

  // Motor control
  if (doc.containsKey("motorSetpoint")) {
    setMotor(doc["motorSetpoint"].as<int>());
  }

  // Temperature control
  if (doc.containsKey("tempSetpoint")) {
    setTemperature(doc["tempSetpoint"].as<float>());
  }

  // Oxygen monitoring
  if (doc.containsKey("oxygenMonitor")) {
    oxyOn = (doc["oxygenMonitor"].as<int>() != 0);
  }

  // pH parameters
  bool pHUpdated = false;
  if (doc.containsKey("pHSetpoint")) {
    pHReference = doc["pHSetpoint"].as<float>();
    pHUpdated = true;
  }
  if (doc.containsKey("pHError")) {
    pHError = doc["pHError"].as<float>();
    pHUpdated = true;
  }
  if (doc.containsKey("pHOperation")) {
    pHOperation = doc["pHOperation"].as<int>();
    pHUpdated = true;
  }
  if (doc.containsKey("pHMix")) {
    pHMix = doc["pHMix"].as<int>();
    pHUpdated = true;
  }
  if (doc.containsKey("pHIntensity")) {
    pHIntensity = doc["pHIntensity"].as<int>();
    pHUpdated = true;
  }
  if (pHUpdated) {
    setPH(pHReference, pHError, pHOperation, pHMix, pHIntensity);
    Serial.println("pH parameters updated");
  }

  // Nutrient parameters
  bool nutriUpdated = false;
  if (doc.containsKey("nutriOperation")) {
    nutriOperation = doc["nutriOperation"].as<int>();
    nutriUpdated = true;
  }
  if (doc.containsKey("nutriMix")) {
    nutriMix = doc["nutriMix"].as<int>();
    nutriUpdated = true;
  }
  if (doc.containsKey("nutriOpCycle")) {
    nutriOpCycle = doc["nutriOpCycle"].as<int>();
    nutriUpdated = true;
  }
  if (doc.containsKey("nutriMixCycle")) {
    nutriMixCycle = doc["nutriMixCycle"].as<int>();
    nutriUpdated = true;
  }
  if (doc.containsKey("nutriIntensity")) {
    nutriIntensity = doc["nutriIntensity"].as<int>();
    nutriUpdated = true;
  }
  if (nutriUpdated) {
    setNutrient(nutriOperation, nutriMix, nutriOpCycle, nutriMixCycle, nutriIntensity);
    Serial.println("Nutrient parameters updated");
  }

  // Antifoam parameters
  bool antifoamUpdated = false;
  if (doc.containsKey("antifoamOperation")) {
    antifoamOperation = doc["antifoamOperation"].as<int>();
    antifoamUpdated = true;
  }
  if (doc.containsKey("antifoamMix")) {
    antifoamMix = doc["antifoamMix"].as<int>();
    antifoamUpdated = true;
  }
  if (doc.containsKey("antifoamIntensity")) {
    antifoamIntensity = doc["antifoamIntensity"].as<int>();
    antifoamUpdated = true;
  }
  if (antifoamUpdated) {
    setAntifoam(antifoamOperation, antifoamMix, antifoamIntensity);
    Serial.println("Antifoam parameters updated");
  }

  // Pressure parameter
  if (doc.containsKey("pressureReference")) {
    pressureReference = doc["pressureReference"].as<int>();
    setPressure(pressureReference);
    Serial.println("Pressure updated");
  }
}

// ------------------------------------------------------------------
// readAndBroadcastSensorData():
// In custom mode, request enabled sensor readings and broadcast them.
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
  
  float oxyVal = -1.0;
  if (oxyOn) {
    String oxyResp = sendSensorCommand("g", true);
    oxyVal = oxyResp.toFloat();
  }
  
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
  String jsonResponse = "{\"Time\":" + String(timeSec, 1);
  jsonResponse += ",\"Tempval\":" + String(temperatureVal, 2);
  jsonResponse += ",\"pHval\":" + String(pHVal, 2);
  jsonResponse += ",\"Oxyval\":" + String(oxyVal, 1);
  jsonResponse += ",\"Antifoam\":" + String(antifoamVal, 0);
  jsonResponse += ",\"Pressure\":" + String(pressureVal, 1);
  jsonResponse += "}";
  
  if (wifiClient && wifiClient.connected()) {
    wifiClient.println(jsonResponse);
  }
  blePrintln(jsonResponse);
  Serial.println(jsonResponse);
}

// ------------------------------------------------------------------
// setMotor():
//   If rpm == 0 -> send "0V" and "0A"
//   Else        -> send "1V" and "<rpm>A" (e.g., "200A")
// ------------------------------------------------------------------
void setMotor(int rpm) {
    if (rpm < 1 || rpm > 1000) {
        rpm = 100;
    }

    motorRPM = rpm;
    String cmdSpeed = String(motorRPM) + "A";

    if (rpm == 0) {
        sendSensorCommand("0V", false);
        sendSensorCommand("0A", false);
    } else {
        sendSensorCommand("1V", false);
        sendSensorCommand(cmdSpeed, false);
    }
}


// ------------------------------------------------------------------
// setTemperature():
//   If t == 0 -> turn off temperature reading and send "100B" (OFF)
//   Else      -> turn on temperature reading and send command (e.g., "281B")
// ------------------------------------------------------------------
void setTemperature(float temp) {
  tempReference = temp;
  if (fabs(tempReference) < 0.001) {
    tempOn = false;
    sendSensorCommand("100B", false);
  } else {
    tempOn = true;
    String tStr  = String(tempReference, 1);
    String noDot = removeDecimal(tStr);
    sendSensorCommand(noDot + "B", false);
  }
}

// ------------------------------------------------------------------
// setPH():
//   If pH == 0 -> turn off pH reading and send "0F" (OFF)
//   Else       -> turn on pH reading and send commands for setpoint,
//                error, operation, mixing, and intensity.
// ------------------------------------------------------------------
void setPH(float pH, float err, int op, int mix, int intensity) {
    // Apply default values if out of bounds
    if (pH < 0.0f || pH > 14.0f) {
        pH = 7.00f;
    }
    if (err <= 0.0f || err >= 2.0f) {
        err = 0.17f;
    }
    if (op < 1 || op >= 1000) {
        op = 5;
    }
    if (mix <= 0 || mix >= 1000) {
        mix = 10;
    }
    if (intensity <= 0 || intensity >= 100) {
        intensity = 99;
    }

    // Assign values to global variables
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
// setNutrient():
//   If nutrient intensity == 0 -> send "0M" (OFF)
//   Else -> send command with intensity followed by "0M"
//   Also send nutrient operation ("N"), mixing ("O"),
//   operation cycle ("P") and mixing cycle ("Q") commands.
// ------------------------------------------------------------------
void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity) {
    // Apply default values if out of bounds
    if (op < 1 || op >= 1000) {
        op = 999;
    }
    if (mix <= 0 || mix >= 1000) {
        mix = 1;
    }
    if (opCycle < 1 || opCycle > 500) {
        opCycle = 500;
    }
    if (mixCycle < 1 || mixCycle >= 1000) {
        mixCycle = 1;
    }
    if (intensity < 0 || intensity >= 100) {
        intensity = 99;
    }

    // Assign values to global variables
    nutriOperation = op;
    nutriMix       = mix;
    nutriOpCycle   = opCycle;
    nutriMixCycle  = mixCycle;
    nutriIntensity = intensity;

    // Create command strings before sending
    String cmdIntensity = String(nutriIntensity) + "0M";
    String cmdOp = String(nutriOperation) + "N";
    String cmdMix = String(nutriMix) + "O";
    String cmdOpCycle = String(nutriOpCycle) + "P";
    String cmdMixCycle = String(nutriMixCycle) + "Q";

    // Send commands to the sensor
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
// setAntifoam():
//   If antifoam intensity == 0 -> send "0I" (OFF)
//   Else -> send command with intensity followed by "0I"
//   Also send antifoam operation ("J") and mixing ("L") commands.
// ------------------------------------------------------------------
void setAntifoam(int op, int mix, int intensity) {
    if (op < 1 || op >= 1000) {
        op = 999;
    }
    if (mix <= 0 || mix >= 1000) {
        mix = 1;
    }
    if (intensity < 0 || intensity >= 100) {
        intensity = 99;
    }

    antifoamOperation = op;
    antifoamMix       = mix;
    antifoamIntensity = intensity;

    String cmdIntensity = String(antifoamIntensity) + "0I";
    String cmdOp = String(antifoamOperation) + "J";
    String cmdMix = String(antifoamMix) + "L";

    if (antifoamIntensity == 0) {
        antifoamOn = false;
        sendSensorCommand("0I", false);
    } else {
        antifoamOn = true;
        sendSensorCommand(cmdIntensity, false);
    }

    sendSensorCommand(cmdOp, false);
    sendSensorCommand(cmdMix, false);
}


// ------------------------------------------------------------------
// setPressure():
//   If pressureReference == 0 -> send "0C" (OFF)
//   Else -> send command with reference followed by "C"
// ------------------------------------------------------------------
void setPressure(int ref) {
    if (ref < 0 || ref > 380) {
        ref = 100; 
    }

    pressureReference = ref;

    // Create command string before sending
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
// sendSensorCommand():
// Sends the given command (followed by a carriage return) via UART
// to the sensor. If readResponse is true, waits briefly for a response.
// ------------------------------------------------------------------
String sendSensorCommand(const String &cmd, bool readResponse) {
  sensorSerial.print(cmd);
  sensorSerial.print("\r");
  Serial.println("Sent to sensor: " + cmd);

  if (readResponse) {
    sensorSerial.setTimeout(50);
    String response = sensorSerial.readStringUntil('\r');
    response.trim();
    return response;
  }
  return "";
}

// ------------------------------------------------------------------
// removeDecimal():
// Remove the decimal point from a numeric string (e.g., "28.1" -> "281").
// ------------------------------------------------------------------
String removeDecimal(const String &s) {
  String ret = s;
  ret.replace(".", "");
  return ret;
}
