/*************************************************************
 * SensorHub ESP32-S3 Code 
 *
 * - WiFi SoftAP: "ModuloTECNAL_2" (password "ModuloTECNAL_2")
 * - Provides an AP for local devices while optionally connecting
 * - HTTP server for command reception and sensor data reading.
 * - Supports HTTP endpoints for Flowmeter data & commands and Distance sensor updates.
 * - Uses ESP32 Preferences library to persist key variables across reboots.
 *************************************************************/

#include <Arduino.h>
#include <WiFi.h>
#include <HTTPClient.h>
#include <ESPAsyncWebServer.h>
#include <vector>
#include <esp_task_wdt.h>
#include <Preferences.h>
#include <math.h>

// ============ USER SETTINGS =============
// WiFi AP credentials (for SensorHub)
#define WIFI_SSID "ModuloTECNAL_2"
#define WIFI_PASSWORD "ModuloTECNAL_2"

// Buffer size for temporary JSON strings (if needed)
#define JSON_BUFFER_LEN 256

// ============ GLOBAL OBJECTS ============

// --- HTTP/AsyncWebServer Objects ---
AsyncWebServer server(80);
String lastSensorJson = "";  // Latest sensor JSON
// identify each freshly produced JSON
uint32_t sampleId = 0;

// ----- Global variable to hold pending flowmeter command (as JSON string) -----
String pendingFlowmeterCommand = "";

// ============ UART CONFIG ============
#define SENSOR_RX_PIN 16         // ESP32-S3 RX pin for sensor (sensor TX -> ESP32-S3 RX)
#define SENSOR_TX_PIN 17         // ESP32-S3 TX pin for sensor (sensor RX -> ESP32-S3 TX)
HardwareSerial sensorSerial(2);  // Use UART2 for sensor
#define MAX_SENSOR_BUFFER_LENGTH 1024
#define WDT_TIMEOUT 10  // Timeout in seconds

// ============ GLOBAL VARIABLES ============
float tempReference = 0.0f;
float pHReference = 0.0f;
float pHError = 0.0f;
int   pHCal = 0;
int   pHOperation = 0;
int   pHMix = 0;
int   pHIntensity = 0;
int   motorRPM = 0;

int   nutriOperation = 0;
int   nutriMix = 0;
int   nutriOpCycle = 0;
int   nutriMixCycle = 0;
int   nutriIntensity = 0;

int   antifoamOperation = 0;
int   antifoamMix = 0;
int   antifoamIntensity = 0;

int   pressureReference = 0;

bool  oxyOn = false;   // Oxygen sensor enabled if nonzero
bool  tempOn = false;  // Temperature reading ON if tempReference ≠ 0
bool  phOn = false;    // pH reading ON if pHReference ≠ 0
bool  pHCalforUpdate = false;
bool  nutrientOn = false;  // Nutrient parameters active if any nutrient value is set
bool  antifoamOn = false;  // Antifoam active if intensity > 0
bool  pressureOn = false;  // Pressure active if reference > 0

// Operating mode flag:
// false = custom mode (JSON commands + periodic sensor reads)
// true  = bypass mode (freeway-like direct relay of commands)
bool  bypassMode = false;

unsigned long dataDelay = 1000;
unsigned long lastDataMillis = 0;

// Global buffer for sensor input from UART:
String sensorBuffer = "";
bool uartSensorOK = true; // true if last UART communication was successful
int uartFailureCount = 0; // how many consecutive failures occurred
const int UART_FAILURE_THRESHOLD = 3;

// Oxygen calibration constants
float a = 0.0305473419314;
float b = -25.09136520919;

// Flowmeter variables – updated when the flowmeter board sends its data via HTTP GET:
bool  flowmeterCommOn = false;
float flowmeterTime = 0.0;
float flowmeterVoltage = 0.0;
float flowmeterRate = 0.0;
float flowmeterSetpoint = 0.0;
int   flowmeterValve1 = 0;
int   flowmeterValve2 = 0;

// --- Distance Sensor variables ---
bool  distanceSensorCommOn = false;
float distanceSensorTime = 0.0;
float distanceSensorValue = -1.0f;
unsigned long distanceSensorLastUpdate = 0;
const unsigned long DISTANCE_TIMEOUT = 1200;

// --- Distance Sensor Reference value (set via JSON command) ---
float distanceSensorReference = 0.0;

// ---- Agitator control & foam dosing ----
String pendingAgitatorCommand = "";   // pulled by Agitator (like /flowCommand)
bool   agitatorAuto           = true; // hub controls agitator automatically when foam
bool   agitatorReEnablePot    = true; // re-enable local pot after foam episode ends
int    agitatorPercentFoam    = 60;   // % duty to run mixer when foam is present
int    agitatorDirFoam        = 1;    // 1=right/forward, 0=left/reverse

// Dosing timers (X/Y/Z in seconds)
float  foamStartDelay_s       = 1.0f; // X: wait before first pulse
float  foamPulse_s            = 1.0f; // Y: pulse length
float  foamInterval_s         = 5.0f; // Z: cumulative foam-ON spacing between pulses


// ============ PERSISTENCE ============
// Create a Preferences object to store configuration across reboots.
Preferences preferences;

// Save all configuration variables that should be persisted.
void saveSettings() {
  preferences.putFloat("tempRef", tempReference);
  preferences.putFloat("pHRef", pHReference);
  preferences.putFloat("pHErr", pHError);
  preferences.putInt("pHCal", pHCal);
  preferences.putInt("pHOp", pHOperation);
  preferences.putInt("pHMix", pHMix);
  preferences.putInt("pHInt", pHIntensity);
  preferences.putInt("motorRPM", motorRPM);
  preferences.putInt("nutriOp", nutriOperation);
  preferences.putInt("nutriMix", nutriMix);
  preferences.putInt("nutriOpCycle", nutriOpCycle);
  preferences.putInt("nutriMixCycle", nutriMixCycle);
  preferences.putInt("nutriInt", nutriIntensity);
  preferences.putInt("antiOp", antifoamOperation);
  preferences.putInt("antiMix", antifoamMix);
  preferences.putInt("antiInt", antifoamIntensity);
  preferences.putInt("presRef", pressureReference);
  preferences.putFloat("distRef", distanceSensorReference);
  preferences.putULong("dataDelay", dataDelay);
  preferences.putBool("oxyOn", oxyOn);
  preferences.putBool("tempOn", tempOn);
  preferences.putBool("phOn", phOn);
  preferences.putBool("nutrientOn", nutrientOn);
  preferences.putBool("antiOn", antifoamOn);
  preferences.putBool("presOn", pressureOn);
  preferences.putBool("agitAuto", agitatorAuto);
  preferences.putBool("agitRePot", agitatorReEnablePot);
  preferences.putInt("agitPct", agitatorPercentFoam);
  preferences.putInt("agitDir", agitatorDirFoam);
  preferences.putFloat("foamDelay", foamStartDelay_s);
  preferences.putFloat("foamPulse", foamPulse_s);
  preferences.putFloat("foamInterval", foamInterval_s);

}

// Load the persisted configuration; if a key isn’t available, use the given default.
void loadSettings() {
  tempReference        = preferences.getFloat("tempRef", 0.0f);
  pHReference          = preferences.getFloat("pHRef", 0.0f);
  pHError              = preferences.getFloat("pHErr", 0.17f);
  pHCal                = preferences.getInt("pHCal", 5);
  pHOperation          = preferences.getInt("pHOp", 5);
  pHMix                = preferences.getInt("pHMix", 10);
  pHIntensity          = preferences.getInt("pHInt", 990);
  motorRPM             = preferences.getInt("motorRPM", 0);
  nutriOperation       = preferences.getInt("nutriOp", 999);
  nutriMix             = preferences.getInt("nutriMix", 1);
  nutriOpCycle         = preferences.getInt("nutriOpCycle", 500);
  nutriMixCycle        = preferences.getInt("nutriMixCycle", 1);
  nutriIntensity       = preferences.getInt("nutriInt", 99);
  antifoamOperation    = preferences.getInt("antiOp", 999);
  antifoamMix          = preferences.getInt("antiMix", 1);
  antifoamIntensity    = preferences.getInt("antiInt", 99);
  pressureReference    = preferences.getInt("presRef", 100);
  distanceSensorReference = preferences.getFloat("distRef", 0.0f);
  dataDelay            = preferences.getULong("dataDelay", 1000);
  oxyOn                = preferences.getBool("oxyOn", false);
  tempOn               = preferences.getBool("tempOn", false);
  phOn                 = preferences.getBool("phOn", false);
  nutrientOn           = preferences.getBool("nutrientOn", false);
  antifoamOn           = preferences.getBool("antiOn", false);
  pressureOn           = preferences.getBool("presOn", false);
  agitatorAuto        = preferences.getBool("agitAuto", true);
  agitatorReEnablePot = preferences.getBool("agitRePot", true);
  agitatorPercentFoam = preferences.getInt("agitPct", 60);
  agitatorDirFoam     = preferences.getInt("agitDir", 1);
  foamStartDelay_s    = preferences.getFloat("foamDelay", 1.0f);
  foamPulse_s         = preferences.getFloat("foamPulse", 1.0f);
  foamInterval_s      = preferences.getFloat("foamInterval", 5.0f);

}

// Function to print all persistent settings for debugging
void debugSettings() {
  Serial.println("==== Loaded Preferences ====");
  Serial.print("tempReference: "); Serial.println(tempReference);
  Serial.print("pHReference: "); Serial.println(pHReference);
  Serial.print("pHError: "); Serial.println(pHError);
  Serial.print("pHCal: "); Serial.println(pHCal);
  Serial.print("pHOperation: "); Serial.println(pHOperation);
  Serial.print("pHMix: "); Serial.println(pHMix);
  Serial.print("pHIntensity: "); Serial.println(pHIntensity);
  Serial.print("motorRPM: "); Serial.println(motorRPM);
  Serial.print("nutriOperation: "); Serial.println(nutriOperation);
  Serial.print("nutriMix: "); Serial.println(nutriMix);
  Serial.print("nutriOpCycle: "); Serial.println(nutriOpCycle);
  Serial.print("nutriMixCycle: "); Serial.println(nutriMixCycle);
  Serial.print("nutriIntensity: "); Serial.println(nutriIntensity);
  Serial.print("antifoamOperation: "); Serial.println(antifoamOperation);
  Serial.print("antifoamMix: "); Serial.println(antifoamMix);
  Serial.print("antifoamIntensity: "); Serial.println(antifoamIntensity);
  Serial.print("pressureReference: "); Serial.println(pressureReference);
  Serial.print("distanceSensorReference: "); Serial.println(distanceSensorReference);
  Serial.print("dataDelay: "); Serial.println(dataDelay);
  Serial.print("oxyOn: "); Serial.println(oxyOn);
  Serial.print("tempOn: "); Serial.println(tempOn);
  Serial.print("phOn: "); Serial.println(phOn);
  Serial.print("nutrientOn: "); Serial.println(nutrientOn);
  Serial.print("antifoamOn: "); Serial.println(antifoamOn);
  Serial.print("pressureOn: "); Serial.println(pressureOn);
  Serial.print("agitatorAuto: "); Serial.println(agitatorAuto);
  Serial.print("agitatorReEnablePot: "); Serial.println(agitatorReEnablePot);
  Serial.print("agitatorPercentFoam: "); Serial.println(agitatorPercentFoam);
  Serial.print("agitatorDirFoam: "); Serial.println(agitatorDirFoam);
  Serial.print("foamStartDelay_s: "); Serial.println(foamStartDelay_s);
  Serial.print("foamPulse_s: "); Serial.println(foamPulse_s);
  Serial.print("foamInterval_s: "); Serial.println(foamInterval_s);
  Serial.println("============================");
}

// ============ HELPER FUNCTION FOR MANUAL JSON PARSING ============
String getValueFromJson(const String &json, const String &key) {
  String searchKey = "\"" + key + "\"";
  int keyPos = json.indexOf(searchKey);
  if (keyPos == -1) return "";
  int colonPos = json.indexOf(":", keyPos);
  if (colonPos == -1) return "";
  int start = colonPos + 1;
  while (start < json.length() && isspace(json.charAt(start))) {
    start++;
  }
  char firstChar = json.charAt(start);
  int end = -1;
  if (firstChar == '\"') {
    start++;
    end = json.indexOf("\"", start);
    if (end == -1) return "";
  } else {
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
String handleSensorData();
String processCommandData(const String &data);  // Process incoming command (JSON or bypass)
void processJsonCommand(const String &json);
void readAndBroadcastSensorData();

void setMotor(int rpm);
void setTemperature(float t);
void setPH(float pH, float err, int op, int mix, int intensity);
void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity);
void setAntifoam(int op, int mix, int intensity);
void setPressure(int ref);
String sendSensorCommand(const String &cmd, bool readResponse);
String removeDecimal(const String &s);
void updateParametersCycle();
void checkDistanceSensorReference();
void flushSerial(Stream &serial);
void resetSensorSerial();

// ------------------------------------------------------------------
// startWiFi():
//   Sets up the WiFi SoftAP, HTTP endpoints, and the WebServer.
// ------------------------------------------------------------------
void startWiFi() {
  // --- Define a static IP configuration for the Access Point ---
  IPAddress apIP(192, 168, 4, 1);
  IPAddress subnet(255, 255, 255, 0);

  // Apply the static IP configuration BEFORE starting the AP
  WiFi.mode(WIFI_MODE_AP);
  WiFi.softAPConfig(apIP, apIP, subnet);

  // Returns true on success, false on failure
  bool apStarted = WiFi.softAP(WIFI_SSID, WIFI_PASSWORD, 6, 0, 4); 

  if (apStarted) {
    Serial.println();
    Serial.print("WiFi SoftAP started successfully. SSID: ");
    Serial.println(WIFI_SSID);
    Serial.print("AP IP address: ");
    Serial.println(WiFi.softAPIP());

    // --- Add a small delay to allow the network stack to stabilize ---
    delay(100); 

    // HTTP POST endpoint to receive commands
    server.on(
      "/command", HTTP_POST, [](AsyncWebServerRequest *request) {},
      NULL,
      [](AsyncWebServerRequest *request, uint8_t *data, size_t len, size_t index, size_t total) {
        if (len) {
          String message = String((char *)data).substring(0, len);
          int startIdx = message.indexOf('{');
          int endIdx = message.lastIndexOf('}');
          if (startIdx != -1 && endIdx != -1 && startIdx < endIdx) {
              String jsonContent = message.substring(startIdx, endIdx + 1);
              String response = processCommandData(jsonContent);
              jsonContent.trim();
              request->send(200, "text/plain", response);
          } else {
              request->send(400, "text/plain", "Invalid JSON format");
          }
        } else {
          request->send(400, "text/plain", "No body received");
        }
    });

    // HTTP GET endpoint to return latest sensor data (JSON) with ETag
    server.on("/readData", HTTP_GET, [](AsyncWebServerRequest *request) {
      String etag = "\"" + String(sampleId) + "\"";  // strong ETag like "42"

      if (request->hasHeader("If-None-Match")) {
        auto *h = request->getHeader("If-None-Match");
        if (h && h->value() == etag) {
          request->send(304);  // unchanged; no body
          return;
        }
      }

      AsyncWebServerResponse *res =
          request->beginResponse(200, "application/json", lastSensorJson);
      res->addHeader("ETag", etag);
      res->addHeader("Cache-Control", "no-store");
      request->send(res);
    });

    // HTTP GET endpoint for ping (simple health check)
    server.on("/ping", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "text/plain", "pong");
    });

    // HTTP GET endpoint for distance sensor updates.
    server.on("/distance", HTTP_GET, [](AsyncWebServerRequest *request) {
      // 1) Validate
      if (!request->hasParam("distance")) {
        request->send(400, "text/plain", "Missing 'distance' parameter");
        return;
      }

      // 2) Parse new reading
      String distStr = request->getParam("distance")->value();
      float   newDistance = distStr.toFloat();

      // 3) Rolling buffer for last 5 raw readings
      static float  buf[5]         = {0.0f};
      static uint8_t bufIndex      = 0;
      static uint8_t bufCount      = 0;
      static float  lastAccepted   = NAN;
      const float   ES            = 1e-3f;

      // Insert and advance
      buf[bufIndex] = newDistance;
      bufIndex = (bufIndex + 1) % 5;
      if (bufCount < 5) ++bufCount;

      // 4) Check stagnation: all 5 values equal within ES?
      bool stagnated = false;
      if (bufCount == 5) {
        stagnated = true;
        for (uint8_t i = 1; i < 5; ++i) {
          if (fabsf(buf[i] - buf[0]) > ES) {
            stagnated = false;
            break;
          }
        }
      }
      // 5) Update global sensor value
      if (stagnated) {
        // treat as disconnection / no‐movement
        distanceSensorValue = -1.0f;
      }
      else if (isnan(lastAccepted) || fabsf(newDistance - lastAccepted) > ES) {
        // only accept truly new values
        distanceSensorValue = newDistance;
        lastAccepted = newDistance;
      }
      // else: leave distanceSensorValue unchanged

      // 6) Optionally parse timestamp
      if (request->hasParam("time")) {
        distanceSensorTime = request->getParam("time")->value().toFloat();
      }
      distanceSensorLastUpdate = millis();

      // 7) Respond (echo JSON if you like)
      String json = "{";
      json += "\"Distance\":" + String(distanceSensorValue, 2) + ",";
      json += "\"Time\":"     + String(distanceSensorTime, 1);
      json += "}";
      request->send(200, "application/json", json);
    });

    // HTTP GET endpoint for flowmeter data.
    server.on("/flowData", HTTP_GET, [](AsyncWebServerRequest *request) {
      if (request->hasParam("seconds") && request->hasParam("flow_voltage") && request->hasParam("flow_rate") && request->hasParam("flow_setpoint") && request->hasParam("valve1State") && request->hasParam("valve2State")) {

        flowmeterTime = request->getParam("seconds")->value().toFloat();
        flowmeterVoltage = request->getParam("flow_voltage")->value().toFloat();
        flowmeterRate = request->getParam("flow_rate")->value().toFloat();
        flowmeterSetpoint = request->getParam("flow_setpoint")->value().toFloat();
        flowmeterValve1 = request->getParam("valve1State")->value().toInt();
        flowmeterValve2 = request->getParam("valve2State")->value().toInt();

        flowmeterCommOn = true;

        String jsonResponse = "{";
        jsonResponse += "\"FlowTime\":" + String(flowmeterTime, 3) + ",";
        jsonResponse += "\"FlowVoltage\":" + String(flowmeterVoltage, 6) + ",";
        jsonResponse += "\"FlowRate\":" + String(flowmeterRate, 6) + ",";
        jsonResponse += "\"FlowSetpoint\":" + String(flowmeterSetpoint, 6) + ",";
        jsonResponse += "\"Valve1\":" + String(flowmeterValve1) + ",";
        jsonResponse += "\"Valve2\":" + String(flowmeterValve2);
        jsonResponse += "}";

        request->send(200, "text/plain", "Flowmeter data received");
      } else {
        request->send(400, "text/plain", "Missing one or more flowmeter parameters");
      }
    });

    // HTTP GET endpoint for flowmeter commands.
    server.on("/flowCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      if (pendingFlowmeterCommand.length() > 0) {
        String commandToSend = pendingFlowmeterCommand;
        pendingFlowmeterCommand = "";  // Clear after sending
        request->send(200, "application/json", commandToSend);
      } else {
        request->send(200, "application/json", "{}");
      }
    });
    // --- Agitator registers/pings the hub ---
    server.on("/agitatorHello", HTTP_GET, [](AsyncWebServerRequest *request) {
      // We can store remote IP if you want (not required for pull-model)
      IPAddress rip = request->client()->remoteIP();
      String msg = String("{\"hello\":\"agitator\",\"ip\":\"") + rip.toString() + "\"}";
      request->send(200, "application/json", msg);
    });

    // --- Agitator polls for commands (pull-model, like /flowCommand) ---
    server.on("/agitatorCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      if (pendingAgitatorCommand.length() > 0) {
        String cmd = pendingAgitatorCommand;
        pendingAgitatorCommand = "";       // one-shot
        request->send(200, "application/json", cmd);
      } else {
        request->send(200, "application/json", "{}");
      }
    });

    server.begin();
  } else {
      Serial.println("Failed to start SoftAP!");
      ESP.restart();
    }
}

// ------------------------------------------------------------------
// startWatchDog():
//   Initializes the watchdog timer.
// ------------------------------------------------------------------
void startWatchDog() {
  esp_task_wdt_config_t wdt_config = {
    .timeout_ms = WDT_TIMEOUT * 1000,
    .trigger_panic = true
  };
  esp_err_t init_result = esp_task_wdt_init(&wdt_config);
  if (init_result != ESP_OK) {
    Serial.printf("WDT initialization failed! Error: %s\n", esp_err_to_name(init_result));
  } else {
    Serial.println("WDT initialized successfully.");
  }
  esp_err_t add_result = esp_task_wdt_add(NULL);
  if (add_result != ESP_OK) {
    Serial.printf("Failed to add current task to WDT! Error: %s\n", esp_err_to_name(add_result));
  } else {
    Serial.println("Current task added to WDT monitoring.");
  }
}

// ------------------------------------------------------------------
// SETUP
// ------------------------------------------------------------------
void setup() {
  Serial.begin(115200);
  // Initialize the preferences in the "SensorHub" namespace.
  preferences.begin("SensorHub", false);
  // Load saved settings from flash.
  loadSettings();
  debugSettings();
  startWatchDog();
  startWiFi();
  sensorSerial.begin(9600, SERIAL_8N1, SENSOR_RX_PIN, SENSOR_TX_PIN);
}

// ------------------------------------------------------------------
// LOOP
// ------------------------------------------------------------------
void loop() {
  // Process USB serial commands.
  handleUSBCommands();

  // Process incoming UART data from sensor/device.
  handleSensorData();

  unsigned long now = millis();
  // Update sensor readings and cyclic parameters if not in bypass mode.
  if (!bypassMode && (now - lastDataMillis) >= dataDelay) {
    lastDataMillis = now;
    if (distanceSensorCommOn){
      checkDistanceSensorReference();
    }
    readAndBroadcastSensorData();
  }

  static unsigned long lastCyclicUpdate = 0;
  if (!bypassMode && (now - lastCyclicUpdate >= 2000)) {
    lastCyclicUpdate = now;
    delay(5);
    updateParametersCycle();
  }

  // Reset the watchdog timer
  esp_task_wdt_reset();
  delay(10);
}

// ------------------------------------------------------------------
// handleUSBCommands():
//   Process USB serial commands.
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
    processCommandData(data);
  }
}

// ------------------------------------------------------------------
// processCommandData(): Process incoming command (JSON or bypass).
// ------------------------------------------------------------------
String processCommandData(const String &data) {
  if (data.length() == 0) {
      return "Empty command";
  }
  if (data.startsWith("{") && data.endsWith("}")) {
      if (bypassMode) { bypassMode = false; }
      processJsonCommand(data);
      return "OK";
  } else {
      if (!bypassMode) { bypassMode = true; }
      String response = sendSensorCommand(data, true);
      Serial.println(response);
      return response;
  }
}


// ------------------------------------------------------------------
// processJsonCommand():
//   Manually parse JSON command and update parameters.
//   Also saves updated persistent settings.
// ------------------------------------------------------------------
void processJsonCommand(const String &json) {
  String value;

  // Reset command: restore variables to default and persist.
  if (json.indexOf("\"resetVariables\"") != -1) {
    tempReference        = 0.0f;
    pHReference          = 0.0f;
    pHError              = 0.17f;
    pHCal                = 5;
    pHOperation          = 5;
    pHMix                = 10;
    pHIntensity          = 990;
    motorRPM             = 0;
    nutriOperation       = 999;
    nutriMix             = 1;
    nutriOpCycle         = 500;
    nutriMixCycle        = 1;
    nutriIntensity       = 99;
    antifoamOperation    = 999;
    antifoamMix          = 1;
    antifoamIntensity    = 99;
    pressureReference    = 100;
    distanceSensorReference = 0.0f;
    dataDelay            = 1000;
    oxyOn                = false;
    tempOn               = false;
    phOn                 = false;
    nutrientOn           = false;
    antifoamOn           = false;
    pressureOn           = false;
    saveSettings();
    return;
  }
  if (json.indexOf("\"restart\"") != -1) {
    ESP.restart();
    return;
  }
  if (json.indexOf("\"comTest\"") != -1) {
    lastSensorJson = "OK";
    Serial.println(lastSensorJson);
    return;
  }
  if (json.indexOf("\"flowmeterComm\"") != -1) {
    value = getValueFromJson(json, "flowmeterComm");
    int commValue = value.toInt();
    flowmeterCommOn = (commValue != 0);
  }
  if (json.indexOf("\"distanceSensorComm\"") != -1) {
    value = getValueFromJson(json, "distanceSensorComm");
    int commValue = value.toInt();
    distanceSensorCommOn = (commValue != 0);
  }
  if (json.indexOf("\"distanceSensorReference\"") != -1) {
    value = getValueFromJson(json, "distanceSensorReference");
    distanceSensorReference = value.toFloat();
  }
  if (json.indexOf("\"dataDelay\"") != -1) {
    value = getValueFromJson(json, "dataDelay");
    int newDelay = value.toInt();
    dataDelay = (newDelay < 100) ? 100 : newDelay;
  }
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
  if (json.indexOf("\"pHCal\"") != -1) {
    value = getValueFromJson(json, "pHCal");
    float pHFloat = value.toFloat();
    pHCal = (int)round(pHFloat * 100);
    pHCalforUpdate = true;
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
  }
  if (json.indexOf("\"pressureReference\"") != -1) {
    value = getValueFromJson(json, "pressureReference");
    pressureReference = value.toInt();
    setPressure(pressureReference);
  }

  // Flowmeter-specific Commands: Build a JSON string manually for the flowmeter command.
  String flowmeterCommand = "";
  bool flowmeterCmdFound = false;
  if (json.indexOf("\"flowSetpoint\"") != -1) {
    flowmeterCommand += "\"flow_setpoint\":" + getValueFromJson(json, "flowSetpoint");
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

  // ----- Agitator & dosing config -----
  if (json.indexOf("\"agitatorAuto\"") != -1) {
    String v = getValueFromJson(json, "agitatorAuto");
    agitatorAuto = (v.toInt() != 0);
  }
  if (json.indexOf("\"agitatorReEnablePot\"") != -1) {
    String v = getValueFromJson(json, "agitatorReEnablePot");
    agitatorReEnablePot = (v.toInt() != 0);
  }
  if (json.indexOf("\"agitatorPercent\"") != -1) {
    String v = getValueFromJson(json, "agitatorPercent");
    int p = v.toInt();
    if (p < 0) p = 0; if (p > 100) p = 100;
    agitatorPercentFoam = p;
  }
  if (json.indexOf("\"agitatorDir\"") != -1) {
    String v = getValueFromJson(json, "agitatorDir");
    agitatorDirFoam = (v.toInt() != 0) ? 1 : 0;
  }
  // Manual agitator ON/OFF using current config (percent/dir/repot)
  if (json.indexOf("\"agitatorOn\"") != -1) {
    String v = getValueFromJson(json, "agitatorOn");
    bool on = (v.toInt() != 0);
    if (on) {
      // run with current percent & direction; disable local pot while ON
      queueAgitatorCmd((float)agitatorPercentFoam, agitatorDirFoam, 0);
    } else {
      // stop; optionally re-enable local pot
      queueAgitatorCmd(0.0f, agitatorDirFoam, agitatorReEnablePot ? 1 : 0);
    }
  }
  if (json.indexOf("\"foamStartDelay_s\"") != -1) {
    foamStartDelay_s = getValueFromJson(json, "foamStartDelay_s").toFloat();
    if (foamStartDelay_s < 0.0f) foamStartDelay_s = 0.0f;
  }
  if (json.indexOf("\"foamPulse_s\"") != -1) {
    foamPulse_s = getValueFromJson(json, "foamPulse_s").toFloat();
    if (foamPulse_s < 0.05f) foamPulse_s = 0.05f;
  }
  if (json.indexOf("\"foamInterval_s\"") != -1) {
    foamInterval_s = getValueFromJson(json, "foamInterval_s").toFloat();
    if (foamInterval_s < 0.05f) foamInterval_s = 0.05f;
  }

  // After processing a JSON command that changes configuration values, store them persistently.
  saveSettings();
}

// ------------------------------------------------------------------
// readAndBroadcastSensorData():
//   Read sensor values and build JSON response.
// ------------------------------------------------------------------
void readAndBroadcastSensorData() {
  delay(10);
  float temperatureVal = -1.0;
  if (tempOn) {
    String temperatureResp = sendSensorCommand("b", true);
    if (temperatureResp.length() > 0) {
      temperatureVal = temperatureResp.toFloat();
      if (temperatureVal < 0.0f || temperatureVal > 100.0f) {
        temperatureVal = -1.0f; // Sentinel for invalid
      }
    }
  }
  delay(10);
  float pHVal = -1.0;
  if (phOn) {
    String pHResp = sendSensorCommand("k", true);
    if (pHResp.length() > 0) {
      pHVal = pHResp.toFloat();
    }
  }
  delay(10);
  float oxyVal = -1.0;
  if (oxyOn) {
    String oxyResp = sendSensorCommand("g", true);
    if (oxyResp.length() > 0) {
      oxyVal = oxyResp.toFloat();
      if (oxyVal < 0.0f || oxyVal > 4095.0f) {
        oxyVal = -1.0f; // Sentinel for invalid ADC value
      }
    }
  }
  delay(10);
  float pressureVal = -1.0;
  if (pressureOn) {
    String pressureResp = sendSensorCommand("c", true);
    pressureVal = pressureResp.toFloat();
  }
  delay(10);
  float antifoamVal = -1.0;
  if (antifoamOn) {
    String antifoamResp = sendSensorCommand("e", true);
    antifoamVal = antifoamResp.toFloat();
  }
  delay(10);
  if (pHCalforUpdate) {
    String phStrCal = String(pHCal);
    sendSensorCommand(phStrCal + "W", false);
    pHCalforUpdate = false;
  }
  float timeSec = millis() / 1000.0;

  bool validDistance = false;
  if (distanceSensorCommOn) {
    unsigned long age = millis() - distanceSensorLastUpdate;
    if (age <= DISTANCE_TIMEOUT && distanceSensorValue >= 0.0f) {
      validDistance = true;
    } else {
      distanceSensorValue = -1.0f;  // Timeout or invalid/stagnated
    }
  }

  String jsonResponse = "{";
  jsonResponse += "\"Time\":" + String(timeSec, 1);
  jsonResponse += ",\"Tempval\":" + String(temperatureVal, 2);
  jsonResponse += ",\"pHval\":" + String(pHVal, 2);
  jsonResponse += ",\"Oxyval\":" + String(oxyVal, 1);
  jsonResponse += ",\"Antifoam\":" + String(antifoamVal, 0);
  jsonResponse += ",\"Pressure\":" + String(pressureVal, 1);
  if (flowmeterCommOn) {
    jsonResponse += ",\"FlowVoltage\":" + String(flowmeterVoltage, 6);
    jsonResponse += ",\"FlowRate\":" + String(flowmeterRate, 6);
    jsonResponse += ",\"FlowSetpoint\":" + String(flowmeterSetpoint, 6);
    jsonResponse += ",\"Valve1\":" + String(flowmeterValve1);
    jsonResponse += ",\"Valve2\":" + String(flowmeterValve2);
  }
  if (validDistance) {
    jsonResponse += ",\"Distance\":" + String(distanceSensorValue, 2);
  }
  jsonResponse += ",\"SensorCommOK\":" + String(uartSensorOK ? "true" : "false");
  jsonResponse += "}";

  lastSensorJson = jsonResponse;
  sampleId++;
  Serial.println(jsonResponse);
}

// Queue one JSON command for the Agitator; it will fetch via /agitatorCommand.
void queueAgitatorCmd(float pct, int dir, int activePot)
{
  if (pct < 0.0f) pct = 0.0f;
  if (pct > 100.0f) pct = 100.0f;
  dir = (dir != 0) ? 1 : 0;
  activePot = (activePot != 0) ? 1 : 0;

  char buf[96];
  snprintf(buf, sizeof(buf),
           "{\"RPM_percent\":%.1f,\"Dir\":%d,\"ActivePot\":%d}",
           pct, dir, activePot);
  pendingAgitatorCommand = String(buf);
}

// ------------------------------------------------------------------
// checkDistanceSensorReference()
//   Foam detection drives the Agitator (via /agitatorCommand) and
//   doses antifoam using X/Y/Z timers while foam persists.
//
//   foamStartDelay_s  (default 1 s)  - wait after detect
//   foamPulse_s       (default 1 s)  - ON pulse length
//   foamInterval_s    (default 5 s)  - cumulative foam-ON spacing
// ------------------------------------------------------------------
void checkDistanceSensorReference()
{
  // ---- persistent state for the state-machine ----
  static bool   foam             = false;    // debounced foam flag
  static bool   mixerOn          = false;    // agitator running due to foam
  static bool   dosingOn         = false;    // nutrient (foam) pump ON pulse
  static unsigned long lastMs    = 0;        // last loop timestamp
  static unsigned long sumOnMs   = 0;        // cumulative time while foam==true
  static unsigned long nextDoseCumMs = 0;    // next pulse start, in cumulative units
  static unsigned long doseStartMs   = 0;    // when current pulse started

  const unsigned long now = millis();
  if (lastMs == 0) lastMs = now;

  // --- local helpers (intensity-only writes so refresher can't re-arm) ---
  auto doseOff = [&]() {
    // OFF via "0M" (no N/O/P/Q to avoid re-arming)
    sendSensorCommand("0M", false);
    dosingOn = false;
  };
  auto doseOn = [&](int intensity /*0..99*/) {
    if (intensity < 0) intensity = 0;
    if (intensity > 99) intensity = 99;
    // ON via "<intensity>0M" only
    sendSensorCommand(String(intensity) + "0M", false);
    dosingOn = (intensity > 0);
  };
  auto stopMixer = [&]() {
    if (mixerOn && agitatorAuto) {
      queueAgitatorCmd(0.0f, agitatorDirFoam, agitatorReEnablePot ? 1 : 0);
      mixerOn = false;
    }
  };
  auto startMixer = [&]() {
    if (!mixerOn && agitatorAuto) {
      queueAgitatorCmd((float)agitatorPercentFoam, agitatorDirFoam, 0);
      mixerOn = true;
    }
  };
  auto resetTimers = [&]() {
    sumOnMs = 0;
    nextDoseCumMs = 0;
    lastMs = now;
  };
  auto allStop = [&]() {
    if (dosingOn) doseOff();
    stopMixer();
    foam = false;
    resetTimers();
  };

  // --- feature disabled: force hard stop and bail ---
  if (distanceSensorReference <= 0.0f) {
    allStop();
    return;
  }

  // --- data validity / timeout guard ---
  const bool dataStale = !distanceSensorCommOn ||
                         (now - distanceSensorLastUpdate > DISTANCE_TIMEOUT) ||
                         (distanceSensorValue < 0.0f);
  if (dataStale) {
    allStop();
    return;
  }

  // --- hysteresis around the reference to avoid chattering ---
  const float h = 0.5f;  // adjust to your sensor noise
  const float lower = distanceSensorReference - h;
  const float upper = distanceSensorReference + h;
  const float d     = distanceSensorValue;

  bool foamFlag;
  if (d > 0.0f && d < lower)      foamFlag = true;
  else if (d > upper)             foamFlag = false;
  else                            foamFlag = foam;   // inside band: keep last state

  // --- accumulate time only while foam is present ---
  const unsigned long dt = (now - lastMs);
  if (foamFlag) sumOnMs += dt;
  lastMs = now;

  // --- rising edge (foam just detected) ---
  if (foamFlag && !foam) {
    startMixer();           // run agitator during foam
    resetTimers();          // cumulative timer counts foam ON-time only
    nextDoseCumMs = (unsigned long)(foamStartDelay_s * 1000.0f); // first pulse after X
    if (dosingOn) doseOff();  // ensure pump is OFF before starting schedule
  }

  // --- falling edge (foam cleared) ---
  if (!foamFlag && foam) {
    if (dosingOn) doseOff();
    stopMixer();
    resetTimers();
  }

  // commit new foam state
  foam = foamFlag;

  // --- while foam persists: schedule and run pulses ---
  if (foam) {
    // start a new pulse when the cumulative-on time reaches the next slot
    if (!dosingOn && sumOnMs >= nextDoseCumMs) {
      doseOn(99);  // start pulse (only M command)
      doseStartMs = now;
      nextDoseCumMs += (unsigned long)(foamInterval_s * 1000.0f);  // schedule next slot (Z)
    }
  }

  // --- end current pulse after Y seconds ---
  if (dosingOn && (now - doseStartMs >= (unsigned long)(foamPulse_s * 1000.0f))) {
    doseOff();
  }
}

// ------------------------------------------------------------------
// setMotor():
//   Send motor control command via sensor protocol.
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
// setTemperature():
//   Configure temperature sensor command.
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
// setPH():
//   Configure pH sensor commands.
// ------------------------------------------------------------------
void setPH(float pH, float err, int op, int mix, int intensity) {
  if (pH < 0.0f || pH > 14.0f) { pH = 7.00f; }
  if (err <= 0.0f || err >= 2.0f) { err = 0.17f; }
  if (op < 1 || op >= 1000) { op = 5; }
  if (mix < 1 || mix >= 1000) { mix = 5; }
  if (intensity < 0 || intensity >= 990) { intensity = 990; }
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
//   Configure nutrient pump commands.
// ------------------------------------------------------------------
void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity) {
  // Keep previous values if caller gives out-of-range (esp. for OFF)
  if (op < 1 || op >= 1000)      op = nutriOperation;
  if (mix < 1 || mix >= 1000)    mix = nutriMix;
  if (opCycle < 1 || opCycle > 500) opCycle = nutriOpCycle;
  if (mixCycle < 1 || mixCycle >= 1000) mixCycle = nutriMixCycle;
  if (intensity < 0) intensity = 0;
  if (intensity > 99) intensity = 99;

  // Update state
  nutriOperation = op;
  nutriMix = mix;
  nutriOpCycle = opCycle;
  nutriMixCycle = mixCycle;
  nutriIntensity = intensity;

  if (intensity == 0) {
    nutrientOn = false;
    sendSensorCommand("0M", false);
    return;
  }

  nutrientOn = true;
  sendSensorCommand(String(nutriIntensity) + "0M", false);
  sendSensorCommand(String(nutriOperation) + "N", false);
  sendSensorCommand(String(nutriMix) + "O", false);
  sendSensorCommand(String(nutriOpCycle) + "P", false);
  sendSensorCommand(String(nutriMixCycle) + "Q", false);
}


inline void setNutrientIntensityOnly(int intensity) {
  if (intensity < 0) intensity = 0;
  if (intensity > 99) intensity = 99;
  nutriIntensity = intensity;
  nutrientOn = (intensity != 0);
  if (intensity == 0) {
    sendSensorCommand("0M", false);                 // OFF - single command
  } else {
    sendSensorCommand(String(intensity) + "0M", false); // ON with given % (0..99)
  }
}

// ------------------------------------------------------------------
// setAntifoam():
//   Configure antifoam sensor commands.
// ------------------------------------------------------------------
void setAntifoam(int op, int mix, int intensity) {
  if (op < 1 || op >= 1000) { op = 999; }
  if (mix < 1 || mix >= 1000) { mix = 1; }
  if (intensity < 0 || intensity >= 99) { intensity = 99; }
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
// setPressure():
//   Configure pressure sensor command.
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
// handleSensorData(): Process incoming UART data from sensor/device.
// ------------------------------------------------------------------
String handleSensorData() {
  while (sensorSerial.available() > 0) {
    char c = sensorSerial.read();

    // Check if the buffer is growing too large before adding the character.
    if (sensorBuffer.length() >= MAX_SENSOR_BUFFER_LENGTH) {
      sensorBuffer = "";
    }
    sensorBuffer += c;
    // Check for the end-of-line to process the message
    if (c == '\n' || c == '\r') {
      sensorBuffer.trim();
      if (sensorBuffer.length() > 0) {
        Serial.println(sensorBuffer);
      }
      sensorBuffer = "";
    }
  }
  return sensorBuffer;
}

unsigned long lastResetTime = 0;  // global or static variable
const unsigned long RESET_INTERVAL = 10000; // 5 seconds

String sendSensorCommand(const String &cmd, bool readResponse) {
  flushSerial(sensorSerial);  // Clear buffer before sending new command
  sensorSerial.print(cmd);
  delay(20);
  if (readResponse) {
    sensorSerial.setTimeout(100);
    String response = sensorSerial.readStringUntil('\r');
    response.trim();
    bool valid = response.length() > 0 && isDigit(response.charAt(0));
    if (valid) {
      uartSensorOK = true;
      uartFailureCount = 0;

      // special case: oxygen calibration
      if (cmd == "g") {
        float rawValue = response.toFloat();
        float calibrated = a * rawValue + b;
        return bypassMode ? String(calibrated, 1) : String(rawValue, 4);
      }

      return response;
    } else {
      uartFailureCount++;
      if (uartFailureCount >= UART_FAILURE_THRESHOLD) {
        unsigned long now = millis();
        if (now - lastResetTime >= RESET_INTERVAL) {
          uartSensorOK = false;
          resetSensorSerial();
          uartFailureCount = 0;
          lastResetTime = now;  // update reset timestamp
        }
      }
      return ""; // return empty string to calling function
    }
  } else {
    // Increase delay to provide sensor extra time to process the command
    delay(10);
    return "";
  }
}

// ------------------------------------------------------------------
// removeDecimal():
//   Remove the decimal point from a numeric string.
// ------------------------------------------------------------------
String removeDecimal(const String &s) {
  String ret = s;
  ret.replace(".", "");
  return ret;
}

// ------------------------------------------------------------------
// updateParametersCycle():
//   In normal mode, sends one command per active parameter in round-robin order.
// ------------------------------------------------------------------
void updateParametersCycle() {
  if (bypassMode) return;
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
  currentIndex %= cmds.size();
  String cmd = cmds[currentIndex];
  sendSensorCommand(cmd, false);
  delay(20);
  currentIndex = (currentIndex + 1) % cmds.size();
}

void flushSerial(Stream &serial) {
  while (serial.available() > 0) {
    serial.read();
  }
}

void resetSensorSerial() {
  sensorSerial.end();
  Serial.println("UART failure detected. Reinitializing sensor UART.");
  delay(100);
  sensorSerial.begin(9600, SERIAL_8N1, SENSOR_RX_PIN, SENSOR_TX_PIN);
  delay(100);
}
