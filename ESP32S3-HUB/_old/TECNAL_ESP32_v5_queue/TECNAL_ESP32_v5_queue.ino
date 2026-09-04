/*************************************************************
 * SensorHub ESP32-S3 Code - Versão de Implementação
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
#include <Preferences.h>
#include <math.h>

// Mutex para proteger a porta serial do sensor
SemaphoreHandle_t sensorSerialMutex = NULL;

// Mutex para proteger as filas de comando (pending... strings)
SemaphoreHandle_t cmdMutex = NULL;

// ============ USER SETTINGS =============
#define WIFI_SSID "ModuloTECNAL_2"
#define WIFI_PASSWORD "ModuloTECNAL_2"
#define JSON_BUFFER_LEN 256
#define MAX_HTTP_PAYLOAD 2048 // Limite de segurança para JSON em /command

// ============ GLOBAL OBJECTS ============
AsyncWebServer server(80);
String lastSensorJson = "";
uint32_t sampleId = 0;

// Filas de comando (protegidas por cmdMutex)
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
  #define ESP32_INFO(msg)   Serial.println(String("[ESP32_INFO]: ") + (msg))
  #define ESP32_AVISO(msg)  Serial.println(String("[ESP32_AVISO]: ") + (msg))
  #define ESP32_ERRO(msg)   Serial.println(String("[ESP32_ERRO]: ") + (msg))
  #define ESP32_EVT(msg)    Serial.println(String("[ESP32_EVT]: ") + (msg))
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

// ============ GLOBAL VARIABLES ============motorRPM
float tempReference = 0.0f;
float pHReference = 0.0f;
float pHError = 0.0f;
int   pHCal = 0;
int   pHOperation = 0;
int   pHMix = 0;
int   pHIntensity = 0;
int   motorRPM = 0;
int   nutriOperation = 0;
int   nutriMix = 0;
int   nutriOpCycle = 0;
int   nutriMixCycle = 0;
int   nutriIntensity = 0;
int   antifoamOperation = 0;
int   antifoamMix = 0;
int   antifoamIntensity = 0;
int   pressureReference = 0;
bool  oxyOn = false;
bool  tempOn = false;
bool  phOn = false;
bool  nutrientOn = false;
bool  antifoamOn = false;
bool  pressureOn = false;
bool  bypassMode = false;
unsigned long dataDelay = 1000;
unsigned long lastDataMillis = 0;
unsigned long lastReadBroadcastTime = 0; 
String sensorBuffer = "";
bool uartSensorOK = true;
int uartFailureCount = 0;
const int UART_FAILURE_THRESHOLD = 3;
float a = 0.0305473419314;
float b = -25.09136520919;
bool  flowmeterCommOn = false;
float flowmeterTime = 0.0;
float flowmeterVoltage = 0.0;
float flowmeterRate = 0.0;
float flowmeterSetpoint = 0.0;
float lastFlowSetpointSent = -9999.0f;
int   flowmeterValve1 = 0;
int   flowmeterValve2 = 0;
bool  distanceSensorCommOn = false;
float distanceSensorTime = 0.0;
float distanceSensorValue = -1.0f;
unsigned long distanceSensorLastUpdate = 0;
const unsigned long DISTANCE_TIMEOUT = 1200;
float distanceSensorReference = 0.0;
bool  biomassCommOn = false;
float biomassAbsorbance = 0.0f;
int   biomassRaw = 0;
int   biomassIt = 0;
float biomassPwm = 0.0f;
unsigned long biomassLastUpdate = 0;
const unsigned long BIOMASS_TIMEOUT = 10000;
bool   agitatorAuto          = true;
bool   agitatorReEnablePot   = true;
int    agitatorPercentFoam   = 80;
int    agitatorDirFoam       = 1;
float  foamStartDelay_s      = 1.0f;
float  foamPulse_s           = 1.0f;
float  foamInterval_s        = 5.0f;
bool  pumpCommOn = false;
int   pumpMode = 0;
int   pumpPwm = 0;
float pumpSpeed = 0.0f;
float pumpFlowRate = 0.0f;
float pumpVolume = 0.0f;
float pumpTargetVolume = 0.0f;
bool  pumpActive = false;
bool  pumpWaiting = false;

// ============ PERSISTENCE ============
Preferences preferences;

// ============ MEMORY SAVE FLAGS ============
volatile bool flagPendingSave = false;
unsigned long lastSaveTriggerTime = 0;
const unsigned long SAVE_DEBOUNCE_MS = 15000; // Aguarda 5s de inatividade antes de gravar na Flash

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
  preferences.putBool("flowComm", flowmeterCommOn);
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
  flowmeterCommOn = preferences.getBool("flowComm", false);
  pumpCommOn = preferences.getBool("pumpComm", false);
}

void debugSettings() {
  Serial.println("==== Loaded Preferences ====");
  Serial.print("tempReference: "); Serial.println(tempReference);
  Serial.print("pHReference: "); Serial.println(pHReference);
  Serial.print("pHError: "); Serial.println(pHError);
  Serial.print("pHCal: "); Serial.println(pHCal);
  Serial.print("pHOperation: "); Serial.println(pHOperation);
  Serial.print("pHMix: "); Serial.println(pHMix);
  Serial.print("pHIntensity: "); Serial.println(pHIntensity);
  Serial.print("motorRPM: "); Serial.println(motorRPM);
  Serial.print("nutriOperation: "); Serial.println(nutriOperation);
  Serial.print("nutriMix: "); Serial.println(nutriMix);
  Serial.print("nutriOpCycle: "); Serial.println(nutriOpCycle);
  Serial.print("nutriMixCycle: "); Serial.println(nutriMixCycle);
  Serial.print("nutriIntensity: "); Serial.println(nutriIntensity);
  Serial.print("antifoamOperation: "); Serial.println(antifoamOperation);
  Serial.print("antifoamMix: "); Serial.println(antifoamMix);
  Serial.print("antifoamIntensity: "); Serial.println(antifoamIntensity);
  Serial.print("pressureReference: "); Serial.println(pressureReference);
  Serial.print("distanceSensorReference: "); Serial.println(distanceSensorReference);
  Serial.print("distanceSensorCommOn: "); Serial.println(distanceSensorCommOn);
  Serial.print("dataDelay: "); Serial.println(dataDelay);
  Serial.print("oxyOn: "); Serial.println(oxyOn);
  Serial.print("tempOn: "); Serial.println(tempOn);
  Serial.print("phOn: "); Serial.println(phOn);
  Serial.print("nutrientOn: "); Serial.println(nutrientOn);
  Serial.print("antifoamOn: "); Serial.println(antifoamOn);
  Serial.print("pressureOn: "); Serial.println(pressureOn);
  Serial.print("agitatorAuto: "); Serial.println(agitatorAuto);
  Serial.print("agitatorReEnablePot: "); Serial.println(agitatorReEnablePot);
  Serial.print("agitatorPercentFoam: "); Serial.println(agitatorPercentFoam);
  Serial.print("agitatorDirFoam: "); Serial.println(agitatorDirFoam);
  Serial.print("foamStartDelay_s: "); Serial.println(foamStartDelay_s);
  Serial.print("foamPulse_s: "); Serial.println(foamPulse_s);
  Serial.print("foamInterval_s: "); Serial.println(foamInterval_s);
  Serial.print("biomassCommOn: "); Serial.println(biomassCommOn);
  Serial.print("flowmeterCommOn: "); Serial.println(flowmeterCommOn);
  Serial.print("pumpCommOn: "); Serial.println(pumpCommOn);
  Serial.println("============================");
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

// ============ FUNCTION DECLARATIONS ============
void handleUSBCommands();
String processCommandData(const String &data);
void processJsonCommand(const String &json);
void readAndBroadcastSensorData();
void setMotor(int rpm);
void setTemperature(float t);
void setPH(float pH, float err, int op, int mix, int intensity);
void setPHCalibration(int calVal);
void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity);
void setAntifoam(int op, int mix, int intensity);
void setPressure(int ref);
String sendSensorCommand(const String &cmd, bool readResponse);
String removeDecimal(const String &s);
void updateParametersCycle();
void checkDistanceSensorReference();
void syncAllSensorSettings();

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
  bool apStarted = WiFi.softAP(WIFI_SSID, WIFI_PASSWORD, 6, 0, 4); 

  if (apStarted) {
    ESP32_INFO("ponto de acesso Wi-Fi iniciado");
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
        flowmeterTime = request->getParam("seconds")->value().toFloat();
        flowmeterVoltage = request->getParam("flow_voltage")->value().toFloat();
        flowmeterRate = request->getParam("flow_rate")->value().toFloat();
        flowmeterSetpoint = request->getParam("flow_setpoint")->value().toFloat();
        flowmeterValve1 = request->getParam("valve1State")->value().toInt();
        flowmeterValve2 = request->getParam("valve2State")->value().toInt();
        flowmeterCommOn = true;
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
      request->send(200, "application/json", takePending(pendingFlowmeterCommand));
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
      ESP32_ERRO("falha ao iniciar o SoftAP; reiniciando ESP32");
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
    ESP32_ERRO(String("falha ao inicializar o watchdog: ") + esp_err_to_name(init_result));
  } else {
    ESP32_INFO("watchdog inicializado");
  }

  esp_err_t add_result = esp_task_wdt_add(NULL);
  if (add_result != ESP_OK) {
    ESP32_ERRO(String("falha ao adicionar a tarefa atual ao watchdog: ") + esp_err_to_name(add_result));
  } else {
    ESP32_INFO("tarefa principal registrada no watchdog");
  }
}

// ============ COMMAND PROCESSOR (COMPLETE) ============
// This function runs inside the main loop. It checks "dirty flags"
// and sends ALL parameters for that subsystem to ensure hardware logic works.
// ============ COMMAND PROCESSOR (FIXED & REORDERED) ============
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
  ESP32_INFO("inicialização iniciada");

  lastSensorJson.reserve(768);
  preferences.begin("SensorHub", false);
  loadSettings();
  // debugSettings();
  startWatchDog();

  sensorSerialMutex = xSemaphoreCreateMutex();
  cmdMutex = xSemaphoreCreateMutex();
  if (sensorSerialMutex == NULL || cmdMutex == NULL) {
    ESP32_ERRO("não foi possível criar os mutexes; reiniciando ESP32");
    ESP.restart();
  }

  ESP32_INFO("mutexes criados com sucesso");

  sensorSerial.begin(9600, SERIAL_8N1, SENSOR_RX_PIN, SENSOR_TX_PIN);
  ESP32_INFO("UART do Módulo TECNAL iniciada em 9600 baud");

  startWiFi();
  syncAllSensorSettings();

  ESP32_EVT("inicialização concluída");
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
}

// ------------------------------------------------------------------
// handleUSBCommands():
// ------------------------------------------------------------------
void handleUSBCommands() {
  String data = "";
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
  // Comandos JSON ativam o modo normal
  if (data.startsWith("{") && data.endsWith("}")) {
      if (bypassMode) { bypassMode = false; }
      processJsonCommand(data);
      return "OK";
  // Comandos diretos ativam o modo bypass
  } else {
      if (!bypassMode) { bypassMode = true; }
      String response = sendSensorCommand(data, true);
      Serial.print(response); // Ecoa a resposta no USB
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
  hash += (flowmeterCommOn ? 1 : 0) * 157;
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
    lastFlowSetpointSent = -9999.0f;
    saveSettings();
    return;
  }

  if (json.indexOf("\"restart\"") != -1) {
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
    flowmeterCommOn = (getValueFromJson(json, "flowmeterComm").toInt() != 0);
  }
  if (json.indexOf("\"biomassComm\"") != -1) {
    biomassCommOn = (getValueFromJson(json, "biomassComm").toInt() != 0);
  }
  if (json.indexOf("\"distanceSensorComm\"") != -1) {
    distanceSensorCommOn = (getValueFromJson(json, "distanceSensorComm").toInt() != 0);
  }
  if (json.indexOf("\"pumpComm\"") != -1) { 
    pumpCommOn = (getValueFromJson(json, "pumpComm").toInt() != 0);
  }
  
  // Note: distanceSensorReference is updated directly as it is used by internal logic, not sent to UART immediately
  if (json.indexOf("\"distanceSensorReference\"") != -1) {
    distanceSensorReference = getValueFromJson(json, "distanceSensorReference").toFloat();
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

  // ============ FLOWMETER (Pass-Through) ============
  static String flowmeterCommand;
  static bool flowReserved = false;
  if (!flowReserved) {
      flowmeterCommand.reserve(128);
      flowReserved = true;
  }
  flowmeterCommand = ""; 
  bool flowmeterCmdFound = false;
  
  if (json.indexOf("\"flowSetpoint\"") != -1) {
    String sVal = getValueFromJson(json, "flowSetpoint");
    float fVal = sVal.toFloat();

    // Change detection logic for Flowmeter Setpoint
    if (fabs(fVal - lastFlowSetpointSent) > 0.001f) {
       flowmeterCommand += "\"flow_setpoint\":" + sVal; 
       flowmeterCmdFound = true;
       lastFlowSetpointSent = fVal; 
    }
  }
  
  if (json.indexOf("\"maxFlow\"") != -1) { 
    if (flowmeterCmdFound) flowmeterCommand += ","; 
    flowmeterCommand += "\"max_flow\":" + getValueFromJson(json, "maxFlow"); 
    flowmeterCmdFound = true; 
  }
  if (json.indexOf("\"k1\"") != -1) { if (flowmeterCmdFound) flowmeterCommand += ","; flowmeterCommand += "\"k1\":" + getValueFromJson(json, "k1"); flowmeterCmdFound = true; }
  if (json.indexOf("\"f1\"") != -1) { if (flowmeterCmdFound) flowmeterCommand += ","; flowmeterCommand += "\"f1\":" + getValueFromJson(json, "f1"); flowmeterCmdFound = true; }
  if (json.indexOf("\"c1\"") != -1) { if (flowmeterCmdFound) flowmeterCommand += ","; flowmeterCommand += "\"c1\":" + getValueFromJson(json, "c1"); flowmeterCmdFound = true; }
  if (json.indexOf("\"k2\"") != -1) { if (flowmeterCmdFound) flowmeterCommand += ","; flowmeterCommand += "\"k2\":" + getValueFromJson(json, "k2"); flowmeterCmdFound = true; }
  if (json.indexOf("\"f2\"") != -1) { if (flowmeterCmdFound) flowmeterCommand += ","; flowmeterCommand += "\"f2\":" + getValueFromJson(json, "f2"); flowmeterCmdFound = true; }
  if (json.indexOf("\"c2\"") != -1) { if (flowmeterCmdFound) flowmeterCommand += ","; flowmeterCommand += "\"c2\":" + getValueFromJson(json, "c2"); flowmeterCmdFound = true; }
  if (json.indexOf("\"valve_1\"") != -1) { if (flowmeterCmdFound) flowmeterCommand += ","; flowmeterCommand += "\"v1\":" + getValueFromJson(json, "valve_1"); flowmeterCmdFound = true; }
  if (json.indexOf("\"valve_2\"") != -1) { if (flowmeterCmdFound) flowmeterCommand += ","; flowmeterCommand += "\"v2\":" + getValueFromJson(json, "valve_2"); flowmeterCmdFound = true; }
  if (json.indexOf("\"v_Flow\"") != -1) { if (flowmeterCmdFound) flowmeterCommand += ","; flowmeterCommand += "\"v_Flow\":" + getValueFromJson(json, "v_Flow"); flowmeterCmdFound = true; }

  if (flowmeterCmdFound && flowmeterCommOn) {
    setPending(pendingFlowmeterCommand, "{" + flowmeterCommand + "}");
  }

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
  float temperatureVal = -1.0;
  if (tempOn) {
    String temperatureResp = sendSensorCommand("b", true);
    if (temperatureResp.length() > 0) {
      temperatureVal = temperatureResp.toFloat();
      if (temperatureVal < 0.0f || temperatureVal > 100.0f) temperatureVal = -1.0f;
    }
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
      jsonResponse.reserve(768);
      jsonReserved = true;
  }
  
  jsonResponse = "{";
  
  jsonResponse += "\"Time\":" + String(timeSec, 1);
  jsonResponse += ",\"Tempval\":" + String(temperatureVal, 2);
  jsonResponse += ",\"pHval\":" + String(pHVal, 2);
  jsonResponse += ",\"Oxyval\":" + String(oxyVal, 1);
  jsonResponse += ",\"Antifoam\":" + String(antifoamVal, 0);
  jsonResponse += ",\"Pressure\":" + String(pressureVal, 1);
  
  if (flowmeterCommOn) {
    jsonResponse += ",\"FlowVoltage\":" + String(flowmeterVoltage, 4);
    jsonResponse += ",\"FlowRate\":" + String(flowmeterRate, 4);
    jsonResponse += ",\"FlowSetpoint\":" + String(flowmeterSetpoint, 4);
    jsonResponse += ",\"Valve1\":" + String(flowmeterValve1);
    jsonResponse += ",\"Valve2\":" + String(flowmeterValve2);
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
  if (distanceSensorReference <= 0.0f) { allStop(); return; }

  // Se os dados estiverem velhos, desliga tudo
  const bool dataStale = !distanceSensorCommOn ||
                         (now - distanceSensorLastUpdate > DISTANCE_TIMEOUT) ||
                         (distanceSensorValue < 0.0f);
  if (dataStale) { allStop(); return; }

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
    startMixer();
    resetNormalTimers();
    resetEmergencyTimers();
    normal_nextDoseCumMs = (unsigned long)(foamStartDelay_s * 1000.0f); // Arma timer X
    if (dosingOn) doseOff();
  }
  if (!currentFoamFlag && foam) { // Espuma acabou de sumir
    allStop();
  }
  foam = currentFoamFlag;

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
  if (xSemaphoreTake(sensorSerialMutex, pdMS_TO_TICKS(300)) != pdTRUE) {
    ESP32_ERRO("Timeout ao obter mutex da UART do Módulo TECNAL");
    return "";
  }

  String response;
  response.reserve(64);
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
  bool valid = isValidSensorReply(response);

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
      ESP32_AVISO("Limite de falhas UART atingido; reinicializando UART do Módulo TECNAL");
      resetSensorUartLocked();
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
String removeDecimal(const String &s) {
  String ret = s;
  ret.replace(",", "."); // [CORREÇÃO (Minor)] Normaliza vírgula decimal
  ret.replace(".", "");
  return ret;
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