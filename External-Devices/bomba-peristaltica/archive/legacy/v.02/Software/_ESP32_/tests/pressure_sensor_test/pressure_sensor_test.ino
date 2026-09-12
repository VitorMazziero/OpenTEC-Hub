const int FSR_Pin = 35; // GPIO34 (ADC1_CH6)
const float Vcc = 5;  // Supply voltage
const float R_fixed = 10000.0; // Fixed resistor value in ohms (10 kΩ)

void setup() {
  Serial.begin(115200); // Initialize serial communication at 115200 baud rate
}

void loop() {
  // Read the analog value (0 - 4095)
  int sensorValue = analogRead(FSR_Pin);
  
  // Convert the analog reading to voltage
  float V_out = sensorValue * Vcc / 4095.0;
  
  // Calculate the resistance of the FSR
  float R_FSR;
  if (V_out != 0) {
    R_FSR = R_fixed * (Vcc - V_out) / V_out;
  } else {
    R_FSR = 0; // Avoid division by zero
  }

  // Estimate the force (in Newtons) using an approximate formula
  float force;
  if (R_FSR != 0) {
    // Scaling factor determined experimentally (placeholder value used here)
    float scalingFactor = 1e6;
    force = (1.0 / R_FSR) * scalingFactor;
  } else {
    force = 0;
  }

  // Print the results
  Serial.print("Analog Reading: ");
  Serial.print(sensorValue);
  Serial.print("  Voltage: ");
  Serial.print(V_out, 3);
  Serial.print(" V  FSR Resistance: ");
  Serial.print(R_FSR);
  Serial.print(" ohms  Estimated Force: ");
  Serial.print(force);
  Serial.println(" N");

  delay(500); // Wait for 500 milliseconds before the next reading
}
