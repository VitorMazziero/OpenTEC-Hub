/****************************************************************************************
 *  Frasco Agitador IBT-2 (BTS7960) script – Rev. H (Hub telemetry + command ack)
 *  ESP32 ▸ IBT-2 (BTS7960) 12 V DC-motor controller
 *  ─────────────────────────────────────────────────────────────────────────────────────
 *  NEW in Rev. H — this device stops being invisible to the Hub
 *    • Pushes telemetry to the Hub: GET /agitatorData every 1 s with the magnitude and
 *      direction actually being driven, whether the bench potentiometer is live, and
 *      WHICH SOURCE last moved the motor. Until now the node only ever pulled commands
 *      and answered its own local /read, so the Hub — and therefore the PC — knew
 *      literally nothing about it: no presence, no speed, no confirmation. The PC was
 *      commanding this motor blind.
 *    • The `src` field is the one that changes operation. The Hub turns an off command
 *      into ActivePot = agitatorReEnablePot, and the loop below re-reads the knob on its
 *      very next pass whenever that is set — so a stop issued with the knob at 60 %
 *      restarts the motor at 60 %. Reporting the source is what lets the PC say the knob
 *      is in charge instead of showing a setpoint this node is not holding.
 *    • Hub commands now carry cmd_id and are applied at most once. The Hub retains a
 *      command until this node echoes the id back on the telemetry push, replacing the
 *      consume-on-read mailbox where a dropped HTTP response lost the command silently.
 *
 *  Rev. G (Dual-Hub, auto-nearest)
 *    • Two SensorHub SoftAPs: "ModuloTECNAL_1" and "ModuloTECNAL_2" (pass = SSID)
 *    • Async Wi-Fi scan every 10 s (non-blocking) to find the nearest hub by RSSI
 *    • Connect to best hub when not connected; switch only if other is ≥10 dB stronger
 *    • Keeps all features: Soft-AP + local HTTP (/cmd, /read), USB JSON, pot override,
 *      pull-model hub GET /agitatorCommand + /agitatorHello, soft-start, telemetry.
 *
 *  Dependencies
 *    • Arduino-ESP32 core ≥ 3.0.0
 *    • ArduinoJson v6.x
 ****************************************************************************************/

#include <Arduino.h>
#include <WiFi.h>
#include <WebServer.h>
#include <ArduinoJson.h>
#include <HTTPClient.h>

/* ───── Wi-Fi Soft-AP (local/manual) ────────────────────────────────────── */
constexpr const char* AP_SSID = "MotorBoeco";
constexpr const char* AP_PASS = "MotorBoeco";

/* ───── Wi-Fi Stations (auto-join SensorHub: choose nearest) ────────────── */
constexpr const char* HUB_SSIDS[2] = {"ModuloTECNAL_1", "ModuloTECNAL_2"};
constexpr const char* HUB_PASS [2] = {"ModuloTECNAL_1", "ModuloTECNAL_2"};

/*  Both hubs use the SensorHub SoftAP IP from your hub firmware */
IPAddress HUB_IP(192, 168, 4, 1);

/* ───── Hardware mapping ─────────────────────────────────────────────────── */
constexpr int RPWM = 25;      // Right / forward PWM
constexpr int LPWM = 26;      // Left  / reverse PWM
constexpr int REN  = 27;      // Enable right half-bridge
constexpr int LEN  = 13;      // Enable left  half-bridge

constexpr int POT_PIN = 36;   // Potentiometer wiper  → ADC1_4
constexpr int RIS_PIN = 34;   // (telemetry only - unused here)
constexpr int LIS_PIN = 35;   // (telemetry only - unused here)

/* ───── PWM parameters ───────────────────────────────────────────────────── */
constexpr uint32_t PWM_FREQ = 15000;   // 15 kHz
constexpr uint8_t  PWM_RES  = 12;      // 12-bit

/* ───── Control constants ────────────────────────────────────────────────── */
constexpr float    PERCENT_EPS   = 2.0f;   // ±2 % pot dead-band
constexpr uint16_t BOOST_DUTY    = 4095;   // 100 % at 12-bits
constexpr uint16_t BOOST_MS      = 200;

/* ───── Scan/roaming constants ───────────────────────────────────────────── */
constexpr uint32_t SCAN_INTERVAL_MS = 10000;  // every 10 s
constexpr int      SWITCH_DELTA_DB  = 10;     // switch only if other hub ≥10 dB stronger
constexpr uint32_t SCAN_CHAN_MS     = 80;     // per-channel scan budget (asynchronous)

/* ───── Global state ─────────────────────────────────────────────────────── */
WebServer server(80);

enum class Source : uint8_t { POT, WIFI, USB, HUB };
volatile Source lastSource        = Source::POT;
volatile float  targetPercent     = 0.0f;      // 0…100
volatile bool   dirRight          = true;      // true = right / forward
volatile bool   potEnabled        = true;      // true = pot can override

String latestTelemetry;

/* Hub-polling state */
static bool hubAnnounced      = false;
static uint32_t tHubPollMs    = 0;
static uint32_t tTelMs        = 0;
static uint32_t tPwmMs        = 0;
static uint32_t tHubPushMs    = 0;

/* Hub command acknowledgement (Rev. H) */
// The Hub re-delivers a command until this id comes back on the telemetry push, so the
// same JSON arrives repeatedly by design. 0 means "nothing applied yet"; the Hub never
// issues id 0.
static uint32_t lastAppliedHubCmdId = 0;

/* Telemetry push period. Three pushes inside the Hub's 3 s presence window, and slow
   enough that two blocking HTTP round trips per second do not starve handleClient(). */
constexpr uint32_t HUB_PUSH_MS = 1000;

/* Roaming/scan state */
static uint32_t tLastScanKick = 0;
static int      lastSeenRSSI[2] = {-999, -999};  // dBm
static int      currentHubIdx    = -1;           // 0 or 1 when connected
static int      desiredHubIdx    = -1;           // best candidate from last scan

/* ───── LEDC helper (ESP32 core v3 convenience) ──────────────────────────── */
static bool attachPwmPin(int pin)
{
  if (!ledcAttach(pin, PWM_FREQ, PWM_RES)) {
    Serial.printf("LEDC attach failed on GPIO %d\n", pin);
    return false;
  }
  ledcWrite(pin, 0);
  return true;
}

/* ───── Motor drive ──────────────────────────────────────────────────────── */
void applyDuty(uint16_t duty12)
{
  digitalWrite(REN, HIGH);
  digitalWrite(LEN, HIGH);

  if (dirRight) {
    ledcWrite(RPWM, duty12);
    ledcWrite(LPWM, 0);
  } else {
    ledcWrite(RPWM, 0);
    ledcWrite(LPWM, duty12);
  }
}

void brakeMotor()
{
  ledcWrite(RPWM, 0);
  ledcWrite(LPWM, 0);
}

/* ───── Pot helpers ──────────────────────────────────────────────────────── */
static uint16_t readPotRaw()               { return analogRead(POT_PIN); }
static float    rawToPercent(uint16_t r)   { return r * 100.0f / 4095.0f; }

/* ───── Hub command id ───────────────────────────────────────────────────── */
// Read with plain string operations rather than a second ArduinoJson document: the id
// has to be inspected before deciding whether to apply the payload, and parsing the
// whole thing twice to answer one integer is not worth the stack on this part.
static uint32_t extractCmdId(const String& s)
{
  int k = s.indexOf("\"cmd_id\"");
  if (k < 0) return 0;
  int c = s.indexOf(':', k);
  if (c < 0) return 0;
  return (uint32_t)strtoul(s.c_str() + c + 1, nullptr, 10);
}

/* ───── JSON command handler (Wi-Fi/USB/Hub) ─────────────────────────────── */
static const char* srcName(Source s)
{
  switch (s) {
    case Source::WIFI: return "Wi-Fi";
    case Source::USB:  return "USB";
    case Source::HUB:  return "Hub";
    default:           return "Pot";
  }
}

static bool parseAndApplyJson(const String& s, Source src)
{
  StaticJsonDocument<192> doc;
  if (deserializeJson(doc, s)) return false;

  bool valid = false;

  if (doc.containsKey("RPM_percent")) {
    float pct = doc["RPM_percent"];
    if (pct >= 0.0f && pct <= 100.0f) {
      targetPercent = pct;
      valid = true;
    } else {
      Serial.printf("[%s] RPM_percent out of range\n", srcName(src));
    }
  }

  if (doc.containsKey("Dir")) {
    int d = doc["Dir"];
    if (d == 0 || d == 1) {
      dirRight = (d == 1);
      valid = true;
    } else {
      Serial.printf("[%s] Dir must be 0 or 1\n", srcName(src));
    }
  }

  if (doc.containsKey("ActivePot")) {
    int ap = doc["ActivePot"];
    if (ap == 0 || ap == 1) {
      potEnabled = (ap == 1);
      valid = true;
      Serial.printf("[%s] Potentiometer %s\n", srcName(src), potEnabled ? "enabled" : "disabled");
    } else {
      Serial.printf("[%s] ActivePot must be 0 or 1\n", srcName(src));
    }
  }

  if (valid) {
    lastSource = src;
    Serial.printf("[%s] Cmd: %.1f %%  Dir:%s  Pot:%s\n",
                  srcName(src), targetPercent, dirRight ? "Right" : "Left",
                  potEnabled ? "ON" : "OFF");
  }
  return valid;
}

/* ───── HTTP handlers ────────────────────────────────────────────────────── */
static void handleCmd()
{
  if (server.method() != HTTP_POST) { server.send(405); return; }

  if (!parseAndApplyJson(server.arg("plain"), Source::WIFI)) {
    server.send(422, "text/plain",
                "JSON: {\"RPM_percent\":0-100, \"Dir\":0|1, \"ActivePot\":0|1}");
    return;
  }
  server.send(200, "text/plain", "OK");
}

static void handleRead()
{
  StaticJsonDocument<64> doc;
  doc["time_s"] = millis() / 1000;
  doc["duty"]   = targetPercent;

  String payload;
  serializeJson(doc, payload);
  latestTelemetry = payload;

  server.send(200, "application/json", payload);
}

/* ───── USB-CDC command poller ───────────────────────────────────────────── */
static void pollSerialCmd()
{
  static String buf;
  while (Serial.available()) {
    char c = char(Serial.read());
    if (c == '\n' || c == '\r') {
      if (buf.length()) parseAndApplyJson(buf, Source::USB);
      buf.clear();
    } else if (buf.length() < 180) buf += c;
  }
}

/* ───── Duty helper with hysteresis/min drive ────────────────────────────── */
uint16_t getEffectiveDuty(float pct)
{
  constexpr float PWM_SCALE   = 40.95f;  // 100 % → 4095
  constexpr float OFF_THRESH  = 1.0f;    // % below which output goes to 0
  constexpr float ON_THRESH   = 1.5f;    // hysteresis restore
  constexpr float MIN_EFF_PCT = 10.0f;   // minimum effective duty %
  constexpr float MAX_EFF_PCT = 100.0f;

  static bool motorOff = false;

  if (pct < OFF_THRESH && !motorOff) { motorOff = true; return 0; }
  if (pct >= ON_THRESH && motorOff)  { motorOff = false; }

  if (motorOff) return 0;

  float effPct = MIN_EFF_PCT + pct * (MAX_EFF_PCT - MIN_EFF_PCT) / 100.0f;
  return uint16_t(effPct * PWM_SCALE);
}

/* ───── Hub connectivity (hello + polling) ───────────────────────────────── */
void hubHello()
{
  if (hubAnnounced) return;
  if (WiFi.status() != WL_CONNECTED) return;

  HTTPClient http;
  String url = String("http://") + HUB_IP.toString() + "/agitatorHello";
  http.begin(url);
  http.setTimeout(500);
  int rc = http.GET();
  if (rc > 0) {
    Serial.printf("Hub hello OK (%d)\n", rc);
    hubAnnounced = true;
  } else {
    Serial.printf("Hub hello failed (%d)\n", rc);
  }
  http.end();
}

void pollHub()
{
  if (millis() - tHubPollMs < 500) return; // 2 Hz
  tHubPollMs = millis();
  if (WiFi.status() != WL_CONNECTED) return;

  HTTPClient http;
  String url = String("http://") + HUB_IP.toString() + "/agitatorCommand";
  http.begin(url);
  http.setTimeout(700);
  int rc = http.GET();
  if (rc == 200) {
    String body = http.getString();
    body.trim();
    if (body.length() > 2 && body != "{}") {
      const uint32_t id = extractCmdId(body);

      if (id != 0 && id == lastAppliedHubCmdId) {
        // Already applied. The Hub keeps re-delivering until it sees the ack, so this
        // is the normal case for a second or two, not an error. Re-applying would be
        // wrong: it would fight a local command the operator issued in the meantime.
      } else {
        const bool applied = parseAndApplyJson(body, Source::HUB);
        if (id != 0) {
          // Acknowledged whether or not a key was recognised. A payload this node
          // cannot use will not become usable on a retry, and leaving it unacknowledged
          // would pin the Hub's mailbox on it forever.
          lastAppliedHubCmdId = id;
          if (applied) Serial.printf("[HubCmd] Applied cmd_id=%lu\n", (unsigned long)id);
          else         Serial.printf("[HubCmd] cmd_id=%lu had no usable key\n", (unsigned long)id);
        }
      }
    }
  }
  http.end();
}

/* ───── Telemetry push to the Hub ────────────────────────────────────────── */
// Everything here was already being computed for the serial log and the local /read;
// it just never left the node. `src` and `pot` are the fields that matter operationally:
// they are how the PC learns that the bench knob, not the app, is holding the motor.
void pushTelemetryToHub()
{
  if (WiFi.status() != WL_CONNECTED) return;

  HTTPClient http;
  String url = String("http://") + HUB_IP.toString() + "/agitatorData"
             + "?pct="        + String(targetPercent, 1)
             + "&dir="        + String(dirRight ? 1 : 0)
             + "&pot="        + String(potEnabled ? 1 : 0)
             + "&src="        + String(srcName(lastSource))
             + "&secs="       + String(millis() / 1000)
             // Sent on every push, not only after a command: the Hub clears its mailbox
             // on a matching id, and omitting it would strand a command whenever the one
             // push right after applying it happened to be lost.
             + "&ack_cmd_id=" + String((unsigned long)lastAppliedHubCmdId);

  http.begin(url);
  http.setTimeout(400);
  http.GET();
  http.end();
}

/* ───── Scan helpers (async) ─────────────────────────────────────────────── */
int hubIndexFromSSID(const String& s) {
  if (s == HUB_SSIDS[0]) return 0;
  if (s == HUB_SSIDS[1]) return 1;
  return -1;
}

void kickAsyncScanIfDue()
{
  int8_t state = WiFi.scanComplete();
  if (state == WIFI_SCAN_RUNNING) return;       // still scanning

  uint32_t now = millis();
  if (now - tLastScanKick < SCAN_INTERVAL_MS) return;

  // clean previous results and start a new async scan
  WiFi.scanDelete();
  // async, show_hidden=false, passive=false, per-channel ms budget
  WiFi.scanNetworks(/*async=*/true, /*show_hidden=*/false, /*passive=*/false, SCAN_CHAN_MS);
  tLastScanKick = now;
}

void handleScanResultAndMaybeRoam()
{
  int n = WiFi.scanComplete();
  if (n < 0) return;  // -1 running, -2 not started

  // Reset last seen
  lastSeenRSSI[0] = lastSeenRSSI[1] = -999;

  for (int i = 0; i < n; ++i) {
    String ssid = WiFi.SSID(i);
    int idx = hubIndexFromSSID(ssid);
    if (idx >= 0) {
      int rssi = WiFi.RSSI(i);
      lastSeenRSSI[idx] = rssi;
    }
  }
  WiFi.scanDelete();  // free results

  // Decide best candidate
  int bestIdx = -1;
  int bestRssi = -999;
  for (int i = 0; i < 2; ++i) {
    if (lastSeenRSSI[i] > bestRssi) {
      bestRssi = lastSeenRSSI[i];
      bestIdx  = i;
    }
  }
  desiredHubIdx = bestIdx;

  // If not connected, connect to best available immediately
  if (WiFi.status() != WL_CONNECTED) {
    if (desiredHubIdx >= 0) {
      if (currentHubIdx != desiredHubIdx) {
        Serial.printf("Connecting to nearest hub: %s (RSSI %d dBm)\n",
                      HUB_SSIDS[desiredHubIdx], bestRssi);
        WiFi.disconnect(true, false);  // stop any pending STA connect
        WiFi.begin(HUB_SSIDS[desiredHubIdx], HUB_PASS[desiredHubIdx]);
        currentHubIdx = desiredHubIdx; // expected target
        hubAnnounced = false;
      }
    } else {
      Serial.println("No SensorHub APs found.");
    }
    return;
  }

  // Already connected — consider switching only if the other is much stronger
  int connectedIdx = hubIndexFromSSID(WiFi.SSID());
  if (connectedIdx >= 0) currentHubIdx = connectedIdx;

  if (desiredHubIdx >= 0 && connectedIdx >= 0 && desiredHubIdx != connectedIdx) {
    int other = desiredHubIdx;
    int otherRssi = lastSeenRSSI[other];
    int curRssi   = WiFi.RSSI(); // RSSI of current link

    if (otherRssi != -999 && (otherRssi - curRssi) >= SWITCH_DELTA_DB) {
      Serial.printf("Roaming: switching %s (RSSI %d) → %s (RSSI %d)\n",
                    HUB_SSIDS[connectedIdx], curRssi, HUB_SSIDS[other], otherRssi);
      WiFi.disconnect(true, false);
      delay(50);
      WiFi.begin(HUB_SSIDS[other], HUB_PASS[other]);
      currentHubIdx = other;
      hubAnnounced = false;
    }
  }
}

/* ───── Setup ────────────────────────────────────────────────────────────── */
void setup()
{
  Serial.begin(115200);
  analogReadResolution(12);
  analogSetPinAttenuation(POT_PIN, ADC_11db);

  pinMode(REN, OUTPUT);
  pinMode(LEN, OUTPUT);
  digitalWrite(REN, HIGH);
  digitalWrite(LEN, HIGH);

  if (!attachPwmPin(RPWM)) while (true) delay(1000);
  if (!attachPwmPin(LPWM)) while (true) delay(1000);

  // Dual mode: Soft-AP (for manual control) + Station (to join nearest SensorHub)
  WiFi.mode(WIFI_AP_STA);
  WiFi.setSleep(false); // reduce STA latency
  if (WiFi.softAP(AP_SSID, AP_PASS)) {
    Serial.printf("AP  %s  IP: %s\n", AP_SSID, WiFi.softAPIP().toString().c_str());
  }

  // Start first async scan immediately, STA connect will follow from results
  tLastScanKick = 0;
  kickAsyncScanIfDue();

  // Local HTTP server
  server.on("/cmd",  HTTP_POST, handleCmd);
  server.on("/read", HTTP_GET,  handleRead);
  server.begin();

  // Soft start pulse
  applyDuty(BOOST_DUTY);
  delay(BOOST_MS);
  brakeMotor();
  Serial.println("Ready.");
}

/* ───── Main loop ────────────────────────────────────────────────────────── */
void loop()
{
  server.handleClient();
  pollSerialCmd();

  // Maintain station link: scan & roam logic
  kickAsyncScanIfDue();
  handleScanResultAndMaybeRoam();

  // Hub hello + command polling
  if (WiFi.status() == WL_CONNECTED) {
    hubHello();
    pollHub();
  } else {
    hubAnnounced = false;
  }

  /* Potentiometer with smoothing + deadband */
  static float    smoothedPotRaw = 0.0f;
  static uint16_t lastPotRaw     = 0;
  constexpr float ALPHA          = 0.5f;
  constexpr uint16_t CLIP_THRESH = 4080;

  if (potEnabled) {
    uint16_t sample = analogRead(POT_PIN);
    if (sample < CLIP_THRESH) {
      smoothedPotRaw = ALPHA * sample + (1.0f - ALPHA) * smoothedPotRaw;
    }
    uint16_t potRaw = uint16_t(smoothedPotRaw);
    if (abs(int(potRaw) - int(lastPotRaw)) > (4095 * PERCENT_EPS / 100.0f)) {
      lastPotRaw    = potRaw;
      targetPercent = rawToPercent(potRaw);
      lastSource    = Source::POT;
      Serial.printf("[Pot ] Cmd: %.1f %%\n", targetPercent);
    }
  }

  /* PWM update every 10 ms */
  if (millis() - tPwmMs >= 10) {
    uint16_t duty = getEffectiveDuty(targetPercent);
    applyDuty(duty);
    tPwmMs = millis();
  }

  /* Telemetry every 500 ms (USB print + cache for /read) */
  if (millis() - tTelMs >= 500) {
    StaticJsonDocument<64> doc;
    doc["time_s"] = millis() / 1000;
    doc["duty"]   = targetPercent;
    serializeJson(doc, Serial);
    Serial.println();

    String tmp;
    serializeJson(doc, tmp);
    latestTelemetry = tmp;
    tTelMs = millis();
  }

  /* Hub telemetry push every 1 s. Kept on its own timer rather than folded into the
     block above so the two blocking HTTP round trips - this and pollHub() - stay
     interleaved instead of landing back to back on the same pass. */
  if (millis() - tHubPushMs >= HUB_PUSH_MS) {
    tHubPushMs = millis();
    pushTelemetryToHub();
  }
}
