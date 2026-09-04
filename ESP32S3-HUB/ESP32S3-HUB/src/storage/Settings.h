// ============ PERSISTENCE ============
Preferences preferences;

// ============ MEMORY SAVE FLAGS ============
volatile bool flagPendingSave = false;
unsigned long lastSaveTriggerTime = 0;
const unsigned long SAVE_DEBOUNCE_MS = 15000; // Aguarda 15s de inatividade antes de gravar na Flash

void saveSettings() {
  preferences.putFloat("tempRef", tempReference);
  preferences.putFloat("pHRef", pHReference);
  preferences.putFloat("pHErr", pHError);
  preferences.putInt("pHCal", pHCal);
  preferences.putInt("pHOp", pHOperation);
  preferences.putInt("pHMix", pHMix);
  preferences.putInt("pHInt", pHIntensity);
  preferences.putInt("motorRPM", motorRPM);
  preferences.putBool("motorModbus", motorControlRoute == MotorControlRoute::Modbus);
  preferences.putInt("nutriOp", nutriOperation);
  preferences.putInt("nutriMix", nutriMix);
  preferences.putInt("nutriOpCycle", nutriOpCycle);
  preferences.putInt("nutriMixCycle", nutriMixCycle);
  preferences.putInt("nutriInt", nutriIntensity);
  preferences.putInt("antiOp", antifoamOperation);
  preferences.putInt("antiMix", antifoamMix);
  preferences.putInt("antiInt", antifoamIntensity);
  preferences.putInt("presRef", pressureReference);
  preferences.putFloat("distRef", distanceSensorReference);
  preferences.putBool("distComm", distanceSensorCommOn);
  preferences.putULong("dataDelay", dataDelay);
  preferences.putBool("oxyOn", oxyOn);
  preferences.putBool("tempOn", tempOn);
  preferences.putBool("phOn", phOn);
  preferences.putBool("nutrientOn", nutrientOn);
  preferences.putBool("antiOn", antifoamOn);
  preferences.putBool("presOn", pressureOn);
  preferences.putBool("agitAuto", agitatorAuto);
  preferences.putBool("agitRePot", agitatorReEnablePot);
  preferences.putInt("agitPct", agitatorPercentFoam);
  preferences.putInt("agitDir", agitatorDirFoam);
  preferences.putFloat("foamDelay", foamStartDelay_s);
  preferences.putFloat("foamPulse", foamPulse_s);
  preferences.putFloat("foamInterval", foamInterval_s);
  preferences.putBool("bioComm", biomassCommOn);
  preferences.putBool("flowComm", flowmeterControlEnabled);
  preferences.putBool("pumpComm", pumpCommOn);
  preferences.putBool("servoComm", servoCommOn);
}

void loadSettings() {
  tempReference       = preferences.getFloat("tempRef", 0.0f);
  pHReference         = preferences.getFloat("pHRef", 0.0f);
  pHError             = preferences.getFloat("pHErr", 0.17f);
  pHCal               = preferences.getInt("pHCal", 5);
  pHOperation         = preferences.getInt("pHOp", 5);
  pHMix               = preferences.getInt("pHMix", 10);
  pHIntensity         = preferences.getInt("pHInt", 990);
  motorRPM            = preferences.getInt("motorRPM", 0);
  motorControlRoute   = preferences.getBool("motorModbus", true)
                          ? MotorControlRoute::Modbus
                          : MotorControlRoute::UartCn1;
  nutriOperation      = preferences.getInt("nutriOp", 999);
  nutriMix            = preferences.getInt("nutriMix", 1);
  nutriOpCycle        = preferences.getInt("nutriOpCycle", 500);
  nutriMixCycle       = preferences.getInt("nutriMixCycle", 1);
  nutriIntensity      = preferences.getInt("nutriInt", 99);
  antifoamOperation   = preferences.getInt("antiOp", 999);
  antifoamMix         = preferences.getInt("antiMix", 1);
  antifoamIntensity   = preferences.getInt("antiInt", 99);
  pressureReference   = preferences.getInt("presRef", 100);
  distanceSensorReference = preferences.getFloat("distRef", 0.0f);
  dataDelay           = preferences.getULong("dataDelay", 1000);
  oxyOn               = preferences.getBool("oxyOn", false);
  tempOn              = preferences.getBool("tempOn", false);
  phOn                = preferences.getBool("phOn", false);
  nutrientOn          = preferences.getBool("nutrientOn", false);
  antifoamOn          = preferences.getBool("antiOn", false);
  pressureOn          = preferences.getBool("presOn", false);
  agitatorAuto        = preferences.getBool("agitAuto", true);
  agitatorReEnablePot = preferences.getBool("agitRePot", true);
  agitatorPercentFoam = preferences.getInt("agitPct", 80);
  agitatorDirFoam     = preferences.getInt("agitDir", 1);
  foamStartDelay_s    = preferences.getFloat("foamDelay", 1.0f);
  foamPulse_s         = preferences.getFloat("foamPulse", 1.0f);
  foamInterval_s      = preferences.getFloat("foamInterval", 5.0f);
  biomassCommOn       = preferences.getBool("bioComm", false);
  distanceSensorCommOn = preferences.getBool("distComm", false);
  flowmeterControlEnabled = preferences.getBool("flowComm", false);
  flowmeterCommOn = false;
  pumpCommOn = preferences.getBool("pumpComm", false);
  servoCommOn = preferences.getBool("servoComm", true);
}

void debugSettings() {
  ESP32_INFO("==== Loaded Preferences ====");
  ESP32_INFO(String("tempReference: ")          + tempReference);
  ESP32_INFO(String("pHReference: ")            + pHReference);
  ESP32_INFO(String("pHError: ")                + pHError);
  ESP32_INFO(String("pHCal: ")                  + pHCal);
  ESP32_INFO(String("pHOperation: ")            + pHOperation);
  ESP32_INFO(String("pHMix: ")                  + pHMix);
  ESP32_INFO(String("pHIntensity: ")            + pHIntensity);
  ESP32_INFO(String("motorRPM: ")               + motorRPM);
  ESP32_INFO(String("motorControlRoute: ")      +
             (motorControlRoute == MotorControlRoute::Modbus ? "Modbus" : "UART/CN1"));
  ESP32_INFO(String("nutriOperation: ")         + nutriOperation);
  ESP32_INFO(String("nutriMix: ")               + nutriMix);
  ESP32_INFO(String("nutriOpCycle: ")           + nutriOpCycle);
  ESP32_INFO(String("nutriMixCycle: ")          + nutriMixCycle);
  ESP32_INFO(String("nutriIntensity: ")         + nutriIntensity);
  ESP32_INFO(String("antifoamOperation: ")      + antifoamOperation);
  ESP32_INFO(String("antifoamMix: ")            + antifoamMix);
  ESP32_INFO(String("antifoamIntensity: ")      + antifoamIntensity);
  ESP32_INFO(String("pressureReference: ")      + pressureReference);
  ESP32_INFO(String("distanceSensorReference: ")+ distanceSensorReference);
  ESP32_INFO(String("distanceSensorCommOn: ")   + distanceSensorCommOn);
  ESP32_INFO(String("dataDelay: ")              + dataDelay);
  ESP32_INFO(String("oxyOn: ")                  + oxyOn);
  ESP32_INFO(String("tempOn: ")                 + tempOn);
  ESP32_INFO(String("phOn: ")                   + phOn);
  ESP32_INFO(String("nutrientOn: ")             + nutrientOn);
  ESP32_INFO(String("antifoamOn: ")             + antifoamOn);
  ESP32_INFO(String("pressureOn: ")             + pressureOn);
  ESP32_INFO(String("agitatorAuto: ")           + agitatorAuto);
  ESP32_INFO(String("agitatorReEnablePot: ")    + agitatorReEnablePot);
  ESP32_INFO(String("agitatorPercentFoam: ")    + agitatorPercentFoam);
  ESP32_INFO(String("agitatorDirFoam: ")        + agitatorDirFoam);
  ESP32_INFO(String("foamStartDelay_s: ")       + foamStartDelay_s);
  ESP32_INFO(String("foamPulse_s: ")            + foamPulse_s);
  ESP32_INFO(String("foamInterval_s: ")         + foamInterval_s);
  ESP32_INFO(String("biomassCommOn: ")          + biomassCommOn);
  ESP32_INFO(String("flowmeterTelemetryOnline: ") + flowmeterCommOn);
  ESP32_INFO(String("flowmeterControlEnabled: ") + flowmeterControlEnabled);
  ESP32_INFO(String("pumpCommOn: ")             + pumpCommOn);
  ESP32_INFO(String("servoCommOn: ")            + servoCommOn);
  ESP32_INFO(String("agitatorPotActive: ")      + agitatorPotActive);
  ESP32_INFO("============================");
}
