/// Modelo do `GET /diag` do nó bath. É o `/status` mais os campos de saúde:
/// `free_heap`, `ssid`, `rssi`, `mac`, `ap_ip`, `hub_fail_streak`.
/// Ver PROTOCOL.md §1.
library;

int _asInt(dynamic v) => v is num ? v.toInt() : 0;

class NodeDiag {
  final bool present;
  final int freeHeap;
  final String ssid;
  final int rssi;
  final String mac;
  final String apIp;
  final int hubFailStreak;

  const NodeDiag({
    required this.present,
    required this.freeHeap,
    required this.ssid,
    required this.rssi,
    required this.mac,
    required this.apIp,
    required this.hubFailStreak,
  });

  factory NodeDiag.empty() => const NodeDiag(
        present: false,
        freeHeap: 0,
        ssid: '',
        rssi: 0,
        mac: '',
        apIp: '',
        hubFailStreak: 0,
      );

  factory NodeDiag.fromJson(Map<String, dynamic> j) => NodeDiag(
        present: true,
        freeHeap: _asInt(j['free_heap']),
        ssid: (j['ssid'] ?? '').toString(),
        rssi: _asInt(j['rssi']),
        mac: (j['mac'] ?? '').toString(),
        apIp: (j['ap_ip'] ?? '').toString(),
        hubFailStreak: _asInt(j['hub_fail_streak']),
      );
}
