import 'package:flutter/material.dart';
import '../models/distance_sensor_state.dart';

class DistanceSensorCard extends StatelessWidget {
  final DistanceSensorState state;
  final double? referenceThresholdMm;

  const DistanceSensorCard({
    super.key,
    required this.state,
    this.referenceThresholdMm,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    Color statusColor;
    IconData statusIcon;
    String statusTitle;
    String statusSubtitle;

    if (state.isDisabled) {
      statusColor = Colors.grey;
      statusIcon = Icons.sensors_off_outlined;
      statusTitle = "DISABLED ON HUB";
      statusSubtitle = "Routing and presence monitoring are switched off on the Hub.";
    } else if (state.isDisconnected) {
      statusColor = Colors.amber.shade800;
      statusIcon = Icons.wifi_tethering_off_outlined;
      statusTitle = "SENSOR DISCONNECTED";
      statusSubtitle = "External sensor node is offline or out of range. No data received within 5s.";
    } else {
      statusColor = Colors.green.shade700;
      statusIcon = Icons.radar;
      statusTitle = "CONNECTED & ACTIVE";
      statusSubtitle = "External Wi-Fi node is streaming valid distance readings.";
    }

    // Normalized level calculation (0 to 500 mm scale for visualization)
    final double levelPercent = state.isConnectedAndActive
        ? (state.distanceMm / 500.0).clamp(0.0, 1.0)
        : 0.0;

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
                        "Distance & Level Sensor",
                        style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.bold),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        "Wi-Fi Node • Foam & Liquid Level",
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

            // Distance Readings & Threshold Display
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              crossAxisAlignment: CrossAxisAlignment.baseline,
              textBaseline: TextBaseline.alphabetic,
              children: [
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text("MEASURED DISTANCE", style: theme.textTheme.labelSmall?.copyWith(letterSpacing: 1.1)),
                    const SizedBox(height: 2),
                    Row(
                      crossAxisAlignment: CrossAxisAlignment.baseline,
                      textBaseline: TextBaseline.alphabetic,
                      children: [
                        Text(
                          state.formattedDistance,
                          style: theme.textTheme.headlineMedium?.copyWith(
                            fontWeight: FontWeight.bold,
                            color: state.isConnectedAndActive ? statusColor : theme.colorScheme.onSurface.withValues(alpha: 0.4),
                          ),
                        ),
                        if (state.isConnectedAndActive) ...[
                          const SizedBox(width: 8),
                          Text(
                            "(${state.formattedDistanceCm})",
                            style: theme.textTheme.titleSmall?.copyWith(color: theme.colorScheme.outline),
                          ),
                        ],
                      ],
                    ),
                  ],
                ),
                if (referenceThresholdMm != null)
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.end,
                    children: [
                      Text("REFERENCE THRESHOLD", style: theme.textTheme.labelSmall?.copyWith(letterSpacing: 1.1)),
                      const SizedBox(height: 2),
                      Text(
                        "${referenceThresholdMm!.toStringAsFixed(1)} mm",
                        style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w600),
                      ),
                    ],
                  ),
              ],
            ),

            const SizedBox(height: 12),

            // Visual Progress / Distance bar
            ClipRRect(
              borderRadius: BorderRadius.circular(4),
              child: LinearProgressIndicator(
                value: levelPercent,
                minHeight: 8,
                backgroundColor: theme.colorScheme.surfaceContainerHighest,
                valueColor: AlwaysStoppedAnimation<Color>(
                  state.isConnectedAndActive ? Colors.indigo : Colors.grey,
                ),
              ),
            ),

            const SizedBox(height: 12),

            // Status message footer
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
              decoration: BoxDecoration(
                color: statusColor.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                children: [
                  Icon(
                    state.isConnectedAndActive ? Icons.check_circle_outline : Icons.info_outline,
                    size: 16,
                    color: statusColor,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      statusSubtitle,
                      style: theme.textTheme.bodySmall?.copyWith(color: statusColor, fontWeight: FontWeight.w500),
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
