#include "DistanceSensor.h"

#include <Arduino.h>
#include <Wire.h>

#include "../config/BoardConfig.h"
#include "../core/AppContext.h"

void i2cInit() {
  if (BoardConfig::SensorXshutPin >= 0) {
    pinMode(BoardConfig::SensorXshutPin, OUTPUT);
    digitalWrite(BoardConfig::SensorXshutPin, HIGH);
    delay(5);
  }

  pinMode(BoardConfig::I2cSda, INPUT_PULLUP);
  pinMode(BoardConfig::I2cScl, INPUT_PULLUP);
  delay(2);
  Wire.end();
  delay(2);
  Wire.begin(BoardConfig::I2cSda, BoardConfig::I2cScl, BoardConfig::I2cFrequencyHz);
  Wire.setTimeOut(BoardConfig::WireTimeoutMs);

  Serial.printf("[I2C] SDA=%d SCL=%d @ %lu Hz, tmo=%ums\n",
                BoardConfig::I2cSda,
                BoardConfig::I2cScl,
                static_cast<unsigned long>(BoardConfig::I2cFrequencyHz),
                static_cast<unsigned>(BoardConfig::WireTimeoutMs));
}

bool sensorInit() {
  for (int attempt = 0; attempt < 3; ++attempt) {
    Serial.printf("[VL53] init attempt %d\n", attempt + 1);
    if (sensor.init()) {
      Serial.println("[VL53] init OK");
      sensor.setTimeout(BoardConfig::WireTimeoutMs);
      sensor.setMeasurementTimingBudget(200000);
      return true;
    }
    delay(20);
  }

  Serial.println("[VL53] init FAILED");
  return false;
}

bool readSingleShot(int& mm) {
  const uint16_t reading = sensor.readRangeSingleMillimeters();
  const bool ok = !sensor.timeoutOccurred() && reading > 0 && reading < 4000 && reading < 8190;
  mm = ok ? static_cast<int>(reading) : -1;
  return ok;
}

void maybeRecover() {
  const unsigned long now = millis();

  // Escada de recuperacao (D09). O degrau e escolhido pelo tamanho do streak, do mais
  // drastico para o mais suave, e o cooldown aplicado e o DESSE degrau. Avaliar L1
  // primeiro (r10) nunca deixava L2/L3 correr; so inverter a ordem tambem nao bastava:
  // com um unico lastRecovery, L2 (15 s) recarregava o relogio antes de L3 (30 s)
  // vencer. Uma vez escalado, o no nao volta a um degrau mais suave ate o streak zerar.
  int level = 0;
  unsigned long cooldown = 0;
  if (failStreak >= L3_XSHUT) {
    level = 3;
    cooldown = COOLDOWN_XSHUT_MS;
  } else if (failStreak >= L2_BUS_CLEAR) {
    level = 2;
    cooldown = COOLDOWN_BUS_MS;
  } else if (failStreak >= L1_SOFT_REINIT) {
    level = 1;
    cooldown = COOLDOWN_SOFT_MS;
  }
  if (level == 0 || now - lastRecovery < cooldown) return;

  if (level == 3) {
    // Nivel 3: power-cycle pelo XSHUT (ou bus clear onde nao ha XSHUT) + re-init
    Serial.printf("[RECOVER] L3 XSHUT power-cycle + re-init (streak=%d)\n", failStreak);
    if (BoardConfig::SensorXshutPin >= 0) {
      digitalWrite(BoardConfig::SensorXshutPin, LOW);
      delay(10);
      digitalWrite(BoardConfig::SensorXshutPin, HIGH);
      delay(10);
    } else {
      i2cBusClear();
    }
    Wire.end();
    delay(2);
    Wire.begin(BoardConfig::I2cSda, BoardConfig::I2cScl, BoardConfig::I2cFrequencyHz);
    Wire.setTimeOut(BoardConfig::WireTimeoutMs);
    sensorInit();
  } else if (level == 2) {
    // Nivel 2: libera o barramento I2C (9 clocks) + re-init
    Serial.printf("[RECOVER] L2 bus clear + re-init (streak=%d)\n", failStreak);
    i2cBusClear();
    Wire.end();
    delay(2);
    Wire.begin(BoardConfig::I2cSda, BoardConfig::I2cScl, BoardConfig::I2cFrequencyHz);
    Wire.setTimeOut(BoardConfig::WireTimeoutMs);
    sensorInit();
  } else {
    // Nivel 1: so re-init do sensor
    Serial.printf("[RECOVER] L1 soft re-init (streak=%d)\n", failStreak);
    sensorInit();
  }
  lastRecovery = now;
}

bool i2cBusClear() {
  Serial.println("[I2C] bus clear...");
  pinMode(BoardConfig::I2cSda, INPUT_PULLUP);
  pinMode(BoardConfig::I2cScl, INPUT_PULLUP);
  delay(2);
  if (digitalRead(BoardConfig::I2cSda) == HIGH) {
    Serial.println("[I2C] SDA already high");
    return true;
  }

  pinMode(BoardConfig::I2cScl, OUTPUT);
  for (int i = 0; i < 16; ++i) {
    digitalWrite(BoardConfig::I2cScl, LOW);
    delayMicroseconds(5);
    digitalWrite(BoardConfig::I2cScl, HIGH);
    delayMicroseconds(5);
    if (digitalRead(BoardConfig::I2cSda) == HIGH) {
      Serial.printf("[I2C] SDA released after %d pulses\n", i + 1);
      break;
    }
  }

  pinMode(BoardConfig::I2cSda, OUTPUT);
  digitalWrite(BoardConfig::I2cSda, LOW);
  delayMicroseconds(5);
  digitalWrite(BoardConfig::I2cScl, HIGH);
  delayMicroseconds(5);
  digitalWrite(BoardConfig::I2cSda, HIGH);
  delayMicroseconds(5);
  const bool ok = digitalRead(BoardConfig::I2cSda) == HIGH;
  Serial.printf("[I2C] clear %s\n", ok ? "OK" : "FAILED");
  return ok;
}

void i2cScanOnce(const char* tag) {
  Serial.printf("%s I2C scan...\n", tag);
  uint8_t count = 0;
  for (uint8_t address = 1; address < 127; ++address) {
    Wire.beginTransmission(address);
    if (Wire.endTransmission() == 0) {
      Serial.printf(" - 0x%02X\n", address);
      ++count;
    }
    delay(2);
  }
  if (count == 0) {
    Serial.println(" - no devices");
  }
}
