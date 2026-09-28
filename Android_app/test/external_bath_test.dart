import 'package:flutter_test/flutter_test.dart';
import 'package:tecnal_app/models/external_bath_state.dart';
import 'package:tecnal_app/providers/device_control_provider.dart';
import 'package:tecnal_app/services/hub_api_service.dart';

class _RecordingApi extends HubApiService {
  Map<String, dynamic>? last;

  @override
  Future<({bool success, int statusCode, String response})> sendCommand(
      Map<String, dynamic> command) async {
    last = command;
    return (success: true, statusCode: 200, response: "OK");
  }
}

void main() {
  group('External bath telemetry (Hub 10.6)', () {
    test('Old Hub without bath keys reports no bath telemetry', () {
      final s = ExternalBathState.fromJson({"Tempval": 30.0});
      expect(s.hasTelemetry, isFalse);
      expect(s.viaBath, isFalse);
      expect(s.summary, contains("sem suporte"));
    });

    test('Owned cascade on the external route is parsed', () {
      final s = ExternalBathState.fromJson({
        "TempControlViaBath": true,
        "BathOnline": true,
        "BathCommEnabled": true,
        "BathOwned": true,
        "BathCascadeActive": true,
        "BathCascadeState": "controlling",
        "BathPv": 31.4,
        "BathSp": 31.5,
        "BathCommandSetpoint": 31.5,
        "TempSetpoint": 30.0,
        "BathMode": 1,
      });
      expect(s.hasTelemetry, isTrue);
      expect(s.viaBath, isTrue);
      expect(s.owned, isTrue);
      expect(s.isAutomatic, isTrue);
      expect(s.bathPv, 31.4);
      expect(s.reactorSetpoint, 30.0);
      expect(s.summary, "Cascata controlando o reator");
    });

    test('Approach state (Hub 10.7) shows the reactor slope', () {
      final s = ExternalBathState.fromJson({
        "TempControlViaBath": true,
        "BathOnline": true,
        "BathCommEnabled": true,
        "BathCascadeState": "approaching",
        "BathCascadeSlopeCMin": 0.35,
      });
      expect(s.slopeCMin, 0.35);
      expect(s.summary, "Cascata em aproximação: reator +0.35 °C/min");
      final noSlope = ExternalBathState.fromJson({
        "TempControlViaBath": true,
        "BathOnline": true,
        "BathCommEnabled": true,
        "BathCascadeState": "approaching",
        "BathCascadeSlopeCMin": null,
      });
      expect(noSlope.summary, contains("aproximação"));
    });

    test('Null values stay null and a fault explains its reason', () {
      final s = ExternalBathState.fromJson({
        "TempControlViaBath": true,
        "BathOnline": true,
        "BathCommEnabled": true,
        "BathCascadeState": "fault",
        "BathCascadeFaultReason": "node_rejected:range",
        "BathPv": null,
        "TempSetpoint": null,
      });
      expect(s.bathPv, isNull);
      expect(s.reactorSetpoint, isNull);
      expect(s.isFault, isTrue);
      expect(s.summary, contains("recusou o comando (range)"));
    });

    test('Stopped cascade says the C404 keeps its last setpoint', () {
      final s = ExternalBathState.fromJson({
        "TempControlViaBath": true,
        "BathOnline": true,
        "BathCommEnabled": true,
        "BathOwned": false,
        "BathCascadeActive": false,
        "BathCascadeState": "off",
        "BathMode": 0,
      });
      expect(s.owned, isFalse);
      expect(s.cascadeActive, isFalse);
      expect(s.summary, contains("último SP"));
    });
  });

  group('External bath commands', () {
    late _RecordingApi api;
    late DeviceControlProvider provider;

    setUp(() {
      api = _RecordingApi();
      provider = DeviceControlProvider(getApiService: () => api);
    });

    test('Stop, reset and guard mode use the Hub keys', () async {
      await provider.stopBath();
      expect(api.last, {"bathAbort": 1});
      await provider.resetBathFault();
      expect(api.last, {"bathCascadeReset": 1});
      await provider.setBathMode(automatic: false);
      expect(api.last, {"bathMode": "manual"});
      await provider.setBathMode(automatic: true);
      expect(api.last, {"bathMode": "auto"});
    });

    test('Invalid temperature is refused instead of clamped', () async {
      final ok = await provider.setTemperature(enabled: true, setpoint: 150.0);
      expect(ok, isFalse);
      expect(api.last, isNull);

      await provider.setTemperature(enabled: true, setpoint: 32.5);
      expect(api.last, {"tempSetpoint": 32.5});

      await provider.setTemperature(enabled: false, setpoint: 32.5);
      expect(api.last, {"tempSetpoint": 0.0});
    });
  });
}
