import 'package:flutter/material.dart';

class ControlSectionCard extends StatelessWidget {
  final String title;
  final IconData icon;
  final Color accentColor;
  final bool? isEnabled;
  final ValueChanged<bool>? onToggle;
  final List<Widget> children;
  final VoidCallback onApply;
  final String applyButtonLabel;
  final bool isBusy;

  const ControlSectionCard({
    super.key,
    required this.title,
    required this.icon,
    required this.accentColor,
    this.isEnabled,
    this.onToggle,
    required this.children,
    required this.onApply,
    this.applyButtonLabel = "Apply",
    this.isBusy = false,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final active = isEnabled ?? true;

    return Card(
      elevation: 1.5,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(14),
        side: BorderSide(
          color: active ? accentColor.withValues(alpha: 0.4) : theme.dividerColor.withValues(alpha: 0.3),
          width: 1.3,
        ),
      ),
      child: Padding(
        padding: const EdgeInsets.all(14.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // Header
            Row(
              children: [
                CircleAvatar(
                  radius: 16,
                  backgroundColor: accentColor.withValues(alpha: 0.12),
                  child: Icon(icon, size: 18, color: accentColor),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    title,
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.bold,
                      color: active ? theme.colorScheme.onSurface : theme.colorScheme.onSurface.withValues(alpha: 0.5),
                    ),
                  ),
                ),
                if (isEnabled != null && onToggle != null)
                  Switch(
                    value: isEnabled!,
                    onChanged: isBusy ? null : onToggle,
                    activeThumbColor: accentColor,
                    materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
                  ),
              ],
            ),
            const Divider(height: 20),

            // Content
            ...children,

            const SizedBox(height: 12),

            // Apply button
            Align(
              alignment: Alignment.centerRight,
              child: FilledButton.tonal(
                onPressed: isBusy ? null : onApply,
                child: isBusy
                    ? const SizedBox(
                        width: 18,
                        height: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          const Icon(Icons.send, size: 16),
                          const SizedBox(width: 6),
                          Text(applyButtonLabel),
                        ],
                      ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
