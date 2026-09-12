import 'package:flutter/material.dart';
import '../services/agitator_service.dart';
import '../theme/app_theme.dart';

class DiagnosticsSheet extends StatelessWidget {
  final AgitatorService service;

  const DiagnosticsSheet({super.key, required this.service});

  static void show(BuildContext context, AgitatorService service) {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: AppTheme.darkCardElevated,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
      ),
      builder: (_) => DiagnosticsSheet(service: service),
    );
  }

  String _formatUptime(int seconds) {
    if (seconds <= 0) return '0s';
    final h = seconds ~/ 3600;
    final m = (seconds % 3600) ~/ 60;
    final s = seconds % 60;
    if (h > 0) return '${h}h ${m}m ${s}s';
    if (m > 0) return '${m}m ${s}s';
    return '${s}s';
  }

  @override
  Widget build(BuildContext context) {
    final t = service.telemetry;
    final isOnline = service.isConnected;

    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 32),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Center(
            child: Container(
              width: 40,
              height: 4,
              decoration: BoxDecoration(
                color: Colors.white24,
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          const SizedBox(height: 16),
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              const Row(
                children: [
                  Icon(Icons.monitor_heart, color: AppTheme.primaryCyan),
                  SizedBox(width: 10),
                  Text(
                    'Diagnóstico e Saúde do Dispositivo',
                    style: TextStyle(fontSize: 17, fontWeight: FontWeight.bold),
                  ),
                ],
              ),
              IconButton(
                tooltip: 'Atualizar agora',
                icon: const Icon(Icons.refresh, color: AppTheme.primaryCyan),
                onPressed: () => service.pollTelemetry(),
              ),
            ],
          ),
          const Divider(color: Colors.white12, height: 24),
          if (!isOnline)
            Container(
              margin: const EdgeInsets.only(bottom: 16),
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: AppTheme.stopRed.withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: AppTheme.stopRed.withValues(alpha: 0.3)),
              ),
              child: const Row(
                children: [
                  Icon(Icons.warning_amber_rounded, color: AppTheme.stopRed),
                  SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      'Dispositivo offline ou sem resposta. Verifique a rede Wi-Fi e o IP configurado.',
                      style: TextStyle(fontSize: 12, color: AppTheme.stopRed),
                    ),
                  ),
                ],
              ),
            ),
          _buildInfoRow('Dispositivo', 'Frasco Agitador (ESP32)'),
          _buildInfoRow('Endereço IP', service.ipAddress),
          _buildInfoRow('Endereço MAC', t.mac.isNotEmpty ? t.mac : '—'),
          _buildInfoRow('Rede Wi-Fi (SSID)', t.ssid.isNotEmpty ? t.ssid : '—'),
          _buildInfoRow('Sinal Wi-Fi (RSSI)', t.rssi != 0 ? '${t.rssi} dBm' : '—'),
          _buildInfoRow('Tempo de Operação (Uptime)', _formatUptime(t.uptimeSeconds)),
          _buildInfoRow(
            'Memória Heap Livre',
            t.freeHeap > 0 ? '${(t.freeHeap / 1024).toStringAsFixed(1)} KB' : '—',
          ),
          _buildInfoRow('Falhas com o Hub', '${t.hubFailStreak} consecutivas'),
          _buildInfoRow(
            'Status Web OTA',
            t.otaInProgress ? 'ATUALIZAÇÃO EM ANDAMENTO' : 'Inativo (Normal)',
            color: t.otaInProgress ? AppTheme.accentAmber : Colors.white70,
          ),
          const SizedBox(height: 16),
          Center(
            child: Text(
              'Endpoints ativos: GET /diag | GET /read | POST /cmd | POST /update',
              style: TextStyle(fontSize: 11, color: Colors.grey.shade500),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildInfoRow(String label, String value, {Color? color}) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6.0),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          Text(label, style: const TextStyle(fontSize: 13, color: Colors.grey)),
          Text(
            value,
            style: TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w600,
              color: color ?? Colors.white,
            ),
          ),
        ],
      ),
    );
  }
}
