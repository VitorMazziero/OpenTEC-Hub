#pragma once

#include <Arduino.h>
#include <math.h>
#include <Preferences.h>

constexpr char* NVS_KEY_PUMP_CAL = (char*)"pump_cal";
const uint32_t PUMP_CAL_MAGIC_V2 = 0x504D5032; // "PMP2"

#ifndef PUMP_DUAL_RANGE_CAL_DEFINED
#define PUMP_DUAL_RANGE_CAL_DEFINED
struct PumpDualRangeCal {
    uint32_t magic;
    float m_low;              // slope do segmento inferior (mL/min por speed unit)
    float m_high;             // slope do segmento superior (mL/min por speed unit)
    float s_t;                // velocidade de transicao (speed units)
    float q_t;                // vazao na transicao (mL/min)
    uint32_t crc32;           // CRC32 IEEE 802.3 dos 4 floats (16 bytes)
};
#endif

extern PumpDualRangeCal g_pumpCal;
extern Preferences g_prefs;

// Calculo de CRC32 padrao IEEE 802.3 (polinomio 0xEDB88320)
static inline uint32_t calculateCalibrationCrc32(const uint8_t* data, size_t length) {
    uint32_t crc = 0xFFFFFFFF;
    for (size_t i = 0; i < length; i++) {
        crc ^= data[i];
        for (uint8_t j = 0; j < 8; j++) {
            crc = (crc >> 1) ^ (0xEDB88320 & (-(crc & 1)));
        }
    }
    return ~crc;
}

void loadPumpCalibration() {
    size_t calSize = g_prefs.getBytesLength(NVS_KEY_PUMP_CAL);
    if (calSize == sizeof(PumpDualRangeCal)) {
        PumpDualRangeCal tempCal;
        if (g_prefs.getBytes(NVS_KEY_PUMP_CAL, &tempCal, sizeof(PumpDualRangeCal)) == sizeof(PumpDualRangeCal)) {
            if (tempCal.magic == PUMP_CAL_MAGIC_V2) {
                uint32_t calcCrc = calculateCalibrationCrc32((const uint8_t*)&tempCal + sizeof(tempCal.magic), 4 * sizeof(float));
                if (calcCrc == tempCal.crc32) {
                    if (isfinite(tempCal.m_low) && tempCal.m_low > 0.0f &&
                        isfinite(tempCal.m_high) && tempCal.m_high > 0.0f &&
                        isfinite(tempCal.s_t) && tempCal.s_t > 0.0f && tempCal.s_t < 1000.0f &&
                        isfinite(tempCal.q_t) && tempCal.q_t > 0.0f) {
                        g_pumpCal = tempCal;
                        Serial.printf("[NVS] Calibracao dupla v2 carregada com sucesso (CRC: %08X): m_low=%.6f m_high=%.6f St=%.2f Qt=%.4f\n",
                                      g_pumpCal.crc32, g_pumpCal.m_low, g_pumpCal.m_high, g_pumpCal.s_t, g_pumpCal.q_t);
                        return;
                    }
                }
            }
        }
    }

    // Migracao transparente da calibracao linear legada (g_config.pumpSlope e pumpIntercept)
    Serial.println("[NVS] Calibracao dupla v2 nao encontrada. Migrando da calibracao linear legada...");
    float slope = g_config.pumpSlope;
    float intercept = g_config.pumpIntercept;
    if (isnan(slope) || slope <= 0.0f || !isfinite(slope)) {
        slope = 0.0280188148f;
    }
    if (isnan(intercept) || !isfinite(intercept)) {
        intercept = 1.7601988934f;
    }
    const float st = 500.0f;
    float qt = slope * st + intercept;
    if (qt <= 0.0f) {
        qt = slope * st;
    }

    g_pumpCal.magic = PUMP_CAL_MAGIC_V2;
    g_pumpCal.m_low = slope;
    g_pumpCal.m_high = slope;
    g_pumpCal.s_t = st;
    g_pumpCal.q_t = qt;
    g_pumpCal.crc32 = calculateCalibrationCrc32((const uint8_t*)&g_pumpCal + sizeof(g_pumpCal.magic), 4 * sizeof(float));

    // Salva o novo registro sem alterar g_config
    g_prefs.putBytes(NVS_KEY_PUMP_CAL, &g_pumpCal, sizeof(PumpDualRangeCal));
    Serial.printf("[NVS] Migrado para Calibracao Dupla v2 (CRC: %08X): m_low=m_high=%.6f St=%.1f Qt=%.4f\n",
                  g_pumpCal.crc32, g_pumpCal.m_low, g_pumpCal.s_t, g_pumpCal.q_t);
}

void savePumpCalibration() {
    g_pumpCal.magic = PUMP_CAL_MAGIC_V2;
    g_pumpCal.crc32 = calculateCalibrationCrc32((const uint8_t*)&g_pumpCal + sizeof(g_pumpCal.magic), 4 * sizeof(float));
    if (g_prefs.putBytes(NVS_KEY_PUMP_CAL, &g_pumpCal, sizeof(PumpDualRangeCal))) {
        Serial.printf("[NVS] Calibracao dupla v2 salva com sucesso (CRC: %08X).\n", g_pumpCal.crc32);
    }
}
