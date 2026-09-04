#include <Arduino.h>
#include <math.h>

/*
 * Primeiro teste de telemetria do servo drive Delta ASDA-B2.
 *
 * Hardware:
 *   ESP32-S3 GPIO17 (TX) -> RXD do HW-519
 *   ESP32-S3 GPIO18 (RX) <- TXD do HW-519
 *   ESP32-S3 3V3         -> VCC do HW-519
 *   ESP32-S3 GND         -> GND do HW-519
 *
 *   HW-519 A+   -> CN3-5 RS-485(+)
 *   HW-519 B-   -> CN3-6 RS-485(-)
 *   HW-519 GND  -> CN3-1 GND          (terceiro borne do bloco de 3 vias)
 *   HW-519 R0   -> nao conectar       (habilita o terminador de 120 ohm)
 *
 * Separacao dos dois UARTs:
 *   UART0 (GPIO43/44) = console USB pela entrada COM da placa.
 *   UART1 (GPIO17/18) = barramento RS-485.
 *
 * NAO use GPIO43/GPIO44 para o RS-485. No ESP32-S3 eles sao U0TXD/U0RXD e
 * estao ligados ao chip USB-serial da propria placa, que fica alimentado e
 * mantem a saida dele empurrando o GPIO44. Isso briga com o TXD do HW-519:
 * o ESP transmite normalmente (o LED RXD do modulo pisca) e nunca recebe
 * nada de volta.
 *
 * O HW-519 faz o controle automatico da direcao RS-485, portanto este
 * sketch nao utiliza pinos DE/RE.
 *
 * Seguranca: o firmware implementa somente a funcao Modbus 03 (leitura).
 * Ele nunca escreve parametros nem comanda o movimento do motor.
 */

// -------------------- USB e UART RS-485 --------------------

constexpr uint32_t USB_SERIAL_BAUD = 115200;
constexpr int8_t RS485_RX_PIN = 18;
constexpr int8_t RS485_TX_PIN = 17;
constexpr uint32_t RS485_BAUD = 38400;

// Exige P3-00 = 0x0001 no drive. O padrao de fabrica e 0x007F (127): com
// ele o drive ignora todo pedido enviado ao endereco 1. Se este sketch der
// TIMEOUT em tudo, rode ASDA_B2_RS485_Scan antes de mexer na fiacao.
constexpr uint8_t MODBUS_SLAVE_ADDRESS = 1;

HardwareSerial rs485Serial(1);

// -------------------- Temporizacao Modbus --------------------

constexpr uint32_t RTU_SILENCE_MS = 12;
constexpr uint32_t RESPONSE_TIMEOUT_MS = 250;
constexpr uint32_t INTERBYTE_TIMEOUT_MS = 20;
constexpr uint32_t POLL_INTERVAL_MS = 1000;
constexpr uint8_t MAX_MODBUS_REGISTERS = 10;
constexpr size_t MAX_RESPONSE_SIZE = 5 + (2 * MAX_MODBUS_REGISTERS);

// -------------------- Motor ECMA-C20604ES --------------------

constexpr float MOTOR_RATED_POWER_W = 400.0f;
constexpr float MOTOR_RATED_SPEED_RPM = 3000.0f;
constexpr float MOTOR_RATED_TORQUE_NM = 1.27f;
constexpr float MOTOR_RATED_CURRENT_A = 2.60f;
constexpr float TWO_PI_OVER_60 = 0.10471975511965977f;

// -------------------- Registradores ASDA-B2 --------------------

constexpr uint16_t REG_ALARM_CODE = 0x0002;       // P0-01, 16 bits
constexpr uint16_t REG_SPEED_AND_LOAD = 0x0012;   // P0-09 e P0-10, 4 words
constexpr uint16_t REG_TORQUE_AND_STATUS = 0x0058; // P0-44..P0-46, 6 words

enum class ModbusStatus : uint8_t {
  Ok,
  InvalidArgument,
  TxError,
  Timeout,
  InterByteTimeout,
  ShortFrame,
  WrongSlave,
  WrongFunction,
  WrongByteCount,
  CrcError,
  ExceptionResponse
};

struct CommunicationStats {
  uint32_t transactions = 0;
  uint32_t successful = 0;
  uint32_t timeouts = 0;
  uint32_t crcErrors = 0;
  uint32_t frameErrors = 0;
  uint32_t exceptions = 0;
  uint32_t consecutiveErrors = 0;
  uint8_t lastExceptionCode = 0;
};

struct Telemetry {
  int32_t speedRaw = 0;
  int32_t averageLoadRaw = 0;
  int32_t torqueRaw = 0;
  uint16_t driveStatus = 0;
  uint16_t alarmCode = 0;
};

CommunicationStats communicationStats;

double accumulatedEnergyJ = 0.0;
double previousPowerW = 0.0;
uint32_t previousValidSampleMs = 0;
bool havePreviousPower = false;

uint32_t lastPollMs = 0;
uint32_t printedRows = 0;
bool protocolReady = false;

// -------------------- Utilitarios --------------------

uint16_t modbusCrc16(const uint8_t *data, size_t length) {
  uint16_t crc = 0xFFFF;

  while (length-- > 0) {
    crc ^= *data++;
    for (uint8_t bit = 0; bit < 8; ++bit) {
      if ((crc & 0x0001U) != 0U) {
        crc = static_cast<uint16_t>((crc >> 1U) ^ 0xA001U);
      } else {
        crc >>= 1U;
      }
    }
  }

  return crc;
}

int32_t combineSigned32(uint16_t lowWord, uint16_t highWord) {
  const uint32_t value = (static_cast<uint32_t>(highWord) << 16U) |
                         static_cast<uint32_t>(lowWord);
  return static_cast<int32_t>(value);
}

const char *modbusStatusName(ModbusStatus status) {
  switch (status) {
    case ModbusStatus::Ok:                return "OK";
    case ModbusStatus::InvalidArgument:   return "ARG";
    case ModbusStatus::TxError:           return "TX";
    case ModbusStatus::Timeout:           return "TIMEOUT";
    case ModbusStatus::InterByteTimeout:  return "INTERBYTE";
    case ModbusStatus::ShortFrame:        return "CURTO";
    case ModbusStatus::WrongSlave:        return "SLAVE";
    case ModbusStatus::WrongFunction:     return "FUNCAO";
    case ModbusStatus::WrongByteCount:    return "TAMANHO";
    case ModbusStatus::CrcError:          return "CRC";
    case ModbusStatus::ExceptionResponse: return "EXCECAO";
    default:                              return "DESCONH";
  }
}

const char *driveStateName(uint16_t status) {
  constexpr uint16_t STATUS_SRDY = (1U << 0U);
  constexpr uint16_t STATUS_SON = (1U << 1U);
  constexpr uint16_t STATUS_ALRM = (1U << 6U);

  if ((status & STATUS_ALRM) != 0U) {
    return "ALARM";
  }
  if ((status & STATUS_SON) != 0U) {
    return "SON";
  }
  if ((status & STATUS_SRDY) != 0U) {
    return "READY";
  }
  return "OFF";
}

void printHex16(uint16_t value) {
  Serial.print(F("0x"));
  if (value < 0x1000U) Serial.print('0');
  if (value < 0x0100U) Serial.print('0');
  if (value < 0x0010U) Serial.print('0');
  Serial.print(value, HEX);
}

void clearRs485Input() {
  while (rs485Serial.available() > 0) {
    (void)rs485Serial.read();
  }
}

void recordCommunicationResult(ModbusStatus status, uint8_t exceptionCode = 0) {
  ++communicationStats.transactions;

  if (status == ModbusStatus::Ok) {
    ++communicationStats.successful;
    communicationStats.consecutiveErrors = 0;
    return;
  }

  ++communicationStats.consecutiveErrors;

  switch (status) {
    case ModbusStatus::Timeout:
    case ModbusStatus::InterByteTimeout:
      ++communicationStats.timeouts;
      break;
    case ModbusStatus::CrcError:
      ++communicationStats.crcErrors;
      break;
    case ModbusStatus::ExceptionResponse:
      ++communicationStats.exceptions;
      communicationStats.lastExceptionCode = exceptionCode;
      break;
    default:
      ++communicationStats.frameErrors;
      break;
  }
}

// -------------------- Modbus RTU somente leitura --------------------

ModbusStatus readHoldingRegisters(uint16_t startAddress,
                                  uint16_t registerCount,
                                  uint16_t *registers,
                                  size_t registerCapacity) {
  if (registers == nullptr || registerCount == 0 ||
      registerCount > MAX_MODBUS_REGISTERS ||
      registerCapacity < registerCount) {
    recordCommunicationResult(ModbusStatus::InvalidArgument);
    return ModbusStatus::InvalidArgument;
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

  clearRs485Input();
  delay(RTU_SILENCE_MS);

  const size_t written = rs485Serial.write(request, sizeof(request));
  rs485Serial.flush();

  if (written != sizeof(request)) {
    delay(RTU_SILENCE_MS);
    recordCommunicationResult(ModbusStatus::TxError);
    return ModbusStatus::TxError;
  }

  uint8_t response[MAX_RESPONSE_SIZE] = {0};
  size_t received = 0;
  size_t expectedLength = 0;
  const uint32_t responseStartMs = millis();
  uint32_t lastByteMs = responseStartMs;
  bool stoppedByInterByteTimeout = false;

  while ((millis() - responseStartMs) < RESPONSE_TIMEOUT_MS) {
    while (rs485Serial.available() > 0 && received < sizeof(response)) {
      response[received++] = static_cast<uint8_t>(rs485Serial.read());
      lastByteMs = millis();

      if (received >= 2 && (response[1] & 0x80U) != 0U) {
        expectedLength = 5;
      } else if (received >= 3) {
        expectedLength = static_cast<size_t>(response[2]) + 5U;
      }
    }

    if (expectedLength > 0 && received >= expectedLength) {
      break;
    }

    if (received > 0 && (millis() - lastByteMs) >= INTERBYTE_TIMEOUT_MS) {
      stoppedByInterByteTimeout = true;
      break;
    }

    delay(1);
  }

  delay(RTU_SILENCE_MS);

  if (received == 0) {
    recordCommunicationResult(ModbusStatus::Timeout);
    return ModbusStatus::Timeout;
  }

  if (received < 5) {
    const ModbusStatus status = stoppedByInterByteTimeout
      ? ModbusStatus::InterByteTimeout
      : ModbusStatus::ShortFrame;
    recordCommunicationResult(status);
    return status;
  }

  if (response[0] != MODBUS_SLAVE_ADDRESS) {
    recordCommunicationResult(ModbusStatus::WrongSlave);
    return ModbusStatus::WrongSlave;
  }

  const size_t frameLength = ((response[1] & 0x80U) != 0U)
    ? 5U
    : static_cast<size_t>(response[2]) + 5U;

  if (frameLength > received || frameLength > sizeof(response)) {
    recordCommunicationResult(ModbusStatus::ShortFrame);
    return ModbusStatus::ShortFrame;
  }

  const uint16_t receivedCrc = static_cast<uint16_t>(response[frameLength - 2U]) |
                               (static_cast<uint16_t>(response[frameLength - 1U]) << 8U);
  const uint16_t calculatedCrc = modbusCrc16(response, frameLength - 2U);

  if (receivedCrc != calculatedCrc) {
    recordCommunicationResult(ModbusStatus::CrcError);
    return ModbusStatus::CrcError;
  }

  if ((response[1] & 0x80U) != 0U) {
    const uint8_t exceptionCode = response[2];
    recordCommunicationResult(ModbusStatus::ExceptionResponse, exceptionCode);
    return ModbusStatus::ExceptionResponse;
  }

  if (response[1] != 0x03U) {
    recordCommunicationResult(ModbusStatus::WrongFunction);
    return ModbusStatus::WrongFunction;
  }

  const uint8_t expectedByteCount = static_cast<uint8_t>(registerCount * 2U);
  if (response[2] != expectedByteCount || frameLength != (5U + expectedByteCount)) {
    recordCommunicationResult(ModbusStatus::WrongByteCount);
    return ModbusStatus::WrongByteCount;
  }

  for (uint16_t index = 0; index < registerCount; ++index) {
    const size_t dataIndex = 3U + (2U * index);
    registers[index] = (static_cast<uint16_t>(response[dataIndex]) << 8U) |
                       static_cast<uint16_t>(response[dataIndex + 1U]);
  }

  recordCommunicationResult(ModbusStatus::Ok);
  return ModbusStatus::Ok;
}

// -------------------- Auditoria de configuracao --------------------

struct ExpectedParameter {
  const char *name;
  uint16_t address;
  uint16_t expectedValue;
};

// Os dois padroes de fabrica que bloqueiam a comunicacao sozinhos:
//   P3-00 vem 0x007F (endereco 127), nao 0x0001.
//   P3-05 vem 0x0001 (RS-232 dedicado ao ASDA-Soft), nao 0x0000.
// P3-01 e P3-02 ja saem de fabrica nos valores que este projeto usa.
constexpr ExpectedParameter EXPECTED_PARAMETERS[] = {
  {"P1-01", 0x0102, 0x0002},
  {"P3-00", 0x0300, 0x0001},
  {"P3-01", 0x0302, 0x0033},
  {"P3-02", 0x0304, 0x0066},
  {"P3-05", 0x030A, 0x0000},
  {"P3-06", 0x030C, 0x0000},
  {"P0-17", 0x0022, 0x0007},
  {"P0-18", 0x0024, 0x000C},
  {"P0-45", 0x005A, 0x0036}
};

void auditDriveConfiguration() {
  Serial.println();
  Serial.println(F("Auditoria somente leitura dos parametros:"));
  Serial.println(F("Parametro  Esperado  Recebido  Resultado"));

  uint8_t answered = 0;

  for (const ExpectedParameter &parameter : EXPECTED_PARAMETERS) {
    uint16_t value = 0;
    const ModbusStatus status = readHoldingRegisters(
      parameter.address, 1, &value, 1
    );

    if (status == ModbusStatus::Ok) {
      ++answered;
    }

    Serial.printf("%-9s ", parameter.name);
    printHex16(parameter.expectedValue);
    Serial.print(F("    "));

    if (status != ModbusStatus::Ok) {
      Serial.print(F("----      FALHA: "));
      Serial.println(modbusStatusName(status));
      continue;
    }

    printHex16(value);
    Serial.print(F("    "));
    Serial.println(value == parameter.expectedValue ? F("OK") : F("DIVERGENTE"));
  }

  Serial.println(F("Fim da auditoria. Nenhum parametro foi escrito."));

  if (answered == 0) {
    Serial.println();
    Serial.println(F("O drive nao respondeu a nenhuma leitura. Antes de mexer na"));
    Serial.println(F("fiacao, carregue o sketch ASDA_B2_RS485_Scan: ele varre"));
    Serial.println(F("endereco, baud e formato, e mostra os bytes crus."));
    Serial.println(F("Suspeitos em ordem: P3-00 ainda em 0x007F; P3-05 ainda em 1;"));
    Serial.println(F("CN3-1 (GND) sem contato; A/B invertidos; conector CN3."));
  }
}

// -------------------- Aquisicao e calculos --------------------

bool readTelemetry(Telemetry &telemetry,
                   ModbusStatus &failureStatus,
                   const char *&failureStage) {
  uint16_t speedAndLoad[4] = {0};
  uint16_t torqueAndStatus[6] = {0};
  uint16_t alarmCode = 0;

  failureStage = "RPM/CARGA";
  failureStatus = readHoldingRegisters(
    REG_SPEED_AND_LOAD, 4, speedAndLoad, 4
  );
  if (failureStatus != ModbusStatus::Ok) {
    return false;
  }

  failureStage = "TORQUE/STATUS";
  failureStatus = readHoldingRegisters(
    REG_TORQUE_AND_STATUS, 6, torqueAndStatus, 6
  );
  if (failureStatus != ModbusStatus::Ok) {
    return false;
  }

  failureStage = "ALARME";
  failureStatus = readHoldingRegisters(REG_ALARM_CODE, 1, &alarmCode, 1);
  if (failureStatus != ModbusStatus::Ok) {
    return false;
  }

  telemetry.speedRaw = combineSigned32(speedAndLoad[0], speedAndLoad[1]);
  telemetry.averageLoadRaw = combineSigned32(speedAndLoad[2], speedAndLoad[3]);
  telemetry.torqueRaw = combineSigned32(torqueAndStatus[0], torqueAndStatus[1]);
  telemetry.driveStatus = torqueAndStatus[4]; // P0-46 em 0x005C
  telemetry.alarmCode = alarmCode;

  failureStatus = ModbusStatus::Ok;
  failureStage = "-";
  return true;
}

void resetEnergy() {
  accumulatedEnergyJ = 0.0;
  previousPowerW = 0.0;
  previousValidSampleMs = 0;
  havePreviousPower = false;
  Serial.println(F("Energia mecanica acumulada zerada."));
}

void updateEnergy(double powerW, uint32_t sampleMs) {
  if (havePreviousPower) {
    const uint32_t elapsedMs = sampleMs - previousValidSampleMs;

    // Nao integrar lacunas: o intervalo normal e 1 s.
    if (elapsedMs <= (POLL_INTERVAL_MS * 5U) / 2U) {
      const double elapsedSeconds = static_cast<double>(elapsedMs) / 1000.0;
      accumulatedEnergyJ += ((previousPowerW + powerW) * 0.5) * elapsedSeconds;
    }
  }

  previousPowerW = powerW;
  previousValidSampleMs = sampleMs;
  havePreviousPower = true;
}

void invalidateEnergyContinuity() {
  havePreviousPower = false;
}

void printTableHeader() {
  Serial.println();
  Serial.println(F("tempo_ms   COMM       DRIVE  ALM   RPM        torque_%  torque_Nm  carga_%  potencia_W  energia_Wh   OK/ERROS"));
  Serial.println(F("-----------------------------------------------------------------------------------------------------------"));
}

void printValidTelemetry(const Telemetry &telemetry, uint32_t sampleMs) {
  const double rpm = static_cast<double>(telemetry.speedRaw) * 0.1;
  const double torquePercent = static_cast<double>(telemetry.torqueRaw) * 0.1;
  const double torqueNm = static_cast<double>(telemetry.torqueRaw) *
                          (static_cast<double>(MOTOR_RATED_TORQUE_NM) / 1000.0);
  const double averageLoadPercent = static_cast<double>(telemetry.averageLoadRaw);
  const double powerW = torqueNm * rpm * static_cast<double>(TWO_PI_OVER_60);

  updateEnergy(powerW, sampleMs);

  Serial.printf(
    "%-10lu %-10s %-6s %04X %10.1f %10.1f %10.4f %8.1f %11.2f %11.6f %lu/%lu\n",
    static_cast<unsigned long>(sampleMs),
    "OK",
    driveStateName(telemetry.driveStatus),
    static_cast<unsigned int>(telemetry.alarmCode),
    rpm,
    torquePercent,
    torqueNm,
    averageLoadPercent,
    powerW,
    accumulatedEnergyJ / 3600.0,
    static_cast<unsigned long>(communicationStats.successful),
    static_cast<unsigned long>(communicationStats.transactions - communicationStats.successful)
  );
}

void printInvalidTelemetry(ModbusStatus status,
                           const char *failureStage,
                           uint32_t sampleMs) {
  invalidateEnergyContinuity();

  Serial.printf(
    "%-10lu %-10s %-6s ---- ---------- ---------- ---------- -------- ----------- ----------- %lu/%lu  etapa=%s",
    static_cast<unsigned long>(sampleMs),
    modbusStatusName(status),
    "---",
    static_cast<unsigned long>(communicationStats.successful),
    static_cast<unsigned long>(communicationStats.transactions - communicationStats.successful),
    failureStage
  );

  if (status == ModbusStatus::ExceptionResponse) {
    Serial.printf(" codigo=0x%02X", communicationStats.lastExceptionCode);
  }
  Serial.println();
}

// -------------------- Interface USB --------------------

void printHelp() {
  Serial.println();
  Serial.println(F("Comandos locais (nao sao enviados ao drive):"));
  Serial.println(F("  H - mostrar esta ajuda"));
  Serial.println(F("  R - zerar a energia mecanica acumulada"));
}

void processUsbCommands() {
  while (Serial.available() > 0) {
    const char command = static_cast<char>(Serial.read());
    if (command == 'H' || command == 'h') {
      printHelp();
    } else if (command == 'R' || command == 'r') {
      resetEnergy();
    }
  }
}

bool runCrcSelfTest() {
  // Exemplo do manual: 01 03 02 00 00 02 -> CRC baixo C5, alto B3.
  const uint8_t testFrame[] = {0x01, 0x03, 0x02, 0x00, 0x00, 0x02};
  return modbusCrc16(testFrame, sizeof(testFrame)) == 0xB3C5U;
}

void printStartupInformation() {
  Serial.println();
  Serial.println(F("============================================================"));
  Serial.println(F(" Delta ASDA-B2 - primeiro teste RS-485 / Modbus RTU"));
  Serial.println(F(" Firmware somente leitura: funcao Modbus 03"));
  Serial.println(F("============================================================"));
  Serial.printf("USB CDC: %lu baud\n", static_cast<unsigned long>(USB_SERIAL_BAUD));
  Serial.printf(
    "RS-485: slave=%u, %lu baud, 8N2, RX=GPIO%d, TX=GPIO%d\n",
    MODBUS_SLAVE_ADDRESS,
    static_cast<unsigned long>(RS485_BAUD),
    RS485_RX_PIN,
    RS485_TX_PIN
  );
  Serial.printf(
    "Motor: ECMA-C20604ES, %.0f W, %.0f rpm, %.2f N.m, %.2f A\n",
    MOTOR_RATED_POWER_W,
    MOTOR_RATED_SPEED_RPM,
    MOTOR_RATED_TORQUE_NM,
    MOTOR_RATED_CURRENT_A
  );
  Serial.println(F("CN1 permanece responsavel pelo comando do motor."));
}

void setup() {
  Serial.begin(USB_SERIAL_BAUD);

  const uint32_t serialWaitStartMs = millis();
  while (!Serial && (millis() - serialWaitStartMs) < 3000U) {
    delay(10);
  }

  printStartupInformation();

  rs485Serial.begin(RS485_BAUD, SERIAL_8N2, RS485_RX_PIN, RS485_TX_PIN);
  delay(100);

  protocolReady = runCrcSelfTest();
  Serial.print(F("Autoteste CRC-16 Modbus: "));
  Serial.println(protocolReady ? F("OK") : F("FALHOU"));

  if (!protocolReady) {
    Serial.println(F("ERRO CRITICO: consultas Modbus foram bloqueadas."));
    return;
  }

  auditDriveConfiguration();
  printHelp();
  printTableHeader();
  lastPollMs = millis();
}

void loop() {
  processUsbCommands();

  if (!protocolReady) {
    delay(10);
    return;
  }

  const uint32_t nowMs = millis();
  if ((nowMs - lastPollMs) < POLL_INTERVAL_MS) {
    delay(1);
    return;
  }
  lastPollMs = nowMs;

  if (printedRows > 0 && (printedRows % 20U) == 0U) {
    printTableHeader();
  }

  Telemetry telemetry;
  ModbusStatus failureStatus = ModbusStatus::Ok;
  const char *failureStage = "-";

  if (readTelemetry(telemetry, failureStatus, failureStage)) {
    printValidTelemetry(telemetry, nowMs);
  } else {
    printInvalidTelemetry(failureStatus, failureStage, nowMs);
  }

  ++printedRows;
}
