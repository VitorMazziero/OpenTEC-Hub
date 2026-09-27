import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'providers/connection_provider.dart';
import 'providers/telemetry_provider.dart';
import 'providers/device_control_provider.dart';
import 'screens/main_shell.dart';
import 'theme/app_theme.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(const OpenTECHubApp());
}

class OpenTECHubApp extends StatelessWidget {
  const OpenTECHubApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MultiProvider(
      providers: [
        ChangeNotifierProvider(create: (_) => ConnectionProvider()),
        ChangeNotifierProxyProvider<ConnectionProvider, TelemetryProvider>(
          create: (_) => TelemetryProvider(),
          update: (_, conn, telemetry) {
            final t = telemetry ?? TelemetryProvider();
            t.attachTelemetryStream(conn.telemetryStream);
            return t;
          },
        ),
        ChangeNotifierProxyProvider<ConnectionProvider, DeviceControlProvider>(
          create: (ctx) => DeviceControlProvider(
            getApiService: () => Provider.of<ConnectionProvider>(ctx, listen: false).apiService,
          ),
          update: (_, conn, control) =>
              control ?? DeviceControlProvider(getApiService: () => conn.apiService),
        ),
      ],
      child: Consumer2<TelemetryProvider, DeviceControlProvider>(
        builder: (context, telemetry, control, child) {
          // Wire automatic pH calibration echo back to hub whenever calibrated pH changes
          telemetry.onPhCalChanged = (phCal) => control.sendPhCalEcho(phCal);

          return MaterialApp(
            title: 'OpenTEC-Hub',
            debugShowCheckedModeBanner: false,
            theme: AppTheme.light(),
            darkTheme: AppTheme.dark(),
            themeMode: ThemeMode.system,
            home: const MainShell(),
          );
        },
      ),
    );
  }
}