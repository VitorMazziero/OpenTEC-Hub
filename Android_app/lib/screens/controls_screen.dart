import 'package:flutter/material.dart';
import 'controls/dosing_sections.dart';
import 'controls/peripheral_sections.dart';
import 'controls/process_sections.dart';
import 'controls/pump_section.dart';
import 'controls/temperature_section.dart';

enum DeviceCategory { internal, external }

/// Setpoints of the bioreactor and of the external nodes, one collapsible card each.
/// Both lists stay mounted (IndexedStack) so a half-filled form survives switching tabs.
class ControlsScreen extends StatefulWidget {
  const ControlsScreen({super.key});

  @override
  State<ControlsScreen> createState() => _ControlsScreenState();
}

class _ControlsScreenState extends State<ControlsScreen> {
  DeviceCategory _category = DeviceCategory.internal;

  static const _gap = SizedBox(height: 10);

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(12, 12, 12, 4),
          child: SizedBox(
            width: double.infinity,
            child: SegmentedButton<DeviceCategory>(
              segments: const [
                ButtonSegment(value: DeviceCategory.internal, icon: Icon(Icons.biotech), label: Text("Biorreator")),
                ButtonSegment(value: DeviceCategory.external, icon: Icon(Icons.devices_other), label: Text("Periféricos")),
              ],
              selected: {_category},
              onSelectionChanged: (s) => setState(() => _category = s.first),
            ),
          ),
        ),
        Expanded(
          child: IndexedStack(
            index: _category.index,
            children: [
              ListView(
                key: const PageStorageKey('bioreactor-controls'),
                padding: const EdgeInsets.all(12),
                children: const [
                  TemperatureSection(),
                  _gap,
                  AgitationSection(),
                  _gap,
                  PhSection(),
                  _gap,
                  OxygenSection(),
                  _gap,
                  PressureSection(),
                  _gap,
                  NutrientSection(),
                  _gap,
                  AntifoamSection(),
                  _gap,
                  FoamResponseSection(),
                  SizedBox(height: 24),
                ],
              ),
              ListView(
                key: const PageStorageKey('peripheral-controls'),
                padding: const EdgeInsets.all(12),
                children: const [
                  PumpSection(),
                  _gap,
                  FlowSection(),
                  _gap,
                  FlaskAgitatorSection(),
                  _gap,
                  BiomassSection(),
                  _gap,
                  DistanceSection(),
                  SizedBox(height: 24),
                ],
              ),
            ],
          ),
        ),
      ],
    );
  }
}
