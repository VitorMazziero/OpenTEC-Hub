#include <Arduino.h>

/*
 * O D10 consegue mesmo puxar o no para baixo?
 *
 * No AVR, digitalRead() le o registrador PINx, que reflete o nivel
 * FISICO do pino -- inclusive quando o pino esta configurado como
 * saida. Entao da para acionar o pino e conferir se o mundo obedeceu.
 *
 *   manda BAIXO, le BAIXO  -> o pino esta puxando. Driver do AVR bom,
 *                             e nada externo o segura.
 *   manda BAIXO, le ALTO   -> o pino NAO consegue puxar. Ou a saida do
 *                             AVR morreu, ou algo externo de baixa
 *                             impedancia segura o no em alto.
 *
 * O D7, que nao esta ligado a nada, serve de controle: ele valida a
 * propria tecnica de leitura. Se o D7 obedecer e o D10 nao, a
 * diferenca esta no D10 ou no que esta pendurado nele.
 *
 * O D11 NAO e acionado como saida em momento algum: ele esta ligado ao
 * RO do transceptor, que e uma saida. Aciona-lo criaria disputa.
 */

constexpr uint8_t PIN_TX = 10;       // vai ao RXD/DI do HW-519
constexpr uint8_t PIN_RX = 11;       // vem do TXD/RO do HW-519
constexpr uint8_t PIN_CONTROL = 7;   // solto, so para validar o metodo

uint16_t driveAndRead(uint8_t pin, uint8_t level) {
  pinMode(pin, OUTPUT);
  digitalWrite(pin, level);
  delay(20);

  uint16_t high = 0;
  for (uint16_t index = 0; index < 300; ++index) {
    if (digitalRead(pin) == HIGH) {
      ++high;
    }
    delayMicroseconds(70);
  }
  return high;
}

void testPin(const __FlashStringHelper *label, uint8_t pin) {
  const uint16_t whenHigh = driveAndRead(pin, HIGH);
  const uint16_t whenLow = driveAndRead(pin, LOW);

  digitalWrite(pin, HIGH);
  delay(5);
  pinMode(pin, INPUT);

  Serial.print(label);
  Serial.print(F("  mandando ALTO leu alto "));
  Serial.print(whenHigh);
  Serial.print(F("/300;  mandando BAIXO leu alto "));
  Serial.print(whenLow);
  Serial.println(F("/300"));

  Serial.print(F("    -> "));
  if (whenHigh >= 290 && whenLow <= 10) {
    Serial.println(F("OBEDECE nos dois sentidos. Pino saudavel."));
  } else if (whenLow >= 290) {
    Serial.println(F("NAO CONSEGUE PUXAR PARA BAIXO."));
    Serial.println(F("       Saida do AVR danificada, ou algo externo de"));
    Serial.println(F("       baixa impedancia segurando o no em alto."));
  } else if (whenHigh <= 10) {
    Serial.println(F("NAO CONSEGUE SUBIR. No presa em baixo."));
  } else {
    Serial.println(F("INSTAVEL. Contato intermitente ou disputa."));
  }
  Serial.println();
}

void setup() {
  Serial.begin(115200);
  delay(400);

  Serial.println();
  Serial.println(F("=== O D10 consegue acionar o no? ==="));
  Serial.println();

  testPin(F("D7  (solto, controle do metodo):"), PIN_CONTROL);
  testPin(F("D10 (ligado ao RXD/DI):         "), PIN_TX);

  // O D11 so e lido, nunca acionado.
  pinMode(PIN_RX, INPUT);
  delay(20);
  uint16_t rxHigh = 0;
  for (uint16_t index = 0; index < 300; ++index) {
    if (digitalRead(PIN_RX) == HIGH) {
      ++rxHigh;
    }
    delayMicroseconds(70);
  }
  Serial.print(F("D11 (so leitura, em repouso):    alto em "));
  Serial.print(rxHigh);
  Serial.println(F("/300"));

  Serial.println();
  Serial.println(F("------------------------------------------------------------"));
  Serial.println(F("Se o D7 obedecer e o D10 nao, o problema esta no D10 ou"));
  Serial.println(F("no que esta pendurado nele -- e a saida basta mudar de"));
  Serial.println(F("pino. Se os dois obedecerem, o D10 esta bom e o driver"));
  Serial.println(F("do modulo e que nao responde ao DI."));
  Serial.println(F("------------------------------------------------------------"));
}

void loop() {
  delay(1000);
}
