import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../../providers/device_control_provider.dart';
import '../../providers/telemetry_provider.dart';
import '../../theme/app_theme.dart';
import '../../widgets/control_section_card.dart';
import '../../widgets/hub_sync.dart';
import '../../widgets/status_strip.dart';

/// Main agitation: the Delta ASDA-B2 servo commanded by the Hub. The command route
/// (Modbus or legacy UART) and diagnostics stay on the Windows app.
class ServoSection extends StatefulWidget {
  const ServoSection({super.key});

  @override
  State<ServoSection> createState() => _ServoSectionState();
}

class _ServoSectionState extends State<ServoSection> with HubSync {
  final _rpm = TextEditingController(text: "0");
  int _rpmValue = 0;
  bool _editing = false;

  @override
  void dispose() {
    _rpm.dispose();
    super.dispose();
  }

  void _setRpm(int v, {bool fromText = false}) {
    setState(() {
      _rpmValue = v.clamp(0, 1000);
      _editing = true;
      if (!fromText) _rpm.text = "$_rpmValue";
    });
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final servo = context.watch<TelemetryProvider>().servoState;

    syncFromHub("rpm", servo.requestedRpm, editing: _editing, apply: () {
      _rpmValue = servo.requestedRpm.clamp(0, 1000);
      _rpm.text = "$_rpmValue";
    });

    final String status;
    final StatusTone tone;
    if (!servo.commEnabled) {
      status = "Comunicação desligada";
      tone = StatusTone.off;
    } else if (!servo.online) {
      status = "Servo offline";
      tone = StatusTone.warning;
    } else if (servo.isFaulted) {
      status = "Falha: ${servo.stateDescription}";
      tone = StatusTone.warning;
    } else {
      status = servo.hasTelemetry
          ? "${servo.rpm.toStringAsFixed(0)} rpm · alvo ${servo.requestedRpm} rpm"
          : "Alvo ${servo.requestedRpm} rpm";
      tone = servo.isRunning ? StatusTone.active : StatusTone.ok;
    }

    return ControlSectionCard(
      title: "Agitação (servo)",
      status: status,
      icon: Icons.cyclone,
      accentColor: AppColors.agitation,
      isEnabled: servo.commEnabled,
      onToggle: (v) async {
        final ok = await control.setServoComm(v);
        if (context.mounted) showCommandFeedback(context, ok, "Comunicação do servo ${v ? 'ligada' : 'desligada'}");
      },
      isBusy: control.isBusy,
      applyButtonLabel: "Aplicar velocidade",
      onApply: () async {
        final rpm = parseInt(_rpm.text) ?? _rpmValue;
        final ok = await control.setMotorRpm(rpm);
        if (!context.mounted) return;
        if (ok) setState(() => _editing = false);
        showCommandFeedback(context, ok, rpm > 0 ? "Agitação $rpm rpm" : "Agitação parada");
      },
      children: [
        StatusStrip(label: status, tone: tone, pending: servo.commandPending),
        Row(
          children: [
            Expanded(
              child: Slider(
                value: _rpmValue.toDouble(),
                min: 0,
                max: 1000,
                divisions: 100,
                label: "$_rpmValue rpm",
                onChanged: (v) => _setRpm(v.round()),
              ),
            ),
            SizedBox(
              width: 110,
              child: NumberField(
                controller: _rpm,
                label: "Velocidade",
                unit: "rpm",
                decimal: false,
                onChanged: (txt) {
                  final v = parseInt(txt);
                  if (v != null) _setRpm(v, fromText: true);
                },
              ),
            ),
          ],
        ),
        const HelpText("Valores de 1 a 14 rpm são enviados como 15 rpm; 0 para o motor."),
        OutlinedButton.icon(
          style: OutlinedButton.styleFrom(foregroundColor: AppColors.danger),
          onPressed: control.isBusy
              ? null
              : () async {
                  _setRpm(0);
                  final ok = await control.stopMotor();
                  if (!context.mounted) return;
                  if (ok) setState(() => _editing = false);
                  showCommandFeedback(context, ok, "Agitação parada");
                },
          icon: const Icon(Icons.stop_circle_outlined),
          label: const Text("Parar agitação"),
        ),
      ],
    );
  }
}

/// pH dosing by the controller board (acid/base pumps).
class PhSection extends StatefulWidget {
  const PhSection({super.key});

  @override
  State<PhSection> createState() => _PhSectionState();
}

class _PhSectionState extends State<PhSection> {
  bool _on = false;
  final _sp = TextEditingController(text: "7.00");
  final _band = TextEditingController(text: "0.17");
  final _op = TextEditingController(text: "5");
  final _mix = TextEditingController(text: "20");
  final _speed = TextEditingController(text: "50");

  @override
  void dispose() {
    for (final c in [_sp, _band, _op, _mix, _speed]) {
      c.dispose();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final ph = context.watch<TelemetryProvider>().calibratedPh;
    final reading = ph >= 0 ? ph.toStringAsFixed(2) : "--";

    return ControlSectionCard(
      title: "pH",
      status: _on ? "Atual $reading · alvo ${_sp.text}" : "Atual $reading · controle desligado",
      icon: Icons.science_outlined,
      accentColor: AppColors.ph,
      isEnabled: _on,
      onToggle: (v) => setState(() => _on = v),
      isBusy: control.isBusy,
      onApply: () async {
        final sp = parseNumber(_sp.text);
        final band = parseNumber(_band.text);
        final op = parseInt(_op.text);
        final mix = parseInt(_mix.text);
        final speed = parseInt(_speed.text);
        if (sp == null || band == null || op == null || mix == null || speed == null ||
            (_on && (sp <= 0 || sp >= 14))) {
          showCommandFeedback(context, false, "Valores de pH inválidos");
          return;
        }
        final ok = await control.setPhControl(
          enabled: _on,
          setpoint: sp,
          error: band,
          opTimeSeconds: op,
          mixTimeSeconds: mix,
          speedPercent: speed,
        );
        if (context.mounted) showCommandFeedback(context, ok, _on ? "pH alvo $sp" : "Controle de pH desligado");
      },
      children: [
        FieldRow([
          NumberField(controller: _sp, label: "pH alvo", enabled: _on),
          NumberField(controller: _band, label: "Banda morta", unit: "pH", enabled: _on),
        ]),
        FieldRow([
          NumberField(controller: _op, label: "Dosagem", unit: "s", decimal: false, enabled: _on),
          NumberField(controller: _mix, label: "Mistura", unit: "s", decimal: false, enabled: _on),
          NumberField(controller: _speed, label: "Bomba", unit: "%", decimal: false, enabled: _on),
        ]),
        const HelpText("A bomba doseia por \"Dosagem\" e espera \"Mistura\" antes de medir de novo."),
      ],
    );
  }
}

/// Dissolved oxygen: reading only. The oxygen cascade runs on the Windows app.
class OxygenSection extends StatefulWidget {
  const OxygenSection({super.key});

  @override
  State<OxygenSection> createState() => _OxygenSectionState();
}

class _OxygenSectionState extends State<OxygenSection> {
  bool _on = false;

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final oxy = context.watch<TelemetryProvider>().calibratedOxygen;
    final reading = oxy >= 0 ? "${oxy.toStringAsFixed(2)} mg/L" : "-- mg/L";

    return ControlSectionCard(
      title: "Oxigênio dissolvido",
      status: "Atual $reading",
      icon: Icons.bubble_chart_outlined,
      accentColor: AppColors.oxygen,
      isEnabled: _on,
      onToggle: (v) async {
        setState(() => _on = v);
        final ok = await control.setOxygenMonitor(v);
        if (context.mounted) showCommandFeedback(context, ok, "Leitura de OD ${v ? 'ligada' : 'desligada'}");
      },
      isBusy: control.isBusy,
      children: const [
        HelpText(
          "Liga a leitura da sonda de OD na placa de sensores. O controle de OD por cascata "
          "fica no aplicativo Windows.",
        ),
      ],
    );
  }
}

/// Vessel pressure reference.
class PressureSection extends StatefulWidget {
  const PressureSection({super.key});

  @override
  State<PressureSection> createState() => _PressureSectionState();
}

class _PressureSectionState extends State<PressureSection> {
  bool _on = false;
  final _ref = TextEditingController(text: "100");

  @override
  void dispose() {
    _ref.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final p = context.watch<TelemetryProvider>().telemetry.pressure;

    return ControlSectionCard(
      title: "Pressão",
      status: _on
          ? "Atual ${p.toStringAsFixed(0)} mmHg · alvo ${_ref.text} mmHg"
          : "Atual ${p.toStringAsFixed(0)} mmHg · controle desligado",
      icon: Icons.speed,
      accentColor: AppColors.pressure,
      isEnabled: _on,
      onToggle: (v) => setState(() => _on = v),
      isBusy: control.isBusy,
      onApply: () async {
        final ref = parseInt(_ref.text);
        if (_on && (ref == null || ref <= 0 || ref > 500)) {
          showCommandFeedback(context, false, "Pressão inválida (1 a 500 mmHg)");
          return;
        }
        final ok = await control.setPressure(enabled: _on, referenceMmHg: ref ?? 0);
        if (context.mounted) showCommandFeedback(context, ok, _on ? "Pressão $ref mmHg" : "Pressão desligada");
      },
      children: [
        NumberField(controller: _ref, label: "Pressão alvo", unit: "mmHg", decimal: false, enabled: _on),
      ],
    );
  }
}
