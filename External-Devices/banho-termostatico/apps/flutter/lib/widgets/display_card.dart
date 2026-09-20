import 'package:flutter/material.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';

/// Leitura ao vivo do display do C404 (`GET /display`, 500 ms). Texto dos 8
/// dígitos, PV/SP interpretados, sp_live vs sp, quadros (plano §4.3).
class DisplayCard extends StatelessWidget {
  final BathService service;
  const DisplayCard({super.key, required this.service});

  @override
  Widget build(BuildContext context) {
    final d = service.display;
    final alive = d.alive;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              Icon(Icons.tv, color: alive ? AppTheme.okGreen : Colors.grey,
                  size: 20),
              const SizedBox(width: 8),
              Text('Display do painel',
                  style: Theme.of(context).textTheme.titleLarge),
              const Spacer(),
              if (!alive)
                const Text('sem sinal',
                    style: TextStyle(color: Colors.grey, fontSize: 12)),
            ]),
            const SizedBox(height: 12),
            Container(
              width: double.infinity,
              padding: const EdgeInsets.symmetric(vertical: 14),
              alignment: Alignment.center,
              decoration: BoxDecoration(
                color: Colors.black,
                borderRadius: BorderRadius.circular(10),
              ),
              child: Text(
                d.text.isNotEmpty ? d.text : '········',
                style: TextStyle(
                  fontFamily: 'monospace',
                  fontSize: 30,
                  letterSpacing: 4,
                  color: alive ? AppTheme.okGreen : Colors.grey.shade700,
                ),
              ),
            ),
            const SizedBox(height: 12),
            _row('PV', _fmt(d.pv)),
            _row('SP', _fmt(d.sp)),
            _row('SP ao vivo', _fmt(d.spLive)),
            _row('Quadros', '${d.frames} (vivos: ${d.liveFrames})'),
            if (d.raw.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Text('raw: ${d.raw.join(' ')}',
                    style: TextStyle(
                        fontFamily: 'monospace',
                        fontSize: 11,
                        color: Colors.grey.shade500)),
              ),
          ],
        ),
      ),
    );
  }

  String _fmt(double? v) => v != null ? v.toStringAsFixed(2) : '—';

  Widget _row(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 3),
        child: Row(mainAxisAlignment: MainAxisAlignment.spaceBetween, children: [
          Text(label, style: const TextStyle(color: Colors.grey, fontSize: 13)),
          Text(value,
              style: const TextStyle(
                  fontWeight: FontWeight.w600, fontSize: 14)),
        ]),
      );
}
