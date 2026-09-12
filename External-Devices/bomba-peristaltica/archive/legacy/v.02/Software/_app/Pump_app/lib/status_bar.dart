import 'package:flutter/material.dart';

class StatusBar extends StatelessWidget {
  final bool isAPConnected;
  //final bool isMqttConnected;

  const StatusBar({super.key, required this.isAPConnected}); //required this.isMqttConnected

  @override
  Widget build(BuildContext context) {
    return Container(
      color: _getConnectionStatusColor(),
      height: 20,
      child: Center(
        child: Text(
          _getConnectionStatusText(),
          style: const TextStyle(color: Colors.white),
        ),
      ),
    );
  }

  Color _getConnectionStatusColor() {
    if (isAPConnected) { //&& isMqttConnected
      return Colors.green; // Both connected
    } else if (!isAPConnected) {
      return Colors.red; // Wi-Fi disconnected
    // } else if (!isMqttConnected) {
    //   return Colors.orange; // MQTT disconnected
    }
    return Colors.grey; // Default color
  }

  String _getConnectionStatusText() {
    if (isAPConnected ) { //&& isMqttConnected
      return "Connected to AP";
    } else if (!isAPConnected) {
      return "AP Disconnected";
    // } else if (!isMqttConnected) {
    //   return "MQTT Broker Disconnected";
    }
    return "Checking connections...";
  }
}
