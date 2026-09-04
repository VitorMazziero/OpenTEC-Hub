import 'dart:async';
import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:flutter_blue_plus/flutter_blue_plus.dart';
import 'package:fl_chart/fl_chart.dart';
import 'package:http/http.dart' as http;
import 'package:permission_handler/permission_handler.dart';
import 'package:provider/provider.dart';

// Enum for selecting communication type
enum CommunicationType { BLE, Wifi }

/// # Communication Service
class CommunicationService with ChangeNotifier {
  CommunicationType _currentType = CommunicationType.BLE;
  String _wifiIP = "192.168.4.1";
  bool _isConnecting = false;

  // BLE-specific objects
  BluetoothDevice? _bleDevice;
  BluetoothCharacteristic? _bleWriteCharacteristic;
  StreamSubscription<List<int>>? _bleNotificationSubscription;
  Timer? _bleKeepAliveTimer;

  // Wi-Fi specific objects
  Timer? _wifiDataPoller;

  // Generic state
  bool _isConnected = false;
  String _statusMessage = "Disconnected";
  final StreamController<String> _messageController = StreamController.broadcast();

  // Public accessors
  bool get isConnecting => _isConnecting;
  bool get isConnected => _isConnected;
  String get statusMessage => _statusMessage;
  CommunicationType get currentType => _currentType;
  Stream<String> get messages => _messageController.stream;

  void setCommunicationType(CommunicationType type) {
    if (_currentType == type) return;
    disconnect();
    _currentType = type;
    notifyListeners();
  }

  void setWifiIP(String ip) {
    _wifiIP = ip;
  }

  Future<bool> _requestPermissions() async {
    Map<Permission, PermissionStatus> statuses = await [
      Permission.bluetoothScan,
      Permission.bluetoothConnect,
    ].request();

    if (statuses[Permission.bluetoothScan]!.isGranted &&
        statuses[Permission.bluetoothConnect]!.isGranted) {
      return true;
    } else {
      _updateStatus("Bluetooth permissions denied.");
      return false;
    }
  }

  Future<void> connect() async {
    if (_isConnected || _isConnecting) return;

    _isConnecting = true;
    notifyListeners();

    if (_currentType == CommunicationType.BLE) {
      await _connectBLE();
    } else {
      await _connectWifi();
    }

    _isConnecting = false;
    notifyListeners();
  }

  Future<void> _connectBLE() async {
    if (!await _requestPermissions()) return;

    _updateStatus("Scanning for BLE devices...");
    try {
      await FlutterBluePlus.startScan(
        withServices: [Guid("6E400001-B5A3-F393-E0A9-E50E24DCCA9E")],
        timeout: const Duration(seconds: 5),
      );

      await for (final results in FlutterBluePlus.scanResults) {
        for (final r in results) {
          if (r.device.platformName == "ModuloTECNAL_1" || r.device.platformName == "ModuloTECNAL_2") {
            _bleDevice = r.device;
            break;
          }
        }
        if (_bleDevice != null) break;
      }
    } catch (e) {
      _updateStatus("BLE Scan Error: $e");
    } finally {
      FlutterBluePlus.stopScan();
    }

    if (_bleDevice == null) {
      _updateStatus("Device 'ModuloTECNAL' not found.");
      return;
    }

    try {
      _updateStatus("Connecting to ${_bleDevice!.platformName}...");
      await _bleDevice!.connect(timeout: const Duration(seconds: 10));
      _updateStatus("Discovering services...");
      List<BluetoothService> services = await _bleDevice!.discoverServices();
      for (var service in services) {
        if (service.uuid.toString().toUpperCase() == "6E400001-B5A3-F393-E0A9-E50E24DCCA9E") {
          for (var c in service.characteristics) {
            if (c.uuid.toString().toUpperCase() == "6E400002-B5A3-F393-E0A9-E50E24DCCA9E") {
              _bleWriteCharacteristic = c;
            }
            if (c.uuid.toString().toUpperCase() == "6E400003-B5A3-F393-E0A9-E50E24DCCA9E") {
              await c.setNotifyValue(true);
              _bleNotificationSubscription = c.onValueReceived.listen((value) {
                _messageController.add(utf8.decode(value, allowMalformed: true));
              });
            }
          }
        }
      }
      if (_bleWriteCharacteristic != null) {
        _isConnected = true;
        _startBleKeepAlive();
        _updateStatus("BLE Connected to ${_bleDevice!.platformName}");
      } else {
        _updateStatus("UART service not found.");
        await _bleDevice?.disconnect();
      }
    } catch (e) {
      _updateStatus("BLE Connection Failed: $e");
      _isConnected = false;
    }
  }

  Future<void> _connectWifi() async {
    _updateStatus("Connecting via Wi-Fi to $_wifiIP...");
    try {
      final response = await http.get(Uri.parse('http://$_wifiIP/ping')).timeout(const Duration(seconds: 5));
      if (response.statusCode == 200 && response.body == "pong") {
        _isConnected = true;
        _startWifiPolling();
        _updateStatus("Wi-Fi Connected to $_wifiIP");
      } else {
        _updateStatus("Wi-Fi check failed. Status: ${response.statusCode}");
      }
    } catch (e) {
      _updateStatus("Wi-Fi connection error: $e");
    }
  }

  void _startBleKeepAlive() {
    _bleKeepAliveTimer?.cancel();
    _bleKeepAliveTimer = Timer.periodic(const Duration(seconds: 4), (timer) {
      if (_isConnected && _currentType == CommunicationType.BLE) {
        sendCommand('{"keepAlive":1}');
      } else {
        timer.cancel();
      }
    });
  }

  void _startWifiPolling() {
    _wifiDataPoller?.cancel();
    _wifiDataPoller = Timer.periodic(const Duration(seconds: 1), (timer) async {
      if (_isConnected && _currentType == CommunicationType.Wifi) {
        try {
          final response = await http.get(Uri.parse('http://$_wifiIP/readData'));
          if (response.statusCode == 200) {
            _messageController.add(response.body);
          }
        } catch (e) {
          // silent fail
        }
      } else {
        timer.cancel();
      }
    });
  }

  Future<void> disconnect() async {
    _bleKeepAliveTimer?.cancel();
    _wifiDataPoller?.cancel();
    _bleNotificationSubscription?.cancel();
    if (_currentType == CommunicationType.BLE && _bleDevice != null) {
      await _bleDevice!.disconnect();
    }
    _bleDevice = null;
    _bleWriteCharacteristic = null;
    _isConnected = false;
    _updateStatus("Disconnected");
  }

  Future<void> sendCommand(String command) async {
    if (!_isConnected) return;
    try {
      if (_currentType == CommunicationType.BLE) {
        if (_bleWriteCharacteristic != null) {
          await _bleWriteCharacteristic!.write(utf8.encode(command));
        }
      } else {
        await http.post(
          Uri.parse('http://$_wifiIP/command'),
          headers: {'Content-Type': 'text/plain'},
          body: command,
        );
      }
    } catch (e) {
      _updateStatus("Send Error: $e");
    }
  }

  void _updateStatus(String message) {
    _statusMessage = message;
    notifyListeners();
  }
}

class SensorData {
  final double time;
  final double temp;
  final double pH;
  final double oxygen;
  final double antifoam;
  final double pressure;
  final double flowRate;
  final double distance;
  final bool sensorCommOK;

  SensorData({
    required this.time,
    required this.temp,
    required this.pH,
    required this.oxygen,
    required this.antifoam,
    required this.pressure,
    required this.flowRate,
    required this.distance,
    required this.sensorCommOK,
  });

  factory SensorData.fromJson(Map<String, dynamic> json) {
    return SensorData(
      time: (json['Time'] ?? 0.0).toDouble(),
      temp: (json['Tempval'] ?? 0.0).toDouble(),
      pH: (json['pHval'] ?? 0.0).toDouble(),
      oxygen: (json['Oxyval'] ?? 0.0).toDouble(),
      antifoam: (json['Antifoam'] ?? 0.0).toDouble(),
      pressure: (json['Pressure'] ?? 0.0).toDouble(),
      flowRate: (json['FlowRate'] ?? 0.0).toDouble(),
      distance: (json['Distance'] ?? 0.0).toDouble(),
      sensorCommOK: json['SensorCommOK'] ?? false,
    );
  }
}

class DataModel extends ChangeNotifier {
  final List<SensorData> _data = [];
  List<SensorData> get data => _data;

  void addData(SensorData sensorData) {
    _data.add(sensorData);
    if (_data.length > 200) _data.removeAt(0);
    notifyListeners();
  }

  void clear() {
    _data.clear();
    notifyListeners();
  }
}

void main() {
  runApp(
    MultiProvider(
      providers: [
        ChangeNotifierProvider(create: (_) => CommunicationService()),
        ChangeNotifierProvider(create: (_) => DataModel()),
      ],
      child: const MyApp(),
    ),
  );
}

class MyApp extends StatelessWidget {
  const MyApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'TECNAL Controller',
      theme: ThemeData(
        useMaterial3: true,
        colorScheme: ColorScheme.fromSeed(
          seedColor: Colors.blueAccent,
          brightness: Brightness.light,
        ),
      ),
      themeMode: ThemeMode.light, // Force light theme
      home: const MainScreen(),
    );
  }
}

class MainScreen extends StatefulWidget {
  const MainScreen({super.key});
  @override
  State<MainScreen> createState() => _MainScreenState();
}

class _MainScreenState extends State<MainScreen> {
  int _selectedIndex = 0;
  StreamSubscription? _messageSubscription;

  final List<Widget> _pages = [const ParameterSettingsPage(), const GraphPage()];

  @override
  void initState() {
    super.initState();
    _messageSubscription = Provider.of<CommunicationService>(context, listen: false)
        .messages.listen((message) {
      final startIndex = message.indexOf('{');
      final endIndex = message.lastIndexOf('}');
      if (startIndex != -1 && endIndex != -1 && endIndex > startIndex) {
        final jsonString = message.substring(startIndex, endIndex + 1);
        try {
          final jsonData = jsonDecode(jsonString);
          final sensorData = SensorData.fromJson(jsonData);
          Provider.of<DataModel>(context, listen: false).addData(sensorData);
        } catch (e) { /* ignore parse errors */ }
      }
    });
  }

  @override
  void dispose() {
    _messageSubscription?.cancel();
    Provider.of<CommunicationService>(context, listen: false).disconnect();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final commStatus = context.watch<CommunicationService>().statusMessage;
    return Scaffold(
      appBar: AppBar(
        leading: const Padding(
          padding: EdgeInsets.all(8.0),
          child: Icon(Icons.biotech_outlined),
        ),
        title: const Text('TECNAL Controller', style: TextStyle(fontWeight: FontWeight.bold)),
        bottom: PreferredSize(
          preferredSize: const Size.fromHeight(20.0),
          child: Padding(
            padding: const EdgeInsets.only(bottom: 4.0),
            child: Text(commStatus, style: Theme.of(context).textTheme.bodySmall),
          ),
        ),
        actions: [
          IconButton(
            icon: const Icon(Icons.link),
            tooltip: "Connection Settings",
            onPressed: () => showDialog(
              context: context,
              barrierDismissible: false, // User must interact with dialog
              builder: (_) => const ConnectionDialog(),
            ),
          ),
          IconButton(
            icon: const Icon(Icons.delete_sweep),
            tooltip: "Clear Graphs",
            onPressed: () => Provider.of<DataModel>(context, listen: false).clear(),
          ),
        ],
      ),
      body: IndexedStack(index: _selectedIndex, children: _pages),
      bottomNavigationBar: BottomNavigationBar(
        currentIndex: _selectedIndex,
        onTap: (index) => setState(() => _selectedIndex = index),
        items: const [
          BottomNavigationBarItem(icon: Icon(Icons.tune), label: 'Parameters'),
          BottomNavigationBarItem(icon: Icon(Icons.show_chart), label: 'Graphs'),
        ],
      ),
    );
  }
}

class ConnectionDialog extends StatefulWidget {
  const ConnectionDialog({super.key});
  @override
  State<ConnectionDialog> createState() => _ConnectionDialogState();
}

class _ConnectionDialogState extends State<ConnectionDialog> {
  late CommunicationType _selectedType;
  final _ipController = TextEditingController(text: "192.168.4.1");

  @override
  void initState() {
    super.initState();
    _selectedType = Provider.of<CommunicationService>(context, listen: false).currentType;
  }

  @override
  void dispose() {
    _ipController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final commService = Provider.of<CommunicationService>(context);

    return AlertDialog(
      title: const Text("Connection Settings"),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          DropdownButton<CommunicationType>(
            value: _selectedType,
            items: const [
              DropdownMenuItem(value: CommunicationType.BLE, child: Text("Bluetooth (BLE)")),
              DropdownMenuItem(value: CommunicationType.Wifi, child: Text("Wi-Fi")),
            ],
            onChanged: commService.isConnecting ? null : (value) {
              if (value != null) {
                setState(() => _selectedType = value);
                commService.setCommunicationType(value);
              }
            },
          ),
          if (_selectedType == CommunicationType.Wifi)
            TextField(
              controller: _ipController,
              decoration: const InputDecoration(labelText: "IP Address"),
              onChanged: (ip) => commService.setWifiIP(ip),
            ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: commService.isConnecting ? null : () => Navigator.of(context).pop(),
          child: const Text("Cancel"),
        ),
        FilledButton(
          onPressed: commService.isConnecting || commService.isConnected ? null : () async {
            await commService.connect();
            if (commService.isConnected && mounted) {
              Navigator.of(context).pop();
            }
          },
          child: commService.isConnecting
              ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2))
              : const Text("Connect"),
        ),
      ],
    );
  }
}

/// # UI Control Blocks
class ControlBlock extends StatelessWidget {
  final String title;
  final Color activationColor;
  final bool isEnabled;
  final ValueChanged<bool> onToggle;
  final List<Widget> children;
  final VoidCallback onSend;

  const ControlBlock({
    super.key,
    required this.title,
    required this.activationColor,
    required this.isEnabled,
    required this.onToggle,
    required this.children,
    required this.onSend,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final borderColor = isEnabled ? activationColor : theme.colorScheme.outline.withOpacity(0.5);

    return Card(
      elevation: 2,
      shape: RoundedRectangleBorder(
        side: BorderSide(color: borderColor, width: 1.5),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Padding(
        padding: const EdgeInsets.all(12.0),
        // This Column now shrinks to fit its content, preventing the layout error.
        child: Column(
          mainAxisSize: MainAxisSize.min, // Prevents unbounded height error in scroll views
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Expanded(child: Text(title, style: theme.textTheme.titleMedium)),
                Switch(value: isEnabled, onChanged: onToggle, activeColor: activationColor, materialTapTargetSize: MaterialTapTargetSize.shrinkWrap),
              ],
            ),
            const Divider(),
            ...children,
            // Spacer was removed. Button is now placed after content.
            if (children.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(top: 8.0), // Add space above button
                child: Align(
                  alignment: Alignment.bottomRight,
                  child: FilledButton.tonal(onPressed: onSend, child: const Text("Send")),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

Widget buildTextField(TextEditingController controller, String label) {
  return Padding(
    padding: const EdgeInsets.symmetric(vertical: 4.0),
    child: TextField(
      controller: controller,
      decoration: InputDecoration(labelText: label, border: const OutlineInputBorder(), isDense: true),
      keyboardType: const TextInputType.numberWithOptions(decimal: true),
    ),
  );
}

/// # Parameter Settings Page
class ParameterSettingsPage extends StatefulWidget {
  const ParameterSettingsPage({super.key});
  @override
  State<ParameterSettingsPage> createState() => _ParameterSettingsPageState();
}

class _ParameterSettingsPageState extends State<ParameterSettingsPage> {
  final Map<String, bool> _enabled = {
    'temp': false, 'motor': false, 'pressure': false, 'oxy': false,
    'flow': false, 'distance': false, 'ph': false, 'nutrient': false,
    'antifoam': false, 'valve1': false, 'valve2': false,
  };

  final _controllers = {
    'temp_setpoint': TextEditingController(text: '25'),
    'motor_rpm': TextEditingController(text: '100'),
    'pressure_setpoint': TextEditingController(text: '100'),
    'oxy_setpoint': TextEditingController(text: '50'),
    'flow_setpoint': TextEditingController(text: '1'),
    'flow_max': TextEditingController(text: '50'),
    'distance_min': TextEditingController(text: '100'),
    'ph_setpoint': TextEditingController(text: '7'),
    'ph_error': TextEditingController(text: '0.17'),
    'ph_op_time': TextEditingController(text: '5'),
    'ph_disable_time': TextEditingController(text: '20'),
    'ph_speed': TextEditingController(text: '50'),
    'nutri_op_time': TextEditingController(text: '999'),
    'nutri_disable_time': TextEditingController(text: '1'),
    'nutri_op_cycle': TextEditingController(text: '500'),
    'nutri_disable_cycle': TextEditingController(text: '1'),
    'nutri_speed': TextEditingController(text: '99'),
    'antifoam_op_time': TextEditingController(text: '5'),
    'antifoam_disable_time': TextEditingController(text: '20'),
    'antifoam_speed': TextEditingController(text: '99'),
  };

  void _sendCommand(Map<String, dynamic> command) {
    Provider.of<CommunicationService>(context, listen: false).sendCommand(jsonEncode(command));
    ScaffoldMessenger.of(context)
      ..removeCurrentSnackBar()
      ..showSnackBar(SnackBar(
        content: Text("Sent: ${jsonEncode(command)}"),
        duration: const Duration(seconds: 2),
      ));
  }

  @override
  void dispose() {
    _controllers.forEach((key, value) => value.dispose());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(8.0),
      child: LayoutBuilder(
          builder: (context, constraints) {
            final smallBlockWidth = (constraints.maxWidth / 2) - 8;
            return Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                SizedBox(width: smallBlockWidth, child: _buildTempBlock()),
                SizedBox(width: smallBlockWidth, child: _buildMotorBlock()),
                SizedBox(width: smallBlockWidth, child: _buildPressureBlock()),
                SizedBox(width: smallBlockWidth, child: _buildOxygenBlock()),
                _buildDistanceBlock(),
                _buildFlowBlock(),
                _buildPhBlock(),
                _buildNutrientBlock(),
                _buildAntifoamBlock(),
              ],
            );
          }
      ),
    );
  }

  ControlBlock _buildTempBlock() {
    return ControlBlock(
      title: "Temp",
      activationColor: Colors.red,
      isEnabled: _enabled['temp']!,
      onToggle: (val) => setState(() => _enabled['temp'] = val),
      onSend: () {
        double setpoint = double.tryParse(_controllers['temp_setpoint']!.text) ?? 25.0;
        _sendCommand({'tempSetpoint': _enabled['temp']! ? setpoint : 0});
      },
      children: [buildTextField(_controllers['temp_setpoint']!, "Setpoint (°C)")],
    );
  }

  ControlBlock _buildMotorBlock() {
    return ControlBlock(
      title: "Motor",
      activationColor: Colors.green,
      isEnabled: _enabled['motor']!,
      onToggle: (val) => setState(() => _enabled['motor'] = val),
      onSend: () {
        int rpm = int.tryParse(_controllers['motor_rpm']!.text) ?? 100;
        _sendCommand({'motorSetpoint': _enabled['motor']! ? rpm : 0});
      },
      children: [buildTextField(_controllers['motor_rpm']!, "RPM")],
    );
  }

  ControlBlock _buildPressureBlock() {
    return ControlBlock(
      title: "Pressure",
      activationColor: Colors.cyan,
      isEnabled: _enabled['pressure']!,
      onToggle: (val) => setState(() => _enabled['pressure'] = val),
      onSend: () {
        int setpoint = int.tryParse(_controllers['pressure_setpoint']!.text) ?? 100;
        _sendCommand({'pressureReference': _enabled['pressure']! ? setpoint : 0});
      },
      children: [buildTextField(_controllers['pressure_setpoint']!, "Setpoint (mmHg)")],
    );
  }

  ControlBlock _buildOxygenBlock() {
    return ControlBlock(
      title: "Oxygen",
      activationColor: Colors.purple,
      isEnabled: _enabled['oxy']!,
      onToggle: (val) => setState(() => _enabled['oxy'] = val),
      onSend: () {
        double setpoint = double.tryParse(_controllers['oxy_setpoint']!.text) ?? 50;
        _sendCommand({'oxygenMonitor': _enabled['oxy']! ? setpoint : 0});
      },
      children: [buildTextField(_controllers['oxy_setpoint']!, "Setpoint (%)")],
    );
  }

  ControlBlock _buildFlowBlock() {
    return ControlBlock(
      title: "Flowmeter",
      activationColor: Colors.yellow.shade700,
      isEnabled: _enabled['flow']!,
      onToggle: (val) => setState(() => _enabled['flow'] = val),
      onSend: () {
        double setpoint = double.tryParse(_controllers['flow_setpoint']!.text) ?? 1.0;
        double maxFlow = double.tryParse(_controllers['flow_max']!.text) ?? 50.0;
        _sendCommand({
          'flowmeterComm': _enabled['flow']! ? 1 : 0,
          'flowSetpoint': setpoint,
          'maxFlow': maxFlow,
        });
      },
      children: [
        buildTextField(_controllers['flow_setpoint']!, "Setpoint"),
        buildTextField(_controllers['flow_max']!, "Max Flow"),
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceAround,
          children: [
            Column(
              children: [
                const Text("Valve 1"),
                Switch(value: _enabled['valve1']!, onChanged: (val) {
                  setState(() => _enabled['valve1'] = val);
                  _sendCommand({'valve_1': val ? 1 : 0});
                }),
              ],
            ),
            Column(
              children: [
                const Text("Valve 2"),
                Switch(value: _enabled['valve2']!, onChanged: (val) {
                  setState(() => _enabled['valve2'] = val);
                  _sendCommand({'valve_2': val ? 1 : 0});
                }),
              ],
            ),
          ],
        )
      ],
    );
  }

  ControlBlock _buildDistanceBlock() {
    return ControlBlock(
      title: "Distance",
      activationColor: Colors.grey,
      isEnabled: _enabled['distance']!,
      onToggle: (val) => setState(() => _enabled['distance'] = val),
      onSend: () {
        double minDistance = double.tryParse(_controllers['distance_min']!.text) ?? 100.0;
        _sendCommand({
          'distanceSensorComm': _enabled['distance']! ? 1 : 0,
          'distanceSensorReference': minDistance
        });
      },
      children: [buildTextField(_controllers['distance_min']!, "Min Distance (cm)")],
    );
  }

  ControlBlock _buildPhBlock() {
    return ControlBlock(
      title: "pH Control",
      activationColor: Colors.blue,
      isEnabled: _enabled['ph']!,
      onToggle: (val) => setState(() => _enabled['ph'] = val),
      onSend: () {
        _sendCommand({
          "pHSetpoint": _enabled['ph']! ? (double.tryParse(_controllers['ph_setpoint']!.text) ?? 7.0) : 0,
          "pHError": double.tryParse(_controllers['ph_error']!.text) ?? 0.17,
          "pHOperation": int.tryParse(_controllers['ph_op_time']!.text) ?? 5,
          "pHMix": int.tryParse(_controllers['ph_disable_time']!.text) ?? 20,
          "pHIntensity": (_enabled['ph']! ? (int.tryParse(_controllers['ph_speed']!.text) ?? 50) : 0) * 10,
        });
      },
      children: [
        buildTextField(_controllers['ph_setpoint']!, "Setpoint"),
        buildTextField(_controllers['ph_error']!, "Inactive Error"),
        buildTextField(_controllers['ph_op_time']!, "Op Time (s)"),
        buildTextField(_controllers['ph_disable_time']!, "Disable Time (s)"),
        buildTextField(_controllers['ph_speed']!, "Pump Speed (%)"),
      ],
    );
  }

  ControlBlock _buildNutrientBlock() {
    return ControlBlock(
      title: "Nutrient Pump",
      activationColor: Colors.brown,
      isEnabled: _enabled['nutrient']!,
      onToggle: (val) => setState(() => _enabled['nutrient'] = val),
      onSend: () {
        _sendCommand({
          "nutriOperation": int.tryParse(_controllers['nutri_op_time']!.text) ?? 999,
          "nutriMix": int.tryParse(_controllers['nutri_disable_time']!.text) ?? 1,
          "nutriOpCycle": int.tryParse(_controllers['nutri_op_cycle']!.text) ?? 500,
          "nutriMixCycle": int.tryParse(_controllers['nutri_disable_cycle']!.text) ?? 1,
          "nutriIntensity": _enabled['nutrient']! ? (int.tryParse(_controllers['nutri_speed']!.text) ?? 99) : 0,
        });
      },
      children: [
        buildTextField(_controllers['nutri_op_time']!, "Op Time (s)"),
        buildTextField(_controllers['nutri_disable_time']!, "Disable Time (s)"),
        buildTextField(_controllers['nutri_op_cycle']!, "Op Cycle (min)"),
        buildTextField(_controllers['nutri_disable_cycle']!, "Disable Cycle (min)"),
        buildTextField(_controllers['nutri_speed']!, "Pump Speed (%)"),
      ],
    );
  }

  ControlBlock _buildAntifoamBlock() {
    return ControlBlock(
      title: "Antifoam",
      activationColor: Colors.orange,
      isEnabled: _enabled['antifoam']!,
      onToggle: (val) => setState(() => _enabled['antifoam'] = val),
      onSend: () {
        _sendCommand({
          "antifoamOperation": int.tryParse(_controllers['antifoam_op_time']!.text) ?? 5,
          "antifoamMix": int.tryParse(_controllers['antifoam_disable_time']!.text) ?? 20,
          "antifoamIntensity": _enabled['antifoam']! ? (int.tryParse(_controllers['antifoam_speed']!.text) ?? 99) : 0,
        });
      },
      children: [
        buildTextField(_controllers['antifoam_op_time']!, "Op Time (s)"),
        buildTextField(_controllers['antifoam_disable_time']!, "Disable Time (s)"),
        buildTextField(_controllers['antifoam_speed']!, "Pump Speed (%)"),
      ],
    );
  }
}

/// # Graph Page and Chart Widget
class GraphPage extends StatelessWidget {
  const GraphPage({super.key});

  @override
  Widget build(BuildContext context) {
    final dataModel = context.watch<DataModel>();
    final data = dataModel.data;
    if (data.isEmpty) {
      return const Center(child: Text("No data received yet. Connect to a device."));
    }
    return ListView(
      padding: const EdgeInsets.all(5.0),
      children: [
        _buildChartCard(context, "Temperature (°C)", data.map((d) => FlSpot(d.time, d.temp)).toList()),
        _buildChartCard(context, "pH", data.map((d) => FlSpot(d.time, d.pH)).toList()),
        _buildChartCard(context, "Oxygen (ADC)", data.map((d) => FlSpot(d.time, d.oxygen)).toList()),
        _buildChartCard(context, "Pressure (mmHg)", data.map((d) => FlSpot(d.time, d.pressure)).toList()),
        _buildChartCard(context, "Flow Rate", data.map((d) => FlSpot(d.time, d.flowRate)).toList()),
        _buildChartCard(context, "Distance (cm)", data.map((d) => FlSpot(d.time, d.distance)).toList()),
        _buildChartCard(context, "Antifoam", data.map((d) => FlSpot(d.time, d.antifoam)).toList()),
      ],
    );
  }

  Widget _buildChartCard(BuildContext context, String title, List<FlSpot> spots) {
    return Card(
      elevation: 2,
      margin: const EdgeInsets.symmetric(vertical: 8.0),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(title, style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 16),
            SizedBox(
              height: 200,
              child: LineChartWidget(spots: spots),
            ),
          ],
        ),
      ),
    );
  }
}

class LineChartWidget extends StatelessWidget {
  final List<FlSpot> spots;
  const LineChartWidget({super.key, required this.spots});

  @override
  Widget build(BuildContext context) {
    bool canCalculateInterval = spots.length >= 2;
    double interval = canCalculateInterval ? ((spots.last.x - spots.first.x) / 5).clamp(0.1, double.infinity) : 1;
    return LineChart(
      LineChartData(
        lineBarsData: [
          LineChartBarData(
            spots: spots,
            isCurved: true,
            barWidth: 2,
            dotData: const FlDotData(show: false),
            color: Theme.of(context).colorScheme.primary,
          ),
        ],
        titlesData: FlTitlesData(
          leftTitles: const AxisTitles(sideTitles: SideTitles(showTitles: true, reservedSize: 40)),
          bottomTitles: AxisTitles(sideTitles: SideTitles(showTitles: true, reservedSize: 30, interval: interval)),
          topTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
          rightTitles: const AxisTitles(sideTitles: SideTitles(showTitles: false)),
        ),
        gridData: FlGridData(
          show: true,
          drawVerticalLine: true,
          getDrawingHorizontalLine: (value) => FlLine(color: Theme.of(context).dividerColor, strokeWidth: 0.5),
          getDrawingVerticalLine: (value) => FlLine(color: Theme.of(context).dividerColor, strokeWidth: 0.5),
        ),
        borderData: FlBorderData(show: true, border: Border.all(color: Theme.of(context).dividerColor)),
      ),
    );
  }
}