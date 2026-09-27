/// Represents state, feedback, and diagnostics for the Delta ASDA-B2 Servo Motor
/// as published by Hub v10 over HTTP.
class ServoState {
  final bool online;
  final bool commEnabled;
  final bool controlCapable;
  final bool viaModbus;

  // Command & Sync tracking
  final int commandId;
  final int commandAck;
  final int routeAck;
  final bool commandPending;
  final int commandAgeMs;

  // Setpoints & Status
  final int requestedRpm;
  final int appliedRpm;
  final int leaseMs;
  final bool motorEnabled;
  final bool controlActive;
  final int controlFault;

  // Live Drive Feedback (when publishable)
  final bool hasTelemetry;
  final double rpm;
  final double torquePct;
  final double torqueNm;
  final double loadPct;
  final double powerW;
  final double energyWh;
  final int state; // 0..3 (0: Not Ready, 1: Ready, 2: Running/Enabled, 3: Fault)
  final int alarm; // Delta ASDA-B2 error/alarm code
  final int commOk;
  final int commErr;

  ServoState({
    required this.online,
    required this.commEnabled,
    required this.controlCapable,
    required this.viaModbus,
    required this.commandId,
    required this.commandAck,
    required this.routeAck,
    required this.commandPending,
    required this.commandAgeMs,
    required this.requestedRpm,
    required this.appliedRpm,
    required this.leaseMs,
    required this.motorEnabled,
    required this.controlActive,
    required this.controlFault,
    required this.hasTelemetry,
    required this.rpm,
    required this.torquePct,
    required this.torqueNm,
    required this.loadPct,
    required this.powerW,
    required this.energyWh,
    required this.state,
    required this.alarm,
    required this.commOk,
    required this.commErr,
  });

  factory ServoState.empty() {
    return ServoState(
      online: false,
      commEnabled: false,
      controlCapable: false,
      viaModbus: true, // Hub default route: servo node (Modbus)
      commandId: 0,
      commandAck: 0,
      routeAck: 0,
      commandPending: false,
      commandAgeMs: 0,
      requestedRpm: 0,
      appliedRpm: 0,
      leaseMs: 0,
      motorEnabled: false,
      controlActive: false,
      controlFault: 0,
      hasTelemetry: false,
      rpm: 0.0,
      torquePct: 0.0,
      torqueNm: 0.0,
      loadPct: 0.0,
      powerW: 0.0,
      energyWh: 0.0,
      state: 0,
      alarm: 0,
      commOk: 0,
      commErr: 0,
    );
  }

  factory ServoState.fromJson(Map<String, dynamic> json) {
    bool hasRpm = json.containsKey('ServoRpm');
    return ServoState(
      online: json['ServoOnline'] == true,
      commEnabled: json['ServoCommEnabled'] == true,
      controlCapable: json['ServoControlCapable'] == true,
      viaModbus: json['MotorControlViaModbus'] != false, // absent: Hub default (servo)
      commandId: (json['ServoMotorCommandId'] is num) ? (json['ServoMotorCommandId'] as num).toInt() : 0,
      commandAck: (json['ServoMotorCommandAck'] is num) ? (json['ServoMotorCommandAck'] as num).toInt() : 0,
      routeAck: (json['ServoMotorRouteAck'] is num) ? (json['ServoMotorRouteAck'] as num).toInt() : 0,
      commandPending: json['ServoMotorCommandPending'] == true,
      commandAgeMs: (json['ServoMotorCommandAgeMs'] is num) ? (json['ServoMotorCommandAgeMs'] as num).toInt() : 0,
      requestedRpm: (json['ServoMotorRequestedRpm'] is num) ? (json['ServoMotorRequestedRpm'] as num).toInt() : 0,
      appliedRpm: (json['ServoMotorAppliedRpm'] is num) ? (json['ServoMotorAppliedRpm'] as num).toInt() : 0,
      leaseMs: (json['ServoMotorLeaseMs'] is num) ? (json['ServoMotorLeaseMs'] as num).toInt() : 0,
      motorEnabled: json['ServoMotorEnabled'] == true,
      controlActive: json['ServoMotorControlActive'] == true,
      controlFault: (json['ServoMotorControlFault'] is num) ? (json['ServoMotorControlFault'] as num).toInt() : 0,
      hasTelemetry: hasRpm,
      rpm: hasRpm && (json['ServoRpm'] is num) ? (json['ServoRpm'] as num).toDouble() : 0.0,
      torquePct: (json['ServoTorquePct'] is num) ? (json['ServoTorquePct'] as num).toDouble() : 0.0,
      torqueNm: (json['ServoTorqueNm'] is num) ? (json['ServoTorqueNm'] as num).toDouble() : 0.0,
      loadPct: (json['ServoLoadPct'] is num) ? (json['ServoLoadPct'] as num).toDouble() : 0.0,
      powerW: (json['ServoPowerW'] is num) ? (json['ServoPowerW'] as num).toDouble() : 0.0,
      energyWh: (json['ServoEnergyWh'] is num) ? (json['ServoEnergyWh'] as num).toDouble() : 0.0,
      state: (json['ServoState'] is num) ? (json['ServoState'] as num).toInt() : 0,
      alarm: (json['ServoAlarm'] is num) ? (json['ServoAlarm'] as num).toInt() : 0,
      commOk: (json['ServoCommOk'] is num) ? (json['ServoCommOk'] as num).toInt() : 0,
      commErr: (json['ServoCommErr'] is num) ? (json['ServoCommErr'] as num).toInt() : 0,
    );
  }

  bool get isFaulted => alarm != 0 || controlFault != 0 || state == 3;
  bool get isReady => online && controlCapable && !isFaulted;
  bool get isRunning => motorEnabled && (appliedRpm > 0 || rpm > 0);

  String get stateDescription {
    if (!online) return "Offline";
    if (alarm != 0) return "Alarme: AL${alarm.toString().padLeft(3, '0')}";
    if (controlFault != 0) return "Falha ($controlFault)";
    switch (state) {
      case 1:
        return "Pronto";
      case 2:
        return isRunning ? "Girando" : "Habilitado";
      case 3:
        return "Falha do drive";
      default:
        return "Parado";
    }
  }

  /// Title of the selected command path
  String get routeName => viaModbus ? "Modbus Direto (ESP32-Servo)" : "UART / CN1 (Placa Controladora)";

  /// Explicit hardware signal flow topology
  String get routePath => viaModbus
      ? "ESP32S3-HUB → ESP32S3-Servo → Servo Delta ASDA-B2"
      : "ESP32S3-HUB → UART → ControllerBoard → Servo Delta ASDA-B2";

  /// Status of the hardware route ACK from the drive
  String get routeAckDescription {
    if (routeAck == 1) return "Confirmado (Modbus)";
    if (routeAck == 0) return "Confirmado (UART/CN1)";
    return "Aguardando confirmação";
  }
}
