import 'package:flutter/material.dart';
import '../services/pump_connection_service.dart';

class SettingsPage extends StatefulWidget {
  final PumpConnectionService connectionService;

  const SettingsPage({Key? key, required this.connectionService}) : super(key: key);

  @override
  _SettingsPageState createState() => _SettingsPageState();
}

class _SettingsPageState extends State<SettingsPage> {
  final TextEditingController kpController = TextEditingController(text: '0.5');
  final TextEditingController kiController = TextEditingController(text: '0.05');
  final TextEditingController kdController = TextEditingController(text: '0.001');

  bool disablePot = false;
  bool sensorEnable = false;
  bool sensorBypass = false;
  bool sensorButtonOverride = true;

  @override
  void dispose() {
    kpController.dispose();
    kiController.dispose();
    kdController.dispose();
    super.dispose();
  }

  void _sendPID() {
    final double? kp = double.tryParse(kpController.text);
    final double? ki = double.tryParse(kiController.text);
    final double? kd = double.tryParse(kdController.text);

    if (kp == null || ki == null || kd == null) {
      _showSnackBar('Please enter valid numeric values for PID constants.', Colors.redAccent);
      return;
    }

    widget.connectionService.sendCommand({
      'pid_kp': kp,
      'pid_ki': ki,
      'pid_kd': kd,
    });
    _showSnackBar('PID parameters uploaded!', Colors.greenAccent);
  }

  void _sendToggleSetting(String key, bool value) {
    widget.connectionService.sendCommand({
      key: value ? 1.0 : 0.0,
    });
    _showSnackBar('$key updated!', Colors.greenAccent);
  }

  void _sendSystemCommand(String cmd) {
    widget.connectionService.sendCommand({'command': cmd});
    _showSnackBar('System command "$cmd" sent.', Colors.blueAccent);
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
    return Scaffold(
      backgroundColor: const Color(0xFF1E222B),
      body: SingleChildScrollView(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // --- PID Configuration Card ---
            _buildPIDCard(),
            const SizedBox(height: 16),

            // --- Hardware Options Card ---
            _buildHardwareCard(),
            const SizedBox(height: 16),

            // --- Utility Controls Card ---
            _buildUtilityCard(),
          ],
        ),
      ),
    );
  }

  Widget _buildPIDCard() {
    return Card(
      color: const Color(0xFF242A38),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: const [
                Icon(Icons.tune, color: Colors.greenAccent),
                SizedBox(width: 10),
                Text(
                  'PID Tuning Parameters',
                  style: TextStyle(color: Colors.white, fontSize: 14, fontWeight: FontWeight.bold),
                ),
              ],
            ),
            const SizedBox(height: 16),
            Row(
              children: [
                Expanded(
                  child: _buildTextField(
                    controller: kpController,
                    label: 'Proportional (Kp)',
                    hint: '0.5',
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: _buildTextField(
                    controller: kiController,
                    label: 'Integral (Ki)',
                    hint: '0.05',
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: _buildTextField(
                    controller: kdController,
                    label: 'Derivative (Kd)',
                    hint: '0.001',
                  ),
                ),
              ],
            ),
            const SizedBox(height: 16),
            ElevatedButton(
              onPressed: _sendPID,
              style: ElevatedButton.styleFrom(
                backgroundColor: const Color(0xFF2E3B4E),
                foregroundColor: Colors.white,
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
              ),
              child: const Text('Update PID Values', style: TextStyle(fontWeight: FontWeight.bold)),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildHardwareCard() {
    return Card(
      color: const Color(0xFF242A38),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: const [
                Icon(Icons.developer_board, color: Colors.greenAccent),
                SizedBox(width: 10),
                Text(
                  'Hardware Gating Settings',
                  style: TextStyle(color: Colors.white, fontSize: 14, fontWeight: FontWeight.bold),
                ),
              ],
            ),
            const SizedBox(height: 10),
            SwitchListTile(
              title: const Text('Disable Potentiometer', style: TextStyle(color: Colors.white, fontSize: 13)),
              subtitle: const Text('Direct command overrides physical knob', style: TextStyle(color: Colors.white30, fontSize: 11)),
              value: disablePot,
              onChanged: (val) {
                setState(() => disablePot = val);
                _sendToggleSetting('disablePot', val);
              },
              activeColor: Colors.greenAccent,
            ),
            const Divider(color: Colors.white10),
            SwitchListTile(
              title: const Text('Enable Wet/Dry Sensor', style: TextStyle(color: Colors.white, fontSize: 13)),
              subtitle: const Text('Stops the pump if dry state detected', style: TextStyle(color: Colors.white30, fontSize: 11)),
              value: sensorEnable,
              onChanged: (val) {
                setState(() => sensorEnable = val);
                _sendToggleSetting('sensorEnable', val);
              },
              activeColor: Colors.greenAccent,
            ),
            const Divider(color: Colors.white10),
            SwitchListTile(
              title: const Text('Sensor Bypass', style: TextStyle(color: Colors.white, fontSize: 13)),
              subtitle: const Text('Ignore sensor checks during operation', style: TextStyle(color: Colors.white30, fontSize: 11)),
              value: sensorBypass,
              onChanged: (val) {
                setState(() => sensorBypass = val);
                _sendToggleSetting('sensorBypass', val);
              },
              activeColor: Colors.greenAccent,
            ),
            const Divider(color: Colors.white10),
            SwitchListTile(
              title: const Text('Sensor Button Override', style: TextStyle(color: Colors.white, fontSize: 13)),
              subtitle: const Text('Forces physical hardware button state', style: TextStyle(color: Colors.white30, fontSize: 11)),
              value: sensorButtonOverride,
              onChanged: (val) {
                setState(() => sensorButtonOverride = val);
                _sendToggleSetting('sensorButtonOverride', val);
              },
              activeColor: Colors.greenAccent,
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildUtilityCard() {
    return Card(
      color: const Color(0xFF242A38),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: const [
                Icon(Icons.settings_suggest, color: Colors.greenAccent),
                SizedBox(width: 10),
                Text(
                  'Hardware Utilities',
                  style: TextStyle(color: Colors.white, fontSize: 14, fontWeight: FontWeight.bold),
                ),
              ],
            ),
            const SizedBox(height: 16),
            Wrap(
              spacing: 12,
              runSpacing: 12,
              alignment: WrapAlignment.center,
              children: [
                _buildUtilButton(
                  title: 'Save Config',
                  icon: Icons.save,
                  color: Colors.greenAccent,
                  onTap: () => _sendSystemCommand('save_config'),
                ),
                _buildUtilButton(
                  title: 'Load Config',
                  icon: Icons.upload_file,
                  color: Colors.cyanAccent,
                  onTap: () => _sendSystemCommand('load_config'),
                ),
                _buildUtilButton(
                  title: 'Print Config',
                  icon: Icons.print,
                  color: Colors.blueAccent,
                  onTap: () => _sendSystemCommand('print_config'),
                ),
                _buildUtilButton(
                  title: 'Clear NVS (Reset)',
                  icon: Icons.delete_forever,
                  color: Colors.redAccent,
                  onTap: () => _sendSystemCommand('clear_nvs'),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildUtilButton({
    required String title,
    required IconData icon,
    required Color color,
    required VoidCallback onTap,
  }) {
    return SizedBox(
      width: 130,
      height: 70,
      child: ElevatedButton(
        onPressed: onTap,
        style: ElevatedButton.styleFrom(
          backgroundColor: const Color(0xFF1E222B),
          foregroundColor: color,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(10),
            side: BorderSide(color: color.withOpacity(0.3), width: 1),
          ),
        ),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(icon, size: 20, color: color),
            const SizedBox(height: 4),
            Text(
              title,
              textAlign: TextAlign.center,
              style: const TextStyle(fontSize: 10, fontWeight: FontWeight.bold, color: Colors.white),
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
