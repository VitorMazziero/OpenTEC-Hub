import 'package:flutter_test/flutter_test.dart';
import 'package:tecnal_app/models/internal_telemetry.dart';
import 'package:tecnal_app/providers/device_control_provider.dart';
import 'package:tecnal_app/services/hub_api_service.dart';

class _RecordingApi extends HubApiService {
  final List<Map<String, dynamic>> sent = [];

  @override
  Future<({bool success, int statusCode, String response})> sendCommand(
      Map<String, dynamic> command) async {
    sent.add(command);
    return (success: true, statusCode: 200, response: "Queued");
  }
}

void main() {
  late _RecordingApi api;
  late DeviceControlProvider provider;

  setUp(() {
    api = _RecordingApi();
    provider = DeviceControlProvider(getApiService: () => api);
  });

  group('Temperature route (Hub 10.6)', () {
    test('route goes alone, without a setpoint in the same frame', () async {
      await provider.setTemperatureRoute(externalBath: true);
      await provider.setTemperatureRoute(externalBath: false);
      expect(api.sent, [
        {"tempControlMode": 1},
        {"tempControlMode": 0},
      ]);
    });

    test('bath link toggle', () async {
      await provider.setBathComm(true);
      await provider.setBathComm(false);
      expect(api.sent, [
        {"bathComm": 1},
        {"bathComm": 0},
      ]);
    });

    test('telemetry exposes the commanded reference', () {
      final on = InternalTelemetry.fromJson({"TempSetpoint": 32.5, "TempSetpointCommanded": true});
      expect(on.tempSetpoint, 32.5);
      expect(on.tempSetpointCommanded, isTrue);

      // External route with no active command publishes null.
      final off = InternalTelemetry.fromJson({"TempSetpoint": null, "TempSetpointCommanded": false});
      expect(off.tempSetpoint, isNull);
      expect(off.tempSetpointCommanded, isFalse);
    });
  });

  group('Automatic foam response', () {
    test('sends reference, timings and agitator flag in one command', () async {
      final ok = await provider.setFoamResponse(
        referenceMm: 80,
        startDelaySeconds: 2,
        pulseSeconds: 1.5,
        intervalSeconds: 10,
        useAgitator: true,
      );
      expect(ok, isTrue);
      expect(api.sent.single, {
        "distanceSensorReference": 80.0,
        "foamStartDelay_s": 2.0,
        "foamPulse_s": 1.5,
        "foamInterval_s": 10.0,
        "agitatorAuto": 1,
      });
    });

    test('reference 0 disables the logic', () async {
      await provider.setFoamResponse(
        referenceMm: 0,
        startDelaySeconds: 1,
        pulseSeconds: 1,
        intervalSeconds: 5,
        useAgitator: false,
      );
      expect(api.sent.single["distanceSensorReference"], 0.0);
      expect(api.sent.single["agitatorAuto"], 0);
    });

    test('refuses a zero pulse or interval instead of sending it', () async {
      final ok = await provider.setFoamResponse(
        referenceMm: 80,
        startDelaySeconds: 1,
        pulseSeconds: 0,
        intervalSeconds: 5,
        useAgitator: false,
      );
      expect(ok, isFalse);
      expect(api.sent, isEmpty);
    });
  });
}
