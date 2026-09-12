import 'package:flutter/material.dart';
import 'status_bar.dart'; // Import if StatusBar is in a separate file
import 'main.dart'; // Import if ConnectionGuideBar is in a separate file

class CustomScaffold extends StatelessWidget {
  final Widget body;
  final AppBar? appBar;
  final BottomNavigationBar? bottomNavigationBar;
  final bool isAPConnected;

  const CustomScaffold({
    super.key,
    required this.body,
    this.appBar,
    this.bottomNavigationBar,
    required this.isAPConnected,
  });

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: appBar,
      body: Column(
        children: [
          const ConnectionGuideBar(), // Connection guide bar
          StatusBar(isAPConnected: isAPConnected),
          Expanded(child: body), // Use Expanded to fill the remaining space
        ],
      ),
      bottomNavigationBar: bottomNavigationBar,
    );
  }
}
