import 'package:flutter_test/flutter_test.dart';
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
  group('Device Control Commands Contract Tests', () {
    late MockHubApiService mockApi;
    late DeviceControlProvider provider;

    setUp(() {
      mockApi = MockHubApiService();
      provider = DeviceControlProvider(getApiService: () => mockApi);
    });

    test('Sends motor setpoint correctly within 0..1000 range', () async {
      await provider.setMotorRpm(350);
      expect(mockApi.lastSentCommand, {"motorSetpoint": 350});

      await provider.stopMotor();
      expect(mockApi.lastSentCommand, {"motorSetpoint": 0});

      // Clamps above 1000
      await provider.setMotorRpm(1500);
      expect(mockApi.lastSentCommand, {"motorSetpoint": 1000});
    });

    test('Sends motor control route (Modbus vs UART/CN1)', () async {
      await provider.setMotorControlMode(1);
      expect(mockApi.lastSentCommand, {"motorControlMode": 1});

      await provider.setMotorControlMode(0);
      expect(mockApi.lastSentCommand, {"motorControlMode": 0});
    });

    test('Sends servo communication enable toggle', () async {
      await provider.setServoComm(true);
      expect(mockApi.lastSentCommand, {"servoComm": 1});

      await provider.setServoComm(false);
      expect(mockApi.lastSentCommand, {"servoComm": 0});
    });

    test('Sends resetServoEnergy', () async {
      await provider.resetServoEnergy();
      expect(mockApi.lastSentCommand, {"resetServoEnergy": 1});
    });

    test('Sends servo poll ms within valid limits', () async {
      await provider.setServoPollMs(500);
      expect(mockApi.lastSentCommand, {"servoPollMs": 500});

      await provider.setServoPollMs(50); // should clamp to 250
      expect(mockApi.lastSentCommand, {"servoPollMs": 250});
    });

    test('Sends temperature setpoint (0 when disabled)', () async {
      await provider.setTemperature(enabled: true, setpoint: 37.0);
      expect(mockApi.lastSentCommand, {"tempSetpoint": 37.0});

      await provider.setTemperature(enabled: false, setpoint: 37.0);
      expect(mockApi.lastSentCommand, {"tempSetpoint": 0.0});
    });

    test('Sends pH control parameters (speed % * 10)', () async {
      await provider.setPhControl(
        enabled: true,
        setpoint: 7.2,
        error: 0.15,
        opTimeSeconds: 4,
        mixTimeSeconds: 25,
        speedPercent: 60,
      );

      expect(mockApi.lastSentCommand, {
        "pHSetpoint": 7.2,
        "pHError": 0.15,
        "pHOperation": 4,
        "pHMix": 25,
        "pHIntensity": 600, // 60 * 10
      });

      // Disabled should set setpoint 0 and intensity 0
      await provider.setPhControl(
        enabled: false,
        setpoint: 7.2,
        error: 0.15,
        opTimeSeconds: 4,
        mixTimeSeconds: 25,
        speedPercent: 60,
      );
      expect(mockApi.lastSentCommand!["pHSetpoint"], 0.0);
      expect(mockApi.lastSentCommand!["pHIntensity"], 0);
    });

    test('Sends nutrient dosing pump parameters', () async {
      await provider.setNutrientPump(
        enabled: true,
        opSeconds: 10,
        mixSeconds: 30,
        opCycleMinutes: 120,
        mixCycleMinutes: 60,
        speedPercent: 80,
      );

      expect(mockApi.lastSentCommand, {
        "nutriOperation": 10,
        "nutriMix": 30,
        "nutriOpCycle": 120,
        "nutriMixCycle": 60,
        "nutriIntensity": 80,
      });
    });

    test('Sends antifoam dosing pump parameters', () async {
      await provider.setAntifoamPump(
        enabled: true,
        opSeconds: 5,
        mixSeconds: 15,
        speedPercent: 90,
      );

      expect(mockApi.lastSentCommand, {
        "antifoamOperation": 5,
        "antifoamMix": 15,
        "antifoamIntensity": 90,
      });
    });

    test('Sends Emergency All-Stop (resetVariables)', () async {
      await provider.emergencyStopAll();
      expect(mockApi.lastSentCommand, {"resetVariables": 1});
    });

    test('Sends CoreSafeStop in single atomic frame', () async {
      await provider.coreSafeStop(maxFlow: 6.0);
      expect(mockApi.lastSentCommand, {
        "tempSetpoint": 0.0,
        "motorSetpoint": 0,
        "oxygenMonitor": 0,
        "flowSetpoint": 0.0,
        "maxFlow": 6.0,
        "valve_1": 0,
        "valve_2": 0,
        "v_Flow": 1,
        "pressureReference": 0,
      });
    });

    test('Sends reboot command', () async {
      await provider.rebootHub();
      expect(mockApi.lastSentCommand, {"restart": 1});
    });
  });
}
