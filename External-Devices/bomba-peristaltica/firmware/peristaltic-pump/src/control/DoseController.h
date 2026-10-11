// Dosagem por pulsos (build 3.12+dose1).
//
// Para volumes muito pequenos (ex.: 50 mL em 12 h = 0,069 mL/min) nenhuma velocidade do motor
// e estavel. O no liga o motor numa velocidade fixa, onde a vazao e conhecida, por
// `volume / vazao` segundos, e repete a cada `periodo`. A vazao (mL/min na velocidade escolhida)
// e medida pelo operador, nao vem da calibracao polinomial.
//
// Comandos (JSON, USB ou POST /command):
//   {"dose_ml":0.35,"dose_period_s":300,"dose_speed":120,"dose_rate_mlmin":5.4,"dose_total_ml":50}
//   {"dose":1}   inicia (primeiro pulso imediato)      {"dose":0}   para
// Os cinco parametros sao guardados na NVS e podem ser enviados no mesmo quadro de "dose":1.
// start/stop, mode, speed e qualquer perfil de vazao cancelam a dosagem: o operador manda.

static const char* DOSE_KEY_VOL  = "d_vol";
static const char* DOSE_KEY_PER  = "d_per";
static const char* DOSE_KEY_SPD  = "d_spd";
static const char* DOSE_KEY_RATE = "d_rate";
static const char* DOSE_KEY_TOT  = "d_tot";
static const char* DOSE_KEY_ACT  = "d_act";
static const char* DOSE_KEY_DONE = "d_done";
static const char* DOSE_KEY_N    = "d_n";

static const char* doseValidate(const DoseConfig& c) {
    if (!isfinite(c.volumeMl) || c.volumeMl <= 0.0f || c.volumeMl > 100.0f)
        return "dose_ml fora de (0,100] mL";
    if (!isfinite(c.periodS) || c.periodS < 2.0f || c.periodS > 86400.0f)
        return "dose_period_s fora de [2,86400] s";
    if (!isfinite(c.speed) || c.speed < ENABLE_EPS || c.speed > V_MAX)
        return "dose_speed fora de [1,1000]";
    if (!isfinite(c.rateMlMin) || c.rateMlMin <= 0.0f || c.rateMlMin > 1000.0f)
        return "dose_rate_mlmin ausente: meca a vazao nessa velocidade";
    if (!isfinite(c.totalMl) || c.totalMl < 0.0f)
        return "dose_total_ml negativo";
    const float onMs = c.volumeMl / c.rateMlMin * 60000.0f;
    if (onMs < static_cast<float>(MIN_MOTOR_ON_TIME_MS))
        return "pulso menor que 0,5 s: aumente dose_ml ou reduza a velocidade";
    if (onMs + 1000.0f > c.periodS * 1000.0f)
        return "pulso nao cabe no periodo";
    return nullptr;
}

static void doseSaveParams() {
    g_prefs.putFloat(DOSE_KEY_VOL, g_dose.volumeMl);
    g_prefs.putFloat(DOSE_KEY_PER, g_dose.periodS);
    g_prefs.putFloat(DOSE_KEY_SPD, g_dose.speed);
    g_prefs.putFloat(DOSE_KEY_RATE, g_dose.rateMlMin);
    g_prefs.putFloat(DOSE_KEY_TOT, g_dose.totalMl);
}

static void doseSaveState() {
    g_prefs.putBool(DOSE_KEY_ACT, g_doseActive);
    g_prefs.putFloat(DOSE_KEY_DONE, g_doseDeliveredMl);
    g_prefs.putUInt(DOSE_KEY_N, g_dosePulses);
}

// O volume da dosagem entra no contador de sessao (`vol`), que o Hub e o app leem. Soma-se
// por pulso, nao por tick: somar fracoes de ms a um float que passa de 1000 mL perderia
// precisao.
static void doseAddToSession(float ml) {
    taskDISABLE_INTERRUPTS();
    g_cumulativeVolumeMl += ml;
    taskENABLE_INTERRUPTS();
}

float doseDeliveredNow() {
    float partial = 0.0f;
    if (g_dosePulseOn) partial = (g_dose.rateMlMin / 60000.0f) * static_cast<float>(g_dosePulseRunMs);
    return g_doseDeliveredMl + partial;
}

void doseCancel(const char* reason) {
    if (!g_doseActive && !g_dosePulseOn) return;
    if (g_dosePulseOn) {
        const float partial = (g_dose.rateMlMin / 60000.0f) * static_cast<float>(g_dosePulseRunMs);
        g_doseDeliveredMl += partial;
        doseAddToSession(partial);
    }
    g_doseActive = false;
    g_dosePulseOn = false;
    g_doseCutoffMs = 0;
    doseSaveState();
    Serial.printf("[DOSE] encerrada (%s): %.3f mL em %lu pulso(s).\n",
                  reason, static_cast<double>(g_doseDeliveredMl),
                  static_cast<unsigned long>(g_dosePulses));
}

// Apos queda ou reinicio a dosagem retoma sozinha: a fase se perde, entao o primeiro pulso
// vem 5 s depois do boot e o volume ja entregue continua contando.
void doseRestore() {
    g_dose.volumeMl  = g_prefs.getFloat(DOSE_KEY_VOL, 0.0f);
    g_dose.periodS   = g_prefs.getFloat(DOSE_KEY_PER, 0.0f);
    g_dose.speed     = g_prefs.getFloat(DOSE_KEY_SPD, 0.0f);
    g_dose.rateMlMin = g_prefs.getFloat(DOSE_KEY_RATE, 0.0f);
    g_dose.totalMl   = g_prefs.getFloat(DOSE_KEY_TOT, 0.0f);
    if (!g_prefs.getBool(DOSE_KEY_ACT, false)) return;

    const char* bad = doseValidate(g_dose);
    if (bad) {
        Serial.printf("[DOSE] estado salvo invalido (%s); dosagem nao retomada.\n", bad);
        g_prefs.putBool(DOSE_KEY_ACT, false);
        return;
    }
    g_doseDeliveredMl = g_prefs.getFloat(DOSE_KEY_DONE, 0.0f);
    g_dosePulses = g_prefs.getUInt(DOSE_KEY_N, 0);
    // O contador de sessao zera no boot; refaz-se com o que a dosagem ja entregou.
    taskDISABLE_INTERRUPTS();
    g_cumulativeVolumeMl = g_doseDeliveredMl;
    taskENABLE_INTERRUPTS();
    g_cycleStartVolumeMl = 0.0f;
    g_doseActive = true;
    g_dosePulseOn = false;
    g_doseLastTickMs = millis();
    g_doseNextPulseMs = g_doseLastTickMs + 5000;
    Serial.printf("[DOSE] retomada apos reinicio: %.3f mL entregues em %lu pulso(s).\n",
                  static_cast<double>(g_doseDeliveredMl), static_cast<unsigned long>(g_dosePulses));
}

static void doseStartPulse(unsigned long now) {
    float volume = g_dose.volumeMl;
    if (g_dose.totalMl > 0.0f) {
        const float remaining = g_dose.totalMl - g_doseDeliveredMl;
        if (remaining < volume) volume = remaining;
    }
    const unsigned long onMs =
        static_cast<unsigned long>(lroundf(volume / g_dose.rateMlMin * 60000.0f));
    if (onMs < MIN_MOTOR_ON_TIME_MS) {
        doseCancel("volume total atingido");
        return;
    }
    g_dosePulseOn = true;
    g_dosePulseStartMs = now;
    g_dosePulseOnMs = onMs;
    g_dosePulseRunMs = 0;
    uint32_t cutoff = static_cast<uint32_t>(now + onMs);
    if (cutoff == 0) cutoff = 1;
    g_doseCutoffMs = cutoff;
    Serial.printf("[DOSE] pulso %lu: %lu ms a velocidade %.1f (%.3f mL)\n",
                  static_cast<unsigned long>(g_dosePulses + 1), onMs,
                  static_cast<double>(g_dose.speed), static_cast<double>(volume));
}

void doseService(unsigned long now) {
    if (!g_doseActive) {
        g_doseLastTickMs = now;
        return;
    }
    if (g_opState != OP_IDLE) {
        doseCancel("perfil de vazao iniciado");
        g_doseLastTickMs = now;
        return;
    }

    if (g_dosePulseOn) {
        const unsigned long stopAt = g_dosePulseStartMs + g_dosePulseOnMs;
        const bool expired = static_cast<long>(now - stopAt) >= 0;
        // Tempo em que o motor de fato girou: so conta com o driver ligado (sensor de liquido
        // e abort podem segurar) e nunca alem do prazo que o core 0 aplica.
        if (g_driverEnabled && fabsf(g_cmdSpeed) >= ENABLE_EPS) {
            const unsigned long upTo = expired ? stopAt : now;
            const long inc = static_cast<long>(upTo - g_doseLastTickMs);
            if (inc > 0) g_dosePulseRunMs += static_cast<unsigned long>(inc);
        }
        if (expired) {
            const float ml = (g_dose.rateMlMin / 60000.0f) * static_cast<float>(g_dosePulseRunMs);
            g_doseDeliveredMl += ml;
            doseAddToSession(ml);
            g_dosePulses++;
            g_dosePulseOn = false;
            // O prazo vencido fica armado: segura o motor mesmo que o travamento minimo de
            // 0,5 s do loop ainda o mantenha ligado alguns ms. Cancelar e novo pulso o trocam.
            Serial.printf("[DOSE] pulso %lu: motor %lu ms, %.3f mL (total %.3f mL)\n",
                          static_cast<unsigned long>(g_dosePulses),
                          static_cast<unsigned long>(g_dosePulseRunMs),
                          static_cast<double>(ml), static_cast<double>(g_doseDeliveredMl));
            g_doseNextPulseMs = g_dosePulseStartMs + static_cast<unsigned long>(g_dose.periodS * 1000.0f);
            if (static_cast<long>(now - g_doseNextPulseMs) >= 0) g_doseNextPulseMs = now + 1000;
            doseSaveState();
            if (g_dose.totalMl > 0.0f && g_doseDeliveredMl >= g_dose.totalMl - 0.005f) {
                doseCancel("volume total atingido");
            }
        }
    } else if (static_cast<long>(now - g_doseNextPulseMs) >= 0) {
        doseStartPulse(now);
    }
    g_doseLastTickMs = now;
}

void doseHandleCommand(const char* json) {
    DoseConfig next = g_dose;
    bool touched = false;
    float v = getJsonFloatValue(json, "dose_ml");
    if (!isnan(v)) { next.volumeMl = v; touched = true; }
    v = getJsonFloatValue(json, "dose_period_s");
    if (!isnan(v)) { next.periodS = v; touched = true; }
    v = getJsonFloatValue(json, "dose_speed");
    if (!isnan(v)) { next.speed = v; touched = true; }
    v = getJsonFloatValue(json, "dose_rate_mlmin");
    if (!isnan(v)) { next.rateMlMin = v; touched = true; }
    v = getJsonFloatValue(json, "dose_total_ml");
    if (!isnan(v)) { next.totalMl = v; touched = true; }
    const float enable = getJsonFloatValue(json, "dose");

    if (!touched && isnan(enable)) return;

    if (!isnan(enable) && enable == 0.0f) {
        if (touched) { g_dose = next; doseSaveParams(); }
        doseCancel("dose:0");
        return;
    }

    const bool starting = !isnan(enable) && enable == 1.0f;
    // Parametros soltos so sao conferidos quando valem de imediato (dosagem ativa ou inicio).
    if (starting || g_doseActive) {
        const char* bad = doseValidate(next);
        if (bad) {
            Serial.printf("[DOSE] recusado: %s\n", bad);
            return;
        }
    }
    if (touched) {
        g_dose = next;
        doseSaveParams();
        Serial.printf("[DOSE] parametros: %.3f mL a cada %.1f s, velocidade %.1f, %.3f mL/min, total %.3f mL\n",
                      static_cast<double>(g_dose.volumeMl), static_cast<double>(g_dose.periodS),
                      static_cast<double>(g_dose.speed), static_cast<double>(g_dose.rateMlMin),
                      static_cast<double>(g_dose.totalMl));
    }
    if (!starting) return;

    if (g_opState != OP_IDLE) {
        Serial.println("[DOSE] recusado: perfil de vazao em execucao; envie stop antes.");
        return;
    }
    hasUsbSpeed = false;
    usbSpeedSteps = 0.0f;
    g_usbSpeedUntilMs = 0;
    g_doseDeliveredMl = 0.0f;
    g_dosePulses = 0;
    g_dosePulseOn = false;
    g_doseCutoffMs = 0;
    // O volume do ciclo (cyc_vol) passa a contar desde o inicio desta dosagem; o da sessao
    // (vol) continua de onde estava e so reset_volume o zera.
    float sessionNow;
    taskDISABLE_INTERRUPTS();
    sessionNow = g_cumulativeVolumeMl;
    taskENABLE_INTERRUPTS();
    g_cycleStartVolumeMl = sessionNow;
    g_doseActive = true;
    g_doseLastTickMs = millis();
    g_doseNextPulseMs = g_doseLastTickMs;
    doseSaveState();
    Serial.println("[DOSE] iniciada.");
}
