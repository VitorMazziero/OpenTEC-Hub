void loadParameters() {
  EEPROM.get(0, calParams);
  bool oldRecord = calParams.magic == CALIBRATION_MAGIC_V2 ||
                   calParams.magic == CALIBRATION_MAGIC_V3 ||
                   calParams.magic == CALIBRATION_MAGIC_V4;
  if (oldRecord) {
    // Same layout prefix. ff_* is valid from v3 on; the curve is deliberately
    // replaced by the factory one and the PI gains are re-seeded for the V07
    // measurement chain (the old Ki=0.1 needed a minute to remove 3 L/min).
    if (calParams.magic == CALIBRATION_MAGIC_V2) {
      calParams.ff_gain = FF_GAIN_DEFAULT;
      calParams.ff_offset = FF_OFFSET_DEFAULT;
    }
    applyFactoryCurve(calParams);
    calParams.kp = KP_DEFAULT;
    calParams.ki = KI_DEFAULT;
    calParams.ramp_rate = RAMP_RATE_DEFAULT;
    calParams.dac_hold = DAC_HOLD_DEFAULT;
    calParams.magic = CALIBRATION_MAGIC;
    EEPROM.put(0, calParams);
    EEPROM.commit();
    Serial.println("Calibration record migrated to v5: factory curve, Kp/Ki, ramp and hold defaults applied; ff_* kept.");
  } else if (calParams.magic != CALIBRATION_MAGIC) {
    calParams.magic = CALIBRATION_MAGIC;
    applyFactoryCurve(calParams);
    calParams.kp = KP_DEFAULT;
    calParams.ki = KI_DEFAULT;
    calParams.ff_gain = FF_GAIN_DEFAULT;
    calParams.ff_offset = FF_OFFSET_DEFAULT;
    calParams.ramp_rate = RAMP_RATE_DEFAULT;
    calParams.dac_hold = DAC_HOLD_DEFAULT;

    EEPROM.put(0, calParams);
    EEPROM.commit();
    Serial.println("Calibration parameters reset to defaults.");
  } else {
    Serial.println("Calibration parameters loaded.");
  }
  a1 = calParams.a1; b1 = calParams.b1;
  k1 = calParams.k1; f1 = calParams.f1; c1 = calParams.c1;
  k2 = calParams.k2; f2 = calParams.f2; c2 = calParams.c2;

  Kp_flow = calParams.kp;
  Ki_flow = calParams.ki;
  ffGain = calParams.ff_gain;
  ffOffset = calParams.ff_offset;
  rampRate = calParams.ramp_rate;
  dacHold = calParams.dac_hold != 0.0f;
  Serial.printf("Feedforward: corrected = %.4f * real + %.4f\n", ffGain, ffOffset);
  Serial.printf("PI: Kp=%.3f Ki=%.3f  ramp_rate=%.2f L/min/s  dac_hold=%s\n",
                Kp_flow, Ki_flow, rampRate, dacHold ? "ON" : "OFF");
}

// Setpoint corrigido for a given setpoint real. Zero stays zero: the MFC must be
// fully closed when nothing is requested, whatever the offset says.
float feedforwardSetpoint(float target) {
  if (target <= 0.0f) return 0.0f;
  return constrain(ffGain * target + ffOffset, 0.0f, maxFlowRate);
}
void saveParameters() {
  EEPROM.put(0, calParams);
  EEPROM.commit();
  Serial.println("Params Saved.");
}
