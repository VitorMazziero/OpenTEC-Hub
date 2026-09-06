import 'package:flutter/material.dart';
import '../models/biomass_sensor_state.dart';

class BiomassSensorCard extends StatelessWidget {
  final BiomassSensorState state;
  final VoidCallback? onStartAcquisition;
  final VoidCallback? onStopAcquisition;
  final VoidCallback? onZeroBlank;

  const BiomassSensorCard({
    super.key,
    required this.state,
    this.onStartAcquisition,
    this.onStopAcquisition,
    this.onZeroBlank,
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
      statusSubtitle = "External biomass node is offline or out of range. No push data received.";
    } else if (state.isAcquiring) {
      statusColor = Colors.teal.shade700;
      statusIcon = Icons.grain;
      statusTitle = "ACQUIRING (ACTIVE)";
      statusSubtitle = "Optical density measurement is active. Streaming absorbance samples.";
    } else {
      statusColor = Colors.blue.shade700;
      statusIcon = Icons.pause_circle_outline;
      statusTitle = "CONNECTED (IDLE)";
      statusSubtitle = "Sensor node is online on SoftAP. Acquisition is paused in standby.";
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
                        "Biomass Optical Density",
                        style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.bold),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        "Optical Absorbance • NIR / Turbidity",
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

            // Absorbance Reading Display
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              crossAxisAlignment: CrossAxisAlignment.baseline,
              textBaseline: TextBaseline.alphabetic,
              children: [
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text("OPTICAL DENSITY / ABSORBANCE", style: theme.textTheme.labelSmall?.copyWith(letterSpacing: 1.1)),
                    const SizedBox(height: 2),
                    Row(
                      crossAxisAlignment: CrossAxisAlignment.baseline,
                      textBaseline: TextBaseline.alphabetic,
                      children: [
                        Text(
                          state.formattedAbsorbance,
                          style: theme.textTheme.headlineMedium?.copyWith(
                            fontWeight: FontWeight.bold,
                            color: state.isAcquiring ? statusColor : theme.colorScheme.onSurface.withValues(alpha: 0.5),
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

            // Secondary metrics row: Raw Counts, Integration Time, Emitter PWM
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
                    label: "Raw Counts",
                    value: state.isAcquiring ? "${state.rawCounts}" : "--",
                  ),
                  _buildSubMetric(
                    context,
                    label: "Integration",
                    value: state.isAcquiring ? "${state.integrationTimeMs} ms" : "--",
                  ),
                  _buildSubMetric(
                    context,
                    label: "Emitter PWM",
                    value: state.isAcquiring ? "${state.pwmPercent.toStringAsFixed(1)}%" : "--",
                  ),
                ],
              ),
            ),

            const SizedBox(height: 12),

            // Status message info box
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
              decoration: BoxDecoration(
                color: statusColor.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                children: [
                  Icon(
                    state.isAcquiring || state.isIdle ? Icons.check_circle_outline : Icons.info_outline,
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

            // Quick action buttons if connected
            if (state.online && state.commEnabled) ...[
              const SizedBox(height: 12),
              Row(
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  if (onZeroBlank != null)
                    OutlinedButton.icon(
                      onPressed: onZeroBlank,
                      icon: const Icon(Icons.exposure_zero, size: 16),
                      label: const Text("Zero Blank"),
                    ),
                  const SizedBox(width: 8),
                  if (state.isAcquiring && onStopAcquisition != null)
                    FilledButton.tonalIcon(
                      onPressed: onStopAcquisition,
                      icon: const Icon(Icons.pause, size: 16),
                      label: const Text("Pause"),
                      style: FilledButton.styleFrom(
                        foregroundColor: Colors.orange.shade800,
                        backgroundColor: Colors.orange.shade50,
                      ),
                    )
                  else if (onStartAcquisition != null)
                    FilledButton.icon(
                      onPressed: onStartAcquisition,
                      icon: const Icon(Icons.play_arrow, size: 16),
                      label: const Text("Start Measurement"),
                      style: FilledButton.styleFrom(backgroundColor: Colors.teal.shade700),
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
  }) {
    final theme = Theme.of(context);
    return Column(
      children: [
        Text(label, style: theme.textTheme.labelSmall?.copyWith(color: theme.colorScheme.outline)),
        const SizedBox(height: 2),
        Text(value, style: theme.textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.bold)),
      ],
    );
  }
}
