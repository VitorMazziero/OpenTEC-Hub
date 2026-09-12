import 'package:flutter/material.dart';
import 'package:fl_chart/fl_chart.dart';

class FlowGraph extends StatelessWidget {
  final double v0;
  final double vf;
  final double t0;
  final double tf;
  final double delta;

  const FlowGraph({
    super.key,
    required this.v0,
    required this.vf,
    required this.t0,
    required this.tf,
  })  : delta = (vf - v0) / (tf - t0);

  @override
  Widget build(BuildContext context) {
    final volumeData = List.generate(
      (tf * 1000).toInt(),
          (index) {
        final t = index / 1000;
        if (t < t0) {
          return FlSpot(t, v0);
        } else {
          return FlSpot(t, v0 + delta * (t - t0));
        }
      },
    );

    final flowData = List.generate(
      (tf * 1000).toInt(),
          (index) {
        final t = index / 1000;
        return FlSpot(t, t < t0 ? 0 : delta*1000/60);
      },
    );

    return Column(
      children: [
        _buildLineChart(volumeData, 'Volume (L)', vf, tf),
        const SizedBox(height: 20),
        _buildLineChart(flowData, 'Flow Rate (L/h)', delta*1000/60, tf),
      ],
    );
  }

  Widget _buildLineChart(List<FlSpot> data, String yAxisLabel, maxY, maxT) {
    return SizedBox(
      height: 300,
      child: LineChart(
        LineChartData(
          lineBarsData: [
            LineChartBarData(
              spots: data,
              isCurved: false,
              dotData: const FlDotData(show: false),
              belowBarData: BarAreaData(show: false),
            ),
          ],
          gridData: const FlGridData(show: true),
          borderData: FlBorderData(show: true),
          minY: delta > 0 ? 0 : -delta * 1000 / 60 * 1.5,
          minX: 0,
          maxY: delta > 0 ? maxY * 1.5: -delta * 1000 / 60 * 1.5,
          maxX: maxT * 1.25,
        ),
      ),
    );
  }
}