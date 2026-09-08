#include <Arduino.h>
#include <WiFi.h>
#include <HTTPClient.h>
#include <esp_task_wdt.h>
#include <math.h>
#include <limits.h>

/*
 * No de telemetria do servo drive Delta ASDA-B2 (ASD-B2-0421-B).
 *
 * Le e comanda o drive por Modbus RTU sobre RS-485 (CN3), associa-se ao
 * SoftAP do TECNAL Hub e troca telemetria/comandos por HTTP.
 *
 * Este sketch e o candidato de producao do no. Os sketches de bancada
 * ASDA_B2_RS485_First_Test e ASDA_B2_RS485_Scan continuam existindo para
 * diagnostico e nao devem ser misturados com este.
 *
 * ------------------------------------------------------------------
 * SEGURANCA
 * ------------------------------------------------------------------
 * O modo direto so e armado depois de receber o protocolo v10 do Hub e de
 * confirmar P1-01/P2-10/P2-12/P2-13. O setpoint interno P1-09 e escrito em
 * duas words por 10H; P3-06 e P4-07 selecionam SON, SPD0 e SPD1 por software.
 * P2-30=5 mantem as escritas ciclicas em RAM. Perda do heartbeat do Hub zera
 * P1-09 e remove SON. Na via UART/CN1, o no confirma P3-06=0 e nao interfere
 * no setpoint da placa original. P0-45=54 continua confirmado para telemetria.
 * O E-stop e os interlocks fisicos continuam obrigatorios e independentes.
 *
 * ------------------------------------------------------------------
 * HARDWARE
 * ------------------------------------------------------------------
 *   ESP32-S3 GPIO17 (TX) ---> DI do HW-097
 *   ESP32-S3 GPIO16      ---> DE e /RE em curto no HW-097
 *   ESP32-S3 GPIO18 (RX) <--- RO do HW-097 POR DIVISOR 1k/2k
 *   ESP32-S3 GND         ---  GND do HW-097 e terra comum
 *
 *   HW-097 VCC  -> 5 V da placa controladora
 *   HW-097 A    -> CN3-5 RS-485(+)
 *   HW-097 B    -> CN3-6 RS-485(-)
 *   malha/dreno -> nao conectar
 *
 *   RO --[1k]--+--> GPIO18
 *               |
 *             [2k]
 *               |
 *              GND
 *
 * Alimentacao pelo 5 V da placa controladora. Com isso o GND do ESP32 e o
 * mesmo no do CN3-1, e a referencia de modo comum do RS-485 vem de graca
 * pelo proprio fio de alimentacao: nao ha fio de GND separado nem resistor
 * de 100 ohm nesta configuracao.
 *
 * Prever pico de ~300 mA na transmissao do WiFi: bulk de 470-1000 uF junto
 * ao ESP32, mais 100 nF ceramico.
 *
 * O divisor no RO e obrigatorio: o MAX485 e alimentado em 5 V e o GPIO18
 * do ESP32-S3 nao tolera 5 V. Em repouso, GPIO16 fica LOW (recepcao).
 *
 * ------------------------------------------------------------------
 * CONCORRENCIA
 * ------------------------------------------------------------------
 *   modbusTask   nucleo 1, prioridade 3 -> polling RS-485
 *   networkTask  nucleo 0, prioridade 1 -> associacao, push e pull HTTP
 *
 * Modbus fica no nucleo 1 porque a stack de WiFi do Arduino-ESP32 roda no
 * nucleo 0. Tres leituras com timeout de 250 ms somam 750 ms no pior caso;
 * num loop unico, junto com o HTTP, o ciclo de 1 s estouraria.
 *
 * Regra que nao pode ser violada: falha de WiFi NAO para o polling Modbus.
 * A integracao de energia mora aqui, nunca no hub, porque precisa da serie
 * continua a 1 Hz.
 */

// -------------------- Configuracao --------------------

constexpr uint32_t USB_SERIAL_BAUD = 115200;

constexpr int8_t RS485_TX_PIN = 17;
constexpr int8_t RS485_RX_PIN = 18;
constexpr int8_t RS485_DE_RE_PIN = 16;
constexpr uint32_t RS485_BAUD = 9600;
constexpr uint8_t MODBUS_SLAVE_ADDRESS = 1;
#define NODE_FIRMWARE_VERSION "2.0.0-dev"
#define NODE_PROTOCOL_VERSION 2

// ===================================================================
// MODULO ALVO DESTA GRAVACAO
// ===================================================================
// Esta e a UNICA linha que muda ao mover o no de um modulo para outro,
// e tem de casar com MODULO_TECNAL do hub, em
// ESP32S3-HUB/ESP32S3-HUB/Config.h.
//
//   1 = ModuloTECNAL_1 (bancada)    2 = ModuloTECNAL_2 (campo)
//
// Mudar so um dos lados deixa o no procurando um SoftAP que nao
// existe: ele nunca associa, nunca empurra, e o hub reporta
// ServoOnline:false -- indistinguivel de "no desligado". Pior: se o
// modulo antigo continuar ligado e ao alcance, o no volta a se
// associar A ELE e empurra a telemetria para o hub errado.
//
// Confira sempre o banner do boot das duas placas antes de instalar.
#define MODULO_TECNAL_ALVO 2

#if MODULO_TECNAL_ALVO == 1
  #define HUB_SSID     "ModuloTECNAL_1"
  #define HUB_PASSWORD "ModuloTECNAL_1"
#elif MODULO_TECNAL_ALVO == 2
  #define HUB_SSID     "ModuloTECNAL_2"
  #define HUB_PASSWORD "ModuloTECNAL_2"
#else
  #error "MODULO_TECNAL_ALVO deve ser 1 ou 2"
#endif

// HUB_HOST nao muda: o softAPConfig() do hub fixa 192.168.4.1 nos dois modulos.
static const char *HUB_HOST = "192.168.4.1";

#define WDT_TIMEOUT_S 10

// Motor ECMA-C20604ES
constexpr float MOTOR_RATED_TORQUE_NM = 1.27f;
constexpr float TWO_PI_OVER_60 = 0.10471975511965977f;

// Registradores ASDA-B2 (endereco = grupo * 0x100 + indice * 2)
constexpr uint16_t REG_ALARM_CODE = 0x0002;        // P0-01
constexpr uint16_t REG_SPEED_AND_LOAD = 0x0012;    // P0-09 e P0-10, 4 words
constexpr uint16_t REG_TORQUE_AND_STATUS = 0x0058; // P0-44..P0-46, 6 words
constexpr uint16_t REG_MONITOR_SELECTOR = 0x005A;  // P0-45
constexpr uint16_t MONITOR_CODE_TORQUE_FEEDBACK = 54;

// Controle direto de velocidade. Endereco = grupo * 0x100 + indice * 2.
constexpr uint16_t REG_CONTROL_MODE = 0x0102;        // P1-01
constexpr uint16_t REG_INTERNAL_SPEED_1 = 0x0112;    // P1-09, signed 32-bit, 0.1 rpm
constexpr uint16_t REG_DI_FUNCTION_BASE = 0x0214;    // P2-10 = DI1; +2 por DI
constexpr uint16_t REG_DI9_FUNCTION = 0x0248;        // P2-36 = DI9, fora do bloco
constexpr uint8_t  DI_COUNT = 9;                     // DI1..DI9, como em P3-06
constexpr uint16_t REG_PARAMETER_WRITE_MODE = 0x023C;// P2-30
constexpr uint16_t REG_SOFTWARE_DI_MASK = 0x030C;    // P3-06
constexpr uint16_t REG_SOFTWARE_DI_STATE = 0x040E;   // P4-07

constexpr uint16_t EXPECTED_CONTROL_MODE_SPEED = 0x0002;
constexpr uint16_t PARAMETER_WRITE_RAM_ONLY = 5;
constexpr uint16_t SOFTWARE_DI_MASK_PHYSICAL = 0x0000;
constexpr uint16_t MAX_MOTOR_RPM = 1000;

// P2-1x codifica a DI como tipo de contato no byte alto e funcao no byte baixo.
constexpr uint16_t DI_FUNCTION_MASK = 0x00FF;
constexpr uint16_t DI_CONTACT_NORMALLY_OPEN = 0x0100;
constexpr uint16_t DI_FUNCTION_SON = 0x01;
constexpr uint16_t DI_FUNCTION_SPD0 = 0x14;
constexpr uint16_t DI_FUNCTION_SPD1 = 0x15;

// P3-06 e P4-07 enderecam DIs por posicao (bit 0 = DI1), mas qual funcao mora
// em qual DI e configuracao do drive, nao constante do projeto: cada instalacao
// acomoda SPD0 no pino que sobra do CN1. Fixar DI1/DI3/DI4 obrigava a remapear
// DIs que a placa original ja usa, entao o mapa e lido do proprio drive.
struct DiProfile {
  uint16_t mask = 0;       // bits que P3-06 deve entregar ao software
  uint16_t stopState = 0;  // P4-07 com SON=0, SPD0=1, SPD1=0
  uint16_t runState = 0;   // P4-07 com SON=1, SPD0=1, SPD1=0
  bool valid = false;
};

// Escrito e lido apenas pela modbusTask, e uma vez no setup antes de as tarefas
// existirem, entao dispensa mutex.
DiProfile diProfile;

enum MotorControlRoute : uint8_t {
  MOTOR_ROUTE_UART_CN1 = 0,
  MOTOR_ROUTE_MODBUS = 1
};

enum MotorControlFault : int {
  MOTOR_FAULT_NONE = 0,
  MOTOR_FAULT_PROFILE_MISMATCH = 1,
  MOTOR_FAULT_MODBUS_APPLY = 2,
  MOTOR_FAULT_LEASE_EXPIRED = 3,
  MOTOR_FAULT_INVALID_COMMAND = 4,
  MOTOR_FAULT_DRIVE_UNAVAILABLE = 5
};

// Temporizacao Modbus
constexpr uint32_t RTU_SILENCE_MS = 12;
constexpr uint32_t RESPONSE_TIMEOUT_MS = 250;
constexpr uint32_t INTERBYTE_TIMEOUT_MS = 20;
constexpr uint8_t MAX_MODBUS_REGISTERS = 10;
constexpr size_t MAX_RESPONSE_SIZE = 5 + (2 * MAX_MODBUS_REGISTERS);

// Temporizacao das tarefas
constexpr uint32_t DEFAULT_POLL_MS = 1000;
constexpr uint32_t MIN_POLL_MS = 250;
constexpr uint32_t MAX_POLL_MS = 10000;
constexpr uint32_t PUSH_INTERVAL_MS = 1000;
constexpr uint32_t COMMAND_INTERVAL_MS = 500;
constexpr uint32_t WIFI_RETRY_MS = 5000;
constexpr uint32_t HTTP_TIMEOUT_MS = 2000;

// O intervalo de polling e comandado pelo hub e pode chegar a MAX_POLL_MS.
// Dormir o intervalo inteiro entre dois esp_task_wdt_reset() faria o watchdog
// reiniciar o no exatamente nos periodos longos que o contrato permite, entao a
// espera e fatiada e o watchdog alimentado a cada fatia.
constexpr uint32_t CONTROL_SERVICE_MS = 50;
constexpr uint32_t CONTROL_VERIFY_MS = 1000;
constexpr uint32_t PROFILE_RETRY_MS = 2000;
constexpr uint32_t MIN_MOTOR_LEASE_MS = 1000;
constexpr uint32_t MAX_MOTOR_LEASE_MS = 10000;

// -------------------- Estado compartilhado --------------------

HardwareSerial rs485Serial(1);
SemaphoreHandle_t stateMutex = NULL;

struct SharedState {
  float rpm = 0.0f;
  float torquePct = 0.0f;
  float torqueNm = 0.0f;
  float loadPct = 0.0f;
  float powerW = 0.0f;
  double energyJ = 0.0;
  int   driveState = 0;             // 0=OFF 1=READY 2=SON 3=ALARM
  int   alarmCode = 0;
  uint32_t commOk = 0;
  uint32_t commErr = 0;
  bool  haveSample = false;         // ja houve ao menos uma leitura valida
  uint32_t pollIntervalMs = DEFAULT_POLL_MS;
  bool  resetEnergyRequest = false; // consumido pela modbusTask
  bool  controlCapable = false;
  uint32_t motorCommandId = 0;
  uint8_t requestedMotorRoute = MOTOR_ROUTE_MODBUS;
  uint16_t requestedMotorRpm = 0;
  bool  requestedMotorEnable = false;
  uint32_t motorLeaseMs = 0;
  uint32_t motorCommandReceivedAt = 0;
  bool  haveMotorCommand = false;
  bool  motorCommandDirty = false;
  uint32_t motorCommandAck = 0;
  int motorRouteAck = -1;
  uint16_t motorAppliedRpm = 0;
  bool  motorControlActive = false;
  int   motorControlFault = MOTOR_FAULT_NONE;
};

SharedState shared;

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

int32_t combineSigned32(uint16_t lowWord, uint16_t highWord) {
  const uint32_t value = (static_cast<uint32_t>(highWord) << 16U) |
                         static_cast<uint32_t>(lowWord);
  return static_cast<int32_t>(value);
}

int driveStateCode(uint16_t status) {
  constexpr uint16_t STATUS_SRDY = (1U << 0U);
  constexpr uint16_t STATUS_SON  = (1U << 1U);
  constexpr uint16_t STATUS_ALRM = (1U << 6U);

  if ((status & STATUS_ALRM) != 0U) return 3;
  if ((status & STATUS_SON)  != 0U) return 2;
  if ((status & STATUS_SRDY) != 0U) return 1;
  return 0;
}

// Extrai o valor de uma chave num JSON plano, no mesmo espirito do
// getValueFromJson do hub. Suficiente para {"reset_energy":1,"poll_ms":2000}.
String jsonValue(const String &json, const char *key) {
  String pattern = String("\"") + key + "\"";
  const int keyAt = json.indexOf(pattern);
  if (keyAt < 0) return "";

  const int colonAt = json.indexOf(':', keyAt + pattern.length());
  if (colonAt < 0) return "";

  int begin = colonAt + 1;
  while (begin < (int)json.length() &&
         (json[begin] == ' ' || json[begin] == '"')) {
    ++begin;
  }

  int end = begin;
  while (end < (int)json.length() &&
         json[end] != ',' && json[end] != '}' && json[end] != '"') {
    ++end;
  }

  return json.substring(begin, end);
}

bool parseUnsignedStrict(String value, uint32_t &result) {
  value.trim();
  if (value.length() == 0) return false;
  uint32_t parsed = 0;
  for (size_t index = 0; index < value.length(); ++index) {
    const char c = value[index];
    if (c < '0' || c > '9') return false;
    const uint8_t digit = static_cast<uint8_t>(c - '0');
    if (parsed > (UINT32_MAX - digit) / 10U) return false;
    parsed = (parsed * 10U) + digit;
  }
  result = parsed;
  return true;
}

// -------------------- Modbus RTU --------------------

bool writeSingleRegister(uint16_t address, uint16_t value);
bool writeMultipleRegisters(uint16_t address, const uint16_t *values, uint16_t count);
bool ensureTorqueMapping();

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
  vTaskDelay(pdMS_TO_TICKS(RTU_SILENCE_MS));

  digitalWrite(RS485_DE_RE_PIN, HIGH);

  if (rs485Serial.write(request, sizeof(request)) != sizeof(request)) {
    digitalWrite(RS485_DE_RE_PIN, LOW);
    return false;
  }
  rs485Serial.flush();

  // flush() espera o ultimo byte chegar ao periférico UART. Mantemos o driver
  // por mais um caractere antes de voltar a recepcao, exatamente como no
  // autoscan que fechou a bancada com o HW-097 em 2026-09-01.
  delayMicroseconds((11UL * 1000000UL / RS485_BAUD) + 150UL);
  digitalWrite(RS485_DE_RE_PIN, LOW);

  // Com DE e /RE em curto, o receptor fica desabilitado durante a transmissao.
  // O divisor pode produzir bytes 0x00 nesse intervalo; descarte-os antes da
  // resposta do drive, cujo atraso P3-07 foi confirmado em 100.
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

    vTaskDelay(pdMS_TO_TICKS(1));
  }

  vTaskDelay(pdMS_TO_TICKS(RTU_SILENCE_MS));

  if (received < 5) return false;
  if (response[0] != MODBUS_SLAVE_ADDRESS) return false;

  const size_t frameLength = ((response[1] & 0x80U) != 0U)
    ? 5U
    : static_cast<size_t>(response[2]) + 5U;

  if (frameLength > received || frameLength > sizeof(response)) return false;

  const uint16_t receivedCrc =
    static_cast<uint16_t>(response[frameLength - 2U]) |
    (static_cast<uint16_t>(response[frameLength - 1U]) << 8U);

  if (receivedCrc != modbusCrc16(response, frameLength - 2U)) return false;

  // Resposta de excecao: quadro valido, mas sem dados.
  if ((response[1] & 0x80U) != 0U) return false;
  if (response[1] != 0x03U) return false;

  const uint8_t expectedByteCount = static_cast<uint8_t>(registerCount * 2U);
  if (response[2] != expectedByteCount ||
      frameLength != (5U + expectedByteCount)) {
    return false;
  }

  for (uint16_t index = 0; index < registerCount; ++index) {
    const size_t dataIndex = 3U + (2U * index);
    registers[index] = (static_cast<uint16_t>(response[dataIndex]) << 8U) |
                       static_cast<uint16_t>(response[dataIndex + 1U]);
  }

  return true;
}

// Uma escrita reprovada so dizia "false". Se o drive responde excecao, o motivo
// esta no quadro e e o unico jeito de separar recusa do drive de problema de
// linha: leitura e escrita 06H tem o mesmo tamanho de quadro, entao se a
// leitura passa e a escrita nao, a linha esta boa e a recusa e do drive.
void logModbusWriteFailure(const char *what, uint16_t address,
                           const uint8_t *response, size_t received) {
  if (received == 0) {
    Serial.printf("[MODBUS] %s em %04X: sem resposta.\n", what, address);
    return;
  }
  if (received >= 3 && (response[1] & 0x80U) != 0U) {
    const char *motivo;
    switch (response[2]) {
      case 0x01: motivo = "funcao ilegal"; break;
      case 0x02: motivo = "endereco ilegal"; break;
      case 0x03: motivo = "valor ilegal"; break;
      case 0x04: motivo = "falha do dispositivo"; break;
      default:   motivo = "codigo nao catalogado"; break;
    }
    Serial.printf("[MODBUS] %s em %04X: excecao %02X (%s).\n",
                  what, address, response[2], motivo);
    return;
  }
  Serial.printf("[MODBUS] %s em %04X: recebido %u bytes:", what, address,
                static_cast<unsigned>(received));
  for (size_t index = 0; index < received; ++index) {
    Serial.printf(" %02X", response[index]);
  }
  Serial.println();
}

// Escreve um unico registrador pela funcao 06H.
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
  vTaskDelay(pdMS_TO_TICKS(RTU_SILENCE_MS));

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
    vTaskDelay(pdMS_TO_TICKS(1));
  }

  vTaskDelay(pdMS_TO_TICKS(RTU_SILENCE_MS));

  if (received != sizeof(response)) {
    logModbusWriteFailure("06H", address, response, received);
    return false;
  }

  const uint16_t receivedCrc =
    static_cast<uint16_t>(response[6]) | (static_cast<uint16_t>(response[7]) << 8U);
  if (receivedCrc != modbusCrc16(response, 6)) {
    // Sem os bytes nao da para separar eco da propria transmissao, quadro
    // deslocado por resto de transacao anterior e resposta truncada.
    Serial.print(F("[MODBUS] 06H: enviado"));
    for (size_t index = 0; index < sizeof(request); ++index) {
      Serial.printf(" %02X", request[index]);
    }
    logModbusWriteFailure("06H CRC", address, response, received);
    return false;
  }

  if (memcmp(request, response, 6) != 0) {
    logModbusWriteFailure("06H", address, response, received);
    return false;
  }
  return true;
}

// Escreve words contiguas por 10H. P1-09 ocupa duas words e precisa ser
// atualizado atomicamente: primeiro a word baixa, depois a alta, como definido
// pelo mapa de parametros do ASDA-B2.
bool writeMultipleRegisters(uint16_t address, const uint16_t *values, uint16_t count) {
  if (values == nullptr || count == 0 || count > MAX_MODBUS_REGISTERS) return false;

  uint8_t request[9 + (2 * MAX_MODBUS_REGISTERS)] = {0};
  request[0] = MODBUS_SLAVE_ADDRESS;
  request[1] = 0x10;
  request[2] = static_cast<uint8_t>(address >> 8U);
  request[3] = static_cast<uint8_t>(address & 0x00FFU);
  request[4] = static_cast<uint8_t>(count >> 8U);
  request[5] = static_cast<uint8_t>(count & 0x00FFU);
  request[6] = static_cast<uint8_t>(count * 2U);
  for (uint16_t index = 0; index < count; ++index) {
    request[7 + (2 * index)] = static_cast<uint8_t>(values[index] >> 8U);
    request[8 + (2 * index)] = static_cast<uint8_t>(values[index] & 0x00FFU);
  }
  const size_t requestLength = 9U + (2U * count);
  const uint16_t requestCrc = modbusCrc16(request, requestLength - 2U);
  request[requestLength - 2U] = static_cast<uint8_t>(requestCrc & 0x00FFU);
  request[requestLength - 1U] = static_cast<uint8_t>(requestCrc >> 8U);

  while (rs485Serial.available() > 0) (void)rs485Serial.read();
  vTaskDelay(pdMS_TO_TICKS(RTU_SILENCE_MS));
  digitalWrite(RS485_DE_RE_PIN, HIGH);
  if (rs485Serial.write(request, requestLength) != requestLength) {
    digitalWrite(RS485_DE_RE_PIN, LOW);
    return false;
  }
  rs485Serial.flush();
  delayMicroseconds((11UL * 1000000UL / RS485_BAUD) + 150UL);
  digitalWrite(RS485_DE_RE_PIN, LOW);
  delayMicroseconds(200);

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
    vTaskDelay(pdMS_TO_TICKS(1));
  }
  vTaskDelay(pdMS_TO_TICKS(RTU_SILENCE_MS));
  if (received != sizeof(response)) {
    logModbusWriteFailure("10H", address, response, received);
    return false;
  }

  const uint16_t receivedCrc = static_cast<uint16_t>(response[6]) |
                               (static_cast<uint16_t>(response[7]) << 8U);
  if (receivedCrc != modbusCrc16(response, 6)) {
    Serial.print(F("[MODBUS] 10H: enviado"));
    for (size_t index = 0; index < requestLength; ++index) {
      Serial.printf(" %02X", request[index]);
    }
    logModbusWriteFailure("10H CRC", address, response, received);
    return false;
  }
  if (response[0] != MODBUS_SLAVE_ADDRESS || response[1] != 0x10U ||
      response[2] != request[2] || response[3] != request[3] ||
      response[4] != request[4] || response[5] != request[5]) {
    logModbusWriteFailure("10H", address, response, received);
    return false;
  }
  return true;
}

bool writeAndConfirmSingle(uint16_t address, uint16_t value) {
  if (!writeSingleRegister(address, value)) return false;
  uint16_t confirmed = 0;
  return readHoldingRegisters(address, 1, &confirmed) && confirmed == value;
}

bool writeAndConfirmInternalSpeed(uint16_t rpm) {
  if (rpm > MAX_MOTOR_RPM) return false;
  const uint32_t raw = static_cast<uint32_t>(rpm) * 10U;
  const uint16_t words[2] = {
    static_cast<uint16_t>(raw & 0xFFFFU),
    static_cast<uint16_t>(raw >> 16U)
  };
  if (!writeMultipleRegisters(REG_INTERNAL_SPEED_1, words, 2)) {
    Serial.println(F("[MOTOR] P1-09: escrita 10H sem resposta valida."));
    return false;
  }
  uint16_t confirmed[2] = {0};
  if (!readHoldingRegisters(REG_INTERNAL_SPEED_1, 2, confirmed)) {
    Serial.println(F("[MOTOR] P1-09: leitura de confirmacao falhou."));
    return false;
  }
  if (confirmed[0] != words[0] || confirmed[1] != words[1]) {
    Serial.printf("[MOTOR] P1-09: escrito %04X%04X, lido %04X%04X.\n",
                  words[1], words[0], confirmed[1], confirmed[0]);
    return false;
  }
  return true;
}

// P4-07 nao e um registrador espelho: a escrita define as DIs por comunicacao,
// mas a leitura devolve o estado combinado das DIs do drive, ja com P3-06
// aplicado. Exigir que a leitura repetisse o valor escrito reprovava escritas
// corretas sempre que qualquer DI fora da mascara estivesse ativa, e reprovava
// sempre quando P3-06 estava fisico. So os bits que P3-06 entrega ao software
// tem eco garantido, entao apenas eles sao comparados.
bool writeSoftwareDiState(uint16_t state, uint16_t mask) {
  if (!writeSingleRegister(REG_SOFTWARE_DI_STATE, state)) return false;
  uint16_t confirmed = 0;
  if (!readHoldingRegisters(REG_SOFTWARE_DI_STATE, 1, &confirmed)) return false;
  if ((confirmed & mask) != (state & mask)) {
    Serial.printf("[MOTOR] P4-07: escrito %04X, lido %04X, mascara %04X.\n",
                  state, confirmed, mask);
    return false;
  }
  return true;
}

// DI1..DI8 ficam em P2-10..P2-17, de duas em duas words; DI9 mora em P2-36.
uint16_t diFunctionRegister(uint8_t index) {
  if (index >= 8) return REG_DI9_FUNCTION;
  return static_cast<uint16_t>(REG_DI_FUNCTION_BASE + (index * 2U));
}

// Varre as DIs e monta mascara e estados de P4-07 a partir de onde SON, SPD0 e
// SPD1 realmente estao. driveResponded separa drive mudo de drive que respondeu
// mas nao tem as funcoes necessarias, para o chamador escolher a falha certa.
bool discoverDiProfile(DiProfile &profile, bool &driveResponded) {
  profile = DiProfile();
  driveResponded = false;

  uint16_t functions[DI_COUNT] = {0};
  int sonBit = -1;
  int spd0Bit = -1;
  int spd1Bit = -1;

  for (uint8_t index = 0; index < DI_COUNT; ++index) {
    if (!readHoldingRegisters(diFunctionRegister(index), 1, &functions[index])) return false;
    // So acionamos contato tipo A: num contato tipo B o bit 1 de P4-07
    // desativaria a funcao, e inverter isso em silencio seria perigoso.
    if ((functions[index] & DI_CONTACT_NORMALLY_OPEN) == 0) continue;
    switch (functions[index] & DI_FUNCTION_MASK) {
      case DI_FUNCTION_SON:  sonBit  = index; break;
      case DI_FUNCTION_SPD0: spd0Bit = index; break;
      case DI_FUNCTION_SPD1: spd1Bit = index; break;
      default: break;
    }
  }
  driveResponded = true;

  if (sonBit < 0 || spd0Bit < 0) {
    Serial.printf("[MOTOR] Perfil recusado: SON=%s, SPD0=%s. Sem SPD0 o par SPD1/SPD0 "
                  "fica em 00 e o drive segue no comando analogico do CN1.\n",
                  sonBit < 0 ? "ausente" : "ok", spd0Bit < 0 ? "ausente" : "ok");
    for (uint8_t index = 0; index < DI_COUNT; ++index) {
      Serial.printf("[MOTOR]   DI%u = %04X%s\n", index + 1, functions[index],
                    functions[index] == 0 ? "  (livre)" : "");
    }
    return false;
  }

  profile.mask = static_cast<uint16_t>((1U << sonBit) | (1U << spd0Bit));
  // SPD1 so entra na mascara se existir: DI sem funcao ja vale zero, mas uma
  // DI com SPD1 no CN1 selecionaria P1-10/P1-11 pelas nossas costas.
  if (spd1Bit >= 0) profile.mask |= static_cast<uint16_t>(1U << spd1Bit);

  profile.stopState = static_cast<uint16_t>(1U << spd0Bit);
  profile.runState = static_cast<uint16_t>(profile.stopState | (1U << sonBit));
  profile.valid = true;

  char spd1Label[12];
  if (spd1Bit >= 0) snprintf(spd1Label, sizeof(spd1Label), "DI%d", spd1Bit + 1);
  else snprintf(spd1Label, sizeof(spd1Label), "nenhuma");
  Serial.printf("[MOTOR] Mapa de DIs: SON=DI%d SPD0=DI%d SPD1=%s -> "
                "P3-06=%04X, P4-07 run=%04X stop=%04X\n",
                sonBit + 1, spd0Bit + 1, spd1Label,
                profile.mask, profile.runState, profile.stopState);
  return true;
}

bool validateDirectControlProfile(bool &driveResponded) {
  uint16_t controlMode = 0;
  driveResponded = readHoldingRegisters(REG_CONTROL_MODE, 1, &controlMode);
  if (!driveResponded) return false;

  if (controlMode != EXPECTED_CONTROL_MODE_SPEED) {
    Serial.printf("[MOTOR] Perfil recusado: P1-01=%04X, esperado %04X (modo velocidade).\n",
                  controlMode, EXPECTED_CONTROL_MODE_SPEED);
    return false;
  }
  return discoverDiProfile(diProfile, driveResponded);
}

bool ensureRamOnlyWrites() {
  uint16_t mode = 0;
  if (!readHoldingRegisters(REG_PARAMETER_WRITE_MODE, 1, &mode)) return false;
  // Se P2-30 ja vale 5 esta funcao nunca escreve, e uma escrita 06H bem
  // sucedida deixa de ser evidencia disponivel. Registrar o valor lido evita
  // concluir que "escrita funciona" a partir de um caminho so de leitura.
  if (mode == PARAMETER_WRITE_RAM_ONLY) return true;
  Serial.printf("[MOTOR] P2-30 lido como %u; escrevendo %u.\n",
                mode, PARAMETER_WRITE_RAM_ONLY);
  return writeAndConfirmSingle(REG_PARAMETER_WRITE_MODE, PARAMETER_WRITE_RAM_ONLY);
}

bool applyMotorCommand(uint16_t rpm, bool enable) {
  if (enable && rpm == 0) return false;
  if (!diProfile.valid) return false;
  if (!ensureRamOnlyWrites()) {
    Serial.println(F("[MOTOR] Aplicacao: falha em P2-30."));
    return false;
  }
  uint16_t currentMask = 0;
  if (!readHoldingRegisters(REG_SOFTWARE_DI_MASK, 1, &currentMask)) {
    Serial.println(F("[MOTOR] Aplicacao: P3-06 ilegivel."));
    return false;
  }
  if (currentMask != diProfile.mask) {
    // Pre-carrega SON=0/SPD0=1/SPD1=0 apenas na tomada de controle. Fazer isso
    // a cada troca de rpm desligaria e religaria SON entre dois setpoints.
    // Enquanto P3-06 ainda esta fisico a leitura de P4-07 reflete o CN1, entao
    // a pre-carga vai sem confirmacao e so e conferida apos a troca da mascara.
    if (!writeSingleRegister(REG_SOFTWARE_DI_STATE, diProfile.stopState)) {
      Serial.println(F("[MOTOR] Aplicacao: falha ao pre-carregar P4-07."));
      return false;
    }
    if (!writeAndConfirmSingle(REG_SOFTWARE_DI_MASK, diProfile.mask)) {
      Serial.println(F("[MOTOR] Aplicacao: falha em P3-06 (tomada de controle)."));
      return false;
    }
    if (!writeSoftwareDiState(diProfile.stopState, diProfile.mask)) {
      Serial.println(F("[MOTOR] Aplicacao: falha em P4-07 (pre-carga com SON=0)."));
      return false;
    }
    Serial.printf("[MOTOR] Controle assumido: P3-06=%04X.\n", diProfile.mask);
  }
  if (!writeAndConfirmInternalSpeed(enable ? rpm : 0)) {
    Serial.printf("[MOTOR] Aplicacao: falha em P1-09 (%u rpm).\n",
                  static_cast<unsigned>(enable ? rpm : 0));
    return false;
  }
  if (!writeSoftwareDiState(enable ? diProfile.runState : diProfile.stopState,
                            diProfile.mask)) {
    Serial.println(F("[MOTOR] Aplicacao: falha em P4-07 (SON)."));
    return false;
  }
  return true;
}

bool forceMotorSafeStop() {
  if (!ensureRamOnlyWrites()) return false;
  const bool speedStopped = writeAndConfirmInternalSpeed(0);
  // A mascara em vigor decide quais bits de P4-07 sao nossos. Com P3-06 fisico
  // nenhum e, e a comparacao vazia diz a verdade: nao ha SON de software aqui.
  uint16_t mask = 0;
  const bool maskRead = readHoldingRegisters(REG_SOFTWARE_DI_MASK, 1, &mask);
  // Com o mapa ainda desconhecido stopState e zero, que zera todas as DIs de
  // software: e a direcao segura, pois derruba SON de qualquer jeito.
  const bool sonRemoved = maskRead && writeSoftwareDiState(diProfile.stopState, mask);
  return speedStopped && sonRemoved;
}

// Devolve DI1/DI3/DI4 aos terminais fisicos do CN1. O Hub ja enviou 0V/0A
// pela UART antes de solicitar esta transicao, mas ainda paramos P1-09 e SON
// enquanto o mask de software esta ativo. P3-06=0 e sempre o ultimo passo.
bool releaseMotorToUartCn1() {
  uint16_t mask = 0;
  if (!readHoldingRegisters(REG_SOFTWARE_DI_MASK, 1, &mask)) {
    Serial.println(F("[MOTOR] Liberacao para UART/CN1: P3-06 ilegivel."));
    return false;
  }
  // P3-06 ja fisico significa que quem comanda e o CN1. Reescrever P1-09 e
  // P4-07 nesse estado nao libera nada e ainda mexe num drive que pertence a
  // placa original. Sem esta saida a liberacao falhava para sempre e o Hub
  // jamais recebia o ACK da via.
  if (mask == SOFTWARE_DI_MASK_PHYSICAL) return true;

  if (!ensureRamOnlyWrites()) {
    Serial.println(F("[MOTOR] Liberacao para UART/CN1: falha em P2-30."));
    return false;
  }
  if (!writeAndConfirmInternalSpeed(0)) {
    Serial.println(F("[MOTOR] Liberacao para UART/CN1: falha em P1-09."));
    return false;
  }
  if (!writeSoftwareDiState(diProfile.stopState, mask)) {
    Serial.println(F("[MOTOR] Liberacao para UART/CN1: falha em P4-07."));
    return false;
  }
  if (!writeAndConfirmSingle(REG_SOFTWARE_DI_MASK, SOFTWARE_DI_MASK_PHYSICAL)) {
    Serial.println(F("[MOTOR] Liberacao para UART/CN1: falha em P3-06."));
    return false;
  }
  return true;
}

void publishMotorControlStatus(bool capable, uint32_t ack, uint16_t appliedRpm,
                               bool active, int routeAck, int fault,
                               bool clearDirty) {
  if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
    shared.controlCapable = capable;
    shared.motorCommandAck = ack;
    shared.motorRouteAck = routeAck;
    shared.motorAppliedRpm = appliedRpm;
    shared.motorControlActive = active;
    shared.motorControlFault = fault;
    if (clearDirty) shared.motorCommandDirty = false;
    xSemaphoreGive(stateMutex);
  }
}

// Se o no reiniciar enquanto o drive ainda estiver sob controle de software,
// remove SON antes mesmo do WiFi. Se P3-06 estiver em modo fisico, nao interfere
// com a instalacao antiga; isso torna seguro gravar o driver antes do Hub.
void recoverStaleDirectControlAtBoot() {
  uint16_t mask = 0;
  if (!readHoldingRegisters(REG_SOFTWARE_DI_MASK, 1, &mask)) {
    Serial.println(F("[MOTOR] Nao foi possivel verificar P3-06 no boot."));
    return;
  }
  // Qualquer bit em P3-06 so pode ter vindo de uma sessao direta anterior deste
  // no: a instalacao original mantem todas as DIs no CN1 fisico.
  if (mask == SOFTWARE_DI_MASK_PHYSICAL) {
    Serial.println(F("[MOTOR] P3-06 ainda fisico; driver permanece passivo."));
    return;
  }
  bool driveResponded = false;
  if (!discoverDiProfile(diProfile, driveResponded)) {
    Serial.println(F("[MOTOR] Mapa de DIs indisponivel no boot; zerando P4-07 mesmo assim."));
  }
  if (forceMotorSafeStop()) {
    Serial.println(F("[MOTOR] Controle direto antigo detectado; SON removido no boot."));
  } else {
    Serial.println(F("[MOTOR] FALHA ao remover SON de uma sessao anterior."));
  }
}

// Garante P0-45 = 54 antes de acreditar em P0-44. Le antes de escrever: em
// regime normal e uma leitura por ciclo e nenhuma escrita. Chamada a cada
// amostra para sobreviver a um religamento do drive sem reiniciar a placa.
bool ensureTorqueMapping() {
  uint16_t selector = 0;

  if (!readHoldingRegisters(REG_MONITOR_SELECTOR, 1, &selector)) return false;
  if (selector == MONITOR_CODE_TORQUE_FEEDBACK) return true;

  Serial.printf("[SERVO] P0-45 estava em %u; reescrevendo 54.\n", selector);

  if (!writeSingleRegister(REG_MONITOR_SELECTOR, MONITOR_CODE_TORQUE_FEEDBACK)) {
    Serial.println(F("[SERVO] Falha ao escrever P0-45."));
    return false;
  }

  // O eco de 06H prova que o quadro chegou, nao que o drive reteve o valor.
  vTaskDelay(pdMS_TO_TICKS(RTU_SILENCE_MS));
  if (!readHoldingRegisters(REG_MONITOR_SELECTOR, 1, &selector)) return false;
  if (selector != MONITOR_CODE_TORQUE_FEEDBACK) return false;

  Serial.println(F("[SERVO] P0-45 = 54 confirmado por leitura."));
  return true;
}

void serviceMotorControl() {
  static uint32_t lastProfileAttemptMs = 0;
  static uint32_t lastControlVerifyMs = 0;
  static uint32_t lastApplyAttemptMs = 0;

  bool haveCommand = false;
  bool commandDirty = false;
  bool capable = false;
  bool active = false;
  uint32_t commandId = 0;
  uint32_t ack = 0;
  uint8_t requestedRoute = MOTOR_ROUTE_MODBUS;
  int routeAck = -1;
  uint32_t receivedAt = 0;
  uint32_t leaseMs = 0;
  uint16_t requestedRpm = 0;
  uint16_t appliedRpm = 0;
  bool requestedEnable = false;

  if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
    haveCommand = shared.haveMotorCommand;
    commandDirty = shared.motorCommandDirty;
    capable = shared.controlCapable;
    active = shared.motorControlActive;
    commandId = shared.motorCommandId;
    ack = shared.motorCommandAck;
    requestedRoute = shared.requestedMotorRoute;
    routeAck = shared.motorRouteAck;
    receivedAt = shared.motorCommandReceivedAt;
    leaseMs = shared.motorLeaseMs;
    requestedRpm = shared.requestedMotorRpm;
    appliedRpm = shared.motorAppliedRpm;
    requestedEnable = shared.requestedMotorEnable;
    xSemaphoreGive(stateMutex);
  }

  if (!haveCommand) return; // protocolo antigo: permanecer totalmente passivo

  const uint32_t nowMs = millis();
  if (leaseMs < MIN_MOTOR_LEASE_MS || leaseMs > MAX_MOTOR_LEASE_MS ||
      (nowMs - receivedAt) > leaseMs) {
    // Uma via UART/CN1 ja liberada nao pertence a este no: perder WiFi nao
    // deve desligar o motor que a placa original esta comandando. Durante uma
    // tomada/liberacao incompleta, porem, o no ainda garante a parada direta.
    const bool stillOwnsDrive = requestedRoute == MOTOR_ROUTE_MODBUS ||
                                 routeAck != MOTOR_ROUTE_UART_CN1 ||
                                 active || appliedRpm != 0;
    if (stillOwnsDrive && (nowMs - lastApplyAttemptMs) >= 250U) {
      lastApplyAttemptMs = nowMs;
      const bool stopped = (!active && appliedRpm == 0) || forceMotorSafeStop();
      publishMotorControlStatus(capable, ack,
                                stopped ? 0 : appliedRpm,
                                stopped ? false : active,
                                routeAck, MOTOR_FAULT_LEASE_EXPIRED, false);
      if (stopped && (active || appliedRpm != 0)) {
        Serial.println(F("[MOTOR] Lease expirou: P1-09=0 e SON removido."));
      } else if (!stopped) {
        Serial.println(F("[MOTOR] FALHA na parada por lease expirado."));
      }
    }
    return;
  }

  if (requestedRoute == MOTOR_ROUTE_UART_CN1) {
    bool needsRelease = commandDirty || ack != commandId ||
                        routeAck != MOTOR_ROUTE_UART_CN1 || active || appliedRpm != 0;
    if (!needsRelease && (lastControlVerifyMs == 0 ||
                          (nowMs - lastControlVerifyMs) >= CONTROL_VERIFY_MS)) {
      lastControlVerifyMs = nowMs;
      uint16_t mask = 0;
      needsRelease = !readHoldingRegisters(REG_SOFTWARE_DI_MASK, 1, &mask) ||
                     mask != SOFTWARE_DI_MASK_PHYSICAL;
    }

    if (!needsRelease || (nowMs - lastApplyAttemptMs) < 250U) return;
    lastApplyAttemptMs = nowMs;

    if (releaseMotorToUartCn1()) {
      publishMotorControlStatus(false, commandId, 0, false,
                                MOTOR_ROUTE_UART_CN1, MOTOR_FAULT_NONE, true);
      Serial.printf("[MOTOR] cmd_id=%lu aplicado: controle liberado para UART/CN1.\n",
                    static_cast<unsigned long>(commandId));
    } else {
      const bool stopped = forceMotorSafeStop();
      publishMotorControlStatus(capable, ack,
                                stopped ? 0 : appliedRpm,
                                stopped ? false : active,
                                routeAck, MOTOR_FAULT_MODBUS_APPLY, false);
      Serial.printf("[MOTOR] Falha ao liberar UART/CN1 para cmd_id=%lu; parada direta solicitada.\n",
                    static_cast<unsigned long>(commandId));
    }
    return;
  }

  if (!capable && (lastProfileAttemptMs == 0 ||
                   (nowMs - lastProfileAttemptMs) >= PROFILE_RETRY_MS)) {
    lastProfileAttemptMs = nowMs;
    bool driveResponded = false;
    capable = validateDirectControlProfile(driveResponded);
    publishMotorControlStatus(capable, ack, appliedRpm, active, routeAck,
                              capable ? MOTOR_FAULT_NONE :
                                (driveResponded ? MOTOR_FAULT_PROFILE_MISMATCH
                                                : MOTOR_FAULT_DRIVE_UNAVAILABLE),
                              false);
    if (capable) Serial.println(F("[MOTOR] Perfil direto confirmado; controle habilitado."));
  }
  if (!capable) return;

  bool needsApply = commandDirty || routeAck != MOTOR_ROUTE_MODBUS ||
                    (requestedEnable != active) ||
                    ((requestedEnable ? requestedRpm : 0) != appliedRpm);

  if (!needsApply && (lastControlVerifyMs == 0 ||
                      (nowMs - lastControlVerifyMs) >= CONTROL_VERIFY_MS)) {
    lastControlVerifyMs = nowMs;
    uint16_t mask = 0;
    uint16_t state = 0;
    const uint16_t expectedState =
        requestedEnable ? diProfile.runState : diProfile.stopState;
    if (!readHoldingRegisters(REG_SOFTWARE_DI_MASK, 1, &mask) ||
        !readHoldingRegisters(REG_SOFTWARE_DI_STATE, 1, &state) ||
        mask != diProfile.mask ||
        (state & diProfile.mask) != (expectedState & diProfile.mask)) {
      needsApply = true; // drive reiniciou ou perdeu o modo volatil
      active = false;
      appliedRpm = 0;
    }
  }

  if (!needsApply || (nowMs - lastApplyAttemptMs) < 250U) return;
  lastApplyAttemptMs = nowMs;

  if (applyMotorCommand(requestedRpm, requestedEnable)) {
    publishMotorControlStatus(true, commandId,
                              requestedEnable ? requestedRpm : 0,
                              requestedEnable, MOTOR_ROUTE_MODBUS,
                              MOTOR_FAULT_NONE, true);
    Serial.printf("[MOTOR] cmd_id=%lu aplicado: %u rpm, enable=%u\n",
                  static_cast<unsigned long>(commandId),
                  static_cast<unsigned>(requestedEnable ? requestedRpm : 0),
                  requestedEnable ? 1U : 0U);
  } else {
    const bool stopped = forceMotorSafeStop();
    publishMotorControlStatus(true, ack,
                              stopped ? 0 : appliedRpm,
                              stopped ? false : active,
                              routeAck, MOTOR_FAULT_MODBUS_APPLY, false);
    Serial.printf("[MOTOR] Falha ao aplicar cmd_id=%lu; parada segura solicitada.\n",
                  static_cast<unsigned long>(commandId));
  }
}


// -------------------- Tarefa Modbus --------------------

void modbusTask(void *parameter) {
  (void)parameter;
  esp_task_wdt_add(NULL);

  double energyJ = 0.0;
  double previousPowerW = 0.0;
  uint32_t previousSampleMs = 0;
  bool havePreviousPower = false;
  uint32_t commOk = 0;
  uint32_t commErr = 0;
  uint32_t lastPollMs = 0;

  for (;;) {
    esp_task_wdt_reset();

    uint32_t pollMs = DEFAULT_POLL_MS;
    if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
      pollMs = shared.pollIntervalMs;
      if (shared.resetEnergyRequest) {
        shared.resetEnergyRequest = false;
        energyJ = 0.0;
        havePreviousPower = false;
        shared.energyJ = 0.0;
        Serial.println(F("[SERVO] Energia acumulada zerada por comando do hub."));
      }
      xSemaphoreGive(stateMutex);
    }

    // Unico dono da UART: comando e telemetria nunca disputam o transceiver.
    serviceMotorControl();

    const uint32_t loopNowMs = millis();
    if (lastPollMs != 0 && (loopNowMs - lastPollMs) < pollMs) {
      vTaskDelay(pdMS_TO_TICKS(CONTROL_SERVICE_MS));
      continue;
    }
    lastPollMs = loopNowMs;

    uint16_t speedAndLoad[4] = {0};
    uint16_t torqueAndStatus[6] = {0};
    uint16_t alarmCode = 0;

    // Sem P0-45 = 54, P0-44 devolve a posicao em pulsos e a amostra inteira
    // seria publicada com um torque plausivel e errado. Uma amostra sem o
    // mapeamento vale menos que nenhuma amostra.
    const bool mappingOk = ensureTorqueMapping();

    const bool ok =
      mappingOk &&
      readHoldingRegisters(REG_SPEED_AND_LOAD, 4, speedAndLoad) &&
      readHoldingRegisters(REG_TORQUE_AND_STATUS, 6, torqueAndStatus) &&
      readHoldingRegisters(REG_ALARM_CODE, 1, &alarmCode);

    if (ok) {
      commOk += 3;

      const int32_t speedRaw = combineSigned32(speedAndLoad[0], speedAndLoad[1]);
      const int32_t loadRaw  = combineSigned32(speedAndLoad[2], speedAndLoad[3]);
      const int32_t torqueRaw = combineSigned32(torqueAndStatus[0], torqueAndStatus[1]);
      const uint16_t status = torqueAndStatus[4]; // P0-46 em 0x005C

      const double rpm = static_cast<double>(speedRaw) * 0.1;
      const double torquePct = static_cast<double>(torqueRaw) * 0.1;
      const double torqueNm = static_cast<double>(torqueRaw) *
                              (static_cast<double>(MOTOR_RATED_TORQUE_NM) / 1000.0);
      const double powerW = torqueNm * rpm * static_cast<double>(TWO_PI_OVER_60);

      // Integracao trapezoidal com rejeicao de lacuna: intervalos muito
      // maiores que o periodo nominal indicam amostras perdidas, e integrar
      // por cima deles inventaria energia que nao foi medida.
      const uint32_t nowMs = millis();
      if (havePreviousPower) {
        const uint32_t elapsedMs = nowMs - previousSampleMs;
        if (elapsedMs <= (pollMs * 5U) / 2U) {
          const double elapsedSeconds = static_cast<double>(elapsedMs) / 1000.0;
          energyJ += ((previousPowerW + powerW) * 0.5) * elapsedSeconds;
        }
      }
      previousPowerW = powerW;
      previousSampleMs = nowMs;
      havePreviousPower = true;

      if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
        shared.rpm = static_cast<float>(rpm);
        shared.torquePct = static_cast<float>(torquePct);
        shared.torqueNm = static_cast<float>(torqueNm);
        shared.loadPct = static_cast<float>(loadRaw);
        shared.powerW = static_cast<float>(powerW);
        shared.energyJ = energyJ;
        shared.driveState = driveStateCode(status);
        shared.alarmCode = static_cast<int>(alarmCode);
        shared.commOk = commOk;
        shared.commErr = commErr;
        shared.haveSample = true;
        xSemaphoreGive(stateMutex);
      }
    } else {
      ++commErr;
      // Lacuna na serie: nao encadear a proxima amostra com a anterior.
      havePreviousPower = false;

      if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
        shared.commOk = commOk;
        shared.commErr = commErr;
        xSemaphoreGive(stateMutex);
      }
    }

    vTaskDelay(pdMS_TO_TICKS(CONTROL_SERVICE_MS));
  }
}

// -------------------- Tarefa de rede --------------------

void ensureWiFi() {
  static uint32_t lastAttemptMs = 0;

  if (WiFi.status() == WL_CONNECTED) return;

  const uint32_t nowMs = millis();
  if (lastAttemptMs != 0 && (nowMs - lastAttemptMs) < WIFI_RETRY_MS) return;
  lastAttemptMs = nowMs;

  Serial.println(F("[SERVO] Conectando ao SoftAP do hub..."));
  WiFi.mode(WIFI_STA);
  WiFi.begin(HUB_SSID, HUB_PASSWORD);
}

void pushTelemetry() {
  SharedState snapshot;
  if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
    snapshot = shared;
    xSemaphoreGive(stateMutex);
  }

  // Sem amostra valida ainda: nao publicar zeros como se fossem medida.
  if (!snapshot.haveSample) return;

  String url;
  url.reserve(520);
  url  = "http://"; url += HUB_HOST; url += "/servoData";
  url += "?rpm=";        url += String(snapshot.rpm, 1);
  url += "&torque_pct="; url += String(snapshot.torquePct, 1);
  url += "&torque_nm=";  url += String(snapshot.torqueNm, 4);
  url += "&load_pct=";   url += String(snapshot.loadPct, 1);
  url += "&power_w=";    url += String(snapshot.powerW, 2);
  url += "&energy_wh=";  url += String(snapshot.energyJ / 3600.0, 6);
  url += "&state=";      url += String(snapshot.driveState);
  url += "&alarm=";      url += String(snapshot.alarmCode);
  url += "&ok=";         url += String(snapshot.commOk);
  url += "&err=";        url += String(snapshot.commErr);
  url += "&control_capable=";    url += String(snapshot.controlCapable ? 1 : 0);
  url += "&motor_ack=";           url += String(snapshot.motorCommandAck);
  url += "&motor_route_ack=";     url += String(snapshot.motorRouteAck);
  url += "&motor_applied_rpm=";   url += String(snapshot.motorAppliedRpm);
  url += "&motor_control_active=";url += String(snapshot.motorControlActive ? 1 : 0);
  url += "&motor_control_fault="; url += String(snapshot.motorControlFault);

  WiFiClient client;
  HTTPClient http;
  http.setConnectTimeout(HTTP_TIMEOUT_MS);
  http.setTimeout(HTTP_TIMEOUT_MS);

  if (!http.begin(client, url)) return;

  const int code = http.GET();
  if (code != 200) {
    Serial.printf("[SERVO] /servoData respondeu %d\n", code);
  }
  http.end();
}

void pullCommand() {
  String url = String("http://") + HUB_HOST + "/servoCommand";

  WiFiClient client;
  HTTPClient http;
  http.setConnectTimeout(HTTP_TIMEOUT_MS);
  http.setTimeout(HTTP_TIMEOUT_MS);

  if (!http.begin(client, url)) return;

  const int code = http.GET();
  if (code != 200) {
    http.end();
    return;
  }

  const String body = http.getString();
  http.end();

  if (body.length() < 3) return; // Hub antigo: permanece passivo para velocidade.

  if (jsonValue(body, "reset_energy").toInt() != 0) {
    if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
      shared.resetEnergyRequest = true;
      xSemaphoreGive(stateMutex);
    }
    Serial.println(F("[SERVO] Comando recebido: zerar energia."));
  }

  const String pollVal = jsonValue(body, "poll_ms");
  if (pollVal.length() > 0) {
    uint32_t requested = static_cast<uint32_t>(pollVal.toInt());
    if (requested < MIN_POLL_MS) requested = MIN_POLL_MS;
    if (requested > MAX_POLL_MS) requested = MAX_POLL_MS;

    if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
      shared.pollIntervalMs = requested;
      xSemaphoreGive(stateMutex);
    }
    Serial.printf("[SERVO] Intervalo de amostragem ajustado para %lu ms\n",
                  static_cast<unsigned long>(requested));
  }

  const String commandIdVal = jsonValue(body, "motor_cmd_id");
  const String motorRpmVal = jsonValue(body, "motor_rpm");
  const String motorEnableVal = jsonValue(body, "motor_enable");
  const String motorRouteVal = jsonValue(body, "motor_route");
  const String motorLeaseVal = jsonValue(body, "motor_lease_ms");
  const bool anyMotorField = commandIdVal.length() > 0 || motorRpmVal.length() > 0 ||
                             motorEnableVal.length() > 0 || motorRouteVal.length() > 0 ||
                             motorLeaseVal.length() > 0;
  if (!anyMotorField) return;

  uint32_t commandId = 0;
  uint32_t motorRpm = 0;
  uint32_t motorEnable = 0;
  uint32_t motorRoute = 0;
  uint32_t motorLeaseMs = 0;
  const bool valid = commandIdVal.length() > 0 && motorRpmVal.length() > 0 &&
                     motorEnableVal.length() > 0 && motorRouteVal.length() > 0 &&
                     motorLeaseVal.length() > 0 &&
                     parseUnsignedStrict(commandIdVal, commandId) && commandId != 0 &&
                     parseUnsignedStrict(motorRpmVal, motorRpm) && motorRpm <= MAX_MOTOR_RPM &&
                     parseUnsignedStrict(motorEnableVal, motorEnable) && motorEnable <= 1 &&
                     parseUnsignedStrict(motorRouteVal, motorRoute) && motorRoute <= 1 &&
                     parseUnsignedStrict(motorLeaseVal, motorLeaseMs) &&
                     motorLeaseMs >= MIN_MOTOR_LEASE_MS &&
                     motorLeaseMs <= MAX_MOTOR_LEASE_MS &&
                     ((motorRoute == MOTOR_ROUTE_MODBUS &&
                       ((motorEnable == 1 && motorRpm > 0) ||
                        (motorEnable == 0 && motorRpm == 0))) ||
                      (motorRoute == MOTOR_ROUTE_UART_CN1 &&
                       motorEnable == 0 && motorRpm == 0));
  if (!valid) {
    if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
      shared.motorControlFault = MOTOR_FAULT_INVALID_COMMAND;
      xSemaphoreGive(stateMutex);
    }
    Serial.println(F("[MOTOR] Resposta do Hub rejeitada: comando incompleto ou fora da faixa."));
    return; // nao renova o lease de um comando anterior
  }

  bool changed = false;
  if (xSemaphoreTake(stateMutex, portMAX_DELAY) == pdTRUE) {
    changed = !shared.haveMotorCommand || shared.motorCommandId != commandId ||
              shared.requestedMotorRoute != motorRoute ||
              shared.requestedMotorRpm != motorRpm ||
              shared.requestedMotorEnable != (motorEnable != 0) ||
              shared.motorLeaseMs != motorLeaseMs;
    shared.motorCommandId = commandId;
    shared.requestedMotorRoute = static_cast<uint8_t>(motorRoute);
    shared.requestedMotorRpm = static_cast<uint16_t>(motorRpm);
    shared.requestedMotorEnable = motorEnable != 0;
    shared.motorLeaseMs = motorLeaseMs;
    shared.motorCommandReceivedAt = millis();
    shared.haveMotorCommand = true;
    shared.motorCommandDirty = shared.motorCommandDirty || changed;
    xSemaphoreGive(stateMutex);
  }
  if (changed) {
    Serial.printf("[MOTOR] cmd_id=%lu recebido: via=%s, %lu rpm, enable=%lu, lease=%lu ms\n",
                  static_cast<unsigned long>(commandId),
                  motorRoute == MOTOR_ROUTE_MODBUS ? "Modbus" : "UART/CN1",
                  static_cast<unsigned long>(motorRpm),
                  static_cast<unsigned long>(motorEnable),
                  static_cast<unsigned long>(motorLeaseMs));
  }
}

void networkTask(void *parameter) {
  (void)parameter;
  esp_task_wdt_add(NULL);

  uint32_t lastPushMs = 0;
  uint32_t lastCommandMs = 0;
  bool wasConnected = false;

  for (;;) {
    esp_task_wdt_reset();
    ensureWiFi();

    const bool connected = (WiFi.status() == WL_CONNECTED);
    if (connected != wasConnected) {
      wasConnected = connected;
      if (connected) {
        Serial.print(F("[SERVO] Associado ao hub. IP: "));
        Serial.println(WiFi.localIP());
      } else {
        Serial.println(F("[SERVO] WiFi caiu. O polling Modbus continua."));
      }
    }

    if (connected) {
      const uint32_t nowMs = millis();

      if ((nowMs - lastPushMs) >= PUSH_INTERVAL_MS) {
        lastPushMs = nowMs;
        pushTelemetry();
      }

      if ((nowMs - lastCommandMs) >= COMMAND_INTERVAL_MS) {
        lastCommandMs = nowMs;
        pullCommand();
      }
    }

    vTaskDelay(pdMS_TO_TICKS(100));
  }
}

// -------------------- Setup --------------------

void startWatchDog() {
  // idle_core_mask espelha CONFIG_ESP_TASK_WDT_CHECK_IDLE_TASK_CPU0=y do core:
  // reconfigurar com mascara zero desinscreveria a idle do nucleo 0, que e onde
  // rodam a stack de WiFi e a networkTask.
  esp_task_wdt_config_t wdt_config = {
    .timeout_ms = WDT_TIMEOUT_S * 1000,
    .idle_core_mask = (1U << 0),
    .trigger_panic = true
  };

  // O core Arduino-ESP32 3.x ja inicializa o TWDT (CONFIG_ESP_TASK_WDT_INIT=y,
  // CONFIG_ESP_TASK_WDT_TIMEOUT_S=5), entao esp_task_wdt_init() devolve
  // ESP_ERR_INVALID_STATE e o WDT_TIMEOUT_S deste sketch nunca chegava a valer.
  esp_err_t result = esp_task_wdt_init(&wdt_config);
  if (result == ESP_ERR_INVALID_STATE) {
    result = esp_task_wdt_reconfigure(&wdt_config);
  }
  if (result != ESP_OK) {
    Serial.printf("[SERVO] Falha ao configurar o watchdog: %s\n",
                  esp_err_to_name(result));
  } else {
    Serial.printf("[SERVO] Watchdog ativo com timeout de %u s\n",
                  static_cast<unsigned>(WDT_TIMEOUT_S));
  }
}

void setup() {
  Serial.begin(USB_SERIAL_BAUD);

  const uint32_t waitStartMs = millis();
  while (!Serial && (millis() - waitStartMs) < 2000U) {
    delay(10);
  }

  Serial.println();
  Serial.println(F("============================================================"));
  Serial.printf(" No ASDA-B2 v%s, protocolo %u -> TECNAL Hub\n",
                NODE_FIRMWARE_VERSION, NODE_PROTOCOL_VERSION);
  Serial.println(F(" Telemetria 03H + velocidade P1-09 por 10H com ACK e lease."));
  Serial.println(F("============================================================"));
  Serial.printf("RS-485: slave=%u, %lu baud, 8N2, TX=GPIO%d, RX=GPIO%d, DE/RE=GPIO%d\n",
                MODBUS_SLAVE_ADDRESS,
                static_cast<unsigned long>(RS485_BAUD),
                RS485_TX_PIN, RS485_RX_PIN, RS485_DE_RE_PIN);
  Serial.printf("Hub: SSID=%s  host=%s\n", HUB_SSID, HUB_HOST);

  // Autoteste do CRC com o exemplo do manual do ASDA-B2.
  const uint8_t testFrame[] = {0x01, 0x03, 0x02, 0x00, 0x00, 0x02};
  if (modbusCrc16(testFrame, sizeof(testFrame)) != 0xB3C5U) {
    Serial.println(F("[SERVO] ERRO CRITICO: autoteste de CRC falhou. Parado."));
    for (;;) delay(1000);
  }

  stateMutex = xSemaphoreCreateMutex();
  if (stateMutex == NULL) {
    Serial.println(F("[SERVO] Falha ao criar o mutex; reiniciando."));
    ESP.restart();
  }

  pinMode(RS485_DE_RE_PIN, OUTPUT);
  digitalWrite(RS485_DE_RE_PIN, LOW); // repouso = recepcao
  rs485Serial.begin(RS485_BAUD, SERIAL_8N2, RS485_RX_PIN, RS485_TX_PIN);
  delay(100);

  recoverStaleDirectControlAtBoot();

  startWatchDog();

  // Modbus no nucleo 1: a stack de WiFi do Arduino-ESP32 vive no nucleo 0.
  xTaskCreatePinnedToCore(modbusTask, "modbus", 6144, NULL, 3, NULL, 1);
  // Rede em prioridade baixa, para nao disputar com a propria stack de WiFi.
  xTaskCreatePinnedToCore(networkTask, "network", 8192, NULL, 1, NULL, 0);

  Serial.println(F("[SERVO] Tarefas iniciadas."));
}

void loop() {
  // Todo o trabalho acontece nas duas tarefas. A loopTask do Arduino nao esta
  // registrada no watchdog, entao aqui basta ceder o processador.
  vTaskDelay(pdMS_TO_TICKS(1000));
}
