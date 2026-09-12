#include <MKS_Servo.h>
#include <HardwareSerial.h>

#define EN_PIN 23
#define MODE 5 // SR_vFOC
#define CURRENT 2800 // mA

HardwareSerial R485Serial(2);

MKS_Servo motor(1, R485Serial); // Initialize motor with slave address 1 and use Serial1 RX_PIN 9 TX_PIN 10

// String variables
String readUSB; // String to store the received input
String inputString = ""; // String to store the received input - "{"

// Data type
int pumpType = 0;
float startTime = 0.0;
float endTime = 0.0;
float coeff0 = 0.0;
float coeff1 = 0.0;
float coeff2 = 0.0;
float pumpSlope = 1.0;
float pumpIntercept = 0.0;
int rawMode = 0;

// Time variables
unsigned long prevTime = 0;
unsigned long runMillis = 0;
unsigned long prevMillisCalc = 0;
unsigned long lastSaveTime = 0; // To track last save time
unsigned long savedMillis = 0; // To track the last saved millis value
float elapsedMillis = 0;
float elapsedSec = 0;
float elapsedMin = 0;

// Pulse config
unsigned long pulsePrevTime = 0;
int pulseCount = 0;
int pulseNumber = 0;
float period = 0.0;

// PID control
unsigned long actualSteps = 0.0;
unsigned long targetSteps;
unsigned long stepMillis = 0;
unsigned long prevStepMillis = 0;

// Motor speed control
float stepperSpeedRaw = 0.0;
float stepperSpeedMl = 0.0;
String sendSpeed = "";

struct Speeds {
    float stepperSpeedRaw;
    float stepperSpeedMl;
    long targetSteps;
};

double currentSpeed = 0; // Current speed of the motor in steps per second
int currentSpeedSet = 0;

// PID Control Variables
double kp = 0.625; // Proportional gain
double ki = 0.075; // Integral gain
double kd = 0.03; // Derivative gain
double integral = 0.0, derivative = 0.0, error = 0.0, previousError = 0.0;
double adjustment = 0;
float antiWindupLimit = 1000.0;
double stepsSinceLast = 0;
unsigned long deltaT = 0;
double fractionalStepsAccumulator = 0.0;
const unsigned long overflowThreshold = 4000000000UL;

void setup() {
    Serial.begin(115200);
    while (!Serial) {
        ; // wait for serial port to connect. Needed for native USB port only
    }
    motor.begin(38400);
    motor.setPins(EN_PIN);
    digitalWrite(EN_PIN, LOW);
    motor.setMode(MODE);
    motor.setCurrent(CURRENT);
}

void loop() {
    checkUSBConnection();
    updateCurrentTime();
    if ((elapsedMin <= endTime && elapsedMin >= startTime) || pumpType == 4) {
        calculateStepSpeedPID();
        motor.setSpeed(currentSpeedSet, 0);
        motor.setDirection(1);
        motor.run();
        checkRemainingSteps();
    } else {
        stopMotor();
    }
}

void checkUSBConnection() {
    if (Serial.available() > 0) {
        // Read the incoming message
        readUSB = Serial.readStringUntil('\n');
        readUSB.trim();

        if (readUSB[0] == '{' && readUSB[readUSB.length() - 1] == '}') {
            readUSB = readUSB.substring(1, readUSB.length() - 1);
            int start = 0;
            while (start < readUSB.length()) {
                int colonIndex = readUSB.indexOf(':', start);
                int commaIndex = readUSB.indexOf(',', start);

                if (commaIndex == -1) {
                    commaIndex = readUSB.length();
                }

                if (colonIndex != -1 && colonIndex < commaIndex) {
                    String key = readUSB.substring(start, colonIndex);
                    String value = readUSB.substring(colonIndex + 1, commaIndex);
                    key.trim();
                    value.trim();

                    if (key == "startTime") {
                        startTime = value.toFloat();
                    } else if (key == "endTime") {
                        endTime = value.toFloat();
                    } else if (key == "coeff0") {
                        coeff0 = value.toFloat();
                    } else if (key == "coeff1") {
                        coeff1 = value.toFloat();
                    } else if (key == "coeff2") {
                        coeff2 = value.toFloat();
                    } else if (key == "pumpSlope") {
                        pumpSlope = value.toFloat() / 100000000;
                    } else if (key == "pumpIntercept") {
                        pumpIntercept = value.toFloat() / 1000;
                    } else if (key == "rawMode") {
                        rawMode = value.toInt();
                    } else if (key == "kp") {
                        kp = value.toFloat();
                    } else if (key == "kd") {
                        kd = value.toFloat();
                    } else if (key == "ki") {
                        ki = value.toFloat();
                    }
                    else if (key == "pumpType") {
                        pumpType = value.toInt();
                        if (pumpType == 0){
                          stopPump();
                        }
                    }
                    start = commaIndex + 1;
                    Serial.println("Variables Updated. Restarting pump...");
                }
            }

            // Reset variables
            prevTime = millis();
            elapsedMillis = 0.0;
            actualSteps = 0.0;
            error = 0.0;
            previousError = 0.0;
            derivative = 0.0;
            integral = 0.0;
            prevStepMillis = millis();
            stepMillis = millis();
            fractionalStepsAccumulator = 0;
            targetSteps = 0;
            savedMillis = 0;
        }
    }
}

void stopPump(){
    Serial.print("Variables reseted...");
    startTime = 0;
    endTime = 0;
    coeff0 = 0;
    coeff1 = 0;
    coeff2 = 0;
    prevTime = 0;
    savedMillis = 0;
    actualSteps = 0;
}

void updateCurrentTime() {
    runMillis = millis();
    elapsedMillis = (runMillis + (savedMillis - prevTime));
    elapsedSec = elapsedMillis / 1000;
    elapsedMin = elapsedSec / 60;
}

void calculateStepSpeedPID() {

    if (runMillis - prevMillisCalc >= 500){
        // Perform the desired calculations based on the received values
        Speeds speeds = calculateRotation(pumpType, coeff0, coeff1, coeff2, elapsedMillis, pumpSlope, pumpIntercept, rawMode);
        stepperSpeedRaw = speeds.stepperSpeedRaw;
        stepperSpeedMl = speeds.stepperSpeedMl;
        targetSteps = speeds.targetSteps; 
        currentSpeed = stepperSpeedRaw;
        
        long remainingSteps = targetSteps - actualSteps;
        error = remainingSteps;

        // PID calculations
        integral += error; // Integrate the error over time
        integral = constrain(integral, -antiWindupLimit, antiWindupLimit);

        derivative = error - previousError; // Change in error
        previousError = error; // Update for next iteration

        // Calculate adjustment based on PID output
        if (elapsedMillis <= 1) {
            adjustment = 0;
            integral = 0;
            derivative = 0;
            error = 0;
        } else {
            if (abs(currentSpeed - stepperSpeedRaw) > 10) {
                kp = 2;
                ki = 0.5;
                kd = 0.25;
            } else {
                kp = 0.625;
                ki = 0.075;
                kd = 0.03;
            }
            adjustment = (kp * error) + (ki * integral) + (kd * derivative);
            adjustment = constrain(adjustment, -stepperSpeedRaw * 0.5, stepperSpeedRaw * 0.5);
        }
        
        if (currentSpeed + adjustment < 0 && stepperSpeedRaw >= 0) {
            currentSpeed = 0;
        } else if (currentSpeed + adjustment > 0 && stepperSpeedRaw <= 0) {
            currentSpeed = 0;
        } else {
            currentSpeed = currentSpeed + adjustment;
        }

        currentSpeedSet = round(currentSpeed);

        // Read and print real-time speed and location
        int realSpeed = motor.readSpeed();
        int realLocation = motor.readLocation();
        Serial.print("{adjustment:");
        Serial.print(adjustment);
        Serial.print(", stepperSpeedRaw:");
        Serial.print(stepperSpeedRaw);
        Serial.print(", currentSpeedSet:");
        Serial.print(currentSpeedSet);
        Serial.print(", targetSteps:");
        Serial.print(targetSteps);
        Serial.print(", actualSteps:");
        Serial.print(actualSteps);
        Serial.print(", elapsedSec: ");
        Serial.print(elapsedSec);
        Serial.print(", Real Speed:");
        Serial.print(realSpeed);
        Serial.print(", Real Location:");
        Serial.print(realLocation);
        Serial.println("}");

        prevMillisCalc = int(runMillis);
    }
}

void checkRemainingSteps() {
    if (elapsedMillis > 200) {
        stepMillis = millis();
        deltaT = stepMillis - prevStepMillis;
        stepsSinceLast = (currentSpeedSet * deltaT) / 1000.0;
        fractionalStepsAccumulator += stepsSinceLast;

        if (fractionalStepsAccumulator >= 1.0) {
            if (actualSteps >= overflowThreshold) {
                actualSteps = 0;
                targetSteps = 0;
            }
            actualSteps += (int)fractionalStepsAccumulator;
            fractionalStepsAccumulator -= (int)fractionalStepsAccumulator;
        }
        prevStepMillis = stepMillis;
    } else {
      actualSteps = 0;
      fractionalStepsAccumulator = 0;
      stepsSinceLast = 0;
      prevStepMillis = stepMillis;
    }
}

void stopMotor() {
    currentSpeedSet = 0;
    motor.setSpeed(currentSpeedSet, 0);
    motor.run();
    digitalWrite(EN_PIN, HIGH);
}

Speeds calculateRotation(int pumpType, float coeff0, float coeff1, float coeff2, unsigned long elapsedMillis, float pumpSlope, float pumpIntercept, int rawMode) {
    Speeds speeds;
    unsigned long currentMillis;

    switch (pumpType) {
        case 0:
            stepperSpeedRaw = 0;
            targetSteps = 0;
            digitalWrite(EN_PIN, HIGH);
            break;
        case 1:
            digitalWrite(EN_PIN, LOW);
            if (rawMode == 1) {
                stepperSpeedRaw = coeff0;
                targetSteps = stepperSpeedRaw * elapsedSec;
            } else {
                stepperSpeedMl = coeff0;
                stepperSpeedRaw = stepperSpeedMl / pumpSlope - pumpIntercept;
                targetSteps = stepperSpeedRaw * elapsedSec;
            }
            break;
        case 2:
            digitalWrite(EN_PIN, LOW);
            if (rawMode == 1) {
                stepperSpeedRaw = coeff0 + coeff1 * elapsedMin;
                targetSteps = coeff0 * elapsedMin + coeff1 * pow(elapsedMin, 2) / 2;
            } else {
                stepperSpeedMl = coeff0 + coeff1 * elapsedMin;
                stepperSpeedRaw = stepperSpeedMl / pumpSlope - pumpIntercept;
                targetSteps = coeff1 * pow(elapsedSec, 2) / (120 * pumpSlope) + elapsedSec * (-pumpIntercept * pumpSlope + coeff0) / pumpSlope;
            }
            break;
        case 3:
            digitalWrite(EN_PIN, LOW);
            if (rawMode == 1) {
                stepperSpeedRaw = coeff0 * exp(coeff1 * elapsedMin);
                targetSteps = coeff0 * exp(coeff1 * elapsedMin) / coeff1;
            } else {
                stepperSpeedMl = coeff0 * exp(coeff1 * elapsedMin);
                stepperSpeedRaw = stepperSpeedMl / pumpSlope - pumpIntercept;
                targetSteps = (60 * coeff0 * exp(coeff1 * elapsedSec / 60) / (pumpSlope * coeff1)) - 60 * coeff0 / (pumpSlope * coeff1);
            }
            break;
        case 4:
            digitalWrite(EN_PIN, LOW);
            currentMillis = millis();
            unsigned long offInterval = coeff2 * 1000;
            unsigned long onInterval = coeff1 * 1000;
            if ((currentMillis - pulsePrevTime) >= (offInterval + onInterval)) {
                pulsePrevTime = currentMillis;
                pulseCount++;
            }
            if ((currentMillis - pulsePrevTime) < onInterval && pulseCount <= pulseNumber) {
                if (rawMode == 1) {
                    stepperSpeedRaw = coeff0;
                } else {
                    stepperSpeedMl = coeff0;
                    stepperSpeedRaw = stepperSpeedMl / pumpSlope - pumpIntercept;
                }
            } else if ((currentMillis - pulsePrevTime) < (offInterval + onInterval)) {
                stepperSpeedRaw = 0;
            } else {
                stepperSpeedRaw = 0;
            }
            break;
    }
    if (stepperSpeedRaw > 2000) {
        stepperSpeedRaw = 2000;
    }
    if (stepperSpeedRaw < -2000) {
        stepperSpeedRaw = -2000;
    }
    if (targetSteps >= overflowThreshold) {
        targetSteps = 0;
    }
    speeds.stepperSpeedRaw = stepperSpeedRaw;
    speeds.stepperSpeedMl = stepperSpeedMl;
    speeds.targetSteps = targetSteps;
    return speeds;
}

// key {pumpType:1,startTime:0,endTime:50,coeff0:1,coeff1:1,coeff2:1,pumpSlope:1,pumpIntercept:1,rawMode:1,kp:0.625,ki:0.075,kd:0.03}
