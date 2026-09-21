import 'package:flutter/material.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';

/// Presença / latência / versão do nó. Toca para trocar o host (plano §4.1).
class ConnectionBadge extends StatelessWidget {
  final BathService service;
  const ConnectionBadge({super.key, required this.service});

  void _showHostDialog(BuildContext context) {
    final controller = TextEditingController(text: service.host);
    showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: AppTheme.darkCardElevated,
        title: const Row(children: [
          Icon(Icons.settings_ethernet, color: AppTheme.primaryCyan),
          SizedBox(width: 10),
          Text('Endereço do banho', style: TextStyle(fontSize: 18)),
        ]),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              'IP do nó bath (AP próprio ou atribuído pelo Hub):',
              style: TextStyle(fontSize: 13, color: Colors.grey),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: controller,
              keyboardType: TextInputType.text,
              decoration: const InputDecoration(
                labelText: 'IP ou hostname',
                hintText: 'Ex: 192.168.8.1',
                prefixIcon: Icon(Icons.lan),
              ),
            ),
            const SizedBox(height: 12),
            const Text('Atalhos:',
                style: TextStyle(fontSize: 12, color: Colors.grey)),
            const SizedBox(height: 6),
            Wrap(spacing: 8, children: [
              ActionChip(
                label: const Text('AP do banho (192.168.8.1)'),
                onPressed: () => controller.text = '192.168.8.1',
              ),
            ]),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(),
            child: const Text('Cancelar'),
          ),
          ElevatedButton(
            style: ElevatedButton.styleFrom(
              backgroundColor: AppTheme.primaryCyan,
              foregroundColor: Colors.black,
            ),
            onPressed: () {
              final h = controller.text.trim();
              if (h.isNotEmpty) service.updateHost(h);
              Navigator.of(ctx).pop();
            },
            child: const Text('Conectar'),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    Color color;
    String text;
    IconData icon;
    switch (service.status) {
      case ConnectionStatus.connected:
        if (service.statusData.supportsFirmware) {
          color = AppTheme.okGreen;
          text = '${service.host} · ${service.lastLatencyMs} ms';
          icon = Icons.wifi;
        } else {
          color = AppTheme.accentAmber;
          text = 'Firmware incompatível';
          icon = Icons.warning_amber_rounded;
        }
        break;
      case ConnectionStatus.connecting:
        color = AppTheme.accentAmber;
        text = 'Conectando…';
        icon = Icons.wifi_find;
        break;
      case ConnectionStatus.error:
        color = AppTheme.stopRed;
        text = 'Sem resposta';
        icon = Icons.wifi_off;
        break;
      case ConnectionStatus.disconnected:
        color = Colors.grey;
        text = service.host;
        icon = Icons.wifi_off;
        break;
    }

    return InkWell(
      onTap: () => _showHostDialog(context),
      borderRadius: BorderRadius.circular(20),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.12),
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: color.withValues(alpha: 0.35)),
        ),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          Icon(icon, size: 16, color: color),
          const SizedBox(width: 6),
          Text(text,
              style: TextStyle(
                  color: color, fontSize: 12, fontWeight: FontWeight.w600)),
          const SizedBox(width: 2),
          Icon(Icons.arrow_drop_down, size: 16, color: color),
        ]),
      ),
    );
  }
}
