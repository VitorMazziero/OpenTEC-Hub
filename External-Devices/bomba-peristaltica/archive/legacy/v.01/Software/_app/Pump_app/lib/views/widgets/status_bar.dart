import 'package:flutter/material.dart';

class StatusBar extends StatelessWidget {
  final bool isWifiConnected;
  //final bool isMqttConnected;

  const StatusBar({super.key, required this.isWifiConnected}); //required this.isMqttConnected

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
    if (isWifiConnected) { //&& isMqttConnected
      return Colors.green; // Both connected
    } else if (!isWifiConnected) {
      return Colors.red; // Wi-Fi disconnected
    // } else if (!isMqttConnected) {
    //   return Colors.orange; // MQTT disconnected
    }
    return Colors.grey; // Default color
  }

  String _getConnectionStatusText() {
    if (isWifiConnected ) { //&& isMqttConnected
      return "Connected to AP";
    } else if (!isWifiConnected) {
      return "AP Disconnected";
    // } else if (!isMqttConnected) {
    //   return "MQTT Broker Disconnected";
    }
    return "Checking connections...";
  }
}
