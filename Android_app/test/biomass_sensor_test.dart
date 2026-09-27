import 'package:flutter_test/flutter_test.dart';
import 'package:tecnal_app/models/biomass_sensor_state.dart';
import 'package:tecnal_app/providers/device_control_provider.dart';
import 'package:tecnal_app/providers/telemetry_provider.dart';
import 'package:tecnal_app/services/hub_api_service.dart';

class MockHubApiService extends HubApiService {
  final List<Map<String, dynamic>> sentCommands = [];

  Map<String, dynamic>? get lastSentCommand =>
      sentCommands.isNotEmpty ? sentCommands.last : null;

  @override
  Future<({bool success, int statusCode, String response})> sendCommand(
      Map<String, dynamic> command) async {
    sentCommands.add(command);
    return (success: true, statusCode: 200, response: "Queued");
  }
}

void main() {
  group('Biomass Sensor Model & Lifecycle Tests', () {
    test('State 1: Acquiring (Active) when online and optical stream is active', () {
      final json = {
        "BiomassOnline": true,
        "BiomassCommEnabled": true,
        "BiomassAbs": 0.742,
        "BiomassRaw": 42100,
        "BiomassIT": 50000,
        "BiomassPWM": 180,
        "BiomassCommandPending": false,
      };

      final state = BiomassSensorState.fromJson(json);

      expect(state.online, isTrue);
      expect(state.commEnabled, isTrue);
      expect(state.hasAbsorbance, isTrue);
      expect(state.absorbance, 0.742);
      expect(state.rawCounts, 42100);
      expect(state.integrationTimeUs, 50000);
      expect(state.pwmDuty, 180);
      expect(state.commandPending, isFalse);

      expect(state.isAcquiring, isTrue);
      expect(state.isIdle, isFalse);
      expect(state.isDisconnected, isFalse);
      expect(state.isDisabled, isFalse);

      expect(state.statusLabel, "Medindo");
      expect(state.formattedAbs, "0.742 AU");
      expect(state.formattedRaw, "42100");
      expect(state.formattedIT, "50000 µs");
      expect(state.formattedPWM, "180/255");

      expect(state.canStartAcquisition, isFalse);
      expect(state.canStopAcquisition, isTrue);
      expect(state.canZeroBlank, isTrue);
    });

    test('State 2: Connected (Idle) when online on SoftAP but acquisition paused', () {
      final json = {
        "BiomassOnline": true,
        "BiomassCommEnabled": true,
        "idle": true,
        "BiomassCommandPending": false,
      };

      final state = BiomassSensorState.fromJson(json);

      expect(state.online, isTrue);
      expect(state.commEnabled, isTrue);
      expect(state.hasAbsorbance, isFalse);
      expect(state.isAcquiring, isFalse);
      expect(state.isIdle, isTrue);
      expect(state.isDisconnected, isFalse);
      expect(state.isDisabled, isFalse);

      expect(state.statusLabel, "Conectado (parado)");
      expect(state.formattedAbs, "--");

      expect(state.canStartAcquisition, isTrue);
      expect(state.canStopAcquisition, isFalse);
      expect(state.canZeroBlank, isTrue);
    });

    test('State 3: Disconnected when comm enabled but node heartbeat expired (>6s)', () {
      final json = {
        "BiomassOnline": false,
        "BiomassCommEnabled": true,
      };

      final state = BiomassSensorState.fromJson(json);

      expect(state.online, isFalse);
      expect(state.commEnabled, isTrue);
      expect(state.isAcquiring, isFalse);
      expect(state.isIdle, isFalse);
      expect(state.isDisconnected, isTrue);
      expect(state.isDisabled, isFalse);

      expect(state.statusLabel, "Sensor desconectado");
      expect(state.canStartAcquisition, isFalse);
      expect(state.canStopAcquisition, isFalse);
      expect(state.canZeroBlank, isFalse);
    });

    test('State 4: Disabled when comm routing turned off by operator', () {
      final json = {
        "BiomassOnline": false,
        "BiomassCommEnabled": false,
      };

      final state = BiomassSensorState.fromJson(json);

      expect(state.online, isFalse);
      expect(state.commEnabled, isFalse);
      expect(state.isAcquiring, isFalse);
      expect(state.isIdle, isFalse);
      expect(state.isDisconnected, isFalse);
      expect(state.isDisabled, isTrue);

      expect(state.statusLabel, "Desligado no Hub");
      expect(state.canStartAcquisition, isFalse);
      expect(state.canStopAcquisition, isFalse);
      expect(state.canZeroBlank, isFalse);
    });

    test('Command pending flag locks actions', () {
      final json = {
        "BiomassOnline": true,
        "BiomassCommEnabled": true,
        "idle": true,
        "BiomassCommandPending": true,
      };

      final state = BiomassSensorState.fromJson(json);

      expect(state.commandPending, isTrue);
      expect(state.canStartAcquisition, isFalse);
      expect(state.canStopAcquisition, isFalse);
      expect(state.canZeroBlank, isFalse);
    });
  });

  group('Biomass Sensor Control Commands Tests', () {
    late MockHubApiService mockApi;
    late DeviceControlProvider provider;

    setUp(() {
      mockApi = MockHubApiService();
      provider = DeviceControlProvider(getApiService: () => mockApi);
    });

    test('Sends biomassComm enable (1)', () async {
      await provider.setBiomassComm(true);
      expect(mockApi.sentCommands, [
        {"biomassComm": 1},
      ]);
    });

    test('Sends stop followed by biomassComm disable (0) for safe shutdown', () async {
      await provider.setBiomassComm(false);
      expect(mockApi.sentCommands, [
        {"stop": 1},
        {"biomassComm": 0},
      ]);
    });

    test('Sends start acquisition command', () async {
      await provider.startBiomassAcquisition();
      expect(mockApi.lastSentCommand, {"start": 1});
    });

    test('Sends stop acquisition command', () async {
      await provider.stopBiomassAcquisition();
      expect(mockApi.lastSentCommand, {"stop": 1});
    });

    test('Sends blank / zero calibration command', () async {
      await provider.zeroBiomassBlank();
      expect(mockApi.lastSentCommand, {"blank": 1});
    });

    test('Sends integration threshold parameters (low, high, opt)', () async {
      await provider.setBiomassThresholds(low: 30000, high: 60000, opt: 50000);
      expect(mockApi.lastSentCommand, {
        "low": 30000,
        "high": 60000,
        "opt": 50000,
      });
    });
  });

  group('Biomass Telemetry History Buffering Tests', () {
    test('Accumulates biomass absorbance data points into history buffer', () {
      final telemetry = TelemetryProvider();

      telemetry.updateFromJson({
        "Time": 10.0,
        "BiomassOnline": true,
        "BiomassCommEnabled": true,
        "BiomassAbs": 0.815,
      });

      expect(telemetry.biomassHistory.length, 1);
      expect(telemetry.biomassHistory.first.time, 10.0);
      expect(telemetry.biomassHistory.first.value, 0.815);

      telemetry.updateFromJson({
        "Time": 12.0,
        "BiomassOnline": true,
        "BiomassCommEnabled": true,
        "BiomassAbs": 0.820,
      });

      expect(telemetry.biomassHistory.length, 2);
      expect(telemetry.biomassHistory.last.value, 0.820);
    });
  });
}
