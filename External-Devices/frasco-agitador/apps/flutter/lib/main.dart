import 'dart:async';
import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:http/http.dart' as http;

/* ──────────────────────────────────────────────────────────────────────────
 *  Flutter client for ESP32‑S3 IBT‑2 motor controller – Rev.F firmware
 *  Modifications (May2025)
 *    • Removes immediate network calls from UI events.
 *    • 100ms watcher identifies local state changes and propagates them.
 *    • 1s heartbeat publishes full state to reinforce synchrony.
 *    • Communication layer adds sendState() for aggregated payloads.
 *  Dependencies (pubspec.yaml):
 *    dependencies:
 *      flutter:
 *        sdk: flutter
 *      http: ^1.2.0
 *  Tested with Flutter3.22 (stable)
 * ──────────────────────────────────────────────────────────────────────────*/

void main() => runApp(const MotorControlApp());

class MotorControlApp extends StatelessWidget {
  const MotorControlApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: ThemeData.dark(useMaterial3: true).copyWith(
        colorScheme: ColorScheme.dark(
          primary: Colors.blueGrey,
          secondary: Colors.amber,
          surface: Colors.grey[900]!,
          onPrimary: Colors.black,
          onSurface: Colors.white,
        ),
        scaffoldBackgroundColor: Colors.black,
        sliderTheme: SliderThemeData(
          activeTrackColor: Colors.blueGrey,
          inactiveTrackColor: Colors.grey,
          thumbColor: Colors.blueGrey[50],
          overlayColor: Colors.blueGrey.withOpacity(0.1),
        ),
        switchTheme: SwitchThemeData(
          thumbColor: WidgetStateProperty.resolveWith<Color>((states) {
            return states.contains(WidgetState.disabled)
                ? Colors.white
                : Colors.blueGrey;
          }),
          trackColor: WidgetStateProperty.resolveWith<Color>((states) {
            return states.contains(WidgetState.disabled)
                ? Colors.white
                : Colors.blueGrey.shade100;
          }),
        ),
        textTheme: const TextTheme(
          bodyMedium: TextStyle(color: Colors.white),
          labelLarge: TextStyle(color: Colors.white),
        ),
      ),
      home: const ControlPage(),
    );
  }
}

/* ───── Communication layer (HTTP POST /cmd) ────────────────────────────── */
class CommunicationService {
  CommunicationService._internal();
  static final CommunicationService _instance = CommunicationService._internal();
  factory CommunicationService() => _instance;

  /// Change if the ESP32 uses another soft‑AP address.
  static const String _baseUrl = 'http://192.168.4.1';

  Future<void> _post(Map<String, dynamic> json) async {
    try {
      await http
          .post(Uri.parse('$_baseUrl/cmd'),
              headers: {'Content-Type': 'application/json'},
              body: jsonEncode(json))
          .timeout(const Duration(seconds: 2));
    } catch (e) {
      debugPrint('HTTP POST failed: $e');
    }
  }

  Future<void> sendDuty(double pct) => _post({'RPM_percent': pct});
  Future<void> sendActivePot(bool enabled) => _post({'ActivePot': enabled ? 1 : 0});
  Future<void> sendDir(bool right) => _post({'Dir': right ? 1 : 0});

  /// Aggregated state dispatch used by the 1 s heartbeat.
  Future<void> sendState({required double pct, required bool potEnabled, required bool dirRight}) =>
      _post({'RPM_percent': pct, 'ActivePot': potEnabled ? 1 : 0, 'Dir': dirRight ? 1 : 0});
}

/* ───── UI ──────────────────────────────────────────────────────────────── */
class ControlPage extends StatefulWidget {
  const ControlPage({super.key});

  @override
  State<ControlPage> createState() => _ControlPageState();
}

class _ControlPageState extends State<ControlPage> {
  final CommunicationService _comm = CommunicationService();

  double _duty = 0.0; // 0…100 %
  bool _potEnabled = true; // ActivePot
  bool _dirRight = true; // Dir (true = right)

  double _lastDutySent = -1.0;
  bool _lastPotSent = true;
  bool _lastDirSent = true;

  late final Timer _changeTimer;   // 100 ms differential sender
  late final Timer _heartbeat;     // 1 s state heartbeat

  final TextEditingController _dutyCtrl = TextEditingController(text: '0');

  /* --- lifecycle -------------------------------------------------------- */
  @override
  void initState() {
    super.initState();

    // Differential sender: pushes only when local state changes.
    _changeTimer = Timer.periodic(const Duration(milliseconds: 100), (_) {
      if (_duty != _lastDutySent) {
        _comm.sendDuty(_duty);
        _lastDutySent = _duty;
      }
      if (_potEnabled != _lastPotSent) {
        _comm.sendActivePot(_potEnabled);
        _lastPotSent = _potEnabled;
      }
      if (_dirRight != _lastDirSent) {
        _comm.sendDir(_dirRight);
        _lastDirSent = _dirRight;
      }
    });

    // Heartbeat sender: publishes full state every second.
    _heartbeat = Timer.periodic(const Duration(seconds: 1), (_) {
      _comm.sendState(pct: _duty, potEnabled: _potEnabled, dirRight: _dirRight);
    });
  }

  @override
  void dispose() {
    _changeTimer.cancel();
    _heartbeat.cancel();
    _dutyCtrl.dispose();
    super.dispose();
  }

  /* --- helpers ---------------------------------------------------------- */
  void _updateDuty(double value) {
    value = double.parse(value.clamp(0.0, 100.0).toStringAsFixed(1));
    setState(() {
      _duty = value;
      _dutyCtrl.text = value.toStringAsFixed(1);
    });
  }

  void _incDuty() => _updateDuty(_duty + 0.1);
  void _decDuty() => _updateDuty(_duty - 0.1);

  /* --- build ------------------------------------------------------------ */
  @override
  Widget build(BuildContext context) {
    return Scaffold(
      resizeToAvoidBottomInset: true,
      body: SafeArea(
        child: Column(
          children: [
            Expanded(
              child: Center(
                child: SingleChildScrollView(
                  padding: EdgeInsets.only(
                    bottom: MediaQuery.of(context).viewInsets.bottom,
                  ),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const SizedBox(height: 50),

                      // ── Vertical slider ────────────────────────────────
                      SizedBox(
                        height: 250,
                        child: RotatedBox(
                          quarterTurns: -1,
                          child: Slider(
                            value: _duty,
                            min: 0,
                            max: 100,
                            divisions: 1000,
                            label: _duty.toStringAsFixed(1),
                            onChanged: _updateDuty,
                          ),
                        ),
                      ),

                      // ── Numeric entry with ± buttons ──────────────────
                      Row(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          IconButton(
                            icon: const Icon(Icons.arrow_left),
                            onPressed: _decDuty,
                          ),
                          SizedBox(
                            width: 80,
                            child: TextField(
                              controller: _dutyCtrl,
                              keyboardType: const TextInputType.numberWithOptions(decimal: true),
                              textAlign: TextAlign.center,
                              decoration: const InputDecoration(border: OutlineInputBorder()),
                              onSubmitted: (s) {
                                final v = double.tryParse(s) ?? _duty;
                                _updateDuty(double.parse(v.toStringAsFixed(1)));
                              },
                            ),
                          ),
                          IconButton(
                            icon: const Icon(Icons.arrow_right),
                            onPressed: _incDuty,
                          ),
                        ],
                      ),
                    ],
                  ),
                ),
              ),
            ),

            // ── Fixed bottom switches ───────────────────────────────────
            Padding(
              padding: const EdgeInsets.only(bottom: 24.0),
              child: Row(
                mainAxisAlignment: MainAxisAlignment.spaceEvenly,
                children: [
                  Column(
                    children: [
                      const Text('Potentiometer'),
                      Switch(
                        value: _potEnabled,
                        onChanged: (v) => setState(() => _potEnabled = v),
                      ),
                    ],
                  ),
                  Column(
                    children: [
                      const Text('Direction'),
                      Switch(
                        value: _dirRight,
                        onChanged: (v) => setState(() => _dirRight = v),
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
