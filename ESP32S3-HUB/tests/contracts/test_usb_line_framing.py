#!/usr/bin/env python3
"""Modelo do leitor serial do Hub (handleUSBCommands, protocol/Commands.h).

Fixa o contrato de 2026-09-11: um comando so e processado quando o seu fim de linha
chega; o que estiver no buffer entre chamadas e acumulado. O leitor anterior tomava os
bytes disponiveis como uma linha inteira - um setpoint de ~40 B cabia num pacote USB e
funcionava, o comando de calibracao do fluxometro (~300 B) chegava em pedacos e cada
pedaco era descartado.
"""
import unittest

USB_LINE_MAX = 1024


class UsbLineReader:
    """Espelha handleUSBCommands(): buffer persistente, limite e flag de descarte."""

    def __init__(self):
        self.data = ""
        self.discarding = False
        self.processed = []
        self.warnings = 0

    def feed(self, chunk: str) -> None:
        for c in chunk:
            if c in "\r\n":
                if self.discarding:
                    self.discarding = False
                    self.data = ""
                    continue
                line = self.data.strip()
                if line:
                    self.processed.append(line)
                self.data = ""
                continue
            if self.discarding:
                continue
            if len(self.data) >= USB_LINE_MAX:
                self.warnings += 1
                self.data = ""
                self.discarding = True
                continue
            self.data += c


CALIBRATION_FRAME = (
    '{"maxFlow":50.0,"a1":-1.2E-05,"b1":0.00034,"k1":2.0,"f1":3.0,"c1":4.0,'
    '"k2":0.0,"f2":5.0,"c2":1.0}'
)


class UsbLineFramingTests(unittest.TestCase):
    def test_a_300_byte_frame_delivered_in_64_byte_usb_packets_is_one_command(self):
        frame = CALIBRATION_FRAME[:-1] + ',"pad":"' + "x" * (300 - len(CALIBRATION_FRAME) - 9) + '"}'
        line = frame + "\n"
        self.assertGreaterEqual(len(line), 300)
        reader = UsbLineReader()
        for start in range(0, len(line), 64):
            reader.feed(line[start:start + 64])
        self.assertEqual([frame], reader.processed)

    def test_two_lines_in_one_batch_are_processed_in_order(self):
        reader = UsbLineReader()
        reader.feed('{"servoPollMs":250}\n{"motorSetpoint":300}\n')
        self.assertEqual(['{"servoPollMs":250}', '{"motorSetpoint":300}'], reader.processed)

    def test_crlf_and_lf_both_end_a_line_without_producing_an_empty_command(self):
        reader = UsbLineReader()
        reader.feed('{"motorSetpoint":300}\r\n')
        reader.feed('{"motorSetpoint":400}\n')
        self.assertEqual(['{"motorSetpoint":300}', '{"motorSetpoint":400}'], reader.processed)

    def test_a_partial_line_waits_for_its_newline(self):
        reader = UsbLineReader()
        reader.feed('{"flowSetpoint":2.0,"valve_2"')
        self.assertEqual([], reader.processed)
        reader.feed(':1,"v_Flow":0}\n')
        self.assertEqual(['{"flowSetpoint":2.0,"valve_2":1,"v_Flow":0}'], reader.processed)

    def test_a_line_over_1024_bytes_is_discarded_to_its_end_and_the_next_one_is_clean(self):
        reader = UsbLineReader()
        reader.feed("x" * 1500 + "\n")
        self.assertEqual([], reader.processed)
        self.assertEqual(1, reader.warnings)
        self.assertFalse(reader.discarding)
        reader.feed('{"motorSetpoint":300}\n')
        self.assertEqual(['{"motorSetpoint":300}'], reader.processed)

    def test_the_tail_of_an_overlong_line_is_not_taken_as_a_new_command(self):
        reader = UsbLineReader()
        reader.feed("x" * 1030)
        reader.feed('{"motorSetpoint":999}')  # still the same runaway line
        reader.feed("\n")
        self.assertEqual([], reader.processed)


if __name__ == "__main__":
    unittest.main()
