// Host test of the Hub <-> bath r3.2 command path (BathCommandCoordinator).
#define _CRT_SECURE_NO_WARNINGS
// Reproduces the defects C1-C3 of IMPLEMENTATION_PLAN_BANHO_CORRECOES.md and the
// stop semantics chosen in decision D-2/D-4.
#include <cstdio>
#include <cstring>
#include <string>

#include "../../ESP32S3-HUB/src/control/BathCommandCoordinator.h"

namespace {
int failures = 0;

#define CHECK(condition, message) \
  do { \
    if (!(condition)) { \
      std::printf("FAIL: %s\n", message); \
      ++failures; \
    } \
  } while (0)

// Minimal node model: applies one delivered payload per push, like HubLink.
struct FakeNode {
  uint32_t ack = 0;
  uint32_t rejId = 0;
  std::string rejErr;
  std::string state = "idle";
  std::string error;
  std::string guard = "watch";
  std::string refuseWith;  // non-empty: refuse the next setpoint with this error

  BathNodeReport report() const {
    BathNodeReport r;
    r.ackCmdId = ack;
    r.rejCmdId = rejId;
    r.rejErr = rejErr.c_str();
    r.state = state.c_str();
    r.error = error.c_str();
    r.guard = guard.c_str();
    return r;
  }

  void apply(const char* payload) {
    unsigned long id = 0;
    std::sscanf(payload, "{\"cmd_id\":%lu", &id);
    if (id == ack) return;  // duplicate
    if (std::strstr(payload, "\"setpoint\"")) {
      if (!refuseWith.empty()) {
        rejId = static_cast<uint32_t>(id);
        rejErr = refuseWith;
        return;
      }
      state = "running";
    } else if (std::strstr(payload, "\"stop\"")) {
      if (state == "running") state = "aborted";
    }
    ack = static_cast<uint32_t>(id);
  }
};

// One node push: report, then take the piggy-backed payload.
void push(BathCommandCoordinator& link, FakeNode& node, uint32_t now) {
  link.onReport(node.report(), now);
  char payload[128];
  if (link.nextPayload(payload, sizeof(payload))) node.apply(payload);
}
}  // namespace

int main() {
  // --- C1: an operation acknowledged during a sequence must not hide completion.
  {
    BathCommandCoordinator link;
    FakeNode node;
    link.seed(5000);
    const uint32_t sp = link.queueSetpoint(31.2f, 0);
    push(link, node, 1000);                  // delivers setpoint
    push(link, node, 3000);                  // ack(sp), state running
    CHECK(link.view(3000).completion == BathCompletionState::Pending, "setpoint must be running");
    link.queueOperation("\"mode\":\"auto\"", 3500);
    push(link, node, 5000);                  // delivers operation
    push(link, node, 7000);                  // ack(op)
    CHECK(node.ack != sp, "node must have acknowledged the operation");
    node.state = "done";
    push(link, node, 9000);
    const BathCoordinatorView v = link.view(9000);
    CHECK(v.completion == BathCompletionState::Done, "C1: done after an operation ACK must complete the setpoint");
    CHECK(v.lastDoneId == sp, "C1: completion must be bound to the setpoint id");
    char reason[48];
    CHECK(!link.takeFault(reason, sizeof(reason)), "C1: no fault expected");
  }

  // --- Operation queued before the setpoint ACK is delivered first; setpoint survives.
  {
    BathCommandCoordinator link;
    FakeNode node;
    link.seed(1);
    const uint32_t sp = link.queueSetpoint(30.0f, 0);
    link.queueOperation("\"sync_sp\":30.00", 0);
    char payload[128];
    CHECK(link.nextPayload(payload, sizeof(payload)) && std::strstr(payload, "sync_sp"),
          "operation has priority over setpoint");
    node.apply(payload);
    push(link, node, 2000);  // ack(op), delivers setpoint
    push(link, node, 4000);  // ack(sp)
    CHECK(node.ack == sp, "setpoint must still be delivered after the operation");
    node.state = "done";
    push(link, node, 6000);
    CHECK(link.view(6000).completion == BathCompletionState::Done, "setpoint must complete");
  }

  // --- D-2/D-4: stop preempts everything and closes the completion.
  {
    BathCommandCoordinator link;
    FakeNode node;
    link.seed(100);
    link.queueSetpoint(35.0f, 0);
    push(link, node, 1000);
    push(link, node, 3000);  // running
    link.queueOperation("\"mode\":\"auto\"", 3500);
    const uint32_t stop = link.queueStop(3600);
    BathCoordinatorView v = link.view(3600);
    CHECK(v.stopPending && !v.operationPending && !v.setpointPending, "stop must drop other slots");
    CHECK(v.completion == BathCompletionState::Stopped, "stop must close the completion as stopped");
    char payload[128];
    CHECK(link.nextPayload(payload, sizeof(payload)) && std::strstr(payload, "\"stop\":1"),
          "stop payload first");
    node.apply(payload);
    push(link, node, 5000);
    v = link.view(5000);
    CHECK(node.ack == stop && !v.anyPending, "stop must be acknowledged and cleared");
    char reason[48];
    // The node's aborted state after the stop is an edge; the runtime discards it
    // because the cascade is already off. Here we only check it is reported once.
    const bool edge = link.takeFault(reason, sizeof(reason));
    CHECK(!edge || std::strcmp(reason, "bath_aborted") == 0, "only an aborted edge may follow a stop");
  }

  // --- C3: a non-busy refusal fails immediately with its cause.
  {
    BathCommandCoordinator link;
    FakeNode node;
    link.seed(200);
    node.refuseWith = "range";
    link.queueSetpoint(95.0f, 0);
    push(link, node, 1000);  // delivered, refused
    push(link, node, 3000);  // rejection reported
    const BathCoordinatorView v = link.view(3000);
    CHECK(!v.setpointPending, "C3: refused setpoint must not be redelivered");
    CHECK(v.completion == BathCompletionState::Failed, "C3: refused setpoint must fail");
    char reason[48] = "";
    CHECK(link.takeFault(reason, sizeof(reason)) && std::strcmp(reason, "node_rejected:range") == 0,
          "C3: fault must carry the node's reason");
  }

  // --- busy refusals are transient: the command is delivered again.
  {
    BathCommandCoordinator link;
    FakeNode node;
    link.seed(300);
    node.refuseWith = "busy";
    const uint32_t sp = link.queueSetpoint(31.0f, 0);
    push(link, node, 1000);
    push(link, node, 3000);
    CHECK(link.view(3000).setpointPending, "busy refusal must keep the command pending");
    node.refuseWith.clear();
    push(link, node, 5000);  // redelivered and accepted
    push(link, node, 7000);
    CHECK(node.ack == sp && !link.view(7000).setpointPending, "busy command must be accepted later");
  }

  // --- C2: faults are edges; rearmEdges forgets a state already present.
  {
    BathCommandCoordinator link;
    FakeNode node;
    link.seed(400);
    node.state = "error";
    node.error = "sp_mismatch";
    push(link, node, 1000);  // first report: already in error, no edge
    char reason[48];
    CHECK(!link.takeFault(reason, sizeof(reason)), "a pre-existing error is not a new fault");
    node.state = "done";
    push(link, node, 2000);
    node.state = "error";
    push(link, node, 3000);
    CHECK(link.takeFault(reason, sizeof(reason)) && std::strcmp(reason, "bath_error:sp_mismatch") == 0,
          "a transition into error is a fault");
    link.rearmEdges("error", "watch");
    push(link, node, 4000);
    CHECK(!link.takeFault(reason, sizeof(reason)), "C2: after reset the same error must not re-latch");
    node.guard = "suspended";
    push(link, node, 5000);
    CHECK(link.takeFault(reason, sizeof(reason)) && std::strcmp(reason, "guard_suspended") == 0,
          "guard entering suspended is a fault");
  }

  // --- clear() (route change / reset) drops every pending item.
  {
    BathCommandCoordinator link;
    link.seed(500);
    link.queueSetpoint(30.0f, 0);
    link.queueOperation("\"mode\":\"auto\"", 0);
    link.clear();
    const BathCoordinatorView v = link.view(0);
    CHECK(!v.anyPending && v.completion == BathCompletionState::None, "clear must drop everything");
  }

  // --- cmd_id never becomes 0 across a wrap.
  {
    BathCommandCoordinator link;
    link.seed(0xFFFFFFFFu);
    CHECK(link.queueOperation("\"mode\":\"auto\"", 0) == 1, "id 0 is reserved");
  }

  // --- Minimum node version for the r3.2 contract.
  CHECK(bathNodeVersionSupported("r3.2"), "r3.2 supported");
  CHECK(bathNodeVersionSupported("r3.10"), "r3.10 supported");
  CHECK(!bathNodeVersionSupported("r3.1"), "r3.1 lacks rej_cmd_id/stop");
  CHECK(!bathNodeVersionSupported("r3"), "plain r3 predates the contract");
  CHECK(!bathNodeVersionSupported("r4.0"), "a new major needs review");
  CHECK(!bathNodeVersionSupported(""), "empty version rejected");

  std::printf("%s (%d failure(s))\n", failures ? "FAILED" : "ALL PASSED", failures);
  return failures ? 1 : 0;
}
