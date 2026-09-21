#include "ExternalBathCascade.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

namespace {
constexpr float kEpsilon = 0.0001f;

bool finitePositive(float value) {
  return isfinite(value) && value > 0.0f;
}

float quantizeTenth(float value) {
  return roundf(value * 10.0f) / 10.0f;
}
}

ExternalBathCascade::ExternalBathCascade() : config_(defaults()) {
  reset();
}

ExternalBathCascadeConfig ExternalBathCascade::defaults() {
  return ExternalBathCascadeConfig{};
}

bool ExternalBathCascade::validateConfig(const ExternalBathCascadeConfig& candidate) {
  if (!finitePositive(candidate.kp) || !finitePositive(candidate.tiS) ||
      !isfinite(candidate.biasC) || candidate.periodMs < 100 || candidate.periodMs > 600000 ||
      !isfinite(candidate.filterS) || candidate.filterS <= 0.0f || candidate.filterS > 3600.0f ||
      candidate.commandMinMs > 3600000UL || !isfinite(candidate.commandBandC) ||
      candidate.commandBandC <= 0.0f || candidate.commandBandC > 10.0f ||
      !isfinite(candidate.slewCMin) || candidate.slewCMin <= 0.0f || candidate.slewCMin > 100.0f ||
      !isfinite(candidate.offsetHighC) || candidate.offsetHighC < 0.0f || candidate.offsetHighC > 100.0f ||
      !isfinite(candidate.offsetLowC) || candidate.offsetLowC < 0.0f || candidate.offsetLowC > 100.0f ||
      !isfinite(candidate.outputMinC) || !isfinite(candidate.outputMaxC) ||
      candidate.outputMinC < 0.0f || candidate.outputMaxC > 100.0f ||
      candidate.outputMinC >= candidate.outputMaxC) {
    return false;
  }
  return true;
}

bool ExternalBathCascade::configure(const ExternalBathCascadeConfig& candidate) {
  if (!validateConfig(candidate)) {
    return false;
  }
  config_ = candidate;
  return true;
}

void ExternalBathCascade::reset() {
  snapshot_ = ExternalBathCascadeSnapshot{};
  filterInitialized_ = false;
  lastInputMs_ = 0;
  integralC_ = 0.0f;
  outputC_ = 0.0f;
  lastCommandC_ = 0.0f;
}

void ExternalBathCascade::markCommandSent(float setpointC, uint32_t nowMs) {
  lastCommandC_ = quantizeTenth(setpointC);
  snapshot_.commandSetpointC = lastCommandC_;
  snapshot_.hasCommand = true;
  snapshot_.lastCommandMs = nowMs;
  snapshot_.commandReady = false;
}

float ExternalBathCascade::lowerLimit(float referenceC) const {
  const float relative = referenceC - config_.offsetLowC;
  return relative > config_.outputMinC ? relative : config_.outputMinC;
}

float ExternalBathCascade::upperLimit(float referenceC) const {
  const float relative = referenceC + config_.offsetHighC;
  return relative < config_.outputMaxC ? relative : config_.outputMaxC;
}

float ExternalBathCascade::clampOutput(float valueC, float referenceC) const {
  float low = lowerLimit(referenceC);
  float high = upperLimit(referenceC);
  if (low > high) {
    const float middle = (low + high) * 0.5f;
    low = middle;
    high = middle;
  }
  if (valueC < low) return low;
  if (valueC > high) return high;
  return valueC;
}

void ExternalBathCascade::setReason(const char* reason) {
  snprintf(snapshot_.pausedReason, sizeof(snapshot_.pausedReason), "%s", reason ? reason : "");
}

void ExternalBathCascade::setState(ExternalBathCascadeState state, const char* reason) {
  snapshot_.state = state;
  setReason(reason);
}

bool ExternalBathCascade::update(const ExternalBathCascadeInputs& in) {
  snapshot_.commandReady = false;
  snapshot_.lastUpdateMs = in.nowMs;

  if (!in.enabled || (in.referenceValid && in.referenceC <= 0.0f)) {
    integralC_ = 0.0f;
    filterInitialized_ = false;
    outputC_ = 0.0f;
    snapshot_.filteredPvC = 0.0f;
    snapshot_.errorC = 0.0f;
    snapshot_.pC = 0.0f;
    snapshot_.iC = 0.0f;
    snapshot_.rawOutputC = 0.0f;
    snapshot_.commandSetpointC = 0.0f;
    snapshot_.saturated = false;
    snapshot_.hasCommand = false;
    setState(ExternalBathCascadeState::Off);
    lastInputMs_ = in.nowMs;
    return false;
  }

  if (in.fault) {
    setState(ExternalBathCascadeState::Fault, in.faultReason);
    snapshot_.saturated = false;
    return false;
  }
  if (in.pause) {
    setState(ExternalBathCascadeState::Paused, in.pauseReason);
    // Freeze the integrator and restart the elapsed-time origin so a long
    // communication/PV outage cannot create a recovery step.
    lastInputMs_ = in.nowMs;
    return false;
  }

  const bool inputsReady = in.referenceValid && in.referenceC > 0.0f &&
                           in.reactorPvValid && isfinite(in.reactorPvC) &&
                           in.nodeOnline && in.bathCommEnabled && in.bathSpSourceDisplay &&
                           in.bathModeAuto && in.bathGuardHealthy && in.bathSpValid &&
                           isfinite(in.bathSpC);
  if (!inputsReady) {
    setState(ExternalBathCascadeState::WaitingInputs, "inputs");
    lastInputMs_ = in.nowMs;
    return false;
  }

  uint32_t dtMs = 0;
  if (lastInputMs_ != 0 &&
      (snapshot_.state == ExternalBathCascadeState::Controlling ||
       snapshot_.state == ExternalBathCascadeState::ActuatorBusy) &&
      in.nowMs - lastInputMs_ < config_.periodMs) {
    snapshot_.lastUpdateMs = in.nowMs;
    return false;
  }
  if (lastInputMs_ != 0) dtMs = in.nowMs - lastInputMs_;
  lastInputMs_ = in.nowMs;
  const float dtS = dtMs > 0 ? dtMs / 1000.0f : config_.periodMs / 1000.0f;

  if (!filterInitialized_) {
    snapshot_.filteredPvC = in.reactorPvC;
    filterInitialized_ = true;
  } else {
    const float alpha = dtS / (config_.filterS + dtS);
    snapshot_.filteredPvC += alpha * (in.reactorPvC - snapshot_.filteredPvC);
  }
  snapshot_.errorC = in.referenceC - snapshot_.filteredPvC;

  if (snapshot_.state == ExternalBathCascadeState::WaitingInputs ||
      snapshot_.state == ExternalBathCascadeState::Off || !snapshot_.hasCommand) {
    const float seed = in.bathSpValid ? clampOutput(in.bathSpC, in.referenceC)
                                      : clampOutput(in.referenceC + config_.biasC, in.referenceC);
    integralC_ = seed - in.referenceC - config_.biasC - config_.kp * snapshot_.errorC;
    outputC_ = seed;
    snapshot_.pC = config_.kp * snapshot_.errorC;
    snapshot_.iC = integralC_;
    snapshot_.rawOutputC = seed;
    snapshot_.commandSetpointC = quantizeTenth(seed);
    snapshot_.saturated = false;
    setState(ExternalBathCascadeState::Initializing);
    if (in.bathSpValid) markCommandSent(snapshot_.commandSetpointC, in.nowMs);
    setState(ExternalBathCascadeState::Controlling);
    return !in.bathSpValid;
  }

  const float ki = config_.kp / config_.tiS;
  const float candidateIntegral = integralC_ + ki * snapshot_.errorC * dtS;
  const float candidateRaw = in.referenceC + config_.biasC +
                             config_.kp * snapshot_.errorC + candidateIntegral;
  const float limited = clampOutput(candidateRaw, in.referenceC);
  const float maxStep = config_.slewCMin * dtS / 60.0f;
  float nextOutput = limited;
  bool saturated = fabsf(limited - candidateRaw) > kEpsilon;
  if (maxStep > 0.0f && fabsf(limited - outputC_) > maxStep) {
    nextOutput = outputC_ + (limited > outputC_ ? maxStep : -maxStep);
    saturated = true;
  }

  // Conditional anti-windup: do not integrate further into an active limit.
  if (saturated && (candidateRaw - nextOutput) * snapshot_.errorC > 0.0f) {
    const float noWindupRaw = in.referenceC + config_.biasC +
                              config_.kp * snapshot_.errorC + integralC_;
    nextOutput = clampOutput(noWindupRaw, in.referenceC);
    if (maxStep > 0.0f && fabsf(nextOutput - outputC_) > maxStep) {
      nextOutput = outputC_ + (nextOutput > outputC_ ? maxStep : -maxStep);
    }
  } else {
    integralC_ = candidateIntegral;
  }

  outputC_ = nextOutput;
  snapshot_.pC = config_.kp * snapshot_.errorC;
  snapshot_.iC = integralC_;
  snapshot_.rawOutputC = candidateRaw;
  snapshot_.commandSetpointC = quantizeTenth(outputC_);
  snapshot_.saturated = saturated;
  setState(in.actuatorBusy ? ExternalBathCascadeState::ActuatorBusy
                           : ExternalBathCascadeState::Controlling,
           in.actuatorBusy ? "actuator_busy" : "");

  if (in.actuatorBusy) return false;
  if (!snapshot_.hasCommand ||
      fabsf(snapshot_.commandSetpointC - lastCommandC_) >= config_.commandBandC) {
    const uint32_t sinceCommand = snapshot_.hasCommand ? in.nowMs - snapshot_.lastCommandMs : config_.commandMinMs;
    snapshot_.commandReady = sinceCommand >= config_.commandMinMs;
  }
  return snapshot_.commandReady;
}

const char* externalBathCascadeStateName(ExternalBathCascadeState state) {
  switch (state) {
    case ExternalBathCascadeState::Off: return "off";
    case ExternalBathCascadeState::WaitingInputs: return "waiting_inputs";
    case ExternalBathCascadeState::Initializing: return "initializing";
    case ExternalBathCascadeState::Controlling: return "controlling";
    case ExternalBathCascadeState::ActuatorBusy: return "actuator_busy";
    case ExternalBathCascadeState::Paused: return "paused";
    case ExternalBathCascadeState::Fault: return "fault";
  }
  return "fault";
}
