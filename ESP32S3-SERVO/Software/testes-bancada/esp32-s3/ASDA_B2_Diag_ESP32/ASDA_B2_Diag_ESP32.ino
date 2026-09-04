#include <Arduino.h>

/*
 * Diagnostico de sinal -- ESP32-S3 / HW-097, travado em 9600 8N2.
 *
 * Nao e varredura. Serve para separar DUAS causas do "lixo":
 *
 *   A) linha RS-485 sem polarizacao (bias): fica flutuando em repouso e
 *      o receptor inventa bytes mesmo sem ninguem transmitir. O dongle
 *      nao sofria disso porque traz bias interno; o HW-097 pelado, nao.
 *
 *   B) nosso TX / conversor de nivel: a linha e quieta em repouso, mas o
 *      quadro que recebemos depois de transmitir vem corrompido.
 *
 * O teste PASSIVO (nao transmite, so escuta 2 s) decide:
 *   muitos bytes com o barramento parado  -> causa A (falta bias)
 *   zero bytes parado, lixo so apos TX     -> causa B (TX/conversor)
 *
 * Somente leitura. So 03H. Nunca escreve nem habilita nada.
 */

constexpr int RS485_TX = 17;
constexpr int RS485_RX = 18;
constexpr int DE_PIN   = 16;

constexpr uint32_t BAUD = 9600;
constexpr uint32_t CFG  = SERIAL_8N2;

uint8_t lastReq[8]; uint8_t lastReqLen = 0;

void hx(uint8_t v){ if (v < 0x10) Serial.print('0'); Serial.print(v, HEX); Serial.print(' '); }

uint16_t crc16(const uint8_t *d, uint8_t n){
  uint16_t c = 0xFFFF;
  while (n-- > 0){ c ^= *d++; for (uint8_t b=0;b<8;++b) c=(c&1)?(uint16_t)((c>>1)^0xA001):(uint16_t)(c>>1); }
  return c;
}

// Serial1 e iniciado UMA vez no setup(); nao re-inicia por transacao,
// para nao glitchar os primeiros bytes recebidos.
void drainBus(){ while (Serial1.available()) (void)Serial1.read(); }

// escuta pura: NAO transmite, DE fica em LOW (recepcao)
uint16_t passiveListen(uint16_t ms, uint8_t *buf, uint16_t bufSize){
  drainBus();
  digitalWrite(DE_PIN, LOW);
  uint16_t n = 0; const uint32_t t0 = millis();
  while ((millis() - t0) < ms)
    while (Serial1.available() && n < bufSize) buf[n++] = (uint8_t)Serial1.read();
  return n;
}

uint16_t probe(uint8_t slave, uint16_t addr, uint8_t *buf, uint16_t bufSize, uint16_t ms){
  uint8_t req[8] = { slave, 0x03, (uint8_t)(addr>>8), (uint8_t)(addr&0xFF), 0x00, 0x01, 0, 0 };
  const uint16_t c = crc16(req, 6); req[6]=(uint8_t)(c&0xFF); req[7]=(uint8_t)(c>>8);
  memcpy(lastReq, req, 8); lastReqLen = 8;

  drainBus();
  delay(20);
  digitalWrite(DE_PIN, HIGH);
  Serial1.write(req, 8);
  Serial1.flush();
  delayMicroseconds(50);
  digitalWrite(DE_PIN, LOW);

  uint16_t n = 0; const uint32_t t0 = millis();
  while ((millis() - t0) < ms)
    while (Serial1.available() && n < bufSize) buf[n++] = (uint8_t)Serial1.read();
  return n;
}

void setup(){
  pinMode(DE_PIN, OUTPUT); digitalWrite(DE_PIN, LOW);
  Serial.begin(115200); delay(400);
  Serial1.begin(BAUD, CFG, RS485_RX, RS485_TX);   // uma unica vez
  delay(10); drainBus();
  Serial.println();
  Serial.println("=== DIAG 9600 8N2 - ESP32/HW-097 (driver LIGADO) ===");
  uint8_t req[8] = { 0xFF, 0x03, 0x03, 0x00, 0x00, 0x01, 0, 0 };
  const uint16_t c = crc16(req, 6); req[6]=(uint8_t)(c&0xFF); req[7]=(uint8_t)(c>>8);
  Serial.print("Nosso pedido (curinga): "); for (uint8_t i=0;i<8;++i) hx(req[i]); Serial.println();
}

void loop(){
  uint8_t buf[64];

  // 1) ESCUTA PASSIVA -- nao transmite nada
  uint16_t n = passiveListen(2000, buf, sizeof(buf));
  Serial.println();
  Serial.print("[PASSIVO 2s, sem TX] bytes = "); Serial.println(n);
  if (n){ Serial.print("   "); for (uint16_t i=0;i<n && i<48;++i) hx(buf[i]); Serial.println(); }
  Serial.println(n ? "   -> linha RUIDOSA em repouso: falta BIAS (causa A)"
                   : "   -> linha quieta em repouso: bias ok (olhe o TX abaixo)");

  // 2) PROBES com TX, curinga e 0x01
  for (uint8_t k = 0; k < 2; ++k){
    const uint8_t slave = k ? 0x01 : 0xFF;
    for (uint8_t r = 0; r < 3; ++r){
      n = probe(slave, 0x0300, buf, sizeof(buf), 500);
      Serial.print("[TX 0x"); hx(slave); Serial.print("-> ] n="); Serial.print(n); Serial.print("  ");
      for (uint16_t i=0;i<n && i<32;++i) hx(buf[i]);
      Serial.println();
    }
  }

  Serial.println("--- repete em 4 s ---");
  delay(4000);
}
