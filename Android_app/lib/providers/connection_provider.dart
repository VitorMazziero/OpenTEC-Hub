import 'dart:async';
import 'package:flutter/foundation.dart';
import '../constants/api_constants.dart';
import '../services/hub_api_service.dart';

enum ConnectionStatus { disconnected, connecting, connected, error }

class ConnectionProvider with ChangeNotifier {
  String _ip = ApiConstants.defaultIp;
  ConnectionStatus _status = ConnectionStatus.disconnected;
  String _statusMessage = "Desconectado";
  int _rttMs = 0;
  int _failedPollCount = 0;
  String? _lastEtag;

  late HubApiService _apiService;
  Timer? _pollTimer;

  // Stream for passing decoded JSON payloads to TelemetryProvider
  final StreamController<Map<String, dynamic>> _telemetryStreamController =
      StreamController<Map<String, dynamic>>.broadcast();

  ConnectionProvider() {
    _apiService = HubApiService(ip: _ip);
  }

  // Getters
  String get ip => _ip;
  ConnectionStatus get status => _status;
  bool get isConnected => _status == ConnectionStatus.connected;
  bool get isConnecting => _status == ConnectionStatus.connecting;
  String get statusMessage => _statusMessage;
  int get rttMs => _rttMs;
  HubApiService get apiService => _apiService;
  Stream<Map<String, dynamic>> get telemetryStream =>
      _telemetryStreamController.stream;

  void setIp(String newIp) {
    if (_ip == newIp.trim() || newIp.trim().isEmpty) return;
    _ip = newIp.trim();
    _apiService.dispose();
    _apiService = HubApiService(ip: _ip);
    notifyListeners();
  }

  Future<void> connect() async {
    if (isConnected || isConnecting) return;

    _status = ConnectionStatus.connecting;
    _statusMessage = "Conectando a $_ip...";
    notifyListeners();

    // Verify reachability via /ping
    final pingResult = await _apiService.ping();
    if (!pingResult.success) {
      _status = ConnectionStatus.error;
      _statusMessage = "Sem resposta do Hub: ${pingResult.error}";
      notifyListeners();
      return;
    }

    _rttMs = pingResult.rttMs;
    _status = ConnectionStatus.connected;
    _statusMessage = "Conectado a $_ip ($_rttMs ms)";
    _failedPollCount = 0;
    _lastEtag = null;
    notifyListeners();

    _startPolling();
  }

  void _startPolling() {
    _pollTimer?.cancel();
    _pollTimer = Timer.periodic(ApiConstants.defaultPollInterval, (timer) async {
      if (!isConnected) {
        timer.cancel();
        return;
      }

      final result = await _apiService.fetchTelemetry(cachedEtag: _lastEtag);

      if (result.success) {
        _failedPollCount = 0;
        if (!result.notModified && result.data != null) {
          _lastEtag = result.etag;
          _telemetryStreamController.add(result.data!);
        }
      } else {
        _failedPollCount++;
        if (_failedPollCount >= 3) {
          _status = ConnectionStatus.error;
          _statusMessage = "Conexão perdida (${result.error})";
          _pollTimer?.cancel();
          notifyListeners();
        }
      }
    });
  }

  Future<void> pingNow() async {
    final result = await _apiService.ping();
    _rttMs = result.rttMs;
    if (result.success) {
      _statusMessage = "Conectado ($_rttMs ms)";
    } else {
      _statusMessage = "Erro de conexão: ${result.error}";
    }
    notifyListeners();
  }

  void disconnect() {
    _pollTimer?.cancel();
    _pollTimer = null;
    _lastEtag = null;
    _status = ConnectionStatus.disconnected;
    _statusMessage = "Desconectado";
    notifyListeners();
  }

  @override
  void dispose() {
    _pollTimer?.cancel();
    _telemetryStreamController.close();
    _apiService.dispose();
    super.dispose();
  }
}
