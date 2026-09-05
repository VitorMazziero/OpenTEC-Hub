import 'package:flutter_test/flutter_test.dart';
import 'package:tecnal_app/models/distance_sensor_state.dart';
import 'package:tecnal_app/providers/device_control_provider.dart';
import 'package:tecnal_app/services/hub_api_service.dart';

class MockHubApiService extends HubApiService {
  Map<String, dynamic>? lastSentCommand;

  @override
  Future<({bool success, int statusCode, String response})> sendCommand(
      Map<String, dynamic> command) async {
    lastSentCommand = command;
    return (success: true, statusCode: 200, response: "Queued");
  }
}

void main() {
  group('Distance Sensor Model & Tri-State Tests', () {
    test('State 1: Connected & Active when online with valid readings', () {
      final json = {
        "DistanceOnline": true,
        "DistanceCommEnabled": true,
        "Distance": 145.52,
      };

      final state = DistanceSensorState.fromJson(json);

      expect(state.online, isTrue);
      expect(state.commEnabled, isTrue);
      expect(state.hasTelemetry, isTrue);
      expect(state.distanceMm, 145.52);
      expect(state.isConnectedAndActive, isTrue);
      expect(state.isDisconnected, isFalse);
      expect(state.isDisabled, isFalse);
      expect(state.statusLabel, "Connected & Active");
      expect(state.formattedDistance, "145.5 mm");
      expect(state.formattedDistanceCm, "14.6 cm");
    });

    test('State 2: Disconnected when comm is enabled but node is offline (>5s)', () {
      final json = {
        "DistanceOnline": false,
        "DistanceCommEnabled": true,
        // Distance key is omitted by firmware on timeout
      };

      final state = DistanceSensorState.fromJson(json);

      expect(state.online, isFalse);
      expect(state.commEnabled, isTrue);
      expect(state.hasTelemetry, isFalse);
      expect(state.distanceMm, -1.0);
      expect(state.isConnectedAndActive, isFalse);
      expect(state.isDisconnected, isTrue);
      expect(state.isDisabled, isFalse);
      expect(state.statusLabel, "Sensor Disconnected");
      expect(state.formattedDistance, "-- mm");
    });

    test('State 3: Disabled when comm routing is turned off by operator', () {
      final json = {
        "DistanceOnline": false,
        "DistanceCommEnabled": false,
      };

      final state = DistanceSensorState.fromJson(json);

      expect(state.online, isFalse);
      expect(state.commEnabled, isFalse);
      expect(state.isConnectedAndActive, isFalse);
      expect(state.isDisconnected, isFalse);
      expect(state.isDisabled, isTrue);
      expect(state.statusLabel, "Disabled on Hub");
    });
  });

  group('Distance Sensor Control Commands Tests', () {
    late MockHubApiService mockApi;
    late DeviceControlProvider provider;

    setUp(() {
      mockApi = MockHubApiService();
      provider = DeviceControlProvider(getApiService: () => mockApi);
    });

    test('Sends distanceSensorComm command (1=enable, 0=disable)', () async {
      await provider.setDistanceSensorComm(true);
      expect(mockApi.lastSentCommand, {"distanceSensorComm": 1});

      await provider.setDistanceSensorComm(false);
      expect(mockApi.lastSentCommand, {"distanceSensorComm": 0});
    });

    test('Sends distanceSensorReference threshold command', () async {
      await provider.setDistanceSensorReference(120.0);
      expect(mockApi.lastSentCommand, {"distanceSensorReference": 120.0});
    });
  });
}
