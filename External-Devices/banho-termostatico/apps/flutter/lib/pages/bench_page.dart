import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../services/bath_service.dart';
import '../theme/app_theme.dart';
import '../widgets/raw_keys_row.dart';
import '../widgets/display_card.dart';
import '../widgets/result_snackbar.dart';

/// Aba Bancada: teclas cruas, hold, home, display ao vivo (500 ms). Plano §4.3.
class BenchPage extends StatefulWidget {
  const BenchPage({super.key});

  @override
  State<BenchPage> createState() => _BenchPageState();
}

class _BenchPageState extends State<BenchPage> {
  final _homeController = TextEditingController();

  @override
  void dispose() {
    _homeController.dispose();
    super.dispose();
  }

  Future<void> _home(BathService s) async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: AppTheme.darkCardElevated,
        title: const Text('Home'),
        content: const Text(
            'O home pressiona ▼ até saturar o SP em sp_min e depois sobe até o alvo. '
            'Passa pelo SP mínimo e pode levar minutos. Continuar?'),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx, false),
              child: const Text('Cancelar')),
          ElevatedButton(
              onPressed: () => Navigator.pop(ctx, true),
              child: const Text('Fazer home')),
        ],
      ),
    );
    if (ok != true || !mounted) return;
    final text = _homeController.text.trim().replaceAll(',', '.');
    final sp = double.tryParse(text);
    final r = await s.home(setpoint: sp);
    if (mounted) showCommandResult(context, r);
  }

  @override
  Widget build(BuildContext context) {
    final s = context.watch<BathService>();
    // Mantém o poll de /display ativo enquanto esta aba está montada.
    return _DisplayPollScope(
      service: s,
      child: ListView(
        padding: const EdgeInsets.all(12),
        children: [
          RawKeysRow(service: s),
          const SizedBox(height: 12),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('Home', style: Theme.of(context).textTheme.titleLarge),
                  const SizedBox(height: 4),
                  const Text(
                    'Torna o SP conhecido sem leitura, útil se o C404 travar em in.L.',
                    style: TextStyle(fontSize: 12, color: Colors.grey),
                  ),
                  const SizedBox(height: 10),
                  Row(children: [
                    Expanded(
                      child: TextField(
                        controller: _homeController,
                        keyboardType: const TextInputType.numberWithOptions(
                            decimal: true),
                        decoration: const InputDecoration(
                          labelText: 'Alvo após home (°C, opcional)',
                          prefixIcon: Icon(Icons.home),
                        ),
                      ),
                    ),
                    const SizedBox(width: 10),
                    ElevatedButton(
                      onPressed: () => _home(s),
                      style: ElevatedButton.styleFrom(
                        backgroundColor: AppTheme.accentAmber,
                        foregroundColor: Colors.black,
                      ),
                      child: const Text('Home'),
                    ),
                  ]),
                ],
              ),
            ),
          ),
          const SizedBox(height: 12),
          DisplayCard(service: s),
          const SizedBox(height: 12),
          const Card(
            color: AppTheme.darkCardElevated,
            child: Padding(
              padding: EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('Atalhos dos gates',
                      style: TextStyle(fontWeight: FontWeight.bold)),
                  SizedBox(height: 6),
                  Text('• G3b: manter ▲ por 2/5/10 s e anotar ms + taxa.',
                      style: TextStyle(fontSize: 12, color: Colors.grey)),
                  Text('• G7b: observe o painel com ▲+▼ juntas por 5 s.',
                      style: TextStyle(fontSize: 12, color: Colors.grey)),
                ],
              ),
            ),
          ),
          const SizedBox(height: 8),
        ],
      ),
    );
  }
}

/// Liga o poll de /display ao ciclo de vida da aba.
class _DisplayPollScope extends StatefulWidget {
  final BathService service;
  final Widget child;
  const _DisplayPollScope({required this.service, required this.child});

  @override
  State<_DisplayPollScope> createState() => _DisplayPollScopeState();
}

class _DisplayPollScopeState extends State<_DisplayPollScope> {
  @override
  void initState() {
    super.initState();
    widget.service.setDisplayActive(true);
  }

  @override
  void dispose() {
    widget.service.setDisplayActive(false);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
