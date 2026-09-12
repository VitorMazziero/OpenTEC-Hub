#include <Adafruit_MCP4725.h>
#include <Adafruit_ADS1X15.h>
#include <Preferences.h>
#include <BluetoothSerial.h>
#include <WiFi.h>
#include <esp_now.h>

// ADC (ADS1115) and DAC (MCP4725) initialization
Adafruit_ADS1115 ads;
Adafruit_MCP4725 mcp;
BluetoothSerial SerialBT;

// DAC resolution
#define DAC_RESOLUTION 12

// Define connections
#define VALVE1_PIN 2
#define VALVE2_PIN 4
#define RECEIVER_LED 19

// Variables for valve states
uint8_t valve1State = 1;
uint8_t valve2State = 1;

// Sample size for moving average filter
#define NUM_SAMPLES 10
float samples[NUM_SAMPLES];
uint8_t currentSampleIndex = 0;

// Derivative filter parameters
float previousFilteredValue = 0.0;
float previousValue = 0.0;
float previousFirstDerivative = 0.0;
float previousSecondDerivative = 0.0;
float firstDerivativeSmoothed = 0.0;
float secondDerivativeSmoothed = 0.0;
const float first_derivative_threshold_low = 0.00001;
const float first_derivative_threshold_high = 0.001;
const float second_derivative_threshold_low = 0.00001;
const float second_derivative_threshold_high = 0.0003;

// Calibration parameters
float a, b, c;

// Flow meter variables
float flowVoltage = 0.0;
float flowRate = 0.0;
float flowSetpoint = 0.0;
float maxFlowRate = 50.0;

// LED receiver parameters
bool ledBlinking = false;
unsigned long blinkStartTime = 0;
const unsigned long blinkInterval = 200;
uint8_t blinkCount = 0;

// Low-pass filter parameters
const float alpha = 0.05;
float lowPassPreviousFilteredValue = 0;

// Data transmission interval
unsigned long dataInterval = 250;
char outputMessage[200]; // Preallocate memory for output message

// Preferences for storing calibration parameters
Preferences preferences;

// ESP-NOW peer information
esp_now_peer_info_t peerInfo;
uint8_t broadcastAddress[] = {0x24, 0x6F, 0x28, 0xAD, 0x67, 0x1C};  // Replace with the MAC address of the pressure sensor

// Function Prototypes
void readAndProcessADC();
float lowPassFilter(float newValue, float alpha);
float movingAverageFilter(float newValue);
float calculateFirstDerivative(float newValue);
float calculateSecondDerivative(float firstDerivative);
void readBluetoothData();
void writeFlowSetpointToDAC(float flowSetpoint);
void startLEDBlinking();
void updateLEDBlinking();
void loadParameters();
void OnDataSent(const uint8_t *mac_addr, esp_now_send_status_t status);
void OnDataRecv(const uint8_t *mac_addr, const uint8_t *incomingData, int len);

// Define the structure to hold the JSON string
typedef struct struct_message {
    char json[50];  // Adjust size based on expected JSON length
} struct_message;

// Create an instance of the structure
struct_message myData;

void setup() {
  // Initialize serial communication
  Serial.begin(115200);
  SerialBT.begin("Flowmeter 50L/min");

  // Configure Outputs
  pinMode(VALVE1_PIN, OUTPUT);
  pinMode(VALVE2_PIN, OUTPUT);
  digitalWrite(VALVE1_PIN, valve1State);
  digitalWrite(VALVE2_PIN, valve2State);
  pinMode(RECEIVER_LED, OUTPUT);
  digitalWrite(RECEIVER_LED, LOW);

  // ADS1115 setup
  if (!ads.begin(0x48)) {
    Serial.println("Failed to initialize ADS.");
  } else {
    Serial.println("ADC ADS1115 initialized");
  }

  // MCP4725 setup
  if (!mcp.begin(0x60)) {
    Serial.println("Failed to find MCP4725 chip");
  } else {
    Serial.println("DAC MCP4725 initialized");
  }

  // Load calibration parameters from NVS
  loadParameters();

  // Initialize Wi-Fi in STA mode
  WiFi.mode(WIFI_STA);
  if (WiFi.status() == WL_DISCONNECTED) {
    WiFi.begin();
    delay(100);
  }
  if (esp_now_init() != ESP_OK) {
    Serial.println("Error initializing ESP-NOW");
    return;
  }
  esp_now_register_recv_cb(OnDataRecv);

  // Register peer
  memcpy(peerInfo.peer_addr, broadcastAddress, 6);
  peerInfo.channel = 0;
  peerInfo.encrypt = false;
  if (esp_now_add_peer(&peerInfo) != ESP_OK) {
    Serial.println("Failed to add peer");
    return;
  }

  Serial.print("ESP32 MAC Address: ");
  Serial.println(WiFi.macAddress());
  
}

void loop() {
  // Read Bluetooth data and update LED blinking
  readBluetoothData();
  updateLEDBlinking();

  // Read data from flowmeter and process it
  if (mcp.begin(0x60)) readAndProcessADC();

  // Calculate the elapsed time in seconds
  float seconds = millis() / 1000.0;

  // Create and send the JSON string
  snprintf(outputMessage, sizeof(outputMessage),
           "{\"seconds\":%.3f,\"flow_voltage\":%.6f,\"flow_rate\":%.6f,\"valve1State\":%d,\"valve2State\":%d}",
           seconds, flowVoltage, flowRate, valve1State, valve2State);

  Serial.println(outputMessage);
  SerialBT.println(outputMessage);

  // Format the flow rate into the JSON string and assign it to myData.json
  snprintf(myData.json, sizeof(myData.json), "{\"flow_rate\":%.6f}", flowRate);

  // Check if the ESP-NOW peer exists before sending the message
  if (esp_now_is_peer_exist(broadcastAddress)) {
    // Send the JSON string via ESP-NOW
    esp_err_t result = esp_now_send(broadcastAddress, (uint8_t *) &myData, sizeof(myData));

    if (result == ESP_OK) {
      Serial.println("Sent with success");
    } else {
      Serial.println("Error sending the data");
    }
  } else {
    Serial.println("ESP-NOW peer not available");
  }

  delay(dataInterval);
}

// Function to read Bluetooth data and update variables accordingly
void readBluetoothData() {
  if (SerialBT.available()) {
    String data = SerialBT.readStringUntil('\n');
    data.trim();

    if (data[0] == '{' && data[data.length() - 1] == '}') {
      data = data.substring(1, data.length() - 1);
      int start = 0;
      while (start < data.length()) {
        int colonIndex = data.indexOf(':', start);
        int commaIndex = data.indexOf(',', start);

        if (commaIndex == -1) {
          commaIndex = data.length();
        }

        if (colonIndex != -1 && colonIndex < commaIndex) {
          String key = data.substring(start, colonIndex);
          String value = data.substring(colonIndex + 1, commaIndex);
          key.trim();
          value.trim();

          if (key == "v1_aux") {
            valve1State = value.toInt();
            digitalWrite(VALVE1_PIN, valve1State);
          } else if (key == "v2_opt") {
            valve2State = value.toFloat();
            digitalWrite(VALVE2_PIN, valve2State);
          } else if (key == "flow_setpoint") {
            flowSetpoint = value.toFloat();
            writeFlowSetpointToDAC(flowSetpoint);
          } else if (key == "max_flow") {
            maxFlowRate = value.toFloat();
          } else if (key == "data_interval") {
            dataInterval = value.toFloat();
          } else if (key == "a") {
            a = value.toFloat();
            preferences.putFloat("a", a);
          } else if (key == "b") {
            b = value.toFloat();
            preferences.putFloat("b", b);
          } else if (key == "c") {
            c = value.toFloat();
            preferences.putFloat("c", c);
          }
          start = commaIndex + 1;
        } else {
          Serial.println("Error 001: Command with invalid format");
          break;
        }
      }
      startLEDBlinking();
    }
  }
}

// Function to write the flow setpoint to the DAC
void writeFlowSetpointToDAC(float flowSetpoint) {
  int dacValue = (flowSetpoint / maxFlowRate) * 4095;
  dacValue = constrain(dacValue, 0, 4095);
  mcp.setVoltage(dacValue, false);
}

// Function to read ADC value, apply filters, and calculate flow rate
void readAndProcessADC() {
  int16_t adc0 = ads.readADC_SingleEnded(0);
  float newFlowVoltage = ads.computeVolts(adc0);

  newFlowVoltage = lowPassFilter(newFlowVoltage, alpha);
  newFlowVoltage = movingAverageFilter(newFlowVoltage);

  float firstDerivative = calculateFirstDerivative(newFlowVoltage);
  float secondDerivative = calculateSecondDerivative(firstDerivative);

  firstDerivativeSmoothed = alpha * firstDerivative + (1 - alpha) * firstDerivativeSmoothed;
  secondDerivativeSmoothed = alpha * secondDerivative + (1 - alpha) * secondDerivativeSmoothed;

  if ((firstDerivativeSmoothed < first_derivative_threshold_low || firstDerivativeSmoothed > first_derivative_threshold_high) &&
      (secondDerivativeSmoothed < second_derivative_threshold_low || secondDerivativeSmoothed > second_derivative_threshold_high)) {
    flowVoltage = lowPassFilter(newFlowVoltage, alpha);
  } else {
    flowVoltage = lowPassFilter(previousFilteredValue, alpha);
  }

  previousFilteredValue = flowVoltage;
  flowRate = a * flowVoltage * flowVoltage + b * flowVoltage + c;
}

// Function to start the LED blinking sequence
void startLEDBlinking() {
  ledBlinking = true;
  blinkStartTime = millis();
  blinkCount = 0;
  digitalWrite(RECEIVER_LED, HIGH);
}

// Function to update the LED blinking state
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

// Function to apply a low-pass filter
float lowPassFilter(float newValue, float alpha) {
  float filteredValue = alpha * newValue + (1 - alpha) * lowPassPreviousFilteredValue;
  lowPassPreviousFilteredValue = filteredValue;
  return filteredValue;
}

// Function to apply a moving average filter
float movingAverageFilter(float newValue) {
  samples[currentSampleIndex] = newValue;
  currentSampleIndex = (currentSampleIndex + 1) % NUM_SAMPLES;
  float sum = 0;
  for (uint8_t i = 0; i < NUM_SAMPLES; i++) {
    sum += samples[i];
  }
  return sum / NUM_SAMPLES;
}

// Function to calculate the first derivative
float calculateFirstDerivative(float newValue) {
  float firstDerivative = newValue - previousValue;
  previousValue = newValue;
  return firstDerivative;
}

// Function to calculate the second derivative
float calculateSecondDerivative(float firstDerivative) {
  float secondDerivative = firstDerivative - previousFirstDerivative;
  previousFirstDerivative = firstDerivative;
  return secondDerivative;
}

// Function to load calibration parameters from NVS
void loadParameters() {
  preferences.begin("calibration", true);
  a = preferences.getFloat("a1", 8.9195849710);
  b = preferences.getFloat("b1", -92.4280419304);
  c = preferences.getFloat("c1", 174.7609071901);
  preferences.end();
}

// Callback when data is received
void OnDataRecv(const esp_now_recv_info *info, const uint8_t *incomingData, int len) {
  Serial.print("Bytes received: ");
  Serial.println(len);
  
  // Copy received data into a buffer and convert to String
  char receivedData[len + 1];
  memcpy(receivedData, incomingData, len);
  receivedData[len] = '\0';  // Null-terminate the string
  String data = String(receivedData);
  data.trim();

  if (data[0] == '{' && data[data.length() - 1] == '}') {
    data = data.substring(1, data.length() - 1);
    int start = 0;
    while (start < data.length()) {
      int colonIndex = data.indexOf(':', start);
      int commaIndex = data.indexOf(',', start);

      if (commaIndex == -1) {
        commaIndex = data.length();
      }

      if (colonIndex != -1 && colonIndex < commaIndex) {
        String key = data.substring(start, colonIndex);
        String value = data.substring(colonIndex + 1, commaIndex);
        key.trim();
        value.trim();

        if (key == "v1_aux") {
          valve1State = value.toInt();
          digitalWrite(VALVE1_PIN, valve1State);
        } else if (key == "v2_opt") {
          valve2State = value.toFloat();
          digitalWrite(VALVE2_PIN, valve2State);
        } else if (key == "flow_setpoint") {
          flowSetpoint = value.toFloat();
          writeFlowSetpointToDAC(flowSetpoint);
        } else if (key == "data_interval") {
          dataInterval = value.toFloat();
        }
        start = commaIndex + 1;
      } else {
        Serial.println("Error 001: Command with invalid format");
        break;
      }
    }
    startLEDBlinking();
  }
}
