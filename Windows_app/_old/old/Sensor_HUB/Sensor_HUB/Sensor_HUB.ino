#include <esp_now.h>
#include <WiFi.h>
#include <BluetoothSerial.h> // Include BluetoothSerial library

// Replace with your flowmeter controller's MAC Address
uint8_t flowmeterAddress[] = {0xFC, 0xB4, 0x67, 0x77, 0xB5, 0x04};

// Initialize BluetoothSerial object
BluetoothSerial SerialBT;

// Structure for outgoing commands (to peripherals)
typedef struct __attribute__((packed)) {
  char data[200]; // Adjust size as needed
} esp_now_command_t;

// Structure for incoming data (from peripherals)
typedef struct __attribute__((packed)) {
  char message[200]; // Adjust size as needed
} esp_now_data_t;

// Variables to hold incoming and outgoing data
esp_now_data_t incomingData;
esp_now_command_t outgoingCommand;

// Command Queue Definitions
#define MAX_COMMANDS 10 // Maximum number of queued commands

String commandQueue[MAX_COMMANDS];
int queueHead = 0;
int queueTail = 0;

// Hub variables
unsigned long dataInterval = 100; // Default delay in milliseconds

// Time tracking
unsigned long startTime = 0;

// Flowmeter data variables
bool flowmeterConnected = false;
float flowmeter_readFlowRate = 0.0;
float flowmeter_readFlowSetpoint = 0.0;

// Last time data was received from flowmeter
unsigned long lastFlowmeterDataTime = 0;
const unsigned long deviceTimeout = 2000; // Time in milliseconds to consider device disconnected

// Function to enqueue a new command
bool enqueueCommand(String command) {
  int nextTail = (queueTail + 1) % MAX_COMMANDS;
  if (nextTail == queueHead) {
    // Queue is full
    Serial.println("Command Queue Full. Command discarded.");
    return false;
  }
  commandQueue[queueTail] = command;
  queueTail = nextTail;
  return true;
}

// Function to dequeue a command
bool dequeueCommand(String &command) {
  if (queueHead == queueTail) {
    // Queue is empty
    return false;
  }
  command = commandQueue[queueHead];
  queueHead = (queueHead + 1) % MAX_COMMANDS;
  return true;
}

// Callback when data is received from peripheral
void OnDataRecv(const esp_now_recv_info_t *info, const uint8_t *incomingDataBuffer, int len) {
  memcpy(&incomingData, incomingDataBuffer, sizeof(incomingData));
  String data = String(incomingData.message);

  // Process the received data
  // Instead of printing, we parse and store the data into variables
  // We can use a similar parsing method as in the flowmeter code

  // For now, we only have flowmeter data
  // We can identify the device by its MAC address

  char macStr[18];
  snprintf(macStr, sizeof(macStr), "%02X:%02X:%02X:%02X:%02X:%02X",
           info->src_addr[0], info->src_addr[1], info->src_addr[2],
           info->src_addr[3], info->src_addr[4], info->src_addr[5]);
  String senderMac = String(macStr);

  // Check if the data is from the flowmeter
  char flowmeterMacStr[18];
  snprintf(flowmeterMacStr, sizeof(flowmeterMacStr), "%02X:%02X:%02X:%02X:%02X:%02X",
           flowmeterAddress[0], flowmeterAddress[1], flowmeterAddress[2],
           flowmeterAddress[3], flowmeterAddress[4], flowmeterAddress[5]);
  String flowmeterMac = String(flowmeterMacStr);

  if (senderMac.equals(flowmeterMac)) {
    // Data is from flowmeter
    flowmeterConnected = true;
    lastFlowmeterDataTime = millis();

    // Parse the data
    processFlowmeterData(data);
  }

  // Optionally, forward this data via Bluetooth to the user
  // SerialBT.println(data);
}

// Callback when data is sent via ESP-NOW
void OnDataSent(const uint8_t *mac_addr, esp_now_send_status_t status) {
  Serial.print("Last Packet Send Status: ");
  Serial.println(status == ESP_NOW_SEND_SUCCESS ? "Success" : "Fail");
}

void setup() {
  Serial.begin(115200);

  // Initialize Bluetooth
  SerialBT.begin("Central Hub"); // Start Bluetooth with device name "Central Hub"

  // Initialize Wi-Fi in STA mode
  WiFi.mode(WIFI_STA);
  WiFi.disconnect(); // Ensure Wi-Fi is not connected to any network

  // Print the MAC address
  Serial.print("Central Hub ESP32 MAC Address: ");
  Serial.println(WiFi.macAddress());

  // Initialize ESP-NOW
  if (esp_now_init() != ESP_OK) {
    Serial.println("Error initializing ESP-NOW");
    while (1);
  }

  // Register the receive callback
  esp_now_register_recv_cb(OnDataRecv);

  // Register the send callback
  esp_now_register_send_cb(OnDataSent);

  // Register the peer (flowmeter controller)
  esp_now_peer_info_t peerInfo;
  memcpy(peerInfo.peer_addr, flowmeterAddress, 6);
  peerInfo.channel = 0;
  peerInfo.encrypt = false;

  if (esp_now_add_peer(&peerInfo) != ESP_OK){
    Serial.println("Failed to add peer");
    while (1);
  }

  // Initialize start time
  startTime = millis();
}

void readSerialData() {
  if (SerialBT.available() || Serial.available()) {
    String data;
    if (SerialBT.available()) {
      data = SerialBT.readStringUntil('\n');
      Serial.println("Received via Bluetooth: " + data);
    } else {
      data = Serial.readStringUntil('\n');
      Serial.println("Received via USB Serial: " + data);
    }
    processReceivedData(data);
  }
}

void processReceivedData(String data) {
  data.trim();

  if (data[0] == '{' && data[data.length() - 1] == '}') {
    data = data.substring(1, data.length() - 1); // Remove braces
    int start = 0;
    String target = "";
    String remainingData = ""; // To store the rest of the data
    bool targetFound = false;

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

        // Remove quotes from key and value if present
        key.replace("\"", "");
        value.replace("\"", "");

        if (key == "target") {
          target = value;
          targetFound = true;
        } else {
          // Accumulate the rest of the data to send
          if (remainingData.length() > 0) {
            remainingData += ",";
          }
          remainingData += key + ":" + value;
        }

        start = commaIndex + 1;
      } else {
        Serial.println("Error: Command with invalid format");
        break;
      }
    }

    if (targetFound) {
      if (target == "hub") {
        // Process command intended for the hub
        processHubCommand(remainingData);
      } else if (target == "flowmeter") {
        // Enqueue command to send to flowmeter
        String commandToSend = "{" + remainingData + "}";
        if (enqueueCommand(commandToSend)) {
          Serial.println("Command enqueued for transmission to " + target);
        }
      } else {
        Serial.println("Error: Unknown target '" + target + "'");
      }
    } else {
      Serial.println("Error: No target specified in command");
    }
  } else {
    Serial.println("Error: Invalid data format");
  }
}

void processHubCommand(String data) {
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

      // Remove quotes from key and value if present
      key.replace("\"", "");
      value.replace("\"", "");

      // Handle hub variables
      if (key == "data_interval") {
        dataInterval = value.toInt(); // Assuming dataInterval is unsigned long
        Serial.println("Hub dataInterval set to " + String(dataInterval));
      } else {
        Serial.println("Unknown hub variable: " + key);
      }

      start = commaIndex + 1;
    } else {
      Serial.println("Error: Command with invalid format");
      break;
    }
  }
}

void sendCommandToPeripheral(String command, uint8_t *peerAddress) {
  // Prepare the outgoing command structure
  memset(outgoingCommand.data, 0, sizeof(outgoingCommand.data)); // Clear previous data
  strncpy(outgoingCommand.data, command.c_str(), sizeof(outgoingCommand.data) - 1);

  // Send the command via ESP-NOW
  esp_err_t result = esp_now_send(peerAddress, (uint8_t *) &outgoingCommand, sizeof(outgoingCommand));

  if (result == ESP_OK) {
    Serial.println("Command sent successfully via ESP-NOW:");
    Serial.println(command);
  } else {
    Serial.println("Error sending command via ESP-NOW");
    // Optionally, re-enqueue the command for retry
    enqueueCommand(command);
  }
}

void loop() {
  // Read Bluetooth or USB Serial data
  readSerialData();

  // Check if there are any commands in the queue
  String commandToSend;
  if (dequeueCommand(commandToSend)) {
    // Send the command via ESP-NOW to the appropriate peripheral
    // For now, we only have flowmeter
    sendCommandToPeripheral(commandToSend, flowmeterAddress);
  }

  // Update the connection status of peripherals
  unsigned long currentTime = millis();

  // Check if we have not received data from flowmeter for a while
  if (flowmeterConnected && (currentTime - lastFlowmeterDataTime > deviceTimeout)) {
    flowmeterConnected = false;
  }

  // Prepare data to send via Serial and Bluetooth
  unsigned long elapsedTime = (currentTime - startTime) / 1000; // Time in seconds

  String outputMessage = "{\"time_s\":" + String(elapsedTime);

  outputMessage += ",\"flowmeter_on\":" + String(flowmeterConnected ? 1 : 0);

  if (flowmeterConnected) {
    outputMessage += ",\"read_flow_rate\":" + String(flowmeter_readFlowRate, 4) + ",\"read_flow_setpoint\":" + String(flowmeter_readFlowSetpoint, 4); 
  }

  outputMessage += "}";

  // Send data via Serial and Bluetooth
  Serial.println(outputMessage);
  SerialBT.println(outputMessage);

  // Optional: Add a small delay to prevent overwhelming the ESP32
  delay(dataInterval);
}

// Function to parse and store flowmeter data
void processFlowmeterData(String data) {
  data.trim();

  if (data[0] == '{' && data[data.length() - 1] == '}') {
    data = data.substring(1, data.length() - 1);
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

        // Remove quotes from key and value if present
        key.replace("\"", "");
        value.replace("\"", "");

        // Parse the data fields
        if (key == "flow_rate") {
          flowmeter_readFlowRate = value.toFloat();
        } else if (key == "flow_setpoint"){
          flowmeter_readFlowSetpoint = value.toFloat();
        }
        else{

        }

        start = commaIndex + 1;
      } else {
        Serial.println("Error: Received data with invalid format");
        break;
      }
    }
  } else {
    Serial.println("Error: Invalid data format in received data");
  }
}

//{"target:hub,"data_interval":500}
//{"target:flowmeter,"flow_setpoint":5}
