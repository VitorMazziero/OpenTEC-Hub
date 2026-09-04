/*************************************************************
 * SensorHub ESP32-S3 Code - TECNAL Hub v7
 *
 * Flowmeter reliability additions:
 * - Full desired valve/setpoint state is revisioned with cmd_id.
 * - A command remains available until flowmeter telemetry acknowledges it.
 * - Direct flowmeter state is synchronized after pending hub work completes.
 * - Flow telemetry status is independent from the stored control-enable flag.
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

// Mutex para proteger a porta serial do sensor
SemaphoreHandle_t sensorSerialMutex = NULL;

// Mutex para proteger as filas de comando (pending... strings)
SemaphoreHandle_t cmdMutex = NULL;

// ============ USER SETTINGS =============
#define WIFI_SSID "ModuloTECNAL_1"
#define WIFI_PASSWORD "ModuloTECNAL_1"
#define JSON_BUFFER_LEN 256
#define MAX_HTTP_PAYLOAD 2048 // Limite de segurança para JSON em /command

// ============ GLOBAL OBJECTS ============
AsyncWebServer server(80);
String lastSensorJson = "";
uint32_t sampleId = 0;

// Filas de comando (protegidas por cmdMutex)
// The flowmeter mailbox is revisioned and retained until acknowledgement.
// Other peripheral mailboxes retain the v6 consume-on-read behavior.
String pendingFlowmeterCommand = "";
String pendingBiomassCommand = "";
String pendingAgitatorCommand = "";
String pendingPumpCommand = "";

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
int   motorRPM = 0;

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
const unsigned long DISTANCE_TIMEOUT = 1200;

// ---------- Biomass Sensor ----------
bool  biomassCommOn = false;
float biomassAbsorbance = 0.0f;
int   biomassRaw = 0;
int   biomassIt = 0;
float biomassPwm = 0.0f;
unsigned long biomassLastUpdate = 0;
const unsigned long BIOMASS_TIMEOUT = 10000;

// ---------- Pump ----------
bool  pumpCommOn = false;
int   pumpMode = 0;
int   pumpPwm = 0;
float pumpSpeed = 0.0f;
float pumpFlowRate = 0.0f;
float pumpVolume = 0.0f;
bool  pumpActive = false;
bool  pumpWaiting = false;

// ============ PERSISTENCE ============
Preferences preferences;

// ============ MEMORY SAVE FLAGS ============
volatile bool flagPendingSave = false;
unsigned long lastSaveTriggerTime = 0;
const unsigned long SAVE_DEBOUNCE_MS = 15000; // Aguarda 15s de inatividade antes de gravar na Flash

void saveSettings() {
  preferences.putFloat("tempRef", tempReference);
  preferences.putFloat("pHRef", pHReference);
  preferences.putFloat("pHErr", pHError);
  preferences.putInt("pHCal", pHCal);
  preferences.putInt("pHOp", pHOperation);
  preferences.putInt("pHMix", pHMix);
  preferences.putInt("pHInt", pHIntensity);
  preferences.putInt("motorRPM", motorRPM);
  preferences.putInt("nutriOp", nutriOperation);
  preferences.putInt("nutriMix", nutriMix);
  preferences.putInt("nutriOpCycle", nutriOpCycle);
  preferences.putInt("nutriMixCycle", nutriMixCycle);
  preferences.putInt("nutriInt", nutriIntensity);
  preferences.putInt("antiOp", antifoamOperation);
  preferences.putInt("antiMix", antifoamMix);
  preferences.putInt("antiInt", antifoamIntensity);
  preferences.putInt("presRef", pressureReference);
  preferences.putFloat("distRef", distanceSensorReference);
  preferences.putBool("distComm", distanceSensorCommOn);
  preferences.putULong("dataDelay", dataDelay);
  preferences.putBool("oxyOn", oxyOn);
  preferences.putBool("tempOn", tempOn);
  preferences.putBool("phOn", phOn);
  preferences.putBool("nutrientOn", nutrientOn);
  preferences.putBool("antiOn", antifoamOn);
  preferences.putBool("presOn", pressureOn);
  preferences.putBool("agitAuto", agitatorAuto);
  preferences.putBool("agitRePot", agitatorReEnablePot);
  preferences.putInt("agitPct", agitatorPercentFoam);
  preferences.putInt("agitDir", agitatorDirFoam);
  preferences.putFloat("foamDelay", foamStartDelay_s);
  preferences.putFloat("foamPulse", foamPulse_s);
  preferences.putFloat("foamInterval", foamInterval_s);
  preferences.putBool("bioComm", biomassCommOn);
  preferences.putBool("flowComm", flowmeterControlEnabled);
  preferences.putBool("pumpComm", pumpCommOn);
}

void loadSettings() {
  tempReference       = preferences.getFloat("tempRef", 0.0f);
  pHReference         = preferences.getFloat("pHRef", 0.0f);
  pHError             = preferences.getFloat("pHErr", 0.17f);
  pHCal               = preferences.getInt("pHCal", 5);
  pHOperation         = preferences.getInt("pHOp", 5);
  pHMix               = preferences.getInt("pHMix", 10);
  pHIntensity         = preferences.getInt("pHInt", 990);
  motorRPM            = preferences.getInt("motorRPM", 0);
  nutriOperation      = preferences.getInt("nutriOp", 999);
  nutriMix            = preferences.getInt("nutriMix", 1);
  nutriOpCycle        = preferences.getInt("nutriOpCycle", 500);
  nutriMixCycle       = preferences.getInt("nutriMixCycle", 1);
  nutriIntensity      = preferences.getInt("nutriInt", 99);
  antifoamOperation   = preferences.getInt("antiOp", 999);
  antifoamMix         = preferences.getInt("antiMix", 1);
  antifoamIntensity   = preferences.getInt("antiInt", 99);
  pressureReference   = preferences.getInt("presRef", 100);
  distanceSensorReference = preferences.getFloat("distRef", 0.0f);
  dataDelay           = preferences.getULong("dataDelay", 1000);
  oxyOn               = preferences.getBool("oxyOn", false);
  tempOn              = preferences.getBool("tempOn", false);
  phOn                = preferences.getBool("phOn", false);
  nutrientOn          = preferences.getBool("nutrientOn", false);
  antifoamOn          = preferences.getBool("antiOn", false);
  pressureOn          = preferences.getBool("presOn", false);
  agitatorAuto        = preferences.getBool("agitAuto", true);
  agitatorReEnablePot = preferences.getBool("agitRePot", true);
  agitatorPercentFoam = preferences.getInt("agitPct", 80);
  agitatorDirFoam     = preferences.getInt("agitDir", 1);
  foamStartDelay_s    = preferences.getFloat("foamDelay", 1.0f);
  foamPulse_s         = preferences.getFloat("foamPulse", 1.0f);
  foamInterval_s      = preferences.getFloat("foamInterval", 5.0f);
  biomassCommOn       = preferences.getBool("bioComm", false);
  distanceSensorCommOn = preferences.getBool("distComm", false);
  flowmeterControlEnabled = preferences.getBool("flowComm", false);
  flowmeterCommOn = false;
  pumpCommOn = preferences.getBool("pumpComm", false);
}

void debugSettings() {
  ESP32_INFO("==== Loaded Preferences ====");
  ESP32_INFO(String("tempReference: ")          + tempReference);
  ESP32_INFO(String("pHReference: ")            + pHReference);
  ESP32_INFO(String("pHError: ")                + pHError);
  ESP32_INFO(String("pHCal: ")                  + pHCal);
  ESP32_INFO(String("pHOperation: ")            + pHOperation);
  ESP32_INFO(String("pHMix: ")                  + pHMix);
  ESP32_INFO(String("pHIntensity: ")            + pHIntensity);
  ESP32_INFO(String("motorRPM: ")               + motorRPM);
  ESP32_INFO(String("nutriOperation: ")         + nutriOperation);
  ESP32_INFO(String("nutriMix: ")               + nutriMix);
  ESP32_INFO(String("nutriOpCycle: ")           + nutriOpCycle);
  ESP32_INFO(String("nutriMixCycle: ")          + nutriMixCycle);
  ESP32_INFO(String("nutriIntensity: ")         + nutriIntensity);
  ESP32_INFO(String("antifoamOperation: ")      + antifoamOperation);
  ESP32_INFO(String("antifoamMix: ")            + antifoamMix);
  ESP32_INFO(String("antifoamIntensity: ")      + antifoamIntensity);
  ESP32_INFO(String("pressureReference: ")      + pressureReference);
  ESP32_INFO(String("distanceSensorReference: ")+ distanceSensorReference);
  ESP32_INFO(String("distanceSensorCommOn: ")   + distanceSensorCommOn);
  ESP32_INFO(String("dataDelay: ")              + dataDelay);
  ESP32_INFO(String("oxyOn: ")                  + oxyOn);
  ESP32_INFO(String("tempOn: ")                 + tempOn);
  ESP32_INFO(String("phOn: ")                   + phOn);
  ESP32_INFO(String("nutrientOn: ")             + nutrientOn);
  ESP32_INFO(String("antifoamOn: ")             + antifoamOn);
  ESP32_INFO(String("pressureOn: ")             + pressureOn);
  ESP32_INFO(String("agitatorAuto: ")           + agitatorAuto);
  ESP32_INFO(String("agitatorReEnablePot: ")    + agitatorReEnablePot);
  ESP32_INFO(String("agitatorPercentFoam: ")    + agitatorPercentFoam);
  ESP32_INFO(String("agitatorDirFoam: ")        + agitatorDirFoam);
  ESP32_INFO(String("foamStartDelay_s: ")       + foamStartDelay_s);
  ESP32_INFO(String("foamPulse_s: ")            + foamPulse_s);
  ESP32_INFO(String("foamInterval_s: ")         + foamInterval_s);
  ESP32_INFO(String("biomassCommOn: ")          + biomassCommOn);
  ESP32_INFO(String("flowmeterTelemetryOnline: ") + flowmeterCommOn);
  ESP32_INFO(String("flowmeterControlEnabled: ") + flowmeterControlEnabled);
  ESP32_INFO(String("pumpCommOn: ")             + pumpCommOn);
  ESP32_INFO("============================");
}

// ============ HELPERS DE SINCRONIZAÇÃO ============

// Define um valor em uma fila 'pending' de forma segura (thread-safe)
inline void setPending(String& dst, const String& val) {
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) { 
    dst = val; 
    xSemaphoreGive(cmdMutex); 
  }
}

// Obtém e limpa um valor de uma fila 'pending' de forma segura (thread-safe)
inline String takePending(String& src) {
  String out = "{}";
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) { 
    if (src.length() > 0) { 
      out = src; 
      src = ""; 
    } 
    xSemaphoreGive(cmdMutex); 
  }
  return out;
}

// Build the current flowmeter message while cmdMutex is held. Actuator state
// is always complete; calibration fields are included only while pending ack.
String buildFlowCommandLocked() {
  String cmd;
  cmd.reserve(512);
  cmd = "{\"cmd_id\":" + String(flowCommandRevision);
  cmd += ",\"flow_setpoint\":" + String(desiredFlowSetpoint, 6);
  cmd += ",\"v1\":" + String(desiredFlowValve1);
  cmd += ",\"v2\":" + String(desiredFlowValve2);
  cmd += ",\"v_Flow\":" + String(desiredFlowValveFlow);
  if (pendingMaxFlow) cmd += ",\"max_flow\":" + String(desiredMaxFlow, 6);
  if (pendingK1) cmd += ",\"k1\":" + String(desiredK1, 9);
  if (pendingF1) cmd += ",\"f1\":" + String(desiredF1, 9);
  if (pendingC1) cmd += ",\"c1\":" + String(desiredC1, 9);
  if (pendingK2) cmd += ",\"k2\":" + String(desiredK2, 9);
  if (pendingF2) cmd += ",\"f2\":" + String(desiredF2, 9);
  if (pendingC2) cmd += ",\"c2\":" + String(desiredC2, 9);
  cmd += "}";
  return cmd;
}

// Unlike the v6 mailbox, reading never clears the flowmeter command. It is
// retained until /flowData acknowledges its cmd_id.
String getReliableFlowCommand() {
  String out = "{}";
  bool firstDelivery = false;
  uint32_t revision = 0;
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
    if (flowCommandAwaitingAck && pendingFlowmeterCommand.length() > 0) {
      out = pendingFlowmeterCommand;
      flowCommandDeliveryCount++;
      firstDelivery = (flowCommandDeliveryCount == 1);
      revision = flowCommandRevision;
    }
    xSemaphoreGive(cmdMutex);
  }
  if (firstDelivery) {
    ESP32_EVT(String("Flow command first delivery cmd_id=") + revision);
  }
  return out;
}

// ============ HELPER DE PARSING JSON ============
String getValueFromJson(const String &json, const String &key) {
  String searchKey = "\"" + key + "\"";
  int keyPos = json.indexOf(searchKey);
  if (keyPos == -1) return "";
  int colonPos = json.indexOf(':', keyPos + key.length() + 2);
  if (colonPos == -1) return "";
  int start = colonPos + 1;
  while (start < json.length() && isspace(json.charAt(start))) {
    start++;
  }
  char firstChar = json.charAt(start);
  int end = -1;
  if (firstChar == '\"') {
    start++;
    end = json.indexOf("\"", start);
    if (end == -1) return "";
  } else {
    end = json.indexOf(",", start);
    if (end == -1) {
      end = json.indexOf("}", start);
      if (end == -1) end = json.length();
    }
  }
  String value = json.substring(start, end);
  value.trim();
  return value;
}

uint32_t queueReliableFlowCommandFromJson(const String &json) {
  bool hasFlow = json.indexOf("\"flowSetpoint\"") != -1;
  bool hasV1 = json.indexOf("\"valve_1\"") != -1;
  bool hasV2 = json.indexOf("\"valve_2\"") != -1;
  bool hasVFlow = json.indexOf("\"v_Flow\"") != -1;
  bool hasMax = json.indexOf("\"maxFlow\"") != -1;
  bool hasK1 = json.indexOf("\"k1\"") != -1;
  bool hasF1 = json.indexOf("\"f1\"") != -1;
  bool hasC1 = json.indexOf("\"c1\"") != -1;
  bool hasK2 = json.indexOf("\"k2\"") != -1;
  bool hasF2 = json.indexOf("\"f2\"") != -1;
  bool hasC2 = json.indexOf("\"c2\"") != -1;

  if (!(hasFlow || hasV1 || hasV2 || hasVFlow || hasMax ||
        hasK1 || hasF1 || hasC1 || hasK2 || hasF2 || hasC2)) {
    return 0;
  }

  uint32_t revision = 0;
  String payload;
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
    if (hasFlow) {
      desiredFlowSetpoint = max(0.0f, getValueFromJson(json, "flowSetpoint").toFloat());
      // A setpoint-only command is common in cascade control. Infer the main
      // shutoff valve only when the sender did not specify it explicitly.
      if (!hasVFlow) desiredFlowValveFlow = (desiredFlowSetpoint > 0.0f) ? 0 : 1;
    }
    if (hasV1) desiredFlowValve1 = getValueFromJson(json, "valve_1").toInt() != 0;
    if (hasV2) desiredFlowValve2 = getValueFromJson(json, "valve_2").toInt() != 0;
    if (hasVFlow) desiredFlowValveFlow = getValueFromJson(json, "v_Flow").toInt() != 0;

    if (hasMax) { desiredMaxFlow = getValueFromJson(json, "maxFlow").toFloat(); pendingMaxFlow = true; }
    if (hasK1) { desiredK1 = getValueFromJson(json, "k1").toFloat(); pendingK1 = true; }
    if (hasF1) { desiredF1 = getValueFromJson(json, "f1").toFloat(); pendingF1 = true; }
    if (hasC1) { desiredC1 = getValueFromJson(json, "c1").toFloat(); pendingC1 = true; }
    if (hasK2) { desiredK2 = getValueFromJson(json, "k2").toFloat(); pendingK2 = true; }
    if (hasF2) { desiredF2 = getValueFromJson(json, "f2").toFloat(); pendingF2 = true; }
    if (hasC2) { desiredC2 = getValueFromJson(json, "c2").toFloat(); pendingC2 = true; }

    flowCommandRevision++;
    if (flowCommandRevision == 0) flowCommandRevision = 1;
    flowCommandAwaitingAck = true;
    flowCommandDeliveryCount = 0;
    flowCommandQueuedAt = millis();
    pendingFlowmeterCommand = buildFlowCommandLocked();
    revision = flowCommandRevision;
    payload = pendingFlowmeterCommand;
    xSemaphoreGive(cmdMutex);
  }

  if (revision != 0) {
    ESP32_EVT(String("Flow command queued cmd_id=") + revision + " payload=" + payload);
  }
  return revision;
}

// ============ FUNCTION DECLARATIONS ============
void startWiFi();
void startWatchDog();
void handleUSBCommands();
String processCommandData(const String &data);
void processJsonCommand(const String &json);
void processOutgoingCommands();
void readAndBroadcastSensorData();
void setMotor(int rpm);
void setTemperature(float t);
void setPH(float pH, float err, int op, int mix, int intensity);
void setPHCalibration(int calVal);
void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity);
void setAntifoam(int op, int mix, int intensity);
void setPressure(int ref);
String sendSensorCommand(const String &cmd, bool readResponse);
String removeDecimal(String s);
void updateParametersCycle();
void checkDistanceSensorReference();
void syncAllSensorSettings();
uint32_t computeStateHash();
void queueAgitatorCmd(float pct, int dir, int activePot);
String buildFlowCommandLocked();
String getReliableFlowCommand();
uint32_t queueReliableFlowCommandFromJson(const String &json);

// Helper de validação de resposta da UART (portável, sem range-based for)
static inline bool isValidSensorReply(const String& r) {
  if (r.length() == 0) return false;
  char c0 = r[0];
  // Permite números, negativos, decimais
  if (isDigit(c0) || c0 == '-' || c0 == '+' || c0 == '.') return true;
  // Permite respostas alfanuméricas (comandos de status, etc)
  for (size_t i = 0; i < r.length(); ++i) {
    if (isAlphaNumeric(r[i])) return true;
  }
  return false;
}

// [CORREÇÃO (Issue 1)] Helper para acúmulo de POST por requisição
struct CmdBuf { String buf; size_t total = 0; };

// ------------------------------------------------------------------
// startWiFi():
// ------------------------------------------------------------------
void startWiFi() {
  IPAddress apIP(192, 168, 4, 1);
  IPAddress subnet(255, 255, 255, 0);
  WiFi.mode(WIFI_MODE_AP);
  WiFi.softAPConfig(apIP, apIP, subnet);
  // PC + flowmeter + biomass + agitator + pump already exceeds the v6 limit.
  bool apStarted = WiFi.softAP(WIFI_SSID, WIFI_PASSWORD, 6, 0, 8);

  if (apStarted) {
    ESP32_INFO("Ponto de acesso Wi-Fi iniciado");
    ESP32_INFO(String("SSID: ") + WIFI_SSID);
    ESP32_INFO(String("IP do ponto de acesso: ") + WiFi.softAPIP().toString());
    vTaskDelay(pdMS_TO_TICKS(100)); // Permite a estabilização da stack de rede

    // Handler de POST /command (robusto, lida com 'chunks' de dados)
    server.on(
      "/command", HTTP_POST, [](AsyncWebServerRequest *request) {},
      NULL,
      [](AsyncWebServerRequest *request, uint8_t *data, size_t len, size_t index, size_t total) {
        
        // [CORREÇÃO (Issue 1)] Usa estado por requisição (request->_tempObject)
        auto *state = (CmdBuf*)request->_tempObject;

        if (index == 0) { // Primeiro chunk
          if (total > MAX_HTTP_PAYLOAD) {
            request->send(413, "text/plain", "Payload too large");
            return;
          }
          state = new CmdBuf();
          if (!state) {
            request->send(500, "text/plain", "Out of memory");
            return;
          }
          state->buf.reserve(total + 1);
          state->total = total;
          request->_tempObject = state;

          // [CORREÇÃO (Issue 1)] Anexa o handler de desconexão *ao request*
          // para limpar o buffer em caso de aborto.
          // [CORREÇÃO DE COMPILAÇÃO] Captura [request] e não recebe argumentos (void()).
          request->onDisconnect([request](){
            if (request->_tempObject != nullptr) {
                auto *state = (CmdBuf*)request->_tempObject;
                delete state;
                request->_tempObject = nullptr;
                // Serial.println("DEBUG: /command client disconnected, buffer cleared.");
            }
          });
        }

        if (state) {
          // [CORREÇÃO (Minor)] Evita cópia extra de string
          state->buf += String((const char*)data, len);
        }

        // Aguarda todos os 'chunks' chegarem
        if (index + len < total) {
          return;
        }

        // Todos os 'chunks' recebidos, processa o corpo (body) completo
        if (state) {
          int s = state->buf.indexOf('{');
          int e = state->buf.lastIndexOf('}');
          if (s != -1 && e != -1 && s < e) {
            String jsonContent = state->buf.substring(s, e + 1);
            String response = processCommandData(jsonContent);
            request->send(200, "text/plain", response);
          } else {
            request->send(400, "text/plain", "Invalid JSON format");
          }
          // Limpa o estado da requisição (buffer)
          delete state;
          // Seta como nullptr para que o onDisconnect não faça double-delete
          request->_tempObject = nullptr; 
        }
    });

    // Handler de GET /readData (com ETag para cache)
    server.on("/readData", HTTP_GET, [](AsyncWebServerRequest *request) {
      String etag = "\"" + String(sampleId) + "\"";
      if (request->hasHeader("If-None-Match")) {
        auto *h = request->getHeader("If-None-Match");
        if (h && h->value() == etag) {
          request->send(304); // Not Modified
          return;
        }
      }
      AsyncWebServerResponse *res =
          request->beginResponse(200, "application/json", lastSensorJson);
      res->addHeader("ETag", etag);
      res->addHeader("Cache-Control", "no-store");
      request->send(res);
    });

    // Handler de GET /ping (health check)
    server.on("/ping", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "text/plain", "pong");
    });

    // Handler de GET /distance (recebe dados do sensor de distância)
    server.on("/distance", HTTP_GET, [](AsyncWebServerRequest *request) {
      if (!request->hasParam("distance")) {
        request->send(400, "text/plain", "Missing 'distance' parameter");
        return;
      }
      String distStr = request->getParam("distance")->value();
      float   newDistance = distStr.toFloat();
      
      // Filtro de estagnação
      static float   buf[5]        = {0.0f};
      static uint8_t bufIndex      = 0;
      static uint8_t bufCount      = 0;
      static float   lastAccepted  = NAN;
      const float   ES            = 1e-3f;
      buf[bufIndex] = newDistance;
      bufIndex = (bufIndex + 1) % 5;
      if (bufCount < 5) ++bufCount;
      bool stagnated = false;
      if (bufCount == 5) {
        stagnated = true;
        for (uint8_t i = 1; i < 5; ++i) {
          if (fabsf(buf[i] - buf[0]) > ES) {
            stagnated = false;
            break;
          }
        }
      }
      if (stagnated) {
        distanceSensorValue = -1.0f; // Trata como desconectado
      }
      else if (isnan(lastAccepted) || fabsf(newDistance - lastAccepted) > ES) {
        distanceSensorValue = newDistance;
        lastAccepted = newDistance;
      }
      
      if (request->hasParam("time")) {
        distanceSensorTime = request->getParam("time")->value().toFloat();
      }
      distanceSensorLastUpdate = millis();
      String json = "{\"Distance\":" + String(distanceSensorValue, 2) + "}";
      request->send(200, "application/json", json);
    });

    // Handler de GET /flowData (recebe dados do fluxômetro)
    server.on("/flowData", HTTP_GET, [](AsyncWebServerRequest *request) {
      if (request->hasParam("seconds") && request->hasParam("flow_voltage") && request->hasParam("flow_rate") && request->hasParam("flow_setpoint") && request->hasParam("valve1State") && request->hasParam("valve2State")) {
        float newTime = request->getParam("seconds")->value().toFloat();
        float newVoltage = request->getParam("flow_voltage")->value().toFloat();
        float newRate = request->getParam("flow_rate")->value().toFloat();
        float newSetpoint = request->getParam("flow_setpoint")->value().toFloat();
        int newValve1 = request->getParam("valve1State")->value().toInt() != 0;
        int newValve2 = request->getParam("valve2State")->value().toInt() != 0;
        int newValveFlow = request->hasParam("valveFlowState")
                             ? (request->getParam("valveFlowState")->value().toInt() != 0)
                             : flowmeterValveFlow;
        bool hasAck = request->hasParam("ack_cmd_id");
        uint32_t reportedAck = hasAck
                                 ? (uint32_t)strtoul(request->getParam("ack_cmd_id")->value().c_str(), NULL, 10)
                                 : 0;
        String reportedSource = request->hasParam("command_source")
                                  ? request->getParam("command_source")->value()
                                  : "unknown";

        bool ackedNow = false;
        uint32_t ackedRevision = 0;
        if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
          flowmeterTime = newTime;
          flowmeterVoltage = newVoltage;
          flowmeterRate = newRate;
          flowmeterSetpoint = newSetpoint;
          flowmeterValve1 = newValve1;
          flowmeterValve2 = newValve2;
          flowmeterValveFlow = newValveFlow;
          flowmeterCommOn = true;
          flowmeterLastUpdate = millis();
          flowmeterLastCommandSource = reportedSource;

          if (hasAck) flowCommandAck = reportedAck;
          if (hasAck && flowCommandAwaitingAck && reportedAck == flowCommandRevision) {
            flowCommandAwaitingAck = false;
            flowCommandAckAt = millis();
            pendingFlowmeterCommand = "";
            pendingMaxFlow = false;
            pendingK1 = pendingF1 = pendingC1 = false;
            pendingK2 = pendingF2 = pendingC2 = false;
            ackedNow = true;
            ackedRevision = reportedAck;
          }

          if (!flowCommandAwaitingAck) {
            desiredFlowSetpoint = flowmeterSetpoint;
            desiredFlowValve1 = flowmeterValve1;
            desiredFlowValve2 = flowmeterValve2;
            desiredFlowValveFlow = flowmeterValveFlow;
          }
          xSemaphoreGive(cmdMutex);
        }

        if (ackedNow) {
          ESP32_EVT(String("Flow command acknowledged cmd_id=") + ackedRevision);
        }
        request->send(200, "text/plain", "Flowmeter data received");
      } else {
        request->send(400, "text/plain", "Missing one or more flowmeter parameters");
      }
    });

    // Handler de GET /biomassData (recebe dados do sensor de biomassa)
    server.on("/biomassData", HTTP_GET, [](AsyncWebServerRequest *request) {
        if (!biomassCommOn) {
            request->send(403, "text/plain", "Biomass comm disabled on hub");
            return;
        }
        if (!request->hasParam("absorbance") || !request->hasParam("raw")) {
            request->send(400, "text/plain", "Missing biomass parameters");
            return;
        }
        biomassAbsorbance = request->getParam("absorbance")->value().toFloat();
        biomassRaw        = request->getParam("raw")->value().toInt();
        
        // Parâmetros opcionais
        if (request->hasParam("it"))  biomassIt  = request->getParam("it")->value().toInt();
        if (request->hasParam("pwm")) biomassPwm = request->getParam("pwm")->value().toFloat();

        biomassLastUpdate = millis();
        request->send(200, "text/plain", "Biomass data received");
    });

    // Handler de GET /pumpData (recebe dados da bomba peristáltica)
    server.on("/pumpData", HTTP_GET, [](AsyncWebServerRequest *request) {
      // Check for the minimal required parameters
      if (request->hasParam("mode") && request->hasParam("flow") && request->hasParam("vol") && request->hasParam("v_tgt")) {
        pumpMode = request->getParam("mode")->value().toInt();
        pumpPwm = request->getParam("pwm")->value().toInt();
        pumpSpeed = request->getParam("speed")->value().toFloat();
        pumpFlowRate = request->getParam("flow")->value().toFloat();
        pumpVolume = request->getParam("vol")->value().toFloat();
        pumpTargetVolume = request->getParam("v_tgt")->value().toFloat();
        pumpActive = request->getParam("active")->value().toInt() == 1;
        pumpWaiting = request->getParam("waiting")->value().toInt() == 1;

        request->send(200, "text/plain", "Pump data received");
      } else {
        request->send(400, "text/plain", "Missing one or more pump parameters");
      }
    });

    // Handlers 'Pull' (thread-safe)
    
    server.on("/flowCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "application/json", getReliableFlowCommand());
    });

    server.on("/biomassCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "application/json", takePending(pendingBiomassCommand));
    });

    server.on("/agitatorHello", HTTP_GET, [](AsyncWebServerRequest *request) {
      IPAddress rip = request->client()->remoteIP();
      String msg = String("{\"hello\":\"agitator\",\"ip\":\"") + rip.toString() + "\"}";
      request->send(200, "application/json", msg);
    });

    server.on("/agitatorCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "application/json", takePending(pendingAgitatorCommand));
    });

    server.on("/pumpCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "application/json", takePending(pendingPumpCommand));
    });

    server.begin();
  } else {
      ESP32_ERRO("Falha ao iniciar o SoftAP; reiniciando ESP32");
      ESP.restart();
    }
}

// ------------------------------------------------------------------
// startWatchDog():
// ------------------------------------------------------------------
void startWatchDog() {
  esp_task_wdt_config_t wdt_config = {
    .timeout_ms = WDT_TIMEOUT * 1000,
    .trigger_panic = true
  };

  esp_err_t init_result = esp_task_wdt_init(&wdt_config);
  if (init_result != ESP_OK) {
    ESP32_ERRO(String("Falha ao inicializar o watchdog: ") + esp_err_to_name(init_result) + String(" - Reiniciar Módulo TECNAL"));
  } else {
    ESP32_INFO("Watchdog inicializado");
  }

  esp_err_t add_result = esp_task_wdt_add(NULL);
  if (add_result != ESP_OK) {
    ESP32_ERRO(String("Falha ao adicionar a tarefa atual ao watchdog: ") + esp_err_to_name(add_result) + String(" - Reiniciar Módulo TECNAL"));
  } else {
    ESP32_INFO("Tarefa principal registrada no watchdog");
  }
}

// ============ COMMAND PROCESSOR (COMPLETE) ============
// This function runs inside the main loop. It checks "dirty flags"
// and sends ALL parameters for that subsystem to ensure hardware logic works.
void processOutgoingCommands() {
  
  // 1. MOTOR UPDATE
  if (flagMotorDirty) {
    if (motorRPM == 0) {
      sendSensorCommand("0V", false);
      vTaskDelay(pdMS_TO_TICKS(15));
      sendSensorCommand("0A", false);
    } else {
      sendSensorCommand("1V", false);
      vTaskDelay(pdMS_TO_TICKS(15));
      sendSensorCommand(String(motorRPM) + "A", false);
    }
    flagMotorDirty = false; 
    vTaskDelay(pdMS_TO_TICKS(10)); 
  }

  // 2. TEMPERATURE UPDATE
  if (flagTempDirty) {
    if (fabs(tempReference) < 0.001) {
      tempOn = false;
      sendSensorCommand("100B", false); 
    } else {
      tempOn = true;
      String tStr = String(tempReference, 1);
      String noDot = removeDecimal(tStr);
      sendSensorCommand(noDot + "B", false);
    }
    flagTempDirty = false;
    vTaskDelay(pdMS_TO_TICKS(10));
  }

  // 3. PH UPDATE
  // Original Order: Setpoint(D) -> Error(E) -> Op(G) -> Mix(H) -> Intensity(F)
  if (flagPhDirty) {
    if (fabs(pHReference) < 0.001) { // Check reference, not intensity (matches original)
       phOn = false;
       sendSensorCommand("0F", false);
    } else {
       phOn = true;
       // 1. Setpoint (D)
       String phStr = String(pHReference, 2);
       sendSensorCommand(removeDecimal(phStr) + "D", false);
       vTaskDelay(pdMS_TO_TICKS(20)); // Increased delay to match original
       
       // 2. Error (E)
       String errStr = String(pHError, 2);
       sendSensorCommand(removeDecimal(errStr) + "E", false);
       vTaskDelay(pdMS_TO_TICKS(20));

       // 3. Operation Mode (G)
       sendSensorCommand(String(pHOperation) + "G", false);
       vTaskDelay(pdMS_TO_TICKS(20));

       // 4. Mix Time (H)
       sendSensorCommand(String(pHMix) + "H", false);
       vTaskDelay(pdMS_TO_TICKS(20));

       // 5. Intensity (F)
       sendSensorCommand(String(pHIntensity) + "F", false);
    }
    flagPhDirty = false;
    vTaskDelay(pdMS_TO_TICKS(10));
  }

  // 4. NUTRIENT UPDATE
  // Original Order: Intensity(0M - ON) -> Op(N) -> Mix(O) -> OpCycle(P) -> MixCycle(Q)
  if (flagNutriDirty) {
    if (nutriIntensity == 0) {
      nutrientOn = false;
      sendSensorCommand("0M", false);
    } else {
      nutrientOn = true;
      
      // 1. Intensity (0M) - Turns ON FIRST (Restored Original Logic)
      sendSensorCommand(String(nutriIntensity) + "0M", false);
      vTaskDelay(pdMS_TO_TICKS(20));

      // 2. Operation Mode (N)
      sendSensorCommand(String(nutriOperation) + "N", false);
      vTaskDelay(pdMS_TO_TICKS(20));

      // 3. Mix Time (O)
      sendSensorCommand(String(nutriMix) + "O", false);
      vTaskDelay(pdMS_TO_TICKS(20));

      // 4. Op Cycle (P)
      sendSensorCommand(String(nutriOpCycle) + "P", false);
      vTaskDelay(pdMS_TO_TICKS(20));

      // 5. Mix Cycle (Q)
      sendSensorCommand(String(nutriMixCycle) + "Q", false);
    }
    flagNutriDirty = false;
    vTaskDelay(pdMS_TO_TICKS(10));
  }

  // 5. ANTIFOAM UPDATE
  // Original Order: Intensity(0I - ON) -> Op(J) -> Mix(L)
  if (flagAntiDirty) {
    if (antifoamIntensity == 0) {
      antifoamOn = false;
      sendSensorCommand("0I", false);
    } else {
      antifoamOn = true;
      
      // 1. Intensity (0I) - Turns ON FIRST
      sendSensorCommand(String(antifoamIntensity) + "0I", false);
      vTaskDelay(pdMS_TO_TICKS(20));

      // 2. Operation Mode (J)
      sendSensorCommand(String(antifoamOperation) + "J", false);
      vTaskDelay(pdMS_TO_TICKS(20));

      // 3. Mix Time (L)
      sendSensorCommand(String(antifoamMix) + "L", false);
    }
    flagAntiDirty = false;
    vTaskDelay(pdMS_TO_TICKS(10));
  }

  // 6. PRESSURE UPDATE
  if (flagPressureDirty) {
    if (pressureReference == 0) {
      pressureOn = false;
      sendSensorCommand("0C", false);
    } else {
      pressureOn = true;
      sendSensorCommand(String(pressureReference) + "C", false);
    }
    flagPressureDirty = false;
    vTaskDelay(pdMS_TO_TICKS(10));
  }
}

// ------------------------------------------------------------------
// SETUP
// ------------------------------------------------------------------
void setup() {
  Serial.begin(115200);
  ESP32_INFO("Inicialização iniciada");

  lastSensorJson.reserve(1536);
  preferences.begin("SensorHub", false);
  loadSettings();
  // debugSettings();
  startWatchDog();

  sensorSerialMutex = xSemaphoreCreateMutex();
  cmdMutex = xSemaphoreCreateMutex();
  if (sensorSerialMutex == NULL || cmdMutex == NULL) {
    ESP32_ERRO("Não foi possível criar os mutexes; reiniciando ESP32");
    ESP.restart();
  }

  ESP32_INFO("Mutexes criados com sucesso");

  // Start every hub boot in a different command-ID region. If the hub reboots
  // while the flowmeter remains powered, a new command cannot be mistaken for
  // an already-applied command from the previous hub session.
  flowCommandRevision = esp_random();
  if (flowCommandRevision == 0) flowCommandRevision = 1;
  flowCommandAck = flowCommandRevision;
  ESP32_INFO(String("Flow command session seed: ") + flowCommandRevision);

  sensorSerial.begin(9600, SERIAL_8N1, SENSOR_RX_PIN, SENSOR_TX_PIN);
  ESP32_INFO("UART do Módulo TECNAL iniciada em 9600 baud");

  startWiFi();
  syncAllSensorSettings();

  ESP32_EVT("Inicialização concluída");
}

// ------------------------------------------------------------------
// LOOP
// ------------------------------------------------------------------
void loop() {
  // 1. Process USB Debug Commands
  handleUSBCommands();

  unsigned long now = millis();
  
  // 2. PRIORITY: Process Outgoing UART Commands (The "Mailbox")
  // We check this BEFORE reading sensors. If Python sent a command,
  // we send it to the hardware now.
  if (!bypassMode) {
     processOutgoingCommands();
  }

  // 3. PERIODIC: Read Sensors
  // We only read if enough time has passed.
  if (!bypassMode && (now - lastDataMillis) >= dataDelay) {
    lastDataMillis = now;
    
    // Safety check for Foam
    if (distanceSensorCommOn){
      checkDistanceSensorReference(); 
    }
    
    // Now it is safe to read because processOutgoingCommands() has finished.
    readAndBroadcastSensorData(); 
  }

  // NVS FLASH SAVE (Debounced)
  // Só grava na memória física se houve alteração e já se passaram 5 segundos sem novos comandos
  if (flagPendingSave && (now - lastSaveTriggerTime >= SAVE_DEBOUNCE_MS)) {
    saveSettings();
    flagPendingSave = false;
    ESP32_INFO("Parametros salvos na NVS (Flash)");
  }

  // Feed Watchdog
  esp_task_wdt_reset();
  vTaskDelay(pdMS_TO_TICKS(10)); 

  // Low-frequency heap log
  static unsigned long lastHeapLog = 0;
  if (millis() - lastHeapLog >= 60000) {
    lastHeapLog = millis();
    ESP32_INFO(String("Heap livre: ") + heap_caps_get_free_size(MALLOC_CAP_DEFAULT));
  }
}

// ------------------------------------------------------------------
// handleUSBCommands():
// ------------------------------------------------------------------
void handleUSBCommands() {
  static String data;
  static bool reserved = false;
  if (!reserved) {
    data.reserve(256);
    reserved = true;
  }

  data = "";
  while (Serial.available() > 0) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') break;
    data += c;
  }

  data.trim();
  if (data.length() > 0) {
    processCommandData(data);
  }
}

// ------------------------------------------------------------------
// processCommandData():
// ------------------------------------------------------------------
String processCommandData(const String &data) {
  if (data.length() == 0) {
      return "Empty command";
  }

  if (data.startsWith("{") && data.endsWith("}")) {
      if (bypassMode) {
        bypassMode = false;
        ESP32_EVT("Modo bypass desativado; retornando ao modo JSON");
      }
      processJsonCommand(data);
      return "OK";
  } else {
      if (!bypassMode) {
        bypassMode = true;
        ESP32_EVT("Modo bypass ativado via USB");
      }
      String response = sendSensorCommand(data, true);
      Serial.print(response);
      return response;
  }
}

// ------------------------------------------------------------------
// computeStateHash():
//   Gera uma assinatura numérica (hash simples) do estado atual 
//   das variáveis do sistema que devem ser salvas na Flash.
// ------------------------------------------------------------------
uint32_t computeStateHash() {
  uint32_t hash = 0;
  // Multiplicadores primos arbitrários para criar entropia simples
  hash += (uint32_t)(tempReference * 100) * 3;
  hash += (uint32_t)(pHReference * 100) * 7;
  hash += (uint32_t)(pHError * 100) * 11;
  hash += (uint32_t)pHCal * 13;
  hash += (uint32_t)pHOperation * 17;
  hash += (uint32_t)pHMix * 19;
  hash += (uint32_t)pHIntensity * 23;
  hash += (uint32_t)motorRPM * 29;
  hash += (uint32_t)nutriOperation * 31;
  hash += (uint32_t)nutriMix * 37;
  hash += (uint32_t)nutriOpCycle * 41;
  hash += (uint32_t)nutriMixCycle * 43;
  hash += (uint32_t)nutriIntensity * 47;
  hash += (uint32_t)antifoamOperation * 53;
  hash += (uint32_t)antifoamMix * 59;
  hash += (uint32_t)antifoamIntensity * 61;
  hash += (uint32_t)pressureReference * 67;
  hash += (uint32_t)(distanceSensorReference * 100) * 71;
  hash += dataDelay * 73;
  
  // Variáveis Booleanas
  hash += (oxyOn ? 1 : 0) * 79;
  hash += (tempOn ? 1 : 0) * 83;
  hash += (phOn ? 1 : 0) * 89;
  hash += (nutrientOn ? 1 : 0) * 97;
  hash += (antifoamOn ? 1 : 0) * 101;
  hash += (pressureOn ? 1 : 0) * 103;
  hash += (agitatorAuto ? 1 : 0) * 107;
  hash += (agitatorReEnablePot ? 1 : 0) * 109;
  hash += (uint32_t)agitatorPercentFoam * 113;
  hash += (uint32_t)agitatorDirFoam * 127;
  hash += (uint32_t)(foamStartDelay_s * 100) * 131;
  hash += (uint32_t)(foamPulse_s * 100) * 137;
  hash += (uint32_t)(foamInterval_s * 100) * 139;
  hash += (biomassCommOn ? 1 : 0) * 149;
  hash += (distanceSensorCommOn ? 1 : 0) * 151;
  hash += (flowmeterControlEnabled ? 1 : 0) * 157;
  hash += (pumpCommOn ? 1 : 0) * 163;

  return hash;
}

// ------------------------------------------------------------------
// processJsonCommand():
//   Fully Corrected Version with Temporary Variables to fix
//   the "Double Assignment" bug on Dirty Flags.
// ------------------------------------------------------------------
void processJsonCommand(const String &json) {
  String value;

  // 1. Calcula a assinatura do estado ANTES das mudanças
  uint32_t preHash = computeStateHash();

  // ============ SYSTEM COMMANDS ============
  if (json.indexOf("\"resetVariables\"") != -1) {
    ESP32_EVT("Comando de reset das variáveis recebido");
    tempReference = 0.0f; 
    pHReference = 0.0f; pHError = 0.17f; 
    pHCal = 5; pHOperation = 5; pHMix = 10; pHIntensity = 990;
    motorRPM = 0; 
    nutriOperation = 999; nutriMix = 1; nutriOpCycle = 500;
    nutriMixCycle = 1; nutriIntensity = 99; 
    antifoamOperation = 999; antifoamMix = 1; antifoamIntensity = 99; 
    pressureReference = 100;
    distanceSensorReference = 0.0f; 
    dataDelay = 1000; 
    oxyOn = false; tempOn = false; phOn = false; 
    nutrientOn = false; antifoamOn = false; pressureOn = false; 
    if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
      desiredFlowSetpoint = 0.0f;
      desiredFlowValve1 = 0;
      desiredFlowValve2 = 0;
      desiredFlowValveFlow = 1;
      flowCommandRevision++;
      if (flowCommandRevision == 0) flowCommandRevision = 1;
      flowCommandAwaitingAck = true;
      flowCommandDeliveryCount = 0;
      flowCommandQueuedAt = millis();
      pendingFlowmeterCommand = buildFlowCommandLocked();
      xSemaphoreGive(cmdMutex);
    }
    flowmeterControlEnabled = false;
    saveSettings();
    return;
  }

  if (json.indexOf("\"restart\"") != -1) {
    ESP32_EVT("Comando de reinicialização do ESP32 recebido");
    ESP.restart();
    return;
  }

  if (json.indexOf("\"comTest\"") != -1) {
    lastSensorJson = "OK";
    Serial.println(lastSensorJson);
    return;
  }

  // ============ COMM SETTINGS ============
  if (json.indexOf("\"flowmeterComm\"") != -1) {
    bool newVal = (getValueFromJson(json, "flowmeterComm").toInt() != 0);
    if (newVal != flowmeterControlEnabled) {
      ESP32_EVT(String("Comunicação do fluxômetro ") + (newVal ? "ativada" : "desativada"));
    }
    flowmeterControlEnabled = newVal;
  }

  if (json.indexOf("\"biomassComm\"") != -1) {
    bool newVal = (getValueFromJson(json, "biomassComm").toInt() != 0);
    if (newVal != biomassCommOn) {
      ESP32_EVT(String("Comunicação do sensor de biomassa ") + (newVal ? "ativada" : "desativada"));
    }
    biomassCommOn = newVal;
  }

  if (json.indexOf("\"distanceSensorComm\"") != -1) {
    bool newVal = (getValueFromJson(json, "distanceSensorComm").toInt() != 0);
    if (newVal != distanceSensorCommOn) {
      ESP32_EVT(String("Comunicação do sensor de distância ") + (newVal ? "ativada" : "desativada"));
    }
    distanceSensorCommOn = newVal;
  }

  if (json.indexOf("\"pumpComm\"") != -1) {
    bool newVal = (getValueFromJson(json, "pumpComm").toInt() != 0);
    if (newVal != pumpCommOn) {
      ESP32_EVT(String("Comunicação da bomba peristáltica ") + (newVal ? "ativada" : "desativada"));
    }
    pumpCommOn = newVal;
  }
  
  // Note: distanceSensorReference is updated directly as it is used by internal logic, not sent to UART immediately
  if (json.indexOf("\"distanceSensorReference\"") != -1) {
    float newRef = getValueFromJson(json, "distanceSensorReference").toFloat();
    if (fabs(newRef - distanceSensorReference) > 0.01f) {
        distanceSensorReference = newRef;
        flagDistanceReferenceDirty = true;   // new flag
    }
  }
  
  if (json.indexOf("\"dataDelay\"") != -1) {
    int newDelay = getValueFromJson(json, "dataDelay").toInt();
    dataDelay = (newDelay < 100) ? 100 : newDelay;
  }

  if (json.indexOf("\"oxygenMonitor\"") != -1) {
    oxyOn = (getValueFromJson(json, "oxygenMonitor").toInt() != 0);
  }

  // ============ MOTOR CONTROL ============
  if (json.indexOf("\"motorSetpoint\"") != -1) {
    setMotor(getValueFromJson(json, "motorSetpoint").toInt());
  }

  // ============ TEMPERATURE CONTROL ============
  if (json.indexOf("\"tempSetpoint\"") != -1) {
    setTemperature(getValueFromJson(json, "tempSetpoint").toFloat());
  }

  // ============ PH CONTROL (CRITICAL FIX) ============
  // Use temporary variables so we don't update globals before setPH is called
  float tmp_pHRef = pHReference;
  float tmp_pHErr = pHError;
  int tmp_pHOp = pHOperation;
  int tmp_pHMix = pHMix;
  int tmp_pHInt = pHIntensity;
  bool pHUpdated = false;

  if (json.indexOf("\"pHSetpoint\"") != -1) { tmp_pHRef = getValueFromJson(json, "pHSetpoint").toFloat(); pHUpdated = true; }
  if (json.indexOf("\"pHError\"") != -1) { tmp_pHErr = getValueFromJson(json, "pHError").toFloat(); pHUpdated = true; }
  if (json.indexOf("\"pHOperation\"") != -1) { tmp_pHOp = getValueFromJson(json, "pHOperation").toInt(); pHUpdated = true; }
  if (json.indexOf("\"pHMix\"") != -1) { tmp_pHMix = getValueFromJson(json, "pHMix").toInt(); pHUpdated = true; }
  if (json.indexOf("\"pHIntensity\"") != -1) { tmp_pHInt = getValueFromJson(json, "pHIntensity").toInt(); pHUpdated = true; }

  if (pHUpdated) {
    setPH(tmp_pHRef, tmp_pHErr, tmp_pHOp, tmp_pHMix, tmp_pHInt);
  }
  
  // pH Calibration is handled immediately as it triggers a specific W command
  if (json.indexOf("\"pHCal\"") != -1) {
    float pHFloat = getValueFromJson(json, "pHCal").toFloat();
    int newpHCal = (int)round(pHFloat * 100);
    if (newpHCal != pHCal) { 
       setPHCalibration(newpHCal);
    }
  }

  // ============ NUTRIENT PUMP (CRITICAL FIX) ============
  int tmp_nOp = nutriOperation;
  int tmp_nMix = nutriMix;
  int tmp_nOpC = nutriOpCycle;
  int tmp_nMixC = nutriMixCycle;
  int tmp_nInt = nutriIntensity;
  bool nutriUpdated = false;

  if (json.indexOf("\"nutriOperation\"") != -1) { tmp_nOp = getValueFromJson(json, "nutriOperation").toInt(); nutriUpdated = true; }
  if (json.indexOf("\"nutriMix\"") != -1) { tmp_nMix = getValueFromJson(json, "nutriMix").toInt(); nutriUpdated = true; }
  if (json.indexOf("\"nutriOpCycle\"") != -1) { tmp_nOpC = getValueFromJson(json, "nutriOpCycle").toInt(); nutriUpdated = true; }
  if (json.indexOf("\"nutriMixCycle\"") != -1) { tmp_nMixC = getValueFromJson(json, "nutriMixCycle").toInt(); nutriUpdated = true; }
  if (json.indexOf("\"nutriIntensity\"") != -1) { tmp_nInt = getValueFromJson(json, "nutriIntensity").toInt(); nutriUpdated = true; }

  if (nutriUpdated) {
    setNutrient(tmp_nOp, tmp_nMix, tmp_nOpC, tmp_nMixC, tmp_nInt);
  }

  // ============ ANTIFOAM PUMP (CRITICAL FIX) ============
  int tmp_aOp = antifoamOperation;
  int tmp_aMix = antifoamMix;
  int tmp_aInt = antifoamIntensity;
  bool antiUpdated = false;

  if (json.indexOf("\"antifoamOperation\"") != -1) { tmp_aOp = getValueFromJson(json, "antifoamOperation").toInt(); antiUpdated = true; }
  if (json.indexOf("\"antifoamMix\"") != -1) { tmp_aMix = getValueFromJson(json, "antifoamMix").toInt(); antiUpdated = true; }
  if (json.indexOf("\"antifoamIntensity\"") != -1) { tmp_aInt = getValueFromJson(json, "antifoamIntensity").toInt(); antiUpdated = true; }

  if (antiUpdated) {
    setAntifoam(tmp_aOp, tmp_aMix, tmp_aInt);
  }

  // ============ PRESSURE CONTROL ============
  if (json.indexOf("\"pressureReference\"") != -1) {
    setPressure(getValueFromJson(json, "pressureReference").toInt());
  }

  // ============ FLOWMETER (reliable desired-state pass-through) ============
  // Explicit flow commands are always queued, even while telemetry is offline
  // or the UI enable flag is changing. This is required for safe shutdown.
  queueReliableFlowCommandFromJson(json);

  // ============ BIOMASS (Pass-Through) ============
  static String biomassCommand;
  static bool bioReserved = false;
  if (!bioReserved) {
      biomassCommand.reserve(128);
      bioReserved = true;
  }
  biomassCommand = "";
  bool biomassCmdFound = false;

  String startVal = getValueFromJson(json, "start");
  if (startVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"start\":" + startVal; biomassCmdFound = true; }
  String stopVal = getValueFromJson(json, "stop");
  if (stopVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"stop\":" + stopVal; biomassCmdFound = true; }
  String blankVal = getValueFromJson(json, "blank");
  if (blankVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"blank\":" + blankVal; biomassCmdFound = true; }
  String lowVal = getValueFromJson(json, "low");
  if (lowVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"low\":" + lowVal; biomassCmdFound = true; }
  String highVal = getValueFromJson(json, "high");
  if (highVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"high\":" + highVal; biomassCmdFound = true; }
  String optVal = getValueFromJson(json, "opt");
  if (optVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"opt\":" + optVal; biomassCmdFound = true; }
  String testVal = getValueFromJson(json, "test_period");
  if (testVal.length() > 0) { if (biomassCmdFound) biomassCommand += ","; biomassCommand += "\"test_period\":" + testVal; biomassCmdFound = true; }

  if (biomassCmdFound && biomassCommOn) {
      setPending(pendingBiomassCommand, "{" + biomassCommand + "}");
  }

  // ============ AGITATOR ============
  if (json.indexOf("\"agitatorAuto\"") != -1) {
    agitatorAuto = (getValueFromJson(json, "agitatorAuto").toInt() != 0);
  }
  if (json.indexOf("\"agitatorReEnablePot\"") != -1) {
    agitatorReEnablePot = (getValueFromJson(json, "agitatorReEnablePot").toInt() != 0);
  }
  if (json.indexOf("\"agitatorPercent\"") != -1) {
    int p = getValueFromJson(json, "agitatorPercent").toInt();
    if (p < 0) p = 0; if (p > 100) p = 100;
    agitatorPercentFoam = p;
  }
  if (json.indexOf("\"agitatorDir\"") != -1) {
    agitatorDirFoam = (getValueFromJson(json, "agitatorDir").toInt() != 0) ? 1 : 0;
  }
  
  if (json.indexOf("\"agitatorOn\"") != -1) {
    bool on = (getValueFromJson(json, "agitatorOn").toInt() != 0);
    if (on) {
      queueAgitatorCmd((float)agitatorPercentFoam, agitatorDirFoam, 0);
    } else {
      queueAgitatorCmd(0.0f, agitatorDirFoam, agitatorReEnablePot ? 1 : 0);
    }
  }
  if (json.indexOf("\"foamStartDelay_s\"") != -1) foamStartDelay_s = getValueFromJson(json, "foamStartDelay_s").toFloat();
  if (json.indexOf("\"foamPulse_s\"") != -1) foamPulse_s = getValueFromJson(json, "foamPulse_s").toFloat();
  if (json.indexOf("\"foamInterval_s\"") != -1) foamInterval_s = getValueFromJson(json, "foamInterval_s").toFloat();

  // ============ PUMP (Pass-Through) ============
  static String pumpCommand;
  static bool pumpReserved = false;
  if (!pumpReserved) {
      pumpCommand.reserve(1536);
      pumpReserved = true;
  }
  pumpCommand = "";
  bool pumpCmdFound = false;

  const char* simpleKeys[] = {
    "pump_command", "mode", "pump_speed", "init_t", "final_t",
    "lambda_const", "lambda_linear", "phi_linear", "lambda_exp", "phi_exp"
  };
  const char* polyKeys[] = {
    "p0", "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p9", "p10",
    "p11", "p12", "p13", "p14", "p15", "p16", "p17", "p18", "p19", "p20"
  };

  for (const char* key : simpleKeys) {
    String val = getValueFromJson(json, key);
    if (val.length() > 0) {
      if (pumpCmdFound) pumpCommand += ",";
      String cleanKey = String(key);
      if (cleanKey.startsWith("pump_")) cleanKey = cleanKey.substring(5); 
      if (cleanKey == "command") pumpCommand += "\"" + cleanKey + "\":\"" + val + "\"";
      else pumpCommand += "\"" + cleanKey + "\":" + val;
      pumpCmdFound = true;
    }
  }

  for (const char* key : polyKeys) {
    String val = getValueFromJson(json, key);
    if (val.length() > 0) {
      if (pumpCmdFound) pumpCommand += ",";
      pumpCommand += "\"" + String(key) + "\":" + val;
      pumpCmdFound = true;
    }
  }
  
  bool maybePumpSegments =
    json.indexOf("\"pump_command\"") != -1 ||
    json.indexOf("\"num_segments\"") != -1 ||
    json.indexOf("\"t0\"") != -1 ||
    json.indexOf("\"q0\"") != -1;

  if (maybePumpSegments) {
    const int MAX_PUMP_SEGMENTS = 100;
    for (int i = 0; i < MAX_PUMP_SEGMENTS; i++) {
        String t_key = "t" + String(i);
        String t_val = getValueFromJson(json, t_key);
        if (t_val.length() > 0) {
            if (pumpCmdFound) pumpCommand += ",";
            pumpCommand += "\"" + t_key + "\":" + t_val;
            pumpCmdFound = true;
        }

        String q_key = "q" + String(i);
        String q_val = getValueFromJson(json, q_key);
        if (q_val.length() > 0) {
            if (pumpCmdFound) pumpCommand += ",";
            pumpCommand += "\"" + q_key + "\":" + q_val;
            pumpCmdFound = true;
        }
    }
  }

  String numSegVal = getValueFromJson(json, "num_segments");
  if (numSegVal.length() > 0) {
    if (pumpCmdFound) pumpCommand += ",";
    pumpCommand += "\"num_segments\":" + numSegVal;
    pumpCmdFound = true;
  }

  if (pumpCmdFound && pumpCommOn) {
    setPending(pendingPumpCommand, "{" + pumpCommand + "}");
  }

  // 2. Compara a assinatura do estado DEPOIS das mudanças
  uint32_t postHash = computeStateHash();

  if (preHash != postHash) {
    // Houve mutação no estado das variáveis.
    // Aciona a flag global de salvamento e reseta o cronômetro do debounce.
    flagPendingSave = true;
    lastSaveTriggerTime = millis();
  }
}

// ------------------------------------------------------------------
// readAndBroadcastSensorData():
// ------------------------------------------------------------------
void readAndBroadcastSensorData() {
  // 1. LEITURA DOS SENSORES
  vTaskDelay(pdMS_TO_TICKS(10)); 
  // Measurement is independent from closed-loop control. The sensor must keep
  // publishing PV while tempOn is false; tempOn only enables controller actuation.
  float temperatureVal = -1.0;
  String temperatureResp = sendSensorCommand("b", true);
  if (temperatureResp.length() > 0) {
    temperatureVal = temperatureResp.toFloat();
    if (temperatureVal < 0.0f || temperatureVal > 100.0f) temperatureVal = -1.0f;
  }
  vTaskDelay(pdMS_TO_TICKS(10));
  float pHVal = -1.0;
  if (phOn) {
    String pHResp = sendSensorCommand("k", true);
    if (pHResp.length() > 0) pHVal = pHResp.toFloat();
  }
  vTaskDelay(pdMS_TO_TICKS(10));
  float oxyVal = -1.0;
  if (oxyOn) {
    String oxyResp = sendSensorCommand("g", true);
    if (oxyResp.length() > 0) {
      oxyVal = oxyResp.toFloat();
      if (oxyVal < 0.0f || oxyVal > 4095.0f) oxyVal = -1.0f;
    }
  }
  vTaskDelay(pdMS_TO_TICKS(10));
  float pressureVal = -1.0;
  if (pressureOn) {
    String pressureResp = sendSensorCommand("c", true);
    pressureVal = pressureResp.toFloat();
  }
  vTaskDelay(pdMS_TO_TICKS(10));
  float antifoamVal = -1.0;
  if (antifoamOn) {
    String antifoamResp = sendSensorCommand("e", true);
    antifoamVal = antifoamResp.toFloat();
  }

  float timeSec = millis() / 1000.0;

  // Validação de Distância
  bool validDistance = false;
  if (distanceSensorCommOn) {
    unsigned long age = millis() - distanceSensorLastUpdate;
    if (age <= DISTANCE_TIMEOUT && distanceSensorValue >= 0.0f) {
      validDistance = true;
    } else {
      distanceSensorValue = -1.0f;
    }
  }

  // Validação de Biomassa
  bool validBiomass = false;
  if (biomassCommOn && biomassLastUpdate > 0) {     
      unsigned long age = millis() - biomassLastUpdate;
      if (age <= BIOMASS_TIMEOUT) validBiomass = true;
  }

  // 2. CONSTRUÇÃO DO JSON
  static String jsonResponse;
  static bool jsonReserved = false;
  if (!jsonReserved) {
      jsonResponse.reserve(1536);
      jsonReserved = true;
  }
  
  bool snapFlowOnline;
  bool snapFlowPending;
  bool snapFlowControlEnabled;
  float snapFlowVoltage, snapFlowRate, snapFlowSetpoint;
  int snapValve1, snapValve2, snapValveFlow;
  uint32_t snapFlowRevision, snapFlowAck, snapDeliveryCount;
  unsigned long snapQueuedAt;
  String snapFlowSource;
  if (xSemaphoreTake(cmdMutex, portMAX_DELAY) == pdTRUE) {
    if (flowmeterCommOn && (millis() - flowmeterLastUpdate > FLOWMETER_TIMEOUT)) {
      flowmeterCommOn = false;
    }
    snapFlowOnline = flowmeterCommOn;
    snapFlowPending = flowCommandAwaitingAck;
    snapFlowControlEnabled = flowmeterControlEnabled;
    snapFlowVoltage = flowmeterVoltage;
    snapFlowRate = flowmeterRate;
    snapFlowSetpoint = flowmeterSetpoint;
    snapValve1 = flowmeterValve1;
    snapValve2 = flowmeterValve2;
    snapValveFlow = flowmeterValveFlow;
    snapFlowRevision = flowCommandRevision;
    snapFlowAck = flowCommandAck;
    snapDeliveryCount = flowCommandDeliveryCount;
    snapQueuedAt = flowCommandQueuedAt;
    snapFlowSource = flowmeterLastCommandSource;
    xSemaphoreGive(cmdMutex);
  }

  jsonResponse = "{";
  
  jsonResponse += "\"Time\":" + String(timeSec, 1);
  jsonResponse += ",\"Tempval\":" + String(temperatureVal, 2);
  jsonResponse += ",\"pHval\":" + String(pHVal, 2);
  jsonResponse += ",\"Oxyval\":" + String(oxyVal, 1);
  jsonResponse += ",\"Antifoam\":" + String(antifoamVal, 0);
  jsonResponse += ",\"Pressure\":" + String(pressureVal, 1);
  jsonResponse += ",\"HubStations\":" + String(WiFi.softAPgetStationNum());
  
  jsonResponse += ",\"FlowmeterOnline\":" + String(snapFlowOnline ? "true" : "false");
  jsonResponse += ",\"FlowControlEnabled\":" + String(snapFlowControlEnabled ? "true" : "false");
  jsonResponse += ",\"FlowCommandPending\":" + String(snapFlowPending ? "true" : "false");
  jsonResponse += ",\"FlowCommandId\":" + String(snapFlowRevision);
  jsonResponse += ",\"FlowCommandAck\":" + String(snapFlowAck);
  jsonResponse += ",\"FlowCommandDeliveries\":" + String(snapDeliveryCount);
  jsonResponse += ",\"FlowCommandAgeMs\":" + String(snapFlowPending ? millis() - snapQueuedAt : 0);
  jsonResponse += ",\"FlowCommandSource\":\"" + snapFlowSource + "\"";
  if (snapFlowOnline) {
    jsonResponse += ",\"FlowVoltage\":" + String(snapFlowVoltage, 4);
    jsonResponse += ",\"FlowRate\":" + String(snapFlowRate, 4);
    jsonResponse += ",\"FlowSetpoint\":" + String(snapFlowSetpoint, 4);
    jsonResponse += ",\"Valve1\":" + String(snapValve1);
    jsonResponse += ",\"Valve2\":" + String(snapValve2);
    jsonResponse += ",\"ValveFlow\":" + String(snapValveFlow);
  }

  if (validBiomass) {
      jsonResponse += ",\"BiomassAbs\":" + String(biomassAbsorbance, 3);
      jsonResponse += ",\"BiomassRaw\":" + String(biomassRaw);
      jsonResponse += ",\"BiomassIT\":" + String(biomassIt);
      jsonResponse += ",\"BiomassPWM\":" + String(biomassPwm, 1);
  }

  if (validDistance) {
    jsonResponse += ",\"Distance\":" + String(distanceSensorValue, 2);
  }

  if (pumpCommOn) { 
    jsonResponse += ",\"PumpMode\":" + String(pumpMode);
    jsonResponse += ",\"PumpPWM\":" + String(pumpPwm);
    jsonResponse += ",\"PumpSpeed\":" + String(pumpSpeed, 1);
    jsonResponse += ",\"PumpFlow\":" + String(pumpFlowRate, 3);
    jsonResponse += ",\"PumpVol\":" + String(pumpVolume, 3);
    jsonResponse += ",\"PumpTargetVol\":" + String(pumpTargetVolume, 3);
    jsonResponse += ",\"PumpActive\":" + String(pumpActive ? "true" : "false");
    jsonResponse += ",\"PumpWaiting\":" + String(pumpWaiting ? "true" : "false");
  }

  jsonResponse += ",\"SensorCommOK\":" + String(uartSensorOK ? "true" : "false");
  jsonResponse += "}";

  lastSensorJson = jsonResponse;
  sampleId++;
  
  Serial.println(jsonResponse); 

  lastReadBroadcastTime = millis();
}

// ------------------------------------------------------------------
// queueAgitatorCmd():
// ------------------------------------------------------------------
void queueAgitatorCmd(float pct, int dir, int activePot)
{
  if (pct < 0.0f) pct = 0.0f;
  if (pct > 100.0f) pct = 100.0f;
  dir = (dir != 0) ? 1 : 0;
  activePot = (activePot != 0) ? 1 : 0;
  char buf[96];
  snprintf(buf, sizeof(buf),
           "{\"RPM_percent\":%.1f,\"Dir\":%d,\"ActivePot\":%d}",
           pct, dir, activePot);
  // Coloca na fila (thread-safe)
  setPending(pendingAgitatorCommand, String(buf));
}

// ------------------------------------------------------------------
// checkDistanceSensorReference():
// (Lógica de espuma de zona dupla: Normal vs Emergência)
// ------------------------------------------------------------------
void checkDistanceSensorReference()
{
  static bool   foam           = false;
  static bool   mixerOn        = false;
  static bool   dosingOn       = false;
  static unsigned long lastMs = 0;      
  static unsigned long normal_sumOnMs = 0;
  static unsigned long normal_nextDoseCumMs = 0;
  static unsigned long normal_doseStartMs = 0;
  static unsigned long emergency_doseStartMs = 0;
  static unsigned long emergency_lastDoseTimestamp = 0;
  const float SQUIRT_ZONE_LIMIT = 10.0f; 
  const unsigned long EMERGENCY_DOSE_INTERVAL_MS = 600000; // 10 min
  const unsigned long EMERGENCY_DOSE_PULSE_MS = 1000;      // 1 seg
  const unsigned long now = millis();
  static bool lastDataStale = false;
  static bool emergencyMode = false;

  // --- NEW: reset all static state when reference was changed mid-run ---
  if (flagDistanceReferenceDirty) {
    flagDistanceReferenceDirty = false;

    // Stop any active dosing or mixing immediately
    if (dosingOn) {
      sendSensorCommand("0M", false);
      dosingOn = false;
    }
    if (mixerOn && agitatorAuto) {
      queueAgitatorCmd(0.0f, agitatorDirFoam, agitatorReEnablePot ? 1 : 0);
      mixerOn = false;
    }

    // Reset all timers and state flags
    foam                         = false;
    normal_sumOnMs               = 0;
    normal_nextDoseCumMs         = 0;
    normal_doseStartMs           = 0;
    emergency_doseStartMs        = 0;
    emergency_lastDoseTimestamp  = 0;
    lastMs                       = 0;   // forces re-anchor on next tick
    lastDataStale                = false;
    emergencyMode                = false;

    ESP32_EVT(String("Referencia do sensor de distancia alterada para ")
              + distanceSensorReference
              + String("; estado da logica de espuma reiniciado"));
  }

  if (lastMs == 0) lastMs = now;

  // Helpers de ação (chamam sendSensorCommand, que é 'thread-safe')
  auto doseOff = [&]() { sendSensorCommand("0M", false); dosingOn = false; };
  auto doseOn = [&](int intensity) {
    if (intensity < 0) intensity = 0; if (intensity > 99) intensity = 99;
    sendSensorCommand(String(intensity) + "0M", false); dosingOn = (intensity > 0);
  };
  auto stopMixer = [&]() {
    if (mixerOn && agitatorAuto) {
      queueAgitatorCmd(0.0f, agitatorDirFoam, agitatorReEnablePot ? 1 : 0);
      mixerOn = false;
    }
  };
  auto startMixer = [&]() {
    if (!mixerOn && agitatorAuto) {
      queueAgitatorCmd((float)agitatorPercentFoam, agitatorDirFoam, 0);
      mixerOn = true;
    }
  };
  auto resetNormalTimers = [&]() {
    normal_sumOnMs = 0; normal_nextDoseCumMs = 0; normal_doseStartMs = 0;
  };
  auto resetEmergencyTimers = [&]() {
    emergency_doseStartMs = 0; emergency_lastDoseTimestamp = 0;
  };
  auto allStop = [&]() {
    if (dosingOn) doseOff();
    stopMixer();
    foam = false;
    resetNormalTimers();
    resetEmergencyTimers();
    lastMs = now;
  };

  // Se a referência for 0, desliga tudo
  if (distanceSensorReference <= 0.0f) {
    if (foam || mixerOn || dosingOn) {
      ESP32_EVT("Lógica de espuma desativada: referência do sensor de distância zerada");
    }
    allStop();
    emergencyMode = false;
    lastDataStale = false;
    return;
  }

  // Se os dados estiverem velhos, desliga tudo
  const bool dataStale = !distanceSensorCommOn ||
                        (now - distanceSensorLastUpdate > DISTANCE_TIMEOUT) ||
                        (distanceSensorValue < 0.0f);

  if (dataStale) {
    if (!lastDataStale) {
      ESP32_AVISO("Lógica de espuma pausada: dados do sensor de distância inválidos ou expirados");
    }
    lastDataStale = true;
    allStop();
    emergencyMode = false;
    return;
  }
  lastDataStale = false;

  // Lógica de histerese
  const float h = 0.5f;
  const float foamThreshold_lower = distanceSensorReference - h;
  const float foamThreshold_upper = distanceSensorReference + h;
  const float d = distanceSensorValue;
  bool currentFoamFlag;
  if (d > 0.0f && d <= foamThreshold_lower)   currentFoamFlag = true;
  else if (d > foamThreshold_upper)          currentFoamFlag = false;
  else                                       currentFoamFlag = foam;
  
  // Acumula tempo de espuma
  const unsigned long dt = (now - lastMs);
  if (currentFoamFlag) normal_sumOnMs += dt;
  lastMs = now;

  // Transições de estado
  if (currentFoamFlag && !foam) { // Espuma acabou de aparecer
    ESP32_EVT("Espuma detectada; iniciando resposta automática");
    startMixer();
    resetNormalTimers();
    resetEmergencyTimers();
    normal_nextDoseCumMs = (unsigned long)(foamStartDelay_s * 1000.0f);
    if (dosingOn) doseOff();
  }
  if (!currentFoamFlag && foam) { // Espuma acabou de sumir
    ESP32_EVT("Espuma normalizada; desligando resposta automática");
    allStop();
    emergencyMode = false;
  }
  foam = currentFoamFlag;

  bool nowEmergency = (foam && d <= SQUIRT_ZONE_LIMIT);

  if (nowEmergency && !emergencyMode) {
    ESP32_AVISO("Modo de emergência de espuma ativado");
  }
  if (!nowEmergency && emergencyMode) {
    ESP32_EVT("Modo de emergência de espuma desativado");
  }
  emergencyMode = nowEmergency;

  // Máquina de estados (só roda se houver espuma)
  if (foam) {
    if (d <= SQUIRT_ZONE_LIMIT) {
      // ESTADO DE EMERGÊNCIA (Esguicho/Obstrução)
      if (dosingOn && normal_doseStartMs > 0) {
        doseOff(); // Para qualquer pulso normal
        normal_doseStartMs = 0;
      }
      if (!dosingOn) {
        if (now - emergency_lastDoseTimestamp >= EMERGENCY_DOSE_INTERVAL_MS) {
          doseOn(99); // Pulso de emergência
          emergency_doseStartMs = now;
          emergency_lastDoseTimestamp = now;
        }
      } else {
        if (emergency_doseStartMs > 0 && (now - emergency_doseStartMs >= EMERGENCY_DOSE_PULSE_MS)) {
          doseOff(); // Termina o pulso de emergência
          emergency_doseStartMs = 0;
        }
      }
    } else {
      // ESTADO NORMAL (Espuma Real)
      if (dosingOn && emergency_doseStartMs > 0) {
        doseOff(); // Para qualquer pulso de emergência
        emergency_doseStartMs = 0;
      }
      if (!dosingOn) {
        if (normal_sumOnMs >= normal_nextDoseCumMs) {
          doseOn(99); // Pulso normal (Y)
          normal_doseStartMs = now;
          normal_nextDoseCumMs += (unsigned long)(foamInterval_s * 1000.0f); // Agenda próximo (Z)
        }
      } else {
        if (normal_doseStartMs > 0 && (now - normal_doseStartMs >= (unsigned long)(foamPulse_s * 1000.0f))) {
          doseOff(); // Termina o pulso normal (Y)
          normal_doseStartMs = 0;
        }
      }
    }
  }
}

// ------------------------------------------------------------------
// Funções 'set' (Non-blocking, updates variables and flags)
// ------------------------------------------------------------------

void setMotor(int rpm) {
  // Range Check
  if (rpm < 0) rpm = 0;
  if (rpm > 1000) rpm = 1000;
  
  // Check change
  if (motorRPM != rpm) {
      motorRPM = rpm;
      flagMotorDirty = true;
  }
}

void setTemperature(float temp) {
  // Range Check (typical bioreactor range)
  if (temp < 0.0f) temp = 0.0f;
  if (temp > 100.0f) temp = 100.0f;

  if (fabs(tempReference - temp) > 0.01f) {
      tempReference = temp;
      flagTempDirty = true;
  }
}

void setPH(float pH, float err, int op, int mix, int intensity) {
  // Range Checks
  if (pH < 0.0f) pH = 0.0f; 
  if (pH > 14.0f) pH = 14.0f;
  if (err < 0.0f) err = 0.0f;
  if (op < 0) op = 0; 
  if (mix < 0) mix = 0; 
  if (intensity < 0) intensity = 0; if (intensity > 999) intensity = 999;

  // Detect ANY change
  bool changed = false;
  if (fabs(pHReference - pH) > 0.01f) changed = true;
  if (fabs(pHError - err) > 0.01f) changed = true;
  if (pHOperation != op) changed = true;
  if (pHMix != mix) changed = true;
  if (pHIntensity != intensity) changed = true;
  
  pHReference = pH;
  pHError = err;
  pHOperation = op;
  pHMix = mix;
  pHIntensity = intensity;
  
  if (changed) flagPhDirty = true;
}

// setPHCalibration remains direct because it is a manual action
void setPHCalibration(int calVal) {
    pHCal = calVal; 
    sendSensorCommand(String(pHCal) + "W", false); 
    vTaskDelay(pdMS_TO_TICKS(10)); 
}

void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity) {
  // Range Checks
  if (op < 0) op = 0; 
  if (mix < 0) mix = 0;
  if (opCycle < 0) opCycle = 0;
  if (mixCycle < 0) mixCycle = 0;
  if (intensity < 0) intensity = 0; if (intensity > 99) intensity = 99;

  // Detect ANY change
  bool changed = false;
  if (nutriOperation != op) changed = true;
  if (nutriMix != mix) changed = true;
  if (nutriOpCycle != opCycle) changed = true;
  if (nutriMixCycle != mixCycle) changed = true;
  if (nutriIntensity != intensity) changed = true;

  nutriOperation = op;
  nutriMix = mix;
  nutriOpCycle = opCycle;
  nutriMixCycle = mixCycle;
  nutriIntensity = intensity;
  
  if (changed) flagNutriDirty = true;
}

void setAntifoam(int op, int mix, int intensity) {
  // Range Checks
  if (op < 0) op = 0; 
  if (mix < 0) mix = 0;
  if (intensity < 0) intensity = 0; if (intensity > 99) intensity = 99;

  bool changed = false;
  if (antifoamOperation != op) changed = true;
  if (antifoamMix != mix) changed = true;
  if (antifoamIntensity != intensity) changed = true;

  antifoamOperation = op;
  antifoamMix = mix;
  antifoamIntensity = intensity;
  
  if (changed) flagAntiDirty = true;
}

void setPressure(int ref) {
  if (ref < 0) ref = 0;
  if (ref > 380) ref = 380; // Assuming 380 is hardware max
  
  if (pressureReference != ref) {
      pressureReference = ref;
      flagPressureDirty = true;
  }
}

// ------------------------------------------------------------------
// sendSensorCommand():
//   Função 'core' de I/O da UART. Protegida por Mutex.
// ------------------------------------------------------------------
String sendSensorCommand(const String &cmd, bool readResponse) {
  if (!uartSensorOK && millis() < uartCooldownUntil) {
    return "";
  }

  if (xSemaphoreTake(sensorSerialMutex, pdMS_TO_TICKS(300)) != pdTRUE) {
    ESP32_ERRO("Timeout ao obter mutex da UART do Módulo TECNAL");
    return "";
  }

  static String response;
  static bool responseReserved = false;
  if (!responseReserved) {
    response.reserve(64);
    responseReserved = true;
  }

  response = "";   // clear stale reply every call
  bool needSyncAfterUnlock = false;

  clearSensorRxLocked();

  sensorSerial.print(cmd);
  sensorSerial.flush();

  if (!readResponse) {
    const uint32_t ackWindowMs = 20;
    const uint32_t t0 = millis();
    while ((millis() - t0) < ackWindowMs) {
      while (sensorSerial.available() > 0) {
        sensorSerial.read();
      }
      vTaskDelay(pdMS_TO_TICKS(1));
    }
    xSemaphoreGive(sensorSerialMutex);
    return "";
  }

  const uint32_t globalTimeoutMs = 250;
  const uint32_t interByteTimeoutMs = 15;
  const uint32_t tStart = millis();
  uint32_t lastByteTime = tStart;
  bool gotAnyByte = false;
  bool lineComplete = false;

  while (!lineComplete && (millis() - tStart) < globalTimeoutMs) {
    while (sensorSerial.available() > 0) {
      char c = sensorSerial.read();
      gotAnyByte = true;
      lastByteTime = millis();

      if (c == '\r' || c == '\n') {
        if (response.length() > 0) {
          lineComplete = true;
          break;
        }
      } else {
        if (response.length() < (MAX_SENSOR_BUFFER_LENGTH - 1)) {
          response += c;
        } else {
          lineComplete = true;
          break;
        }
      }
    }

    if (gotAnyByte && (millis() - lastByteTime > interByteTimeoutMs)) {
      lineComplete = true;
    }

    if (!lineComplete) {
      vTaskDelay(pdMS_TO_TICKS(1));
    }
  }

  response.trim();
  bool valid = gotAnyByte && isValidSensorReply(response);

  if (valid) {
    if (!uartSensorOK) {
      needSyncAfterUnlock = true;
    }
    uartSensorOK = true;
    uartFailureCount = 0;
  } else {
    uartFailureCount++;
    if (uartFailureCount == 1) {
      ESP32_AVISO(String("Falha de leitura UART do Módulo TECNAL após comando '") + cmd + "'");
    }
    if (uartFailureCount >= UART_FAILURE_THRESHOLD) {
      uartFailureCount = UART_FAILURE_THRESHOLD;
      uartSensorOK = false;
      ESP32_AVISO("Limite de falhas UART atingido; reinicializando UART e iniciando cooldown de 5s");
      resetSensorUartLocked();
      uartCooldownUntil = millis() + UART_COOLDOWN_MS;
    }
    response = "";
  }

  xSemaphoreGive(sensorSerialMutex);

  if (needSyncAfterUnlock) {
    ESP32_EVT("Módulo TECNAL voltou a responder; sincronização agendada");
    syncAllSensorSettings();
  }

  if (cmd == "g" && response.length() > 0) {
    float rawValue = response.toFloat();
    float calibrated = a * rawValue + b;
    return bypassMode ? String(calibrated, 1) : String(rawValue, 4);
  }

  return response;
}

// ------------------------------------------------------------------
// removeDecimal():
// ------------------------------------------------------------------
String removeDecimal(String s) {
  s.replace(",", ".");
  s.replace(".", "");
  return s;
}

static void clearSensorRxLocked() {
  while (sensorSerial.available() > 0) {
    sensorSerial.read();
  }
}

static void resetSensorUartLocked() {
  sensorSerial.flush();
  clearSensorRxLocked();
  sensorSerial.end();
  vTaskDelay(pdMS_TO_TICKS(30));
  sensorSerial.begin(9600, SERIAL_8N1, SENSOR_RX_PIN, SENSOR_TX_PIN);
  clearSensorRxLocked();
}

// ------------------------------------------------------------------
// syncAllSensorSettings():
//   Forces the resending of all configurations to the hardware.
//   CORRECTED: Manually raises flags to bypass the "if(changed)" check
//   inside the setters during startup.
// ------------------------------------------------------------------
void syncAllSensorSettings() {
  ESP32_EVT("Sincronização das configurações com o Módulo TECNAL iniciada");
  vTaskDelay(pdMS_TO_TICKS(100));

  setMotor(motorRPM);
  flagMotorDirty = true;

  setTemperature(tempReference);
  flagTempDirty = true;

  setPH(pHReference, pHError, pHOperation, pHMix, pHIntensity);
  flagPhDirty = true;

  setNutrient(nutriOperation, nutriMix, nutriOpCycle, nutriMixCycle, nutriIntensity);
  flagNutriDirty = true;

  setAntifoam(antifoamOperation, antifoamMix, antifoamIntensity);
  flagAntiDirty = true;

  setPressure(pressureReference);
  flagPressureDirty = true;

  ESP32_INFO("Sincronização agendada; o loop principal enviará os comandos");

  if (biomassCommOn) {
    ESP32_INFO("Comando de início do sensor de biomassa reenfileirado");
    setPending(pendingBiomassCommand, "{\"start\":1}");
  }
}
