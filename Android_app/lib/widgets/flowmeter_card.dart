import 'package:flutter/material.dart';
import '../models/flowmeter_state.dart';

class FlowmeterCard extends StatelessWidget {
  final FlowmeterState state;
  final VoidCallback? onStopFlow;

  const FlowmeterCard({
    super.key,
    required this.state,
    this.onStopFlow,
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
      statusSubtitle = "External flowmeter node is offline or out of range. No push data received.";
    } else {
      statusColor = Colors.cyan.shade700;
      statusIcon = Icons.air;
      statusTitle = "CONNECTED & ACTIVE";
      statusSubtitle = "Gas flow measurement and valve telemetry are actively streaming.";
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
                        "Gas Flowmeter & Sparging",
                        style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.bold),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        "Aeration • O₂ / N₂ Gas Sparging Subsystem",
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

            // Flow Rate Reading Display
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              crossAxisAlignment: CrossAxisAlignment.baseline,
              textBaseline: TextBaseline.alphabetic,
              children: [
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text("MEASURED GAS FLOW RATE", style: theme.textTheme.labelSmall?.copyWith(letterSpacing: 1.1)),
                    const SizedBox(height: 2),
                    Row(
                      crossAxisAlignment: CrossAxisAlignment.baseline,
                      textBaseline: TextBaseline.alphabetic,
                      children: [
                        Text(
                          state.formattedFlowRate,
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

            // Secondary metrics row: Target Setpoint, Sensor Voltage, Main Valve Path
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
                    label: "Target Setpoint",
                    value: state.formattedSetpoint,
                  ),
                  _buildSubMetric(
                    context,
                    label: "Sensor Voltage",
                    value: state.formattedVoltage,
                  ),
                  _buildSubMetric(
                    context,
                    label: "Main Path",
                    value: state.isConnectedAndActive ? (state.isMainFlowOpen ? "FLOWING" : "SHUT") : "--",
                    valueColor: state.isConnectedAndActive
                        ? (state.isMainFlowOpen ? Colors.teal.shade700 : Colors.deepOrange)
                        : null,
                  ),
                ],
              ),
            ),

            const SizedBox(height: 12),

            // Valve Status Pills Row
            Row(
              children: [
                Expanded(
                  child: _buildValveIndicator(
                    label: "Valve 1 (Air/Aux)",
                    isOpen: state.valve1,
                    activeColor: Colors.green.shade700,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: _buildValveIndicator(
                    label: "Valve 2 (Nitrogen)",
                    isOpen: state.valve2,
                    activeColor: Colors.blue.shade700,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: _buildValveIndicator(
                    label: "Shutoff Valve",
                    isOpen: !state.isMainFlowOpen, // valveFlow == 1 is shut
                    activeLabel: "SHUT",
                    inactiveLabel: "PASS",
                    activeColor: Colors.deepOrange,
                  ),
                ),
              ],
            ),

            const SizedBox(height: 12),

            // Subtitle status banner
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

            // Quick action button: Emergency Gas Cutoff / Stop Flow
            if (state.isConnectedAndActive && onStopFlow != null) ...[
              const SizedBox(height: 12),
              Align(
                alignment: Alignment.centerRight,
                child: FilledButton.tonalIcon(
                  onPressed: onStopFlow,
                  icon: const Icon(Icons.stop_circle_outlined, size: 16),
                  label: const Text("Stop Gas Flow"),
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

  Widget _buildValveIndicator({
    required String label,
    required bool isOpen,
    String activeLabel = "OPEN",
    String inactiveLabel = "CLOSED",
    required Color activeColor,
  }) {
    final color = isOpen ? activeColor : Colors.grey;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 6),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(6),
        border: Border.all(color: color.withValues(alpha: 0.3)),
      ),
      child: Column(
        children: [
          Text(
            label,
            style: const TextStyle(fontSize: 10, fontWeight: FontWeight.w500, color: Colors.black54),
            overflow: TextOverflow.ellipsis,
          ),
          const SizedBox(height: 2),
          Text(
            isOpen ? activeLabel : inactiveLabel,
            style: TextStyle(
              fontSize: 11,
              fontWeight: FontWeight.bold,
              color: color,
            ),
          ),
        ],
      ),
    );
  }
}
