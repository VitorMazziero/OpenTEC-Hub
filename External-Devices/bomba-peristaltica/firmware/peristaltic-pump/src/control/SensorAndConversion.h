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

float speedUnitsToMlmin(float speedUnits) {
    if (speedUnits <= g_pumpCal.s_t) {
        return ((((g_pumpCal.a1 * speedUnits) + g_pumpCal.b1) * speedUnits + g_pumpCal.k1) * speedUnits + g_pumpCal.f1) * speedUnits + g_pumpCal.c1;
    } else {
        return (g_pumpCal.k2 * speedUnits + g_pumpCal.f2) * speedUnits + g_pumpCal.c2;
    }
}

float mlminToSpeedUnits(float mlMin) {
    float qTransition = speedUnitsToMlmin(g_pumpCal.s_t);
    float lower = mlMin <= qTransition ? 0.0f : g_pumpCal.s_t;
    float upper = mlMin <= qTransition ? g_pumpCal.s_t : 1000.0f;
    if (mlMin <= speedUnitsToMlmin(0.0f)) return 0.0f;
    if (mlMin >= speedUnitsToMlmin(1000.0f)) return 1000.0f;
    for (uint8_t i = 0; i < 32; i++) {
        float middle = (lower + upper) * 0.5f;
        if (speedUnitsToMlmin(middle) < mlMin) lower = middle;
        else upper = middle;
    }
    return (lower + upper) * 0.5f;
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

