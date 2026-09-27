import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../../models/peristaltic_pump_state.dart';
import '../../providers/device_control_provider.dart';
import '../../providers/telemetry_provider.dart';
import '../../services/pump_profile_math.dart';
import '../../theme/app_theme.dart';
import '../../widgets/control_section_card.dart';
import '../../widgets/status_strip.dart';

/// External peristaltic feed pump: feeding profile with a live preview of the dose.
class PumpSection extends StatefulWidget {
  const PumpSection({super.key});

  @override
  State<PumpSection> createState() => _PumpSectionState();
}

class _PumpSectionState extends State<PumpSection> {
  PeristalticPumpMode _mode = PeristalticPumpMode.constant;
  final _init = TextEditingController(text: "0");
  final _final = TextEditingController(text: "60");
  final _lambda = TextEditingController(text: "1.0");
  final _phi = TextEditingController(text: "0.05");
  final _poly = TextEditingController(text: "1.0, 0.05");
  final _times = TextEditingController(text: "0, 30, 60");
  final _flows = TextEditingController(text: "1.0, 2.5, 0.5");
  PumpPreview? _preview;
  String? _error;

  @override
  void initState() {
    super.initState();
    _recalculate();
  }

  @override
  void dispose() {
    for (final c in [_init, _final, _lambda, _phi, _poly, _times, _flows]) {
      c.dispose();
    }
    super.dispose();
  }

  List<double> _list(TextEditingController c) =>
      c.text.split(',').map((v) => double.tryParse(v.trim())).whereType<double>().toList();

  PumpProfileSpec _spec() => PumpProfileSpec(
        mode: _mode,
        initMinutes: parseNumber(_init.text) ?? 0.0,
        finalMinutes: parseNumber(_final.text) ?? 60.0,
        lambda: parseNumber(_lambda.text) ?? 1.0,
        phi: parseNumber(_phi.text) ?? 0.0,
        polynomialCoefficients: _mode == PeristalticPumpMode.polynomial ? _list(_poly) : const [],
        piecewiseTimes: _mode == PeristalticPumpMode.piecewise ? _list(_times) : const [],
        piecewiseFlows: _mode == PeristalticPumpMode.piecewise ? _list(_flows) : const [],
      );

  void _recalculate() {
    final spec = _spec();
    final err = PumpProfileMath.validateSpec(spec);
    setState(() {
      _error = err;
      _preview = err == null ? PumpProfileMath.sample(spec) : null;
    });
  }

  Widget _field(TextEditingController c, String label, {String? unit, bool enabled = true, bool list = false}) {
    return TextField(
      controller: c,
      enabled: enabled,
      keyboardType: list ? TextInputType.text : const TextInputType.numberWithOptions(decimal: true),
      decoration: InputDecoration(labelText: label, suffixText: unit),
      onChanged: (_) => _recalculate(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final s = context.watch<TelemetryProvider>().pumpState;
    final on = s.commEnabled;
    final theme = Theme.of(context);

    return ControlSectionCard(
      title: "Bomba peristáltica",
      status: s.isConnectedAndActive ? "${s.statusLabel} · ${s.formattedVolume}" : s.statusLabel,
      icon: Icons.water_drop,
      accentColor: AppColors.pump,
      isEnabled: on,
      onToggle: (v) async {
        final ok = await control.setPumpComm(v);
        if (context.mounted) showCommandFeedback(context, ok, "Bomba ${v ? 'ligada' : 'desligada'}");
      },
      isBusy: control.isBusy,
      applyButtonLabel: "Enviar perfil",
      onApply: () async {
        if (_error != null) {
          showCommandFeedback(context, false, _error!);
          return;
        }
        final spec = _spec();
        final ok = await control.applyPumpProfile(spec);
        if (context.mounted) showCommandFeedback(context, ok, "Perfil: ${spec.mode.label}");
      },
      children: [
        StatusStrip(
          label: s.statusLabel,
          value: s.isConnectedAndActive ? s.formattedFlow : null,
          tone: s.isDisabled
              ? StatusTone.off
              : s.isDisconnected
                  ? StatusTone.warning
                  : s.isDosing
                      ? StatusTone.active
                      : StatusTone.ok,
          pending: s.isCommandPending,
        ),
        if (s.isConnectedAndActive && s.targetVolume > 0) ...[
          LinearProgressIndicator(value: s.progressFraction, color: AppColors.pump, minHeight: 6),
          const SizedBox(height: 4),
          Text("Dosado ${s.formattedVolume} de ${s.formattedTargetVolume}", style: theme.textTheme.bodySmall),
          const SizedBox(height: 12),
        ],
        DropdownButtonFormField<PeristalticPumpMode>(
          initialValue: _mode,
          isExpanded: true,
          decoration: const InputDecoration(labelText: "Perfil de alimentação"),
          items: const [
            DropdownMenuItem(value: PeristalticPumpMode.constant, child: Text("Constante (Q = λ)")),
            DropdownMenuItem(value: PeristalticPumpMode.linear, child: Text("Linear (Q = λ + φ·t)")),
            DropdownMenuItem(value: PeristalticPumpMode.exponential, child: Text("Exponencial (Q = λ·e^(φ·t))")),
            DropdownMenuItem(value: PeristalticPumpMode.polynomial, child: Text("Polinomial (p0 + p1·t + …)")),
            DropdownMenuItem(value: PeristalticPumpMode.piecewise, child: Text("Segmentos (pontos t, Q)")),
          ],
          onChanged: on
              ? (v) {
                  if (v == null) return;
                  _mode = v;
                  _recalculate();
                }
              : null,
        ),
        const SizedBox(height: 10),
        FieldRow([
          _field(_init, "Início", unit: "min", enabled: on),
          _field(_final, "Fim", unit: "min", enabled: on),
        ]),
        if (_mode == PeristalticPumpMode.constant)
          _field(_lambda, "Vazão λ", unit: "mL/min", enabled: on),
        if (_mode == PeristalticPumpMode.linear)
          FieldRow([
            _field(_lambda, "Vazão inicial λ", unit: "mL/min", enabled: on),
            _field(_phi, "Inclinação φ", unit: "mL/min²", enabled: on),
          ]),
        if (_mode == PeristalticPumpMode.exponential)
          FieldRow([
            _field(_lambda, "Vazão inicial λ", unit: "mL/min", enabled: on),
            _field(_phi, "Taxa φ (μ)", unit: "1/min", enabled: on),
          ]),
        if (_mode == PeristalticPumpMode.polynomial)
          _field(_poly, "Coeficientes p0, p1, p2…", enabled: on, list: true),
        if (_mode == PeristalticPumpMode.piecewise) ...[
          _field(_times, "Tempos (min, separados por vírgula)", enabled: on, list: true),
          const SizedBox(height: 10),
          _field(_flows, "Vazões (mL/min)", enabled: on, list: true),
        ],
        const SizedBox(height: 12),
        Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: (_error == null ? AppColors.pump : AppColors.danger).withValues(alpha: 0.08),
            borderRadius: BorderRadius.circular(10),
          ),
          child: _error != null || _preview == null
              ? Text(_error ?? "Parâmetros inválidos", style: const TextStyle(color: AppColors.danger))
              : Wrap(
                  spacing: 20,
                  runSpacing: 8,
                  children: [
                    _previewValue(theme, "Volume total", "${_preview!.totalVolume.toStringAsFixed(1)} mL"),
                    _previewValue(theme, "Vazão máx.", "${_preview!.peakFlow.toStringAsFixed(2)} mL/min"),
                    _previewValue(theme, "Vazão média", "${_preview!.averageFlow.toStringAsFixed(2)} mL/min"),
                  ],
                ),
        ),
        const SizedBox(height: 10),
        OutlinedButton.icon(
          style: OutlinedButton.styleFrom(foregroundColor: AppColors.danger),
          icon: const Icon(Icons.stop_circle_outlined),
          label: const Text("Parar bomba"),
          onPressed: control.isBusy
              ? null
              : () async {
                  final ok = await control.stopPump();
                  if (context.mounted) showCommandFeedback(context, ok, "Bomba parada");
                },
        ),
      ],
    );
  }

  Widget _previewValue(ThemeData theme, String label, String value) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(label, style: theme.textTheme.labelSmall),
          Text(value, style: theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.bold)),
        ],
      );
}
