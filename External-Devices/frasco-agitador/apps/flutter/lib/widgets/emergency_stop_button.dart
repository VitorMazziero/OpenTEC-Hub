import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import '../services/agitator_service.dart';
import '../theme/app_theme.dart';

class EmergencyStopButton extends StatelessWidget {
  final AgitatorService service;

  const EmergencyStopButton({super.key, required this.service});

  @override
  Widget build(BuildContext context) {
    final isRunning = service.targetDuty > 0.0;

    return SizedBox(
      width: double.infinity,
      height: 56,
      child: ElevatedButton.icon(
        icon: const Icon(Icons.stop_circle, size: 28),
        label: Text(
          isRunning ? 'PARADA DE EMERGÊNCIA (FREIO)' : 'MOTOR DESLIGADO (SEGURO)',
          style: const TextStyle(
            fontSize: 15,
            fontWeight: FontWeight.bold,
            letterSpacing: 0.5,
          ),
        ),
        style: ElevatedButton.styleFrom(
          backgroundColor: isRunning ? AppTheme.stopRed : const Color(0xFF2B3240),
          foregroundColor: Colors.white,
          elevation: isRunning ? 6 : 0,
          shadowColor: isRunning ? AppTheme.stopRed.withValues(alpha: 0.5) : Colors.transparent,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(16),
            side: BorderSide(
              color: isRunning ? Colors.white.withValues(alpha: 0.3) : Colors.transparent,
              width: 1.5,
            ),
          ),
        ),
        onPressed: () {
          HapticFeedback.heavyImpact();
          service.emergencyStop();
          ScaffoldMessenger.of(context).showSnackBar(
            const SnackBar(
              content: Text('Comando de parada e freio enviado ao motor.'),
              duration: Duration(seconds: 2),
              backgroundColor: AppTheme.stopRed,
            ),
          );
        },
      ),
    );
  }
}
