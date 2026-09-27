import 'package:flutter/material.dart';
import 'package:fl_chart/fl_chart.dart';
import 'package:provider/provider.dart';
import '../providers/telemetry_provider.dart';

enum GraphMetric {
  temperature,
  ph,
  oxygen,
  pressure,
  servoRpm,
  servoTorquePower,
  distance,
  biomass,
  flowRate,
  agitatorSpeed,
  pumpFlow,
  pumpVolume,
}

class GraphsScreen extends StatefulWidget {
  const GraphsScreen({super.key});

  @override
  State<GraphsScreen> createState() => _GraphsScreenState();
}

class _GraphsScreenState extends State<GraphsScreen> {
  GraphMetric _selectedMetric = GraphMetric.temperature;

  @override
  Widget build(BuildContext context) {
    final telemetry = context.watch<TelemetryProvider>();
    final theme = Theme.of(context);

    return Scaffold(
      body: Padding(
        padding: const EdgeInsets.all(12.0),
        child: Column(
          children: [
            // Metric selector chips
            SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(
                children: [
                  _buildFilterChip("Temperatura", GraphMetric.temperature, Colors.redAccent),
                  const SizedBox(width: 6),
                  _buildFilterChip("pH", GraphMetric.ph, Colors.blueAccent),
                  const SizedBox(width: 6),
                  _buildFilterChip("OD", GraphMetric.oxygen, Colors.teal),
                  const SizedBox(width: 6),
                  _buildFilterChip("Pressão", GraphMetric.pressure, Colors.orangeAccent),
                  const SizedBox(width: 6),
                  _buildFilterChip("Agitação", GraphMetric.servoRpm, Colors.green),
                  const SizedBox(width: 6),
                  _buildFilterChip("Torque e potência", GraphMetric.servoTorquePower, Colors.purple),
                  const SizedBox(width: 6),
                  _buildFilterChip("Distância", GraphMetric.distance, Colors.indigo),
                  const SizedBox(width: 6),
                  _buildFilterChip("Biomassa", GraphMetric.biomass, Colors.teal),
                  const SizedBox(width: 6),
                  _buildFilterChip("Vazão de gás", GraphMetric.flowRate, Colors.cyan),
                  const SizedBox(width: 6),
                  _buildFilterChip("Frasco agitador", GraphMetric.agitatorSpeed, Colors.deepPurple),
                  const SizedBox(width: 6),
                  _buildFilterChip("Vazão da bomba", GraphMetric.pumpFlow, Colors.orange.shade800),
                  const SizedBox(width: 6),
                  _buildFilterChip("Volume dosado", GraphMetric.pumpVolume, Colors.amber.shade900),
                ],
              ),
            ),
            const SizedBox(height: 12),

            // Active Chart Display
            Expanded(
              child: Card(
                elevation: 2,
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
                child: Padding(
                  padding: const EdgeInsets.fromLTRB(16, 16, 20, 12),
                  child: _buildSelectedChart(telemetry, theme),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildFilterChip(String label, GraphMetric metric, Color color) {
    final isSelected = _selectedMetric == metric;
    return FilterChip(
      label: Text(label),
      selected: isSelected,
      onSelected: (_) => setState(() => _selectedMetric = metric),
      selectedColor: color.withValues(alpha: 0.2),
      checkmarkColor: color,
      labelStyle: TextStyle(
        color: isSelected ? color : null,
        fontWeight: isSelected ? FontWeight.bold : FontWeight.normal,
      ),
    );
  }

  Widget _buildSelectedChart(TelemetryProvider prov, ThemeData theme) {
    switch (_selectedMetric) {
      case GraphMetric.temperature:
        return _buildSingleLineChart(
          title: "Temperatura (°C)",
          points: prov.tempHistory,
          color: Colors.redAccent,
          unit: "°C",
        );
      case GraphMetric.ph:
        return _buildSingleLineChart(
          title: "pH",
          points: prov.phHistory,
          color: Colors.blueAccent,
          unit: "pH",
          minY: 0,
          maxY: 14,
        );
      case GraphMetric.oxygen:
        return _buildSingleLineChart(
          title: "Oxigênio dissolvido (mg/L)",
          points: prov.oxygenHistory,
          color: Colors.teal,
          unit: "mg/L",
          minY: 0,
        );
      case GraphMetric.pressure:
        return _buildSingleLineChart(
          title: "Pressão (mmHg)",
          points: prov.pressureHistory,
          color: Colors.orangeAccent,
          unit: "mmHg",
        );
      case GraphMetric.servoRpm:
        return _buildServoRpmChart(prov);
      case GraphMetric.servoTorquePower:
        return _buildServoTorquePowerChart(prov);
      case GraphMetric.distance:
        return _buildSingleLineChart(
          title: "Distância (mm)",
          points: prov.distanceHistory,
          color: Colors.indigo,
          unit: "mm",
          minY: 0,
        );
      case GraphMetric.biomass:
        return _buildSingleLineChart(
          title: "Biomassa (AU)",
          points: prov.biomassHistory,
          color: Colors.teal,
          unit: "AU",
          minY: 0,
        );
      case GraphMetric.flowRate:
        return _buildSingleLineChart(
          title: "Vazão de gás (L/min)",
          points: prov.flowRateHistory,
          color: Colors.cyan.shade700,
          unit: "L/min",
          minY: 0,
        );
      case GraphMetric.agitatorSpeed:
        return _buildSingleLineChart(
          title: "Frasco agitador (%)",
          points: prov.agitatorSpeedHistory,
          color: Colors.deepPurple,
          unit: "%",
          minY: 0,
          maxY: 100,
        );
      case GraphMetric.pumpFlow:
        return _buildSingleLineChart(
          title: "Vazão da bomba (mL/min)",
          points: prov.pumpFlowHistory,
          color: Colors.orange.shade800,
          unit: "mL/min",
          minY: 0,
        );
      case GraphMetric.pumpVolume:
        return _buildSingleLineChart(
          title: "Volume dosado (mL)",
          points: prov.pumpVolumeHistory,
          color: Colors.amber.shade900,
          unit: "mL",
          minY: 0,
        );
    }
  }

  Widget _buildSingleLineChart({
    required String title,
    required List<TelemetryDataPoint> points,
    required Color color,
    required String unit,
    double? minY,
    double? maxY,
  }) {
    if (points.isEmpty) {
      return Center(
        child: Text(
          "Aguardando dados do Hub...",
          style: TextStyle(color: Colors.grey.shade600),
        ),
      );
    }

    final latestVal = points.last.value;
    final spots = points.map((p) => FlSpot(p.time, p.value)).toList();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Text(title, style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 16)),
            Text(
              "${latestVal.toStringAsFixed(2)} $unit",
              style: TextStyle(fontWeight: FontWeight.bold, fontSize: 18, color: color),
            ),
          ],
        ),
        const SizedBox(height: 16),
        Expanded(
          child: LineChart(
            LineChartData(
              minY: minY,
              maxY: maxY,
              lineBarsData: [
                LineChartBarData(
                  spots: spots,
                  isCurved: true,
                  color: color,
                  barWidth: 2.5,
                  isStrokeCapRound: true,
                  dotData: const FlDotData(show: false),
                  belowBarData: BarAreaData(
                    show: true,
                    color: color.withValues(alpha: 0.08),
                  ),
                ),
              ],
              titlesData: FlTitlesData(
                leftTitles: const AxisTitles(
                  sideTitles: SideTitles(showTitles: true, reservedSize: 42),
                ),
                bottomTitles: AxisTitles(
                  sideTitles: SideTitles(
                    showTitles: true,
                    reservedSize: 28,
                    interval: _calculateInterval(points),
                  ),
                ),
                topTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
                rightTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
              ),
              gridData: FlGridData(
                show: true,
                drawVerticalLine: true,
                getDrawingHorizontalLine: (v) => FlLine(color: Colors.grey.shade200, strokeWidth: 0.8),
                getDrawingVerticalLine: (v) => FlLine(color: Colors.grey.shade200, strokeWidth: 0.8),
              ),
              borderData: FlBorderData(
                show: true,
                border: Border.all(color: Colors.grey.shade300),
              ),
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildServoRpmChart(TelemetryProvider prov) {
    final actualPoints = prov.servoActualRpmHistory;
    final appliedPoints = prov.servoAppliedRpmHistory;

    if (actualPoints.isEmpty && appliedPoints.isEmpty) {
      return Center(
        child: Text(
          "Aguardando dados do servo...",
          style: TextStyle(color: Colors.grey.shade600),
        ),
      );
    }

    final actualSpots = actualPoints.map((p) => FlSpot(p.time, p.value)).toList();
    final appliedSpots = appliedPoints.map((p) => FlSpot(p.time, p.value)).toList();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            const Text("Agitação (medida × comandada, rpm)", style: TextStyle(fontWeight: FontWeight.bold, fontSize: 16)),
            Row(
              children: [
                _buildLegendItem("Medida", Colors.green),
                const SizedBox(width: 8),
                _buildLegendItem("Comandada", Colors.blue),
              ],
            ),
          ],
        ),
        const SizedBox(height: 16),
        Expanded(
          child: LineChart(
            LineChartData(
              minY: 0,
              maxY: 1000,
              lineBarsData: [
                if (appliedSpots.isNotEmpty)
                  LineChartBarData(
                    spots: appliedSpots,
                    isCurved: false,
                    color: Colors.blue.withValues(alpha: 0.6),
                    barWidth: 2,
                    dashArray: [5, 4],
                    dotData: const FlDotData(show: false),
                  ),
                if (actualSpots.isNotEmpty)
                  LineChartBarData(
                    spots: actualSpots,
                    isCurved: true,
                    color: Colors.green,
                    barWidth: 2.5,
                    dotData: const FlDotData(show: false),
                    belowBarData: BarAreaData(show: true, color: Colors.green.withValues(alpha: 0.06)),
                  ),
              ],
              titlesData: FlTitlesData(
                leftTitles: const AxisTitles(sideTitles: SideTitles(showTitles: true, reservedSize: 42)),
                bottomTitles: AxisTitles(
                  sideTitles: SideTitles(
                    showTitles: true,
                    reservedSize: 28,
                    interval: _calculateInterval(actualPoints.isNotEmpty ? actualPoints : appliedPoints),
                  ),
                ),
                topTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
                rightTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
              ),
              gridData: FlGridData(
                show: true,
                getDrawingHorizontalLine: (v) => FlLine(color: Colors.grey.shade200, strokeWidth: 0.8),
                getDrawingVerticalLine: (v) => FlLine(color: Colors.grey.shade200, strokeWidth: 0.8),
              ),
              borderData: FlBorderData(
                show: true,
                border: Border.all(color: Colors.grey.shade300),
              ),
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildServoTorquePowerChart(TelemetryProvider prov) {
    final torquePoints = prov.servoTorqueHistory;
    final powerPoints = prov.servoPowerHistory;

    if (torquePoints.isEmpty && powerPoints.isEmpty) {
      return Center(
        child: Text(
          "Aguardando leitura do servo...",
          style: TextStyle(color: Colors.grey.shade600),
        ),
      );
    }

    final torqueSpots = torquePoints.map((p) => FlSpot(p.time, p.value)).toList();
    final powerSpots = powerPoints.map((p) => FlSpot(p.time, p.value)).toList();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            const Text("Torque (%) e potência (W)", style: TextStyle(fontWeight: FontWeight.bold, fontSize: 16)),
            Row(
              children: [
                _buildLegendItem("Torque %", Colors.orange),
                const SizedBox(width: 8),
                _buildLegendItem("Potência W", Colors.purple),
              ],
            ),
          ],
        ),
        const SizedBox(height: 16),
        Expanded(
          child: LineChart(
            LineChartData(
              lineBarsData: [
                if (torqueSpots.isNotEmpty)
                  LineChartBarData(
                    spots: torqueSpots,
                    isCurved: true,
                    color: Colors.orange,
                    barWidth: 2,
                    dotData: const FlDotData(show: false),
                  ),
                if (powerSpots.isNotEmpty)
                  LineChartBarData(
                    spots: powerSpots,
                    isCurved: true,
                    color: Colors.purple,
                    barWidth: 2,
                    dotData: const FlDotData(show: false),
                  ),
              ],
              titlesData: FlTitlesData(
                leftTitles: const AxisTitles(sideTitles: SideTitles(showTitles: true, reservedSize: 42)),
                bottomTitles: AxisTitles(
                  sideTitles: SideTitles(
                    showTitles: true,
                    reservedSize: 28,
                    interval: _calculateInterval(torquePoints.isNotEmpty ? torquePoints : powerPoints),
                  ),
                ),
                topTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
                rightTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
              ),
              gridData: FlGridData(
                show: true,
                getDrawingHorizontalLine: (v) => FlLine(color: Colors.grey.shade200, strokeWidth: 0.8),
                getDrawingVerticalLine: (v) => FlLine(color: Colors.grey.shade200, strokeWidth: 0.8),
              ),
              borderData: FlBorderData(
                show: true,
                border: Border.all(color: Colors.grey.shade300),
              ),
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildLegendItem(String label, Color color) {
    return Row(
      children: [
        Container(width: 10, height: 10, decoration: BoxDecoration(color: color, shape: BoxShape.circle)),
        const SizedBox(width: 4),
        Text(label, style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w500)),
      ],
    );
  }

  double _calculateInterval(List<TelemetryDataPoint> points) {
    if (points.length < 2) return 1.0;
    final diff = points.last.time - points.first.time;
    if (diff <= 0) return 1.0;
    return (diff / 5).clamp(1.0, double.infinity);
  }
}
