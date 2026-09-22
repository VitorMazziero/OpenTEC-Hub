import 'package:flutter/material.dart';
import '../models/bath_config.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';
import 'result_snackbar.dart';

/// Formulário com todas as chaves do PROTOCOL §4. Aplica só o que mudou; o nó
/// responde `config_unchanged` quando nada muda. reset_nvs com confirmação
/// dupla (plano §4.4).
class ConfigForm extends StatefulWidget {
  final BathService service;
  const ConfigForm({super.key, required this.service});

  @override
  State<ConfigForm> createState() => _ConfigFormState();
}

class _ConfigFormState extends State<ConfigForm> {
  final Map<String, TextEditingController> _controllers = {
    for (final f in kConfigFields) f.key: TextEditingController(),
  };
  bool _loaded = false;

  BathService get s => widget.service;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    for (final c in _controllers.values) {
      c.dispose();
    }
    super.dispose();
  }

  Future<void> _load() async {
    final ok = await s.loadConfig();
    if (!mounted) return;
    if (ok) _fillFromConfig();
    setState(() => _loaded = ok);
  }

  void _fillFromConfig() {
    for (final f in kConfigFields) {
      final v = s.config.values[f.key];
      if (v != null) {
        _controllers[f.key]!.text =
            f.type == CfgType.integer ? v.toInt().toString() : v.toString();
      }
    }
  }

  Future<void> _apply() async {
    final edited = {
      for (final f in kConfigFields) f.key: _controllers[f.key]!.text,
    };
    Map<String, dynamic> diff;
    try {
      diff = s.config.toCommandJson(edited);
    } on FormatException catch (e) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(
        content: Text(e.message),
        backgroundColor: AppTheme.stopRed,
      ));
      return;
    }
    if (diff.containsKey('hub_enabled')) {
      final disabling = diff['hub_enabled'] == 0;
      final confirm = await showDialog<bool>(
        context: context,
        builder: (ctx) => AlertDialog(
          backgroundColor: AppTheme.darkCardElevated,
          title: const Text('Alterar enlace com o Hub'),
          content: Text(disabling
              ? 'Desligar hub_enabled tira o banho da cascata do Hub: o Hub passa a vê-lo '
                  'offline e para de corrigir a temperatura do reator. Continuar?'
              : 'Ligar hub_enabled conecta o banho ao Hub; com a cascata ativa o Hub '
                  'assume o controle e os comandos locais ficam bloqueados. Continuar?'),
          actions: [
            TextButton(
                onPressed: () => Navigator.of(ctx).pop(false),
                child: const Text('Cancelar')),
            FilledButton(
                onPressed: () => Navigator.of(ctx).pop(true),
                child: const Text('Confirmar')),
          ],
        ),
      );
      if (confirm != true || !mounted) return;
    }
    final r = await s.applyConfig(diff);
    if (!mounted) return;
    showCommandResult(context, r);
    if (r.ok) _fillFromConfig();
  }

  Future<void> _resetNvs() async {
    final first = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: AppTheme.darkCardElevated,
        title: const Text('Restaurar padrões (reset_nvs)'),
        content: const Text(
            'Apaga a NVS e volta a configuração de fábrica. A sombra volta a 30,0 conhecida. Continuar?'),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx, false),
              child: const Text('Cancelar')),
          TextButton(
              onPressed: () => Navigator.pop(ctx, true),
              child: const Text('Continuar')),
        ],
      ),
    );
    if (first != true || !mounted) return;
    final second = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: AppTheme.darkCardElevated,
        title: const Text('Confirmar reset_nvs'),
        content: const Text('Esta ação é irreversível. Apagar mesmo?'),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx, false),
              child: const Text('Não')),
          ElevatedButton(
            style: ElevatedButton.styleFrom(
                backgroundColor: AppTheme.stopRed,
                foregroundColor: Colors.white),
            onPressed: () => Navigator.pop(ctx, true),
            child: const Text('Apagar NVS'),
          ),
        ],
      ),
    );
    if (second != true || !mounted) return;
    final r = await s.resetNvs();
    if (!mounted) return;
    showCommandResult(context, r);
    if (r.ok) _load();
  }

  @override
  Widget build(BuildContext context) {
    final busy = s.statusData.seqBusy;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              Text('Configuração', style: Theme.of(context).textTheme.titleLarge),
              const Spacer(),
              IconButton(
                tooltip: 'Recarregar do dispositivo',
                icon: const Icon(Icons.refresh, color: AppTheme.primaryCyan),
                onPressed: _load,
              ),
            ]),
            if (busy)
              Container(
                margin: const EdgeInsets.only(bottom: 8),
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: AppTheme.accentAmber.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: const Text(
                  'Sequência em andamento — configuração é recusada (busy).',
                  style: TextStyle(color: AppTheme.accentAmber, fontSize: 12),
                ),
              ),
            if (!_loaded)
              const Padding(
                padding: EdgeInsets.symmetric(vertical: 8),
                child: Text('Configuração não carregada (offline?).',
                    style: TextStyle(color: Colors.grey, fontSize: 12)),
              ),
            const SizedBox(height: 8),
            for (final f in kConfigFields) _field(f),
            const SizedBox(height: 12),
            Row(children: [
              Expanded(
                child: ElevatedButton.icon(
                  onPressed: _apply,
                  icon: const Icon(Icons.upload),
                  label: const Text('Aplicar (só o que mudou)'),
                  style: ElevatedButton.styleFrom(
                    backgroundColor: AppTheme.primaryCyan,
                    foregroundColor: Colors.black,
                  ),
                ),
              ),
            ]),
            const SizedBox(height: 8),
            OutlinedButton.icon(
              onPressed: _resetNvs,
              icon: const Icon(Icons.restart_alt, color: AppTheme.stopRed),
              label: const Text('Restaurar padrões (reset_nvs)',
                  style: TextStyle(color: AppTheme.stopRed)),
              style: OutlinedButton.styleFrom(
                side: const BorderSide(color: AppTheme.stopRed),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _field(ConfigField f) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 5),
        child: TextField(
          controller: _controllers[f.key],
          keyboardType: TextInputType.numberWithOptions(
              decimal: f.type == CfgType.decimal, signed: f.min < 0),
          decoration: InputDecoration(
            labelText: f.key,
            helperText: '${f.hint}  ·  faixa ${f.min}–${f.max}',
            helperMaxLines: 2,
          ),
        ),
      );
}
