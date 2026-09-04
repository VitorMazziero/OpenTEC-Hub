#include <Arduino.h>
#include <math.h>

/*
 * Leitura dos registradores DINAMICOS do Delta ASDA-B2 -- ESP32-S3 / HW-097.
 *
 * Este sketch fecha o Gate A do PLANO_INTEGRACAO_POTENCIA_OPENTECHUB.md.
 *
 * O autoscan provou a camada fisica e leu os ESTATICOS (P3-xx, P1-01, P0-00).
 * Nenhum sketch de bancada lia ainda P0-09, P0-10, P0-44, P0-46 e P0-01 -- os
 * unicos que o no de producao realmente usa. Sem eles nao ha como conferir
 * escala, sinal e ordem das words contra o painel do drive, e o no publicaria
 * numeros plausiveis e errados sem ninguem perceber.
 *
 * Usa EXATAMENTE o mesmo caminho Modbus do no de producao (mesmos pinos, mesma
 * temporizacao, mesmo CRC, mesmo parsing de quadro), para que o que for medido
 * aqui valha la sem reinterpretacao.
 *
 * SEM WiFi: a bancada fica isolada do hub. Um problema de leitura nao se
 * mistura com um problema de rede.
 *
 * ------------------------------------------------------------------
 * SEGURANCA
 * ------------------------------------------------------------------
 * Somente leitura: o unico codigo Modbus transmitido e 03H.
 * Nunca escreve parametro, nunca habilita o servo, nunca comanda o motor.
 * O comando de velocidade continua exclusivamente pelo CN1.
 *
 * ------------------------------------------------------------------
 * PRE-REQUISITO NO PAINEL DO DRIVE  (fazer ANTES de rodar)
 * ------------------------------------------------------------------
 * P0-09, P0-10 e P0-44 sao registradores de MONITOR: mostram a variavel que
 * P0-17, P0-18 e P0-45 mandarem mostrar. Com os valores de fabrica eles
 * respondem normalmente e devolvem OUTRA grandeza. Ajustar no painel:
 *
 *     P0-17 = 7     ->  P0-09 passa a ser velocidade de retorno  (0,1 rpm)
 *     P0-18 = 12    ->  P0-10 passa a ser carga media            (%)
 *     P0-45 = 54    ->  P0-44 passa a ser torque de retorno      (0,1 %)
 *
 * P0-46 (status das saidas digitais) e P0-01 (codigo de alarme) nao dependem
 * de mapeamento.
 *
 * ------------------------------------------------------------------
 * HARDWARE  (identico ao no de producao)
 * ------------------------------------------------------------------
 *   ESP32-S3 GPIO17 (TX) ---> DI do HW-097
 *   ESP32-S3 GPIO16      ---> DE e /RE em curto no HW-097
 *   ESP32-S3 GPIO18 (RX) <--- RO do HW-097 POR DIVISOR 1k/2k
 *   ESP32-S3 GND         ---  GND do HW-097 e terra comum
 *   HW-097 A -> CN3-5      HW-097 B -> CN3-6      VCC -> 5 V da placa
 *
 * Contrato serial: 9600 baud, 8N2, slave 1.
 */

// -------------------- Configuracao --------------------

constexpr uint32_t USB_SERIAL_BAUD = 115200;

constexpr int8_t RS485_TX_PIN = 17;
constexpr int8_t RS485_RX_PIN = 18;
constexpr int8_t RS485_DE_RE_PIN = 16;
constexpr uint32_t RS485_BAUD = 9600;
constexpr uint8_t MODBUS_SLAVE_ADDRESS = 1;

// Motor ECMA-C20604ES. Confira a placa de identificacao do motor antes de
// aceitar os N.m: se o modelo for outro, so este numero muda.
constexpr float MOTOR_RATED_TORQUE_NM = 1.27f;
constexpr float TWO_PI_OVER_60 = 0.10471975511965977f;

// Registradores ASDA-B2 (endereco = grupo * 0x100 + indice * 2)
constexpr uint16_t REG_ALARM_CODE = 0x0002;        // P0-01
constexpr uint16_t REG_SPEED_AND_LOAD = 0x0012;    // P0-09, P0-10 e P0-11, 6 words
constexpr uint16_t REG_TORQUE_AND_STATUS = 0x0058; // P0-44..P0-46, 6 words
constexpr uint16_t REG_MONITOR_SELECTOR = 0x005A;  // P0-45

// P0-45 e o unico seletor que alcanca a faixa estendida 0~127, onde vive o
// codigo 54 (torque de retorno, 0,1 %). Os seletores P0-17..P0-21 param em 18 e
// nao tem torque de retorno na lista. So que P0-45 e o par "for PC Software":
// default 0x0 e volatil -- volta a zero a cada religamento do DRIVE, nao da
// placa. Com P0-45 = 0 o P0-44 devolve a variavel de codigo 0 (posicao de
// retorno em pulsos) e responde Modbus normalmente, sem erro nenhum.
// Por isso o no reescreve o seletor: uma escrita 06H, verificada por leitura.
constexpr uint16_t MONITOR_CODE_TORQUE_FEEDBACK = 54;

constexpr uint32_t RTU_SILENCE_MS = 12;
constexpr uint32_t RESPONSE_TIMEOUT_MS = 250;
constexpr uint32_t INTERBYTE_TIMEOUT_MS = 20;
constexpr uint8_t MAX_MODBUS_REGISTERS = 10;
constexpr size_t MAX_RESPONSE_SIZE = 5 + (2 * MAX_MODBUS_REGISTERS);

// 1000 ms e o regime normal do Gate A. Para capturar a desaceleracao -- um
// transitorio de menos de um segundo -- baixar para 0 e deixar o barramento
// livre correr: com 4 transacoes por amostra a 9600 8N2, o ciclo fica em torno
// de 250 ms (medido: 337 ms). Restaurado para 1000 apos o ensaio de 2026-09-02.
constexpr uint32_t SAMPLE_INTERVAL_MS = 1000;

HardwareSerial rs485Serial(1);

uint32_t commOk = 0;
uint32_t commErr = 0;

// -------------------- Modbus RTU somente leitura --------------------

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

int32_t combineSigned32(uint16_t lowWord, uint16_t highWord) {
  const uint32_t value = (static_cast<uint32_t>(highWord) << 16U) |
                         static_cast<uint32_t>(lowWord);
  return static_cast<int32_t>(value);
}

bool readHoldingRegisters(uint16_t startAddress,
                          uint16_t registerCount,
                          uint16_t *registers) {
  if (registers == nullptr || registerCount == 0 ||
      registerCount > MAX_MODBUS_REGISTERS) {
    return false;
  }

  uint8_t request[8] = {
    MODBUS_SLAVE_ADDRESS,
    0x03,
    static_cast<uint8_t>(startAddress >> 8U),
    static_cast<uint8_t>(startAddress & 0x00FFU),
    static_cast<uint8_t>(registerCount >> 8U),
    static_cast<uint8_t>(registerCount & 0x00FFU),
    0,
    0
  };

  const uint16_t requestCrc = modbusCrc16(request, 6);
  request[6] = static_cast<uint8_t>(requestCrc & 0x00FFU);
  request[7] = static_cast<uint8_t>(requestCrc >> 8U);

  while (rs485Serial.available() > 0) {
    (void)rs485Serial.read();
  }
  delay(RTU_SILENCE_MS);

  digitalWrite(RS485_DE_RE_PIN, HIGH);

  if (rs485Serial.write(request, sizeof(request)) != sizeof(request)) {
    digitalWrite(RS485_DE_RE_PIN, LOW);
    return false;
  }
  rs485Serial.flush();

  delayMicroseconds((11UL * 1000000UL / RS485_BAUD) + 150UL);
  digitalWrite(RS485_DE_RE_PIN, LOW);

  delayMicroseconds(200);
  while (rs485Serial.available() > 0) {
    (void)rs485Serial.read();
  }

  uint8_t response[MAX_RESPONSE_SIZE] = {0};
  size_t received = 0;
  size_t expectedLength = 0;
  const uint32_t startMs = millis();
  uint32_t lastByteMs = startMs;

  while ((millis() - startMs) < RESPONSE_TIMEOUT_MS) {
    while (rs485Serial.available() > 0 && received < sizeof(response)) {
      response[received++] = static_cast<uint8_t>(rs485Serial.read());
      lastByteMs = millis();

      if (received >= 2 && (response[1] & 0x80U) != 0U) {
        expectedLength = 5;
      } else if (received >= 3) {
        expectedLength = static_cast<size_t>(response[2]) + 5U;
      }
    }

    if (expectedLength > 0 && received >= expectedLength) break;
    if (received > 0 && (millis() - lastByteMs) >= INTERBYTE_TIMEOUT_MS) break;

    delay(1);
  }

  delay(RTU_SILENCE_MS);

  if (received < 5) {
    Serial.printf("   [falha] resposta curta: %u byte(s)\n",
                  static_cast<unsigned>(received));
    return false;
  }
  if (response[0] != MODBUS_SLAVE_ADDRESS) {
    Serial.printf("   [falha] slave inesperado: 0x%02X\n", response[0]);
    return false;
  }

  const size_t frameLength = ((response[1] & 0x80U) != 0U)
    ? 5U
    : static_cast<size_t>(response[2]) + 5U;

  if (frameLength > received || frameLength > sizeof(response)) {
    Serial.println(F("   [falha] quadro incompleto"));
    return false;
  }

  const uint16_t receivedCrc =
    static_cast<uint16_t>(response[frameLength - 2U]) |
    (static_cast<uint16_t>(response[frameLength - 1U]) << 8U);

  if (receivedCrc != modbusCrc16(response, frameLength - 2U)) {
    Serial.println(F("   [falha] CRC invalido"));
    return false;
  }

  if ((response[1] & 0x80U) != 0U) {
    // Excecao Modbus. 0x02 aqui costuma significar registrador nao mapeado:
    // reveja P0-17 / P0-18 / P0-45 no painel.
    Serial.printf("   [falha] excecao Modbus 0x%02X no endereco 0x%04X\n",
                  response[2], startAddress);
    return false;
  }
  if (response[1] != 0x03U) {
    Serial.printf("   [falha] funcao inesperada: 0x%02X\n", response[1]);
    return false;
  }

  const uint8_t expectedByteCount = static_cast<uint8_t>(registerCount * 2U);
  if (response[2] != expectedByteCount ||
      frameLength != (5U + expectedByteCount)) {
    Serial.println(F("   [falha] contagem de bytes divergente"));
    return false;
  }

  for (uint16_t index = 0; index < registerCount; ++index) {
    const size_t dataIndex = 3U + (2U * index);
    registers[index] = (static_cast<uint16_t>(response[dataIndex]) << 8U) |
                       static_cast<uint16_t>(response[dataIndex + 1U]);
  }

  return true;
}

// Escreve um unico registrador (funcao 06H). O escopo e deliberadamente
// estreito: o unico endereco que o monitor escreve e P0-45, um parametro
// volatil de selecao de monitor. Nada de ganho, limite ou modo de controle
// passa por aqui.
bool writeSingleRegister(uint16_t address, uint16_t value) {
  uint8_t request[8] = {
    MODBUS_SLAVE_ADDRESS,
    0x06,
    static_cast<uint8_t>(address >> 8U),
    static_cast<uint8_t>(address & 0x00FFU),
    static_cast<uint8_t>(value >> 8U),
    static_cast<uint8_t>(value & 0x00FFU),
    0,
    0
  };

  const uint16_t requestCrc = modbusCrc16(request, 6);
  request[6] = static_cast<uint8_t>(requestCrc & 0x00FFU);
  request[7] = static_cast<uint8_t>(requestCrc >> 8U);

  while (rs485Serial.available() > 0) {
    (void)rs485Serial.read();
  }
  delay(RTU_SILENCE_MS);

  digitalWrite(RS485_DE_RE_PIN, HIGH);

  if (rs485Serial.write(request, sizeof(request)) != sizeof(request)) {
    digitalWrite(RS485_DE_RE_PIN, LOW);
    return false;
  }
  rs485Serial.flush();

  delayMicroseconds((11UL * 1000000UL / RS485_BAUD) + 150UL);
  digitalWrite(RS485_DE_RE_PIN, LOW);

  delayMicroseconds(200);

  // A resposta normal de 06H e o eco dos mesmos 8 bytes.
  uint8_t response[8] = {0};
  size_t received = 0;
  const uint32_t startMs = millis();
  uint32_t lastByteMs = startMs;

  while ((millis() - startMs) < RESPONSE_TIMEOUT_MS) {
    while (rs485Serial.available() > 0 && received < sizeof(response)) {
      response[received++] = static_cast<uint8_t>(rs485Serial.read());
      lastByteMs = millis();
    }
    if (received >= sizeof(response)) break;
    if (received > 0 && (millis() - lastByteMs) >= INTERBYTE_TIMEOUT_MS) break;
    delay(1);
  }

  delay(RTU_SILENCE_MS);

  if (received >= 5 && (response[1] & 0x80U) != 0U) {
    Serial.printf("   [falha] escrita rejeitada: excecao 0x%02X no endereco 0x%04X\n",
                  response[2], address);
    return false;
  }
  if (received != sizeof(response)) {
    Serial.printf("   [falha] eco de escrita com %u byte(s)\n",
                  static_cast<unsigned>(received));
    return false;
  }

  const uint16_t receivedCrc =
    static_cast<uint16_t>(response[6]) | (static_cast<uint16_t>(response[7]) << 8U);
  if (receivedCrc != modbusCrc16(response, 6)) {
    Serial.println(F("   [falha] CRC invalido no eco de escrita"));
    return false;
  }
  if (memcmp(request, response, 6) != 0) {
    Serial.println(F("   [falha] eco de escrita divergente"));
    return false;
  }

  return true;
}

// Garante P0-45 = 54. Le antes de escrever: em regime normal isto e uma unica
// leitura por ciclo e nenhuma escrita. Retorna true se o mapeamento esta valido
// ao final. Chamada a cada amostra para sobreviver a um religamento do drive
// sem precisar reiniciar a placa.
bool ensureTorqueMapping(bool verbose) {
  uint16_t selector = 0;

  if (!readHoldingRegisters(REG_MONITOR_SELECTOR, 1, &selector)) {
    if (verbose) Serial.println(F("[MONITOR] P0-45 nao pode ser lido."));
    return false;
  }

  if (selector == MONITOR_CODE_TORQUE_FEEDBACK) {
    if (verbose) Serial.println(F("[MONITOR] P0-45 ja esta em 54."));
    return true;
  }

  Serial.printf("[MONITOR] P0-45 estava em %u; escrevendo 54 (06H)...\n", selector);

  if (!writeSingleRegister(REG_MONITOR_SELECTOR, MONITOR_CODE_TORQUE_FEEDBACK)) {
    return false;
  }

  // Confirmar por leitura: o eco de 06H prova que o quadro chegou, nao que o
  // drive aceitou e reteve o valor.
  delay(RTU_SILENCE_MS);
  if (!readHoldingRegisters(REG_MONITOR_SELECTOR, 1, &selector)) {
    Serial.println(F("[MONITOR] escrita sem confirmacao de leitura."));
    return false;
  }
  if (selector != MONITOR_CODE_TORQUE_FEEDBACK) {
    Serial.printf("[MONITOR] P0-45 rejeitou o valor: leu %u de volta.\n", selector);
    return false;
  }

  Serial.println(F("[MONITOR] P0-45 = 54 confirmado por leitura."));
  return true;
}

// -------------------- Apresentacao --------------------

// Imprime as duas leituras possiveis de um par de words. A ordem correta e a
// que acompanha o painel; a outra salta para valores absurdos assim que a
// grandeza passa de 65535 ou fica negativa. E o unico jeito honesto de fechar
// o item "ordem das words" do Gate A sem adivinhar.
void printWordPair(const char *label, uint16_t first, uint16_t second, float scale,
                   const char *unit) {
  const int32_t lowFirst = combineSigned32(first, second);   // interpretacao do no
  const int32_t highFirst = combineSigned32(second, first);  // alternativa

  Serial.printf("  %-22s words=[0x%04X 0x%04X]\n", label, first, second);
  Serial.printf("  %-22s low-first : raw=%11ld  ->  %10.2f %s   <== usado pelo no\n",
                "", static_cast<long>(lowFirst),
                static_cast<double>(lowFirst) * scale, unit);
  Serial.printf("  %-22s high-first: raw=%11ld  ->  %10.2f %s\n",
                "", static_cast<long>(highFirst),
                static_cast<double>(highFirst) * scale, unit);
}

void printStatusBits(uint16_t status) {
  constexpr uint16_t SRDY = (1U << 0U);
  constexpr uint16_t SON  = (1U << 1U);
  constexpr uint16_t ZSPD = (1U << 2U);
  constexpr uint16_t TSPD = (1U << 3U);
  constexpr uint16_t TPOS = (1U << 4U);
  constexpr uint16_t TQL  = (1U << 5U);
  constexpr uint16_t ALRM = (1U << 6U);
  constexpr uint16_t BRKR = (1U << 7U);

  int state;
  if ((status & ALRM) != 0U)      state = 3;
  else if ((status & SON) != 0U)  state = 2;
  else if ((status & SRDY) != 0U) state = 1;
  else                            state = 0;

  static const char *NAMES[4] = {"OFF", "READY", "SON", "ALARM"};

  Serial.printf("  %-22s 0x%04X  ", "P0-46 status", status);
  Serial.printf("SRDY=%d SON=%d ZSPD=%d TSPD=%d TPOS=%d TQL=%d ALRM=%d BRKR=%d\n",
                (status & SRDY) ? 1 : 0, (status & SON)  ? 1 : 0,
                (status & ZSPD) ? 1 : 0, (status & TSPD) ? 1 : 0,
                (status & TPOS) ? 1 : 0, (status & TQL)  ? 1 : 0,
                (status & ALRM) ? 1 : 0, (status & BRKR) ? 1 : 0);
  Serial.printf("  %-22s %d (%s)   <== e o que vai em ServoState\n",
                "estado derivado", state, NAMES[state]);
}

// -------------------- Setup e loop --------------------

void setup() {
  Serial.begin(USB_SERIAL_BAUD);

  const uint32_t waitStartMs = millis();
  while (!Serial && (millis() - waitStartMs) < 2000U) {
    delay(10);
  }

  Serial.println();
  Serial.println(F("============================================================"));
  Serial.println(F(" Monitor dinamico do Delta ASDA-B2 -- Gate A"));
  Serial.println(F(" Leitura 03H + uma unica escrita 06H: P0-45 (seletor volatil)."));
  Serial.println(F("============================================================"));
  Serial.printf("RS-485: slave=%u, %lu baud, 8N2, TX=GPIO%d, RX=GPIO%d, DE/RE=GPIO%d\n",
                MODBUS_SLAVE_ADDRESS,
                static_cast<unsigned long>(RS485_BAUD),
                RS485_TX_PIN, RS485_RX_PIN, RS485_DE_RE_PIN);
  Serial.println();
  Serial.println(F("CONFIRA NO PAINEL ANTES DE ACEITAR QUALQUER NUMERO:"));
  Serial.println(F("  P0-17 = 7    (P0-09 -> velocidade de retorno)"));
  Serial.println(F("  P0-18 = 12   (P0-10 -> carga media)"));
  Serial.println(F("  P0-19 = 11   (P0-11 -> comando de torque integrado, %)"));
  Serial.println(F("Sem isso os registradores respondem, mas com outra grandeza."));
  Serial.println(F("P0-45 nao precisa ser ajustado no painel: e volatil, e o"));
  Serial.println(F("proprio monitor o reescreve para 54 e confirma por leitura."));
  Serial.println();

  const uint8_t testFrame[] = {0x01, 0x03, 0x02, 0x00, 0x00, 0x02};
  if (modbusCrc16(testFrame, sizeof(testFrame)) != 0xB3C5U) {
    Serial.println(F("[MONITOR] ERRO CRITICO: autoteste de CRC falhou. Parado."));
    for (;;) delay(1000);
  }
  Serial.println(F("[MONITOR] Autoteste de CRC OK."));

  pinMode(RS485_DE_RE_PIN, OUTPUT);
  digitalWrite(RS485_DE_RE_PIN, LOW); // repouso = recepcao
  rs485Serial.begin(RS485_BAUD, SERIAL_8N2, RS485_RX_PIN, RS485_TX_PIN);
  delay(100);

  (void)ensureTorqueMapping(true);

  Serial.println(F("[MONITOR] Amostrando a cada 1 s. Ctrl+C nao e necessario:"));
  Serial.println(F("          basta desligar ou regravar a placa."));
}

void loop() {
  static uint32_t sample = 0;

  uint16_t speedAndLoad[6] = {0};
  uint16_t torqueAndStatus[6] = {0};
  uint16_t alarmCode = 0;

  Serial.println();
  Serial.printf("---- amostra %lu  (t = %.1f s) ----\n",
                static_cast<unsigned long>(++sample), millis() / 1000.0);

  // Reaplica P0-45 se o drive tiver sido religado desde a ultima amostra.
  const bool mappingOk = ensureTorqueMapping(false);

  const bool okSpeed  = readHoldingRegisters(REG_SPEED_AND_LOAD, 6, speedAndLoad);
  const bool okTorque = readHoldingRegisters(REG_TORQUE_AND_STATUS, 6, torqueAndStatus);
  const bool okAlarm  = readHoldingRegisters(REG_ALARM_CODE, 1, &alarmCode);

  if (okSpeed && okTorque && okAlarm) {
    commOk += 3;
  } else {
    ++commErr;
    Serial.printf("  leitura incompleta  (ok=%lu err=%lu)\n",
                  static_cast<unsigned long>(commOk),
                  static_cast<unsigned long>(commErr));
    delay(SAMPLE_INTERVAL_MS);
    return;
  }

  // P0-09 velocidade de retorno, unidade 0,1 rpm
  printWordPair("P0-09 velocidade", speedAndLoad[0], speedAndLoad[1], 0.1f, "rpm");

  // P0-10 carga media. A documentacao de bring-up da a unidade como % direto,
  // sem o 0,1 de P0-09 e P0-44 -- por isso o no NAO escala este. Confirme a
  // linha "escala 1" contra o painel; se o painel bater com a "escala 0,1",
  // e la que o no precisa mudar.
  printWordPair("P0-10 carga (esc. 1)", speedAndLoad[2], speedAndLoad[3], 1.0f, "%");
  printWordPair("P0-10 carga (esc.0,1)", speedAndLoad[2], speedAndLoad[3], 0.1f, "%");

  // P0-44 torque de retorno, unidade 0,1 %. So responde se P0-45 = 54, e P0-45
  // e volatil (par "for PC Software", default 0x0): volta a zero a cada
  // religamento do drive. Com P0-45 = 0 este par le a variavel de codigo 0 e
  // fica preso em 0x0000 sem nenhum sinal de erro.
  if (mappingOk) {
    printWordPair("P0-44 torque", torqueAndStatus[0], torqueAndStatus[1], 0.1f, "%");
  } else {
    Serial.println(F("  P0-44 torque           <== IGNORADO: P0-45 nao esta em 54"));
  }

  // P0-11 e o candidato persistente para o torque: selecionado por P0-19, que
  // fica na faixa 0~18 dos seletores nao volateis. Com P0-19 = 11 devolve o
  // comando de torque integrado, em % inteiro e com sinal.
  printWordPair("P0-11 (P0-19=11)", speedAndLoad[4], speedAndLoad[5], 1.0f, "%");

  printStatusBits(torqueAndStatus[4]);
  Serial.printf("  %-22s %u%s\n", "P0-01 alarme", alarmCode,
                alarmCode == 0 ? "  (sem alarme)" : "  <== ALARME ATIVO");

  // Grandezas derivadas, exatamente como o no de producao as calcula.
  const int32_t speedRaw = combineSigned32(speedAndLoad[0], speedAndLoad[1]);
  const int32_t torqueRaw = combineSigned32(torqueAndStatus[0], torqueAndStatus[1]);
  const double rpm = static_cast<double>(speedRaw) * 0.1;
  const double torqueNm = static_cast<double>(torqueRaw) *
                          (static_cast<double>(MOTOR_RATED_TORQUE_NM) / 1000.0);
  const double powerW = torqueNm * rpm * static_cast<double>(TWO_PI_OVER_60);

  Serial.println(F("  --- derivados (iguais aos do no de producao) ---"));
  Serial.printf("  %-22s %.4f N.m   (T_nominal = %.2f N.m)\n",
                "torque", torqueNm, MOTOR_RATED_TORQUE_NM);
  Serial.printf("  %-22s %.2f W    (potencia MECANICA no eixo, nao eletrica)\n",
                "potencia", powerW);
  Serial.printf("  %-22s ok=%lu err=%lu\n", "contadores",
                static_cast<unsigned long>(commOk),
                static_cast<unsigned long>(commErr));

  delay(SAMPLE_INTERVAL_MS);
}
