"""Reads a few telemetry lines from the ESP32-S3 without resetting it.

The two lines that matter are `dtr = False` and `rts = False`. On the ESP32-S3's
USB-serial bridge those two signals are wired to EN and BOOT, so opening the port
with either asserted reboots the board - and a probe meant to observe a running
controller would instead restart it, losing exactly the state being investigated.
PySerial asserts both by default, which is why this is worth a file rather than a
one-liner typed from memory.

The one-second wait is the board settling after the port opens.

    python tools/read_serial.py [COM5] [115200]
"""
import sys
import time

import serial

port = sys.argv[1] if len(sys.argv) > 1 else "COM5"
baud = int(sys.argv[2]) if len(sys.argv) > 2 else 115200

connection = serial.Serial(port, baud, timeout=2)
connection.dtr = False
connection.rts = False
time.sleep(1)

for _ in range(5):
    print(connection.readline())
