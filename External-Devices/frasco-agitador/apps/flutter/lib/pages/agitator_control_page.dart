import 'package:flutter/material.dart';
import '../services/agitator_service.dart';
import '../theme/app_theme.dart';
import '../widgets/connection_badge.dart';
import '../widgets/diagnostics_sheet.dart';
import '../widgets/emergency_stop_button.dart';
import '../widgets/motor_controls_card.dart';
import '../widgets/quick_presets_row.dart';
import '../widgets/speed_gauge_card.dart';

class AgitatorControlPage extends StatelessWidget {
  final AgitatorService service;

  const AgitatorControlPage({super.key, required this.service});

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: service,
      builder: (context, _) {
        return Scaffold(
          appBar: AppBar(
            title: const Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Frasco Agitador',
                  style: TextStyle(fontSize: 18, fontWeight: FontWeight.bold),
                ),
                Text(
                  'TECNAL OpenTEC Hub',
                  style: TextStyle(fontSize: 11, color: AppTheme.primaryCyan),
                ),
              ],
            ),
            actions: [
              ConnectionBadge(service: service),
              const SizedBox(width: 8),
              IconButton(
                tooltip: 'Saúde e Diagnóstico',
                icon: const Icon(Icons.analytics_outlined, color: Colors.white70),
                onPressed: () => DiagnosticsSheet.show(context, service),
              ),
              const SizedBox(width: 8),
            ],
          ),
          body: SafeArea(
            child: RefreshIndicator(
              color: AppTheme.primaryCyan,
              backgroundColor: AppTheme.darkCard,
              onRefresh: () => service.pollTelemetry(),
              child: SingleChildScrollView(
                physics: const AlwaysScrollableScrollPhysics(),
                padding: const EdgeInsets.symmetric(horizontal: 16.0, vertical: 12.0),
                child: Column(
                  children: [
                    // 1. Botão de Parada de Emergência
                    EmergencyStopButton(service: service),
                    const SizedBox(height: 14),

                    // 2. Card com Mostrador de Velocidade (Gauge)
                    SpeedGaugeCard(service: service),
                    const SizedBox(height: 14),

                    // 3. Ajustes Rápidos e Passo Fino
                    QuickPresetsRow(service: service),
                    const SizedBox(height: 14),

                    // 4. Parâmetros Operacionais (Sentido e Potenciômetro)
                    MotorControlsCard(service: service),
                    const SizedBox(height: 24),
                  ],
                ),
              ),
            ),
          ),
        );
      },
    );
  }
}
