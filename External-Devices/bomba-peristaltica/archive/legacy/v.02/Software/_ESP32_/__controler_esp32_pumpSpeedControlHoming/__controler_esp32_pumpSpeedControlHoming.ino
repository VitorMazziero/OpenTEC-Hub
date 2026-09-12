#include <Adafruit_SSD1306.h>
#include <Wire.h>
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
 * @brief Define SSD1306 screen settings and initial logo.
 */

#define SCREEN_WIDTH 128  // OLED display width in pixels
#define SCREEN_HEIGHT 64  // OLED display height in pixels
#define OLED_RESET -1  // Reset pin (or -1 if sharing Arduino reset pin)

Adafruit_SSD1306 display(SCREEN_WIDTH, SCREEN_HEIGHT, &Wire, OLED_RESET); // I2C communication setup using the Adafruit_SSD1306.h library.

const unsigned char startupBitmap[] PROGMEM = {
	0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
	0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
	0x00, 0x00, 0x3f, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff, 0xff, 0xf0, 0x00, 0x00, 0x00, 
	0x00, 0x00, 0x80, 0x00, 0x1c, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x07, 0x00, 0x00, 0x00, 
	0x00, 0x00, 0x80, 0x00, 0x01, 0x80, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0xc0, 0x00, 0x00, 
	0x00, 0x00, 0x80, 0x00, 0x00, 0x60, 0x00, 0x00, 0x00, 0x1f, 0xff, 0xff, 0xc0, 0x30, 0x00, 0x00, 
	0x00, 0x7f, 0xff, 0xff, 0xe0, 0x10, 0x00, 0x00, 0x00, 0x6f, 0xff, 0xff, 0x20, 0x18, 0x00, 0x00, 
	0x00, 0x6f, 0xff, 0xff, 0xe0, 0x08, 0x00, 0x00, 0x03, 0xff, 0xc0, 0x1f, 0xe0, 0x0c, 0x00, 0x00, 
	0x0b, 0xff, 0x3f, 0xcf, 0xe0, 0x0c, 0x00, 0x00, 0x0b, 0xfe, 0x79, 0xf7, 0xe0, 0x0c, 0x00, 0x00, 
	0x03, 0xfd, 0xc0, 0x3b, 0xe0, 0x04, 0x00, 0x00, 0x00, 0x79, 0x40, 0x25, 0xe0, 0x04, 0x00, 0x00, 
	0x00, 0x7b, 0xa9, 0xcc, 0xe0, 0x04, 0x00, 0x00, 0x00, 0x77, 0x4f, 0x56, 0xe0, 0x04, 0x00, 0x00, 
	0x00, 0x76, 0x1f, 0xa6, 0xe0, 0x04, 0x00, 0x00, 0x00, 0x76, 0x59, 0xc6, 0xe0, 0x00, 0x00, 0x00, 
	0x00, 0x76, 0x78, 0xc2, 0x60, 0x1e, 0x00, 0x00, 0x00, 0x76, 0x78, 0xc2, 0x60, 0x1e, 0x00, 0x00, 
	0x00, 0x76, 0x5d, 0xe6, 0xe0, 0x1e, 0x00, 0x00, 0x00, 0x76, 0x1f, 0xa6, 0xe0, 0x7f, 0xc0, 0x00, 
	0x00, 0x73, 0x46, 0x46, 0xe0, 0x7f, 0xc0, 0x00, 0x00, 0x7b, 0x8f, 0x4c, 0xe0, 0x7f, 0x80, 0x00, 
	0x00, 0x79, 0x00, 0x25, 0xe0, 0x7f, 0xc0, 0x00, 0x00, 0x7c, 0xe0, 0x3b, 0xe0, 0x7f, 0xc0, 0x00, 
	0x03, 0xfe, 0x7f, 0xf7, 0xe0, 0x7f, 0xc0, 0x00, 0x0b, 0xff, 0x1f, 0xcf, 0xe0, 0x7f, 0xc0, 0x00, 
	0x0b, 0xff, 0xc0, 0x3f, 0xe0, 0x7f, 0xc0, 0x00, 0x03, 0xff, 0xff, 0xff, 0xe0, 0x7f, 0xc0, 0x00, 
	0x00, 0x6f, 0xff, 0xff, 0x20, 0x7f, 0xc0, 0x00, 0x00, 0x7f, 0xff, 0xff, 0xe0, 0x7f, 0x80, 0x00, 
	0x00, 0x3f, 0xff, 0xff, 0xc0, 0x04, 0x00, 0x00, 0x00, 0x07, 0xc0, 0x3e, 0x00, 0x04, 0x00, 0x00, 
	0x00, 0x07, 0xc0, 0x3e, 0x00, 0x04, 0x00, 0x00, 0x00, 0x03, 0xc0, 0x1c, 0x00, 0x0c, 0x00, 0x00, 
	0x00, 0x03, 0xff, 0xff, 0xff, 0xfc, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
	0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
	0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
};
// Buffer to hold the formatted flow rate string
char displayFlowRateBuffer[12];  // Buffer size for flow rate formatting

/**
 * @brief Define pin configurations for stepper motor control and receiver check.
 */
#define EN_PIN_PUMP 13  // Enable signal pin for the pump stepper motor.
#define STP_PIN_PUMP 14  // Step pulse pin for the pump stepper motor.
#define DIR_PIN_PUMP 27  // Direction control pin for the pump stepper motor.

#define EN_PIN_AXIS 26  // Enable signal pin for the axis stepper motor.
#define STP_PIN_AXIS 33  // Step pulse pin for the axisstepper motor.
#define DIR_PIN_AXIS 32  // Direction control pin for the axis stepper motor.

#define BUZZER_PIN 4 // Beeper output pin to indicate receiver status and warnings.

// Beeper control variables
volatile bool beeperActive = false; // Beeper is active continuously
volatile bool beeperOnce = false;   // Beeper should beep once
unsigned long beeperOnTime = 100;   // On time in milliseconds
unsigned long beeperOffTime = 100;  // Off time in milliseconds

// Task handle for the beeper and display
TaskHandle_t beeperTaskHandle = NULL;
TaskHandle_t displayTaskHandle = NULL;

// Function prototypes
void beeperTask(void *parameter);

/**
 * @brief Define pin configurations for the force sensor and magnetic sensor.
 */
#define FORCE_SENSOR_PIN 25  // Analog input pin for force sensor AO output.
#define MAGNETIC_SENSOR_PIN 35  // Analog input pin for magnetic sensor.

/**
 * @brief Define parameters for the force sensor and magnetic sensor.
 */
 
float hallVoltage;
float pressureVoltage;
const float referenceVoltage = 5.0; // Reference voltage for analog conversion
const float thresholdHall = 4.85; // Threshold for detecting near magnetic field
const float thresholdPressure = 1.0; // Threshold for detecting pressure
const float R_fixed = 10000.0;  // Fixed resistor value in ohms (10 kΩ) for the force sensor
bool isHomed = false;
bool isPressed = false;
bool initialPositionSet = false;
int pressTargetPosition = 5600;

/**
 * @brief Configure the stepper motors with the AccelStepper library.
 */
AccelStepper pumpStepper(AccelStepper::DRIVER, STP_PIN_PUMP, DIR_PIN_PUMP);
AccelStepper axisStepper(AccelStepper::DRIVER, STP_PIN_AXIS, DIR_PIN_AXIS); 

/**
 * @brief Maximum allowed motor speeds and acceleration.
 */
float maxRelativeSpeed = 100000; // Maximum speed allowed at 1 microstepping resolution.
int maxAccelerationAxisStepper = 1000; // Maximum allowed acceleration for the motor.
int maxAccelerationPumpStepper = 250; // Maximum allowed acceleration for the motor.

/**
 * @brief Variables to track previous state values for controls.
 */
uint16_t prevmStepping = 0;  // Previous microstepping value for comparison.
uint8_t prevEnable = LOW;    // Previous enable state to detect changes.

/**
 * @brief Address configuration for I2C or similar communication protocols.
 */
uint8_t slaveAddr = 1; // I2C slave address, if used.

/**
 * @brief List of available microstepping resolutions supported by the hardware.
 */
const uint8_t mStepList[] = { 1, 2, 4, 8, 16, 32, 64, 128 };
uint16_t smStep = 256; // Smallest default mStep
uint16_t mStepSet = 0;

/**
 * @brief Buffer and count variables for UART communication.
 */
uint8_t txBuffer[20];   // Transmission buffer for outgoing data.
uint8_t rxBuffer[20];   // Reception buffer for incoming data.mSteppingValue
uint8_t rxCnt = 0;      // Count of bytes received in the current packet.

/**
 * @brief Task handle for pulse operations on RTOS.
 */
TaskHandle_t pumpTaskHandle = NULL;  // RTOS task handle for pulse generation tasks.
unsigned long previousMicros = 0;  // Time of the last operation for timing control.

volatile int currentPressTargetPosition = 0;  // Shared variable for target position
volatile bool newPositionAvailable = false; // Flag to indicate a new target
int initialPressPosition = 0;               // Initial reference position for the press procedure
float accumulatedError = 0.0;               // Accumulated error for integral control
float fractionalError = 0.0;                // Stores fractional steps from previous iterations

TaskHandle_t axisTaskHandle = NULL; // Task handle for axis stepper

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
 * @brief Communication variables.
 */
int pumpType = 0; // Type of pump control algorithm to be used.
float startTime = 0.0; // Start time for timed operations.
float finalTime = -1.0; // End time for timed operations.
float coeff0 = 0.0; // Coefficient for constant flow equation.
float coeff1 = 0.0; // Coefficient for linear and exponential flow equation.
float coeff2 = 0.0; // Coefficient for linear and exponential flow equation.
float coeff3 = 0.0; // Coefficient for polynimuial flow equation.
float coeff4 = 0.0; // Coefficient for polynimuial flow equation.
float coeff5 = 0.0; // Coefficient for polynimuial flow equation.
float coeff6 = 0.0; // Coefficient for polynimuial flow equation.
float pumpSlope = 1.0; // Slope for converting raw speed to mL/min.
float pumpIntercept = 0.0; // Intercept for converting raw speed to mL/min.
int isRaw = 0; // Flag to determine if raw speed values should be used.
bool isMotorRunning = 0;
int home_x = 0;
int press_x = 0;
int direction = 0;

/**
 * @brief Speed and microstepping settings for the stepper motor.
 */
double effectiveSpeed = 0;  // Calculated effective speed in microsteps per second.
int effectiveMotorSpeed = 0;
uint16_t mStep = 0;    // Current microstepping resolution.
float previousrelativeSpeed = -1;

/**
 * @brief PID control parameters and variables for motor speed control with cumulative steps.
 */
double Kp = 0.00125; // Proportional gain for PID.
double Ki = 0.0001; // Integral gain for PID.
double Kd = 0.00075;  // Derivative gain for PID.
double integral = 0.0, derivative = 0.0, error = 0.0, previousError = 0.0; // PID variables.
double adjustment = 0; // Adjustment calculated from PID.
float antiWindupLimit = 1000.0; // Limit for PID integral windup.

unsigned long effectiveSteps = 0; // Current step count.
double relativeSpeed = 0; // Current speed of the motor in steps per second.
double relativeSpeed_mL = 0.0; // Current speed of the motor in mL per minute.
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

//filter parameters
float previousPressureVoltage = 0.0;
float previousHallVoltage = 0.0;
const int samplesNumber = 10;
float samples[samplesNumber];
uint8_t currentSampleIndex = 0;

// Function Prototypes
void displayTask(void *parameter);
void beeperTask();
void updateTime();
void processMotorControl();
void readPressureSensor();
void handleData(String data);
void updateVariables(String key, String value);
void resetVariables();
void readBluetoothData();
void readUSBData();
void sendOutputs();
void performPIDControl();
void runPumpMotor(double relativeSpeed);
void stopPumpMotor();
void updateSteps();
void calculateMStep(double relativeSpeed);
flowEquationOutputs calculateSpeed(int pumpType, float coeff0, float coeff1, float coeff2, unsigned long elapsedTime, float pumpSlope, float pumpIntercept, int isRaw, float pulseNumber);
uint8_t getCheckSum(uint8_t *buffer, uint8_t len);
uint8_t waitingForACK(uint8_t len);
void performPulses(void *parameter);
void homing();
void press();
void adjustPress();
void displayFlowRate();
void readHallSensor();
void readPressureSensor();

/**
 * @brief Initialize system components and configure initial states. Sets up serial communications
 * via USB and Bluetooth, configures pin modes for motor control, and initializes stepper settings.
 */
void setup() {
    // Initialize USB serial communication at 115200 baud rate.
    USB_Serial.begin(115200);

    // Initialize Bluetooth Serial with a device name.
    Bluetooth_Serial.begin("Peristaltic Pump v.02");

    // Initialize the display
    if (!display.begin(SSD1306_SWITCHCAPVCC, 0x3C)) {
      Serial.println(F("SSD1306 allocation failed"));
    }

    // Clear the display buffer
    display.clearDisplay();

    // Display the startup bitmap
    display.drawBitmap(36, 16, startupBitmap, 64, 48, WHITE);
    display.display();

    // Pause to show the startup screen
    delay(2000);

    // Clear the display after showing the startup bitmap
    display.clearDisplay();

    // Create the FreeRTOS task for the display
    xTaskCreate(
        displayTask,      // Function that implements the task.
        "Display Task",   // Text name for the task.
        2000,             // Stack size in words, not bytes.
        NULL,             // Parameter passed into the task.
        1,                // Priority at which the task is created.
        &displayTaskHandle // Used to pass out the created task's handle.
    );

    // Configure motor control pins as output.
    pinMode(STP_PIN_PUMP, OUTPUT);  // Step pin
    pinMode(STP_PIN_AXIS, OUTPUT);  

    // Enable the outputs.
    pumpStepper.enableOutputs();
    axisStepper.enableOutputs();

    // Disable pump stepper.
    pinMode(EN_PIN_PUMP, OUTPUT);  // Enable pin
    digitalWrite(EN_PIN_PUMP, HIGH);  // Disable the motor initially

    //Enable axis stepper
    pinMode(EN_PIN_AXIS, OUTPUT);  // Enable pin
    digitalWrite(EN_PIN_AXIS, LOW);  // Enable the motor

    // Set the maximum stepping speed of the stepper.
    pumpStepper.setMaxSpeed(maxRelativeSpeed);
    axisStepper.setMaxSpeed(maxRelativeSpeed);

    // Set the acceleration rate of the stepper.
    pumpStepper.setAcceleration(maxAccelerationPumpStepper);
    axisStepper.setAcceleration(maxAccelerationAxisStepper);

    // Initialize the force sensor and magnetic sensor pins
    pinMode(FORCE_SENSOR_PIN, INPUT);
    pinMode(MAGNETIC_SENSOR_PIN, INPUT);

    // Starts Serial2 communication on RX2 (GPIO16) and TX2 (GPIO17) at 38400 baud, using 8 data bits, no parity, and 1 stop bit configuration.
    MKS_Serial.begin(38400, SERIAL_8N1, 16, 17); 

    // Create the FreeRTOS task for the beeper
    xTaskCreate(
        beeperTask,      // Function that implements the task.
        "Beeper Task",   // Text name for the task.
        1000,            // Stack size in words, not bytes.
        NULL,            // Parameter passed into the task.
        1,               // Priority at which the task is created.
        &beeperTaskHandle // Used to pass out the created task's handle.
    );

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

    // Updates the system time or related timing functions.
    updateTime();

    // Processes any received motor control commands and adjusts motor settings accordingly.
    processMotorControl();

    // Display changes in flow rate
    vTaskDelay(pdMS_TO_TICKS(1));  // Yield to FreeRTOS tasks
}

/**
 * @brief Reads and processes data from the Bluetooth Serial.
 * Checks if data is available from the Bluetooth serial interface. If data is present,
 * it reads the incoming string until a '\n' character is encountered, trims any whitespace,
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
        beeperOnce = true;
    }
}

/**
 * @brief Reads and processes data from the USB Serial.
 * Checks if data is available from the USB serial interface. If data is present,
 * it reads the incoming string until a '\n' character is encountered, trims any whitespace,
 * and then processes the trimmed data. This method ensures that only complete messages
 * delimited by '\n' are processed.
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
        beeperOnce = true;
    }
}

/**
  * @brief Output the current control status to USB and Bluetooth for monitoring.
  
  */
void sendOutputs(){
  
    USB_Serial.printf("{targetSpeed: %.6f, targetSpeed_mL: %.6f, relativeSpeed: %.6f, effectiveSpeed: %.6f, effectiveMotorSpeed: %.d, targetSteps: %.lu, effectiveSteps: %.lu, mStep: %d, pressureVoltage: %.3f, currentTargetPosition: %d, timeSec: %.2f}\n",
                             targetSpeed_raw, targetSpeed_mL, relativeSpeed, effectiveSpeed, effectiveMotorSpeed, targetSteps, effectiveSteps, mStep, pressureVoltage, currentPressTargetPosition, elapsedTimeSec);
    
    Bluetooth_Serial.printf("{targetSpeed: %.6f, targetSpeed_mL: %.6f, relativeSpeed: %.6f, effectiveSpeed: %.6f, effectiveMotorSpeed: %.d, targetSteps: %.lu, effectiveSteps: %.lu, mStep: %d, pressureVoltage: %.3f, currentTargetPosition: %d, timeSec: %.2f}\n",
                             targetSpeed_raw, targetSpeed_mL, relativeSpeed, effectiveSpeed, effectiveMotorSpeed, targetSteps, effectiveSteps, mStep, pressureVoltage, currentPressTargetPosition, elapsedTimeSec);
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
    if (key == "\"pumpType\"") {
        int prevPumpType = pumpType;
        pumpType = value.toInt(); // Convert value to integer and update pumpType.
        if (pumpType != 0) {
            isMotorRunning = true;
        }
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"coeff0\"") {
        coeff0 = value.toFloat(); // Convert value to float and update coeff0.
        if (coeff0 < 0) {
            direction = 1;
            coeff0 = abs(coeff0);
        } else {
            direction = 0;
        }
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"coeff1\"") {
        coeff1 = value.toFloat(); // Convert value to float and update coeff1.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"coeff2\"") {
        coeff2 = value.toFloat(); // Convert value to float and update coeff2.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"startTime\"") {
        startTime = value.toFloat(); // Convert value to float and update startTime.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"finalTime\"") {
        finalTime = value.toFloat(); // Convert value to float and update finalTime.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"slope\"") {
        pumpSlope = value.toFloat(); // Convert value to float and update pumpSlope.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"intercept\"") {
        pumpIntercept = value.toFloat(); // Convert value to float and update pumpIntercept.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"isRaw\"") {
        isRaw = value.toInt(); // Convert value to integer and update isRaw.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"smStep\"") {
        mStepSet = value.toInt(); // Convert value to integer and update mStepSet.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"Kp\"") {
        Kp = value.toFloat(); // Convert value to float and update Kp.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"Ki\"") {
        Ki = value.toFloat(); // Convert value to float and update Ki.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"Kd\"") {
        Kd = value.toFloat(); // Convert value to float and update Kd.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"press_target\"") {
        pressTargetPosition = value.toInt(); // Convert value to integer and update pressTargetPosition.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"open\"") {
        home_x = value.toInt(); // Convert value to integer and update home_x.
        homing(); // Initiate homing procedure.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"press\"") {
        press_x = value.toInt(); // Convert value to integer and update press_x.
        press(); // Initiate press operation.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else if (key == "\"dir\"") {
        direction = value.toInt(); // Convert value to integer and update direction.
        USB_Serial.print(key);
        USB_Serial.print(": ");
        USB_Serial.println(value);
    } else {
        // Handle unknown key with an error message.
        USB_Serial.print("Unknown variable: ");
        USB_Serial.println(key);
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
    effectiveSteps = 0.0;
    relativeSpeed = 0;
    targetSpeed_raw = 0;
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
}

/**
 * @brief Stops the motor by disabling outputs and terminating the pulse task.
 * This function immediately stops the motor by disabling its outputs and safely terminates any
 * ongoing pulse tasks. It ensures that all related motor control activities are halted and
 * the system resources are appropriately freed.
 */
void stopPumpMotor() {
    // Set the enable pin low to disable the motor.
    digitalWrite(EN_PIN_PUMP, LOW);
    prevEnable = LOW;
    pumpType = 0;

    // Ensure the step pin is low to stop any ongoing pulses.
    digitalWrite(STP_PIN_PUMP, LOW);

    // If a pulse task is running, stop and delete the task to free up resources.
    if (pumpTaskHandle != NULL) {
        // Delete the task managed by FreeRTOS.
        vTaskDelete(pumpTaskHandle);
        // Nullify the task handle as it's no longer valid.
        pumpTaskHandle = NULL;
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
    if ((elapsedTimeMin <= finalTime && elapsedTimeMin >= startTime) && pumpType != 0 && isPressed) {
      // Perform PID control calculations and motor adjustments at specific intervals.
      if (int(elapsedTimeMillis) % 200 == 0 && int(elapsedTimeMillis) != previousPIDTime || int(elapsedTimeMillis) < 20) {        
        
        // Run or adjust motor speed according to the current speed settings and microstep configuration.
        runPumpMotor(relativeSpeed);
        if (!isMotorRunning){
          isMotorRunning = true;
        }
        
        // Update step counts to track motor movement accurately.
        updateSteps();

        // Perform pressure read and adjust if necessary
        adjustPress();
      }
    } 
    else if (pumpType != 0 && !isPressed){
      // Perform pressure read and adjust if necessary
      press();
    } 
    else {
        // Stop the motor if outside of operational window or conditions are not met.
        if (isMotorRunning){
          stopPumpMotor();
          isMotorRunning = false;
          resetVariables();
        }
        else{
          delay(250);
        }
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
        stepsSinceLast = (abs(relativeSpeed * mStep) * deltaT) / 1000.0;

        // Accumulate fractional steps.
        fractionalStepsAccumulator += stepsSinceLast;

        // Only update actual steps when at least one full step has accumulated.
        if (fractionalStepsAccumulator >= 1.0) {
            // Check for step count overflow and reset if necessary.
            if (effectiveSteps >= overflowThreshold) {
                // Reset actual steps and target steps if overflow occurs.
                effectiveSteps = 0;
            }

            // Add accumulated full steps to total and adjust the accumulator.
            effectiveSteps += (int)fractionalStepsAccumulator;
            fractionalStepsAccumulator -= (int)fractionalStepsAccumulator;
        }

        // Update the previous step time to the current time for the next calculation.
        previousStepUpdateTime = stepUpdateTime;
    } else {
        // If not enough time has passed, reset steps and accumulators.
        effectiveSteps = 0;
        fractionalStepsAccumulator = 0;
        stepsSinceLast = 0;

        // Update the previous step time to the current time to prepare for the next potential update.
        previousStepUpdateTime = millis();
    }
}

/**
 * @brief Start the motor with the specified absolute speed and microstepping value.
 * This function initiates motor movement at a given speed and microstepping resolution. It handles
 * microstepping adjustments, effective speed calculations, and motor state management, including enabling
 * the motor if it was previously disabled and starting a dedicated task for motor pulses if necessary.
 * 
 * @param relativeSpeed Absolute speed of the motor in seconds^-1, determining how fast the motor should spin.
 */
void runPumpMotor(double relativeSpeed) {
    // Check if the microstepping value is zero and set subdivision accordingly.
    if (relativeSpeed != previousrelativeSpeed) {
        // Call function to calculate or set the appropriate microstepping based on given parameters.
        calculateMStep(relativeSpeed);
    }

    // Execute PID control to adjust motor settings based on current feedback.
    performPIDControl();  

    // Calculate effective speed as the product of absolute speed and microstepping value.
    previousrelativeSpeed = relativeSpeed;
    effectiveSpeed = relativeSpeed * mStep;

    effectiveMotorSpeed = (int)round(effectiveSpeed);

    // Check if the motor was previously disabled and enable it if necessary.
    if (prevEnable == LOW) {
        // Send signal to enable the motor.
        digitalWrite(EN_PIN_PUMP, HIGH);
        // Update the previous enable state to reflect the motor is enabled.
        prevEnable = HIGH;
    }

    // Start a dedicated task to handle motor pulses if it's not already running.
    if (pumpTaskHandle == NULL) {
        xTaskCreate(
            performPulses,    // Pointer to the function that will handle motor pulses.
            "Pulse Task",     // A descriptive name for the task for debugging purposes.
            10000,            // Stack size allocated for the task in words (not bytes).
            NULL,             // Pointer that will be used as the task's parameter (not used here).
            1,                // Priority of the task, with higher numbers representing higher priority.
            &pumpTaskHandle  // Pointer to the task handle that will be updated by this function.
        );
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

    if (targetSpeed_raw <= 1) {
      Kp = 0.000075; Ki = 0.000025; Kd = 0.00001; // Lower coefficients for finer control.
    }
    else{
      Kp = 0.0005; Ki = 0.00025; Kd = 0.00001; 
    }

    // Calculate the error between target and actual steps, and integrate over time.
    long error = targetSteps - effectiveSteps;
    integral += error;
    integral = constrain(integral, -antiWindupLimit, antiWindupLimit); // Prevent integral wind-up.

    // Calculate the current speed setting and apply PID adjustment.
    relativeSpeed = targetSpeed_raw;
    float adjustment = (Kp * error) + (Ki * integral) + (Kd * (error - previousError));
    previousError = error;
    adjustment = constrain(adjustment, -relativeSpeed * 0.2, relativeSpeed* 0.2); // Limit adjustment to prevent excessive changes.

    // Prevent motor reversal if direction should not change.
    if ((relativeSpeed < 0 && targetSpeed_raw >= 0) || (relativeSpeed > 0 && targetSpeed_raw <= 0)) {
        relativeSpeed = 0;
    }
    else{
      relativeSpeed = relativeSpeed + adjustment;
      relativeSpeed_mL = pumpSlope * relativeSpeed + pumpIntercept;
    }

    sendOutputs();

    // Record the time of the last PID calculation to maintain consistent update intervals.
    previousPIDTime = int(elapsedTimeMillis);
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
 * @param coeff2 Coefficient for specialized calculations .
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
            digitalWrite(EN_PIN_PUMP, LOW); // Disable the stepper motor.
            break;

        case 1: // Case 1: Constant flow rate
            digitalWrite(EN_PIN_PUMP, HIGH); // Enable the stepper motor.
            // Calculate speed and steps based on whether raw speed is used directly.
            if (isRaw) {
                targetSpeed_raw = coeff0; // Use the base coefficient as the raw speed.
                float targetEffectiveSpeed_raw = targetSpeed_raw * mStep;
                targetSteps = targetEffectiveSpeed_raw * elapsedTimeSec; // Calculate target steps for the elapsed time.
            } else {
                targetSpeed_mL = coeff0; // Treat the base coefficient as mL/min.
                targetSpeed_raw = (targetSpeed_mL / pumpSlope) - pumpIntercept; // Convert mL/min to raw speed.
                float targetEffectiveSpeed_raw = targetSpeed_raw * mStep;
                targetSteps = targetEffectiveSpeed_raw * elapsedTimeSec; // Calculate target steps.
            }
            break;

        case 2: // Case 2: Linear varying flow rate
            digitalWrite(EN_PIN_PUMP, HIGH); // Enable the stepper motor.
            // Calculate linearly varying speed and steps.
            if (isRaw) {
                targetSpeed_raw = coeff1 + coeff2 * elapsedTimeSec; // Increase speed over time.
                targetSteps = coeff0 * elapsedTimeSec + coeff1 * pow(elapsedTimeSec, 2) / 2; // Area under speed-time graph.
            } else {
                targetSpeed_mL = coeff1 + coeff2 * elapsedTimeMin; // Increase mL/min over time.
                targetSpeed_raw = targetSpeed_mL / pumpSlope - pumpIntercept; // Convert to raw speed.
                targetSteps = coeff2 * pow(elapsedTimeSec, 2) / (120 * pumpSlope) + elapsedTimeSec * (-pumpIntercept * pumpSlope + coeff1) / pumpSlope; // Area under curve.
            }
            break;

        case 3: // Case 3: Exponential varying flow rate
            digitalWrite(EN_PIN_PUMP, HIGH); // Enable the stepper motor.
            // Calculate exponentially varying speed and steps.
            if (isRaw) {
                targetSpeed_raw = coeff1 * exp(coeff2 * elapsedTimeSec); // Exponentially increase speed.
                targetSteps = coeff1 * exp(coeff2 * elapsedTimeSec) / coeff2; // Integral of exponential function gives steps.
            } else {
                targetSpeed_mL = coeff1 * exp(coeff2 * elapsedTimeMin); // Exponential increase of mL/min.
                targetSpeed_raw = targetSpeed_mL / pumpSlope - pumpIntercept; // Convert to raw speed.
                targetSteps = (60 * coeff1 * exp(coeff2 * elapsedTimeSec / 60) / (pumpSlope * coeff2)) - 60 * coeff1 / (pumpSlope * coeff2); // Integral of exponential function modified for mL/min to raw conversion.
            }
            break;
          
        case 4: // Case 4: Polynomial varying flow rate
            digitalWrite(EN_PIN_PUMP, HIGH); // Enable the stepper motor.
            // Calculate polynomial varying speed and steps.
            if (isRaw) {
                // Calculate targetSpeed_raw using the polynomial function
                targetSpeed_raw = coeff0
                                  + coeff1 * elapsedTimeSec
                                  + coeff2 * pow(elapsedTimeSec, 2)
                                  + coeff3 * pow(elapsedTimeSec, 3)
                                  + coeff4 * pow(elapsedTimeSec, 4)
                                  + coeff5 * pow(elapsedTimeSec, 5)
                                  + coeff6 * pow(elapsedTimeSec, 6);

                // Calculate targetSteps as the integral of targetSpeed_raw over time
                targetSteps = (coeff0 * elapsedTimeSec)
                              + (coeff1 / 2.0) * pow(elapsedTimeSec, 2)
                              + (coeff2 / 3.0) * pow(elapsedTimeSec, 3)
                              + (coeff3 / 4.0) * pow(elapsedTimeSec, 4)
                              + (coeff4 / 5.0) * pow(elapsedTimeSec, 5)
                              + (coeff5 / 6.0) * pow(elapsedTimeSec, 6)
                              + (coeff6 / 7.0) * pow(elapsedTimeSec, 7);
            } else {
                // Calculate targetSpeed_mL using the polynomial function
                targetSpeed_mL = coeff0
                                + coeff1 * elapsedTimeMin
                                + coeff2 * pow(elapsedTimeMin, 2)
                                + coeff3 * pow(elapsedTimeMin, 3)
                                + coeff4 * pow(elapsedTimeMin, 4)
                                + coeff5 * pow(elapsedTimeMin, 5)
                                + coeff6 * pow(elapsedTimeMin, 6);

                // Convert targetSpeed_mL to targetSpeed_raw using the calibration curve
                targetSpeed_raw = targetSpeed_mL / pumpSlope - pumpIntercept;

                // Calculate cumulativeVolume_mL as the integral of targetSpeed_mL over time
                float cumulativeVolume_mL = (coeff0 * elapsedTimeMin)
                                            + (coeff1 / 2.0) * pow(elapsedTimeMin, 2)
                                            + (coeff2 / 3.0) * pow(elapsedTimeMin, 3)
                                            + (coeff3 / 4.0) * pow(elapsedTimeMin, 4)
                                            + (coeff4 / 5.0) * pow(elapsedTimeMin, 5)
                                            + (coeff5 / 6.0) * pow(elapsedTimeMin, 6)
                                            + (coeff6 / 7.0) * pow(elapsedTimeMin, 7);

                // Convert cumulativeVolume_mL to targetSteps
                targetSteps = (cumulativeVolume_mL * 60.0 / pumpSlope) - pumpIntercept * elapsedTimeSec;
            }
            break;
    }

    // Constrain raw speed to prevent exceeding maximum allowable values.
    targetSpeed_raw = std::clamp(targetSpeed_raw, -maxRelativeSpeed, maxRelativeSpeed);

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
 * @brief Set the microstepping value based on the desired stepping resolution and motor speed.
 * This function adjusts the microstepping value according to the absolute speed if no specific
 * microstepping value is provided, otherwise, it sets the microstepping directly from the input value.
 * 
 * @param relativeSpeed Absolute speed in seconds^-1, used for calculating appropriate microstepping if mStepSet is zero.
 */
void calculateMStep(double relativeSpeed) {
    relativeSpeed = abs(relativeSpeed);  // Ensure speed is a non-negative value.

    // Calculate microstepping based on speed if no explicit microstepping value is provided.
    if (mStepSet == 0) {
        if (relativeSpeed <= smStep * 2 / 256) {
            mStep = 256;  // Set maximum microstepping if speed is very low.
        } else {
            for (uint8_t step : mStepList) {
                // Find the first microstepping value that matches the speed requirement.
                if (relativeSpeed * step >= smStep) {
                    mStep = step;
                    break;
                }
            }
        }
    } else {
        mStep = mStepSet;  // Directly set the microstepping to the provided value.
    }

    // Send mStep only if it differs from the previous value
    //if (mStep != prevmStepping) {
      //#prevmStepping = mStep;  // Update the previous microstepping value
      //sendMstep();
    //}
}

void sendMstep() {
    // Prepare and send the new mStep command
    txBuffer[0] = 0xFA;                      // Frame header for the start of a new command.
    txBuffer[1] = slaveAddr;                 // Address of the slave device.
    txBuffer[2] = 0x84;                      // Function code for setting microstepping.
    txBuffer[3] = mStep;                     // The microstepping value to set.
    txBuffer[4] = getCheckSum(txBuffer, 4);  // Calculate checksum for the first 4 bytes of the buffer.

    MKS_Serial.write(txBuffer, 5);           // Send the 5 byte command packet via Serial2.

    waitingForACK(5);                        // Function call to handle acknowledgement.
    USB_Serial.print("mStep_sent: ");
    USB_Serial.println(mStep);
    USB_Serial.print("prevmStepping: ");
    USB_Serial.println(prevmStepping);
}

/**
 * @brief Executes motor pulses based on current motor settings.
 * This function is intended to run as a FreeRTOS task and continuously performs motor operations
 * such as setting speed and executing movement steps.
 * @param parameter Unused parameter, present to comply with FreeRTOS task signature.
 */
void performPulses(void *parameter) {

    // Infinite loop to continuously perform motor operations.
    while (true) {

        // Set motor speed and execute movement.
        if (direction == 0){
          pumpStepper.setSpeed(effectiveMotorSpeed);        
        }
        else{
          pumpStepper.setSpeed(-effectiveMotorSpeed);
        }
        pumpStepper.runSpeed();                      // Perform a single step operation at the set speed.

        vTaskDelay(1);  // Delay briefly to allow other tasks to execute.
    }
}

/**
 * @brief Executes motor pulses for axis stepper based on the current target position.
 * This function runs as a FreeRTOS task and handles motor movement asynchronously.
 * @param parameter Unused parameter, present to comply with FreeRTOS task signature.
 */
void performAxisPulses(void *parameter) {
    while (true) {
        // If a new target position is available, move the motor
        if (newPositionAvailable) {
            axisStepper.moveTo(currentPressTargetPosition); // Set the new target position
            newPositionAvailable = false;             // Clear the flag
        }

        // Execute motor movement towards the target position
        axisStepper.run();

        // Let other tasks run
        vTaskDelay(1);
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

/**
 * @brief Runs the x-axis motor in the opposite direction until the magnetic sensor reads 4.85V.
 * This method opens the pump head fully to allow hose connection and stops the pump motor.
 */

void homing() {
  digitalWrite(EN_PIN_AXIS, LOW);
  stopPumpMotor();
  // If a pulse task is running, stop and delete the task to free up resources.
    if (axisTaskHandle != NULL) {
        // Delete the task managed by FreeRTOS.
        vTaskDelete(axisTaskHandle);
        // Nullify the task handle as it's no longer valid.
        axisTaskHandle = NULL;
    }
  // Read the hall sensor
  readHallSensor();

  if (hallVoltage <= thresholdHall){
    
    Serial.println("Starting homing procedure...");
    axisStepper.setSpeed(-3000);  // Set initial speed for homing
    beeperActive = true; // Start beeping during homing

    while (true) {

      axisStepper.runSpeed();  // Move the stepper motor

      // Read the hall sensor
      readHallSensor();

      if (hallVoltage >= thresholdHall) {
        Serial.println("Homing complete. Hall sensor detected magnetic field.");
        axisStepper.stop();  // Stop the motor
        axisStepper.setCurrentPosition(0);
        isHomed = true;
        isPressed = false;
        beeperActive = false; // Stop beeping after homing
        break;
      }
    }
  }
}

void readHallSensor() {
  // Read the analog value from the Hall Effect sensor 
  float hallValue = analogRead(MAGNETIC_SENSOR_PIN);

  // Convert the analog reading to a voltage
  hallVoltage = hallValue * (referenceVoltage / 4095.0);
  hallVoltage = lowPassFilter(hallVoltage, previousHallVoltage, 0.5);
  previousHallVoltage = hallVoltage;
  USB_Serial.println(hallVoltage);
}

/**
* @brief Moves the NEMA17 motor towards the force sensor to find the pressed position.
* This method moves the motor quickly until the force sensor detects a small force,
* then backs off a few millimeters, and finally presses the hose slowly until a threshold is met.
*/
void press() {
  digitalWrite(EN_PIN_AXIS, LOW);

  if (!isHomed){
    homing();
  }

  stopPumpMotor();
    // If a pulse task is running, stop and delete the task to free up resources.
    if (axisTaskHandle != NULL) {
        // Delete the task managed by FreeRTOS.
        vTaskDelete(axisTaskHandle);
        // Nullify the task handle as it's no longer valid.
        axisTaskHandle = NULL;
    }
    // Read the hall sensor
    Serial.println("Starting PRESS procedure...");
    // Reset adjustPress corrections
    resetAdjustPressCorrections();
    beeperActive = true; // Start beeping during pressing

    // Set the target position 
    axisStepper.setMaxSpeed(1000);   // Set the maximum speed for this movement
    axisStepper.setAcceleration(250);
    axisStepper.moveTo(pressTargetPosition); // Specify the target position

    // Move to the target position
    while (axisStepper.distanceToGo() != 0) {
      axisStepper.run();  // Continue moving until the target position is reached
      readPressureSensor();
    }

    Serial.println("Reached target position.");
    beeperActive = false; // Stop beeping after pressing
    isPressed = true;
}

/**
 * @brief Resets the adjustments and corrections for `adjustPress`.
 * This function clears accumulated error, fractional error, and resets the target position.
 */
void resetAdjustPressCorrections() {
    // Reset integral controller variables
    accumulatedError = 0.0;
    fractionalError = 0.0;

    // Reset the current target position to the motor's current position
    initialPressPosition = axisStepper.currentPosition();
    currentPressTargetPosition = initialPressPosition;
    initialPositionSet = false;

    // Notify the FreeRTOS task to use the reset values
    newPositionAvailable = true;

    Serial.println("AdjustPress corrections reset.");
}

/**
 * @brief Reads the force sensor and adjusts the NEMA17 motor to maintain constant pressure using a proportional controller.
 */
void adjustPress() {
    digitalWrite(EN_PIN_AXIS, LOW);
    if (isHomed){
      // Create the FreeRTOS task if not already running
      if (axisTaskHandle == NULL) {
          xTaskCreate(
              performAxisPulses,    // Function to handle motor pulses.
              "Axis Task",          // Task name for debugging purposes.
              10000,                // Stack size in words.
              NULL,                 // Parameter (not used here).
              1,                    // Priority (1 is low, higher numbers have higher priority).
              &axisTaskHandle       // Task handle for reference.
          );
      }

      // Controller constants
      float desiredForce = 2.35;        // Desired force (adjust according to calibration)
      const float forceTolerance = 0.005;    // Allowable deviation
      const float Kp = 25;                // Proportional gain (steps per unit force)
      const int maxCorrectionSteps = 25; // Maximum step correction to prevent large jumps
      const int maxPosition = pressTargetPosition + 200;

      if (!initialPositionSet) {
          initialPressPosition = axisStepper.currentPosition(); // Get current position as the initial reference
          currentPressTargetPosition = initialPressPosition;         // Initialize target position
          initialPositionSet = true;
      }

      // Variables for calculating desiredForce
      static uint32_t startTime = 0;
      static uint32_t dataAccumulationStartTime = 0;
      static bool desiredForceCalculated = false;
      static float pressureSum = 0.0;
      static uint32_t pressureCount = 0;

      // Initialize startTime if not already initialized
      if (startTime == 0) {
          startTime = xTaskGetTickCount() * portTICK_PERIOD_MS;  // Get current time in milliseconds
      }

      uint32_t currentTime = xTaskGetTickCount() * portTICK_PERIOD_MS;
      uint32_t elapsedTime = currentTime - startTime;

      // Read the current pressure from the sensor
      readPressureSensor();

      // Check if we need to calculate desiredForce
      if (!desiredForceCalculated && isPressed) {
          if (elapsedTime >= 60000) { // After 60 seconds
              // Start accumulating data
              if (dataAccumulationStartTime == 0) {
                  dataAccumulationStartTime = currentTime;
              }
              // Accumulate pressure readings
              pressureSum += pressureVoltage;
              pressureCount++;

              uint32_t accumulationTime = currentTime - dataAccumulationStartTime;

              if (accumulationTime >= 120000) { // After 60 seconds of data accumulation
                  // Calculate the mean pressureVoltage
                  desiredForce = pressureSum / pressureCount + 0.05;
                  desiredForceCalculated = true;

                  // Optionally, print or log the calculated desiredForce
                  Serial.print("Calculated desiredForce: ");
                  Serial.println(desiredForce);
              }
          }
      } else {
        // Calculate error
        float error = desiredForce - pressureVoltage;

        // Calculate proportional correction
        int correctionSteps = static_cast<int>(Kp * error);

        // Clamp the correction to avoid excessive steps
        correctionSteps = constrain(correctionSteps, -maxCorrectionSteps, maxCorrectionSteps);

        // Apply the correction if error exceeds the tolerance
        if (abs(error) > forceTolerance && currentPressTargetPosition < maxPosition && pressureVoltage > 2 && currentPressTargetPosition > initialPressPosition) {
            currentPressTargetPosition += correctionSteps;  // Update the absolute target position
            newPositionAvailable = true;              // Notify the task of the new target
        }
      }   
    }
    else{
      homing();
      press();
    }
}

void readPressureSensor() {
  // Read the analog value
  float pressureValue = analogRead(FORCE_SENSOR_PIN);
  
  // Convert the analog reading to voltage
  pressureVoltage = pressureValue * (referenceVoltage / 4095.0);
  pressureVoltage = lowPassFilter(pressureVoltage, previousPressureVoltage, 0.8);
  pressureVoltage = movingAverageFilter(pressureVoltage);
  previousPressureVoltage = pressureVoltage;
  USB_Serial.print(millis());
  USB_Serial.print(",");
  USB_Serial.println(pressureVoltage);

}

// Low-pass Filter Function
float lowPassFilter(float value, float previousFilteredValue, float alpha) {
  return alpha * value + (1 - alpha) * previousFilteredValue;
}

// Moving Average Filter Function
float movingAverageFilter(float newValue) {
  samples[currentSampleIndex] = newValue;
  currentSampleIndex = (currentSampleIndex + 1) % samplesNumber;
  float sum = 0;
  for (uint8_t i = 0; i < samplesNumber; i++) {
    sum += samples[i];
  }
  return sum / samplesNumber;
}

/**
 * @brief FreeRTOS task that controls the beeper based on on and off times.
 * @param parameter Unused parameter, present to comply with FreeRTOS task signature.
 */
void beeperTask(void *parameter) {
    pinMode(BUZZER_PIN, OUTPUT);
    digitalWrite(BUZZER_PIN, LOW);
    bool beeperState = LOW;
    unsigned long lastToggleTime = millis();

    while (1) {
        unsigned long currentTime = millis();

        if (beeperOnce) {
            // Beep once with short time
            if (beeperState == LOW && currentTime - lastToggleTime >= beeperOffTime) {
                digitalWrite(BUZZER_PIN, HIGH);
                beeperState = HIGH;
                lastToggleTime = currentTime;
            } else if (beeperState == HIGH && currentTime - lastToggleTime >= beeperOnTime) {
                digitalWrite(BUZZER_PIN, LOW);
                beeperState = LOW;
                lastToggleTime = currentTime;
                beeperOnce = false; // Beeped once, reset the flag
            }
        } else if (beeperActive) {
            // Keep beeping while beeperActive is true
            if (beeperState == LOW && currentTime - lastToggleTime >= beeperOffTime) {
                digitalWrite(BUZZER_PIN, HIGH);
                beeperState = HIGH;
                lastToggleTime = currentTime;
            } else if (beeperState == HIGH && currentTime - lastToggleTime >= beeperOnTime) {
                digitalWrite(BUZZER_PIN, LOW);
                beeperState = LOW;
                lastToggleTime = currentTime;
            }
        } else {
            // Beeper is inactive
            if (beeperState == HIGH) {
                digitalWrite(BUZZER_PIN, LOW);
                beeperState = LOW;
            }
        }
        vTaskDelay(1); // Yield to other tasks
    }
}

/**
 * @brief Sets the values for the SSD1306 display for a given flow rate.
 * This method is called on each iteration to update the screen content.
 */

/**
 * @brief Sets the values for the SSD1306 display for a given flow rate.
 * This task updates the OLED display at regular intervals.
 */
void displayTask(void *parameter) {
    while (true) {
        // Clear the display buffer
        display.clearDisplay();

        if (targetSpeed_raw == 0) {
            display.setCursor(55, 0);   
            display.print("OFF");
        } else {
            display.setTextSize(2);
            display.setTextColor(WHITE);
            
            if (pumpType == 1) {
                display.setCursor(16, 0);   
                display.print("Constant");
            } 
            else if (pumpType == 2) {
                display.setCursor(28, 0);   
                display.print("Linear");
            } 
            else if (pumpType == 3) {
                display.setCursor(45, 0);   
                display.print("Exp");
            } 
            else if (pumpType == 4) {
                display.setCursor(40, 0);   
                display.print("Poly");
            }
        }

        // Draw a horizontal line below the mode label
        display.drawLine(0, 16, 127, 16, WHITE);
        
        if (!beeperActive){
          // Display the unit "mL/min" below the mode label
          display.setTextSize(1);
          display.setCursor(44, 18);  
          display.print("mL/min");

          // Format the flow rate to 6 decimal places
          if (pumpType == 0){
            dtostrf(0.0, 9, 2, displayFlowRateBuffer);  
          }
          else{
            dtostrf(relativeSpeed_mL, 9, 5, displayFlowRateBuffer);  
          }

          // Conditionally adjust cursor for displaying the flow rate based on its size
          display.setTextSize(2);
          display.setTextColor(WHITE);
          if (relativeSpeed_mL >= 10) {
              display.setCursor(10, 30);  // Cursor position for flow rates >= 10
          } else {
              display.setCursor(4, 30);   // Cursor position for flow rates < 10
          }

          // Display the formatted flow rate
          display.print(displayFlowRateBuffer);
        }
        else {
          // Conditionally adjust cursor for displaying the flow rate based on its size
          display.setTextSize(2);
          display.setTextColor(WHITE);
          display.setCursor(32, 30);
          display.print("HOMING");  
        }

        // Draw a horizontal line below the flow rate
        display.drawLine(0, 47, 127, 47, WHITE);

        // Convert flow rate to L/min and display it
        dtostrf(relativeSpeed_mL * 60 / 1000, 6, 4, displayFlowRateBuffer);  // Convert mL/min to L/min

        // Display the time information
        display.setTextSize(1);
        display.setCursor(0, 54);  
        display.print("t- ");
        display.print(finalTime / 60, 2);    // Display the time with 2 decimal places
        display.print("h / ");
        display.print(finalTime, 1);  // Display time in minutes with 1 decimal place
        display.print("min");
        // Update the OLED display with the new content
        display.display();

        // Delay for 500 ms to update the display periodically
        vTaskDelay(pdMS_TO_TICKS(500));
    }
}

// {"pumpType":1,"coeff0":1,"startTime":0,"finalTime":99,"isRaw":1}