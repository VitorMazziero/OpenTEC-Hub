#pragma once

#include <stddef.h>
#include <stdint.h>

// Command path between the Hub and the bath node r3.2. Pure C++ (no Arduino, HTTP
// or RTOS) so the host test can drive it; the firmware wraps every call in cmdMutex.
//
// Three independent slots replace the single reliable mailbox used up to 10.5.1:
//
//   stop       > operation (mode / sync_sp) > setpoint (cascade output)
//
// One payload is delivered per node push, highest priority first. Each slot keeps
// its own cmd_id, so an operation acknowledged in the middle of a setpoint sequence
// no longer hides the setpoint's completion (defect C1 of the correction plan).
// Completion of a setpoint is: its cmd_id acknowledged, then the node reporting
// done/idle. Faults are *events* (edges), not levels, so a cascade reset is not
// immediately re-latched by an old `error` still shown by the node (defect C2).

enum class BathCompletionState : uint8_t { None = 0, Pending, Done, Failed, Stopped };

struct BathNodeReport {
  uint32_t ackCmdId = 0;
  uint32_t rejCmdId = 0;
  const char* rejErr = "";
  const char* state = "idle";
  const char* error = "";
  const char* guard = "off";
};

struct BathCoordinatorView {
  bool stopPending = false;
  bool operationPending = false;
  bool setpointPending = false;
  bool anyPending = false;
  uint32_t lastIssuedId = 0;
  uint32_t lastAck = 0;
  uint32_t setpointId = 0;
  float setpointC = 0.0f;
  BathCompletionState completion = BathCompletionState::None;
  uint32_t completionAgeMs = 0;
  uint32_t lastDoneId = 0;
  uint32_t lastDoneMs = 0;
  bool hasDoneSetpoint = false;
  float lastDoneSetpointC = 0.0f;
  uint32_t lastRejectId = 0;
  char lastRejectError[32] = "";
  char lastOperationError[32] = "";
};

class BathCommandCoordinator {
 public:
  static constexpr size_t kInnerCapacity = 64;

  // Session seed: every Hub boot starts in its own cmd_id region so a node that kept
  // running never mistakes a new command for one it already applied.
  void seed(uint32_t base);
  // Drops every pending command and the completion (route change, comm off, reset).
  void clear();

  uint32_t queueSetpoint(float setpointC, uint32_t nowMs);
  // `inner` is the key/value body without braces, e.g. "\"mode\":\"auto\"".
  uint32_t queueOperation(const char* inner, uint32_t nowMs);
  // Stop preempts everything still pending and closes the completion as Stopped.
  uint32_t queueStop(uint32_t nowMs);

  // Writes {"cmd_id":N,...} for the highest-priority pending slot. False if none.
  bool nextPayload(char* out, size_t capacity);

  // Folds one validated node push. Sets a fault event when the node refuses the
  // setpoint, fails/aborts it, or when its state/guard *changes* into
  // error/aborted/suspended.
  void onReport(const BathNodeReport& report, uint32_t nowMs);

  // Consumes the pending fault event, if any.
  bool takeFault(char* reason, size_t capacity);

  // Forgets the node's last observed state/guard so a stale error is not replayed as
  // a new edge after a cascade reset.
  void rearmEdges(const char* state, const char* guard);

  BathCoordinatorView view(uint32_t nowMs) const;

 private:
  struct Slot {
    bool pending = false;
    uint32_t id = 0;
    char inner[kInnerCapacity] = "";
    uint32_t queuedMs = 0;
    uint32_t deliveries = 0;
  };

  uint32_t nextId();
  uint32_t fill(Slot& slot, const char* inner, uint32_t nowMs);
  void raiseFault(const char* reason);

  Slot stop_;
  Slot operation_;
  Slot setpoint_;
  uint32_t revision_ = 0;
  uint32_t lastAck_ = 0;
  float setpointC_ = 0.0f;

  BathCompletionState completion_ = BathCompletionState::None;
  bool completionAcked_ = false;
  uint32_t completionId_ = 0;
  uint32_t completionSinceMs_ = 0;
  uint32_t lastDoneId_ = 0;
  uint32_t lastDoneMs_ = 0;
  bool hasDoneSetpoint_ = false;
  float lastDoneSetpointC_ = 0.0f;

  uint32_t lastRejectId_ = 0;
  char lastRejectError_[32] = "";
  char lastOperationError_[32] = "";

  char prevState_[16] = "";
  char prevGuard_[16] = "";
  bool faultPending_ = false;
  char faultReason_[48] = "";
};

// r3.2 or a later r3.x: the Hub needs rej_cmd_id/sp_min/sp_max and the stop action.
bool bathNodeVersionSupported(const char* version);
