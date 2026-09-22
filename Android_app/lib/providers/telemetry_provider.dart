import 'dart:async';
import 'package:flutter/foundation.dart';
import '../constants/api_constants.dart';
import '../models/internal_telemetry.dart';
import '../models/servo_state.dart';
import '../models/distance_sensor_state.dart';
import '../models/biomass_sensor_state.dart';
import '../models/flowmeter_state.dart';
import '../models/flask_agitator_state.dart';
import '../models/peristaltic_pump_state.dart';
import '../models/external_bath_state.dart';
import '../services/signal_filter_service.dart';

class TelemetryDataPoint {
  final double time; // seconds
  final double value;
  TelemetryDataPoint(this.time, this.value);
}

class TelemetryProvider with ChangeNotifier {
  InternalTelemetry _telemetry = InternalTelemetry.empty();
  ServoState _servoState = ServoState.empty();
  DistanceSensorState _distanceState = DistanceSensorState.empty();
  BiomassSensorState _biomassState = BiomassSensorState.empty();
  FlowmeterState _flowmeterState = FlowmeterState.empty();
  FlaskAgitatorState _agitatorState = FlaskAgitatorState.empty();
  PeristalticPumpState _pumpState = PeristalticPumpState.empty();
  ExternalBathState _bathState = ExternalBathState.empty();
  final SignalFilterService _filterService = SignalFilterService();

  double _calibratedPh = -1.0;
  double _calibratedOxygen = -1.0;

  // History buffers for plotting
  final List<TelemetryDataPoint> _tempHistory = [];
  final List<TelemetryDataPoint> _phHistory = [];
  final List<TelemetryDataPoint> _oxygenHistory = [];
  final List<TelemetryDataPoint> _pressureHistory = [];
  final List<TelemetryDataPoint> _servoActualRpmHistory = [];
  final List<TelemetryDataPoint> _servoAppliedRpmHistory = [];
  final List<TelemetryDataPoint> _servoTorqueHistory = [];
  final List<TelemetryDataPoint> _servoPowerHistory = [];
  final List<TelemetryDataPoint> _distanceHistory = [];
  final List<TelemetryDataPoint> _biomassHistory = [];
  final List<TelemetryDataPoint> _flowRateHistory = [];
  final List<TelemetryDataPoint> _agitatorSpeedHistory = [];
  final List<TelemetryDataPoint> _pumpFlowHistory = [];
  final List<TelemetryDataPoint> _pumpVolumeHistory = [];

  StreamSubscription<Map<String, dynamic>>? _subscription;

  // Callback to dispatch pHCal echo back to hub
  Future<void> Function(String phCalString)? onPhCalChanged;

  // Getters
  InternalTelemetry get telemetry => _telemetry;
  ServoState get servoState => _servoState;
  DistanceSensorState get distanceState => _distanceState;
  BiomassSensorState get biomassState => _biomassState;
  FlowmeterState get flowmeterState => _flowmeterState;
  FlaskAgitatorState get agitatorState => _agitatorState;
  PeristalticPumpState get pumpState => _pumpState;
  ExternalBathState get bathState => _bathState;
  SignalFilterService get filterService => _filterService;

  double get calibratedPh => _calibratedPh;
  double get calibratedOxygen => _calibratedOxygen;

  List<TelemetryDataPoint> get tempHistory => List.unmodifiable(_tempHistory);
  List<TelemetryDataPoint> get phHistory => List.unmodifiable(_phHistory);
  List<TelemetryDataPoint> get oxygenHistory => List.unmodifiable(_oxygenHistory);
  List<TelemetryDataPoint> get pressureHistory => List.unmodifiable(_pressureHistory);
  List<TelemetryDataPoint> get servoActualRpmHistory => List.unmodifiable(_servoActualRpmHistory);
  List<TelemetryDataPoint> get servoAppliedRpmHistory => List.unmodifiable(_servoAppliedRpmHistory);
  List<TelemetryDataPoint> get servoTorqueHistory => List.unmodifiable(_servoTorqueHistory);
  List<TelemetryDataPoint> get servoPowerHistory => List.unmodifiable(_servoPowerHistory);
  List<TelemetryDataPoint> get distanceHistory => List.unmodifiable(_distanceHistory);
  List<TelemetryDataPoint> get biomassHistory => List.unmodifiable(_biomassHistory);
  List<TelemetryDataPoint> get flowRateHistory => List.unmodifiable(_flowRateHistory);
  List<TelemetryDataPoint> get agitatorSpeedHistory => List.unmodifiable(_agitatorSpeedHistory);
  List<TelemetryDataPoint> get pumpFlowHistory => List.unmodifiable(_pumpFlowHistory);
  List<TelemetryDataPoint> get pumpVolumeHistory => List.unmodifiable(_pumpVolumeHistory);

  void attachTelemetryStream(Stream<Map<String, dynamic>> stream) {
    _subscription?.cancel();
    _subscription = stream.listen(updateFromJson);
  }

  void updateFromJson(Map<String, dynamic> json) {
    _telemetry = InternalTelemetry.fromJson(json);
    _servoState = ServoState.fromJson(json);
    _distanceState = DistanceSensorState.fromJson(json);
    _biomassState = BiomassSensorState.fromJson(json);
    _flowmeterState = FlowmeterState.fromJson(json);
    _agitatorState = FlaskAgitatorState.fromJson(json);
    _pumpState = PeristalticPumpState.fromJson(json);
    _bathState = ExternalBathState.fromJson(json);

    // Compute calibrated values
    if (_telemetry.hasValidPh) {
      _calibratedPh = _filterService.rawToPh(_telemetry.rawPh);
      final echo = _filterService.getPhCalEchoIfChanged(_calibratedPh);
      if (echo != null && onPhCalChanged != null) {
        onPhCalChanged!(echo);
      }
    } else {
      _calibratedPh = -1.0;
    }

    if (_telemetry.hasValidOxygen) {
      _calibratedOxygen = _filterService.rawToOxygen(_telemetry.rawOxygen);
    } else {
      _calibratedOxygen = -1.0;
    }

    // Append to histories
    final t = _telemetry.time;
    if (_telemetry.hasValidTemperature) {
      _appendHistory(_tempHistory, t, _telemetry.temperature);
    }
    if (_calibratedPh >= 0.0) {
      _appendHistory(_phHistory, t, _calibratedPh);
    }
    if (_calibratedOxygen >= 0.0) {
      _appendHistory(_oxygenHistory, t, _calibratedOxygen);
    }
    if (_telemetry.pressure >= 0.0) {
      _appendHistory(_pressureHistory, t, _telemetry.pressure);
    }

    // Servo histories
    if (_servoState.online) {
      _appendHistory(_servoAppliedRpmHistory, t, _servoState.appliedRpm.toDouble());
      if (_servoState.hasTelemetry) {
        _appendHistory(_servoActualRpmHistory, t, _servoState.rpm);
        _appendHistory(_servoTorqueHistory, t, _servoState.torquePct);
        _appendHistory(_servoPowerHistory, t, _servoState.powerW);
      }
    }

    // Distance sensor history (only when actively connected)
    if (_distanceState.isConnectedAndActive) {
      _appendHistory(_distanceHistory, t, _distanceState.distanceMm);
    }

    // Biomass sensor history (only when acquiring optical samples)
    if (_biomassState.isAcquiring) {
      _appendHistory(_biomassHistory, t, _biomassState.absorbance);
    }

    // Flowmeter history (only when actively connected and measuring flow)
    if (_flowmeterState.isConnectedAndActive) {
      _appendHistory(_flowRateHistory, t, _flowmeterState.flowRate);
    }

    // Flask Agitator history (only when online and reporting)
    if (_agitatorState.isConnectedAndActive) {
      _appendHistory(_agitatorSpeedHistory, t, _agitatorState.speedPercent);
    }

    // Peristaltic Pump history (only when actively connected and routing)
    if (_pumpState.isConnectedAndActive) {
      _appendHistory(_pumpFlowHistory, t, _pumpState.flow);
      _appendHistory(_pumpVolumeHistory, t, _pumpState.volume);
    }

    notifyListeners();
  }

  void _appendHistory(List<TelemetryDataPoint> list, double time, double value) {
    list.add(TelemetryDataPoint(time, value));
    if (list.length > ApiConstants.maxTelemetryHistoryPoints) {
      list.removeAt(0);
    }
  }

  void clearHistory() {
    _tempHistory.clear();
    _phHistory.clear();
    _oxygenHistory.clear();
    _pressureHistory.clear();
    _servoActualRpmHistory.clear();
    _servoAppliedRpmHistory.clear();
    _servoTorqueHistory.clear();
    _servoPowerHistory.clear();
    _distanceHistory.clear();
    _biomassHistory.clear();
    _flowRateHistory.clear();
    _agitatorSpeedHistory.clear();
    _pumpFlowHistory.clear();
    _pumpVolumeHistory.clear();
    _filterService.resetEchoTracking();
    notifyListeners();
  }

  @override
  void dispose() {
    _subscription?.cancel();
    super.dispose();
  }
}
