/// Represents telemetry and connection state for the External Biomass Sensor.
///
/// The Hub publishes:
/// - BiomassOnline: whether the node is answering inside its timeout window (6000ms).
/// - BiomassCommEnabled: whether routing is turned on in Hub NVS.
/// - BiomassCommandPending: whether an action command is queued in the Hub's mailbox.
/// - BiomassAbs, BiomassRaw, BiomassIT, BiomassPWM: optical metrics when acquiring.
class BiomassSensorState {
  final bool online;
  final bool commEnabled;
  final bool commandPending;
  final double absorbance; // AU (absorbance units)
  final int rawCounts;
  final int integrationTimeUs;
  final int pwmDuty;
  final bool hasTelemetry;

  BiomassSensorState({
    required this.online,
    required this.commEnabled,
    required this.commandPending,
    required this.absorbance,
    required this.rawCounts,
    required this.integrationTimeUs,
    required this.pwmDuty,
    required this.hasTelemetry,
  });

  int get integrationTimeMs => integrationTimeUs ~/ 1000;
  double get pwmPercent => pwmDuty > 0 ? (pwmDuty / 255.0) * 100.0 : 0.0;

  factory BiomassSensorState.empty() {
    return BiomassSensorState(
      online: false,
      commEnabled: false,
      commandPending: false,
      absorbance: -1.0,
      rawCounts: 0,
      integrationTimeUs: 0,
      pwmDuty: 0,
      hasTelemetry: false,
    );
  }

  factory BiomassSensorState.fromJson(Map<String, dynamic> json) {
    final bool isOnline = json['BiomassOnline'] == true;
    final bool isCommEnabled = json['BiomassCommEnabled'] == true;
    final bool isPending = json['BiomassCommandPending'] == true;
    final bool hasAbs = json.containsKey('BiomassAbs');

    final double absVal = hasAbs && (json['BiomassAbs'] is num)
        ? (json['BiomassAbs'] as num).toDouble()
        : -1.0;
    final int raw = (json['BiomassRaw'] is num) ? (json['BiomassRaw'] as num).toInt() : 0;
    final int it = (json['BiomassIT'] is num) ? (json['BiomassIT'] as num).toInt() : 0;
    final int pwm = (json['BiomassPWM'] is num) ? (json['BiomassPWM'] as num).toInt() : 0;

    return BiomassSensorState(
      online: isOnline,
      commEnabled: isCommEnabled,
      commandPending: isPending,
      absorbance: (isOnline && isCommEnabled && absVal >= 0.0) ? absVal : -1.0,
      rawCounts: raw,
      integrationTimeUs: it,
      pwmDuty: pwm,
      hasTelemetry: hasAbs && isOnline && isCommEnabled && absVal >= 0.0,
    );
  }

  /// Whether optical absorbance data is present and valid
  bool get hasAbsorbance => hasTelemetry;

  /// Sensor is enabled, communicating, and streaming optical absorbance samples
  bool get isAcquiring => online && commEnabled && hasTelemetry && absorbance >= 0.0;

  /// Sensor is connected to Hub (knocking via idle heartbeat), but not actively measuring
  bool get isIdle => online && commEnabled && !hasTelemetry;

  /// Comm is enabled on Hub, but the external sensor node is offline / disconnected
  bool get isDisconnected => commEnabled && !online;

  /// Routing is switched off by the operator in Hub settings
  bool get isDisabled => !commEnabled;

  /// Action availability guards based on connection and mailbox status
  bool get canStartAcquisition => online && commEnabled && !commandPending && !isAcquiring;
  bool get canStopAcquisition => online && commEnabled && !commandPending && isAcquiring;
  bool get canZeroBlank => online && commEnabled && !commandPending;

  String get statusLabel {
    if (isDisabled) return "Disabled on Hub";
    if (isDisconnected) return "Sensor Disconnected";
    if (isAcquiring) return "Acquiring (Active)";
    if (isIdle) return "Connected (Idle)";
    return "Standby";
  }

  String get formattedAbs => isAcquiring ? "${absorbance.toStringAsFixed(3)} AU" : "--";
  String get formattedAbsorbance => isAcquiring ? "${absorbance.toStringAsFixed(3)} AU" : (isIdle ? "Idle" : "-- AU");
  String get formattedRaw => isAcquiring ? "$rawCounts" : "--";
  String get formattedIT => isAcquiring ? "$integrationTimeUs µs" : "--";
  String get formattedPWM => isAcquiring ? "$pwmDuty/255" : "--";
}
