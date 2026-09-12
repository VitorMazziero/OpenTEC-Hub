import 'package:flutter/material.dart';
import '../../services/pump_connection_service.dart';

class PolynomialModePage extends StatefulWidget {
  final PumpConnectionService connectionService;

  const PolynomialModePage({Key? key, required this.connectionService}) : super(key: key);

  @override
  _PolynomialModePageState createState() => _PolynomialModePageState();
}

class _PolynomialModePageState extends State<PolynomialModePage> {
  final TextEditingController initTimeController = TextEditingController(text: '0.0');
  final TextEditingController finalTimeController = TextEditingController(text: '60.0');
  final TextEditingController coeffsController = TextEditingController(text: '1.0, 0.05, 0.001');

  List<double> parsedCoeffs = [1.0, 0.05, 0.001];

  @override
  void initState() {
    super.initState();
    coeffsController.addListener(_onCoeffsChanged);
  }

  @override
  void dispose() {
    coeffsController.removeListener(_onCoeffsChanged);
    initTimeController.dispose();
    finalTimeController.dispose();
    coeffsController.dispose();
    super.dispose();
  }

  void _onCoeffsChanged() {
    final text = coeffsController.text.trim();
    if (text.isEmpty) {
      setState(() => parsedCoeffs = []);
      return;
    }

    final parts = text.split(',');
    final List<double> list = [];
    for (var part in parts) {
      final val = double.tryParse(part.trim());
      if (val != null) {
        list.add(val);
      }
    }
    setState(() => parsedCoeffs = list);
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

    if (parsedCoeffs.isEmpty) {
      _showSnackBar('Please enter at least one coefficient.', Colors.redAccent);
      return;
    }

    if (parsedCoeffs.length > 21) {
      _showSnackBar('Maximum of 21 coefficients (p0 to p20) allowed.', Colors.redAccent);
      return;
    }

    // Build polynomial payload
    final Map<String, dynamic> payload = {
      'mode': 4,
      'init_t': initT,
      'final_t': finalT,
    };

    // Include p0..p20, filling unset coefficients with 0.0
    for (int i = 0; i < 21; i++) {
      if (i < parsedCoeffs.length) {
        payload['p$i'] = parsedCoeffs[i];
      } else {
        payload['p$i'] = 0.0;
      }
    }

    widget.connectionService.sendCommand(payload);
    _showSnackBar('Polynomial Mode profile uploaded!', Colors.greenAccent);
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
            // --- Telemetry Dashboard ---
            _buildTelemetryDashboard(telemetry),
            const SizedBox(height: 16),

            // --- Action Buttons ---
            _buildGlobalActionPanel(),
            const SizedBox(height: 24),

            // --- Form Card ---
            _buildPolynomialForm(),
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

  Widget _buildPolynomialForm() {
    return Card(
      color: const Color(0xFF242A38),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const Text(
              'Polynomial Flow Profile',
              style: TextStyle(color: Colors.white, fontSize: 14, fontWeight: FontWeight.bold),
            ),
            const SizedBox(height: 4),
            const Text(
              'Q(t\') = p0 + p1·t\' + p2·t\'² + ... + p20·t\'²⁰',
              style: TextStyle(color: Colors.white30, fontSize: 11),
            ),
            const SizedBox(height: 16),
            _buildTextField(
              controller: initTimeController,
              label: 'Initial Time [min] (t_init)',
              hint: '0.0',
            ),
            const SizedBox(height: 12),
            _buildTextField(
              controller: finalTimeController,
              label: 'Final Time [min] (t_final)',
              hint: '60.0',
            ),
            const SizedBox(height: 12),
            _buildTextField(
              controller: coeffsController,
              label: 'Coefficients p0..p20 (comma separated)',
              hint: '1.0, 0.05, 0.001',
            ),
            const SizedBox(height: 16),
            const Text(
              'Parsed Coefficients Preview:',
              style: TextStyle(color: Colors.white70, fontSize: 12, fontWeight: FontWeight.bold),
            ),
            const SizedBox(height: 6),
            Container(
              padding: const EdgeInsets.all(10),
              height: 110,
              decoration: BoxDecoration(
                color: const Color(0xFF1E222B),
                borderRadius: BorderRadius.circular(8),
              ),
              child: parsedCoeffs.isEmpty
                  ? const Center(
                      child: Text('No coefficients parsed.', style: TextStyle(color: Colors.white24, fontSize: 12)),
                    )
                  : ListView.builder(
                      itemCount: parsedCoeffs.length,
                      itemBuilder: (context, index) {
                        return Padding(
                          padding: const EdgeInsets.symmetric(vertical: 2.0),
                          child: Text(
                            'p$index = ${parsedCoeffs[index]}',
                            style: const TextStyle(color: Colors.greenAccent, fontSize: 12, fontFamily: 'monospace'),
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
              child: const Text('Upload & Load Profile', style: TextStyle(fontWeight: FontWeight.bold)),
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
        Text(label, style: const TextStyle(color: Colors.white70, fontSize: 12)),
        const SizedBox(height: 6),
        TextField(
          controller: controller,
          style: const TextStyle(color: Colors.white, fontSize: 14),
          decoration: InputDecoration(
            hintText: hint,
            hintStyle: const TextStyle(color: Colors.white24),
            filled: true,
            fillColor: const Color(0xFF1E222B),
            border: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
              borderSide: BorderSide.none,
            ),
            contentPadding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          ),
        ),
      ],
    );
  }
}
