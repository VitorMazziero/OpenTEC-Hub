import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:bath_app/services/bath_service.dart';
import 'package:bath_app/theme/app_theme.dart';
import 'package:bath_app/widgets/setpoint_card.dart';
import 'package:bath_app/widgets/mode_card.dart';

/// Cliente falso que devolve um /status fixo — dá ao serviço um estado
/// conhecido sem depender de rede real (bloqueada no flutter_test).
http.Client _statusClient(Map<String, dynamic> status) =>
    MockClient((req) async => http.Response(jsonEncode(status), 200,
        headers: {'content-type': 'application/json'}));

Widget _wrap(BathService s, Widget child) => ChangeNotifierProvider.value(
      value: s,
      child: MaterialApp(
        theme: AppTheme.darkTheme,
        home: Scaffold(body: SingleChildScrollView(child: child)),
      ),
    );

void main() {
  setUp(() => SharedPreferences.setMockInitialValues({}));

  testWidgets('campo de setpoint recusa texto inválido e não envia',
      (tester) async {
    final s = BathService(client: _statusClient({
      'version': 'BathClient r2',
      'mode': 'manual',
      'guard': 'off',
      'sp_shadow': 30.0,
      'sp_known': true,
      'seq_state': 'idle',
    }));
    await s.pollStatus();

    await tester.pumpWidget(_wrap(s, SetpointCard(service: s)));
    await tester.pump();

    final cmdBefore = s.lastCmdId;
    await tester.enterText(find.byType(TextField).first, 'abc');
    await tester.tap(find.text('Enviar'));
    await tester.pump();

    expect(find.text('Valor inválido'), findsOneWidget);
    // Nada foi enviado → nenhum cmd_id novo consumido.
    expect(s.lastCmdId, cmdBefore);

    s.dispose();
  });

  testWidgets('SETPOINT DESCONHECIDO aparece quando sp_known é false',
      (tester) async {
    final s = BathService(client: _statusClient({
      'version': 'BathClient r2',
      'mode': 'manual',
      'sp_shadow': 30.0,
      'sp_known': false,
      'seq_state': 'idle',
    }));
    await s.pollStatus();

    await tester.pumpWidget(_wrap(s, SetpointCard(service: s)));
    await tester.pump();

    expect(find.textContaining('SETPOINT DESCONHECIDO'), findsOneWidget);

    s.dispose();
  });

  testWidgets('seletor de modo mostra Manual/Automático', (tester) async {
    final s = BathService(client: _statusClient({
      'version': 'BathClient r2',
      'mode': 'auto',
      'guard': 'watch',
      'sp_shadow': 30.0,
      'sp_known': true,
      'seq_state': 'idle',
    }));
    await s.pollStatus();

    await tester.pumpWidget(_wrap(s, ModeCard(service: s)));
    await tester.pump();

    expect(find.text('Manual'), findsOneWidget);
    expect(find.text('Automático'), findsWidgets);

    s.dispose();
  });
}
