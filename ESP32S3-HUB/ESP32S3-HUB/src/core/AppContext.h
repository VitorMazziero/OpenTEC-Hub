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
ReliableMailbox distanceBox;
ReliableMailbox bathBox;

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

// ---------- External thermostatic bath (H01 foundation) ----------
bool bathCommOn = false;
float bathSp = NAN;
bool bathSpKnown = false;
float bathTarget = NAN;
float bathPv = NAN;
bool bathPvValid = false;
float bathDisplaySp = NAN;
bool bathDisplaySpValid = false;
uint8_t bathSpSource = 0;
uint8_t bathMode = 0;
float bathDeviation = NAN;
bool bathDeviationValid = false;
char bathState[16] = "idle";
char bathPhase[20] = "";
char bathError[48] = "";
char bathGuard[16] = "off";
unsigned long bathLastUpdate = 0;

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

// Uma troca de via retem o proximo setpoint ate o ESP32S3-driver confirmar que
// soltou (ou assumiu) P3-06. A retencao precisa de prazo: sem ele, um no que
// nunca confirma o ACK deixava o Hub mudo tambem na via UART/CN1, que sequer
// depende dele. Vencido o prazo, o setpoint segue para a via escolhida.
bool motorRouteTransitionPending = true;
uint32_t motorRouteTransitionStartedMs = 0;
const uint32_t MOTOR_ROUTE_TRANSITION_TIMEOUT_MS = 5000;

inline void beginMotorRouteTransition(uint32_t nowMs) {
  motorRouteTransitionPending = true;
  motorRouteTransitionStartedMs = nowMs;
}

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

// v06 tags every telemetry frame with the id of the flowmeter power-on that produced
// it. The hub adopts the reported state whenever nothing is pending, so a silent
// reboot used to hand it a zero setpoint as if the operator had asked for it - the gas
// stopped and the PC displayed 0 as the requested value. A changed id means "this is a
// different session": re-assert the desired state instead of adopting. Zero means the
// node has not reported one yet (a v05 flowmeter never will), and a first observation
// only records the id - re-asserting on first contact would let a lone hub reboot stomp
// a running aeration.
uint32_t flowmeterBootId = 0;

// Mirrors the node's own reconnect_wifi flag so the operator can see a flowmeter that
// has had its reconnection logic switched off. Without this the node just looks absent.
bool flowmeterReconnectWifi = true;

// Calibration/configuration fields remain pending only until acknowledged.
bool pendingMaxFlow = false;
// reconnect_wifi is the node's own "keep looking for a hub" switch. It can be turned
// off over the node's USB serial or its private AP, and until now nothing could turn
// it back on from here, so a flowmeter parked that way never came back on its own.
bool pendingReconnectWifi = false;
int desiredReconnectWifi = 1;
// a1/b1 are the x^4 and x^3 terms of the flowmeter's low-range curve. The app has
// sent them since the quartic fit; until now the hub dropped them, and a low segment
// delivered as k1/f1/c1 alone is taken by the node as a quadratic (a1=b1=0), which
// corrupted the low range on every "enviar curva".
bool pendingA1 = false, pendingB1 = false;
bool pendingK1 = false, pendingF1 = false, pendingC1 = false;
bool pendingK2 = false, pendingF2 = false, pendingC2 = false;
float desiredMaxFlow = 50.0f;
float desiredA1 = 0.0f, desiredB1 = 0.0f;
float desiredK1 = 0.0f, desiredF1 = 0.0f, desiredC1 = 0.0f;
float desiredK2 = 0.0f, desiredF2 = 0.0f, desiredC2 = 0.0f;

// Sintonia de vazao (PI, Feedforward e Rampa) pendente de ack (Hub 10.2 / v11).
bool  pendingFlowKp = false, pendingFlowKi = false;
bool  pendingFlowFfGain = false, pendingFlowFfOffset = false, pendingFlowRampRate = false;
float desiredFlowKp = 0.0f, desiredFlowKi = 0.0f;
float desiredFlowFfGain = 0.0f, desiredFlowFfOffset = 0.0f, desiredFlowRampRate = 0.0f;

// Transicao de calibracao dupla do fluxometro (Hub 10.3 / v12).
bool  pendingFlowTransitionVoltage = false;
float desiredFlowTransitionVoltage = 0.0545f;

// Ecos de sintonia e diagnostico interno do fluxometro (Hub 10.2 / v11, v12).
// NAN / false = nunca ecoado neste boot.
float flowmeterKp = NAN;
float flowmeterKi = NAN;
float flowmeterFfGain = NAN;
float flowmeterFfOffset = NAN;
float flowmeterRampRate = NAN;
float flowmeterOutput = NAN;
float flowmeterSetpointCorrected = NAN;
float flowmeterTransitionVoltage = NAN;
uint8_t flowmeterHwStatus = 7;
uint32_t flowmeterCalCrc = 0;
bool  flowmeterEchoSeen = false;

// ---------- Distance Sensor ----------
bool  distanceSensorCommOn = false;
float distanceSensorTime = 0.0;
float distanceSensorValue = -1.0f;
unsigned long distanceSensorLastUpdate = 0;

// Ecos de configuracao do sensor de distancia (Hub 10.2).
// NAN / 0 / false = nunca ecoado neste boot (so emitidos apos primeiro push v11).
float    distanceOffsetMm = NAN;
uint32_t distanceSamplePeriodMs = 0;
uint32_t distanceSendPeriodMs = 0;
bool     distanceEchoSeen = false;

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

// D03 (2026-09-13): the node accepts send_period up to 60 s, so a fixed 3 s presence window
// would flap on anything above 2.5 s. The window follows the send period the node echoes
// (send_ms): 2.5 periods, never below the 3 s floor. Until the first echo the floor applies.
// DISTANCE_TIMEOUT (interlock) is deliberately NOT widened: a foam reading older than 1.2 s
// must not actuate, whatever the send period - so send_period > 1 s disables the interlock.
inline unsigned long distancePresenceWindowMs(uint32_t sendPeriodMs) {
  unsigned long dyn = (unsigned long)(sendPeriodMs * 2.5f);
  return dyn > DISTANCE_PRESENCE_TIMEOUT ? dyn : DISTANCE_PRESENCE_TIMEOUT;
}

// ---------- Biomass Sensor ----------
bool  biomassCommOn = false;
float biomassAbsorbance = 0.0f;
int   biomassRaw = 0;
int   biomassIt = 0;
float biomassPwm = 0.0f;

// Ecos de configuracao da biomassa (Hub 10.2 / v11).
// -1 / NAN / 0 / false = nunca ecoado neste boot.
int      biomassGear = -1;
float    biomassEma = NAN;
uint32_t biomassProbePeriodMs = 0;
bool     biomassEchoSeen = false;
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

// B01 (2026-09-13): Dynamic presence window for Biomass sensor.
// In MEASURING, the node samples every probe_period (default 25s, thermal floor ~24.3s).
// To prevent presence flapping and false alarms, dynamic presence window is 2.5x probe_ms,
// bounded below by the 10s floor (BIOMASS_TIMEOUT).
inline unsigned long biomassPresenceWindowMs(int probePeriodMs) {
  if (probePeriodMs <= 0) return BIOMASS_TIMEOUT;
  unsigned long dynamicWin = (unsigned long)(probePeriodMs * 2.5f);
  return (dynamicWin > BIOMASS_TIMEOUT) ? dynamicWin : BIOMASS_TIMEOUT;
}

inline unsigned long biomassPresenceWindowMs(uint32_t probePeriodMs) {
  return biomassPresenceWindowMs((int)probePeriodMs);
}

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

// 3.10 echoes (NAN / -1 = nunca ecoado neste boot): ganhos PID, potenciometros em comando
// (1) ou travados por "pot":0 / "speed" (0), e o volume do ciclo corrente.
float pumpPidKp = NAN;
float pumpPidKi = NAN;
float pumpPidKd = NAN;
int   pumpPotEnabled = -1;
float pumpCycleVolume = NAN;
float pumpTransitionSpeed = NAN;
float pumpA1 = NAN, pumpB1 = NAN, pumpK1 = NAN, pumpF1 = NAN, pumpC1 = NAN;
float pumpK2 = NAN, pumpF2 = NAN, pumpC2 = NAN;
uint32_t pumpCalCrc = 0;
bool  pumpEchoSeen = false;

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

// ---------- Device Registry (Auto-Discovery & Presence) ----------
enum ExternalDeviceId {
  DEV_DISTANCE = 0,
  DEV_AGITATOR,
  DEV_PUMP,
  DEV_FLOWMETER,
  DEV_BIOMASS,
  DEV_BATH,
  DEV_COUNT
};

struct DeviceNodeEntry {
  const char* name;
  IPAddress ip;
  char mac[18];
  char version[16];
  unsigned long lastHelloMs;
  unsigned long lastDataMs;
  bool registered;
};

DeviceNodeEntry g_deviceRegistry[DEV_COUNT] = {
  { "distance",  IPAddress(0, 0, 0, 0), "", "", 0, 0, false },
  { "agitator",  IPAddress(0, 0, 0, 0), "", "", 0, 0, false },
  { "pump",      IPAddress(0, 0, 0, 0), "", "", 0, 0, false },
  { "flowmeter", IPAddress(0, 0, 0, 0), "", "", 0, 0, false },
  { "biomass",   IPAddress(0, 0, 0, 0), "", "", 0, 0, false },
  { "bath",      IPAddress(0, 0, 0, 0), "", "", 0, 0, false }
};

// Health snapshots are fetched by NodeDiagTask, never by an AsyncWebServer callback.
// Keeping the bounded bodies out of the aggregate frame preserves the serial/Wi-Fi
// telemetry cadence while making the same diagnostics available over both links.
struct NodeDiagCache {
  char body[512];
  unsigned long fetchedMs;
  int code;
  size_t bodyBytes;   // tamanho real da resposta do nó, antes do corte em 511 B
  bool truncated;     // bodyBytes > 511: o corpo guardado não é o documento inteiro
};

NodeDiagCache g_nodeDiagCache[DEV_COUNT] = {};
TaskHandle_t g_nodeDiagTaskHandle = nullptr;

// Shared by the aggregate frame (Telemetry.h) and the /readData cache copy
// (Runtime.h): the two Strings must reserve the same size or the assignment reallocates.
#define HUB_TELEMETRY_JSON_RESERVE 3072

// Appends "<Prefix>IP" and, when the node has registered, "<Prefix>NodeVer" and
// "<Prefix>NodeMac" to an aggregate frame under construction. Version and MAC come
// from the node's own /nodeHello, never from a hard-coded default.
inline void appendNodeIdentity(String& json, const char* prefix, const DeviceNodeEntry& e) {
  json += ",\"";
  json += prefix;
  json += "IP\":\"" + e.ip.toString() + "\"";
  if (!e.registered) return;
  if (e.version[0] != '\0') {
    json += ",\"";
    json += prefix;
    json += "NodeVer\":\"";
    json += e.version;
    json += "\"";
  }
  if (e.mac[0] != '\0') {
    json += ",\"";
    json += prefix;
    json += "NodeMac\":\"";
    json += e.mac;
    json += "\"";
  }
}

inline void recordDeviceActivity(ExternalDeviceId devId, const IPAddress& ip, unsigned long nowMs, bool isHello, const char* ver = nullptr, const char* mac = nullptr) {
  if (devId >= DEV_COUNT) return;
  DeviceNodeEntry& entry = g_deviceRegistry[devId];
  if (ip != IPAddress(0, 0, 0, 0)) {
    entry.ip = ip;
  }
  if (isHello) {
    entry.lastHelloMs = nowMs;
    entry.registered = true;
    if (ver && ver[0] != '\0') {
      strncpy(entry.version, ver, sizeof(entry.version) - 1);
      entry.version[sizeof(entry.version) - 1] = '\0';
    }
    if (mac && mac[0] != '\0') {
      strncpy(entry.mac, mac, sizeof(entry.mac) - 1);
      entry.mac[sizeof(entry.mac) - 1] = '\0';
    }
  } else {
    entry.lastDataMs = nowMs;
  }
}
