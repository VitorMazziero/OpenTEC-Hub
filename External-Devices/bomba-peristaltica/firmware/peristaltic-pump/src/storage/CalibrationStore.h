#pragma once

#include <Arduino.h>
#include <math.h>
#include <Preferences.h>

constexpr const char* NVS_KEY_PUMP_POLY_CAL = "pump_poly_cal";
constexpr const char* NVS_KEY_PUMP_CAL_V2 = "pump_cal";
const uint32_t PUMP_CAL_MAGIC_V3 = 0x504D5033; // PMP3
const uint32_t PUMP_CAL_MAGIC_V2 = 0x504D5032; // PMP2

#ifndef PUMP_DUAL_RANGE_CAL_DEFINED
#define PUMP_DUAL_RANGE_CAL_DEFINED
struct PumpDualRangeCal {
    uint32_t magic;
    float a1, b1, k1, f1, c1; // quartica: faixa baixa
    float k2, f2, c2;         // quadratica: faixa alta
    float s_t;
    uint32_t crc32;            // CRC32 dos nove floats
};
#endif

struct PumpDualRangeCalV2 {
    uint32_t magic;
    float m_low, m_high, s_t, q_t;
    uint32_t crc32;
};

extern PumpDualRangeCal g_pumpCal;
extern Preferences g_prefs;

static inline uint32_t calculateCalibrationCrc32(const uint8_t* data, size_t length) {
    uint32_t crc = 0xFFFFFFFF;
    for (size_t i = 0; i < length; i++) {
        crc ^= data[i];
        for (uint8_t j = 0; j < 8; j++) crc = (crc >> 1) ^ (0xEDB88320 & (-(crc & 1)));
    }
    return ~crc;
}

static inline float evaluatePumpCalibration(const PumpDualRangeCal& cal, float speed) {
    if (speed <= cal.s_t) {
        return ((((cal.a1 * speed + cal.b1) * speed + cal.k1) * speed + cal.f1) * speed + cal.c1);
    }
    return (cal.k2 * speed + cal.f2) * speed + cal.c2;
}

static inline bool isPumpCalibrationValid(const PumpDualRangeCal& cal) {
    const float values[] = {cal.a1, cal.b1, cal.k1, cal.f1, cal.c1,
                            cal.k2, cal.f2, cal.c2, cal.s_t};
    for (float value : values) if (!isfinite(value)) return false;
    if (cal.s_t <= 0.0f || cal.s_t >= 1000.0f) return false;

    const float lowAtTransition = evaluatePumpCalibration(cal, cal.s_t);
    const float highAtTransition = (cal.k2 * cal.s_t + cal.f2) * cal.s_t + cal.c2;
    const float lowDerivative = ((4.0f * cal.a1 * cal.s_t + 3.0f * cal.b1) * cal.s_t + 2.0f * cal.k1) * cal.s_t + cal.f1;
    const float highDerivative = 2.0f * cal.k2 * cal.s_t + cal.f2;
    if (fabsf(lowAtTransition - highAtTransition) > 1e-3f ||
        fabsf(lowDerivative - highDerivative) > 1e-3f) return false;

    float previous = evaluatePumpCalibration(cal, 0.0f);
    if (!isfinite(previous) || previous < -1e-5f) return false;
    for (uint8_t index = 1; index <= 100; index++) {
        const float current = evaluatePumpCalibration(cal, index * 10.0f);
        if (!isfinite(current) || current < previous - 1e-5f) return false;
        previous = current;
    }
    return previous > evaluatePumpCalibration(cal, 0.0f) + 1e-5f;
}

static inline void setLinearPumpCalibration(float lowSlope, float highSlope, float st, float qt) {
    float slope = lowSlope;
    g_pumpCal.magic = PUMP_CAL_MAGIC_V3;
    g_pumpCal.a1 = g_pumpCal.b1 = g_pumpCal.k1 = 0.0f;
    g_pumpCal.f1 = slope;
    g_pumpCal.c1 = qt - slope * st;
    g_pumpCal.k2 = 0.0f;
    g_pumpCal.f2 = slope;
    g_pumpCal.c2 = qt - slope * st;
    g_pumpCal.s_t = st;
}

void savePumpCalibration() {
    g_pumpCal.magic = PUMP_CAL_MAGIC_V3;
    g_pumpCal.crc32 = calculateCalibrationCrc32((const uint8_t*)&g_pumpCal.a1, 9 * sizeof(float));
    g_prefs.putBytes(NVS_KEY_PUMP_POLY_CAL, &g_pumpCal, sizeof(g_pumpCal));
}

void loadPumpCalibration() {
    PumpDualRangeCal candidate{};
    if (g_prefs.getBytesLength(NVS_KEY_PUMP_POLY_CAL) == sizeof(candidate) &&
        g_prefs.getBytes(NVS_KEY_PUMP_POLY_CAL, &candidate, sizeof(candidate)) == sizeof(candidate) &&
        candidate.magic == PUMP_CAL_MAGIC_V3 &&
        calculateCalibrationCrc32((const uint8_t*)&candidate.a1, 9 * sizeof(float)) == candidate.crc32 &&
        isPumpCalibrationValid(candidate)) {
        g_pumpCal = candidate;
        Serial.printf("[NVS] Calibracao polinomial v3 carregada (CRC %08X).\n", g_pumpCal.crc32);
        return;
    }

    PumpDualRangeCalV2 old{};
    if (g_prefs.getBytesLength(NVS_KEY_PUMP_CAL_V2) == sizeof(old) &&
        g_prefs.getBytes(NVS_KEY_PUMP_CAL_V2, &old, sizeof(old)) == sizeof(old) &&
        old.magic == PUMP_CAL_MAGIC_V2 &&
        calculateCalibrationCrc32((const uint8_t*)&old.m_low, 4 * sizeof(float)) == old.crc32 &&
        isfinite(old.m_low) && old.m_low > 0.0f && isfinite(old.s_t) && old.s_t > 0.0f && old.s_t < 1000.0f &&
        isfinite(old.q_t) && old.q_t - old.m_low * old.s_t >= -1e-5f) {
        setLinearPumpCalibration(old.m_low, old.m_high, old.s_t, old.q_t);
        savePumpCalibration();
        Serial.println("[NVS] Calibracao v2 de duas retas migrada para polinomios equivalentes.");
        return;
    }

    float slope = isfinite(g_config.pumpSlope) && g_config.pumpSlope > 0 ? g_config.pumpSlope : 0.0280188148f;
    float intercept = isfinite(g_config.pumpIntercept) ? g_config.pumpIntercept : 1.7601988934f;
    const float st = 500.0f;
    setLinearPumpCalibration(slope, slope, st, slope * st + intercept);
    savePumpCalibration();
    Serial.println("[NVS] Calibracao linear legada migrada para polinomios equivalentes.");
}
