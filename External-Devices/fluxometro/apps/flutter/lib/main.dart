import 'package:flutter/material.dart';
import 'package:community_charts_flutter/community_charts_flutter.dart'
    as charts;
import 'package:web_socket_channel/web_socket_channel.dart';
import 'dart:convert';
import 'dart:async';

void main() => runApp(const MyApp());

class MyApp extends StatelessWidget {
  const MyApp({super.key});

  @override
  Widget build(BuildContext context) {
    // A modern, desaturated, and minimalist theme
    final theme = ThemeData(
      brightness: Brightness.light,
      colorScheme: const ColorScheme(
        brightness: Brightness.light,
        primary: Color(0xFF4A90E2), // A calm, desaturated blue
        onPrimary: Colors.white,
        secondary: Color(0xFF50E3C2), // A muted teal for secondary actions
        onSecondary: Colors.black,
        error: Color(0xFFE57373), // A soft, desaturated red
        onError: Colors.white,
        surface: Colors.white,
        onSurface: Color(0xFF2F3542),
      ),
      textTheme:
          const TextTheme(
            titleLarge: TextStyle(fontWeight: FontWeight.bold, fontSize: 18.0),
            bodyLarge: TextStyle(fontSize: 16.0),
            bodyMedium: TextStyle(fontSize: 14.0, color: Colors.grey),
            labelLarge: TextStyle(fontWeight: FontWeight.bold),
          ).apply(
            bodyColor: const Color(0xFF2F3542),
            displayColor: const Color(0xFF2F3542),
          ),
      scaffoldBackgroundColor: const Color(0xFFF8F9FA),
      appBarTheme: const AppBarTheme(
        backgroundColor: Colors.white,
        foregroundColor: Color(0xFF2F3542),
        elevation: 1,
        centerTitle: true,
      ),
      cardTheme: CardThemeData(
        color: Colors.white,
        elevation: 2,
        margin: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
        shadowColor: Colors.black.withValues(alpha: 0.05),
      ),
      elevatedButtonTheme: ElevatedButtonThemeData(
        style: ElevatedButton.styleFrom(
          elevation: 0,
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
          padding: const EdgeInsets.symmetric(vertical: 16),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          foregroundColor: const Color(0xFF4A90E2),
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
          borderSide: BorderSide(color: Colors.grey.shade300),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
          borderSide: BorderSide(color: Colors.grey.shade300),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
          borderSide: const BorderSide(color: Color(0xFF4A90E2), width: 2),
        ),
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 16,
          vertical: 12,
        ),
      ),
      toggleButtonsTheme: ToggleButtonsThemeData(
        borderRadius: BorderRadius.circular(8),
        selectedColor: Colors.white,
        color: const Color(0xFF4A90E2),
        fillColor: const Color(0xFF4A90E2),
        constraints: const BoxConstraints(minHeight: 40.0),
      ),
    );

    return MaterialApp(
      title: 'Flowmeter App',
      debugShowCheckedModeBanner: false,
      theme: theme,
      home: const MyHomePage(),
    );
  }
}

class MyHomePage extends StatefulWidget {
  const MyHomePage({super.key});
  @override
  MyHomePageState createState() => MyHomePageState();
}

class _PendingCommand {
  _PendingCommand(this.payload, this.lastSent, this.attempts);

  final String payload;
  DateTime lastSent;
  int attempts;
}

class MyHomePageState extends State<MyHomePage> {
  // Connection State Management
  bool _isConnecting = false;
  bool _isConnected = false;

  // WiFi (WebSocket) specific variables
  WebSocketChannel? _wsChannel;
  StreamSubscription? _wsSubscription;
  final String _flowmeterWsUrl = 'ws://192.168.10.1/ws';
  final Map<int, _PendingCommand> _pendingCommands = {};
  late final int _directSessionId;
  int _nextDirectCommandId = 1;
  Timer? _commandRetryTimer;
  static const Duration _commandAckTimeout = Duration(milliseconds: 400);
  static const int _maxCommandAttempts = 5;

  // Data variables from ESP32
  double timeSeconds = 0.0;
  int valve1State = 0;
  int valve2State = 0;
  int valveFlowState = 0;
  double flowVoltage = 0.0;
  double flowRate = 0.0;

  // Chart data lists
  List<double> liveFlowValues = [];
  List<int> valve1States = [];
  List<int> valve2States = [];
  List<double> timeValues = [];

  // Control Screen Controllers
  final TextEditingController _flowSetpointController = TextEditingController(
    text: '0',
  );
  final TextEditingController _maxFlowRateController = TextEditingController(
    text: '50',
  );

  // Calibration Screen Controllers
  final TextEditingController _k1Controller = TextEditingController();
  final TextEditingController _f1Controller = TextEditingController();
  final TextEditingController _c1Controller = TextEditingController();
  final TextEditingController _k2Controller = TextEditingController();
  final TextEditingController _f2Controller = TextEditingController();
  final TextEditingController _c2Controller = TextEditingController();

  // Chart Colors (desaturated)
  final _flowColor = charts.ColorUtil.fromDartColor(const Color(0xFF4A90E2));
  final _valve1Color = charts.ColorUtil.fromDartColor(
    const Color(0xFF50E3C2),
  ); // Teal
  final _valve2Color = charts.ColorUtil.fromDartColor(
    const Color(0xFFBDBDBD),
  ); // Gray

  @override
  void initState() {
    super.initState();
    _directSessionId = DateTime.now().millisecondsSinceEpoch & 0xFFFFFFFF;
    _commandRetryTimer = Timer.periodic(
      const Duration(milliseconds: 100),
      _retryUnacknowledgedCommands,
    );
    // Set default calibration values
    _k1Controller.text = '-139.0570077428';
    _f1Controller.text = '21.9738888302';
    _c1Controller.text = '-0.0341880209';
    _k2Controller.text = '-0.8724324917';
    _f2Controller.text = '10.6573301479';
    _c2Controller.text = '0.1953756879';
  }

  @override
  void dispose() {
    _commandRetryTimer?.cancel();
    _disconnect();
    _flowSetpointController.dispose();
    _maxFlowRateController.dispose();
    _k1Controller.dispose();
    _f1Controller.dispose();
    _c1Controller.dispose();
    _k2Controller.dispose();
    _f2Controller.dispose();
    _c2Controller.dispose();
    super.dispose();
  }

  // --- Connection and Data Handling ---

  void _connect() async {
    if (_isConnected || _isConnecting) return;
    setState(() => _isConnecting = true);

    try {
      _wsChannel = WebSocketChannel.connect(Uri.parse(_flowmeterWsUrl));
      await _wsChannel!.ready.timeout(const Duration(seconds: 8));

      if (!mounted) return;
      setState(() {
        _isConnecting = false;
        _isConnected = true;
      });
      _showSnackbar('Connected via Wi-Fi');

      _wsSubscription = _wsChannel!.stream.listen(
        (data) => _parseIncomingData(data as String),
        onDone: () => _disconnect(),
        onError: (error) => _disconnect(),
      );
    } catch (e) {
      _showSnackbar('Wi-Fi connection failed: ${e.toString()}');
      await _disconnect();
    }
  }

  Future<void> _disconnect() async {
    await _wsSubscription?.cancel();
    await _wsChannel?.sink.close();

    final hadUnconfirmedCommands = _pendingCommands.isNotEmpty;
    _pendingCommands.clear();

    if (mounted) {
      setState(() {
        _isConnected = false;
        _isConnecting = false;
        _wsChannel = null;
        _wsSubscription = null;
      });
      if (hadUnconfirmedCommands) {
        _showSnackbar(
          'Disconnected before a command was confirmed. Check valve state.',
        );
      }
    }
  }

  void _parseIncomingData(String data) {
    try {
      if (data.startsWith('{') && data.endsWith('}')) {
        final parsedData = json.decode(data) as Map<String, dynamic>;
        if (!mounted) return;

        final ackSession = (parsedData['ack_direct_session_id'] as num?)
            ?.toInt();
        final ackCommand = (parsedData['ack_direct_cmd_id'] as num?)?.toInt();
        if (ackSession == _directSessionId && ackCommand != null) {
          _pendingCommands.removeWhere((id, _) => id <= ackCommand);
        }

        final isSensorFrame = parsedData.containsKey('seconds');

        setState(() {
          timeSeconds =
              (parsedData['seconds'] as num?)?.toDouble() ?? timeSeconds;
          flowVoltage =
              (parsedData['flow_voltage'] as num?)?.toDouble() ?? flowVoltage;
          flowRate = (parsedData['flow_rate'] as num?)?.toDouble() ?? flowRate;
          valve1State = (parsedData['valve1State'] as int?) ?? valve1State;
          valve2State = (parsedData['valve2State'] as int?) ?? valve2State;
          valveFlowState =
              (parsedData['valveFlowState'] as int?) ?? valveFlowState;

          if (isSensorFrame) {
            timeValues.add(timeSeconds);
            liveFlowValues.add(flowRate);
            valve1States.add(valve1State);
            valve2States.add(valve2State);
          }

          const int maxDataPoints = 100;
          if (timeValues.length > maxDataPoints) {
            timeValues.removeAt(0);
            liveFlowValues.removeAt(0);
            valve1States.removeAt(0);
            valve2States.removeAt(0);
          }
        });
      }
    } catch (e) {
      // silent fail
    }
  }

  void _sendCommand(String command) {
    if (!_isConnected) {
      _showSnackbar("Not connected!");
      return;
    }
    try {
      final payload = json.decode(command) as Map<String, dynamic>;
      final commandId = _nextDirectCommandId++;
      payload['direct_session_id'] = _directSessionId;
      payload['direct_cmd_id'] = commandId;
      final encoded = json.encode(payload);
      _pendingCommands[commandId] = _PendingCommand(encoded, DateTime.now(), 1);
      _wsChannel?.sink.add(encoded);
    } catch (_) {
      _showSnackbar('Invalid command; nothing was sent.');
    }
  }

  void _retryUnacknowledgedCommands(Timer _) {
    if (!_isConnected || _pendingCommands.isEmpty) return;

    final now = DateTime.now();
    final expired = <int>[];
    for (final entry in _pendingCommands.entries.toList()) {
      final pending = entry.value;
      if (now.difference(pending.lastSent) < _commandAckTimeout) continue;

      if (pending.attempts >= _maxCommandAttempts) {
        expired.add(entry.key);
        continue;
      }

      pending.attempts += 1;
      pending.lastSent = now;
      _wsChannel?.sink.add(pending.payload);
    }

    for (final id in expired) {
      _pendingCommands.remove(id);
    }
    if (expired.isNotEmpty) {
      _showSnackbar(
        'Flowmeter did not confirm a command. Check the valve state.',
      );
    }
  }

  // --- Command Sending Helper Methods ---

  void _updateValveState(String key, bool isOn) {
    final value = isOn ? 1 : 0;
    _sendCommand('{"$key":$value}');
  }

  void _updateMaxFlow() {
    final value = double.tryParse(_maxFlowRateController.text);
    if (value != null) {
      _sendCommand('{"max_flow":$value}');
      _showSnackbar('Max Flow Rate set to $value');
    } else {
      _showSnackbar('Invalid Max Flow Rate value');
    }
  }

  void _updateFlowSetpoint() {
    final value = double.tryParse(_flowSetpointController.text);
    if (value != null) {
      _sendCommand('{"flow_setpoint":$value}');
      _showSnackbar('Flow Setpoint set to $value L/min');
    } else {
      _showSnackbar('Invalid Flow Setpoint value');
    }
  }

  void _saveCalibration() {
    final params = {
      'k1': double.tryParse(_k1Controller.text),
      'f1': double.tryParse(_f1Controller.text),
      'c1': double.tryParse(_c1Controller.text),
      'k2': double.tryParse(_k2Controller.text),
      'f2': double.tryParse(_f2Controller.text),
      'c2': double.tryParse(_c2Controller.text),
    };

    if (params.containsValue(null)) {
      _showSnackbar('All calibration fields must be valid numbers.');
      return;
    }

    final jsonString = json.encode(params);
    _sendCommand(jsonString);
    _showSnackbar('Calibration parameters sent to device.');
  }

  void _showSnackbar(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(message),
        duration: const Duration(seconds: 2),
        behavior: SnackBarBehavior.floating,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
      ),
    );
  }

  // --- UI BUILD METHODS ---

  @override
  Widget build(BuildContext context) {
    return DefaultTabController(
      length: 3,
      child: Scaffold(
        appBar: AppBar(
          title: const Text('Flowmeter Control'),
          bottom: const TabBar(
            tabs: [
              Tab(icon: Icon(Icons.dashboard), text: 'Dashboard'),
              Tab(icon: Icon(Icons.toggle_on), text: 'Controls'),
              Tab(icon: Icon(Icons.science), text: 'Calibration'),
            ],
          ),
        ),
        body: TabBarView(
          children: [
            _buildDashboardTab(),
            _buildControlsTab(),
            _buildCalibrationTab(),
          ],
        ),
      ),
    );
  }

  // --- TAB 1: Dashboard ---
  Widget _buildDashboardTab() {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(8.0),
      child: Column(
        children: [
          _buildConnectionCard(),
          _buildDataCard(),
          _buildGraphsCard(),
        ],
      ),
    );
  }

  // --- TAB 2: Controls ---
  Widget _buildControlsTab() {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(8.0),
      child: Column(
        children: [_buildValveControlCard(), _buildFlowControlCard()],
      ),
    );
  }

  // --- TAB 3: Calibration ---
  Widget _buildCalibrationTab() {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(8.0),
      child: Column(children: [_buildCalibrationCard()]),
    );
  }

  // --- UI Cards ---

  Widget _buildConnectionCard() {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Connection', style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 16),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
              decoration: BoxDecoration(
                color: Theme.of(context).colorScheme.surface,
                borderRadius: BorderRadius.circular(8),
                border: Border.all(color: Colors.grey.shade300),
              ),
              child: const Row(
                children: [
                  Icon(Icons.wifi, color: Colors.blue),
                  SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          'Flowmeter_AP',
                          style: TextStyle(fontWeight: FontWeight.bold),
                        ),
                        Text(
                          'Connect to this Wi-Fi network first',
                          style: TextStyle(color: Colors.grey),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 16),
            ElevatedButton.icon(
              icon: _isConnecting
                  ? const SizedBox(
                      width: 20,
                      height: 20,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        color: Colors.white,
                      ),
                    )
                  : Icon(_isConnected ? Icons.link_off : Icons.link),
              label: Text(
                _isConnecting
                    ? 'Connecting...'
                    : (_isConnected ? 'Disconnect' : 'Connect'),
              ),
              onPressed: _isConnecting
                  ? null
                  : (_isConnected ? _disconnect : _connect),
              style: ElevatedButton.styleFrom(
                backgroundColor: _isConnected
                    ? Theme.of(context).colorScheme.error
                    : Theme.of(context).colorScheme.primary,
                foregroundColor: Colors.white,
                minimumSize: const Size(double.infinity, 50),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildDataCard() {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Live Data', style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 8),
            _buildDataRow('Time (s)', timeSeconds.toStringAsFixed(2)),
            _buildDataRow(
              'Flow Voltage',
              '${flowVoltage.toStringAsFixed(4)} V',
            ),
            _buildDataRow(
              'Flow Rate',
              '${flowRate.toStringAsFixed(4)} L/min',
              valueColor: Theme.of(context).colorScheme.primary,
            ),
            _buildDataRow(
              'Valve 1 State',
              valve1State == 1 ? 'ON' : 'OFF',
              valueColor: valve1State == 1 ? Colors.green : Colors.red,
            ),
            _buildDataRow(
              'Valve 2 State',
              valve2State == 1 ? 'ON' : 'OFF',
              valueColor: valve2State == 1 ? Colors.green : Colors.red,
            ),
            _buildDataRow(
              'Flow Valve State',
              valveFlowState == 1 ? 'ON' : 'OFF',
              valueColor: valveFlowState == 1 ? Colors.green : Colors.red,
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildValveControlCard() {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Valve Controls',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 8),
            SwitchListTile(
              title: const Text('Valve 1'),
              value: valve1State == 1,
              onChanged: _isConnected
                  ? (val) => _updateValveState('v1', val)
                  : null,
            ),
            SwitchListTile(
              title: const Text('Valve 2'),
              value: valve2State == 1,
              onChanged: _isConnected
                  ? (val) => _updateValveState('v2', val)
                  : null,
            ),
            SwitchListTile(
              title: const Text('Flow Valve'),
              value: valveFlowState == 1,
              onChanged: _isConnected
                  ? (val) => _updateValveState('v_Flow', val)
                  : null,
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildFlowControlCard() {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Flow Controls',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 16),
            TextField(
              controller: _flowSetpointController,
              keyboardType: const TextInputType.numberWithOptions(
                decimal: true,
              ),
              decoration: const InputDecoration(
                labelText: 'Flow Setpoint (L/min)',
              ),
            ),
            const SizedBox(height: 16),
            ElevatedButton(
              onPressed: _isConnected ? _updateFlowSetpoint : null,
              style: ElevatedButton.styleFrom(
                minimumSize: const Size(double.infinity, 50),
              ),
              child: const Text('Set Flow Setpoint'),
            ),
            const SizedBox(height: 24),
            TextField(
              controller: _maxFlowRateController,
              keyboardType: const TextInputType.numberWithOptions(
                decimal: true,
              ),
              decoration: const InputDecoration(labelText: 'Max Flow Rate'),
            ),
            const SizedBox(height: 16),
            ElevatedButton(
              onPressed: _isConnected ? _updateMaxFlow : null,
              style: ElevatedButton.styleFrom(
                minimumSize: const Size(double.infinity, 50),
              ),
              child: const Text('Set Max Flow Rate'),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildCalibrationCard() {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Calibration Parameters',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 16),
            Text(
              'Curve 1 (Low Flow)',
              style: Theme.of(context).textTheme.bodyLarge,
            ),
            const SizedBox(height: 8),
            _buildCalTextField(_k1Controller, 'k1'),
            const SizedBox(height: 8),
            _buildCalTextField(_f1Controller, 'f1'),
            const SizedBox(height: 8),
            _buildCalTextField(_c1Controller, 'c1'),
            const SizedBox(height: 24),
            Text(
              'Curve 2 (High Flow)',
              style: Theme.of(context).textTheme.bodyLarge,
            ),
            const SizedBox(height: 8),
            _buildCalTextField(_k2Controller, 'k2'),
            const SizedBox(height: 8),
            _buildCalTextField(_f2Controller, 'f2'),
            const SizedBox(height: 8),
            _buildCalTextField(_c2Controller, 'c2'),
            const SizedBox(height: 24),
            ElevatedButton.icon(
              icon: const Icon(Icons.save),
              label: const Text('Save to Device'),
              onPressed: _isConnected ? _saveCalibration : null,
              style: ElevatedButton.styleFrom(
                backgroundColor: Theme.of(context).colorScheme.secondary,
                foregroundColor: Colors.black,
                minimumSize: const Size(double.infinity, 50),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildCalTextField(TextEditingController controller, String label) {
    return TextField(
      controller: controller,
      keyboardType: const TextInputType.numberWithOptions(
        signed: true,
        decimal: true,
      ),
      decoration: InputDecoration(labelText: label),
    );
  }

  // Helper and Graphing methods below...
  Widget _buildDataRow(String label, String value, {Color? valueColor}) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8.0),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          Text(label, style: Theme.of(context).textTheme.bodyMedium),
          Text(
            value,
            style: Theme.of(context).textTheme.bodyLarge?.copyWith(
              fontWeight: FontWeight.bold,
              color: valueColor,
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildGraphsCard() {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          children: [
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(
                  'Live Graphs',
                  style: Theme.of(context).textTheme.titleLarge,
                ),
                TextButton(
                  onPressed: _resetFlowValues,
                  child: const Text('Reset'),
                ),
              ],
            ),
            const SizedBox(height: 10),
            SizedBox(
              height: 200,
              child: timeValues.isEmpty
                  ? const Center(child: Text('Waiting for data...'))
                  : charts.LineChart(
                      _createFlowRateData(),
                      animate: false,
                      behaviors: [
                        charts.LinePointHighlighter(
                          showHorizontalFollowLine:
                              charts.LinePointHighlighterFollowLineType.none,
                          showVerticalFollowLine:
                              charts.LinePointHighlighterFollowLineType.none,
                        ),
                        charts.SelectNearest(
                          eventTrigger: charts.SelectionTrigger.tapAndDrag,
                        ),
                      ],
                    ),
            ),
            const SizedBox(height: 20),
            SizedBox(
              height: 200,
              child: timeValues.isEmpty
                  ? const Center(child: Text('Waiting for data...'))
                  : charts.LineChart(
                      _createValveStateData(),
                      animate: false,
                      behaviors: [
                        charts.LinePointHighlighter(
                          showHorizontalFollowLine:
                              charts.LinePointHighlighterFollowLineType.none,
                          showVerticalFollowLine:
                              charts.LinePointHighlighterFollowLineType.none,
                        ),
                        charts.SelectNearest(
                          eventTrigger: charts.SelectionTrigger.tapAndDrag,
                        ),
                      ],
                    ),
            ),
          ],
        ),
      ),
    );
  }

  List<charts.Series<TimeSeriesData, num>> _createFlowRateData() {
    final data = [
      for (int i = 0; i < liveFlowValues.length; i++)
        TimeSeriesData(timeValues[i], liveFlowValues[i]),
    ];
    return [
      charts.Series<TimeSeriesData, num>(
        id: 'Flow Rate',
        colorFn: (_, __) => _flowColor,
        domainFn: (TimeSeriesData point, _) => point.time,
        measureFn: (TimeSeriesData point, _) => point.value,
        data: data,
      ),
    ];
  }

  List<charts.Series<TimeSeriesData, num>> _createValveStateData() {
    final v1Data = [
      for (int i = 0; i < valve1States.length; i++)
        TimeSeriesData(timeValues[i], valve1States[i].toDouble()),
    ];
    final v2Data = [
      for (int i = 0; i < valve2States.length; i++)
        TimeSeriesData(timeValues[i], valve2States[i].toDouble()),
    ];
    return [
      charts.Series<TimeSeriesData, num>(
        id: 'Valve 1',
        colorFn: (_, __) => _valve1Color,
        domainFn: (TimeSeriesData point, _) => point.time,
        measureFn: (TimeSeriesData point, _) => point.value,
        data: v1Data,
      ),
      charts.Series<TimeSeriesData, num>(
        id: 'Valve 2',
        colorFn: (_, __) => _valve2Color,
        domainFn: (TimeSeriesData point, _) => point.time,
        measureFn: (TimeSeriesData point, _) => point.value,
        data: v2Data,
      ),
    ];
  }

  void _resetFlowValues() {
    setState(() {
      liveFlowValues.clear();
      valve1States.clear();
      valve2States.clear();
      timeValues.clear();
    });
  }
}

class TimeSeriesData {
  final double time;
  final double value;
  TimeSeriesData(this.time, this.value);
}
