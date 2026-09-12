import 'dart:async';

import 'package:flutter/material.dart';
import 'widgets/flow_graph.dart';
import 'widgets/status_bar.dart';
//import '../controllers/mqtt_controller.dart';
import '../services/connectivity_service.dart';
import '../controllers/http_controller.dart';


class ConstantPage extends StatefulWidget {
  const ConstantPage({super.key});

  @override
  State<ConstantPage> createState() => _ConstantPageState();
}

class _ConstantPageState extends State<ConstantPage> {
  final TextEditingController _initialTimeController = TextEditingController();
  final TextEditingController _finalTimeController = TextEditingController();
  final TextEditingController _initialVolumeController = TextEditingController();
  final TextEditingController _finalVolumeController = TextEditingController();
  bool isStepsPerSecond = false;
  bool isWifiConnected = false;
  bool isMqttConnected = false;
  final int pumpSlope = 14266;
  final int pumpIntercept = 11787;
  double flowRate = 0;
  double time = 0;
  Timer? _timer;
  bool isGraphVisible = false;
  //MqttController mqttController = MqttController();
  HttpController httpController = HttpController();
  ConnectivityService connectivityService = ConnectivityService();

  @override
  void initState() {
    super.initState();
    _checkWifiConnection();
    //_setupMqttClient();
    _timer = Timer.periodic(const Duration(seconds: 10), (Timer t) => fetchData());
    fetchData(); // Initial fetch
  }

  void _checkWifiConnection() async {
    isWifiConnected = await connectivityService.checkWifiConnection();
    setState(() {});
    connectivityService.onConnectivityChanged().listen((isConnected) {
      setState(() {
        isWifiConnected = isConnected;
      });
    });
  }

  void _startPump() {
    double ti = (double.tryParse(_initialTimeController.text) ?? 0) * 60;
    double tf = (double.tryParse(_finalTimeController.text) ?? 0) * 60;
    double vi = (double.tryParse(_initialVolumeController.text) ?? 0) * 1000;
    double vf = (double.tryParse(_finalVolumeController.text) ?? 0) * 1000;
    double b0 = (vf - vi) / (tf - ti);
    int rawValue = isStepsPerSecond ? 1 : 0;
    String message = "{1,$ti,$tf,$b0,0,0,$pumpSlope,$pumpIntercept,$rawValue,}";
    //mqttController.publishMessage(message);
    httpController.sendCommand(message);
  }

  void _stopPump() {
    double ti = (double.tryParse(_initialTimeController.text) ?? 0) * 60;
    double tf = (double.tryParse(_finalTimeController.text) ?? 0) * 60;
    double vi = (double.tryParse(_initialVolumeController.text) ?? 0) * 1000;
    double vf = (double.tryParse(_finalVolumeController.text) ?? 0) * 1000;
    double b0 = (vf - vi) / (tf - ti);
    int rawValue = isStepsPerSecond ? 1 : 0;
    String message = "{0,$ti,$tf,$b0,0,0,$pumpSlope,$pumpIntercept,$rawValue,}";
    //mqttController.publishMessage(message);
    httpController.sendCommand(message);
  }

  void fetchData() async {
    Map<String, double> parsedData = await httpController.readAndParseData();
    setState(() {
      flowRate = parsedData['FlowRate'] ?? 0;
      time = parsedData['Time'] ?? 0;
    });
  }

  // void _setupMqttClient() {
  //   mqttController.initialize();
  //   mqttController.onConnected = () {
  //     setState(() {
  //       isMqttConnected = true;
  //     });
  //   };
  //   mqttController.onDisconnected = () {
  //     setState(() {
  //       isMqttConnected = false;
  //     });
  //   };
  //   mqttController.connect();
  // }

  Widget _buildTextField({required TextEditingController controller, required String label}) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 15.0),
      child: SizedBox(
        width: 200, // Set your desired width
        child: TextFormField(
          controller: controller,
          keyboardType: TextInputType.number,
          decoration: InputDecoration(
            labelText: label,
            border: const OutlineInputBorder(),
          ),
        ),
      ),
    );
  }

  Widget _buildValueTable() {
    double ti = (double.tryParse(_initialTimeController.text) ?? 0) * 60;
    double tf = (double.tryParse(_finalTimeController.text) ?? 0) * 60;
    double vi = (double.tryParse(_initialVolumeController.text) ?? 0) * 1000;
    double vf = (double.tryParse(_finalVolumeController.text) ?? 0) * 1000;
    double b0 = (vf - vi) / (tf - ti);

    return Table(
      border: TableBorder.all(),
      children: [
        const TableRow(
          children: [
            Padding(padding: EdgeInsets.all(8.0), child: Text('Initial Time (min)', textAlign: TextAlign.center)),
            Padding(padding: EdgeInsets.all(8.0), child: Text('Final Time (min)', textAlign: TextAlign.center)),
            Padding(padding: EdgeInsets.all(8.0), child: Text('Initial Volume (mL)', textAlign: TextAlign.center)),
            Padding(padding: EdgeInsets.all(8.0), child: Text('Final Volume (mL)', textAlign: TextAlign.center)),
            Padding(padding: EdgeInsets.all(8.0), child: Text('Flow Rate (mL/min)', textAlign: TextAlign.center)),
          ],
        ),
        TableRow(
          children: [
            Padding(padding: const EdgeInsets.all(8.0), child: Text(ti.toStringAsFixed(2), textAlign: TextAlign.center)),
            Padding(padding: const EdgeInsets.all(8.0), child: Text(tf.toStringAsFixed(2), textAlign: TextAlign.center)),
            Padding(padding: const EdgeInsets.all(8.0), child: Text(vi.toStringAsFixed(2), textAlign: TextAlign.center)),
            Padding(padding: const EdgeInsets.all(8.0), child: Text(vf.toStringAsFixed(2), textAlign: TextAlign.center)),
            Padding(
              padding: const EdgeInsets.all(8.0),
              child: Text(b0.isNaN || b0.isInfinite ? '0' : b0.toStringAsFixed(4), textAlign: TextAlign.center),
            ),
          ],
        ),
      ],
    );
  }

  @override
  Widget build(BuildContext context) {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(16.0),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          StatusBar(isWifiConnected: isWifiConnected), //isMqttConnected: isMqttConnected
          const Text('Constant flow rate: dV/dt = α'),
          Text('Flow rate: $flowRate', style: const TextStyle(fontSize: 20)),
          Text('Time: $time', style: const TextStyle(fontSize: 20)),
          SwitchListTile(
            title: const Text('Steps per second'),
            value: isStepsPerSecond,
            onChanged: (bool value) {
              setState(() {
                isStepsPerSecond = value;
              });
            },
          ),
          const SizedBox(height: 20),
          _buildTextField(controller: _initialTimeController, label: 'Initial Time (h)'),
          _buildTextField(controller: _finalTimeController, label: 'Final Time (h)'),
          _buildTextField(controller: _initialVolumeController, label: 'Initial Volume (L)'),
          _buildTextField(controller: _finalVolumeController, label: 'Final Volume (L)'),
          const SizedBox(height: 20),
          ValueListenableBuilder(
            valueListenable: _initialTimeController,
            builder: (context, _, __) => ValueListenableBuilder(
              valueListenable: _finalTimeController,
              builder: (context, _, __) => ValueListenableBuilder(
                valueListenable: _initialVolumeController,
                builder: (context, _, __) => ValueListenableBuilder(
                  valueListenable: _finalVolumeController,
                  builder: (context, _, __) => _buildValueTable(),
                ),
              ),
            ),
          ),
          const SizedBox(height: 20),
          ElevatedButton(
            onPressed: _startPump,
            child: const Text('Start Pump'),
          ),
          ElevatedButton(
            onPressed: _stopPump,
            child: const Text('Stop Pump'),
          ),
          ElevatedButton(
            onPressed: () {
              setState(() {
                isGraphVisible = true;
              });
            },
            child: const Text('Simulate flow'),
          ),
          if (isGraphVisible)
            FlowGraph(
              v0: double.tryParse(_initialVolumeController.text) ?? 0.0,
              vf: double.tryParse(_finalVolumeController.text) ?? 0.0,
              t0: double.tryParse(_initialTimeController.text) ?? 0.0,
              tf: double.tryParse(_finalTimeController.text) ?? 0.0,
            ),
        ],
      ),
    );
  }

  @override
  void dispose() {
    _initialTimeController.dispose();
    _finalTimeController.dispose();
    _initialVolumeController.dispose();
    _finalVolumeController.dispose();
    _timer?.cancel();
    super.dispose();
  }
}
