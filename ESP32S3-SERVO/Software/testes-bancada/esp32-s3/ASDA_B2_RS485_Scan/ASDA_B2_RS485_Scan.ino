#include <Arduino.h>

/*
 * Diagnostico da camada fisica RS-485 do Delta ASDA-B2.
 *
 * Este sketch NAO assume nada: nem endereco, nem baud, nem formato.
 * Ele existe para descobrir onde a comunicacao esta parando.
 *
 * Nunca escreve parametro nem comanda o motor: so usa a funcao 03H.
 *
 * ------------------------------------------------------------------
 * ATENCAO AOS PINOS
 * ------------------------------------------------------------------
 * UART0 (GPIO43/44) = console, pela entrada COM da placa.
 * UART1 (GPIO17/18) = barramento RS-485.
 *
 * NAO use GPIO43/GPIO44 para o RS-485. Eles sao U0TXD/U0RXD e, na maioria
 * das placas de desenvolvimento, estao ligados fisicamente ao chip
 * USB-serial (CH340/CP2102) da propria placa. Esse chip fica alimentado
 * junto com a placa e mantem a saida dele empurrando o GPIO44, brigando
 * com o TXD do HW-519. Resultado tipico: o ESP transmite (LED RXD do
 * modulo pisca) e nunca recebe nada de volta.
 *
 * Use GPIOs livres. No ESP32-S3 evite: 0, 3, 45, 46 (strapping),
 * 19/20 (USB nativo), 26..32 (flash SPI) e 33..37 em modulos com
 * PSRAM octal (N8R8/N16R8).
 *
 * ------------------------------------------------------------------
 * LIGACOES RS-485
 * ------------------------------------------------------------------
 *   HW-519 A+   -> CN3-5 RS-485(+)
 *   HW-519 B-   -> CN3-6 RS-485(-)
 *   HW-519 GND  -> CN3-1 GND
 *   HW-519 R0   -> nao conectar (terminador de 120 ohm)
 */

constexpr int8_t RS485_TX_PIN = 17;  // ESP32 TX  -> RXD do HW-519
constexpr int8_t RS485_RX_PIN = 18;  // ESP32 RX  <- TXD do HW-519

constexpr uint32_t USB_SERIAL_BAUD = 115200;

HardwareSerial rs485Serial(1);

// -------------------- Combinacoes varridas --------------------

struct SerialFormat {
  const char *name;
  uint32_t config;
};

// Ordem espelha a tabela de P3-02 do manual (0..8).
constexpr SerialFormat SERIAL_FORMATS[] = {
  {"8N2", SERIAL_8N2},  // P3-02 = 6 (RTU) -- padrao de fabrica
  {"8E1", SERIAL_8E1},  // P3-02 = 7 (RTU)
  {"8O1", SERIAL_8O1}   // P3-02 = 8 (RTU)
};

constexpr uint32_t BAUD_RATES[] = {38400, 9600, 19200, 115200, 57600, 4800};

constexpr size_t FORMAT_COUNT = sizeof(SERIAL_FORMATS) / sizeof(SERIAL_FORMATS[0]);
constexpr size_t BAUD_COUNT = sizeof(BAUD_RATES) / sizeof(BAUD_RATES[0]);

// Registrador consultado na varredura: P3-00 (Address Setting).
// Toda unidade ASDA-B2 responde a ele, e o valor lido confirma o
// endereco em que o drive esta configurado.
constexpr uint16_t REG_P3_00 = 0x0300;

// Endereco curinga. Secao 8.2 do manual, na descricao do P3-00:
// "When the communication address setting of MODBUS is set to 0xFF, the
//  servo drive will automatically reply and receive data regardless of
//  the address."
// Ou seja: o drive atende ao pedido em 0xFF seja qual for o P3-00 dele.
// Isso elimina o endereco como incognita da varredura. O manual nao diz
// com que endereco ele responde, entao a classificacao aceita qualquer um.
constexpr uint8_t SLAVE_WILDCARD = 0xFF;

// -------------------- Estado corrente --------------------

// Copia do ultimo pedido transmitido, para reconhecer o eco.
uint8_t lastRequest[8] = {0};
size_t lastRequestLength = 0;

size_t currentBaudIndex = 0;
size_t currentFormatIndex = 0;
uint8_t currentSlave = 1;
bool verboseDump = true;

// -------------------- Utilitarios --------------------

uint16_t modbusCrc16(const uint8_t *data, size_t length) {
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

void applyUartSettings() {
  rs485Serial.end();
  delay(20);
  rs485Serial.begin(
    BAUD_RATES[currentBaudIndex],
    SERIAL_FORMATS[currentFormatIndex].config,
    RS485_RX_PIN,
    RS485_TX_PIN
  );
  delay(50);
  while (rs485Serial.available() > 0) {
    (void)rs485Serial.read();
  }
}

void printCurrentSettings() {
  Serial.printf(
    "UART: %lu baud, %s, TX=GPIO%d, RX=GPIO%d, slave=%u (0x%02X)\n",
    static_cast<unsigned long>(BAUD_RATES[currentBaudIndex]),
    SERIAL_FORMATS[currentFormatIndex].name,
    RS485_TX_PIN,
    RS485_RX_PIN,
    currentSlave,
    currentSlave
  );
  if (currentSlave == SLAVE_WILDCARD) {
    Serial.println(F("  (0xFF = curinga: responde qualquer que seja o P3-00)"));
  }
}

void dumpBytes(const uint8_t *data, size_t length) {
  for (size_t index = 0; index < length; ++index) {
    Serial.printf("%02X ", data[index]);
  }
}

// -------------------- Transacao crua --------------------

/*
 * Envia uma leitura 03H e devolve TUDO que chegou dentro da janela de
 * tempo, sem filtrar eco, sem descartar lixo. O objetivo aqui e ver os
 * bytes, nao interpreta-los.
 */
size_t rawRead03(uint8_t slave,
                 uint16_t startAddress,
                 uint16_t registerCount,
                 uint8_t *buffer,
                 size_t bufferSize,
                 uint32_t windowMs) {
  uint8_t request[8] = {
    slave,
    0x03,
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

  while (rs485Serial.available() > 0) {
    (void)rs485Serial.read();
  }
  delay(5);

  rs485Serial.write(request, sizeof(request));
  rs485Serial.flush();

  size_t received = 0;
  const uint32_t startMs = millis();

  while ((millis() - startMs) < windowMs) {
    while (rs485Serial.available() > 0 && received < bufferSize) {
      buffer[received++] = static_cast<uint8_t>(rs485Serial.read());
    }
    delay(1);
  }

  return received;
}

/*
 * Classifica o que voltou:
 *   0 = nada
 *   1 = so o eco do proprio pedido
 *   2 = resposta valida do slave (CRC confere)
 *   3 = chegaram bytes, mas nao formam resposta valida
 *
 * Com slave == SLAVE_WILDCARD qualquer byte de endereco e aceito na
 * resposta; o endereco de quem atendeu sai em foundSlave.
 */
uint8_t classifyReply(const uint8_t *buffer,
                      size_t length,
                      uint8_t slave,
                      uint16_t *firstRegister,
                      uint8_t *foundSlave = nullptr) {
  if (length == 0) {
    return 0;
  }

  /*
   * O eco tem de ser descartado ANTES da busca por quadro, ou vira falso
   * positivo. Motivo: o pedido "03H, 1 registrador" tem 8 bytes e, lido
   * como se fosse resposta, da endereco = o mesmo, funcao = 0x03,
   * byte count = 0x03 -> quadro de 8 bytes cujo CRC nos bytes 6 e 7 e
   * exatamente o CRC do proprio pedido. Confere sempre. Sem este
   * descarte, um modulo que ecoa reportaria "RESPOSTA P3-00=0x0000".
   */
  size_t searchStart = 0;
  if (lastRequestLength > 0 && length >= lastRequestLength &&
      memcmp(buffer, lastRequest, lastRequestLength) == 0) {
    if (length == lastRequestLength) {
      return 1;
    }
    searchStart = lastRequestLength;
  }

  // Procura um quadro valido em qualquer offset: se o modulo ecoa, a
  // resposta real vem depois dos 8 bytes do pedido.
  for (size_t offset = searchStart; offset + 5 <= length; ++offset) {
    if (slave != SLAVE_WILDCARD && buffer[offset] != slave) {
      continue;
    }

    const uint8_t function = buffer[offset + 1];

    if ((function & 0x80U) != 0U) {
      const size_t frameLength = 5;
      if (offset + frameLength > length) {
        continue;
      }
      const uint16_t got = static_cast<uint16_t>(buffer[offset + 3]) |
                           (static_cast<uint16_t>(buffer[offset + 4]) << 8U);
      if (got == modbusCrc16(&buffer[offset], 3)) {
        Serial.printf(" [EXCECAO 0x%02X]", buffer[offset + 2]);
        if (foundSlave != nullptr) {
          *foundSlave = buffer[offset];
        }
        return 2;
      }
      continue;
    }

    if (function != 0x03U) {
      continue;
    }

    const size_t frameLength = static_cast<size_t>(buffer[offset + 2]) + 5U;
    if (frameLength < 7 || offset + frameLength > length) {
      continue;
    }

    const uint16_t got = static_cast<uint16_t>(buffer[offset + frameLength - 2]) |
                         (static_cast<uint16_t>(buffer[offset + frameLength - 1]) << 8U);
    if (got != modbusCrc16(&buffer[offset], frameLength - 2)) {
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

void testEchoPath() {
  Serial.println();
  Serial.println(F("=== TESTE 1: caminho TTL <-> modulo ==="));
  Serial.println(F("Desconecte A e B do drive. Deixe o HW-519 solto no barramento."));
  Serial.println(F("Muitos modulos com direcao automatica mantem o receptor ligado"));
  Serial.println(F("durante a transmissao, entao o proprio pedido volta como eco."));
  Serial.println();

  printCurrentSettings();

  uint8_t buffer[64] = {0};
  const size_t received = rawRead03(currentSlave, REG_P3_00, 1, buffer, sizeof(buffer), 200);

  Serial.printf("Enviados 8 bytes, recebidos %u bytes: ", static_cast<unsigned>(received));
  dumpBytes(buffer, received);
  Serial.println();
  Serial.println();

  if (received == 0) {
    Serial.println(F("RESULTADO: nenhum byte chegou ao GPIO de RX."));
    Serial.println(F("  Se o LED RXD do modulo pisca, o ESP esta transmitindo."));
    Serial.println(F("  Entao o problema esta entre o TXD do modulo e o RX do ESP:"));
    Serial.println(F("    - GPIO43/44 em conflito com o chip USB-serial da placa;"));
    Serial.println(F("    - TXD do modulo nao ligado, ou ligado no pino errado;"));
    Serial.println(F("    - modulo sem alimentacao estavel em 3V3;"));
    Serial.println(F("    - modulo simplesmente nao ecoa (alguns nao ecoam)."));
    Serial.println(F("  Se este modulo nao ecoa, este teste e inconclusivo:"));
    Serial.println(F("  faca um curto entre A e B de dois modulos, ou use o teste 2."));
  } else {
    Serial.println(F("RESULTADO: bytes chegaram no RX. O caminho ESP<->modulo funciona."));
    Serial.println(F("Se forem exatamente os 8 bytes enviados, e eco: o driver"));
    Serial.println(F("do RS-485 esta chaveando e o receptor esta ativo. Otimo sinal."));
  }
}

void scanAddresses() {
  Serial.println();
  Serial.println(F("=== TESTE 2: varredura de enderecos 0x01..0x7F ==="));
  printCurrentSettings();
  Serial.println(F("Lendo P3-00 (0x0300) em cada endereco..."));
  Serial.println();

  uint8_t found = 0;

  for (uint8_t slave = 0x01; slave <= 0x7F; ++slave) {
    uint8_t buffer[64] = {0};
    uint16_t value = 0;
    const size_t received = rawRead03(slave, REG_P3_00, 1, buffer, sizeof(buffer), 80);
    const uint8_t verdict = classifyReply(buffer, received, slave, &value);

    if (verdict == 2) {
      Serial.printf(
        ">>> RESPOSTA no endereco %u (0x%02X). P3-00 lido = 0x%04X. Bytes: ",
        slave, slave, value
      );
      dumpBytes(buffer, received);
      Serial.println();
      ++found;
      currentSlave = slave;
    } else if (verdict == 3 && verboseDump) {
      Serial.printf("    ruido no endereco %u (0x%02X): ", slave, slave);
      dumpBytes(buffer, received);
      Serial.println();
    }

    if ((slave % 16) == 0) {
      Serial.printf("    ... %u/127\n", slave);
    }
  }

  Serial.println();
  if (found == 0) {
    Serial.println(F("Nenhum drive respondeu nesta combinacao de baud/formato."));
    Serial.println(F("Rode o teste 3 antes de mexer na fiacao."));
  } else {
    Serial.printf("Drives encontrados: %u. Endereco corrente ajustado.\n", found);
  }
}

void scanBaudAndFormat() {
  Serial.println();
  Serial.println(F("=== TESTE 3: varredura de baud x formato ==="));
  Serial.println(F("Sonda cada combinacao em tres enderecos:"));
  Serial.println(F("  0xFF = curinga - o drive atende seja qual for o P3-00"));
  Serial.println(F("  0x01 = alvo deste projeto"));
  Serial.println(F("  0x7F = padrao de fabrica do P3-00"));
  Serial.println(F("Se 0xFF calar em tudo, o endereco esta descartado como causa."));
  Serial.println();

  const uint8_t candidates[] = {SLAVE_WILDCARD, 0x01, 0x7F};
  const size_t savedBaud = currentBaudIndex;
  const size_t savedFormat = currentFormatIndex;
  bool found = false;

  for (size_t baudIndex = 0; baudIndex < BAUD_COUNT && !found; ++baudIndex) {
    for (size_t formatIndex = 0; formatIndex < FORMAT_COUNT && !found; ++formatIndex) {
      currentBaudIndex = baudIndex;
      currentFormatIndex = formatIndex;
      applyUartSettings();

      Serial.printf(
        "%-7lu %-4s : ",
        static_cast<unsigned long>(BAUD_RATES[baudIndex]),
        SERIAL_FORMATS[formatIndex].name
      );

      for (uint8_t slave : candidates) {
        uint8_t buffer[64] = {0};
        uint16_t value = 0;
        uint8_t answered = 0;
        const size_t received = rawRead03(slave, REG_P3_00, 1, buffer, sizeof(buffer), 120);
        const uint8_t verdict = classifyReply(buffer, received, slave, &value, &answered);

        Serial.printf("0x%02X=", slave);
        switch (verdict) {
          case 0: Serial.print(F("silencio  ")); break;
          case 1: Serial.print(F("so-eco    ")); break;
          case 2:
            // O valor lido de P3-00 e o endereco real do drive.
            Serial.printf("RESPOSTA(atendeu 0x%02X, P3-00=0x%04X)  ", answered, value);
            currentSlave = (value >= 0x01U && value <= 0x7FU)
                             ? static_cast<uint8_t>(value)
                             : answered;
            found = true;
            break;
          default: Serial.print(F("lixo      ")); break;
        }
      }

      Serial.println();
    }
  }

  if (!found) {
    currentBaudIndex = savedBaud;
    currentFormatIndex = savedFormat;
    applyUartSettings();
    Serial.println();
    Serial.println(F("Nenhuma combinacao respondeu."));
    Serial.println(F("Isso empurra a suspeita para a camada fisica ou para o P3-05:"));
    Serial.println(F("  - P3-05 de fabrica vem 1 (RS-232 via ASDA-Soft). Precisa ser 0."));
    Serial.println(F("  - CN3 pino 1 (GND) precisa estar ligado ao GND do HW-519."));
    Serial.println(F("  - Contato mecanico do conector CN3."));
    Serial.println(F("  - Polaridade A/B invertida (rode o teste 4)."));
  } else {
    Serial.println();
    Serial.println(F("Combinacao encontrada e mantida. Anote-a."));
    printCurrentSettings();
  }
}

void repeatedProbe() {
  Serial.println();
  Serial.println(F("=== TESTE 4: sondagem repetida (para inverter A/B ao vivo) ==="));
  Serial.println(F("Envie qualquer tecla para parar."));
  printCurrentSettings();
  Serial.println();

  while (Serial.available() > 0) {
    (void)Serial.read();
  }

  while (Serial.available() == 0) {
    uint8_t buffer[64] = {0};
    uint16_t value = 0;
    const size_t received = rawRead03(currentSlave, REG_P3_00, 1, buffer, sizeof(buffer), 150);
    const uint8_t verdict = classifyReply(buffer, received, currentSlave, &value);

    Serial.printf("%-9lu ", static_cast<unsigned long>(millis()));
    switch (verdict) {
      case 0: Serial.println(F("silencio")); break;
      case 1: Serial.println(F("so eco do proprio pedido")); break;
      case 2: Serial.printf("RESPOSTA  P3-00=0x%04X\n", value); break;
      default:
        Serial.print(F("bytes sem quadro valido: "));
        dumpBytes(buffer, received);
        Serial.println();
        break;
    }

    delay(400);
  }

  while (Serial.available() > 0) {
    (void)Serial.read();
  }
  Serial.println(F("Sondagem interrompida."));
}

void dumpKeyParameters() {
  Serial.println();
  Serial.println(F("=== TESTE 5: leitura dos parametros de comunicacao ==="));
  printCurrentSettings();
  Serial.println();

  struct Param {
    const char *name;
    uint16_t address;
    const char *note;
  };

  const Param params[] = {
    {"P3-00", 0x0300, "endereco Modbus (fabrica 0x007F)"},
    {"P3-01", 0x0302, "baud, digito RS-485 e o de dezena (fabrica 0x0033)"},
    {"P3-02", 0x0304, "formato/protocolo (fabrica 0x0066 = 8N2 RTU)"},
    {"P3-05", 0x030A, "mecanismo: precisa ser 0 (fabrica 1)"},
    {"P3-07", 0x030E, "atraso de resposta (x1 ms)"},
    {"P1-01", 0x0102, "modo de controle - NAO ALTERAR"},
    {"P3-06", 0x030C, "origem das DI - NAO ALTERAR"}
  };

  for (const Param &param : params) {
    uint8_t buffer[64] = {0};
    uint16_t value = 0;
    const size_t received = rawRead03(currentSlave, param.address, 1, buffer, sizeof(buffer), 150);
    const uint8_t verdict = classifyReply(buffer, received, currentSlave, &value);

    Serial.printf("%-6s @0x%04X = ", param.name, param.address);
    if (verdict == 2) {
      Serial.printf("0x%04X", value);
    } else {
      Serial.print(F("  ----"));
    }
    Serial.printf("   %s\n", param.note);
  }
}

// -------------------- Menu --------------------

void printMenu() {
  Serial.println();
  Serial.println(F("------------------------------------------------------------"));
  Serial.println(F(" Diagnostico RS-485 / Modbus RTU - Delta ASDA-B2"));
  Serial.println(F(" Somente leitura (funcao 03H). Nada e escrito no drive."));
  Serial.println(F("------------------------------------------------------------"));
  printCurrentSettings();
  Serial.println();
  Serial.println(F(" 1 - teste de eco (A/B desconectados do drive)"));
  Serial.println(F(" 2 - varrer enderecos 0x01..0x7F no baud/formato atual"));
  Serial.println(F(" 3 - varrer baud x formato nos enderecos 0xFF, 0x01 e 0x7F"));
  Serial.println(F(" 4 - sondagem repetida (util para inverter A/B ao vivo)"));
  Serial.println(F(" 5 - ler os parametros P3-xx do drive encontrado"));
  Serial.println(F(" b - proximo baud rate"));
  Serial.println(F(" f - proximo formato (8N2 / 8E1 / 8O1)"));
  Serial.println(F(" a - proximo endereco de slave"));
  Serial.println(F(" w - endereco curinga 0xFF (dispensa saber o P3-00)"));
  Serial.println(F(" v - liga/desliga o despejo de bytes na varredura"));
  Serial.println(F(" m - mostrar este menu"));
  Serial.println(F("------------------------------------------------------------"));
}

void handleCommand(char command) {
  switch (command) {
    case '1': testEchoPath(); break;
    case '2': scanAddresses(); break;
    case '3': scanBaudAndFormat(); break;
    case '4': repeatedProbe(); break;
    case '5': dumpKeyParameters(); break;

    case 'b':
    case 'B':
      currentBaudIndex = (currentBaudIndex + 1) % BAUD_COUNT;
      applyUartSettings();
      printCurrentSettings();
      break;

    case 'f':
    case 'F':
      currentFormatIndex = (currentFormatIndex + 1) % FORMAT_COUNT;
      applyUartSettings();
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
      Serial.printf("Despejo de bytes: %s\n", verboseDump ? "ligado" : "desligado");
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
  Serial.begin(USB_SERIAL_BAUD);

  const uint32_t waitStartMs = millis();
  while (!Serial && (millis() - waitStartMs) < 3000U) {
    delay(10);
  }

  // Autoteste do CRC com o exemplo do manual do ASDA-B2.
  const uint8_t testFrame[] = {0x01, 0x03, 0x02, 0x00, 0x00, 0x02};
  const bool crcOk = (modbusCrc16(testFrame, sizeof(testFrame)) == 0xB3C5U);

  applyUartSettings();
  printMenu();

  Serial.print(F("Autoteste CRC-16: "));
  Serial.println(crcOk ? F("OK") : F("FALHOU"));
  Serial.println(F("Sugestao: comece pelo teste 3."));
  Serial.println(F("Se der silencio ate em 0xFF, o problema nao e endereco:"));
  Serial.println(F("va para o painel do drive (P3-05) e para o teste 1."));
}

void loop() {
  while (Serial.available() > 0) {
    handleCommand(static_cast<char>(Serial.read()));
  }
  delay(10);
}
