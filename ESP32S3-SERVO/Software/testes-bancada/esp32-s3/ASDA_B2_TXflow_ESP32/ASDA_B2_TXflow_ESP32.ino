#include <Arduino.h>

/*
 * Teste de FLUXO de TX -- ESP32-S3 / HW-097.
 *
 * O loopback nao ecoou. O teste estatico so provou que DI fica no nivel
 * de repouso (mark) -- nunca provou que o GPIO17 realmente TRANSMITE os
 * dados. Aqui o UART1 transmite SEM PARAR o padrao 0x55 (01010101), com
 * o driver ligado (DE=HIGH fixo). Assim da para ver no multimetro se o
 * dado esta saindo:
 *
 *   - DI (GPIO17 -> pino DI):  parado = 3,3 V.  Transmitindo 0x55 =
 *     alterna rapido -> a media cai para ~1,5 a 1,8 V.
 *       DI continua 3,3 V  -> o GPIO17 NAO esta transmitindo (o problema)
 *       DI caiu p/ ~1,6 V   -> o dado ESTA saindo do GPIO17
 *
 *   - A menos B: parado = ~+2,9 V (mark). Transmitindo alterna ->
 *     a media cai bastante (perto de 0, oscilando).
 *       A-B continua +2,9 V -> o driver nao recebe dado do DI
 *       A-B caiu/oscila      -> o driver esta pondo dado no barramento
 *
 * Somente escreve 0x55 no barramento (nao e Modbus valido; o drive
 * ignora por CRC). Nao habilita servo, nao comanda motor.
 */

constexpr int RS485_TX = 17;
constexpr int RS485_RX = 18;
constexpr int DE_PIN   = 16;

uint8_t pattern[32];

void setup(){
  Serial.begin(115200);
  delay(400);

  pinMode(DE_PIN, OUTPUT);
  digitalWrite(DE_PIN, HIGH);              // driver LIGADO o tempo todo
  Serial1.begin(9600, SERIAL_8N2, RS485_RX, RS485_TX);

  for (uint8_t i = 0; i < sizeof(pattern); ++i) pattern[i] = 0x55;

  Serial.println();
  Serial.println("=== LOOPBACK dinamico (RE no GND): TX 0x55 + le o eco ===");
  Serial.println("Com DE=alto e RE no GND, o modulo ecoa a propria transmissao.");
  Serial.println("Conta quantos bytes voltam pelo RO->divisor->GPIO18.");
  Serial.println("  bytes/s > 0 e 0x55  -> a RECEPCAO dinamica FUNCIONA");
  Serial.println("  bytes/s = 0          -> RX nao enquadra dado (divisor/GPIO18)");
  Serial.println();
}

uint32_t rxCount = 0;
uint8_t  lastByte = 0;
uint32_t lastReport = 0;

void loop(){
  // transmite um bloco e, em seguida, le o que voltou (eco do loopback)
  Serial1.write(pattern, 8);
  Serial1.flush();
  while (Serial1.available() > 0){
    lastByte = (uint8_t)Serial1.read();
    ++rxCount;
  }

  if (millis() - lastReport >= 1000){
    lastReport = millis();
    Serial.print("eco: ");
    Serial.print(rxCount);
    Serial.print(" bytes/s   ultimo = 0x");
    if (lastByte < 0x10) Serial.print('0');
    Serial.println(lastByte, HEX);
    rxCount = 0;
  }
}
