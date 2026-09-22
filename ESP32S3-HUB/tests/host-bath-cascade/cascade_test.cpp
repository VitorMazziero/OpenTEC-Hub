#include <cmath>
#include <cstdio>
#include <cstring>

#include "../../ESP32S3-HUB/src/control/ExternalBathCascade.h"

namespace {
int failures = 0;

#define CHECK(condition, message) \
  do { \
    if (!(condition)) { \
      std::printf("FAIL: %s\n", message); \
      ++failures; \
    } \
  } while (0)

ExternalBathCascadeInputs readyInputs(uint32_t nowMs) {
  ExternalBathCascadeInputs in;
  in.nowMs = nowMs;
  in.enabled = true;
  in.referenceValid = true;
  in.referenceC = 30.0f;
  in.reactorPvValid = true;
  in.reactorPvC = 29.0f;
  in.nodeOnline = true;
  in.bathCommEnabled = true;
  in.bathSpSourceDisplay = true;
  in.bathModeAuto = true;
  in.bathGuardHealthy = true;
  in.bathSpValid = true;
  in.bathSpC = 30.5f;
  return in;
}
}  // namespace

int main() {
  ExternalBathCascade cascade;
  auto in = readyInputs(1000);
  cascade.update(in);
  CHECK(cascade.snapshot().state == ExternalBathCascadeState::Controlling,
        "valid inputs must initialize the controller");

  in.nowMs = 2000;
  in.fault = true;
  in.faultReason = "bath_fault";
  cascade.update(in);
  CHECK(cascade.snapshot().state == ExternalBathCascadeState::Fault,
        "bath fault must enter Fault");

  in.nowMs = 3000;
  in.fault = false;
  in.faultReason = "";
  cascade.update(in);
  CHECK(cascade.snapshot().state == ExternalBathCascadeState::Fault,
        "fault must remain latched after the input recovers");
  CHECK(std::strcmp(cascade.snapshot().pausedReason, "bath_fault") == 0,
        "latched fault must preserve its original reason");

  cascade.reset();
  in.nowMs = 4000;
  cascade.update(in);
  CHECK(cascade.snapshot().state == ExternalBathCascadeState::Controlling,
        "explicit reset must permit bumpless reinitialization");

  std::printf("%s (%d failure(s))\n", failures ? "FAILED" : "ALL PASSED", failures);
  return failures ? 1 : 0;
}
