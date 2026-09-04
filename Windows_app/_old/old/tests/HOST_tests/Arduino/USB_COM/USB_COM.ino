/*************************************************************
 * Example ESP32-S3 code for:
 *  - WiFi SoftAP: "ModuloTECNAL" (password "ModuloTECNAL")
 *  - TCP server on port 23 (telnet-like)
 *  - Bluetooth Low Energy (BLE) UART service
 *  - JSON command parsing
 *  - Communication with a USB device via the USB‑C port 
 *    (using Adafruit_TinyUSB as USB Host)
 *  - Periodic sensor readings broadcasted in JSON
 *************************************************************/

#include <Arduino.h>
#include <WiFi.h>
#include <WiFiClient.h>
#include <Adafruit_TinyUSB.h>  // For USB Host functionality
#include <NimBLEDevice.h>
// Include the TinyUSB header to get access to host mode functions.
extern "C" {
  #include "tusb.h"  // Provides tuh_task() for USB host polling
}

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
NimBLEServer*           pServer          = nullptr;
NimBLECharacteristic*   pTxCharacteristic = nullptr;
NimBLECharacteristic*   pRxCharacteristic = nullptr;
bool                    deviceConnected  = false;

// --- WiFi Objects ---
WiFiServer wifiServer(WIFI_PORT);
WiFiClient wifiClient; // track a single connected client

// --- USB Device (CDC ACM via USB‑C) ---
Adafruit_USBH_CDC usbSerial;  // USB Host Serial object

// ============ GLOBAL VARIABLES ============
// Variables to hold setpoints & states
float setpointTemp = 0.0f;
float setpointPH   = 0.0f;
float pHError      = 0.0f;
int   pHOperation  = 0;
int   pHMix        = 0;
int   pHIntensity  = 0;

int   motorRPM     = 0;  // 0 means OFF
bool  oxyOn        = false;

// The user-specified delay (ms) to read sensor data
unsigned long dataDelay = 1000;  
unsigned long lastDataMillis = 0;

// ============ FUNCTION DECLARATIONS ============
void handleWiFiClient();
void handleBluetoothData();  // (BLE uses callbacks; function left empty)
void handleUSBData();
void handleData(const String& data);
void updateVariables(const String& key, const String& value);
void setMotor(int rpm);
void setTemperature(float t);
void setPH(float ph);
String sendSensorCommand(const String& cmd, bool readResponse);
float parseFloatOrDefault(const String& s, float defaultVal);
String removeDecimal(const String& s);
void readAndBroadcastSensorData();
void initializeDeviceCommunication();

// ============ BLE CALLBACK CLASSES ============

class MyServerCallbacks : public NimBLEServerCallbacks {
  void onConnect(NimBLEServer* pServer) {
    deviceConnected = true;
    Serial.println("BLE client connected");
  }

  void onDisconnect(NimBLEServer* pServer) {
    deviceConnected = false;
    Serial.println("BLE client disconnected");
    NimBLEDevice::getAdvertising()->start();  // Restart advertising
  }
};

class MyCallbacks : public NimBLECharacteristicCallbacks {
  void onWrite(NimBLECharacteristic* pCharacteristic, NimBLEConnInfo& connInfo) {
    std::string rxValue = pCharacteristic->getValue();
    if (!rxValue.empty()) {
      String data = String(rxValue.c_str());
      Serial.print("Received via BLE: ");
      Serial.println(data);
      // Process the received data (JSON commands)
      handleData(data);
    }
  }
};


// ============ HELPER FUNCTIONS FOR BLE PRINTING ============

void blePrint(const String& data) {
  if (pTxCharacteristic != nullptr && deviceConnected) {
    pTxCharacteristic->setValue(data.c_str());
    pTxCharacteristic->notify();
  }
}

void blePrintln(const String& data) {
  blePrint(data + "\n");
}

// ============ SETUP ============

void setup() {
  // Start Serial for debug output
  Serial.begin(115200);
  delay(1500);
  Serial.println("ESP32-S3 starting up...");

  // --- Initialize USB Host ---
  // If your board requires manually enabling 5V on VBUS, do it here.
  pinMode(12, OUTPUT);         // Adjust this pin if needed
  digitalWrite(12, HIGH);      // Enable 5V to the USB device
  
  // Start the TinyUSB host stack
  //TinyUSBHost.begin();

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
  Serial.println("BLE advertising started. Device name: ESP32_TECNAL");

  // --- Initialize WiFi in AP mode ---
  WiFi.mode(WIFI_MODE_AP);
  WiFi.softAP(WIFI_SSID, WIFI_PASSWORD);
  Serial.println();
  Serial.print("WiFi SoftAP started. SSID: ");
  Serial.println(WIFI_SSID);
  Serial.print("Password: ");
  Serial.println(WIFI_PASSWORD);

  // Print out the AP IP (default is 192.168.4.1)
  IPAddress myIP = WiFi.softAPIP();
  Serial.print("AP IP address: ");
  Serial.println(myIP);

  // Start the WiFi server
  wifiServer.begin();
  Serial.print("WiFi server started on port ");
  Serial.println(WIFI_PORT);

  // --- Initialize Communication with the USB Device ---
  initializeDeviceCommunication();
}

// ============ LOOP ============

void loop() {

  #ifdef TINYUSB_NEED_POLLING_TASK
    tuh_task();
  #endif
  
  // 0) Check for incoming data from the USB device
  handleUSBData();

  // 1) Check WiFi client for incoming commands
  handleWiFiClient();

  // 2) Periodically request sensor readings from the USB device and broadcast them
  unsigned long now = millis();
  if ((now - lastDataMillis) >= dataDelay) {
    lastDataMillis = now;
    readAndBroadcastSensorData();
  }
}

// ------------------------------------------------------------------
// USB Handling: Read data from the USB device connected via USB‑C
// ------------------------------------------------------------------
void handleUSBData() {
  if (usbSerial && usbSerial.connected()) {
    while (usbSerial.available()) {
      String data = "";
      // Read until newline
      while (usbSerial.available()) {
        char c = usbSerial.read();
        if (c == '\n') break;
        data += c;
      }
      data.trim();
      if (data.length() > 0) {
        // Forward the data to Serial (debug), WiFi client, and BLE client
        Serial.println("From USB Device: " + data);
        if (wifiClient && wifiClient.connected()) {
          wifiClient.println(data);
        }
        blePrintln(data);
      }
    }
  }
}

// ------------------------------------------------------------------
// 1) WiFi Handling
// ------------------------------------------------------------------
void handleWiFiClient() {
  // If no active client or the existing client is disconnected, accept a new one.
  if (!wifiClient || !wifiClient.connected()) {
    wifiClient = wifiServer.available();
  } else {
    // If connected, check for incoming data
    while (wifiClient.available()) {
      String data = wifiClient.readStringUntil('\n');
      data.trim();
      if (data.length() > 0) {
        handleData(data);  // Parse and process the command
      }
    }
  }
}

// ------------------------------------------------------------------
// 2) Reading from the USB device & broadcasting sensor data in JSON
// ------------------------------------------------------------------
void readAndBroadcastSensorData() {
  // Request sensor readings from the USB device via commands.
  // (These commands are examples—adjust them according to your USB device's protocol.)
  String temperatureResp = sendSensorCommand("b", true);  // e.g., "b" returns temperature
  float temperatureVal = parseFloatOrDefault(temperatureResp, -1.0);

  String pHResp = sendSensorCommand("d", true);  // e.g., "d" returns pH
  float pHVal = parseFloatOrDefault(pHResp, -1.0);

  float oxyVal = -1.0;
  if (oxyOn) {
    String oxyResp = sendSensorCommand("g", true);  // e.g., "g" returns oxygen reading
    oxyVal = parseFloatOrDefault(oxyResp, -1.0);
  }

  // Build JSON string (example format):
  // {"Time":10.5,"Tempval":18.4,"pHval":6.8,"Oxyval":451}
  float timeSec = millis() / 1000.0;
  String jsonResponse = "{\"Time\":";
  jsonResponse += String(timeSec, 1);
  jsonResponse += ",\"Tempval\":";
  jsonResponse += String(temperatureVal, 2);
  jsonResponse += ",\"pHval\":";
  jsonResponse += String(pHVal, 2);
  jsonResponse += ",\"Oxyval\":";
  jsonResponse += String(oxyVal, 1);
  jsonResponse += "}";

  // Broadcast the JSON response via WiFi, BLE, and print it to Serial
  if (wifiClient && wifiClient.connected()) {
    wifiClient.println(jsonResponse);
  }
  blePrintln(jsonResponse);
  Serial.println(jsonResponse);
}

// ------------------------------------------------------------------
// Initialization / Handshake with the USB device (example)
// ------------------------------------------------------------------
void initializeDeviceCommunication() {
  String resp1 = sendSensorCommand("i", true);
  if (resp1 != "i") {
    Serial.println("USB device handshake step 'i' failed or no response.");
  }
  delay(50);
  
  String resp2 = sendSensorCommand("j", true);
  if (resp2 != "j") {
    Serial.println("USB device handshake step 'j' failed or no response.");
  }
  Serial.println("USB device communication initialization done.");
}

// ------------------------------------------------------------------
// handleData() - Interpret JSON commands, e.g.:
// {"Datadelay":500,"Motorset":200,"Tempset":28.1,"Oxy":1,
//  "pHset":5.8,"pHerr":0.17,"pHop":2,"pHmix":10,"pHintensity":20}
// ------------------------------------------------------------------
void handleData(const String& data) {
  if (!(data.startsWith("{") && data.endsWith("}"))) {
    Serial.println("Error: not a JSON string (missing braces).");
    return;
  }

  // Remove outer braces
  String content = data.substring(1, data.length() - 1);
  int start = 0;
  while (start < content.length()) {
    int colonIndex = content.indexOf(':', start);
    if (colonIndex == -1) break;  // no more key-value pairs

    int commaIndex = content.indexOf(',', start);
    if (commaIndex == -1) {
      commaIndex = content.length();
    }

    String key   = content.substring(start, colonIndex);
    String value = content.substring(colonIndex + 1, commaIndex);
    key.trim();
    value.trim();

    updateVariables(key, value);
    start = commaIndex + 1;
  }
}

// ------------------------------------------------------------------
// updateVariables() - Match recognized keys and forward commands to USB device
// ------------------------------------------------------------------
void updateVariables(const String& key, const String& value) {
  // Remove quotes from keys if present
  String cleanedKey = key;
  cleanedKey.replace("\"", "");

  if (cleanedKey.equals("Datadelay")) {
    dataDelay = value.toInt();
    if (dataDelay < 100) dataDelay = 100;
  }
  else if (cleanedKey.equals("Motorset")) {
    int newRpm = value.toInt();
    setMotor(newRpm);
  }
  else if (cleanedKey.equals("Tempset")) {
    float newTemp = value.toFloat();
    setTemperature(newTemp);
  }
  else if (cleanedKey.equals("pHset")) {
    float newPH = value.toFloat();
    setPH(newPH);
  }
  else if (cleanedKey.equals("pHerr")) {
    pHError = value.toFloat();
    // If pH control is active, forward the error immediately
    if (setpointPH != 0.0f) {
      String cmd = removeDecimal(value) + "E";  // e.g., "017E"
      sendSensorCommand(cmd, false);
    }
  }
  else if (cleanedKey.equals("pHop")) {
    pHOperation = value.toInt();
    if (setpointPH != 0.0f) {
      String cmd = String(pHOperation) + "G";   // e.g., "2G"
      sendSensorCommand(cmd, false);
    }
  }
  else if (cleanedKey.equals("pHmix")) {
    pHMix = value.toInt();
    if (setpointPH != 0.0f) {
      String cmd = String(pHMix) + "H";         // e.g., "10H"
      sendSensorCommand(cmd, false);
    }
  }
  else if (cleanedKey.equals("pHintensity")) {
    pHIntensity = value.toInt();
    if (setpointPH != 0.0f) {
      String cmd = String(pHIntensity) + "F";     // e.g., "20F"
      sendSensorCommand(cmd, false);
    }
  }
  else if (cleanedKey.equals("Oxy")) {
    int val = value.toInt();
    oxyOn = (val == 0) ? false : true;
  }
  // Add additional key processing as needed...
}

// ------------------------------------------------------------------
// Motor control:
//   If rpm == 0 -> send "0V" and "0A"
//   Else         -> send "1V" and "<rpm>A" (e.g., "200A")
// ------------------------------------------------------------------
void setMotor(int rpm) {
  motorRPM = rpm;
  if (rpm == 0) {
    sendSensorCommand("0V", false);
    sendSensorCommand("0A", false);
  } else {
    sendSensorCommand("1V", false);
    String cmd = String(rpm) + "A";  // e.g., "200A"
    sendSensorCommand(cmd, false);
  }
}

// ------------------------------------------------------------------
// Temperature control:
//   If t == 0 -> send "100B" (OFF)
//   Else      -> remove the decimal and send "<value>B" (e.g., "281B" for 28.1°C)
// ------------------------------------------------------------------
void setTemperature(float t) {
  setpointTemp = t;
  if (fabs(t) < 0.001) {
    sendSensorCommand("100B", false);
  } else {
    String tStr = String(t, 1);        // e.g., "28.1"
    String noDot = removeDecimal(tStr); // e.g., "281"
    String cmd = noDot + "B";
    sendSensorCommand(cmd, false);
  }
}

// ------------------------------------------------------------------
// pH control:
//   If ph == 0 -> send "0F" (OFF)
//   Else      -> send commands such as "<value>D" and, if set, error and mix commands
// ------------------------------------------------------------------
void setPH(float ph) {
  setpointPH = ph;
  if (fabs(ph) < 0.001) {
    sendSensorCommand("0F", false);
  } else {
    String phStr = String(ph, 2);
    String noDot = removeDecimal(phStr);
    String cmd = noDot + "D";  // e.g., "700D"
    sendSensorCommand(cmd, false);

    if (pHError != 0.0f) {
      String cmdErr = removeDecimal(String(pHError, 2)) + "E";
      sendSensorCommand(cmdErr, false);
    }
    if (pHOperation > 0) {
      String cmdOp = String(pHOperation) + "G";
      sendSensorCommand(cmdOp, false);
    }
    if (pHMix > 0) {
      String cmdMix = String(pHMix) + "H";
      sendSensorCommand(cmdMix, false);
    }
    if (pHIntensity > 0) {
      String cmdInt = String(pHIntensity) + "F";
      sendSensorCommand(cmdInt, false);
    }
  }
}

// ------------------------------------------------------------------
// sendSensorCommand(cmd, readResponse):
// Sends the command (plus a newline) to the USB device via the host interface,
// and, if readResponse is true, waits for a response (up to ~2000ms).
// ------------------------------------------------------------------
String sendSensorCommand(const String &cmd, bool readResponse) {
  if (usbSerial && usbSerial.connected()) {
    usbSerial.write(cmd.c_str(), cmd.length());
    usbSerial.write("\n", 1);
    Serial.print("Command Sent to USB device: ");
    Serial.println(cmd);
    blePrint("Command Sent: " + cmd);
    blePrint("\n");

    if (readResponse) {
      unsigned long start = millis();
      while ((millis() - start) < 2000) {
        if (usbSerial.available()) {
          String response = "";
          while (usbSerial.available()) {
            char c = usbSerial.read();
            if (c == '\n') break;
            response += c;
          }
          response.trim();
          Serial.print("Response from USB device: ");
          Serial.println(response);
          blePrint("Response: " + response);
          blePrint("\n");
          return response;
        }
      }
      // No response received within the timeout period
      return "";
    }
  } else {
    Serial.println("USB device not connected.");
    return "";
  }
  return "";
}

// ------------------------------------------------------------------
// parseFloatOrDefault: Converts string to float or returns defaultVal if conversion fails
// ------------------------------------------------------------------
float parseFloatOrDefault(const String& s, float defaultVal) {
  if (s.length() == 0) return defaultVal;
  return s.toFloat();
}

// ------------------------------------------------------------------
// removeDecimal: Removes the decimal point from a numeric string (e.g., "28.1" -> "281")
// ------------------------------------------------------------------
String removeDecimal(const String& s) {
  String ret = s;
  ret.replace(".", "");
  return ret;
}
