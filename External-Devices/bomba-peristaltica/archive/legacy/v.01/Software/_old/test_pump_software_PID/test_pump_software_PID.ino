#include <AccelStepper.h>

#define MS1_PIN 10
#define MS2_PIN 11
#define DIR_PIN   9   // Direction
#define STEP_PIN  8   // Step
#define USB_PORT Serial // USB port

AccelStepper stepper(AccelStepper::DRIVER, STEP_PIN, DIR_PIN);

// String variables
String Read_USB;
String inputString = ""; // String to store the received input

struct Speeds {
    float stepperSpeed_raw;
    float stepperSpeed_mL;
    long targetSteps;
};

// Stepper speed type
float stepperSpeed = 0.0;
float stepperSpeed_raw = 0.0;
float stepperSpeed_mL = 0.0;
String send_speed = "";

// Data type
int Pumptype = 0;
float ti = 0.0;
float tf = 0.0;
float b0 = 0.0;
float b1 = 0.0;
float b2 = 0.0;
float Pump_slope = 1.0;
float Pump_intercept = 0.0;
int raw = 0;
int dir = 1;

// Time variables
unsigned long lastReconnectAttempt = 0;
unsigned long startTime;
unsigned long currentTime;
unsigned long prev_time = 0;
unsigned long runMillis = 0;
unsigned long stepMillis = 0;
unsigned long prev_millis = -1;      // Initialize previous millis for 10-second interval
unsigned long prev_step_millis = 0;
unsigned long prev_mqtt_millis = -1; // For MQTT publishing
int count = 1;
float t = 0;
float t_sec = 0;
float t_min = 0;

// Pulse config
unsigned long pulse_prev_time = 0;
int pulse_count = 0;
int pulse_number = 0;
long lowerBound = 0;
long upperBound = 1;
long currentIntegerValue = lowerBound;
float Period = 0.0;

//PID control
unsigned long actualSteps = 0.0; // Updated in an ISR or step command function
unsigned long targetSteps;

// Motor speed control
double currentSpeed = 0; // Current speed of the motor in steps per second
int currentSpeed_set = 0;

// PID Control Variables
double Kp = 0.3; // Proportional gain
double Ki = 0.5; // Integral gain
double Kd = 0.75; // Derivative gain
double integral = 0.0, derivative = 0.0, error = 0.0, previousError = 0.0;
double adjustment = 0; float antiWindupLimit = 1000.0;;

double stepsSinceLast = 0;
unsigned long deltaT = 0;
double fractionalStepsAccumulator = 0.0;
const unsigned long overflowThreshold  = 4000000000UL;

void setup() {
  
  // Set the step resolution pins as outputs
  pinMode(MS1_PIN, OUTPUT);
  pinMode(MS2_PIN, OUTPUT);
  pinMode(DIR_PIN, OUTPUT);
  pinMode(STEP_PIN, OUTPUT);
  stepper.enableOutputs();
  digitalWrite(MS1_PIN, HIGH);
  digitalWrite(MS2_PIN, HIGH);
  digitalWrite(DIR_PIN, HIGH);
  stepper.setMaxSpeed(10000);  // Adjust the maximum speed as desired
  stepper.setAcceleration(100000);  // Adjust the acceleration as desired

  // Port config 
  USB_PORT.begin(115200);  // Serial for Mega communication
  
}

void loop() {

  // Check USB connection
  if (USB_PORT.available() > 0) {
    // Read the incoming message from the Mega
    Read_USB = USB_PORT.readStringUntil('}');
    inputString = Read_USB.substring(1); // Remove first "{"
    // Split the inputString into an array of substrings using ","
    String values[9]; // Assuming you have 6 values in the string
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
      t = 0.0;
      prev_time = millis();
      USB_PORT.print("b0: ");
      USB_PORT.println(b0);
      USB_PORT.print("b1: ");
      USB_PORT.println(b1);
      runMillis = millis();
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

  // Interpolation conditional
  if ((t_min <= tf and t_min >= ti) or Pumptype == 4){
    if ((int(t) % 250 == 0 and int(t) != prev_millis) or t == 1) {  
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
      } else{
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
      // USB_PORT.print("adjustment");
      // USB_PORT.println(adjustment);
      // USB_PORT.print("targetSpeed");
      // USB_PORT.println(stepperSpeed_raw);
      // USB_PORT.print("actualSpeed");
        USB_PORT.print(currentSpeed_set);
        USB_PORT.print(", ");
        USB_PORT.println(stepperSpeed_raw);
      // USB_PORT.print("targetSteps");
      // USB_PORT.println(targetSteps);
      // USB_PORT.print("actualSteps");
      // USB_PORT.println(actualSteps);
      prev_millis = int(t);
    }
    
    stepper.setSpeed(currentSpeed_set);
    stepper.runSpeed();
    
    // Calculate deltaT somehow, or if loop timing is consistent, define it as a constant
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
    // At the end of your loop, update prev_millis for the next iteration
    prev_step_millis = stepMillis;
    
    
    } else {
      stepperSpeed = 0;
      currentIntegerValue = 0;
      stepper.setSpeed(stepperSpeed);
      stepper.runSpeed();
    }

  // Send data every 10 seconds
  if (int(t_sec) % 5 == 0 and int(t_sec) != prev_millis) {  
    // Send to USB
    //USB_PORT.print("{");
    //USB_PORT.print(currentSpeed);
    //USB_PORT.print(",");
    //USB_PORT.print(t_sec);
    //USB_PORT.println(",}");
    //USB_PORT.flush();
    prev_millis = int(t_sec);
  }
}

// Function to calculate the velocity based on the received values
Speeds calculateRotation(int Pumptype, float b0, float b1, float b2, unsigned long t, float Pump_slope, float Pump_intercept, int raw, float pulse_number) {
  Speeds speeds;
  unsigned long currentMillis;
  // Perform the desired calculations based on the Pumptype and coefficients
  switch (Pumptype) {
    case 0: 
      stepperSpeed_raw = 0;
      break;

    case 1:
      if (raw == 1){
        stepperSpeed_raw = b0;
        targetSteps = stepperSpeed_raw * t_sec;
        digitalWrite(DIR_PIN, LOW);
        break;
      } else {
        stepperSpeed_mL = b0;
        stepperSpeed_raw = stepperSpeed_mL/Pump_slope - Pump_intercept;
        targetSteps = stepperSpeed_raw * t_sec;
        digitalWrite(DIR_PIN, LOW);
        break;
      }

    case 2:
    if (raw == 1){
      stepperSpeed_raw = b0 + b1 * t_min;
      targetSteps = b0 * t_min + b1 * pow(t_min, 2)/2;
    } else {
      stepperSpeed_mL = b0 + b1 * t_min;
      stepperSpeed_raw = stepperSpeed_mL / Pump_slope - Pump_intercept;
      targetSteps = b1 * pow(t_min, 2) / (2 * Pump_slope) + t_min * (-Pump_intercept * Pump_slope + b0) / Pump_slope;
      digitalWrite(DIR_PIN, LOW); 
    }
    break;

    case 3:
    if (raw == 1){
      stepperSpeed_raw = b0 + b1 * t_min;
      targetSteps = b0 * exp(b1 * t_min) / b1;
    } else {
      stepperSpeed_mL = b0 * exp(b1 * t_min);
      stepperSpeed_raw = stepperSpeed_mL / Pump_slope - Pump_intercept;
      targetSteps = -Pump_intercept*t_min + (b0*exp(b1*t_min)/(Pump_slope*b1));
    }
    break;

    case 4:
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
  if (stepperSpeed_raw > 500000){
    stepperSpeed_raw = 500000;
  }
  if (stepperSpeed_raw < -500000){
    stepperSpeed_raw = -500000;
  }
  if (targetSteps >= overflowThreshold) {
    targetSteps = 0;
  }
  speeds.stepperSpeed_raw = stepperSpeed_raw;
  speeds.stepperSpeed_mL = stepperSpeed_mL;
  speeds.targetSteps = targetSteps;
  return speeds;
}

