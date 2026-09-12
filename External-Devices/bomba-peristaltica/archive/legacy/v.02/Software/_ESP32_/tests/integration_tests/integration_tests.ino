#include <Wire.h>
#include <Adafruit_GFX.h>
#include <Adafruit_SSD1306.h>
#include <AccelStepper.h>

int EN_PIN_PUMP = 12;   // Enable signal pin for the stepper motor.
int STP_PIN_PUMP = 14;  // Step pulse pin for the stepper motor.
int DIR_PIN_PUMP = 27;  // Direction control pin for the stepper motor.

int EN_PIN_AXIS = 26;   // Enable signal pin for the stepper motor.
int STP_PIN_AXIS = 33;  // Step pulse pin for the stepper motor.
int DIR_PIN_AXIS = 32;  // Direction control pin for the stepper motor.

/**
 * @brief Configure the stepper motor with the AccelStepper library.
 */
AccelStepper stepper(AccelStepper::DRIVER, STP_PIN_AXIS, DIR_PIN_AXIS);


//     ####################   Define OLED display parameters    #################### //

#define SCREEN_WIDTH 128  // OLED display width in pixels
#define SCREEN_HEIGHT 64  // OLED display height in pixels
#define OLED_RESET -1  // Reset pin (or -1 if sharing Arduino reset pin)

#define BUZZER_PIN 4 // Beeper output pin to indicate receiver status and warnings.

Adafruit_SSD1306 display(SCREEN_WIDTH, SCREEN_HEIGHT, &Wire, OLED_RESET);

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

// Flow rate variables
float flowRate = 10;             // Starting flow rate
float flowRateStep = 0.002434;    // Flow rate increment
float flowRateMax = 12.0;         // Maximum flow rate

// Buffer to hold the formatted flow rate string
char flowRateBuffer[12];  // Buffer size for flow rate formatting



//     ####################   Homming sensors configuraion    #################### //

// Hall sensor variables
const int hallPin = 35;          // Analog pin connected to the 49E Hall Effect sensor
float hallVoltage;                   // Variable to store the voltage value

// Force sensor variables
const int forcePin = 25;         // Analog input pin for force sensor
float pressureVoltage;
const float R_fixed = 10000.0;   // Fixed resistor value in ohms (10 kΩ)

//filter parameters
float previousPressureVoltage = 0.0;
float previousHallVoltage = 0.0;
const int samplesNumber = 10;
float samples[samplesNumber];
uint8_t currentSampleIndex = 0;

// Voltage settings
const float referenceVoltage = 5.0;         // Reference voltage for analog conversion
const float thresholdHall = 4.8; // Threshold for detecting near magnetic field
const float thresholdPressure = 1.0; // Threshold for detecting near magnetic field

// Timing variables for flow rate update
unsigned long previousMillis = 0;
const long interval = 100;  // Interval (in ms) at which to update flow rate

void setup() {
  // Initialize serial communication for debugging
  Serial.begin(115200);

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

  // Initialize stepper motor
  pinMode(EN_PIN_AXIS, OUTPUT);
  digitalWrite(EN_PIN_AXIS, LOW);  // Enable stepper driver
  stepper.setMaxSpeed(10000);
  stepper.setAcceleration(10000);

  // Perform homing procedure
  //homing();
  //press();
}

void loop() {
  // Update flow rate based on the defined time interval
  unsigned long currentMillis = millis();
  if (currentMillis - previousMillis >= interval) {
    previousMillis = currentMillis;

    // Update the flow rate
    flowRate += flowRateStep;
    if (flowRate > flowRateMax) {
      flowRate = 0.0001;  // Reset flow rate to starting value
    }

    // Display the flow rate on the OLED
    displayFlowRate();

    // Read hall sensor data
    readHallSensor();

    // Read pressure data
    redPressureSensor();

    // Print the results
    Serial.println(pressureVoltage, 3);
    Serial.println(hallVoltage, 5);
  }

}

void readHallSensor() {
  // Read the analog value from the Hall Effect sensor 
  float hallValue = analogRead(hallPin);

  // Convert the analog reading to a voltage
  hallVoltage = hallValue * (referenceVoltage / 4095.0);
  hallVoltage = lowPassFilter(hallVoltage, previousHallVoltage, 0.5);
  previousHallVoltage = hallVoltage;
}

void redPressureSensor() {
  // Read the analog value
  float pressureValue = analogRead(forcePin);
  
  // Convert the analog reading to voltage
  pressureVoltage = pressureValue * (referenceVoltage / 4095.0);
  pressureVoltage = lowPassFilter(pressureVoltage, previousPressureVoltage, 0.9);
  pressureVoltage = movingAverageFilter(pressureVoltage);
  previousPressureVoltage = pressureVoltage;

  
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

void homing() {

  Serial.println("Starting homing procedure...");
  stepper.setSpeed(4000);  // Set initial speed for homing

  while (true) {
    stepper.runSpeed();  // Move the stepper motor

    // Read the hall sensor
    readHallSensor();

    if (hallVoltage >= thresholdHall) {
      Serial.println("Homing complete. Hall sensor detected magnetic field.");
      stepper.stop();  // Stop the motor
      break;
    }
  }
}

void press(){
  Serial.println("Starting PRESS procedure...");

  // Set the target position (e.g., 1000 steps away from the homing position)
  int targetPosition = 20000; // Adjust this value as needed for your specific setup
  stepper.setMaxSpeed(2500);   // Set the maximum speed for this movement
  stepper.setAcceleration(1000);
  stepper.setCurrentPosition(0);
  stepper.moveTo(targetPosition); // Specify the target position

  // Move to the target position
  while (stepper.distanceToGo() != 0) {
    stepper.run();  // Continue moving until the target position is reached
    redPressureSensor();
  }

  Serial.println("Reached target position.");
}

void displayFlowRate() {
  // Clear the display buffer
  display.clearDisplay();

  if (flowRate == 0){
    display.setCursor(45, 0);   // Cursor position unchanged
    display.print("OFF");
  }
  else{
    // Display a fixed label for the mode of operation (constant, linear, or exp)
    display.setTextSize(2);
    display.setTextColor(WHITE);
    //display.setCursor(16, 0);   // Cursor position unchanged
    //display.print("Constant");

    //display.setCursor(28, 0);   // Cursor position unchanged
    //display.print("Linear");
    
    display.setCursor(45, 0);   // Cursor position unchanged
    display.print("Exp");
  }

  // Draw a horizontal line below the mode label
  display.drawLine(0, 16, 127, 16, WHITE);

  // Display the unit "mL/min" below the mode label
  display.setTextSize(1);
  display.setCursor(44, 18);  // Cursor position unchanged
  display.print("mL/min");

  // Format the flow rate to 6 decimal places
  dtostrf(flowRate, 9, 6, flowRateBuffer);  // Format: 9 characters total, 6 decimals

  // Conditionally adjust cursor for displaying the flow rate based on its size
  display.setTextSize(2);
  display.setTextColor(WHITE);
  if (flowRate >= 10) {
    display.setCursor(10, 30);  // Cursor position for flow rates >= 10
  } else {
    display.setCursor(4, 30);   // Cursor position for flow rates < 10
  }

  // Display the formatted flow rate
  display.print(flowRateBuffer);

  // Draw a horizontal line below the flow rate
  display.drawLine(0, 47, 127, 47, WHITE);

  // Convert flow rate to L/min and display it
  dtostrf(flowRate * 60 / 1000, 6, 4, flowRateBuffer);  // Convert mL/min to L/min

  // Example time variable
  float time = 10.45;

  // Display the time information
  display.setTextSize(1);
  display.setCursor(0, 54);  // Cursor position unchanged
  display.print("t- ");
  display.print(time, 2);    // Display the time with 2 decimal places
  display.print("h / ");
  display.print(time * 60, 1);  // Display time in minutes with 1 decimal place
  display.print("min");

  // Update the OLED display with the new content
  display.display();
}

/**
 * @brief FreeRTOS Task for reading and calculating the real-time position of the motor.
 * This task continuously checks the motor's position by sending requests to the motor driver
 * and processes the responses to update the stepLocation in real-time.
 * 
 * @param parameter FreeRTOS task parameter (not used here).
 
void readPumpLocationTask(void *parameter) {
    while (true) {
        // The original functionality of readPumpLocation
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
        if (isWaiting && millis() - lastCallTime > 25) { // Check every 25 milliseconds.
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

        // Delay to allow FreeRTOS to schedule other tasks
        vTaskDelay(pdMS_TO_TICKS(50)); // Delay for 50 milliseconds
    }
}
*/