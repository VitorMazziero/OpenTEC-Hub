import 'package:flutter/material.dart';
import '../models/external_bath_state.dart';

/// Supervision of the Hub-routed external bath. Route change and cascade tuning stay on
/// the Windows app; the phone can stop the bath, reset a fault and switch the C404 guard.
class ExternalBathCard extends StatelessWidget {
  final ExternalBathState state;
  final double reactorTemperature;
  final bool busy;
  final VoidCallback onStop;
  final VoidCallback onResetFault;
  final ValueChanged<bool> onModeChanged;

  const ExternalBathCard({
    super.key,
    required this.state,
    required this.reactorTemperature,
    required this.busy,
    required this.onStop,
    required this.onResetFault,
    required this.onModeChanged,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final Color accent = state.isFault
        ? Colors.red.shade700
        : state.owned
            ? Colors.deepOrange.shade600
            : Colors.blueGrey;

    return Card(
      elevation: 2,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(16),
        side: BorderSide(color: accent.withValues(alpha: 0.6), width: 1.5),
      ),
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                CircleAvatar(
                  radius: 18,
                  backgroundColor: accent.withValues(alpha: 0.15),
                  child: Icon(Icons.hot_tub_outlined, color: accent),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text("Banho externo C404", style: theme.textTheme.titleMedium),
                      Text(state.summary,
                          style: theme.textTheme.bodySmall?.copyWith(color: accent)),
                    ],
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 16,
              runSpacing: 8,
              children: [
                _metric(theme, "Reator (Tempval)",
                    reactorTemperature > 0 ? "${reactorTemperature.toStringAsFixed(2)} °C" : "--"),
                _metric(theme, "Referência", ExternalBathState.fmt(state.reactorSetpoint)),
                _metric(theme, "C404 PV", ExternalBathState.fmt(state.bathPv)),
                _metric(theme, "C404 SP", ExternalBathState.fmt(state.bathSp)),
                _metric(theme, "Saída da cascata", ExternalBathState.fmt(state.commandSetpoint)),
              ],
            ),
            const SizedBox(height: 8),
            Text(
              state.owned
                  ? "O Hub controla o banho: comandos locais no nó ficam bloqueados (só Abortar)."
                  : "Banho livre: sem posse do Hub.",
              style: theme.textTheme.bodySmall,
            ),
            const SizedBox(height: 4),
            Text(
              "Parar/desligar a temperatura não desliga o C404: ele fica em manual no último SP.",
              style: theme.textTheme.bodySmall?.copyWith(color: Colors.red.shade700),
            ),
            if (state.viaBath && state.online) ...[
              const SizedBox(height: 8),
              SwitchListTile(
                contentPadding: EdgeInsets.zero,
                title: const Text("Guarda automática (C404)"),
                subtitle: const Text("Automático desfaz mudanças no painel; a cascata só controla em automático."),
                value: state.isAutomatic,
                onChanged: busy ? null : onModeChanged,
              ),
            ],
            const SizedBox(height: 8),
            Row(
              children: [
                Expanded(
                  child: FilledButton.icon(
                    style: FilledButton.styleFrom(backgroundColor: Colors.red.shade700),
                    onPressed: busy || !state.viaBath ? null : onStop,
                    icon: const Icon(Icons.stop_circle_outlined),
                    label: const Text("Parar banho"),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: OutlinedButton.icon(
                    onPressed: busy || !state.isFault ? null : onResetFault,
                    icon: const Icon(Icons.restart_alt),
                    label: const Text("Reset falha"),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Widget _metric(ThemeData theme, String label, String value) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label, style: theme.textTheme.labelSmall),
        Text(value, style: theme.textTheme.titleSmall),
      ],
    );
  }
}
