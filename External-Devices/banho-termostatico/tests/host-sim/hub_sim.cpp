#include <cassert>
#include <vector>
#include "../../firmware/thermostatic-bath/src/network/HubLink.cpp"

bool g_serialVerbose = false;
SerialClass Serial;
WiFiClass WiFi;
static uint32_t nowMs = 10000, stopMs = 0;
static int helloCode = 200, pushCode = 200;
static unsigned networkCalls = 0;
static std::vector<char> requests;
struct EndRun {};
unsigned long millis() { return nowMs; }
void vTaskDelay(unsigned long ms) {
  nowMs += static_cast<uint32_t>(ms);
  if (nowMs >= stopMs) throw EndRun{};
}
void checkWifi(bool) { ++networkCalls; }
bool httpGet(const String& url, int& code, String& body, String* owner) {
  const bool hello = url.indexOf("nodeHello") >= 0;
  requests.push_back(hello ? 'H' : 'P');
  code = hello ? helloCode : pushCode;
  body = "{}";
  if (owner) *owner = "0";
  return code >= 200 && code < 300;
}
bool displayPvValid() { return false; }
bool displaySpValid() { return false; }
float displayPv() { return 0; }
float displaySp() { return 0; }
const char* setpointStateName() { return "idle"; }
const char* setpointPhaseName() { return "none"; }
const String& setpointLastError() { static String s; return s; }
const char* guardStateName() { return "off"; }
bool guardDeviation(float&) { return false; }
bool processCommand(const char*, String&, CommandSource) { return true; }
void runFor(uint32_t duration) {
  stopMs = nowMs + duration;
  try { hubTask(nullptr); } catch (const EndRun&) {}
}
int main() {
  g_cfg.hubEnabled = 1;
  hubLinkPublishSnapshot(nowMs);
  g_hubAnnounced = false;
  helloCode = 500;
  runFor(1900);
  assert(requests.size() == 1 && requests[0] == 'H' && g_hubFailStreak == 1);

  // No reboot do Hub: hello -> push recusado -> hello -> push recuperado.
  requests.clear();
  helloCode = 200;
  pushCode = 403;
  runFor(20);
  assert((requests == std::vector<char>{'H', 'P'}));
  assert(!g_hubAnnounced && g_hubFailStreak == 0);
  requests.clear();
  pushCode = 200;
  runFor(1020);
  assert((requests == std::vector<char>{'H', 'P'}));
  assert(g_hubAnnounced && g_hubFailStreak == 0);

  // Retry de hello limitado a 2 s dentro de uma execucao continua da tarefa.
  requests.clear();
  helloCode = -1;
  g_hubAnnounced = false;
  runFor(4100);
  assert((requests == std::vector<char>{'H', 'H', 'H'}));
  assert(g_hubFailStreak == 3);

  // A flag ao vivo bloqueia OTA mesmo se o snapshot ainda nao foi atualizado.
  requests.clear();
  g_otaInProgress = true;
  const unsigned calls = networkCalls;
  runFor(300);
  assert(requests.empty() && networkCalls == calls);
  g_otaInProgress = false;
  g_cfg.hubEnabled = 0;
  hubLinkPublishSnapshot(nowMs);
  runFor(100);
  assert(requests.empty() && networkCalls == calls + 1);
  std::puts("HUB PASSED: registration gate, 403 recovery, bounded hello retries, live OTA pause, disabled mode");
}
