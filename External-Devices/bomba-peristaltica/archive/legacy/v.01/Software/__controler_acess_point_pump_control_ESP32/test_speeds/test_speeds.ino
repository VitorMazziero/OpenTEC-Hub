#include <AccelStepper.h>
#include <TMCStepper.h>

// Pin Definitions
#define EN_PIN    5    // Enable Pin
#define DIR_PIN   2    // Direction Pin
#define STEP_PIN  4    // Step Pin

#define TMC2209_TX 17  // TMC2209 TX Pin (UART RX)
#define TMC2209_RX 16  // TMC2209 RX Pin (UART TX)
#define DRIVER_ADDRESS 0b00 // Driver address (usually 0 for single driver)
#define R_SENSE 0.11f // Sense resistor value

// Stepper Configuration
const uint16_t smStepOptions[] = {1, 2, 4, 8, 16, 32}; // Available microstepping options
const size_t numSmSteps = sizeof(smStepOptions) / sizeof(smStepOptions[0]);
size_t currentSmStepIndex = 4; // Start with 16 microsteps (index 4)

const float maxAbsoluteSpeed = 10000; // Maximum speed (steps per second)
const int maxAcceleration = 5000; // Maximum acceleration (steps per second^2)

TMC2209Stepper driver(&Serial2, R_SENSE, DRIVER_ADDRESS); // Initialize TMC2209
AccelStepper stepper(AccelStepper::DRIVER, STEP_PIN, DIR_PIN); // Initialize AccelStepper

void setup() {
    // Initialize USB Serial
    Serial.begin(115200);
    while (!Serial) { ; } // Wait for Serial to be ready
    Serial.println("Stepper Motor Control Initialized");

    // Configure Pin Modes
    pinMode(DIR_PIN, OUTPUT);
    pinMode(STEP_PIN, OUTPUT);
    pinMode(EN_PIN, OUTPUT);

    // Disable Driver Initially
    digitalWrite(EN_PIN, HIGH); // Disable driver

    // Initialize Serial2 for TMC2209
    Serial2.begin(115200, SERIAL_8N1, TMC2209_RX, TMC2209_TX);
    delay(100); // Wait for Serial2 to initialize

    // Initialize TMC2209 Driver
    driver.begin();
    driver.toff(4);
    driver.rms_current(1200); // Set RMS current (mA)
    driver.microsteps(smStepOptions[currentSmStepIndex]); // Set initial microstepping

    // Initialize AccelStepper
    stepper.setMaxSpeed(maxAbsoluteSpeed);
    stepper.setAcceleration(maxAcceleration);
    stepper.setSpeed(1000); // Set an initial speed (steps per second)

    // Enable Driver
    digitalWrite(EN_PIN, LOW); // Enable driver

    // Feedback
    Serial.print("Initial Microsteps set to: ");
    Serial.println(smStepOptions[currentSmStepIndex]);
    Serial.print("Initial Speed set to: ");
    Serial.println(stepper.speed());
}

void loop() {
    // Handle USB Serial Commands
    if (Serial.available()) {
        String command = Serial.readStringUntil('\n');
        command.trim(); // Remove any trailing whitespace

        if (command.startsWith("SPEED")) {
            // Example command: SPEED 1500
            int spaceIndex = command.indexOf(' ');
            if (spaceIndex != -1) {
                String speedStr = command.substring(spaceIndex + 1);
                long newSpeed = speedStr.toInt();
                if (newSpeed >= 0 && newSpeed <= maxAbsoluteSpeed) {
                    stepper.setSpeed(newSpeed);
                    Serial.print("Speed set to: ");
                    Serial.println(newSpeed);
                } else {
                    Serial.println("Error: Speed out of range.");
                }
            } else {
                Serial.println("Error: Invalid SPEED command format.");
            }
        }
        else if (command.startsWith("MSTEPS")) {
            // Example command: MSTEPS 8
            int spaceIndex = command.indexOf(' ');
            if (spaceIndex != -1) {
                String smStr = command.substring(spaceIndex + 1);
                long newSm = smStr.toInt();
                bool valid = false;
                for (size_t i = 0; i < numSmSteps; i++) {
                    if (smStepOptions[i] == newSm) {
                        currentSmStepIndex = i;
                        valid = true;
                        break;
                    }
                }
                if (valid) {
                    driver.microsteps(smStepOptions[currentSmStepIndex]);
                    Serial.print("Microsteps set to: ");
                    Serial.println(smStepOptions[currentSmStepIndex]);
                } else {
                    Serial.print("Error: Invalid microsteps. Available options: ");
                    for (size_t i = 0; i < numSmSteps; i++) {
                        Serial.print(smStepOptions[i]);
                        if (i < numSmSteps - 1) Serial.print(", ");
                    }
                    Serial.println();
                }
            } else {
                Serial.println("Error: Invalid MSTEPS command format.");
            }
        }
        else {
            Serial.println("Error: Unknown command.");
        }
    }

    // Run the Stepper at Constant Speed
    stepper.runSpeed();
}
