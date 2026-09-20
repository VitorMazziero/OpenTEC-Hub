import 'package:flutter/material.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';
import 'result_snackbar.dart';

/// Teclas cruas ×n e ▲/▼ mantidas por N ms (gate G3b). Aviso: manter `*` volta
/// à tela principal (manual §7.1). Plano §4.3.
class RawKeysRow extends StatefulWidget {
  final BathService service;
  const RawKeysRow({super.key, required this.service});

  @override
  State<RawKeysRow> createState() => _RawKeysRowState();
}

class _RawKeysRowState extends State<RawKeysRow> {
  int _count = 1;
  int _holdMs = 2000;

  BathService get s => widget.service;

  Future<void> _key(String k) async {
    final r = await s.sendKey(k, count: _count);
    if (mounted) showCommandResult(context, r);
  }

  Future<void> _hold(String k) async {
    final r = await s.holdKey(k, _holdMs);
    if (mounted) showCommandResult(context, r);
  }

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Teclas cruas',
                style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 4),
            const Text(
              'Toques crus não atualizam a sombra; envolvendo *, ▲ ou ▼ deixam o SP desconhecido no modo sombra.',
              style: TextStyle(fontSize: 12, color: Colors.grey),
            ),
            const SizedBox(height: 12),
            Wrap(spacing: 8, runSpacing: 8, children: [
              _keyBtn('*', 'star'),
              _keyBtn('▲', 'up'),
              _keyBtn('▼', 'down'),
              _keyBtn('ENTER', 'enter'),
            ]),
            const SizedBox(height: 12),
            Row(children: [
              const Text('Repetições ×',
                  style: TextStyle(color: Colors.grey, fontSize: 13)),
              Expanded(
                child: Slider(
                  value: _count.toDouble(),
                  min: 1,
                  max: 100,
                  divisions: 99,
                  label: '$_count',
                  onChanged: (v) => setState(() => _count = v.round()),
                ),
              ),
              SizedBox(
                  width: 36,
                  child: Text('$_count',
                      textAlign: TextAlign.end,
                      style: const TextStyle(fontWeight: FontWeight.bold))),
            ]),
            const Divider(color: Colors.white12, height: 24),
            Text('Tecla mantida (▲ / ▼) — gate G3b',
                style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 4),
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: AppTheme.accentAmber.withValues(alpha: 0.1),
                borderRadius: BorderRadius.circular(8),
              ),
              child: const Text(
                'Aviso: manter * devolve o C404 à tela principal (manual §7.1). '
                'Só ▲/▼ são mantidas aqui.',
                style: TextStyle(fontSize: 12, color: AppTheme.accentAmber),
              ),
            ),
            const SizedBox(height: 12),
            Row(children: [
              const Text('Duração',
                  style: TextStyle(color: Colors.grey, fontSize: 13)),
              Expanded(
                child: Slider(
                  value: _holdMs.toDouble(),
                  min: 100,
                  max: 20000,
                  divisions: 199,
                  label: '$_holdMs ms',
                  onChanged: (v) =>
                      setState(() => _holdMs = (v / 100).round() * 100),
                ),
              ),
              SizedBox(
                  width: 64,
                  child: Text('$_holdMs ms',
                      textAlign: TextAlign.end,
                      style: const TextStyle(fontWeight: FontWeight.bold))),
            ]),
            const SizedBox(height: 8),
            Wrap(spacing: 8, children: [
              _holdBtn('▲ manter', 'up'),
              _holdBtn('▼ manter', 'down'),
            ]),
          ],
        ),
      ),
    );
  }

  Widget _keyBtn(String label, String k) => SizedBox(
        width: 74,
        child: OutlinedButton(
          onPressed: () => _key(k),
          child: Text(label),
        ),
      );

  Widget _holdBtn(String label, String k) => ElevatedButton.icon(
        onPressed: () => _hold(k),
        icon: const Icon(Icons.touch_app, size: 18),
        label: Text(label),
        style: ElevatedButton.styleFrom(
          backgroundColor: AppTheme.accentAmber,
          foregroundColor: Colors.black,
        ),
      );
}
