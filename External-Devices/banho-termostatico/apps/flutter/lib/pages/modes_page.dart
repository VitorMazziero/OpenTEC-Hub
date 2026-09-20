import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../services/bath_service.dart';
import '../widgets/mode_card.dart';

/// Aba Modos. Plano §4.2.
class ModesPage extends StatelessWidget {
  const ModesPage({super.key});

  @override
  Widget build(BuildContext context) {
    final s = context.watch<BathService>();
    return ListView(
      padding: const EdgeInsets.all(12),
      children: [
        ModeCard(service: s),
        const SizedBox(height: 8),
      ],
    );
  }
}
