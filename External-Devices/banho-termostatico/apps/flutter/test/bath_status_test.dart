import 'dart:convert';
import 'package:flutter_test/flutter_test.dart';
import 'package:bath_app/models/bath_status.dart';

void main() {
  group('BathStatus.fromJson', () {
    test('status r2 completo', () {
      final j = jsonDecode('''
        {"device":"bath","version":"BathClient r2 x","uptime_s":120,
         "mode":"auto","guard":"watch","deviation_c":-0.3,"guard_corrections":2,
         "arrows_held_ms":1500,
         "sp_shadow":30.0,"sp_known":true,"sp_target":31.5,"sp_source":1,
         "seq_state":"running","seq_kind":"setpoint","seq_phase":"presses","seq_error":"",
         "presses_done":7,"presses_total":17,"presses_unconfirmed":1,
         "hold_ms":0,"hold_rounds":1,"hold_rate":0.0,
         "display_alive":true,"display_pv":29.8,"display_sp":30.0,"display_text":"29.8",
         "manual_presses":3,"manual_age_s":12,
         "wifi_status":3,"ip":"192.168.8.1","ota":false,"last_cmd_id":4}
      ''') as Map<String, dynamic>;
      final s = BathStatus.fromJson(j);

      expect(s.present, true);
      expect(s.mode, BathMode.auto);
      expect(s.guard, GuardState.watch);
      expect(s.deviationC, -0.3);
      expect(s.guardCorrections, 2);
      expect(s.arrowsHeldMs, 1500);
      expect(s.spShadow, 30.0);
      expect(s.spKnown, true);
      expect(s.spTarget, 31.5);
      expect(s.spSource, 1);
      expect(s.seqState, SeqState.running);
      expect(s.seqKind, SeqKind.setpoint);
      expect(s.seqPhase, SeqPhase.presses);
      expect(s.pressesDone, 7);
      expect(s.pressesTotal, 17);
      expect(s.pressesUnconfirmed, 1);
      expect(s.displayAlive, true);
      expect(s.displayPv, 29.8);
      expect(s.manualPresses, 3);
      expect(s.manualAgeS, 12);
      expect(s.lastCmdId, 4);
      expect(s.seqBusy, true);
      expect(s.supportsModes, true);
    });

    test('sem display: campos null preservados, não viram 0', () {
      final j = jsonDecode('''
        {"device":"bath","version":"BathClient r2","uptime_s":10,
         "mode":"manual","guard":"off","deviation_c":null,"guard_corrections":0,
         "arrows_held_ms":0,
         "sp_shadow":30.0,"sp_known":false,"sp_target":30.0,"sp_source":0,
         "seq_state":"idle","seq_kind":"none","seq_phase":"","seq_error":"",
         "presses_done":0,"presses_total":0,"presses_unconfirmed":0,
         "hold_ms":0,"hold_rounds":0,"hold_rate":0.0,
         "display_alive":false,"display_pv":null,"display_sp":null,"display_text":"",
         "manual_presses":0,"manual_age_s":-1,
         "wifi_status":6,"ip":"0.0.0.0","ota":false,"last_cmd_id":0}
      ''') as Map<String, dynamic>;
      final s = BathStatus.fromJson(j);

      expect(s.deviationC, isNull);
      expect(s.displayPv, isNull);
      expect(s.displaySp, isNull);
      expect(s.displayAlive, false);
      expect(s.spKnown, false);
      expect(s.seqPhase, SeqPhase.none);
      expect(s.seqBusy, false);
      expect(s.manualAgeS, -1);
    });

    test('hold em curso', () {
      final j = jsonDecode('''
        {"mode":"manual","guard":"off","sp_shadow":30.0,"sp_known":true,
         "seq_state":"running","seq_kind":"setpoint","seq_phase":"hold",
         "hold_ms":3200,"hold_rounds":1,"hold_rate":9.5,
         "presses_done":0,"presses_total":300,"display_alive":true}
      ''') as Map<String, dynamic>;
      final s = BathStatus.fromJson(j);
      expect(s.seqPhase, SeqPhase.hold);
      expect(s.holdMs, 3200);
      expect(s.holdRate, 9.5);
      expect(s.holdRounds, 1);
    });

    test('firmware sem modos: supportsModes false', () {
      final j = jsonDecode('{"sp_shadow":30.0,"sp_known":true}')
          as Map<String, dynamic>;
      final s = BathStatus.fromJson(j);
      expect(s.mode, BathMode.unknown);
      expect(s.supportsModes, false);
    });
  });
}
