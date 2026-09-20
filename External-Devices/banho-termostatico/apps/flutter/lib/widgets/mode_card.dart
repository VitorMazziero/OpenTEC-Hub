import 'package:flutter/material.dart';
import '../models/bath_status.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';
import 'result_snackbar.dart';

/// Manual/auto, guarda, desvio, barra do gesto ▲+▼, sincronizar sombra.
class ModeCard extends StatefulWidget {
  final BathService service;
  const ModeCard({super.key, required this.service});

  @override
  State<ModeCard> createState() => _ModeCardState();
}

class _ModeCardState extends State<ModeCard> {
  final _syncController = TextEditingController();
  String? _syncError;

  @override
  void dispose() {
    _syncController.dispose();
    super.dispose();
  }

  BathService get s => widget.service;

  Future<void> _setMode(BathMode m) async {
    final r = await s.setMode(m);
    if (mounted) showCommandResult(context, r);
  }

  Future<void> _sync() async {
    final text = _syncController.text.trim().replaceAll(',', '.');
    final v = double.tryParse(text);
    if (v == null) {
      setState(() => _syncError = 'Valor inválido');
      return;
    }
    setState(() => _syncError = null);
    final r = await s.syncSp(v);
    if (mounted) showCommandResult(context, r);
  }

  @override
  Widget build(BuildContext context) {
    final st = s.statusData;

    if (st.present && !st.supportsModes) {
      return const Card(
        child: Padding(
          padding: EdgeInsets.all(16),
          child: Row(children: [
            Icon(Icons.info_outline, color: Colors.grey),
            SizedBox(width: 10),
            Expanded(
                child: Text('Firmware sem suporte a modos (requer r2/r3).',
                    style: TextStyle(color: Colors.grey))),
          ]),
        ),
      );
    }

    final isAuto = st.mode == BathMode.auto;
    final modeHoldMs = s.config.values['mode_hold_ms']?.toInt() ?? 3000;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              Text('Modo de operação',
                  style: Theme.of(context).textTheme.titleLarge),
              const Spacer(),
              Text(st.modeLabel,
                  style: TextStyle(
                      color: isAuto ? AppTheme.accentAmber : AppTheme.primaryCyan,
                      fontWeight: FontWeight.bold)),
            ]),
            const SizedBox(height: 8),
            SegmentedButton<BathMode>(
              segments: const [
                ButtonSegment(
                    value: BathMode.manual,
                    label: Text('Manual'),
                    icon: Icon(Icons.pan_tool_alt)),
                ButtonSegment(
                    value: BathMode.auto,
                    label: Text('Automático'),
                    icon: Icon(Icons.autorenew)),
              ],
              selected: {isAuto ? BathMode.auto : BathMode.manual},
              onSelectionChanged: (sel) => _setMode(sel.first),
            ),
            const SizedBox(height: 8),
            Text(
              isAuto
                  ? 'Automático: mudanças feitas no painel são revertidas após o painel parar (guard_delay).'
                  : 'Manual: mudanças no painel são apenas reportadas (desvio); o nó não reverte.',
              style: const TextStyle(fontSize: 12, color: Colors.grey),
            ),
            const Divider(color: Colors.white12, height: 24),
            _row('Guarda', st.guardLabel),
            if (st.guardCorrections > 0)
              _row('Correções do guarda', '${st.guardCorrections}'),
            Row(mainAxisAlignment: MainAxisAlignment.spaceBetween, children: [
              const Text('Desvio (display − alvo)',
                  style: TextStyle(color: Colors.grey, fontSize: 13)),
              Text(
                st.deviationC != null
                    ? '${st.deviationC! >= 0 ? '+' : ''}${st.deviationC!.toStringAsFixed(1)} °C'
                    : '—',
                style: TextStyle(
                  fontWeight: FontWeight.w600,
                  fontSize: 13,
                  color: st.deviationC == null
                      ? Colors.grey
                      : (st.deviationC!.abs() < 0.05
                          ? AppTheme.okGreen
                          : AppTheme.accentAmber),
                ),
              ),
            ]),
            if (st.arrowsHeldMs > 0) ...[
              const SizedBox(height: 10),
              Text('Gesto ▲+▼ mantido: ${st.arrowsHeldMs} ms',
                  style: const TextStyle(fontSize: 12, color: Colors.grey)),
              const SizedBox(height: 4),
              LinearProgressIndicator(
                value: modeHoldMs > 0
                    ? (st.arrowsHeldMs / modeHoldMs).clamp(0.0, 1.0)
                    : null,
                minHeight: 6,
                borderRadius: BorderRadius.circular(3),
                backgroundColor: Colors.white.withValues(alpha: 0.1),
              ),
            ],
            const Divider(color: Colors.white12, height: 24),
            Text('Sincronizar sombra',
                style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 4),
            const Text(
              'Declare o SP lido no painel quando a sombra está desconhecida. Não aciona relés.',
              style: TextStyle(fontSize: 12, color: Colors.grey),
            ),
            const SizedBox(height: 10),
            Row(children: [
              Expanded(
                child: TextField(
                  controller: _syncController,
                  keyboardType:
                      const TextInputType.numberWithOptions(decimal: true),
                  decoration: InputDecoration(
                    labelText: 'SP lido no painel (°C)',
                    errorText: _syncError,
                    prefixIcon: const Icon(Icons.sync),
                  ),
                ),
              ),
              const SizedBox(width: 10),
              ElevatedButton(
                onPressed: _sync,
                style: ElevatedButton.styleFrom(
                  backgroundColor: AppTheme.primaryCyan,
                  foregroundColor: Colors.black,
                ),
                child: const Text('Sincronizar'),
              ),
            ]),
          ],
        ),
      ),
    );
  }

  Widget _row(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 3),
        child: Row(mainAxisAlignment: MainAxisAlignment.spaceBetween, children: [
          Text(label, style: const TextStyle(color: Colors.grey, fontSize: 13)),
          Text(value,
              style: const TextStyle(
                  fontWeight: FontWeight.w600, fontSize: 13)),
        ]),
      );
}
