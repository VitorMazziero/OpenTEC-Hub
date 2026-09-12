#include <HardwareSerial.h>
#include <AccelStepper.h>
#include <BluetoothSerial.h>

#define USB_Serial Serial // Define USB_Serial as an alias for Serial.

BluetoothSerial Bluetooth_Serial; // Instance for Bluetooth communication.

/**
 * @brief Create an instance of the HardwareSerial class for Serial2 communication.
 */
HardwareSerial MKS_Serial(2);

/**
 * @brief Define pin configurations for stepper motor control and receiver check.
 */
int EN_PIN = 21;   // Enable signal pin for the stepper motor.
int STP_PIN = 19;  // Step pulse pin for the stepper motor.
int DIR_PIN = 18;  // Direction control pin for the stepper motor.
int RECEIVER_LED = 5; // LED output pin to indicate receiver status.

/**
 * @brief Configure the stepper motor with the AccelStepper library.
 */
AccelStepper stepper(AccelStepper::DRIVER, STP_PIN, DIR_PIN);

/**
 * @brief Maximum allowed motor speeds and acceleration.
 */
float maxAbsoluteSpeed = 5000; // Maximum speed allowed at 1 microstepping resolution.
int maxAcceleration = 1000; // Maximum allowed acceleration for the motor.

/**
 * @brief Track stepper motor position and status.
 */
float readSteps;                 // Read current step count from the stepper.
float startLocation = -1;     // Initial position for tracking relative movement.

/**
 * @brief Variables to track previous state values for controls.
 */
uint8_t prevmStepping = 0;  // Previous microstepping value for comparison.
uint8_t prevEnable = HIGH;    // Previous enable state to detect changes.

/**
 * @brief Address configuration for I2C or similar communication protocols.
 */
uint8_t slaveAddr = 1; // I2C slave address, if used.

/**
 * @brief List of available microstepping resolutions supported by the hardware.
 */
const uint8_t mStepList[] = { 1, 2, 4, 8, 16, 32, 64, 128 };
uint16_t smStep = 256; // Smallest defaut mStep
uint16_t mStepSet = 0;

/**
 * @brief Buffer and count variables for UART communication.
 */
uint8_t txBuffer[20];   // Transmission buffer for outgoing data.
uint8_t rxBuffer[20];   // Reception buffer for incoming data.
uint8_t rxCnt = 0;      // Count of bytes received in the current packet.

/**
 * @brief Task handle for pulse operations on RTOS.
 */
TaskHandle_t pulseTaskHandle = NULL;  // RTOS task handle for pulse generation tasks.
unsigned long previousMicros = 0;  // Time of the last operation for timing control.

/**
 * @brief Variables related to flow equations and operational parameters.
 */
float targetSpeed_raw = 0; // Target raw speed in stepper units.
float targetSpeed_mL = 0;  // Target speed in mL/min for flow calculations.
unsigned long targetSteps; // Target steps in stepper units.

/**
 * @brief Timing variables calculated from the system clock.
 */
float elapsedTimeMillis = 0.0; // Elapsed time in milliseconds.
float elapsedTimeSec = 0.0;    // Elapsed time in seconds.
float elapsedTimeMin = 0.0;    // Elapsed time in minutes.

unsigned long loopTime = 0, previousLoopTime = 0; // Timing for main control loop.
unsigned long stepUpdateTime = 0, previousStepUpdateTime = 0; // Timing for step updates.
unsigned long previousPIDTime = 0; // Last update time for PID control.

/**
 * @brief Serial variables.
 */
int pumpType = 0; // Type of pump control algorithm to be used.
float startTime = 0.0; // Start time for timed operations.
float finalTime = -1.0; // End time for timed operations.
float coeff0 = 0.0; // Coefficient for flow equation.
float coeff1 = 0.0; // Coefficient for flow equation.
float coeff2 = 0.0; // Coefficient for flow equation.
float pumpSlope = 1.0; // Slope for converting raw speed to mL/min.
float pumpIntercept = 0.0; // Intercept for converting raw speed to mL/min.
int isRaw = 0; // Flag to determine if raw speed values should be used.
bool isMotorRunning = 0;

/**
 * @brief Speed and microstepping settings for the stepper motor.
 */
int effectiveSpeed = 0;  // Calculated effective speed in microsteps per second.
float absoluteSpeed = 0.0;   // Calculated absolute speed in steps per second.
uint16_t mStep = 0;    // Current microstepping resolution.
float previousAbsSpeed = -1;

/**
 * @brief PID control parameters and variables for motor speed control with cumulative steps.
 */
double Kp = 0.00125; // Proportional gain for PID.
double Ki = 0.0001; // Integral gain for PID.
double Kd = 0.00075;  // Derivative gain for PID.
double integral = 0.0, derivative = 0.0, error = 0.0, previousError = 0.0; // PID variables.
double adjustment = 0; // Adjustment calculated from PID.
float antiWindupLimit = 1000.0; // Limit for PID integral windup.

unsigned long currentSteps = 0; // Current step count.
double currentSpeed = 0; // Current speed of the motor in steps per second.
int pulseNumber = 0; // Number of pulses to be generated for pulse flow mode.
int targetStepsCounter = 0;

double stepsSinceLast = 0; // Steps calculated since the last update.
unsigned long deltaT = 0; // Time delta for step calculation.
double fractionalStepsAccumulator = 0.0; // Accumulator for fractional steps.
const unsigned long overflowThreshold = 4000000000UL; // Threshold for step overflow protection.

struct flowEquationOutputs {
    float targetSpeed_raw;
    float targetSpeed_mL;
    unsigned long targetSteps;
};

/**
 * @brief Initialize system components and configure initial states. Sets up serial communications
 * via USB and Bluetooth, configures pin modes for motor control, and initializes stepper settings.
 */
void setup() {
    // Initialize USB serial communication at 115200 baud rate.
    USB_Serial.begin(115200);

    // Initialize Bluetooth Serial with a device name.
    Bluetooth_Serial.begin("Peristaltic Pump v.02");

    // Configure motor control pins as output.
    pinMode(EN_PIN, OUTPUT);  // Enable pin
    pinMode(STP_PIN, OUTPUT);  // Step pin

    // Activate the motor by setting the enable pin high.
    digitalWrite(EN_PIN, LOW);

    // Enable the outputs of the stepper driver.
    stepper.enableOutputs();

    // Set the maximum stepping speed of the stepper.
    stepper.setMaxSpeed(maxAbsoluteSpeed);

    // Set the acceleration rate of the stepper.
    stepper.setAcceleration(maxAcceleration);

    // Starts Serial2 communication on RX2 (GPIO16) and TX2 (GPIO17) at 38400 baud, using 8 data bits, no parity, and 1 stop bit configuration.
    MKS_Serial.begin(38400, SERIAL_8N1, 16, 17); 

    // Introduce a delay to allow all systems to stabilize after setup.
    delay(50);
}

/**
 * @brief Main control loop that processes serial commands to adjust motor settings and actions.
 * The loop function includes operations for reading data from Bluetooth and USB,
 * updating LED status, reading the motor's location, updating time, processing motor controls,
 * and managing loop timing with a delay.
 */
void loop() {
    // Checks for incoming data over Bluetooth and updates the receiving LED accordingly.
    readBluetoothData();

    // Checks if there is incoming serial data available from USB to process.
    readUSBData();

    // Updates the blinking state of the LED to reflect the current receiving status.
    updateLEDBlinking();

    // Updates the system time or related timing functions.
    updateTime();

    // Processes any received motor control commands and adjusts motor settings accordingly.
    processMotorControl();
}

/**
 * @brief Reads and processes data from the Bluetooth Serial.
 * Checks if data is available from the Bluetooth serial interface. If data is present,
 * it reads the incoming string until a '}' character is encountered, trims any whitespace,
 * and then processes the trimmed data.
 */
void readBluetoothData() {
    // Check if there is data available to read from Bluetooth Serial.
    if (Bluetooth_Serial.available()) {
        // Read the incoming data as a string until '\n' character is found.
        String data = Bluetooth_Serial.readStringUntil('\n');
        // Remove any leading or trailing whitespace from the data.
        data.trim();
        // Handle the processed data.
        handleData(data);
    }
}

/**
 * @brief Reads and processes data from the USB Serial.
 * Checks if data is available from the USB serial interface. If data is present,
 * it reads the incoming string until a '}' character is encountered, trims any whitespace,
 * and then processes the trimmed data. This method ensures that only complete messages
 * delimited by '}' are processed.
 */
void readUSBData() {
    // Check if there is data available to read from USB Serial.
    if (USB_Serial.available() > 0) {
        // Read the incoming data as a string until '\n' character is found.
        String data = USB_Serial.readStringUntil('\n');
        // Remove any leading or trailing whitespace from the data.
        data.trim();
        // Handle the processed data.
        handleData(data);
    }
}

/**
 * @brief Handles the common data processing tasks from both USB and Bluetooth inputs.
 * This function processes raw string data received from communication interfaces, ensuring it
 * extracts commands formatted in a key-value pair structure enclosed in curly braces.
 * Each valid command updates corresponding variables in the system, providing a robust method
 * for parsing and applying settings or commands dynamically. It also initiates the LED blinking
 * sequence once data processing is complete, providing visual feedback of operation status.
 * 
 * @param data Raw string data received from either USB or Bluetooth, expected to be enclosed in curly braces.
 */
void handleData(String data) {
    // Ensure the data string is properly formatted with curly braces
    if (data.startsWith("{") && data.endsWith("}")) {
        // Remove the enclosing braces to isolate the content
        data = data.substring(1, data.length() - 1);

        // Initialize parsing index
        int start = 0;
        // Continue to parse through the string until all data is processed
        while (start < data.length()) {
            // Find the location of the colon and comma to identify key-value pairs
            int colonIndex = data.indexOf(':', start);
            int commaIndex = data.indexOf(',', start);

            // If there is no comma, this is the last key-value pair
            if (commaIndex == -1) {
                commaIndex = data.length();
            }

            // Validate and process each key-value pair
            if (colonIndex != -1 && colonIndex < commaIndex) {
                // Extract key and value from the string
                String key = data.substring(start, colonIndex);
                String value = data.substring(colonIndex + 1, commaIndex);
                key.trim();
                value.trim();

                // Update the system variables based on the extracted key and value
                updateVariables(key, value);
                
                // Move to the next part of the string
                start = commaIndex + 1;
            } else {
                // Handle format errors by logging and breaking the loop
                USB_Serial.println("Error 001: Command with invalid format");
                break;
            }
        }

        // Initiate the LED blinking sequence after handling all data
        startLEDBlinking();
    }
}

/**
 * @brief Initializes the LED blinking sequence.
 * 
 * This function starts the LED blinking sequence by setting an initial state, recording the start time,
 * and resetting the blink count. It should be called once when you want to start the LED blinking process.
 */
void startLEDBlinking() {
    // Flag to indicate if the LED should continue blinking.
    static bool ledBlinking = true;
    // Record the start time of the blinking.
    static unsigned long blinkStartTime = millis();
    // Counter for the number of blinks that have occurred.
    static int blinkCount = 0;

    // Turn on the LED for the first blink.
    digitalWrite(RECEIVER_LED, HIGH);
}

/**
 * @brief Updates the LED blinking state based on predefined intervals.
 * 
 * This function manages the on-off cycle of an LED. It checks the time elapsed since the last toggle
 * and updates the LED state accordingly. If the maximum number of blinks has been reached, it stops
 * the blinking and resets the counter.
 */
void updateLEDBlinking() {
    // Timestamp of the last blink toggle.
    static unsigned long blinkStartTime = 0;
    // Counts the number of toggles to control the blinking sequence.
    static int blinkCount = 0;
    // Time interval in milliseconds between blinks.
    static const int blinkInterval = 500;
    // Total number of blink toggles (on and off pairs).
    static const int maxBlinks = 4;

    // Check if the blink interval has elapsed.
    if (millis() - blinkStartTime >= blinkInterval) {
        // Reset the blink timer to the current time.
        blinkStartTime = millis();
        // Toggle the LED state.
        digitalWrite(RECEIVER_LED, !digitalRead(RECEIVER_LED));

        // Increment the count of blinks.
        blinkCount++;
        // Check if the maximum number of blinks has been reached.
        if (blinkCount >= maxBlinks) {
            // Ensure the LED is off at the end of the sequence.
            digitalWrite(RECEIVER_LED, LOW);
            // Reset the blink counter for the next sequence.
            blinkCount = 0;
        }
    }
}

/**
 * @brief Updates system configuration settings dynamically based on received key-value pairs.
 *
 * This function serves as the central mechanism to adjust parameters in a pump control system, reflecting
 * real-time changes in operational settings. It interprets and applies values from serialized data,
 * typically received over communication interfaces such as USB or Bluetooth. The parameters managed include
 * fundamental pump operation settings, timing controls, conversion factors for unit translations, and PID
 * control constants for precise operational tuning.
 * 
 * @param key A string that identifies the configuration parameter to update.
 * @param value A string representing the new value for the specified configuration parameter, to be converted
 *              to the appropriate data type.
 * 
 * Managed parameters include:
 * - `pumpType`: Specifies the type of pump, affecting calculation methods and operational behaviors.
 * - `coeff0`, `coeff1`, `coeff2`: Coefficients used for various speed and operational calculations.
 * - `startTime`: Starting time for operations, useful in time-dependent control scenarios.
 * - `finalTime`: Ending or target time for operations, defining operational duration.
 * - `pumpSlope`: A factor for converting flow rate from mL/min to raw speed units for precise control.
 * - `pumpIntercept`: An offset used in conjunction with `pumpSlope` for accurate speed adjustments.
 * - `isRaw`: A binary flag (0 or 1) indicating if raw or processed speed settings should be applied.
 * - `Kp`, `Ki`, `Kd`: Proportional, Integral, and Derivative constants for PID control, enhancing the
 *                     responsiveness and stability of pump operations.
 */
void updateVariables(String key, String value) {
    // Compare key and update the appropriate variable.
    if (key == "pumpType") {
        pumpType = value.toInt(); // Convert value to integer and update pumpType.
        if (pumpType != 0) {
          isMotorRunning = true;
          }
    } else if (key == "coeff0") {
        coeff0 = value.toFloat(); // Convert value to float and update coeff0.
    } else if (key == "coeff1") {
        coeff1 = value.toFloat(); // Convert value to float and update coeff1.
    } else if (key == "coeff2") {
        coeff2 = value.toFloat(); // Convert value to float and update coeff2.
    } else if (key == "startTime") {
        startTime = value.toFloat(); // Convert value to float and update startTime.
    } else if (key == "finalTime") {
        finalTime = value.toFloat(); // Convert value to float and update finalTime.
    } else if (key == "pumpSlope") {
        pumpSlope = value.toFloat(); // Convert value to float and update pumpSlope.
    } else if (key == "pumpIntercept") {
        pumpIntercept = value.toFloat(); // Convert value to float and update pumpIntercept.
    } else if (key == "isRaw") {
        isRaw = value.toInt(); // Convert value to integer and update isRaw.
    } else if (key == "mStepSet") {
        mStepSet = value.toInt(); // Convert value to integer and update mStep.
    } else if (key == "Kp") {
        Kp = value.toFloat(); // Convert value to float and update Kp.
    } else if (key == "Ki") {
        Ki = value.toFloat(); // Convert value to float and update Ki.
    } else if (key == "Kd") {
        Kd = value.toFloat(); // Convert value to float and update Kd.
    } else {
        // Handle unknown key with an error message.
        Serial.print("Unknown variable: ");
        Serial.println(key);
    }

    // Reset various operational and control variables for consistent state management.
    resetVariables();
}

/**
 * @brief Resets various operational and control variables for consistent state management.
 */
void resetVariables() {
    // Reset timing and control parameters to their initial state.
    previousLoopTime = millis();
    elapsedTimeMillis = 0.0;
    previousStepUpdateTime = millis();
    fractionalStepsAccumulator = 0;
    deltaT = 0;
    stepsSinceLast = 0;
    stepUpdateTime = 0.0;
    error = 0.0;
    previousError = 0.0;
    derivative = 0.0;
    integral = 0.0;
    adjustment = 0;
    currentSteps = 0.0;
    currentSpeed = 0;
    targetSpeed_raw = 0;
    startLocation = -1;
}

/**
 * @brief Updates time variables and calculates pulse parameters if necessary.
 *
 * This function updates the running time since the last recorded time and calculates 
 * the necessary pulse parameters if the system is set to a pulsing mode. It supports
 * various time conversions and pulse calculations based on operational settings.
 */
void updateTime() {
    // Record the current time in milliseconds since the Arduino started.
    loopTime = millis();

    // Calculate the time elapsed in milliseconds since the last update.
    elapsedTimeMillis = loopTime - previousLoopTime;

    // Convert elapsed time from milliseconds to seconds.
    elapsedTimeSec = elapsedTimeMillis / 1000.0;

    // Further convert elapsed time from seconds to minutes.
    elapsedTimeMin = elapsedTimeSec / 60.0;

    // Check if the pump type is set to pulse mode (type 4).
    if (pumpType == 4) {
        // Calculate the period of the pulse in minutes using coefficients.
        float Period = (coeff1 + coeff2) / 60.0;

        // Calculate the number of pulses needed based on the total time frame.
        pulseNumber = int(ceil(finalTime / Period));
    }
}

/**
 * @brief Manages motor operation based on current timing and predefined conditions.
 *
 * This function decides whether to activate motor control and step updating processes or to stop the motor
 * depending on the current time within specified limits and the type of pump operation. It uses time-based
 * checks and specific pump operation types to control motor activity, ensuring precise handling based on
 * dynamic conditions.
 */
void processMotorControl() {
    // Check if the current time is within the operational window or if the pump is in pulse mode.
    if ((elapsedTimeMin <= finalTime && elapsedTimeMin >= startTime) && pumpType != 0 && isMotorRunning) {
        // Perform PID control calculations and motor adjustments at specific intervals.
        if (int(elapsedTimeMillis) % 200 == 0 && int(elapsedTimeMillis) != previousPIDTime || int(elapsedTimeMillis) < 20) {

            // Retrieves and prints the current location of the motor as part of routine monitoring.
            readLocation();
            
            // Execute PID control to adjust motor settings based on current feedback.
            performPIDControl();           
            
            // Run or adjust motor speed according to the current speed settings and microstep configuration.
            runMotor(currentSpeed, mStepSet);
            if (!isMotorRunning){
              isMotorRunning = true;
            }
            
            // Update step counts to track motor movement accurately.
            updateSteps();
        }
    } else {
        // Stop the motor if outside of operational window or conditions are not met.
        if (isMotorRunning){
          stopMotor();
          isMotorRunning = false;
          resetVariables();
        }
        else{
          delay(250);
        }
    }
}

/**
 * @brief Performs calculations for motor speed, executes PID control, and outputs the status.
 * 
 * This function integrates various motor control tasks including calculating the rotation
 * speeds, applying PID control based on the error between target and actual steps, dynamically
 * adjusting PID coefficients, and outputting current control status to USB and Bluetooth interfaces.
 * It ensures precise and responsive motor operation by adapting to real-time performance feedback.
 */
void performPIDControl() {
    // Calculate target motor speeds and steps using current system settings and elapsed time.
    flowEquationOutputs targets = calculateSpeed(pumpType, coeff0, coeff1, coeff2, elapsedTimeMillis, pumpSlope, pumpIntercept, isRaw, pulseNumber);
    targetSpeed_raw = targets.targetSpeed_raw;
    targetSteps = targets.targetSteps;

    
    Kp = 0.00125; Ki = 0.0001; Kd = 0.00075; // Lower coefficients for finer control.

    // Calculate the error between target and actual steps, and integrate over time.
    long error = targetSteps - currentSteps;
    integral += error;
    integral = constrain(integral, -antiWindupLimit, antiWindupLimit); // Prevent integral wind-up.

    // Calculate the current speed setting and apply PID adjustment.
    currentSpeed = targetSpeed_raw;
    float adjustment = (Kp * error) + (Ki * integral) + (Kd * (error - previousError));
    previousError = error;
    adjustment = constrain(adjustment, -currentSpeed * 0.2, currentSpeed* 0.2); // Limit adjustment to prevent excessive changes.

    // Prevent motor reversal if direction should not change.
    if ((currentSpeed < 0 && targetSpeed_raw >= 0) || (currentSpeed > 0 && targetSpeed_raw <= 0)) {
        currentSpeed = 0;
    }
    else{
      currentSpeed = currentSpeed + adjustment;
    }

    // Output the current control status to USB and Bluetooth for monitoring.
    USB_Serial.printf("%.6f,", adjustment);
    USB_Serial.printf("%.6f,", targetSpeed_raw);
    USB_Serial.printf("%.6f,", targetSpeed_mL);
    USB_Serial.printf("%.6f,", currentSpeed);
    USB_Serial.printf("%.lu,", targetSteps);
    USB_Serial.printf("%.lu,", currentSteps);
    USB_Serial.printf("%.2f,", readSteps);
    USB_Serial.printf("%.2f,", elapsedTimeSec);
    USB_Serial.print(mStep);
    USB_Serial.print(",");
    USB_Serial.println(effectiveSpeed); // `println` for the last value to move to a new line after printing

    Bluetooth_Serial.printf("{adjustment: %.4f, targetSpeed: %.6f, targetSpeed_mL: %.6f, currentSpeed: %.6f, targetSteps: %.lu, currentSteps: %.lu, readSteps: %.4f, timeSec: %.2f, mStep: %d, effectiveSpeed: %d}\n",
                            adjustment, targetSpeed_raw, targetSpeed_mL, currentSpeed, targetSteps, currentSteps, readSteps, elapsedTimeSec, mStep, effectiveSpeed);

    // Record the time of the last PID calculation to maintain consistent update intervals.
    previousPIDTime = int(elapsedTimeMillis);
}

/**
 * @brief Start the motor with the specified absolute speed and microstepping value.
 * This function initiates motor movement at a given speed and microstepping resolution. It handles
 * microstepping adjustments, effective speed calculations, and motor state management, including enabling
 * the motor if it was previously disabled and starting a dedicated task for motor pulses if necessary.
 * 
 * @param absSpeed Absolute speed of the motor in seconds^-1, determining how fast the motor should spin.
 * @param mStepValue The microstepping resolution to be applied, affecting the precision of motor movement.
 */
void runMotor(float absSpeed, uint8_t mStepValue) {
    // Check if the microstepping value is zero and set subdivision accordingly.
    if (mStepValue <= 0 && absSpeed != previousAbsSpeed) {
        // Call function to calculate or set the appropriate microstepping based on given parameters.
        calculateMStep(mStepValue, absSpeed);
    }

    // Calculate effective speed as the product of absolute speed and microstepping value.
    previousAbsSpeed = absSpeed;
    effectiveSpeed = ceil(absSpeed * mStep);

    // Check if the motor was previously disabled and enable it if necessary.
    if (prevEnable == HIGH) {
        // Send signal to enable the motor.
        digitalWrite(EN_PIN, LOW);
        // Update the previous enable state to reflect the motor is enabled.
        prevEnable = LOW;
    }

    // Start a dedicated task to handle motor pulses if it's not already running.
    if (pulseTaskHandle == NULL) {
        xTaskCreate(
            performPulses,    // Pointer to the function that will handle motor pulses.
            "Pulse Task",     // A descriptive name for the task for debugging purposes.
            10000,            // Stack size allocated for the task in words (not bytes).
            NULL,             // Pointer that will be used as the task's parameter (not used here).
            1,                // Priority of the task, with higher numbers representing higher priority.
            &pulseTaskHandle  // Pointer to the task handle that will be updated by this function.
        );
    }
}

/**
 * @brief Updates the motor step count based on elapsed time and the current motor speed.
 *
 * This function calculates how many steps the motor should have taken since the last update,
 * adjusting the actual step count accordingly. It also manages fractional steps to ensure accurate
 * step counts over time. It handles step count overflow and resets counters when not enough time has passed.
 */
void updateSteps() {
    // Check if enough time has passed to update steps.
    if (elapsedTimeMillis > 200) {
        // Current time in milliseconds for step update.
        stepUpdateTime = millis();

        // Calculate the time elapsed since the last step update in milliseconds.
        deltaT = stepUpdateTime - previousStepUpdateTime;

        // Calculate the number of steps since the last update based on current speed.
        // Convert time to seconds and multiply by steps per second to get step count.
        stepsSinceLast = (abs(currentSpeed * smStep) * deltaT) / 1000.0;

        // Accumulate fractional steps.
        fractionalStepsAccumulator += stepsSinceLast;

        // Only update actual steps when at least one full step has accumulated.
        if (fractionalStepsAccumulator >= 1.0) {
            // Check for step count overflow and reset if necessary.
            if (currentSteps >= overflowThreshold) {
                // Reset actual steps and target steps if overflow occurs.
                currentSteps = 0;
            }

            // Add accumulated full steps to total and adjust the accumulator.
            currentSteps += (int)fractionalStepsAccumulator;
            fractionalStepsAccumulator -= (int)fractionalStepsAccumulator;
        }

        // Update the previous step time to the current time for the next calculation.
        previousStepUpdateTime = stepUpdateTime;
    } else {
        // If not enough time has passed, reset steps and accumulators.
        currentSteps = 0;
        fractionalStepsAccumulator = 0;
        stepsSinceLast = 0;

        // Update the previous step time to the current time to prepare for the next potential update.
        previousStepUpdateTime = millis();
    }
}

/**
 * @brief Stops the motor by disabling outputs and terminating the pulse task.
 * This function immediately stops the motor by disabling its outputs and safely terminates any
 * ongoing pulse tasks. It ensures that all related motor control activities are halted and
 * the system resources are appropriately freed.
 */
void stopMotor() {
    // Set the enable pin low to disable the motor.
    digitalWrite(EN_PIN, HIGH);
    prevEnable = HIGH;

    // Ensure the step pin is low to stop any ongoing pulses.
    digitalWrite(STP_PIN, LOW);

    // If a pulse task is running, stop and delete the task to free up resources.
    if (pulseTaskHandle != NULL) {
        // Delete the task managed by FreeRTOS.
        vTaskDelete(pulseTaskHandle);
        // Nullify the task handle as it's no longer valid.
        pulseTaskHandle = NULL;
    }
}

/**
 * @brief Calculate the rotation speed based on pump type and other parameters.
 * 
 * This function determines the motor speed based on various parameters such as the pump type,
 * coefficients, and time elapsed. It adjusts the motor speed according to predefined equations
 * for different modes of operation, including constant, linear, exponential, and pulse flows,
 * and returns the calculated speeds and step targets in a structured format.
 * 
 * @param pumpType Type of pump which dictates the calculation method.
 * @param coeff0 Base coefficient used in the speed calculations.
 * @param coeff1 Time-dependent coefficient.
 * @param coeff2 Coefficient for specialized calculations (e.g., interval in pump type 4).
 * @param elapsedTime Elapsed time since the start (used in time-dependent calculations).
 * @param pumpSlope Linear conversion factor from mL/min to raw speed.
 * @param pumpIntercept Intercept used in linear conversion from mL/min to raw speed.
 * @param isRaw Flag indicating if the raw speed should be used directly.
 * @param pulseCountTarget The number of pulses expected, used in pump type 4.
 * @return A struct containing calculated speeds and step targets.
 */
flowEquationOutputs calculateSpeed(int pumpType, float coeff0, float coeff1, float coeff2, 
                                   unsigned long elapsedTime, float pumpSlope, float pumpIntercept, 
                                   int isRaw, float pulseCountTarget) {
    flowEquationOutputs targets; // Struct to store and return the calculated speeds and steps
    unsigned long currentMillis; // Variable to store the current time in milliseconds

    // Handle motor speed calculation based on pump type
    switch (pumpType) {
        case 0: // Case 0: Stop the pump
            digitalWrite(EN_PIN, HIGH); // Disable the stepper motor.
            break;

        case 1: // Case 1: Constant flow rate
            digitalWrite(EN_PIN, LOW); // Enable the stepper motor.
            // Calculate speed and steps based on whether raw speed is used directly.
            if (isRaw) {
                targetSpeed_raw = coeff0; // Use the base coefficient as the raw speed.
                targetSteps = targetSpeed_raw * elapsedTimeSec; // Calculate target steps for the elapsed time.
            } else {
                targetSpeed_mL = coeff0; // Treat the base coefficient as mL/min.
                targetSpeed_raw = (targetSpeed_mL / pumpSlope) - pumpIntercept; // Convert mL/min to raw speed.
                targetSteps = targetSpeed_raw * elapsedTimeSec; // Calculate target steps.
            }
            break;

        case 2: // Case 2: Linear varying flow rate
            digitalWrite(EN_PIN, LOW); // Enable the stepper motor.
            // Calculate linearly varying speed and steps.
            if (isRaw) {
                targetSpeed_raw = coeff0 + coeff1 * elapsedTimeMin; // Increase speed over time.
                targetSteps = coeff0 * elapsedTimeMin + coeff1 * pow(elapsedTimeMin, 2) / 2; // Area under speed-time graph.
            } else {
                targetSpeed_mL = coeff0 + coeff1 * elapsedTimeMin; // Increase mL/min over time.
                targetSpeed_raw = targetSpeed_mL / pumpSlope - pumpIntercept; // Convert to raw speed.
                targetSteps = coeff1 * pow(elapsedTimeSec, 2) / (120 * pumpSlope) + elapsedTimeSec * (-pumpIntercept * pumpSlope + coeff0) / pumpSlope; // Area under curve.
            }
            break;

        case 3: // Case 3: Exponential varying flow rate
            digitalWrite(EN_PIN, LOW); // Enable the stepper motor.
            // Calculate exponentially varying speed and steps.
            if (isRaw) {
                targetSpeed_raw = coeff0 * exp(coeff1 * elapsedTimeMin); // Exponentially increase speed.
                targetSteps = coeff0 * exp(coeff1 * elapsedTimeMin) / coeff1; // Integral of exponential function gives steps.
            } else {
                targetSpeed_mL = coeff0 * exp(coeff1 * elapsedTimeMin); // Exponential increase of mL/min.
                targetSpeed_raw = targetSpeed_mL / pumpSlope - pumpIntercept; // Convert to raw speed.
                targetSteps = (60 * coeff0 * exp(coeff1 * elapsedTimeSec / 60) / (pumpSlope * coeff1)) - 60 * coeff0 / (pumpSlope * coeff1); // Integral of exponential function modified for mL/min to raw conversion.
            }
            break;

        case 4: // Case 4: Pulse flow
            digitalWrite(EN_PIN, LOW); // Enable the stepper motor.
            currentMillis = millis(); // Get the current time.
            static unsigned long pulsePrevTime = 0; // Remember the last pulse time.
            static unsigned int pulseCount = 0; // Count pulses.
            // Define time intervals for pulsing.
            unsigned long offInterval = coeff2 * 1000; // Off interval in milliseconds.
            unsigned long onInterval = coeff1 * 1000; // On interval in milliseconds.
            if ((currentMillis - pulsePrevTime) >= (offInterval + onInterval)) {
                pulsePrevTime = currentMillis; // Reset the last pulse time.
                pulseCount++; // Increment pulse count.
            }
            // Determine pump status based on the current time.
            if ((currentMillis - pulsePrevTime) < onInterval && pulseCount <= pulseCountTarget) {
                if (isRaw) {
                    targetSpeed_raw = coeff0; // Raw speed during "on" phase.
                } else {
                    targetSpeed_mL = coeff0; // mL/min during "on" phase.
                    targetSpeed_raw = targetSpeed_mL / pumpSlope - pumpIntercept; // Convert mL/min to raw speed.
                }
            } else {
                targetSpeed_raw = 0; // Set speed to zero during "off" phase.
            }
            break;
    }

    // Constrain raw speed to prevent exceeding maximum allowable values.
    targetSpeed_raw = std::clamp(targetSpeed_raw, -maxAbsoluteSpeed, maxAbsoluteSpeed);

    // Handle potential overflow
    targetSteps *= smStep; // Apply scaling

    while (targetSteps >= overflowThreshold) {
        targetSteps -= overflowThreshold;
    }

    // Populate and return the calculated values in a struct.
    targets.targetSpeed_raw = targetSpeed_raw;
    targets.targetSpeed_mL = targetSpeed_mL;
    targets.targetSteps = targetSteps;

    return targets;
}

/**
 * @brief Executes motor pulses based on current motor settings.
 * This function is intended to run as a FreeRTOS task and continuously performs motor operations
 * such as setting speed and executing movement steps.
 * @param parameter Unused parameter, present to comply with FreeRTOS task signature.
 */
void performPulses(void *parameter) {
    unsigned long lastPrintTime = 0;  // Tracks the last time output was sent to the serial.

    // Infinite loop to continuously perform motor operations.
    while (true) {
        unsigned long currentMicros = micros();  // Get current time in microseconds.

        // Set motor speed and execute movement.
        stepper.setSpeed(effectiveSpeed);        // Configure speed for the stepper.
        stepper.runSpeed();                      // Perform a single step operation at the set speed.

        vTaskDelay(1);  // Delay briefly to allow other tasks to execute.
    }
}

/**
 * @brief Set the microstepping value based on the desired stepping resolution and motor speed.
 * This function adjusts the microstepping value according to the absolute speed if no specific
 * microstepping value is provided, otherwise, it sets the microstepping directly from the input value.
 * 
 * @param mSteppingValue Desired microstepping value. If zero, calculates an appropriate value based on speed.
 * @param absSpeed Absolute speed in seconds^-1, used for calculating appropriate microstepping if mSteppingValue is zero.
 */
void calculateMStep(uint8_t mSteppingValue, float absSpeed) {
    absSpeed = abs(absSpeed);  // Ensure speed is a non-negative value.

    // Calculate microstepping based on speed if no explicit microstepping value is provided.
    if (mSteppingValue == 0) {
        if (absSpeed <= smStep * 2 / 256) {
            mStep = 256;  // Set maximum microstepping if speed is very low.
        } else {
            for (uint8_t step : mStepList) {
                // Find the first microstepping value that matches the speed requirement.
                if (absSpeed * step >= smStep) {
                    mStep = step;
                    break;
                }
            }
        }
    } else {
        mStep = mSteppingValue;  // Directly set the microstepping to the provided value.
    }

    // Update motor settings if the current microstepping is significantly different from previous.
    if (prevmStepping <= 3 * mStep) {
        txBuffer[0] = 0xFA;                      // Frame header for the start of a new command.
        txBuffer[1] = slaveAddr;                 // Address of the slave device.
        txBuffer[2] = 0x84;                      // Function code for setting microstepping.
        txBuffer[3] = mStep;                 // The microstepping value to set.
        txBuffer[4] = getCheckSum(txBuffer, 4);  // Calculate checksum for the first 4 bytes of the buffer.

        MKS_Serial.write(txBuffer, 5);           // Send the 5 byte command packet via Serial2.

        waitingForACK(5);                        // Function call to handle acknowledgement.  
    }

    prevmStepping += mStep;  // Update the previous microstepping value to the current.
}

/**
 * @brief Reads and calculates the real-time position of the motor.
 * This function sends a request to the motor driver to get the current position and processes the
 * response. If successful, it updates the motor's stepLocation based on the received data.
 */
void readLocation() {
    static bool isWaiting = false;         // Flag to check if the system is currently waiting for a response.
    static unsigned long lastCallTime = 0; // Timestamp of the last request sent.

    // Send position request if not currently waiting for a response.
    if (!isWaiting) {
        txBuffer[0] = 0xFA; // Frame header.
        txBuffer[1] = slaveAddr; // Address of the slave device.
        txBuffer[2] = 0x31; // Function code for requesting position.
        txBuffer[3] = getCheckSum(txBuffer, 3); // Compute and add checksum.
        MKS_Serial.write(txBuffer, 4); // Send the request.
        lastCallTime = millis(); // Update the last request timestamp.
        isWaiting = true; // Set the waiting flag.
    }

    // Process the response if it's time to check.
    if (isWaiting && millis() - lastCallTime > 25) { // Check every 50 milliseconds.
        uint8_t result = waitingForACK(10);
        if (result == 1) { // ACK received.
            uint32_t loc = (uint32_t)rxBuffer[5] << 24 | (uint32_t)rxBuffer[6] << 16 | (uint32_t)rxBuffer[7] << 8 | (uint32_t)rxBuffer[8];
            float stepLocation = loc / 81.92 * mStep; // Calculate the real step location.
            if (startLocation == -1) {
                startLocation = stepLocation; // Set start location if it's the first reading.
                readSteps = 0;
            } else {
                readSteps = stepLocation - startLocation; // Calculate the number of steps since the start.
            }
            
            isWaiting = false; // Reset waiting flag after processing.
        } else if (result == 0) { // Timeout or error.
            isWaiting = false; // Reset waiting flag on failure.
        }
    }
}

/**
 * @brief Calculates the checksum for a buffer of data.
 * The checksum is the sum of all bytes, truncated to the lowest 8 bits.
 *
 * @param buffer Pointer to the data buffer.
 * @param len Length of the data buffer.
 * @return Computed checksum value.
 */
uint8_t getCheckSum(uint8_t *buffer, uint8_t len) {
    uint16_t sum = 0; // Initialize sum.
    for (uint8_t i = 0; i < len; i++) {
        sum += buffer[i]; // Add each byte to sum.
    }
    return (sum & 0xFF); // Return the lowest 8 bits of the sum.
}

/**
 * @brief Waits for an acknowledgment response from the slave device.
 * This function reads bytes from the serial until the expected length is received or timeout occurs.
 * It checks for a correct checksum to validate the response.
 *
 * @param len Expected length of the response frame.
 * @return 1 if a valid response is received, 0 for timeout or error, 2 if still waiting.
 */
uint8_t waitingForACK(uint8_t len) {
    static unsigned long sTime = 0; // Start time of the request.
    static uint8_t rxCnt = 0; // Count of received bytes.

    // Initialize timing and start counting if first call.
    if (sTime == 0) sTime = millis();

    // Read available bytes from the serial.
    while (MKS_Serial.available() > 0) {
        uint8_t rxByte = MKS_Serial.read();
        // Start buffering if we receive the start frame or are in middle of reception.
        if (rxCnt != 0 || rxByte == 0xFB) {
            rxBuffer[rxCnt++] = rxByte;
        }
        // Check if all expected bytes are received.
        if (rxCnt == len) {
            // Validate checksum.
            if (rxBuffer[len - 1] == getCheckSum(rxBuffer, len - 1)) {
                rxCnt = 0; // Reset counter for next call.
                sTime = 0; // Reset start time.
                return 1;  // Return success on valid data and checksum.
            } else {
                rxCnt = 0; // Reset counter on checksum failure.
            }
        }
    }

    // Handle timeout (250 ms).
    if ((millis() - sTime) > 250) {
        sTime = 0; // Reset start time.
        rxCnt = 0; // Reset byte counter.
        return 0;  // Return timeout or error indicator.
    }

    return 2; // Indicate still waiting if no timeout or complete frame yet.
}

// {pumpType:1,startTime:0,finalTime:300,coeff0:0.001171875,pumpSlope:1}
