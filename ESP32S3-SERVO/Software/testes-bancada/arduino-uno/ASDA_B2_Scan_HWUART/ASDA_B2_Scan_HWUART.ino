#include <Arduino.h>

/*
 * Varredura Modbus RTU do Delta ASDA-B2 -- UART de HARDWARE do UNO.
 *
 * Substitui o ASDA_B2_RS485_Scan_UNO, que usava SoftwareSerial. Serve
 * tanto para o enlace RS-232 (MAX3232 no CN3-2/CN3-4) quanto para
 * RS-485, e nos dois casos elimina as limitacoes da biblioteca:
 *
 *   SoftwareSerial              UART de hardware
 *   ------------------------    --------------------------------
 *   so transmite 8N1            8N2, 8E1 e 8O1 de verdade
 *   8N2 era emulado com pausa   sem emulacao, sem aproximacao
 *   surda enquanto transmite    full-duplex: captura eco
 *   instavel acima de 38400     115200 sem esforco
 *   temporizacao por software   temporizacao por hardware
 *
 * Somente leitura: o unico codigo Modbus transmitido e o 03H. Nunca
 * escreve parametro, nunca habilita o servo, nunca comanda o motor.
 *
 * ------------------------------------------------------------------
 * DUAS RESSALVAS DO USO DA UART DE HARDWARE
 * ------------------------------------------------------------------
 * 1. DESLIGUE O FIO DO D0 ANTES DE CADA UPLOAD.
 *    O D0 e o RX do bootloader. Com o conversor pendurado nele, o
 *    bootloader nao enxerga os dados do PC e o upload falha com
 *    "not in sync". Puxe o fio, grave, recoloque.
 *
 * 2. O TEXTO DO CONSOLE SAI NO ENLACE.
 *    O D1 e TX do console e do barramento ao mesmo tempo. Em RS-232
 *    isso e inofensivo: o drive descarta quadro com CRC invalido, e o
 *    CN3 e porta de telemetria, nao de comando. Em RS-485 com o pino
 *    DE ligado (abaixo), o console nem chega ao barramento, porque o
 *    driver so e habilitado durante a transacao.
 *
 * ------------------------------------------------------------------
 * LIGACOES
 * ------------------------------------------------------------------
 * RS-232, pelo MAX3232 (montagem atual):
 *
 *   Arduino D1 (TX) ---> RXD do MAX3232
 *   Arduino D0 (RX) <--- TXD do MAX3232      (soltar no upload)
 *   Arduino 5V      ---> VCC
 *   Arduino GND     ---> GND
 *
 *   DB9 pino 2 (saida do modulo) ---> CN3-4  (RX do drive)
 *   DB9 pino 3 (entrada)         <--- CN3-2  (TX do drive)
 *   DB9 pino 5                   ---  CN3-1 / GND da placa do CN1
 *   carcaca do DB9               ---  NAO CONECTAR
 *
 * RS-485 pelo modulo HW-097 (MAX485, controle manual de direcao):
 *
 *   Arduino D1 (TX) ---> DI
 *   Arduino D0 (RX) <--- RO                  (soltar no upload)
 *   Arduino D2      ---> DE e RE em curto     (DE_PIN = 2 abaixo)
 *   Arduino 5V      ---> VCC
 *   Arduino GND     ---> GND
 *
 *   HW-097 A (D+)   ---> CN3-5
 *   HW-097 B (D-)   ---> CN3-6
 *   Arduino GND     ---  terra da placa do CN1  (referencia obrigatoria)
 *
 *   RE e ativo-baixo, DE e ativo-alto: em curto, um so pino controla o
 *   sentido -- LOW escuta, HIGH transmite. Detalhes e diagrama em
 *   PINOUT_HW097_RS485.md, ao lado deste sketch.
 *
 * ------------------------------------------------------------------
 * O QUE JA ESTA CONFIRMADO NESTA BANCADA
 * ------------------------------------------------------------------
 *   painel:  P3-00 = 0001   endereco 1
 *            P3-01 = 0011   9600 nos dois lados (digito Y = RS-485,
 *                           digito X = RS-232)
 *            P3-02 = 0066   8N2 MODBUS RTU nos dois lados
 *            P3-05 = 0000   RS-232 via Modbus, nao ASDA-Soft
 *            P3-07 = 100    atraso de resposta -- por isso as janelas
 *                           aqui sao de 500 ms
 *
 *   enlace:  DB9 pino 2 em -5,2 V, e -5,2 V tambem no outro extremo do
 *            fio, no contato 4. Nossa saida chega no drive.
 *            DB9 pino 3 em -7,6 V, vindo do CN3-2. O transmissor do
 *            drive esta vivo e o plugue faz contato.
 *            Laco pelo MAX3232 com os pinos 2 e 3 em curto: solido,
 *            tres rodadas identicas, resiste ao pull-up.
 *
 * Ou seja: os dois sentidos do enlace estao provados no nivel fisico.
 * Se esta versao continuar muda, o problema nao esta mais no caminho.
 *
 * ------------------------------------------------------------------
 * RESOLVIDO EM 2026-09-01 -- O DRIVE ESTA BOM
 * ------------------------------------------------------------------
 * O dongle USB-RS485 (CH340, COM12) conversou com o drive na PRIMEIRA
 * combinacao: 9600 8N2, endereco 1. Leu tudo -- P3-00=0x0001,
 * P3-01=0x0011, P3-02=0x0066, P3-07=0x0064, P0-00=0x03F6. Ver
 * Software/PC_Modbus_Scan/asda_scan.py.
 *
 * Consequencia: o drive, a fiacao do CN3 (5=D+, 6=D-) e o protocolo
 * estao PROVADOS. O silencio de antes era a nossa cadeia -- Arduino +
 * conversor + jumpers + SoftwareSerial. Esta versao (UART de hardware +
 * HW-097 com DE/RE manual) existe para reproduzir, no embarcado, o que
 * o COM12 ja fez. O alvo nao e mais diagnosticar: e validar a nossa
 * ponta. Pinout completo em PINOUT_HW097_RS485.md.
 */

// -------------------- Configuracao --------------------

constexpr uint32_t CONSOLE_BAUD = 115200;

/*
 * Pino de DE/RE para modulo RS-485 com controle manual de direcao.
 * -1 desliga o controle: e o valor para RS-232 (full-duplex, sem
 * direcao) e para modulo com direcao automatica.
 *
 * Com um pino valido, o driver e habilitado imediatamente antes do
 * quadro e liberado logo apos o Serial.flush(), que no AVR so retorna
 * quando o ultimo bit saiu do shift register. Sem isso o driver corta
 * o proprio ultimo byte.
 */
constexpr int8_t DE_PIN = 2;  // HW-097: DE e RE em curto no D2. -1 = RS-232.

struct SerialFormat {
  const char *name;
  uint8_t config;
};

// Tabela do P3-02 do manual: 6, 7 e 8 sao os modos RTU.
const SerialFormat SERIAL_FORMATS[] = {
  {"8N2", SERIAL_8N2},  // P3-02 = 6 -- o do drive
  {"8E1", SERIAL_8E1},  // P3-02 = 7
  {"8O1", SERIAL_8O1}   // P3-02 = 8
};

// 9600 primeiro: e o do drive, lido no painel.
const uint32_t BAUD_RATES[] = {9600, 38400, 19200, 115200, 57600, 4800};

constexpr uint8_t FORMAT_COUNT = sizeof(SERIAL_FORMATS) / sizeof(SERIAL_FORMATS[0]);
constexpr uint8_t BAUD_COUNT = sizeof(BAUD_RATES) / sizeof(BAUD_RATES[0]);

constexpr uint16_t REG_P3_00 = 0x0300;

/*
 * Endereco curinga. Secao 8.2 do manual, na descricao do P3-00:
 * "When the communication address setting of MODBUS is set to 0xFF,
 *  the servo drive will automatically reply and receive data
 *  regardless of the address."
 * O manual nao diz com que endereco ele responde, entao a
 * classificacao aceita qualquer um.
 */
constexpr uint8_t SLAVE_WILDCARD = 0xFF;

// Janela de resposta folgada: o P3-07 deste drive esta em 100.
constexpr uint16_t WINDOW_SCAN_MS = 500;
constexpr uint16_t WINDOW_ADDR_MS = 300;

// -------------------- Estado --------------------

uint8_t currentBaudIndex = 0;
uint8_t currentFormatIndex = 0;
uint8_t currentSlave = SLAVE_WILDCARD;
bool verboseDump = true;

uint8_t lastRequest[8];
uint8_t lastRequestLength = 0;

// -------------------- Impressao (o AVR nao tem printf) --------------------

void printHex8(uint8_t value) {
  if (value < 0x10) {
    Serial.print('0');
  }
  Serial.print(value, HEX);
}

void printHex16(uint16_t value) {
  for (int8_t shift = 12; shift >= 0; shift -= 4) {
    Serial.print(static_cast<uint8_t>((value >> shift) & 0x0FU), HEX);
  }
}

void printPadded(const char *text, uint8_t width) {
  Serial.print(text);
  for (uint8_t index = strlen(text); index < width; ++index) {
    Serial.print(' ');
  }
}

void printBaudPadded(uint32_t baud) {
  Serial.print(baud);
  uint8_t digits = 4;
  if (baud >= 100000UL) {
    digits = 6;
  } else if (baud >= 10000UL) {
    digits = 5;
  }
  for (uint8_t index = digits; index < 7; ++index) {
    Serial.print(' ');
  }
}

void dumpBytes(const uint8_t *data, uint8_t length) {
  for (uint8_t index = 0; index < length; ++index) {
    printHex8(data[index]);
    Serial.print(' ');
  }
}

// -------------------- Troca de modo da UART --------------------

/*
 * Entre end() e begin() o D1 fica em alta impedancia. Segura-lo em
 * nivel alto mantem a linha em repouso (mark) durante a troca, em vez
 * de deixar a entrada do conversor flutuando.
 */
void holdTxIdle() {
  pinMode(1, OUTPUT);
  digitalWrite(1, HIGH);
}

void enterConsole() {
  Serial.flush();
  Serial.end();
  holdTxIdle();
  Serial.begin(CONSOLE_BAUD, SERIAL_8N1);
  delay(2);
}

void enterBus(uint8_t baudIndex, uint8_t formatIndex) {
  Serial.flush();
  Serial.end();
  holdTxIdle();
  Serial.begin(BAUD_RATES[baudIndex], SERIAL_FORMATS[formatIndex].config);
  delay(5);
  while (Serial.available() > 0) {
    (void)Serial.read();
  }
}

void printCurrentSettings() {
  Serial.print(F("UART: "));
  Serial.print(BAUD_RATES[currentBaudIndex]);
  Serial.print(F(" baud, "));
  Serial.print(SERIAL_FORMATS[currentFormatIndex].name);
  Serial.print(F(", TX=D1, RX=D0, slave=0x"));
  printHex8(currentSlave);
  Serial.println();
  if (currentSlave == SLAVE_WILDCARD) {
    Serial.println(F("  (0xFF = curinga: responde qualquer que seja o P3-00)"));
  }
  if (DE_PIN >= 0) {
    Serial.print(F("  DE/RE no pino D"));
    Serial.println(DE_PIN);
  }
}

// -------------------- CRC --------------------

uint16_t modbusCrc16(const uint8_t *data, uint8_t length) {
  uint16_t crc = 0xFFFF;

  while (length-- > 0) {
    crc ^= *data++;
    for (uint8_t bit = 0; bit < 8; ++bit) {
      crc = ((crc & 0x0001U) != 0U) ? static_cast<uint16_t>((crc >> 1U) ^ 0xA001U)
                                    : static_cast<uint16_t>(crc >> 1U);
    }
  }

  return crc;
}

// -------------------- Transacao crua --------------------

uint8_t rawRead(uint8_t slave,
                uint16_t startAddress,
                uint16_t registerCount,
                uint8_t *buffer,
                uint8_t bufferSize,
                uint16_t windowMs,
                uint8_t functionCode = 0x03) {
  uint8_t request[8] = {
    slave,
    functionCode,
    static_cast<uint8_t>(startAddress >> 8U),
    static_cast<uint8_t>(startAddress & 0x00FFU),
    static_cast<uint8_t>(registerCount >> 8U),
    static_cast<uint8_t>(registerCount & 0x00FFU),
    0,
    0
  };

  const uint16_t crc = modbusCrc16(request, 6);
  request[6] = static_cast<uint8_t>(crc & 0x00FFU);
  request[7] = static_cast<uint8_t>(crc >> 8U);

  memcpy(lastRequest, request, sizeof(request));
  lastRequestLength = sizeof(request);

  while (Serial.available() > 0) {
    (void)Serial.read();
  }

  // O RTU pede 3,5 tempos de caractere de silencio antes do quadro.
  // Em 4800 baud isso da 8 ms; 20 ms cobre toda a lista com folga.
  delay(20);

  if (DE_PIN >= 0) {
    digitalWrite(DE_PIN, HIGH);
  }

  Serial.write(request, sizeof(request));

  // flush() no AVR so retorna quando o ultimo bit saiu do shift
  // register -- e o unico ponto seguro para liberar o DE.
  Serial.flush();

  if (DE_PIN >= 0) {
    digitalWrite(DE_PIN, LOW);
  }

  uint8_t received = 0;
  const uint32_t startMs = millis();

  while ((millis() - startMs) < windowMs) {
    while (Serial.available() > 0 && received < bufferSize) {
      buffer[received++] = static_cast<uint8_t>(Serial.read());
    }
  }

  return received;
}

/*
 * Classifica o que voltou:
 *   0 = nada
 *   1 = so o eco do proprio pedido
 *   2 = resposta valida (CRC confere)
 *   3 = bytes que nao formam quadro valido
 *   4 = excecao Modbus com CRC valido (codigo em firstRegister)
 *
 * Com slave == SLAVE_WILDCARD qualquer endereco e aceito na resposta;
 * quem atendeu sai em foundSlave.
 */
uint8_t classifyReply(const uint8_t *buffer,
                      uint8_t length,
                      uint8_t slave,
                      uint16_t *firstRegister,
                      uint8_t *foundSlave = nullptr,
                      uint8_t expectedFunction = 0x03) {
  if (length == 0) {
    return 0;
  }

  /*
   * O eco tem de sair ANTES da busca por quadro. O pedido "03H, 1
   * registrador" tem 8 bytes e, lido como resposta, da funcao 0x03 e
   * byte count 0x03 -> quadro de 8 bytes cujo CRC nos bytes 6 e 7 e
   * exatamente o CRC do pedido. Confere sempre. Sem este descarte um
   * eco viraria "RESPOSTA P3-00=0x0000".
   *
   * Em full-duplex isto deixa de ser hipotetico: o eco chega mesmo,
   * seja por laco de teste, seja por diafonia entre TX e RX.
   */
  uint8_t searchStart = 0;
  if (lastRequestLength > 0 && length >= lastRequestLength &&
      memcmp(buffer, lastRequest, lastRequestLength) == 0) {
    if (length == lastRequestLength) {
      return 1;
    }
    searchStart = lastRequestLength;
  }

  for (uint8_t offset = searchStart; offset + 5 <= length; ++offset) {
    if (slave != SLAVE_WILDCARD && buffer[offset] != slave) {
      continue;
    }

    const uint8_t function = buffer[offset + 1];

    if ((function & 0x80U) != 0U) {
      const uint16_t got = static_cast<uint16_t>(buffer[offset + 3]) |
                           (static_cast<uint16_t>(buffer[offset + 4]) << 8U);
      if (got == modbusCrc16(&buffer[offset], 3)) {
        if (firstRegister != nullptr) {
          *firstRegister = buffer[offset + 2];
        }
        if (foundSlave != nullptr) {
          *foundSlave = buffer[offset];
        }
        return 4;
      }
      continue;
    }

    if (function != expectedFunction) {
      continue;
    }

    const uint16_t frameLength = static_cast<uint16_t>(buffer[offset + 2]) + 5U;
    if (frameLength < 7 || offset + frameLength > length) {
      continue;
    }

    const uint16_t got = static_cast<uint16_t>(buffer[offset + frameLength - 2]) |
                         (static_cast<uint16_t>(buffer[offset + frameLength - 1]) << 8U);
    if (got != modbusCrc16(&buffer[offset], static_cast<uint8_t>(frameLength - 2))) {
      continue;
    }

    if (firstRegister != nullptr) {
      *firstRegister = (static_cast<uint16_t>(buffer[offset + 3]) << 8U) |
                       static_cast<uint16_t>(buffer[offset + 4]);
    }
    if (foundSlave != nullptr) {
      *foundSlave = buffer[offset];
    }
    return 2;
  }

  return 3;
}

// -------------------- Testes --------------------

/*
 * Agora este teste vale de verdade. Em full-duplex o receptor fica
 * ligado durante a transmissao, entao o eco -- se existir caminho de
 * volta -- aparece. Foi justamente isto que a SoftwareSerial nunca
 * pode fazer, e o que tornou o diagnostico do HW-519 inconclusivo.
 */
void testEcho() {
  Serial.println();
  Serial.println(F("=== TESTE 1: eco / laco ==="));
  Serial.println(F("Montagens que fazem sentido aqui:"));
  Serial.println(F("  (a) DB9 pino 2 em curto com o pino 3, fora do CN3."));
  Serial.println(F("      Devem voltar os 8 bytes exatos."));
  Serial.println(F("  (b) ligado no CN3. O que voltar e diafonia do nosso"));
  Serial.println(F("      proprio TX, ou resposta do drive."));
  Serial.println();
  printCurrentSettings();

  uint8_t buffer[48];
  memset(buffer, 0, sizeof(buffer));

  enterBus(currentBaudIndex, currentFormatIndex);
  const uint8_t received = rawRead(currentSlave, REG_P3_00, 1,
                                     buffer, sizeof(buffer), 300);
  enterConsole();

  Serial.print(F("Enviados 8, recebidos "));
  Serial.print(received);
  Serial.print(F(": "));
  dumpBytes(buffer, received);
  Serial.println();

  uint16_t value = 0;
  uint8_t answered = 0;
  const uint8_t verdict = classifyReply(buffer, received, currentSlave,
                                        &value, &answered);
  Serial.println();
  switch (verdict) {
    case 0:
      Serial.println(F("Nada voltou."));
      break;
    case 1:
      Serial.println(F("ECO LIMPO: os 8 bytes exatos do pedido."));
      Serial.println(F("O caminho TX->RX esta fechado e a UART esta certa."));
      break;
    case 2:
      Serial.print(F("RESPOSTA VALIDA de 0x"));
      printHex8(answered);
      Serial.print(F(", P3-00 = 0x"));
      printHex16(value);
      Serial.println();
      break;
    case 4:
      Serial.print(F("EXCECAO Modbus 0x"));
      printHex8(static_cast<uint8_t>(value));
      Serial.println(F(" -- o drive RESPONDEU. Ele nos ouve."));
      break;
    default:
      Serial.println(F("Bytes sem quadro valido. Se forem poucos 0x00, e"));
      Serial.println(F("diafonia do proprio TX -- confirme com o teste 6."));
      break;
  }
}

void scanAddresses() {
  Serial.println();
  Serial.println(F("=== TESTE 2: enderecos 0x01..0x7F ==="));
  printCurrentSettings();
  Serial.println(F("Aguarde ~40 s..."));

  uint8_t hitSlave[6];
  uint16_t hitValue[6];
  uint8_t hitCount = 0;
  uint8_t noiseCount = 0;

  enterBus(currentBaudIndex, currentFormatIndex);

  for (uint8_t slave = 0x01; slave <= 0x7F; ++slave) {
    uint8_t buffer[48];
    memset(buffer, 0, sizeof(buffer));
    uint16_t value = 0;
    const uint8_t received = rawRead(slave, REG_P3_00, 1,
                                       buffer, sizeof(buffer), WINDOW_ADDR_MS);
    const uint8_t verdict = classifyReply(buffer, received, slave, &value);

    if ((verdict == 2 || verdict == 4) && hitCount < 6) {
      hitSlave[hitCount] = slave;
      hitValue[hitCount] = value;
      ++hitCount;
    } else if (verdict == 3) {
      ++noiseCount;
    }
  }

  enterConsole();

  for (uint8_t index = 0; index < hitCount; ++index) {
    Serial.print(F(">>> RESPOSTA no endereco 0x"));
    printHex8(hitSlave[index]);
    Serial.print(F(", P3-00 = 0x"));
    printHex16(hitValue[index]);
    Serial.println();
  }

  if (noiseCount > 0) {
    Serial.print(F("Enderecos com bytes sem quadro valido: "));
    Serial.println(noiseCount);
  }

  if (hitCount == 0) {
    Serial.println(F("Nenhum drive respondeu nesta combinacao."));
  } else {
    currentSlave = hitSlave[0];
    Serial.println(F("Endereco corrente ajustado."));
  }
}

void scanBaudAndFormat() {
  Serial.println();
  Serial.println(F("=== TESTE 3: baud x formato ==="));
  Serial.println(F("Seis bauds x tres formatos, em 0xFF, 0x01 e 0x7F."));
  Serial.println(F("Agora com 8N2/8E1/8O1 de hardware, sem emulacao."));
  Serial.println();

  const uint8_t candidates[3] = {SLAVE_WILDCARD, 0x01, 0x7F};
  const uint8_t savedBaud = currentBaudIndex;
  const uint8_t savedFormat = currentFormatIndex;
  bool found = false;

  for (uint8_t baudIndex = 0; baudIndex < BAUD_COUNT && !found; ++baudIndex) {
    for (uint8_t formatIndex = 0; formatIndex < FORMAT_COUNT && !found; ++formatIndex) {
      uint8_t verdicts[3];
      uint16_t values[3];
      uint8_t answered[3];
      uint8_t lengths[3];
      uint8_t dumps[3][12];

      enterBus(baudIndex, formatIndex);

      for (uint8_t index = 0; index < 3; ++index) {
        uint8_t buffer[48];
        memset(buffer, 0, sizeof(buffer));
        values[index] = 0;
        answered[index] = 0;
        const uint8_t received = rawRead(candidates[index], REG_P3_00, 1,
                                           buffer, sizeof(buffer), WINDOW_SCAN_MS);
        verdicts[index] = classifyReply(buffer, received, candidates[index],
                                        &values[index], &answered[index]);
        lengths[index] = (received > 12) ? 12 : received;
        memcpy(dumps[index], buffer, lengths[index]);
      }

      enterConsole();

      printBaudPadded(BAUD_RATES[baudIndex]);
      printPadded(SERIAL_FORMATS[formatIndex].name, 5);
      Serial.print(F(": "));

      for (uint8_t index = 0; index < 3; ++index) {
        Serial.print(F("0x"));
        printHex8(candidates[index]);
        Serial.print('=');

        switch (verdicts[index]) {
          case 0:
            Serial.print(F("silencio  "));
            break;
          case 1:
            Serial.print(F("so-eco    "));
            break;
          case 2:
            Serial.print(F("RESPOSTA(0x"));
            printHex8(answered[index]);
            Serial.print(F(", P3-00=0x"));
            printHex16(values[index]);
            Serial.print(F(")  "));
            currentBaudIndex = baudIndex;
            currentFormatIndex = formatIndex;
            currentSlave = (values[index] >= 0x01U && values[index] <= 0x7FU)
                             ? static_cast<uint8_t>(values[index])
                             : answered[index];
            found = true;
            break;
          case 4:
            Serial.print(F("EXCECAO(0x"));
            printHex8(static_cast<uint8_t>(values[index]));
            Serial.print(F(")  "));
            currentBaudIndex = baudIndex;
            currentFormatIndex = formatIndex;
            currentSlave = answered[index];
            found = true;
            break;
          default:
            Serial.print(F("lixo      "));
            break;
        }
      }

      Serial.println();

      if (verboseDump) {
        for (uint8_t index = 0; index < 3; ++index) {
          if (verdicts[index] == 3) {
            Serial.print(F("        0x"));
            printHex8(candidates[index]);
            Serial.print(F(" devolveu: "));
            dumpBytes(dumps[index], lengths[index]);
            Serial.println();
          }
        }
      }
    }
  }

  Serial.println();
  if (!found) {
    currentBaudIndex = savedBaud;
    currentFormatIndex = savedFormat;
    Serial.println(F("Nada respondeu -- nem no curinga, nem em 8E1/8O1."));
    Serial.println(F("Com o enlace ja provado nos dois sentidos no nivel"));
    Serial.println(F("fisico, e com a SoftwareSerial fora do caminho, o"));
    Serial.println(F("problema deixa de estar do nosso lado. O proximo"));
    Serial.println(F("passo e fazer o drive RECLAMAR: configurar o P3-03"));
    Serial.println(F("para sinalizar erro de comunicacao. Se ele acusar"));
    Serial.println(F("falha com o nosso trafego, o receptor dele nos ouve."));
  } else {
    Serial.println(F("ACHOU. Anote a combinacao."));
    printCurrentSettings();
  }
}

void repeatedProbe() {
  Serial.println();
  Serial.println(F("=== TESTE 4: sondagem repetida ==="));
  Serial.println(F("Qualquer tecla para parar."));
  printCurrentSettings();

  while (Serial.available() > 0) {
    (void)Serial.read();
  }

  while (true) {
    uint8_t buffer[48];
    memset(buffer, 0, sizeof(buffer));
    uint16_t value = 0;
    uint8_t answered = 0;

    enterBus(currentBaudIndex, currentFormatIndex);
    const uint8_t received = rawRead(currentSlave, REG_P3_00, 1,
                                       buffer, sizeof(buffer), WINDOW_SCAN_MS);
    const uint8_t verdict = classifyReply(buffer, received, currentSlave,
                                          &value, &answered);
    const uint8_t dumpLength = (received > 12) ? 12 : received;
    uint8_t dump[12];
    memcpy(dump, buffer, dumpLength);
    enterConsole();

    Serial.print(millis());
    Serial.print(F("  "));
    switch (verdict) {
      case 0: Serial.println(F("silencio")); break;
      case 1: Serial.println(F("so eco")); break;
      case 2:
        Serial.print(F("RESPOSTA de 0x"));
        printHex8(answered);
        Serial.print(F("  P3-00=0x"));
        printHex16(value);
        Serial.println();
        break;
      case 4:
        Serial.print(F("EXCECAO 0x"));
        printHex8(static_cast<uint8_t>(value));
        Serial.println();
        break;
      default:
        Serial.print(F("lixo: "));
        dumpBytes(dump, dumpLength);
        Serial.println();
        break;
    }

    if (Serial.available() > 0) {
      break;
    }
    delay(400);
  }

  while (Serial.available() > 0) {
    (void)Serial.read();
  }
  Serial.println(F("Parado."));
}

void dumpKeyParameters() {
  Serial.println();
  Serial.println(F("=== TESTE 5: parametros P3-xx ==="));
  printCurrentSettings();
  Serial.println();

  const uint16_t addresses[7] = {0x0300, 0x0302, 0x0304, 0x030A, 0x030E, 0x0102, 0x030C};

  for (uint8_t index = 0; index < 7; ++index) {
    uint8_t buffer[48];
    memset(buffer, 0, sizeof(buffer));
    uint16_t value = 0;

    enterBus(currentBaudIndex, currentFormatIndex);
    const uint8_t received = rawRead(currentSlave, addresses[index], 1,
                                       buffer, sizeof(buffer), WINDOW_SCAN_MS);
    const uint8_t verdict = classifyReply(buffer, received, currentSlave, &value);
    enterConsole();

    switch (index) {
      case 0: Serial.print(F("P3-00 @0x0300 = ")); break;
      case 1: Serial.print(F("P3-01 @0x0302 = ")); break;
      case 2: Serial.print(F("P3-02 @0x0304 = ")); break;
      case 3: Serial.print(F("P3-05 @0x030A = ")); break;
      case 4: Serial.print(F("P3-07 @0x030E = ")); break;
      case 5: Serial.print(F("P1-01 @0x0102 = ")); break;
      default: Serial.print(F("P3-06 @0x030C = ")); break;
    }

    if (verdict == 2) {
      Serial.print(F("0x"));
      printHex16(value);
    } else {
      Serial.print(F("  ----"));
    }

    switch (index) {
      case 0: Serial.println(F("   endereco (painel: 0001)")); break;
      case 1: Serial.println(F("   baud (painel: 0011 = 9600)")); break;
      case 2: Serial.println(F("   protocolo (painel: 0066 = 8N2 RTU)")); break;
      case 3: Serial.println(F("   mecanismo (painel: 0000)")); break;
      case 4: Serial.println(F("   atraso de resposta (painel: 100)")); break;
      case 5: Serial.println(F("   modo de controle - NAO ALTERAR")); break;
      default: Serial.println(F("   origem das DI - NAO ALTERAR")); break;
    }
  }
}

void passiveListen() {
  Serial.println();
  Serial.println(F("=== TESTE 6: escuta passiva, nao transmite ==="));
  Serial.println(F("Cinco segundos so ouvindo. E o controle para separar"));
  Serial.println(F("trafego real de diafonia do nosso proprio TX: se o"));
  Serial.println(F("lixo some aqui, somos nos que o causamos."));
  printCurrentSettings();

  uint8_t buffer[48];
  memset(buffer, 0, sizeof(buffer));
  uint8_t received = 0;

  enterBus(currentBaudIndex, currentFormatIndex);
  const uint32_t startMs = millis();
  while ((millis() - startMs) < 5000UL) {
    while (Serial.available() > 0 && received < sizeof(buffer)) {
      buffer[received++] = static_cast<uint8_t>(Serial.read());
    }
  }
  enterConsole();

  Serial.print(F("Bytes em 5 s: "));
  Serial.println(received);
  if (received > 0) {
    dumpBytes(buffer, received);
    Serial.println();
  }
}

/*
 * Teste 7 -- variacoes de protocolo.
 *
 * Ate aqui todo pedido foi identico: funcao 03H, UM registrador, no
 * endereco 0x0300. Se o drive rejeita justamente essa forma, silencio
 * absoluto e o sintoma, e nenhuma varredura de baud ou endereco jamais
 * encontraria o problema.
 *
 * O detalhe que motiva isto: toda pagina de parametro do manual lista
 * DOIS enderecos -- "P3-00 ... Address: 0300H, 0301H". Parametros Delta
 * ocupam dois registradores consecutivos. Ler um so pode ser leitura
 * parcial de um bloco de 32 bits, e ha implementacoes que respondem a
 * isso com excecao ou com nada.
 *
 * Uma EXCECAO aqui vale ouro: excecao com CRC valido prova que o drive
 * recebeu, entendeu e recusou. Ou seja, que ele nos ouve.
 */
void testProtocolVariants() {
  Serial.println();
  Serial.println(F("=== TESTE 7: variacoes de protocolo ==="));
  Serial.println(F("Mesmo baud e formato; muda a FORMA do pedido."));
  Serial.println(F("Qualquer resposta, inclusive excecao, prova que o"));
  Serial.println(F("drive nos ouve."));
  Serial.println();

  struct Variant {
    uint8_t function;
    uint16_t address;
    uint16_t count;
    const char *label;
  };

  const Variant variants[] = {
    {0x03, 0x0300, 1, "03H P3-00 x1  (o que sempre usamos)"},
    {0x03, 0x0300, 2, "03H P3-00 x2  (parametro completo, 32 bits)"},
    {0x03, 0x0000, 1, "03H P0-00 x1  (versao de firmware)"},
    {0x03, 0x0000, 2, "03H P0-00 x2"},
    {0x03, 0x0102, 2, "03H P1-01 x2  (modo de controle)"},
    {0x04, 0x0300, 2, "04H P3-00 x2  (input registers)"}
  };

  const uint8_t slaves[2] = {SLAVE_WILDCARD, 0x01};

  for (uint8_t v = 0; v < 6; ++v) {
    for (uint8_t k = 0; k < 2; ++k) {
      uint8_t buffer[48];
      memset(buffer, 0, sizeof(buffer));
      uint16_t value = 0;
      uint8_t answered = 0;

      enterBus(currentBaudIndex, currentFormatIndex);
      const uint8_t received = rawRead(slaves[k], variants[v].address,
                                       variants[v].count, buffer,
                                       sizeof(buffer), WINDOW_SCAN_MS,
                                       variants[v].function);
      const uint8_t verdict = classifyReply(buffer, received, slaves[k],
                                            &value, &answered,
                                            variants[v].function);
      const uint8_t dumpLength = (received > 12) ? 12 : received;
      uint8_t dump[12];
      memcpy(dump, buffer, dumpLength);
      enterConsole();

      Serial.print(F("  0x"));
      printHex8(slaves[k]);
      Serial.print(F("  "));
      printPadded(variants[v].label, 40);
      Serial.print(F(" -> "));

      switch (verdict) {
        case 0: Serial.println(F("silencio")); break;
        case 1: Serial.println(F("so eco")); break;
        case 2:
          Serial.print(F("RESPOSTA! valor 0x"));
          printHex16(value);
          Serial.println();
          break;
        case 4:
          Serial.print(F("EXCECAO 0x"));
          printHex8(static_cast<uint8_t>(value));
          Serial.println(F("  -- O DRIVE NOS OUVE."));
          break;
        default:
          Serial.print(F("lixo: "));
          dumpBytes(dump, dumpLength);
          Serial.println();
          break;
      }
    }
  }

  Serial.println();
  Serial.println(F("Se TUDO calou, a forma do pedido nao e a causa."));
}

// -------------------- Menu --------------------

void printMenu() {
  Serial.println();
  Serial.println(F("------------------------------------------------------------"));
  Serial.println(F(" ASDA-B2 Modbus RTU - UNO, UART de HARDWARE"));
  Serial.println(F(" Somente leitura (03H). Nada e escrito no drive."));
  Serial.println(F(" TX=D1, RX=D0.  SOLTE O D0 ANTES DE CADA UPLOAD."));
  Serial.println(F("------------------------------------------------------------"));
  printCurrentSettings();
  Serial.println();
  Serial.println(F(" 1 - eco / laco (agora conclusivo: full-duplex)"));
  Serial.println(F(" 2 - varrer enderecos 0x01..0x7F"));
  Serial.println(F(" 3 - varrer 6 bauds x 3 formatos em 0xFF, 0x01, 0x7F"));
  Serial.println(F(" 4 - sondagem repetida"));
  Serial.println(F(" 5 - ler os parametros P3-xx"));
  Serial.println(F(" 6 - escuta passiva 5 s (nao transmite)"));
  Serial.println(F(" 7 - variacoes de protocolo (1 vs 2 registradores, 04H)"));
  Serial.println(F(" b - proximo baud"));
  Serial.println(F(" f - proximo formato (8N2 / 8E1 / 8O1)"));
  Serial.println(F(" a - proximo endereco"));
  Serial.println(F(" w - curinga 0xFF"));
  Serial.println(F(" v - despejo de bytes on/off"));
  Serial.println(F(" m - menu"));
  Serial.println(F("------------------------------------------------------------"));
}

void handleCommand(char command) {
  switch (command) {
    case '1': testEcho(); break;
    case '2': scanAddresses(); break;
    case '3': scanBaudAndFormat(); break;
    case '4': repeatedProbe(); break;
    case '5': dumpKeyParameters(); break;
    case '6': passiveListen(); break;
    case '7': testProtocolVariants(); break;

    case 'b':
    case 'B':
      currentBaudIndex = (currentBaudIndex + 1) % BAUD_COUNT;
      printCurrentSettings();
      break;

    case 'f':
    case 'F':
      currentFormatIndex = (currentFormatIndex + 1) % FORMAT_COUNT;
      printCurrentSettings();
      break;

    case 'a':
    case 'A':
      if (currentSlave == SLAVE_WILDCARD) {
        currentSlave = 0x01;
      } else if (currentSlave >= 0x7F) {
        currentSlave = SLAVE_WILDCARD;
      } else {
        currentSlave = static_cast<uint8_t>(currentSlave + 1);
      }
      printCurrentSettings();
      break;

    case 'w':
    case 'W':
      currentSlave = SLAVE_WILDCARD;
      printCurrentSettings();
      break;

    case 'v':
    case 'V':
      verboseDump = !verboseDump;
      Serial.print(F("Despejo: "));
      Serial.println(verboseDump ? F("ligado") : F("desligado"));
      break;

    case 'm':
    case 'M':
      printMenu();
      break;

    default:
      break;
  }
}

void setup() {
  if (DE_PIN >= 0) {
    pinMode(DE_PIN, OUTPUT);
    digitalWrite(DE_PIN, LOW);  // repouso = recepcao
  }

  holdTxIdle();
  Serial.begin(CONSOLE_BAUD, SERIAL_8N1);
  delay(300);

  const uint8_t testFrame[] = {0x01, 0x03, 0x02, 0x00, 0x00, 0x02};
  const bool crcOk = (modbusCrc16(testFrame, sizeof(testFrame)) == 0xB3C5U);

  printMenu();

  Serial.print(F("Autoteste CRC-16: "));
  Serial.println(crcOk ? F("OK") : F("FALHOU"));
  Serial.println();
  Serial.println(F("Sugestao: teste 1 com os pinos 2 e 3 do DB9 em curto,"));
  Serial.println(F("fora do CN3. Deve dar ECO LIMPO -- isso valida a UART,"));
  Serial.println(F("o formato e o conversor de uma vez. So depois ligue no"));
  Serial.println(F("CN3 e rode o teste 3."));
}

void loop() {
  while (Serial.available() > 0) {
    handleCommand(static_cast<char>(Serial.read()));
  }
  delay(10);
}
