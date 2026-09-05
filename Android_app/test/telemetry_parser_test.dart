import 'package:flutter_test/flutter_test.dart';
import 'package:tecnal_app/models/internal_telemetry.dart';
import 'package:tecnal_app/models/servo_state.dart';
import 'package:tecnal_app/services/signal_filter_service.dart';

void main() {
  group('Hub v10 Telemetry Parser Tests', () {
    test('Parses internal sensors and system status accurately', () {
      final sampleJson = {
        "HubFirmwareVersion": "10.0.0-dev",
        "HubProtocolVersion": 10,
        "Time": 142.5,
        "Tempval": 36.52,
        "pHval": 15200.0,
        "Oxyval": 980.0,
        "Antifoam": 0.0,
        "Pressure": 102.4,
        "HubStations": 2,
        "SensorCommOK": true,
        // External devices that must be ignored
        "FlowmeterOnline": true,
        "FlowRate": 2.5,
        "BiomassOnline": false,
        "Distance": 120.0,
      };

      final telemetry = InternalTelemetry.fromJson(sampleJson);

      expect(telemetry.hubFirmwareVersion, "10.0.0-dev");
      expect(telemetry.hubProtocolVersion, 10);
      expect(telemetry.time, 142.5);
      expect(telemetry.temperature, 36.52);
      expect(telemetry.rawPh, 15200.0);
      expect(telemetry.rawOxygen, 980.0);
      expect(telemetry.pressure, 102.4);
      expect(telemetry.antifoam, 0.0);
      expect(telemetry.hubStations, 2);
      expect(telemetry.sensorCommOk, isTrue);
      expect(telemetry.hasValidTemperature, isTrue);
      expect(telemetry.hasValidPh, isTrue);
      expect(telemetry.hasValidOxygen, isTrue);
    });

    test('Parses Servo ASDA-B2 state, diagnostics, and feedback', () {
      final sampleJson = {
        "ServoOnline": true,
        "ServoCommEnabled": true,
        "ServoControlCapable": true,
        "MotorControlViaModbus": true,
        "ServoMotorCommandId": 42,
        "ServoMotorCommandAck": 42,
        "ServoMotorRouteAck": 1,
        "ServoMotorCommandPending": false,
        "ServoMotorCommandDeliveries": 1,
        "ServoMotorCommandAgeMs": 15,
        "ServoMotorRequestedRpm": 300,
        "ServoMotorAppliedRpm": 300,
        "ServoMotorLeaseMs": 3000,
        "ServoMotorEnabled": true,
        "ServoMotorControlActive": true,
        "ServoMotorControlFault": 0,
        "ServoRpm": 300.2,
        "ServoTorquePct": 18.5,
        "ServoTorqueNm": 0.58,
        "ServoLoadPct": 22.0,
        "ServoPowerW": 45.3,
        "ServoEnergyWh": 1.25,
        "ServoState": 2,
        "ServoAlarm": 0,
        "ServoCommOk": 100,
        "ServoCommErr": 0,
      };

      final servo = ServoState.fromJson(sampleJson);

      expect(servo.online, isTrue);
      expect(servo.commEnabled, isTrue);
      expect(servo.controlCapable, isTrue);
      expect(servo.viaModbus, isTrue);
      expect(servo.commandId, 42);
      expect(servo.commandAck, 42);
      expect(servo.requestedRpm, 300);
      expect(servo.appliedRpm, 300);
      expect(servo.rpm, 300.2);
      expect(servo.torquePct, 18.5);
      expect(servo.powerW, 45.3);
      expect(servo.isRunning, isTrue);
      expect(servo.isFaulted, isFalse);
      expect(servo.stateDescription, "Running");
    });

    test('Identifies drive alarm / fault state', () {
      final sampleJson = {
        "ServoOnline": true,
        "ServoCommEnabled": true,
        "ServoControlCapable": false,
        "ServoMotorRequestedRpm": 0,
        "ServoMotorAppliedRpm": 0,
        "ServoMotorEnabled": false,
        "ServoMotorControlActive": false,
        "ServoMotorControlFault": 0,
        "ServoState": 3,
        "ServoAlarm": 9, // AL009
      };

      final servo = ServoState.fromJson(sampleJson);
      expect(servo.isFaulted, isTrue);
      expect(servo.stateDescription, "Alarm: AL009");
    });
  });

  group('Signal Filter & Calibration Tests', () {
    test('Calibrates raw ADC counts to pH and generates invariant dot string', () {
      final filter = SignalFilterService();
      // Using standard defaults: slope ~0.00050124, intercept ~ -0.60038
      // For raw ADC ~15163, pH ~ 7.00
      final ph = filter.rawToPh(15163.0);
      expect(ph, greaterThan(6.9));
      expect(ph, lessThan(7.1));

      final echo1 = filter.getPhCalEchoIfChanged(ph);
      expect(echo1, isNotNull);
      expect(echo1, contains(".")); // Must use invariant dot, not comma
      expect(echo1!.length, 4); // "7.00"

      // Subsequent identical calls return null (no unnecessary duplicate transmissions)
      final echo2 = filter.getPhCalEchoIfChanged(ph);
      expect(echo2, isNull);
    });

    test('Calibrates Dissolved Oxygen raw ADC', () {
      final filter = SignalFilterService();
      // raw <= 0.1 returns -1.0
      expect(filter.rawToOxygen(0.0), -1.0);
      // valid counts yield positive DO
      final oxygen = filter.rawToOxygen(1000.0);
      expect(oxygen, greaterThan(0.0));
    });
  });
}
