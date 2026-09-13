void updateSensorGate() {
    static int lastSteadyState = HIGH;
    static int lastFlickerState = HIGH;
    static unsigned long lastDebounceTime = 0;

    int currentState = digitalRead(SENSOR_PIN);

    if (currentState != lastFlickerState) {
        lastDebounceTime = millis();
        lastFlickerState = currentState;
    }

    if ((millis() - lastDebounceTime) > SENSOR_DEBOUNCE_MS) {
        if (currentState != lastSteadyState) {
            lastSteadyState = currentState;
            sensorWetState = (lastSteadyState == LOW);
        }
    }
}

float mlminToSpeedUnits(float mlMin) {
    if (mlMin <= g_pumpCal.q_t) {
        if (fabsf(g_pumpCal.m_low) < 1e-6f) return 0.0f;
        return g_pumpCal.s_t + (mlMin - g_pumpCal.q_t) / g_pumpCal.m_low;
    } else {
        if (fabsf(g_pumpCal.m_high) < 1e-6f) return 0.0f;
        return g_pumpCal.s_t + (mlMin - g_pumpCal.q_t) / g_pumpCal.m_high;
    }
}

float speedUnitsToMlmin(float speedUnits) {
    if (speedUnits <= g_pumpCal.s_t) {
        return g_pumpCal.q_t + g_pumpCal.m_low * (speedUnits - g_pumpCal.s_t);
    } else {
        return g_pumpCal.q_t + g_pumpCal.m_high * (speedUnits - g_pumpCal.s_t);
    }
}

float pwmDutyToMlmin(int duty) {
    if (duty < PWM_BREAKAWAY) return 0.0f;
    float duty_active_range = (float)(duty - PWM_BREAKAWAY);
    float duty_max_range = (float)(PWM_MAX_DUTY - PWM_BREAKAWAY);
    float s_frac = constrain(duty_active_range / duty_max_range, 0.0f, 1.0f);
    float speed = ENABLE_EPS + s_frac * (V_MAX - ENABLE_EPS);
    return speedUnitsToMlmin(speed);
}

void applyDutyFromSpeed(float, bool) {}

