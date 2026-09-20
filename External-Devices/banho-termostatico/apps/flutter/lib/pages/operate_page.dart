import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';
import '../widgets/setpoint_card.dart';
import '../widgets/sequence_card.dart';
import '../widgets/trace_card.dart';

/// Aba Operação (padrão ao abrir). Plano §4.1.
class OperatePage extends StatelessWidget {
  const OperatePage({super.key});

  @override
  Widget build(BuildContext context) {
    final s = context.watch<BathService>();
    final st = s.statusData;
    return ListView(
      padding: const EdgeInsets.all(12),
      children: [
        SetpointCard(service: s),
        const SizedBox(height: 12),
        SequenceCard(service: s),
        const SizedBox(height: 12),
        // Leituras do painel a partir do /status.
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(children: [
                  Icon(Icons.thermostat_auto,
                      color: st.displayAlive ? AppTheme.okGreen : Colors.grey,
                      size: 20),
                  const SizedBox(width: 8),
                  Text('Leituras do painel',
                      style: Theme.of(context).textTheme.titleLarge),
                  const Spacer(),
                  if (!st.displayAlive)
                    const Text('sem sinal',
                        style: TextStyle(color: Colors.grey, fontSize: 12)),
                ]),
                const SizedBox(height: 8),
                _row('PV',
                    st.displayPv != null ? '${st.displayPv!.toStringAsFixed(2)} °C' : '—'),
                _row('SP',
                    st.displaySp != null ? '${st.displaySp!.toStringAsFixed(2)} °C' : '—'),
                if (st.displayText.isNotEmpty)
                  _row('Texto', st.displayText),
              ],
            ),
          ),
        ),
        const SizedBox(height: 12),
        TraceCard(service: s),
        const SizedBox(height: 8),
      ],
    );
  }

  Widget _row(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 3),
        child: Row(mainAxisAlignment: MainAxisAlignment.spaceBetween, children: [
          Text(label, style: const TextStyle(color: Colors.grey, fontSize: 13)),
          Text(value,
              style:
                  const TextStyle(fontWeight: FontWeight.w600, fontSize: 14)),
        ]),
      );
}
