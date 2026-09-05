/// Represents telemetry from internal bioreactor sensors and the hub core.
/// External devices (flowmeter, external pump, biomass, distance, flask agitator)
/// are excluded.
class InternalTelemetry {
  final double time; // seconds since boot
  final double temperature; // deg C (-1.0 if invalid/absent)
  final double rawPh; // raw ADC count (-1.0 if invalid/absent)
  final double rawOxygen; // raw ADC count (-1.0 if invalid/absent)
  final double antifoam; // internal probe value
  final double pressure; // mmHg
  final int hubStations; // number of Wi-Fi clients
  final bool sensorCommOk; // OpenTEC UART link status
  final String hubFirmwareVersion;
  final int hubProtocolVersion;

  InternalTelemetry({
    required this.time,
    required this.temperature,
    required this.rawPh,
    required this.rawOxygen,
    required this.antifoam,
    required this.pressure,
    required this.hubStations,
    required this.sensorCommOk,
    required this.hubFirmwareVersion,
    required this.hubProtocolVersion,
  });

  factory InternalTelemetry.empty() {
    return InternalTelemetry(
      time: 0.0,
      temperature: -1.0,
      rawPh: -1.0,
      rawOxygen: -1.0,
      antifoam: 0.0,
      pressure: 0.0,
      hubStations: 0,
      sensorCommOk: false,
      hubFirmwareVersion: "",
      hubProtocolVersion: 0,
    );
  }

  factory InternalTelemetry.fromJson(Map<String, dynamic> json) {
    final num? rawTime = (json['Time'] is num) ? (json['Time'] as num) : ((json['time'] is num) ? (json['time'] as num) : null);
    return InternalTelemetry(
      time: rawTime?.toDouble() ?? 0.0,
      temperature: (json['Tempval'] is num) ? (json['Tempval'] as num).toDouble() : -1.0,
      rawPh: (json['pHval'] is num) ? (json['pHval'] as num).toDouble() : -1.0,
      rawOxygen: (json['Oxyval'] is num) ? (json['Oxyval'] as num).toDouble() : -1.0,
      antifoam: (json['Antifoam'] is num) ? (json['Antifoam'] as num).toDouble() : 0.0,
      pressure: (json['Pressure'] is num) ? (json['Pressure'] as num).toDouble() : 0.0,
      hubStations: (json['HubStations'] is int) ? json['HubStations'] as int : 0,
      sensorCommOk: json['SensorCommOK'] == true,
      hubFirmwareVersion: json['HubFirmwareVersion']?.toString() ?? "",
      hubProtocolVersion: (json['HubProtocolVersion'] is int)
          ? json['HubProtocolVersion'] as int
          : int.tryParse(json['HubProtocolVersion']?.toString() ?? "0") ?? 0,
    );
  }

  bool get hasValidTemperature => temperature > 0.0 && temperature < 100.0;
  bool get hasValidPh => rawPh > 0.1;
  bool get hasValidOxygen => rawOxygen > 0.1;
}
