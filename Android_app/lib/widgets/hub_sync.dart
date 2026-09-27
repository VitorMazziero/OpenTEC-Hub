import 'package:flutter/widgets.dart';

/// Keeps a form showing what the Hub currently holds (a setpoint sent from the Windows
/// app, for instance) until the operator starts editing it.
mixin HubSync<T extends StatefulWidget> on State<T> {
  final Map<String, Object?> _lastSeen = {};

  /// Runs [apply] after this frame when the Hub value for [key] changed and the operator
  /// is not [editing]. Controllers are not touched during build.
  void syncFromHub(String key, Object? value, {required bool editing, required VoidCallback apply}) {
    if (editing) return;
    if (_lastSeen.containsKey(key) && _lastSeen[key] == value) return;
    _lastSeen[key] = value;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) setState(apply);
    });
  }
}
