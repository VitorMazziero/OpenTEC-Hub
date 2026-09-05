import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../providers/device_control_provider.dart';
import '../providers/telemetry_provider.dart';
import '../widgets/control_section_card.dart';
import '../models/peristaltic_pump_state.dart';
import '../services/pump_profile_math.dart';

class ControlsScreen extends StatefulWidget {
  const ControlsScreen({super.key});

  @override
  State<ControlsScreen> createState() => _ControlsScreenState();
}

class _ControlsScreenState extends State<ControlsScreen> {
  // Distance Sensor (External)
  bool _distanceCommOn = true;
  late TextEditingController _distanceRefController;

  // Biomass Sensor (External)
  bool _biomassCommOn = true;
  late TextEditingController _biomassLowController;
  late TextEditingController _biomassHighController;
  late TextEditingController _biomassOptController;

  // Flowmeter & Gas Sparging (External)
  bool _flowmeterCommOn = true;
  bool _valve1On = false;
  bool _valve2On = false;
  late TextEditingController _flowSetpointController;

  // Flask Agitator (External)
  int _agitatorPercent = 80;
  int _agitatorDir = 1; // 1 = CW, 0 = CCW
  bool _agitatorAuto = true;
  bool _agitatorReEnablePot = true;
  late TextEditingController _agitatorPercentController;

  // Peristaltic Feed Pump (External)
  bool _pumpCommOn = true;
  PeristalticPumpMode _selectedPumpMode = PeristalticPumpMode.constant;
  late TextEditingController _pumpInitMinutesController;
  late TextEditingController _pumpFinalMinutesController;
  late TextEditingController _pumpLambdaController;
  late TextEditingController _pumpPhiController;
  late TextEditingController _pumpPolyCoeffsController;
  late TextEditingController _pumpPiecewiseTimesController;
  late TextEditingController _pumpPiecewiseFlowsController;
  PumpPreview? _pumpPreview;
  String? _pumpValidationError;

  // Servo controls
  int _motorRpm = 100;
  int _motorRoute = 1; // 1 = Modbus Direct, 0 = UART/CN1
  bool _servoCommOn = true;
  late TextEditingController _motorRpmController;

  // Temperature
  bool _tempOn = false;
  late TextEditingController _tempSetpointController;

  // pH
  bool _phOn = false;
  late TextEditingController _phSetpointController;
  late TextEditingController _phErrorController;
  late TextEditingController _phOpTimeController;
  late TextEditingController _phMixTimeController;
  late TextEditingController _phSpeedController;

  // Oxygen
  bool _oxyOn = false;

  // Pressure
  bool _pressureOn = false;
  late TextEditingController _pressureRefController;

  // Nutrient Pump
  bool _nutrientOn = false;
  late TextEditingController _nutriOpTimeController;
  late TextEditingController _nutriMixTimeController;
  late TextEditingController _nutriOpCycleController;
  late TextEditingController _nutriMixCycleController;
  late TextEditingController _nutriSpeedController;

  // Antifoam Pump
  bool _antifoamOn = false;
  late TextEditingController _antiOpTimeController;
  late TextEditingController _antiMixTimeController;
  late TextEditingController _antiSpeedController;

  @override
  void initState() {
    super.initState();
    _distanceRefController = TextEditingController(text: "100.0");
    _biomassLowController = TextEditingController(text: "30000");
    _biomassHighController = TextEditingController(text: "60000");
    _biomassOptController = TextEditingController(text: "50000");
    _flowSetpointController = TextEditingController(text: "0.0");
    _agitatorPercentController = TextEditingController(text: "$_agitatorPercent");
    _motorRpmController = TextEditingController(text: "$_motorRpm");
    _tempSetpointController = TextEditingController(text: "25.0");
    _phSetpointController = TextEditingController(text: "7.0");
    _phErrorController = TextEditingController(text: "0.17");
    _phOpTimeController = TextEditingController(text: "5");
    _phMixTimeController = TextEditingController(text: "20");
    _phSpeedController = TextEditingController(text: "50");
    _pressureRefController = TextEditingController(text: "100");
    _nutriOpTimeController = TextEditingController(text: "999");
    _nutriMixTimeController = TextEditingController(text: "1");
    _nutriOpCycleController = TextEditingController(text: "500");
    _nutriMixCycleController = TextEditingController(text: "1");
    _nutriSpeedController = TextEditingController(text: "99");
    _antiOpTimeController = TextEditingController(text: "5");
    _antiMixTimeController = TextEditingController(text: "20");
    _antiSpeedController = TextEditingController(text: "99");

    _pumpInitMinutesController = TextEditingController(text: "0");
    _pumpFinalMinutesController = TextEditingController(text: "60");
    _pumpLambdaController = TextEditingController(text: "1.0");
    _pumpPhiController = TextEditingController(text: "0.05");
    _pumpPolyCoeffsController = TextEditingController(text: "1.0, 0.05");
    _pumpPiecewiseTimesController = TextEditingController(text: "0, 30, 60");
    _pumpPiecewiseFlowsController = TextEditingController(text: "1.0, 2.5, 0.5");
    _recalculatePumpPreview();
  }

  @override
  void dispose() {
    _distanceRefController.dispose();
    _biomassLowController.dispose();
    _biomassHighController.dispose();
    _biomassOptController.dispose();
    _flowSetpointController.dispose();
    _agitatorPercentController.dispose();
    _pumpInitMinutesController.dispose();
    _pumpFinalMinutesController.dispose();
    _pumpLambdaController.dispose();
    _pumpPhiController.dispose();
    _pumpPolyCoeffsController.dispose();
    _pumpPiecewiseTimesController.dispose();
    _pumpPiecewiseFlowsController.dispose();
    _motorRpmController.dispose();
    _tempSetpointController.dispose();
    _phSetpointController.dispose();
    _phErrorController.dispose();
    _phOpTimeController.dispose();
    _phMixTimeController.dispose();
    _phSpeedController.dispose();
    _pressureRefController.dispose();
    _nutriOpTimeController.dispose();
    _nutriMixTimeController.dispose();
    _nutriOpCycleController.dispose();
    _nutriMixCycleController.dispose();
    _nutriSpeedController.dispose();
    _antiOpTimeController.dispose();
    _antiMixTimeController.dispose();
    _antiSpeedController.dispose();
    super.dispose();
  }

  PumpProfileSpec _buildCurrentPumpSpec() {
    final initT = double.tryParse(_pumpInitMinutesController.text) ?? 0.0;
    final finalT = double.tryParse(_pumpFinalMinutesController.text) ?? 60.0;
    final lambda = double.tryParse(_pumpLambdaController.text) ?? 1.0;
    final phi = double.tryParse(_pumpPhiController.text) ?? 0.0;

    List<double> polyCoeffs = [];
    if (_selectedPumpMode == PeristalticPumpMode.polynomial) {
      polyCoeffs = _pumpPolyCoeffsController.text
          .split(',')
          .map((s) => double.tryParse(s.trim()))
          .whereType<double>()
          .toList();
    }

    List<double> pwTimes = [];
    List<double> pwFlows = [];
    if (_selectedPumpMode == PeristalticPumpMode.piecewise) {
      pwTimes = _pumpPiecewiseTimesController.text
          .split(',')
          .map((s) => double.tryParse(s.trim()))
          .whereType<double>()
          .toList();
      pwFlows = _pumpPiecewiseFlowsController.text
          .split(',')
          .map((s) => double.tryParse(s.trim()))
          .whereType<double>()
          .toList();
    }

    return PumpProfileSpec(
      mode: _selectedPumpMode,
      initMinutes: initT,
      finalMinutes: finalT,
      lambda: lambda,
      phi: phi,
      polynomialCoefficients: polyCoeffs,
      piecewiseTimes: pwTimes,
      piecewiseFlows: pwFlows,
    );
  }

  void _recalculatePumpPreview() {
    final spec = _buildCurrentPumpSpec();
    final err = PumpProfileMath.validateSpec(spec);
    if (err != null) {
      setState(() {
        _pumpValidationError = err;
        _pumpPreview = null;
      });
    } else {
      setState(() {
        _pumpValidationError = null;
        _pumpPreview = PumpProfileMath.sample(spec);
      });
    }
  }

  void _showFeedback(BuildContext context, bool ok, String action) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(ok ? "$action dispatched" : "$action failed"),
        backgroundColor: ok ? Colors.green.shade800 : Colors.red.shade800,
        duration: const Duration(seconds: 2),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final distanceState = context.watch<TelemetryProvider>().distanceState;
    final biomassState = context.watch<TelemetryProvider>().biomassState;
    final flowmeterState = context.watch<TelemetryProvider>().flowmeterState;
    final agitatorState = context.watch<TelemetryProvider>().agitatorState;
    final pumpState = context.watch<TelemetryProvider>().pumpState;
    final servoState = context.watch<TelemetryProvider>().servoState;

    return ListView(
      padding: const EdgeInsets.all(12.0),
      children: [
        // ====================================================
        // 1. SERVO MOTOR (DELTA ASDA-B2)
        // ====================================================
        ControlSectionCard(
          title: "Servo Agitator (Delta ASDA-B2)",
          icon: Icons.cyclone,
          accentColor: Colors.blueAccent,
          isEnabled: _servoCommOn,
          onToggle: (val) async {
            setState(() => _servoCommOn = val);
            final ok = await control.setServoComm(val);
            if (context.mounted) _showFeedback(context, ok, "Servo Comm ${val ? 'ON' : 'OFF'}");
          },
          isBusy: control.isBusy,
          applyButtonLabel: "Apply Speed",
          onApply: () async {
            final parsedRpm = int.tryParse(_motorRpmController.text) ?? _motorRpm;
            final ok = await control.setMotorRpm(parsedRpm);
            if (context.mounted) _showFeedback(context, ok, "Motor $parsedRpm RPM");
          },
          children: [
            // Command Route Selection & Hardware Path Explanation
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: Theme.of(context).colorScheme.surfaceContainerHighest.withValues(alpha: 0.35),
                borderRadius: BorderRadius.circular(10),
                border: Border.all(
                  color: _motorRoute == 1 ? Colors.blue.shade300 : Colors.teal.shade300,
                  width: 1.2,
                ),
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Icon(
                        _motorRoute == 1 ? Icons.alt_route : Icons.cable,
                        size: 16,
                        color: _motorRoute == 1 ? Colors.blue.shade800 : Colors.teal.shade800,
                      ),
                      const SizedBox(width: 6),
                      Text(
                        "SELEÇÃO DA VIA DE CONTROLE DO SERVO",
                        style: TextStyle(
                          fontSize: 11,
                          fontWeight: FontWeight.bold,
                          letterSpacing: 0.5,
                          color: _motorRoute == 1 ? Colors.blue.shade900 : Colors.teal.shade900,
                        ),
                      ),
                      const Spacer(),
                      if (servoState.online)
                        Container(
                          padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                          decoration: BoxDecoration(
                            color: servoState.routeAck >= 0 ? Colors.green.shade50 : Colors.amber.shade50,
                            borderRadius: BorderRadius.circular(4),
                            border: Border.all(
                              color: servoState.routeAck >= 0 ? Colors.green.shade300 : Colors.amber.shade300,
                              width: 0.8,
                            ),
                          ),
                          child: Text(
                            "ACK: ${servoState.routeAckDescription}",
                            style: TextStyle(
                              fontSize: 10,
                              fontWeight: FontWeight.bold,
                              color: servoState.routeAck >= 0 ? Colors.green.shade800 : Colors.amber.shade900,
                            ),
                          ),
                        ),
                    ],
                  ),
                  const SizedBox(height: 10),

                  // Route Switch Segments
                  SizedBox(
                    width: double.infinity,
                    child: SegmentedButton<int>(
                      segments: const [
                        ButtonSegment(
                          value: 1,
                          label: Text("1. Modbus Direto (ESP32-Servo)"),
                          icon: Icon(Icons.flash_on, size: 16),
                        ),
                        ButtonSegment(
                          value: 0,
                          label: Text("2. UART Legada (Placa Controladora)"),
                          icon: Icon(Icons.settings_input_composite, size: 16),
                        ),
                      ],
                      selected: {_motorRoute},
                      onSelectionChanged: (set) async {
                        final selected = set.first;
                        setState(() {
                          _motorRoute = selected;
                          // Firmware disables motor on route change
                          _motorRpm = 0;
                          _motorRpmController.text = "0";
                        });
                        final ok = await control.setMotorControlMode(selected);
                        if (context.mounted) {
                          _showFeedback(
                            context,
                            ok,
                            "Via alterada para ${selected == 1 ? 'Modbus Direto' : 'UART/CN1 Legada'} (Motor desabilitado por segurança)",
                          );
                        }
                      },
                    ),
                  ),
                  const SizedBox(height: 10),

                  // Detailed Flow Path
                  Container(
                    width: double.infinity,
                    padding: const EdgeInsets.all(8),
                    decoration: BoxDecoration(
                      color: Theme.of(context).colorScheme.surface,
                      borderRadius: BorderRadius.circular(6),
                      border: Border.all(color: Colors.grey.shade300),
                    ),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          "Fluxo Físico de Comando:",
                          style: TextStyle(fontSize: 11, fontWeight: FontWeight.bold, color: Colors.grey.shade700),
                        ),
                        const SizedBox(height: 2),
                        Text(
                          _motorRoute == 1
                              ? "ESP32S3-HUB → ESP32S3-Servo → Servo Delta ASDA-B2"
                              : "ESP32S3-HUB → UART → ControllerBoard → Servo Delta ASDA-B2",
                          style: TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.bold,
                            color: _motorRoute == 1 ? Colors.blue.shade800 : Colors.teal.shade800,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          _motorRoute == 1
                              ? "• Modbus RS-485 via nó ESP32-Servo dedicado gravando no registrador P1-09. Suporta leitura completa de telemetria, torque e energia."
                              : "• Envio via barramento serial UART para a Placa Controladora TECNAL, que aciona o servo pelo conector CN1 (modo analógico/pulso legado).",
                          style: TextStyle(fontSize: 11, color: Colors.grey.shade800, height: 1.3),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 6),
                  Row(
                    children: [
                      Icon(Icons.info_outline, size: 13, color: Colors.amber.shade900),
                      const SizedBox(width: 4),
                      Expanded(
                        child: Text(
                          "A troca de via desabilita o motor imediatamente no firmware (0 RPM). Aplique um novo setpoint de velocidade após a seleção.",
                          style: TextStyle(fontSize: 10, fontStyle: FontStyle.italic, color: Colors.amber.shade900),
                        ),
                      ),
                    ],
                  ),
                ],
              ),
            ),
            const SizedBox(height: 12),

            Row(
              children: [
                Expanded(
                  flex: 3,
                  child: Slider(
                    value: _motorRpm.toDouble().clamp(0.0, 1000.0),
                    min: 0,
                    max: 1000,
                    divisions: 100,
                    label: "$_motorRpm RPM",
                    onChanged: (val) {
                      setState(() {
                        _motorRpm = val.toInt();
                        _motorRpmController.text = "$_motorRpm";
                      });
                    },
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  flex: 1,
                  child: TextField(
                    controller: _motorRpmController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(
                      labelText: "RPM",
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                    onChanged: (txt) {
                      final parsed = int.tryParse(txt);
                      if (parsed != null && parsed >= 0 && parsed <= 1000) {
                        setState(() => _motorRpm = parsed);
                      }
                    },
                  ),
                ),
              ],
            ),
            const SizedBox(height: 6),

            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                OutlinedButton.icon(
                  onPressed: () async {
                    final ok = await control.resetServoEnergy();
                    if (context.mounted) _showFeedback(context, ok, "Reset Energy");
                  },
                  icon: const Icon(Icons.refresh, size: 16),
                  label: const Text("Reset Energy"),
                ),
                FilledButton.tonalIcon(
                  onPressed: () async {
                    setState(() {
                      _motorRpm = 0;
                      _motorRpmController.text = "0";
                    });
                    final ok = await control.stopMotor();
                    if (context.mounted) _showFeedback(context, ok, "Motor Stop (0 RPM)");
                  },
                  icon: const Icon(Icons.stop, size: 16),
                  label: const Text("Stop (0 RPM)"),
                  style: FilledButton.styleFrom(
                    foregroundColor: Colors.red.shade700,
                    backgroundColor: Colors.red.shade50,
                  ),
                ),
              ],
            ),
          ],
        ),
        const SizedBox(height: 12),

        // ====================================================
        // 2. TEMPERATURE CONTROL
        // ====================================================
        ControlSectionCard(
          title: "Temperature Control",
          icon: Icons.thermostat,
          accentColor: Colors.redAccent,
          isEnabled: _tempOn,
          onToggle: (val) => setState(() => _tempOn = val),
          isBusy: control.isBusy,
          onApply: () async {
            final sp = double.tryParse(_tempSetpointController.text) ?? 25.0;
            final ok = await control.setTemperature(enabled: _tempOn, setpoint: sp);
            if (context.mounted) _showFeedback(context, ok, "Temperature ${_tempOn ? '$sp°C' : 'OFF'}");
          },
          children: [
            TextField(
              controller: _tempSetpointController,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: const InputDecoration(
                labelText: "Target Temperature (°C)",
                hintText: "10.0 - 90.0",
                isDense: true,
                border: OutlineInputBorder(),
              ),
              enabled: _tempOn,
            ),
          ],
        ),
        const SizedBox(height: 12),

        // ====================================================
        // 3. PH CONTROLLER
        // ====================================================
        ControlSectionCard(
          title: "pH Control (Internal Acid/Base)",
          icon: Icons.science_outlined,
          accentColor: Colors.blueAccent,
          isEnabled: _phOn,
          onToggle: (val) => setState(() => _phOn = val),
          isBusy: control.isBusy,
          onApply: () async {
            final sp = double.tryParse(_phSetpointController.text) ?? 7.0;
            final err = double.tryParse(_phErrorController.text) ?? 0.17;
            final op = int.tryParse(_phOpTimeController.text) ?? 5;
            final mix = int.tryParse(_phMixTimeController.text) ?? 20;
            final speed = int.tryParse(_phSpeedController.text) ?? 50;

            final ok = await control.setPhControl(
              enabled: _phOn,
              setpoint: sp,
              error: err,
              opTimeSeconds: op,
              mixTimeSeconds: mix,
              speedPercent: speed,
            );
            if (context.mounted) _showFeedback(context, ok, "pH Control ${_phOn ? 'ON' : 'OFF'}");
          },
          children: [
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _phSetpointController,
                    keyboardType: const TextInputType.numberWithOptions(decimal: true),
                    decoration: const InputDecoration(
                      labelText: "Target pH",
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                    enabled: _phOn,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _phErrorController,
                    keyboardType: const TextInputType.numberWithOptions(decimal: true),
                    decoration: const InputDecoration(
                      labelText: "Error Band (pH)",
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                    enabled: _phOn,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _phOpTimeController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(
                      labelText: "Op Time (s)",
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                    enabled: _phOn,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _phMixTimeController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(
                      labelText: "Mix Time (s)",
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                    enabled: _phOn,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _phSpeedController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(
                      labelText: "Speed (%)",
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                    enabled: _phOn,
                  ),
                ),
              ],
            ),
          ],
        ),
        const SizedBox(height: 12),

        // ====================================================
        // 4. DISSOLVED OXYGEN (DO)
        // ====================================================
        ControlSectionCard(
          title: "Dissolved Oxygen Monitor",
          icon: Icons.bubble_chart_outlined,
          accentColor: Colors.teal,
          isEnabled: _oxyOn,
          onToggle: (val) => setState(() => _oxyOn = val),
          isBusy: control.isBusy,
          onApply: () async {
            final ok = await control.setOxygenMonitor(_oxyOn);
            if (context.mounted) _showFeedback(context, ok, "Oxygen Monitor ${_oxyOn ? 'ENABLED' : 'DISABLED'}");
          },
          children: [
            Text(
              "Enables DO probe reading and UART acquisition on the internal OpenTEC sensor board.",
              style: Theme.of(context).textTheme.bodySmall?.copyWith(color: Theme.of(context).colorScheme.outline),
            ),
          ],
        ),
        const SizedBox(height: 12),

        // ====================================================
        // 5. PRESSURE REFERENCE
        // ====================================================
        ControlSectionCard(
          title: "Pressure Control",
          icon: Icons.speed,
          accentColor: Colors.orangeAccent,
          isEnabled: _pressureOn,
          onToggle: (val) => setState(() => _pressureOn = val),
          isBusy: control.isBusy,
          onApply: () async {
            final ref = int.tryParse(_pressureRefController.text) ?? 100;
            final ok = await control.setPressure(enabled: _pressureOn, referenceMmHg: ref);
            if (context.mounted) _showFeedback(context, ok, "Pressure ${_pressureOn ? '$ref mmHg' : 'OFF'}");
          },
          children: [
            TextField(
              controller: _pressureRefController,
              keyboardType: TextInputType.number,
              decoration: const InputDecoration(
                labelText: "Pressure Reference (mmHg)",
                isDense: true,
                border: OutlineInputBorder(),
              ),
              enabled: _pressureOn,
            ),
          ],
        ),
        const SizedBox(height: 12),

        // ====================================================
        // 6. NUTRIENT DOSING PUMP (INTERNAL)
        // ====================================================
        ControlSectionCard(
          title: "Nutrient Pump (Internal)",
          icon: Icons.water_drop_outlined,
          accentColor: Colors.brown,
          isEnabled: _nutrientOn,
          onToggle: (val) => setState(() => _nutrientOn = val),
          isBusy: control.isBusy,
          onApply: () async {
            final op = int.tryParse(_nutriOpTimeController.text) ?? 999;
            final mix = int.tryParse(_nutriMixTimeController.text) ?? 1;
            final opC = int.tryParse(_nutriOpCycleController.text) ?? 500;
            final mixC = int.tryParse(_nutriMixCycleController.text) ?? 1;
            final spd = int.tryParse(_nutriSpeedController.text) ?? 99;

            final ok = await control.setNutrientPump(
              enabled: _nutrientOn,
              opSeconds: op,
              mixSeconds: mix,
              opCycleMinutes: opC,
              mixCycleMinutes: mixC,
              speedPercent: spd,
            );
            if (context.mounted) _showFeedback(context, ok, "Nutrient Pump ${_nutrientOn ? 'ON' : 'OFF'}");
          },
          children: [
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _nutriOpTimeController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(labelText: "Op Time (s)", isDense: true, border: OutlineInputBorder()),
                    enabled: _nutrientOn,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _nutriMixTimeController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(labelText: "Mix Time (s)", isDense: true, border: OutlineInputBorder()),
                    enabled: _nutrientOn,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _nutriOpCycleController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(labelText: "Op Cycle (min)", isDense: true, border: OutlineInputBorder()),
                    enabled: _nutrientOn,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _nutriMixCycleController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(labelText: "Mix Cycle (min)", isDense: true, border: OutlineInputBorder()),
                    enabled: _nutrientOn,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _nutriSpeedController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(labelText: "Speed (%)", isDense: true, border: OutlineInputBorder()),
                    enabled: _nutrientOn,
                  ),
                ),
              ],
            ),
          ],
        ),
        const SizedBox(height: 12),

        // ====================================================
        // 7. ANTIFOAM DOSING PUMP (INTERNAL)
        // ====================================================
        ControlSectionCard(
          title: "Antifoam Pump (Internal)",
          icon: Icons.shield_outlined,
          accentColor: Colors.deepOrangeAccent,
          isEnabled: _antifoamOn,
          onToggle: (val) => setState(() => _antifoamOn = val),
          isBusy: control.isBusy,
          onApply: () async {
            final op = int.tryParse(_antiOpTimeController.text) ?? 5;
            final mix = int.tryParse(_antiMixTimeController.text) ?? 20;
            final spd = int.tryParse(_antiSpeedController.text) ?? 99;

            final ok = await control.setAntifoamPump(
              enabled: _antifoamOn,
              opSeconds: op,
              mixSeconds: mix,
              speedPercent: spd,
            );
            if (context.mounted) _showFeedback(context, ok, "Antifoam Pump ${_antifoamOn ? 'ON' : 'OFF'}");
          },
          children: [
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _antiOpTimeController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(labelText: "Op Time (s)", isDense: true, border: OutlineInputBorder()),
                    enabled: _antifoamOn,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _antiMixTimeController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(labelText: "Mix Time (s)", isDense: true, border: OutlineInputBorder()),
                    enabled: _antifoamOn,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _antiSpeedController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(labelText: "Speed (%)", isDense: true, border: OutlineInputBorder()),
                    enabled: _antifoamOn,
                  ),
                ),
              ],
            ),
          ],
        ),

        const SizedBox(height: 16),

        // ====================================================
        // EXTERNAL PERIPHERALS
        // ====================================================
        Row(
          children: [
            const Icon(Icons.devices_other, size: 20, color: Colors.indigo),
            const SizedBox(width: 8),
            Text(
              "External Peripherals",
              style: Theme.of(context).textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.bold,
                letterSpacing: 0.5,
              ),
            ),
            const Spacer(),
            Text(
              "Wi-Fi Subsystems",
              style: Theme.of(context).textTheme.labelSmall?.copyWith(color: Theme.of(context).colorScheme.outline),
            ),
          ],
        ),
        const SizedBox(height: 8),

        ControlSectionCard(
          title: "Distance & Level Sensor (External)",
          icon: Icons.radar,
          accentColor: Colors.indigo,
          isEnabled: _distanceCommOn,
          onToggle: (val) async {
            setState(() => _distanceCommOn = val);
            final ok = await control.setDistanceSensorComm(val);
            if (context.mounted) _showFeedback(context, ok, "Distance Comm ${val ? 'ON' : 'OFF'}");
          },
          isBusy: control.isBusy,
          applyButtonLabel: "Apply Distance Settings",
          onApply: () async {
            final ref = double.tryParse(_distanceRefController.text) ?? 100.0;
            final ok1 = await control.setDistanceSensorComm(_distanceCommOn);
            final ok2 = await control.setDistanceSensorReference(ref);
            if (context.mounted) _showFeedback(context, ok1 && ok2, "Distance Reference ($ref mm)");
          },
          children: [
            // Live Connection Status indicator inside the control card
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
              decoration: BoxDecoration(
                color: distanceState.isConnectedAndActive
                    ? Colors.green.shade50
                    : distanceState.isDisconnected
                        ? Colors.amber.shade50
                        : Colors.grey.shade100,
                borderRadius: BorderRadius.circular(8),
                border: Border.all(
                  color: distanceState.isConnectedAndActive
                      ? Colors.green.shade300
                      : distanceState.isDisconnected
                          ? Colors.amber.shade300
                          : Colors.grey.shade300,
                ),
              ),
              child: Row(
                children: [
                  Icon(
                    distanceState.isConnectedAndActive
                        ? Icons.check_circle
                        : distanceState.isDisconnected
                            ? Icons.warning_amber_rounded
                            : Icons.info_outline,
                    size: 16,
                    color: distanceState.isConnectedAndActive
                        ? Colors.green.shade800
                        : distanceState.isDisconnected
                            ? Colors.amber.shade900
                            : Colors.grey.shade700,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      distanceState.statusLabel,
                      style: TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.bold,
                        color: distanceState.isConnectedAndActive
                            ? Colors.green.shade800
                            : distanceState.isDisconnected
                                ? Colors.amber.shade900
                                : Colors.grey.shade700,
                      ),
                    ),
                  ),
                  if (distanceState.isConnectedAndActive)
                    Text(
                      distanceState.formattedDistance,
                      style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 13),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _distanceRefController,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: const InputDecoration(
                labelText: "Reference Threshold / Level Limit (mm)",
                hintText: "100.0",
                isDense: true,
                border: OutlineInputBorder(),
              ),
              enabled: _distanceCommOn,
            ),
          ],
        ),

        const SizedBox(height: 12),

        ControlSectionCard(
          title: "Biomass Sensor (Optical Density)",
          icon: Icons.grain,
          accentColor: Colors.teal,
          isEnabled: _biomassCommOn,
          onToggle: (val) async {
            setState(() => _biomassCommOn = val);
            final ok = await control.setBiomassComm(val);
            if (context.mounted) _showFeedback(context, ok, "Biomass Comm ${val ? 'ON' : 'OFF'}");
          },
          isBusy: control.isBusy,
          applyButtonLabel: "Apply Thresholds (Low/High/Opt)",
          onApply: () async {
            final low = int.tryParse(_biomassLowController.text) ?? 30000;
            final high = int.tryParse(_biomassHighController.text) ?? 60000;
            final opt = int.tryParse(_biomassOptController.text) ?? 50000;
            final ok = await control.setBiomassThresholds(low: low, high: high, opt: opt);
            if (context.mounted) _showFeedback(context, ok, "Biomass Thresholds (low: $low, high: $high, opt: $opt)");
          },
          children: [
            // Live Status indicator badge
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
              decoration: BoxDecoration(
                color: biomassState.isAcquiring
                    ? Colors.teal.shade50
                    : biomassState.isIdle
                        ? Colors.blue.shade50
                        : biomassState.isDisconnected
                            ? Colors.amber.shade50
                            : Colors.grey.shade100,
                borderRadius: BorderRadius.circular(8),
                border: Border.all(
                  color: biomassState.isAcquiring
                      ? Colors.teal.shade300
                      : biomassState.isIdle
                          ? Colors.blue.shade300
                          : biomassState.isDisconnected
                              ? Colors.amber.shade300
                              : Colors.grey.shade300,
                ),
              ),
              child: Row(
                children: [
                  Icon(
                    biomassState.isAcquiring
                        ? Icons.grain
                        : biomassState.isIdle
                            ? Icons.pause_circle_outline
                            : biomassState.isDisconnected
                                ? Icons.wifi_tethering_off_outlined
                                : Icons.sensors_off_outlined,
                    size: 16,
                    color: biomassState.isAcquiring
                        ? Colors.teal.shade800
                        : biomassState.isIdle
                            ? Colors.blue.shade800
                            : biomassState.isDisconnected
                                ? Colors.amber.shade900
                                : Colors.grey.shade700,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      biomassState.statusLabel,
                      style: TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.bold,
                        color: biomassState.isAcquiring
                            ? Colors.teal.shade800
                            : biomassState.isIdle
                                ? Colors.blue.shade800
                                : biomassState.isDisconnected
                                    ? Colors.amber.shade900
                                    : Colors.grey.shade700,
                      ),
                    ),
                  ),
                  if (biomassState.isAcquiring)
                    Text(
                      biomassState.formattedAbs,
                      style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 13),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 12),

            // Quick Actions: Start, Stop, Zero Blank
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                OutlinedButton.icon(
                  icon: const Icon(Icons.play_arrow, size: 16),
                  label: const Text("Start Acquisition"),
                  onPressed: (_biomassCommOn && biomassState.canStartAcquisition && !control.isBusy)
                      ? () async {
                          final ok = await control.startBiomassAcquisition();
                          if (context.mounted) _showFeedback(context, ok, "Start Acquisition");
                        }
                      : null,
                ),
                OutlinedButton.icon(
                  icon: const Icon(Icons.stop, size: 16),
                  label: const Text("Stop Acquisition"),
                  onPressed: (_biomassCommOn && biomassState.canStopAcquisition && !control.isBusy)
                      ? () async {
                          final ok = await control.stopBiomassAcquisition();
                          if (context.mounted) _showFeedback(context, ok, "Stop Acquisition");
                        }
                      : null,
                ),
                OutlinedButton.icon(
                  icon: const Icon(Icons.adjust, size: 16),
                  label: const Text("Zero Blank"),
                  onPressed: (_biomassCommOn && biomassState.canZeroBlank && !control.isBusy)
                      ? () async {
                          final ok = await control.zeroBiomassBlank();
                          if (context.mounted) _showFeedback(context, ok, "Zero Blank");
                        }
                      : null,
                ),
              ],
            ),
            const SizedBox(height: 12),

            // Thresholds row: low, high, opt
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _biomassLowController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(
                      labelText: "Low Limit (cts)",
                      hintText: "30000",
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                    enabled: _biomassCommOn,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _biomassHighController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(
                      labelText: "High Limit (cts)",
                      hintText: "60000",
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                    enabled: _biomassCommOn,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _biomassOptController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(
                      labelText: "Opt Target (cts)",
                      hintText: "50000",
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                    enabled: _biomassCommOn,
                  ),
                ),
              ],
            ),
          ],
        ),

        const SizedBox(height: 12),

        ControlSectionCard(
          title: "Gas Flowmeter & Sparging (External)",
          icon: Icons.air,
          accentColor: Colors.cyan,
          isEnabled: _flowmeterCommOn,
          onToggle: (val) async {
            setState(() => _flowmeterCommOn = val);
            final ok = await control.setFlowmeterComm(val);
            if (context.mounted) _showFeedback(context, ok, "Flowmeter Comm ${val ? 'ON' : 'OFF'}");
          },
          isBusy: control.isBusy,
          applyButtonLabel: "Apply Flow Setpoint",
          onApply: () async {
            final sp = double.tryParse(_flowSetpointController.text) ?? 0.0;
            final ok = await control.setFlowSetpoint(sp);
            if (context.mounted) _showFeedback(context, ok, "Flow Setpoint ($sp L/min)");
          },
          children: [
            // Live Status indicator badge
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
              decoration: BoxDecoration(
                color: flowmeterState.isConnectedAndActive
                    ? Colors.cyan.shade50
                    : flowmeterState.isDisconnected
                        ? Colors.amber.shade50
                        : Colors.grey.shade100,
                borderRadius: BorderRadius.circular(8),
                border: Border.all(
                  color: flowmeterState.isConnectedAndActive
                      ? Colors.cyan.shade300
                      : flowmeterState.isDisconnected
                          ? Colors.amber.shade300
                          : Colors.grey.shade300,
                ),
              ),
              child: Row(
                children: [
                  Icon(
                    flowmeterState.isConnectedAndActive
                        ? Icons.air
                        : flowmeterState.isDisconnected
                            ? Icons.wifi_tethering_off_outlined
                            : Icons.sensors_off_outlined,
                    size: 16,
                    color: flowmeterState.isConnectedAndActive
                        ? Colors.cyan.shade800
                        : flowmeterState.isDisconnected
                            ? Colors.amber.shade900
                            : Colors.grey.shade700,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      flowmeterState.statusLabel,
                      style: TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.bold,
                        color: flowmeterState.isConnectedAndActive
                            ? Colors.cyan.shade800
                            : flowmeterState.isDisconnected
                                ? Colors.amber.shade900
                                : Colors.grey.shade700,
                      ),
                    ),
                  ),
                  if (flowmeterState.isConnectedAndActive)
                    Text(
                      flowmeterState.formattedFlowRate,
                      style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 13),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 12),

            // Flow Setpoint Input
            TextField(
              controller: _flowSetpointController,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: const InputDecoration(
                labelText: "Gas Flow Setpoint (L/min)",
                hintText: "0.0",
                isDense: true,
                border: OutlineInputBorder(),
              ),
              enabled: _flowmeterCommOn,
            ),
            const SizedBox(height: 12),

            // Gas Valves toggles row
            Row(
              children: [
                Expanded(
                  child: SwitchListTile(
                    title: const Text("Valve 1 (Air)", style: TextStyle(fontSize: 13, fontWeight: FontWeight.w500)),
                    subtitle: Text(_valve1On ? "OPEN" : "CLOSED", style: const TextStyle(fontSize: 11)),
                    value: _valve1On,
                    activeThumbColor: Colors.green.shade700,
                    dense: true,
                    contentPadding: EdgeInsets.zero,
                    onChanged: _flowmeterCommOn
                        ? (val) async {
                            setState(() => _valve1On = val);
                            final ok = await control.setFlowValves(valve1: val);
                            if (context.mounted) _showFeedback(context, ok, "Valve 1 ${val ? 'OPEN' : 'CLOSED'}");
                          }
                        : null,
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: SwitchListTile(
                    title: const Text("Valve 2 (N₂)", style: TextStyle(fontSize: 13, fontWeight: FontWeight.w500)),
                    subtitle: Text(_valve2On ? "OPEN" : "CLOSED", style: const TextStyle(fontSize: 11)),
                    value: _valve2On,
                    activeThumbColor: Colors.blue.shade700,
                    dense: true,
                    contentPadding: EdgeInsets.zero,
                    onChanged: _flowmeterCommOn
                        ? (val) async {
                            setState(() => _valve2On = val);
                            final ok = await control.setFlowValves(valve2: val);
                            if (context.mounted) _showFeedback(context, ok, "Valve 2 ${val ? 'OPEN' : 'CLOSED'}");
                          }
                        : null,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),

            // Emergency Gas Cutoff Quick Action
            Align(
              alignment: Alignment.centerRight,
              child: OutlinedButton.icon(
                icon: const Icon(Icons.block, size: 16, color: Colors.red),
                label: const Text("Emergency Gas Cutoff", style: TextStyle(color: Colors.red)),
                onPressed: (_flowmeterCommOn && !control.isBusy)
                    ? () async {
                        setState(() {
                          _valve1On = false;
                          _valve2On = false;
                          _flowSetpointController.text = "0.0";
                        });
                        final ok = await control.stopFlow();
                        if (context.mounted) _showFeedback(context, ok, "Emergency Gas Cutoff");
                      }
                    : null,
              ),
            ),
          ],
        ),

        const SizedBox(height: 12),

        // ====================================================
        // 11. FLASK AGITATOR & STIRRER (EXTERNAL)
        // ====================================================
        ControlSectionCard(
          title: "Flask Agitator & Stirrer (External)",
          icon: Icons.rotate_right,
          accentColor: Colors.deepPurple,
          isEnabled: agitatorState.isOnline,
          isBusy: control.isBusy,
          applyButtonLabel: "Start Agitator",
          onApply: () async {
            final pct = int.tryParse(_agitatorPercentController.text) ?? _agitatorPercent;
            final ok = await control.setAgitatorState(
              on: true,
              speedPercent: pct,
              direction: _agitatorDir,
            );
            if (context.mounted) _showFeedback(context, ok, "Agitator START ($pct%)");
          },
          children: [
            // Live Status indicator badge
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
              decoration: BoxDecoration(
                color: agitatorState.isConnectedAndActive
                    ? Colors.deepPurple.shade50
                    : Colors.grey.shade100,
                borderRadius: BorderRadius.circular(8),
                border: Border.all(
                  color: agitatorState.isConnectedAndActive
                      ? Colors.deepPurple.shade300
                      : Colors.grey.shade300,
                ),
              ),
              child: Row(
                children: [
                  Icon(
                    agitatorState.isConnectedAndActive
                        ? Icons.rotate_right
                        : Icons.wifi_tethering_off_outlined,
                    size: 16,
                    color: agitatorState.isConnectedAndActive
                        ? Colors.deepPurple.shade800
                        : Colors.grey.shade700,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      agitatorState.statusLabel,
                      style: TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.bold,
                        color: agitatorState.isConnectedAndActive
                            ? Colors.deepPurple.shade800
                            : Colors.grey.shade700,
                      ),
                    ),
                  ),
                  if (agitatorState.isCommandPending)
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                      margin: const EdgeInsets.only(right: 8),
                      decoration: BoxDecoration(
                        color: Colors.deepPurple.shade100,
                        borderRadius: BorderRadius.circular(4),
                      ),
                      child: const Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          SizedBox(
                            width: 10,
                            height: 10,
                            child: CircularProgressIndicator(strokeWidth: 1.5, color: Colors.deepPurple),
                          ),
                          SizedBox(width: 4),
                          Text("PENDING", style: TextStyle(fontSize: 10, fontWeight: FontWeight.bold, color: Colors.deepPurple)),
                        ],
                      ),
                    ),
                  if (agitatorState.isConnectedAndActive)
                    Text(
                      agitatorState.formattedPercent,
                      style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 13),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 12),

            // Rotation Speed and Direction
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _agitatorPercentController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(
                      labelText: "Rotation Speed (%)",
                      hintText: "80",
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                    onChanged: (val) {
                      final parsed = int.tryParse(val);
                      if (parsed != null) {
                        setState(() => _agitatorPercent = parsed.clamp(0, 100));
                      }
                    },
                  ),
                ),
                const SizedBox(width: 12),
                SegmentedButton<int>(
                  segments: const [
                    ButtonSegment(value: 1, label: Text("CW"), icon: Icon(Icons.rotate_right, size: 16)),
                    ButtonSegment(value: 0, label: Text("CCW"), icon: Icon(Icons.rotate_left, size: 16)),
                  ],
                  selected: {_agitatorDir},
                  onSelectionChanged: (set) {
                    setState(() => _agitatorDir = set.first);
                  },
                ),
              ],
            ),
            const SizedBox(height: 12),

            // Mode switches
            SwitchListTile(
              title: const Text("Auto Foam Response", style: TextStyle(fontSize: 13, fontWeight: FontWeight.w500)),
              subtitle: Text(
                _agitatorAuto ? "Hub automatically triggers agitator during foam events" : "Disabled",
                style: const TextStyle(fontSize: 11),
              ),
              value: _agitatorAuto,
              activeThumbColor: Colors.deepPurple.shade700,
              dense: true,
              contentPadding: EdgeInsets.zero,
              onChanged: (val) async {
                setState(() => _agitatorAuto = val);
                final ok = await control.setAgitatorSettings(autoFoam: val);
                if (context.mounted) _showFeedback(context, ok, "Auto Foam ${val ? 'ENABLED' : 'DISABLED'}");
              },
            ),
            SwitchListTile(
              title: const Text("Re-Enable Physical Potentiometer", style: TextStyle(fontSize: 13, fontWeight: FontWeight.w500)),
              subtitle: Text(
                _agitatorReEnablePot ? "Allow physical knob control" : "Ignore knob",
                style: const TextStyle(fontSize: 11),
              ),
              value: _agitatorReEnablePot,
              activeThumbColor: Colors.deepPurple.shade700,
              dense: true,
              contentPadding: EdgeInsets.zero,
              onChanged: (val) async {
                setState(() => _agitatorReEnablePot = val);
                final ok = await control.setAgitatorSettings(reEnablePot: val);
                if (context.mounted) _showFeedback(context, ok, "Potentiometer ${val ? 'RE-ENABLED' : 'DISABLED'}");
              },
            ),
            const SizedBox(height: 8),

            // Quick Stop Agitator (Safe Stop with Potentiometer Lockout)
            Align(
              alignment: Alignment.centerRight,
              child: OutlinedButton.icon(
                icon: const Icon(Icons.stop, size: 16, color: Colors.red),
                label: const Text("Stop Agitator", style: TextStyle(color: Colors.red)),
                onPressed: (!control.isBusy)
                    ? () async {
                        final ok = await control.safeStopAgitator();
                        if (context.mounted) _showFeedback(context, ok, "Agitator Safe STOP (Pot Locked)");
                      }
                    : null,
              ),
            ),
          ],
        ),

        const SizedBox(height: 12),

        // ====================================================
        // 12. PERISTALTIC FEED PUMP (EXTERNAL)
        // ====================================================
        ControlSectionCard(
          title: "Peristaltic Feed Pump (External)",
          icon: Icons.water_drop,
          accentColor: Colors.orange.shade800,
          isEnabled: _pumpCommOn,
          onToggle: (val) async {
            setState(() => _pumpCommOn = val);
            final ok = await control.setPumpComm(val);
            if (context.mounted) _showFeedback(context, ok, "Pump Comm ${val ? 'ON' : 'OFF'}");
          },
          isBusy: control.isBusy,
          applyButtonLabel: "Apply Feed Profile",
          onApply: _pumpValidationError == null
              ? () async {
                  final spec = _buildCurrentPumpSpec();
                  final ok = await control.applyPumpProfile(spec);
                  if (context.mounted) {
                    _showFeedback(context, ok, "Profile (${spec.mode.label}) applied");
                  }
                }
              : () {
                  ScaffoldMessenger.of(context).showSnackBar(
                    SnackBar(
                      content: Text(_pumpValidationError ?? "Invalid profile parameters"),
                      backgroundColor: Colors.red.shade800,
                      duration: const Duration(seconds: 2),
                    ),
                  );
                },
          children: [
            // Live Status indicator badge
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
              decoration: BoxDecoration(
                color: pumpState.isConnectedAndActive
                    ? Colors.orange.shade50
                    : pumpState.isDisconnected
                        ? Colors.amber.shade50
                        : Colors.grey.shade100,
                borderRadius: BorderRadius.circular(8),
                border: Border.all(
                  color: pumpState.isConnectedAndActive
                      ? Colors.orange.shade300
                      : pumpState.isDisconnected
                          ? Colors.amber.shade300
                          : Colors.grey.shade300,
                ),
              ),
              child: Row(
                children: [
                  Icon(
                    pumpState.isDosing
                        ? Icons.water_drop
                        : pumpState.isWaitingWindow
                            ? Icons.schedule
                            : pumpState.isConnectedAndActive
                                ? Icons.pause_circle_outline
                                : pumpState.isDisconnected
                                    ? Icons.wifi_tethering_off_outlined
                                    : Icons.sensors_off_outlined,
                    size: 16,
                    color: pumpState.isConnectedAndActive
                        ? Colors.orange.shade900
                        : pumpState.isDisconnected
                            ? Colors.amber.shade900
                            : Colors.grey.shade700,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      pumpState.statusLabel,
                      style: TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.bold,
                        color: pumpState.isConnectedAndActive
                            ? Colors.orange.shade900
                            : pumpState.isDisconnected
                                ? Colors.amber.shade900
                                : Colors.grey.shade700,
                      ),
                    ),
                  ),
                  if (pumpState.isCommandPending)
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                      margin: const EdgeInsets.only(right: 8),
                      decoration: BoxDecoration(
                        color: Colors.orange.shade100,
                        borderRadius: BorderRadius.circular(4),
                      ),
                      child: const Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          SizedBox(
                            width: 10,
                            height: 10,
                            child: CircularProgressIndicator(strokeWidth: 1.5, color: Colors.orange),
                          ),
                          SizedBox(width: 4),
                          Text("PENDING", style: TextStyle(fontSize: 10, fontWeight: FontWeight.bold, color: Colors.orange)),
                        ],
                      ),
                    ),
                  if (pumpState.isConnectedAndActive)
                    Text(
                      pumpState.formattedFlow,
                      style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 13),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 12),

            // Mode Selector
            DropdownButtonFormField<PeristalticPumpMode>(
              initialValue: _selectedPumpMode,
              decoration: const InputDecoration(
                labelText: "Feeding Profile Mode",
                isDense: true,
                border: OutlineInputBorder(),
              ),
              items: const [
                DropdownMenuItem(value: PeristalticPumpMode.constant, child: Text("Constant Flow (Q = λ)")),
                DropdownMenuItem(value: PeristalticPumpMode.linear, child: Text("Linear Profile (Q = λ + φ·t)")),
                DropdownMenuItem(value: PeristalticPumpMode.exponential, child: Text("Exponential Profile (Q = λ·e^(φ·t))")),
                DropdownMenuItem(value: PeristalticPumpMode.polynomial, child: Text("Polynomial Profile (p0 + p1·t + ...)")),
                DropdownMenuItem(value: PeristalticPumpMode.piecewise, child: Text("Piecewise Segments (t, Q points)")),
              ],
              onChanged: _pumpCommOn
                  ? (val) {
                      if (val != null) {
                        setState(() => _selectedPumpMode = val);
                        _recalculatePumpPreview();
                      }
                    }
                  : null,
            ),
            const SizedBox(height: 12),

            // Time Window (Start Time & End Time)
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _pumpInitMinutesController,
                    keyboardType: const TextInputType.numberWithOptions(decimal: true),
                    decoration: const InputDecoration(
                      labelText: "Start Time (min)",
                      hintText: "0",
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                    enabled: _pumpCommOn,
                    onChanged: (_) => _recalculatePumpPreview(),
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _pumpFinalMinutesController,
                    keyboardType: const TextInputType.numberWithOptions(decimal: true),
                    decoration: const InputDecoration(
                      labelText: "End Time (min)",
                      hintText: "60",
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                    enabled: _pumpCommOn,
                    onChanged: (_) => _recalculatePumpPreview(),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),

            // Mode-specific inputs
            if (_selectedPumpMode == PeristalticPumpMode.constant) ...[
              TextField(
                controller: _pumpLambdaController,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: const InputDecoration(
                  labelText: "Flow Rate λ (mL/min)",
                  hintText: "1.0",
                  isDense: true,
                  border: OutlineInputBorder(),
                ),
                enabled: _pumpCommOn,
                onChanged: (_) => _recalculatePumpPreview(),
              ),
            ] else if (_selectedPumpMode == PeristalticPumpMode.linear) ...[
              Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: _pumpLambdaController,
                      keyboardType: const TextInputType.numberWithOptions(decimal: true),
                      decoration: const InputDecoration(
                        labelText: "Initial Flow λ (mL/min)",
                        hintText: "1.0",
                        isDense: true,
                        border: OutlineInputBorder(),
                      ),
                      enabled: _pumpCommOn,
                      onChanged: (_) => _recalculatePumpPreview(),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: TextField(
                      controller: _pumpPhiController,
                      keyboardType: const TextInputType.numberWithOptions(decimal: true),
                      decoration: const InputDecoration(
                        labelText: "Slope φ (mL/min²)",
                        hintText: "0.05",
                        isDense: true,
                        border: OutlineInputBorder(),
                      ),
                      enabled: _pumpCommOn,
                      onChanged: (_) => _recalculatePumpPreview(),
                    ),
                  ),
                ],
              ),
            ] else if (_selectedPumpMode == PeristalticPumpMode.exponential) ...[
              Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: _pumpLambdaController,
                      keyboardType: const TextInputType.numberWithOptions(decimal: true),
                      decoration: const InputDecoration(
                        labelText: "Initial Flow λ (mL/min)",
                        hintText: "1.0",
                        isDense: true,
                        border: OutlineInputBorder(),
                      ),
                      enabled: _pumpCommOn,
                      onChanged: (_) => _recalculatePumpPreview(),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: TextField(
                      controller: _pumpPhiController,
                      keyboardType: const TextInputType.numberWithOptions(decimal: true),
                      decoration: const InputDecoration(
                        labelText: "Growth Factor φ (1/min)",
                        hintText: "0.05",
                        isDense: true,
                        border: OutlineInputBorder(),
                      ),
                      enabled: _pumpCommOn,
                      onChanged: (_) => _recalculatePumpPreview(),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 4),
              Text(
                "Substrate feed model: φ represents specific growth rate (μ).",
                style: Theme.of(context).textTheme.bodySmall?.copyWith(color: Colors.grey.shade600, fontSize: 11),
              ),
            ] else if (_selectedPumpMode == PeristalticPumpMode.polynomial) ...[
              TextField(
                controller: _pumpPolyCoeffsController,
                decoration: const InputDecoration(
                  labelText: "Coefficients p0, p1, p2... (comma-separated)",
                  hintText: "1.0, 0.05",
                  isDense: true,
                  border: OutlineInputBorder(),
                ),
                enabled: _pumpCommOn,
                onChanged: (_) => _recalculatePumpPreview(),
              ),
              const SizedBox(height: 4),
              Text(
                "Evaluates Q(t') = p0 + p1·t' + p2·(t')² + ... (max 21 terms)",
                style: Theme.of(context).textTheme.bodySmall?.copyWith(color: Colors.grey.shade600, fontSize: 11),
              ),
            ] else if (_selectedPumpMode == PeristalticPumpMode.piecewise) ...[
              TextField(
                controller: _pumpPiecewiseTimesController,
                decoration: const InputDecoration(
                  labelText: "Time Points (min, comma-separated)",
                  hintText: "0, 30, 60",
                  isDense: true,
                  border: OutlineInputBorder(),
                ),
                enabled: _pumpCommOn,
                onChanged: (_) => _recalculatePumpPreview(),
              ),
              const SizedBox(height: 8),
              TextField(
                controller: _pumpPiecewiseFlowsController,
                decoration: const InputDecoration(
                  labelText: "Flow Points (mL/min, comma-separated)",
                  hintText: "1.0, 2.5, 0.5",
                  isDense: true,
                  border: OutlineInputBorder(),
                ),
                enabled: _pumpCommOn,
                onChanged: (_) => _recalculatePumpPreview(),
              ),
              const SizedBox(height: 4),
              Text(
                "Linear interpolation between sequential coordinates (t0 must start at 0.0).",
                style: Theme.of(context).textTheme.bodySmall?.copyWith(color: Colors.grey.shade600, fontSize: 11),
              ),
            ],

            const SizedBox(height: 12),

            // Live Mathematical Simulation Preview Box
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: _pumpValidationError == null
                    ? Colors.orange.shade50.withValues(alpha: 0.5)
                    : Colors.red.shade50.withValues(alpha: 0.5),
                borderRadius: BorderRadius.circular(10),
                border: Border.all(
                  color: _pumpValidationError == null
                      ? Colors.orange.shade300
                      : Colors.red.shade300,
                ),
              ),
              child: _pumpValidationError == null && _pumpPreview != null
                  ? Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Row(
                          children: [
                            Icon(Icons.calculate_outlined, size: 16, color: Colors.orange.shade900),
                            const SizedBox(width: 6),
                            Text(
                              "PROFILE SIMULATION PREVIEW",
                              style: TextStyle(
                                fontSize: 11,
                                fontWeight: FontWeight.bold,
                                letterSpacing: 0.8,
                                color: Colors.orange.shade900,
                              ),
                            ),
                          ],
                        ),
                        const SizedBox(height: 8),
                        Row(
                          mainAxisAlignment: MainAxisAlignment.spaceBetween,
                          children: [
                            Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                const Text("Est. Total Dose", style: TextStyle(fontSize: 11, color: Colors.black54)),
                                const SizedBox(height: 2),
                                Text(
                                  "${_pumpPreview!.totalVolume.toStringAsFixed(1)} mL",
                                  style: TextStyle(fontSize: 15, fontWeight: FontWeight.bold, color: Colors.orange.shade900),
                                ),
                              ],
                            ),
                            Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                const Text("Peak Flow Rate", style: TextStyle(fontSize: 11, color: Colors.black54)),
                                const SizedBox(height: 2),
                                Text(
                                  "${_pumpPreview!.peakFlow.toStringAsFixed(2)} mL/min",
                                  style: const TextStyle(fontSize: 15, fontWeight: FontWeight.bold),
                                ),
                              ],
                            ),
                            Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                const Text("Average Flow", style: TextStyle(fontSize: 11, color: Colors.black54)),
                                const SizedBox(height: 2),
                                Text(
                                  "${_pumpPreview!.averageFlow.toStringAsFixed(2)} mL/min",
                                  style: const TextStyle(fontSize: 15, fontWeight: FontWeight.bold),
                                ),
                              ],
                            ),
                          ],
                        ),
                      ],
                    )
                  : Row(
                      children: [
                        Icon(Icons.warning_amber_rounded, size: 18, color: Colors.red.shade800),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Text(
                            _pumpValidationError ?? "Invalid profile parameters",
                            style: TextStyle(color: Colors.red.shade800, fontSize: 12, fontWeight: FontWeight.w500),
                          ),
                        ),
                      ],
                    ),
            ),
            const SizedBox(height: 8),

            // Quick Stop Pump Action
            Align(
              alignment: Alignment.centerRight,
              child: OutlinedButton.icon(
                icon: const Icon(Icons.stop, size: 16, color: Colors.red),
                label: const Text("Stop Pump", style: TextStyle(color: Colors.red)),
                onPressed: (!control.isBusy)
                    ? () async {
                        final ok = await control.stopPump();
                        if (context.mounted) _showFeedback(context, ok, "Pump STOP");
                      }
                    : null,
              ),
            ),
          ],
        ),
      ],
    );
  }
}
