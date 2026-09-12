#include <AccelStepper.h>
#include <BluetoothSerial.h>
#include <TMCStepper.h>  
#include "esp_task_wdt.h"

#define EN_PIN    5  // Enable
#define DIR_PIN   2  // Direction
#define STEP_PIN  4  // Step
#define USB_Serial Serial // USB port

// UART communication pins for TMC2209
#define TMC2209_TX 17   // UART transmit pin
#define TMC2209_RX 16   // UART receive pin
#define DRIVER_ADDRESS 0b00 // Default driver address
#define R_SENSE 0.11f      // Sense resistor value

// Instances for drivers and communication
TMC2209Stepper driver(&Serial2, R_SENSE, DRIVER_ADDRESS);
BluetoothSerial Bluetooth_Serial;

// Stepper configuration
AccelStepper stepper(AccelStepper::DRIVER, STEP_PIN, DIR_PIN);
const float maxAbsoluteSpeed = 10000; // Maximum speed in steps/s
const int maxAcceleration = 1000;     // Maximum acceleration

// Motor control variables
float startLocation = -1;     
uint8_t prevEnable = HIGH;    
uint16_t smStep = 2;    

// Variables related to flow and timing
float targetSpeed_raw = 0; 
float targetSpeed_mL = 0;  
unsigned long targetSteps = 0;

float elapsedTimeMillis = 0.0; 
float elapsedTimeSec = 0.0;    
float elapsedTimeMin = 0.0;

unsigned long loopTime = 0, previousLoopTime = 0;
unsigned long previousControlTime = 0; 

// Pump configuration variables
int pumpType = 0; 
float startTime = 0.0; 
float finalTime = -1.0; 
float coeff0 = 0.0; 
float coeff1 = 0.0; 
float coeff2 = 0.0; 
float pumpSlope = 1.0; 
float pumpIntercept = 0.0; 
int isRaw = 0; 
bool isMotorRunning = false;

// Stepper runtime variables
float setSpeed = 0; 
unsigned long currentSteps = 0; 
int actualSpeed = 0;

// Control interval (in ms)
const unsigned long controlInterval = 100; 

struct flowEquationOutputs {
    float targetSpeed_raw;
    float targetSpeed_mL;
    unsigned long targetSteps;
};

void setup() {
    USB_Serial.begin(115200);
    Bluetooth_Serial.begin("Peristaltic Pump v.01");

    // Motor pins
    pinMode(DIR_PIN, OUTPUT);
    pinMode(STEP_PIN, OUTPUT);
    pinMode(EN_PIN, OUTPUT);

    digitalWrite(DIR_PIN, HIGH);
    digitalWrite(EN_PIN, HIGH); // Disable driver initially

    // TMC2209 initialization
    Serial2.begin(115200, SERIAL_8N1, TMC2209_RX, TMC2209_TX);
    driver.begin();
    driver.toff(4);
    driver.rms_current(1500);
    driver.microsteps(smStep);
    driver.semin(5);
    driver.semax(2);
    driver.intpol(true);

    // Stepper configuration
    stepper.enableOutputs();
    stepper.setMaxSpeed(maxAbsoluteSpeed);
    stepper.setAcceleration(maxAcceleration);
    stepper.setCurrentPosition(0);

    delay(50); // Short delay for system stabilization
}

void loop() {
    readUSBData();
    readBluetoothData();
    updateTime();
    processMotorControl();

    // Call runSpeed frequently to maintain accurate stepping
    if (isMotorRunning) {
        stepper.runSpeed();
    }

    actualSpeed = stepper.speed();
}

/**
 * @brief Reads and processes data from the Bluetooth Serial.
 */
void readBluetoothData() {
    if (Bluetooth_Serial.available()) {
        String data = Bluetooth_Serial.readStringUntil('\n');
        data.trim();
        handleData(data);
    }
}

/**
 * @brief Reads and processes data from the USB Serial.
 */
void readUSBData() {
    if (USB_Serial.available() > 0) {
        String data = USB_Serial.readStringUntil('\n');
        data.trim();
        handleData(data);
    }
}

/**
 * @brief Parses received data and updates variables accordingly.
 */
void handleData(String data) {
    if (data.startsWith("{") && data.endsWith("}")) {
        data = data.substring(1, data.length() - 1); // Remove braces

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
                updateVariables(key, value);
                start = commaIndex + 1;
            } else {
                USB_Serial.println("Error 001: Command with invalid format");
                break;
            }
        }
    }
}

/**
 * @brief Updates system configuration based on received key-value pairs.
 */
void updateVariables(String key, String value) {
    if (key == "\"pumpType\"") {
        pumpType = value.toInt();
        if (pumpType != 0) {
            isMotorRunning = true;
        }
    } 
    else if (key == "\"coeff0\"") {
        coeff0 = value.toFloat();
    } 
    else if (key == "\"coeff1\"") {
        coeff1 = value.toFloat();
    } 
    else if (key == "\"coeff2\"") {
        coeff2 = value.toFloat();
    } 
    else if (key == "\"startTime\"") {
        startTime = value.toFloat();
    } 
    else if (key == "\"finalTime\"") {
        finalTime = value.toFloat();
    } 
    else if (key == "\"slope\"") {  // Changed from "pumpSlope" to "slope" based on your data
        pumpSlope = value.toFloat();
    } 
    else if (key == "\"intercept\"") {  // Changed from "pumpIntercept" to "intercept" based on your data
        pumpIntercept = value.toFloat();
    } 
    else if (key == "\"isRaw\"") {
        isRaw = value.toInt();
    } 
    else if (key == "\"smStep\"") {
        smStep = value.toInt();  // Assuming smStep is an integer; adjust if necessary
        driver.microsteps(smStep);
    } 
    else {
        USB_Serial.print("Unknown variable: ");
        USB_Serial.println(key);
    }

    resetVariables();
}


/**
 * @brief Resets variables for consistent state management.
 */
void resetVariables() {
    previousLoopTime = millis();
    elapsedTimeMillis = 0.0;
    currentSteps = 0;
    setSpeed = 0;
    targetSpeed_raw = 0;
    targetSpeed_mL = 0;
    startLocation = -1;
    stepper.setCurrentPosition(0);
}

/**
 * @brief Updates timing variables.
 */
void updateTime() {
    loopTime = millis();
    elapsedTimeMillis = loopTime - previousLoopTime;
    elapsedTimeSec = elapsedTimeMillis / 1000.0;
    elapsedTimeMin = elapsedTimeSec / 60.0;
}

/**
 * @brief Manages motor operation based on time windows and pump conditions.
 */
void processMotorControl() {
    unsigned long currentMillis = millis();
    bool withinTimeWindow = (elapsedTimeMin >= startTime && (finalTime < 0 || elapsedTimeMin <= finalTime));
    bool shouldRunMotor = (withinTimeWindow && pumpType != 0 && isMotorRunning);

    if (shouldRunMotor) {
        // Update control logic every 'controlInterval' ms
        if (currentMillis - previousControlTime >= controlInterval) {
            performProportionalControl();
            runMotor(int(round(setSpeed)));
            updateSteps();
            previousControlTime = currentMillis;
        }
    } else {
        // Outside operating window or conditions not met
        if (isMotorRunning) {
            stopMotor();
            isMotorRunning = false;
            resetVariables();
        }
    }
}

/**
 * @brief Calculates motor speed and applies proportional control.
 */
void performProportionalControl() {
    flowEquationOutputs targets = calculateSpeed(pumpType, coeff0, coeff1, coeff2,
                                                 (unsigned long)elapsedTimeMillis, pumpSlope, pumpIntercept, isRaw);

    targetSpeed_raw = targets.targetSpeed_raw;
    targetSpeed_mL = targets.targetSpeed_mL;
    targetSteps = targets.targetSteps;

    // Dynamic Kp scaling
    const double Kp_base = 0.001;
    double Kp_scaled = Kp_base * (maxAbsoluteSpeed / max(fabs(targetSpeed_raw), (float)maxAbsoluteSpeed));

    long error = targetSteps - currentSteps;
    setSpeed = targetSpeed_raw + Kp_scaled * error;

    outputSerial();
}

/**
 * @brief Runs the motor at the specified speed.
 */
void runMotor(float speed) {
    stepper.setSpeed(speed);
    if (prevEnable == HIGH) {
        digitalWrite(EN_PIN, LOW);
        prevEnable = LOW;
    }
}

/**
 * @brief Updates the motor step count.
 */
void updateSteps() {
    currentSteps = stepper.currentPosition();
}

/**
 * @brief Stops the motor.
 */
void stopMotor() {
    digitalWrite(EN_PIN, HIGH);
    prevEnable = HIGH;
    digitalWrite(STEP_PIN, LOW);
}

/**
 * @brief Calculates speed and step targets based on pump type and parameters.
 */
flowEquationOutputs calculateSpeed(int pumpType, float coeff0, float coeff1, float coeff2,
                                   unsigned long elapsedTime, float pumpSlope, float pumpIntercept,
                                   int isRaw) {
    flowEquationOutputs targets;
    targets.targetSpeed_raw = 0.0;
    targets.targetSpeed_mL = 0.0;
    targets.targetSteps = 0;

    // Use global elapsedTimeSec and elapsedTimeMin for calculations
    switch (pumpType) {
        case 0: // Stop the pump
            digitalWrite(EN_PIN, HIGH);
            break;

        case 1: // Constant flow
            digitalWrite(EN_PIN, LOW);
            if (isRaw) {
                targets.targetSpeed_raw = coeff0;
                targets.targetSteps = (unsigned long)(coeff0 * elapsedTimeSec);
            } else {
                float localTarget_mL = coeff0;
                targets.targetSpeed_mL = localTarget_mL;
                targets.targetSpeed_raw = (localTarget_mL / pumpSlope) - pumpIntercept;
                targets.targetSteps = (unsigned long)(targets.targetSpeed_raw * elapsedTimeSec);
            }
            break;

        case 2: // Linear varying flow
            digitalWrite(EN_PIN, LOW);
            if (isRaw) {
                targets.targetSpeed_raw = coeff0 + coeff1 * elapsedTimeMin;
                targets.targetSteps = (unsigned long)(coeff0 * elapsedTimeMin + (coeff1 * pow(elapsedTimeMin, 2) / 2));
            } else {
                float localTarget_mL = coeff0 + coeff1 * elapsedTimeMin;
                targets.targetSpeed_mL = localTarget_mL;
                targets.targetSpeed_raw = (localTarget_mL / pumpSlope) - pumpIntercept;
                // Approximate integral for steps if needed. The formula here is specific and should be verified.
                targets.targetSteps = (unsigned long)(targets.targetSpeed_raw * elapsedTimeSec);
            }
            break;

        case 3: // Exponential varying flow
            digitalWrite(EN_PIN, LOW);
            if (isRaw) {
                targets.targetSpeed_raw = coeff0 * exp(coeff1 * elapsedTimeMin);
                targets.targetSteps = (unsigned long)((coeff0 * exp(coeff1 * elapsedTimeMin)) / coeff1);
            } else {
                float localTarget_mL = coeff0 * exp(coeff1 * elapsedTimeMin);
                targets.targetSpeed_mL = localTarget_mL;
                targets.targetSpeed_raw = (localTarget_mL / pumpSlope) - pumpIntercept;
                // For simplicity, just multiply by elapsed time to get an approximate step count
                targets.targetSteps = (unsigned long)(targets.targetSpeed_raw * elapsedTimeSec);
            }
            break;
    }

    return targets;
}

void outputSerial(){
  // Output current status
    USB_Serial.printf("%.6f,", targetSpeed_raw);
    USB_Serial.printf("%.6f,", targetSpeed_mL);
    USB_Serial.printf("%.6f,", setSpeed);
    USB_Serial.printf("%d,", actualSpeed);
    USB_Serial.printf("%lu,", targetSteps);
    USB_Serial.printf("%lu,", currentSteps);
    USB_Serial.printf("%.2f,", elapsedTimeSec);
    USB_Serial.println();

    Bluetooth_Serial.printf("{\"targetSpeed\": %.6f, \"targetSpeed_mL\": %.6f, \"setSpeed\": %.6f, \"actualSpeed\": %d, \"targetSteps\": %lu, \"currentSteps\": %lu, \"timeSec\": %.2f}\n",
                        targetSpeed_raw, targetSpeed_mL, setSpeed, actualSpeed, targetSteps, currentSteps, elapsedTimeSec);

}

// Example commands:
// {pumpType:1,startTime:0,finalTime:300,coeff0:0.001171875,pumpSlope:1}
// {pumpType:1,startTime:0,finalTime:300,coeff0:2000,pumpSlope:1,smStep:1}
