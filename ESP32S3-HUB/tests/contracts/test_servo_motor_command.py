#!/usr/bin/env python3
import unittest


class ServoMotorMailboxModel:
    LEASE_MS = 3000

    def __init__(self, seed=100, enabled=True, route=1):
        self.enabled = enabled
        self.command_id = seed
        self.ack = 0
        self.rpm = 0
        self.route = route
        self.route_ack = -1
        self.motor_enabled = False
        self.pending = True
        self.deliveries = 0

    def set_motor(self, rpm, enable, route=None):
        route = self.route if route is None else route
        if not 0 <= rpm <= 1000 or (enable and rpm == 0):
            return False
        if enable and (not self.enabled or route != 1):
            return False
        rpm = rpm if enable else 0
        if (rpm, enable, route) != (self.rpm, self.motor_enabled, self.route):
            self.rpm = rpm
            self.motor_enabled = enable
            self.route = route
            self.command_id = (self.command_id + 1) & 0xFFFFFFFF or 1
            self.pending = True
            self.deliveries = 0
        return True

    def set_comm(self, enabled):
        if self.enabled == enabled:
            return
        self.enabled = enabled
        if not enabled:
            self.rpm = 0
            self.motor_enabled = False
            self.command_id = (self.command_id + 1) & 0xFFFFFFFF or 1
            self.pending = True
            self.deliveries = 0

    def take(self):
        self.deliveries += 1
        direct = self.enabled and self.motor_enabled and self.route == 1
        return {
            "motor_cmd_id": self.command_id,
            "motor_rpm": self.rpm if direct else 0,
            "motor_enable": int(direct),
            "motor_route": self.route,
            "motor_lease_ms": self.LEASE_MS,
        }

    def push_ack(self, ack, route_ack):
        self.ack = ack
        self.route_ack = route_ack
        if ack == self.command_id and route_ack == self.route:
            self.pending = False


class ServoMotorMailboxTests(unittest.TestCase):
    def test_boot_always_publishes_revisioned_stop(self):
        box = ServoMotorMailboxModel(seed=0x12345678)
        self.assertEqual(
            {"motor_cmd_id": 0x12345678, "motor_rpm": 0,
             "motor_enable": 0, "motor_route": 1, "motor_lease_ms": 3000},
            box.take(),
        )
        self.assertTrue(box.pending)

    def test_latest_setpoint_replaces_previous_without_fifo(self):
        box = ServoMotorMailboxModel()
        box.set_motor(400, True)
        first_id = box.command_id
        box.set_motor(1000, True)
        command = box.take()
        self.assertGreater(command["motor_cmd_id"], first_id)
        self.assertEqual(1000, command["motor_rpm"])

    def test_ack_only_clears_matching_revision(self):
        box = ServoMotorMailboxModel()
        box.set_motor(500, True)
        box.push_ack(box.command_id - 1, 1)
        self.assertTrue(box.pending)
        box.push_ack(box.command_id, 0)
        self.assertTrue(box.pending)
        box.push_ack(box.command_id, 1)
        self.assertFalse(box.pending)

    def test_command_remains_available_after_ack_as_lease_heartbeat(self):
        box = ServoMotorMailboxModel()
        box.set_motor(750, True)
        expected = box.take()
        box.push_ack(box.command_id, 1)
        self.assertEqual(expected, box.take())
        self.assertEqual(2, box.deliveries)

    def test_disabling_route_revisions_an_immediate_stop(self):
        box = ServoMotorMailboxModel()
        box.set_motor(600, True)
        running_id = box.command_id
        box.set_comm(False)
        command = box.take()
        self.assertGreater(command["motor_cmd_id"], running_id)
        self.assertEqual((0, 0), (command["motor_rpm"], command["motor_enable"]))
        self.assertFalse(box.set_motor(600, True))

    def test_route_change_is_revisioned_and_forces_zero(self):
        box = ServoMotorMailboxModel()
        box.set_motor(600, True)
        running_id = box.command_id
        box.set_motor(0, False, route=0)
        command = box.take()
        self.assertGreater(command["motor_cmd_id"], running_id)
        self.assertEqual((0, 0, 0),
                         (command["motor_rpm"], command["motor_enable"],
                          command["motor_route"]))
        box.push_ack(box.command_id, 1)
        self.assertTrue(box.pending)
        box.push_ack(box.command_id, 0)
        self.assertFalse(box.pending)


if __name__ == "__main__":
    unittest.main(verbosity=2)
