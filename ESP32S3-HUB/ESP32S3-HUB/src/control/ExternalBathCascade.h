#pragma once

#include <stdint.h>

enum class ExternalBathCascadeState : uint8_t {
  Off = 0,
  WaitingInputs,
  Initializing,
  Controlling,
  ActuatorBusy,
  Paused,
  Fault,
  // Far from the reference (or not yet settled): the bath holds ref + bias + the
  // learned integral and the PI does not act.
  Approaching,
};

struct ExternalBathCascadeConfig {
  float kp = 0.5f;
  float tiS = 600.0f;
  float biasC = 0.6f;
  uint32_t periodMs = 10000;
  float filterS = 20.0f;
  uint32_t commandMinMs = 30000;
  float commandBandC = 0.1f;
  float slewCMin = 0.5f;
  float offsetHighC = 5.0f;
  float offsetLowC = 5.0f;
  float outputMinC = 5.0f;
  float outputMaxC = 90.0f;
  // Fine-tuning gate. The PI only acts close to the reference with the reactor
  // settled: it engages when |error| < fineEnterBandC and |dPV/dt| < fineSlopeCMin,
  // and releases only when |error| > fineExitBandC (the slope never releases it:
  // the PI itself moves the reactor).
  float fineEnterBandC = 5.0f;
  float fineExitBandC = 6.0f;
  float fineSlopeCMin = 0.1f;
  uint32_t slopeWindowMs = 120000;
};

struct ExternalBathCascadeInputs {
  uint32_t nowMs = 0;
  bool enabled = false;
  bool referenceValid = false;
  float referenceC = 0.0f;
  bool reactorPvValid = false;
  float reactorPvC = 0.0f;
  bool nodeOnline = false;
  bool bathCommEnabled = false;
  bool bathSpSourceDisplay = false;
  bool bathModeAuto = false;
  bool bathGuardHealthy = false;
  bool bathSpValid = false;
  float bathSpC = 0.0f;
  bool actuatorBusy = false;
  bool pause = false;
  const char* pauseReason = "";
  bool fault = false;
  const char* faultReason = "";
  // First missing input, published while waiting (e.g. "node_offline").
  const char* waitingReason = "inputs";
  // Range the node itself accepts (sp_min/sp_max, r3.2). The output is clamped to the
  // intersection with the configured limits so the node never refuses for `range`.
  bool actuatorRangeValid = false;
  float actuatorMinC = 0.0f;
  float actuatorMaxC = 100.0f;
};

struct ExternalBathCascadeSnapshot {
  ExternalBathCascadeState state = ExternalBathCascadeState::Off;
  float filteredPvC = 0.0f;
  float errorC = 0.0f;
  float pC = 0.0f;
  float iC = 0.0f;
  float rawOutputC = 0.0f;
  float commandSetpointC = 0.0f;
  bool saturated = false;
  bool fineActive = false;
  bool slopeValid = false;
  float slopeCMin = 0.0f;
  bool commandReady = false;
  bool hasCommand = false;
  uint32_t lastUpdateMs = 0;
  uint32_t lastCommandMs = 0;
  char pausedReason[32] = "";
};

class ExternalBathCascade {
 public:
  ExternalBathCascade();

  static ExternalBathCascadeConfig defaults();
  static bool validateConfig(const ExternalBathCascadeConfig& config);
  bool configure(const ExternalBathCascadeConfig& config);
  const ExternalBathCascadeConfig& config() const { return config_; }

  void reset();
  void markCommandSent(float setpointC, uint32_t nowMs);
  bool update(const ExternalBathCascadeInputs& inputs);
  ExternalBathCascadeSnapshot snapshot() const { return snapshot_; }
  // Last setpoint handed to the node (or the display SP it was seeded from).
  float lastCommandC() const { return lastCommandC_; }

 private:
  ExternalBathCascadeConfig config_;
  ExternalBathCascadeSnapshot snapshot_;
  bool filterInitialized_ = false;
  uint32_t lastInputMs_ = 0;
  float integralC_ = 0.0f;
  float outputC_ = 0.0f;
  float lastCommandC_ = 0.0f;
  bool faultLatched_ = false;
  // Set after a pause: the filter restarts from the fresh PV and the integral is
  // rebased so the output continues from where it stopped (no recovery step).
  bool rebasePending_ = false;
  float lastReferenceC_ = 0.0f;
  bool actuatorRangeValid_ = false;
  float actuatorMinC_ = 0.0f;
  float actuatorMaxC_ = 100.0f;
  // Filtered-PV history for the slope: kSlopeSlots samples spaced by
  // slopeWindowMs / (kSlopeSlots - 1), so a full ring spans the window.
  static constexpr uint8_t kSlopeSlots = 13;
  uint32_t slopeTimeMs_[kSlopeSlots] = {};
  float slopePvC_[kSlopeSlots] = {};
  uint8_t slopeHead_ = 0;
  uint8_t slopeCount_ = 0;

  float lowerLimit(float referenceC) const;
  float upperLimit(float referenceC) const;
  float clampOutput(float valueC, float referenceC) const;
  void rebaseIntegral(float referenceC);
  void clearSlope();
  void pushSlope(uint32_t nowMs, float pvC);
  void setReason(const char* reason);
  void setState(ExternalBathCascadeState state, const char* reason = "");
};

const char* externalBathCascadeStateName(ExternalBathCascadeState state);
