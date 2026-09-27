import 'package:flutter/material.dart';

/// Which actuator the Hub uses for a variable (original controller board or the
/// alternative node). Shown on the card header, always visible.
class RouteSelector extends StatelessWidget {
  final bool alternative;
  final String boardLabel;
  final String alternativeLabel;
  final IconData alternativeIcon;
  final ValueChanged<bool>? onChanged;
  final String? note;

  const RouteSelector({
    super.key,
    required this.alternative,
    required this.boardLabel,
    required this.alternativeLabel,
    required this.alternativeIcon,
    required this.onChanged,
    this.note,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SegmentedButton<bool>(
          segments: [
            ButtonSegment(value: false, icon: const Icon(Icons.memory, size: 18), label: Text(boardLabel)),
            ButtonSegment(value: true, icon: Icon(alternativeIcon, size: 18), label: Text(alternativeLabel)),
          ],
          selected: {alternative},
          showSelectedIcon: false,
          onSelectionChanged: onChanged == null ? null : (s) => onChanged!(s.first),
        ),
        if (note != null)
          Padding(
            padding: const EdgeInsets.only(top: 4),
            child: Text(note!, style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.outline)),
          ),
      ],
    );
  }
}

/// Asks before switching a control route: the Hub stops the actuator on the switch.
Future<bool> confirmRouteChange(BuildContext context, {required String title, required String message}) async {
  final ok = await showDialog<bool>(
    context: context,
    builder: (ctx) => AlertDialog(
      icon: const Icon(Icons.alt_route),
      title: Text(title),
      content: Text(message),
      actions: [
        TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text("Cancelar")),
        FilledButton(onPressed: () => Navigator.pop(ctx, true), child: const Text("Trocar via")),
      ],
    ),
  );
  return ok == true;
}
