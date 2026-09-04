/*************************************************************
 * SensorHub ESP32-S3 Code - TECNAL Hub v9
 *
 * Novidade da v8 em relacao a v7: no de telemetria do servo drive
 * Delta ASDA-B2 (ASD-B2-0421-B), lido por Modbus RTU sobre RS-485 por um
 * ESP32-S3 dedicado que se associa a este SoftAP.
 *
 *   GET /servoData     -> o no empurra telemetria (query params)
 *   GET /servoCommand  -> estado desejado do motor + eventos FIFO
 *   campos Servo* entram no JSON agregado de /readData
 *
 * A integracao e ADITIVA: nao altera a maquina de ack do flowmeter, nem o
 * sensorSerial, nem os mutexes existentes.
 *
 * O ESP32S3-driver escreve a referencia interna P1-09 por Modbus 10H e usa
 * P3-06/P4-07 para selecionar SPD0 e SON. Comando tem cmd_id, ACK e lease.
 *
 * Flowmeter reliability additions:
 * - Full desired valve/setpoint state is revisioned with cmd_id.
 * - A command remains available until flowmeter telemetry acknowledges it.
 * - Direct flowmeter state is synchronized after pending hub work completes.
 * - Flow telemetry status is independent from the stored control-enable flag.
 *
 * Padronizacao dos dispositivos externos (v8.1):
 * - A caixa revisionada do fluxometro vira ReliableMailbox e passa a valer tambem
 *   para biomassa, bomba e agitador: o comando fica retido ate o no devolver o
 *   cmd_id que aplicou. A caixa v6 era limpa na leitura e sobrescrita por
 *   setPending, entao um comando perdido no ar sumia sem aviso e um "blank"
 *   emitido logo antes de um "start" era silenciosamente substituido.
 * - Toda a presenca fica observavel: BiomassOnline, PumpOnline, DistanceOnline e
 *   AgitatorOnline sao publicados SEMPRE, para o PC distinguir "no ausente" de
 *   "Hub antigo que nao publica a chave". A bomba nao tinha janela de validade
 *   nenhuma e republicava a ultima amostra de um no morto para sempre.
 * - O roteamento persistido tambem e ecoado (*CommEnabled). O Hub guarda esses
 *   flags na NVS e o PC guarda os dele em disco; depois de um reboot os dois
 *   podiam divergir e o Hub descartava todo sub-comando de biomassa/bomba em
 *   silencio (if (cmdFound && commOn)).
 * - Novo handler /agitatorData: o frasco agitador nao reportava absolutamente
 *   nada, entao o app comandava no escuro. Agora reporta magnitude, sentido, se
 *   o potenciometro de bancada esta ativo e quem moveu o motor por ultimo.
 *
 * Arquitetura de Concorrência:
 * - Mutex da UART (sensorSerialMutex): Protege a porta serial (HardwareSerial)
 * contra acessos concorrentes (leitura/escrita) entre a 
 * tarefa de leitura de dados e a tarefa de atualização de parâmetros.
 * - Mutex de Comando (cmdMutex): Protege as variáveis 'pending'
 * (Strings) que são compartilhadas entre diferentes handlers HTTP
 * (ex: POST /command escreve, GET /flowCommand lê).
 * - Servidor HTTP Assíncrono: Lida com I/O de rede. O handler
 * de /command acumula 'chunks' de dados para robustez.
 * - Lógica de Leitura da UART: Não bloqueante, com timeout explícito,
 * robusta contra múltiplos terminadores (\r, \n) e lixo de buffer.
 * - RTOS: vTaskDelay é usado em vez de delay() para permitir
 * cooperação e agendamento de tarefas.
 *
 *************************************************************/

#include <Arduino.h>
#include <WiFi.h>
#include <HTTPClient.h>
#include <ESPAsyncWebServer.h>
#include <vector>
#include <esp_task_wdt.h>
#include <esp_random.h>
#include <Preferences.h>
#include <math.h>

#include "esp_heap_caps.h"
#include "../../Config.h"
#include "FirmwareApp.h"
#include "../devices/ServoDevice.h"
#include "../protocol/HttpCommandQueue.h"
#include "../protocol/JsonUtils.h"

// Mutex para proteger a porta serial do sensor
SemaphoreHandle_t sensorSerialMutex = NULL;

// Mutex para proteger as filas de comando (pending... strings)
SemaphoreHandle_t cmdMutex = NULL;
SemaphoreHandle_t stateMutex = NULL;

// ============ GLOBAL OBJECTS ============
AsyncWebServer server(80);
String lastSensorJson = "";
uint32_t sampleId = 0;
HttpCommandQueue httpCommandQueue;
ServoDevice servoDevice;

// Filas de comando (protegidas por cmdMutex)
// The flowmeter keeps its own hand-rolled revisioned mailbox (it also carries the
// calibration pending-flags). Biomass, pump and agitator now use the generalised one
// below. Servo uses a reliable latest-wins desired state for speed and keeps a fixed
// FIFO only for non-motion events such as energy reset and poll interval.
String pendingFlowmeterCommand = "";

// A command retained until the node echoes back the revision it applied.
//
// The v6 mailbox was cleared by the act of reading it, so a dropped HTTP response lost
// the command with nobody the wiser, and setPending overwrote an unread one. Neither is
// acceptable for a command that moves something.
struct ReliableMailbox {
  String        payload;          // complete JSON including cmd_id, or empty
  uint32_t      revision = 0;     // bumped per queued command; never 0 once used
  uint32_t      ack = 0;          // last revision the node reported applying
  bool          awaiting = false; // a command is outstanding
  uint32_t      deliveries = 0;   // how many times it has been handed out
  unsigned long queuedAt = 0;
};

ReliableMailbox biomassBox;
ReliableMailbox pumpBox;
ReliableMailbox agitatorBox;

// ============ UART CONFIG ============
#define SENSOR_RX_PIN 16
#define SENSOR_TX_PIN 17
HardwareSerial sensorSerial(2);
#define MAX_SENSOR_BUFFER_LENGTH 1024
#define WDT_TIMEOUT 10

#define ESP32_LOG_ENABLED 1

#if ESP32_LOG_ENABLED
  #define ESP32_INFO(msg)  do { Serial.print("[ESP32_INFO]: ");  Serial.println(msg); } while (0)
  #define ESP32_AVISO(msg) do { Serial.print("[ESP32_AVISO]: "); Serial.println(msg); } while (0)
  #define ESP32_ERRO(msg)  do { Serial.print("[ESP32_ERRO]: ");  Serial.println(msg); } while (0)
  #define ESP32_EVT(msg)   do { Serial.print("[ESP32_EVT]: ");   Serial.println(msg); } while (0)
#else
  #define ESP32_INFO(msg)
  #define ESP32_AVISO(msg)
  #define ESP32_ERRO(msg)
  #define ESP32_EVT(msg)
#endif

// ============ SYNC FLAGS (NEW) ============
// These flags act as a "mailbox". When the Python script sends a command,
// we just raise the flag. The main loop sees the flag and handles the UART.
volatile bool flagMotorDirty = false;
volatile bool flagMotorRouteDirty = false;
volatile bool flagTempDirty = false;
volatile bool flagPhDirty = false;
volatile bool flagNutriDirty = false;
volatile bool flagAntiDirty = false;
volatile bool flagPressureDirty = false;
volatile bool flagDistanceReferenceDirty = false;

// ============ GLOBAL VARIABLES ============

// ---------- References / Setpoints ----------
float tempReference = 0.0f;
float pHReference = 0.0f;
int   pressureReference = 0;
float flowmeterSetpoint = 0.0f;
float distanceSensorReference = 0.0;
float pumpTargetVolume = 0.0f;

// ---------- Main Enable Flags ----------
bool oxyOn = false;
bool tempOn = false;
bool phOn = false;
bool nutrientOn = false;
bool antifoamOn = false;
bool pressureOn = false;
bool bypassMode = false;

// ---------- pH Control ----------
float pHError = 0.0f;
int   pHCal = 0;
int   pHOperation = 0;
int   pHMix = 0;
int   pHIntensity = 0;

// ---------- Agitation / Motor ----------
// motorRPM e sempre a referencia do operador. A via e selecionavel: Modbus
// direto no ESP32S3-driver ou UART/CN1 pela placa original, sem PI no Hub.
int   motorRPM = 0;
MotorControlRoute motorControlRoute = MotorControlRoute::Modbus;
bool motorRouteTransitionPending = true;

bool   agitatorAuto        = true;
bool   agitatorReEnablePot = true;
int    agitatorPercentFoam = 80;
int    agitatorDirFoam     = 1;
float  foamStartDelay_s    = 1.0f;
float  foamPulse_s         = 1.0f;
float  foamInterval_s      = 5.0f;

// ---------- Nutrient Control ----------
int nutriOperation = 0;
int nutriMix = 0;
int nutriOpCycle = 0;
int nutriMixCycle = 0;
int nutriIntensity = 0;

// ---------- Antifoam Control ----------
int antifoamOperation = 0;
int antifoamMix = 0;
int antifoamIntensity = 0;

// ---------- Data Timing ----------
unsigned long dataDelay = 1000;
unsigned long lastDataMillis = 0;
unsigned long lastReadBroadcastTime = 0;

// ---------- Sensor UART ----------
String sensorBuffer = "";
bool uartSensorOK = true;
int uartFailureCount = 0;
const int UART_FAILURE_THRESHOLD = 3;

// --- COOLDOWN VARIABLES ---
unsigned long uartCooldownUntil = 0;            // Stores the timestamp when UART can be used again
const unsigned long UART_COOLDOWN_MS = 5000;   // 2 seconds cooldown time

// ---------- Flowmeter Calibration ----------
float a = 0.0305473419314;
float b = -25.09136520919;

// ---------- Flowmeter ----------
bool  flowmeterCommOn = false;          // telemetry online, not user enable
bool  flowmeterControlEnabled = false;  // persisted UI control preference
float flowmeterTime = 0.0;
float flowmeterVoltage = 0.0;
float flowmeterRate = 0.0;
int   flowmeterValve1 = 0;
int   flowmeterValve2 = 0;
int   flowmeterValveFlow = 0;
unsigned long flowmeterLastUpdate = 0;
// v05 publishes every 500 ms. Six seconds tolerates transient Wi-Fi/HTTP
// contention while still declaring a genuine loss after 12 missed frames.
const unsigned long FLOWMETER_TIMEOUT = 6000;

// Reliable desired-state protocol. The actuator fields travel together so a
// later command cannot omit a previous nitrogen-valve closure.
float desiredFlowSetpoint = 0.0f;
int desiredFlowValve1 = 0;
int desiredFlowValve2 = 0;
int desiredFlowValveFlow = 0;
uint32_t flowCommandRevision = 0;
uint32_t flowCommandAck = 0;
bool flowCommandAwaitingAck = false;
uint32_t flowCommandDeliveryCount = 0;
unsigned long flowCommandQueuedAt = 0;
unsigned long flowCommandAckAt = 0;
String flowmeterLastCommandSource = "boot";

// Calibration/configuration fields remain pending only until acknowledged.
bool pendingMaxFlow = false;
bool pendingK1 = false, pendingF1 = false, pendingC1 = false;
bool pendingK2 = false, pendingF2 = false, pendingC2 = false;
float desiredMaxFlow = 50.0f;
float desiredK1 = 0.0f, desiredF1 = 0.0f, desiredC1 = 0.0f;
float desiredK2 = 0.0f, desiredF2 = 0.0f, desiredC2 = 0.0f;

// ---------- Distance Sensor ----------
bool  distanceSensorCommOn = false;
float distanceSensorTime = 0.0;
float distanceSensorValue = -1.0f;
unsigned long distanceSensorLastUpdate = 0;

// Two windows, deliberately different.
//
// DISTANCE_TIMEOUT gates the foam interlock in checkDistanceSensorReference(), which
// actuates pumps and the agitator - it must refuse to act on anything but a fresh
// reading, so it stays tight.
//
// DISTANCE_PRESENCE_TIMEOUT gates what the operator sees. The node pushes once a second,
// so a 1.2 s display window drops "offline" on every single lost packet. Widening only
// the display window means a reading can be shown while already being too old to act on,
// which is the honest description of that state rather than a contradiction.
const unsigned long DISTANCE_TIMEOUT = 1200;
const unsigned long DISTANCE_PRESENCE_TIMEOUT = 3000;

// ---------- Biomass Sensor ----------
bool  biomassCommOn = false;
float biomassAbsorbance = 0.0f;
int   biomassRaw = 0;
int   biomassIt = 0;
float biomassPwm = 0.0f;
// Presence and freshness are separate clocks.
//
// From v05 the node also sends a 5 s heartbeat while it is IDLE, carrying &idle=1 and
// the last sample it happened to have. Recording that as a fresh sample would leave a
// stopped sensor's final absorbance on the operator's screen looking live for the rest
// of the cultivation - the exact defect the heartbeat exists to fix.
//
// So: any push proves the node is alive; only a non-idle push proves the reading is.
unsigned long biomassLastUpdate = 0;
unsigned long biomassSampleLastUpdate = 0;
const unsigned long BIOMASS_TIMEOUT = 10000;

// ---------- Pump ----------
bool  pumpCommOn = false;
unsigned long pumpLastUpdate = 0;
// Four times the node's 1 s push. Stock v8 had no window here at all, so a dead pump's
// last sample was republished forever and the PC showed a flow that was not happening.
const unsigned long PUMP_TIMEOUT = 4000;
int   pumpMode = 0;
int   pumpPwm = 0;
float pumpSpeed = 0.0f;
float pumpFlowRate = 0.0f;
float pumpVolume = 0.0f;
bool  pumpActive = false;
bool  pumpWaiting = false;

// ---------- Servo drive Delta ASDA-B2 ----------
// Telemetria empurrada por um no ESP32-S3 dedicado, que le o drive por
// Modbus RTU sobre RS-485 (CN3) e se associa a este SoftAP.
//
// A integracao de energia acontece NO NO, nunca aqui: ela precisa da serie
// continua a 1 Hz, e uma queda de WiFi corromperia a integral se ela fosse
// calculada a partir dos pushes.
//
// servoCommOn ja nasce true: o no deve subir sozinho assim que energizado,
// sem depender de um comando do PC.
// ---------- Frasco Agitador ----------
// Reported by the node's /agitatorData push. Before it existed the Hub knew nothing
// about this device: no presence, no actual speed, and no way for the PC to see that
// the bench potentiometer had taken the motor back.
float  agitatorActualPercent = 0.0f;
int    agitatorActualDir = 1;
bool   agitatorPotActive = true;
String agitatorSource = "unknown";
unsigned long agitatorLastUpdate = 0;
// Six times the node's 500 ms push.
const unsigned long AGITATOR_TIMEOUT = 3000;

bool servoCommOn = true;  // NVS mirror; ServoDevice is the synchronized runtime owner.

