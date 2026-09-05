/// Represents telemetry and state for the External Flask Agitator node.
///
/// Published by Hub Protocol v10 via /readData:
/// - AgitatorOnline: whether the node has pushed telemetry within AGITATOR_TIMEOUT (3000ms).
/// - AgitatorCommandPending: whether a command is awaiting ACK in the Hub's mailbox.
/// - AgitatorPercent: actual rotation speed percentage (0.0 to 100.0%).
/// - AgitatorDir: rotation direction (1 = CW / Normal, 0 = CCW / Reverse).
/// - AgitatorPotActive: whether onboard potentiometer is active / overriding.
/// - AgitatorSource: actuation origin ("user", "auto", "foam", "pot", etc.).
class FlaskAgitatorState {
  final bool online;
  final bool commandPending;
  final double speedPercent; // 0.0 to 100.0%
  final int direction; // 1 = CW, 0 = CCW
  final bool potActive;
  final String source;
  final bool hasTelemetry;

  FlaskAgitatorState({
    required this.online,
    required this.commandPending,
    required this.speedPercent,
    required this.direction,
    required this.potActive,
    required this.source,
    required this.hasTelemetry,
  });

  factory FlaskAgitatorState.empty() {
    return FlaskAgitatorState(
      online: false,
      commandPending: false,
      speedPercent: 0.0,
      direction: 1,
      potActive: false,
      source: "",
      hasTelemetry: false,
    );
  }

  factory FlaskAgitatorState.fromJson(Map<String, dynamic> json) {
    final bool isOnline = json['AgitatorOnline'] == true;
    final bool isPending = json['AgitatorCommandPending'] == true;
    final bool hasSpeed = json.containsKey('AgitatorPercent') && (json['AgitatorPercent'] is num);
    final double pct = hasSpeed ? (json['AgitatorPercent'] as num).toDouble() : 0.0;
    final int dir = (json['AgitatorDir'] is num) ? (json['AgitatorDir'] as num).toInt() : 1;
    final bool pot = json['AgitatorPotActive'] == true;
    final String src = json['AgitatorSource']?.toString() ?? "";

    final bool validTelemetry = isOnline && hasSpeed;

    return FlaskAgitatorState(
      online: isOnline,
      commandPending: isPending,
      speedPercent: validTelemetry ? pct : 0.0,
      direction: dir,
      potActive: pot,
      source: src,
      hasTelemetry: validTelemetry,
    );
  }

  /// Agitator node is online and reporting live telemetry
  bool get isConnectedAndActive => online && hasTelemetry;

  /// Node is online
  bool get isOnline => online;

  /// Node is offline or timed out (>3000ms)
  bool get isDisconnected => !online;

  /// Command is pending ACK from node
  bool get isCommandPending => commandPending;

  /// Agitator is actively rotating (speed > 0%)
  bool get isSpinning => isConnectedAndActive && speedPercent > 0.0;

  /// Rotation direction is clockwise (1) vs counter-clockwise (0)
  bool get isClockwise => direction == 1;

  String get statusLabel {
    if (isDisconnected) return "Agitator Disconnected";
    if (isSpinning) return "Agitating (${speedPercent.toStringAsFixed(0)}%)";
    return "Connected (Idle)";
  }

  String get formattedSpeed => isConnectedAndActive ? "${speedPercent.toStringAsFixed(0)}%" : "--%";
  String get formattedPercent => formattedSpeed;
  String get directionLabel => isConnectedAndActive ? (isClockwise ? "CW (Normal)" : "CCW (Reverse)") : "--";
  String get potLabel => isConnectedAndActive ? (potActive ? "POT OVERRIDE" : "SW CONTROL") : "--";
  String get potActiveLabel => isConnectedAndActive ? (potActive ? "Potentiometer Active" : "Remote Hub Control") : "--";
}
