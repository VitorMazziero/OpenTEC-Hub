import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../../providers/connection_provider.dart';
import '../../providers/device_control_provider.dart';
import '../../providers/telemetry_provider.dart';
import '../../theme/app_theme.dart';
import '../../widgets/control_section_card.dart';
import '../../widgets/hub_sync.dart';
import '../../widgets/route_selector.dart';
import '../../widgets/status_strip.dart';

/// Main agitation. The Hub drives the same Delta ASDA-B2 through one of two routes
/// (`motorControlMode`): the original controller board (UART/CN1) or the ESP32 servo
/// node (Modbus). The route is stored in the Hub; switching it stops the motor.
class AgitationSection extends StatefulWidget {
  const AgitationSection({super.key});

  @override
  State<AgitationSection> createState() => _AgitationSectionState();
}

class _AgitationSectionState extends State<AgitationSection> with HubSync {
  final _rpm = TextEditingController(text: "0");
  int _rpmValue = 0;
  bool _on = false;
  bool _editing = false;

  @override
  void dispose() {
    _rpm.dispose();
    super.dispose();
  }

  /// Switch off stops the motor at once; switch on only opens the speed for Apply.
  Future<void> _toggle(DeviceControlProvider control, bool on) async {
    setState(() {
      _on = on;
      _editing = true;
    });
    if (on) return;
    final ok = await control.stopMotor();
    if (!mounted) return;
    if (ok) setState(() => _editing = false);
    showCommandFeedback(context, ok, "Agitação desligada");
  }

  void _setRpm(int v, {bool fromText = false}) {
    setState(() {
      _rpmValue = v.clamp(0, 1000);
      _editing = true;
      if (!fromText) _rpm.text = "$_rpmValue";
    });
  }

  Future<void> _changeRoute(DeviceControlProvider control, bool toServo) async {
    final confirmed = await confirmRouteChange(
      context,
      title: toServo ? "Agitar pelo servo?" : "Agitar pela placa?",
      message: "O Hub para o motor ao trocar a via "
          "(${toServo ? 'Hub → ESP32 servo → ASDA-B2, Modbus' : 'Hub → placa controladora → ASDA-B2, UART/CN1'})."
          "\n\nDepois da troca, envie uma nova velocidade.",
    );
    if (!confirmed || !mounted) return;
    final ok = await control.setMotorControlMode(toServo ? 1 : 0);
    if (!mounted) return;
    if (ok) {
      setState(() {
        _on = false;
        _editing = false;
      });
    }
    showCommandFeedback(context, ok, toServo ? "Agitação pelo servo" : "Agitação pela placa");
  }

  @override
  Widget build(BuildContext context) {
    final control = context.watch<DeviceControlProvider>();
    final servo = context.watch<TelemetryProvider>().servoState;
    final viaServo = servo.viaModbus;
    // Without telemetry the route is unknown: the selector stays disabled.
    final connected = context.watch<ConnectionProvider>().isConnected;

    // Only the servo route echoes the commanded speed; the board route does not publish it.
    if (viaServo) {
      syncFromHub("rpm", servo.requestedRpm, editing: _editing, apply: () {
        _on = servo.requestedRpm > 0;
        if (_on) {
          _rpmValue = servo.requestedRpm.clamp(0, 1000);
          _rpm.text = "$_rpmValue";
        }
      });
    }

    final measured = servo.online && servo.hasTelemetry ? "${servo.rpm.toStringAsFixed(0)} rpm medidos" : null;
    final String status;
    final StatusTone tone;
    if (viaServo && !servo.commEnabled) {
      status = "Comunicação com o servo desligada";
      tone = StatusTone.off;
    } else if (viaServo && !servo.online) {
      status = "Servo offline";
      tone = StatusTone.warning;
    } else if (servo.online && servo.isFaulted) {
      status = "Falha: ${servo.stateDescription}";
      tone = StatusTone.warning;
    } else if (viaServo) {
      status = "${measured ?? 'Sem leitura'} · alvo ${servo.requestedRpm} rpm";
      tone = servo.isRunning ? StatusTone.active : StatusTone.ok;
    } else {
      status = measured ?? "Via placa controladora";
      tone = StatusTone.ok;
    }

    return ControlSectionCard(
      title: "Agitação",
      status: status,
      icon: Icons.cyclone,
      accentColor: AppColors.agitation,
      isEnabled: _on,
      onToggle: (v) => _toggle(control, v),
      isBusy: control.isBusy,
      header: RouteSelector(
        alternative: viaServo,
        boardLabel: "Placa",
        alternativeLabel: "Servo",
        alternativeIcon: Icons.settings_input_component,
        alternativeFirst: true,
        onChanged: control.isBusy || !connected ? null : (toServo) => _changeRoute(control, toServo),
        note: servo.online && servo.routeAck != (viaServo ? 1 : 0)
            ? "Aguardando o servo confirmar a via"
            : null,
      ),
      applyButtonLabel: "Aplicar velocidade",
      onApply: () async {
        final rpm = _on ? (parseInt(_rpm.text) ?? _rpmValue) : 0;
        if (_on && rpm <= 0) {
          showCommandFeedback(context, false, "Velocidade inválida (use 15 a 1000 rpm)");
          return;
        }
        final ok = await control.setMotorRpm(rpm);
        if (!context.mounted) return;
        if (ok) setState(() => _editing = false);
        showCommandFeedback(context, ok, rpm > 0 ? "Agitação $rpm rpm" : "Agitação parada");
      },
      children: [
        StatusStrip(label: status, tone: tone, pending: viaServo && servo.commandPending),
        if (viaServo) ...[
          // Hub <-> servo node link, meaningful on the servo route only.
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            dense: true,
            title: const Text("Comunicação com o servo"),
            subtitle: Text(servo.online ? "Servo online" : "Servo offline"),
            value: servo.commEnabled,
            onChanged: control.isBusy
                ? null
                : (v) async {
                    final ok = await control.setServoComm(v);
                    if (context.mounted) {
                      showCommandFeedback(context, ok, "Comunicação do servo ${v ? 'ligada' : 'desligada'}");
                    }
                  },
          ),
          const SizedBox(height: 4),
        ],
        Row(
          children: [
            Expanded(
              child: Slider(
                value: _rpmValue.toDouble(),
                min: 0,
                max: 1000,
                divisions: 100,
                label: "$_rpmValue rpm",
                onChanged: _on ? (v) => _setRpm(v.round()) : null,
              ),
            ),
            SizedBox(
              width: 110,
              child: NumberField(
                controller: _rpm,
                label: "Velocidade",
                unit: "rpm",
                decimal: false,
                enabled: _on,
                onChanged: (txt) {
                  final v = parseInt(txt);
                  if (v != null) _setRpm(v, fromText: true);
                },
              ),
            ),
          ],
        ),
        const HelpText("Valores de 1 a 14 rpm são enviados como 15 rpm. Desligar a chave para o motor na hora."),
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
