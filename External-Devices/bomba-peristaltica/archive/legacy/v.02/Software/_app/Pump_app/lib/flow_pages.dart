import 'package:flutter/material.dart';
import 'bottom_bar.dart';
import 'status_bar.dart';

class ConstantFlowPage extends StatefulWidget {
  final bool isAPConnected;
  final TextEditingController initialTimeController;
  final TextEditingController finalTimeController;
  final TextEditingController initialVolumeController;
  final TextEditingController finalVolumeController;
  final TextEditingController initialFlowController;
  final TextEditingController cx0Controller;
  final TextEditingController seController;
  final TextEditingController yxsController;
  final TextEditingController pumpSlope;
  final TextEditingController pumpIntercept;
  final TextEditingController directFlowController;

  const ConstantFlowPage({
    super.key,
    required this.isAPConnected,
    required this.initialTimeController,
    required this.finalTimeController,
    required this.initialVolumeController,
    required this.finalVolumeController,
    required this.initialFlowController,
    required this.cx0Controller,
    required this.seController,
    required this.yxsController,
    required this.pumpSlope,
    required this.pumpIntercept,
    required this.directFlowController,
  });

  @override
  ConstantFlowPageState createState() => ConstantFlowPageState();
}

class ConstantFlowPageState extends State<ConstantFlowPage> {
  get flowType => 1;
  bool isDirectFlow = false;

  void _updateWarning() {
    // Try to parse the text values to double, will be null if parsing fails
    double? initialTime = double.tryParse(widget.initialTimeController.text);
    double? finalTime = double.tryParse(widget.finalTimeController.text);

    // Check if both values are non-null and if initialTime is greater than finalTime
    if (initialTime != null && finalTime != null && initialTime > finalTime) {
      _showWarningDialog();
    }
  }

  void _showWarningDialog() {
    showDialog(
      context: context,
      builder: (BuildContext context) {
        return AlertDialog(
          title: const Text('Warning'),
          content: const SingleChildScrollView(
            child: ListBody(
              children: <Widget>[
                Text('Initial time cannot be greater than final time.'),
              ],
            ),
          ),
          actions: <Widget>[
            TextButton(
              child: const Text('OK'),
              onPressed: () {
                Navigator.of(context).pop(); // Close the dialog
              },
            ),
          ],
        );
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Constant Flow')),
      body: SingleChildScrollView(
       child: Column(
        children: [
          StatusBar(isAPConnected: widget.isAPConnected), // Corrected
          const SizedBox(height: 20),
          _buildTextField(
            controller: widget.initialTimeController,
            label: 'Initial Time (h)',
            updateWarningCallback: _updateWarning, // Include the warning update call here
          ),
          _buildTextField(
            controller: widget.finalTimeController,
            label: 'Final Time (h)',
            updateWarningCallback: _updateWarning, // Include the warning update call here
          ),
          _buildTextField(controller: widget.initialVolumeController, label: 'Initial Volume (L)'),
          _buildTextField(controller: widget.finalVolumeController, label: 'Final Volume (L)'),
          const SizedBox(height: 5),
          const Text(
            'Table 1. Parameters Selected.',
            textAlign: TextAlign.center,
            style: TextStyle(
              fontSize: 12.0, // Adjust the font size as needed
              fontWeight: FontWeight.normal, // You can specify the font weight
              color: Colors.blueGrey, // You can set the text color
            ),
          ),
          const SizedBox(height: 5),
          Padding(
            padding: const EdgeInsets.only(left: 20.0, right: 20.0), // Adjust the padding values as needed
            child: ValueListenableBuilder(
              valueListenable: widget.initialTimeController,
              builder: (context, _, __) => ValueListenableBuilder(
                valueListenable: widget.finalTimeController,
                builder: (context, _, __) => ValueListenableBuilder(
                  valueListenable: widget.initialVolumeController,
                  builder: (context, _, __) => ValueListenableBuilder(
                    valueListenable: widget.finalVolumeController,
                    builder: (context, _, __) => _buildValueTable(
                      initialTimeController: widget.initialTimeController,
                      finalTimeController: widget.finalTimeController,
                      initialVolumeController: widget.initialVolumeController,
                      finalVolumeController: widget.finalVolumeController,
                      initialFlowController: widget.initialFlowController,
                      cx0Controller: widget.cx0Controller,
                      seController: widget.seController,
                      yxsController: widget.yxsController,
                    ),
                  ),
                ),
              ),
            ),
          ),
          const SizedBox(height: 20),
          Padding(
            padding: const EdgeInsets.only(bottom: 15.0, left: 3, right: 140), // Removed const due to the dynamic value
            child: CheckboxListTile(
              title: const Text(
                'Direct Flow Setting',
                textAlign: TextAlign.center, // This will centralize the title text
                style: TextStyle(
                  fontSize: 14.0, // Adjust the font size as needed
                  fontWeight: FontWeight.bold, // You can specify the font weight
                  color: Colors.blueGrey, // You can set the text color
                ),
              ),
              activeColor: Colors.blueGrey,
              value: isDirectFlow,
              onChanged: (bool? value) {
                setState(() {
                  isDirectFlow = value ?? false;
                });
              },
            ),
          ),
          Padding(
          padding: const EdgeInsets.only(bottom: 15.0, left: 20, right: 20),
          child: TextFormField(
            enabled: isDirectFlow,
            controller: widget.directFlowController,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: const InputDecoration(
              labelText: "Flow Rate (mL/min)",
              border: OutlineInputBorder(),
            ),
          ),
        ),
        ],
      ),
      ),
      bottomNavigationBar: BottomBar(
          isDirectFlow: isDirectFlow,
          flowType: flowType,
          initialTimeController: widget.initialTimeController,
          finalTimeController: widget.finalTimeController,
          initialVolumeController: widget.initialVolumeController,
          finalVolumeController: widget.finalVolumeController,
          initialFlowController: widget.initialFlowController,
          directFlowController: widget.directFlowController,
          cx0Controller: widget.cx0Controller,
          seController: widget.seController,
          yxsController: widget.yxsController,
          pumpSlope: widget.pumpSlope,
          pumpIntercept: widget.pumpIntercept)
    );
  }
}

class LinearFlowPage extends StatefulWidget {
  final bool isAPConnected;
  final TextEditingController initialTimeController;
  final TextEditingController finalTimeController;
  final TextEditingController initialVolumeController;
  final TextEditingController finalVolumeController;
  final TextEditingController initialFlowController;
  final TextEditingController cx0Controller;
  final TextEditingController seController;
  final TextEditingController yxsController;
  final TextEditingController pumpSlope;
  final TextEditingController pumpIntercept;
  final TextEditingController directFlowController;

  const LinearFlowPage({
    super.key,
    required this.isAPConnected,
    required this.initialTimeController,
    required this.finalTimeController,
    required this.initialVolumeController,
    required this.finalVolumeController,
    required this.initialFlowController,
    required this.cx0Controller,
    required this.seController,
    required this.yxsController,
    required this.pumpSlope,
    required this.pumpIntercept,
    required this.directFlowController,
  });

  @override
  LinearFlowPageState createState() => LinearFlowPageState();
}

class LinearFlowPageState extends State<LinearFlowPage> {
  get flowType => 2;

  @override
  Widget build(BuildContext context) {

    return Scaffold(
      appBar: AppBar(title: const Text('Linear Flow')),
      body: SingleChildScrollView(
        child: Column(
          children: [
            StatusBar(isAPConnected: widget.isAPConnected),
            const SizedBox(height: 20),
            _buildTextField(controller: widget.initialTimeController, label: 'Initial Time (h)'),
            _buildTextField(controller: widget.finalTimeController, label: 'Final Time (h)'),
            _buildTextField(controller: widget.initialVolumeController, label: 'Initial Volume (L)'),
            _buildTextField(controller: widget.finalVolumeController, label: 'Final Volume (L)'),
            _buildTextField(controller: widget.initialFlowController, label: 'Initial Flow (mL/min)'),
            const SizedBox(height: 20),
          ],
        ),
      ),
      bottomNavigationBar: BottomBar(
          flowType: flowType,
          initialTimeController: widget.initialTimeController,
          finalTimeController: widget.finalTimeController,
          initialVolumeController: widget.initialVolumeController,
          finalVolumeController: widget.finalVolumeController,
          initialFlowController: widget.initialFlowController,
          directFlowController: widget.directFlowController,
          cx0Controller: widget.cx0Controller,
          seController: widget.seController,
          yxsController: widget.yxsController,
          pumpSlope: widget.pumpSlope,
          pumpIntercept: widget.pumpIntercept),
    );
  }
}

class ExponentialFlowPage extends StatefulWidget {
  final bool isAPConnected;
  final TextEditingController initialTimeController;
  final TextEditingController finalTimeController;
  final TextEditingController initialVolumeController;
  final TextEditingController finalVolumeController;
  final TextEditingController initialFlowController;
  final TextEditingController cx0Controller;
  final TextEditingController seController;
  final TextEditingController yxsController;
  final TextEditingController pumpSlope;
  final TextEditingController pumpIntercept;
  final TextEditingController directFlowController;

  const ExponentialFlowPage({
    super.key,
    required this.isAPConnected,
    required this.initialTimeController,
    required this.finalTimeController,
    required this.initialVolumeController,
    required this.finalVolumeController,
    required this.initialFlowController,
    required this.cx0Controller,
    required this.seController,
    required this.yxsController,
    required this.pumpSlope,
    required this.pumpIntercept,
    required this.directFlowController,
  });

  @override
  ExponentialFlowPageState createState() => ExponentialFlowPageState();
}

class ExponentialFlowPageState extends State<ExponentialFlowPage> {
  get flowType => 3;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
        appBar: AppBar(title: const Text('Exponential Flow')),
        body: SingleChildScrollView(
          child: Column(
            children: [
              StatusBar(isAPConnected: widget.isAPConnected), // Include the StatusBar
              const SizedBox(height: 20),
              _buildTextField(controller: widget.initialTimeController, label: 'Initial Time (h)'),
              _buildTextField(controller: widget.finalTimeController, label: 'Final Time (h)'),
              _buildTextField(controller: widget.initialVolumeController, label: 'Initial Volume (L)'),
              _buildTextField(controller: widget.finalVolumeController, label: 'Final Volume (L)'),
              _buildTextField(controller: widget.cx0Controller, label: 'Initial cell concentration (g/L)'),
              _buildTextField(controller: widget.seController, label: 'Substrate feed concentration (g/L)'),
              _buildTextField(controller: widget.yxsController, label: 'Substrate to cell conversion factor (-)'),
              const SizedBox(height: 20),
            ],
          ),
        ),
        bottomNavigationBar: BottomBar(
            flowType: flowType,
            initialTimeController: widget.initialTimeController,
            finalTimeController: widget.finalTimeController,
            initialVolumeController: widget.initialVolumeController,
            finalVolumeController: widget.finalVolumeController,
            initialFlowController: widget.initialFlowController,
            directFlowController: widget.directFlowController,
            cx0Controller: widget.cx0Controller,
            seController: widget.seController,
            yxsController: widget.yxsController,
            pumpSlope: widget.pumpSlope,
            pumpIntercept: widget.pumpIntercept)
    );
  }
}

class PulseFlowPage extends StatefulWidget {
  final bool isAPConnected;
  final TextEditingController initialTimeController;
  final TextEditingController finalTimeController;
  final TextEditingController initialVolumeController;
  final TextEditingController finalVolumeController;
  final TextEditingController initialFlowController;
  final TextEditingController cx0Controller;
  final TextEditingController seController;
  final TextEditingController yxsController;
  final TextEditingController pumpSlope;
  final TextEditingController pumpIntercept;
  final TextEditingController directFlowController;

  const PulseFlowPage({
    super.key,
    required this.isAPConnected,
    required this.initialTimeController,
    required this.finalTimeController,
    required this.initialVolumeController,
    required this.finalVolumeController,
    required this.initialFlowController,
    required this.cx0Controller,
    required this.seController,
    required this.yxsController,
    required this.pumpSlope,
    required this.pumpIntercept,
    required this.directFlowController
  });

  @override
  PulseFlowPageState createState() => PulseFlowPageState();
}

class PulseFlowPageState extends State<PulseFlowPage> {
  get flowType => 4;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Pulse Flow')),
      body: Column(
        children: [
          StatusBar(isAPConnected: widget.isAPConnected), // Include the StatusBar
          const SizedBox(height: 20),
          const Expanded(
            child: Center(child: Text('Constant Flow Content')),
          ),
        ],
      ),
      bottomNavigationBar: BottomBar(
          flowType: flowType,
          initialTimeController: widget.initialTimeController,
          finalTimeController: widget.finalTimeController,
          initialVolumeController: widget.initialVolumeController,
          finalVolumeController: widget.finalVolumeController,
          initialFlowController: widget.initialFlowController,
          directFlowController: widget.directFlowController,
          cx0Controller: widget.cx0Controller,
          seController: widget.seController,
          yxsController: widget.yxsController,
          pumpSlope: widget.pumpSlope,
          pumpIntercept: widget.pumpIntercept),
    );
  }
}

Widget _buildTextField({
  required TextEditingController controller,
  required String label,
  void Function(String)? onChanged,
  VoidCallback? updateWarningCallback, // Make this optional
}) {
  return Padding(
    padding: const EdgeInsets.only(bottom: 15.0, left: 20, right: 20),
    child: TextFormField(
      controller: controller,
      keyboardType: const TextInputType.numberWithOptions(decimal: true),
      decoration: InputDecoration(
        labelText: label,
        border: const OutlineInputBorder(),
      ),
      onChanged: (value) {
        if (onChanged != null) {
          onChanged(value);
        }
        updateWarningCallback?.call(); // Only call if the callback is provided
      },
    ),
  );
}


Widget _buildValueTable({
  required initialTimeController,
  required finalTimeController,
  required initialVolumeController,
  required finalVolumeController,
  required initialFlowController,
  required cx0Controller,
  required seController,
  required yxsController}) {
  double ti = (double.tryParse(initialTimeController.text) ?? 0) * 60;
  double tf = (double.tryParse(finalTimeController.text) ?? 0) * 60;
  double vi = (double.tryParse(initialVolumeController.text) ?? 0) * 1000;
  double vf = (double.tryParse(finalVolumeController.text) ?? 0) * 1000;
  double b0 = (vf - vi) / (tf - ti);

  return Container(
      decoration: BoxDecoration(
        border: Border.all(width: 1, color: Colors.blueGrey),
        borderRadius: BorderRadius.circular(10.0), // Adjust the radius here
      ),
      child: ClipRRect(
      borderRadius: BorderRadius.circular(10.0), // Same radius as the container
        child: Table(
        border: TableBorder.symmetric(
        inside: const BorderSide(width: 1, color: Colors.blueGrey), // Border for cells inside the table
        ),
        children: [
        const TableRow(
        children: [
          Padding(padding: EdgeInsets.all(6.0), child: Text('Initial Time (min)', textAlign: TextAlign.center, style: TextStyle(fontSize: 12.0))),
          Padding(padding: EdgeInsets.all(8.0), child: Text('Final Time (min)', textAlign: TextAlign.center, style: TextStyle(fontSize: 12.0))),
          Padding(padding: EdgeInsets.all(6.0), child: Text('Initial Volume (mL)', textAlign: TextAlign.center, style: TextStyle(fontSize: 12.0))),
          Padding(padding: EdgeInsets.all(6.0), child: Text('Final Volume (mL)', textAlign: TextAlign.center, style: TextStyle(fontSize: 12.0))),
          Padding(padding: EdgeInsets.all(8.0), child: Text('Flow Rate (mL/min)', textAlign: TextAlign.center, style: TextStyle(fontSize: 12.0))),
          ],
        ),
      TableRow(
        children: [
          Padding(padding: const EdgeInsets.all(6.0), child: Text(ti.toStringAsFixed(2), textAlign: TextAlign.center)),
          Padding(padding: const EdgeInsets.all(6.0), child: Text(tf.toStringAsFixed(2), textAlign: TextAlign.center)),
          Padding(padding: const EdgeInsets.all(6.0), child: Text(vi.toStringAsFixed(2), textAlign: TextAlign.center)),
          Padding(padding: const EdgeInsets.all(6.0), child: Text(vf.toStringAsFixed(2), textAlign: TextAlign.center)),
          Padding(
            padding: const EdgeInsets.all(6.0),
            child: Text(b0.isNaN || b0.isInfinite ? '0' : b0.toStringAsFixed(4), textAlign: TextAlign.center),
          ),
        ],
      ),
    ],
  )));
}