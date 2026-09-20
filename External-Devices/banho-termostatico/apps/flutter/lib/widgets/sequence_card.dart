import 'package:flutter/material.dart';
import '../models/bath_status.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';

/// Estado/fase/erro da sequência; barra de progresso de toques; hold: ms e taxa.
class SequenceCard extends StatelessWidget {
  final BathService service;
  const SequenceCard({super.key, required this.service});

  @override
  Widget build(BuildContext context) {
    final st = service.statusData;
    final busy = st.seqBusy;

    Color stateColor;
    switch (st.seqState) {
      case SeqState.error:
        stateColor = AppTheme.stopRed;
        break;
      case SeqState.running:
      case SeqState.settling:
        stateColor = AppTheme.accentAmber;
        break;
      case SeqState.done:
        stateColor = AppTheme.okGreen;
        break;
      default:
        stateColor = Colors.grey;
    }

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              Icon(Icons.timeline, color: stateColor, size: 20),
              const SizedBox(width: 8),
              Text('Sequência',
                  style: Theme.of(context).textTheme.titleLarge),
              const Spacer(),
              Container(
                padding:
                    const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                decoration: BoxDecoration(
                  color: stateColor.withValues(alpha: 0.15),
                  borderRadius: BorderRadius.circular(12),
                ),
                child: Text(
                  busy ? 'em execução' : st.seqStateLabel,
                  style: TextStyle(
                      color: stateColor, fontWeight: FontWeight.w600),
                ),
              ),
            ]),
            const SizedBox(height: 8),
            _row('Tipo', st.seqKindLabel),
            if (st.seqPhaseLabel.isNotEmpty) _row('Fase', st.seqPhaseLabel),
            if (st.seqPhase == SeqPhase.presses && st.pressesTotal > 0) ...[
              const SizedBox(height: 8),
              LinearProgressIndicator(
                value: st.pressesTotal > 0
                    ? (st.pressesDone / st.pressesTotal).clamp(0.0, 1.0)
                    : null,
                minHeight: 8,
                borderRadius: BorderRadius.circular(4),
                backgroundColor: Colors.white.withValues(alpha: 0.1),
              ),
              const SizedBox(height: 4),
              Text(
                '${st.pressesDone}/${st.pressesTotal} toques'
                '${st.pressesUnconfirmed > 0 ? ' · ${st.pressesUnconfirmed} não confirmados' : ''}',
                style: const TextStyle(fontSize: 12, color: Colors.grey),
              ),
            ],
            if (st.holdMs > 0) ...[
              const SizedBox(height: 8),
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: AppTheme.accentAmber.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Text(
                  'Tecla mantida há ${st.holdMs} ms · '
                  '${st.holdRate.toStringAsFixed(1)} toques/s'
                  '${st.holdRounds > 0 ? ' · rodada ${st.holdRounds}' : ''}',
                  style: const TextStyle(
                      color: AppTheme.accentAmber, fontSize: 13),
                ),
              ),
            ],
            if (st.seqError.isNotEmpty) ...[
              const SizedBox(height: 8),
              Row(children: [
                const Icon(Icons.error_outline,
                    color: AppTheme.stopRed, size: 18),
                const SizedBox(width: 6),
                // erro literal do nó
                Text(st.seqError,
                    style: const TextStyle(color: AppTheme.stopRed)),
              ]),
            ],
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
