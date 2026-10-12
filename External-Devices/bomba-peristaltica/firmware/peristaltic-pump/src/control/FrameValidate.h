#pragma once
// Validacao do quadro inteiro antes de aplicar (3.13), sem dependencia de Arduino.
//
// Antes, cada parte do quadro era aplicada ou recusada sozinha e o Hub recebia ACK de
// qualquer jeito: uma calibracao ou dosagem recusada parecia aceita. Agora, se qualquer
// parte que pode ser recusada falhar, nada e aplicado e o no publica rej_cmd_id/rej_err
// (o mesmo padrao do banho r3.2).

#include <cmath>
#include <cstring>

#include "../protocol/JsonScan.h"
#include "DoseMath.h"

struct FrameContext {
    bool profileBusy;   // perfil rodando ou aguardando init_t
    bool doseActive;
    DoseConfig dose;    // parametros atuais da dosagem
};

static const int FRAME_MAX_SEGMENTS = 100;

// Mesma forma exigida pelo aplicador: quarto grau ate St, quadratico acima, C0+C1 em St,
// vazao nao negativa e monotonica em 0..1000.
inline bool calShapeOk(const float c[9]) {
    const float a1 = c[0], b1 = c[1], k1 = c[2], f1 = c[3], c1 = c[4];
    const float k2 = c[5], f2 = c[6], c2 = c[7], st = c[8];
    auto q = [&](float s) {
        if (s <= st) return ((((a1 * s) + b1) * s + k1) * s + f1) * s + c1;
        return (k2 * s + f2) * s + c2;
    };
    const float lowValue = ((((a1 * st) + b1) * st + k1) * st + f1) * st + c1;
    const float highValue = (k2 * st + f2) * st + c2;
    const float lowSlope = (((4 * a1 * st) + (3 * b1)) * st + (2 * k1)) * st + f1;
    const float highSlope = 2 * k2 * st + f2;
    if (std::fabs(lowValue - highValue) > 1e-3f || std::fabs(lowSlope - highSlope) > 1e-3f) return false;
    float prior = q(0.0f);
    if (!(prior >= -1e-5f)) return false;
    for (int i = 1; i <= 100; i++) {
        const float current = q(i * 10.0f);
        if (!std::isfinite(current) || current < prior - 1e-5f) return false;
        prior = current;
    }
    return true;
}

// nullptr se o quadro pode ser aplicado; senao o codigo rej_err.
inline const char* validateFrame(const char* json, const FrameContext& ctx) {
    char cmd[24] = "";
    getJsonStringInto(json, "command", cmd, sizeof(cmd));

    // Estado de perfil depois dos comandos que o mudam na hora (start/stop/speed/mode).
    bool busyAfter = ctx.profileBusy;
    if (strcmp(cmd, "stop") == 0) busyAfter = false;
    if (strcmp(cmd, "start") == 0) busyAfter = true;
    if (jsonHasKey(json, "speed")) busyAfter = false;

    const float mode = getJsonFloatValue(json, "mode");
    if (!std::isnan(mode)) {
        const int m = static_cast<int>(mode);
        if (m < 0 || m > 5) return "mode_range";
        busyAfter = m > 0;
    }

    const float seg = getJsonFloatValue(json, "num_segments");
    if (!std::isnan(seg)) {
        const int n = static_cast<int>(seg);
        if (n < 2 || n > FRAME_MAX_SEGMENTS) return "seg_range";
    }

    // Calibracao: atomica, com a bomba sem perfil.
    static const char* pumpKeys[] = { "pumpA1", "pumpB1", "pumpK1", "pumpF1", "pumpC1",
                                      "pumpK2", "pumpF2", "pumpC2", "pumpTransitionSpeed" };
    static const char* nativeKeys[] = { "a1", "b1", "k1", "f1", "c1", "k2", "f2", "c2", "transition_speed" };
    float cal[9];
    bool anyCal = false, allCal = true;
    for (int i = 0; i < 9; i++) {
        cal[i] = getJsonFloatValue(json, pumpKeys[i]);
        if (std::isnan(cal[i])) cal[i] = getJsonFloatValue(json, nativeKeys[i]);
        anyCal |= !std::isnan(cal[i]);
        allCal &= !std::isnan(cal[i]) && std::isfinite(cal[i]);
    }
    if (anyCal) {
        // A calibracao e lida antes do fim do quadro: vale o estado antes de mode, como no
        // aplicador (start/stop/speed ja mudaram o estado).
        bool calBusy = ctx.profileBusy;
        if (strcmp(cmd, "stop") == 0 || jsonHasKey(json, "speed")) calBusy = false;
        if (strcmp(cmd, "start") == 0) calBusy = true;
        if (calBusy) return "cal_busy";
        if (!allCal) return "cal_incomplete";
        if (cal[8] <= 0.0f || cal[8] >= 1000.0f) return "cal_st_range";
        if (!calShapeOk(cal)) return "cal_shape";
    }

    // Dosagem.
    DoseConfig next = ctx.dose;
    bool touched = false;
    struct { const char* key; float* field; } doseKeys[] = {
        {"dose_ml", &next.volumeMl}, {"dose_period_s", &next.periodS}, {"dose_speed", &next.speed},
        {"dose_rate_mlmin", &next.rateMlMin}, {"dose_total_ml", &next.totalMl}, {"dose_dead_ms", &next.deadMs},
    };
    for (auto& k : doseKeys) {
        const float v = getJsonFloatValue(json, k.key);
        if (!std::isnan(v)) { *k.field = v; touched = true; }
    }
    const float enable = getJsonFloatValue(json, "dose");
    if (touched && !(std::isfinite(next.deadMs) && next.deadMs >= 0.0f && next.deadMs <= DOSE_DEAD_MAX_MS))
        return "dose_dead";
    const bool stopping = !std::isnan(enable) && enable == 0.0f;
    const bool starting = !std::isnan(enable) && enable == 1.0f;
    if (!std::isnan(enable) && !stopping && !starting) return "dose_range";
    // Algo no quadro que cancela a dosagem antes do dose_* (start/stop/mode/speed).
    const bool cancelsDose = strcmp(cmd, "start") == 0 || strcmp(cmd, "stop") == 0 ||
                             !std::isnan(mode) || jsonHasKey(json, "speed");
    const bool doseStaysActive = ctx.doseActive && !cancelsDose && !stopping;
    if (starting || (touched && doseStaysActive)) {
        const char* bad = doseValidateCode(next);
        if (bad) return bad;
    }
    if (starting && busyAfter) return "dose_busy";
    return nullptr;
}
