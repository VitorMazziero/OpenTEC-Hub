/// Profile modes supported by the external peristaltic pump firmware
enum PeristalticPumpMode {
  stop(0, "Parada"),
  constant(1, "Vazão constante"),
  linear(2, "Perfil linear"),
  exponential(3, "Perfil exponencial"),
  polynomial(4, "Perfil polinomial"),
  piecewise(5, "Segmentos (t, Q)");

  final int code;
  final String label;
  const PeristalticPumpMode(this.code, this.label);

  static PeristalticPumpMode fromCode(int code) {
    for (final m in values) {
      if (m.code == code) return m;
    }
    return PeristalticPumpMode.stop;
  }
}

/// Represents the state and telemetry of the External Peristaltic Pump node.
///
/// Published by Hub Protocol v10 via /readData:
/// - PumpOnline: Node pushed telemetry within PUMP_TIMEOUT (4000ms).
/// - PumpCommEnabled: Echo of pumpComm flag persisted in Hub NVS.
/// - PumpCommandPending: Command queued in Hub mailbox awaiting ACK.
/// - PumpMode: Active mode code (0..5).
/// - PumpPWM: Actuation PWM (0..255).
/// - PumpSpeed: Actual motor speed (RPM).
/// - PumpFlow: Instantaneous flow rate (mL/min).
/// - PumpVol: Accumulated dosed volume (mL).
/// - PumpTargetVol: Expected volume integrated by node's profile (mL).
/// - PumpActive: True when motor is actively dosing within operating window.
/// - PumpWaiting: True when node is waiting for initial window (t < init_t).
class PeristalticPumpState {
  final bool online;
  final bool commEnabled;
  final bool commandPending;
  final PeristalticPumpMode mode;
  final int pwm;
  final double speed;
  final double flow;
  final double volume;
  final double targetVolume;
  final bool isActive;
  final bool isWaiting;
  final bool hasTelemetry;

  PeristalticPumpState({
    required this.online,
    required this.commEnabled,
    required this.commandPending,
    required this.mode,
    required this.pwm,
    required this.speed,
    required this.flow,
    required this.volume,
    required this.targetVolume,
    required this.isActive,
    required this.isWaiting,
    required this.hasTelemetry,
  });

  factory PeristalticPumpState.empty() {
    return PeristalticPumpState(
      online: false,
      commEnabled: false,
      commandPending: false,
      mode: PeristalticPumpMode.stop,
      pwm: 0,
      speed: 0.0,
      flow: 0.0,
      volume: 0.0,
      targetVolume: 0.0,
      isActive: false,
      isWaiting: false,
      hasTelemetry: false,
    );
  }

  factory PeristalticPumpState.fromJson(Map<String, dynamic> json) {
    final bool isOnline = json['PumpOnline'] == true;
    final bool isCommEnabled = json['PumpCommEnabled'] == true;
    final bool isPending = json['PumpCommandPending'] == true;

    final int modeCode = (json['PumpMode'] is num) ? (json['PumpMode'] as num).toInt() : 0;
    final int rawPwm = (json['PumpPWM'] is num) ? (json['PumpPWM'] as num).toInt() : 0;
    final double rawSpeed = (json['PumpSpeed'] is num) ? (json['PumpSpeed'] as num).toDouble() : 0.0;
    final double rawFlow = (json['PumpFlow'] is num) ? (json['PumpFlow'] as num).toDouble() : 0.0;
    final double rawVol = (json['PumpVol'] is num) ? (json['PumpVol'] as num).toDouble() : 0.0;
    final double rawTarget = (json['PumpTargetVol'] is num) ? (json['PumpTargetVol'] as num).toDouble() : 0.0;
    final bool rawActive = json['PumpActive'] == true;
    final bool rawWaiting = json['PumpWaiting'] == true;

    final bool validTelemetry = isOnline && isCommEnabled && json.containsKey('PumpFlow');

    return PeristalticPumpState(
      online: isOnline,
      commEnabled: isCommEnabled,
      commandPending: isPending,
      mode: PeristalticPumpMode.fromCode(modeCode),
      pwm: rawPwm,
      speed: rawSpeed,
      flow: validTelemetry ? rawFlow : 0.0,
      volume: validTelemetry ? rawVol : 0.0,
      targetVolume: validTelemetry ? rawTarget : 0.0,
      isActive: validTelemetry ? rawActive : false,
      isWaiting: validTelemetry ? rawWaiting : false,
      hasTelemetry: validTelemetry,
    );
  }

  /// Node is online, routing is enabled, and live telemetry is flowing
  bool get isConnectedAndActive => online && commEnabled && hasTelemetry;

  /// Whether the node is online
  bool get isOnline => online;

  /// Routing is enabled, but node has timed out or disconnected (>4000ms)
  bool get isDisconnected => commEnabled && !online;

  /// Routing is turned off in Hub NVS (pumpComm == 0)
  bool get isDisabled => !commEnabled;

  /// Command is pending in the Hub mailbox awaiting node ACK
  bool get isCommandPending => commandPending;

  /// Pump motor is actively dosing fluid
  bool get isDosing => isConnectedAndActive && isActive;

  /// Pump profile is scheduled but waiting for the start window (t < init_t)
  bool get isWaitingWindow => isConnectedAndActive && isWaiting;

  /// Pump is idle or stopped
  bool get isIdle => isConnectedAndActive && !isActive && !isWaiting;

  /// Human-readable label of the active mode
  String get modeLabel => mode.label;

  /// Status badge summary
  String get statusLabel {
    if (isDisabled) return "Desligado no Hub";
    if (isDisconnected) return "Bomba desconectada";
    if (isDosing) return "Dosando ($formattedFlow)";
    if (isWaitingWindow) return "Aguardando início";
    if (isConnectedAndActive) return "Parada";
    return "Offline";
  }

  /// Formatted instantaneous flow rate (e.g. "1.50 mL/min" or "-- mL/min")
  String get formattedFlow =>
      isConnectedAndActive ? "${flow.toStringAsFixed(2)} mL/min" : "-- mL/min";

  /// Formatted accumulated dosed volume (e.g. "25.4 mL" or "-- mL")
  String get formattedVolume =>
      isConnectedAndActive ? "${volume.toStringAsFixed(1)} mL" : "-- mL";

  /// Formatted target volume from profile integral (e.g. "30.0 mL" or "-- mL")
  String get formattedTargetVolume =>
      isConnectedAndActive ? "${targetVolume.toStringAsFixed(1)} mL" : "-- mL";

  /// Formatted motor speed (RPM)
  String get formattedSpeed =>
      isConnectedAndActive ? "${speed.toStringAsFixed(1)} RPM" : "-- RPM";

  /// Formatted PWM (0-255)
  String get formattedPwm => isConnectedAndActive ? "$pwm" : "--";

  /// Progress fraction (0.0 to 1.0) towards target volume
  double get progressFraction {
    if (!isConnectedAndActive || targetVolume <= 0.0) return 0.0;
    final frac = volume / targetVolume;
    return frac.clamp(0.0, 1.0);
  }
}
