import 'package:flutter/material.dart';
import '../models/servo_state.dart';

class ServoMonitorCard extends StatelessWidget {
  final ServoState servoState;
  final VoidCallback? onStopPressed;

  const ServoMonitorCard({
    super.key,
    required this.servoState,
    this.onStopPressed,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    Color statusColor;
    if (!servoState.online) {
      statusColor = Colors.grey;
    } else if (servoState.isFaulted) {
      statusColor = Colors.red;
    } else if (servoState.isRunning) {
      statusColor = Colors.green;
    } else {
      statusColor = Colors.blue;
    }

    final double rpmRatio = (servoState.rpm / 1000.0).clamp(0.0, 1.0);

    return Card(
      elevation: 2,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(16),
        side: BorderSide(color: statusColor.withValues(alpha: 0.5), width: 1.5),
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
                  child: Icon(Icons.cyclone, color: statusColor, size: 22),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        "Agitator Servo (ASDA-B2)",
                        style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.bold),
                      ),
                      Text(
                        servoState.viaModbus ? "Route: Modbus Direct" : "Route: Legacy UART/CN1",
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
                  child: Text(
                    servoState.stateDescription,
                    style: theme.textTheme.labelSmall?.copyWith(
                      color: statusColor,
                      fontWeight: FontWeight.bold,
                    ),
                  ),
                ),
              ],
            ),
            const Divider(height: 20),

            // Hardware Command Route Flow Banner
            Container(
              margin: const EdgeInsets.only(bottom: 12),
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
              decoration: BoxDecoration(
                color: theme.colorScheme.surfaceContainerHighest.withValues(alpha: 0.4),
                borderRadius: BorderRadius.circular(8),
                border: Border.all(
                  color: servoState.viaModbus ? Colors.blue.shade300 : Colors.teal.shade300,
                  width: 1,
                ),
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Icon(
                        servoState.viaModbus ? Icons.alt_route : Icons.cable,
                        size: 14,
                        color: servoState.viaModbus ? Colors.blue.shade800 : Colors.teal.shade800,
                      ),
                      const SizedBox(width: 6),
                      Text(
                        "VIA DE COMANDO RPM: ${servoState.viaModbus ? 'MODBUS DIRETO' : 'UART / PLACA CONTROLADORA'}",
                        style: TextStyle(
                          fontSize: 10,
                          fontWeight: FontWeight.bold,
                          letterSpacing: 0.5,
                          color: servoState.viaModbus ? Colors.blue.shade900 : Colors.teal.shade900,
                        ),
                      ),
                      const Spacer(),
                      Text(
                        servoState.routeAckDescription,
                        style: TextStyle(
                          fontSize: 10,
                          fontWeight: FontWeight.w600,
                          color: servoState.routeAck >= 0 ? Colors.green.shade800 : Colors.orange.shade800,
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 4),
                  Text(
                    servoState.routePath,
                    style: TextStyle(
                      fontSize: 11,
                      fontWeight: FontWeight.w600,
                      color: theme.colorScheme.onSurface,
                    ),
                  ),
                ],
              ),
            ),

            // Speed Display Row
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              crossAxisAlignment: CrossAxisAlignment.baseline,
              textBaseline: TextBaseline.alphabetic,
              children: [
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text("ACTUAL SPEED", style: theme.textTheme.labelSmall?.copyWith(letterSpacing: 1.1)),
                    const SizedBox(height: 2),
                    Row(
                      crossAxisAlignment: CrossAxisAlignment.baseline,
                      textBaseline: TextBaseline.alphabetic,
                      children: [
                        Text(
                          servoState.online ? servoState.rpm.toStringAsFixed(1) : "--",
                          style: theme.textTheme.headlineMedium?.copyWith(
                            fontWeight: FontWeight.bold,
                            color: servoState.isRunning ? Colors.green.shade700 : theme.colorScheme.onSurface,
                          ),
                        ),
                        const SizedBox(width: 4),
                        Text("RPM", style: theme.textTheme.titleSmall),
                      ],
                    ),
                  ],
                ),
                Column(
                  crossAxisAlignment: CrossAxisAlignment.end,
                  children: [
                    Text("TARGET SETPOINT", style: theme.textTheme.labelSmall?.copyWith(letterSpacing: 1.1)),
                    const SizedBox(height: 2),
                    Row(
                      crossAxisAlignment: CrossAxisAlignment.baseline,
                      textBaseline: TextBaseline.alphabetic,
                      children: [
                        Text(
                          "${servoState.requestedRpm}",
                          style: theme.textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w600),
                        ),
                        const SizedBox(width: 4),
                        Text("RPM", style: theme.textTheme.bodySmall),
                      ],
                    ),
                    Text(
                      "Applied: ${servoState.appliedRpm} RPM",
                      style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.outline),
                    ),
                  ],
                ),
              ],
            ),

            const SizedBox(height: 12),
            ClipRRect(
              borderRadius: BorderRadius.circular(4),
              child: LinearProgressIndicator(
                value: rpmRatio,
                minHeight: 8,
                backgroundColor: theme.colorScheme.surfaceContainerHighest,
                valueColor: AlwaysStoppedAnimation<Color>(
                  servoState.isRunning ? Colors.green : theme.colorScheme.primary,
                ),
              ),
            ),

            const SizedBox(height: 16),

            // Telemetry grid: Torque, Power, Energy, Comm Status
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
                    label: "Torque",
                    value: servoState.hasTelemetry
                        ? "${servoState.torquePct.toStringAsFixed(0)}%"
                        : "--",
                    sub: servoState.hasTelemetry
                        ? "${servoState.torqueNm.toStringAsFixed(2)} Nm"
                        : "",
                  ),
                  _buildSubMetric(
                    context,
                    label: "Power",
                    value: servoState.hasTelemetry
                        ? "${servoState.powerW.toStringAsFixed(1)} W"
                        : "--",
                  ),
                  _buildSubMetric(
                    context,
                    label: "Energy",
                    value: servoState.hasTelemetry
                        ? "${servoState.energyWh.toStringAsFixed(2)} Wh"
                        : "--",
                  ),
                  _buildSubMetric(
                    context,
                    label: "Cmd Sync",
                    value: "ID: ${servoState.commandId}",
                    sub: "ACK: ${servoState.commandAck}",
                  ),
                ],
              ),
            ),

            if (servoState.isRunning && onStopPressed != null) ...[
              const SizedBox(height: 12),
              Align(
                alignment: Alignment.centerRight,
                child: FilledButton.tonalIcon(
                  onPressed: onStopPressed,
                  icon: const Icon(Icons.stop, size: 18),
                  label: const Text("Stop Motor"),
                  style: FilledButton.styleFrom(
                    foregroundColor: Colors.red.shade700,
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
    String? sub,
  }) {
    final theme = Theme.of(context);
    return Column(
      children: [
        Text(label, style: theme.textTheme.labelSmall?.copyWith(color: theme.colorScheme.outline)),
        const SizedBox(height: 2),
        Text(value, style: theme.textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.bold)),
        if (sub != null && sub.isNotEmpty)
          Text(sub, style: theme.textTheme.bodySmall?.copyWith(fontSize: 10, color: theme.colorScheme.outline)),
      ],
    );
  }
}
