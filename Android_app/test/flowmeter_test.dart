import 'package:flutter_test/flutter_test.dart';
import 'package:tecnal_app/models/flowmeter_state.dart';
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
  group('Flowmeter Sensor Model & Tri-State Tests', () {
    test('State 1: Connected & Active when online with valid flow telemetry', () {
      final json = {
        "FlowmeterOnline": true,
        "FlowControlEnabled": true,
        "FlowCommandPending": false,
        "FlowCommandId": 12,
        "FlowCommandAck": 12,
        "FlowCommandDeliveries": 1,
        "FlowCommandAgeMs": 0,
        "FlowCommandSource": "app",
        "FlowRate": 2.50,
        "FlowSetpoint": 2.50,
        "FlowVoltage": 1.234,
        "Valve1": 1,
        "Valve2": 0,
        "ValveFlow": 0,
      };

      final state = FlowmeterState.fromJson(json);

      expect(state.online, isTrue);
      expect(state.commEnabled, isTrue);
      expect(state.commandPending, isFalse);
      expect(state.commandId, 12);
      expect(state.commandAck, 12);
      expect(state.hasTelemetry, isTrue);
      expect(state.flowRate, 2.50);
      expect(state.flowSetpoint, 2.50);
      expect(state.flowVoltage, 1.234);
      expect(state.valve1, isTrue);
      expect(state.valve2, isFalse);
      expect(state.valveFlow, 0);
      expect(state.isMainFlowOpen, isTrue);

      expect(state.isConnectedAndActive, isTrue);
      expect(state.isDisconnected, isFalse);
      expect(state.isDisabled, isFalse);

      expect(state.statusLabel, "Connected & Active");
      expect(state.formattedFlowRate, "2.50 L/min");
      expect(state.formattedSetpoint, "2.50 L/min");
      expect(state.formattedVoltage, "1.234 V");
      expect(state.valve1Label, "OPEN");
      expect(state.valve2Label, "CLOSED");
      expect(state.mainFlowLabel, "OPEN (ACTIVE)");
    });

    test('State 2: Disconnected when comm enabled but node heartbeat expired (>5s)', () {
      final json = {
        "FlowmeterOnline": false,
        "FlowControlEnabled": true,
        "FlowCommandPending": false,
      };

      final state = FlowmeterState.fromJson(json);

      expect(state.online, isFalse);
      expect(state.commEnabled, isTrue);
      expect(state.hasTelemetry, isFalse);
      expect(state.flowRate, -1.0);
      expect(state.isConnectedAndActive, isFalse);
      expect(state.isDisconnected, isTrue);
      expect(state.isDisabled, isFalse);
      expect(state.statusLabel, "Sensor Disconnected");
      expect(state.formattedFlowRate, "-- L/min");
      expect(state.formattedVoltage, "-- V");
    });

    test('State 3: Disabled when comm routing turned off by operator', () {
      final json = {
        "FlowmeterOnline": false,
        "FlowControlEnabled": false,
      };

      final state = FlowmeterState.fromJson(json);

      expect(state.online, isFalse);
      expect(state.commEnabled, isFalse);
      expect(state.isConnectedAndActive, isFalse);
      expect(state.isDisconnected, isFalse);
      expect(state.isDisabled, isTrue);
      expect(state.statusLabel, "Disabled on Hub");
    });

    test('Command pending flag indicates queued mailbox command awaiting ACK', () {
      final json = {
        "FlowmeterOnline": true,
        "FlowControlEnabled": true,
        "FlowCommandPending": true,
        "FlowCommandId": 15,
        "FlowCommandAck": 14,
      };

      final state = FlowmeterState.fromJson(json);

      expect(state.commandPending, isTrue);
      expect(state.commandId, 15);
      expect(state.commandAck, 14);
    });
  });

  group('Flowmeter Sensor Control Commands Tests', () {
    late MockHubApiService mockApi;
    late DeviceControlProvider provider;

    setUp(() {
      mockApi = MockHubApiService();
      provider = DeviceControlProvider(getApiService: () => mockApi);
    });

    test('Sends flowmeterComm enable (1)', () async {
      await provider.setFlowmeterComm(true);
      expect(mockApi.sentCommands, [
        {"flowmeterComm": 1},
      ]);
    });

    test('Sends safe stop before flowmeterComm disable (0)', () async {
      await provider.setFlowmeterComm(false);
      expect(mockApi.sentCommands, [
        {
          "flowSetpoint": 0.0,
          "valve_1": 0,
          "valve_2": 0,
          "v_Flow": 1,
        },
        {"flowmeterComm": 0},
      ]);
    });

    test('Sends flowSetpoint command with automatic un-shutoff (v_Flow: 0)', () async {
      await provider.setFlowSetpoint(3.5);
      expect(mockApi.lastSentCommand, {
        "flowSetpoint": 3.5,
        "v_Flow": 0,
      });
    });

    test('Sends zero setpoint with shutoff (v_Flow: 1)', () async {
      await provider.setFlowSetpoint(0.0);
      expect(mockApi.lastSentCommand, {
        "flowSetpoint": 0.0,
        "v_Flow": 1,
      });
    });

    test('Sends individual valve state commands', () async {
      await provider.setFlowValves(valve1: true);
      expect(mockApi.lastSentCommand, {"valve_1": 1});

      await provider.setFlowValves(valve2: false, mainFlowOpen: true);
      expect(mockApi.lastSentCommand, {"valve_2": 0, "v_Flow": 0});
    });

    test('Sends emergency stopFlow command', () async {
      await provider.stopFlow();
      expect(mockApi.lastSentCommand, {
        "flowSetpoint": 0.0,
        "valve_1": 0,
        "valve_2": 0,
        "v_Flow": 1,
      });
    });
  });

  group('Flowmeter Telemetry History Buffering Tests', () {
    test('Accumulates flow rate samples into history buffer when active', () {
      final telemetry = TelemetryProvider();

      telemetry.updateFromJson({
        "Time": 20.0,
        "FlowmeterOnline": true,
        "FlowControlEnabled": true,
        "FlowRate": 1.85,
      });

      expect(telemetry.flowRateHistory.length, 1);
      expect(telemetry.flowRateHistory.first.time, 20.0);
      expect(telemetry.flowRateHistory.first.value, 1.85);

      telemetry.updateFromJson({
        "Time": 21.0,
        "FlowmeterOnline": true,
        "FlowControlEnabled": true,
        "FlowRate": 1.92,
      });

      expect(telemetry.flowRateHistory.length, 2);
      expect(telemetry.flowRateHistory.last.value, 1.92);
    });
  });
}
