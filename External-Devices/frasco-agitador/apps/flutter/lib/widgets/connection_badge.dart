import 'package:flutter/material.dart';
import '../services/agitator_service.dart';
import '../theme/app_theme.dart';

class ConnectionBadge extends StatelessWidget {
  final AgitatorService service;

  const ConnectionBadge({super.key, required this.service});

  void _showIpDialog(BuildContext context) {
    final controller = TextEditingController(text: service.ipAddress);

    showDialog(
      context: context,
      builder: (ctx) {
        return AlertDialog(
          backgroundColor: AppTheme.darkCardElevated,
          title: const Row(
            children: [
              Icon(Icons.settings_ethernet, color: AppTheme.primaryCyan),
              SizedBox(width: 10),
              Text('Endereço IP do Agitador', style: TextStyle(fontSize: 18)),
            ],
          ),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text(
                'Informe o IP do módulo (SoftAP ou atribuído pelo Hub):',
                style: TextStyle(fontSize: 13, color: Colors.grey),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: controller,
                keyboardType: TextInputType.text,
                decoration: InputDecoration(
                  labelText: 'IP ou Hostname',
                  hintText: 'Ex: 192.168.4.1',
                  prefixIcon: const Icon(Icons.lan),
                  border: OutlineInputBorder(borderRadius: BorderRadius.circular(12)),
                ),
              ),
              const SizedBox(height: 12),
              const Text('Atalhos Rápidos:', style: TextStyle(fontSize: 12, color: Colors.grey)),
              const SizedBox(height: 6),
              Wrap(
                spacing: 8,
                children: [
                  ActionChip(
                    label: const Text('SoftAP (192.168.4.1)'),
                    onPressed: () => controller.text = '192.168.4.1',
                  ),
                  ActionChip(
                    label: const Text('Hub DHCP (192.168.4.2)'),
                    onPressed: () => controller.text = '192.168.4.2',
                  ),
                ],
              ),
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
                final newIp = controller.text.trim();
                if (newIp.isNotEmpty) {
                  service.updateIpAddress(newIp);
                }
                Navigator.of(ctx).pop();
              },
              child: const Text('Conectar'),
            ),
          ],
        );
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    Color statusColor;
    String statusText;
    IconData statusIcon;

    switch (service.status) {
      case ConnectionStatus.connected:
        statusColor = const Color(0xFF2EC4B6);
        statusText = service.ipAddress;
        statusIcon = Icons.wifi;
        break;
      case ConnectionStatus.connecting:
        statusColor = AppTheme.accentAmber;
        statusText = 'Conectando...';
        statusIcon = Icons.wifi_find;
        break;
      case ConnectionStatus.error:
        statusColor = AppTheme.stopRed;
        statusText = 'Sem Resposta';
        statusIcon = Icons.wifi_off;
        break;
      case ConnectionStatus.disconnected:
        statusColor = Colors.grey;
        statusText = service.ipAddress;
        statusIcon = Icons.wifi_off;
        break;
    }

    return InkWell(
      onTap: () => _showIpDialog(context),
      borderRadius: BorderRadius.circular(20),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        decoration: BoxDecoration(
          color: statusColor.withValues(alpha: 0.12),
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: statusColor.withValues(alpha: 0.35)),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(statusIcon, size: 16, color: statusColor),
            const SizedBox(width: 6),
            Text(
              statusText,
              style: TextStyle(
                color: statusColor,
                fontSize: 12,
                fontWeight: FontWeight.w600,
              ),
            ),
            const SizedBox(width: 4),
            Icon(Icons.arrow_drop_down, size: 16, color: statusColor),
          ],
        ),
      ),
    );
  }
}
