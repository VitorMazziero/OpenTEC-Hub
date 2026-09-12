"""Lightweight structural check of the active modular firmware."""
import re
import sys
from pathlib import Path

# tests -> pc_client -> desktop-python -> apps -> sensor-biomassa
DEVICE_ROOT = Path(__file__).resolve().parents[4]
BASE = DEVICE_ROOT / "firmware" / "biomass-sensor"
INO = BASE / "biomass-sensor.ino"
HDR = BASE / "web_ui.h"

fails = []


def check(cond, msg):
    print(("  PASS  " if cond else "  FAIL  ") + msg)
    if not cond:
        fails.append(msg)


def strip_code(src):
    """Remove comments, string/char literals and C++11 raw strings."""
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        if c == '/' and i + 1 < n and src[i + 1] == '/':
            i = src.find('\n', i)
            if i < 0:
                break
        elif c == '/' and i + 1 < n and src[i + 1] == '*':
            j = src.find('*/', i + 2)
            i = n if j < 0 else j + 2
        elif c == 'R' and i + 1 < n and src[i + 1] == '"':
            m = re.match(r'R"([^(]*)\(', src[i:])
            if m:
                delim = ')' + m.group(1) + '"'
                j = src.find(delim, i)
                i = n if j < 0 else j + len(delim)
                out.append('RAWSTR')
            else:
                out.append(c)
                i += 1
        elif c == '"' or c == "'":
            q, i = c, i + 1
            while i < n and src[i] != q:
                i += 2 if src[i] == '\\' else 1
            i += 1
            out.append('LIT')
        else:
            out.append(c)
            i += 1
    return ''.join(out)


sources = [INO, *sorted((BASE / "src").rglob("*.h")),
           *sorted((BASE / "src").rglob("*.cpp"))]
ino = "\n".join(path.read_text(encoding="utf-8") for path in sources)
hdr = HDR.read_text(encoding="utf-8")
code = strip_code(ino)
hcode = strip_code(hdr)

print("[structure]")
check(code.count('{') == code.count('}'),
      f"ino braces balanced ({code.count('{')} vs {code.count('}')})")
check(code.count('(') == code.count(')'),
      f"ino parens balanced ({code.count('(')} vs {code.count(')')})")
check(hcode.count('{') == hcode.count('}'), "web_ui.h braces balanced")

print("\n[raw string literal]")
check(hdr.count('R"HTML(') == 1, "exactly one R\"HTML( opener")
check(hdr.count(')HTML"') == 1, "exactly one )HTML\" closer")
body = hdr.split('R"HTML(', 1)[1].rsplit(')HTML"', 1)[0]
check(')HTML"' not in body, "delimiter does not appear inside the HTML body")
check(body.strip().startswith('<!DOCTYPE html>'), "raw string starts with doctype")
check(body.strip().endswith('</html>'), "raw string ends with </html>")

print("\n[declared vs defined]")
decls = set(re.findall(r'^\s*(?:void|bool|String|uint16_t|uint32_t|long|int)\s+'
                       r'(\w+)\s*\([^;{]*\)\s*;', code, re.M))
defs = set(re.findall(r'^\s*(?:void|bool|String|uint16_t|uint32_t|long|int)\s+'
                      r'(\w+)\s*\([^;{]*\)\s*\{', code, re.M))
missing = sorted(d for d in decls if d not in defs)
check(not missing, f"every forward declaration has a definition (missing: {missing})")

print("\n[re-entrancy guards]")
check('if (g_wdtReady) esp_task_wdt_reset();' in ino, "WDT reset guarded by g_wdtReady")
check('if (g_serverStarted && !g_inHttpHandler) server.handleClient();' in ino,
      "handleClient guarded against nesting")
check('g_inHttpHandler = true;' in ino and 'g_inHttpHandler = false;' in ino,
      "handleCommand sets/clears the handler guard")
check('processJsonCommand(body, /*allowBlocking=*/false);' in ino,
      "HTTP handler forbids blocking commands")
check('processJsonCommand(pending, /*allowBlocking=*/true);' in ino,
      "loop() runs the deferred command with blocking allowed")
check('g_serverStarted = true;' in ino, "g_serverStarted set after server.begin()")
check('g_wdtReady = true;' in ino, "g_wdtReady set after esp_task_wdt_add()")
# A default argument must appear on the prototype and never on the
# definition, or the compiler rejects it as a redefinition. Checked per
# function rather than by a global count, so adding another defaulted
# function does not silently break the check.
for fn in ("processJsonCommand", "handleSerialInput",
           "takePulsedReading"):
    protos = re.findall(rf'^\s*(?:void|bool)\s+{fn}\s*\([^;{{]*\)\s*;',
                        code, re.M)
    defs = re.findall(rf'^\s*(?:void|bool)\s+{fn}\s*\([^;{{]*\)\s*\{{',
                      code, re.M)
    check(len(protos) == 1 and "=" in protos[0].split("(", 1)[1],
          f"{fn} declares its default argument on the prototype")
    check(len(defs) == 1 and "=" not in defs[0].split("(", 1)[1],
          f"{fn} definition omits the default argument")

print("\n[no leftover blocking delays in serviced paths]")
for fn, start in [("runBlankingRoutine", ino.find("void runBlankingRoutine")),
                  ("findAndSetOptimalGear", ino.find("void findAndSetOptimalGear")),
                  ("runMeasurementLoop", ino.find("void runMeasurementLoop"))]:
    end = ino.find("\n}", start)
    seg = ino[start:end]
    bare = re.findall(r'(?<!\w)delay\(', seg)
    check(not bare, f"{fn} uses delayServiced, not delay ({len(bare)} bare calls)")

print("\n[hub gating]")
check('if (!g_hubEnabled) return; // Direct-only' in ino, "checkWifi gated on hub flag")
check('if (g_hubEnabled && WiFi.status() == WL_CONNECTED)' in ino,
      "hub push gated on hub flag")
check('if (g_hubEnabled && now - lastHubPollMs' in ino, "hub poll gated on hub flag")
check('url += "?absorbance=" + String(g_lastAbsorbance, 3);' in ino,
      "hub URL format unchanged from v2.5")

print("\n[NVS compatibility]")
check('constexpr const char* NVS_NAMESPACE' in ino, "NVS_NAMESPACE is const char*")
check('NVS_KEY_HUB_EN' in ino and 'hub_en' in ino,
      "hub flag stored in its own NVS key (DeviceConfig stays v2.5-compatible)")
m = re.search(r'struct DeviceConfig \{(.*?)\n\};', ino, re.S)
check(m and 'hubEnabled' not in m.group(1),
      "DeviceConfig has no new fields (blank calibration survives upgrade)")

print("\n[routes]")
for route in ['"/"', '"/readData"', '"/api/status"', '"/api/history"',
              '"/api/blank"', '"/api/command"', '"/command"']:
    check(f'server.on({route}' in ino.replace('server.on(', 'server.on('),
          f"route {route} registered")

print("\n[sample identity fields]")
for f in ['"seq"', '"t_ms"', '"boot_id"', '"i0"', '"sat"']:
    check(f'json += {f}' in ino or f'\\{f[1:-1]}\\' in ino or f[1:-1] in ino,
          f"payload carries {f}")

print("\n[v4.1 desktop control surface]")
for command in ["auto", "manual", "set_gear", "read_once", "led", "led_off",
                "set_pwm", "set_it", "reset_health", "factory", "save_config",
                "load_config", "test_on", "test_off", "probe_period"]:
    check(f'cmd.equals("{command}")' in ino,
          f"firmware implements desktop command {command}")
for field in ["auto_range", "it_index", "pwm_index", "led_duty", "manual_led",
              "led_test", "test_period", "ema", "it_table", "pwm_table"]:
    check(f'\\"{field}\\"' in ino,
          f"status reports {field}")
check('if (val > 3600000L) val = 3600000L;' in ino,
      "sampling interval capped at 3600 s")

print("\n[LED thermal duty limit]")
check('constexpr float LED_DUTY_LIMIT' in ino, "duty limit is a named constant")
check('uint32_t minSafeRefreshMs()' in ino, "safe-interval helper exists")
# The floor must assume the worst gear auto-ranging could pick: a longer
# integration time means a longer LED on-time per reading.
check('if (g_autoRange) {' in ino and 'if (g_config.itDelays[i] > itMs) itMs' in ino,
      "floor uses the longest reachable IT while auto-ranging")
check('itMs = g_config.itDelays[g_currentItIndex];' in ino,
      "floor uses the locked IT in manual mode")
check('long floorMs = (long)minSafeRefreshMs();' in ino,
      "refresh_ms is clamped to the thermal floor")
check('enforceRefreshFloor(true);' in ino,
      "floor re-applied when the gear lock changes")
check('\\"min_refresh_ms\\"' in ino, "status reports min_refresh_ms")
check('\\"led_duty_limit\\"' in ino, "status reports led_duty_limit")

print("\n[gear selection uses only valid blanks]")
check('bool blankIsValid(int itIndex, int pwmIndex)' in ino,
      "blank validity helper exists")
# Validity itself is asserted under [blank table trust]; this only pins the
# saturation half of the test, which predates the plausibility floor.
check('v < SATURATION_RAW' in ino, "a saturated blank counts as invalid")
check('bool findBrightestValidGear(' in ino, "brightest-valid-gear helper exists")
check('if (!blankIsValid(it, pwm)) continue;' in ino,
      "gear search skips gears with no usable blank")
check('findBrightestValidGear(hdIt, hdPwm)' in ino,
      "High Density Mode targets the brightest VALID gear, not absolute max")
check(ino.count('findBrightestValidGear(') >= 3,
      "search fallback also uses the brightest valid gear")
check('if (v > g_config.HIGH_THRESHOLD_RAW) continue;' in ino,
      "Smart Start stays inside the auto-ranger's accepted band")

print("\n[integration time encoding]")
check('constexpr uint16_t IT_BITS[IT_CHOICE_COUNT]' in ino,
      "IT codes live in one shared table")
check('0b1100, 0b1000, 0b0000, 0b0001, 0b0010, 0b0011' in ino,
      "ALS_IT codes match the VEML7700 datasheet")
check('uint16_t itRegisterFor(uint32_t ms)' in ino, "encoder helper exists")
check('g_config.itSettings[i] = itRegisterFor(g_config.itDelays[i]);' in ino,
      "defaults derive their register value from the shared table")
# The old broken literals must be gone entirely, not merely unused.
for bad in ('0b00100 << 6', '0b01000 << 6', '0b01100 << 6'):
    check(bad not in ino, f"broken legacy IT literal removed ({bad})")
check('itRepaired' in ino and 'g_prefs.remove(NVS_KEY_BLANK);' in ino,
      "stored configs repaired and their blank discarded on boot")

print("\n[serial stays responsive during sweeps]")
check('handleSerialInput(/*allowBlocking=*/false);' in ino,
      "serviceNetwork pumps serial with blocking commands deferred")
# loop() must NOT allow blocking commands either. Running one from inside
# handleSerialInput() leaves its inPump guard set for the whole routine, so
# every nested pump returns immediately and serial dies for ~26 s during a
# blanking sweep -- which is exactly how a serial client's "wait for the sweep
# to start" timed out and never sent "start".
check('handleSerialInput(/*allowBlocking=*/true);' not in ino,
      "loop never runs a blocking command from inside the serial pump")
check(ino.count('handleSerialInput(/*allowBlocking=*/false);') >= 2,
      "both serviceNetwork and loop defer blocking serial commands")
check('Error: a command is already queued' in ino,
      "a second queued command is refused, not silently dropped")
check('if (inPump) return;' in ino, "serial pump is re-entrancy guarded")
check('processJsonCommand(cmd, allowBlocking);' in ino,
      "serial honours the blocking policy it was called with")

print("\n[deterministic integration window]")
check('inline uint32_t ledOnMsFor(uint32_t itMs)' in ino,
      "LED on-time has one definition")
check('inline uint32_t integrationGuardMs(uint32_t itMs)' in ino,
      "the guarded conversion period has one definition")
# The nominal integration time is a label, not a period: measured ~8.5% long.
# Any timing derived from it must carry the guard factor.
check('constexpr float    IT_PERIOD_GUARD  = 1.20f;' in ino,
      "period guard is 1.20 (9.9% over the 1.092 measured)")
# The guard must sit above what was measured with real margin -- it guards an
# oscillator with no published tolerance, not the measurement itself.
_m = re.search(r'IT_PERIOD_GUARD\s*=\s*([\d.]+)f', ino)
check(_m and float(_m.group(1)) >= 1.15,
      f"guard keeps a real margin over the measured 1.092 "
      f"({_m.group(1) if _m else '?'})")
check('IT_PERIOD_GUARD * (float)itMs' in ino,
      "guard is applied to the nominal integration time")
check('return LED_SETTLE_MS + 2 * integrationGuardMs(itMs);' in ino,
      "on-time budgets two guarded periods (worst case)")
# The reading itself must ANCHOR on a boundary it observed, not compute where
# one ought to be -- that is what makes accuracy independent of the guard.
check('uint32_t waitForConversionBoundary(' in ino,
      "boundary-observing helper exists")
check('waitForConversionBoundary(baseline, guardMs, atBoundary)' in ino,
      "the read anchors on an observed conversion boundary")
check('const uint32_t darkFor = millis() - g_ledOffSinceMs;' in ino,
      "a dark conversion is guaranteed before the pulse")
# The LED-off instant must be stamped in ONE place -- pwmSetDutyPercent, which
# every path goes through -- not at each of the dozen call sites that switch
# the LED off.
check('if (percent <= 0.0f && g_targetPct > 0.0f) g_ledOffSinceMs = millis();'
      in ino, "the LED-off instant is stamped inside pwmSetDutyPercent")
check(ino.count('g_ledOffSinceMs = millis();') == 1,
      f"only one place stamps it "
      f"({ino.count('g_ledOffSinceMs = millis();')})")
check('g_boundaryMisses++;' in ino,
      "reads that fail to anchor are counted, not silently accepted")
check('\\"boundary_misses\\"' in ino, "status reports boundary_misses")
check('2 * itMs + INTEGRATION_MARGIN_MS' not in ino,
      "the v4.5 wait on the NOMINAL period is gone")
check('vemlRestartIntegration' not in ino.split(' */')[-1],
      "the rejected ALS_SD restart is not in the code path")
check('ledOnMsFor(itMs) / LED_DUTY_LIMIT' in ino,
      "thermal floor is derived from the real on-time")
check('bool takePulsedReading(' in ino, "one shared pulsed-read path")
# Every measurement path must funnel through it, or the LED-on window and
# the integration window can drift apart again in whichever one opted out.
check(ino.count('takePulsedReading(') >= 6,
      f"all read paths use it ({ino.count('takePulsedReading(')} references)")
# Direct data-register reads are allowed only in the four places that are not
# producing a sample: the boundary poll, takePulsedReading's baseline and its
# final read, and the period probe's dark baseline. Anything else is a fifth
# hand-rolled read sequence, which is how the windows drifted apart before.
_direct = ino.count('vemlRead16(REG_ALS_DATA_L')
check(_direct == 4, f"no unaccounted direct sensor reads ({_direct}, want 4)")
check('constexpr uint8_t  REG_WHITE_DATA  = 0x05;' in ino,
      "0x05 is named as the WHITE channel, not an ALS high byte")

print("\n[conversion period diagnostic]")
check('void probeConversionPeriod(int pwmIndex)' in ino,
      "probe_period measures the real conversion period")
check('cmd.equals("probe_period")' in ino, "probe_period command exists")
check('json += ",\\"within_guard\\":";' in ino,
      "the probe says whether the part stays inside IT_PERIOD_GUARD")
# A period without the temperature it was measured at cannot answer whether
# the guard survives a different thermal environment.
check('\\"temp_c\\"' in ino, "the probe pairs each period with a temperature")
check('\\"soc_temp_c\\"' in ino, "status reports the SoC temperature")
check(ino.count('temperatureRead()') >= 2,
      "temperature is read for both status and the probe")

print("\n[blank table trust]")
check('constexpr uint16_t MIN_VALID_BLANK' in ino,
      "a plausibility floor for blank cells exists")
check('v >= MIN_VALID_BLANK && v < SATURATION_RAW' in ino,
      "a handful of counts no longer passes as a usable gear")
check('constexpr uint32_t BLANK_EPOCH' in ino,
      "blanks carry the epoch of the measurement path that made them")
check('epoch != BLANK_EPOCH' in ino,
      "a blank from an older measurement path is discarded on boot")
check('g_prefs.putUInt(NVS_KEY_BLANKFW, BLANK_EPOCH);' in ino,
      "saving a blank stamps the current epoch")
check(ino.count('g_prefs.remove(NVS_KEY_BLANKFW);') >= 2,
      "every path that erases the blank erases its epoch too")


print("\n[recommended LED ladder]")
check('void applyRecommendedPwmTable()' in ino, "preset helper exists")
check('2.0f, 3.5f, 6.0f, 10.5f, 18.0f, 32.0f, 57.0f, 100.0f' in ino,
      "geometric ladder is the default table")
check('cmd.equals("pwm_preset")' in ino, "pwm_preset command exists")
# Ratios must stay uniform: equal light ratios are equal absorbance steps.
import math as _math
_ladder = [2.0, 3.5, 6.0, 10.5, 18.0, 32.0, 57.0, 100.0]
_ratios = [_ladder[i + 1] / _ladder[i] for i in range(len(_ladder) - 1)]
check(max(_ratios) / min(_ratios) < 1.06,
      f"ladder ratios uniform within 6% ({min(_ratios):.2f}-{max(_ratios):.2f})")
_steps = [_math.log10(r) for r in _ratios]
check(max(_steps) - min(_steps) < 0.02,
      f"absorbance step per gear is uniform "
      f"({min(_steps):.3f}-{max(_steps):.3f} AU)")

print("\n[sample history]")
check('void historyClear()' in ino, "the ring buffer can be emptied")
check('cmd.equals("clear_history")' in ino, "clear_history command exists")
# seq must survive a clear: clients deduplicate by it, and restarting the
# count would make every new sample look like one they already had.
_clear = ino[ino.find('void historyClear()'):]
_clear = _clear[:_clear.find('\n}')]
check('g_seq' not in _clear, "clearing the buffer does not reset g_seq")
check('g_historyHead  = 0;' in _clear and 'g_historyCount = 0;' in _clear,
      "clearing resets both the head and the count")
# The command answers with the status object; that reply is the only way a
# client can tell a real clear from firmware that never had the command,
# since unknown commands are ignored in silence.
_dispatch = ino[ino.find('cmd.equals("clear_history")'):]
check('buildStatusJson()' in _dispatch[:_dispatch.find('return;')],
      "clear_history replies with the status so a client can confirm it")
# first_seq must come from the record actually held. Derived from "samples
# dropped + 1" it silently assumes the buffer still starts at seq 1, which a
# clear makes false, and the client then asks for records that are gone.
check('g_history[readPos].seq' in ino,
      "history reports first_seq from the oldest record it holds")
check('oldestIndex' not in code,
      "the assume-it-starts-at-1 first_seq derivation is gone")

print("\n[paced blanking sweep]")
check('void blankCoolDown(' in ino, "the cool-down helper exists")
check('blankCoolDown(ledOnMsFor(g_config.itDelays[i]), dutyPct);' in ino,
      "each swept cell pays for its own on-time, so long integration slots "
      "wait proportionally longer")
# It must sit inside the else branch: cells past saturation are never pulsed
# and would otherwise buy a cool-down for heat they did not generate.
_sweep = ino[ino.find('void runBlankingRoutine(float dutyPct)'):]
_sweep = _sweep[:_sweep.find('\nString ') if '\nString ' in _sweep else len(_sweep)]
check(_sweep.count('blankCoolDown(') == 1,
      "one cool-down per pulsed cell, not one per table entry")
check('if (g_abortRequested) return;' in
      ino[ino.find('void blankCoolDown('):ino.find('void runBlankingRoutine')],
      "a stop during a cool-down is not made to wait it out")
check('getJsonFloat(json, LIT, found)' in code[code.find('cmd.equals(LIT)'):]
      or 'duty_pct' in ino, "blank accepts a duty_pct")
check('runBlankingRoutine(duty);' in ino,
      "the parsed duty reaches the sweep")
check('duty = 0.0f;' in ino,
      "no duty_pct means the pre-v5.2 back-to-back sweep, unchanged")
check('duty > 60.0f' in ino and 'duty < 1.0f' in ino,
      "the duty is clamped to a range a sweep can finish in")
# Default argument on the prototype only, never on the definition.
_protos = re.findall(r'^\s*void\s+runBlankingRoutine\s*\([^;{]*\)\s*;',
                     code, re.M)
_defs = re.findall(r'^\s*void\s+runBlankingRoutine\s*\([^;{]*\)\s*\{',
                   code, re.M)
check(len(_protos) == 1 and "=" in _protos[0],
      "runBlankingRoutine declares its default argument on the prototype")
check(len(_defs) == 1 and "=" not in _defs[0],
      "runBlankingRoutine definition omits the default argument")
check('\\"sweep_duty_pct\\"' in ino and '\\"sweep_ms\\"' in ino,
      "the blank table reports how it was swept")
check('g_blankSweepDutyPct = -1.0f' in ino,
      "a table restored from NVS admits it does not know how it was swept")

print("\n[JSON command parsing]")
# The Python app posts over HTTP with requests' `json=` kwarg, whose
# json.dumps() defaults put a space after every colon. Firmware up to v5.2
# searched for the literal `"command":"` and a `"command" : "` fallback, so
# {"command": "blank"} matched neither: every string command arriving over
# WiFi was silently dropped while the numeric getters -- which did skip
# whitespace -- kept working, so the link looked healthy and nothing ran.
check('int jsonValueIndex(' in ino,
      "one scanner walks key -> colon -> value")
for _getter in ('long getJsonValue(', 'float getJsonFloat(',
                'String getJsonStringValue('):
    _body = ino[ino.find(_getter):]
    _body = _body[:_body.find('\n}\n') + 3]
    check('jsonValueIndex(json, key)' in _body,
          f"{_getter.split()[1].rstrip('(')} uses the shared scanner")
    check('searchKey' not in _body,
          f"{_getter.split()[1].rstrip('(')} hard-codes no separator literal")
# The scanner itself must skip whitespace on BOTH sides of the colon; one
# side was the whole bug.
_scan = ino[ino.find('int jsonValueIndex('):]
_scan = _scan[:_scan.find('\n}\n') + 3]
check(_scan.count('isspace(json.charAt(i))') == 2,
      "whitespace is skipped before and after the colon")

print("\n[published precision]")
check('constexpr int ABSORBANCE_DECIMALS = 4;' in ino,
      "absorbance is published with 4 decimals")
check(ino.count('String(g_lastAbsorbance, ABSORBANCE_DECIMALS)') == 1 and
      ino.count('String(s.absorbance, ABSORBANCE_DECIMALS)') == 1,
      "live samples and history agree on precision")
check('String(s.absorbance, 3)' not in ino,
      "no JSON payload still rounds absorbance to 0.001 AU")
# The hub URL is the one exception, and stays at 3 decimals on purpose: its
# format is byte-identical to v2.5 so an existing TECNAL hub keeps parsing it.
check('url += "?absorbance=" + String(g_lastAbsorbance, 3);' in ino,
      "the v2.5 hub URL format is left alone")

print("\n" + ("ALL PASS" if not fails else f"{len(fails)} FAILURES:"))
for f in fails:
    print("  - " + f)
sys.exit(1 if fails else 0)
