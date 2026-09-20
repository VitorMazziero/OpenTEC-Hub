import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'services/bath_service.dart';
import 'theme/app_theme.dart';
import 'pages/operate_page.dart';
import 'pages/modes_page.dart';
import 'pages/bench_page.dart';
import 'pages/settings_page.dart';
import 'widgets/connection_badge.dart';
import 'widgets/emergency_abort_button.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  final service = BathService();
  service.init();
  runApp(BathApp(service: service));
}

class BathApp extends StatelessWidget {
  final BathService service;
  const BathApp({super.key, required this.service});

  @override
  Widget build(BuildContext context) {
    return ChangeNotifierProvider<BathService>.value(
      value: service,
      child: MaterialApp(
        title: 'Banho Termostático TECNAL',
        debugShowCheckedModeBanner: false,
        theme: AppTheme.darkTheme,
        home: const BathShell(),
      ),
    );
  }
}

class BathShell extends StatefulWidget {
  const BathShell({super.key});

  @override
  State<BathShell> createState() => _BathShellState();
}

class _BathShellState extends State<BathShell> {
  int _index = 0;

  static const _pages = [
    OperatePage(),
    ModesPage(),
    BenchPage(),
    SettingsPage(),
  ];

  @override
  Widget build(BuildContext context) {
    final service = context.watch<BathService>();
    return Scaffold(
      appBar: AppBar(
        title: const Text('Banho Termostático'),
        actions: [
          Padding(
            padding: const EdgeInsets.only(right: 12),
            child: Center(child: ConnectionBadge(service: service)),
          ),
        ],
      ),
      body: IndexedStack(index: _index, children: _pages),
      bottomNavigationBar: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Abortar nunca é escondido nem desabilitado (plano §5.6).
          Padding(
            padding: const EdgeInsets.only(top: 4, bottom: 2),
            child: EmergencyAbortButton(service: service),
          ),
          NavigationBar(
            selectedIndex: _index,
            onDestinationSelected: (i) => setState(() => _index = i),
            destinations: const [
              NavigationDestination(
                  icon: Icon(Icons.thermostat), label: 'Operação'),
              NavigationDestination(icon: Icon(Icons.tune), label: 'Modos'),
              NavigationDestination(
                  icon: Icon(Icons.keyboard), label: 'Bancada'),
              NavigationDestination(
                  icon: Icon(Icons.settings), label: 'Config'),
            ],
          ),
        ],
      ),
    );
  }
}
