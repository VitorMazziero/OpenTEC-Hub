import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../models/external_bath_state.dart';
import '../providers/connection_provider.dart';
import '../providers/telemetry_provider.dart';
import '../theme/app_theme.dart';
import '../widgets/metric_tile.dart';

/// Read-only overview: every process variable with its setpoint, then the external
/// nodes that are switched on. Setpoints are changed on the Controls tab.
class DashboardScreen extends StatelessWidget {
  const DashboardScreen({super.key});

  static const int expectedProtocol = 10;

  @override
  Widget build(BuildContext context) {
    final conn = context.watch<ConnectionProvider>();
    final p = context.watch<TelemetryProvider>();
    final t = p.telemetry;
    final servo = p.servoState;
    final bath = p.bathState;

    final tempSub = t.tempSetpointCommanded && t.tempSetpoint != null
        ? "SP ${t.tempSetpoint!.toStringAsFixed(1)} °C${bath.viaBath ? ' · banho' : ''}"
        : "Controle desligado${bath.viaBath ? ' · banho' : ''}";

    final viaServo = servo.viaModbus;
    final String servoValue;
    final String servoSub;
    if (viaServo && !servo.commEnabled) {
      servoValue = "--";
      servoSub = "Servo · comunicação desligada";
    } else if (viaServo && !servo.online) {
      servoValue = "--";
      servoSub = "Servo offline";
    } else {
      servoValue = servo.online && servo.hasTelemetry ? servo.rpm.toStringAsFixed(0) : "--";
      servoSub = servo.online && servo.isFaulted
          ? servo.stateDescription
          : viaServo
              ? "Servo · alvo ${servo.requestedRpm} rpm"
              : "Via placa controladora";
    }

    final peripherals = <Widget>[
      if (p.pumpState.commEnabled)
        MetricTile(
          title: "Bomba peristáltica",
          value: p.pumpState.isConnectedAndActive ? p.pumpState.flow.toStringAsFixed(2) : "--",
          unit: "mL/min",
          subtext: p.pumpState.isConnectedAndActive
              ? "${p.pumpState.statusLabel.split(' (').first} · ${p.pumpState.formattedVolume}"
              : p.pumpState.statusLabel,
          icon: Icons.water_drop,
          accentColor: AppColors.pump,
          isActive: p.pumpState.isConnectedAndActive,
        ),
      if (p.flowmeterState.commEnabled)
        MetricTile(
          title: "Vazão de gás",
          value: p.flowmeterState.isConnectedAndActive ? p.flowmeterState.flowRate.toStringAsFixed(2) : "--",
          unit: "L/min",
          subtext: p.flowmeterState.isConnectedAndActive
              ? "Alvo ${p.flowmeterState.flowSetpoint.toStringAsFixed(2)} L/min"
              : p.flowmeterState.statusLabel,
          icon: Icons.air,
          accentColor: AppColors.flow,
          isActive: p.flowmeterState.isConnectedAndActive,
        ),
      if (p.agitatorState.isOnline)
        MetricTile(
          title: "Frasco agitador",
          value: p.agitatorState.isConnectedAndActive ? p.agitatorState.speedPercent.toStringAsFixed(0) : "--",
          unit: "%",
          subtext: p.agitatorState.statusLabel,
          icon: Icons.rotate_right,
          accentColor: AppColors.flask,
          isActive: p.agitatorState.isSpinning,
        ),
      if (p.biomassState.commEnabled)
        MetricTile(
          title: "Biomassa",
          value: p.biomassState.isAcquiring ? p.biomassState.absorbance.toStringAsFixed(3) : "--",
          unit: "AU",
          subtext: p.biomassState.statusLabel,
          icon: Icons.grain,
          accentColor: AppColors.biomass,
          isActive: p.biomassState.isAcquiring,
        ),
      if (p.distanceState.commEnabled)
        MetricTile(
          title: "Distância",
          value: p.distanceState.isConnectedAndActive ? p.distanceState.distanceMm.toStringAsFixed(0) : "--",
          unit: "mm",
          subtext: p.distanceState.statusLabel,
          icon: Icons.radar,
          accentColor: AppColors.distance,
          isActive: p.distanceState.isConnectedAndActive,
        ),
    ];

    return ListView(
      padding: const EdgeInsets.all(12),
      children: [
        if (!conn.isConnected)
          const _Banner(
            icon: Icons.wifi_off,
            text: "Sem conexão com o Hub. Conecte o celular à rede Wi-Fi do Hub e toque no ícone de Wi-Fi.",
          )
        else if (t.hubProtocolVersion != 0 && t.hubProtocolVersion != expectedProtocol)
          _Banner(
            icon: Icons.warning_amber_rounded,
            text: "Hub com protocolo ${t.hubProtocolVersion}; este app espera o protocolo $expectedProtocol.",
          ),
        _TileGrid([
          MetricTile(
            title: "Temperatura",
            value: t.hasValidTemperature ? t.temperature.toStringAsFixed(1) : "--",
            unit: "°C",
            subtext: tempSub,
            icon: Icons.thermostat,
            accentColor: AppColors.temperature,
            isActive: t.hasValidTemperature,
          ),
          MetricTile(
            title: "Agitação",
            value: servoValue,
            unit: "rpm",
            subtext: servoSub,
            icon: Icons.cyclone,
            accentColor: AppColors.agitation,
            isActive: servo.online && servo.hasTelemetry,
          ),
          MetricTile(
            title: "pH",
            value: p.calibratedPh >= 0 ? p.calibratedPh.toStringAsFixed(2) : "--",
            subtext: t.hasValidPh ? "Sonda ok" : "Sonda ausente",
            icon: Icons.science_outlined,
            accentColor: AppColors.ph,
            isActive: p.calibratedPh >= 0,
          ),
          MetricTile(
            title: "Oxigênio dissolvido",
            value: p.calibratedOxygen >= 0 ? p.calibratedOxygen.toStringAsFixed(2) : "--",
            unit: "mg/L",
            subtext: t.hasValidOxygen ? "Sonda ok" : "Sonda ausente",
            icon: Icons.bubble_chart_outlined,
            accentColor: AppColors.oxygen,
            isActive: p.calibratedOxygen >= 0,
          ),
          MetricTile(
            title: "Pressão",
            value: t.pressure.toStringAsFixed(0),
            unit: "mmHg",
            icon: Icons.speed,
            accentColor: AppColors.pressure,
            isActive: conn.isConnected,
          ),
          MetricTile(
            title: "Espuma",
            value: t.antifoam > 0.5 ? "Espuma" : "Livre",
            subtext: "Sensor da placa",
            icon: Icons.waves,
            accentColor: AppColors.antifoam,
            isActive: t.antifoam > 0.5,
          ),
        ]),
        if (bath.hasTelemetry && bath.viaBath) ...[
          const SizedBox(height: 10),
          _BathSummary(),
        ],
        if (peripherals.isNotEmpty) ...[
          const SizedBox(height: 18),
          Text("Periféricos", style: Theme.of(context).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w600)),
          const SizedBox(height: 8),
          _TileGrid(peripherals),
        ],
        const SizedBox(height: 18),
        Center(
          child: Text(
            [
              if (t.hubFirmwareVersion.isNotEmpty) "Hub ${t.hubFirmwareVersion}",
              if (t.hubProtocolVersion != 0) "protocolo ${t.hubProtocolVersion}",
              "placa de sensores ${t.sensorCommOk ? 'ok' : 'sem resposta'}",
            ].join(" · "),
            style: Theme.of(context).textTheme.bodySmall?.copyWith(color: Theme.of(context).colorScheme.outline),
          ),
        ),
      ],
    );
  }
}

class _TileGrid extends StatelessWidget {
  final List<Widget> tiles;
  const _TileGrid(this.tiles);

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(builder: (context, c) {
      final columns = c.maxWidth > 700 ? 3 : 2;
      final width = (c.maxWidth - 10 * (columns - 1)) / columns;
      return Wrap(
        spacing: 10,
        runSpacing: 10,
        children: [for (final t in tiles) SizedBox(width: width, child: t)],
      );
    });
  }
}

class _Banner extends StatelessWidget {
  final IconData icon;
  final String text;
  const _Banner({required this.icon, required this.text});

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Container(
      margin: const EdgeInsets.only(bottom: 12),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(color: scheme.errorContainer, borderRadius: BorderRadius.circular(12)),
      child: Row(
        children: [
          Icon(icon, color: scheme.onErrorContainer),
          const SizedBox(width: 10),
          Expanded(child: Text(text, style: TextStyle(color: scheme.onErrorContainer))),
        ],
      ),
    );
  }
}

class _BathSummary extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final bath = context.watch<TelemetryProvider>().bathState;
    final theme = Theme.of(context);
    final color = bath.isFault ? AppColors.danger : AppColors.temperature;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Row(
          children: [
            Icon(Icons.hot_tub_outlined, color: color),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text("Banho externo C404", style: theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w600)),
                  Text(bath.summary, style: theme.textTheme.bodySmall?.copyWith(color: color)),
                ],
              ),
            ),
            Column(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Text("PV ${ExternalBathState.fmt(bath.bathPv)}", style: theme.textTheme.bodySmall),
                Text("SP ${ExternalBathState.fmt(bath.bathSp)}", style: theme.textTheme.bodySmall),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
