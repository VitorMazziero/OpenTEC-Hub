import 'package:mqtt_client/mqtt_client.dart' as mqtt;
import 'package:mqtt_client/mqtt_server_client.dart';
import 'package:flutter/foundation.dart';

class MqttController {
  MqttServerClient? client;
  Function? onConnected;
  Function? onDisconnected;
  String topic = 'pump_01';

  void initialize() {
    client = MqttServerClient.withPort('192.168.0.100', 'FlutterClient', 1883);
    client?.onConnected = _onConnected;
    client?.onDisconnected = _onDisconnected;

    client?.logging(on: true);
    final connMessage = mqtt.MqttConnectMessage()
        .authenticateAs('MVLabs', 'xW1-*82y.0vXoLMWS}\$k&EBI482~uO')
        .withWillQos(mqtt.MqttQos.atLeastOnce);
    client?.connectionMessage = connMessage;
  }

  void connect() async {
    try {
      await client?.connect();
    } catch (e) {
      if (kDebugMode) {
        print('Error connecting to MQTT broker: $e');
      }
    }
  }

  void _onConnected() {
    onConnected?.call();
  }

  void _onDisconnected() {
    onDisconnected?.call();
  }

  void publishMessage(String message) {
    if (client?.connectionStatus?.state == mqtt.MqttConnectionState.connected) {
      final builder = mqtt.MqttClientPayloadBuilder();
      builder.addString(message);
      client?.publishMessage(
        topic,
        mqtt.MqttQos.atLeastOnce,
        builder.payload!,
      );
    } else {
      if (kDebugMode) {
        print('MQTT Client is not connected. Cannot send message.');
      }
    }
  }

  void disconnect() {
    client?.disconnect();
  }
}
