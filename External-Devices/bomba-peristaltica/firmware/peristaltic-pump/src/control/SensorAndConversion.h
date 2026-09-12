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
    if (fabsf(g_config.pumpSlope) < 1e-6f) return 0.0f;
    return (mlMin - g_config.pumpIntercept) / g_config.pumpSlope;
}

float speedUnitsToMlmin(float speedUnits) {
    return speedUnits * g_config.pumpSlope + g_config.pumpIntercept;
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

