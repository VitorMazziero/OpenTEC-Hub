#include <WiFi.h>
#include <PubSubClient.h>
#include <AccelStepper.h>

// WiFi credentials
const char* ssid = "LUX";
const char* password = "27061965";

// MQTT Broker settings
const char* mqttServer = "broker.hivemq.com";
const int mqttPort = 1883;
const char* mqttTopic = "stepper/control";

unsigned long lastMillis = 0;

// Stepper motor settings
#define STEP_PIN 4
#define DIR_PIN 5

// AccelStepper instance for controlling the stepper motor
AccelStepper stepper(AccelStepper::DRIVER, STEP_PIN, DIR_PIN);

// WiFi and MQTT Clients
WiFiClient espClient;
PubSubClient client(espClient);

void setup() {
  Serial.begin(9600);
  // Connect to WiFi
  WiFi.begin(ssid, password);
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.println("Disconnected");
  }

  // Connect to MQTT Broker
  client.setServer(mqttServer, mqttPort);
  client.setCallback(mqttCallback);

  // Initialize stepper motor settings
  stepper.setMaxSpeed(1000);
  stepper.setAcceleration(500);

  // Attempt to connect to MQTT broker
  connectToMQTT();
}

void loop() {
  if (!client.connected()) {
    connectToMQTT();
  }
  client.loop(); // Maintain MQTT connection

  // Check if one second has passed; if so, publish the message
  if (millis() - lastMillis > 1000) {
    lastMillis = millis();
    client.publish(mqttTopic, "1"); // Publish "1" to the MQTT topic
    Serial.println("Topic_sent");
  }

  // Run the stepper motor
  stepper.runSpeed();
}

void connectToMQTT() {
  while (!client.connected()) {
    if (client.connect("ESP32Client")) {
      client.subscribe(mqttTopic); // Subscribe to the topic
    } else {
      delay(50); // Wait 5 seconds before retrying
    }
  }
}

void mqttCallback(char* topic, byte* payload, unsigned int length) {
  // Convert the incoming byte array to a String
  String message;
  for (unsigned int i = 0; i < length; i++) {
    message += (char)payload[i];
  }

  // Set stepper speed based on the received message
  int speed = message.toInt();
  stepper.setSpeed(speed);
}
