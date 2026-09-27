/// Represents telemetry and state for the External Flowmeter & Gas Sparging subsystem.
///
/// Published by Hub Protocol v10 via /readData:
/// - FlowmeterOnline: node heartbeat status (<5000ms).
/// - FlowControlEnabled: stored routing flag in Hub NVS (flowmeterComm).
/// - FlowCommandPending: whether a flow command is queued awaiting ACK.
/// - FlowRate: measured gas flow rate in L/min.
/// - FlowSetpoint: target gas flow setpoint in L/min.
/// - FlowVoltage: raw sensor analog voltage in V.
/// - Valve1: auxiliary / air valve state (0 = closed, 1 = open).
/// - Valve2: nitrogen (N₂) valve state (0 = closed, 1 = open).
/// - ValveFlow: proportional / shutoff valve (0 = open/active flow, 1 = closed/shutoff).
class FlowmeterState {
  final bool online;
  final bool commEnabled;
  final bool commandPending;
  final int commandId;
  final int commandAck;
  final int commandDeliveries;
  final int commandAgeMs;
  final String commandSource;
  final double flowRate; // L/min
  final double flowSetpoint; // L/min
  final double flowVoltage; // V
  final bool valve1; // Aux / Air
  final bool valve2; // Nitrogen
  final int valveFlow; // 0 = open, 1 = shutoff
  final bool hasTelemetry;

  FlowmeterState({
    required this.online,
    required this.commEnabled,
    required this.commandPending,
    required this.commandId,
    required this.commandAck,
    required this.commandDeliveries,
    required this.commandAgeMs,
    required this.commandSource,
    required this.flowRate,
    required this.flowSetpoint,
    required this.flowVoltage,
    required this.valve1,
    required this.valve2,
    required this.valveFlow,
    required this.hasTelemetry,
  });

  factory FlowmeterState.empty() {
    return FlowmeterState(
      online: false,
      commEnabled: false,
      commandPending: false,
      commandId: 0,
      commandAck: 0,
      commandDeliveries: 0,
      commandAgeMs: 0,
      commandSource: "",
      flowRate: -1.0,
      flowSetpoint: 0.0,
      flowVoltage: 0.0,
      valve1: false,
      valve2: false,
      valveFlow: 1, // 1 = shutoff
      hasTelemetry: false,
    );
  }

  factory FlowmeterState.fromJson(Map<String, dynamic> json) {
    final bool isOnline = json['FlowmeterOnline'] == true;
    final bool isCommEnabled = json['FlowControlEnabled'] == true;
    final bool isPending = json['FlowCommandPending'] == true;
    final int cmdId = (json['FlowCommandId'] is num) ? (json['FlowCommandId'] as num).toInt() : 0;
    final int cmdAck = (json['FlowCommandAck'] is num) ? (json['FlowCommandAck'] as num).toInt() : 0;
    final int deliveries = (json['FlowCommandDeliveries'] is num) ? (json['FlowCommandDeliveries'] as num).toInt() : 0;
    final int ageMs = (json['FlowCommandAgeMs'] is num) ? (json['FlowCommandAgeMs'] as num).toInt() : 0;
    final String source = json['FlowCommandSource']?.toString() ?? "";

    final bool hasRate = json.containsKey('FlowRate') && (json['FlowRate'] is num);
    final double rate = hasRate ? (json['FlowRate'] as num).toDouble() : -1.0;
    final double sp = (json['FlowSetpoint'] is num) ? (json['FlowSetpoint'] as num).toDouble() : 0.0;
    final double volt = (json['FlowVoltage'] is num) ? (json['FlowVoltage'] as num).toDouble() : 0.0;

    final bool v1 = json['Valve1'] == 1 || json['Valve1'] == true;
    final bool v2 = json['Valve2'] == 1 || json['Valve2'] == true;
    final int vFlow = (json['ValveFlow'] is num) ? (json['ValveFlow'] as num).toInt() : 1;

    final bool validTelemetry = hasRate && isOnline && isCommEnabled && rate >= 0.0;

    return FlowmeterState(
      online: isOnline,
      commEnabled: isCommEnabled,
      commandPending: isPending,
      commandId: cmdId,
      commandAck: cmdAck,
      commandDeliveries: deliveries,
      commandAgeMs: ageMs,
      commandSource: source,
      flowRate: validTelemetry ? rate : -1.0,
      flowSetpoint: sp,
      flowVoltage: volt,
      valve1: v1,
      valve2: v2,
      valveFlow: vFlow,
      hasTelemetry: validTelemetry,
    );
  }

  /// Flowmeter node is connected, routing is enabled, and live flow samples are streaming
  bool get isConnectedAndActive => online && commEnabled && hasTelemetry && flowRate >= 0.0;

  /// Routing is enabled on Hub, but the external sensor node is offline / timed out (>5s)
  bool get isDisconnected => commEnabled && !online;

  /// Flow routing is disabled in Hub settings (flowmeterComm == 0)
  bool get isDisabled => !commEnabled;

  /// In the Hub firmware, valveFlow is active-low for flow: 0 = Open/Active, 1 = Shutoff
  bool get isMainFlowOpen => valveFlow == 0;

  String get statusLabel {
    if (isDisabled) return "Desligado no Hub";
    if (isDisconnected) return "Sensor desconectado";
    if (isConnectedAndActive) return "Conectado e medindo";
    return "Em espera";
  }

  String get formattedFlowRate => isConnectedAndActive ? "${flowRate.toStringAsFixed(2)} L/min" : "-- L/min";
  String get formattedSetpoint => "${flowSetpoint.toStringAsFixed(2)} L/min";
  String get formattedVoltage => isConnectedAndActive ? "${flowVoltage.toStringAsFixed(3)} V" : "-- V";
  String get valve1Label => valve1 ? "OPEN" : "CLOSED";
  String get valve2Label => valve2 ? "OPEN" : "CLOSED";
  String get mainFlowLabel => isMainFlowOpen ? "OPEN (ACTIVE)" : "SHUTOFF";
}
