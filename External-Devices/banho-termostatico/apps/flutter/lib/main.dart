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
        title: 'Banho Lucadema',
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
        titleSpacing: 12,
        title: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            ClipRRect(
              borderRadius: BorderRadius.circular(6),
              child: Image.asset(
                'assets/icon/app_logo.png',
                width: 24,
                height: 24,
              ),
            ),
            const SizedBox(width: 8),
            const Flexible(
              child: Text(
                'Banho Lucadema',
                style: TextStyle(
                  fontSize: 16,
                  fontWeight: FontWeight.w600,
                  letterSpacing: -0.2,
                ),
                overflow: TextOverflow.ellipsis,
              ),
            ),
          ],
        ),
        actions: [
          Padding(
            padding: const EdgeInsets.only(right: 12),
            child: Center(child: ConnectionBadge(service: service)),
          ),
        ],
      ),
      body: Column(
        children: [
          if (service.statusData.hubOwned)
            Container(
              width: double.infinity,
              color: Colors.deepOrange.shade700,
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
              child: const Text(
                'Controlado pelo Hub — cascata ativa. Comandos locais bloqueados; '
                'só Abortar funciona. Pare a cascata no Hub para operar por aqui.',
                style: TextStyle(color: Colors.white, fontSize: 12),
              ),
            ),
          Expanded(child: IndexedStack(index: _index, children: _pages)),
        ],
      ),
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
