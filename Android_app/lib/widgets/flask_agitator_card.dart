import 'package:flutter/material.dart';
import '../models/flask_agitator_state.dart';

class FlaskAgitatorCard extends StatelessWidget {
  final FlaskAgitatorState state;
  final VoidCallback? onStart;
  final VoidCallback? onStop;

  const FlaskAgitatorCard({
    super.key,
    required this.state,
    this.onStart,
    this.onStop,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    Color statusColor;
    IconData statusIcon;
    String statusTitle;
    String statusSubtitle;

    if (state.isDisabled) {
      statusColor = Colors.grey.shade600;
      statusIcon = Icons.sensors_off_outlined;
      statusTitle = "DISABLED ON HUB";
      statusSubtitle = "External flask agitator node is offline or out of range. No push data received.";
    } else if (state.isSpinning) {
      statusColor = Colors.deepPurple.shade700;
      statusIcon = Icons.cyclone;
      statusTitle = "AGITATING (${state.speedPercent.toStringAsFixed(0)}%)";
      statusSubtitle = "Flask magnetic stirrer is active. Speed controlled via Hub telemetry.";
    } else {
      statusColor = Colors.blue.shade700;
      statusIcon = Icons.pause_circle_outline;
      statusTitle = "CONNECTED (IDLE)";
      statusSubtitle = "Agitator node is online on SoftAP. Stirrer is currently stopped.";
    }

    return Card(
      elevation: 2,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(16),
        side: BorderSide(color: statusColor.withValues(alpha: 0.6), width: 1.5),
      ),
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Header Row
            Row(
              children: [
                CircleAvatar(
                  radius: 18,
                  backgroundColor: statusColor.withValues(alpha: 0.15),
                  child: Icon(statusIcon, color: statusColor, size: 22),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        "Flask Agitator & Stirrer",
                        style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.bold),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        "Wi-Fi Node • Secondary Flask / Vessel Stirrer",
                        style: theme.textTheme.labelSmall?.copyWith(color: theme.colorScheme.outline),
                      ),
                    ],
                  ),
                ),
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                  decoration: BoxDecoration(
                    color: statusColor.withValues(alpha: 0.15),
                    borderRadius: BorderRadius.circular(12),
                    border: Border.all(color: statusColor, width: 1),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Container(
                        width: 8,
                        height: 8,
                        decoration: BoxDecoration(color: statusColor, shape: BoxShape.circle),
                      ),
                      const SizedBox(width: 6),
                      Text(
                        statusTitle,
                        style: theme.textTheme.labelSmall?.copyWith(
                          color: statusColor,
                          fontWeight: FontWeight.bold,
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
            const Divider(height: 22),

            // Speed Display
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              crossAxisAlignment: CrossAxisAlignment.baseline,
              textBaseline: TextBaseline.alphabetic,
              children: [
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text("ROTATION SPEED", style: theme.textTheme.labelSmall?.copyWith(letterSpacing: 1.1)),
                    const SizedBox(height: 2),
                    Row(
                      crossAxisAlignment: CrossAxisAlignment.baseline,
                      textBaseline: TextBaseline.alphabetic,
                      children: [
                        Text(
                          state.formattedSpeed,
                          style: theme.textTheme.headlineMedium?.copyWith(
                            fontWeight: FontWeight.bold,
                            color: state.isConnectedAndActive ? statusColor : theme.colorScheme.onSurface.withValues(alpha: 0.5),
                          ),
                        ),
                      ],
                    ),
                  ],
                ),
                if (state.commandPending)
                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                    decoration: BoxDecoration(
                      color: Colors.orange.shade50,
                      borderRadius: BorderRadius.circular(8),
                      border: Border.all(color: Colors.orange.shade300),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        const SizedBox(
                          width: 10,
                          height: 10,
                          child: CircularProgressIndicator(strokeWidth: 2, color: Colors.orange),
                        ),
                        const SizedBox(width: 6),
                        Text(
                          "Cmd Pending",
                          style: TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: Colors.orange.shade900),
                        ),
                      ],
                    ),
                  ),
              ],
            ),

            const SizedBox(height: 14),

            // Secondary metrics row: Direction, Potentiometer, Actuation Source
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: theme.colorScheme.surfaceContainerHighest.withValues(alpha: 0.35),
                borderRadius: BorderRadius.circular(10),
              ),
              child: Row(
                mainAxisAlignment: MainAxisAlignment.spaceAround,
                children: [
                  _buildSubMetric(
                    context,
                    label: "Direction",
                    value: state.isConnectedAndActive ? state.directionLabel : "--",
                  ),
                  _buildSubMetric(
                    context,
                    label: "Potentiometer",
                    value: state.isConnectedAndActive ? state.potLabel : "--",
                    valueColor: state.potActive ? Colors.orange.shade800 : null,
                  ),
                  _buildSubMetric(
                    context,
                    label: "Actuation Source",
                    value: state.isConnectedAndActive && state.source.isNotEmpty
                        ? state.source.toUpperCase()
                        : "--",
                  ),
                ],
              ),
            ),

            const SizedBox(height: 12),

            // Status message
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
              decoration: BoxDecoration(
                color: statusColor.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                children: [
                  Icon(Icons.info_outline, size: 14, color: statusColor),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      statusSubtitle,
                      style: theme.textTheme.bodySmall?.copyWith(color: statusColor, fontWeight: FontWeight.w500),
                    ),
                  ),
                ],
              ),
            ),

            // Quick actions
            if (state.isConnectedAndActive) ...[
              const SizedBox(height: 12),
              Row(
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  if (state.isSpinning && onStop != null)
                    FilledButton.tonalIcon(
                      onPressed: onStop,
                      icon: const Icon(Icons.stop, size: 16),
                      label: const Text("Stop Agitator"),
                      style: FilledButton.styleFrom(
                        foregroundColor: Colors.red.shade800,
                        backgroundColor: Colors.red.shade50,
                      ),
                    )
                  else if (!state.isSpinning && onStart != null)
                    FilledButton.icon(
                      onPressed: onStart,
                      icon: const Icon(Icons.play_arrow, size: 16),
                      label: const Text("Start Agitator"),
                      style: FilledButton.styleFrom(
                        backgroundColor: Colors.deepPurple.shade700,
                      ),
                    ),
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }

  Widget _buildSubMetric(
    BuildContext context, {
    required String label,
    required String value,
    Color? valueColor,
  }) {
    final theme = Theme.of(context);
    return Column(
      children: [
        Text(label, style: theme.textTheme.labelSmall?.copyWith(color: theme.colorScheme.outline)),
        const SizedBox(height: 2),
        Text(
          value,
          style: theme.textTheme.bodyMedium?.copyWith(
            fontWeight: FontWeight.bold,
            color: valueColor,
          ),
        ),
      ],
    );
  }
}
