void pwmTask(void* pv) {
    uint32_t lastUs = micros();

    for (;;) {
        float s = g_cmdSpeed;
        bool enabled = g_driverEnabled;
        // Prazo do pulso de dosagem: aqui, no core 0, o fim do pulso nao espera o loop.
        const uint32_t cutoff = g_doseCutoffMs;
        if (cutoff != 0 && static_cast<int32_t>(static_cast<uint32_t>(millis()) - cutoff) >= 0) {
            enabled = false;
        }

        uint32_t duty = 0;
        bool dirPos = true;
        
        if (enabled) {
            float sAbs = fabsf(s);
            sAbs = constrain(sAbs, 0.0f, V_MAX);
            dirPos = (s >= 0.0f);
            if (sAbs < ENABLE_EPS) {
                duty = 0;
            } else {
                float s_frac = (sAbs - ENABLE_EPS) / (V_MAX - ENABLE_EPS);
                s_frac = constrain(s_frac, 0.0f, 1.0f);
                duty = (uint32_t)lroundf(
                    s_frac * (float)(PWM_MAX_DUTY - PWM_BREAKAWAY) + PWM_BREAKAWAY
                );
                duty = constrain(duty, (uint32_t)PWM_BREAKAWAY, (uint32_t)PWM_MAX_DUTY);
            }
        }

        g_actualPwmDuty = duty;

        if (duty == 0) {
            ledcWrite(R_PWM_PIN, 0);
            ledcWrite(L_PWM_PIN, 0);
        } else {
            if (dirPos) {
                ledcWrite(R_PWM_PIN, duty);
                ledcWrite(L_PWM_PIN, 0);
            } else {
                ledcWrite(R_PWM_PIN, 0);
                ledcWrite(L_PWM_PIN, duty);
            }
        }

        uint32_t nowUs = micros();
        float dt_sec = (nowUs - lastUs) * 1e-6f;
        lastUs = nowUs;

        float q_actual_ml_per_sec = 0.0f;
        if (duty >= PWM_BREAKAWAY) {
            float q_actual_mlmin = pwmDutyToMlmin(duty);
            q_actual_ml_per_sec = q_actual_mlmin / 60.0f;
        }

        // Em dosagem o volume vem da vazao medida pelo operador (DoseController.h), nao da
        // calibracao polinomial: contar os dois somaria duas estimativas diferentes.
        if (q_actual_ml_per_sec != 0.0f && !g_doseActive) {
            taskDISABLE_INTERRUPTS();
            g_cumulativeVolumeMl += (q_actual_ml_per_sec * (double)dt_sec);
            taskENABLE_INTERRUPTS();
        }

        vTaskDelay(pdMS_TO_TICKS(TASK_DELAY_MS));
    }
}


