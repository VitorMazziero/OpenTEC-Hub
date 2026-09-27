import 'package:flutter/material.dart';
import '../theme/app_theme.dart';

enum StatusTone { ok, active, warning, off }

/// Live state of a device inside a control card: icon, label, optional reading and a
/// "pendente" chip while the Hub waits for the node to acknowledge a command.
class StatusStrip extends StatelessWidget {
  final String label;
  final String? value;
  final StatusTone tone;
  final bool pending;

  const StatusStrip({
    super.key,
    required this.label,
    this.value,
    required this.tone,
    this.pending = false,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final (Color color, IconData icon) = switch (tone) {
      StatusTone.ok => (AppColors.ok, Icons.check_circle),
      StatusTone.active => (theme.colorScheme.primary, Icons.play_circle),
      StatusTone.warning => (AppColors.warn, Icons.warning_amber_rounded),
      StatusTone.off => (theme.colorScheme.outline, Icons.power_settings_new),
    };

    return Container(
      margin: const EdgeInsets.only(bottom: 12),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.10),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Row(
        children: [
          Icon(icon, size: 18, color: color),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              label,
              style: theme.textTheme.bodyMedium?.copyWith(color: color, fontWeight: FontWeight.w600),
            ),
          ),
          if (pending) ...[
            SizedBox(
              width: 12,
              height: 12,
              child: CircularProgressIndicator(strokeWidth: 1.6, color: color),
            ),
            const SizedBox(width: 6),
            Text("pendente", style: theme.textTheme.labelSmall?.copyWith(color: color)),
            const SizedBox(width: 8),
          ],
          if (value != null)
            Text(value!, style: theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.bold)),
        ],
      ),
    );
  }
}

/// Snackbar confirming whether the Hub accepted a command.
void showCommandFeedback(BuildContext context, bool ok, String action) {
  if (!context.mounted) return;
  final messenger = ScaffoldMessenger.of(context);
  messenger.hideCurrentSnackBar();
  messenger.showSnackBar(
    SnackBar(
      content: Text(ok ? "$action — enviado" : "$action — falhou (Hub sem resposta?)"),
      backgroundColor: ok ? AppColors.ok : AppColors.danger,
      duration: const Duration(seconds: 2),
    ),
  );
}
