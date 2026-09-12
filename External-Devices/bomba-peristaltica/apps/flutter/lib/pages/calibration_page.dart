import 'dart:async';
import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:shared_preferences/shared_preferences.dart';
import '../services/pump_connection_service.dart';

class CalibrationPage extends StatefulWidget {
  final PumpConnectionService connectionService;

  const CalibrationPage({Key? key, required this.connectionService}) : super(key: key);

  @override
  _CalibrationPageState createState() => _CalibrationPageState();
}

class _CalibrationPageState extends State<CalibrationPage> {
  final TextEditingController initialSpeedController = TextEditingController(text: '50');
  final TextEditingController lastSpeedController = TextEditingController(text: '1000');
  final TextEditingController numDataPointsController = TextEditingController(text: '5');
  
  List<double> stepsPerSecondList = [];
  List<TextEditingController> volumeControllers = [];
  List<TextEditingController> timeControllers = [];
  double? slope;
  double? intercept;

  List<String> profiles = [];
  String? selectedProfile;
  int? runningIndex;
  Timer? runTimer;

  @override
  void initState() {
    super.initState();
    _loadProfiles();
  }

  @override
  void dispose() {
    runTimer?.cancel();
    initialSpeedController.dispose();
    lastSpeedController.dispose();
    numDataPointsController.dispose();
    for (var controller in volumeControllers) {
      controller.dispose();
    }
    for (var controller in timeControllers) {
      controller.dispose();
    }
    super.dispose();
  }

  Future<void> _loadProfiles() async {
    SharedPreferences prefs = await SharedPreferences.getInstance();
    setState(() {
      profiles = prefs.getStringList('profiles') ?? [];
      if (profiles.isNotEmpty) {
        selectedProfile = profiles[0];
        _loadProfileData(selectedProfile!);
      }
    });
  }

  Future<void> _saveProfiles() async {
    SharedPreferences prefs = await SharedPreferences.getInstance();
    await prefs.setStringList('profiles', profiles);
  }

  Future<void> _loadProfileData(String profileName) async {
    SharedPreferences prefs = await SharedPreferences.getInstance();
    String? profileDataString = prefs.getString('profile_$profileName');
    if (profileDataString != null) {
      Map<String, dynamic> profileData = jsonDecode(profileDataString);

      setState(() {
        initialSpeedController.text = profileData['initialSpeed']?.toString() ?? '50';
        lastSpeedController.text = profileData['lastSpeed']?.toString() ?? '1000';
        numDataPointsController.text = profileData['numDataPoints']?.toString() ?? '5';

        stepsPerSecondList = List<double>.from(profileData['stepsPerSecondList'] ?? []);
        volumeControllers = [];
        timeControllers = [];

        List<String> volumeValues = List<String>.from(profileData['volumeValues'] ?? []);
        List<String> timeValues = List<String>.from(profileData['timeValues'] ?? []);

        for (int i = 0; i < stepsPerSecondList.length; i++) {
          volumeControllers.add(TextEditingController(
              text: i < volumeValues.length ? volumeValues[i] : ''));
          timeControllers.add(TextEditingController(
              text: i < timeValues.length ? timeValues[i] : '30')); // Default test time: 30s
        }

        slope = profileData['slope'];
        intercept = profileData['intercept'];
      });
    } else {
      setState(() {
        initialSpeedController.text = '50';
        lastSpeedController.text = '1000';
        numDataPointsController.text = '5';
        stepsPerSecondList = [];
        volumeControllers = [];
        timeControllers = [];
        slope = null;
        intercept = null;
      });
    }
  }

  Future<void> _saveProfileData() async {
    SharedPreferences prefs = await SharedPreferences.getInstance();
    if (selectedProfile == null) return;

    List<String> volumeValues =
        volumeControllers.map((controller) => controller.text).toList();
    List<String> timeValues =
        timeControllers.map((controller) => controller.text).toList();

    Map<String, dynamic> profileData = {
      'initialSpeed': double.tryParse(initialSpeedController.text) ?? 50,
      'lastSpeed': double.tryParse(lastSpeedController.text) ?? 1000,
      'numDataPoints': int.tryParse(numDataPointsController.text) ?? 5,
      'stepsPerSecondList': stepsPerSecondList,
      'volumeValues': volumeValues,
      'timeValues': timeValues,
      'slope': slope,
      'intercept': intercept,
    };

    await prefs.setString('profile_$selectedProfile', jsonEncode(profileData));
  }

  void _calculateStepsPerSecondList() {
    double initialSpeed = double.tryParse(initialSpeedController.text) ?? 50;
    double lastSpeed = double.tryParse(lastSpeedController.text) ?? 1000;
    int numDataPoints = int.tryParse(numDataPointsController.text) ?? 5;

    setState(() {
      stepsPerSecondList = [];
      volumeControllers = [];
      timeControllers = [];
      if (numDataPoints < 2) numDataPoints = 2;

      double step = (lastSpeed - initialSpeed) / (numDataPoints - 1);
      for (int i = 0; i < numDataPoints; i++) {
        double speed = initialSpeed + step * i;
        stepsPerSecondList.add(speed);
        volumeControllers.add(TextEditingController());
        timeControllers.add(TextEditingController(text: '30'));
      }
    });

    _saveProfileData();
  }

  void _calculateCalibrationParameters() {
    List<double> xData = []; // Speed (steps/s)
    List<double> yData = []; // Flow rates (mL/min)

    for (int i = 0; i < volumeControllers.length; i++) {
      double? volume = double.tryParse(volumeControllers[i].text);
      double? time = double.tryParse(timeControllers[i].text);

      if (volume != null && time != null && time > 0) {
        // Flow rate (mL/min) = (Volume in mL / Time in seconds) * 60
        double flowRate = (volume / time) * 60.0;
        xData.add(stepsPerSecondList[i]);
        yData.add(flowRate);
      }
    }

    if (xData.length >= 2) {
      double n = xData.length.toDouble();
      double sumX = xData.reduce((a, b) => a + b);
      double sumY = yData.reduce((a, b) => a + b);
      double sumXY = 0;
      double sumX2 = 0;

      for (int i = 0; i < xData.length; i++) {
        sumXY += xData[i] * yData[i];
        sumX2 += xData[i] * xData[i];
      }

      double denominator = (n * sumX2) - (sumX * sumX);
      if (denominator != 0) {
        // Fit: Y (FlowRate) = Slope * X (Speed) + Intercept
        double calculatedSlope = ((n * sumXY) - (sumX * sumY)) / denominator;
        double calculatedIntercept = ((sumY * sumX2) - (sumX * sumXY)) / denominator;

        setState(() {
          slope = calculatedSlope;
          intercept = calculatedIntercept;
        });

        _saveProfileData();
        _showSnackBar('Calibration parameters calculated successfully!', Colors.greenAccent);
      } else {
        _showSnackBar('Error: Linear fit denominator is zero.', Colors.redAccent);
      }
    } else {
      _showSnackBar('At least 2 valid measurements are required.', Colors.redAccent);
    }
  }

  void _sendCalibrationToPump() {
    if (slope == null || intercept == null) {
      _showSnackBar('Perform calibration calculations first.', Colors.redAccent);
      return;
    }

    widget.connectionService.sendCommand({
      'pumpSlope': slope,
      'pumpIntercept': intercept,
      'command': 'save_config',
    });
    _showSnackBar('Calibration coefficients sent and saved on Pump!', Colors.greenAccent);
  }

  Future<void> _createNewProfile() async {
    TextEditingController nameController = TextEditingController();
    await showDialog(
      context: context,
      builder: (context) => AlertDialog(
        backgroundColor: const Color(0xFF242A38),
        title: const Text('Create Calibration Profile', style: TextStyle(color: Colors.white)),
        content: TextField(
          controller: nameController,
          style: const TextStyle(color: Colors.white),
          decoration: const InputDecoration(
            labelText: 'Profile Name',
            labelStyle: TextStyle(color: Colors.white54),
            enabledBorder: UnderlineInputBorder(borderSide: BorderSide(color: Colors.white24)),
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Cancel', style: TextStyle(color: Colors.white54)),
          ),
          ElevatedButton(
            onPressed: () async {
              String name = nameController.text.trim();
              if (name.isNotEmpty && !profiles.contains(name)) {
                setState(() {
                  profiles.add(name);
                  selectedProfile = name;
                  initialSpeedController.text = '50';
                  lastSpeedController.text = '1000';
                  numDataPointsController.text = '5';
                  stepsPerSecondList = [];
                  volumeControllers = [];
                  timeControllers = [];
                  slope = null;
                  intercept = null;
                });
                await _saveProfiles();
                _saveProfileData();
                Navigator.of(context).pop();
              }
            },
            child: const Text('Create'),
          ),
        ],
      ),
    );
  }

  Future<void> _deleteProfile() async {
    if (selectedProfile == null) return;
    SharedPreferences prefs = await SharedPreferences.getInstance();
    await prefs.remove('profile_$selectedProfile');
    setState(() {
      profiles.remove(selectedProfile);
      selectedProfile = profiles.isNotEmpty ? profiles[0] : null;
    });
    await _saveProfiles();
    if (selectedProfile != null) {
      _loadProfileData(selectedProfile!);
    }
  }

  void _runCalibrationPoint(int index) {
    if (runningIndex != null) {
      _showSnackBar('A calibration point is already running.', Colors.amberAccent);
      return;
    }

    final double speed = stepsPerSecondList[index];
    final double? duration = double.tryParse(timeControllers[index].text);

    if (duration == null || duration <= 0) {
      _showSnackBar('Enter a valid test duration.', Colors.redAccent);
      return;
    }

    setState(() {
      runningIndex = index;
    });

    // 1. Send run speed command to ESP32 (direct speed mode)
    widget.connectionService.sendCommand({
      'speed': speed,
      'disablePot': 1.0, // Override knob
    });

    // 2. Set timer to automatically stop the pump
    runTimer = Timer(Duration(seconds: duration.toInt()), () {
      widget.connectionService.sendCommand({
        'speed': 0.0,
        'disablePot': 0.0, // Release override
      });
      if (mounted) {
        setState(() {
          runningIndex = null;
        });
        _showSnackBar('Test finished. Measure and enter volume.', Colors.greenAccent);
      }
    });
  }

  void _showSnackBar(String msg, Color color) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(msg, style: const TextStyle(fontWeight: FontWeight.bold)),
        backgroundColor: color.withOpacity(0.9),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: const Color(0xFF1E222B),
      body: Column(
        children: [
          // Profile Manager Header Bar
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
            color: const Color(0xFF161A22),
            child: Row(
              children: [
                Expanded(
                  child: DropdownButtonHideUnderline(
                    child: DropdownButton<String>(
                      value: selectedProfile,
                      hint: const Text('Select Profile', style: TextStyle(color: Colors.white54)),
                      dropdownColor: const Color(0xFF161A22),
                      style: const TextStyle(color: Colors.white),
                      items: profiles.map((p) {
                        return DropdownMenuItem<String>(
                          value: p,
                          child: Text(p),
                        );
                      }).toList(),
                      onChanged: (val) {
                        if (val != null) {
                          setState(() => selectedProfile = val);
                          _loadProfileData(val);
                        }
                      },
                    ),
                  ),
                ),
                IconButton(
                  icon: const Icon(Icons.add_circle_outline, color: Colors.greenAccent),
                  onPressed: _createNewProfile,
                ),
                IconButton(
                  icon: const Icon(Icons.delete_outline, color: Colors.redAccent),
                  onPressed: _deleteProfile,
                ),
              ],
            ),
          ),
          Expanded(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(16),
              child: selectedProfile == null
                  ? const Center(
                      child: Padding(
                        padding: EdgeInsets.only(top: 40.0),
                        child: Text(
                          'Create or select a calibration profile to begin.',
                          style: TextStyle(color: Colors.white38),
                        ),
                      ),
                    )
                  : Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        // Configuration Card
                        Card(
                          color: const Color(0xFF242A38),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
                          child: Padding(
                            padding: const EdgeInsets.all(16),
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.stretch,
                              children: [
                                const Text(
                                  'Generate Speed Steps Table',
                                  style: TextStyle(color: Colors.white, fontWeight: FontWeight.bold, fontSize: 14),
                                ),
                                const SizedBox(height: 12),
                                Row(
                                  children: [
                                    Expanded(
                                      child: _buildTextField(
                                        controller: initialSpeedController,
                                        label: 'Start Speed [steps/s]',
                                        hint: '50',
                                      ),
                                    ),
                                    const SizedBox(width: 10),
                                    Expanded(
                                      child: _buildTextField(
                                        controller: lastSpeedController,
                                        label: 'End Speed [steps/s]',
                                        hint: '1000',
                                      ),
                                    ),
                                    const SizedBox(width: 10),
                                    Expanded(
                                      child: _buildTextField(
                                        controller: numDataPointsController,
                                        label: 'Points',
                                        hint: '5',
                                      ),
                                    ),
                                  ],
                                ),
                                const SizedBox(height: 16),
                                ElevatedButton(
                                  onPressed: _calculateStepsPerSecondList,
                                  style: ElevatedButton.styleFrom(
                                    backgroundColor: const Color(0xFF2E3B4E),
                                    foregroundColor: Colors.white,
                                    shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                                  ),
                                  child: const Text('Generate Table', style: TextStyle(fontWeight: FontWeight.bold)),
                                ),
                              ],
                            ),
                          ),
                        ),
                        const SizedBox(height: 16),

                        // Calibration point steps table
                        if (stepsPerSecondList.isNotEmpty) ...[
                          Card(
                            color: const Color(0xFF242A38),
                            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
                            child: Padding(
                              padding: const EdgeInsets.all(12),
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.stretch,
                                children: [
                                  const Text(
                                    'Calibration Readings',
                                    style: TextStyle(color: Colors.white, fontWeight: FontWeight.bold, fontSize: 14),
                                  ),
                                  const SizedBox(height: 10),
                                  SingleChildScrollView(
                                    scrollDirection: Axis.horizontal,
                                    child: DataTable(
                                      columnSpacing: 16.0,
                                      columns: const [
                                        DataColumn(label: Text('Speed (X)\n[steps/s]', style: TextStyle(color: Colors.white70, fontSize: 11))),
                                        DataColumn(label: Text('Time\n[s]', style: TextStyle(color: Colors.white70, fontSize: 11))),
                                        DataColumn(label: Text('Vol\n[mL]', style: TextStyle(color: Colors.white70, fontSize: 11))),
                                        DataColumn(label: Text('Action', style: TextStyle(color: Colors.white70, fontSize: 11))),
                                      ],
                                      rows: List<DataRow>.generate(
                                        stepsPerSecondList.length,
                                        (index) {
                                          final isRunning = runningIndex == index;
                                          return DataRow(
                                            cells: [
                                              DataCell(Text(
                                                stepsPerSecondList[index].toStringAsFixed(1),
                                                style: const TextStyle(color: Colors.white, fontSize: 13),
                                              )),
                                              DataCell(SizedBox(
                                                width: 50,
                                                child: TextField(
                                                  controller: timeControllers[index],
                                                  keyboardType: TextInputType.number,
                                                  style: const TextStyle(color: Colors.white, fontSize: 13),
                                                  decoration: const InputDecoration(contentPadding: EdgeInsets.symmetric(vertical: 4)),
                                                  onChanged: (_) => _saveProfileData(),
                                                ),
                                              )),
                                              DataCell(SizedBox(
                                                width: 60,
                                                child: TextField(
                                                  controller: volumeControllers[index],
                                                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                                                  style: const TextStyle(color: Colors.white, fontSize: 13),
                                                  decoration: const InputDecoration(
                                                    hintText: '0.0',
                                                    hintStyle: TextStyle(color: Colors.white24),
                                                    contentPadding: EdgeInsets.symmetric(vertical: 4),
                                                  ),
                                                  onChanged: (_) => _saveProfileData(),
                                                ),
                                              )),
                                              DataCell(
                                                IconButton(
                                                  icon: Icon(
                                                    isRunning ? Icons.hourglass_top : Icons.play_arrow,
                                                    color: isRunning ? Colors.amberAccent : Colors.greenAccent,
                                                  ),
                                                  onPressed: () => _runCalibrationPoint(index),
                                                ),
                                              ),
                                            ],
                                          );
                                        },
                                      ),
                                    ),
                                  ),
                                  const SizedBox(height: 16),
                                  ElevatedButton(
                                    onPressed: _calculateCalibrationParameters,
                                    style: ElevatedButton.styleFrom(
                                      backgroundColor: const Color(0xFF2E3B4E),
                                      foregroundColor: Colors.white,
                                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                                    ),
                                    child: const Text('Calculate Regression', style: TextStyle(fontWeight: FontWeight.bold)),
                                  ),
                                ],
                              ),
                            ),
                          ),
                          const SizedBox(height: 16),
                        ],

                        // Linear regression output and Sync to hardware
                        if (slope != null && intercept != null) ...[
                          Card(
                            color: const Color(0xFF242A38),
                            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
                            child: Padding(
                              padding: const EdgeInsets.all(16),
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.stretch,
                                children: [
                                  const Text(
                                    'Regression Results',
                                    style: TextStyle(color: Colors.white, fontWeight: FontWeight.bold, fontSize: 14),
                                  ),
                                  const SizedBox(height: 8),
                                  Text(
                                    'Equation: Flow (mL/min) = Speed × (${slope!.toStringAsFixed(6)}) + (${intercept!.toStringAsFixed(6)})',
                                    style: const TextStyle(color: Colors.greenAccent, fontSize: 12, fontFamily: 'monospace'),
                                  ),
                                  const SizedBox(height: 16),
                                  ElevatedButton.icon(
                                    onPressed: _sendCalibrationToPump,
                                    icon: const Icon(Icons.sync, size: 18),
                                    label: const Text('Send & Save on Pump', style: TextStyle(fontWeight: FontWeight.bold)),
                                    style: ElevatedButton.styleFrom(
                                      backgroundColor: Colors.greenAccent,
                                      foregroundColor: Colors.black,
                                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                                    ),
                                  ),
                                ],
                              ),
                            ),
                          ),
                        ],
                      ],
                    ),
                  ),
          ),
        ],
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
        Text(label, style: const TextStyle(color: Colors.white70, fontSize: 10)),
        const SizedBox(height: 4),
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
              borderRadius: BorderRadius.circular(6),
              borderSide: BorderSide.none,
            ),
            contentPadding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
          ),
        ),
      ],
    );
  }
}
