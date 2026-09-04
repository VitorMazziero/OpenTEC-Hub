#include <Arduino.h>

/*
 * Loopback de PINO -- ESP32-S3, GPIO17 -> GPIO18 por JUMPER direto.
 *
 * O loopback interno provou que o periferico UART1 recebe. Este prova o
 * PINO fisico GPIO18: alimenta o RX diretamente pelo TX (GPIO17), sem
 * modulo e sem divisor.
 *
 *   LIGACAO: um jumper de GPIO17 para GPIO18. Divisor solto do GPIO18.
 *
 *   bytes/s > 0 e 0x55 -> GPIO18 recebe sinal externo: o pino esta OK.
 *                         O defeito e o caminho RO/divisor do modulo.
 *   bytes/s = 0         -> GPIO18 nao recebe: pino ocupado/danificado.
 */

constexpr int RS485_TX = 17;
constexpr int RS485_RX = 18;

uint8_t pattern[8];
uint32_t rxCount = 0, lastReport = 0;
uint8_t lastByte = 0;

void setup(){
  Serial.begin(115200);
  delay(400);
  Serial1.begin(9600, SERIAL_8N2, RS485_RX, RS485_TX);   // sem loopback interno
  for (uint8_t i = 0; i < sizeof(pattern); ++i) pattern[i] = 0x55;

  Serial.println();
  Serial.println("=== LOOPBACK DE PINO: jumper GPIO17->GPIO18 ===");
  Serial.println("bytes/s > 0 e 0x55 -> GPIO18 OK; culpa e do RO/divisor");
  Serial.println("bytes/s = 0         -> GPIO18 nao recebe sinal externo");
  Serial.println();
}

void loop(){
  Serial1.write(pattern, sizeof(pattern));
  Serial1.flush();
  while (Serial1.available() > 0){ lastByte = (uint8_t)Serial1.read(); ++rxCount; }

  if (millis() - lastReport >= 1000){
    lastReport = millis();
    Serial.print("pino: "); Serial.print(rxCount);
    Serial.print(" bytes/s   ultimo = 0x");
    if (lastByte < 0x10) Serial.print('0');
    Serial.println(lastByte, HEX);
    rxCount = 0;
  }
}
