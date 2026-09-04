#include <Arduino.h>
#include <SoftwareSerial.h>

/*
 * Diagnostico da camada fisica RS-485 do Delta ASDA-B2 -- Arduino UNO.
 *
 * Segundo mestre, independente do ESP32-S3: silicio diferente, logica
 * de 5 V, biblioteca serial diferente. Serve para responder "o mestre
 * e o problema?" sem depender de nada que ja foi testado.
 *
 * Somente leitura: o unico codigo Modbus transmitido e o 03H. Nunca
 * escreve parametro, nunca habilita o servo, nunca comanda o motor.
 *
 * ------------------------------------------------------------------
 * LIGACOES (as que voce ja fez)
 * ------------------------------------------------------------------
 *   Arduino D10 ---> RXD do HW-519      D10 e a SAIDA do Arduino
 *   Arduino D11 <--- TXD do HW-519      D11 e a ENTRADA do Arduino
 *   Arduino 5V  ---> VCC do HW-519
 *   Arduino GND ---> GND do HW-519 (lado TTL)
 *
 *   HW-519 A+   -> CN3-5 RS-485(+)
 *   HW-519 B-   -> CN3-6 RS-485(-)
 *   HW-519 R0   -> nao conectar (terminador de 120 ohm)
 *
 * Por isso a construcao e SoftwareSerial(11, 10): a assinatura e
 * (rxPin, txPin), e o pino que vai ao RXD do modulo e o TX daqui.
 *
 * O modulo vai em 5 V, nao em 3,3 V. E de proposito: e a unica
 * variavel fisica que muda em relacao ao ESP32-S3. Se o UNO conversar
 * e o ESP32 nao, a suspeita cai sobre a excursao diferencial do driver
 * alimentado em 3,3 V.
 *
 * AO VOLTAR PARA O ESP32-S3, DEVOLVA O VCC DO MODULO PARA 3V3. Com o
 * modulo em 5 V o TXD dele entrega 5 V, e o GPIO do ESP32 e 3,3 V.
 *
 * ------------------------------------------------------------------
 * OS DOIS GNDs DO MODULO NAO APITAM NA CONTINUIDADE
 * ------------------------------------------------------------------
 * Duas placas diferentes deram o mesmo resultado, entao nao e defeito:
 * e projeto. Isso derruba a secao 3.1 do documento de setup, que
 * afirmava "os dois pinos sao o mesmo net, tem de bipar".
 *
 * A explicacao mais provavel e a mais banal: o borne do lado RS-485
 * chega ao terra logico atraves de um RESISTOR EM SERIE, tipicamente
 * de 100 ohm. Nao e gambiarra de fabricante -- e o que a propria
 * TIA/EIA-485 recomenda para ligar terras de sinal entre nos, limitando
 * a corrente de circulacao. Um apito de continuidade dispara em torno
 * de 50 ohm, entao ele fica mudo num resistor de 100 ohm sem que haja
 * nada de errado.
 *
 * MEDIR EM OHMS, NAO NO APITO. Entre o GND do lado TTL e o borne do
 * lado RS-485:
 *
 *   0 a 5 ohm       mesmo cobre. Era o que o documento supunha.
 *   100 a 1000 ohm  resistor em serie. Normal. Use o borne como estava.
 *   OL / megaohms   ou o modulo e isolado -- procure um bloco DC-DC
 *                   preto marcado B0505S e um rasgo fresado no meio da
 *                   placa -- ou aquele borne simplesmente nao e GND.
 *
 * E MEDIR TAMBEM O BORNE CONTRA O VCC DO LADO TTL. Se der perto de
 * zero, o borne e VCC, nao GND: ligado ao CN3-1 ele estaria injetando
 * 5 V no terra de sinal do drive. Desligue antes de energizar de novo.
 *
 * Enquanto a duvida existir, a referencia mais confiavel e o GND do
 * lado TTL, o mesmo pino em que esta o GND do Arduino: leve o CN3-1
 * ate la. Sem referencia comum o modo comum entre as duas pontas fica
 * solto, o receptor sai da janela de entrada, e o sintoma e
 * exatamente este -- silencio absoluto com A e B corretos.
 *
 * ------------------------------------------------------------------
 * A LIMITACAO DA SoftwareSerial, E COMO ELA E CONTORNADA AQUI
 * ------------------------------------------------------------------
 * A SoftwareSerial transmite exclusivamente em 8N1: nao gera segundo
 * stop bit nem bit de paridade. O ASDA-B2 so fala RTU em 8N2, 8E1 ou
 * 8O1 (P3-02 = 6, 7 ou 8), e de fabrica vem em 8N2.
 *
 * 8N2 da para produzir: veja writeFrameAs8N2() abaixo. 8E1 e 8O1 nao
 * dao, de jeito nenhum. Se o painel mostrar o digito RS-485 do P3-02
 * em 7 ou 8, mude-o para 6 pelo painel -- e parametro de comunicacao,
 * seguro de mexer, ao contrario do P1-01 e do P3-06.
 *
 * A SoftwareSerial tambem desliga as interrupcoes enquanto transmite,
 * entao ela e surda durante a propria transmissao. Por isso aqui nao
 * existe teste de eco: no lugar dele ficam o teste 1 (transmissao
 * continua, para ver LED e medir A-B com o multimetro) e o teste 6
 * (escuta passiva). Acima de 38400 a SoftwareSerial fica instavel no
 * UNO, por isso a lista de bauds para em 38400.
 */

// -------------------- Barramento --------------------

// (rxPin, txPin) -- D11 recebe do TXD do modulo, D10 alimenta o RXD.
SoftwareSerial rs485(11, 10);

constexpr uint32_t CONSOLE_BAUD = 115200;

// A SoftwareSerial nao e confiavel acima de 38400 num UNO de 16 MHz.
// 9600 primeiro: lido no painel em 31/08/2026, P3-01 = 0x0011, cujo
// digito das dezenas (RS-485) vale 1 = 9600. Nao e o padrao de fabrica
// (0x0033 = 38400): este drive ja foi reconfigurado por alguem.
const uint32_t BAUD_RATES[] = {9600, 38400, 19200, 4800};
constexpr uint8_t BAUD_COUNT = sizeof(BAUD_RATES) / sizeof(BAUD_RATES[0]);

// Registrador de sondagem: P3-00 (Address Setting).
constexpr uint16_t REG_P3_00 = 0x0300;

/*
 * Endereco curinga. Secao 8.2 do manual, na descricao do P3-00:
 * "When the communication address setting of MODBUS is set to 0xFF, the
 *  servo drive will automatically reply and receive data regardless of
 *  the address."
 * Com ele o endereco deixa de ser incognita. O manual nao diz com que
 * endereco o drive responde, entao a classificacao aceita qualquer um.
 */
constexpr uint8_t SLAVE_WILDCARD = 0xFF;

// -------------------- Estado corrente --------------------

uint8_t currentBaudIndex = 0;
// P3-00 = 0x0001 lido no painel. O curinga fica como padrao mesmo
// assim: custa nada e cobre o caso de o painel ter sido mal lido.
uint8_t currentSlave = SLAVE_WILDCARD;
uint32_t activeBaud = 9600;
bool verboseDump = true;

// Copia do ultimo pedido transmitido, para reconhecer o eco.
uint8_t lastRequest[8];
uint8_t lastRequestLength = 0;

// -------------------- Impressao (o UNO nao tem printf) --------------------

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

void printBaudPadded(uint32_t baud) {
  Serial.print(baud);
  const uint8_t digits = (baud >= 10000UL) ? 5 : 4;
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

// -------------------- Barramento --------------------

void applyBus(uint8_t baudIndex) {
  activeBaud = BAUD_RATES[baudIndex];
  rs485.end();
  delay(5);
  rs485.begin(activeBaud);
  rs485.listen();
  delay(10);
  while (rs485.available() > 0) {
    (void)rs485.read();
  }
}

void printCurrentSettings() {
  Serial.print(F("Barramento: "));
  Serial.print(BAUD_RATES[currentBaudIndex]);
  Serial.print(F(" baud, 8N2 emulado, TX=D10, RX=D11, slave=0x"));
  printHex8(currentSlave);
  Serial.println();
  if (currentSlave == SLAVE_WILDCARD) {
    Serial.println(F("  (0xFF = curinga: responde qualquer que seja o P3-00)"));
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

// -------------------- Transmissao 8N2 sobre uma UART 8N1 --------------------

/*
 * A SoftwareSerial so emite 8N1. Um receptor em 8N2 procura o segundo
 * stop bit exatamente na posicao onde, num fluxo RTU de bytes colados,
 * chegaria o start bit do byte seguinte: le nivel baixo, marca erro de
 * enquadramento e descarta o quadro inteiro.
 *
 * A correcao e uma pausa de dois tempos de bit com a linha em repouso
 * (nivel alto) depois de cada byte. O receptor em 8N2 le esse tempo
 * ocioso como o segundo stop bit, e a pausa fica muito abaixo do
 * limite de 1,5 caractere que encerraria o quadro RTU:
 *
 *   38400 baud -> pausa de 52 us, limite de quadro em ~430 us
 *    4800 baud -> pausa de 416 us, limite de quadro em ~3400 us
 *
 * De quebra, um receptor em 8N1 tambem aceita: para ele a pausa e so
 * tempo ocioso entre bytes.
 */
void writeFrameAs8N2(const uint8_t *data, uint8_t length) {
  const uint16_t twoBitTimesUs = static_cast<uint16_t>(2000000UL / activeBaud);

  for (uint8_t index = 0; index < length; ++index) {
    rs485.write(data[index]);
    delayMicroseconds(twoBitTimesUs);
  }
}

// -------------------- Transacao crua --------------------

/*
 * Envia uma leitura 03H e devolve TUDO que chegou dentro da janela de
 * tempo, sem filtrar eco, sem descartar lixo.
 */
uint8_t rawRead03(uint8_t slave,
                  uint16_t startAddress,
                  uint16_t registerCount,
                  uint8_t *buffer,
                  uint8_t bufferSize,
                  uint16_t windowMs) {
  /*
   * Sobre windowMs: o P3-07 do drive e o "Communication Response Delay
   * Time", em milissegundos. De fabrica vale 0, mas este drive ja foi
   * reconfigurado -- o P3-01 provou isso. Com um P3-07 alto o drive
   * responde, so que depois da janela, e o resultado e indistinguivel
   * de silencio. Por isso as janelas aqui sao folgadas.
   */
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

  while (rs485.available() > 0) {
    (void)rs485.read();
  }

  // O RTU exige 3,5 tempos de caractere de silencio antes do quadro.
  // Em 4800 baud isso da 8 ms; os 5 ms de antes nao cobriam. 20 ms cobre
  // todos os bauds da lista com folga e nao custa nada numa sondagem.
  delay(20);

  writeFrameAs8N2(request, sizeof(request));

  uint8_t received = 0;
  const uint32_t startMs = millis();

  while ((millis() - startMs) < windowMs) {
    while (rs485.available() > 0 && received < bufferSize) {
      buffer[received++] = static_cast<uint8_t>(rs485.read());
    }
  }

  return received;
}

/*
 * Classifica o que voltou:
 *   0 = nada
 *   1 = so o eco do proprio pedido
 *   2 = resposta valida do slave (CRC confere)
 *   3 = chegaram bytes, mas nao formam resposta valida
 *   4 = excecao Modbus com CRC valido (o codigo sai em firstRegister)
 *
 * Com slave == SLAVE_WILDCARD qualquer byte de endereco e aceito na
 * resposta; o endereco de quem atendeu sai em foundSlave.
 */
uint8_t classifyReply(const uint8_t *buffer,
                      uint8_t length,
                      uint8_t slave,
                      uint16_t *firstRegister,
                      uint8_t *foundSlave = nullptr) {
  if (length == 0) {
    return 0;
  }

  /*
   * O eco tem de ser descartado ANTES da busca por quadro, ou vira
   * falso positivo. Motivo: o pedido "03H, 1 registrador" tem 8 bytes
   * e, lido como se fosse resposta, da endereco = o mesmo, funcao =
   * 0x03, byte count = 0x03 -> quadro de 8 bytes cujo CRC nos bytes 6
   * e 7 e exatamente o CRC do proprio pedido. Confere sempre. Sem este
   * descarte, um eco viraria "RESPOSTA P3-00=0x0000".
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

    if (function != 0x03U) {
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
 * A SoftwareSerial e surda enquanto transmite, entao nao da para
 * detectar eco por software. O que sobra e melhor mesmo: transmitir
 * sem parar por 10 s e deixar voce medir o que sai do modulo.
 */
void testContinuousTx() {
  Serial.println();
  Serial.println(F("=== TESTE 1: transmissao continua por 10 s ==="));
  Serial.println(F("Nada e classificado aqui. Este teste e para o olho e"));
  Serial.println(F("para o multimetro. Enquanto roda, verifique:"));
  Serial.println();
  Serial.println(F("  LED do modulo   -> tem de piscar. Se nao piscar, o"));
  Serial.println(F("                     D10 nao chegou ao RXD do modulo."));
  Serial.println();
  Serial.println(F("  A+ contra B-    -> multimetro em tensao continua."));
  Serial.println(F("                     Em repouso: alguns decimos de volt."));
  Serial.println(F("                     Transmitindo: a leitura muda e"));
  Serial.println(F("                     oscila. Se ficar cravada em 0,000 V"));
  Serial.println(F("                     o driver nao esta chaveando."));
  Serial.println();
  Serial.println(F("  A+ contra GND   -> tem de ficar entre 0 e 5 V. Fora"));
  Serial.println(F("  B- contra GND      disso, ha problema de referencia:"));
  Serial.println(F("                     e o caso do GND do lado RS-485."));
  Serial.println();
  Serial.println(F("Comecando..."));

  applyBus(currentBaudIndex);

  uint8_t request[8] = {currentSlave, 0x03, 0x03, 0x00, 0x00, 0x01, 0, 0};
  const uint16_t crc = modbusCrc16(request, 6);
  request[6] = static_cast<uint8_t>(crc & 0x00FFU);
  request[7] = static_cast<uint8_t>(crc >> 8U);

  const uint32_t startMs = millis();
  uint16_t frames = 0;
  while ((millis() - startMs) < 10000UL) {
    writeFrameAs8N2(request, sizeof(request));
    ++frames;
    delay(20);
  }

  Serial.print(F("Fim. Quadros transmitidos: "));
  Serial.println(frames);
}

void scanAddresses() {
  Serial.println();
  Serial.println(F("=== TESTE 2: varredura de enderecos 0x01..0x7F ==="));
  printCurrentSettings();
  Serial.println(F("Lendo P3-00 (0x0300) em cada endereco. Aguarde ~45 s..."));
  Serial.println();

  uint8_t hitSlave[6];
  uint16_t hitValue[6];
  uint8_t hitCount = 0;
  uint8_t noiseCount = 0;

  applyBus(currentBaudIndex);

  for (uint8_t slave = 0x01; slave <= 0x7F; ++slave) {
    uint8_t buffer[40];
    memset(buffer, 0, sizeof(buffer));
    uint16_t value = 0;
    const uint8_t received = rawRead03(slave, REG_P3_00, 1,
                                       buffer, sizeof(buffer), 300);
    const uint8_t verdict = classifyReply(buffer, received, slave, &value);

    if ((verdict == 2 || verdict == 4) && hitCount < 6) {
      hitSlave[hitCount] = slave;
      hitValue[hitCount] = value;
      ++hitCount;
    } else if (verdict == 3) {
      ++noiseCount;
    }
  }

  for (uint8_t index = 0; index < hitCount; ++index) {
    Serial.print(F(">>> RESPOSTA no endereco 0x"));
    printHex8(hitSlave[index]);
    Serial.print(F(", P3-00 lido = 0x"));
    printHex16(hitValue[index]);
    Serial.println();
  }

  if (noiseCount > 0) {
    Serial.print(F("Enderecos que devolveram bytes sem quadro valido: "));
    Serial.println(noiseCount);
  }

  Serial.println();
  if (hitCount == 0) {
    Serial.println(F("Nenhum drive respondeu neste baud."));
    Serial.println(F("Rode o teste 3 antes de mexer na fiacao."));
  } else {
    currentSlave = hitSlave[0];
    Serial.println(F("Endereco corrente ajustado para o primeiro achado."));
  }
}

void scanBaud() {
  Serial.println();
  Serial.println(F("=== TESTE 3: varredura de baud ==="));
  Serial.println(F("Formato fixo em 8N2 -- a SoftwareSerial nao produz 8E1"));
  Serial.println(F("nem 8O1. Se o P3-02 estiver com o digito RS-485 em 7 ou"));
  Serial.println(F("8, corrija para 6 pelo painel antes de acreditar neste"));
  Serial.println(F("resultado."));
  Serial.println();
  Serial.println(F("Cada baud e sondado em tres enderecos:"));
  Serial.println(F("  0xFF = curinga - o drive atende seja qual for o P3-00"));
  Serial.println(F("  0x01 = alvo deste projeto"));
  Serial.println(F("  0x7F = padrao de fabrica do P3-00"));
  Serial.println(F("Se 0xFF calar em tudo, o endereco esta descartado."));
  Serial.println();

  const uint8_t candidates[3] = {SLAVE_WILDCARD, 0x01, 0x7F};
  const uint8_t savedBaud = currentBaudIndex;
  bool found = false;

  for (uint8_t baudIndex = 0; baudIndex < BAUD_COUNT && !found; ++baudIndex) {
    applyBus(baudIndex);

    printBaudPadded(BAUD_RATES[baudIndex]);
    Serial.print(F("8N2  : "));

    for (uint8_t index = 0; index < 3; ++index) {
      uint8_t buffer[40];
      memset(buffer, 0, sizeof(buffer));
      uint16_t value = 0;
      uint8_t answered = 0;
      const uint8_t received = rawRead03(candidates[index], REG_P3_00, 1,
                                         buffer, sizeof(buffer), 500);
      const uint8_t verdict = classifyReply(buffer, received, candidates[index],
                                            &value, &answered);

      Serial.print(F("0x"));
      printHex8(candidates[index]);
      Serial.print('=');

      switch (verdict) {
        case 0:
          Serial.print(F("silencio  "));
          break;
        case 1:
          Serial.print(F("so-eco    "));
          break;
        case 2:
          // O valor lido de P3-00 e o endereco real do drive.
          Serial.print(F("RESPOSTA(atendeu 0x"));
          printHex8(answered);
          Serial.print(F(", P3-00=0x"));
          printHex16(value);
          Serial.print(F(")  "));
          currentBaudIndex = baudIndex;
          currentSlave = (value >= 0x01U && value <= 0x7FU)
                           ? static_cast<uint8_t>(value)
                           : answered;
          found = true;
          break;
        case 4:
          Serial.print(F("EXCECAO(0x"));
          printHex8(static_cast<uint8_t>(value));
          Serial.print(F(")  "));
          currentBaudIndex = baudIndex;
          currentSlave = answered;
          found = true;
          break;
        default:
          Serial.print(F("lixo      "));
          break;
      }

      if (verdict == 3 && verboseDump) {
        Serial.println();
        Serial.print(F("        bytes: "));
        dumpBytes(buffer, (received > 16) ? 16 : received);
      }
    }

    Serial.println();
  }

  Serial.println();
  if (!found) {
    currentBaudIndex = savedBaud;
    applyBus(currentBaudIndex);
    Serial.println(F("Nenhum baud respondeu -- nem no endereco curinga."));
    Serial.println(F("Isso descarta baud e endereco. Sobram:"));
    Serial.println(F("  - referencia comum: leve o CN3-1 ao GND do lado TTL"));
    Serial.println(F("    (o mesmo pino do GND do Arduino) e repita."));
    Serial.println(F("  - P3-05 (fabrica = 1). O manual lista P3-00, P3-01,"));
    Serial.println(F("    P3-02 e P3-05 como essenciais para a comunicacao."));
    Serial.println(F("  - P3-02 em ASCII (0..5) ou em 8E1/8O1 (7, 8)."));
    Serial.println(F("  - Contato mecanico do conector CN3."));
    Serial.println(F("  - Polaridade A/B invertida (rode o teste 4)."));
    Serial.println(F("  - Caminho ate o modulo (rode o teste 1)."));
  } else {
    Serial.println(F("Baud encontrado e mantido. Anote."));
    printCurrentSettings();
  }
}

void repeatedProbe() {
  Serial.println();
  Serial.println(F("=== TESTE 4: sondagem repetida (inverter A/B ao vivo) ==="));
  Serial.println(F("Com o curinga 0xFF nao e preciso saber o endereco."));
  Serial.println(F("Troque A com B com o teste rodando. Qualquer tecla para."));
  printCurrentSettings();
  Serial.println();

  while (Serial.available() > 0) {
    (void)Serial.read();
  }

  applyBus(currentBaudIndex);

  while (Serial.available() == 0) {
    uint8_t buffer[40];
    memset(buffer, 0, sizeof(buffer));
    uint16_t value = 0;
    uint8_t answered = 0;

    const uint8_t received = rawRead03(currentSlave, REG_P3_00, 1,
                                       buffer, sizeof(buffer), 500);
    const uint8_t verdict = classifyReply(buffer, received, currentSlave,
                                          &value, &answered);

    Serial.print(millis());
    Serial.print(F("  "));
    switch (verdict) {
      case 0:
        Serial.println(F("silencio"));
        break;
      case 1:
        Serial.println(F("so eco do proprio pedido"));
        break;
      case 2:
        Serial.print(F("RESPOSTA de 0x"));
        printHex8(answered);
        Serial.print(F("  P3-00=0x"));
        printHex16(value);
        Serial.println();
        break;
      case 4:
        Serial.print(F("EXCECAO Modbus 0x"));
        printHex8(static_cast<uint8_t>(value));
        Serial.println();
        break;
      default:
        Serial.print(F("bytes sem quadro valido: "));
        dumpBytes(buffer, (received > 16) ? 16 : received);
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

  const uint16_t addresses[7] = {0x0300, 0x0302, 0x0304, 0x030A, 0x030E, 0x0102, 0x030C};

  applyBus(currentBaudIndex);

  for (uint8_t index = 0; index < 7; ++index) {
    uint8_t buffer[40];
    memset(buffer, 0, sizeof(buffer));
    uint16_t value = 0;

    const uint8_t received = rawRead03(currentSlave, addresses[index], 1,
                                       buffer, sizeof(buffer), 500);
    const uint8_t verdict = classifyReply(buffer, received, currentSlave, &value);

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
      case 0: Serial.println(F("   endereco Modbus (fabrica 0x007F)")); break;
      case 1: Serial.println(F("   baud; RS-485 e o digito das dezenas (fabrica 0x0033)")); break;
      case 2: Serial.println(F("   protocolo; RS-485 nas dezenas (fabrica 0x0066 = 8N2 RTU)")); break;
      case 3: Serial.println(F("   mecanismo: precisa ser 0 (fabrica 1)")); break;
      case 4: Serial.println(F("   atraso de resposta (x1 ms)")); break;
      case 5: Serial.println(F("   modo de controle - NAO ALTERAR")); break;
      default: Serial.println(F("   origem das DI - NAO ALTERAR")); break;
    }
  }
}

void passiveListen() {
  Serial.println();
  Serial.println(F("=== TESTE 6: escuta passiva (nao transmite nada) ==="));
  Serial.println(F("So escuta por 5 s. Serve para ver se ha outro mestre no"));
  Serial.println(F("barramento e se a linha esta limpa. Um jorro de 0x00 ou"));
  Serial.println(F("0xFF indica RX preso em nivel, nao trafego."));
  printCurrentSettings();
  Serial.println();

  uint8_t buffer[40];
  memset(buffer, 0, sizeof(buffer));
  uint8_t received = 0;

  applyBus(currentBaudIndex);
  const uint32_t startMs = millis();
  while ((millis() - startMs) < 5000UL) {
    while (rs485.available() > 0 && received < sizeof(buffer)) {
      buffer[received++] = static_cast<uint8_t>(rs485.read());
    }
  }

  Serial.print(F("Bytes ouvidos em 5 s: "));
  Serial.println(received);
  if (received > 0) {
    dumpBytes(buffer, received);
    Serial.println();
  } else {
    Serial.println(F("Barramento em silencio. Esperado: o ASDA-B2 e escravo,"));
    Serial.println(F("so fala quando perguntado."));
  }
}

// -------------------- Menu --------------------

void printMenu() {
  Serial.println();
  Serial.println(F("------------------------------------------------------------"));
  Serial.println(F(" Diagnostico RS-485 / Modbus RTU - Delta ASDA-B2 - UNO"));
  Serial.println(F(" Somente leitura (funcao 03H). Nada e escrito no drive."));
  Serial.println(F(" SoftwareSerial: D10 -> RXD do modulo, D11 <- TXD."));
  Serial.println(F(" Formato fixo 8N2 (8N1 + pausa). 8E1/8O1 nao sao"));
  Serial.println(F(" produziveis por SoftwareSerial."));
  Serial.println(F("------------------------------------------------------------"));
  printCurrentSettings();
  Serial.println();
  Serial.println(F(" 1 - transmitir 10 s seguidos (medir A-B, ver o LED)"));
  Serial.println(F(" 2 - varrer enderecos 0x01..0x7F no baud atual"));
  Serial.println(F(" 3 - varrer os bauds nos enderecos 0xFF, 0x01 e 0x7F"));
  Serial.println(F(" 4 - sondagem repetida (util para inverter A/B ao vivo)"));
  Serial.println(F(" 5 - ler os parametros P3-xx do drive encontrado"));
  Serial.println(F(" 6 - escuta passiva por 5 s (nao transmite)"));
  Serial.println(F(" b - proximo baud rate"));
  Serial.println(F(" a - proximo endereco de slave"));
  Serial.println(F(" w - endereco curinga 0xFF (dispensa saber o P3-00)"));
  Serial.println(F(" v - liga/desliga o despejo de bytes na varredura"));
  Serial.println(F(" m - mostrar este menu"));
  Serial.println(F("------------------------------------------------------------"));
}

void handleCommand(char command) {
  switch (command) {
    case '1': testContinuousTx(); break;
    case '2': scanAddresses(); break;
    case '3': scanBaud(); break;
    case '4': repeatedProbe(); break;
    case '5': dumpKeyParameters(); break;
    case '6': passiveListen(); break;

    case 'b':
    case 'B':
      currentBaudIndex = (currentBaudIndex + 1) % BAUD_COUNT;
      applyBus(currentBaudIndex);
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
      Serial.print(F("Despejo de bytes: "));
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
  Serial.begin(CONSOLE_BAUD);
  delay(300);

  // Autoteste do CRC com o exemplo do manual do ASDA-B2.
  const uint8_t testFrame[] = {0x01, 0x03, 0x02, 0x00, 0x00, 0x02};
  const bool crcOk = (modbusCrc16(testFrame, sizeof(testFrame)) == 0xB3C5U);

  applyBus(currentBaudIndex);
  printMenu();

  Serial.print(F("Autoteste CRC-16: "));
  Serial.println(crcOk ? F("OK") : F("FALHOU"));
  Serial.println();
  Serial.println(F("JA DESCARTADO -- nao repita estes:"));
  Serial.println(F("  painel: P3-00=0001, P3-01=0011 (RS-485 9600),"));
  Serial.println(F("          P3-02=0066 (8N2 RTU), P3-05=0000. Drive OK."));
  Serial.println(F("  laco eletrico D10->modulo->A/B->modulo->D11: 1000/1000."));
  Serial.println(F("          Modulo alimentado, driver chaveia, RO enxerga,"));
  Serial.println(F("          GPIOs nos pinos certos, A e B nao estao em curto."));
  Serial.println(F("  baud, formato e endereco: varridos nos dois mestres."));
  Serial.println();
  Serial.println(F("SOBRA so o trecho de A/B ate o drive:"));
  Serial.println(F("  1. referencia comum -- o borne GND do lado RS-485 mediu"));
  Serial.println(F("     aberto. Se o CN3-1 esta so nele, o drive nao tem"));
  Serial.println(F("     referencia. Leve o CN3-1 ao GND do lado TTL."));
  Serial.println(F("  2. polaridade A/B -- rode o teste 4 e troque ao vivo."));
  Serial.println(F("  3. o par chegando nos pinos errados do CN3, ou contato."));
}

void loop() {
  while (Serial.available() > 0) {
    handleCommand(static_cast<char>(Serial.read()));
  }
  delay(10);
}
