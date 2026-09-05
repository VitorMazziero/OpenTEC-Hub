import 'package:flutter_test/flutter_test.dart';
import 'package:tecnal_app/models/flask_agitator_state.dart';
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
  group('Flask Agitator Model & State Tests', () {
    test('State 1: Active spinning CW with valid telemetry', () {
      final json = {
        "AgitatorOnline": true,
        "AgitatorCommandPending": false,
        "AgitatorPercent": 75.0,
        "AgitatorDir": 1,
        "AgitatorPotActive": true,
        "AgitatorSource": "user",
      };

      final state = FlaskAgitatorState.fromJson(json);

      expect(state.online, isTrue);
      expect(state.commandPending, isFalse);
      expect(state.speedPercent, 75.0);
      expect(state.direction, 1);
      expect(state.potActive, isTrue);
      expect(state.source, "user");

      expect(state.isConnectedAndActive, isTrue);
      expect(state.isDisconnected, isFalse);
      expect(state.isSpinning, isTrue);
      expect(state.isClockwise, isTrue);

      expect(state.statusLabel, "Agitating (75%)");
      expect(state.formattedPercent, "75%");
      expect(state.directionLabel, "CW (Normal)");
      expect(state.potActiveLabel, "Potentiometer Active");
    });

    test('State 2: Active idle (0% rotation speed)', () {
      final json = {
        "AgitatorOnline": true,
        "AgitatorCommandPending": false,
        "AgitatorPercent": 0.0,
        "AgitatorDir": 1,
        "AgitatorPotActive": false,
        "AgitatorSource": "idle",
      };

      final state = FlaskAgitatorState.fromJson(json);

      expect(state.online, isTrue);
      expect(state.isConnectedAndActive, isTrue);
      expect(state.isSpinning, isFalse);
      expect(state.formattedPercent, "0%");
      expect(state.statusLabel, "Connected (Idle)");
      expect(state.potActiveLabel, "Remote Hub Control");
    });

    test('State 3: Disconnected when AgitatorOnline is false', () {
      final json = {
        "AgitatorOnline": false,
        "AgitatorCommandPending": false,
      };

      final state = FlaskAgitatorState.fromJson(json);

      expect(state.online, isFalse);
      expect(state.isConnectedAndActive, isFalse);
      expect(state.isDisconnected, isTrue);
      expect(state.statusLabel, "Agitator Disconnected");
      expect(state.formattedPercent, "--%");
      expect(state.directionLabel, "--");
      expect(state.potActiveLabel, "--");
    });

    test('State 4: CCW rotation direction (dir == 0)', () {
      final json = {
        "AgitatorOnline": true,
        "AgitatorPercent": 50.0,
        "AgitatorDir": 0,
      };

      final state = FlaskAgitatorState.fromJson(json);

      expect(state.direction, 0);
      expect(state.isClockwise, isFalse);
      expect(state.directionLabel, "CCW (Reverse)");
    });

    test('Command pending flag indicates queued command awaiting ACK', () {
      final json = {
        "AgitatorOnline": true,
        "AgitatorCommandPending": true,
        "AgitatorPercent": 80.0,
        "AgitatorDir": 1,
      };

      final state = FlaskAgitatorState.fromJson(json);

      expect(state.commandPending, isTrue);
    });
  });

  group('Flask Agitator Control Commands Tests', () {
    late MockHubApiService mockApi;
    late DeviceControlProvider provider;

    setUp(() {
      mockApi = MockHubApiService();
      provider = DeviceControlProvider(getApiService: () => mockApi);
    });

    test('Sends setAgitatorState with on, speed, and direction', () async {
      await provider.setAgitatorState(
        on: true,
        speedPercent: 85,
        direction: 1,
      );

      expect(mockApi.lastSentCommand, {
        "agitatorOn": 1,
        "agitatorPercent": 85,
        "agitatorDir": 1,
      });
    });

    test('Clamps speedPercent between 0 and 100', () async {
      await provider.setAgitatorState(
        on: true,
        speedPercent: 120,
        direction: 0,
      );

      expect(mockApi.lastSentCommand, {
        "agitatorOn": 1,
        "agitatorPercent": 100,
        "agitatorDir": 0,
      });
    });

    test('Sends stopAgitator command', () async {
      await provider.stopAgitator();

      expect(mockApi.lastSentCommand, {
        "agitatorOn": 0,
      });
    });

    test('Sends safeStopAgitator with potentiometer lockout', () async {
      await provider.safeStopAgitator();

      expect(mockApi.lastSentCommand, {
        "agitatorOn": 0,
        "agitatorReEnablePot": 0,
      });
    });

    test('Sends setAgitatorSettings with auto foam and pot re-enable', () async {
      await provider.setAgitatorSettings(
        autoFoam: true,
        reEnablePot: false,
      );

      expect(mockApi.lastSentCommand, {
        "agitatorAuto": 1,
        "agitatorReEnablePot": 0,
      });
    });

    test('Sends setAgitatorSettings with single setting', () async {
      await provider.setAgitatorSettings(autoFoam: false);

      expect(mockApi.lastSentCommand, {
        "agitatorAuto": 0,
      });
    });
  });

  group('Flask Agitator Telemetry History Buffering Tests', () {
    test('Accumulates agitator speed samples into history buffer when active', () {
      final telemetry = TelemetryProvider();

      telemetry.updateFromJson({
        "Time": 30.0,
        "AgitatorOnline": true,
        "AgitatorPercent": 60.0,
      });

      expect(telemetry.agitatorSpeedHistory.length, 1);
      expect(telemetry.agitatorSpeedHistory.first.time, 30.0);
      expect(telemetry.agitatorSpeedHistory.first.value, 60.0);

      telemetry.updateFromJson({
        "Time": 31.0,
        "AgitatorOnline": true,
        "AgitatorPercent": 75.0,
      });

      expect(telemetry.agitatorSpeedHistory.length, 2);
      expect(telemetry.agitatorSpeedHistory.last.value, 75.0);

      telemetry.clearHistory();
      expect(telemetry.agitatorSpeedHistory.isEmpty, isTrue);
    });
  });
}
