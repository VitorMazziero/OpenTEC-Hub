// Define hardware serial pins for RX and TX
#define RX_PIN 16
#define TX_PIN 17

// Create a hardware serial instance for communication with Totalizer-IO
HardwareSerial totalizerSerial(1); // Using UART1 for communication

void setup() {
  // Start hardware serial for debugging (USB Serial)
  Serial.begin(9600);

  // Start hardware serial (UART1) for Totalizer-IO communication
  totalizerSerial.begin(115200, SERIAL_8N1, RX_PIN, TX_PIN);

  delay(1000); // Allow time for initialization

  Serial.println("Setup complete. Enter a command to send:");
}

void loop() {
  // Check if the user has entered a command via Serial Monitor
  if (Serial.available()) {
    String command = Serial.readStringUntil('\n'); // Read user input
    command.trim(); // Remove any leading/trailing whitespace or newlines

    if (command.length() > 0) {
      sendCommand(command); // Send the command to Totalizer-IO
    }
  }

  // Continuously check for incoming data from the Totalizer-IO
  String response = readResponse();
  if (response.length() > 0) {
    Serial.print("Received response: ");
    Serial.println(response);
  }

  delay(100); // Adjust delay as needed
}

void sendCommand(String cmd) {
  totalizerSerial.print(cmd + "\r\n"); // Send the command with CR+LF
  Serial.println("Command Sent: " + cmd);
}

String readResponse() {
  String response = "";
  while (totalizerSerial.available()) {
    char c = totalizerSerial.read();
    if (c == '\r') { // Check for carriage return which indicates the end of the response
      break;
    }
    response += c;
  }
  return response;
}
