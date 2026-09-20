import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';

/// Folha de diagnóstico: /diag (heap, RSSI, streak do Hub), versão, last_cmd_id,
/// e log das últimas respostas (plano §4.4, §1).
class DiagnosticsSheet extends StatefulWidget {
  final BathService service;
  const DiagnosticsSheet({super.key, required this.service});

  static void show(BuildContext context, BathService service) {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: AppTheme.darkCardElevated,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
      ),
      builder: (_) => ChangeNotifierProvider.value(
        value: service,
        child: DiagnosticsSheet(service: service),
      ),
    );
  }

  @override
  State<DiagnosticsSheet> createState() => _DiagnosticsSheetState();
}

class _DiagnosticsSheetState extends State<DiagnosticsSheet> {
  @override
  void initState() {
    super.initState();
    widget.service.setDiagActive(true);
  }

  @override
  void dispose() {
    widget.service.setDiagActive(false);
    super.dispose();
  }

  String _uptime(int s) {
    if (s <= 0) return '0s';
    final h = s ~/ 3600, m = (s % 3600) ~/ 60, sec = s % 60;
    if (h > 0) return '${h}h ${m}m ${sec}s';
    if (m > 0) return '${m}m ${sec}s';
    return '${sec}s';
  }

  @override
  Widget build(BuildContext context) {
    final s = context.watch<BathService>();
    final st = s.statusData;
    final d = s.diag;
    return DraggableScrollableSheet(
      expand: false,
      initialChildSize: 0.7,
      maxChildSize: 0.95,
      builder: (_, controller) => SingleChildScrollView(
        controller: controller,
        padding: const EdgeInsets.fromLTRB(20, 16, 20, 32),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Center(
              child: Container(
                width: 40,
                height: 4,
                decoration: BoxDecoration(
                    color: Colors.white24,
                    borderRadius: BorderRadius.circular(2)),
              ),
            ),
            const SizedBox(height: 16),
            const Row(children: [
              Icon(Icons.monitor_heart, color: AppTheme.primaryCyan),
              SizedBox(width: 10),
              Text('Diagnóstico do nó',
                  style: TextStyle(fontSize: 17, fontWeight: FontWeight.bold)),
            ]),
            const Divider(color: Colors.white12, height: 24),
            _row('Firmware', st.version.isNotEmpty ? st.version : '—'),
            _row('Host', s.host),
            _row('IP do nó', st.ip.isNotEmpty ? st.ip : '—'),
            _row('Uptime', _uptime(st.uptimeS)),
            _row('MAC', d.mac.isNotEmpty ? d.mac : '—'),
            _row('SSID', d.ssid.isNotEmpty ? d.ssid : '—'),
            _row('RSSI', d.rssi != 0 ? '${d.rssi} dBm' : '—'),
            _row('AP IP', d.apIp.isNotEmpty ? d.apIp : '—'),
            _row('Heap livre',
                d.freeHeap > 0 ? '${(d.freeHeap / 1024).toStringAsFixed(1)} KB' : '—'),
            _row('Falhas com o Hub', '${d.hubFailStreak} consecutivas'),
            _row('Último cmd_id', '${s.lastCmdId} (nó: ${st.lastCmdId})'),
            _row('OTA', st.ota ? 'EM ANDAMENTO' : 'inativo',
                color: st.ota ? AppTheme.accentAmber : Colors.white70),
            const SizedBox(height: 16),
            Text('Log das respostas',
                style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 8),
            if (s.log.isEmpty)
              const Text('Sem comandos ainda.',
                  style: TextStyle(color: Colors.grey, fontSize: 12))
            else
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: Colors.black.withValues(alpha: 0.3),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    for (final e in s.log)
                      Padding(
                        padding: const EdgeInsets.symmetric(vertical: 2),
                        child: Text(
                          '${e.t.hour.toString().padLeft(2, '0')}:'
                          '${e.t.minute.toString().padLeft(2, '0')}:'
                          '${e.t.second.toString().padLeft(2, '0')}  ${e.text}',
                          style: TextStyle(
                            fontFamily: 'monospace',
                            fontSize: 11,
                            color: e.isError ? AppTheme.stopRed : Colors.white70,
                          ),
                        ),
                      ),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }

  Widget _row(String label, String value, {Color? color}) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 5),
        child: Row(mainAxisAlignment: MainAxisAlignment.spaceBetween, children: [
          Text(label, style: const TextStyle(fontSize: 13, color: Colors.grey)),
          Flexible(
            child: Text(value,
                textAlign: TextAlign.end,
                style: TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                    color: color ?? Colors.white)),
          ),
        ]),
      );
}
