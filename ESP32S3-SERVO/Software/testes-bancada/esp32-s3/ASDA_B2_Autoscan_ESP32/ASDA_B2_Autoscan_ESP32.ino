#include <Arduino.h>

/*
 * Varredura Modbus RTU do ASDA-B2 -- ESP32-S3, AUTO-RUN, sem menu.
 *
 * Por que ESP32 e nao UNO: no UNO o barramento tem de dividir os pinos
 * D0/D1 com o chip USB. Num clone CH340 essa disputa impede a recepcao
 * -- o Arduino nunca ouve a resposta do drive, apesar de tudo ligado.
 * O ESP32-S3 tem UART1 (GPIO17/18) INDEPENDENTE do console USB: sem
 * disputa, sem troca de modo, sem glitch.
 *
 * Somente leitura: transmite apenas 03H. Nunca escreve parametro,
 * nunca habilita o servo, nunca comanda o motor.
 *
 * Faz, ao ligar (e a cada 5 s):
 *   1. Varre baud x formato em 0xFF (curinga) ate achar resposta.
 *   2. Na combinacao achada, le e imprime P3-xx / P1-01 / P0-00.
 *
 * Alvo: reproduzir o que o dongle USB-RS485 ja fez -- 9600 8N2,
 * endereco 1, P3-00 = 0x0001.
 *
 * ------------------------------------------------------------------
 * LIGACOES (HW-097 MAX485, 5 V)
 * ------------------------------------------------------------------
 *   ESP32 GPIO17 (TX1) ---> DI              (3,3 V aciona o DI: ok)
 *   ESP32 GPIO16       ---> DE e RE em curto (repouso LOW = recepcao)
 *   ESP32 GPIO18 (RX1) <--- RO POR DIVISOR   (RO e 5 V! ver abaixo)
 *   ESP32 GND          ---  terra comum
 *
 *   RO --[1k]--+--> GPIO18       divisor 5 V -> 3,3 V, OBRIGATORIO:
 *              |                 o GPIO do ESP32 NAO tolera 5 V.
 *            [2k]
 *              |
 *             GND
 *
 *   HW-097 VCC ---> 5 V da placa controladora
 *   HW-097 GND ---> terra comum
 *   HW-097 A   ---> CN3-5        (polaridade provada pelo dongle)
 *   HW-097 B   ---> CN3-6
 *
 *   Terra comum: ESP32 GND + HW-097 GND + GND do 5 V da placa +
 *   perna do 2k + CN1 GND do drive -- tudo no mesmo no.
 */

// -------------------- Configuracao --------------------

constexpr int RS485_TX = 17;   // GPIO17 -> DI
constexpr int RS485_RX = 18;   // GPIO18 <- RO (via divisor)
constexpr int DE_PIN   = 16;   // GPIO16 -> DE e RE em curto

struct SerialFormat { const char *name; uint32_t config; };
const SerialFormat FORMATS[] = {
  {"8N2", SERIAL_8N2},         // P3-02 = 6 -- o do drive
  {"8E1", SERIAL_8E1},         // P3-02 = 7
  {"8O1", SERIAL_8O1}          // P3-02 = 8
};
const uint32_t BAUDS[] = {9600, 38400, 19200, 115200, 57600, 4800};

constexpr uint8_t FMT_N  = sizeof(FORMATS) / sizeof(FORMATS[0]);
constexpr uint8_t BAUD_N = sizeof(BAUDS) / sizeof(BAUDS[0]);

constexpr uint8_t  WILDCARD  = 0xFF;
constexpr uint16_t WINDOW_MS = 500;   // folgada: P3-07 do drive esta em 100

uint32_t g_baud = 9600;               // baud corrente, p/ calcular o hold do DE

struct Param { uint16_t addr; const char *name; const char *note; };
const Param PARAMS[] = {
  {0x0300, "P3-00", "endereco (painel: 0001)"},
  {0x0302, "P3-01", "baud (painel: 0011 = 9600)"},
  {0x0304, "P3-02", "protocolo (painel: 0066 = 8N2 RTU)"},
  {0x030A, "P3-05", "mecanismo (painel: 0000)"},
  {0x030E, "P3-07", "atraso de resposta (painel: 100)"},
  {0x0102, "P1-01", "modo de controle - NAO ALTERAR"},
  {0x0000, "P0-00", "versao de firmware"},
};
constexpr uint8_t PARAM_N = sizeof(PARAMS) / sizeof(PARAMS[0]);

uint8_t lastReq[8];
uint8_t lastReqLen = 0;

// -------------------- Impressao (no console USB) --------------------

void printHex8(uint8_t v)  { if (v < 0x10) Serial.print('0'); Serial.print(v, HEX); }
void printHex16(uint16_t v){ for (int8_t s = 12; s >= 0; s -= 4) Serial.print((uint8_t)((v >> s) & 0xF), HEX); }
void pad(const char *t, uint8_t w){ Serial.print(t); for (uint8_t i = strlen(t); i < w; ++i) Serial.print(' '); }
void padBaud(uint32_t b){ Serial.print(b); uint8_t d = (b >= 100000UL) ? 6 : (b >= 10000UL) ? 5 : 4; for (uint8_t i = d; i < 7; ++i) Serial.print(' '); }
void dump(const uint8_t *d, uint8_t n){ for (uint8_t i = 0; i < n; ++i){ printHex8(d[i]); Serial.print(' '); } }

// -------------------- CRC --------------------

uint16_t crc16(const uint8_t *d, uint8_t n){
  uint16_t crc = 0xFFFF;
  while (n-- > 0){
    crc ^= *d++;
    for (uint8_t b = 0; b < 8; ++b)
      crc = (crc & 1) ? (uint16_t)((crc >> 1) ^ 0xA001) : (uint16_t)(crc >> 1);
  }
  return crc;
}

// -------------------- Barramento --------------------

void setBus(uint8_t baudIdx, uint8_t fmtIdx){
  g_baud = BAUDS[baudIdx];
  Serial1.end();
  Serial1.begin(g_baud, FORMATS[fmtIdx].config, RS485_RX, RS485_TX);
  delay(5);
  while (Serial1.available() > 0) (void)Serial1.read();
}

uint8_t rawRead(uint8_t slave, uint16_t addr, uint16_t qty,
                uint8_t *buf, uint8_t bufSize, uint16_t windowMs){
  uint8_t req[8] = { slave, 0x03,
    (uint8_t)(addr >> 8), (uint8_t)(addr & 0xFF),
    (uint8_t)(qty >> 8),  (uint8_t)(qty & 0xFF), 0, 0 };
  const uint16_t c = crc16(req, 6);
  req[6] = (uint8_t)(c & 0xFF); req[7] = (uint8_t)(c >> 8);
  memcpy(lastReq, req, sizeof(req)); lastReqLen = sizeof(req);

  while (Serial1.available() > 0) (void)Serial1.read();
  delay(20);                                   // >= 3,5 caracteres de silencio

  digitalWrite(DE_PIN, HIGH);                  // habilita o transmissor
  Serial1.write(req, sizeof(req));
  Serial1.flush();                             // espera o quadro sair
  // Solta o DE logo apos o ultimo bit -- so ~1 caractere de folga. NAO
  // segurar muito: o drive pode responder rapido e o driver ligado
  // atropelaria a resposta.
  delayMicroseconds(11UL * 1000000UL / g_baud + 150UL);
  digitalWrite(DE_PIN, LOW);                   // volta a receber

  // Durante a transmissao o receptor fica desligado (RE alto) e o RO
  // fica em alta impedancia -- o divisor puxa o GPIO18 para baixo e a
  // UART acumula 0x00 nesse periodo. Descarta esse lixo AGORA, antes de
  // esperar a resposta do drive (que chega ~50 ms depois).
  delayMicroseconds(200);
  while (Serial1.available() > 0) (void)Serial1.read();

  uint8_t n = 0; const uint32_t t0 = millis();
  while ((millis() - t0) < windowMs)
    while (Serial1.available() > 0 && n < bufSize)
      buf[n++] = (uint8_t)Serial1.read();
  return n;
}

// veredito: 0 nada / 1 so-eco / 2 resposta / 3 lixo / 4 excecao
uint8_t classify(const uint8_t *buf, uint8_t len, uint8_t slave,
                 uint16_t *reg, uint8_t *found){
  if (len == 0) return 0;

  uint8_t start = 0;
  if (lastReqLen > 0 && len >= lastReqLen && memcmp(buf, lastReq, lastReqLen) == 0){
    if (len == lastReqLen) return 1;
    start = lastReqLen;
  }
  for (uint8_t off = start; off + 5 <= len; ++off){
    if (slave != WILDCARD && buf[off] != slave) continue;
    const uint8_t f = buf[off + 1];
    if (f & 0x80){
      const uint16_t got = (uint16_t)buf[off + 3] | ((uint16_t)buf[off + 4] << 8);
      if (got == crc16(&buf[off], 3)){ if (reg) *reg = buf[off + 2]; if (found) *found = buf[off]; return 4; }
      continue;
    }
    if (f != 0x03) continue;
    const uint16_t flen = (uint16_t)buf[off + 2] + 5;
    if (flen < 7 || off + flen > len) continue;
    const uint16_t got = (uint16_t)buf[off + flen - 2] | ((uint16_t)buf[off + flen - 1] << 8);
    if (got != crc16(&buf[off], (uint8_t)(flen - 2))) continue;
    if (reg)   *reg = ((uint16_t)buf[off + 3] << 8) | buf[off + 4];
    if (found) *found = buf[off];
    return 2;
  }
  return 3;
}

// -------------------- Ciclo automatico --------------------

void readAllParams(uint8_t baudIdx, uint8_t fmtIdx, uint8_t slave){
  Serial.println();
  Serial.print("=== Parametros @ ");
  Serial.print(BAUDS[baudIdx]); Serial.print(' ');
  Serial.print(FORMATS[fmtIdx].name); Serial.print(", slave 0x");
  printHex8(slave); Serial.println(" ===");

  for (uint8_t i = 0; i < PARAM_N; ++i){
    uint8_t buf[48]; memset(buf, 0, sizeof(buf)); uint16_t v = 0; uint8_t fnd = 0;
    setBus(baudIdx, fmtIdx);
    const uint8_t n = rawRead(slave, PARAMS[i].addr, 1, buf, sizeof(buf), WINDOW_MS);
    const uint8_t verd = classify(buf, n, slave, &v, &fnd);

    Serial.print(PARAMS[i].name); Serial.print(" @0x");
    printHex16(PARAMS[i].addr); Serial.print(" = ");
    if (verd == 2){ Serial.print("0x"); printHex16(v); }
    else          { Serial.print("  ----"); }
    Serial.print("   "); Serial.println(PARAMS[i].note);
  }
}

void runOnce(){
  Serial.println();
  Serial.println("=== Varredura automatica: baud x formato (curinga 0xFF) ===");

  bool found = false; uint8_t fBaud = 0, fFmt = 0, fSlave = 0x01;

  for (uint8_t b = 0; b < BAUD_N && !found; ++b){
    for (uint8_t f = 0; f < FMT_N && !found; ++f){
      uint8_t buf[48]; memset(buf, 0, sizeof(buf)); uint16_t v = 0; uint8_t fnd = 0;
      setBus(b, f);
      const uint8_t n = rawRead(WILDCARD, 0x0300, 1, buf, sizeof(buf), WINDOW_MS);
      const uint8_t verd = classify(buf, n, WILDCARD, &v, &fnd);
      const uint8_t dl = (n > 12) ? 12 : n; uint8_t d[12]; memcpy(d, buf, dl);

      padBaud(BAUDS[b]); pad(FORMATS[f].name, 5); Serial.print(": ");
      switch (verd){
        case 0: Serial.println("silencio"); break;
        case 1: Serial.println("so-eco"); break;
        case 2:
          Serial.print("RESPOSTA(de 0x"); printHex8(fnd);
          Serial.print(", P3-00=0x"); printHex16(v); Serial.println(")");
          fBaud = b; fFmt = f; fSlave = (v >= 1 && v <= 0x7F) ? (uint8_t)v : fnd; found = true;
          break;
        case 4:
          Serial.print("EXCECAO(0x"); printHex8((uint8_t)v);
          Serial.println(")  <<< O DRIVE NOS OUVE");
          fBaud = b; fFmt = f; fSlave = fnd; found = true;
          break;
        default: Serial.print("lixo: "); dump(d, dl); Serial.println(); break;
      }
    }
  }

  if (found){
    Serial.println();
    Serial.println(">>> ACHOU. A cadeia ESP32 fala com o drive.");
    readAllParams(fBaud, fFmt, fSlave);
  } else {
    Serial.println();
    Serial.println("Nada respondeu. Com o UNO fora do caminho, suspeite do");
    Serial.println("divisor no RO (GPIO18 sem sinal?), do sentido DE/RE (GPIO16),");
    Serial.println("de A/B (5<->6) ou do terra comum ate o CN1.");
  }
}

void setup(){
  pinMode(DE_PIN, OUTPUT);
  digitalWrite(DE_PIN, LOW);              // repouso = recepcao
  Serial.begin(115200);
  delay(400);

  Serial.println();
  Serial.println("------------------------------------------------------------");
  Serial.println(" ASDA-B2 Modbus RTU - ESP32-S3 / HW-097 - AUTO-RUN");
  Serial.println(" Somente leitura (03H). UART1: TX=GPIO17 RX=GPIO18 DE/RE=GPIO16");
  Serial.println("------------------------------------------------------------");

  const uint8_t tf[] = {0x01, 0x03, 0x02, 0x00, 0x00, 0x02};
  Serial.print("Autoteste CRC-16: ");
  Serial.println(crc16(tf, sizeof(tf)) == 0xB3C5 ? "OK" : "FALHOU");
}

void loop(){
  runOnce();
  Serial.println();
  Serial.println("--- novo ciclo em 5 s ---");
  delay(5000);
}
