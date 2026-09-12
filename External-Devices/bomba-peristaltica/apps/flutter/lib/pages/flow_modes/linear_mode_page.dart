import 'package:flutter/material.dart';
import '../../services/pump_connection_service.dart';

class LinearModePage extends StatefulWidget {
  final PumpConnectionService connectionService;

  const LinearModePage({Key? key, required this.connectionService}) : super(key: key);

  @override
  _LinearModePageState createState() => _LinearModePageState();
}

class _LinearModePageState extends State<LinearModePage> {
  final TextEditingController initTimeController = TextEditingController(text: '0.0');
  final TextEditingController finalTimeController = TextEditingController(text: '60.0');
  final TextEditingController lambdaController = TextEditingController(text: '5.0');
  final TextEditingController phiController = TextEditingController(text: '0.25');

  @override
  void dispose() {
    initTimeController.dispose();
    finalTimeController.dispose();
    lambdaController.dispose();
    phiController.dispose();
    super.dispose();
  }

  void _sendProfile() {
    final double? initT = double.tryParse(initTimeController.text);
    final double? finalT = double.tryParse(finalTimeController.text);
    final double? lambda = double.tryParse(lambdaController.text);
    final double? phi = double.tryParse(phiController.text);

    if (initT == null || finalT == null || lambda == null || phi == null) {
      _showSnackBar('Please enter valid numeric values.', Colors.redAccent);
      return;
    }

    if (finalT <= initT && finalT > 0) {
      _showSnackBar('Final time must be greater than start time.', Colors.redAccent);
      return;
    }

    widget.connectionService.sendCommand({
      'mode': 2,
      'init_t': initT,
      'final_t': finalT,
      'lambda_linear': lambda,
      'phi_linear': phi,
    });
    _showSnackBar('Linear Mode profile uploaded!', Colors.greenAccent);
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
            // --- Telemetry Monitor Card ---
            _buildTelemetryDashboard(telemetry),
            const SizedBox(height: 16),

            // --- Global Action Buttons ---
            _buildGlobalActionPanel(),
            const SizedBox(height: 24),

            // --- Form Inputs Card ---
            _buildLinearForm(),
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

  Widget _buildLinearForm() {
    return Card(
      color: const Color(0xFF242A38),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const Text(
              'Linear Flow Profile: Q(t\') = λ + φ·t\'',
              style: TextStyle(color: Colors.white, fontSize: 14, fontWeight: FontWeight.bold),
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
              controller: lambdaController,
              label: 'Initial Flow Rate λ [mL/min]',
              hint: '5.0',
            ),
            const SizedBox(height: 12),
            _buildTextField(
              controller: phiController,
              label: 'Flow Acceleration Coefficient φ [mL/min²]',
              hint: '0.25',
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
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
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
