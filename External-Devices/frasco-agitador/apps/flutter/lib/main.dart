import 'package:flutter/material.dart';
import 'pages/agitator_control_page.dart';
import 'services/agitator_service.dart';
import 'theme/app_theme.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  final agitatorService = AgitatorService();
  runApp(AgitatorApp(service: agitatorService));
}

class AgitatorApp extends StatelessWidget {
  final AgitatorService service;

  const AgitatorApp({super.key, required this.service});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Frasco Agitador TECNAL',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.darkTheme,
      home: AgitatorControlPage(service: service),
    );
  }
}
