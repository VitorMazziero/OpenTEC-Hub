import 'package:flutter/material.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';

/// Mostra o resultado literal de um comando (plano §5.1, §5.4). `busy` não é
/// erro do operador; recebe uma mensagem própria (plano §5.4).
void showCommandResult(BuildContext context, CommandResult r) {
  if (!context.mounted) return;
  String text;
  Color color;
  if (r.networkFailure) {
    text = 'Sem resposta do dispositivo — verifique a rede.';
    color = AppTheme.stopRed;
  } else if (r.ok) {
    text = r.action == 'config_unchanged'
        ? 'Nada mudou na configuração.'
        : 'OK: ${r.action.isNotEmpty ? r.action : "aceito"}';
    color = AppTheme.okGreen;
  } else if (r.error == 'busy') {
    text = 'Sequência em andamento — aguarde ou aborte.';
    color = AppTheme.accentAmber;
  } else if (r.error == 'duplicate') {
    text = 'Comando já aplicado (reentrega).';
    color = AppTheme.okGreen;
  } else {
    // Erro literal do nó, nunca traduzido para um valor plausível.
    text = 'Recusado: ${r.error}';
    color = AppTheme.stopRed;
  }
  ScaffoldMessenger.of(context).showSnackBar(SnackBar(
    content: Text(text),
    backgroundColor: color,
    duration: const Duration(seconds: 3),
  ));
}
