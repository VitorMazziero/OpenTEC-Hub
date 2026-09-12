/*************************************************************
 * Cliente do Sensor de Distância – Medição a cada 200ms
 *
 * - Usa um sensor VL53L0X (Adafruit) para medição de distância de alta precisão.
 * - Opera em modo contínuo com tempo de medição de 200ms (200.000µs).
 * - A cada 200ms, obtém a medição atual e imprime "tempo,distância"
 *   (tempo em segundos e distância em mm) via Serial.
 *************************************************************/
#include <Arduino.h>
#include <Wire.h>
#include <Adafruit_VL53L0X.h>

Adafruit_VL53L0X lox = Adafruit_VL53L0X();

unsigned long lastMeasurementMillis = 0;
const unsigned long measurementInterval = 200;  // Medição a cada 200ms

// Função para inicializar o sensor
void ensureSensorInitialized() {
  Serial.println("Inicializando sensor VL53L0X...");
  while (!lox.begin()) {
    Serial.println("Falha ao inicializar sensor. Tentando novamente em 1 segundo...");
    delay(1000);
  }
  Serial.println("Sensor inicializado com sucesso.");
  
  // Configura o tempo de medição para 200ms (200.000µs)
  lox.setMeasurementTimingBudgetMicroSeconds(200000);
  
  // Inicia o modo contínuo
  lox.startRangeContinuous();
}

void setup() {
  Serial.begin(115200);
  delay(1000);
  ensureSensorInitialized();
}

void loop() {
  unsigned long now = millis();

  if (now - lastMeasurementMillis >= measurementInterval) {
    lastMeasurementMillis = now;

    VL53L0X_RangingMeasurementData_t measure;
    
    lox.rangingTest(&measure, true);  // TRUE forces single-shot
    
    int distance = measure.RangeMilliMeter - 16.5;

    float timeSec = now / 1000.0;
    Serial.print(timeSec, 3);
    Serial.print(",");
    Serial.println(distance);
  }
}

