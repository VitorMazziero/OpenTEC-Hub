#include <AccelStepper.h>
#include <TMCStepper.h>
#include <PubSubClient.h>
#include <WiFi.h>

#define EN_PIN    5  // Enable
#define DIR_PIN   2   // Direction
#define STEP_PIN  4   // Step
#define USB_PORT Serial // USB port
#define SERIAL_PORT Serial2  // Serial port for TMC2209
#define DRIVER_ADDRESS 0b00  // TMC2209 Driver address
#define R_SENSE 0.11f        // Value in ohms for current sense

const char* mqtt_server = "192.168.0.100";  // MQTT Broker IP
const int mqtt_port = 1883;               // MQTT port (default is 1883)
const char* mqtt_user = "MVLabs";         // MQTT username
const char* mqtt_password = "xW1-*82y.0vXoLMWS}$k&EBI482~uO"; // MQTT password
const char* mqtt_topic = "pump_01"; // MQTT topic

WiFiClient espClient;
PubSubClient client(espClient);

const char* ssid = "LUX";  // Your WiFi SSID
const char* password = "27061965";  // Your WiFi password

TMC2209Stepper driver(&SERIAL_PORT, R_SENSE, DRIVER_ADDRESS);
AccelStepper stepper(AccelStepper::DRIVER, STEP_PIN, DIR_PIN);

// String variables
String Read_USB;
String inputString = ""; // String to store the received input
String extractedValues = ""; // Store the extracted values

// Stepper speed type
float stepperSpeed = 0.0;
float stepperSpeed_raw = 0.0;
float stepperSpeed_mL = 0.0;

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
unsigned long prev_millis = -1;      // For USB printing
unsigned long prev_mqtt_millis = -1; // For MQTT publishing
int count = 1;
float t = 0;
float t_sec = 0;
float t_min = 0;

// Buffer

const int maxMessages = 50;  // Adjust size as needed
String messageBuffer[maxMessages];
int messageCount = 0;

// Pulse config
unsigned long pulse_prev_time = 0;
int pulse_count = 0;
int pulse_number = 0;
int lowerBound = 0;
int upperBound = 1;
int currentIntegerValue = lowerBound;
float Period = 0.0;


void setup() {
  pinMode(EN_PIN, OUTPUT);
  digitalWrite(EN_PIN, HIGH); // Disable driver
  SERIAL_PORT.begin(115200); // Serial for TMC2209
  USB_PORT.begin(115200);  // Serial for Mega communication

  driver.begin();
  driver.toff(5);
  driver.rms_current(1500); // Motor RMS current
  driver.microsteps(16);   // Microstepping
  driver.semin(5);         // CoolStep minimum current
  driver.semax(10);         // CoolStep maximum current
  driver.sedn(0b01);       // CoolStep down step
  driver.SGTHRS(100);      // StallGuard threshold
  driver.en_spreadCycle(false); // Disable SpreadCycle
  driver.intpol(true);     // interpolate to 256 microsteps
  
  // Enable StealthChop
  //driver.en_pwm_mode(true);      // Enable extremely quiet stepping
  driver.pwm_autoscale(true);    // Enable automatic current scaling
  driver.pwm_freq(1);            // Adjust to reduce or eliminate noise further
  driver.pwm_grad(4);            // Adjust to control the rate of current change
  //driver.pwm_ampl(180);          // Adjust to set the amplitude of current

  stepper.setMaxSpeed(1000);     // Max speed
  stepper.setAcceleration(10000);// Acceleration

  WiFi.begin(ssid, password);
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    USB_PORT.println("Connecting to WiFi...");
  }
  USB_PORT.println(WiFi.localIP());
  client.setServer(mqtt_server, mqtt_port);
  client.setCallback(mqttCallback);
}

void loop() {

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
      Pump_slope = values[6].toFloat() / -10000000000;
      Pump_intercept = values[7].toFloat() / 1000000;
      raw = values[8].toFloat();
      t = 0;
      prev_time = millis();
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
    // Perform the desired calculations based on the received values
    stepperSpeed = calculateRotation(Pumptype, b0, b1, b2, t, Pump_slope, Pump_intercept, raw, pulse_number);
    if (stepperSpeed == int(stepperSpeed)){
      currentIntegerValue = stepperSpeed;
      // Set the stepper motor speed
      stepper.setSpeed(stepperSpeed);
      // Move the stepper motor
      stepper.runSpeed();
    } else {
      lowerBound = int(stepperSpeed);
      upperBound = lowerBound + 1;
      currentTime = millis();
      
      if (currentTime - startTime <= calculatePeriod(stepperSpeed)) {
        // Switch to the other integer value
        currentIntegerValue = upperBound;
      } 
      
      else {
        currentIntegerValue = lowerBound;
      }
      stepper.setSpeed(currentIntegerValue);
      stepper.runSpeed();
      if (currentTime - startTime > 10000){
        startTime = currentTime; // Reset the start time for the new period
      }
    }
  } else {
      stepperSpeed = 0.0;
      currentIntegerValue = 0;
      stepper.setSpeed(int(stepperSpeed));
      stepper.runSpeed();
      digitalWrite(EN_PIN, HIGH);
    }

  if (int(t_sec) % 10 == 0 and int(t_sec) != prev_millis) {  
    USB_PORT.print("{");
    USB_PORT.print(stepperSpeed);
    USB_PORT.println(",}");
    USB_PORT.flush();
    prev_millis = int(t_sec);
  }

  // Check MQTT connection and handle MQTT tasks
  if (!client.connected()) {
    reconnect();
  } else {
    client.loop();

    // Normal MQTT publishing
    if (int(t_sec) % 10 == 0 && int(t_sec) != prev_mqtt_millis) {
      char msg[50];
      snprintf(msg, 50, "{%.2f,}", stepperSpeed);
      if (client.publish(mqtt_topic, msg)) {
        USB_PORT.println("Message published [" + String(mqtt_topic) + "]: " + msg);
      } else {
        USB_PORT.println("Failed to publish message.");
      }
      prev_mqtt_millis = int(t_sec);
    }
  }
}

// Function to calculate the velocity based on the received values
float calculateRotation(int Pumptype, float b0, float b1, float b2, unsigned long t, float Pump_slope, float Pump_intercept, int raw, float pulse_number) {
  unsigned long currentMillis;
  // Perform the desired calculations based on the Pumptype and coefficients
  switch (Pumptype) {
    case 0: 
      stepperSpeed_raw = 0;
      digitalWrite(EN_PIN, HIGH); // Disable the motor driver
      break;

    case 1:
      if (raw == 1){
        stepperSpeed_raw = b0;
        digitalWrite(EN_PIN, LOW); // Enable the motor driver
        digitalWrite(DIR_PIN, LOW);
        break;
      }
      else {
        stepperSpeed_mL = b0;
        discriminant = Pump_intercept * Pump_intercept - 4 * Pump_slope * -stepperSpeed_mL;
        stepperSpeed_raw = (-Pump_intercept + sqrt(discriminant)) / (2 * Pump_slope);
        digitalWrite(EN_PIN, LOW); 
        digitalWrite(DIR_PIN, LOW);
        break;
      }

    case 2:
      stepperSpeed_mL = b0 + b1*t_min;
      discriminant = Pump_intercept * Pump_intercept - 4 * Pump_slope * -stepperSpeed_mL;
      stepperSpeed_raw = (-Pump_intercept + sqrt(discriminant)) / (2 * Pump_slope);
      digitalWrite(EN_PIN, LOW);
      digitalWrite(DIR_PIN, LOW);
      break;

    case 3:
      stepperSpeed_mL = b0*exp(b1*t_min);
      discriminant = Pump_intercept * Pump_intercept - 4 * Pump_slope * -stepperSpeed_mL;
      stepperSpeed_raw = (-Pump_intercept + sqrt(discriminant)) / (2 * Pump_slope);
      digitalWrite(EN_PIN, LOW);
      digitalWrite(DIR_PIN, LOW);
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
            discriminant = Pump_intercept * Pump_intercept - 4 * Pump_slope * -stepperSpeed_mL;
            stepperSpeed_raw = (-Pump_intercept + sqrt(discriminant)) / (2 * Pump_slope);
          }
      } else if ((currentMillis - pulse_prev_time) < (offInterval + onInterval)) {
          // Pump is off
          stepperSpeed_raw = 0;
      }
      else{
        // Pump is off
        stepperSpeed_raw = 0;
      }
      digitalWrite(EN_PIN, LOW);
      digitalWrite(DIR_PIN, LOW);
      break;
  }
  if (stepperSpeed_raw > 1000){
    stepperSpeed_raw = 1000;
  }
  if (stepperSpeed_raw < -1000){
    stepperSpeed_raw = -1000;
  }
  return stepperSpeed_raw;
}

unsigned long calculatePeriod(int intValue) {
  float decimalPart = (stepperSpeed - intValue) * 100.0; 
  decimalPart = int(decimalPart) / 100.0; // Remove +3 decimals
  // Calculate a period that's proportional to the distance from the current integer value to the target value
  unsigned long period = map(abs(decimalPart * 100), 0, 100, 1000, 10000); // Adjust the mapping as needed
  return period;
}

void mqttCallback(char* topic, byte* payload, unsigned int length) {
    // Convert the incoming byte array to a String
    String message;
    for (unsigned int i = 0; i < length; i++) {
        message += (char)payload[i];
    }
    
    // Process the message in the same way as the USB data
    if (message.startsWith("{") && message.length() > 10) {
        String inputString = message.substring(1); // Remove first "{"
        // Split the inputString into an array of substrings using ","
        String values[9];
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
        if (values[0].length() > 0) {
          Pumptype = values[0].toInt();
          ti = values[1].toFloat();
          tf = values[2].toFloat();
          b0 = values[3].toFloat();
          b1 = values[4].toFloat();
          b2 = values[5].toFloat();
          Pump_slope = values[6].toFloat() / -10000000000;
          Pump_intercept = values[7].toFloat() / 1000000;
          raw = values[8].toFloat();
          t = 0;
          prev_time = millis();
        }
      
      USB_PORT.println("Message arrived [" + String(topic) + "]: " + message);
    }
}

boolean reconnect() {
  if (client.connected()) {
    return true; // Already connected to MQTT broker
  }

  unsigned long now = millis();
  if (now - lastReconnectAttempt > 5000) {
    lastReconnectAttempt = now;
    USB_PORT.println("Attempting MQTT connection...");

    if (client.connect("ESP32Client", mqtt_user, mqtt_password)) {
      USB_PORT.println("MQTT connected");
      client.subscribe(mqtt_topic);

      // Publish messages from buffer
      for (int i = 0; i < messageCount; i++) {
        client.publish(mqtt_topic, messageBuffer[i].c_str());
        USB_PORT.println("Published buffered message: " + messageBuffer[i]);
      }
      messageCount = 0; // Reset buffer
      return true;
    } else {
      USB_PORT.print("MQTT connection failed, rc=");
      USB_PORT.println(client.state());
    }
  }
  return false;
}


