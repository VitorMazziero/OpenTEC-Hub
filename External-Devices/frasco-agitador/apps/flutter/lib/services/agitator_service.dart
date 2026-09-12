import 'dart:async';
import 'dart:convert';
import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;
import 'package:shared_preferences/shared_preferences.dart';
import '../models/agitator_telemetry.dart';

enum ConnectionStatus {
  disconnected,
  connecting,
  connected,
  error,
}

class AgitatorService extends ChangeNotifier {
  static const String _prefKeyIp = 'agitator_ip_address';
  static const String defaultIp = '192.168.4.1';

  String _ipAddress = defaultIp;
  ConnectionStatus _status = ConnectionStatus.disconnected;
  AgitatorTelemetry _telemetry = AgitatorTelemetry.empty();

  double _targetDuty = 0.0;
  bool _dirRight = true;
  bool _potEnabled = true;

  double _lastDutySent = -1.0;
  bool _lastDirSent = true;
  bool _lastPotSent = true;

  int _consecutiveFailures = 0;
  Timer? _pollTimer;
  Timer? _changeTimer;
  Timer? _heartbeatTimer;

  // Getters
  String get ipAddress => _ipAddress;
  ConnectionStatus get status => _status;
  AgitatorTelemetry get telemetry => _telemetry;
  double get targetDuty => _targetDuty;
  bool get dirRight => _dirRight;
  bool get potEnabled => _potEnabled;
  bool get isConnected => _status == ConnectionStatus.connected;
  bool get isConnecting => _status == ConnectionStatus.connecting;

  AgitatorService() {
    _init();
  }

  Future<void> _init() async {
    await _loadSavedIp();
    _startBackgroundTasks();
  }

  Future<void> _loadSavedIp() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      final savedIp = prefs.getString(_prefKeyIp);
      if (savedIp != null && savedIp.trim().isNotEmpty) {
        _ipAddress = savedIp.trim();
      }
    } catch (e) {
      debugPrint('[AgitatorService] SharedPreferences load failed: $e');
    }
    notifyListeners();
  }

  Future<void> updateIpAddress(String newIp) async {
    final sanitized = newIp.trim();
    if (sanitized.isEmpty || sanitized == _ipAddress) return;

    _ipAddress = sanitized;
    _status = ConnectionStatus.connecting;
    notifyListeners();

    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setString(_prefKeyIp, _ipAddress);
    } catch (e) {
      debugPrint('[AgitatorService] SharedPreferences save failed: $e');
    }

    await pollTelemetry();
  }

  void _startBackgroundTasks() {
    _pollTimer?.cancel();
    _changeTimer?.cancel();
    _heartbeatTimer?.cancel();

    // 1. Polling de telemetria a cada 1.5s
    _pollTimer = Timer.periodic(const Duration(milliseconds: 1500), (_) {
      pollTelemetry();
    });

    // 2. Disparador diferencial (100ms)
    _changeTimer = Timer.periodic(const Duration(milliseconds: 100), (_) {
      _sendPendingChanges();
    });

    // 3. Heartbeat consolidado a cada 1s quando conectado
    _heartbeatTimer = Timer.periodic(const Duration(seconds: 1), (_) {
      if (_status == ConnectionStatus.connected) {
        _post({
          'RPM_percent': _targetDuty,
          'ActivePot': _potEnabled ? 1 : 0,
          'Dir': _dirRight ? 1 : 0,
        });
      }
    });
  }

  Future<void> pollTelemetry() async {
    if (_status == ConnectionStatus.disconnected) {
      _status = ConnectionStatus.connecting;
      notifyListeners();
    }

    try {
      final url = Uri.parse('http://$_ipAddress/diag');
      final response = await http.get(url).timeout(const Duration(seconds: 2));

      if (response.statusCode == 200) {
        final data = jsonDecode(response.body) as Map<String, dynamic>;
        _telemetry = AgitatorTelemetry.fromJson(data);
        _consecutiveFailures = 0;
        if (_status != ConnectionStatus.connected) {
          _status = ConnectionStatus.connected;
        }
        notifyListeners();
        return;
      }
    } catch (_) {
      // Fallback para /read se /diag nao estiver disponivel
      try {
        final fallbackUrl = Uri.parse('http://$_ipAddress/read');
        final fbRes = await http.get(fallbackUrl).timeout(const Duration(seconds: 2));
        if (fbRes.statusCode == 200) {
          final data = jsonDecode(fbRes.body) as Map<String, dynamic>;
          _telemetry = AgitatorTelemetry.fromJson(data);
          _consecutiveFailures = 0;
          if (_status != ConnectionStatus.connected) {
            _status = ConnectionStatus.connected;
          }
          notifyListeners();
          return;
        }
      } catch (_) {}
    }

    _consecutiveFailures++;
    if (_consecutiveFailures >= 3) {
      if (_status != ConnectionStatus.error) {
        _status = ConnectionStatus.error;
        notifyListeners();
      }
    }
  }

  Future<void> _sendPendingChanges() async {
    if (_targetDuty != _lastDutySent) {
      await _post({'RPM_percent': _targetDuty});
      _lastDutySent = _targetDuty;
    }
    if (_potEnabled != _lastPotSent) {
      await _post({'ActivePot': _potEnabled ? 1 : 0});
      _lastPotSent = _potEnabled;
    }
    if (_dirRight != _lastDirSent) {
      await _post({'Dir': _dirRight ? 1 : 0});
      _lastDirSent = _dirRight;
    }
  }

  Future<bool> _post(Map<String, dynamic> jsonPayload) async {
    try {
      final url = Uri.parse('http://$_ipAddress/cmd');
      final response = await http
          .post(
            url,
            headers: {'Content-Type': 'application/json'},
            body: jsonEncode(jsonPayload),
          )
          .timeout(const Duration(seconds: 2));

      if (response.statusCode == 200) {
        _consecutiveFailures = 0;
        if (_status != ConnectionStatus.connected) {
          _status = ConnectionStatus.connected;
          notifyListeners();
        }
        return true;
      }
    } catch (e) {
      debugPrint('[AgitatorService] POST /cmd falhou: $e');
    }
    return false;
  }

  // Comandos de Controle
  void setDuty(double value) {
    final clamped = double.parse(value.clamp(0.0, 100.0).toStringAsFixed(1));
    if (_targetDuty != clamped) {
      _targetDuty = clamped;
      notifyListeners();
    }
  }

  void adjustDuty(double delta) {
    setDuty(_targetDuty + delta);
  }

  void setDirection(bool right) {
    if (_dirRight != right) {
      _dirRight = right;
      notifyListeners();
    }
  }

  void toggleDirection() {
    setDirection(!_dirRight);
  }

  void setPotEnabled(bool enabled) {
    if (_potEnabled != enabled) {
      _potEnabled = enabled;
      notifyListeners();
    }
  }

  void togglePotEnabled() {
    setPotEnabled(!_potEnabled);
  }

  /// Parada Imediata de Emergência com envio forçado
  Future<void> emergencyStop() async {
    _targetDuty = 0.0;
    _lastDutySent = 0.0;
    notifyListeners();

    // Disparo imediato direto
    await _post({
      'RPM_percent': 0.0,
      'ActivePot': _potEnabled ? 1 : 0,
      'Dir': _dirRight ? 1 : 0,
    });
  }

  @override
  void dispose() {
    _pollTimer?.cancel();
    _changeTimer?.cancel();
    _heartbeatTimer?.cancel();
    super.dispose();
  }
}
