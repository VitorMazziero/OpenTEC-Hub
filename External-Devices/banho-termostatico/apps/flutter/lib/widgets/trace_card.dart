import 'package:flutter/material.dart';
import 'package:fl_chart/fl_chart.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';

/// Traço de 10 min (em memória) de display_pv e sp_shadow (plano §4.1).
class TraceCard extends StatelessWidget {
  final BathService service;
  const TraceCard({super.key, required this.service});

  @override
  Widget build(BuildContext context) {
    final trace = service.trace;
    if (trace.length < 2) {
      return const Card(
        child: Padding(
          padding: EdgeInsets.all(16),
          child: Row(children: [
            Icon(Icons.show_chart, color: Colors.grey),
            SizedBox(width: 10),
            Text('Coletando dados do traço (10 min)…',
                style: TextStyle(color: Colors.grey)),
          ]),
        ),
      );
    }

    final now = DateTime.now();
    final pvSpots = <FlSpot>[];
    final spSpots = <FlSpot>[];
    for (final p in trace) {
      final xMin = -now.difference(p.t).inMilliseconds / 60000.0; // min, ≤ 0
      if (p.pv != null) pvSpots.add(FlSpot(xMin, p.pv!));
      if (p.sp != null) spSpots.add(FlSpot(xMin, p.sp!));
    }

    return Card(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(12, 16, 16, 12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              const Icon(Icons.show_chart,
                  color: AppTheme.primaryCyan, size: 20),
              const SizedBox(width: 8),
              Text('Últimos 10 min',
                  style: Theme.of(context).textTheme.titleLarge),
              const Spacer(),
              _legend(AppTheme.primaryCyan, 'PV'),
              const SizedBox(width: 12),
              _legend(AppTheme.accentAmber, 'SP sombra'),
            ]),
            const SizedBox(height: 16),
            SizedBox(
              height: 180,
              child: LineChart(
                LineChartData(
                  minX: -10,
                  maxX: 0,
                  gridData: FlGridData(
                    show: true,
                    getDrawingHorizontalLine: (_) =>
                        FlLine(color: Colors.white.withValues(alpha: 0.06), strokeWidth: 1),
                    getDrawingVerticalLine: (_) =>
                        FlLine(color: Colors.white.withValues(alpha: 0.06), strokeWidth: 1),
                  ),
                  titlesData: FlTitlesData(
                    topTitles: const AxisTitles(
                        sideTitles: SideTitles(showTitles: false)),
                    rightTitles: const AxisTitles(
                        sideTitles: SideTitles(showTitles: false)),
                    bottomTitles: AxisTitles(
                      sideTitles: SideTitles(
                        showTitles: true,
                        interval: 2,
                        getTitlesWidget: (v, m) => Text(
                          v == 0 ? 'agora' : '${v.toInt()}',
                          style: const TextStyle(
                              color: Colors.grey, fontSize: 10),
                        ),
                      ),
                    ),
                    leftTitles: AxisTitles(
                      sideTitles: SideTitles(
                        showTitles: true,
                        reservedSize: 36,
                        getTitlesWidget: (v, m) => Text(
                          v.toStringAsFixed(0),
                          style: const TextStyle(
                              color: Colors.grey, fontSize: 10),
                        ),
                      ),
                    ),
                  ),
                  borderData: FlBorderData(show: false),
                  lineBarsData: [
                    if (spSpots.isNotEmpty)
                      LineChartBarData(
                        spots: spSpots,
                        isCurved: false,
                        color: AppTheme.accentAmber,
                        barWidth: 2,
                        dotData: const FlDotData(show: false),
                      ),
                    if (pvSpots.isNotEmpty)
                      LineChartBarData(
                        spots: pvSpots,
                        isCurved: true,
                        color: AppTheme.primaryCyan,
                        barWidth: 2.5,
                        dotData: const FlDotData(show: false),
                      ),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _legend(Color c, String label) => Row(mainAxisSize: MainAxisSize.min, children: [
        Container(width: 12, height: 3, color: c),
        const SizedBox(width: 4),
        Text(label, style: const TextStyle(fontSize: 11, color: Colors.grey)),
      ]);
}
