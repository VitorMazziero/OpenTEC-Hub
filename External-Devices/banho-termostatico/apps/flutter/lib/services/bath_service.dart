import 'dart:async';
import 'dart:convert';
import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;
import 'package:shared_preferences/shared_preferences.dart';

import '../models/bath_status.dart';
import '../models/bath_config.dart';
import '../models/display_frame.dart';
import '../models/node_diag.dart';
import 'command_ids.dart';

enum ConnectionStatus { disconnected, connecting, connected, error }

/// Resultado de um comando. O app **mostra o `error` literal** e nunca o
/// traduz para um valor padrão (plano §5.1).
class CommandResult {
  final bool ok;
  final String action;
  final String error;
  final int statusCode;
  final bool networkFailure;
  final Map<String, dynamic> body;

  const CommandResult({
    required this.ok,
    required this.action,
    required this.error,
    required this.statusCode,
    required this.networkFailure,
    required this.body,
  });

  factory CommandResult.network() => const CommandResult(
        ok: false,
        action: '',
        error: 'sem resposta do dispositivo',
        statusCode: 0,
        networkFailure: true,
        body: {},
      );
}

class TracePoint {
  final DateTime t;
  final double? pv;
  final double? sp;
  const TracePoint(this.t, this.pv, this.sp);
}

class LogEntry {
  final DateTime t;
  final String text;
  final bool isError;
  const LogEntry(this.t, this.text, this.isError);
}

/// Cliente direto do nó bath: poll de `/status`, fila de comandos com `cmd_id`
/// e reentrega, último erro visível, log circular e traço de 10 min em memória.
class BathService extends ChangeNotifier {
  static const String _prefKeyHost = 'bath_host';
  static const String defaultHost = '192.168.8.1';
  static const Duration _timeout = Duration(seconds: 3);
  static const Duration traceWindow = Duration(minutes: 10);
  static const int _maxLog = 50;
  static const int _maxRetries = 3;

  final http.Client _client;
  final CommandIds _cmdIds;

  String _host = defaultHost;
  ConnectionStatus _status = ConnectionStatus.disconnected;
  BathStatus _statusData = BathStatus.empty();
  BathConfig _config = BathConfig.empty();
  DisplayFrame _display = DisplayFrame.empty();
  NodeDiag _diag = NodeDiag.empty();

  int _lastLatencyMs = 0;
  String _lastError = '';
  int _consecutiveFailures = 0;

  final List<TracePoint> _trace = [];
  final List<LogEntry> _log = [];

  Timer? _pollTimer;
  bool _displayActive = false;
  Timer? _displayTimer;
  bool _diagActive = false;
  Timer? _diagTimer;

  BathService({http.Client? client, CommandIds? cmdIds})
      : _client = client ?? http.Client(),
        _cmdIds = cmdIds ?? CommandIds();

  // Getters ------------------------------------------------------------------
  String get host => _host;
  ConnectionStatus get status => _status;
  BathStatus get statusData => _statusData;
  BathConfig get config => _config;
  DisplayFrame get display => _display;
  NodeDiag get diag => _diag;
  int get lastLatencyMs => _lastLatencyMs;
  String get lastError => _lastError;
  int get lastCmdId => _cmdIds.current;
  List<TracePoint> get trace => List.unmodifiable(_trace);
  List<LogEntry> get log => List.unmodifiable(_log);
  bool get isConnected => _status == ConnectionStatus.connected;

  Future<void> init() async {
    await _cmdIds.load();
    await _loadHost();
    startPolling();
  }

  Future<void> _loadHost() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      final saved = prefs.getString(_prefKeyHost);
      if (saved != null && saved.trim().isNotEmpty) _host = saved.trim();
    } catch (e) {
      debugPrint('[BathService] load host falhou: $e');
    }
    notifyListeners();
  }

  Future<void> updateHost(String newHost) async {
    final sanitized = newHost.trim();
    if (sanitized.isEmpty || sanitized == _host) return;
    _host = sanitized;
    _status = ConnectionStatus.connecting;
    // Trocar de host reinicia o poll e limpa o traço (plano §5.5).
    _trace.clear();
    _statusData = BathStatus.empty();
    notifyListeners();
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setString(_prefKeyHost, _host);
    } catch (e) {
      debugPrint('[BathService] save host falhou: $e');
    }
    await pollStatus();
  }

  // Poll de /status ----------------------------------------------------------
  void startPolling() {
    _pollTimer?.cancel();
    pollStatus();
    _pollTimer = Timer.periodic(const Duration(seconds: 1), (_) => pollStatus());
  }

  Future<void> pollStatus() async {
    if (_status == ConnectionStatus.disconnected) {
      _status = ConnectionStatus.connecting;
      notifyListeners();
    }
    final started = DateTime.now();
    try {
      final res = await _client
          .get(Uri.parse('http://$_host/status'))
          .timeout(_timeout);
      if (res.statusCode == 200) {
        final data = jsonDecode(res.body) as Map<String, dynamic>;
        _statusData = BathStatus.fromJson(data);
        _lastLatencyMs = DateTime.now().difference(started).inMilliseconds;
        _consecutiveFailures = 0;
        _status = ConnectionStatus.connected;
        _recordTrace();
        notifyListeners();
        return;
      }
    } catch (_) {
      // cai no tratamento de falha abaixo
    }
    _consecutiveFailures++;
    if (_consecutiveFailures >= 3 && _status != ConnectionStatus.error) {
      _status = ConnectionStatus.error;
      notifyListeners();
    }
  }

  void _recordTrace() {
    _trace.add(TracePoint(
      DateTime.now(),
      _statusData.displayAlive ? _statusData.displayPv : null,
      _statusData.spShadow,
    ));
    final cutoff = DateTime.now().subtract(traceWindow);
    _trace.removeWhere((p) => p.t.isBefore(cutoff));
  }

  // Display (500 ms enquanto a aba está aberta) ------------------------------
  void setDisplayActive(bool active) {
    if (_displayActive == active) return;
    _displayActive = active;
    _displayTimer?.cancel();
    if (active) {
      pollDisplay();
      _displayTimer =
          Timer.periodic(const Duration(milliseconds: 500), (_) => pollDisplay());
    }
  }

  Future<void> pollDisplay() async {
    try {
      final res = await _client
          .get(Uri.parse('http://$_host/display'))
          .timeout(_timeout);
      if (res.statusCode == 200) {
        _display = DisplayFrame.fromJson(
            jsonDecode(res.body) as Map<String, dynamic>);
        notifyListeners();
      }
    } catch (_) {}
  }

  // Diagnóstico (5 s enquanto aberto) ----------------------------------------
  void setDiagActive(bool active) {
    if (_diagActive == active) return;
    _diagActive = active;
    _diagTimer?.cancel();
    if (active) {
      pollDiag();
      _diagTimer =
          Timer.periodic(const Duration(seconds: 5), (_) => pollDiag());
    }
  }

  Future<void> pollDiag() async {
    try {
      final res =
          await _client.get(Uri.parse('http://$_host/diag')).timeout(_timeout);
      if (res.statusCode == 200) {
        _diag =
            NodeDiag.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
        notifyListeners();
      }
    } catch (_) {}
  }

  // Configuração -------------------------------------------------------------
  Future<bool> loadConfig() async {
    try {
      final res =
          await _client.get(Uri.parse('http://$_host/config')).timeout(_timeout);
      if (res.statusCode == 200) {
        _config =
            BathConfig.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
        notifyListeners();
        return true;
      }
    } catch (e) {
      _addLog('config: sem resposta ($e)', isError: true);
    }
    return false;
  }

  /// Envia só o diff de configuração. Retorna o resultado literal do nó.
  Future<CommandResult> applyConfig(Map<String, dynamic> diff) async {
    if (diff.isEmpty) {
      return const CommandResult(
        ok: true,
        action: 'config_unchanged',
        error: '',
        statusCode: 200,
        networkFailure: false,
        body: {'action': 'config_unchanged'},
      );
    }
    final result = await _post(diff, relay: false);
    if (result.ok) await loadConfig();
    return result;
  }

  // Ações --------------------------------------------------------------------
  // relay = true → ações que acionam relés: carregam cmd_id e são reentregues.
  Future<CommandResult> setSetpoint(double sp) =>
      _post({'setpoint': sp}, relay: true);

  Future<CommandResult> adjustDelta(double delta) =>
      _post({'delta': delta}, relay: true);

  Future<CommandResult> syncSp(double sp) =>
      _post({'sync_sp': sp}, relay: false);

  Future<CommandResult> setMode(BathMode mode) =>
      _post({'mode': mode == BathMode.auto ? 'auto' : 'manual'}, relay: false);

  Future<CommandResult> abort() => _post({'abort': 1}, relay: true);

  Future<CommandResult> sendKey(String key, {int count = 1}) =>
      _post({'key': key, 'count': count}, relay: true);

  Future<CommandResult> holdKey(String key, int holdMs) =>
      _post({'key': key, 'hold_ms': holdMs}, relay: true);

  Future<CommandResult> home({double? setpoint}) => _post(
        setpoint == null ? {'home': 1} : {'home': 1, 'setpoint': setpoint},
        relay: true,
      );

  Future<CommandResult> resetNvs() => _post({'reset_nvs': 1}, relay: false);

  /// POST /command. Com `relay`, anexa um `cmd_id` e reentrega a MESMA
  /// requisição até 3× em caso de timeout de rede (plano §2, §5.2).
  Future<CommandResult> _post(Map<String, dynamic> payload,
      {required bool relay}) async {
    // Com a posse do Hub o nó recusaria de qualquer forma; não gastar cmd_id nem
    // tráfego. Abortar/parar sempre seguem (nunca dependem do Hub).
    final stopOnly = payload.length == 1 &&
        (payload.containsKey('abort') || payload.containsKey('stop'));
    if (_statusData.hubOwned && !stopOnly) {
      const refused = CommandResult(
        ok: false,
        action: '',
        error: 'hub_owned',
        statusCode: 409,
        networkFailure: false,
        body: {},
      );
      _lastError = refused.error;
      _addLog('${_describe(payload)} → hub_owned (não enviado)', isError: true);
      notifyListeners();
      return refused;
    }
    final body = Map<String, dynamic>.from(payload);
    if (relay) {
      body['cmd_id'] = await _cmdIds.next();
    }
    final encoded = jsonEncode(body);

    CommandResult result = CommandResult.network();
    final attempts = relay ? _maxRetries : 1;
    for (var attempt = 1; attempt <= attempts; attempt++) {
      result = await _postOnce(encoded);
      if (!result.networkFailure) break;
    }

    if (result.networkFailure) {
      _lastError = result.error;
      _addLog('${_describe(payload)}: ${result.error}', isError: true);
    } else if (!result.ok) {
      _lastError = result.error;
      _addLog('${_describe(payload)} → ${result.error}', isError: true);
    } else {
      _lastError = '';
      final action = result.action.isNotEmpty ? result.action : 'ok';
      _addLog('${_describe(payload)} → $action', isError: false);
    }
    notifyListeners();
    return result;
  }

  Future<CommandResult> _postOnce(String encoded) async {
    try {
      final res = await _client
          .post(
            Uri.parse('http://$_host/command'),
            headers: {'Content-Type': 'application/json'},
            body: encoded,
          )
          .timeout(_timeout);
      Map<String, dynamic> body = {};
      try {
        final decoded = jsonDecode(res.body);
        if (decoded is Map<String, dynamic>) body = decoded;
      } catch (_) {}
      final ok = body['ok'] == true || (res.statusCode == 200 && body['ok'] != false);
      return CommandResult(
        ok: ok,
        action: (body['action'] ?? '').toString(),
        error: (body['error'] ?? (ok ? '' : 'http_${res.statusCode}')).toString(),
        statusCode: res.statusCode,
        networkFailure: false,
        body: body,
      );
    } catch (_) {
      return CommandResult.network();
    }
  }

  String _describe(Map<String, dynamic> payload) {
    final key = payload.keys.first;
    return payload.length == 1 ? '$key=${payload[key]}' : payload.toString();
  }

  void _addLog(String text, {required bool isError}) {
    _log.insert(0, LogEntry(DateTime.now(), text, isError));
    if (_log.length > _maxLog) _log.removeRange(_maxLog, _log.length);
  }

  void clearError() {
    if (_lastError.isEmpty) return;
    _lastError = '';
    notifyListeners();
  }

  @override
  void dispose() {
    _pollTimer?.cancel();
    _displayTimer?.cancel();
    _diagTimer?.cancel();
    _client.close();
    super.dispose();
  }
}
