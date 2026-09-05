import 'package:flutter_test/flutter_test.dart';
import 'package:tecnal_app/models/peristaltic_pump_state.dart';
import 'package:tecnal_app/providers/device_control_provider.dart';
import 'package:tecnal_app/providers/telemetry_provider.dart';
import 'package:tecnal_app/services/hub_api_service.dart';
import 'package:tecnal_app/services/pump_profile_math.dart';

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
  group('Peristaltic Pump State & Tri-State Model Tests', () {
    test('State 1: Connected & Active while dosing with valid telemetry', () {
      final json = {
        "PumpOnline": true,
        "PumpCommEnabled": true,
        "PumpCommandPending": false,
        "PumpMode": 1,
        "PumpPWM": 180,
        "PumpSpeed": 45.2,
        "PumpFlow": 2.50,
        "PumpVol": 15.0,
        "PumpTargetVol": 50.0,
        "PumpActive": true,
        "PumpWaiting": false,
      };

      final state = PeristalticPumpState.fromJson(json);

      expect(state.online, isTrue);
      expect(state.commEnabled, isTrue);
      expect(state.commandPending, isFalse);
      expect(state.mode, PeristalticPumpMode.constant);
      expect(state.modeLabel, "Constant Flow");
      expect(state.pwm, 180);
      expect(state.speed, 45.2);
      expect(state.flow, 2.50);
      expect(state.volume, 15.0);
      expect(state.targetVolume, 50.0);
      expect(state.isActive, isTrue);
      expect(state.isWaiting, isFalse);

      expect(state.isConnectedAndActive, isTrue);
      expect(state.isDisconnected, isFalse);
      expect(state.isDisabled, isFalse);
      expect(state.isDosing, isTrue);
      expect(state.isWaitingWindow, isFalse);
      expect(state.isIdle, isFalse);

      expect(state.statusLabel, "Dosing (2.50 mL/min)");
      expect(state.formattedFlow, "2.50 mL/min");
      expect(state.formattedVolume, "15.0 mL");
      expect(state.formattedTargetVolume, "50.0 mL");
      expect(state.formattedSpeed, "45.2 RPM");
      expect(state.formattedPwm, "180");
      expect(state.progressFraction, closeTo(0.30, 0.01));
    });

    test('State 2: Scheduled / Waiting for Window (time < init_t)', () {
      final json = {
        "PumpOnline": true,
        "PumpCommEnabled": true,
        "PumpCommandPending": false,
        "PumpMode": 3,
        "PumpPWM": 0,
        "PumpSpeed": 0.0,
        "PumpFlow": 0.0,
        "PumpVol": 0.0,
        "PumpTargetVol": 30.0,
        "PumpActive": false,
        "PumpWaiting": true,
      };

      final state = PeristalticPumpState.fromJson(json);

      expect(state.isConnectedAndActive, isTrue);
      expect(state.isDosing, isFalse);
      expect(state.isWaitingWindow, isTrue);
      expect(state.statusLabel, "Waiting for Window");
      expect(state.mode, PeristalticPumpMode.exponential);
    });

    test('State 3: Disconnected when comm enabled but node offline (>4s)', () {
      final json = {
        "PumpOnline": false,
        "PumpCommEnabled": true,
        "PumpCommandPending": false,
      };

      final state = PeristalticPumpState.fromJson(json);

      expect(state.online, isFalse);
      expect(state.commEnabled, isTrue);
      expect(state.isConnectedAndActive, isFalse);
      expect(state.isDisconnected, isTrue);
      expect(state.isDisabled, isFalse);
      expect(state.statusLabel, "Pump Disconnected");
      expect(state.formattedFlow, "-- mL/min");
      expect(state.formattedVolume, "-- mL");
      expect(state.formattedSpeed, "-- RPM");
      expect(state.formattedPwm, "--");
    });

    test('State 4: Disabled when routing is off in Hub NVS (pumpComm=0)', () {
      final json = {
        "PumpOnline": false,
        "PumpCommEnabled": false,
      };

      final state = PeristalticPumpState.fromJson(json);

      expect(state.online, isFalse);
      expect(state.commEnabled, isFalse);
      expect(state.isConnectedAndActive, isFalse);
      expect(state.isDisconnected, isFalse);
      expect(state.isDisabled, isTrue);
      expect(state.statusLabel, "Disabled on Hub");
    });

    test('Command pending flag indicates queued command awaiting ACK', () {
      final json = {
        "PumpOnline": true,
        "PumpCommEnabled": true,
        "PumpCommandPending": true,
      };

      final state = PeristalticPumpState.fromJson(json);

      expect(state.commandPending, isTrue);
    });
  });

  group('Pump Profile Mathematics & Simulation Tests', () {
    test('Constant flow mode evaluation and trapezoidal integration', () {
      final spec = PumpProfileSpec.constant(
        initMinutes: 0.0,
        finalMinutes: 10.0,
        lambda: 2.0,
      );

      expect(PumpProfileMath.flowAt(spec, 0.0), 2.0);
      expect(PumpProfileMath.flowAt(spec, 5.0), 2.0);
      expect(PumpProfileMath.flowAt(spec, 10.0), 2.0);
      expect(PumpProfileMath.flowAt(spec, 11.0), 0.0); // beyond horizon

      final preview = PumpProfileMath.sample(spec, count: 100);
      expect(preview.peakFlow, 2.0);
      // Integral of 2.0 mL/min over 10 min = 20.0 mL
      expect(preview.totalVolume, closeTo(20.0, 0.05));
      expect(preview.averageFlow, closeTo(2.0, 0.05));
    });

    test('Linear flow mode evaluation and integration', () {
      // Q(t') = 1.0 + 0.2 * t' over [0, 10] min
      final spec = PumpProfileSpec.linear(
        initMinutes: 0.0,
        finalMinutes: 10.0,
        lambda: 1.0,
        phi: 0.2,
      );

      expect(PumpProfileMath.flowAt(spec, 0.0), 1.0);
      expect(PumpProfileMath.flowAt(spec, 5.0), 2.0);
      expect(PumpProfileMath.flowAt(spec, 10.0), 3.0);

      final preview = PumpProfileMath.sample(spec, count: 100);
      expect(preview.peakFlow, closeTo(3.0, 0.01));
      // Integral of (1 + 0.2*t) dt from 0 to 10 = 10 + 0.1*(100) = 20.0 mL
      expect(preview.totalVolume, closeTo(20.0, 0.05));
    });

    test('Exponential flow mode evaluation and integration', () {
      // Q(t') = 1.0 * exp(0.1 * t') over [0, 10] min
      final spec = PumpProfileSpec.exponential(
        initMinutes: 0.0,
        finalMinutes: 10.0,
        lambda: 1.0,
        phi: 0.1,
      );

      expect(PumpProfileMath.flowAt(spec, 0.0), 1.0);
      expect(PumpProfileMath.flowAt(spec, 10.0), closeTo(2.718, 0.01));

      final preview = PumpProfileMath.sample(spec, count: 200);
      // Integral of e^(0.1*t) from 0 to 10 = (e^1 - 1)/0.1 = 17.18 mL
      expect(preview.totalVolume, closeTo(17.18, 0.1));
    });

    test('Polynomial mode evaluation via Horner scheme', () {
      // Q(t') = 2.0 + 0.5*t' + 0.1*(t')^2
      final spec = PumpProfileSpec.polynomial(
        initMinutes: 0.0,
        finalMinutes: 10.0,
        coefficients: [2.0, 0.5, 0.1],
      );

      expect(PumpProfileMath.flowAt(spec, 0.0), 2.0);
      expect(PumpProfileMath.flowAt(spec, 2.0), 2.0 + 1.0 + 0.4); // 3.4
    });

    test('Piecewise segments linear interpolation', () {
      final spec = PumpProfileSpec.piecewise(
        initMinutes: 0.0,
        finalMinutes: 30.0,
        times: [0.0, 10.0, 30.0],
        flows: [1.0, 3.0, 1.0],
      );

      expect(PumpProfileMath.flowAt(spec, 0.0), 1.0);
      expect(PumpProfileMath.flowAt(spec, 5.0), 2.0); // midpoint 0..10
      expect(PumpProfileMath.flowAt(spec, 10.0), 3.0);
      expect(PumpProfileMath.flowAt(spec, 20.0), 2.0); // midpoint 10..30
      expect(PumpProfileMath.flowAt(spec, 30.0), 1.0);
    });

    test('Validation detects invalid specifications', () {
      // final <= init
      expect(
        PumpProfileMath.validateSpec(PumpProfileSpec.constant(initMinutes: 10, finalMinutes: 5, lambda: 1)),
        isNotNull,
      );

      // negative lambda
      expect(
        PumpProfileMath.validateSpec(PumpProfileSpec.constant(initMinutes: 0, finalMinutes: 10, lambda: -1)),
        isNotNull,
      );

      // piecewise t0 != 0
      expect(
        PumpProfileMath.validateSpec(PumpProfileSpec.piecewise(
          initMinutes: 0,
          finalMinutes: 10,
          times: [5.0, 10.0],
          flows: [1.0, 2.0],
        )),
        isNotNull,
      );

      // piecewise decreasing times
      expect(
        PumpProfileMath.validateSpec(PumpProfileSpec.piecewise(
          initMinutes: 0,
          finalMinutes: 10,
          times: [0.0, 15.0, 10.0],
          flows: [1.0, 2.0, 3.0],
        )),
        isNotNull,
      );
    });
  });

  group('Peristaltic Pump Control Commands Tests', () {
    late MockHubApiService mockApi;
    late DeviceControlProvider provider;

    setUp(() {
      mockApi = MockHubApiService();
      provider = DeviceControlProvider(getApiService: () => mockApi);
    });

    test('Enabling pumpComm sends single frame {"pumpComm": 1}', () async {
      await provider.setPumpComm(true);
      expect(mockApi.sentCommands, [
        {"pumpComm": 1},
      ]);
    });

    test('CRITICAL: Disabling pumpComm performs ordered two-step safe shutdown', () async {
      await provider.setPumpComm(false);
      // First frame halts profile on node, second frame turns off Hub routing
      expect(mockApi.sentCommands, [
        {"mode": 0, "speed": 0},
        {"pumpComm": 0},
      ]);
    });

    test('stopPump sends {"mode": 0, "speed": 0}', () async {
      await provider.stopPump();
      expect(mockApi.lastSentCommand, {
        "mode": 0,
        "speed": 0,
      });
    });

    test('applyPumpProfile builds Constant mode frame', () async {
      final spec = PumpProfileSpec.constant(initMinutes: 0, finalMinutes: 60, lambda: 2.5);
      await provider.applyPumpProfile(spec);

      expect(mockApi.lastSentCommand, {
        "mode": 1,
        "init_t": 0.0,
        "final_t": 60.0,
        "lambda_const": 2.5,
      });
    });

    test('applyPumpProfile builds Linear mode frame', () async {
      final spec = PumpProfileSpec.linear(initMinutes: 5, finalMinutes: 30, lambda: 1.0, phi: 0.15);
      await provider.applyPumpProfile(spec);

      expect(mockApi.lastSentCommand, {
        "mode": 2,
        "init_t": 5.0,
        "final_t": 30.0,
        "lambda_linear": 1.0,
        "phi_linear": 0.15,
      });
    });

    test('applyPumpProfile builds Exponential mode frame', () async {
      final spec = PumpProfileSpec.exponential(initMinutes: 0, finalMinutes: 60, lambda: 1.2, phi: 0.05);
      await provider.applyPumpProfile(spec);

      expect(mockApi.lastSentCommand, {
        "mode": 3,
        "init_t": 0.0,
        "final_t": 60.0,
        "lambda_exp": 1.2,
        "phi_exp": 0.05,
      });
    });

    test('applyPumpProfile builds Polynomial mode frame with p0..pn', () async {
      final spec = PumpProfileSpec.polynomial(
        initMinutes: 0,
        finalMinutes: 40,
        coefficients: [1.5, 0.02, 0.001],
      );
      await provider.applyPumpProfile(spec);

      expect(mockApi.lastSentCommand, {
        "mode": 4,
        "init_t": 0.0,
        "final_t": 40.0,
        "p0": 1.5,
        "p1": 0.02,
        "p2": 0.001,
      });
    });

    test('applyPumpProfile builds Piecewise mode frame with t0, q0...', () async {
      final spec = PumpProfileSpec.piecewise(
        initMinutes: 0,
        finalMinutes: 60,
        times: [0.0, 30.0, 60.0],
        flows: [1.0, 2.5, 0.5],
      );
      await provider.applyPumpProfile(spec);

      expect(mockApi.lastSentCommand, {
        "mode": 5,
        "init_t": 0.0,
        "final_t": 60.0,
        "num_segments": 3,
        "t0": 0.0,
        "q0": 1.0,
        "t1": 30.0,
        "q1": 2.5,
        "t2": 60.0,
        "q2": 0.5,
      });
    });
  });

  group('Peristaltic Pump Telemetry History Buffering Tests', () {
    test('Accumulates flow and volume samples into history buffer when active', () {
      final telemetry = TelemetryProvider();

      telemetry.updateFromJson({
        "Time": 10.0,
        "PumpOnline": true,
        "PumpCommEnabled": true,
        "PumpFlow": 1.5,
        "PumpVol": 5.0,
      });

      expect(telemetry.pumpFlowHistory.length, 1);
      expect(telemetry.pumpFlowHistory.first.value, 1.5);
      expect(telemetry.pumpVolumeHistory.length, 1);
      expect(telemetry.pumpVolumeHistory.first.value, 5.0);

      telemetry.updateFromJson({
        "Time": 11.0,
        "PumpOnline": true,
        "PumpCommEnabled": true,
        "PumpFlow": 2.0,
        "PumpVol": 7.0,
      });

      expect(telemetry.pumpFlowHistory.length, 2);
      expect(telemetry.pumpFlowHistory.last.value, 2.0);
      expect(telemetry.pumpVolumeHistory.length, 2);
      expect(telemetry.pumpVolumeHistory.last.value, 7.0);

      telemetry.clearHistory();
      expect(telemetry.pumpFlowHistory.isEmpty, isTrue);
      expect(telemetry.pumpVolumeHistory.isEmpty, isTrue);
    });
  });
}
