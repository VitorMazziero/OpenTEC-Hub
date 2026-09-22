/// Hub-routed external bath (Contemp C404) and its thermal cascade (Hub 10.6).
///
/// The phone only supervises: the PI runs in the Hub. What matters here is that the
/// operator can see which route controls the reactor temperature, whether the Hub owns
/// the bath, why the cascade is waiting or faulted, and that "turning temperature off" on
/// the external route stops the cascade but does NOT switch the C404 off: it stays at its
/// last setpoint, with its guard in manual.
class ExternalBathState {
  /// The Hub publishes bath keys at all (Hub 10.5+). False on older Hubs.
  final bool hasTelemetry;
  final bool viaBath;
  final bool online;
  final bool commEnabled;
  final bool owned;
  final bool cascadeActive;
  final String cascadeState;
  final String faultReason;
  final String pausedReason;
  final double? bathPv;
  final double? bathSp;
  final double? commandSetpoint;
  final double? reactorSetpoint;
  final int? mode; // 1 = auto (guard reverts the panel), 0 = manual
  final String guard;
  final String nodeState;

  const ExternalBathState({
    required this.hasTelemetry,
    required this.viaBath,
    required this.online,
    required this.commEnabled,
    required this.owned,
    required this.cascadeActive,
    required this.cascadeState,
    required this.faultReason,
    required this.pausedReason,
    required this.bathPv,
    required this.bathSp,
    required this.commandSetpoint,
    required this.reactorSetpoint,
    required this.mode,
    required this.guard,
    required this.nodeState,
  });

  factory ExternalBathState.empty() => const ExternalBathState(
        hasTelemetry: false,
        viaBath: false,
        online: false,
        commEnabled: false,
        owned: false,
        cascadeActive: false,
        cascadeState: "",
        faultReason: "",
        pausedReason: "",
        bathPv: null,
        bathSp: null,
        commandSetpoint: null,
        reactorSetpoint: null,
        mode: null,
        guard: "",
        nodeState: "",
      );

  factory ExternalBathState.fromJson(Map<String, dynamic> json) {
    double? number(String key) {
      final v = json[key];
      return (v is num && v.isFinite) ? v.toDouble() : null;
    }

    String text(String key) => json[key] is String ? json[key] as String : "";

    final has = json.containsKey('TempControlViaBath') || json.containsKey('BathOnline');
    final viaBath = json['TempControlViaBath'] == true;
    return ExternalBathState(
      hasTelemetry: has,
      viaBath: viaBath,
      online: json['BathOnline'] == true,
      commEnabled: json['BathCommEnabled'] == true,
      owned: json['BathOwned'] == true,
      // Hubs before 10.6 do not publish BathCascadeActive: fall back to the route.
      cascadeActive: json.containsKey('BathCascadeActive')
          ? json['BathCascadeActive'] == true
          : viaBath,
      cascadeState: text('BathCascadeState'),
      faultReason: text('BathCascadeFaultReason'),
      pausedReason: text('BathCascadePausedReason'),
      bathPv: number('BathPv'),
      bathSp: number('BathSp'),
      commandSetpoint: number('BathCommandSetpoint'),
      reactorSetpoint: number('TempSetpoint'),
      mode: json['BathMode'] is int ? json['BathMode'] as int : null,
      guard: text('BathGuard'),
      nodeState: text('BathState'),
    );
  }

  bool get isFault => cascadeState == "fault";
  bool get isAutomatic => mode == 1;

  /// One-line operator summary, pt-BR like the rest of the bath surfaces.
  String get summary {
    if (!hasTelemetry) return "Hub sem suporte ao banho externo";
    if (!viaBath) return "Temperatura pela placa original (UART)";
    if (!commEnabled) return "Via externa, comunicação do banho desligada";
    if (!online) return "Banho externo offline";
    switch (cascadeState) {
      case "controlling":
        return "Cascata controlando o reator";
      case "actuator_busy":
        return "Cascata aguardando o C404 concluir";
      case "waiting_inputs":
        return "Cascata aguardando: ${describeReason(pausedReason)}";
      case "paused":
        return "Cascata pausada: ${describeReason(pausedReason)}";
      case "fault":
        return "Falha da cascata: ${describeReason(faultReason.isNotEmpty ? faultReason : pausedReason)}";
      case "off":
        return "Cascata desligada — C404 no último SP";
      default:
        return "Estado da cascata: $cascadeState";
    }
  }

  /// Same wording as the Windows app (BathReasons).
  static String describeReason(String code) {
    if (code.isEmpty) return "sem motivo informado";
    if (code.startsWith("node_rejected:")) {
      return "o banho recusou o comando (${code.substring("node_rejected:".length)})";
    }
    if (code.startsWith("bath_error:")) {
      return "erro na sequência do C404 (${code.substring("bath_error:".length)})";
    }
    const texts = {
      "no_reference": "sem referência do reator",
      "reactor_pv_invalid": "temperatura do reator inválida",
      "reactor_pv_stale": "temperatura do reator inválida",
      "node_offline": "banho offline",
      "bath_comm_off": "comunicação do banho desligada",
      "sp_source_shadow": "nó sem leitura do display",
      "bath_manual": "guarda do C404 em manual",
      "guard_suspended": "guarda do C404 suspensa",
      "display_sp_invalid": "SP do display ilegível",
      "bath_aborted": "sequência do C404 abortada",
      "bath_completion_timeout": "comando não concluído em 300 s",
      "target_override": "alvo do C404 alterado por fora",
      "actuator_busy": "C404 executando comando",
    };
    return texts[code] ?? code;
  }

  static String fmt(double? v) => v == null ? "--" : "${v.toStringAsFixed(1)} °C";
}
