#include <Arduino.h>
#include "driver/uart.h"

/*
 * Loopback INTERNO do UART1 -- ESP32-S3.
 *
 * Liga TX->RX DENTRO do chip (uart_set_loop_back), sem passar por pino,
 * fio, divisor ou modulo. Isola a pergunta:
 *
 *   recebe no interno  -> o periferico UART1 (TX+RX) esta OK; o problema
 *                         esta FORA: GPIO18, divisor ou RO do modulo.
 *   nao recebe          -> a config do Serial1 esta errada (pino/param).
 *
 * Transmite 0x55 e conta o que volta pelo caminho interno.
 */

constexpr int RS485_TX = 17;
constexpr int RS485_RX = 18;

uint8_t pattern[8];
uint32_t rxCount = 0, lastReport = 0;
uint8_t lastByte = 0;

void setup(){
  Serial.begin(115200);
  delay(400);

  Serial1.begin(9600, SERIAL_8N2, RS485_RX, RS485_TX);
  uart_set_loop_back(UART_NUM_1, true);      // TX->RX interno

  for (uint8_t i = 0; i < sizeof(pattern); ++i) pattern[i] = 0x55;

  Serial.println();
  Serial.println("=== LOOPBACK INTERNO do UART1 (sem pinos) ===");
  Serial.println("bytes/s > 0 e 0x55 -> periferico OK, problema e externo");
  Serial.println("bytes/s = 0         -> config do Serial1 errada");
  Serial.println();
}

void loop(){
  Serial1.write(pattern, sizeof(pattern));
  Serial1.flush();
  while (Serial1.available() > 0){ lastByte = (uint8_t)Serial1.read(); ++rxCount; }

  if (millis() - lastReport >= 1000){
    lastReport = millis();
    Serial.print("interno: "); Serial.print(rxCount);
    Serial.print(" bytes/s   ultimo = 0x");
    if (lastByte < 0x10) Serial.print('0');
    Serial.println(lastByte, HEX);
    rxCount = 0;
  }
}
