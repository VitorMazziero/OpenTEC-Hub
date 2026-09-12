#include <WiFi.h>
#include <HTTPClient.h>
#include <AccelStepper.h>
//#include <TMCStepper.h>
#include <ESPAsyncWebServer.h>

#define EN_PIN    5  // Enable
#define DIR_PIN   2   // Direction
#define STEP_PIN  4   // Step
#define MS1_PIN 19
#define MS2_PIN 21 
#define USB_PORT Serial // USB port
//#define SERIAL_PORT Serial2  // Serial port for TMC2209
//#define DRIVER_ADDRESS 0b00  // TMC2209 Driver address
//#define R_SENSE 0.11f        // Value in ohms for current sense

//TMC2209Stepper driver(&SERIAL_PORT, R_SENSE, DRIVER_ADDRESS);
AccelStepper stepper(AccelStepper::DRIVER, STEP_PIN, DIR_PIN);
AsyncWebServer server(80);

// String variables
String Read_USB; // String to store the received input
String inputString = ""; // String to store the received input - "{"

// Data type
int Pumptype = 0;
float ti = 0.0;
float tf = 0.0;
float b0 = 0.0;
float b1 = 0.0;
float b2 = 0.0;
float Pump_slope = 1.0;
float Pump_intercept = 0.0;
double discriminant;
int raw = 0;
int dir = 1;

// Time variables
unsigned long lastReconnectAttempt = 0;
unsigned long startTime;
unsigned long currentTime;
unsigned long prev_time = 0;
unsigned long runMillis = 0;
unsigned long prev_millis = -1; // Initialize previous millis for 5-second interval
unsigned long prev_millis_calc = 0; // Initialize previous millis for 150-milisecond interval    
unsigned long prev_mqtt_millis = -1; // For MQTT publishing
int count = 1;
float t = 0;
float t_sec = 0;
float t_min = 0;

// Pulse config
unsigned long pulse_prev_time = 0;
int pulse_count = 0;
int pulse_number = 0;
float Period = 0.0;

//PID control
unsigned long actualSteps = 0.0; // Updated in an ISR or step command function
unsigned long targetSteps;
unsigned long stepMillis = 0;
unsigned long prev_step_millis = 0;

// Motor speed control
// Stepper speed type
float stepperSpeed_raw = 0.0;
float stepperSpeed_mL = 0.0;
String send_speed = "";

struct Speeds {
    float stepperSpeed_raw;
    float stepperSpeed_mL;
    long targetSteps;
};

double currentSpeed = 0; // Current speed of the motor in steps per second
int currentSpeed_set = 0;

// PID Control Variables
double Kp = 0.625; // Proportional gain
double Ki = 0.075; // Integral gain
double Kd = 0.03; // Derivative gain
double integral = 0.0, derivative = 0.0, error = 0.0, previousError = 0.0;
double adjustment = 0; float antiWindupLimit = 1000.0;;

double stepsSinceLast = 0;
unsigned long deltaT = 0;
double fractionalStepsAccumulator = 0.0;
const unsigned long overflowThreshold  = 4000000000UL;

//== Setup ===============================================================================

void setup() {

  // Port config 
  USB_PORT.begin(115200);  // Serial for Mega communication

  // Motor setup
  pinMode(MS1_PIN, OUTPUT);
  pinMode(MS2_PIN, OUTPUT);
  pinMode(DIR_PIN, OUTPUT);
  pinMode(STEP_PIN, OUTPUT);
  pinMode(EN_PIN, OUTPUT);

  //driver.begin();                                                                                                                                                                                                                                                                                                                            // UART: Init SW UART (if selected) with default 115200 baudrate
  //driver.toff(5);                 // Enables driver in software
  //driver.rms_current(500);        // Set motor RMS current
  //driver.microsteps(256);         // Set microsteps
  //driver.en_spreadCycle(false);
  //driver.pwm_autoscale(true);     // Needed for stealthChop

  // Set the step resolution pins as outputs
  stepper.enableOutputs();
  digitalWrite(MS1_PIN, HIGH);
  digitalWrite(MS2_PIN, HIGH);
  digitalWrite(DIR_PIN, HIGH);
  digitalWrite(EN_PIN, HIGH); // Disable driver
  
  // Speed max
  stepper.setMaxSpeed(5000);  // Adjust the maximum speed 
  stepper.setAcceleration(100000);  // Adjust the acceleration

  // Acess point config
  WiFi.mode(WIFI_AP);
  WiFi.softAP("PP_01-AP", "pump_control");
  USB_PORT.print("AP IP address: ");
  USB_PORT.println(WiFi.softAPIP());

  // Server config
  server.on("/command", HTTP_POST, [](AsyncWebServerRequest *request) {}, 
    NULL,
    [](AsyncWebServerRequest *request, uint8_t *data, size_t len, size_t index, size_t total) {
        if (len) {
            // Assuming 'data' is not null-terminated.
            String message = String((char *)data).substring(0, len);
            handleCommand(message);
            request->send(200, "text/plain", "Command received");
        } else {
            request->send(400, "text/plain", "No body received");
        }
  });
  server.on("/readData", HTTP_GET, [](AsyncWebServerRequest *request){
    String response = createResponseString();
    request->send(200, "text/plain", response);
});
  server.begin();
}

//== Loop =================================================================================

void loop() {

  // Check USB connection
  if (USB_PORT.available() > 0) {
    // Read the incoming message from the Mega
    Read_USB = USB_PORT.readStringUntil('}');
    inputString = Read_USB.substring(1); // Remove first "{"
    // Split the inputString into an array of substrings using ","
    String values[11];
    int count = 0;
    int lastIndex = 0;

    for (int i = 0; i < inputString.length(); i++) {
      if (inputString.charAt(i) == ',') {
        values[count] = inputString.substring(lastIndex, i);
        count++;
        lastIndex = i + 1;
      }
    }
    // Extract the values from the values array
    if (values[0].length() > 0){
      Pumptype = values[0].toInt();
      ti = values[1].toFloat();
      tf = values[2].toFloat();
      b0 = values[3].toFloat();
      b1 = values[4].toFloat();
      b2 = values[5].toFloat();
      Pump_slope = values[6].toFloat() / 100000000;
      Pump_intercept = values[7].toFloat() / 1000;
      raw = values[8].toFloat();
      Kp = values[9].toFloat();
      Kd = values[10].toFloat();
      Ki = values[11].toFloat();

      USB_PORT.print("b0: ");
      USB_PORT.println(b0);
      USB_PORT.print("b1: ");
      USB_PORT.println(b1);

      // Reset variables
      prev_time = millis();
      t = 0.0;
      actualSteps = 0.0;
      error = 0.0;
      previousError = 0.0;
      derivative = 0.0;
      integral = 0.0;
      prev_step_millis = runMillis;
      stepMillis = runMillis;
      stepsSinceLast = 0;
      currentSpeed = 0;
      currentSpeed_set = 0;
      adjustment = 0;
      stepperSpeed_raw = 0;
      deltaT = 0;
      fractionalStepsAccumulator = 0;
      targetSteps = 0;
    }
  }

  // Set current time
  runMillis = millis();
  t = runMillis - prev_time;
  t_sec = t/1000;
  t_min = t_sec/60;
  // Check for pulse variables
  if (Pumptype == 4){
    Period = b1/60 + b2/60;
    pulse_number = int(ceil(tf/Period));
  }
  
  if ((t_min <= tf and t_min >= ti) or Pumptype == 4){

    if ((int(t) % 250 == 0 and int(t) != prev_millis_calc) or int(t) == 1) {  
      
      // Perform the desired calculations based on the received values
      Speeds speeds = calculateRotation(Pumptype, b0, b1, b2, t, Pump_slope, Pump_intercept, raw, pulse_number);
      stepperSpeed_raw = speeds.stepperSpeed_raw;
      stepperSpeed_mL = speeds.stepperSpeed_mL;
      targetSteps = speeds.targetSteps; 
      currentSpeed = stepperSpeed_raw;
      
      long remainingSteps = targetSteps - actualSteps;
      error = remainingSteps;

      // PID calculations
      integral += error; // Integrate the error over time
      if (integral > antiWindupLimit) {
          integral = antiWindupLimit;
      } else if (integral < -antiWindupLimit) {
          integral = -antiWindupLimit;
      }

      
      derivative = error - previousError; // Change in error
      previousError = error; // Update for next iteration

      // Calculate adjustment based on PID output
      if (t == 1) {
        adjustment = 0;
        integral = 0;
        derivative = 0;
        error = 0;
      } else{
        if (abs(currentSpeed - stepperSpeed_raw > 10)){
          Kp = 2;
          Ki = 0.5;
          Kd = 0.25;
        }else{
          Kp = 0.625;
          Ki = 0.075;
          Kd = 0.03;
        }
        adjustment = (Kp * error) + (Ki * integral) + (Kd * derivative);
        adjustment = constrain(adjustment, -stepperSpeed_raw*0.5, stepperSpeed_raw*0.5);
      }
      
      if (currentSpeed + adjustment < 0 && stepperSpeed_raw >= 0){
        currentSpeed = 0;
      } else if (currentSpeed + adjustment > 0 && stepperSpeed_raw <= 0){
        currentSpeed = 0;
      } else {
        currentSpeed = currentSpeed + adjustment;
      }

      currentSpeed_set = round(currentSpeed);
      USB_PORT.print("{");
      USB_PORT.print(adjustment);
      USB_PORT.print(", ");
      USB_PORT.print(stepperSpeed_raw);
      USB_PORT.print(", ");
      USB_PORT.print(currentSpeed_set);
      USB_PORT.print(", ");
      USB_PORT.print(targetSteps);
      USB_PORT.print(", ");
      USB_PORT.print(actualSteps);
      USB_PORT.print(", ");
      USB_PORT.print(t_sec);
      USB_PORT.println(",}");

      prev_millis_calc = int(t);
    }
    stepper.setSpeed(currentSpeed_set);
    stepper.runSpeed();
    
    if (t > 200){
      
      stepMillis = millis();
      deltaT = stepMillis - prev_step_millis; // prev_millis should be updated at the end of the loop
      // Assuming currentSpeed is steps per second, calculate steps since last iteration
      stepsSinceLast = (currentSpeed_set * deltaT) / 1000.0;
      fractionalStepsAccumulator += stepsSinceLast; // Accumulate fractional steps

      // Only update actualSteps when you have a full step accumulated
      if (fractionalStepsAccumulator >= 1.0) {
        if (actualSteps >= overflowThreshold) {
          // Handle overflow for actualSteps
          actualSteps = 0; 
          targetSteps = 0;
        }
        if(currentSpeed_set >= 0){
          actualSteps += (int)fractionalStepsAccumulator; // Convert accumulated steps to integer
        } else{
          actualSteps -= (int)fractionalStepsAccumulator; // Convert accumulated steps to integer
        }
        fractionalStepsAccumulator -= (int)fractionalStepsAccumulator; // Remove the integer part from the accumulator
      }
      prev_step_millis = stepMillis;
    }
    else{
      actualSteps = 0;
      fractionalStepsAccumulator = 0;
      stepsSinceLast = 0;
      prev_step_millis = stepMillis;
    }
  } else {
    currentSpeed_set = 0;
    stepper.setSpeed(currentSpeed_set);
    stepper.runSpeed();
    digitalWrite(EN_PIN, HIGH);
  }
}

void handleCommand(String message) {
    // Remove the curly braces
    message.remove(0, 1); // remove the first character '{'
    message.remove(message.length() - 1); // remove the last character '}'

    // Split the string by commas and process each value
    String values[9]; // Adjust the size based on the expected number of values
    int count = 0;
    int lastIndex = 0;

    for (int i = 0; i < message.length(); i++) {
        if (message.charAt(i) == ',') {
            values[count] = message.substring(lastIndex, i);
            count++;
            lastIndex = i + 1;
        }
    }

    // Extract the values from the values array
    if (values[0].length() > 0) {
      Pumptype = values[0].toInt();
      ti = values[1].toFloat();
      tf = values[2].toFloat();
      b0 = values[3].toFloat();
      b1 = values[4].toFloat();
      b2 = values[5].toFloat();
      Pump_slope = values[6].toFloat() / 100000000;
      Pump_intercept = values[7].toFloat() / 1000;
      raw = values[8].toInt();
      t = 0;
      prev_time = millis();
    }
}

// Function to convert variables to the desired string format
String createResponseString() {
  if (raw == 1){
    send_speed = "{" + String(stepperSpeed_raw) + "," + String(t_sec) + "}";
  }
  else{
    send_speed = "{" + String(stepperSpeed_mL) + "," + String(t_sec) + "}";
  }
    return send_speed;
}

// Function to calculate the velocity based on the received values
Speeds calculateRotation(int Pumptype, float b0, float b1, float b2, unsigned long t, float Pump_slope, float Pump_intercept, int raw, float pulse_number) {
  Speeds speeds;
  unsigned long currentMillis;
  // Perform the desired calculations based on the Pumptype and coefficients
  switch (Pumptype) {
    case 0: 
      stepperSpeed_raw = 0;
      targetSteps = 0;
      digitalWrite(EN_PIN, HIGH);
      break;

    case 1:
      digitalWrite(EN_PIN, LOW);
      if (raw == 1){
        stepperSpeed_raw = b0;
        targetSteps = stepperSpeed_raw * t_sec;
      } else {
        stepperSpeed_mL = b0;
        stepperSpeed_raw = stepperSpeed_mL/Pump_slope - Pump_intercept;
        targetSteps = stepperSpeed_raw * t_sec;
      }
      digitalWrite(DIR_PIN, LOW);
      break;

    case 2:
      digitalWrite(EN_PIN, LOW);
      if (raw == 1){
        stepperSpeed_raw = b0 + b1 * t_min;
        targetSteps = b0 * t_min + b1 * pow(t_min, 2)/2;
      } else {
        stepperSpeed_mL = b0 + b1 * t_min;
        stepperSpeed_raw = stepperSpeed_mL / Pump_slope - Pump_intercept;
        targetSteps = b1 * pow(t_sec, 2) / (120 * Pump_slope) + t_sec * (-Pump_intercept * Pump_slope + b0) / Pump_slope;
      }
      digitalWrite(DIR_PIN, LOW); 
      break;

    case 3:
      digitalWrite(EN_PIN, LOW);
      if (raw == 1){
        stepperSpeed_raw = b0 * exp(b1 * t_min);
        targetSteps = b0 * exp(b1 * t_min) / b1;
      } else {
        stepperSpeed_mL = b0 * exp(b1 * t_min);
        stepperSpeed_raw = stepperSpeed_mL / Pump_slope - Pump_intercept;
        targetSteps = (60 * b0 * exp(b1 * t_sec / 60) / (Pump_slope * b1)) - 60 * b0 / (Pump_slope * b1);
      }
      digitalWrite(DIR_PIN, LOW); 
      break;

    case 4:
      digitalWrite(EN_PIN, LOW);
      currentMillis = millis();

      // Calculate the time intervals in milliseconds
      unsigned long offInterval = b2*1000;
      unsigned long onInterval = b1*1000;
      if ((currentMillis - pulse_prev_time) >= (offInterval + onInterval)) {
          pulse_prev_time = currentMillis;
          pulse_count++;
      }

      // Determine whether the pump should be on or off based on the current time
      if ((currentMillis - pulse_prev_time) < onInterval & pulse_count <= pulse_number) {
          // Pump is on
          if (raw == 1){
            stepperSpeed_raw = b0;
          }
          else{
            stepperSpeed_mL = b0;
            stepperSpeed_raw = stepperSpeed_mL/Pump_slope - Pump_intercept;
          }
      } else if ((currentMillis - pulse_prev_time) < (offInterval + onInterval)) {
          // Pump is off
          stepperSpeed_raw = 0;
      }
      else{
        // Pump is off
        stepperSpeed_raw = 0;
      }
      digitalWrite(DIR_PIN, LOW);
      break;
  }
  if (stepperSpeed_raw > 5000){
    stepperSpeed_raw = 5000;
  }
  if (stepperSpeed_raw < -5000){
    stepperSpeed_raw = -5000;
  }
  if (targetSteps >= overflowThreshold) {
    targetSteps = 0;
  }
  speeds.stepperSpeed_raw = stepperSpeed_raw;
  speeds.stepperSpeed_mL = stepperSpeed_mL;
  speeds.targetSteps = targetSteps;
  return speeds;
}

// key {1,0,300,100,0,0,869604,0,1,0.1,0.05,0.025,}