import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../../providers/device_control_provider.dart';
import '../../providers/telemetry_provider.dart';
import '../../theme/app_theme.dart';
import '../../widgets/control_section_card.dart';
import '../../widgets/status_strip.dart';

/// Nutrient pump of the controller board: runs "Dosagem" seconds every cycle.
class NutrientSection extends StatefulWidget {
  const NutrientSection({super.key});

  @override
  State<NutrientSection> createState() => _NutrientSectionState();
}

class _NutrientSectionState extends State<NutrientSection> {
  bool _on = false;
  final _op = TextEditingController(text: "999");
  final _mix = TextEditingController(text: "1");
  final _opCycle = TextEditingController(text: "500");
  final _mixCycle = TextEditingController(text: "1");
  final _speed = TextEditingController(text: "99");

  @override
  void dispose() {
    for (final c in [_op, _mix, _opCycle, _mixCycle, _speed]) {
      c.dispose();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();

    return ControlSectionCard(
      title: "Bomba de nutriente",
      status: _on ? "Ligada · ${_speed.text}%" : "Desligada",
      icon: Icons.water_drop_outlined,
      accentColor: AppColors.nutrient,
      isEnabled: _on,
      onToggle: (v) => setState(() => _on = v),
      isBusy: control.isBusy,
      onApply: () async {
        final values = [_op, _mix, _opCycle, _mixCycle, _speed].map((c) => parseInt(c.text)).toList();
        if (values.any((v) => v == null || v < 0)) {
          showCommandFeedback(context, false, "Valores da bomba de nutriente inválidos");
          return;
        }
        final ok = await control.setNutrientPump(
          enabled: _on,
          opSeconds: values[0]!,
          mixSeconds: values[1]!,
          opCycleMinutes: values[2]!,
          mixCycleMinutes: values[3]!,
          speedPercent: values[4]!,
        );
        if (context.mounted) showCommandFeedback(context, ok, "Bomba de nutriente ${_on ? 'ligada' : 'desligada'}");
      },
      children: [
        FieldRow([
          NumberField(controller: _op, label: "Dosagem", unit: "s", decimal: false, enabled: _on),
          NumberField(controller: _mix, label: "Pausa", unit: "s", decimal: false, enabled: _on),
        ]),
        FieldRow([
          NumberField(controller: _opCycle, label: "Ciclo dosagem", unit: "min", decimal: false, enabled: _on),
          NumberField(controller: _mixCycle, label: "Ciclo pausa", unit: "min", decimal: false, enabled: _on),
          NumberField(controller: _speed, label: "Bomba", unit: "%", decimal: false, enabled: _on),
        ]),
      ],
    );
  }
}

/// Antifoam pump of the controller board, in manual timing.
class AntifoamSection extends StatefulWidget {
  const AntifoamSection({super.key});

  @override
  State<AntifoamSection> createState() => _AntifoamSectionState();
}

class _AntifoamSectionState extends State<AntifoamSection> {
  bool _on = false;
  final _op = TextEditingController(text: "5");
  final _mix = TextEditingController(text: "20");
  final _speed = TextEditingController(text: "99");

  @override
  void dispose() {
    for (final c in [_op, _mix, _speed]) {
      c.dispose();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final foamSensor = context.watch<TelemetryProvider>().telemetry.antifoam > 0.5;

    return ControlSectionCard(
      title: "Bomba de antiespumante",
      status: "${_on ? 'Ligada' : 'Desligada'} · sensor de espuma: ${foamSensor ? 'espuma' : 'livre'}",
      icon: Icons.shield_outlined,
      accentColor: AppColors.antifoam,
      isEnabled: _on,
      onToggle: (v) => setState(() => _on = v),
      isBusy: control.isBusy,
      onApply: () async {
        final values = [_op, _mix, _speed].map((c) => parseInt(c.text)).toList();
        if (values.any((v) => v == null || v < 0)) {
          showCommandFeedback(context, false, "Valores do antiespumante inválidos");
          return;
        }
        final ok = await control.setAntifoamPump(
          enabled: _on,
          opSeconds: values[0]!,
          mixSeconds: values[1]!,
          speedPercent: values[2]!,
        );
        if (context.mounted) showCommandFeedback(context, ok, "Antiespumante ${_on ? 'ligado' : 'desligado'}");
      },
      children: [
        FieldRow([
          NumberField(controller: _op, label: "Dosagem", unit: "s", decimal: false, enabled: _on),
          NumberField(controller: _mix, label: "Pausa", unit: "s", decimal: false, enabled: _on),
          NumberField(controller: _speed, label: "Bomba", unit: "%", decimal: false, enabled: _on),
        ]),
      ],
    );
  }
}

/// Automatic foam response run by the Hub: the distance sensor detects foam below the
/// reference, then the antifoam pump is pulsed and, optionally, the flask agitator
/// (external node) is started with the speed and direction set on its own card.
class FoamResponseSection extends StatefulWidget {
  const FoamResponseSection({super.key});

  @override
  State<FoamResponseSection> createState() => _FoamResponseSectionState();
}

class _FoamResponseSectionState extends State<FoamResponseSection> {
  bool _on = false;
  bool _useAgitator = false;
  final _ref = TextEditingController(text: "100");
  final _delay = TextEditingController(text: "1");
  final _pulse = TextEditingController(text: "1");
  final _interval = TextEditingController(text: "5");

  @override
  void dispose() {
    for (final c in [_ref, _delay, _pulse, _interval]) {
      c.dispose();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final distance = context.watch<TelemetryProvider>().distanceState;
    final ref = parseNumber(_ref.text);
    final foam = _on && distance.isConnectedAndActive && ref != null && distance.distanceMm <= ref;

    final String status;
    final StatusTone tone;
    if (!distance.commEnabled) {
      status = "Sensor de distância desligado";
      tone = StatusTone.off;
    } else if (!distance.isConnectedAndActive) {
      status = "Sensor de distância sem leitura";
      tone = StatusTone.warning;
    } else {
      status = "Distância ${distance.formattedDistance}${foam ? ' · espuma' : ''}";
      tone = foam ? StatusTone.warning : StatusTone.ok;
    }

    return ControlSectionCard(
      title: "Espuma automática",
      status: _on ? status : "Desligada",
      icon: Icons.waves,
      accentColor: AppColors.antifoam,
      isEnabled: _on,
      onToggle: (v) => setState(() => _on = v),
      isBusy: control.isBusy,
      onApply: () async {
        final values = [_ref, _delay, _pulse, _interval].map((c) => parseNumber(c.text)).toList();
        if (values.any((v) => v == null)) {
          showCommandFeedback(context, false, "Valores de espuma inválidos");
          return;
        }
        final ok = await control.setFoamResponse(
          referenceMm: _on ? values[0]! : 0.0,
          startDelaySeconds: values[1]!,
          pulseSeconds: values[2]!,
          intervalSeconds: values[3]!,
          useAgitator: _on && _useAgitator,
        );
        if (context.mounted) {
          showCommandFeedback(context, ok, _on ? "Espuma automática ligada" : "Espuma automática desligada");
        }
      },
      children: [
        StatusStrip(label: status, tone: tone),
        const HelpText(
          "Usa o sensor de distância (ligue-o em Periféricos). Espuma = distância abaixo da "
          "referência; o Hub então pulsa a bomba de antiespumante.",
        ),
        NumberField(controller: _ref, label: "Referência de espuma", unit: "mm", enabled: _on),
        const SizedBox(height: 10),
        FieldRow([
          NumberField(controller: _delay, label: "Atraso", unit: "s", enabled: _on),
          NumberField(controller: _pulse, label: "Pulso", unit: "s", enabled: _on),
          NumberField(controller: _interval, label: "Intervalo", unit: "s", enabled: _on),
        ]),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          dense: true,
          title: const Text("Ligar também o frasco agitador"),
          subtitle: const Text("Usa a velocidade e o sentido do cartão Frasco agitador"),
          value: _useAgitator,
          onChanged: _on ? (v) => setState(() => _useAgitator = v) : null,
        ),
      ],
    );
  }
}
