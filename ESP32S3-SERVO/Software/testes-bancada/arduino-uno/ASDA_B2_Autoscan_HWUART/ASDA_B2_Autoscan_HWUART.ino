#include <Arduino.h>

/*
 * Varredura Modbus RTU do ASDA-B2 -- versao AUTO-RUN, sem menu.
 *
 * Por que existe: com o HW-097 ligado, o RO fica soldado no D0 (RX). O
 * receptor do modulo dirige a linha em repouso, e isso VENCE o TX do
 * chip USB (que passa por resistor serie no UNO). Resultado: o PC nao
 * consegue mandar tecla nenhuma para o Arduino -- o menu interativo do
 * ASDA_B2_Scan_HWUART fica surdo. Mas o Arduino TRANSMITE normalmente
 * (D1) e RECEBE do barramento (RO->D0), entao um sketch que roda tudo
 * sozinho no boot resolve: nao precisa de entrada do console.
 *
 * Somente leitura: transmite apenas 03H. Nunca escreve parametro,
 * nunca habilita o servo, nunca comanda o motor.
 *
 * Faz, ao ligar:
 *   1. Varre baud x formato em 0xFF (curinga) ate achar resposta.
 *   2. Na combinacao achada, le e imprime os parametros P3-xx / P0-00.
 *   3. Repete o ciclo a cada 5 s (util para observar estabilidade).
 *
 * Alvo: reproduzir, no embarcado, o que o dongle USB-RS485 (COM12) ja
 * fez -- 9600 8N2, endereco 1, P3-00 = 0x0001.
 *
 * SOLTE O D0 ANTES DE CADA UPLOAD, recoloque depois.
 */

// -------------------- Configuracao --------------------

constexpr uint32_t CONSOLE_BAUD = 115200;
constexpr int8_t   DE_PIN = 2;          // HW-097: DE e RE em curto no D2

struct SerialFormat { const char *name; uint8_t config; };
const SerialFormat FORMATS[] = {
  {"8N2", SERIAL_8N2},                  // P3-02 = 6 -- o do drive
  {"8E1", SERIAL_8E1},                  // P3-02 = 7
  {"8O1", SERIAL_8O1}                   // P3-02 = 8
};
const uint32_t BAUDS[] = {9600, 38400, 19200, 115200, 57600, 4800};

constexpr uint8_t FMT_N  = sizeof(FORMATS) / sizeof(FORMATS[0]);
constexpr uint8_t BAUD_N = sizeof(BAUDS) / sizeof(BAUDS[0]);

constexpr uint8_t  WILDCARD = 0xFF;
constexpr uint16_t WINDOW_MS = 500;     // folgada: P3-07 do drive esta em 100

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

// -------------------- Estado do ultimo pedido (p/ descartar eco) ------

uint8_t lastReq[8];
uint8_t lastReqLen = 0;

// -------------------- Impressao --------------------

void printHex8(uint8_t v)  { if (v < 0x10) Serial.print('0'); Serial.print(v, HEX); }
void printHex16(uint16_t v){ for (int8_t s = 12; s >= 0; s -= 4) Serial.print((uint8_t)((v >> s) & 0xF), HEX); }
void pad(const char *t, uint8_t w){ Serial.print(t); for (uint8_t i = strlen(t); i < w; ++i) Serial.print(' '); }
void padBaud(uint32_t b){ Serial.print(b); uint8_t d = (b >= 100000UL) ? 6 : (b >= 10000UL) ? 5 : 4; for (uint8_t i = d; i < 7; ++i) Serial.print(' '); }
void dump(const uint8_t *d, uint8_t n){ for (uint8_t i = 0; i < n; ++i){ printHex8(d[i]); Serial.print(' '); } }

// -------------------- Troca de modo da UART --------------------

void holdTxIdle(){ pinMode(1, OUTPUT); digitalWrite(1, HIGH); }

void enterConsole(){
  Serial.flush(); Serial.end(); holdTxIdle();
  Serial.begin(CONSOLE_BAUD, SERIAL_8N1); delay(2);
}
void enterBus(uint8_t baudIdx, uint8_t fmtIdx){
  Serial.flush(); Serial.end(); holdTxIdle();
  Serial.begin(BAUDS[baudIdx], FORMATS[fmtIdx].config); delay(5);
  while (Serial.available() > 0) (void)Serial.read();
}

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

// -------------------- Transacao crua --------------------

uint8_t rawRead(uint8_t slave, uint16_t addr, uint16_t qty,
                uint8_t *buf, uint8_t bufSize, uint16_t windowMs){
  uint8_t req[8] = { slave, 0x03,
    (uint8_t)(addr >> 8), (uint8_t)(addr & 0xFF),
    (uint8_t)(qty >> 8),  (uint8_t)(qty & 0xFF), 0, 0 };
  const uint16_t c = crc16(req, 6);
  req[6] = (uint8_t)(c & 0xFF); req[7] = (uint8_t)(c >> 8);
  memcpy(lastReq, req, sizeof(req)); lastReqLen = sizeof(req);

  while (Serial.available() > 0) (void)Serial.read();
  delay(20);                              // >= 3,5 caracteres de silencio

  if (DE_PIN >= 0) digitalWrite(DE_PIN, HIGH);
  Serial.write(req, sizeof(req));
  Serial.flush();                         // so retorna quando o ultimo bit saiu
  if (DE_PIN >= 0) digitalWrite(DE_PIN, LOW);

  uint8_t n = 0; const uint32_t t0 = millis();
  while ((millis() - t0) < windowMs)
    while (Serial.available() > 0 && n < bufSize)
      buf[n++] = (uint8_t)Serial.read();
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
  Serial.print(F("=== Parametros @ "));
  Serial.print(BAUDS[baudIdx]); Serial.print(' ');
  Serial.print(FORMATS[fmtIdx].name); Serial.print(F(", slave 0x"));
  printHex8(slave); Serial.println(F(" ==="));

  for (uint8_t i = 0; i < PARAM_N; ++i){
    uint8_t buf[48]; memset(buf, 0, sizeof(buf)); uint16_t v = 0, r = 0; uint8_t fnd = 0;
    enterBus(baudIdx, fmtIdx);
    const uint8_t n = rawRead(slave, PARAMS[i].addr, 1, buf, sizeof(buf), WINDOW_MS);
    const uint8_t verd = classify(buf, n, slave, &v, &fnd);
    enterConsole();

    Serial.print(PARAMS[i].name); Serial.print(F(" @0x"));
    printHex16(PARAMS[i].addr); Serial.print(F(" = "));
    if (verd == 2){ Serial.print(F("0x")); printHex16(v); }
    else          { Serial.print(F("  ----")); }
    Serial.print(F("   ")); Serial.println(PARAMS[i].note);
  }
}

void runOnce(){
  Serial.println();
  Serial.println(F("=== Varredura automatica: baud x formato (curinga 0xFF) ==="));

  bool found = false; uint8_t fBaud = 0, fFmt = 0, fSlave = 0x01;

  for (uint8_t b = 0; b < BAUD_N && !found; ++b){
    for (uint8_t f = 0; f < FMT_N && !found; ++f){
      uint8_t buf[48]; memset(buf, 0, sizeof(buf)); uint16_t v = 0; uint8_t fnd = 0;
      enterBus(b, f);
      const uint8_t n = rawRead(WILDCARD, 0x0300, 1, buf, sizeof(buf), WINDOW_MS);
      const uint8_t verd = classify(buf, n, WILDCARD, &v, &fnd);
      const uint8_t dl = (n > 12) ? 12 : n; uint8_t d[12]; memcpy(d, buf, dl);
      enterConsole();

      padBaud(BAUDS[b]); pad(FORMATS[f].name, 5); Serial.print(F(": "));
      switch (verd){
        case 0: Serial.println(F("silencio")); break;
        case 1: Serial.println(F("so-eco")); break;
        case 2:
          Serial.print(F("RESPOSTA(de 0x")); printHex8(fnd);
          Serial.print(F(", P3-00=0x")); printHex16(v); Serial.println(')');
          fBaud = b; fFmt = f; fSlave = (v >= 1 && v <= 0x7F) ? (uint8_t)v : fnd; found = true;
          break;
        case 4:
          Serial.print(F("EXCECAO(0x")); printHex8((uint8_t)v);
          Serial.println(F(")  <<< O DRIVE NOS OUVE"));
          fBaud = b; fFmt = f; fSlave = fnd; found = true;
          break;
        default: Serial.print(F("lixo: ")); dump(d, dl); Serial.println(); break;
      }
    }
  }

  if (found){
    Serial.println();
    Serial.println(F(">>> ACHOU. A cadeia embarcada fala com o drive."));
    readAllParams(fBaud, fFmt, fSlave);
  } else {
    Serial.println();
    Serial.println(F("Nada respondeu em nenhuma combinacao."));
    Serial.println(F("O drive ja foi provado pelo dongle, entao suspeite da"));
    Serial.println(F("NOSSA ponta: sentido DE/RE (D2), A/B trocados (5<->6),"));
    Serial.println(F("terra ate o CN1, ou ligacao TTL fria."));
  }
}

void setup(){
  if (DE_PIN >= 0){ pinMode(DE_PIN, OUTPUT); digitalWrite(DE_PIN, LOW); }  // repouso = recepcao
  holdTxIdle();
  Serial.begin(CONSOLE_BAUD, SERIAL_8N1);
  delay(300);

  Serial.println();
  Serial.println(F("------------------------------------------------------------"));
  Serial.println(F(" ASDA-B2 Modbus RTU - UNO/HW-097 - AUTO-RUN (sem menu)"));
  Serial.println(F(" Somente leitura (03H). DE/RE no D2. TX=D1, RX=D0."));
  Serial.println(F("------------------------------------------------------------"));

  const uint8_t tf[] = {0x01, 0x03, 0x02, 0x00, 0x00, 0x02};
  Serial.print(F("Autoteste CRC-16: "));
  Serial.println(crc16(tf, sizeof(tf)) == 0xB3C5 ? F("OK") : F("FALHOU"));
}

void loop(){
  runOnce();
  Serial.println();
  Serial.println(F("--- novo ciclo em 5 s ---"));
  delay(5000);
}
