/// Modelo do `GET /display` do nó bath (firmware r2). Ver PROTOCOL.md §1.
///
/// Leitor cru do display do C404: texto dos 8 dígitos, PV/SP interpretados,
/// `sp_live` (SP ao vivo durante um hold) e contadores de quadros.
library;

double? _asDouble(dynamic v) => v is num ? v.toDouble() : null;
int _asInt(dynamic v) => v is num ? v.toInt() : 0;
bool _asBool(dynamic v) => v == true || v == 1;

class DisplayFrame {
  final bool present;
  final bool alive;
  final int frames;
  final int liveFrames;
  final String text;
  final double? pv;
  final double? sp;
  final double? spLive;
  final List<int> raw;

  const DisplayFrame({
    required this.present,
    required this.alive,
    required this.frames,
    required this.liveFrames,
    required this.text,
    required this.pv,
    required this.sp,
    required this.spLive,
    required this.raw,
  });

  factory DisplayFrame.empty() => const DisplayFrame(
        present: false,
        alive: false,
        frames: 0,
        liveFrames: 0,
        text: '',
        pv: null,
        sp: null,
        spLive: null,
        raw: [],
      );

  factory DisplayFrame.fromJson(Map<String, dynamic> j) {
    final rawList = <int>[];
    final r = j['raw'];
    if (r is List) {
      for (final e in r) {
        rawList.add(e is num ? e.toInt() : 0);
      }
    }
    return DisplayFrame(
      present: true,
      alive: _asBool(j['alive']),
      frames: _asInt(j['frames']),
      liveFrames: _asInt(j['live_frames']),
      text: (j['text'] ?? '').toString(),
      pv: _asDouble(j['pv']),
      sp: _asDouble(j['sp']),
      spLive: _asDouble(j['sp_live']),
      raw: rawList,
    );
  }
}
