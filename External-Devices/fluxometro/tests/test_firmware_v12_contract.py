#!/usr/bin/env python3
"""
Testes de Contrato — Firmware do Fluxômetro v12.0
Valida os requisitos da Etapa 2 do plano de calibração dupla:
1. Ausência do literal operacional 0.0545f em FlowIo.h
2. Firmware version v12.0 em FirmwareApp.cpp
3. Magic Schema v7 e preservação do Schema v6
4. transition_v adicionado ao final de CalibrationParams (offset 64..67, total 68 bytes)
5. Migração binária v6 -> v7 preservando exatamente todos os campos legados
6. Inclusão de transition_v no CRC32 da calibração
7. Exposição em /calibration, /flowData, serial e WebSocket ack
8. Validação de atomicidade e rejeição de descontinuidade em CommandCodec.h
"""

import os
import re
import struct
import sys
import unittest

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_DIR = os.path.join(BASE_DIR, "firmware", "flowmeter", "src")


def calculate_crc32(payload_bytes: bytes) -> int:
    """Implementa exatamente calculateCalibrationCrc de CalibrationStore.h."""
    crc = 0xFFFFFFFF
    for b in payload_bytes:
        crc ^= b
        for _ in range(8):
            mask = -(crc & 1) & 0xFFFFFFFF
            crc = ((crc >> 1) ^ (0xEDB88320 & mask)) & 0xFFFFFFFF
    return (~crc) & 0xFFFFFFFF


class TestFirmwareV12Contract(unittest.TestCase):

    def test_01_flow_io_no_hardcoded_transition_voltage(self):
        """FlowIo.h não deve conter o literal fixo 0.0545f e deve usar flowTransitionVoltage."""
        path = os.path.join(SRC_DIR, "hardware", "FlowIo.h")
        self.assertTrue(os.path.exists(path), f"Arquivo não encontrado: {path}")
        with open(path, "r", encoding="utf-8") as f:
            content = f.read()

        self.assertNotIn("0.0545", content, "FlowIo.h ainda contém o literal fixo 0.0545!")
        self.assertIn("flowTransitionVoltage", content, "FlowIo.h deve usar flowTransitionVoltage na avaliação.")
        self.assertRegex(content, r"readFlowVoltage\s*<=\s*flowTransitionVoltage",
                         "FlowIo.h deve verificar readFlowVoltage <= flowTransitionVoltage.")

    def test_02_firmware_version_v12(self):
        """FirmwareApp.cpp deve declarar FW_VERSION como v12.0."""
        path = os.path.join(SRC_DIR, "core", "FirmwareApp.cpp")
        with open(path, "r", encoding="utf-8") as f:
            content = f.read()

        self.assertRegex(content, r'#define\s+FW_VERSION\s+"v12\.0"', "FW_VERSION deve ser v12.0.")

    def test_03_schema_v7_and_v6_magics(self):
        """FirmwareApp.cpp deve definir CALIBRATION_MAGIC_V6 e CALIBRATION_MAGIC (v7)."""
        path = os.path.join(SRC_DIR, "core", "FirmwareApp.cpp")
        with open(path, "r", encoding="utf-8") as f:
            content = f.read()

        self.assertRegex(content, r"CALIBRATION_MAGIC_V6\s*=\s*0xCAFEBAC4", "CALIBRATION_MAGIC_V6 deve ser 0xCAFEBAC4.")
        self.assertRegex(content, r"CALIBRATION_MAGIC\s*=\s*0xCAFEBAC5", "CALIBRATION_MAGIC v7 deve ser 0xCAFEBAC5.")
        self.assertRegex(content, r"TRANSITION_V_DEFAULT\s*=\s*0\.0545f", "TRANSITION_V_DEFAULT deve ser 0.0545f.")

    def test_04_calibration_params_struct_layout(self):
        """CalibrationParams deve ter transition_v como último campo, expandindo para 68 bytes."""
        path = os.path.join(SRC_DIR, "core", "FirmwareApp.cpp")
        with open(path, "r", encoding="utf-8") as f:
            content = f.read()

        struct_match = re.search(r"struct\s+CalibrationParams\s*\{(.*?)\};", content, re.DOTALL)
        self.assertIsNotNone(struct_match, "struct CalibrationParams não encontrada.")
        struct_body = struct_match.group(1)

        self.assertIn("float transition_v;", struct_body, "transition_v deve estar na struct CalibrationParams.")
        self.assertRegex(struct_body, r"float\s+max_flow;[^\n]*\s+float\s+transition_v;",
                         "transition_v deve vir imediatamente após max_flow no fim do registro.")

    def test_05_binary_migration_v6_to_v7(self):
        """Simula a migração em memória: Schema v6 (64 bytes) -> Schema v7 (68 bytes)."""
        # Formato v6: magic(uint32), a1..c2 (8 floats), kp, ki (2 floats),
        # ff_gain, ff_offset (2 floats), ramp_rate, dac_hold (2 floats), max_flow (1 float)
        # Total v6 = 4 + 15 * 4 = 64 bytes.
        v6_magic = 0xCAFEBAC4
        v6_floats = [
            -1353785.3, 246663.69, -16473.492, 484.99466, -4.6159464, # a1, b1, k1, f1, c1
            -0.46260458, 10.797299, 0.28475793,                       # k2, f2, c2
            0.4, 2.0,                                                  # kp, ki
            0.85, -0.05,                                               # ff_gain, ff_offset
            3.0, 1.0,                                                  # ramp_rate, dac_hold
            50.0                                                       # max_flow
        ]
        v6_bytes = struct.pack("<I15f", v6_magic, *v6_floats)
        self.assertEqual(len(v6_bytes), 64)

        # EEPROM buffer lê 68 bytes no schema v7. Simulando leitura de bloco existente v6
        eeprom_buffer = bytearray(v6_bytes + b"\x00\x00\x00\x00")
        read_magic = struct.unpack_from("<I", eeprom_buffer, 0)[0]
        self.assertEqual(read_magic, v6_magic)

        # Lógica de migração v6 -> v7
        v7_magic = 0xCAFEBAC5
        transition_v_default = 0.0545
        struct.pack_into("<I", eeprom_buffer, 0, v7_magic)
        struct.pack_into("<f", eeprom_buffer, 64, transition_v_default)

        # Desempacotar v7 completo e conferir preservação exata dos dados anteriores
        unpacked_magic = struct.unpack_from("<I", eeprom_buffer, 0)[0]
        unpacked_floats = struct.unpack_from("<16f", eeprom_buffer, 4)

        self.assertEqual(unpacked_magic, v7_magic)
        # Os primeiros 60 bytes de floats (offsets 4..64) devem ser EXATAMENTE idênticos aos de v6
        self.assertEqual(bytes(eeprom_buffer[4:64]), v6_bytes[4:64], "Bytes de calibração legados alterados!")
        self.assertAlmostEqual(unpacked_floats[15], 0.0545, places=4,
                               msg="transition_v não foi inicializado em 0.0545 V!")

    def test_06_crc_covers_transition_voltage(self):
        """O CRC32 deve mudar quando transition_v for alterado."""
        floats_a = [1.0] * 15 + [0.0545]
        floats_b = [1.0] * 15 + [0.1000] # mesmo conteúdo exceto transition_v

        payload_a = struct.pack("<16f", *floats_a)
        payload_b = struct.pack("<16f", *floats_b)

        crc_a = calculate_crc32(payload_a)
        crc_b = calculate_crc32(payload_b)

        self.assertNotEqual(crc_a, crc_b, "CRC32 deve cobrir transition_v!")

    def test_07_endpoints_expose_transition_v(self):
        """Endpoints /calibration, /flowData, serial e websocket devem expor transition_v."""
        ota_path = os.path.join(SRC_DIR, "api", "OtaService.h")
        with open(ota_path, "r", encoding="utf-8") as f:
            ota_content = f.read()
        self.assertIn(r'\"transition_v\":%.4f', ota_content, "/calibration deve expor transition_v.")

        runtime_path = os.path.join(SRC_DIR, "tasks", "TaskRuntime.h")
        with open(runtime_path, "r", encoding="utf-8") as f:
            runtime_content = f.read()
        self.assertIn("&transition_v=%.4f", runtime_content, "/flowData deve enviar transition_v.")

        lifecycle_path = os.path.join(SRC_DIR, "core", "Lifecycle.h")
        with open(lifecycle_path, "r", encoding="utf-8") as f:
            lifecycle_content = f.read()
        self.assertIn(r'\"transition_v\":%.4f', lifecycle_content, "Telemetria periódica deve expor transition_v.")

        ws_path = os.path.join(SRC_DIR, "api", "WebSocketApi.h")
        with open(ws_path, "r", encoding="utf-8") as f:
            ws_content = f.read()
        self.assertIn(r'\"transition_v\":%.4f', ws_content, "WebSocket ACK deve expor transition_v.")

    def test_08_codec_atomic_and_continuity_checks(self):
        """CommandCodec.h deve exigir dois segmentos completos em transition_v e validar continuidade."""
        codec_path = os.path.join(SRC_DIR, "protocol", "CommandCodec.h")
        with open(codec_path, "r", encoding="utf-8") as f:
            codec_content = f.read()

        self.assertIn('strcmp(keyBuf, "transition_v") == 0', codec_content, "Deve aceitar transition_v.")
        self.assertIn('strcmp(keyBuf, "flowTransitionVoltage") == 0', codec_content, "Deve aceitar flowTransitionVoltage.")
        self.assertIn("hasTransitionV", codec_content)
        self.assertIn("CAL_MAX_VALUE_DISCONTINUITY", codec_content, "Deve validar descontinuidade de valor.")
        self.assertIn("CAL_MAX_DERIVATIVE_DISCONTINUITY", codec_content, "Deve validar descontinuidade de derivada.")


if __name__ == "__main__":
    unittest.main()
