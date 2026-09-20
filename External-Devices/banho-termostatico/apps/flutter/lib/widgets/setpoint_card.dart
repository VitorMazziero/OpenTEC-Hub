import 'package:flutter/material.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';
import 'result_snackbar.dart';

/// Sombra grande, "desconhecido", alvo, campo + Enviar, ±0,1/±0,5/±1,0.
class SetpointCard extends StatefulWidget {
  final BathService service;
  const SetpointCard({super.key, required this.service});

  @override
  State<SetpointCard> createState() => _SetpointCardState();
}

class _SetpointCardState extends State<SetpointCard> {
  final _controller = TextEditingController();
  String? _fieldError;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  BathService get s => widget.service;

  Future<void> _send() async {
    final text = _controller.text.trim().replaceAll(',', '.');
    final value = double.tryParse(text);
    if (value == null) {
      setState(() => _fieldError = 'Valor inválido');
      return;
    }
    final min = s.config.spMin, max = s.config.spMax;
    if (min != null && max != null && (value < min || value > max)) {
      setState(() => _fieldError =
          'Fora da faixa ${min.toStringAsFixed(1)}–${max.toStringAsFixed(1)} °C');
      return;
    }
    setState(() => _fieldError = null);
    final r = await s.setSetpoint(value);
    if (mounted) showCommandResult(context, r);
  }

  Future<void> _delta(double d) async {
    final r = await s.adjustDelta(d);
    if (mounted) showCommandResult(context, r);
  }

  @override
  Widget build(BuildContext context) {
    final st = s.statusData;
    final known = st.spKnown;
    final busy = st.seqBusy;
    // Bloqueia Enviar/ajustes até sync_sp ou home quando o SP é desconhecido.
    final canCommand = known && !busy;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Text(
                  st.spShadow != null
                      ? st.spShadow!.toStringAsFixed(1)
                      : '--.-',
                  style: const TextStyle(
                      fontSize: 52, fontWeight: FontWeight.bold, height: 1),
                ),
                const Padding(
                  padding: EdgeInsets.only(bottom: 8, left: 4),
                  child: Text('°C sombra',
                      style: TextStyle(fontSize: 14, color: Colors.grey)),
                ),
                const Spacer(),
                if (st.spTarget != null)
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.end,
                    children: [
                      const Text('alvo',
                          style: TextStyle(fontSize: 12, color: Colors.grey)),
                      Text('${st.spTarget!.toStringAsFixed(1)} °C',
                          style: const TextStyle(
                              fontSize: 18, fontWeight: FontWeight.w600)),
                    ],
                  ),
              ],
            ),
            if (!known) ...[
              const SizedBox(height: 10),
              Container(
                width: double.infinity,
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: AppTheme.stopRed.withValues(alpha: 0.15),
                  borderRadius: BorderRadius.circular(10),
                  border:
                      Border.all(color: AppTheme.stopRed.withValues(alpha: 0.4)),
                ),
                child: const Row(children: [
                  Icon(Icons.help_outline, color: AppTheme.stopRed, size: 18),
                  SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      'SETPOINT DESCONHECIDO — sincronize (aba Modos) ou faça home',
                      style: TextStyle(
                          color: AppTheme.stopRed,
                          fontSize: 12,
                          fontWeight: FontWeight.w600),
                    ),
                  ),
                ]),
              ),
            ],
            const SizedBox(height: 16),
            Row(children: [
              Expanded(
                child: TextField(
                  controller: _controller,
                  enabled: canCommand,
                  keyboardType:
                      const TextInputType.numberWithOptions(decimal: true),
                  decoration: InputDecoration(
                    labelText: 'Setpoint alvo (°C)',
                    hintText: 'ex: 31,5',
                    errorText: _fieldError,
                    prefixIcon: const Icon(Icons.thermostat),
                  ),
                  onSubmitted: (_) => canCommand ? _send() : null,
                ),
              ),
              const SizedBox(width: 10),
              ElevatedButton(
                onPressed: canCommand ? _send : null,
                style: ElevatedButton.styleFrom(
                  backgroundColor: AppTheme.primaryCyan,
                  foregroundColor: Colors.black,
                ),
                child: const Text('Enviar'),
              ),
            ]),
            const SizedBox(height: 12),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final d in [-1.0, -0.5, -0.1, 0.1, 0.5, 1.0])
                  OutlinedButton(
                    onPressed: canCommand ? () => _delta(d) : null,
                    child: Text('${d > 0 ? '+' : ''}${d.toStringAsFixed(1)}'),
                  ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
