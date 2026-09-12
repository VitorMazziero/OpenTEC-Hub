class AgitatorTelemetry {
  final double duty;
  final bool dirRight;
  final bool potEnabled;
  final int uptimeSeconds;
  final int freeHeap;
  final int wifiStatus;
  final String ssid;
  final int rssi;
  final String ip;
  final String mac;
  final int hubFailStreak;
  final bool otaInProgress;
  final DateTime timestamp;

  const AgitatorTelemetry({
    this.duty = 0.0,
    this.dirRight = true,
    this.potEnabled = true,
    this.uptimeSeconds = 0,
    this.freeHeap = 0,
    this.wifiStatus = 0,
    this.ssid = '',
    this.rssi = 0,
    this.ip = '',
    this.mac = '',
    this.hubFailStreak = 0,
    this.otaInProgress = false,
    required this.timestamp,
  });

  factory AgitatorTelemetry.empty() {
    return AgitatorTelemetry(timestamp: DateTime.now());
  }

  factory AgitatorTelemetry.fromJson(Map<String, dynamic> json) {
    double parseDuty() {
      if (json['duty'] != null) {
        return (json['duty'] as num).toDouble();
      }
      return 0.0;
    }

    bool parseDir() {
      if (json['dir'] != null) {
        final d = json['dir'];
        if (d is bool) return d;
        if (d is num) return d != 0;
      }
      return true;
    }

    bool parsePot() {
      if (json['pot'] != null) {
        final p = json['pot'];
        if (p is bool) return p;
        if (p is num) return p != 0;
      }
      return true;
    }

    return AgitatorTelemetry(
      duty: parseDuty(),
      dirRight: parseDir(),
      potEnabled: parsePot(),
      uptimeSeconds: (json['uptime_s'] as num?)?.toInt() ?? (json['time_s'] as num?)?.toInt() ?? 0,
      freeHeap: (json['free_heap'] as num?)?.toInt() ?? 0,
      wifiStatus: (json['wifi_status'] as num?)?.toInt() ?? 0,
      ssid: (json['ssid'] as String?) ?? '',
      rssi: (json['rssi'] as num?)?.toInt() ?? 0,
      ip: (json['ip'] as String?) ?? '',
      mac: (json['mac'] as String?) ?? '',
      hubFailStreak: (json['hub_fail_streak'] as num?)?.toInt() ?? 0,
      otaInProgress: json['ota'] == true,
      timestamp: DateTime.now(),
    );
  }
}
