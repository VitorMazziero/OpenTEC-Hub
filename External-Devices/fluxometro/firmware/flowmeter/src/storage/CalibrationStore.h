static inline uint32_t calculateCalibrationCrc(const CalibrationParams& p) {
  const uint8_t* data = (const uint8_t*)&p + sizeof(p.magic);
  size_t length = sizeof(p) - sizeof(p.magic);
  uint32_t crc = 0xFFFFFFFF;
  for (size_t i = 0; i < length; i++) {
    crc ^= data[i];
    for (uint8_t j = 0; j < 8; j++) {
      crc = (crc >> 1) ^ (0xEDB88320 & (-(crc & 1)));
    }
  }
  return ~crc;
}

void loadParameters() {
  EEPROM.get(0, calParams);
  if (calParams.magic == CALIBRATION_MAGIC_V6) {
    // Migracao limpa de V6 para V7:
    // Preserva coeficientes de calibracao laboratorial (a1..c2), sintonia PI, feedforward e max_flow.
    // Inicializa somente o novo campo transition_v com 0.0545f.
    calParams.transition_v = TRANSITION_V_DEFAULT;
    calParams.magic = CALIBRATION_MAGIC;
    EEPROM.put(0, calParams);
    EEPROM.commit();
    Serial.println("[EEPROM] Migrado de V6 para V7: calibracao e max_flow preservados; transition_v=0.0545 V.");
  } else if (calParams.magic == CALIBRATION_MAGIC_V5) {
    // Migracao limpa de V5 para V7:
    calParams.max_flow = MAX_FLOW_DEFAULT;
    calParams.transition_v = TRANSITION_V_DEFAULT;
    calParams.magic = CALIBRATION_MAGIC;
    EEPROM.put(0, calParams);
    EEPROM.commit();
    Serial.println("[EEPROM] Migrado de V5 para V7: calibracao preservada; max_flow=50.0 L/min, transition_v=0.0545 V.");
  } else if (calParams.magic == CALIBRATION_MAGIC_V2 ||
             calParams.magic == CALIBRATION_MAGIC_V3 ||
             calParams.magic == CALIBRATION_MAGIC_V4) {
    if (calParams.magic == CALIBRATION_MAGIC_V2) {
      calParams.ff_gain = FF_GAIN_DEFAULT;
      calParams.ff_offset = FF_OFFSET_DEFAULT;
    }
    applyFactoryCurve(calParams);
    calParams.kp = KP_DEFAULT;
    calParams.ki = KI_DEFAULT;
    calParams.ramp_rate = RAMP_RATE_DEFAULT;
    calParams.dac_hold = DAC_HOLD_DEFAULT;
    calParams.max_flow = MAX_FLOW_DEFAULT;
    calParams.transition_v = TRANSITION_V_DEFAULT;
    calParams.magic = CALIBRATION_MAGIC;
    EEPROM.put(0, calParams);
    EEPROM.commit();
    Serial.println("[EEPROM] Registro legado migrado para V7 com defaults.");
  } else if (calParams.magic != CALIBRATION_MAGIC) {
    // Fallback flash virgem: inicializacao explicita obrigatoria
    calParams.magic = CALIBRATION_MAGIC;
    applyFactoryCurve(calParams);
    calParams.kp = KP_DEFAULT;
    calParams.ki = KI_DEFAULT;
    calParams.ff_gain = FF_GAIN_DEFAULT;
    calParams.ff_offset = FF_OFFSET_DEFAULT;
    calParams.ramp_rate = RAMP_RATE_DEFAULT;
    calParams.dac_hold = DAC_HOLD_DEFAULT;
    calParams.max_flow = MAX_FLOW_DEFAULT;
    calParams.transition_v = TRANSITION_V_DEFAULT;

    EEPROM.put(0, calParams);
    EEPROM.commit();
    Serial.println("[EEPROM] Parametros reinicializados para padrao de fabrica (Schema V7).");
  } else {
    Serial.println("[EEPROM] Parametros de calibracao carregados com sucesso (Schema V7).");
  }

  // Validacao defensiva pos-carga contra flash degradada
  if (isnan(calParams.max_flow) || calParams.max_flow <= 0.01f || calParams.max_flow > 500.0f) {
    calParams.max_flow = MAX_FLOW_DEFAULT;
    EEPROM.put(0, calParams);
    EEPROM.commit();
  }
  if (isnan(calParams.transition_v) || calParams.transition_v <= 0.0f || calParams.transition_v >= 3.3f) {
    calParams.transition_v = TRANSITION_V_DEFAULT;
    EEPROM.put(0, calParams);
    EEPROM.commit();
    Serial.println("[EEPROM] Aviso: transition_v invalido fora de (0, 3.3) V, restaurado default 0.0545 V.");
  }

  // Propagacao obrigatoria para as variaveis ativas em RAM
  a1 = calParams.a1; b1 = calParams.b1;
  k1 = calParams.k1; f1 = calParams.f1; c1 = calParams.c1;
  k2 = calParams.k2; f2 = calParams.f2; c2 = calParams.c2;

  flowTransitionVoltage = calParams.transition_v;

  Kp_flow = calParams.kp;
  Ki_flow = calParams.ki;
  ffGain = calParams.ff_gain;
  ffOffset = calParams.ff_offset;
  rampRate = calParams.ramp_rate;
  dacHold = calParams.dac_hold != 0.0f;
  maxFlowRate = calParams.max_flow;

  currentCalCrc = calculateCalibrationCrc(calParams);

  Serial.printf("Feedforward: corrected = %.4f * real + %.4f\n", ffGain, ffOffset);
  Serial.printf("PI: Kp=%.3f Ki=%.3f  ramp_rate=%.2f L/min/s  dac_hold=%s  max_flow=%.2f L/min  transition_v=%.4f V (CRC:%08X)\n",
                Kp_flow, Ki_flow, rampRate, dacHold ? "ON" : "OFF", maxFlowRate, flowTransitionVoltage, currentCalCrc);
}

// Setpoint corrigido for a given setpoint real. Zero stays zero: the MFC must be
// fully closed when nothing is requested, whatever the offset says.
float feedforwardSetpoint(float target) {
  if (target <= 0.0f) return 0.0f;
  return constrain(ffGain * target + ffOffset, 0.0f, maxFlowRate);
}

void saveParameters() {
  currentCalCrc = calculateCalibrationCrc(calParams);
  EEPROM.put(0, calParams);
  EEPROM.commit();
  Serial.printf("[EEPROM] Params Saved. CRC: %08X\n", currentCalCrc);
}
