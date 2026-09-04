#!/usr/bin/env python3
import unittest
from collections import deque


class ServoQueueModel:
    CAPACITY = 8
    MIN_POLL = 250
    MAX_POLL = 10000

    def __init__(self):
        self.items = deque()

    def enqueue(self, reset=False, poll=None):
        if not reset and poll is None:
            return "invalid"
        if poll is not None and not self.MIN_POLL <= poll <= self.MAX_POLL:
            return "invalid"
        if not reset and poll is not None:
            for index in range(len(self.items) - 1, -1, -1):
                item = self.items[index]
                if not item["reset"] and item["poll"] is not None:
                    item["poll"] = poll
                    return "coalesced"
        if len(self.items) == self.CAPACITY:
            return "full"
        self.items.append({"reset": reset, "poll": poll})
        return "queued"

    def take(self):
        return self.items.popleft() if self.items else None


class ServoQueueTests(unittest.TestCase):
    def test_poll_limits(self):
        queue = ServoQueueModel()
        self.assertEqual("invalid", queue.enqueue(poll=249))
        self.assertEqual("queued", queue.enqueue(poll=250))
        self.assertEqual("coalesced", queue.enqueue(poll=10000))
        self.assertEqual("invalid", queue.enqueue(poll=10001))

    def test_poll_only_coalesces(self):
        queue = ServoQueueModel()
        queue.enqueue(poll=500)
        self.assertEqual("coalesced", queue.enqueue(poll=1000))
        self.assertEqual(1, len(queue.items))
        self.assertEqual(1000, queue.take()["poll"])

    def test_reset_is_fifo_and_never_coalesced(self):
        queue = ServoQueueModel()
        queue.enqueue(reset=True)
        queue.enqueue(reset=True)
        queue.enqueue(reset=True, poll=750)
        self.assertEqual(3, len(queue.items))
        self.assertTrue(queue.take()["reset"])
        self.assertTrue(queue.take()["reset"])
        self.assertEqual(750, queue.take()["poll"])

    def test_full_rejects_without_overwrite(self):
        queue = ServoQueueModel()
        for _ in range(queue.CAPACITY):
            self.assertEqual("queued", queue.enqueue(reset=True))
        before = list(queue.items)
        self.assertEqual("full", queue.enqueue(reset=True))
        self.assertEqual(before, list(queue.items))

    def test_false_reset_is_no_command(self):
        queue = ServoQueueModel()
        self.assertEqual("invalid", queue.enqueue(reset=False))
        self.assertIsNone(queue.take())


if __name__ == "__main__":
    unittest.main(verbosity=2)

