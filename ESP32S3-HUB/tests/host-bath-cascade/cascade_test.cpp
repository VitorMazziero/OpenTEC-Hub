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

  // Drive the controller into steady operation: commands accepted, actuator idle.
  auto run = [&](ExternalBathCascade& c, ExternalBathCascadeInputs base, uint32_t fromMs,
                 uint32_t toMs) {
    for (uint32_t t = fromMs; t <= toMs; t += 1000) {
      base.nowMs = t;
      if (c.update(base)) c.markCommandSent(c.snapshot().commandSetpointC, t);
    }
  };

  {
    // Actuator range (r3.2): output never leaves the node's sp_min/sp_max.
    ExternalBathCascade c;
    auto r = readyInputs(1000);
    r.referenceC = 34.0f;
    r.reactorPvC = 20.0f;  // large error pushes the output up
    r.bathSpC = 34.0f;
    r.actuatorRangeValid = true;
    r.actuatorMinC = 5.0f;
    r.actuatorMaxC = 35.0f;
    run(c, r, 1000, 1800000);
    CHECK(c.snapshot().commandSetpointC <= 35.0f + 0.001f,
          "output must respect the node range reported by the bath");
  }

  {
    // Bumpless retune: a new Kp must not move the output by itself.
    ExternalBathCascade c;
    auto r = readyInputs(1000);
    r.reactorPvC = 29.5f;
    run(c, r, 1000, 600000);
    const float before = c.snapshot().commandSetpointC;
    ExternalBathCascadeConfig cfg = c.config();
    cfg.kp = 2.0f;
    CHECK(c.configure(cfg), "valid retune must be accepted");
    r.nowMs = 610000;
    c.update(r);
    const float after = c.snapshot().commandSetpointC;
    CHECK(std::fabs(after - before) <= 0.11f, "retune must not step the output");
  }

  {
    // Pause/resume after a long PV outage: no recovery step, filter restarts on
    // the fresh PV instead of the value from before the outage.
    ExternalBathCascade c;
    auto r = readyInputs(1000);
    r.reactorPvC = 29.0f;
    run(c, r, 1000, 300000);
    const float before = c.snapshot().commandSetpointC;
    r.pause = true;
    r.pauseReason = "reactor_pv_stale";
    for (uint32_t t = 310000; t < 3910000; t += 1000) {
      r.nowMs = t;
      c.update(r);
    }
    CHECK(c.snapshot().state == ExternalBathCascadeState::Paused, "stale PV must pause");
    r.pause = false;
    r.reactorPvC = 27.0f;
    r.nowMs = 3910000;  // one hour later
    c.update(r);
    CHECK(std::fabs(c.snapshot().filteredPvC - 27.0f) < 0.001f,
          "filter must restart from the fresh PV after a pause");
    CHECK(std::fabs(c.snapshot().commandSetpointC - before) <= 0.11f,
          "resume after a pause must not step the output");
  }

  {
    // Waiting reason is published and the filter is re-seeded after waiting.
    ExternalBathCascade c;
    auto r = readyInputs(1000);
    run(c, r, 1000, 60000);
    r.nodeOnline = false;
    r.waitingReason = "node_offline";
    r.nowMs = 61000;
    c.update(r);
    CHECK(c.snapshot().state == ExternalBathCascadeState::WaitingInputs &&
          std::strcmp(c.snapshot().pausedReason, "node_offline") == 0,
          "waiting state must carry the first missing input");
    r.nodeOnline = true;
    r.reactorPvC = 25.0f;
    r.nowMs = 3661000;
    c.update(r);
    CHECK(std::fabs(c.snapshot().filteredPvC - 25.0f) < 0.001f,
          "re-initialization must use the fresh PV, not the pre-outage filter");
  }

  std::printf("%s (%d failure(s))\n", failures ? "FAILED" : "ALL PASSED", failures);
  return failures ? 1 : 0;
}
