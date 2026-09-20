import 'dart:convert';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:bath_app/services/bath_service.dart';
import 'package:bath_app/models/bath_status.dart';

/// O binding do flutter_test bloqueia HTTP real (devolve 400), então usamos o
/// MockClient do package:http — determinístico e sem sockets — para exercitar
/// a lógica de cmd_id, reentrega e tratamento de 409/rede (equivalente ao
/// `check` do bath_app.py contra um servidor falso).
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  const statusJson = {
    'device': 'bath',
    'version': 'BathClient r2',
    'mode': 'manual',
    'guard': 'off',
    'sp_shadow': 30.0,
    'sp_known': true,
    'seq_state': 'idle',
  };

  late List<int?> receivedCmdIds;
  late List<Map<String, dynamic>> receivedBodies;
  late int failFirst; // nº de POSTs que "falham" (rede) antes de responder
  late int postCount;
  late int postStatus;
  late Map<String, dynamic> postBody;

  http.Client makeClient() => MockClient((req) async {
        if (req.method == 'POST') {
          postCount++;
          final body = jsonDecode(req.body) as Map<String, dynamic>;
          if (postCount <= failFirst) {
            throw http.ClientException('falha de rede simulada');
          }
          receivedBodies.add(body);
          receivedCmdIds.add(body['cmd_id'] as int?);
          return http.Response(jsonEncode(postBody), postStatus,
              headers: {'content-type': 'application/json'});
        }
        return http.Response(jsonEncode(statusJson), 200,
            headers: {'content-type': 'application/json'});
      });

  late BathService service;

  setUp(() async {
    SharedPreferences.setMockInitialValues({});
    receivedCmdIds = [];
    receivedBodies = [];
    failFirst = 0;
    postCount = 0;
    postStatus = 200;
    postBody = {'ok': true, 'action': 'setpoint'};
    service = BathService(client: makeClient());
    await service.pollStatus();
  });

  tearDown(() => service.dispose());

  test('pollStatus conecta e decodifica', () {
    expect(service.status, ConnectionStatus.connected);
    expect(service.statusData.mode, BathMode.manual);
    expect(service.statusData.spShadow, 30.0);
  });

  test('ação com relé carrega cmd_id crescente', () async {
    await service.setSetpoint(31.5);
    await service.adjustDelta(0.5);
    expect(receivedCmdIds.length, 2);
    expect(receivedCmdIds[0], isNotNull);
    expect(receivedCmdIds[1], receivedCmdIds[0]! + 1);
  });

  test('sync_sp não carrega cmd_id (não aciona relé)', () async {
    await service.syncSp(30.0);
    expect(receivedBodies.single.containsKey('cmd_id'), false);
  });

  test('reentrega usa o MESMO cmd_id e acaba OK', () async {
    failFirst = 2; // duas falhas de rede, depois sucesso
    final r = await service.setSetpoint(40.0);
    expect(r.ok, true);
    // O nó só registra a requisição que chegou inteira (a 3ª tentativa).
    expect(receivedCmdIds.length, 1);
    expect(receivedCmdIds.single, isNotNull);
  });

  test('409 vira erro visível com o error literal', () async {
    postStatus = 409;
    postBody = {'ok': false, 'error': 'busy'};
    final r = await service.setSetpoint(31.5);
    expect(r.ok, false);
    expect(r.error, 'busy');
    expect(r.networkFailure, false);
    expect(service.lastError, 'busy');
  });

  test('falha de rede persistente após 3 tentativas', () async {
    failFirst = 5; // mais que as 3 tentativas
    final r = await service.setSetpoint(31.5);
    expect(r.networkFailure, true);
    expect(r.ok, false);
    expect(receivedBodies, isEmpty);
  });

  test('applyConfig vazio responde config_unchanged sem tocar na rede',
      () async {
    final r = await service.applyConfig({});
    expect(r.ok, true);
    expect(r.action, 'config_unchanged');
    expect(receivedBodies, isEmpty);
  });
}
