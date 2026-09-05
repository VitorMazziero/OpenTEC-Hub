import 'package:flutter/material.dart';
import '../models/peristaltic_pump_state.dart';

class PeristalticPumpCard extends StatelessWidget {
  final PeristalticPumpState state;
  final VoidCallback? onStop;

  const PeristalticPumpCard({
    super.key,
    required this.state,
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
      statusSubtitle = "Pump command routing disabled in Hub settings (pumpComm=0).";
    } else if (state.isDisconnected) {
      statusColor = Colors.amber.shade800;
      statusIcon = Icons.wifi_tethering_off_outlined;
      statusTitle = "PUMP DISCONNECTED";
      statusSubtitle = "External peristaltic pump node is offline or timed out (>4s).";
    } else if (state.isDosing) {
      statusColor = Colors.orange.shade800;
      statusIcon = Icons.water_drop;
      statusTitle = "DOSING (${state.modeLabel})";
      statusSubtitle = "Actively dosing liquid into vessel according to programmed profile.";
    } else if (state.isWaitingWindow) {
      statusColor = Colors.amber.shade700;
      statusIcon = Icons.schedule;
      statusTitle = "SCHEDULED (WAITING)";
      statusSubtitle = "Profile is active but waiting for start window (time < init_t).";
    } else {
      statusColor = Colors.blue.shade700;
      statusIcon = Icons.pause_circle_outline;
      statusTitle = "CONNECTED (STANDBY)";
      statusSubtitle = "Pump node is online on SoftAP. Profile is currently stopped.";
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
                      Row(
                        children: [
                          Text(
                            "Peristaltic Feed Pump",
                            style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.bold),
                          ),
                          const SizedBox(width: 6),
                          Container(
                            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                            decoration: BoxDecoration(
                              color: Colors.orange.shade50,
                              borderRadius: BorderRadius.circular(4),
                              border: Border.all(color: Colors.orange.shade200, width: 0.8),
                            ),
                            child: Text(
                              "EXTERNAL",
                              style: theme.textTheme.labelSmall?.copyWith(
                                color: Colors.orange.shade900,
                                fontWeight: FontWeight.bold,
                                fontSize: 9,
                              ),
                            ),
                          ),
                        ],
                      ),
                      const SizedBox(height: 2),
                      Text(
                        "Wi-Fi Node • Substrate & Reagent Precision Feeding",
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

            // Flow Rate & Profile Mode
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              crossAxisAlignment: CrossAxisAlignment.baseline,
              textBaseline: TextBaseline.alphabetic,
              children: [
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text("FLOW RATE", style: theme.textTheme.labelSmall?.copyWith(letterSpacing: 1.1)),
                    const SizedBox(height: 2),
                    Text(
                      state.formattedFlow,
                      style: theme.textTheme.headlineMedium?.copyWith(
                        fontWeight: FontWeight.bold,
                        color: state.isConnectedAndActive ? statusColor : theme.colorScheme.onSurface.withValues(alpha: 0.5),
                      ),
                    ),
                  ],
                ),
                Column(
                  crossAxisAlignment: CrossAxisAlignment.end,
                  children: [
                    if (state.commandPending)
                      Container(
                        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                        margin: const EdgeInsets.only(bottom: 6),
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
                    Text(
                      state.isConnectedAndActive ? state.modeLabel : "--",
                      style: theme.textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.bold),
                    ),
                  ],
                ),
              ],
            ),

            const SizedBox(height: 14),

            // Volume Progress Bar (Dosed vs Target)
            if (state.isConnectedAndActive && state.targetVolume > 0.0) ...[
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Text(
                    "Volume Progress: ${state.formattedVolume} / ${state.formattedTargetVolume}",
                    style: theme.textTheme.labelSmall?.copyWith(fontWeight: FontWeight.w600),
                  ),
                  Text(
                    "${(state.progressFraction * 100).toStringAsFixed(0)}%",
                    style: theme.textTheme.labelSmall?.copyWith(fontWeight: FontWeight.bold, color: statusColor),
                  ),
                ],
              ),
              const SizedBox(height: 6),
              ClipRRect(
                borderRadius: BorderRadius.circular(6),
                child: LinearProgressIndicator(
                  value: state.progressFraction,
                  backgroundColor: theme.colorScheme.surfaceContainerHighest,
                  color: statusColor,
                  minHeight: 8,
                ),
              ),
              const SizedBox(height: 12),
            ],

            // Secondary metrics row
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
                    label: "Dosed Vol",
                    value: state.formattedVolume,
                  ),
                  _buildSubMetric(
                    context,
                    label: "Target Vol",
                    value: state.formattedTargetVolume,
                  ),
                  _buildSubMetric(
                    context,
                    label: "Motor Speed",
                    value: state.formattedSpeed,
                  ),
                  _buildSubMetric(
                    context,
                    label: "PWM",
                    value: state.formattedPwm,
                  ),
                ],
              ),
            ),

            const SizedBox(height: 12),

            // Status message banner
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

            // Quick Stop Action
            if (state.isConnectedAndActive && (state.isActive || state.isWaiting)) ...[
              const SizedBox(height: 12),
              Align(
                alignment: Alignment.centerRight,
                child: FilledButton.tonalIcon(
                  onPressed: onStop,
                  icon: const Icon(Icons.stop, size: 16),
                  label: const Text("Stop Dosing"),
                  style: FilledButton.styleFrom(
                    foregroundColor: Colors.red.shade800,
                    backgroundColor: Colors.red.shade50,
                  ),
                ),
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
        Text(
          value,
          style: theme.textTheme.bodyMedium?.copyWith(
            fontWeight: FontWeight.bold,
          ),
        ),
      ],
    );
  }
}
