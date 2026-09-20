import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Contador de `cmd_id` persistido em shared_preferences. Toda ação que aciona
/// relés carrega um `cmd_id` crescente; o nó responde `duplicate` a reentregas
/// e nunca reaplica toques (PROTOCOL.md §3, plano §2).
class CommandIds {
  static const String _prefKey = 'bath_last_cmd_id';
  int _current = 0;

  Future<void> load() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      _current = prefs.getInt(_prefKey) ?? 0;
    } catch (e) {
      debugPrint('[CommandIds] load falhou: $e');
      _current = 0;
    }
  }

  int get current => _current;

  /// Reserva e persiste o próximo id. O mesmo id é reutilizado em reentregas
  /// da mesma requisição (o chamador guarda o valor retornado).
  Future<int> next() async {
    _current += 1;
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setInt(_prefKey, _current);
    } catch (e) {
      debugPrint('[CommandIds] save falhou: $e');
    }
    return _current;
  }
}
