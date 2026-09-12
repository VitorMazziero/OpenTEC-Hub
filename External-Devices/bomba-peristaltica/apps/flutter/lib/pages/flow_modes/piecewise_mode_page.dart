import 'package:flutter/material.dart';
import '../../services/pump_connection_service.dart';

class PiecewiseModePage extends StatefulWidget {
  final PumpConnectionService connectionService;

  const PiecewiseModePage({Key? key, required this.connectionService}) : super(key: key);

  @override
  _PiecewiseModePageState createState() => _PiecewiseModePageState();
}

class _PiecewiseModePageState extends State<PiecewiseModePage> {
  final TextEditingController initTimeController = TextEditingController(text: '0.0');
  final TextEditingController finalTimeController = TextEditingController(text: '60.0');

  // Fields for adding a new point
  final TextEditingController pointTimeController = TextEditingController();
  final TextEditingController pointFlowController = TextEditingController();

  List<Map<String, double>> points = [
    {'t': 0.0, 'q': 2.0},
    {'t': 20.0, 'q': 10.0},
    {'t': 60.0, 'q': 5.0},
  ];

  @override
  void dispose() {
    initTimeController.dispose();
    finalTimeController.dispose();
    pointTimeController.dispose();
    pointFlowController.dispose();
    super.dispose();
  }

  void _addPoint() {
    final double? t = double.tryParse(pointTimeController.text);
    final double? q = double.tryParse(pointFlowController.text);

    if (t == null || q == null) {
      _showSnackBar('Please enter valid numeric time and flow rate.', Colors.redAccent);
      return;
    }

    if (t < 0) {
      _showSnackBar('Time point cannot be negative.', Colors.redAccent);
      return;
    }

    if (points.any((p) => p['t'] == t)) {
      _showSnackBar('A point at time $t min already exists.', Colors.redAccent);
      return;
    }

    setState(() {
      points.add({'t': t, 'q': q});
      // Sort points by time ascending
      points.sort((a, b) => a['t']!.compareTo(b['t']!));
    });

    pointTimeController.clear();
    pointFlowController.clear();
    _showSnackBar('Point added!', Colors.greenAccent);
  }

  void _deletePoint(int index) {
    if (points[index]['t'] == 0.0) {
      _showSnackBar('First point (t0 = 0.0) cannot be deleted.', Colors.redAccent);
      return;
    }
    setState(() {
      points.removeAt(index);
    });
    _showSnackBar('Point removed.', Colors.amberAccent);
  }

  void _sendProfile() {
    final double? initT = double.tryParse(initTimeController.text);
    final double? finalT = double.tryParse(finalTimeController.text);

    if (initT == null || finalT == null) {
      _showSnackBar('Please enter valid numeric times.', Colors.redAccent);
      return;
    }

    if (finalT <= initT && finalT > 0) {
      _showSnackBar('Final time must be greater than start time.', Colors.redAccent);
      return;
    }

    if (points.length < 2) {
      _showSnackBar('At least 2 points are required to define segments.', Colors.redAccent);
      return;
    }

    if (points.first['t'] != 0.0) {
      _showSnackBar('The first point (t0) must start at 0.0 min.', Colors.redAccent);
      return;
    }

    if (points.length > 100) {
      _showSnackBar('Maximum of 100 segments allowed by ESP32.', Colors.redAccent);
      return;
    }

    // Build the piecewise linear payload
    final Map<String, dynamic> payload = {
      'mode': 5,
      'init_t': initT,
      'final_t': finalT,
      'num_segments': points.length,
    };

    for (int i = 0; i < points.length; i++) {
      payload['t$i'] = points[i]['t'];
      payload['q$i'] = points[i]['q'];
    }

    widget.connectionService.sendCommand(payload);
    _showSnackBar('Piecewise profile uploaded!', Colors.greenAccent);
  }

  void _sendGlobalCommand(String cmd) {
    widget.connectionService.sendCommand({'command': cmd});
    _showSnackBar('Command "$cmd" sent.', Colors.blueAccent);
  }

  void _showSnackBar(String msg, Color color) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(msg, style: const TextStyle(fontWeight: FontWeight.bold)),
        backgroundColor: color.withOpacity(0.9),
        duration: const Duration(seconds: 2),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final telemetry = widget.connectionService;

    return Scaffold(
      backgroundColor: const Color(0xFF1E222B),
      body: SingleChildScrollView(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // --- Live Telemetry Dashboard ---
            _buildTelemetryDashboard(telemetry),
            const SizedBox(height: 16),

            // --- Global Action Panel ---
            _buildGlobalActionPanel(),
            const SizedBox(height: 24),

            // --- Piecewise Config Form ---
            _buildPiecewiseForm(),
          ],
        ),
      ),
    );
  }

  Widget _buildTelemetryDashboard(PumpConnectionService tel) {
    String status = 'IDLE';
    Color statusColor = Colors.grey;
    if (tel.isActive) {
      status = 'RUNNING';
      statusColor = Colors.greenAccent;
    } else if (tel.isWaiting) {
      status = 'WAITING';
      statusColor = Colors.amberAccent;
    }

    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: const Color(0xFF242A38),
        borderRadius: BorderRadius.circular(16),
        boxShadow: [
          BoxShadow(
            color: Colors.black.withOpacity(0.3),
            blurRadius: 10,
            offset: const Offset(0, 4),
          ),
        ],
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              const Text(
                'Live Telemetry',
                style: TextStyle(color: Colors.white, fontSize: 16, fontWeight: FontWeight.bold),
              ),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                decoration: BoxDecoration(
                  color: statusColor.withOpacity(0.15),
                  borderRadius: BorderRadius.circular(20),
                  border: Border.all(color: statusColor, width: 1.5),
                ),
                child: Text(
                  status,
                  style: TextStyle(color: statusColor, fontSize: 11, fontWeight: FontWeight.bold),
                ),
              ),
            ],
          ),
          const SizedBox(height: 16),
          Row(
            children: [
              Expanded(
                child: _buildTelemetryStat(
                  'Volume',
                  '${tel.cumVolume.toStringAsFixed(2)} mL',
                  subtitle: tel.isActive ? 'Tgt: ${tel.vTarget.toStringAsFixed(1)} mL' : null,
                ),
              ),
              Container(width: 1, height: 40, color: Colors.white10),
              Expanded(
                child: _buildTelemetryStat(
                  'Flow Rate',
                  '${tel.flowRate.toStringAsFixed(2)} mL/min',
                ),
              ),
              Container(width: 1, height: 40, color: Colors.white10),
              Expanded(
                child: _buildTelemetryStat(
                  'Time',
                  '${tel.currentTime.toStringAsFixed(2)} min',
                  subtitle: tel.finalTime > 0 ? 'Limit: ${tel.finalTime.toStringAsFixed(1)}' : null,
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          LinearProgressIndicator(
            value: (tel.finalTime > 0 && tel.currentTime <= tel.finalTime)
                ? (tel.currentTime / tel.finalTime)
                : 0.0,
            backgroundColor: const Color(0xFF1E222B),
            color: Colors.greenAccent,
          ),
        ],
      ),
    );
  }

  Widget _buildTelemetryStat(String label, String value, {String? subtitle}) {
    return Column(
      children: [
        Text(label, style: const TextStyle(color: Colors.white38, fontSize: 11)),
        const SizedBox(height: 4),
        Text(value, style: const TextStyle(color: Colors.white, fontSize: 16, fontWeight: FontWeight.bold)),
        if (subtitle != null) ...[
          const SizedBox(height: 2),
          Text(subtitle, style: const TextStyle(color: Colors.white30, fontSize: 10)),
        ],
      ],
    );
  }

  Widget _buildGlobalActionPanel() {
    return Container(
      padding: const EdgeInsets.symmetric(vertical: 12, horizontal: 16),
      decoration: BoxDecoration(
        color: const Color(0xFF161A22),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.spaceEvenly,
        children: [
          _buildActionButton(
            icon: Icons.play_arrow,
            label: 'Start',
            color: Colors.greenAccent,
            onTap: () => _sendGlobalCommand('start'),
          ),
          _buildActionButton(
            icon: Icons.stop,
            label: 'Stop',
            color: Colors.redAccent,
            onTap: () => _sendGlobalCommand('stop'),
          ),
          _buildActionButton(
            icon: Icons.refresh,
            label: 'Reset Vol',
            color: Colors.amberAccent,
            onTap: () => _sendGlobalCommand('reset_volume'),
          ),
          _buildActionButton(
            icon: Icons.save_outlined,
            label: 'Save Config',
            color: Colors.blueAccent,
            onTap: () => _sendGlobalCommand('save_config'),
          ),
        ],
      ),
    );
  }

  Widget _buildActionButton({
    required IconData icon,
    required String label,
    required Color color,
    required VoidCallback onTap,
  }) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(8),
      child: Padding(
        padding: const EdgeInsets.all(8.0),
        child: Column(
          children: [
            Icon(icon, color: color, size: 24),
            const SizedBox(height: 4),
            Text(
              label,
              style: TextStyle(color: color.withOpacity(0.8), fontSize: 10, fontWeight: FontWeight.bold),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildPiecewiseForm() {
    return Card(
      color: const Color(0xFF242A38),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const Text(
              'Piecewise Linear Profile',
              style: TextStyle(color: Colors.white, fontSize: 14, fontWeight: FontWeight.bold),
            ),
            const SizedBox(height: 16),
            Row(
              children: [
                Expanded(
                  child: _buildTextField(
                    controller: initTimeController,
                    label: 'Initial Time [min]',
                    hint: '0.0',
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: _buildTextField(
                    controller: finalTimeController,
                    label: 'Final Time [min]',
                    hint: '60.0',
                  ),
                ),
              ],
            ),
            const SizedBox(height: 20),
            const Divider(color: Colors.white10),
            const SizedBox(height: 10),
            const Text(
              'Add Profile Data Point (t\', Q)',
              style: TextStyle(color: Colors.white70, fontSize: 13, fontWeight: FontWeight.bold),
            ),
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: _buildTextField(
                    controller: pointTimeController,
                    label: 'Relative Time t\' [min]',
                    hint: '30.0',
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: _buildTextField(
                    controller: pointFlowController,
                    label: 'Flow Q [mL/min]',
                    hint: '8.0',
                  ),
                ),
                const SizedBox(width: 12),
                Padding(
                  padding: const EdgeInsets.only(top: 18.0),
                  child: ElevatedButton(
                    onPressed: _addPoint,
                    style: ElevatedButton.styleFrom(
                      backgroundColor: Colors.greenAccent,
                      foregroundColor: Colors.black,
                      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                    ),
                    child: const Icon(Icons.add, size: 20),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 20),
            const Text(
              'Profile Coordinates Table:',
              style: TextStyle(color: Colors.white70, fontSize: 12, fontWeight: FontWeight.bold),
            ),
            const SizedBox(height: 8),
            Container(
              height: 180,
              decoration: BoxDecoration(
                color: const Color(0xFF1E222B),
                borderRadius: BorderRadius.circular(8),
              ),
              child: points.isEmpty
                  ? const Center(
                      child: Text('No points defined.', style: TextStyle(color: Colors.white24, fontSize: 12)),
                    )
                  : ListView.builder(
                      itemCount: points.length,
                      itemBuilder: (context, index) {
                        final p = points[index];
                        return Container(
                          decoration: const BoxDecoration(
                            border: Border(bottom: BorderSide(color: Colors.white10, width: 0.5)),
                          ),
                          child: ListTile(
                            dense: true,
                            title: Text(
                              'Time t\' = ${p['t']!.toStringAsFixed(1)} min',
                              style: const TextStyle(color: Colors.white, fontWeight: FontWeight.bold, fontSize: 12),
                            ),
                            subtitle: Text(
                              'Flow Q = ${p['q']!.toStringAsFixed(2)} mL/min',
                              style: const TextStyle(color: Colors.white54, fontSize: 11),
                            ),
                            trailing: IconButton(
                              icon: const Icon(Icons.delete, color: Colors.redAccent, size: 18),
                              onPressed: () => _deletePoint(index),
                            ),
                          ),
                        );
                      },
                    ),
            ),
            const SizedBox(height: 20),
            ElevatedButton(
              onPressed: _sendProfile,
              style: ElevatedButton.styleFrom(
                backgroundColor: const Color(0xFF2E3B4E),
                foregroundColor: Colors.white,
                padding: const EdgeInsets.symmetric(vertical: 14),
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
              ),
              child: const Text('Upload & Load Piecewise Profile', style: TextStyle(fontWeight: FontWeight.bold)),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildTextField({
    required TextEditingController controller,
    required String label,
    required String hint,
  }) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label, style: const TextStyle(color: Colors.white70, fontSize: 11)),
        const SizedBox(height: 6),
        TextField(
          controller: controller,
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          style: const TextStyle(color: Colors.white, fontSize: 13),
          decoration: InputDecoration(
            hintText: hint,
            hintStyle: const TextStyle(color: Colors.white24),
            filled: true,
            fillColor: const Color(0xFF1E222B),
            border: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
              borderSide: BorderSide.none,
            ),
            contentPadding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
          ),
        ),
      ],
    );
  }
}
