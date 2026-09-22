#!/usr/bin/env python3
"""H04 route/orchestration contract checks."""
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2] / "ESP32S3-HUB"


class BathRoutingTests(unittest.TestCase):
    def read(self, rel):
        return (ROOT / rel).read_text(encoding="utf-8")

    def test_route_switch_is_break_before_make_and_discards_same_frame_setpoint(self):
        commands = self.read("src/protocol/Commands.h")
        self.assertIn('sendSensorCommand("100B", false)', commands)
        self.assertIn("tempRouteChangedInFrame", commands)
        self.assertIn("tempSetpoint ignorado: troca de via exige novo comando", commands)
        self.assertIn("tempReferenceCommanded = false", commands)

    def test_external_route_never_marks_direct_uart_temperature_dirty(self):
        uart = self.read("src/sensor/SensorUart.h")
        runtime = self.read("src/core/Runtime.h")
        self.assertIn("tempControlRoute == TempControlRoute::UartModule", uart)
        self.assertIn("tempControlRoute == TempControlRoute::ExternalBath", runtime)
        self.assertIn("BathCommandSetpoint", self.read("src/sensor/Telemetry.h"))

    def test_latest_wins_waits_for_ack_and_done(self):
        runtime = self.read("src/core/Runtime.h")
        self.assertIn("in.actuatorBusy = link.anyPending || completionPending || nodeSequenceRunning", runtime)
        self.assertIn("postDoneCooldown", runtime)
        self.assertIn("bathCommandLatestWins = ready", runtime)
        self.assertIn("bathQueueSetpoint(updatedSnapshot.commandSetpointC)", runtime)

    def test_stop_keeps_route_and_communication(self):
        commands = self.read("src/protocol/Commands.h")
        stop = commands[commands.index("static void requestBathStop"):commands.index("static void resetBathCommandPath")]
        self.assertIn("bathQueueStop()", stop)
        self.assertIn("tempReferenceCommanded = false", stop)
        self.assertNotIn("tempControlRoute =", stop)
        self.assertNotIn("bathCommOn =", stop)
        reset = commands[commands.index('if (json.indexOf("\\"resetVariables\\"")'):commands.index('if (json.indexOf("\\"restart\\"")')]
        self.assertIn('requestBathStop("resetVariables")', reset)
        self.assertNotIn("TempControlRoute::UartModule;", reset)
        self.assertNotIn("bathCascadeConfig = ExternalBathCascade::defaults()", reset)
        self.assertIn('requestBathStop("bathAbort/tempSetpoint=0")', commands)

    def test_reset_clears_completion_and_rearms_edges(self):
        commands = self.read("src/protocol/Commands.h")
        reset = commands[commands.index('"bathCascadeReset", rawReset'):commands.index("// ============ SYSTEM COMMANDS")]
        self.assertIn("resetBathCommandPath()", reset)
        self.assertIn("bathLink.rearmEdges(nodeState, nodeGuard)", reset)

    def test_out_of_range_reference_is_rejected_not_clamped(self):
        commands = self.read("src/protocol/Commands.h")
        self.assertIn("requested >= 0.0f && requested <= 100.0f", commands)

    def test_same_value_rearms_uart_and_long_running_command_faults(self):
        uart = self.read("src/sensor/SensorUart.h")
        runtime = self.read("src/core/Runtime.h")
        self.assertIn("changed || !tempReferenceCommanded", uart)
        self.assertIn("BATH_COMMAND_COMPLETION_TIMEOUT_MS", runtime)
        self.assertIn('"bath_completion_timeout"', runtime)
        self.assertIn('"target_override"', runtime)

    def test_reset_variables_disables_original_temperature_output(self):
        commands = self.read("src/protocol/Commands.h")
        reset = commands[commands.index('if (json.indexOf("\\"resetVariables\\"")'):commands.index('if (json.indexOf("\\"restart\\"")')]
        self.assertIn("tempReference = 0.0f", reset)
        self.assertIn("flagTempDirty = true", reset)

    def test_100b_is_documented_as_existing_module_disable(self):
        runtime = self.read("src/core/Runtime.h")
        commands = self.read("src/protocol/Commands.h")
        self.assertGreaterEqual((runtime + commands).count('"100B"'), 2)


if __name__ == "__main__":
    unittest.main(verbosity=2)
