import 'dart:math';
import 'package:flutter/material.dart';
import '../services/agitator_service.dart';
import '../theme/app_theme.dart';

class SpeedGaugeCard extends StatelessWidget {
  final AgitatorService service;

  const SpeedGaugeCard({super.key, required this.service});

  @override
  Widget build(BuildContext context) {
    final duty = service.targetDuty;
    final telemetryDuty = service.telemetry.duty;
    final isRunning = duty > 0.0;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20.0),
        child: Column(
          children: [
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Row(
                  children: [
                    Icon(
                      Icons.speed,
                      color: isRunning ? AppTheme.primaryCyan : Colors.grey,
                      size: 20,
                    ),
                    const SizedBox(width: 8),
                    const Text(
                      'Velocidade do Motor',
                      style: TextStyle(fontWeight: FontWeight.w600, fontSize: 16),
                    ),
                  ],
                ),
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                  decoration: BoxDecoration(
                    color: isRunning
                        ? AppTheme.primaryCyan.withValues(alpha: 0.15)
                        : Colors.white.withValues(alpha: 0.05),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Text(
                    isRunning ? 'EM OPERAÇÃO' : 'PARADO',
                    style: TextStyle(
                      fontSize: 11,
                      fontWeight: FontWeight.bold,
                      color: isRunning ? AppTheme.primaryCyan : Colors.grey,
                    ),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 24),

            // Gauge Circular com CustomPainter
            SizedBox(
              height: 200,
              width: 200,
              child: Stack(
                alignment: Alignment.center,
                children: [
                  CustomPaint(
                    size: const Size(200, 200),
                    painter: _GaugePainter(
                      progress: duty / 100.0,
                      telemetryProgress: telemetryDuty / 100.0,
                      activeColor: AppTheme.primaryCyan,
                    ),
                  ),
                  Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text(
                        duty.toStringAsFixed(1),
                        style: const TextStyle(
                          fontSize: 44,
                          fontWeight: FontWeight.bold,
                          letterSpacing: -1,
                          color: Colors.white,
                        ),
                      ),
                      const Text(
                        '% PWM',
                        style: TextStyle(
                          fontSize: 14,
                          color: Colors.grey,
                          fontWeight: FontWeight.w500,
                        ),
                      ),
                      if (service.isConnected && (telemetryDuty - duty).abs() > 1.0)
                        Padding(
                          padding: const EdgeInsets.only(top: 4.0),
                          child: Text(
                            'Real: ${telemetryDuty.toStringAsFixed(1)}%',
                            style: const TextStyle(
                              fontSize: 11,
                              color: AppTheme.accentAmber,
                              fontWeight: FontWeight.w500,
                            ),
                          ),
                        ),
                    ],
                  ),
                ],
              ),
            ),
            const SizedBox(height: 20),

            // Slider horizontal deslizante
            Slider(
              value: duty,
              min: 0.0,
              max: 100.0,
              divisions: 1000,
              label: '${duty.toStringAsFixed(1)}%',
              onChanged: (val) => service.setDuty(val),
            ),
          ],
        ),
      ),
    );
  }
}

class _GaugePainter extends CustomPainter {
  final double progress; // 0.0 - 1.0
  final double telemetryProgress;
  final Color activeColor;

  _GaugePainter({
    required this.progress,
    required this.telemetryProgress,
    required this.activeColor,
  });

  @override
  void paint(Canvas canvas, Size size) {
    final center = Offset(size.width / 2, size.height / 2);
    final radius = min(size.width / 2, size.height / 2) - 16;
    const startAngle = 135 * (pi / 180);
    const sweepAngle = 270 * (pi / 180);

    // 1. Fundo do arco
    final bgPaint = Paint()
      ..color = Colors.white.withValues(alpha: 0.08)
      ..style = PaintingStyle.stroke
      ..strokeWidth = 14
      ..strokeCap = StrokeCap.round;

    canvas.drawArc(
      Rect.fromCircle(center: center, radius: radius),
      startAngle,
      sweepAngle,
      false,
      bgPaint,
    );

    // 2. Arco do comando ativo
    if (progress > 0) {
      final activePaint = Paint()
        ..shader = SweepGradient(
          startAngle: startAngle,
          endAngle: startAngle + sweepAngle,
          colors: [
            activeColor.withValues(alpha: 0.6),
            activeColor,
          ],
        ).createShader(Rect.fromCircle(center: center, radius: radius))
        ..style = PaintingStyle.stroke
        ..strokeWidth = 14
        ..strokeCap = StrokeCap.round;

      canvas.drawArc(
        Rect.fromCircle(center: center, radius: radius),
        startAngle,
        sweepAngle * progress.clamp(0.0, 1.0),
        false,
        activePaint,
      );
    }

    // 3. Indicador da telemetria real (se houver discrepância)
    if (telemetryProgress > 0) {
      final markAngle = startAngle + sweepAngle * telemetryProgress.clamp(0.0, 1.0);
      final markPaint = Paint()
        ..color = AppTheme.accentAmber
        ..style = PaintingStyle.stroke
        ..strokeWidth = 3;

      final p1 = Offset(
        center.dx + (radius - 9) * cos(markAngle),
        center.dy + (radius - 9) * sin(markAngle),
      );
      final p2 = Offset(
        center.dx + (radius + 9) * cos(markAngle),
        center.dy + (radius + 9) * sin(markAngle),
      );
      canvas.drawLine(p1, p2, markPaint);
    }
  }

  @override
  bool shouldRepaint(covariant _GaugePainter oldDelegate) {
    return oldDelegate.progress != progress ||
        oldDelegate.telemetryProgress != telemetryProgress ||
        oldDelegate.activeColor != activeColor;
  }
}
