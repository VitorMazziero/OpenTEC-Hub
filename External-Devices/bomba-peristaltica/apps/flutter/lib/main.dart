import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'services/pump_connection_service.dart';
import 'pages/flow_selection_page.dart';
import 'pages/graphs_page.dart';
import 'pages/calibration_page.dart';
import 'pages/settings_page.dart';

void main() {
  runApp(
    ChangeNotifierProvider(
      create: (_) => PumpConnectionService(),
      child: const MaterialApp(
        debugShowCheckedModeBanner: false,
        home: PumpControlApp(),
      ),
    ),
  );
}

class PumpControlApp extends StatefulWidget {
  const PumpControlApp({super.key});

  @override
  State<PumpControlApp> createState() => _PumpControlAppState();
}

class _PumpControlAppState extends State<PumpControlApp> {
  int _selectedDrawerIndex = 0;
  final GlobalKey<ScaffoldState> _scaffoldKey = GlobalKey<ScaffoldState>();

  @override
  Widget build(BuildContext context) {
    final connectionService = Provider.of<PumpConnectionService>(context);

    Widget page;
    switch (_selectedDrawerIndex) {
      case 0:
        page = FlowSelectionPage(connectionService: connectionService);
        break;
      case 1:
        page = const GraphsPage();
        break;
      case 2:
        page = CalibrationPage(connectionService: connectionService);
        break;
      case 3:
        page = SettingsPage(connectionService: connectionService);
        break;
      default:
        page = FlowSelectionPage(connectionService: connectionService);
    }

    return Scaffold(
      key: _scaffoldKey,
      backgroundColor: const Color(0xFF1E222B), // Modern dark slate
      appBar: AppBar(
        backgroundColor: const Color(0xFF161A22),
        elevation: 0,
        title: const Text(
          'Pump Advanced Control',
          style: TextStyle(
            fontSize: 20,
            fontWeight: FontWeight.bold,
            color: Colors.white,
            letterSpacing: 0.5,
          ),
        ),
        leading: IconButton(
          icon: const Icon(Icons.menu_open, color: Colors.white70),
          onPressed: () {
            _scaffoldKey.currentState?.openDrawer();
          },
        ),
        actions: [
          Row(
            children: [
              Text(
                connectionService.isConnected ? 'Online' : 'Offline',
                style: TextStyle(
                  fontSize: 12,
                  color: connectionService.isConnected ? Colors.greenAccent : Colors.white38,
                  fontWeight: FontWeight.w600,
                ),
              ),
              IconButton(
                icon: Icon(
                  connectionService.isConnected ? Icons.wifi : Icons.wifi_off,
                  color: connectionService.isConnected ? Colors.greenAccent : Colors.redAccent,
                ),
                onPressed: () => _showConnectionDialog(context, connectionService),
              ),
            ],
          ),
        ],
      ),
      drawer: SizedBox(
        width: 250,
        child: Drawer(
          child: Container(
            color: const Color(0xFF161A22),
            child: ListView(
              padding: EdgeInsets.zero,
              children: [
                DrawerHeader(
                  decoration: const BoxDecoration(
                    gradient: LinearGradient(
                      colors: [Color(0xFF2C3E50), Color(0xFF000000)],
                      begin: Alignment.topLeft,
                      end: Alignment.bottomRight,
                    ),
                  ),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    mainAxisAlignment: MainAxisAlignment.end,
                    children: [
                      const Text(
                        'PUMP CONTROL',
                        style: TextStyle(
                          color: Colors.white,
                          fontSize: 20,
                          fontWeight: FontWeight.bold,
                          letterSpacing: 1.5,
                        ),
                      ),
                      const SizedBox(height: 4),
                      Text(
                        'IP: ${connectionService.ipAddress}',
                        style: TextStyle(
                          color: Colors.white.withOpacity(0.6),
                          fontSize: 12,
                        ),
                      ),
                    ],
                  ),
                ),
                _buildDrawerItem(
                  icon: Icons.tune,
                  title: 'Select Flow Type',
                  index: 0,
                ),
                _buildDrawerItem(
                  icon: Icons.show_chart,
                  title: 'Graphs',
                  index: 1,
                ),
                _buildDrawerItem(
                  icon: Icons.auto_graph,
                  title: 'Calibration',
                  index: 2,
                ),
                _buildDrawerItem(
                  icon: Icons.settings,
                  title: 'Settings',
                  index: 3,
                ),
              ],
            ),
          ),
        ),
      ),
      body: page,
    );
  }

  Widget _buildDrawerItem({
    required IconData icon,
    required String title,
    required int index,
  }) {
    final isSelected = _selectedDrawerIndex == index;
    return Container(
      margin: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: isSelected ? const Color(0xFF2E3B4E) : Colors.transparent,
        borderRadius: BorderRadius.circular(8),
      ),
      child: ListTile(
        leading: Icon(
          icon,
          color: isSelected ? Colors.greenAccent : Colors.white70,
        ),
        title: Text(
          title,
          style: TextStyle(
            color: isSelected ? Colors.white : Colors.white70,
            fontWeight: isSelected ? FontWeight.bold : FontWeight.normal,
          ),
        ),
        selected: isSelected,
        onTap: () {
          setState(() {
            _selectedDrawerIndex = index;
          });
          Navigator.pop(context);
        },
      ),
    );
  }

  void _showConnectionDialog(BuildContext context, PumpConnectionService service) {
    final controller = TextEditingController(text: service.ipAddress);
    bool testing = false;
    String statusMsg = '';

    showDialog(
      context: context,
      builder: (context) => StatefulBuilder(
        builder: (context, setDialogState) => AlertDialog(
          backgroundColor: const Color(0xFF242A38),
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
          title: Row(
            children: const [
              Icon(Icons.wifi, color: Colors.greenAccent),
              SizedBox(width: 10),
              Text(
                'WiFi Connection',
                style: TextStyle(color: Colors.white, fontWeight: FontWeight.bold),
              ),
            ],
          ),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text(
                'Enter Peristaltic Pump IP Address:',
                style: TextStyle(color: Colors.white70, fontSize: 13),
              ),
              const SizedBox(height: 8),
              TextField(
                controller: controller,
                style: const TextStyle(color: Colors.white),
                decoration: InputDecoration(
                  hintText: '192.168.6.1',
                  hintStyle: const TextStyle(color: Colors.white30),
                  filled: true,
                  fillColor: const Color(0xFF1E222B),
                  border: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(8),
                    borderSide: BorderSide.none,
                  ),
                  contentPadding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                ),
              ),
              if (statusMsg.isNotEmpty) ...[
                const SizedBox(height: 12),
                Text(
                  statusMsg,
                  style: TextStyle(
                    color: statusMsg.contains('Success') ? Colors.greenAccent : Colors.redAccent,
                    fontSize: 12,
                    fontWeight: FontWeight.bold,
                  ),
                ),
              ],
            ],
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(context).pop(),
              child: const Text('Close', style: TextStyle(color: Colors.white54)),
            ),
            ElevatedButton(
              onPressed: testing
                  ? null
                  : () async {
                      setDialogState(() {
                        testing = true;
                        statusMsg = 'Testing connection...';
                      });
                      final success = await service.testConnection(controller.text);
                      setDialogState(() {
                        testing = false;
                        statusMsg = success ? 'Success: Connected to Pump!' : 'Error: Could not reach Pump.';
                      });
                    },
              style: ElevatedButton.styleFrom(
                backgroundColor: const Color(0xFF2E3B4E),
                foregroundColor: Colors.white,
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
              ),
              child: testing
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        valueColor: AlwaysStoppedAnimation<Color>(Colors.white),
                      ),
                    )
                  : const Text('Test & Save'),
            ),
          ],
        ),
      ),
    );
  }
}
