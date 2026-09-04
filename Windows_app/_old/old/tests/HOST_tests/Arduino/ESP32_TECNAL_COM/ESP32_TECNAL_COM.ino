#include <Arduino.h>
#include <Adafruit_TinyUSB.h>

Adafruit_USBH_CDC usbSerial;  // USB Host Serial Object

void setup() {
    Serial.begin(115200);
    Serial.println("ESP32-S3 USB Host - Serial Communication");

    // Enable VBUS Power if required (Check your board specs)
    pinMode(12, OUTPUT);  
    digitalWrite(12, HIGH);  // Turn on USB VBUS if needed

    // Initialize TinyUSB in Host Mode
    TinyUSBHost.begin();

    Serial.println("Waiting for USB Serial Device...");
}

void loop() {
    // Check if USB Serial Device is connected
    if (usbSerial && usbSerial.connected()) {
        Serial.println("USB Serial Device Connected!");

        // Read incoming data from USB Serial device
        if (usbSerial.available()) {
            char receivedData = usbSerial.read();
            Serial.print("Received from USB device: ");
            Serial.println(receivedData);
        }

        // Send data to the USB Serial device
        String message = "Hello from ESP32-S3 USB Host!\n";
        usbSerial.write(message.c_str(), message.length());

        delay(1000); // Wait before next message
    }
}
