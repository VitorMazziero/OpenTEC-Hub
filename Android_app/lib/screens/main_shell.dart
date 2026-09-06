import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../providers/connection_provider.dart';
import '../providers/telemetry_provider.dart';
import '../providers/device_control_provider.dart';
import '../widgets/connection_dialog.dart';
import 'dashboard_screen.dart';
import 'controls_screen.dart';
import 'graphs_screen.dart';

class MainShell extends StatefulWidget {
  const MainShell({super.key});

  @override
  State<MainShell> createState() => _MainShellState();
}

class _MainShellState extends State<MainShell> {
  int _selectedIndex = 0;

  final List<Widget> _pages = const [
    DashboardScreen(),
    ControlsScreen(),
    GraphsScreen(),
  ];

  void _confirmEmergencyStop(BuildContext context) {
    showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Row(
          children: [
            Icon(Icons.warning_amber_rounded, color: Colors.red),
            SizedBox(width: 8),
            Text("Emergency All-Stop"),
          ],
        ),
        content: const Text(
          "This will send 'resetVariables: 1' to the Hub.\n\n"
          "All motor setpoints, dosing pumps, and heaters will be immediately shut down and set to safe states.",
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(),
            child: const Text("Cancel"),
          ),
          FilledButton(
            style: FilledButton.styleFrom(backgroundColor: Colors.red.shade700),
            onPressed: () async {
              Navigator.of(ctx).pop();
              final control = Provider.of<DeviceControlProvider>(context, listen: false);
              final ok = await control.emergencyStopAll();
              if (context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(
                  SnackBar(
                    content: Text(ok ? "ALL STOP EXECUTED" : "Emergency stop failed"),
                    backgroundColor: Colors.red.shade900,
                    duration: const Duration(seconds: 4),
                  ),
                );
              }
            },
            child: const Text("SHUT DOWN ALL"),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final conn = context.watch<ConnectionProvider>();
    final theme = Theme.of(context);

    Color statusDotColor;
    if (conn.isConnected) {
      statusDotColor = Colors.greenAccent.shade700;
    } else if (conn.isConnecting) {
      statusDotColor = Colors.orange;
    } else {
      statusDotColor = Colors.red;
    }

    return Scaffold(
      appBar: AppBar(
        leading: const Padding(
          padding: EdgeInsets.all(8.0),
          child: Icon(Icons.biotech, size: 28),
        ),
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              "OpenTEC-Hub",
              style: TextStyle(fontWeight: FontWeight.bold, fontSize: 18),
            ),
            Row(
              children: [
                Container(
                  width: 8,
                  height: 8,
                  decoration: BoxDecoration(color: statusDotColor, shape: BoxShape.circle),
                ),
                const SizedBox(width: 6),
                Text(
                  conn.statusMessage,
                  style: theme.textTheme.labelSmall?.copyWith(color: theme.colorScheme.outline),
                ),
              ],
            ),
          ],
        ),
        actions: [
          // Clear Graphs
          IconButton(
            icon: const Icon(Icons.delete_sweep_outlined),
            tooltip: "Clear Chart History",
            onPressed: () {
              Provider.of<TelemetryProvider>(context, listen: false).clearHistory();
              ScaffoldMessenger.of(context).showSnackBar(
                const SnackBar(content: Text("Graph history cleared"), duration: Duration(seconds: 1)),
              );
            },
          ),
          // Connection Dialog
          IconButton(
            icon: Icon(
              conn.isConnected ? Icons.wifi : Icons.wifi_off,
              color: conn.isConnected ? Colors.green.shade700 : null,
            ),
            tooltip: "Connection Settings",
            onPressed: () => showDialog(
              context: context,
              builder: (_) => const ConnectionDialog(),
            ),
          ),
          // Emergency Stop button
          Padding(
            padding: const EdgeInsets.only(right: 8.0),
            child: IconButton.filled(
              icon: const Icon(Icons.dangerous, size: 22),
              tooltip: "Emergency All-Stop",
              style: IconButton.styleFrom(
                backgroundColor: Colors.red.shade700,
                foregroundColor: Colors.white,
              ),
              onPressed: () => _confirmEmergencyStop(context),
            ),
          ),
        ],
      ),
      body: IndexedStack(
        index: _selectedIndex,
        children: _pages,
      ),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _selectedIndex,
        onDestinationSelected: (idx) => setState(() => _selectedIndex = idx),
        destinations: const [
          NavigationDestination(
            icon: Icon(Icons.dashboard_outlined),
            selectedIcon: Icon(Icons.dashboard),
            label: "Dashboard",
          ),
          NavigationDestination(
            icon: Icon(Icons.tune_outlined),
            selectedIcon: Icon(Icons.tune),
            label: "Controls",
          ),
          NavigationDestination(
            icon: Icon(Icons.show_chart_outlined),
            selectedIcon: Icon(Icons.show_chart),
            label: "Graphs",
          ),
        ],
      ),
    );
  }
}
