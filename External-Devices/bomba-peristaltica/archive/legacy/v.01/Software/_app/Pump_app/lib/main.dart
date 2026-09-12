import 'package:flutter/material.dart';
import 'views/constant_page.dart'; // Replace with the actual file name where ConstantPage is defined

void main() {
  runApp(const MyApp());
}

class MyApp extends StatelessWidget {
  const MyApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Peristaltic Pump Control',
      theme: ThemeData(
        primarySwatch: Colors.blue,
      ),
      home: const MyHomePage(),
    );
  }
}

class MyHomePage extends StatelessWidget {
  const MyHomePage({super.key});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Peristaltic Pump Control'),
      ),
      body: const ConstantPage(), // Your custom page
    );
  }
}
