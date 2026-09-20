/// Modelo do `GET /status` do nó bath (firmware r2). Ver PROTOCOL.md §2.
///
/// Campos que o nó pode mandar `null` (por não ter display legível, por
/// exemplo) ficam nullable aqui; o app nunca substitui `null` por um valor
/// plausível (regra §5.1 do plano / regra de v.6 do app Windows).
library;

enum SeqState { idle, running, settling, done, error, aborted, unknown }

enum SeqKind { none, setpoint, home, raw, unknown }

enum SeqPhase { none, enter, plan, hold, holdSettle, presses, unknown }

enum BathMode { manual, auto, unknown }

enum GuardState { off, watch, pending, correcting, suspended, unknown }

SeqState _seqStateFrom(String? v) {
  switch (v) {
    case 'idle':
      return SeqState.idle;
    case 'running':
      return SeqState.running;
    case 'settling':
      return SeqState.settling;
    case 'done':
      return SeqState.done;
    case 'error':
      return SeqState.error;
    case 'aborted':
      return SeqState.aborted;
    default:
      return SeqState.unknown;
  }
}

SeqKind _seqKindFrom(String? v) {
  switch (v) {
    case 'none':
      return SeqKind.none;
    case 'setpoint':
      return SeqKind.setpoint;
    case 'home':
      return SeqKind.home;
    case 'raw':
      return SeqKind.raw;
    default:
      return SeqKind.unknown;
  }
}

SeqPhase _seqPhaseFrom(String? v) {
  switch (v) {
    case '':
    case null:
      return SeqPhase.none;
    case 'enter':
      return SeqPhase.enter;
    case 'plan':
      return SeqPhase.plan;
    case 'hold':
      return SeqPhase.hold;
    case 'hold_settle':
      return SeqPhase.holdSettle;
    case 'presses':
      return SeqPhase.presses;
    default:
      return SeqPhase.unknown;
  }
}

BathMode _modeFrom(String? v) {
  switch (v) {
    case 'manual':
      return BathMode.manual;
    case 'auto':
      return BathMode.auto;
    default:
      return BathMode.unknown;
  }
}

GuardState _guardFrom(String? v) {
  switch (v) {
    case 'off':
      return GuardState.off;
    case 'watch':
      return GuardState.watch;
    case 'pending':
      return GuardState.pending;
    case 'correcting':
      return GuardState.correcting;
    case 'suspended':
      return GuardState.suspended;
    default:
      return GuardState.unknown;
  }
}

double? _asDouble(dynamic v) => v is num ? v.toDouble() : null;
int _asInt(dynamic v) => v is num ? v.toInt() : 0;
bool _asBool(dynamic v) => v == true || v == 1;

class BathStatus {
  final bool present; // true quando veio um /status válido
  final String device;
  final String version;
  final int uptimeS;

  // Modo e guarda (§3.2)
  final BathMode mode;
  final GuardState guard;
  final double? deviationC;
  final int guardCorrections;
  final int arrowsHeldMs;

  // Sombra do setpoint
  final double? spShadow;
  final bool spKnown;
  final double? spTarget;
  final int spSource;

  // Sequência
  final SeqState seqState;
  final SeqKind seqKind;
  final SeqPhase seqPhase;
  final String seqError;
  final int pressesDone;
  final int pressesTotal;
  final int pressesUnconfirmed;
  final int holdMs;
  final int holdRounds;
  final double holdRate;

  // Display do painel
  final bool displayAlive;
  final double? displayPv;
  final double? displaySp;
  final String displayText;

  // Toques manuais
  final int manualPresses;
  final int manualAgeS;

  // Rede / OTA
  final int wifiStatus;
  final String ip;
  final bool ota;
  final int lastCmdId;

  const BathStatus({
    required this.present,
    required this.device,
    required this.version,
    required this.uptimeS,
    required this.mode,
    required this.guard,
    required this.deviationC,
    required this.guardCorrections,
    required this.arrowsHeldMs,
    required this.spShadow,
    required this.spKnown,
    required this.spTarget,
    required this.spSource,
    required this.seqState,
    required this.seqKind,
    required this.seqPhase,
    required this.seqError,
    required this.pressesDone,
    required this.pressesTotal,
    required this.pressesUnconfirmed,
    required this.holdMs,
    required this.holdRounds,
    required this.holdRate,
    required this.displayAlive,
    required this.displayPv,
    required this.displaySp,
    required this.displayText,
    required this.manualPresses,
    required this.manualAgeS,
    required this.wifiStatus,
    required this.ip,
    required this.ota,
    required this.lastCmdId,
  });

  factory BathStatus.empty() => const BathStatus(
        present: false,
        device: '',
        version: '',
        uptimeS: 0,
        mode: BathMode.unknown,
        guard: GuardState.unknown,
        deviationC: null,
        guardCorrections: 0,
        arrowsHeldMs: 0,
        spShadow: null,
        spKnown: false,
        spTarget: null,
        spSource: 0,
        seqState: SeqState.unknown,
        seqKind: SeqKind.unknown,
        seqPhase: SeqPhase.none,
        seqError: '',
        pressesDone: 0,
        pressesTotal: 0,
        pressesUnconfirmed: 0,
        holdMs: 0,
        holdRounds: 0,
        holdRate: 0.0,
        displayAlive: false,
        displayPv: null,
        displaySp: null,
        displayText: '',
        manualPresses: 0,
        manualAgeS: -1,
        wifiStatus: 0,
        ip: '',
        ota: false,
        lastCmdId: 0,
      );

  factory BathStatus.fromJson(Map<String, dynamic> j) => BathStatus(
        present: true,
        device: (j['device'] ?? '').toString(),
        version: (j['version'] ?? '').toString(),
        uptimeS: _asInt(j['uptime_s']),
        mode: _modeFrom(j['mode'] as String?),
        guard: _guardFrom(j['guard'] as String?),
        deviationC: _asDouble(j['deviation_c']),
        guardCorrections: _asInt(j['guard_corrections']),
        arrowsHeldMs: _asInt(j['arrows_held_ms']),
        spShadow: _asDouble(j['sp_shadow']),
        spKnown: _asBool(j['sp_known']),
        spTarget: _asDouble(j['sp_target']),
        spSource: _asInt(j['sp_source']),
        seqState: _seqStateFrom(j['seq_state'] as String?),
        seqKind: _seqKindFrom(j['seq_kind'] as String?),
        seqPhase: _seqPhaseFrom(j['seq_phase'] as String?),
        seqError: (j['seq_error'] ?? '').toString(),
        pressesDone: _asInt(j['presses_done']),
        pressesTotal: _asInt(j['presses_total']),
        pressesUnconfirmed: _asInt(j['presses_unconfirmed']),
        holdMs: _asInt(j['hold_ms']),
        holdRounds: _asInt(j['hold_rounds']),
        holdRate: _asDouble(j['hold_rate']) ?? 0.0,
        displayAlive: _asBool(j['display_alive']),
        displayPv: _asDouble(j['display_pv']),
        displaySp: _asDouble(j['display_sp']),
        displayText: (j['display_text'] ?? '').toString(),
        manualPresses: _asInt(j['manual_presses']),
        manualAgeS: j['manual_age_s'] is num ? _asInt(j['manual_age_s']) : -1,
        wifiStatus: _asInt(j['wifi_status']),
        ip: (j['ip'] ?? '').toString(),
        ota: _asBool(j['ota']),
        lastCmdId: _asInt(j['last_cmd_id']),
      );

  bool get seqBusy =>
      seqState == SeqState.running || seqState == SeqState.settling;

  /// O firmware r2 traz os campos de modo/guarda/hold. Firmwares antigos não;
  /// nesse caso os cartões correspondentes mostram "firmware sem suporte".
  bool get supportsModes => mode != BathMode.unknown;

  String get seqStateLabel {
    switch (seqState) {
      case SeqState.idle:
        return 'ocioso';
      case SeqState.running:
        return 'em execução';
      case SeqState.settling:
        return 'assentando';
      case SeqState.done:
        return 'concluído';
      case SeqState.error:
        return 'erro';
      case SeqState.aborted:
        return 'abortado';
      case SeqState.unknown:
        return '—';
    }
  }

  String get seqKindLabel {
    switch (seqKind) {
      case SeqKind.none:
        return 'nenhuma';
      case SeqKind.setpoint:
        return 'setpoint';
      case SeqKind.home:
        return 'home';
      case SeqKind.raw:
        return 'teclas cruas';
      case SeqKind.unknown:
        return '—';
    }
  }

  String get seqPhaseLabel {
    switch (seqPhase) {
      case SeqPhase.none:
        return '';
      case SeqPhase.enter:
        return 'entrada';
      case SeqPhase.plan:
        return 'planejando';
      case SeqPhase.hold:
        return 'tecla mantida';
      case SeqPhase.holdSettle:
        return 'assentando hold';
      case SeqPhase.presses:
        return 'toques';
      case SeqPhase.unknown:
        return '?';
    }
  }

  String get modeLabel {
    switch (mode) {
      case BathMode.manual:
        return 'Manual';
      case BathMode.auto:
        return 'Automático';
      case BathMode.unknown:
        return '—';
    }
  }

  String get guardLabel {
    switch (guard) {
      case GuardState.off:
        return 'desligado';
      case GuardState.watch:
        return 'observando';
      case GuardState.pending:
        return 'desvio visto';
      case GuardState.correcting:
        return 'corrigindo';
      case GuardState.suspended:
        return 'suspenso';
      case GuardState.unknown:
        return '—';
    }
  }
}
