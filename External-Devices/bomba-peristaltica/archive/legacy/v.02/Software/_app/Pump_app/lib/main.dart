import 'package:flutter/material.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:permission_handler/permission_handler.dart';
import 'flow_pages.dart';
import 'status_bar.dart';
import 'connectivity_service.dart';

void main() => runApp(const MyApp());

class MyApp extends StatelessWidget {
  const MyApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Flow Simulation',
      theme: ThemeData(
        primarySwatch: Colors.blue,
        fontFamily: 'Oxygen', // Set a default font for the app
      ),
      home: const MyHomePage(),
    );
  }
}

class MyHomePage extends StatefulWidget {
  const MyHomePage({super.key});

  @override
  MyHomePageState createState() => MyHomePageState();
}

class MyHomePageState extends State<MyHomePage> {
  final TextEditingController _initialTimeController = TextEditingController();
  final TextEditingController _finalTimeController = TextEditingController();
  final TextEditingController _initialVolumeController = TextEditingController();
  final TextEditingController _finalVolumeController = TextEditingController();
  final TextEditingController _directFlowController = TextEditingController(); // Direct control
  final TextEditingController _initialFlowController = TextEditingController(); //Linear flow
  final TextEditingController _cx0Controller = TextEditingController(); // Exponential flow
  final TextEditingController _seController = TextEditingController(); // Exponential flow
  final TextEditingController _yxsController = TextEditingController(); // Exponential flow
  final TextEditingController _pumpSlope = TextEditingController(); // Calibration
  final TextEditingController _pumpIntercept = TextEditingController(); // Calibration
  bool isAPConnected = false;
  final ConnectivityService connectivityService = ConnectivityService();

  @override
  void initState() {
    super.initState();
    _loadValues();
    _requestPermission();
    _checkAPConnection();
  }

  void _checkAPConnection() async {
    bool isConnected = await connectivityService.checkAPConnection();
    setState(() {
      isAPConnected = isConnected;
    });

    connectivityService.onConnectivityChanged().listen((isConnected) {
      setState(() {
        isAPConnected = isConnected;
      });
    });
  }

  Future<void> _requestPermission() async {
    var status = await Permission.location.status;
    if (!status.isGranted) {
      await Permission.location.request();
    }
  }
  Future<void> _showInputDialog() async {
    // Load the values before showing the dialog
    await _loadValues();

    // Start the showDialog with the current context which is synchronous
    showDialog<void>(
      context: context,
      barrierDismissible: false, // User must tap button to close the dialog
      builder: (BuildContext context) {
        // Builder is called synchronously and provides a context that is valid for the AlertDialog
        return AlertDialog(
          title: const Text('Calibration Values'),
          content: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min, // Use the minimum space needed by the children
              children: <Widget>[
                const SizedBox(height: 20),
                TextFormField(
                  controller: _pumpIntercept,
                  keyboardType: TextInputType.number,
                  decoration: const InputDecoration(
                    labelText: 'Intercept (×10^-3)',
                    border: OutlineInputBorder(),
                  ),
                ),
                const SizedBox(height: 20),
                TextFormField(
                  controller: _pumpSlope,
                  keyboardType: TextInputType.number,
                  decoration: const InputDecoration(
                    labelText: 'Slope (×-10^-8)',
                    border: OutlineInputBorder(),
                  ),
                ),
                const SizedBox(height: 20),
                const Center(child: Text('Slope*x+Intercept')),
                const Center(child: Text('x in steps/s')),
              ],
            ),
          ),
          actions: <Widget>[
            TextButton(
              child: const Text('Cancel'),
              onPressed: () {
                Navigator.of(context).pop(); // This context is from the builder and valid
              },
            ),
            TextButton(
              child: const Text('Save'),
              onPressed: () async {
                await _saveValues(); // Save values and await the async call
                Navigator.of(context).pop(); // This context is from the builder and valid
              },
            ),
          ],
        );
      },
    );
  }

  Future<void> _saveValues() async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString('pumpSlope', _pumpSlope.text);
    await prefs.setString('pumpIntercept', _pumpIntercept.text);
  }

  Future<void> _loadValues() async {
    final prefs = await SharedPreferences.getInstance();
    // Assuming _pumpSlope and _pumpIntercept are TextEditingController instances
    _pumpSlope.text = prefs.getString('pumpSlope') ?? '869604'; // Set default value if null
    _pumpIntercept.text = prefs.getString('pumpIntercept') ?? '0'; // Set default value if null
    _finalTimeController.text = prefs.getString('finalTime') ?? '24';
    _initialTimeController.text = prefs.getString('initialTime') ?? '0';
    _finalVolumeController.text = prefs.getString('finalVolume') ?? '5';
    _initialVolumeController.text = prefs.getString('initialVolume')?? '2';
    _initialFlowController.text = prefs.getString('initialFlow') ?? '0';
    _cx0Controller.text = prefs.getString('cx0') ?? '0.4';
    _seController.text = prefs.getString('se') ?? '200';
    _yxsController.text = prefs.getString('yxs')?? '0.6';

  }

  @override
  void dispose() {
    _pumpSlope.dispose();
    _pumpIntercept.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: Column(
      children: [
        Container(
        height: 30,
        color: Theme.of(context).primaryColor.withOpacity(0.1),
      ),
        const ConnectionGuideBar(), // Connection guide bar at the top of the body
        StatusBar(isAPConnected: isAPConnected),
        Align(
          alignment: Alignment.bottomRight,
          child: IconButton(
            icon: const Icon(Icons.save), // Choose the icon you want
            onPressed: _showInputDialog,
          ),
        ),
        Expanded( // Makes sure the grid takes the remaining space
          child: Align(
          alignment: Alignment.center,
            child: Container(
              constraints: const BoxConstraints(maxWidth: 600), // Set a max width if you want to limit the width on larger screens
              child: GridView.count(
              shrinkWrap: true, // Use this to make GridView take the size of its children
              crossAxisCount: 2,
              crossAxisSpacing: 5,
              mainAxisSpacing: 20,
              padding: const EdgeInsets.all(40),
              children: <Widget>[
                FlowIcon(
                  imagePath: 'assets/icons/constant.png',
                  label: 'Constant Flow',
                  onTap: () => Navigator.push(context, MaterialPageRoute(builder: (context) => ConstantFlowPage(
                    isAPConnected: isAPConnected,
                    initialTimeController: _initialTimeController,
                    finalTimeController: _finalTimeController,
                    initialVolumeController: _initialVolumeController,
                    finalVolumeController: _finalVolumeController,
                    initialFlowController: _initialFlowController,
                    directFlowController: _directFlowController,
                    cx0Controller: _cx0Controller,
                    seController: _seController,
                    yxsController: _yxsController,
                    pumpSlope: _pumpSlope,
                    pumpIntercept: _pumpIntercept
                    ),
                    )
                  ),
                ),
                FlowIcon(
                  imagePath: 'assets/icons/linear.png',
                  label: 'Linear Flow',
                  onTap: () => Navigator.push(context, MaterialPageRoute(builder: (context) => LinearFlowPage(
                    isAPConnected: isAPConnected,
                    initialTimeController: _initialTimeController,
                    finalTimeController: _finalTimeController,
                    initialVolumeController: _initialVolumeController,
                    finalVolumeController: _finalVolumeController,
                    initialFlowController: _initialFlowController,
                    directFlowController: _directFlowController,
                    cx0Controller: _cx0Controller,
                    seController: _seController,
                    yxsController: _yxsController,
                    pumpSlope: _pumpSlope,
                    pumpIntercept: _pumpIntercept
                  ),
                  ),
                ),
                ),
                FlowIcon(
                  imagePath: 'assets/icons/exponential.png',
                  label: 'Exponential Flow',
                  onTap: () => Navigator.push(context, MaterialPageRoute(builder: (context) => ExponentialFlowPage(
                    isAPConnected: isAPConnected,
                    initialTimeController: _initialTimeController,
                    finalTimeController: _finalTimeController,
                    initialVolumeController: _initialVolumeController,
                    finalVolumeController: _finalVolumeController,
                    initialFlowController: _initialFlowController,
                    directFlowController: _directFlowController,
                    cx0Controller: _cx0Controller,
                    seController: _seController,
                    yxsController: _yxsController,
                    pumpSlope: _pumpSlope,
                    pumpIntercept: _pumpIntercept
                  ),
                  ),
                ),
                ),
                FlowIcon(
                  imagePath: 'assets/icons/pulse.png',
                  label: 'Pulse Flow',
                  onTap: () => Navigator.push(context, MaterialPageRoute(builder: (context) => PulseFlowPage(
                    isAPConnected: isAPConnected,
                    initialTimeController: _initialTimeController,
                    finalTimeController: _finalTimeController,
                    initialVolumeController: _initialVolumeController,
                    finalVolumeController: _finalVolumeController,
                    initialFlowController: _initialFlowController,
                    directFlowController: _directFlowController,
                    cx0Controller: _cx0Controller,
                    seController: _seController,
                    yxsController: _yxsController,
                    pumpSlope: _pumpSlope,
                    pumpIntercept: _pumpIntercept
                  ),
                  ),
                  ),
                ),
              ],
              )
            )
          )
        )
      ],
    ));
  }
}

class FlowIcon extends StatelessWidget {
  final String imagePath;
  final String label;
  final VoidCallback onTap;

  const FlowIcon({super.key, required this.imagePath, required this.label, required this.onTap});

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        mainAxisAlignment: MainAxisAlignment.center, // Center the column's children vertically.
        children: <Widget>[
          Image.asset(imagePath, width: 100, height: 100),
          const SizedBox(height: 8), // Adjust the height to increase/decrease the space
          Text(
            label,
            style: const TextStyle(
              fontSize: 16, // Change the font size as needed
              fontWeight: FontWeight.normal,
            ),
            textAlign: TextAlign.center, // Center align the text
          ),
        ],
      ),
    );
  }
}

class ConnectionGuideBar extends StatelessWidget {
  const ConnectionGuideBar({super.key});

  @override
  Widget build(BuildContext context) {
    Color barBackgroundColor = Theme.of(context).primaryColor.withOpacity(0.1);
    Color textColor = Theme.of(context).primaryColorDark;

    return Container(
      color: barBackgroundColor,
      padding: const EdgeInsets.all(16),
      child: Row(
        children: [
          Icon(Icons.wifi, color: textColor),
          const SizedBox(width: 8),
          Expanded(
            child: RichText(
              textAlign: TextAlign.center, // Center-align the entire RichText
              text: TextSpan(
                style: TextStyle(color: textColor, fontSize: 16, fontFamily: 'Oxygen'),
                children: const [
                  TextSpan(text: 'Connect to '),
                  TextSpan(
                    text: 'PP_0x-AP',
                    style: TextStyle(fontWeight: FontWeight.bold),
                  )
                ],
              ),
            ),
          ),
          IconButton(
            icon: Icon(Icons.info_outline, color: textColor),
            onPressed: () {
              _showConnectionGuide(context);
            },
          ),
        ],
      ),
    );
  }
}

void _showConnectionGuide(BuildContext context) {
  showDialog(
    context: context,
    builder: (BuildContext context) {
      return AlertDialog(
        title: const Center(
          child: Text(
            'How to Connect to Pump AP',
            style: TextStyle(
              fontSize: 20, // Adjust the font size as needed
              fontWeight: FontWeight.bold,
            ),
          ),
        ),
        content: const SingleChildScrollView(
          child: ListBody(
            children: <Widget>[
              Text('1. Open the WiFi settings on your device.'),
              Text('2. Look for the AP named "PP_0x-AP".'),
              Text('3. Select it and enter the password: "pump_control".'),
              Text('4. Ignore the "No internet connection" warning.'),
              Text('5. Once connected, return to the app.'),
            ],
          ),
        ),
        actions: <Widget>[
          TextButton(
            child: const Text('Got it'),
            onPressed: () {
              Navigator.of(context).pop();
            },
          ),
        ],
      );
    },
  );
}

