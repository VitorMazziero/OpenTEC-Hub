/*************************************************************
 * Flowmeter ESP32 Code (Continuous ADC Reading)
 * - Reads ADC via ADS1115 using continuous, interrupt-driven sampling.
 * - Controls DAC via MCP4725.
 * - Communicates via Bluetooth Serial.
 * - Connects to a specific WiFi (Sensor Hub’s SoftAP) only if available.
 * - Periodically sends sensor data to the Sensor Hub via HTTP GET.
 * - Periodically polls the Sensor Hub for pending commands.
 * - USB and Bluetooth communications continue regardless of WiFi status.
 * - Calibration parameters are stored using EEPROM.
 * - ADC settings are adjusted for improved precision and filtering.
 *************************************************************/
#include <Adafruit_MCP4725.h>
#include <Adafruit_ADS1X15.h>
#include <BluetoothSerial.h>
#include <WiFi.h>
#include <Wire.h>
#include <HTTPClient.h>
#include <nvs_flash.h>  // For NVS initialization
#include <EEPROM.h>     // For EEPROM-based calibration storage

// ----- WiFi Credentials (only connect to this network) -----
String currentSSID = "";
String currentPassword = "";

// Global variables for WiFi management
unsigned long lastWiFiCheckTime = 0;
const unsigned long wifiCheckInterval = 5000;  // 5 seconds between scans

// ADC (ADS1115) and DAC (MCP4725) initialization
Adafruit_ADS1115 ads;
Adafruit_MCP4725 mcp;
BluetoothSerial SerialBT;

// ----- ADC Interrupt Handling -----
// Pin connected to the ALERT/RDY signal for new sample notification.
#define READY_PIN 4 // Using GPIO 4 for the ready signal

// This is required on ESP32 to put the ISR in IRAM.
#ifndef IRAM_ATTR
#define IRAM_ATTR
#endif

// Flag to indicate a new ADC sample is ready
volatile bool new_data_available = false;

// ISR to be called when the ADC has a new sample ready
void IRAM_ATTR NewDataReadyISR() {
  new_data_available = true;
}

// Define pins
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

// Global calibration parameters (used in calculations)
float k1, f1, c1;
float k2, f2, c2;

// Flow meter variables
float readFlowVoltage = 0.0;
float readFlowRate = 0.0;
float maxFlowRate = 50.0;
float flowSetpoint = 0.0;

// LED blinking variables
bool ledBlinking = false;
unsigned long blinkStartTime = 0;
const unsigned long blinkInterval = 200;
uint8_t blinkCount = 0;
float lowPassPreviousFilteredValue = 0;

// Buffer for JSON output
char outputMessage[300];

// WiFi/HTTP variables
String sensorHubURL = "http://192.168.4.1";  // Sensor Hub IP
unsigned long lastHTTPDataTime = 0;
unsigned long lastCommandPollTime = 0;
const unsigned long commandPollInterval = 2000;  // Poll every 2 sec

// ---------- Calibration Storage using EEPROM ----------
struct CalibrationParams {
  uint32_t magic;  // Should equal 0xDEADBEEF if valid
  float k1;
  float f1;
  float c1;
  float k2;
  float f2;
  float c2;
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

// ----- FreeRTOS Task Prototypes -----
void sensorTask(void * parameter);
void httpTask(void * parameter);
void wifiTask(void * parameter);

void setup() {
  Serial.begin(115200);
  SerialBT.begin("Flowmeter 50L/min");

  // Initialize NVS for other purposes
  esp_err_t err = nvs_flash_init();
  if(err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND){
    nvs_flash_erase();
    err = nvs_flash_init();
  }
  if(err != ESP_OK){
    Serial.println("Failed to initialize NVS");
  }

  // Initialize EEPROM (reserve 64 bytes)
  EEPROM.begin(64);

  // Initialize I2C
  Wire.begin();
  Wire.setClock(100000);  // 100 kHz for proper level shifting
  Serial.println("I2C bus initialized.");

  // Set WiFi mode to STA (WiFi connection managed in wifiTask)
  WiFi.mode(WIFI_STA);
  Serial.println("Waiting for target WiFi network...");

  // Setup pins
  pinMode(VALVE_FLOW_PIN, OUTPUT);
  pinMode(VALVE1_PIN, OUTPUT);
  pinMode(VALVE2_PIN, OUTPUT);
  digitalWrite(VALVE_FLOW_PIN, valveFlowState);
  digitalWrite(VALVE1_PIN, valve1State);
  digitalWrite(VALVE2_PIN, valve2State);
  pinMode(RECEIVER_LED, OUTPUT);
  digitalWrite(RECEIVER_LED, LOW);
  
  // Setup interrupt pin for ADC
  pinMode(READY_PIN, INPUT);

  // Initialize ADS1115
  if (!ads.begin(0x48)) {
    Serial.println(F("Failed to initialize ADS1115."));
    while(1); // Halt if ADC fails
  } else {
    Serial.println(F("ADC ADS1115 initialized."));
  }
  
  // Set ADC gain and data rate
  // Use GAIN_ONE for a 0–5V signal (±4.096V full-scale)
  ads.setGain(GAIN_TWOTHIRDS);
  ads.setDataRate(RATE_ADS1115_8SPS); // 8 SPS (125ms conversion time)

  // Attach interrupt to the READY_PIN. A falling edge indicates a new sample is ready.
  attachInterrupt(digitalPinToInterrupt(READY_PIN), NewDataReadyISR, FALLING);
  Serial.println("ADC interrupt attached.");

  // Start continuous conversions on single-ended channel A3.
  ads.startADCReading(ADS1X15_REG_CONFIG_MUX_SINGLE_3, /*continuous=*/true);
  Serial.println("ADC started in continuous mode on A3.");

  // Initialize MCP4725
  if (!mcp.begin(0x60)) {
    Serial.println(F("Failed to find MCP4725 chip."));
  } else {
    Serial.println(F("DAC MCP4725 initialized."));
  }
  
  delay(50);  // Allow DAC to settle

  // Load calibration parameters (or set defaults)
  loadParameters();
  writeFlowSetpointToDAC(flowSetpoint);

  // Create FreeRTOS tasks (pinned to core 1)
  xTaskCreatePinnedToCore(sensorTask, "SensorTask", 4096, NULL, 2, NULL, 1); // Higher priority for sensor reading
  xTaskCreatePinnedToCore(httpTask, "HTTPTask", 4096, NULL, 1, NULL, 1);
  xTaskCreatePinnedToCore(wifiTask, "WiFiTask", 4096, NULL, 1, NULL, 1);
}

static unsigned long lastLoop = 0;

void loop() {
  unsigned long now = millis();

  // Every 250 ms, do one cycle
  if (now - lastLoop >= 250) {
    lastLoop += 250;        // schedule next
    readSerialData();       // handle incoming commands
    updateLEDBlinking();    // pulse status LED

    // build & send JSON
    float seconds = now / 1000.0;
    snprintf(outputMessage, sizeof(outputMessage),
             "{\"seconds\":%.3f,\"flow_voltage\":%.6f,\"flow_rate\":%.6f"
             ",\"flow_setpoint\":%.6f,\"valve1State\":%d"
             ",\"valve2State\":%d,\"valveFlowState\":%d}",
             seconds, readFlowVoltage, readFlowRate,
             flowSetpoint, valve1State, valve2State, valveFlowState);
    Serial.println(outputMessage);
    SerialBT.println(outputMessage);
  }

  // give RTOS & I2C time to run
  yield();
}

// ----- sensorTask: Waits for ADC interrupt and processes data -----
void sensorTask(void * parameter) {
  for(;;) {
    // Wait for the ISR to signal that new data is available
    if (new_data_available) {
      readAndProcessADC(); // This function also resets the flag
    }
    // Yield to other tasks. A small delay prevents this task from hogging the CPU.
    vTaskDelay(pdMS_TO_TICKS(10)); 
  }
}

// ----- httpTask: Perform HTTP GET to send sensor data and poll commands -----
void httpTask(void * parameter) {
  for(;;) {
    unsigned long now = millis();
    if (WiFi.status() == WL_CONNECTED) {
      // Send sensor data via HTTP GET
      HTTPClient http;
      String url = sensorHubURL + "/flowData?seconds=" + String(now/1000.0, 3) +
                   "&flow_voltage=" + String(readFlowVoltage, 6) +
                   "&flow_rate=" + String(readFlowRate, 6) +
                   "&flow_setpoint=" + String(flowSetpoint, 6) +
                   "&valve1State=" + String(valve1State) +
                   "&valve2State=" + String(valve2State) +
                   "&valveFlowState=" + String(valveFlowState);
      http.begin(url);
      http.GET();  // Ignoring response code for brevity
      http.end();

      // Poll for commands every commandPollInterval
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
            Serial.println("Received flowmeter command: " + commandPayload);
            processReceivedData(commandPayload);
          }
        } else {
          Serial.println("Error polling flow command: " + String(cmdCode));
        }
        httpCmd.end();
      }
    }
    vTaskDelay(pdMS_TO_TICKS(50));  // Check every 50ms
  }
}

// ----- wifiTask: Manage WiFi connection asynchronously -----
void wifiTask(void * parameter) {
  for(;;) {
    if (WiFi.status() != WL_CONNECTED) {
      if (millis() - lastWiFiCheckTime >= wifiCheckInterval) {
        int n = WiFi.scanComplete();
        if (n == -1) {
          WiFi.scanNetworks(true);  // Start a new scan
          Serial.println("Scanning for WiFi networks...");
        } else if (n >= 0) {
          bool targetFound = false;
          Serial.printf("WiFi scan complete. %d networks found.\n", n);
          for (int i = 0; i < n; i++) {
            String ssid = WiFi.SSID(i);
            if (ssid == "ModuloTECNAL_1" || ssid == "ModuloTECNAL_2") {
              currentSSID = ssid;
              currentPassword = ssid;  // Password is same as SSID
              targetFound = true;
              break;
            }
          }
          WiFi.scanDelete();
          if (targetFound) {
            Serial.println("Target WiFi found: " + currentSSID + ". Connecting...");
            WiFi.begin(currentSSID.c_str(), currentPassword.c_str());
          } else {
            Serial.println("Target WiFi not found.");
          }
        } else if (n == -2) {
          Serial.println("WiFi scan failed. Starting new scan...");
          WiFi.scanNetworks(true);
        }
        lastWiFiCheckTime = millis();
      }
    }
    vTaskDelay(pdMS_TO_TICKS(500));
  }
}

// ----- readSerialData: Read and process incoming commands via Serial/BT -----
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

// ----- processReceivedData: Parse JSON commands and update settings -----
void processReceivedData(String data) {
  data.trim();
  if (data.length() < 2) {
    Serial.println(F("Error: Received data too short"));
    return;
  }
  if (data.startsWith("{") && data.endsWith("}")) {
    data = data.substring(1, data.length() - 1);
    int start = 0;
    bool calParamsUpdated = false;
    while (start < data.length()) {
      int colonIndex = data.indexOf(':', start);
      int commaIndex = data.indexOf(',', start);
      if (colonIndex == -1) {
        Serial.println(F("Error: Colon not found in command"));
        break;
      }
      if (commaIndex == -1)
        commaIndex = data.length();
      if (colonIndex < commaIndex) {
        String key = data.substring(start, colonIndex);
        String value = data.substring(colonIndex + 1, commaIndex);
        key.trim();
        value.trim();
        // Ensure key is enclosed in quotes
        if (key.startsWith("\"") && key.endsWith("\""))
          key = key.substring(1, key.length() - 1);
        else {
          Serial.println(F("Error: Key not enclosed in quotes"));
          start = commaIndex + 1;
          continue;
        }
        if (key == "v_Flow") {
          valveFlowState = value.toInt();
          digitalWrite(VALVE_FLOW_PIN, valveFlowState);
        } else if (key == "v1") {
          valve1State = value.toInt();
          digitalWrite(VALVE1_PIN, valve1State);
        } else if (key == "v2") {
          valve2State = value.toInt();
          digitalWrite(VALVE2_PIN, valve2State);
        } else if (key == "flow_setpoint") {
          flowSetpoint = value.toFloat();
          writeFlowSetpointToDAC(flowSetpoint);
        } else if (key == "max_flow") {
          maxFlowRate = value.toFloat();
        } else if (key == "data_interval") {
          Serial.println(F("Note: data_interval is obsolete. ADC is interrupt-driven."));
        }
        // Calibration parameter updates:
        else if (key == "k1") {
          calParams.k1 = value.toFloat();
          k1 = calParams.k1;
          calParamsUpdated = true;
        } else if (key == "f1") {
          calParams.f1 = value.toFloat();
          f1 = calParams.f1;
          calParamsUpdated = true;
        } else if (key == "c1") {
          calParams.c1 = value.toFloat();
          c1 = calParams.c1;
          calParamsUpdated = true;
        } else if (key == "k2") {
          calParams.k2 = value.toFloat();
          k2 = calParams.k2;
          calParamsUpdated = true;
        } else if (key == "f2") {
          calParams.f2 = value.toFloat();
          f2 = calParams.f2;
          calParamsUpdated = true;
        } else if (key == "c2") {
          calParams.c2 = value.toFloat();
          c2 = calParams.c2;
          calParamsUpdated = true;
        }
        start = commaIndex + 1;
      } else {
        Serial.println(F("Error: Command with invalid format"));
        break;
      }
    }
    if (calParamsUpdated)
      saveParameters();
    startLEDBlinking();
  }
}

// ----- writeFlowSetpointToDAC: Update DAC output -----
void writeFlowSetpointToDAC(float flowSetpoint) {
  uint16_t dacValue = (flowSetpoint / maxFlowRate) * 4095;
  dacValue = constrain(dacValue, 0, 4095);
  Serial.print("Setting DAC value: ");
  Serial.println(dacValue);
  mcp.setVoltage(dacValue, false);
}

// ----- readAndProcessADC: Read last result, filter, calibrate, and update globals -----
void readAndProcessADC() {
  // Reset the flag immediately to avoid missing an interrupt
  noInterrupts();
  new_data_available = false;
  interrupts();

  // 1) get the last conversion result from the ADC
  int16_t raw = ads.getLastConversionResults();
  
  // 2) compute the voltage
  float newVolts = ads.computeVolts(raw);

  // 3) apply your filters
  float filtered    = lowPassFilter(newVolts, 0.25f);
  float smoothed    = movingAverageFilter(filtered);
  readFlowVoltage   = smoothed;

  // 4) calibration curve → flow rate
  if (readFlowVoltage <= 0.0545f) {
    readFlowRate = k1 * sq(readFlowVoltage) + f1 * readFlowVoltage + c1;
  } else {
    readFlowRate = k2 * sq(readFlowVoltage) + f2 * readFlowVoltage + c2;
  }

  if (readFlowRate < 0.0f) 
    readFlowRate = 0.0f;
}


// ----- LED Blinking Functions -----
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

// ----- Filtering Functions -----
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

// ----- Calibration Functions -----
void loadParameters() {
  EEPROM.get(0, calParams);
  if (calParams.magic != CALIBRATION_MAGIC) {
    calParams.magic = CALIBRATION_MAGIC;
    calParams.k1 = -139.0570077428;
    calParams.f1 = 21.9738888302;
    calParams.c1 = -0.0341880209;
    calParams.k2 = -0.8724324917;
    calParams.f2 = 10.6573301479;
    calParams.c2 = 0.1953756879;
    EEPROM.put(0, calParams);
    EEPROM.commit();
    Serial.println("Calibration parameters not found, using defaults.");
  } else {
    Serial.println("Calibration parameters loaded from EEPROM.");
  }
  k1 = calParams.k1;
  f1 = calParams.f1;
  c1 = calParams.c1;
  k2 = calParams.k2;
  f2 = calParams.f2;
  c2 = calParams.c2;
  Serial.printf("k1: %.6f, f1: %.6f, c1: %.6f\n", k1, f1, c1);
  Serial.printf("k2: %.6f, f2: %.6f, c2: %.6f\n", k2, f2, c2);
}

void saveParameters() {
  EEPROM.put(0, calParams);
  EEPROM.commit();
  Serial.println("Calibration parameters updated and saved to EEPROM.");
}
