"""Execute the production HTTP tasks with a clock and HTTP fault injection.

Host simulation only: it does not qualify the ESP32 radio or the MFC hardware.
Requires the Visual Studio C++ toolchain on Windows.
"""
from pathlib import Path
import os
import re
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "firmware/flowmeter/src"

STUBS = r'''
#include <algorithm>
#include <atomic>
#include <cassert>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>
using std::min;
using std::max;
struct String : std::string {
 using std::string::string;
 String(const std::string& s):std::string(s){}
 void trim(){}
 int indexOf(const char* s) const {auto p=find(s);return p==npos?-1:(int)p;}
 float toFloat() const {return empty()?0:std::stof(*this);}
 int toInt() const {return (int)toFloat();}
};
String operator+(const char* a,const String& b){return std::string(a)+b;}
struct SerialMock {
 void println(const char*){} void println(const String&){}
 template<class... T> void printf(const char*,T...){}
} Serial;
unsigned long tick=0, stopAt=0;
unsigned long millis(){return tick;}
int scenario=0, busAttempts=0;
struct Stop{};
#define pdMS_TO_TICKS(x) (x)
const int pdTRUE=1, portMAX_DELAY=-1, WL_CONNECTED=3;
void* hubHttpMutex=(void*)1; void* commandMutex=(void*)2;
void* cmdMutex=nullptr;
int xSemaphoreTake(void* mutex,int){
 if((scenario==6 || scenario==7) && mutex==hubHttpMutex && ++busAttempts<=3){tick+=100;return 0;}
 return pdTRUE;
}
void xSemaphoreGive(void*){}
void vTaskDelay(unsigned long ms){tick+=ms;if(tick>=stopAt)throw Stop{};}
struct WifiMock {
 int drops=0; int status(){return WL_CONNECTED;}
 String macAddress(){return "00:11:22:33:44:55";}
 void disconnect(bool,bool){drops++;}
} WiFi;
bool otaInProgress=false, reconnect_Wifi=true;
String sensorHubURL="http://192.168.4.1",lastCommandSource="hub";
float targetFlowSetpoint=1.9f,flowSetpoint=1.9f,flowFeedforward=1.9f;
float flowTransitionVoltage=.0545f, readFlowVoltage=.1f,readFlowRate=1.9f;
float ffGain=1,ffOffset=0,Kp_flow=.1f,Ki_flow=.05f,rampRate=3;
uint8_t valve1State=0,valve2State=1,valveFlowState=0;
uint32_t lastAppliedHubCommandId=1,bootSessionId=1,currentCalCrc=1;
unsigned long lastCommandApplyMs=0,lastHTTPDataTime=0,lastCommandPollTime=0;
bool adsHealthy=true,dacHealthy=true,hardwareFaultLatched=false;
#define FW_VERSION "host-test"
int telemetryCalls=0, helloCalls=0,commandCalls=0;
unsigned long firstCommandAt=0;
std::vector<unsigned long> telemetryTimes,helloTimes;
struct HTTPClient {
 std::string url;
 void setReuse(bool){} void setConnectTimeout(int){} void setTimeout(int){}
 void begin(const char* value){url=value;}
 int GET(){
  tick+=50;
  if(url.find("flowCommand")!=std::string::npos){if(commandCalls++==0)firstCommandAt=tick;return -1;}
  if(url.find("nodeHello")!=std::string::npos){helloCalls++;helloTimes.push_back(tick);return scenario==2 || scenario==3?-1:200;}
  telemetryCalls++;telemetryTimes.push_back(tick);
  if(scenario==8)return 500;
  return scenario==2 || scenario==9 || (scenario==4 && telemetryCalls<=4)?-1:200;
 }
 String getString(){return "{}";} void end(){}
};
void processReceivedData(const String&,int){}
const int COMMAND_HUB=0;
#define ESP32_EVT(x) ((void)0)
String getValueFromJson(const String& json,const char* key){
 auto pos=json.find(std::string("\"")+key+"\"");
 if(pos==std::string::npos)return "";
 pos=json.find(':',pos)+1;
 return json.substr(pos,json.find_first_of(",}",pos)-pos);
}
'''

MAILBOX_MAIN = r'''
void checkMailbox(){
 String first="{\"flowSetpoint\":1.9,\"valve_1\":0,\"valve_2\":1,\"v_Flow\":0}";
 assert(queueReliableFlowCommandFromJson(first)==1); // first contact
 auto payload=pendingFlowmeterCommand;
 pendingMaxFlow=true;desiredMaxFlow=50;
 assert(queueReliableFlowCommandFromJson(first)==0); // same state while pending
 assert(flowCommandRevision==1 && flowCommandAwaitingAck && pendingMaxFlow);
 assert(pendingFlowmeterCommand==payload);
 assert(queueReliableFlowCommandFromJson("{\"flowSetpoint\":2.0}")==2);
 assert(pendingMaxFlow && desiredFlowValve1==0 && desiredFlowValve2==1);
 flowCommandAwaitingAck=false;pendingMaxFlow=false;flowmeterCommOn=true;
 assert(queueReliableFlowCommandFromJson("{\"flowSetpoint\":2.0}")==0);
 assert(queueReliableFlowCommandFromJson("{\"valve_1\":1}")==3); // route change
 assert(queueReliableFlowCommandFromJson("{\"maxFlow\":50}")==4); // explicit configuration
 flowCommandAwaitingAck=false;flowmeterCommOn=false;
 assert(queueReliableFlowCommandFromJson("{\"flowSetpoint\":2.0}")==5); // offline retry
 assert(queueReliableFlowCommandFromJson("{\"flowSetpoint\":0}")==6);
 assert(desiredFlowValveFlow==1); // zero still closes the shutoff
 flowCommandAwaitingAck=false;flowmeterCommOn=true;
 tick=FLOWMETER_TIMEOUT+1; // aggregate online flag has not been refreshed yet
 assert(queueReliableFlowCommandFromJson("{\"flowSetpoint\":0}")==7);
 puts("PASS: production Hub mailbox deduplication");
}
'''

MAIN = r'''
int main(int argc,char** argv){
 scenario=std::stoi(argv[1]);
 if(scenario==5){checkMailbox();return 0;}
 if(scenario==7){
  stopAt=3000;try{httpTask(nullptr);}catch(Stop&){}
  assert(firstCommandAt<1000); // missed mutex must not postpone by 500 ms each time
  return 0;
 }
 if(scenario==1){
  stopAt=15000;try{httpTask(nullptr);}catch(Stop&){}
  assert(hubCommandFailures.load()>0);
  assert(WiFi.drops==0);
  tick=0;lastHTTPDataTime=0;
 }
 stopAt=16000;try{telemetryTask(nullptr);}catch(Stop&){}
 assert(telemetryTimes.size()>10);
 assert(telemetryTimes.front()<1200);
 for(size_t i=1;i<telemetryTimes.size();i++)assert(telemetryTimes[i]-telemetryTimes[i-1]<=1200);
 if(scenario==1)assert(WiFi.drops==0 && hubTelemetryFailures.load()==0);
 if(scenario==2)assert(WiFi.drops>=1); // command success cannot mask telemetry loss
 if(scenario==3){
  assert(WiFi.drops==0 && helloCalls>=2 && helloCalls<=4);
  for(size_t i=1;i<helloTimes.size();i++)assert(helloTimes[i]-helloTimes[i-1]>=5000);
 }
 if(scenario==4)assert(WiFi.drops==0 && hubTelemetryFailures.load()==0);
 if(scenario==6)assert(telemetryTimes.front()<1000);
 if(scenario==8)assert(WiFi.drops==0 && hubTelemetryFailures.load()>0);
 if(scenario==9)assert(WiFi.drops==0 && helloCalls>0 && hubTelemetryFailures.load()>0);
 puts("PASS: production network task fault injection");
}
'''


class NetworkReliabilityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        vswhere = Path(os.environ.get("ProgramFiles(x86)", "")) / "Microsoft Visual Studio/Installer/vswhere.exe"
        if not vswhere.exists():
            raise unittest.SkipTest("Visual Studio C++ tools unavailable")
        install = subprocess.check_output([str(vswhere), "-latest", "-products", "*",
            "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"], text=True).strip()
        if not install:
            raise unittest.SkipTest("Visual Studio C++ tools unavailable")
        cls.temp = tempfile.TemporaryDirectory(prefix="tecnal-flow-network-")
        cls.addClassCleanup(cls.temp.cleanup)
        folder = Path(cls.temp.name)
        app = (SRC / "core/FirmwareApp.cpp").read_text(encoding="utf-8")
        names = ("commandPollInterval", "telemetryInterval", "hubConnectTimeoutMs",
                 "hubRequestTimeoutMs", "MAX_HUB_BACKOFF_MS", "MAX_TELEMETRY_BACKOFF_MS",
                 "telemetryFailuresBeforeRelink", "hubTelemetryFailures", "hubCommandFailures",
                 "hubLastResponseMs", "hubSilenceBeforeRelinkMs")
        declarations = []
        for name in names:
            declarations.append(re.search(r"^.*\b" + name + r"\b.*;\s*$", app, re.M).group())
        tasks = (SRC / "tasks/TaskRuntime.h").read_text(encoding="utf-8")
        tasks = tasks[tasks.index("void httpTask("):tasks.index("// No broad WiFi scan")]
        mailbox_source = (ROOT.parents[1] / "ESP32S3-HUB/ESP32S3-HUB/src/protocol/Mailboxes.h").read_text(encoding="utf-8")
        mailbox = mailbox_source[mailbox_source.index("uint32_t queueReliableFlowCommandFromJson("):mailbox_source.index("// ============ FUNCTION DECLARATIONS")]
        mailbox_declarations = []
        for name in sorted(set(re.findall(r"\b(?:desired|pending)\w+", mailbox))):
            if name == "pendingFlowmeterCommand":
                mailbox_declarations.append("String pendingFlowmeterCommand;")
            else:
                kind = "bool" if name.startswith("pending") else "float"
                mailbox_declarations.append(f"{kind} {name}=0;")
        mailbox_declarations += ["bool flowCommandAwaitingAck=false,flowmeterCommOn=false;",
            "unsigned long flowmeterLastUpdate=0; const unsigned long FLOWMETER_TIMEOUT=6000;",
            "uint32_t flowCommandRevision=0,flowCommandDeliveryCount=0;",
            "unsigned long flowCommandQueuedAt=0;",
            'String buildFlowCommandLocked(){return std::to_string(flowCommandRevision);}']
        (folder / "network.cpp").write_text(STUBS + "\n" + "\n".join(declarations + mailbox_declarations) + "\n" + tasks + mailbox + MAILBOX_MAIN + MAIN, encoding="utf-8")
        vcvars = Path(install) / "VC/Auxiliary/Build/vcvars64.bat"
        (folder / "build.cmd").write_text(f'@echo off\ncall "{vcvars}" >nul\ncl /nologo /EHsc /std:c++17 /utf-8 network.cpp /Fe:network.exe\n', encoding="utf-8")
        result = subprocess.run(["cmd", "/c", str(folder / "build.cmd")], cwd=folder, capture_output=True, text=True)
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)
        cls.binary = folder / "network.exe"

    def run_scenario(self, scenario):
        result = subprocess.run([str(self.binary), str(scenario)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_command_failures_do_not_delay_or_disconnect_telemetry(self): self.run_scenario(1)
    def test_persistent_telemetry_failure_still_reassociates(self): self.run_scenario(2)
    def test_hello_failure_does_not_create_a_request_storm(self): self.run_scenario(3)
    def test_transient_telemetry_failure_recovers_without_reassociation(self): self.run_scenario(4)
    def test_hub_dedup_preserves_pending_commands_configuration_and_offline_retry(self): self.run_scenario(5)
    def test_busy_http_bus_does_not_skip_telemetry_periods(self): self.run_scenario(6)
    def test_busy_http_bus_does_not_skip_command_periods(self): self.run_scenario(7)
    def test_http_server_errors_do_not_force_wifi_disconnect(self): self.run_scenario(8)
    def test_other_hub_responses_prevent_false_mute_reassociation(self): self.run_scenario(9)


if __name__ == "__main__":
    unittest.main()
