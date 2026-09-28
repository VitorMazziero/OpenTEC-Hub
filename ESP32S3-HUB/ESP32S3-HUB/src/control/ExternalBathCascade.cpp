#include "ExternalBathCascade.h"

#include <math.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>

namespace {
constexpr float kEpsilon = 0.0001f;
// Past the x.x5 rounding boundary: a 0.1 step needs the output 0.08 away.
constexpr float kCommandHysteresisC = 0.03f;

bool finitePositive(float value) {
  return isfinite(value) && value > 0.0f;
}

float quantizeTenth(float value) {
  return roundf(value * 10.0f) / 10.0f;
}

// Setpoints are whole tenths, so they are compared as integers. In float,
// 33.5f - 33.4f = 0.0999985 < 0.1f: about a third of the 0.1 steps were taken
// as smaller than the band and the output had to move 0.2 before a command.
long toTenths(float value) {
  return lroundf(value * 10.0f);
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
      candidate.outputMinC >= candidate.outputMaxC ||
      !isfinite(candidate.fineEnterBandC) || candidate.fineEnterBandC <= 0.0f ||
      candidate.fineEnterBandC > 50.0f || !isfinite(candidate.fineExitBandC) ||
      candidate.fineExitBandC < candidate.fineEnterBandC || candidate.fineExitBandC > 50.0f ||
      !isfinite(candidate.fineSlopeCMin) || candidate.fineSlopeCMin <= 0.0f ||
      candidate.fineSlopeCMin > 10.0f || candidate.slopeWindowMs < 10000UL ||
      candidate.slopeWindowMs > 3600000UL) {
    return false;
  }
  return true;
}

bool ExternalBathCascade::configure(const ExternalBathCascadeConfig& candidate) {
  if (!validateConfig(candidate)) {
    return false;
  }
  config_ = candidate;
  // Bumpless retune: a new Kp/bias must not move the output by itself. The integral
  // absorbs the difference so only future error changes the command.
  if (filterInitialized_ && snapshot_.hasCommand &&
      (snapshot_.state == ExternalBathCascadeState::Controlling ||
       snapshot_.state == ExternalBathCascadeState::ActuatorBusy)) {
    rebaseIntegral(lastReferenceC_);
    snapshot_.iC = integralC_;
  }
  return true;
}

void ExternalBathCascade::rebaseIntegral(float referenceC) {
  integralC_ = outputC_ - referenceC - config_.biasC - config_.kp * snapshot_.errorC;
}

void ExternalBathCascade::clearSlope() {
  slopeHead_ = 0;
  slopeCount_ = 0;
  snapshot_.slopeValid = false;
  snapshot_.slopeCMin = 0.0f;
}

void ExternalBathCascade::pushSlope(uint32_t nowMs, float pvC) {
  const uint32_t spacingMs = config_.slopeWindowMs / (kSlopeSlots - 1);
  if (slopeCount_ > 0) {
    const uint8_t newest = (slopeHead_ + kSlopeSlots - 1) % kSlopeSlots;
    // 3/4 of the spacing tolerates loop jitter without halving the sample rate.
    if (nowMs - slopeTimeMs_[newest] < spacingMs - spacingMs / 4) return;
  }
  slopeTimeMs_[slopeHead_] = nowMs;
  slopePvC_[slopeHead_] = pvC;
  slopeHead_ = (slopeHead_ + 1) % kSlopeSlots;
  if (slopeCount_ < kSlopeSlots) slopeCount_++;
  if (slopeCount_ < kSlopeSlots) return;
  // Full ring: slopeHead_ now points at the oldest sample.
  const uint8_t newest = (slopeHead_ + kSlopeSlots - 1) % kSlopeSlots;
  const uint32_t spanMs = slopeTimeMs_[newest] - slopeTimeMs_[slopeHead_];
  if (spanMs == 0) return;
  snapshot_.slopeCMin = (slopePvC_[newest] - slopePvC_[slopeHead_]) * 60000.0f / spanMs;
  snapshot_.slopeValid = true;
}

void ExternalBathCascade::reset() {
  snapshot_ = ExternalBathCascadeSnapshot{};
  filterInitialized_ = false;
  lastInputMs_ = 0;
  integralC_ = 0.0f;
  outputC_ = 0.0f;
  lastCommandC_ = 0.0f;
  faultLatched_ = false;
  rebasePending_ = false;
  lastReferenceC_ = 0.0f;
  clearSlope();
}

void ExternalBathCascade::markCommandSent(float setpointC, uint32_t nowMs) {
  lastCommandC_ = quantizeTenth(setpointC);
  snapshot_.commandSetpointC = lastCommandC_;
  snapshot_.hasCommand = true;
  snapshot_.lastCommandMs = nowMs;
  snapshot_.commandReady = false;
}

float ExternalBathCascade::lowerLimit(float referenceC) const {
  float absolute = config_.outputMinC;
  if (actuatorRangeValid_ && actuatorMinC_ > absolute) absolute = actuatorMinC_;
  const float relative = referenceC - config_.offsetLowC;
  return relative > absolute ? relative : absolute;
}

float ExternalBathCascade::upperLimit(float referenceC) const {
  float absolute = config_.outputMaxC;
  if (actuatorRangeValid_ && actuatorMaxC_ < absolute) absolute = actuatorMaxC_;
  const float relative = referenceC + config_.offsetHighC;
  return relative < absolute ? relative : absolute;
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
  actuatorRangeValid_ = in.actuatorRangeValid && isfinite(in.actuatorMinC) &&
                        isfinite(in.actuatorMaxC) && in.actuatorMinC < in.actuatorMaxC;
  actuatorMinC_ = in.actuatorMinC;
  actuatorMaxC_ = in.actuatorMaxC;

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
    snapshot_.fineActive = false;
    clearSlope();
    setState(ExternalBathCascadeState::Off);
    lastInputMs_ = in.nowMs;
    return false;
  }

  if (in.fault) {
    faultLatched_ = true;
    setReason(in.faultReason);
  }
  if (faultLatched_) {
    snapshot_.state = ExternalBathCascadeState::Fault;
    snapshot_.saturated = false;
    // A falha exige reset explícito. Atualizar a origem temporal evita que um
    // reset tardio transforme todo o tempo parado em um único passo integral.
    lastInputMs_ = in.nowMs;
    return false;
  }
  if (in.pause) {
    setState(ExternalBathCascadeState::Paused, in.pauseReason);
    // Freeze the integrator and restart the elapsed-time origin so a long
    // communication/PV outage cannot create a recovery step. On return the filter
    // restarts from the fresh PV and the integral is rebased around the held output.
    lastInputMs_ = in.nowMs;
    rebasePending_ = true;
    return false;
  }

  const bool inputsReady = in.referenceValid && in.referenceC > 0.0f &&
                           in.reactorPvValid && isfinite(in.reactorPvC) &&
                           in.nodeOnline && in.bathCommEnabled && in.bathSpSourceDisplay &&
                           in.bathModeAuto && in.bathGuardHealthy && in.bathSpValid &&
                           isfinite(in.bathSpC);
  if (!inputsReady) {
    setState(ExternalBathCascadeState::WaitingInputs,
             in.waitingReason && in.waitingReason[0] ? in.waitingReason : "inputs");
    lastInputMs_ = in.nowMs;
    // The next valid sample re-seeds from the display SP; an old filtered PV from
    // before a long outage must not survive into that seed.
    filterInitialized_ = false;
    rebasePending_ = false;
    return false;
  }

  uint32_t dtMs = 0;
  if (lastInputMs_ != 0 &&
      (snapshot_.state == ExternalBathCascadeState::Controlling ||
       snapshot_.state == ExternalBathCascadeState::ActuatorBusy ||
       snapshot_.state == ExternalBathCascadeState::Approaching) &&
      in.nowMs - lastInputMs_ < config_.periodMs) {
    snapshot_.lastUpdateMs = in.nowMs;
    return false;
  }
  if (lastInputMs_ != 0) dtMs = in.nowMs - lastInputMs_;
  // A stalled loop must not turn into one huge integral/filter step.
  if (dtMs > 3 * config_.periodMs) dtMs = 3 * config_.periodMs;
  lastInputMs_ = in.nowMs;
  const float dtS = dtMs > 0 ? dtMs / 1000.0f : config_.periodMs / 1000.0f;

  lastReferenceC_ = in.referenceC;
  if (!filterInitialized_ || rebasePending_) {
    snapshot_.filteredPvC = in.reactorPvC;
    filterInitialized_ = true;
    // A re-seeded filter jumps; a slope across that jump would be fiction.
    clearSlope();
  } else {
    const float alpha = dtS / (config_.filterS + dtS);
    snapshot_.filteredPvC += alpha * (in.reactorPvC - snapshot_.filteredPvC);
  }
  snapshot_.errorC = in.referenceC - snapshot_.filteredPvC;
  pushSlope(in.nowMs, snapshot_.filteredPvC);

  if (snapshot_.state == ExternalBathCascadeState::WaitingInputs ||
      snapshot_.state == ExternalBathCascadeState::Off || !snapshot_.hasCommand) {
    setState(ExternalBathCascadeState::Initializing);
    // The display SP is where the bath really is: it is the slew origin and what the
    // next command is compared with. The integral is never seeded from the startup
    // error (a large error would load it with a value unrelated to the heat loss);
    // it starts at zero after Off and keeps what it learned after a wait.
    outputC_ = in.bathSpC;
    lastCommandC_ = quantizeTenth(in.bathSpC);
    snapshot_.hasCommand = true;
    // Nothing was sent yet, so the first real command does not wait commandMinMs.
    snapshot_.lastCommandMs = in.nowMs - config_.commandMinMs;
  }

  const float absError = fabsf(snapshot_.errorC);
  if (snapshot_.fineActive) {
    if (absError > config_.fineExitBandC) snapshot_.fineActive = false;
  } else if (absError < config_.fineEnterBandC && snapshot_.slopeValid &&
             fabsf(snapshot_.slopeCMin) < config_.fineSlopeCMin) {
    snapshot_.fineActive = true;
  }

  if (!snapshot_.fineActive) {
    // Approach: the bath holds ref + bias + the learned integral. One direct command
    // (no slew: each step would be a relay sequence on the panel), no integration.
    rebasePending_ = false;
    const float target = in.referenceC + config_.biasC + integralC_;
    outputC_ = clampOutput(target, in.referenceC);
    snapshot_.pC = 0.0f;
    snapshot_.iC = integralC_;
    snapshot_.rawOutputC = target;
    snapshot_.commandSetpointC = quantizeTenth(outputC_);
    snapshot_.saturated = fabsf(outputC_ - target) > kEpsilon;
    setState(ExternalBathCascadeState::Approaching, in.actuatorBusy ? "actuator_busy" : "");
  } else {
    if (rebasePending_) {
      rebaseIntegral(in.referenceC);
      rebasePending_ = false;
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
  }

  // The C404 resolves 0.1: a band below one tenth still means one tenth.
  const long bandTenths = toTenths(config_.commandBandC) > 1 ? toTenths(config_.commandBandC) : 1;
  const long stepTenths = labs(toTenths(snapshot_.commandSetpointC) - toTenths(lastCommandC_));
  // Rounding alone would switch at x.x5; an output parked there (the steady state
  // often is) plus sensor noise flipped the command every minute. The output must
  // pass the rounding boundary by kCommandHysteresisC before the tenth changes;
  // until then the command in force is what is published.
  const bool moved = !snapshot_.hasCommand ||
                     (stepTenths >= bandTenths &&
                      fabsf(outputC_ - lastCommandC_) >=
                          bandTenths * 0.1f - 0.05f + kCommandHysteresisC);
  if (!moved) snapshot_.commandSetpointC = lastCommandC_;

  if (in.actuatorBusy) return false;
  if (moved) {
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
    case ExternalBathCascadeState::Approaching: return "approaching";
  }
  return "fault";
}
