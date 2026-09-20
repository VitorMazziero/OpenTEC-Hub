import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';
import 'result_snackbar.dart';

/// Botão Abortar. Nunca é escondido nem desabilitado (plano §5.6). Abre todos
/// os relés e limpa a fila; no modo sombra deixa `sp_known = false`.
class EmergencyAbortButton extends StatelessWidget {
  final BathService service;
  const EmergencyAbortButton({super.key, required this.service});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: OutlinedButton.icon(
        icon: const Icon(Icons.pan_tool, size: 18),
        label: const Text(
          'Abortar',
          style: TextStyle(fontSize: 14, fontWeight: FontWeight.w600),
        ),
        style: OutlinedButton.styleFrom(
          foregroundColor: AppTheme.stopRed,
          side: BorderSide(color: AppTheme.stopRed.withValues(alpha: 0.7)),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(12),
          ),
          visualDensity: VisualDensity.compact,
        ),
        onPressed: () async {
          HapticFeedback.heavyImpact();
          final r = await service.abort();
          if (context.mounted) showCommandResult(context, r);
        },
      ),
    );
  }
}
