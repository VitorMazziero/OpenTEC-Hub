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
float previousFirstDerivative = 0.0;
float firstDerivativePreviousFilteredValue = 0.0;
float previousSecondDerivative = 0.0;
float secondDerivativePreviousFilteredValue = 0.0;
float firstDerivativeSmoothed = 0.0;
float secondDerivativeSmoothed = 0.0;
const float derivative_threshold_low = 0.000005;
const float derivative_threshold_high = 0.001;

// Calibration parameters
float a1, b1, c1;
float a2, b2, c2;

// Flow meter variables
float flowVoltage = 0.0;
float flowRate = 0.0;
float maxFlowRate = 50.0;

//Setpoint PID parameters
float flowSetpoint = 0.0;
float pidOutput = 0;
float lastOutput = 0;
unsigned long lastSettime = 0;
unsigned long set_time = 0;
bool init_computePID = 1;

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
  ads.begin(0x48);
  delay(50);
  if (!ads.begin(0x48)) {
    Serial.println("Failed to initialize ADS.");
  } else {
    Serial.println("ADC ADS1115 initialized");
  }

  // MCP4725 setup
  mcp.begin(0x60);
  delay(100);
  if (!mcp.begin(0x60)) {
    Serial.println("Failed to find MCP4725 chip");
  } else {
    Serial.println("DAC MCP4725 initialized");
  }

  // Load calibration parameters from NVS
  loadParameters();
  
}

void loop() {
  // Read Bluetooth data and update LED blinking
  readBluetoothData();
  updateLEDBlinking();

  // Read data from flowmeter and process it
  if (mcp.begin(0x60)) readAndProcessADC();

  // Calculate the elapsed time in seconds
  float seconds = millis() / 1000.0;

  set_time = millis() - lastSettime;

  // Update DAC if the error is significant
  if (abs(flowSetpoint - flowRate) > 0.05 && set_time >= 30000) {
    static unsigned long lastDACUpdateTime = 0;
    
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
           "{\"seconds\":%.3f,\"flow_voltage\":%.6f,\"flow_rate\":%.6f,\"flow_setpoint\":%.6f,\"valve1State\":%d,\"valve2State\":%d}",
           seconds, flowVoltage, flowRate, pidOutput, valve1State, valve2State);

  Serial.println(outputMessage);
  SerialBT.println(outputMessage);

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
            init_computePID = 1;
          } else if (key == "max_flow") {
            maxFlowRate = value.toFloat();
          } else if (key == "data_interval") {
            dataInterval = value.toFloat();
          } else if (key == "a1") {
            a1 = value.toFloat();
            preferences.putFloat("a1", a1);
          } else if (key == "b1") {
            b1 = value.toFloat();
            preferences.putFloat("b1", b1);
          } else if (key == "c1") {
            c1 = value.toFloat();
            preferences.putFloat("c1", c1);
          } else if (key == "a2") {
            a2 = value.toFloat();
            preferences.putFloat("a2", a2);
          } else if (key == "b2") {
            b2 = value.toFloat();
            preferences.putFloat("b2", b2);
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

// PID Control Function
float computePID(float setpoint, float measuredValue) {
  // Constants
  const float Kp = 0.2; // Proportional gain
  const float Ki = 0.05;   // Integral gain
  const float Kd = 0.01;   // Derivative gain
  const float maxFlowRate = 50.0;

  // Static variables for maintaining state between function calls
  static float previousError = 0.0;
  static float integral = 0.0;
  static unsigned long lastTime = 0;
  static bool init_computePID = true;

  // Reset variables if initializing PID
  if (init_computePID) {
    lastSettime = millis();
    lastTime = millis();
    previousError = 0.0;
    integral = 0.0;
    init_computePID = false;
    return setpoint;
  }

  unsigned long currentTime = millis();
  float elapsedTime = (currentTime - lastTime) / 1000.0;
  lastTime = currentTime;
  float error = setpoint - measuredValue;

  // Calculate the PID output
  float derivative = (error - previousError) / elapsedTime;


  // Update the error values only if the output is within the range
  if (output >= setpoint - 1 && output <= setpoint + 1) {
    integral += error * elapsedTime;
    derivative = (error - previousError) / elapsedTime;
    previousError = error;
    float output = setpoint + Kp * error + Ki * integral + Kd * derivative;
  }

  return constrain(output, 0, maxFlowRate);
}


// Function to write the flow setpoint to the DAC
void writeFlowSetpointToDAC(float flowSetpoint) {
  int dacValue = (flowSetpoint / maxFlowRate) * 4095;
  dacValue = constrain(dacValue, 0, 4095);
  mcp.setVoltage(dacValue, false);
  delay(100); 
  mcp.setVoltage(dacValue, false);
}

// Function to read ADC value, apply filters, and calculate flow rate
void readAndProcessADC() {
  int16_t adc0 = ads.readADC_SingleEnded(0);
  float newFlowVoltage = ads.computeVolts(adc0);

  newFlowVoltage = lowPassFilter(newFlowVoltage, 0.2);
  newFlowVoltage = movingAverageFilter(newFlowVoltage);

  // Calculate first and second derivatives
  float firstDerivative = abs(calculateFirstDerivative(newFlowVoltage));
  firstDerivativeSmoothed = lowPassFilterFirstDerivative(firstDerivative, 0.1);
  float secondDerivative = abs(calculateSecondDerivative(firstDerivativeSmoothed));
  secondDerivativeSmoothed = lowPassFilterSecondDerivative(secondDerivative, 0.1);

  // Check if current values indicate a peak
  bool isPeak = ((secondDerivativeSmoothed > derivative_threshold_low && secondDerivativeSmoothed < derivative_threshold_high) 
                && (firstDerivativeSmoothed > derivative_threshold_low && firstDerivativeSmoothed < derivative_threshold_high));

  if (!isPeak) {
    // Update flowVoltage only if no peak is detected
    flowVoltage = newFlowVoltage;
  }

  flowVoltage = medianFilter(flowVoltage);
  flowVoltage = movingAverageFilter(flowVoltage);

  // Calculate flow rate
  if (flowVoltage <= 0.05) {
    flowRate = a1*flowVoltage*flowVoltage + b1*flowVoltage + c1;
    }
  else {
    flowRate = a2*flowVoltage*flowVoltage + b2*flowVoltage + c2;
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

// Function to apply a low-pass filter
float lowPassFilter(float newValue, float alpha) {
  float filteredValue = alpha * newValue + (1 - alpha) * lowPassPreviousFilteredValue;
  lowPassPreviousFilteredValue = filteredValue;
  return filteredValue;
}

float lowPassFilterFirstDerivative(float newValue, float alpha) {
  float filteredValue = alpha * newValue + (1 - alpha) * firstDerivativePreviousFilteredValue;
  firstDerivativePreviousFilteredValue = filteredValue;
  return filteredValue;
}

float lowPassFilterSecondDerivative(float newValue, float alpha) {
  float filteredValue = alpha * newValue + (1 - alpha) * secondDerivativePreviousFilteredValue;
  secondDerivativePreviousFilteredValue = filteredValue;
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
    const int windowSize = 10;
    static float buffer[windowSize] = {0};
    static int index = 0;

    buffer[index] = input;
    index = (index + 1) % windowSize;

    float sorted[windowSize];
    std::copy(buffer, buffer + windowSize, sorted);
    std::sort(sorted, sorted + windowSize);

    return sorted[windowSize / 2];
}

// Function to load calibration parameters from NVS
void loadParameters() {
  preferences.begin("calibration", true);
  a1 = preferences.getFloat("a1", -462.6972923131);
  b1 = preferences.getFloat("b1", 48.3343019646);
  c1 = preferences.getFloat("c1", -0.7144184687);
  a2 = preferences.getFloat("a2", -0.751252929);
  b2 = preferences.getFloat("b2", 10.8050249361);
  c2 = preferences.getFloat("c2", -0.0040361693);
  preferences.end();
}
