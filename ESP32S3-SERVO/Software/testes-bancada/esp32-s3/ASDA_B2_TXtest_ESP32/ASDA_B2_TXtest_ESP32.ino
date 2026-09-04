#include <Arduino.h>

/*
 * Teste de TRANSMISSAO com multimetro -- ESP32-S3 / HW-097.
 *
 * O loopback deu n=0: nosso transmissor nao poe o quadro no barramento.
 * Este sketch forca o driver PERMANENTEMENTE ligado (DE=HIGH) e deixa a
 * linha em repouso (mark). Assim da para medir tensoes DC estaveis e
 * achar onde a cadeia de TX quebra.
 *
 * MEDIDAS (multimetro em DC):
 *
 *   1. GPIO16 -> pino DE do modulo ...... deve dar ~3,3 V
 *      (se 0 V: fio GPIO16->DE frio, ou GPIO errado)
 *
 *   2. GPIO17 -> pino DI do modulo ...... deve dar ~3,3 V (idle = mark)
 *      (se 0 V ou solto: fio GPIO17->DI frio)
 *
 *   3. A (D+) menos B (D-) no modulo .... deve dar ~+2 a +5 V
 *      ponta VERMELHA em A, PRETA em B.
 *      (com DE e DI altos, o driver poe A alto e B baixo)
 *      - deu ~0 V com 1 e 2 ok?  -> modulo/driver morto, ou A/B soltos
 *      - deu tensao?             -> o TX FUNCIONA; o problema era so a
 *                                   temporizacao do DE no envio real
 *
 * Nada e transmitido para o drive aqui (linha so em repouso). Depois
 * deste teste, volte ao sketch de varredura.
 */

constexpr int RS485_TX = 17;   // GPIO17 -> DI
constexpr int RS485_RX = 18;   // GPIO18 <- RO (via conversor)
constexpr int DE_PIN   = 16;   // GPIO16 -> DE

void setup(){
  Serial.begin(115200);
  delay(400);

  pinMode(DE_PIN, OUTPUT);
  digitalWrite(DE_PIN, HIGH);            // driver LIGADO permanentemente

  // UART1 em repouso: a linha TX (DI) fica em mark = nivel alto
  Serial1.begin(9600, SERIAL_8N2, RS485_RX, RS485_TX);

  Serial.println();
  Serial.println("=== TESTE DE TX (driver forcado LIGADO) ===");
  Serial.println("Meca com o multimetro em DC:");
  Serial.println("  1. GPIO16 -> DE do modulo : espera ~3,3 V");
  Serial.println("  2. GPIO17 -> DI do modulo : espera ~3,3 V (mark)");
  Serial.println("  3. A(+) menos B(-)        : espera ~+2 a +5 V");
  Serial.println("     (vermelha em A, preta em B)");
  Serial.println();
  Serial.println("Linha segurada em repouso. Nada e enviado ao drive.");
}

void loop(){
  // reafirma o estado a cada segundo e pisca um heartbeat no console
  digitalWrite(DE_PIN, HIGH);
  Serial.println("driver LIGADO, linha em mark -- pode medir");
  delay(1000);
}
