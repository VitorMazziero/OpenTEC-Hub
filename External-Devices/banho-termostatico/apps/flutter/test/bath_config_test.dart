import 'package:flutter_test/flutter_test.dart';
import 'package:bath_app/models/bath_config.dart';

void main() {
  final base = BathConfig.fromJson({
    'press_ms': 150,
    'gap_ms': 150,
    'step_c': 0.1,
    'sp_min': 5.0,
    'sp_max': 90.0,
    'enter_key': 1,
  });

  group('BathConfig.toCommandJson (diff)', () {
    test('envia só as chaves alteradas', () {
      final diff = base.toCommandJson({
        'press_ms': '200', // mudou
        'gap_ms': '150', // igual
        'sp_max': '90.0', // igual
      });
      expect(diff.keys, ['press_ms']);
      expect(diff['press_ms'], 200);
    });

    test('int vs float: press_ms arredonda, step_c mantém decimal', () {
      final diff = base.toCommandJson({
        'press_ms': '180.0',
        'step_c': '0.05',
      });
      expect(diff['press_ms'], 180);
      expect(diff['press_ms'], isA<int>());
      expect(diff['step_c'], 0.05);
    });

    test('vírgula é aceita como separador decimal', () {
      final diff = base.toCommandJson({'sp_max': '85,5'});
      expect(diff['sp_max'], 85.5);
    });

    test('valor inválido lança FormatException com a chave', () {
      expect(
        () => base.toCommandJson({'press_ms': 'abc'}),
        throwsA(isA<FormatException>()
            .having((e) => e.message, 'msg', contains('press_ms'))),
      );
    });

    test('fora da faixa é bloqueado', () {
      expect(
        () => base.toCommandJson({'press_ms': '5000'}),
        throwsA(isA<FormatException>()
            .having((e) => e.message, 'msg', contains('faixa'))),
      );
    });

    test('campos vazios são ignorados', () {
      final diff = base.toCommandJson({'press_ms': '', 'gap_ms': '   '});
      expect(diff, isEmpty);
    });
  });
}
