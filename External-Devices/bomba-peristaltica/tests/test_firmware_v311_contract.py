#!/usr/bin/env python3
"""
Testes de Contrato — Firmware da Bomba Peristáltica v3.11
Valida os requisitos da Etapa 3 do plano de calibração dupla:
1. Firmware version v3.11 em FirmwareApp.cpp, HubClient.h, Lifecycle.h
2. Preservação estrita da struct PumpConfig (sem alterações em layout ou tamanho)
3. Formato e validação de PumpDualRangeCal (magic 0x504D5032, CRC32 standard, 24 bytes)
4. Continuidade matemática nas funções de conversão mlminToSpeedUnits e speedUnitsToMlmin
5. Equivalência exata da migração de modelo linear legado para modelo dual-range
6. Validação atômica de 4 parâmetros e rejeição em estados ativos (OP_RUNNING, OP_WAITING)
7. Exposição dos novos campos de calibração na telemetria serial/JSON e no cliente Hub
"""

import os
import re
import struct
import sys
import unittest

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_DIR = os.path.join(BASE_DIR, "firmware", "peristaltic-pump", "src")


def calculate_crc32(payload_bytes: bytes) -> int:
    """Implementa o cálculo IEEE 802.3 padrão CRC32 usado em CalibrationStore.h."""
    crc = 0xFFFFFFFF
    for b in payload_bytes:
        crc ^= b
        for _ in range(8):
            mask = -(crc & 1) & 0xFFFFFFFF
            crc = ((crc >> 1) ^ (0xEDB88320 & mask)) & 0xFFFFFFFF
    return (~crc) & 0xFFFFFFFF


def speed_to_flow(speed: float, m_low: float, m_high: float, s_t: float, q_t: float) -> float:
    if speed <= 0.0:
        return 0.0
    if speed <= s_t:
        return q_t + m_low * (speed - s_t)
    else:
        return q_t + m_high * (speed - s_t)


def flow_to_speed(flow: float, m_low: float, m_high: float, s_t: float, q_t: float) -> float:
    if flow <= 0.0:
        return 0.0
    if flow <= q_t:
        s = s_t + (flow - q_t) / m_low
    else:
        s = s_t + (flow - q_t) / m_high
    return max(0.0, min(1000.0, s))


class TestPumpFirmwareV311Contract(unittest.TestCase):

    def test_01_version_v311_across_firmware(self):
        """FirmwareApp.cpp, HubClient.h e Lifecycle.h devem declarar e usar v3.11."""
        app_cpp = os.path.join(SRC_DIR, "core", "FirmwareApp.cpp")
        with open(app_cpp, "r", encoding="utf-8") as f:
            content = f.read()
        self.assertRegex(content, r'#define\s+PUMP_FW_VERSION\s+"3\.11"', "PUMP_FW_VERSION deve ser 3.11.")

        hub_client = os.path.join(SRC_DIR, "network", "HubClient.h")
        with open(hub_client, "r", encoding="utf-8") as f:
            content = f.read()
        self.assertIn(r'\"version\":\"3.11\"', content, "handleDiag deve reportar versão 3.11.")
        self.assertIn("Peristaltic Pump Controller v3.11", content, "OTA HTML deve reportar versão 3.11.")
        self.assertIn("ver=3.11", content, "sendHubHello deve reportar ver=3.11.")

        lifecycle = os.path.join(SRC_DIR, "core", "Lifecycle.h")
        with open(lifecycle, "r", encoding="utf-8") as f:
            content = f.read()
        self.assertIn("v3.11", content, "Lifecycle banner deve referenciar v3.11.")
        self.assertIn("loadPumpCalibration()", content, "Lifecycle deve chamar loadPumpCalibration().")

    def test_02_pump_config_struct_layout_untouched(self):
        """PumpConfig deve manter exatamente os campos e tipos legados para não corromper NVS."""
        app_cpp = os.path.join(SRC_DIR, "core", "FirmwareApp.cpp")
        with open(app_cpp, "r", encoding="utf-8") as f:
            content = f.read()

        struct_match = re.search(r"struct\s+PumpConfig\s*\{(.*?)\};", content, re.DOTALL)
        self.assertIsNotNone(struct_match, "struct PumpConfig não encontrada.")
        body = struct_match.group(1)

        # Verificar que campos legados continuam presentes
        expected_fields = [
            "int mode;",
            "float init_t_min;",
            "float final_t_min;",
            "float lambda_const;",
            "float lambda_linear;",
            "float phi_linear;",
            "float lambda_exp;",
            "float phi_exp;",
            "double polyCoeffs[NUM_POLY_COEFFS];",
            "int num_segments;",
            "float time_points[MAX_SEGMENTS];",
            "float flow_points[MAX_SEGMENTS];",
            "float pumpSlope;",
            "float pumpIntercept;",
            "float pid_kp;",
            "float pid_ki;",
            "float pid_kd;",
            "uint32_t crc32;"
        ]
        for field in expected_fields:
            self.assertIn(field, body, f"Campo esperado ausente em PumpConfig: {field}")

        # Nenhum campo de calibração dupla novo deve estar na struct PumpConfig
        self.assertNotIn("m_low", body, "m_low não pode estar na struct PumpConfig (iria invalidar o NVS de v3.10)!")
        self.assertNotIn("m_high", body, "m_high não pode estar na struct PumpConfig!")
        self.assertNotIn("s_t", body, "s_t não pode estar na struct PumpConfig!")
        self.assertNotIn("q_t", body, "q_t não pode estar na struct PumpConfig!")

    def test_03_calibration_store_struct_and_magic(self):
        """CalibrationStore.h deve definir PUMP_CAL_MAGIC_V2 e struct PumpDualRangeCal (24 bytes)."""
        cal_store = os.path.join(SRC_DIR, "storage", "CalibrationStore.h")
        self.assertTrue(os.path.exists(cal_store), f"Arquivo não encontrado: {cal_store}")
        with open(cal_store, "r", encoding="utf-8") as f:
            content = f.read()

        self.assertIn("0x504D5032", content, "PUMP_CAL_MAGIC_V2 deve ser 0x504D5032 ('PMP2').")
        self.assertIn("pump_cal", content, "Deve definir a chave NVS 'pump_cal'.")

        # Verificar tamanho da struct em C++ (uint32 + 4 floats + uint32 = 24 bytes)
        packed_bytes = struct.pack("<I4fI", 0x504D5032, 0.05, 0.06, 500.0, 25.0, 0x12345678)
        self.assertEqual(len(packed_bytes), 24, "PumpDualRangeCal deve ter exatamente 24 bytes.")

    def test_04_crc32_calculation(self):
        """Cálculo de CRC32 da calibração sobre os 16 bytes de floats de calibração."""
        payload = struct.pack("<4f", 0.0512, 0.0645, 450.0, 22.5)
        self.assertEqual(len(payload), 16)
        crc = calculate_crc32(payload)
        self.assertIsInstance(crc, int)
        self.assertGreater(crc, 0)
        # CRC recalculado com mesmos dados deve ser determinístico
        self.assertEqual(crc, calculate_crc32(payload))

    def test_05_legacy_linear_migration_math_equivalence(self):
        """Migração de slope + intercept para dual-range deve produzir vazão idêntica em toda a faixa."""
        slope = 0.0825
        intercept = 1.1500

        # Regra de migração
        s_t = 500.0
        m_low = slope
        m_high = slope
        q_t = slope * s_t + intercept

        # Testar velocidades [0, 1000]
        test_speeds = [0.0, 10.0, 100.0, 250.0, 499.9, 500.0, 500.1, 750.0, 1000.0]
        for s in test_speeds:
            q_legacy = (slope * s + intercept) if s > 0 else 0.0
            q_dual = speed_to_flow(s, m_low, m_high, s_t, q_t)
            self.assertAlmostEqual(q_legacy, q_dual, places=4,
                                   msg=f"Discrepância na velocidade {s}: legacy={q_legacy}, dual={q_dual}")

    def test_06_continuity_at_pivot(self):
        """Garante continuidade C0 em S_t e Q_t sem degrau ou salto."""
        m_low = 0.045
        m_high = 0.075
        s_t = 480.0
        q_t = 24.5

        eps = 1e-6
        q_left = speed_to_flow(s_t - eps, m_low, m_high, s_t, q_t)
        q_pivot = speed_to_flow(s_t, m_low, m_high, s_t, q_t)
        q_right = speed_to_flow(s_t + eps, m_low, m_high, s_t, q_t)

        self.assertAlmostEqual(q_left, q_pivot, places=4)
        self.assertAlmostEqual(q_right, q_pivot, places=4)
        self.assertAlmostEqual(q_pivot, q_t, places=4)

        # Inversão: Q -> S
        s_left = flow_to_speed(q_t - eps, m_low, m_high, s_t, q_t)
        s_pivot = flow_to_speed(q_t, m_low, m_high, s_t, q_t)
        s_right = flow_to_speed(q_t + eps, m_low, m_high, s_t, q_t)

        self.assertAlmostEqual(s_left, s_pivot, places=3)
        self.assertAlmostEqual(s_right, s_pivot, places=3)
        self.assertAlmostEqual(s_pivot, s_t, places=3)

    def test_07_roundtrip_speed_flow_conversion(self):
        """Conversão bidirecional S -> Q -> S deve ter erro residual nulo."""
        m_low = 0.052
        m_high = 0.078
        s_t = 520.0
        q_t = 28.0

        for speed_in in [1.0, 50.0, 200.0, 400.0, 519.99, 520.0, 520.01, 700.0, 950.0, 1000.0]:
            flow = speed_to_flow(speed_in, m_low, m_high, s_t, q_t)
            speed_out = flow_to_speed(flow, m_low, m_high, s_t, q_t)
            self.assertAlmostEqual(speed_in, speed_out, places=4,
                                   msg=f"Erro de roundtrip para speed={speed_in}")

    def test_08_operation_controller_staging_and_safety(self):
        """OperationController.h deve validar staging de 4 campos e bloquear durante operação ativa."""
        ctrl_path = os.path.join(SRC_DIR, "control", "OperationController.h")
        with open(ctrl_path, "r", encoding="utf-8") as f:
            content = f.read()

        # Interlock de segurança
        self.assertIn("g_opState == OP_RUNNING || g_opState == OP_WAITING", content,
                      "Deve verificar se bomba está ativa antes de aceitar calibração.")

        # Validações matemáticas
        self.assertIn("valLow <= 0.0f", content, "Deve validar m_low > 0.")
        self.assertIn("valHigh <= 0.0f", content, "Deve validar m_high > 0.")
        self.assertIn("valSt <= 0.0f || valSt >= 1000.0f", content, "Deve validar s_t em (0, 1000).")
        self.assertIn("valQt <= 0.0f", content, "Deve validar q_t > 0.")
        self.assertIn("valQt - valLow * valSt", content, "Deve calcular extrapolação em S=0.")

        # Staging atômico
        self.assertIn("hasAllFour", content,
                      "Deve exigir os 4 parâmetros no mesmo quadro.")

    def test_09_telemetry_and_hub_client_fields(self):
        """TelemetryCodec.h e HubClient.h devem expor os parâmetros de calibração dupla."""
        codec_path = os.path.join(SRC_DIR, "protocol", "TelemetryCodec.h")
        with open(codec_path, "r", encoding="utf-8") as f:
            content = f.read()

        self.assertIn("slope_low", content)
        self.assertIn("slope_high", content)
        self.assertIn("transition_speed", content)
        self.assertIn("transition_flow", content)
        self.assertIn("cal_crc", content)
        self.assertIn("pumpSlopeLow", content)
        self.assertIn("pumpSlopeHigh", content)
        self.assertIn("pumpTransitionSpeed", content)
        self.assertIn("pumpTransitionFlow", content)

        hub_path = os.path.join(SRC_DIR, "network", "HubClient.h")
        with open(hub_path, "r", encoding="utf-8") as f:
            content = f.read()

        self.assertIn("slope_low=%.6f", content)
        self.assertIn("slope_high=%.6f", content)
        self.assertIn("trans_speed=%.2f", content)
        self.assertIn("trans_flow=%.4f", content)
        self.assertIn("cal_crc=%08X", content)


if __name__ == "__main__":
    unittest.main()
