#include "BathCommandCoordinator.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

namespace {
bool same(const char* a, const char* b) {
  return strcmp(a ? a : "", b ? b : "") == 0;
}

void copyText(char* destination, size_t capacity, const char* source) {
  snprintf(destination, capacity, "%s", source ? source : "");
}
}  // namespace

void BathCommandCoordinator::seed(uint32_t base) {
  clear();
  revision_ = base;
  lastAck_ = 0;
}

void BathCommandCoordinator::clear() {
  stop_.pending = false;
  operation_.pending = false;
  setpoint_.pending = false;
  completion_ = BathCompletionState::None;
  completionAcked_ = false;
  faultPending_ = false;
  faultReason_[0] = '\0';
}

uint32_t BathCommandCoordinator::nextId() {
  ++revision_;
  if (revision_ == 0) revision_ = 1;  // 0 means "never acknowledged" on the wire
  return revision_;
}

uint32_t BathCommandCoordinator::fill(Slot& slot, const char* inner, uint32_t nowMs) {
  slot.pending = true;
  slot.id = nextId();
  copyText(slot.inner, sizeof(slot.inner), inner);
  slot.queuedMs = nowMs;
  slot.deliveries = 0;
  return slot.id;
}

uint32_t BathCommandCoordinator::queueSetpoint(float setpointC, uint32_t nowMs) {
  char inner[kInnerCapacity];
  snprintf(inner, sizeof(inner), "\"setpoint\":%.1f", static_cast<double>(setpointC));
  const uint32_t id = fill(setpoint_, inner, nowMs);
  setpointC_ = setpointC;
  completion_ = BathCompletionState::Pending;
  completionAcked_ = false;
  completionId_ = id;
  completionSinceMs_ = nowMs;
  return id;
}

uint32_t BathCommandCoordinator::queueOperation(const char* inner, uint32_t nowMs) {
  lastOperationError_[0] = '\0';
  return fill(operation_, inner, nowMs);
}

uint32_t BathCommandCoordinator::queueStop(uint32_t nowMs) {
  operation_.pending = false;
  setpoint_.pending = false;
  if (completion_ == BathCompletionState::Pending) completion_ = BathCompletionState::Stopped;
  completionAcked_ = false;
  return fill(stop_, "\"stop\":1", nowMs);
}

bool BathCommandCoordinator::nextPayload(char* out, size_t capacity) {
  Slot* slot = stop_.pending ? &stop_
             : operation_.pending ? &operation_
             : setpoint_.pending ? &setpoint_
             : nullptr;
  if (!slot || !out || capacity == 0) return false;
  snprintf(out, capacity, "{\"cmd_id\":%lu,%s}", static_cast<unsigned long>(slot->id), slot->inner);
  slot->deliveries++;
  return true;
}

void BathCommandCoordinator::raiseFault(const char* reason) {
  if (faultPending_) return;  // keep the first cause
  faultPending_ = true;
  copyText(faultReason_, sizeof(faultReason_), reason);
}

void BathCommandCoordinator::onReport(const BathNodeReport& r, uint32_t nowMs) {
  const char* state = r.state ? r.state : "";
  const char* guard = r.guard ? r.guard : "";
  lastAck_ = r.ackCmdId;

  if (r.ackCmdId != 0) {
    if (stop_.pending && r.ackCmdId == stop_.id) stop_.pending = false;
    if (operation_.pending && r.ackCmdId == operation_.id) operation_.pending = false;
    if (setpoint_.pending && r.ackCmdId == setpoint_.id) {
      setpoint_.pending = false;
      if (completion_ == BathCompletionState::Pending && completionId_ == r.ackCmdId) {
        completionAcked_ = true;
      }
    }
  }

  if (r.rejCmdId != 0) {
    lastRejectId_ = r.rejCmdId;
    copyText(lastRejectError_, sizeof(lastRejectError_), r.rejErr);
    // `busy` is transient: the slot stays pending and is delivered again.
    if (!same(r.rejErr, "busy")) {
      if (setpoint_.pending && r.rejCmdId == setpoint_.id) {
        setpoint_.pending = false;
        if (completionId_ == r.rejCmdId) completion_ = BathCompletionState::Failed;
        char reason[48];
        snprintf(reason, sizeof(reason), "node_rejected:%s", r.rejErr ? r.rejErr : "");
        raiseFault(reason);
      } else if (operation_.pending && r.rejCmdId == operation_.id) {
        operation_.pending = false;
        copyText(lastOperationError_, sizeof(lastOperationError_), r.rejErr);
      }
    }
  }

  if (completion_ == BathCompletionState::Pending && completionAcked_) {
    if (same(state, "done") || same(state, "idle")) {
      completion_ = BathCompletionState::Done;
      lastDoneId_ = completionId_;
      lastDoneMs_ = nowMs;
      hasDoneSetpoint_ = true;
      lastDoneSetpointC_ = setpointC_;
    } else if (same(state, "error")) {
      completion_ = BathCompletionState::Failed;
      char reason[48];
      snprintf(reason, sizeof(reason), "bath_error:%s", r.error ? r.error : "");
      raiseFault(reason);
    } else if (same(state, "aborted")) {
      completion_ = BathCompletionState::Failed;
      raiseFault("bath_aborted");
    }
  }

  // Edges only: a state the node was already in when the Hub first heard it (or when
  // the cascade was reset) is not a new failure.
  if (prevState_[0] != '\0' && !same(prevState_, state)) {
    if (same(state, "error")) {
      char reason[48];
      snprintf(reason, sizeof(reason), "bath_error:%s", r.error ? r.error : "");
      raiseFault(reason);
    } else if (same(state, "aborted")) {
      raiseFault("bath_aborted");
    }
  }
  if (prevGuard_[0] != '\0' && !same(prevGuard_, guard) && same(guard, "suspended")) {
    raiseFault("guard_suspended");
  }
  copyText(prevState_, sizeof(prevState_), state);
  copyText(prevGuard_, sizeof(prevGuard_), guard);
}

bool BathCommandCoordinator::takeFault(char* reason, size_t capacity) {
  if (!faultPending_) return false;
  faultPending_ = false;
  if (reason && capacity) copyText(reason, capacity, faultReason_);
  faultReason_[0] = '\0';
  return true;
}

void BathCommandCoordinator::rearmEdges(const char* state, const char* guard) {
  copyText(prevState_, sizeof(prevState_), state);
  copyText(prevGuard_, sizeof(prevGuard_), guard);
  faultPending_ = false;
  faultReason_[0] = '\0';
}

BathCoordinatorView BathCommandCoordinator::view(uint32_t nowMs) const {
  BathCoordinatorView v;
  v.stopPending = stop_.pending;
  v.operationPending = operation_.pending;
  v.setpointPending = setpoint_.pending;
  v.anyPending = stop_.pending || operation_.pending || setpoint_.pending;
  v.lastIssuedId = revision_;
  v.lastAck = lastAck_;
  v.setpointId = completionId_;
  v.setpointC = setpointC_;
  v.completion = completion_;
  v.completionAgeMs = completion_ == BathCompletionState::Pending ? nowMs - completionSinceMs_ : 0;
  v.lastDoneId = lastDoneId_;
  v.lastDoneMs = lastDoneMs_;
  v.hasDoneSetpoint = hasDoneSetpoint_;
  v.lastDoneSetpointC = lastDoneSetpointC_;
  v.lastRejectId = lastRejectId_;
  copyText(v.lastRejectError, sizeof(v.lastRejectError), lastRejectError_);
  copyText(v.lastOperationError, sizeof(v.lastOperationError), lastOperationError_);
  return v;
}

bool bathNodeVersionSupported(const char* version) {
  if (!version || version[0] != 'r') return false;
  char* end = nullptr;
  const long major = strtol(version + 1, &end, 10);
  if (end == version + 1 || major != 3) return false;
  if (*end != '.') return false;  // plain "r3" predates the r3.2 contract
  const char* minorStart = end + 1;
  const long minor = strtol(minorStart, &end, 10);
  if (end == minorStart) return false;
  return minor >= 2;
}
