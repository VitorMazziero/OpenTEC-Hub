import 'package:flutter/foundation.dart';
import '../models/peristaltic_pump_state.dart';
import '../services/hub_api_service.dart';
import '../services/pump_profile_math.dart';

class DeviceControlProvider with ChangeNotifier {
  final HubApiService Function() _getApiService;

  bool _isBusy = false;
  String _lastCommandStatus = "";

  DeviceControlProvider({required HubApiService Function() getApiService})
      : _getApiService = getApiService;

  bool get isBusy => _isBusy;
  String get lastCommandStatus => _lastCommandStatus;

  Future<bool> sendRawCommand(Map<String, dynamic> command) async {
    _isBusy = true;
    notifyListeners();

    final result = await _getApiService().sendCommand(command);
    _isBusy = false;

    if (result.success) {
      _lastCommandStatus = "Command accepted (${result.response})";
    } else {
      _lastCommandStatus = "Command failed: ${result.response}";
    }
    notifyListeners();
    return result.success;
  }

  // ==========================================
  // SERVO COMMANDS
  // ==========================================

  Future<bool> setMotorRpm(int rpm) async {
    final clampedRpm = rpm > 0 ? rpm.clamp(15, 1000) : 0;
    return sendRawCommand({"motorSetpoint": clampedRpm});
  }

  Future<bool> stopMotor() async {
    return sendRawCommand({"motorSetpoint": 0});
  }

  Future<bool> setMotorControlMode(int mode) async {
    // 0 = UART/CN1, 1 = Modbus direct
    final m = mode == 1 ? 1 : 0;
    return sendRawCommand({"motorControlMode": m});
  }

  Future<bool> setServoComm(bool enabled) async {
    return sendRawCommand({"servoComm": enabled ? 1 : 0});
  }

  Future<bool> resetServoEnergy() async {
    return sendRawCommand({"resetServoEnergy": 1});
  }

  Future<bool> setServoPollMs(int pollMs) async {
    final clamped = pollMs.clamp(250, 10000);
    return sendRawCommand({"servoPollMs": clamped});
  }

  // ==========================================
  // TEMPERATURE COMMANDS
  // ==========================================

  /// Reactor temperature reference. On the external-bath route the Hub uses it as the
  /// cascade reference, and `0` is the bath stop (C404 left in manual at its last SP).
  /// Out-of-range values are refused here instead of being clamped to a plausible one.
  Future<bool> setTemperature({required bool enabled, required double setpoint}) async {
    if (enabled && (!setpoint.isFinite || setpoint <= 0.0 || setpoint > 100.0)) {
      _lastCommandStatus = "Setpoint de temperatura inválido: $setpoint";
      notifyListeners();
      return false;
    }
    final sp = enabled ? setpoint : 0.0;
    return sendRawCommand({"tempSetpoint": sp});
  }

  // ==========================================
  // EXTERNAL BATH (Hub 10.6)
  // ==========================================

  /// Bath stop: cascade off, C404 sequence aborted and guard left in manual. The route
  /// and bath communication are kept; the bath itself cannot be switched off remotely.
  Future<bool> stopBath() async {
    return sendRawCommand({"bathAbort": 1});
  }

  Future<bool> resetBathFault() async {
    return sendRawCommand({"bathCascadeReset": 1});
  }

  /// C404 guard: auto reverts panel changes; the cascade only controls in auto.
  Future<bool> setBathMode({required bool automatic}) async {
    return sendRawCommand({"bathMode": automatic ? "auto" : "manual"});
  }

  // ==========================================
  // PH COMMANDS
  // ==========================================

  Future<bool> setPhControl({
    required bool enabled,
    required double setpoint,
    required double error,
    required int opTimeSeconds,
    required int mixTimeSeconds,
    required int speedPercent,
  }) async {
    return sendRawCommand({
      "pHSetpoint": enabled ? setpoint : 0.0,
      "pHError": error,
      "pHOperation": opTimeSeconds,
      "pHMix": mixTimeSeconds,
      "pHIntensity": enabled ? (speedPercent.clamp(0, 99) * 10) : 0,
    });
  }

  Future<bool> sendPhCalEcho(String calibratedPhString) async {
    return sendRawCommand({"pHCal": calibratedPhString});
  }

  // ==========================================
  // DISSOLVED OXYGEN COMMANDS
  // ==========================================

  Future<bool> setOxygenMonitor(bool enabled) async {
    return sendRawCommand({"oxygenMonitor": enabled ? 1 : 0});
  }

  // ==========================================
  // PRESSURE COMMANDS
  // ==========================================

  Future<bool> setPressure({required bool enabled, required int referenceMmHg}) async {
    final ref = enabled ? referenceMmHg.clamp(0, 500) : 0;
    return sendRawCommand({"pressureReference": ref});
  }

  // ==========================================
  // NUTRIENT PUMP (INTERNAL)
  // ==========================================

  Future<bool> setNutrientPump({
    required bool enabled,
    required int opSeconds,
    required int mixSeconds,
    required int opCycleMinutes,
    required int mixCycleMinutes,
    required int speedPercent,
  }) async {
    return sendRawCommand({
      "nutriOperation": opSeconds,
      "nutriMix": mixSeconds,
      "nutriOpCycle": opCycleMinutes,
      "nutriMixCycle": mixCycleMinutes,
      "nutriIntensity": enabled ? speedPercent.clamp(0, 99) : 0,
    });
  }

  // ==========================================
  // ANTIFOAM PUMP (INTERNAL)
  // ==========================================

  Future<bool> setAntifoamPump({
    required bool enabled,
    required int opSeconds,
    required int mixSeconds,
    required int speedPercent,
  }) async {
    return sendRawCommand({
      "antifoamOperation": opSeconds,
      "antifoamMix": mixSeconds,
      "antifoamIntensity": enabled ? speedPercent.clamp(0, 99) : 0,
    });
  }

  // ==========================================
  // EXTERNAL PERIPHERALS: DISTANCE SENSOR
  // ==========================================

  Future<bool> setDistanceSensorComm(bool enabled) async {
    return sendRawCommand({"distanceSensorComm": enabled ? 1 : 0});
  }

  Future<bool> setDistanceSensorReference(double referenceMm) async {
    return sendRawCommand({"distanceSensorReference": referenceMm});
  }

  // ==========================================
  // EXTERNAL PERIPHERALS: BIOMASS SENSOR
  // ==========================================

  Future<bool> setBiomassComm(bool enabled) async {
    if (!enabled) {
      // Safe shutdown: issue stop first so optical sensor shuts down cleanly
      await stopBiomassAcquisition();
    }
    return sendRawCommand({"biomassComm": enabled ? 1 : 0});
  }

  Future<bool> startBiomassAcquisition() async {
    return sendRawCommand({"start": 1});
  }

  Future<bool> stopBiomassAcquisition() async {
    return sendRawCommand({"stop": 1});
  }

  Future<bool> zeroBiomassBlank() async {
    return sendRawCommand({"blank": 1});
  }

  Future<bool> setBiomassThresholds({
    required int low,
    required int high,
    required int opt,
  }) async {
    return sendRawCommand({
      "low": low,
      "high": high,
      "opt": opt,
    });
  }

  // ==========================================
  // EXTERNAL PERIPHERALS: FLOWMETER & VALVES
  // ==========================================

  Future<bool> setFlowmeterComm(bool enabled) async {
    if (!enabled) {
      // Safe shutdown: zero setpoint and shut off valves before dropping comm routing
      await stopFlow();
    }
    return sendRawCommand({"flowmeterComm": enabled ? 1 : 0});
  }

  Future<bool> setFlowSetpoint(double setpointLpm) async {
    final sp = setpointLpm < 0 ? 0.0 : setpointLpm;
    return sendRawCommand({
      "flowSetpoint": sp,
      "v_Flow": sp > 0.0 ? 0 : 1,
    });
  }

  Future<bool> setFlowValves({
    bool? valve1,
    bool? valve2,
    bool? mainFlowOpen,
  }) async {
    final Map<String, dynamic> cmd = {};
    if (valve1 != null) cmd["valve_1"] = valve1 ? 1 : 0;
    if (valve2 != null) cmd["valve_2"] = valve2 ? 1 : 0;
    if (mainFlowOpen != null) cmd["v_Flow"] = mainFlowOpen ? 0 : 1;
    return sendRawCommand(cmd);
  }

  Future<bool> stopFlow() async {
    return sendRawCommand({
      "flowSetpoint": 0.0,
      "valve_1": 0,
      "valve_2": 0,
      "v_Flow": 1, // 1 = shutoff in firmware
    });
  }

  // ==========================================
  // EXTERNAL PERIPHERALS: FLASK AGITATOR
  // ==========================================

  Future<bool> setAgitatorState({
    required bool on,
    int? speedPercent,
    int? direction,
  }) async {
    final Map<String, dynamic> cmd = {"agitatorOn": on ? 1 : 0};
    if (speedPercent != null) {
      cmd["agitatorPercent"] = speedPercent.clamp(0, 100);
    }
    if (direction != null) {
      cmd["agitatorDir"] = direction == 1 ? 1 : 0;
    }
    return sendRawCommand(cmd);
  }

  Future<bool> stopAgitator({bool lockoutPot = false}) async {
    final Map<String, dynamic> cmd = {"agitatorOn": 0};
    if (lockoutPot) {
      cmd["agitatorReEnablePot"] = 0;
    }
    return sendRawCommand(cmd);
  }

  /// Emergency/Safe stop of flask agitator, locking out the bench potentiometer
  /// so it cannot inadvertently restart rotation.
  Future<bool> safeStopAgitator() async {
    return stopAgitator(lockoutPot: true);
  }

  Future<bool> setAgitatorSettings({
    bool? autoFoam,
    bool? reEnablePot,
  }) async {
    final Map<String, dynamic> cmd = {};
    if (autoFoam != null) cmd["agitatorAuto"] = autoFoam ? 1 : 0;
    if (reEnablePot != null) cmd["agitatorReEnablePot"] = reEnablePot ? 1 : 0;
    return sendRawCommand(cmd);
  }

  // ==========================================
  // EXTERNAL PERIPHERALS: PERISTALTIC PUMP
  // ==========================================

  /// Enables or disables external peristaltic pump routing on the Hub.
  ///
  /// CRITICAL: When disabling routing, an ordered two-step shutdown sequence
  /// is performed: first halting the active profile with {"mode": 0, "speed": 0},
  /// followed by {"pumpComm": 0}.
  Future<bool> setPumpComm(bool enabled) async {
    if (!enabled) {
      await stopPump();
      return sendRawCommand({"pumpComm": 0});
    }
    return sendRawCommand({"pumpComm": 1});
  }

  /// Safely halts the pump dosing profile (mode 0, speed 0).
  Future<bool> stopPump() async {
    return sendRawCommand({
      "mode": 0,
      "speed": 0,
    });
  }

  /// Dispatches a mathematical dosing profile to the external peristaltic pump.
  Future<bool> applyPumpProfile(PumpProfileSpec spec) async {
    final Map<String, dynamic> cmd = {
      "mode": spec.mode.code,
      "init_t": spec.initMinutes,
      "final_t": spec.finalMinutes,
    };

    switch (spec.mode) {
      case PeristalticPumpMode.constant:
        cmd["lambda_const"] = spec.lambda;
        break;
      case PeristalticPumpMode.linear:
        cmd["lambda_linear"] = spec.lambda;
        cmd["phi_linear"] = spec.phi;
        break;
      case PeristalticPumpMode.exponential:
        cmd["lambda_exp"] = spec.lambda;
        cmd["phi_exp"] = spec.phi;
        break;
      case PeristalticPumpMode.polynomial:
        for (int i = 0; i < spec.polynomialCoefficients.length; i++) {
          cmd["p$i"] = spec.polynomialCoefficients[i];
        }
        break;
      case PeristalticPumpMode.piecewise:
        cmd["num_segments"] = spec.piecewiseTimes.length;
        for (int i = 0; i < spec.piecewiseTimes.length; i++) {
          cmd["t$i"] = spec.piecewiseTimes[i];
          cmd["q$i"] = spec.piecewiseFlows[i];
        }
        break;
      case PeristalticPumpMode.stop:
        cmd["speed"] = 0;
        break;
    }

    return sendRawCommand(cmd);
  }

  // ==========================================
  // SYSTEM LEVEL COMMANDS
  // ==========================================

  /// Safely disables every subsystem in the Phase 1 core loop in one atomic command,
  /// matching Windows App's CoreSafeStop.
  Future<bool> coreSafeStop({double maxFlow = 5.0}) async {
    return sendRawCommand({
      "tempSetpoint": 0.0,
      "motorSetpoint": 0,
      "oxygenMonitor": 0,
      "flowSetpoint": 0.0,
      "maxFlow": maxFlow,
      "valve_1": 0,
      "valve_2": 0,
      "v_Flow": 1,
      "pressureReference": 0,
    });
  }

  /// Emergency Stop / Safe Reset of all process variables
  Future<bool> emergencyStopAll() async {
    return sendRawCommand({"resetVariables": 1});
  }

  /// Software reboot of ESP32-S3 Hub
  Future<bool> rebootHub() async {
    return sendRawCommand({"restart": 1});
  }

  /// Communication loopback test
  Future<bool> comTest() async {
    return sendRawCommand({"comTest": 1});
  }
}
