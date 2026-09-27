import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../../models/external_bath_state.dart';
import '../../providers/device_control_provider.dart';
import '../../providers/telemetry_provider.dart';
import '../../theme/app_theme.dart';
import '../../widgets/control_section_card.dart';
import '../../widgets/external_bath_card.dart';
import '../../widgets/hub_sync.dart';
import '../../widgets/route_selector.dart';
import '../../widgets/status_strip.dart';

/// Reactor temperature: route (original module or external C404 bath), bath link and
/// the reactor setpoint. On the external route the setpoint is the reference of the
/// Hub cascade; turning it off stops the cascade and leaves the C404 in manual at its
/// last setpoint (the bath is never switched off by software).
class TemperatureSection extends StatefulWidget {
  const TemperatureSection({super.key});

  @override
  State<TemperatureSection> createState() => _TemperatureSectionState();
}

class _TemperatureSectionState extends State<TemperatureSection> with HubSync {
  final _setpoint = TextEditingController(text: "37.0");
  bool _on = false;
  bool _editing = false;

  @override
  void dispose() {
    _setpoint.dispose();
    super.dispose();
  }

  Future<void> _apply(DeviceControlProvider control, bool viaBath) async {
    final sp = parseNumber(_setpoint.text);
    if (_on && (sp == null || sp <= 0.0 || sp > 100.0)) {
      showCommandFeedback(context, false, "Setpoint inválido (use 0,1 a 100 °C)");
      return;
    }
    final ok = await control.setTemperature(enabled: _on, setpoint: sp ?? 0.0);
    if (!mounted) return;
    if (ok) setState(() => _editing = false);
    final what = _on
        ? "Temperatura ${sp!.toStringAsFixed(1)} °C"
        : viaBath
            ? "Cascata desligada (C404 em manual no último SP)"
            : "Temperatura desligada";
    showCommandFeedback(context, ok, what);
  }

  Future<void> _changeRoute(DeviceControlProvider control, bool toBath) async {
    final confirmed = await confirmRouteChange(
      context,
      title: toBath ? "Usar o banho externo?" : "Usar a placa controladora?",
      message: "O Hub desliga o controle de temperatura ao trocar a via e libera o aquecedor "
          "da placa original.${toBath ? "" : " O banho externo é parado e o C404 fica em manual no último SP."}"
          "\n\nDepois da troca, envie um novo setpoint.",
    );
    if (!confirmed || !mounted) return;
    final ok = await control.setTemperatureRoute(externalBath: toBath);
    if (!mounted) return;
    if (ok) setState(() => _on = false);
    showCommandFeedback(context, ok, toBath ? "Temperatura pelo banho externo" : "Temperatura pela placa");
  }

  Future<void> _toggleBathComm(DeviceControlProvider control, ExternalBathState bath, bool on) async {
    if (!on && bath.owned) {
      final confirmed = await showDialog<bool>(
        context: context,
        builder: (ctx) => AlertDialog(
          title: const Text("Desligar a comunicação?"),
          content: const Text(
            "O Hub está controlando o banho. Desligar a comunicação para a cascata e deixa "
            "o C404 em manual no último SP.",
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text("Cancelar")),
            FilledButton(onPressed: () => Navigator.pop(ctx, true), child: const Text("Desligar")),
          ],
        ),
      );
      if (confirmed != true || !mounted) return;
    }
    final ok = await control.setBathComm(on);
    if (mounted) showCommandFeedback(context, ok, "Comunicação com o banho ${on ? 'ligada' : 'desligada'}");
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final telemetryProv = context.watch<TelemetryProvider>();
    final t = telemetryProv.telemetry;
    final bath = telemetryProv.bathState;
    final viaBath = bath.viaBath;

    syncFromHub("temp", (t.tempSetpointCommanded, t.tempSetpoint), editing: _editing, apply: () {
      _on = t.tempSetpointCommanded;
      if (t.tempSetpoint != null) _setpoint.text = t.tempSetpoint!.toStringAsFixed(1);
    });

    final reading = t.hasValidTemperature ? "${t.temperature.toStringAsFixed(1)} °C" : "-- °C";
    final status = t.tempSetpointCommanded && t.tempSetpoint != null
        ? "Reator $reading · SP ${t.tempSetpoint!.toStringAsFixed(1)} °C${viaBath ? ' · banho' : ''}"
        : "Reator $reading · desligada${viaBath ? ' · banho' : ''}";

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        ControlSectionCard(
          title: "Temperatura",
          status: status,
          icon: Icons.thermostat,
          accentColor: AppColors.temperature,
          isEnabled: _on,
          onToggle: (v) => setState(() {
            _on = v;
            _editing = true;
          }),
          isBusy: control.isBusy,
          onApply: () => _apply(control, viaBath),
          // Always shown; usable once the Hub reports the route (Hub 10.5+ and connected).
          header: RouteSelector(
            alternative: viaBath,
            boardLabel: "Placa",
            alternativeLabel: "Banho externo",
            alternativeIcon: Icons.hot_tub_outlined,
            onChanged: control.isBusy || !bath.hasTelemetry ? null : (toBath) => _changeRoute(control, toBath),
          ),
          children: [
            if (viaBath) ...[
              SwitchListTile(
                contentPadding: EdgeInsets.zero,
                dense: true,
                title: const Text("Comunicação com o banho"),
                subtitle: Text(bath.online ? "C404 online" : "C404 offline"),
                value: bath.commEnabled,
                onChanged: control.isBusy ? null : (v) => _toggleBathComm(control, bath, v),
              ),
              const SizedBox(height: 4),
            ],
            if (viaBath)
              const HelpText(
                "Banho externo: o setpoint é a temperatura desejada no reator; o Hub ajusta o C404. "
                "Desligar para a cascata, mas o C404 fica ligado em manual no último SP.",
              ),
            NumberField(
              controller: _setpoint,
              label: viaBath ? "Setpoint do reator" : "Setpoint",
              unit: "°C",
              enabled: _on,
              onChanged: (_) => _editing = true,
            ),
          ],
        ),
        if (bath.hasTelemetry && (viaBath || bath.online)) ...[
          const SizedBox(height: 10),
          ExternalBathCard(
            state: bath,
            reactorTemperature: t.temperature,
            busy: control.isBusy,
            onStop: () async {
              final ok = await control.stopBath();
              if (context.mounted) showCommandFeedback(context, ok, "Parada do banho (C404 em manual no último SP)");
            },
            onResetFault: () async {
              final ok = await control.resetBathFault();
              if (context.mounted) showCommandFeedback(context, ok, "Reset da falha da cascata");
            },
            onModeChanged: (automatic) async {
              final ok = await control.setBathMode(automatic: automatic);
              if (context.mounted) {
                showCommandFeedback(context, ok, "Guarda do C404 ${automatic ? 'automática' : 'manual'}");
              }
            },
          ),
        ],
      ],
    );
  }
}
