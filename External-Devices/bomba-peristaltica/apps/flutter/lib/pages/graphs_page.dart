import 'dart:async';
import 'package:flutter/material.dart';
import 'package:fl_chart/fl_chart.dart';
import 'package:provider/provider.dart';
import '../services/pump_connection_service.dart';

class GraphsPage extends StatefulWidget {
  const GraphsPage({Key? key}) : super(key: key);

  @override
  State<GraphsPage> createState() => _GraphsPageState();
}

class _GraphsPageState extends State<GraphsPage> {
  final List<FlSpot> _volumeSpots = [];
  final List<FlSpot> _vTargetSpots = [];
  final List<FlSpot> _flowRateSpots = [];
  final List<FlSpot> _speedSpots = [];

  StreamSubscription? _telemetrySubscription;
  bool _lastActiveState = false;

  @override
  void initState() {
    super.initState();
    // Delay subscription until first build to access context safely
    WidgetsBinding.instance.addPostFrameCallback((_) => _startListening());
  }

  void _startListening() {
    final service = Provider.of<PumpConnectionService>(context, listen: false);
    _telemetrySubscription = service.telemetryStream.listen((data) {
      final bool isActive = data['isActive'] as bool? ?? false;
      final double time = data['currentTime'] as double? ?? 0.0;
      final double vol = data['cumVolume'] as double? ?? 0.0;
      final double vTgt = data['vTarget'] as double? ?? 0.0;
      final double flow = data['flowRate'] as double? ?? 0.0;
      final double speed = data['speed'] as double? ?? 0.0;

      // Detect transition from inactive -> active to clear charts automatically
      if (isActive && !_lastActiveState) {
        _clearCharts();
      }
      _lastActiveState = isActive;

      if (isActive) {
        setState(() {
          // Append points
          _volumeSpots.add(FlSpot(time, vol));
          _vTargetSpots.add(FlSpot(time, vTgt));
          _flowRateSpots.add(FlSpot(time, flow));
          _speedSpots.add(FlSpot(time, speed));

          // Cap lists to avoid rendering lag
          if (_volumeSpots.length > 500) {
            _volumeSpots.removeAt(0);
            _vTargetSpots.removeAt(0);
            _flowRateSpots.removeAt(0);
            _speedSpots.removeAt(0);
          }
        });
      }
    });
  }

  void _clearCharts() {
    setState(() {
      _volumeSpots.clear();
      _vTargetSpots.clear();
      _flowRateSpots.clear();
      _speedSpots.clear();
    });
  }

  @override
  void dispose() {
    _telemetrySubscription?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: const Color(0xFF1E222B),
      body: SingleChildScrollView(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // --- Chart Controller Header ---
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                const Text(
                  'Real-Time Telemetry Plots',
                  style: TextStyle(color: Colors.white, fontSize: 16, fontWeight: FontWeight.bold),
                ),
                ElevatedButton.icon(
                  onPressed: _clearCharts,
                  icon: const Icon(Icons.clear_all, size: 16, color: Colors.black),
                  label: const Text('Clear Plots', style: TextStyle(color: Colors.black, fontSize: 12)),
                  style: ElevatedButton.styleFrom(
                    backgroundColor: Colors.amberAccent,
                    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                    shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 20),

            // --- Chart 1: Volume ---
            _buildChartCard(
              title: 'Cumulative Volume (mL) vs Time (min)',
              subtitle: 'Blue: Actual, Dashed Cyan: Target',
              chart: LineChart(_buildVolumeChartData()),
            ),
            const SizedBox(height: 20),

            // --- Chart 2: Flow Rate ---
            _buildChartCard(
              title: 'Flow Rate (mL/min) vs Time (min)',
              subtitle: 'Instantaneous flow rate setpoint',
              chart: LineChart(_buildFlowRateChartData()),
            ),
            const SizedBox(height: 20),

            // --- Chart 3: Motor Speed ---
            _buildChartCard(
              title: 'Motor Speed (steps/s) vs Time (min)',
              subtitle: 'Control command output',
              chart: LineChart(_buildSpeedChartData()),
            ),
            const SizedBox(height: 20),
          ],
        ),
      ),
    );
  }

  Widget _buildChartCard({
    required String title,
    required String subtitle,
    required Widget chart,
  }) {
    return Card(
      color: const Color(0xFF242A38),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(title, style: const TextStyle(color: Colors.white, fontSize: 14, fontWeight: FontWeight.bold)),
            Text(subtitle, style: const TextStyle(color: Colors.white30, fontSize: 11)),
            const SizedBox(height: 24),
            SizedBox(
              height: 200,
              child: Padding(
                padding: const EdgeInsets.only(right: 12.0),
                child: chart,
              ),
            ),
          ],
        ),
      ),
    );
  }

  LineChartData _buildVolumeChartData() {
    return LineChartData(
      gridData: const FlGridData(show: true, drawVerticalLine: true, horizontalInterval: 10),
      titlesData: _getAxisTitles(),
      borderData: FlBorderData(show: true, border: Border.all(color: Colors.white10)),
      lineBarsData: [
        // Actual volume
        LineChartBarData(
          spots: _volumeSpots.isEmpty ? [const FlSpot(0, 0)] : _volumeSpots,
          isCurved: true,
          barWidth: 3,
          color: Colors.blueAccent,
          dotData: const FlDotData(show: false),
        ),
        // Target volume
        LineChartBarData(
          spots: _vTargetSpots.isEmpty ? [const FlSpot(0, 0)] : _vTargetSpots,
          isCurved: true,
          barWidth: 2,
          color: Colors.cyanAccent,
          dashArray: [5, 5],
          dotData: const FlDotData(show: false),
        ),
      ],
    );
  }

  LineChartData _buildFlowRateChartData() {
    return LineChartData(
      gridData: const FlGridData(show: true, drawVerticalLine: true),
      titlesData: _getAxisTitles(),
      borderData: FlBorderData(show: true, border: Border.all(color: Colors.white10)),
      lineBarsData: [
        LineChartBarData(
          spots: _flowRateSpots.isEmpty ? [const FlSpot(0, 0)] : _flowRateSpots,
          isCurved: true,
          barWidth: 3,
          color: Colors.redAccent,
          dotData: const FlDotData(show: false),
        ),
      ],
    );
  }

  LineChartData _buildSpeedChartData() {
    return LineChartData(
      gridData: const FlGridData(show: true, drawVerticalLine: true),
      titlesData: _getAxisTitles(),
      borderData: FlBorderData(show: true, border: Border.all(color: Colors.white10)),
      lineBarsData: [
        LineChartBarData(
          spots: _speedSpots.isEmpty ? [const FlSpot(0, 0)] : _speedSpots,
          isCurved: true,
          barWidth: 3,
          color: Colors.orangeAccent,
          dotData: const FlDotData(show: false),
        ),
      ],
    );
  }

  FlTitlesData _getAxisTitles() {
    return FlTitlesData(
      show: true,
      topTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
      rightTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
      leftTitles: AxisTitles(
        sideTitles: SideTitles(
          showTitles: true,
          reservedSize: 40,
          getTitlesWidget: (value, meta) {
            return Text(
              value.toStringAsFixed(1),
              style: const TextStyle(color: Colors.white30, fontSize: 10),
            );
          },
        ),
      ),
      bottomTitles: AxisTitles(
        sideTitles: SideTitles(
          showTitles: true,
          reservedSize: 22,
          getTitlesWidget: (value, meta) {
            return Text(
              '${value.toStringAsFixed(1)}m',
              style: const TextStyle(color: Colors.white30, fontSize: 10),
            );
          },
        ),
      ),
    );
  }
}
