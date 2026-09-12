void pwmTask(void* pv) {
    uint32_t lastUs = micros();

    for (;;) {
        float s = g_cmdSpeed;
        bool enabled = g_driverEnabled;

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

        if (q_actual_ml_per_sec != 0.0f) {
            taskDISABLE_INTERRUPTS();
            g_cumulativeVolumeMl += (q_actual_ml_per_sec * (double)dt_sec);
            taskENABLE_INTERRUPTS();
        }

        vTaskDelay(pdMS_TO_TICKS(TASK_DELAY_MS));
    }
}


