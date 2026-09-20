/// Modelo do `GET /config` do nó bath (firmware r2). Ver PROTOCOL.md §4.
///
/// Cada chave tem tipo (int/float), faixa e dica para o formulário. Aplicar
/// envia **só o que mudou** (diff); o nó responde `config_unchanged` quando
/// nada muda.
library;

enum CfgType { integer, decimal }

class ConfigField {
  final String key;
  final CfgType type;
  final num min;
  final num max;
  final String hint;

  const ConfigField(this.key, this.type, this.min, this.max, this.hint);

  bool inRange(num v) => v >= min && v <= max;
}

/// Ordem e faixas conforme PROTOCOL.md §4 (idêntico ao CONFIG_FIELDS do
/// bath_app.py).
const List<ConfigField> kConfigFields = [
  ConfigField('press_ms', CfgType.integer, 20, 2000, 'relé fechado por toque (ms)'),
  ConfigField('gap_ms', CfgType.integer, 20, 5000, 'relé aberto entre toques (ms)'),
  ConfigField('menu_ms', CfgType.integer, 0, 10000, 'espera após a tecla de entrada (ms)'),
  ConfigField('settle_ms', CfgType.integer, 0, 60000, 'espera após confirmar (ms)'),
  ConfigField('step_c', CfgType.decimal, 0.01, 10, 'graus por toque (d.P do C404)'),
  ConfigField('sp_min', CfgType.decimal, -200, 900, 'in.L do C404'),
  ConfigField('sp_max', CfgType.decimal, -200, 900, 'in.H do C404'),
  ConfigField('enter_key', CfgType.integer, 0, 2, '0 nenhuma, 1 *, 2 ENTER'),
  ConfigField('confirm_key', CfgType.integer, 0, 2, '0 nenhuma, 1 *, 2 ENTER'),
  ConfigField('sp_source', CfgType.integer, 0, 1, '0 sombra, 1 display'),
  ConfigField('sense_enabled', CfgType.integer, 0, 1, 'leitura das teclas'),
  ConfigField('sense_mask', CfgType.integer, 0, 15, 'bit0 *, bit1 ▲, bit2 ▼, bit3 ENTER (6 = setas)'),
  ConfigField('hub_enabled', CfgType.integer, 0, 1, 'STA + push para o Hub'),
  ConfigField('disp_seg_low', CfgType.integer, 0, 1, 'segmento aceso = LOW'),
  ConfigField('disp_dig_low', CfgType.integer, 0, 1, 'dígito ativo = LOW'),
  ConfigField('disp_seg_lead', CfgType.integer, 0, 1, 'segmentos mudam antes do dígito'),
  ConfigField('home_margin', CfgType.integer, 0, 1000, 'toques extras de ▼ no home'),
  ConfigField('send_period', CfgType.integer, 100, 60000, 'período do push ao Hub (ms)'),
  ConfigField('hold_enabled', CfgType.integer, 0, 1, 'tecla mantida (só com sp_source = 1)'),
  ConfigField('hold_min_steps', CfgType.integer, 1, 1000, 'distância mínima p/ manter a tecla (toques)'),
  ConfigField('hold_stop_steps', CfgType.integer, 0, 100, 'soltar a esta distância do alvo (toques)'),
  ConfigField('hold_lag_ms', CfgType.integer, 0, 2000, 'atraso display→relé compensado (ms)'),
  ConfigField('hold_settle_ms', CfgType.integer, 0, 10000, 'espera após soltar antes de reler (ms)'),
  ConfigField('hold_stall_ms', CfgType.integer, 200, 20000, 'display parado com tecla mantida = soltar (ms)'),
  ConfigField('mode_hold_ms', CfgType.integer, 0, 20000, '▲+▼ mantidas alternam o modo (0 desliga)'),
  ConfigField('guard_delay_ms', CfgType.integer, 1000, 60000, 'auto: painel parado antes de reverter (ms)'),
];

class BathConfig {
  /// Valores vindos do dispositivo, por chave.
  final Map<String, num> values;

  const BathConfig(this.values);

  factory BathConfig.empty() => const BathConfig({});

  factory BathConfig.fromJson(Map<String, dynamic> j) {
    final map = <String, num>{};
    for (final f in kConfigFields) {
      final v = j[f.key];
      if (v is num) map[f.key] = v;
    }
    return BathConfig(map);
  }

  double? get spMin => values['sp_min']?.toDouble();
  double? get spMax => values['sp_max']?.toDouble();

  /// Converte o texto do formulário no diff a enviar: só as chaves cujo valor
  /// mudou em relação ao que o dispositivo reportou. Lança [FormatException]
  /// com a chave se um valor for inválido ou fora da faixa.
  Map<String, dynamic> toCommandJson(Map<String, String> edited) {
    final diff = <String, dynamic>{};
    for (final f in kConfigFields) {
      final raw = edited[f.key];
      if (raw == null) continue;
      final text = raw.trim().replaceAll(',', '.');
      if (text.isEmpty) continue;

      final parsed = double.tryParse(text);
      if (parsed == null) {
        throw FormatException('${f.key}: valor inválido');
      }
      if (!f.inRange(parsed)) {
        throw FormatException('${f.key}: fora da faixa (${f.min}–${f.max})');
      }
      final num value = f.type == CfgType.integer ? parsed.round() : parsed;

      final current = values[f.key];
      if (current == null || current != value) {
        diff[f.key] = value;
      }
    }
    return diff;
  }
}
