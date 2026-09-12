/*************************************************************
 * Flowmeter ESP32 Code (Simulation Mode for Server Testing)
 * - Bypasses the ADC (ADS1115) and DAC (MCP4725) hardware.
 * - Uses simulated placeholder values for the measured variables.
 * - Communicates via Bluetooth Serial.
 * - Connects to WiFi as a station (to join Sensor Hub’s SoftAP).
 * - Periodically sends sensor data (seconds, readFlowVoltage, readFlowRate,
 *   flowSetpoint, valve1State, valve2State) to the Sensor Hub via an HTTP GET request.
 * - Periodically polls the Sensor Hub for any pending commands via /flowCommand.
 *************************************************************/
#include <Adafruit_MCP4725.h>
#include <Adafruit_ADS1X15.h>
#include <Preferences.h>
#include <BluetoothSerial.h>
#include <WiFi.h>
#include <Wire.h>
#include <HTTPClient.h>
#include <math.h>  // for sin()

// ----- WiFi Credentials (match the Sensor Hub AP) -----
#define WIFI_SSID     "ModuloTECNAL"
#define WIFI_PASSWORD "ModuloTECNAL"

// Create ADC and DAC objects (not used in simulation mode)
Adafruit_ADS1115 ads;
Adafruit_MCP4725 mcp;
BluetoothSerial SerialBT;

// Define pins for valve control and LED indicator
#define VALVE1_PIN 2
#define VALVE2_PIN 4
#define RECEIVER_LED 19

uint8_t valve1State = 0;
uint8_t valve2State = 0;

#define NUM_SAMPLES 10
float samples[NUM_SAMPLES];
uint8_t currentSampleIndex = 0;

// Calibration parameters (placeholders)
float k1, f1, c1;
float k2, f2, c2;

// Flow meter variables (placeholders)
float readFlowVoltage = 0.0;
float readFlowRate = 0.0;
float maxFlowRate = 50.0;    // Maximum flow rate (L/min)
float flowSetpoint = 0.0;

// LED blinking variables
bool ledBlinking = false;
unsigned long blinkStartTime = 0;
const unsigned long blinkInterval = 200;
uint8_t blinkCount = 0;

float lowPassPreviousFilteredValue = 0;

// Data transmission interval (ms)
unsigned long dataInterval = 250;

// Buffer for JSON output
char outputMessage[300];

// Preferences for calibration parameters
Preferences preferences;

// ----- WiFi / HTTP variables -----
String sensorHubURL = "http://192.168.4.1";  // Sensor Hub IP (SoftAP default)
unsigned long lastHTTPDataTime = 0;
unsigned long lastCommandPollTime = 0;
const unsigned long commandPollInterval = 250;  // Poll every 250 msec for pending commands

// ----- Simulation mode flag -----
// When true, the ADC and DAC functions are bypassed and simulated values are used.
bool simulationMode = true;

// Forward declarations
void readAndProcessADC();
float lowPassFilter(float newValue, float alpha);
float movingAverageFilter(float newValue);
void readSerialData();
void writeFlowSetpointToDAC(float flowSetpoint);
void startLEDBlinking();
void updateLEDBlinking();
void loadParameters();
void processReceivedData(String data);

void setup() {
  Serial.begin(115200);
  SerialBT.begin("Flowmeter 50L/min");

  // Connect to WiFi (Sensor Hub SoftAP)
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  Serial.print("Connecting to WiFi");
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
  }
  Serial.println();
  Serial.print("Connected. IP address: ");
  Serial.println(WiFi.localIP());

  // Initialize valve pins and LED
  pinMode(VALVE1_PIN, OUTPUT);
  pinMode(VALVE2_PIN, OUTPUT);
  digitalWrite(VALVE1_PIN, valve1State);
  digitalWrite(VALVE2_PIN, valve2State);
  pinMode(RECEIVER_LED, OUTPUT);
  digitalWrite(RECEIVER_LED, LOW);

  if (!simulationMode) {
    // Initialize ADS1115 ADC
    if (!ads.begin(0x48)) {
      Serial.println(F("Failed to initialize ADS1115."));
    } else {
      Serial.println(F("ADC ADS1115 initialized"));
    }
    ads.setGain(GAIN_ONE); // ±4.096V
    ads.setDataRate(RATE_ADS1115_860SPS);

    // Initialize MCP4725 DAC
    if (!mcp.begin(0x60)) {
      Serial.println(F("Failed to find MCP4725 chip"));
    } else {
      Serial.println(F("DAC MCP4725 initialized"));
    }
  } else {
    Serial.println("Simulation mode enabled: ADC and DAC bypassed.");
  }

  // Load calibration parameters (if any)
  if (!preferences.begin("calibration", false)) {
    Serial.println(F("Failed to initialize Preferences"));
  }
  loadParameters();
  writeFlowSetpointToDAC(flowSetpoint);
}

void loop() {
  readSerialData();
  updateLEDBlinking();
  readAndProcessADC();

  // Build JSON output message
  float seconds = millis() / 1000.0;
  snprintf(outputMessage, sizeof(outputMessage),
           "{\"seconds\":%.3f,\"flow_voltage\":%.6f,\"flow_rate\":%.6f,\"flow_setpoint\":%.6f,\"valve1State\":%d,\"valve2State\":%d}",
           seconds, readFlowVoltage, readFlowRate, flowSetpoint, valve1State, valve2State);
  
  Serial.println(outputMessage);
  SerialBT.println(outputMessage);

  // Send sensor data to Sensor Hub via HTTP GET periodically
  if (millis() - lastHTTPDataTime >= dataInterval) {
    lastHTTPDataTime = millis();
    HTTPClient http;
    // Construct URL with query parameters (simple URL concatenation)
    String url = sensorHubURL + "/flowData?seconds=" + String(seconds, 3) +
                 "&flow_voltage=" + String(readFlowVoltage, 6) +
                 "&flow_rate=" + String(readFlowRate, 6) +
                 "&flow_setpoint=" + String(flowSetpoint, 6) +
                 "&valve1State=" + String(valve1State) +
                 "&valve2State=" + String(valve2State);
    http.begin(url);
    int httpResponseCode = http.GET();
    http.end();
  }
  
  // Poll Sensor Hub for any pending flowmeter command every commandPollInterval
  if (millis() - lastCommandPollTime >= commandPollInterval) {
    lastCommandPollTime = millis();
    HTTPClient http;
    String url = sensorHubURL + "/flowCommand";
    http.begin(url);
    int httpResponseCode = http.GET();
    if (httpResponseCode == 200) {
      String commandPayload = http.getString();
      commandPayload.trim();
      if (commandPayload.length() > 2) { // assume non-empty JSON (e.g., "{}" is empty)
        Serial.println("Received flowmeter command: " + commandPayload);
        processReceivedData(commandPayload);
      }
    } else {
      Serial.println("Error polling flow command: " + String(httpResponseCode));
    }
    http.end();
  }

  delay(dataInterval);
}

void readSerialData() {
  if (SerialBT.available() || Serial.available()) {
    String data;
    if (SerialBT.available()) {
      data = SerialBT.readStringUntil('\n');
    } else {
      data = Serial.readStringUntil('\n');
    }
    processReceivedData(data);
  }
}

void processReceivedData(String data) {
  data.trim();
  Serial.println(data);
  if (data.length() < 2) {
    Serial.println(F("Error: Received data too short"));
    return;
  }
  // Process incoming JSON command (expected format similar to:
  // {"v1_aux":<valve1State>, "v2_opt":<valve2State>, "flow_setpoint":<value>, ... })
  if (data.startsWith("{") && data.endsWith("}")) {
    data = data.substring(1, data.length() - 1);
    int start = 0;
    bool preferencesOpen = false;
    while (start < data.length()) {
      int colonIndex = data.indexOf(':', start);
      int commaIndex = data.indexOf(',', start);
      if (colonIndex == -1) {
        Serial.println(F("Error: Colon not found in command"));
        break;
      }
      if (commaIndex == -1) { commaIndex = data.length(); }
      if (colonIndex < commaIndex) {
        String key = data.substring(start, colonIndex);
        String value = data.substring(colonIndex + 1, commaIndex);
        key.trim();
        value.trim();
        if (key == "\"v1_aux\"") {
          valve1State = value.toInt();
          digitalWrite(VALVE1_PIN, valve1State);
        } else if (key == "\"v2_opt\"") {
          valve2State = value.toInt();
          digitalWrite(VALVE2_PIN, valve2State);
        } else if (key == "\"flow_setpoint\"") {
          flowSetpoint = value.toFloat();
          writeFlowSetpointToDAC(flowSetpoint);
        } else if (key == "\"max_flow\"") {
          maxFlowRate = value.toFloat();
        } else if (key == "\"data_interval\"") {
          unsigned long newInterval = value.toFloat();
          if (newInterval >= 50 && newInterval <= 1000) {
            dataInterval = newInterval;
          } else {
            Serial.println(F("Error: data_interval out of bounds"));
          }
        } else if (key == "k1" || key == "f1" || key == "c1" ||
                   key == "k2" || key == "f2" || key == "c2") {
          if (!preferencesOpen) {
            if (preferences.begin("calibration", false)) { preferencesOpen = true; }
            else { Serial.println(F("Error: Unable to open Preferences for writing")); }
          }
          if (preferencesOpen) {
            float paramValue = value.toFloat();
            if (key == "k1") { k1 = paramValue; preferences.putFloat("k1", k1); }
            else if (key == "f1") { f1 = paramValue; preferences.putFloat("f1", f1); }
            else if (key == "c1") { c1 = paramValue; preferences.putFloat("c1", c1); }
            else if (key == "k2") { k2 = paramValue; preferences.putFloat("k2", k2); }
            else if (key == "f2") { f2 = paramValue; preferences.putFloat("f2", f2); }
            else if (key == "c2") { c2 = paramValue; preferences.putFloat("c2", c2); }
          }
        }
        start = commaIndex + 1;
      } else {
        Serial.println(F("Error: Command with invalid format"));
        break;
      }
    }
    if (preferencesOpen) { preferences.end(); }
    startLEDBlinking();
  }
  else{ 
    Serial.println("Not with {}");
  }
}

void writeFlowSetpointToDAC(float flowSetpoint) {
  if (simulationMode) {
    Serial.print("Simulated DAC set to flowSetpoint: ");
    Serial.println(flowSetpoint);
    return;
  }
  uint16_t dacValue = (flowSetpoint / maxFlowRate) * 4095;
  dacValue = constrain(dacValue, 0, 4095);
  Serial.print("Setting DAC value: ");
  Serial.println(dacValue);
  mcp.setVoltage(dacValue, false);
  Wire.beginTransmission(0x60);
  uint8_t error = Wire.endTransmission();
  if (error != 0) {
    Serial.print("I2C error after setting DAC value: ");
    Serial.println(error);
  }
}

void readAndProcessADC() {
  if (simulationMode) {
    // Simulate a fluctuating voltage using a sine wave (in volts)
    float simulatedVoltage = 2.5 + 0.5 * sin(millis() / 1000.0);
    readFlowVoltage = simulatedVoltage;
    // Simulate flow rate (L/min) based on the simulated voltage
    readFlowRate = 10.0 + 5.0 * sin(millis() / 1500.0);
    return;
  }
  
  // If not in simulation mode, perform an actual ADC read:
  int16_t adc0 = ads.readADC_Differential_0_1();
  if (adc0 == 32767 || adc0 == -32768) {
    Serial.println(F("Error reading ADC"));
    return;
  }
  float newFlowVoltage = ads.computeVolts(adc0);
  newFlowVoltage = lowPassFilter(newFlowVoltage, 0.25);
  newFlowVoltage = movingAverageFilter(newFlowVoltage);
  readFlowVoltage = newFlowVoltage;
  if (readFlowVoltage <= 0.05)
    readFlowRate = k1 * readFlowVoltage * readFlowVoltage + f1 * readFlowVoltage + c1;
  else
    readFlowRate = k2 * readFlowVoltage * readFlowVoltage + f2 * readFlowVoltage + c2;
  Serial.printf("Calculated Flow Rate: %.6f L/min\n", readFlowRate);
  if (readFlowRate <= 0) { readFlowRate = 0.0; }
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
  for (uint8_t i = 0; i < NUM_SAMPLES; i++) {
    sum += samples[i];
  }
  return sum / NUM_SAMPLES;
}

void loadParameters() {
  if (preferences.begin("calibration", true)) {
    k1 = preferences.getFloat("k1", -462.6972923131);
    f1 = preferences.getFloat("f1", 48.3343019646);
    c1 = preferences.getFloat("c1", -0.7144184687);
    k2 = preferences.getFloat("k2", -0.751252929);
    f2 = preferences.getFloat("f2", 10.8050249361);
    c2 = preferences.getFloat("c2", -0.0040361693);
    preferences.end();
    Serial.println("Calibration Constants Loaded:");
    Serial.printf("k1: %.6f, f1: %.6f, c1: %.6f\n", k1, f1, c1);
    Serial.printf("k2: %.6f, f2: %.6f, c2: %.6f\n", k2, f2, c2);
  } else {
    Serial.println(F("Failed to open preferences"));
  }
}
