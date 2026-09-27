import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../providers/connection_provider.dart';
import '../providers/telemetry_provider.dart';

class ConnectionDialog extends StatefulWidget {
  const ConnectionDialog({super.key});

  @override
  State<ConnectionDialog> createState() => _ConnectionDialogState();
}

class _ConnectionDialogState extends State<ConnectionDialog> {
  late TextEditingController _ipController;

  @override
  void initState() {
    super.initState();
    final conn = Provider.of<ConnectionProvider>(context, listen: false);
    _ipController = TextEditingController(text: conn.ip);
  }

  @override
  void dispose() {
    _ipController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final conn = context.watch<ConnectionProvider>();
    final telemetry = context.watch<TelemetryProvider>().telemetry;
    final theme = Theme.of(context);

    return AlertDialog(
      title: Row(
        children: [
          Icon(Icons.wifi, color: theme.colorScheme.primary),
          const SizedBox(width: 8),
          const Text("Conexão com o Hub"),
        ],
      ),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            TextField(
              controller: _ipController,
              decoration: const InputDecoration(
                labelText: "Endereço IP do Hub",
                hintText: "192.168.4.1",
                border: OutlineInputBorder(),
                prefixIcon: Icon(Icons.router_outlined),
                isDense: true,
              ),
              enabled: !conn.isConnected && !conn.isConnecting,
            ),
            const SizedBox(height: 12),

            // Connection Status Tile
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: theme.colorScheme.surfaceContainerHighest.withValues(alpha: 0.4),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Container(
                        width: 10,
                        height: 10,
                        decoration: BoxDecoration(
                          shape: BoxShape.circle,
                          color: conn.isConnected
                              ? Colors.green
                              : conn.isConnecting
                                  ? Colors.orange
                                  : Colors.red,
                        ),
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          conn.statusMessage,
                          style: theme.textTheme.bodySmall?.copyWith(fontWeight: FontWeight.w600),
                        ),
                      ),
                    ],
                  ),
                  if (conn.isConnected) ...[
                    const SizedBox(height: 8),
                    const Divider(height: 1),
                    const SizedBox(height: 8),
                    Text(
                      "Firmware: ${telemetry.hubFirmwareVersion.isNotEmpty ? telemetry.hubFirmwareVersion : 'v10'}",
                      style: theme.textTheme.bodySmall,
                    ),
                    Text(
                      "Protocolo: ${telemetry.hubProtocolVersion} • Clientes: ${telemetry.hubStations}",
                      style: theme.textTheme.bodySmall,
                    ),
                    Text(
                      "Placa de sensores: ${telemetry.sensorCommOk ? 'ok' : 'sem resposta'}",
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: telemetry.sensorCommOk ? Colors.green.shade700 : Colors.red.shade700,
                      ),
                    ),
                  ],
                ],
              ),
            ),
            const SizedBox(height: 12),

            // Ping test button
            OutlinedButton.icon(
              onPressed: conn.isConnecting
                  ? null
                  : () async {
                      conn.setIp(_ipController.text);
                      await conn.pingNow();
                    },
              icon: const Icon(Icons.speed, size: 16),
              label: Text("Testar conexão ${conn.rttMs > 0 ? '(${conn.rttMs} ms)' : ''}"),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text("Fechar"),
        ),
        if (conn.isConnected)
          FilledButton.tonal(
            onPressed: () {
              conn.disconnect();
            },
            style: FilledButton.styleFrom(
              foregroundColor: Colors.red.shade700,
              backgroundColor: Colors.red.shade50,
            ),
            child: const Text("Desconectar"),
          )
        else
          FilledButton(
            onPressed: conn.isConnecting
                ? null
                : () async {
                    conn.setIp(_ipController.text);
                    await conn.connect();
                    if (!context.mounted) return;
                    if (conn.isConnected) {
                      Navigator.of(context).pop();
                    }
                  },
            child: conn.isConnecting
                ? const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white),
                  )
                : const Text("Conectar"),
          ),
      ],
    );
  }
}
