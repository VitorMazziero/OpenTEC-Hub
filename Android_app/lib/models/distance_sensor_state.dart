/// Represents telemetry and connection state for the External Distance & Level Sensor.
///
/// The Hub publishes:
/// - DistanceOnline: whether the external sensor node pushed data within the presence timeout (5000ms).
/// - DistanceCommEnabled: whether routing is turned on in Hub NVS.
/// - Distance: measured distance in millimeters (only present when valid & online).
class DistanceSensorState {
  final bool online;
  final bool commEnabled;
  final double distanceMm;
  final bool hasTelemetry;

  DistanceSensorState({
    required this.online,
    required this.commEnabled,
    required this.distanceMm,
    required this.hasTelemetry,
  });

  factory DistanceSensorState.empty() {
    return DistanceSensorState(
      online: false,
      commEnabled: false,
      distanceMm: -1.0,
      hasTelemetry: false,
    );
  }

  factory DistanceSensorState.fromJson(Map<String, dynamic> json) {
    final bool isOnline = json['DistanceOnline'] == true;
    final bool isCommEnabled = json['DistanceCommEnabled'] == true;
    final bool hasDistKey = json.containsKey('Distance');
    final double distVal = hasDistKey && (json['Distance'] is num)
        ? (json['Distance'] as num).toDouble()
        : -1.0;

    return DistanceSensorState(
      online: isOnline,
      commEnabled: isCommEnabled,
      distanceMm: (isOnline && distVal >= 0.0) ? distVal : -1.0,
      hasTelemetry: hasDistKey && distVal >= 0.0,
    );
  }

  /// Device is enabled on Hub and the external node is actively transmitting
  bool get isConnectedAndActive => online && commEnabled && distanceMm >= 0.0;

  /// Device is enabled on Hub, but the external node is offline / disconnected
  bool get isDisconnected => commEnabled && !online;

  /// Device routing is turned off by the operator on the Hub
  bool get isDisabled => !commEnabled;

  String get statusLabel {
    if (isDisabled) return "Desligado no Hub";
    if (isDisconnected) return "Sensor desconectado";
    if (isConnectedAndActive) return "Conectado e medindo";
    return "Em espera";
  }

  String get formattedDistance {
    if (isConnectedAndActive) {
      return "${distanceMm.toStringAsFixed(1)} mm";
    }
    return "-- mm";
  }

  String get formattedDistanceCm {
    if (isConnectedAndActive) {
      return "${(distanceMm / 10.0).toStringAsFixed(1)} cm";
    }
    return "-- cm";
  }
}
