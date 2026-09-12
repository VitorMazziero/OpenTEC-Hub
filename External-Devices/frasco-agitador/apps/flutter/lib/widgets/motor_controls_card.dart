import 'package:flutter/material.dart';
import '../services/agitator_service.dart';
import '../theme/app_theme.dart';

class MotorControlsCard extends StatelessWidget {
  final AgitatorService service;

  const MotorControlsCard({super.key, required this.service});

  @override
  Widget build(BuildContext context) {
    final dirRight = service.dirRight;
    final potEnabled = service.potEnabled;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              'Parâmetros Operacionais',
              style: TextStyle(fontWeight: FontWeight.w600, fontSize: 15),
            ),
            const SizedBox(height: 16),

            // 1. Sentido de Rotação (Direção)
            Row(
              children: [
                Icon(
                  dirRight ? Icons.rotate_right : Icons.rotate_left,
                  color: AppTheme.primaryCyan,
                  size: 24,
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const Text(
                        'Sentido de Rotação',
                        style: TextStyle(fontSize: 14, fontWeight: FontWeight.w500),
                      ),
                      Text(
                        dirRight ? 'Horário (CW / Direita)' : 'Anti-Horário (CCW / Esquerda)',
                        style: TextStyle(
                          fontSize: 12,
                          color: dirRight ? AppTheme.primaryCyan : Colors.grey,
                        ),
                      ),
                    ],
                  ),
                ),
                Switch(
                  value: dirRight,
                  onChanged: (val) => service.setDirection(val),
                ),
              ],
            ),
            const Divider(height: 24, color: Colors.white12),

            // 2. Potenciômetro Físico (ActivePot)
            Row(
              children: [
                Icon(
                  Icons.tune,
                  color: potEnabled ? AppTheme.accentAmber : Colors.grey,
                  size: 24,
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const Text(
                        'Potenciômetro Físico',
                        style: TextStyle(fontSize: 14, fontWeight: FontWeight.w500),
                      ),
                      Text(
                        potEnabled ? 'Ativo (Knob físico habilitado)' : 'Travado (Controle 100% via App)',
                        style: TextStyle(
                          fontSize: 12,
                          color: potEnabled ? AppTheme.accentAmber : Colors.grey,
                        ),
                      ),
                    ],
                  ),
                ),
                Switch(
                  value: potEnabled,
                  activeThumbColor: AppTheme.accentAmber,
                  onChanged: (val) => service.setPotEnabled(val),
                ),
              ],
            ),
            if (potEnabled)
              Container(
                margin: const EdgeInsets.only(top: 12),
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: AppTheme.accentAmber.withValues(alpha: 0.1),
                  borderRadius: BorderRadius.circular(8),
                  border: Border.all(color: AppTheme.accentAmber.withValues(alpha: 0.3)),
                ),
                child: const Row(
                  children: [
                    Icon(Icons.info_outline, size: 16, color: AppTheme.accentAmber),
                    SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        'Com o potenciômetro ativo, o knob analógico do agitador pode sobrepor os comandos do app.',
                        style: TextStyle(fontSize: 11, color: AppTheme.accentAmber),
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
}
