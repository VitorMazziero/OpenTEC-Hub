#!/usr/bin/env python3
import unittest
from collections import deque


class HttpFrameModel:
    MAX_PAYLOAD = 2048
    CAPACITY = 8

    def __init__(self):
        self.frames = deque()

    def receive(self, chunks, declared_total=None):
        total = sum(len(chunk) for chunk in chunks) if declared_total is None else declared_total
        if total > self.MAX_PAYLOAD:
            return 413
        body = "".join(chunks)
        start, end = body.find("{"), body.rfind("}")
        if start < 0 or end <= start:
            return 400
        if len(self.frames) == self.CAPACITY:
            return 503
        self.frames.append(body[start : end + 1])
        return 200


class HttpFrameTests(unittest.TestCase):
    def test_chunked_frame_is_reassembled_once(self):
        model = HttpFrameModel()
        self.assertEqual(200, model.receive(["prefix{\"servo", "Comm\":1}suffix"]))
        self.assertEqual(['{"servoComm":1}'], list(model.frames))

    def test_invalid_and_oversized_frames_are_rejected(self):
        model = HttpFrameModel()
        self.assertEqual(400, model.receive(["not-json"]))
        self.assertEqual(413, model.receive(["{}"], declared_total=2049))
        self.assertFalse(model.frames)

    def test_full_queue_rejects_before_mutation(self):
        model = HttpFrameModel()
        for index in range(model.CAPACITY):
            self.assertEqual(200, model.receive([f'{{"n":{index}}}']))
        before = list(model.frames)
        self.assertEqual(503, model.receive(['{"n":99}']))
        self.assertEqual(before, list(model.frames))


if __name__ == "__main__":
    unittest.main(verbosity=2)

