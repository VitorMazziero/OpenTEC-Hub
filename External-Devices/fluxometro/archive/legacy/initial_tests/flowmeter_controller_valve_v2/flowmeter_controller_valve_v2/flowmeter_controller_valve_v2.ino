#include <Adafruit_MCP4725.h>
#include <Adafruit_ADS1X15.h>
#include <Preferences.h>
#include <BluetoothSerial.h>

// ADC (ADS1115) and DAC (MCP4725) initialization
Adafruit_ADS1115 ads;
Adafruit_MCP4725 mcp;
BluetoothSerial SerialBT;

// DAC resolution
#define DAC_RESOLUTION 5

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
float previousFlowVoltage = 0.0;
float previousValue = 0.0;

// Calibration parameters
float k1, f1, c1;
float k2, f2, c2;

// Flow meter variables
float flowVoltage = 0.0;
float flowRate = 0.0;
float maxFlowRate = 50.0;

// Setpoint PID parameters
float flowSetpoint = 0.0;
float pidOutput = 0;
float lastOutput = 0;
unsigned long lastSettime = 0;
unsigned long set_time = 0;
bool init_computePID = false;
static unsigned long lastDACUpdateTime = 0;

// LED receiver parameters
bool ledBlinking = false;
unsigned long blinkStartTime = 0;
const unsigned long blinkInterval = 200;
uint8_t blinkCount = 0;

// Low-pass filter parameters
const float alpha = 0.5;
float lowPassPreviousFilteredValue = 0;

// Data transmission interval
unsigned long dataInterval = 250;
char outputMessage[200]; // Preallocate memory for output message

// Preferences for storing calibration parameters
Preferences preferences;

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
    while (1);
  }
  Serial.println("ADC ADS1115 initialized");

  // Configure ADS1115 for differential reading between AIN0 and AIN1
  ads.setGain(GAIN_ONE); // ±4.096V
  ads.setDataRate(RATE_ADS1115_860SPS); // 860 samples per second

  // MCP4725 setup
  if (!mcp.begin(0x60)) {
    Serial.println("Failed to find MCP4725 chip");
    while (1);
  }
  Serial.println("DAC MCP4725 initialized");

  // Load calibration parameters from NVS
  loadParameters();

  writeFlowSetpointToDAC(0.0);
}

void loop() {
  // Read Bluetooth data and update LED blinking
  readBluetoothData();
  updateLEDBlinking();
  checkDAC_ADC();

  // Read data from flowmeter and process it
  readAndProcessADC();

  // Calculate the elapsed time in seconds
  float seconds = millis() / 1000.0;

  set_time = millis() - lastSettime;

  // Update DAC if the error is significant

  if (abs(flowSetpoint - flowRate) > 0.05 && set_time >= 8000) {
    // Compute PID output
    pidOutput = computePID(flowSetpoint, flowRate);

    if (millis() - lastDACUpdateTime > 2000 && lastOutput != pidOutput && flowSetpoint != 0) {
      writeFlowSetpointToDAC(pidOutput);
      lastDACUpdateTime = millis();
    }

    lastOutput = pidOutput;
  }

  // Create and send the JSON string
  snprintf(outputMessage, sizeof(outputMessage),
           "{\"seconds\":%.3f,\"flow_voltage\":%.6f,\"flow_rate\":%.6f,\"flow_setpoint\":%.6f,\"valve1State\":%d,\"valve2State\":%d,\"PID_output\":%.6f}",
           seconds, flowVoltage, flowRate, flowSetpoint, valve1State, valve2State, pidOutput);

  Serial.println(outputMessage);
  SerialBT.println(outputMessage);

  delay(dataInterval);
}

// Function to read Bluetooth data and update variables accordingly
void readBluetoothData() {
  if (SerialBT.available() || Serial.available()) {
    String data;
    if (SerialBT.available()) {
      data = SerialBT.readStringUntil('\n');
    }
    else{
      data = Serial.readStringUntil('\n');
    }
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
            init_computePID = true;
          } else if (key == "max_flow") {
            maxFlowRate = value.toFloat();
          } else if (key == "data_interval") {
            dataInterval = value.toFloat();
          } else if (key == "k1") {
            k1 = value.toFloat();
            preferences.putFloat("k1", k1);
          } else if (key == "f1") {
            f1 = value.toFloat();
            preferences.putFloat("f1", f1);
          } else if (key == "c1") {
            c1 = value.toFloat();
            preferences.putFloat("c1", c1);
          } else if (key == "k2") {
            k2 = value.toFloat();
            preferences.putFloat("k2", k2);
          } else if (key == "f2") {
            f2 = value.toFloat();
            preferences.putFloat("f2", f2);
          } else if (key == "c2") {
            c2 = value.toFloat();
            preferences.putFloat("c2", c2);
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

float computePID(float setpoint, float measuredValue) {
  // Constants
  const float Kp = 0.2;    // Proportional gain
  const float Ki = 0.05;   // Integral gain
  const float Kd = 0.01;   // Derivative gain

  // Static variables for maintaining state between function calls
  static float previousError = 0.0;
  static float integral = 0.0;
  static unsigned long lastTime = 0;

  // Reset variables if initializing PID
  if (init_computePID) {
    lastSettime = millis();
    lastTime = millis();
    previousError = 0.0;
    integral = 0.0;
    init_computePID = false;
    return flowSetpoint;  // Start with zero output
  }

  unsigned long currentTime = millis();
  float elapsedTime = (currentTime - lastTime) / 1000.0;
  lastTime = currentTime;
  float error = setpoint - measuredValue;

  // Update integral term
  integral += error * elapsedTime;

  // Calculate derivative term
  float derivative = (error - previousError) / elapsedTime;

  // Save current error for next derivative calculation
  previousError = error;

  // Calculate the PID output
  float output = Kp * error + Ki * integral + Kd * derivative;

  // integral = constrain(integral, -maxIntegral, maxIntegral);

  // Constrain output to allowable range
  output = constrain(output, -flowSetpoint, flowSetpoint);

  return flowSetpoint + output;
}

// Function to write the flow setpoint to the DAC
void writeFlowSetpointToDAC(float flowSetpoint) {
  int dacValue = (flowSetpoint / maxFlowRate) * 4095;
  dacValue = constrain(dacValue, 0, 4095);
  Serial.println(dacValue);
  mcp.setVoltage(dacValue, false);
  delay(100); 
  mcp.setVoltage(dacValue, false);
}

// Function to read ADC value, apply filters, and calculate flow rate
void readAndProcessADC() {
  // Read the differential voltage between AIN0 and AIN1
  int16_t adc0 = ads.readADC_Differential_0_1();
  
  // Convert the ADC reading to volts
  float newFlowVoltage = ads.computeVolts(adc0); // Already in differential mode
  
  // Apply your existing filters
  newFlowVoltage = lowPassFilter(newFlowVoltage, 0.25);
  newFlowVoltage = movingAverageFilter(newFlowVoltage);
  
  flowVoltage = newFlowVoltage;
  
  // Calculate flow rate based on calibration
  if (flowVoltage <= 0.05) {
    flowRate = k1 * flowVoltage * flowVoltage + f1 * flowVoltage + c1;
  } else {
    flowRate = k2 * flowVoltage * flowVoltage + f2 * flowVoltage + c2;
  }
  
  if (flowRate <= 0){
    flowRate = 0.0;
  }
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

// Function to apply a median filter
float medianFilter(float input) {
    const int windowSize = 5;
    static float buffer[windowSize] = {0};
    static int index = 0;

    buffer[index] = input;
    index = (index + 1) % windowSize;

    float sorted[windowSize];
    std::copy(buffer, buffer + windowSize, sorted);
    std::sort(sorted, sorted + windowSize);

    return sorted[windowSize / 2];
}

void checkDAC_ADC(){
  if (!mcp.begin(0x60)) {
    mcp.begin(0x60);
    delay(100);
    if (!mcp.begin(0x60)) {
      Serial.println("Failed to find MCP4725 chip");
      Serial.println("Restarting ESP32...");
      ESP.restart();  // This will restart the ESP32
    }
  }

  if (!ads.begin(0x48)) {
    ads.begin(0x48);
    delay(100);
    if (!ads.begin(0x48)){
      Serial.println("Failed to initialize ADS.");
      Serial.println("Restarting ESP32...");
      ESP.restart();  // This will restart the ESP32
    }
  }
}

// Function to load calibration parameters from NVS
void loadParameters() {
  preferences.begin("calibration", true);
  k1 = preferences.getFloat("k1", -462.6972923131);
  f1 = preferences.getFloat("f1", 48.3343019646);
  c1 = preferences.getFloat("c1", -0.7144184687);
  k2 = preferences.getFloat("k2", -0.751252929);
  f2 = preferences.getFloat("f2", 10.8050249361);
  c2 = preferences.getFloat("c2", -0.0040361693);
  preferences.end();
}