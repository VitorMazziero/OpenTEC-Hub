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
  CHECK(cascade.snapshot().state == ExternalBathCascadeState::Approaching,
        "valid inputs must initialize in approach until the reactor has settled");

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
  CHECK(cascade.snapshot().state == ExternalBathCascadeState::Approaching,
        "explicit reset must permit reinitialization");

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

  {
    // Startup far from the reference: one direct command to ref + bias, no PI, no
    // integral, however long the approach takes.
    ExternalBathCascade c;
    auto r = readyInputs(1000);
    r.referenceC = 37.0f;
    r.reactorPvC = 20.0f;
    r.bathSpC = 25.0f;
    int commands = 0;
    float firstCommand = 0.0f;
    for (uint32_t t = 1000; t <= 1800000; t += 1000) {
      r.nowMs = t;
      r.reactorPvC = 20.0f + 15.0f * (t / 1800000.0f);  // 0.5 C/min
      if (c.update(r)) {
        if (commands == 0) firstCommand = c.snapshot().commandSetpointC;
        ++commands;
        c.markCommandSent(c.snapshot().commandSetpointC, t);
      }
      if (t == 1000) {
        CHECK(commands == 1, "the first approach command must not wait commandMinMs");
      }
    }
    CHECK(commands == 1, "the approach must send exactly one command");
    CHECK(std::fabs(firstCommand - 37.6f) < 0.001f, "approach command must be ref + bias");
    CHECK(c.snapshot().state == ExternalBathCascadeState::Approaching,
          "a reactor still rising must not engage the PI");
    CHECK(std::fabs(c.snapshot().iC) < 0.0001f, "the integral must not move during the approach");
    CHECK(c.snapshot().slopeValid && std::fabs(c.snapshot().slopeCMin - 0.5f) < 0.05f,
          "slope must track the reactor ramp in C/min");
  }

  {
    // Entry needs both conditions; exit only by the wider band; integral is kept.
    ExternalBathCascade c;
    auto r = readyInputs(1000);
    r.referenceC = 30.0f;
    r.bathSpC = 30.6f;
    r.reactorPvC = 26.0f;  // |e| = 4 < 5, but rising fast
    uint32_t t = 1000;
    for (; t <= 300000; t += 1000) {
      r.nowMs = t;
      r.reactorPvC = 26.0f + 0.5f * (t / 60000.0f);  // 0.5 C/min, reaches 28.5
      if (c.update(r)) c.markCommandSent(c.snapshot().commandSetpointC, t);
    }
    CHECK(c.snapshot().state == ExternalBathCascadeState::Approaching,
          "inside the band but still moving must stay in approach");
    r.reactorPvC = 28.5f;  // settles 1.5 C below the reference
    bool engaged = false;
    for (; t <= 900000; t += 1000) {
      r.nowMs = t;
      if (c.update(r)) c.markCommandSent(c.snapshot().commandSetpointC, t);
      engaged = engaged || c.snapshot().fineActive;
    }
    CHECK(engaged && c.snapshot().state == ExternalBathCascadeState::Controlling,
          "settled inside the band must engage the PI");
    CHECK(c.snapshot().iC > 0.0f, "fine control below the reference must integrate upward");
    CHECK(c.snapshot().commandSetpointC > 30.6f + 0.2f, "fine control must raise the bath SP");

    const float learned = c.snapshot().iC;
    r.reactorPvC = 24.5f;  // |e| = 5.5: outside the entry band, inside the exit band
    for (uint32_t end = t + 60000; t <= end; t += 1000) {
      r.nowMs = t;
      if (c.update(r)) c.markCommandSent(c.snapshot().commandSetpointC, t);
    }
    CHECK(c.snapshot().fineActive, "hysteresis must keep the PI between the two bands");

    r.reactorPvC = 22.0f;  // |e| = 8 > 6
    for (uint32_t end = t + 120000; t <= end; t += 1000) {
      r.nowMs = t;
      if (c.update(r)) c.markCommandSent(c.snapshot().commandSetpointC, t);
    }
    CHECK(c.snapshot().state == ExternalBathCascadeState::Approaching,
          "leaving the exit band must release the PI");
    const float held = c.snapshot().iC;
    CHECK(held >= learned - 0.05f, "the learned integral must survive the release");
    CHECK(std::fabs(c.snapshot().commandSetpointC - (30.0f + 0.6f + held)) < 0.051f,
          "approach output must be ref + bias + learned integral");
  }

  {
    // A reference step while fine returns to approach with a single command.
    ExternalBathCascade c;
    auto r = readyInputs(1000);
    r.reactorPvC = 29.8f;
    uint32_t t = 1000;
    for (; t <= 600000; t += 1000) {
      r.nowMs = t;
      if (c.update(r)) c.markCommandSent(c.snapshot().commandSetpointC, t);
    }
    CHECK(c.snapshot().fineActive, "a settled reactor near the reference must be in fine control");
    r.referenceC = 40.0f;
    int commands = 0;
    for (uint32_t end = t + 600000; t <= end; t += 1000) {
      r.nowMs = t;
      if (c.update(r)) {
        ++commands;
        c.markCommandSent(c.snapshot().commandSetpointC, t);
      }
    }
    CHECK(c.snapshot().state == ExternalBathCascadeState::Approaching,
          "a large reference step must go back to approach");
    CHECK(commands == 1, "a reference step must cost a single bath command");
  }

  {
    // Gate parameters are validated.
    ExternalBathCascadeConfig cfg = ExternalBathCascade::defaults();
    cfg.fineExitBandC = cfg.fineEnterBandC - 1.0f;
    CHECK(!ExternalBathCascade::validateConfig(cfg), "exit band below entry band must be refused");
    cfg = ExternalBathCascade::defaults();
    cfg.fineSlopeCMin = 0.0f;
    CHECK(!ExternalBathCascade::validateConfig(cfg), "zero slope threshold must be refused");
    CHECK(ExternalBathCascade::validateConfig(ExternalBathCascade::defaults()),
          "defaults must be valid");
  }

  std::printf("%s (%d failure(s))\n", failures ? "FAILED" : "ALL PASSED", failures);
  return failures ? 1 : 0;
}
