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
