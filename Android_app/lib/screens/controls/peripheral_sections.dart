import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../../providers/device_control_provider.dart';
import '../../providers/telemetry_provider.dart';
import '../../theme/app_theme.dart';
import '../../widgets/control_section_card.dart';
import '../../widgets/hub_sync.dart';
import '../../widgets/status_strip.dart';

StatusTone _tone({required bool disabled, required bool disconnected, required bool active}) {
  if (disabled) return StatusTone.off;
  if (disconnected) return StatusTone.warning;
  return active ? StatusTone.active : StatusTone.ok;
}

/// Distance / level sensor node. Its reference is also the foam threshold.
class DistanceSection extends StatefulWidget {
  const DistanceSection({super.key});

  @override
  State<DistanceSection> createState() => _DistanceSectionState();
}

class _DistanceSectionState extends State<DistanceSection> {
  final _ref = TextEditingController(text: "100");

  @override
  void dispose() {
    _ref.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final s = context.watch<TelemetryProvider>().distanceState;

    return ControlSectionCard(
      title: "Sensor de distância",
      status: s.isConnectedAndActive ? "${s.statusLabel} · ${s.formattedDistance}" : s.statusLabel,
      icon: Icons.radar,
      accentColor: AppColors.distance,
      isEnabled: s.commEnabled,
      onToggle: (v) async {
        final ok = await control.setDistanceSensorComm(v);
        if (context.mounted) showCommandFeedback(context, ok, "Sensor de distância ${v ? 'ligado' : 'desligado'}");
      },
      isBusy: control.isBusy,
      applyButtonLabel: "Aplicar referência",
      onApply: () async {
        final ref = parseNumber(_ref.text);
        if (ref == null || ref < 0) {
          showCommandFeedback(context, false, "Referência inválida");
          return;
        }
        final ok = await control.setDistanceSensorReference(ref);
        if (context.mounted) showCommandFeedback(context, ok, "Referência ${ref.toStringAsFixed(1)} mm");
      },
      children: [
        StatusStrip(
          label: s.statusLabel,
          value: s.isConnectedAndActive ? s.formattedDistance : null,
          tone: _tone(disabled: s.isDisabled, disconnected: s.isDisconnected, active: s.isConnectedAndActive),
        ),
        NumberField(
          controller: _ref,
          label: "Referência de nível / espuma",
          unit: "mm",
          helper: "0 desativa a resposta automática à espuma",
          enabled: s.commEnabled,
        ),
      ],
    );
  }
}

/// Optical biomass sensor node.
class BiomassSection extends StatefulWidget {
  const BiomassSection({super.key});

  @override
  State<BiomassSection> createState() => _BiomassSectionState();
}

class _BiomassSectionState extends State<BiomassSection> {
  final _low = TextEditingController(text: "30000");
  final _high = TextEditingController(text: "60000");
  final _opt = TextEditingController(text: "50000");

  @override
  void dispose() {
    for (final c in [_low, _high, _opt]) {
      c.dispose();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final s = context.watch<TelemetryProvider>().biomassState;
    final busy = control.isBusy;

    Future<void> run(Future<bool> Function() cmd, String what) async {
      final ok = await cmd();
      if (context.mounted) showCommandFeedback(context, ok, what);
    }

    return ControlSectionCard(
      title: "Sensor de biomassa",
      status: s.isAcquiring ? "${s.statusLabel} · ${s.formattedAbs}" : s.statusLabel,
      icon: Icons.grain,
      accentColor: AppColors.biomass,
      isEnabled: s.commEnabled,
      onToggle: (v) => run(() => control.setBiomassComm(v), "Sensor de biomassa ${v ? 'ligado' : 'desligado'}"),
      isBusy: busy,
      applyButtonLabel: "Aplicar limites",
      onApply: () async {
        final low = parseInt(_low.text), high = parseInt(_high.text), opt = parseInt(_opt.text);
        if (low == null || high == null || opt == null || low >= high) {
          showCommandFeedback(context, false, "Limites inválidos (mínimo < máximo)");
          return;
        }
        await run(() => control.setBiomassThresholds(low: low, high: high, opt: opt), "Limites da biomassa");
      },
      children: [
        StatusStrip(
          label: s.statusLabel,
          value: s.isAcquiring ? s.formattedAbs : null,
          tone: _tone(disabled: s.isDisabled, disconnected: s.isDisconnected, active: s.isAcquiring),
          pending: s.commandPending,
        ),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            FilledButton.tonalIcon(
              icon: const Icon(Icons.play_arrow, size: 18),
              label: const Text("Iniciar"),
              onPressed: s.canStartAcquisition && !busy
                  ? () => run(control.startBiomassAcquisition, "Aquisição iniciada")
                  : null,
            ),
            FilledButton.tonalIcon(
              icon: const Icon(Icons.pause, size: 18),
              label: const Text("Pausar"),
              onPressed: s.canStopAcquisition && !busy
                  ? () => run(control.stopBiomassAcquisition, "Aquisição pausada")
                  : null,
            ),
            OutlinedButton.icon(
              icon: const Icon(Icons.adjust, size: 18),
              label: const Text("Zerar branco"),
              onPressed: s.canZeroBlank && !busy ? () => run(control.zeroBiomassBlank, "Branco zerado") : null,
            ),
          ],
        ),
        const SizedBox(height: 12),
        FieldRow([
          NumberField(controller: _low, label: "Mínimo", unit: "cts", decimal: false, enabled: s.commEnabled),
          NumberField(controller: _high, label: "Máximo", unit: "cts", decimal: false, enabled: s.commEnabled),
          NumberField(controller: _opt, label: "Ótimo", unit: "cts", decimal: false, enabled: s.commEnabled),
        ]),
      ],
    );
  }
}

/// Gas flowmeter node: flow setpoint and the two gas valves.
class FlowSection extends StatefulWidget {
  const FlowSection({super.key});

  @override
  State<FlowSection> createState() => _FlowSectionState();
}

class _FlowSectionState extends State<FlowSection> with HubSync {
  final _sp = TextEditingController(text: "0.0");
  bool _editing = false;

  @override
  void dispose() {
    _sp.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final s = context.watch<TelemetryProvider>().flowmeterState;
    final busy = control.isBusy;

    syncFromHub("flow", s.flowSetpoint, editing: _editing, apply: () {
      _sp.text = s.flowSetpoint.toStringAsFixed(2);
    });

    Future<void> run(Future<bool> Function() cmd, String what) async {
      final ok = await cmd();
      if (context.mounted) showCommandFeedback(context, ok, what);
    }

    return ControlSectionCard(
      title: "Fluxômetro (gases)",
      status: s.isConnectedAndActive
          ? "${s.formattedFlowRate} · alvo ${s.flowSetpoint.toStringAsFixed(2)} L/min"
          : s.statusLabel,
      icon: Icons.air,
      accentColor: AppColors.flow,
      isEnabled: s.commEnabled,
      onToggle: (v) => run(() => control.setFlowmeterComm(v), "Fluxômetro ${v ? 'ligado' : 'desligado'}"),
      isBusy: busy,
      applyButtonLabel: "Aplicar vazão",
      onApply: () async {
        final sp = parseNumber(_sp.text);
        if (sp == null || sp < 0) {
          showCommandFeedback(context, false, "Vazão inválida");
          return;
        }
        final ok = await control.setFlowSetpoint(sp);
        if (!context.mounted) return;
        if (ok) setState(() => _editing = false);
        showCommandFeedback(context, ok, "Vazão ${sp.toStringAsFixed(2)} L/min");
      },
      children: [
        StatusStrip(
          label: s.statusLabel,
          value: s.isConnectedAndActive ? s.formattedFlowRate : null,
          tone: _tone(disabled: s.isDisabled, disconnected: s.isDisconnected, active: s.isConnectedAndActive),
          pending: s.commandPending,
        ),
        NumberField(
          controller: _sp,
          label: "Vazão de gás",
          unit: "L/min",
          enabled: s.commEnabled,
          onChanged: (_) => _editing = true,
        ),
        const SizedBox(height: 4),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          dense: true,
          title: const Text("Válvula 1 (ar)"),
          value: s.valve1,
          onChanged: s.commEnabled && !busy
              ? (v) => run(() => control.setFlowValves(valve1: v), "Válvula 1 ${v ? 'aberta' : 'fechada'}")
              : null,
        ),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          dense: true,
          title: const Text("Válvula 2 (N₂)"),
          value: s.valve2,
          onChanged: s.commEnabled && !busy
              ? (v) => run(() => control.setFlowValves(valve2: v), "Válvula 2 ${v ? 'aberta' : 'fechada'}")
              : null,
        ),
        const SizedBox(height: 4),
        OutlinedButton.icon(
          style: OutlinedButton.styleFrom(foregroundColor: AppColors.danger),
          icon: const Icon(Icons.block),
          label: const Text("Cortar gás"),
          onPressed: s.commEnabled && !busy
              ? () async {
                  setState(() {
                    _sp.text = "0.0";
                    _editing = false;
                  });
                  await run(control.stopFlow, "Gás cortado");
                }
              : null,
        ),
      ],
    );
  }
}

/// Flask agitator node (magnetic stirrer). Its speed/direction are also used by the
/// automatic foam response.
class FlaskAgitatorSection extends StatefulWidget {
  const FlaskAgitatorSection({super.key});

  @override
  State<FlaskAgitatorSection> createState() => _FlaskAgitatorSectionState();
}

class _FlaskAgitatorSectionState extends State<FlaskAgitatorSection> {
  final _pct = TextEditingController(text: "50");
  int _dir = 1;
  bool _auto = false;
  bool _pot = true;

  @override
  void dispose() {
    _pct.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final s = context.watch<TelemetryProvider>().agitatorState;
    final busy = control.isBusy;

    Future<void> run(Future<bool> Function() cmd, String what) async {
      final ok = await cmd();
      if (context.mounted) showCommandFeedback(context, ok, what);
    }

    Future<void> start() async {
      final pct = parseInt(_pct.text);
      if (pct == null || pct <= 0 || pct > 100) {
        showCommandFeedback(context, false, "Velocidade inválida (1 a 100 %)");
        return;
      }
      await run(() => control.setAgitatorState(on: true, speedPercent: pct, direction: _dir), "Agitador $pct %");
    }

    // Unlike the other nodes, the Hub has no link switch for the flask agitator (no
    // agitatorComm command): it is online whenever the node pushes data. The switch
    // therefore starts/stops the stirrer itself, and only while the node is online.
    return ControlSectionCard(
      title: "Frasco agitador",
      status: s.isConnectedAndActive ? "${s.statusLabel} · ${s.directionLabel}" : s.statusLabel,
      icon: Icons.rotate_right,
      accentColor: AppColors.flask,
      isEnabled: s.isSpinning,
      onToggle: s.isOnline
          ? (v) => v ? start() : run(() => control.stopAgitator(), "Agitador desligado")
          : null,
      isBusy: busy,
      applyButtonLabel: "Aplicar velocidade",
      onApply: s.isOnline ? start : null,
      children: [
        if (!s.isOnline)
          const HelpText(
            "O nó do frasco agitador não envia dados ao Hub há mais de 3 s. Verifique se ele "
            "está ligado e conectado à rede Wi-Fi do Hub; não há chave de comunicação a ativar.",
            warning: true,
          ),
        StatusStrip(
          label: s.statusLabel,
          value: s.isConnectedAndActive ? s.formattedPercent : null,
          tone: _tone(disabled: false, disconnected: s.isDisconnected, active: s.isSpinning),
          pending: s.isCommandPending,
        ),
        Row(
          children: [
            Expanded(child: NumberField(controller: _pct, label: "Velocidade", unit: "%", decimal: false)),
            const SizedBox(width: 10),
            SegmentedButton<int>(
              segments: const [
                ButtonSegment(value: 1, label: Text("Horário"), icon: Icon(Icons.rotate_right, size: 16)),
                ButtonSegment(value: 0, label: Text("Anti"), icon: Icon(Icons.rotate_left, size: 16)),
              ],
              selected: {_dir},
              showSelectedIcon: false,
              onSelectionChanged: (v) => setState(() => _dir = v.first),
            ),
          ],
        ),
        const SizedBox(height: 4),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          dense: true,
          title: const Text("Acionar na espuma"),
          subtitle: const Text("O Hub liga o agitador quando detecta espuma"),
          value: _auto,
          onChanged: busy
              ? null
              : (v) {
                  setState(() => _auto = v);
                  run(() => control.setAgitatorSettings(autoFoam: v), "Acionamento na espuma ${v ? 'ligado' : 'desligado'}");
                },
        ),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          dense: true,
          title: const Text("Liberar potenciômetro"),
          subtitle: const Text("Permite ajustar pelo botão do equipamento ao parar"),
          value: _pot,
          onChanged: busy
              ? null
              : (v) {
                  setState(() => _pot = v);
                  run(() => control.setAgitatorSettings(reEnablePot: v), "Potenciômetro ${v ? 'liberado' : 'bloqueado'}");
                },
        ),
        const SizedBox(height: 4),
        OutlinedButton.icon(
          style: OutlinedButton.styleFrom(foregroundColor: AppColors.danger),
          icon: const Icon(Icons.stop_circle_outlined),
          label: const Text("Parar e travar o potenciômetro"),
          onPressed: busy ? null : () => run(control.safeStopAgitator, "Agitador parado"),
        ),
      ],
    );
  }
}
