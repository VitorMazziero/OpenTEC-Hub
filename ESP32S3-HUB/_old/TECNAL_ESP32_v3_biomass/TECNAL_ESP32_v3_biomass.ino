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
 * [CORREÇÃO DE BUG (Heater Reset Loop)]:
 * - A lógica de 'pHCalforUpdate' foi movida de 'readAndBroadcastSensorData'
 * para 'updateParametersCycle' para separar "leitura" de "escrita".
 * - 'processJsonCommand' agora só define 'pHCalforUpdate = true'
 * se o novo valor de 'pHCal' for diferente do valor atual,
 * evitando que o script Python cause resets de aquecedor.
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

// [CORREÇÃO] Removida variável global não utilizada
// static String cmdAccum;

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

// ============ GLOBAL VARIABLES ============
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
bool  pHCalforUpdate = false;
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
  // [CORREÇÃO (Issue 4)] Procura o ':' somente APÓS a chave inteira ("key":)
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
void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity);
void setAntifoam(int op, int mix, int intensity);
void setPressure(int ref);
String sendSensorCommand(const String &cmd, bool readResponse);
String removeDecimal(const String &s);
void updateParametersCycle();
void checkDistanceSensorReference();
void resetSensorSerial();
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
    Serial.println();
    Serial.print("WiFi SoftAP started. SSID: "); Serial.println(WIFI_SSID);
    Serial.print("AP IP address: "); Serial.println(WiFi.softAPIP());
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
        
        // We are getting data, so communication is on.
        // You could also have pumpCommOn set by the UI.
        if (!pumpCommOn) pumpCommOn = true; 

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

    server.on("/agitatorCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "application/json", takePending(pendingAgitatorCommand));
    });

    server.on("/pumpCommand", HTTP_GET, [](AsyncWebServerRequest *request) {
      request->send(200, "application/json", takePending(pendingPumpCommand));
    });

    server.begin();
  } else {
      Serial.println("Failed to start SoftAP!");
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
    Serial.printf("WDT initialization failed! Error: %s\n", esp_err_to_name(init_result));
  } else {
    Serial.println("WDT initialized successfully.");
  }
  esp_err_t add_result = esp_task_wdt_add(NULL);
  if (add_result != ESP_OK) {
    Serial.printf("Failed to add current task to WDT! Error: %s\n", esp_err_to_name(add_result));
  } else {
    Serial.println("Current task added to WDT monitoring.");
  }
}

// ------------------------------------------------------------------
// SETUP
// ------------------------------------------------------------------
void setup() {
  Serial.begin(115200);
  preferences.begin("SensorHub", false);
  loadSettings();
  debugSettings();
  startWatchDog();

  // [CORREÇÃO (Issue 2)] Cria mutexes ANTES de iniciar o servidor ou a serial
  sensorSerialMutex = xSemaphoreCreateMutex();
  cmdMutex = xSemaphoreCreateMutex();
  if (sensorSerialMutex == NULL || cmdMutex == NULL) {
    Serial.println("ERRO FATAL: Nao foi possivel criar os mutexes!");
    ESP.restart();
  }
  
  sensorSerial.begin(9600, SERIAL_8N1, SENSOR_RX_PIN, SENSOR_TX_PIN);
  
  // Inicia o WiFi (e o server.begin()) DEPOIS que os mutexes existem
  startWiFi();
  
  // Sincroniza o hardware DEPOIS que os mutexes foram criados
  syncAllSensorSettings();
}

// ------------------------------------------------------------------
// LOOP
// ------------------------------------------------------------------
void loop() {
  // Processa comandos da USB (Debug)
  handleUSBCommands();

  unsigned long now = millis();
  
  // Tarefa de Leitura Periódica
  if (!bypassMode && (now - lastDataMillis) >= dataDelay) {
    lastDataMillis = now;
    if (distanceSensorCommOn){
      checkDistanceSensorReference(); // Controla a espuma
    }
    readAndBroadcastSensorData(); // Lê os sensores
  }

  // Tarefa de Atualização Cíclica (parâmetros 'fire-and-forget')
  static unsigned long lastCyclicUpdate = 0;
  if (!bypassMode && (now - lastCyclicUpdate >= 2000)) {
    lastCyclicUpdate = now;
    vTaskDelay(pdMS_TO_TICKS(5)); 
    updateParametersCycle();
  }

  // Alimenta o watchdog
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
      Serial.println(response); // Ecoa a resposta no USB
      return response;
  }
}


// ------------------------------------------------------------------
// processJsonCommand():
// ------------------------------------------------------------------
void processJsonCommand(const String &json) {
  String value;

  if (json.indexOf("\"resetVariables\"") != -1) {
    tempReference       = 0.0f;
    pHReference         = 0.0f;
    pHError             = 0.17f;
    pHCal               = 5;
    pHOperation         = 5;
    pHMix               = 10;
    pHIntensity         = 990;
    motorRPM            = 0;
    nutriOperation      = 999;
    nutriMix            = 1;
    nutriOpCycle        = 500;
    nutriMixCycle       = 1;
    nutriIntensity      = 99;
    antifoamOperation   = 999;
    antifoamMix         = 1;
    antifoamIntensity   = 99;
    pressureReference   = 100;
    distanceSensorReference = 0.0f;
    dataDelay           = 1000;
    oxyOn               = false;
    tempOn              = false;
    phOn                = false;
    nutrientOn          = false;
    antifoamOn          = false;
    pressureOn          = false;
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
  if (json.indexOf("\"flowmeterComm\"") != -1) {
    value = getValueFromJson(json, "flowmeterComm");
    flowmeterCommOn = (value.toInt() != 0);
  }
  if (json.indexOf("\"biomassComm\"") != -1) {
    value = getValueFromJson(json, "biomassComm");
    biomassCommOn = (value.toInt() != 0);
  }
  if (json.indexOf("\"distanceSensorComm\"") != -1) {
    value = getValueFromJson(json, "distanceSensorComm");
    distanceSensorCommOn = (value.toInt() != 0);
  }
  if (json.indexOf("\"distanceSensorReference\"") != -1) {
    value = getValueFromJson(json, "distanceSensorReference");
    distanceSensorReference = value.toFloat();
  }
  if (json.indexOf("\"dataDelay\"") != -1) {
    value = getValueFromJson(json, "dataDelay");
    int newDelay = value.toInt();
    dataDelay = (newDelay < 100) ? 100 : newDelay;
  }
  if (json.indexOf("\"pumpComm\"") != -1) { 
    value = getValueFromJson(json, "pumpComm");
    pumpCommOn = (value.toInt() != 0);
  }
  if (json.indexOf("\"motorSetpoint\"") != -1) {
    value = getValueFromJson(json, "motorSetpoint");
    setMotor(value.toInt());
  }
  if (json.indexOf("\"tempSetpoint\"") != -1) {
    value = getValueFromJson(json, "tempSetpoint");
    setTemperature(value.toFloat());
  }
  if (json.indexOf("\"oxygenMonitor\"") != -1) {
    value = getValueFromJson(json, "oxygenMonitor");
    oxyOn = (value.toInt() != 0);
  }
  
  bool pHUpdated = false;
  if (json.indexOf("\"pHSetpoint\"") != -1) {
    value = getValueFromJson(json, "pHSetpoint"); pHReference = value.toFloat(); pHUpdated = true;
  }
  
  // [CORREÇÃO DE BUG] Só define pHCalforUpdate = true se o valor MUDAR.
  if (json.indexOf("\"pHCal\"") != -1) {
    value = getValueFromJson(json, "pHCal"); 
    float pHFloat = value.toFloat();
    int newpHCal = (int)round(pHFloat * 100);
    if (newpHCal != pHCal) { // Evita re-envio desnecessário
       pHCal = newpHCal;
       pHCalforUpdate = true;
    }
  }

  if (json.indexOf("\"pHError\"") != -1) {
    value = getValueFromJson(json, "pHError"); pHError = value.toFloat(); pHUpdated = true;
  }
  if (json.indexOf("\"pHOperation\"") != -1) {
    value = getValueFromJson(json, "pHOperation"); pHOperation = value.toInt(); pHUpdated = true;
  }
  if (json.indexOf("\"pHMix\"") != -1) {
    value = getValueFromJson(json, "pHMix"); pHMix = value.toInt(); pHUpdated = true;
  }
  if (json.indexOf("\"pHIntensity\"") != -1) {
    value = getValueFromJson(json, "pHIntensity"); pHIntensity = value.toInt(); pHUpdated = true;
  }
  if (pHUpdated) {
    setPH(pHReference, pHError, pHOperation, pHMix, pHIntensity);
  }

  bool nutriUpdated = false;
  if (json.indexOf("\"nutriOperation\"") != -1) {
    value = getValueFromJson(json, "nutriOperation"); nutriOperation = value.toInt(); nutriUpdated = true;
  }
  if (json.indexOf("\"nutriMix\"") != -1) {
    value = getValueFromJson(json, "nutriMix"); nutriMix = value.toInt(); nutriUpdated = true;
  }
  if (json.indexOf("\"nutriOpCycle\"") != -1) {
    value = getValueFromJson(json, "nutriOpCycle"); nutriOpCycle = value.toInt(); nutriUpdated = true;
  }
  if (json.indexOf("\"nutriMixCycle\"") != -1) {
    value = getValueFromJson(json, "nutriMixCycle"); nutriMixCycle = value.toInt(); nutriUpdated = true;
  }
  if (json.indexOf("\"nutriIntensity\"") != -1) {
    value = getValueFromJson(json, "nutriIntensity"); nutriIntensity = value.toInt(); nutriUpdated = true;
  }
  if (nutriUpdated) {
    setNutrient(nutriOperation, nutriMix, nutriOpCycle, nutriMixCycle, nutriIntensity);
  }
  bool antifoamUpdated = false;
  if (json.indexOf("\"antifoamOperation\"") != -1) {
    value = getValueFromJson(json, "antifoamOperation"); antifoamOperation = value.toInt(); antifoamUpdated = true;
  }
  if (json.indexOf("\"antifoamMix\"") != -1) {
    value = getValueFromJson(json, "antifoamMix"); antifoamMix = value.toInt(); antifoamUpdated = true;
  }
  if (json.indexOf("\"antifoamIntensity\"") != -1) {
    value = getValueFromJson(json, "antifoamIntensity"); antifoamIntensity = value.toInt(); antifoamUpdated = true;
  }
  if (antifoamUpdated) {
    setAntifoam(antifoamOperation, antifoamMix, antifoamIntensity);
  }
  if (json.indexOf("\"pressureReference\"") != -1) {
    value = getValueFromJson(json, "pressureReference");
    pressureReference = value.toInt();
    setPressure(pressureReference);
  }

  // Constrói o JSON do Fluxômetro e o coloca na fila (thread-safe)
  String flowmeterCommand = "";
  bool flowmeterCmdFound = false;
  if (json.indexOf("\"flowSetpoint\"") != -1) {
    flowmeterCommand += "\"flow_setpoint\":" + getValueFromJson(json, "flowSetpoint"); flowmeterCmdFound = true;
  }
  
  // --- LÓGICA MESCLADA (da versão antiga) ---
  if (json.indexOf("\"maxFlow\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"max_flow\":" + getValueFromJson(json, "maxFlow");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"k1\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"k1\":" + getValueFromJson(json, "k1");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"f1\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"f1\":" + getValueFromJson(json, "f1");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"c1\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"c1\":" + getValueFromJson(json, "c1");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"k2\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"k2\":" + getValueFromJson(json, "k2");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"f2\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"f2\":" + getValueFromJson(json, "f2");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"c2\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"c2\":" + getValueFromJson(json, "c2");
    flowmeterCmdFound = true;
  }
  if (json.indexOf("\"valve_1\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"v1_aux\":" + getValueFromJson(json, "valve_1");
    flowmeterCmdFound = true;
  }
  // --- FIM DA LÓGICA MESCLADA ---

  if (json.indexOf("\"valve_2\"") != -1) {
    if (flowmeterCmdFound) flowmeterCommand += ",";
    flowmeterCommand += "\"v2_opt\":" + getValueFromJson(json, "valve_2");
    flowmeterCmdFound = true;
  }
  if (flowmeterCmdFound && flowmeterCommOn) {
    setPending(pendingFlowmeterCommand, "{" + flowmeterCommand + "}");
  }

  // Constrói o JSON da Biomassa e o coloca na fila (thread-safe)
  String biomassCommand = "";
  bool biomassCmdFound = false;
  String startVal = getValueFromJson(json, "start");
  if (startVal.length() > 0) {
      if (biomassCmdFound) biomassCommand += ",";
      biomassCommand += "\"start\":" + startVal; biomassCmdFound = true;
  }
  String stopVal = getValueFromJson(json, "stop");
  if (stopVal.length() > 0) {
      if (biomassCmdFound) biomassCommand += ",";
      biomassCommand += "\"stop\":" + stopVal;
      biomassCmdFound = true;
  }
  String blankVal = getValueFromJson(json, "blank");
  if (blankVal.length() > 0) {
      if (biomassCmdFound) biomassCommand += ",";
      biomassCommand += "\"blank\":" + blankVal;
      biomassCmdFound = true;
  }
  String lowVal = getValueFromJson(json, "low");
  if (lowVal.length() > 0) {
      if (biomassCmdFound) biomassCommand += ",";
      biomassCommand += "\"low\":" + lowVal;
      biomassCmdFound = true;
  }
  String highVal = getValueFromJson(json, "high");
  if (highVal.length() > 0) {
      if (biomassCmdFound) biomassCommand += ",";
      biomassCommand += "\"high\":" + highVal;
      biomassCmdFound = true;
  }
  String optVal = getValueFromJson(json, "opt");
  if (optVal.length() > 0) {
      if (biomassCmdFound) biomassCommand += ",";
      biomassCommand += "\"opt\":" + optVal;
      biomassCmdFound = true;
  }
  String testVal = getValueFromJson(json, "test_period");
  if (testVal.length() > 0) {
      if (biomassCmdFound) biomassCommand += ",";
      biomassCommand += "\"test_period\":" + testVal; biomassCmdFound = true;
  }
  if (biomassCmdFound && biomassCommOn) {
      setPending(pendingBiomassCommand, "{" + biomassCommand + "}");
  }

  // Configuração do Agitador e Antiespumante
  if (json.indexOf("\"agitatorAuto\"") != -1) {
    String v = getValueFromJson(json, "agitatorAuto"); agitatorAuto = (v.toInt() != 0);
  }
  
  // --- LÓGICA MESCLADA (da versão antiga) ---
  if (json.indexOf("\"agitatorReEnablePot\"") != -1) {
    String v = getValueFromJson(json, "agitatorReEnablePot");
    agitatorReEnablePot = (v.toInt() != 0);
  }
  if (json.indexOf("\"agitatorPercent\"") != -1) {
    String v = getValueFromJson(json, "agitatorPercent");
    int p = v.toInt();
    if (p < 0) p = 0; if (p > 100) p = 100;
    agitatorPercentFoam = p;
  }
  if (json.indexOf("\"agitatorDir\"") != -1) {
    String v = getValueFromJson(json, "agitatorDir");
    agitatorDirFoam = (v.toInt() != 0) ? 1 : 0;
  }
  // Manual agitator ON/OFF using current config (percent/dir/repot)
  if (json.indexOf("\"agitatorOn\"") != -1) {
    String v = getValueFromJson(json, "agitatorOn");
    bool on = (v.toInt() != 0);
    if (on) {
      // run with current percent & direction; disable local pot while ON
      queueAgitatorCmd((float)agitatorPercentFoam, agitatorDirFoam, 0);
    } else {
      // stop; optionally re-enable local pot
      queueAgitatorCmd(0.0f, agitatorDirFoam, agitatorReEnablePot ? 1 : 0);
    }
  }
  if (json.indexOf("\"foamStartDelay_s\"") != -1) {
    foamStartDelay_s = getValueFromJson(json, "foamStartDelay_s").toFloat();
    if (foamStartDelay_s < 0.0f) foamStartDelay_s = 0.0f;
  }
  if (json.indexOf("\"foamPulse_s\"") != -1) {
    foamPulse_s = getValueFromJson(json, "foamPulse_s").toFloat();
    if (foamPulse_s < 0.05f) foamPulse_s = 0.05f;
  }

  if (json.indexOf("\"foamInterval_s\"") != -1) {
    foamInterval_s = getValueFromJson(json, "foamInterval_s").toFloat();
    if (foamInterval_s < 0.05f) foamInterval_s = 0.05f;
  }

  String pumpCommand = "";
  bool pumpCmdFound = false;

  // Lista de chaves simples (texto ou número)
  const char* simpleKeys[] = {
    "pump_command", "pump_mode", "pump_speed", "pump_init_t", "pump_final_t",
    "lambda_const", "lambda_linear", "phi_linear", "lambda_exp", "phi_exp"
  };
  // Lista de chaves do polinômio (p0 a p20)
  const char* polyKeys[] = {
    "p0", "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8", "p9", "p10",
    "p11", "p12", "p13", "p14", "p15", "p16", "p17", "p18", "p19", "p20"
  };

  // Processa chaves simples
  for (const char* key : simpleKeys) {
    String val = getValueFromJson(json, key);
    if (val.length() > 0) {
      if (pumpCmdFound) pumpCommand += ",";
      // Remove o prefixo "pump_" da chave
      String cleanKey = String(key);
      if (cleanKey.startsWith("pump_")) {
        cleanKey = cleanKey.substring(5); 
      }
      // Adiciona aspas se for um comando de texto
      if (cleanKey == "command") {
        pumpCommand += "\"" + cleanKey + "\":\"" + val + "\"";
      } else {
        pumpCommand += "\"" + cleanKey + "\":" + val;
      }
      pumpCmdFound = true;
    }
  }

  // Processa chaves do polinômio (p0-p20)
  for (const char* key : polyKeys) {
    String val = getValueFromJson(json, key);
    if (val.length() > 0) {
      if (pumpCmdFound) pumpCommand += ",";
      pumpCommand += "\"" + String(key) + "\":" + val;
      pumpCmdFound = true;
    }
  }

  // Se algum comando foi encontrado, enfileira (thread-safe)
  if (pumpCmdFound && pumpCommOn) {
    setPending(pendingPumpCommand, "{" + pumpCommand + "}");
  }

  // Salva todas as configurações alteradas
  saveSettings();
}

// ------------------------------------------------------------------
// readAndBroadcastSensorData():
// ------------------------------------------------------------------
void readAndBroadcastSensorData() {
  vTaskDelay(pdMS_TO_TICKS(10)); 
  float temperatureVal = -1.0;
  if (tempOn) {
    String temperatureResp = sendSensorCommand("b", true);
    if (temperatureResp.length() > 0) {
      temperatureVal = temperatureResp.toFloat();
      if (temperatureVal < 0.0f || temperatureVal > 100.0f) {
        temperatureVal = -1.0f;
      }
    }
  }
  vTaskDelay(pdMS_TO_TICKS(10));
  float pHVal = -1.0;
  if (phOn) {
    String pHResp = sendSensorCommand("k", true);
    if (pHResp.length() > 0) {
      pHVal = pHResp.toFloat();
    }
  }
  vTaskDelay(pdMS_TO_TICKS(10));
  float oxyVal = -1.0;
  if (oxyOn) {
    String oxyResp = sendSensorCommand("g", true);
    if (oxyResp.length() > 0) {
      oxyVal = oxyResp.toFloat();
      if (oxyVal < 0.0f || oxyVal > 4095.0f) {
        oxyVal = -1.0f;
      }
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

  // [CORREÇÃO DE BUG] Bloco 'pHCalforUpdate' MOVIDO daqui
  // para 'updateParametersCycle()'

  float timeSec = millis() / 1000.0;

  bool validDistance = false;
  if (distanceSensorCommOn) {
    unsigned long age = millis() - distanceSensorLastUpdate;
    if (age <= DISTANCE_TIMEOUT && distanceSensorValue >= 0.0f) {
      validDistance = true;
    } else {
      distanceSensorValue = -1.0f;
    }
  }

  bool validBiomass = false;
  if (biomassCommOn && biomassLastUpdate > 0) {     
      unsigned long age = millis() - biomassLastUpdate;
      if (age <= BIOMASS_TIMEOUT) {
          validBiomass = true;
      }
  }

  // Constrói o JSON de status
  String jsonResponse = "{";
  jsonResponse += "\"Time\":" + String(timeSec, 1);
  jsonResponse += ",\"Tempval\":" + String(temperatureVal, 2);
  jsonResponse += ",\"pHval\":" + String(pHVal, 2);
  jsonResponse += ",\"Oxyval\":" + String(oxyVal, 1);
  jsonResponse += ",\"Antifoam\":" + String(antifoamVal, 0);
  jsonResponse += ",\"Pressure\":" + String(pressureVal, 1);
  if (flowmeterCommOn) {
    jsonResponse += ",\"FlowVoltage\":" + String(flowmeterVoltage, 6);
    jsonResponse += ",\"FlowRate\":" + String(flowmeterRate, 6);
    jsonResponse += ",\"FlowSetpoint\":" + String(flowmeterSetpoint, 6);
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
  Serial.println(jsonResponse); // Ecoa no USB para debug

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
// Funções 'set' (enviam comandos 'fire-and-forget' para a UART)
// ------------------------------------------------------------------

void setMotor(int rpm) {
  if (rpm < 0) { rpm = 0; }
  if (rpm > 1000) { rpm = 1000; }
  motorRPM = rpm;
  if (rpm == 0) {
    sendSensorCommand("0V", false);
    vTaskDelay(pdMS_TO_TICKS(20));
    sendSensorCommand("0A", false);
  } else {
    sendSensorCommand("1V", false);
    vTaskDelay(pdMS_TO_TICKS(20));
    sendSensorCommand(String(motorRPM) + "A", false);
  }
}

void setTemperature(float temp) {
  tempReference = temp;
  if (fabs(tempReference) < 0.001) {
    tempOn = false;
    sendSensorCommand("100B", false); // Envia comando de desligar
  } else {
    tempOn = true;
    String tStr = String(tempReference, 1);
    String noDot = removeDecimal(tStr);
    sendSensorCommand(noDot + "B", false); // Envia setpoint
  }
}

void setPH(float pH, float err, int op, int mix, int intensity) {
  if (pH < 0.0f || pH > 14.0f) { pH = 7.00f; }
  if (err <= 0.0f || err >= 2.0f) { err = 0.17f; }
  if (op < 1 || op >= 1000) { op = 5; }
  if (mix < 1 || mix >= 1000) { mix = 5; }
  if (intensity < 0 || intensity >= 990) { intensity = 990; }
  pHReference = pH;
  pHError = err;
  pHOperation = op;
  pHMix = mix;
  pHIntensity = intensity;
  if (fabs(pHReference) < 0.001) {
    phOn = false;
    sendSensorCommand("0F", false);
  } else {
    phOn = true;
    String phStr = String(pHReference, 2);
    String noDot = removeDecimal(phStr);
    sendSensorCommand(noDot + "D", false);
    vTaskDelay(pdMS_TO_TICKS(20));
    String cmdErr = removeDecimal(String(pHError, 2)) + "E";
    sendSensorCommand(cmdErr, false);
    vTaskDelay(pdMS_TO_TICKS(20));
    String cmdOp = String(pHOperation) + "G";
    sendSensorCommand(cmdOp, false);
    vTaskDelay(pdMS_TO_TICKS(20));
    String cmdMix = String(pHMix) + "H";
    sendSensorCommand(cmdMix, false);
    vTaskDelay(pdMS_TO_TICKS(20));
    String cmdInt = String(pHIntensity) + "F";
    sendSensorCommand(cmdInt, false);
  }
}

void setNutrient(int op, int mix, int opCycle, int mixCycle, int intensity) {
  if (op < 1 || op >= 1000)      op = nutriOperation;
  if (mix < 1 || mix >= 1000)    mix = nutriMix;
  if (opCycle < 1 || opCycle > 500) opCycle = nutriOpCycle;
  if (mixCycle < 1 || mixCycle >= 1000) mixCycle = nutriMixCycle;
  if (intensity < 0) intensity = 0;
  if (intensity > 99) intensity = 99;
  nutriOperation = op;
  nutriMix = mix;
  nutriOpCycle = opCycle;
  nutriMixCycle = mixCycle;
  nutriIntensity = intensity;
  if (intensity == 0) {
    nutrientOn = false;
    sendSensorCommand("0M", false);
    return;
  }
  nutrientOn = true;
  sendSensorCommand(String(nutriIntensity) + "0M", false);
  vTaskDelay(pdMS_TO_TICKS(20));
  sendSensorCommand(String(nutriOperation) + "N", false);
  vTaskDelay(pdMS_TO_TICKS(20));
  sendSensorCommand(String(nutriMix) + "O", false);
  vTaskDelay(pdMS_TO_TICKS(20));
  sendSensorCommand(String(nutriOpCycle) + "P", false);
  vTaskDelay(pdMS_TO_TICKS(20));
  sendSensorCommand(String(nutriMixCycle) + "Q", false);
}


inline void setNutrientIntensityOnly(int intensity) {
  if (intensity < 0) intensity = 0;
  if (intensity > 99) intensity = 99;
  nutriIntensity = intensity;
  nutrientOn = (intensity != 0);
  if (intensity == 0) {
    sendSensorCommand("0M", false);
  } else {
    sendSensorCommand(String(intensity) + "0M", false);
  }
}

void setAntifoam(int op, int mix, int intensity) {
  if (op < 1 || op >= 1000) { op = 999; }
  if (mix < 1 || mix >= 1000) { mix = 1; }
  if (intensity < 0 || intensity >= 99) { intensity = 99; }
  antifoamOperation = op;
  antifoamMix = mix;
  antifoamIntensity = intensity;
  if (antifoamIntensity == 0) {
    antifoamOn = false;
    sendSensorCommand("0I", false);
  } else {
    antifoamOn = true;
    String cmdIntensity = String(antifoamIntensity) + "0I";
    sendSensorCommand(cmdIntensity, false);
  }
  vTaskDelay(pdMS_TO_TICKS(20));
  sendSensorCommand(String(antifoamOperation) + "J", false);
  vTaskDelay(pdMS_TO_TICKS(20));
  sendSensorCommand(String(antifoamMix) + "L", false);
}

void setPressure(int ref) {
  if (ref < 0 || ref > 380) { ref = 100; }
  pressureReference = ref;
  String cmdPressure = String(pressureReference) + "C";
  if (pressureReference == 0) {
    pressureOn = false;
    sendSensorCommand("0C", false);
  } else {
    pressureOn = true;
    sendSensorCommand(cmdPressure, false);
  }
}

unsigned long lastResetTime = 0;
const unsigned long RESET_INTERVAL = 10000; // 10 segundos

// ------------------------------------------------------------------
// sendSensorCommand():
//   Função 'core' de I/O da UART. Protegida por Mutex.
// ------------------------------------------------------------------
String sendSensorCommand(const String &cmd, bool readResponse) {
  
  // Obtém o mutex da UART, bloqueando se estiver em uso
  if (xSemaphoreTake(sensorSerialMutex, portMAX_DELAY) == pdTRUE) {
    
    sensorSerial.print(cmd);
    
    // Garante que o buffer de TX foi enviado (com guarda de tempo)
    uint32_t t_f = millis();
    sensorSerial.flush(); // Aguarda o buffer TX esvaziar
    if (millis() - t_f > 50) {
      // Opcional: Log de TX lento
    }
    
    vTaskDelay(pdMS_TO_TICKS(5)); // Pequeno delay para processamento do sensor

    String response = "";

    if (readResponse) {
      
      // Lógica de leitura não bloqueante (sem goto)
      // [CORREÇÃO (Issue 3)] Timeout aumentado para 450ms
      const uint32_t tout = 450;
      const uint32_t t_start = millis();
      bool line_complete = false;

      while ((millis() - t_start) < tout && !line_complete) {
        while (sensorSerial.available() && !line_complete) {
          char c = sensorSerial.read();
          if (c == '\r' || c == '\n') {
            if (response.length() > 0) line_complete = true; // Ignora CR/LF vazios
          } else {
            response += c;
            if (response.length() >= MAX_SENSOR_BUFFER_LENGTH - 1) line_complete = true; // Proteção de buffer
          }
        }
        if (!line_complete) vTaskDelay(pdMS_TO_TICKS(2)); // Cede tempo
      }
      response.trim();

      // Validação robusta da resposta
      bool valid = isValidSensorReply(response);

      if (valid) {
        uartSensorOK = true;
        uartFailureCount = 0;

        // Caso especial: calibração de oxigênio
        if (cmd == "g") {
          float rawValue = response.toFloat();
          float calibrated = a * rawValue + b;
          xSemaphoreGive(sensorSerialMutex);
          return bypassMode ? String(calibrated, 1) : String(rawValue, 4);
        }

        xSemaphoreGive(sensorSerialMutex);
        return response;
      } else {
        // Falha na leitura (timeout ou resposta inválida)
        uartFailureCount++;
        if (uartFailureCount >= UART_FAILURE_THRESHOLD) {
          unsigned long now = millis();
          if (now - lastResetTime >= RESET_INTERVAL) {
            uartSensorOK = false;
            lastResetTime = now; // Marca o tempo ANTES do reset
            resetSensorSerial(); // Chamada segura (não reentrante)
            uartFailureCount = 0;
          }
        }
        xSemaphoreGive(sensorSerialMutex);
        return "";
      }
    } else {
      // (readResponse == false: "fire-and-forget")
      // Libera o mutex primeiro, DEPOIS espera.
      xSemaphoreGive(sensorSerialMutex);
      vTaskDelay(pdMS_TO_TICKS(10)); 
      return "";
    }

  } else {
    // Falha catastrófica (nunca deve acontecer com portMAX_DELAY)
    Serial.println("ERRO FATAL: Nao foi possivel obter o mutex da serial!");
    return "";
  }
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

// ------------------------------------------------------------------
// updateParametersCycle():
//   Envia periodicamente os parâmetros 'fire-and-forget'.
// [CORREÇÃO (Issue 3)] Removido std::vector para evitar fragmentação de heap.
//   Usa um 'switch' estático para iterar pelos comandos.
// ------------------------------------------------------------------
void updateParametersCycle() {
  if (bypassMode) return;
  
  // "Back-off": Não compete com a tarefa de leitura se ela acabou de rodar
  if (millis() - lastReadBroadcastTime < 80) {
    return;
  }

  // [CORREÇÃO DE BUG] Lida com 'set' de calibração one-shot AQUI,
  // fora do loop de leitura de dados.
  if (pHCalforUpdate) {
    sendSensorCommand(String(pHCal) + "W", false);
    pHCalforUpdate = false; // Flag resetada
    vTaskDelay(pdMS_TO_TICKS(20)); // Dê um tempo após a calibração
  }

  // Itera pelo carrossel de comandos (17 slots) para encontrar
  // o PRÓXIMO comando ativo para enviar.
  static size_t updateCmdIndex = 0;
  const int MAX_CMDS = 17;

  for (int i = 0; i < MAX_CMDS; ++i) {
    String cmd = "";
    // Inicia a busca do índice atual e avança
    size_t currentIndex = (updateCmdIndex + i) % MAX_CMDS; 

    switch (currentIndex) {
      case 0:  if (motorRPM > 0) cmd = "1V"; break;
      case 1:  if (motorRPM > 0) cmd = String(motorRPM) + "A"; break;
      case 2:  if (tempOn) cmd = removeDecimal(String(tempReference, 1)) + "B"; break;
      case 3:  if (phOn) cmd = removeDecimal(String(pHReference, 2)) + "D"; break;
      case 4:  if (phOn) cmd = removeDecimal(String(pHError, 2)) + "E"; break;
      case 5:  if (phOn) cmd = String(pHOperation) + "G"; break;
      case 6:  if (phOn) cmd = String(pHMix) + "H"; break;
      case 7:  if (phOn) cmd = String(pHIntensity) + "F"; break;
      case 8:  if (nutrientOn) cmd = String(nutriIntensity) + "0M"; break;
      case 9:  if (nutrientOn) cmd = String(nutriOperation) + "N"; break;
      case 10: if (nutrientOn) cmd = String(nutriMix) + "O"; break;
      case 11: if (nutrientOn) cmd = String(nutriOpCycle) + "P"; break;
      case 12: if (nutrientOn) cmd = String(nutriMixCycle) + "Q"; break;
      case 13: if (antifoamOn) cmd = String(antifoamIntensity) + "0I"; break;
      case 14: if (antifoamOn) cmd = String(antifoamOperation) + "J"; break;
      case 15: if (antifoamOn) cmd = String(antifoamMix) + "L"; break;
      case 16: if (pressureOn) cmd = String(pressureReference) + "C"; break;
    }

    if (cmd.length() > 0) {
      sendSensorCommand(cmd, false);
      // Salva a próxima posição para a *próxima* chamada desta função
      updateCmdIndex = (currentIndex + 1) % MAX_CMDS; 
      break; // Sai do 'for' loop; (apenas um comando por ciclo)
    }
  }
  
  // Delay que existia na versão original (além do delay em sendSensorCommand)
  vTaskDelay(pdMS_TO_TICKS(20));
}

// ------------------------------------------------------------------
// resetSensorSerial():
//   Reinicia o hardware da UART (assume que o mutex JÁ FOI OBTIDO)
// ------------------------------------------------------------------
void resetSensorSerial() {
  sensorSerial.end();
  Serial.println("UART failure detected. Reinitializing sensor UART.");
  vTaskDelay(pdMS_TO_TICKS(500));
  sensorSerial.begin(9600, SERIAL_8N1, SENSOR_RX_PIN, SENSOR_TX_PIN);
  vTaskDelay(pdMS_TO_TICKS(500));
}

// ------------------------------------------------------------------
// syncAllSensorSettings():
//   Força o reenvio de todas as configurações para o hardware.
// ------------------------------------------------------------------
void syncAllSensorSettings() {
  Serial.println("Sincronizando configurações com o hardware do sensor (Serial)...");
  vTaskDelay(pdMS_TO_TICKS(100));
  
  // Estas funções 'set' são 'thread-safe' (usam sendSensorCommand)
  // e têm delays internos para não sobrecarregar o sensor.
  setMotor(motorRPM);
  setTemperature(tempReference);
  setPH(pHReference, pHError, pHOperation, pHMix, pHIntensity);
  setNutrient(nutriOperation, nutriMix, nutriOpCycle, nutriMixCycle, nutriIntensity);
  setAntifoam(antifoamOperation, antifoamMix, antifoamIntensity);
  setPressure(pressureReference);
  
  Serial.println("Sincronização Serial concluída.");

  if (biomassCommOn) {
    Serial.println("Re-enfileirando comando 'start' para o Sensor de Biomassa (WiFi)...");
    setPending(pendingBiomassCommand, "{\"start\":1}");
  }
}