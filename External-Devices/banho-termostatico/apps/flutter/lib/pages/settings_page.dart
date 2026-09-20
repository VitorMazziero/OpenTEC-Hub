import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';
import '../widgets/config_form.dart';
import '../widgets/diagnostics_sheet.dart';

/// Aba Configuração + diagnóstico. Plano §4.4.
class SettingsPage extends StatelessWidget {
  const SettingsPage({super.key});

  @override
  Widget build(BuildContext context) {
    final s = context.watch<BathService>();
    return ListView(
      padding: const EdgeInsets.all(12),
      children: [
        Card(
          color: AppTheme.darkCardElevated,
          child: Padding(
            padding: const EdgeInsets.all(14),
            child: Row(
              children: [
                ClipRRect(
                  borderRadius: BorderRadius.circular(12),
                  child: Image.asset(
                    'assets/icon/app_logo.png',
                    width: 52,
                    height: 52,
                  ),
                ),
                const SizedBox(width: 14),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const Text(
                        'Banho Lucadema',
                        style: TextStyle(
                          fontSize: 16,
                          fontWeight: FontWeight.bold,
                          color: Colors.white,
                        ),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        'OpenTEC-Hub (v1.0.0)',
                        style: TextStyle(
                          fontSize: 12,
                          color: Colors.grey.shade400,
                        ),
                      ),
                      const SizedBox(height: 2),
                      const Text(
                        'Autor: Vítor Mazziero',
                        style: TextStyle(
                          fontSize: 12,
                          color: Colors.white70,
                          fontWeight: FontWeight.w500,
                        ),
                      ),
                      const SizedBox(height: 2),
                      const Text(
                        'Controle térmico de precisão',
                        style: TextStyle(
                          fontSize: 12,
                          color: AppTheme.primaryCyan,
                          fontWeight: FontWeight.w500,
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 12),
        OutlinedButton.icon(
          onPressed: () => DiagnosticsSheet.show(context, s),
          icon: const Icon(Icons.monitor_heart, color: AppTheme.primaryCyan),
          label: const Text('Diagnóstico e log'),
        ),
        const SizedBox(height: 12),
        ConfigForm(service: s),
        const SizedBox(height: 8),
      ],
    );
  }
}
