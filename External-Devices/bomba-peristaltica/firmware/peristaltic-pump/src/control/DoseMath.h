#pragma once
// Regras da dosagem por pulsos (3.13), sem dependencia de Arduino: o firmware e a simulacao
// no PC (tests/host-sim) usam o mesmo codigo, e o app espelha a validacao (PumpDoseMath).
//
// Pulso: o motor gira na velocidade fixa `speed`, onde a vazao `rateMlMin` foi medida, por
//   onMs = 60000 * volume / rate + deadMs
// e o volume contado e rate * max(0, motorMs - deadMs) / 60000. deadMs (t0) e o atraso
// efetivo de partida medido na verificacao em pulsos; 0 reproduz o comportamento da 3.12.

#include <cmath>
#include <cstdint>

struct DoseConfig {
    float volumeMl;   // por pulso
    float periodS;    // entre inicios de pulso
    float speed;      // unidades 1..1000, a mesma de "speed"
    float rateMlMin;  // vazao medida nessa velocidade
    float totalMl;    // 0 = sem limite
    float deadMs;     // atraso de partida compensado (0..2000)
};

static const float    DOSE_SPEED_MIN   = 1.0f;
static const float    DOSE_SPEED_MAX   = 1000.0f;
static const float    DOSE_DEAD_MAX_MS = 2000.0f;
// Parte do pulso que entrega volume (sem o atraso). Abaixo disso o erro de partida domina.
static const uint32_t DOSE_MIN_ON_MS   = 200;
// Folga minima entre o fim de um pulso e o inicio do proximo.
static const uint32_t DOSE_GAP_MS      = 500;

// Tempo de motor que entrega `volumeMl`, sem o atraso de partida.
inline float doseDeliveryMs(float volumeMl, float rateMlMin) {
    return volumeMl / rateMlMin * 60000.0f;
}

// Tempo total do pulso, com o atraso de partida.
inline float dosePulseMs(float volumeMl, float rateMlMin, float deadMs) {
    return doseDeliveryMs(volumeMl, rateMlMin) + deadMs;
}

// Volume entregue por `motorMs` de motor girando.
inline float doseCountedMl(float rateMlMin, float motorMs, float deadMs) {
    const float useful = motorMs - deadMs;
    return useful > 0.0f ? rateMlMin * useful / 60000.0f : 0.0f;
}

// nullptr se valida; senao o codigo rej_err publicado ao Hub.
inline const char* doseValidateCode(const DoseConfig& c) {
    if (!std::isfinite(c.volumeMl) || c.volumeMl <= 0.0f || c.volumeMl > 100.0f) return "dose_ml";
    if (!std::isfinite(c.periodS) || c.periodS < 2.0f || c.periodS > 86400.0f) return "dose_period";
    if (!std::isfinite(c.speed) || c.speed < DOSE_SPEED_MIN || c.speed > DOSE_SPEED_MAX) return "dose_speed";
    if (!std::isfinite(c.rateMlMin) || c.rateMlMin <= 0.0f || c.rateMlMin > 1000.0f) return "dose_rate";
    if (!std::isfinite(c.totalMl) || c.totalMl < 0.0f) return "dose_total";
    if (!std::isfinite(c.deadMs) || c.deadMs < 0.0f || c.deadMs > DOSE_DEAD_MAX_MS) return "dose_dead";
    if (doseDeliveryMs(c.volumeMl, c.rateMlMin) < static_cast<float>(DOSE_MIN_ON_MS)) return "dose_short";
    if (dosePulseMs(c.volumeMl, c.rateMlMin, c.deadMs) + static_cast<float>(DOSE_GAP_MS) > c.periodS * 1000.0f)
        return "dose_fit";
    return nullptr;
}

// Texto para o log serial.
inline const char* doseCodeText(const char* code) {
    if (!code) return "";
    struct Entry { const char* code; const char* text; };
    static const Entry table[] = {
        {"dose_ml", "dose_ml fora de (0,100] mL"},
        {"dose_period", "dose_period_s fora de [2,86400] s"},
        {"dose_speed", "dose_speed fora de [1,1000]"},
        {"dose_rate", "dose_rate_mlmin ausente: meca a vazao nessa velocidade"},
        {"dose_total", "dose_total_ml negativo"},
        {"dose_dead", "dose_dead_ms fora de [0,2000] ms"},
        {"dose_short", "pulso menor que 0,2 s: aumente dose_ml ou reduza a velocidade"},
        {"dose_fit", "pulso nao cabe no periodo"},
        {"dose_busy", "perfil de vazao em execucao; envie stop antes"},
    };
    for (const Entry& e : table) {
        const char* a = e.code; const char* b = code;
        while (*a && *a == *b) { a++; b++; }
        if (*a == *b) return e.text;
    }
    return code;
}
