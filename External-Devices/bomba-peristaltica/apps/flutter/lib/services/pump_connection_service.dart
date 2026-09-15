// pump_connection_service.dart

import 'dart:async';
import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:http/http.dart' as http;
import 'package:shared_preferences/shared_preferences.dart';

class PumpConnectionService extends ChangeNotifier {
  String _ipAddress = '192.168.6.1';
  bool _isConnected = false;
  bool _isConnecting = false;
  Timer? _pollTimer;

  // Telemetry properties
  int _mode = 0;
  int _pwm = 0;
  double _speed = 0.0;
  double _flowRate = 0.0;
  double _cumVolume = 0.0;
  double _vTarget = 0.0;
  bool _isActive = false;
  bool _isWaiting = false;
  double _currentTime = 0.0;
  double _initTime = 0.0;
  double _finalTime = 0.0;

  // Optional arrays
  List<double> _polyCoeffs = [];
  int _numSegments = 0;
  List<double> _timePoints = [];
  List<double> _flowPoints = [];

  // Stream controller to replicate stream behavior for graphing pages
  final StreamController<Map<String, dynamic>> _telemetryStreamController =
      StreamController<Map<String, dynamic>>.broadcast();

  // Getters
  String get ipAddress => _ipAddress;
  bool get isConnected => _isConnected;
  bool get isConnecting => _isConnecting;

  int get mode => _mode;
  int get pwm => _pwm;
  double get speed => _speed;
  double get flowRate => _flowRate;
  double get cumVolume => _cumVolume;
  double get vTarget => _vTarget;
  bool get isActive => _isActive;
  bool get isWaiting => _isWaiting;
  double get currentTime => _currentTime;
  double get initTime => _initTime;
  double get finalTime => _finalTime;

  List<double> get polyCoeffs => _polyCoeffs;
  int get numSegments => _numSegments;
  List<double> get timePoints => _timePoints;
  List<double> get flowPoints => _flowPoints;

  Stream<Map<String, dynamic>> get telemetryStream => _telemetryStreamController.stream;

  PumpConnectionService() {
    _loadIpAddress();
  }

  Future<void> _loadIpAddress() async {
    SharedPreferences prefs = await SharedPreferences.getInstance();
    _ipAddress = prefs.getString('pump_ip_address') ?? '192.168.6.1';
    notifyListeners();
    // Proactively start polling
    startPolling();
  }

  Future<void> updateIpAddress(String newIp) async {
    _ipAddress = newIp.trim();
    SharedPreferences prefs = await SharedPreferences.getInstance();
    await prefs.setString('pump_ip_address', _ipAddress);
    notifyListeners();
    // Restart polling with new IP
    startPolling();
  }

  void startPolling() {
    _pollTimer?.cancel();
    _pollTimer = Timer.periodic(const Duration(seconds: 1), (timer) {
      _fetchTelemetry();
    });
  }

  void stopPolling() {
    _pollTimer?.cancel();
    _pollTimer = null;
  }

  Future<bool> testConnection(String ip) async {
    _isConnecting = true;
    notifyListeners();
    try {
      final response = await http.get(Uri.parse('http://$ip/readData')).timeout(const Duration(seconds: 3));
      if (response.statusCode == 200) {
        _isConnecting = false;
        _isConnected = true;
        _ipAddress = ip;
        SharedPreferences prefs = await SharedPreferences.getInstance();
        await prefs.setString('pump_ip_address', _ipAddress);
        _parseTelemetryData(response.body);
        startPolling();
        notifyListeners();
        return true;
      }
    } catch (e) {
      print('Connection test failed: $e');
    }
    _isConnecting = false;
    _isConnected = false;
    notifyListeners();
    return false;
  }

  Future<void> _fetchTelemetry() async {
    try {
      final response = await http.get(Uri.parse('http://$_ipAddress/readData')).timeout(const Duration(milliseconds: 900));
      if (response.statusCode == 200) {
        if (!_isConnected) {
          _isConnected = true;
          notifyListeners();
        }
        _parseTelemetryData(response.body);
      } else {
        if (_isConnected) {
          _isConnected = false;
          notifyListeners();
        }
      }
    } catch (e) {
      if (_isConnected) {
        _isConnected = false;
        notifyListeners();
      }
    }
  }

  void _parseTelemetryData(String body) {
    try {
      final Map<String, dynamic> json = jsonDecode(body);
      
      _mode = json['mode'] as int? ?? 0;
      _pwm = json['pwm'] as int? ?? 0;
      _speed = (json['speed'] as num?)?.toDouble() ?? 0.0;
      
      // ESP32 reports 'flow_rate_mlmin' and 'cum_volume_ml'
      _flowRate = (json['flow_rate_mlmin'] as num?)?.toDouble() ?? 0.0;
      _cumVolume = (json['cum_volume_ml'] as num?)?.toDouble() ?? 0.0;
      _vTarget = (json['v_target_ml'] as num?)?.toDouble() ?? 0.0;
      
      // Parse active/waiting states (ESP32 returns raw JSON booleans/strings: "true"/"false" or bools)
      final activeVal = json['active'];
      if (activeVal is bool) {
        _isActive = activeVal;
      } else if (activeVal is String) {
        _isActive = activeVal.toLowerCase() == 'true';
      } else {
        _isActive = false;
      }

      final waitingVal = json['waiting'];
      if (waitingVal is bool) {
        _isWaiting = waitingVal;
      } else if (waitingVal is String) {
        _isWaiting = waitingVal.toLowerCase() == 'true';
      } else {
        _isWaiting = false;
      }
      
      _currentTime = (json['current_t_min'] as num?)?.toDouble() ?? 0.0;
      _initTime = (json['init_t_min'] as num?)?.toDouble() ?? 0.0;
      _finalTime = (json['final_t_min'] as num?)?.toDouble() ?? 0.0;

      // Handle optional arrays for mode 4/5
      if (json.containsKey('poly')) {
        _polyCoeffs = List<double>.from(
          (json['poly'] as List? ?? []).map((e) => (e as num).toDouble())
        );
      }
      if (json.containsKey('num_segments')) {
        _numSegments = json['num_segments'] as int? ?? 0;
      }
      if (json.containsKey('time_points')) {
        _timePoints = List<double>.from(
          (json['time_points'] as List? ?? []).map((e) => (e as num).toDouble())
        );
      }
      if (json.containsKey('flow_points')) {
        _flowPoints = List<double>.from(
          (json['flow_points'] as List? ?? []).map((e) => (e as num).toDouble())
        );
      }

      // Publish structured message to telemetryStream
      _telemetryStreamController.add({
        'mode': _mode,
        'pwm': _pwm,
        'speed': _speed,
        'flowRate': _flowRate,
        'cumVolume': _cumVolume,
        'vTarget': _vTarget,
        'isActive': _isActive,
        'isWaiting': _isWaiting,
        'currentTime': _currentTime,
        'initTime': _initTime,
        'finalTime': _finalTime,
      });

      notifyListeners();
    } catch (e) {
      print('Error parsing telemetry: $e');
    }
  }

  Future<bool> sendCommand(Map<String, dynamic> command) async {
    if (!_isConnected) {
      // Allow sending commands even if temporarily disconnected, but log it
      print('Warning: Pump Connection is offline, trying to send command anyway...');
    }

    try {
      final jsonString = jsonEncode(command);
      print('Sending command: $jsonString');
      final response = await http.post(
        Uri.parse('http://$_ipAddress/command'),
        headers: <String, String>{
          'Content-Type': 'text/plain; charset=UTF-8',
        },
        body: jsonString,
      ).timeout(const Duration(seconds: 4));

      if (response.statusCode == 200) {
        print('Command success response: ${response.body}');
        // Proactively fetch telemetry right after sending a command to update state
        _fetchTelemetry();
        return true;
      }
    } catch (e) {
      print('Command send failed: $e');
    }
    return false;
  }

  @override
  void dispose() {
    stopPolling();
    _telemetryStreamController.close();
    super.dispose();
  }
}
