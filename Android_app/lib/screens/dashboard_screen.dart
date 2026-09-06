import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../providers/telemetry_provider.dart';
import '../providers/device_control_provider.dart';
import '../widgets/servo_monitor_card.dart';
import '../widgets/metric_tile.dart';
import '../widgets/distance_sensor_card.dart';
import '../widgets/biomass_sensor_card.dart';
import '../widgets/flowmeter_card.dart';
import '../widgets/flask_agitator_card.dart';
import '../widgets/peristaltic_pump_card.dart';

enum DeviceCategory { internal, external }

class DashboardScreen extends StatefulWidget {
  const DashboardScreen({super.key});

  @override
  State<DashboardScreen> createState() => _DashboardScreenState();
}

class _DashboardScreenState extends State<DashboardScreen> {
  DeviceCategory _selectedCategory = DeviceCategory.internal;

  @override
  Widget build(BuildContext context) {
    final telemetryProv = context.watch<TelemetryProvider>();
    final controlProv = context.read<DeviceControlProvider>();
    final telemetry = telemetryProv.telemetry;
    final servo = telemetryProv.servoState;

    final tempVal = telemetry.hasValidTemperature
        ? telemetry.temperature.toStringAsFixed(1)
        : "--";
    final phVal = telemetryProv.calibratedPh >= 0.0
        ? telemetryProv.calibratedPh.toStringAsFixed(2)
        : "--";
    final oxyVal = telemetryProv.calibratedOxygen >= 0.0
        ? telemetryProv.calibratedOxygen.toStringAsFixed(2)
        : "--";
    final pressureVal = telemetry.pressure.toStringAsFixed(1);
    final antifoamVal = telemetry.antifoam > 0.5 ? "DETECTED" : "CLEAR";

    return RefreshIndicator(
      onRefresh: () async {
        // Telemetry updates automatically via background polling
        await Future.delayed(const Duration(milliseconds: 300));
      },
      child: ListView(
        padding: const EdgeInsets.all(12.0),
        children: [
          // Navigation Submenu: Internal vs External
          Padding(
            padding: const EdgeInsets.only(bottom: 12.0),
            child: SegmentedButton<DeviceCategory>(
              segments: const [
                ButtonSegment<DeviceCategory>(
                  value: DeviceCategory.internal,
                  icon: Icon(Icons.biotech),
                  label: Text("Biorreator (Interno)"),
                ),
                ButtonSegment<DeviceCategory>(
                  value: DeviceCategory.external,
                  icon: Icon(Icons.devices_other),
                  label: Text("Periféricos Externos"),
                ),
              ],
              selected: {_selectedCategory},
              onSelectionChanged: (newSelection) {
                setState(() {
                  _selectedCategory = newSelection.first;
                });
              },
            ),
          ),
          if (_selectedCategory == DeviceCategory.internal) ...[
            // 1. Servo Monitor Card (Primary Actuator)
            ServoMonitorCard(
            servoState: servo,
            onStopPressed: () async {
              final ok = await controlProv.stopMotor();
              if (context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(
                  SnackBar(
                    content: Text(ok ? "Motor stopped" : "Failed to stop motor"),
                    duration: const Duration(seconds: 2),
                  ),
                );
              }
            },
          ),
          const SizedBox(height: 12),

          // 2. Internal Sensors Grid
          Row(
            children: [
              Expanded(
                child: MetricTile(
                  title: "Temperature",
                  value: tempVal,
                  unit: "°C",
                  subtext: "Range: 0-100°C",
                  icon: Icons.thermostat,
                  accentColor: Colors.redAccent,
                  isActive: telemetry.hasValidTemperature,
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: MetricTile(
                  title: "pH (Calibrated)",
                  value: phVal,
                  unit: "pH",
                  subtext: telemetry.hasValidPh
                      ? "Raw: ${telemetry.rawPh.toInt()} ADC"
                      : "Sensor absent",
                  icon: Icons.science_outlined,
                  accentColor: Colors.blueAccent,
                  isActive: telemetryProv.calibratedPh >= 0.0,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),

          Row(
            children: [
              Expanded(
                child: MetricTile(
                  title: "Dissolved O₂",
                  value: oxyVal,
                  unit: "mg/L",
                  subtext: telemetry.hasValidOxygen
                      ? "Raw: ${telemetry.rawOxygen.toInt()} ADC"
                      : "Sensor absent",
                  icon: Icons.bubble_chart_outlined,
                  accentColor: Colors.teal,
                  isActive: telemetryProv.calibratedOxygen >= 0.0,
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: MetricTile(
                  title: "Pressure",
                  value: pressureVal,
                  unit: "mmHg",
                  subtext: "Vessel pressure",
                  icon: Icons.speed,
                  accentColor: Colors.orangeAccent,
                  isActive: true,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),

          Row(
            children: [
              Expanded(
                child: MetricTile(
                  title: "Foam Sensor",
                  value: antifoamVal,
                  subtext: "Level: ${telemetry.antifoam.toStringAsFixed(0)}",
                  icon: Icons.waves,
                  accentColor: Colors.deepOrangeAccent,
                  isActive: telemetry.antifoam > 0.5,
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: MetricTile(
                  title: "Sensor Board",
                  value: telemetry.sensorCommOk ? "ACTIVE" : "OFFLINE",
                  subtext: "OpenTEC UART link",
                  icon: Icons.memory,
                  accentColor: telemetry.sensorCommOk ? Colors.green : Colors.red,
                  isActive: telemetry.sensorCommOk,
                ),
              ),
            ],
          ),

          const SizedBox(height: 16),

          // 3. Hub System Diagnostics
          Card(
            elevation: 1,
            color: Theme.of(context).colorScheme.surfaceContainerHighest.withValues(alpha: 0.3),
            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 14.0, vertical: 10.0),
              child: Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Row(
                    children: [
                      const Icon(Icons.info_outline, size: 16, color: Colors.grey),
                      const SizedBox(width: 6),
                      Text(
                        "Hub ${telemetry.hubFirmwareVersion.isNotEmpty ? telemetry.hubFirmwareVersion : 'v10'} (Proto: ${telemetry.hubProtocolVersion})",
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    ],
                  ),
                  Text(
                    "Stations: ${telemetry.hubStations} • Time: ${telemetry.time.toStringAsFixed(0)}s",
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
                ],
              ),
            ),
          ),
        ] else ...[
          // ==========================================
          // PERIFÉRICOS EXTERNOS (ESP32 Wi-Fi NODES)
          // ==========================================
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
            margin: const EdgeInsets.only(bottom: 12),
            decoration: BoxDecoration(
              color: Theme.of(context).colorScheme.primaryContainer.withValues(alpha: 0.25),
              borderRadius: BorderRadius.circular(10),
              border: Border.all(color: Theme.of(context).colorScheme.primary.withValues(alpha: 0.3)),
            ),
            child: Row(
              children: [
                Icon(Icons.wifi_tethering, size: 20, color: Theme.of(context).colorScheme.primary),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    "Módulos Periféricos Sem Fio (ESP32 Wi-Fi Nodes)",
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          fontWeight: FontWeight.bold,
                          color: Theme.of(context).colorScheme.primary,
                        ),
                  ),
                ),
                Text(
                  "5 nós integrados",
                  style: Theme.of(context).textTheme.labelSmall?.copyWith(
                        color: Theme.of(context).colorScheme.outline,
                      ),
                ),
              ],
            ),
          ),

          // 1. External Peristaltic Pump Card (Em destaque no topo dos periféricos!)
          PeristalticPumpCard(
            state: telemetryProv.pumpState,
            onStop: () async {
              final ok = await controlProv.stopPump();
              if (context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(
                  SnackBar(
                    content: Text(ok ? "Pump dosing stopped" : "Failed to stop pump"),
                    duration: const Duration(seconds: 2),
                  ),
                );
              }
            },
          ),
          const SizedBox(height: 12),

          // 2. External Flowmeter & Gas Sparging Card
          FlowmeterCard(
            state: telemetryProv.flowmeterState,
            onStopFlow: () async {
              final ok = await controlProv.stopFlow();
              if (context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(
                  SnackBar(
                    content: Text(ok ? "Gas flow stopped" : "Failed to stop gas flow"),
                    duration: const Duration(seconds: 2),
                  ),
                );
              }
            },
          ),
          const SizedBox(height: 12),

          // 3. External Flask Agitator Card
          FlaskAgitatorCard(
            state: telemetryProv.agitatorState,
            onStart: () async {
              final ok = await controlProv.setAgitatorState(on: true);
              if (context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(
                  SnackBar(
                    content: Text(ok ? "Flask agitator started" : "Failed to start agitator"),
                    duration: const Duration(seconds: 2),
                  ),
                );
              }
            },
            onStop: () async {
              final ok = await controlProv.safeStopAgitator();
              if (context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(
                  SnackBar(
                    content: Text(ok ? "Flask agitator stopped" : "Failed to stop agitator"),
                    duration: const Duration(seconds: 2),
                  ),
                );
              }
            },
          ),
          const SizedBox(height: 12),

          // 4. External Biomass Sensor Card
          BiomassSensorCard(
            state: telemetryProv.biomassState,
            onStartAcquisition: () async {
              final ok = await controlProv.startBiomassAcquisition();
              if (context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(
                  SnackBar(
                    content: Text(ok ? "Biomass acquisition started" : "Failed to start biomass acquisition"),
                    duration: const Duration(seconds: 2),
                  ),
                );
              }
            },
            onStopAcquisition: () async {
              final ok = await controlProv.stopBiomassAcquisition();
              if (context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(
                  SnackBar(
                    content: Text(ok ? "Biomass acquisition stopped" : "Failed to stop biomass acquisition"),
                    duration: const Duration(seconds: 2),
                  ),
                );
              }
            },
            onZeroBlank: () async {
              final ok = await controlProv.zeroBiomassBlank();
              if (context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(
                  SnackBar(
                    content: Text(ok ? "Biomass zero blank command sent" : "Failed to zero blank"),
                    duration: const Duration(seconds: 2),
                  ),
                );
              }
            },
          ),
          const SizedBox(height: 12),

          // 5. External Distance Sensor Card
          DistanceSensorCard(
            state: telemetryProv.distanceState,
          ),
          const SizedBox(height: 12),
        ],
      ],
    ),
  );
}
}
