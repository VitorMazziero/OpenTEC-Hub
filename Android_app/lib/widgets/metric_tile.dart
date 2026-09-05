import 'package:flutter/material.dart';

class MetricTile extends StatelessWidget {
  final String title;
  final String value;
  final String? unit;
  final String? subtext;
  final IconData icon;
  final Color accentColor;
  final bool isActive;

  const MetricTile({
    super.key,
    required this.title,
    required this.value,
    this.unit,
    this.subtext,
    required this.icon,
    required this.accentColor,
    this.isActive = true,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Card(
      elevation: 1.5,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: BorderSide(
          color: isActive ? accentColor.withValues(alpha: 0.35) : theme.dividerColor.withValues(alpha: 0.2),
          width: 1.2,
        ),
      ),
      child: Padding(
        padding: const EdgeInsets.all(12.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Row(
              children: [
                CircleAvatar(
                  radius: 16,
                  backgroundColor: accentColor.withValues(alpha: 0.12),
                  child: Icon(icon, size: 18, color: accentColor),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    title,
                    style: theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w600),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                if (!isActive)
                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                    decoration: BoxDecoration(
                      color: Colors.grey.shade200,
                      borderRadius: BorderRadius.circular(4),
                    ),
                    child: Text("OFF", style: theme.textTheme.labelSmall?.copyWith(color: Colors.grey.shade700)),
                  ),
              ],
            ),
            const SizedBox(height: 8),
            Row(
              crossAxisAlignment: CrossAxisAlignment.baseline,
              textBaseline: TextBaseline.alphabetic,
              children: [
                Text(
                  value,
                  style: theme.textTheme.headlineSmall?.copyWith(
                    fontWeight: FontWeight.bold,
                    color: isActive ? theme.colorScheme.onSurface : theme.colorScheme.onSurface.withValues(alpha: 0.4),
                  ),
                ),
                if (unit != null) ...[
                  const SizedBox(width: 4),
                  Text(
                    unit!,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: theme.colorScheme.onSurfaceVariant,
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                ],
              ],
            ),
            if (subtext != null) ...[
              const SizedBox(height: 4),
              Text(
                subtext!,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.outline,
                ),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
            ],
          ],
        ),
      ),
    );
  }
}
