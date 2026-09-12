/**
 * @file biomass_sensor_analog_v04_direct.ino
 * @brief Firmware for a VEML7700-based biomass sensor. (v4.0 "Direct")
 *
 * Hardware: ESP32-S3-WROOM + VEML7700 (I2C) + LED emitter on PWM.
 *
 * This firmware controls an LED emitter via PWM and reads an ALS (Ambient Light
 * Sensor) to calculate absorbance. It features auto-ranging for both sensor
 * integration time (IT) and LED PWM duty cycle to maintain an optimal
 * signal-to-noise ratio.
 *
 * All settings and blanking calibration data are stored in Non-Volatile
 * Storage (NVS) on the ESP32.
 *
 * ------------------------------------------------------------------------
 * v4.0 IS A COMMUNICATION REWRITE. The measurement core (pulsed read,
 * auto-ranging, blanking, Smart Start, High Density Mode) is UNCHANGED
 * from v2.5 / v03_pulse. Do not "fix" the optical logic here.
 * ------------------------------------------------------------------------
 *
 * DIRECT CONTROL (the point of v4):
 * - USB serial (115200): every machine-readable line is a complete JSON
 *   object starting with '{'. Human log text never starts with '{'.
 * - AP "BiomassSensor" (open) at 192.168.7.1 serving a self-contained
 *   web UI at "/" plus a JSON API.
 *
 * HTTP API:
 *   GET  /                      -> web UI (single page, no external assets)
 *   GET  /readData              -> latest sample (superset of the v2.5 fields)
 *   GET  /api/status            -> device/state/health/config summary
 *   GET  /api/history?since=N   -> samples with seq > N (ring buffer backfill)
 *   GET  /api/blank             -> the 4x8 blanking table (I_0)
 *   POST /api/command           -> JSON command (alias: POST /command)
 *
 * SERIAL COMMANDS (JSON lines, same vocabulary as HTTP):
 *   {"command":"blank"|"start"|"stop"|"status"|"hub_on"|"hub_off"|...}
 *   {"command":"blank","duty_pct":8}  -> paced sweep (~3 min at 8 %)
 *   {"command":"history","since":N}
 *   {"command":"clear_history"}   -> empties the ring buffer, replies status
 *   {"low":10000} {"high":40000} {"opt":25000}
 *
 * HUB COMPATIBILITY:
 * - Retained, but gated on the "hub_en" NVS flag (default ON, so a v2.5
 *   deployment upgrades with no behaviour change).
 * - When enabled: STA connects to "ModuloTECNAL_1"/"ModuloTECNAL_2",
 *   pushes to http://192.168.4.1/biomassData (URL format byte-identical
 *   to v2.5) and polls http://192.168.4.1/biomassCommand.
 * - When disabled: no scan, no push, no poll. The periodic WiFi scan is
 *   what disrupts AP clients, so direct-only users should turn it off.
 *
 * CHANGELOG (v4.0):
 * - NEW: Every sample carries seq, t_ms and boot_id. A client can now
 *   detect dropped samples and device resets instead of silently losing
 *   points. boot_id is persisted in NVS and incremented each boot.
 * - NEW: Samples carry raw AND i0 (the blank used), so absorbance is
 *   recomputable offline if the blank is redone later.
 * - NEW: 1024-sample RAM ring buffer + GET /api/history?since=N. A client
 *   that sleeps or drops WiFi backfills the gap (~85 min at 5 s cadence).
 * - NEW: Self-contained web UI served from PROGMEM (see web_ui.h).
 * - FIXED: Blanking and gear search blocked ALL HTTP for ~15 s. Long waits
 *   now run through delayServiced(), which feeds the watchdog AND services
 *   the web server, so the UI stays alive and shows progress.
 * - FIXED: "stop" now interrupts a running blank/search instead of being
 *   ignored until the blocking routine finishes.
 * - FIXED: doc/code mismatch on the AP address (it is 192.168.7.1) and on
 *   the refresh interval (it is 5 s, the v2.5 comments claimed 10 s).
 * - CHANGED: hub push/poll/scan gated on the "hub_en" flag.
 * - CHANGED: the hub flag lives in its own NVS key, so DeviceConfig stays
 *   binary-compatible with v2.5 and your existing blanking calibration
 *   and thresholds survive the upgrade.
 * - CHANGED: NVS_* string constants are now constexpr const char* (the
 *   old constexpr char* form is an invalid conversion under -std=c++17).
 *
 * CHANGELOG (v4.1) -- manual hardware control for the desktop UI:
 * - NEW: {"command":"manual"} / {"command":"auto"} toggle auto-ranging.
 *   In manual mode the gear never changes on its own and High Density
 *   Mode is suppressed.
 * - NEW: {"command":"set_gear","it":i,"pwm":j} locks a specific gear.
 *   Restricted to the calibrated 4x8 grid on purpose: absorbance needs
 *   I_0, and the blank table only knows I_0 at those combinations.
 * - NEW: {"command":"read_once"} takes a single pulsed read at the current
 *   gear. Unfiltered (no median/EMA history exists for a one-off), so it
 *   is flagged "single" in the record and in the CSV.
 * - NEW: {"command":"led","duty":x} holds the LED at an arbitrary duty
 *   while IDLE, for alignment and optical checks. This produces NO valid
 *   absorbance -- there is no blank for an uncalibrated duty.
 * - NEW: {"refresh_ms":n} sampling interval, {"ema":a} filter coefficient.
 * - NEW: {"command":"set_pwm"} / {"command":"set_it"} edit the gear tables.
 *   Both ERASE the stored blank: the table is indexed by gear, so moving a
 *   gear invalidates its I_0 and every absorbance derived from it. Better
 *   to force a re-blank than to return quietly wrong numbers.
 * - NEW: {"command":"reset_health"}, {"command":"factory"}.
 * - NEW: /api/status reports auto_range, it_index, pwm_index, led_duty,
 *   ema, it_table and pwm_table so a UI can bind to real device state
 *   instead of guessing.
 *
 * CHANGELOG (v4.2) -- gear selection and LED thermal limit:
 * - FIXED: gear selection could land on a gear whose blank saturated during
 *   blanking. Such a gear stores 65535 as I_0, which makes every reading
 *   report the -99 error sentinel. Three places did this: the gear search
 *   scored candidates without checking the blank, and both High Density
 *   Mode and the "no gear found" fallback jumped to absolute max IT/PWM --
 *   a gear that is saturated in any normal calibration. The net effect was
 *   the device going blind exactly when the culture was densest. All three
 *   now go through blankIsValid()/findBrightestValidGear().
 * - CHANGED: Smart Start picks the brightest valid gear at or below
 *   HIGH_THRESHOLD instead of the one nearest OPTIMAL_TARGET. Absorbance
 *   only rises during growth, so starting near the top of the band gives
 *   ~0.6 AU before the first gear change instead of ~0.40 AU. Fewer gear
 *   changes means fewer step discontinuities in the kinetics trace.
 * - CHANGED: default LED ladder is now geometric (2, 3.5, 6, 10.5, 18, 32,
 *   57, 100 %). Each step is ~1.75x the light of the one below, so every
 *   gear change is a uniform ~0.24 AU step. The old table's steps ranged
 *   from 0.10 to 0.30 AU and spanned only 20x; this spans 50x.
 * - NEW: {"command":"pwm_preset"} restores that ladder in one operation.
 * - NEW: sampling intervals that would run the LED above an 8% duty cycle
 *   are refused. Measured drift is -1.0% of signal at 10.8% duty and
 *   -2.6% at 43%, which reads as absorbance that is not there. The floor
 *   assumes the longest IT auto-ranging can reach, because an interval
 *   that is safe at IT 100 ms is 8x too fast at IT 800 ms. Reported as
 *   min_refresh_ms so a UI can bound its control.
 *
 * CHANGELOG (v4.3) -- integration time codes and serial responsiveness:
 * - FIXED: the default integration-time table wrote the wrong ALS_IT
 *   register codes, and had done since v2.5. The codes were encoded as
 *   0,1,2,3 shifted two bits too far, so the slots labelled 200/400/800 ms
 *   actually selected a reserved value, 50 ms and 25 ms. The IT ladder ran
 *   BACKWARDS: measured on hardware, the "400 ms" and "800 ms" slots
 *   returned 0.68x and 0.34x the 100 ms slot instead of 4x and 8x, and the
 *   instrument had a small fraction of its intended dynamic range.
 *   The encoding now lives in one table (IT_BITS/IT_MS) used by both the
 *   defaults and set_it -- the duplicate, divergent copy was the root cause.
 * - NEW: stored configs are repaired on boot. A bad config passes CRC, so
 *   the broken codes would otherwise survive the upgrade. The stored blank
 *   is discarded with them: its cells were measured at integration times
 *   that were not what they claimed.
 * - FIXED: serviceNetwork() pumped the web server but not the serial port,
 *   so USB clients were stonewalled for the ~14 s of a blanking sweep while
 *   HTTP clients stayed responsive -- and "stop" could not abort a sweep
 *   over USB, the recommended transport. Serial is now pumped too, with
 *   the same deferral of blocking commands and a re-entrancy guard.
 *
 * CHANGELOG (v4.4 / v4.5) -- deterministic integration window:
 * - FIXED: a pulsed read did not guarantee that the sensor's integration
 *   window fell inside the LED pulse, so readings were partly or wholly
 *   taken in the dark. This is the most consequential defect found in the
 *   v04 line and it dates back to v03_pulse.
 *
 *   The VEML7700 free-runs: it completes a conversion every IT on its own
 *   internal phase, and a read returns the last one completed. The old code
 *   turned the LED on and waited IT + 15 ms, so the conversion it read
 *   could have STARTED before the LED came on and captured anywhere from
 *   0% to 100% of the illuminated period. Measured on hardware, twelve
 *   reads at a fixed gear with nothing changing:
 *
 *     IT 100 ms -> 573..5222 counts, 89% spread, marching in a sawtooth as
 *                  the polling period beat against the conversion boundary
 *     IT 200 ms -> 6363..9944 counts, 36% spread
 *     IT 800 ms -> first read 18005 against a settled ~40000
 *
 *   It stayed hidden because the v2.5 integration-time bug (see v4.3) made
 *   the 400 ms and 800 ms slots actually 50 ms and 25 ms, far shorter than
 *   their 400/800 ms waits, so a complete window always fitted. Only slot 0
 *   was a genuine 100 ms, and it was affected the whole time. Repairing the
 *   IT codes in v4.3 made every slot honest and exposed this immediately:
 *   an empty beam read 0.903 AU instead of 0.
 *
 * - CHANGED: the LED now stays on for 2x the integration time per reading.
 *   Conversion boundaries are IT apart, so exactly one falls in the window
 *   (t0+IT, t0+2*IT]; that conversion started after the LED came on and is
 *   therefore fully illuminated, whatever the sensor's internal phase.
 *
 *   Restarting the integration by toggling ALS_SD (an I2C register bit --
 *   the part has no shutdown pin) was tried first, to avoid doubling the
 *   on-time. It does not work here: one integration period after the toggle
 *   the data register still read 0..77 counts against an expected 40000+,
 *   with the LED held on continuously to rule out any pulse-timing effect,
 *   and with zero I2C errors, so the writes did land. The part needs far
 *   longer than 2.5 ms + IT to produce a first conversion after waking.
 *
 * - CHANGED: doubling the on-time doubles the thermal duty, so the minimum
 *   sampling interval rises from ~10 s to ~20 s while auto-ranging can
 *   reach IT 800 ms. ledOnMsFor() is the single definition of on-time and
 *   feeds minSafeRefreshMs(), so the thermal limit stays honest. Locking a
 *   shorter IT lowers the floor proportionally.
 * - CHANGED: all four measurement paths (blanking, gear search, the
 *   measurement loop and read_once) now funnel through one
 *   takePulsedReading(). Four hand-rolled copies of the LED/integration
 *   sequence were how the windows drifted apart in the first place.
 *
 * CHANGELOG (v4.6) -- the integration period is not its nominal label:
 * - FIXED: v4.5 waited exactly 2x the NOMINAL integration time and argued
 *   that a conversion boundary must therefore fall inside the LED pulse.
 *   The argument is sound; the premise is not. Measured on this device with
 *   probe_period, the real conversion period runs 9.2 % LONGER than the
 *   label -- and by the same factor in every slot, so it is a fixed scale
 *   error in the oscillator, not scatter:
 *
 *     nominal 100 -> 109.3 ms     nominal 400 -> 436.2 ms
 *     nominal 200 -> 218.7 ms     nominal 800 -> 874.0 ms
 *
 *   When the period is longer than assumed, a wait of 2 x nominal can land
 *   just BEFORE the second boundary and return the first one -- which began
 *   before the LED came on. Measured after v4.5, thirty pulsed reads per
 *   gear with nothing changing: 4 of 30 at IT 400 ms came back 5-11 % low,
 *   1 of 30 at IT 200 ms came back 7.5 % low. That is 0.02-0.05 AU of
 *   absorbance that is not there, appearing at random rather than as drift.
 *   IT 100 ms is immune only by luck (2 x 100 + margin clears 2 x 108.5).
 *
 *   The VEML7700 datasheet (Rev. 1.0, doc 84286) has an Oscillator in its
 *   block diagram and NO integration-time tolerance anywhere in its Basic
 *   Characteristics. There is nothing to design against, so timing that
 *   assumes the label is exact was never sound.
 *
 * - CHANGED: a read now ANCHORS on an observed conversion boundary instead
 *   of trusting the label. The LED comes on, the data register is polled
 *   until it jumps out of the dark, and that instant is a real boundary.
 *   One guarded period later the next conversion has completed, and that
 *   one began after the LED was already on. No assumption about the period
 *   survives except an upper bound (IT_PERIOD_GUARD) used for timeouts.
 * - CHANGED: a dark conversion is now guaranteed to complete before each
 *   pulse. Without it the register still holds the PREVIOUS pulse's bright
 *   value -- true during a blanking sweep, where cells are read back to
 *   back -- and the LED-on transition would be invisible. Costs wall time
 *   only; the LED is off throughout, so it adds no heating.
 * - CHANGED: anchoring makes the typical LED on-time ~1.7x IT instead of a
 *   flat 2x, so the device actually heats LESS than v4.5 despite the
 *   worst-case budget rising to 2.3x IT. The thermal floor uses the worst
 *   case, so the auto-range minimum interval goes from ~20 s to ~23 s.
 * - NEW: {"command":"probe_period"} measures the real conversion period of
 *   each IT slot on the hardware in front of you, by timing two successive
 *   boundaries. Use it to confirm IT_PERIOD_GUARD covers your part.
 * - FIXED: a blank cell of a few counts was accepted as a usable gear.
 *   blankIsValid() only required non-zero, so the 9..109-count rows a
 *   pre-v4.6 sweep leaves behind passed, and the auto-ranger could select a
 *   gear whose I_0 was 9 counts. Cells below MIN_VALID_BLANK are now
 *   rejected.
 * - NEW: blanks carry the epoch of the measurement path that produced them
 *   (NVS "blank_fw"). A blank taken by an older path is discarded on boot
 *   instead of silently poisoning every absorbance. This is what let a
 *   broken table survive on the bench device through three upgrades.
 *
 * CHANGELOG (v5.0) -- margin on the one remaining assumption:
 * - CHANGED: IT_PERIOD_GUARD 1.15 -> 1.20. v4.6 measured the real conversion
 *   period at 1.092x its label and sized the guard 5.3 % above that. That is
 *   a fine margin against the measurement and a poor one against the thing
 *   actually being guarded: an oscillator with no published tolerance and no
 *   published temperature coefficient, characterised at one bench
 *   temperature. 1.20 gives 9.9 % headroom.
 *
 *   Cost, all of it in the worst-case budget rather than in typical
 *   operation, since reads still anchor on an observed boundary:
 *
 *     auto-range minimum sampling interval   23.3 s -> 24.3 s
 *     worst-case LED on-time at IT 800 ms    1866 ms -> 1946 ms
 *
 * - NEW: status reports soc_temp_c, and probe_period pairs each measured
 *   period with the temperature it was measured at. The VEML7700 exposes no
 *   temperature register, so this is the ESP32 die sensor standing in for
 *   board temperature -- enough to tell whether the period moves with
 *   temperature at all, which is the question the guard depends on.
 *
 * CHANGELOG (v5.2) -- the blank stops being taken during its own heat pulse:
 * - NEW: {"command":"blank","duty_pct":D} paces the sweep. Each cell is
 *   followed by an LED-off idle sized from that cell's own on-time, so the
 *   whole sweep holds duty D instead of the ~60 % a back-to-back sweep runs
 *   at. At the firmware's own LED_DUTY_LIMIT (8 %) the sweep takes about
 *   three minutes.
 *
 *   Why it was needed: unpaced, the sweep delivers as much LED energy in 24 s
 *   as five minutes of normal measuring. Every cell is therefore captured at
 *   a different point along a rising temperature, and the finished table
 *   belongs to a thermal state the instrument leaves as soon as it starts
 *   measuring. Measured on this hardware, a blank swept cold left +12 mAU of
 *   absorbance that was not there, decaying over ~25 min. See
 *   characterization/cold_start_warmup.md.
 *
 *   Omitting duty_pct keeps the old behaviour exactly, so a client written
 *   against v5.1 or earlier is unaffected.
 * - NEW: /api/blank reports sweep_duty_pct and sweep_ms, so a run's metadata
 *   records how its I0 was produced. Both read -1/0 after a reboot: the table
 *   survives in NVS, the knowledge of how it was swept does not.
 *
 * CHANGELOG (v5.3) -- JSON with spaces in it is still JSON:
 * - FIXED: no {"command":...} sent over HTTP by the Python desktop app ever
 *   ran. getJsonStringValue() searched for the literal `"command":"` and, as
 *   a fallback, `"command" : "`. Python's json.dumps() defaults to ": "
 *   separators, which is neither, so requests' `json=` kwarg produced
 *   {"command": "blank"} and the key was simply not found. The command came
 *   back empty, no branch matched, and handleCommand() still answered 200
 *   with a status object -- so the app logged the command as accepted and
 *   the device sat idle. "blank" over WiFi was the visible symptom; start,
 *   stop, manual, set_gear and every other string command were equally dead.
 *
 *   It hid behind two things. The web UI uses JSON.stringify() and the app's
 *   serial transport uses json.dumps(separators=(",",":")), both of which
 *   emit compact JSON that the old literals did match -- so the same command
 *   over USB, and every command from the browser, worked. And the NUMERIC
 *   getters already skipped whitespace after the colon, so {"low": 10000}
 *   and friends kept working over HTTP: the link looked perfectly healthy.
 *
 * - CHANGED: key -> colon -> value is now walked once, by jsonValueIndex(),
 *   and all three getters use it. Whitespace is accepted anywhere JSON allows
 *   it. Three getters with three hand-written separator literals was the
 *   defect; one scanner is the fix.
 * - CHANGED: getJsonStringValue() now returns "" for a numeric value instead
 *   of scanning forward to the next quote in the document, which is how it
 *   would have reported the NEXT key's string as this key's value.
 *
 * CHANGELOG (v5.1) -- the buffer stops being permanent:
 * - NEW: {"command":"clear_history"} empties the sample ring buffer and
 *   replies with the status object. The buffer outlives an experiment, so
 *   starting a new one used to mean either inheriting the previous run's
 *   samples or power-cycling the device. g_seq keeps counting across a
 *   clear -- clients deduplicate by it, and restarting the count would make
 *   every new sample look like one they already had.
 * - FIXED: /api/history reported first_seq as "samples dropped + 1", which
 *   assumed the buffer always begins at seq 1. It is now read from the
 *   oldest record actually held, so it stays truthful after a clear.
 * - CHANGED: absorbance is published with 4 decimals instead of 3. One step
 *   of the old 0.001 AU quantum is ~80 sensor counts, against ~12 counts of
 *   measured point-to-point noise on a stable blank: a flat signal was
 *   published as a staircase, and drifts of a few mAU were rounding.
 *
 * DATA VALIDITY NOTE: absorbance recorded at the 100 ms integration slot by
 * any build before v4.4 is unreliable, and blank tables built by those
 * builds inherit the same scatter. Absorbance recorded at 200/400/800 ms by
 * v4.4 or v4.5 carries occasional 5-11 % dropouts from the period error
 * above; those builds' blank tables inherit them too.
 */

#include <Arduino.h>
#include <Wire.h>
#include <Preferences.h>      // For ESP32 Non-Volatile Storage (NVS)
#include <esp_task_wdt.h>     // For Watchdog Timer
#include <algorithm>          // For std::sort (median filter)
#include <cstring>            // For memcpy (median filter)

#include <WiFi.h>
#include <HTTPClient.h>
#include <WebServer.h>

#include "web_ui.h"           // PROGMEM single-page UI

// ==========================
// Firmware Identity
// ==========================
static const char* FW_VERSION = "5.3";
static const char* FW_NAME    = "biomass_sensor_analog_v04_direct";

// ==========================
// Sensor Configuration
// ==========================

// --- PWM ---
constexpr int LED_PWM_PIN   = 18;     // GPIO pin for LED PWM control
constexpr int LEDC_FREQ_HZ  = 2000;   // PWM frequency in Hz
constexpr int LEDC_RES_BITS = 8;      // PWM resolution (8-bit = 0-255)

// --- LED Test Sweep ---
bool     g_ledTestEnable   = false;
float    g_ledTestPct      = 0.0f;
int      g_ledTestDir      = 1;
uint32_t g_ledTestPeriodMs = 50;
uint32_t g_ledTestNextMs   = 0;

// --- I2C ---
constexpr int I2C_SDA_PIN = 8;
constexpr int I2C_SCL_PIN = 9;

// --- LED thermal duty limit ---------------------------------------------
// Measured on hardware (empty cuvette, gear locked at IT 200 ms / PWM 50 %,
// each phase held ~3x the thermal time constant to reach steady state):
//
//     LED duty   interval   raw drift   spurious absorbance
//        4.3%      5000 ms     -0.04%        0.0002 AU
//        6.0%      3583 ms     +0.26%        (within noise)
//        8.0%      2688 ms     -0.34%        0.0015 AU
//        9.0%      2389 ms     -0.45%        0.0020 AU
//       10.8%      2000 ms     -1.00%        0.0044 AU   <-- knee
//       21.5%      1000 ms     -1.62%        0.0071 AU
//       43.0%       500 ms     -2.62%        0.0115 AU
//
// The LED junction heats, output falls, and because I_0 was captured during
// blanking at one thermal state the loss reads as absorbance that is not
// there. It is heating rather than degradation: output recovered within
// minutes of dropping the duty back, with a fitted time constant of ~95 s
// in both directions. (The positive 6% figure is residual recovery from the
// preceding phase, not a real gain -- treat it as zero.)
//
// Drift is flat to ~9% and then turns sharply. 8% sits below that knee with
// margin, costing ~0.0015 AU, which is under the sensor's own noise.
constexpr float LED_DUTY_LIMIT = 0.08f;

// Default sampling interval for a fresh config. The thermal floor works out
// at 24325 ms with auto-ranging able to reach IT 800 ms, so the old default
// -- the floor itself -- was a number nobody would choose on purpose. 25 s is
// the round value just above it: recognisable, legal, and still fast enough
// that cell growth is heavily oversampled. Clamped against the floor anyway,
// so editing the gear tables can never leave the default illegal.
constexpr uint32_t DEFAULT_REFRESH_MS = 25000;

constexpr uint32_t LED_SETTLE_MS         = 10;
constexpr uint32_t INTEGRATION_MARGIN_MS = 8;

// --- Conversion period vs its nominal label -----------------------------
// The VEML7700's conversion period is generated by an internal oscillator.
// The datasheet shows that oscillator in the block diagram and specifies no
// tolerance for it anywhere, so "800 ms" is a label, not a period. Measured
// on this device with probe_period, the real period is 1.092x its label in
// every slot (109.3 / 218.7 / 436.2 / 874.0 ms). v4.5 assumed label == period
// and read 5-11 % low whenever the assumption slipped; see the v4.6 changelog.
//
// The guard is an UPPER bound on period/label, used for the boundary-search
// timeout and for the wait after the boundary. It is the ONE number in the
// timing path still taken on trust, so it is sized for the unknown rather
// than for the measurement:
//
//   measured        1.092  (109.3 / 218.7 / 436.2 / 874.0 ms, all four slots)
//   guard v4.6      1.15   -> 5.3 % headroom
//   guard v5.0      1.20   -> 9.9 % headroom
//
// 1.15 was comfortable at bench temperature and nowhere near comfortable as
// a statement about a part whose oscillator has NO published tolerance and
// NO published temperature coefficient. 1.20 costs ~1 s on the minimum
// sampling interval (23.3 -> 24.3 s in auto-range) and buys back the margin.
//
// Re-measure it whenever low readings appear or the device moves to a
// different thermal environment:
//     {"command":"probe_period"}  ->  per-slot period, temp_c, within_guard
constexpr float    IT_PERIOD_GUARD  = 1.20f;
constexpr uint32_t BOUNDARY_POLL_MS = 5;      // data-register poll interval
constexpr uint16_t BOUNDARY_DELTA   = 32;     // counts that count as "not dark"

/**
 * @brief Longest one conversion can take, with margin.
 *
 * Used as the boundary-search timeout, the post-boundary wait and the dark
 * settle. Everything timing-related derives from this one expression.
 */
inline uint32_t integrationGuardMs(uint32_t itMs) {
  return (uint32_t)(IT_PERIOD_GUARD * (float)itMs) + INTEGRATION_MARGIN_MS;
}

/**
 * @brief WORST-CASE LED on-time for one reading at the given integration
 * time: the boundary search may take a full guarded period, and the wait
 * after the boundary is another.
 *
 * The typical on-time is shorter (the boundary is found after half a period
 * on average), but the thermal floor has to budget for the worst case.
 */
inline uint32_t ledOnMsFor(uint32_t itMs) {
  return LED_SETTLE_MS + 2 * integrationGuardMs(itMs);
}

// --- VEML7700 Sensor ---
constexpr uint8_t  VEML7700_ADDR   = 0x10;
constexpr uint8_t  REG_ALS_CONF    = 0x00;
constexpr uint8_t  REG_ALS_DATA_L  = 0x04;   // ALS result, read as one 16-bit word
// 0x05 is the WHITE channel, NOT the high byte of ALS -- vemlRead16(0x04)
// already returns both bytes. Named for what it is so nobody "completes" a
// 16-bit read by reading 0x05 next.
constexpr uint8_t  REG_WHITE_DATA  = 0x05;
constexpr uint16_t VEML_GAIN_2     = (0b01 << 11); // Fix gain at 2x

// --- VEML7700 ALS_IT encoding (ALS_CONF bits 9:6) -----------------------
// THE single source of truth for integration-time codes. Having two copies
// of this mapping is exactly how v2.5 through v4.1 shipped a broken default
// table: the defaults encoded the codes as 0,1,2,3 shifted two bits too far,
// which put 0b0100 (reserved), 0b1000 (50 ms) and 0b1100 (25 ms) into the
// slots labelled 200, 400 and 800 ms. The integration ladder therefore ran
// backwards -- longer "IT" collected less light -- and the instrument had a
// fraction of its intended dynamic range. Measured on hardware: the 400 ms
// and 800 ms slots returned 0.68x and 0.34x the 100 ms slot instead of 4x
// and 8x.
constexpr int IT_CHOICE_COUNT = 6;
constexpr uint16_t IT_BITS[IT_CHOICE_COUNT] = {
    0b1100, 0b1000, 0b0000, 0b0001, 0b0010, 0b0011};
constexpr uint32_t IT_MS[IT_CHOICE_COUNT] = {25, 50, 100, 200, 400, 800};

/** @brief ALS_CONF field for an integration time, or 0xFFFF if unsupported. */
uint16_t itRegisterFor(uint32_t ms) {
  for (int i = 0; i < IT_CHOICE_COUNT; i++) {
    if (IT_MS[i] == ms) return (uint16_t)(IT_BITS[i] << 6);
  }
  return 0xFFFF;
}
constexpr uint16_t VEML_ALS_ENABLE   = 0x0000;     // Bit 0 = 0 to enable
// ALS_SD (bit 0) is register-only -- there is no shutdown pin on this part.
// Kept documented because toggling it to restart integration was tried and
// rejected; see takePulsedReading().
constexpr uint16_t SATURATION_RAW  = 65530;        // Sensor saturation threshold

// A blank of a handful of counts is not a dim gear, it is a broken cell: the
// dark offset alone is ~3 counts, so I_0 = 9 carries no light information and
// every absorbance divided by it is noise. blankIsValid() used to require
// only "non-zero", which accepted exactly the 9..109-count rows that a
// pre-v4.6 sweep left in NVS. The dimmest genuinely-usable cell measured on
// this optic is ~1700 counts, so 500 rejects corruption without narrowing
// the usable range.
constexpr uint16_t MIN_VALID_BLANK = 500;

// ==========================
// Networking Configuration
// ==========================
// --- Hub (STA) Config ---
static const char* HUB_SSID_A = "ModuloTECNAL_1";
static const char* HUB_SSID_B = "ModuloTECNAL_2";
String sensorHubDataURL    = "http://192.168.4.1/biomassData";
String sensorHubCommandURL = "http://192.168.4.1/biomassCommand";

// --- Access Point (AP) Config ---
static const char* AP_SSID = "BiomassSensor";
IPAddress apIP(192, 168, 7, 1);
IPAddress apGateway(192, 168, 7, 1);
IPAddress apSubnet(255, 255, 255, 0);

WebServer server(80); // Web server for our own AP
HTTPClient http;      // Client for communicating with the hub

// --- Networking Globals ---
String g_lastDataJson  = "{}"; // Latest sample as JSON (serial + /readData)
String g_lastKnownSsid = "";
unsigned long lastWifiCheckMs = 0;
unsigned long WIFI_RECONNECT_PERIOD_MS = 10000; // Check every 10 seconds
unsigned long lastHubPollMs = 0;
unsigned long HUB_POLL_PERIOD_MS = 2000; // Poll hub every 2 seconds
enum WifiReconnectState { WF_IDLE, WF_SCANNING, WF_CONNECTING };
WifiReconnectState g_wifiState = WF_IDLE;
unsigned long g_wifiNextActionMs = 0;

// --- NEW: hub enable flag (direct-only users turn this off) ---
bool g_hubEnabled = true;

// ==========================
// Global Definitions
// ==========================

// --- Watchdog Timer ---
constexpr int WDT_TIMEOUT_S = 10; // 10-second watchdog

// --- State Machine ---
enum SystemState {
  IDLE,
  BLANKING,
  MEASURING,
  SEARCHING // State for finding the best IT/PWM gear
};
SystemState g_state = IDLE;

// --- NEW: cooperative abort for the blocking BLANKING/SEARCHING routines ---
volatile bool g_abortRequested = false;

// --- NEW: re-entrancy guards for serviceNetwork() ---
// serviceNetwork() pumps the web server from inside the long sensor
// routines. Two things must never happen:
//   1. handleClient() called before server.begin() or the WDT fed before
//      the task is subscribed (both happen during setup()).
//   2. handleClient() called while we are already inside a handler. The
//      WebServer keeps the active client in a member, so a nested call
//      would overwrite it and the outer send() would answer the wrong
//      socket. Commands that block are deferred to loop() instead --
//      see g_pendingJson.
bool g_serverStarted  = false;
bool g_wdtReady       = false;
bool g_inHttpHandler  = false;

// --- NEW: command deferred out of an HTTP handler into loop() ---
String g_pendingJson = "";

struct DeviceConfig {
  uint16_t LOW_THRESHOLD_RAW;
  uint16_t HIGH_THRESHOLD_RAW;
  uint16_t OPTIMAL_TARGET_RAW;

  static const int IT_COUNT = 4;
  uint16_t itSettings[IT_COUNT];      // VEML7700 register values for IT
  uint32_t itDelays[IT_COUNT];        // Wait time in ms for each IT
  uint32_t itRefreshTimes[IT_COUNT];  // Poll interval for each IT

  static const int PWM_COUNT = 8;
  float pwmSettings[PWM_COUNT];       // % duty cycle levels

  uint32_t crc32; // Checksum for config integrity
};
DeviceConfig g_config; // The "live" global config

// --- NVS (Non-Volatile Storage) ---
Preferences g_prefs;
constexpr const char* NVS_NAMESPACE  = "biomass_sensor";
constexpr const char* NVS_KEY_CONFIG = "config";
constexpr const char* NVS_KEY_BLANK  = "blanking";
constexpr const char* NVS_KEY_HUB_EN = "hub_en";   // NEW (kept out of DeviceConfig on purpose)
constexpr const char* NVS_KEY_BOOTID = "boot_id";  // NEW
constexpr const char* NVS_KEY_EMA    = "ema";      // NEW (v4.1)
constexpr const char* NVS_KEY_AUTO   = "autorange";// NEW (v4.1)
constexpr const char* NVS_KEY_BLANKFW = "blank_fw";// NEW (v4.6)

// Epoch of the measurement path that produced a stored blank. Bump this
// whenever takePulsedReading() changes in a way that alters what a reading
// MEANS -- a blank is a set of readings, so a table taken by an older path
// is not comparable with what the current path measures.
//
//   1  v2.5..v4.3  pulsed read, wait IT + 15 ms
//   2  v4.4/v4.5   wait 2 x nominal IT (still period-dependent)
//   3  v4.6        anchored on an observed conversion boundary
//
// The bench device carried a v4.2-era table through three upgrades because
// nothing checked; rows 200/400/800 ms held 9..109 counts and every
// absorbance computed from them was meaningless.
constexpr uint32_t BLANK_EPOCH = 3;

struct BlankingData {
  uint16_t blankValues[DeviceConfig::IT_COUNT][DeviceConfig::PWM_COUNT];
  uint32_t timestamp;
  uint32_t crc32;
};
BlankingData g_blankingData; // The "live" global blanking data

// --- Smoothing Filter ---
constexpr int FILTER_WINDOW_SIZE = 5;
uint16_t g_readingWindow[FILTER_WINDOW_SIZE] = {0};
int      g_readingWindowIndex                = 0;
bool     g_filterIsPrimed                    = false;

// --- EMA Low-pass filter ---
float    g_emaFilteredRaw    = 0.0f;
bool     g_emaFilterIsPrimed = false;

// --- Runtime Globals ---
volatile float g_targetPct = 0.0f; // Last commanded duty cycle %
uint16_t g_lastAlsRaw      = 0;    // The *doubly filtered* sensor reading
float    g_lastAbsorbance  = 0.0f;
int      g_currentItIndex  = 0;
int      g_currentPwmIndex = 0;
bool     g_blankIsDone     = false;
uint32_t g_nextReadTime    = 0;

// --- NEW (v4.6): pulse timing ---
// When the LED was last switched off. takePulsedReading() needs a dark
// conversion to have completed before it pulses, otherwise the data register
// still holds the previous pulse and the LED-on transition is invisible.
// Zero is right at boot: the LED has genuinely been off the whole time.
uint32_t g_ledOffSinceMs   = 0;
// Reads that could not see a conversion boundary and fell back to the blind
// wait. Should stay at zero; a rising count means the signal is near
// BOUNDARY_DELTA, i.e. the gear is too dim to be trusted.
uint32_t g_boundaryMisses  = 0;

// --- NEW: sample identity, so a client can detect gaps and resets ---
uint32_t g_seq        = 0;  // Increments once per published sample
uint32_t g_bootId     = 0;  // Persisted in NVS, incremented each boot
uint32_t g_lastSampleMs = 0;
uint16_t g_lastI0     = 0;  // The blank actually used for g_lastAbsorbance
bool     g_lastSat    = false;

// --- Auto-ranging hysteresis counters ---
int g_consecutiveLowReadings  = 0;
int g_consecutiveHighReadings = 0;

// --- High Density Mode globals ---
int  g_consecutiveGearSearches = 0; // Counts failed searches
bool g_highDensityMode         = false;

// --- NEW (v4.1): manual hardware control ---
// With auto-ranging off the firmware never changes gear on its own, so the
// operator owns IT and PWM. High Density Mode is also suppressed -- it exists
// to rescue the auto-ranger, and silently overriding a manually chosen gear
// would be exactly the surprise a manual mode is meant to prevent.
bool  g_autoRange    = true;
bool  g_manualLedOn  = false; // LED held on by the operator while IDLE
float g_manualLedPct = 0.0f;  // duty it is held at
float g_emaAlpha     = 0.8f;  // EMA low-pass coefficient (0 < a <= 1)
bool  g_lastSingle   = false; // last published sample was a single shot

// --- Health Metrics ---
uint32_t g_saturationCount = 0;
uint32_t g_i2cErrorCount   = 0;
uint32_t g_sensorResets    = 0;

// ==========================
// NEW: Sample Ring Buffer
// ==========================
// 1024 samples x 20 bytes = 20 KB of the S3's 512 KB SRAM.
// At the 5 s refresh interval that is ~85 minutes of backfill, which
// comfortably covers a laptop sleeping or a WiFi drop mid-cultivation.
constexpr int HISTORY_SIZE = 1024;
constexpr int HISTORY_MAX_RESPONSE = 60; // Records per /api/history call

// Decimal places for absorbance in every JSON payload. Three was coarser
// than the instrument: one step of 0.001 AU is ~80 sensor counts, while the
// measured point-to-point noise on a stable blank is ~12 counts, so a flat
// signal was published as a staircase and small real drifts disappeared into
// the rounding. Four places puts the quantum (~8 counts) below the noise.
constexpr int ABSORBANCE_DECIMALS = 4;

struct Sample {
  uint32_t seq;
  uint32_t t_ms;
  float    absorbance;
  uint16_t raw;
  uint16_t i0;
  uint8_t  itIdx;
  uint8_t  pwmIdx;
  uint8_t  flags;   // bit0 = HD mode, bit1 = saturated
  uint8_t  _pad;
};
constexpr uint8_t SF_HD_MODE   = 0x01;
constexpr uint8_t SF_SATURATED = 0x02;
//: single-shot reads bypass the median+EMA chain, so they are noisier than
//: the surrounding kinetics. Flagged so analysis can exclude them rather
//: than silently averaging them in with filtered data.
constexpr uint8_t SF_SINGLE    = 0x04;
constexpr uint8_t SF_MANUAL    = 0x08; // taken with auto-ranging disabled

Sample g_history[HISTORY_SIZE];
int      g_historyHead  = 0; // Next write position
uint32_t g_historyCount = 0; // Total samples ever stored (>= HISTORY_SIZE means wrapped)

// --- How the stored blank table was swept (v5.2) ---------------------------
// Reported with the table so a run's metadata says whether its I0 came from
// the fast back-to-back sweep or a paced one. -1 means the table was loaded
// from NVS and this session did not sweep it, so nothing is known.
float    g_blankSweepDutyPct = -1.0f;  // 0 = unpaced, >0 = paced at that duty
uint32_t g_blankSweepMs      = 0;      // wall time the sweep took

// ==========================
// Forward Declarations
// ==========================
// The .ino auto-prototype pass is fragile once a local header is included,
// so everything used before its definition is declared explicitly.
void     saveConfig();
void     loadConfig();
bool     loadBlankingData();
void     saveBlankingData();
void     buildDataJson();
void     sendDataToHub();
void     pollHubForCommands();
void     checkWifi();
void     processJsonCommand(String json, bool allowBlocking = true);
void     runBlankingRoutine(float dutyPct = 0.0f);
void     blankCoolDown(uint32_t onMs, float dutyPct);
void     findAndSetOptimalGear();
void     runMeasurementLoop();
void     publishSample(bool single);
void     readOnce();
void     setAutoRange(bool enabled);
void     setManualGear(int itIndex, int pwmIndex);
void     invalidateBlank(const char* reason);
void     applyRecommendedPwmTable();
uint32_t minSafeRefreshMs();
void     enforceRefreshFloor(bool announce);
bool     blankIsValid(int itIndex, int pwmIndex);
bool     findBrightestValidGear(int &itIndex, int &pwmIndex);
void     findOptimalBlankGear(int &bestItIndex, int &bestPwmIndex);
int      jsonValueIndex(const String& json, const String& key);
float    getJsonFloat(String json, String key, bool &found);
bool     vemlSetConfig(int itIndex);
bool     takePulsedReading(int itIndex, int pwmIndex, uint16_t &out,
                           bool holdLed = false);
uint32_t waitForConversionBoundary(uint16_t baseline, uint32_t timeoutMs,
                                   uint16_t &value);
void     probeConversionPeriod(int pwmIndex);
void     pwmSetDutyPercent(float percent);
void     pwmSetLevel(int pwmIndex);
void     handleI2CError();
bool     resetSensor();
void     resetReadingFilter();
uint16_t getFilteredReading(uint16_t newReading);
void     serviceNetwork();
void     handleSerialInput(bool allowBlocking = true);
uint16_t itRegisterFor(uint32_t ms);
void     delayServiced(uint32_t ms);
void     historyPush(const Sample& s);
void     historyClear();
String   buildStatusJson();
String   buildHistoryJson(uint32_t sinceSeq);
String   buildBlankJson();
void     setHubEnabled(bool enabled);

// ==========================
// CRC32 Helper
// ==========================
// NOTE: this is a byte sum, not a real CRC32. It is kept exactly as-is
// because changing it would invalidate every already-stored NVS blob.
uint32_t calculateCRC32(const uint8_t *data, size_t length) {
  uint32_t crc = 0;
  for (size_t i = 0; i < length; i++) {
    crc += data[i];
  }
  return crc;
}

// ==========================
// NEW: Cooperative Service / Delay
// ==========================

/**
 * @brief Feeds the watchdog and services the web server.
 *
 * Called from inside the long blocking routines (blanking, gear search) so
 * that the AP stays responsive. In v2.5 those routines starved the web
 * server for ~15 s at a time and any live UI simply froze with no
 * explanation.
 *
 * Safe to call mid-routine: the HTTP handlers only read published globals,
 * and processJsonCommand() refuses to start a second blocking routine while
 * one is already running (see the BLANKING/SEARCHING guard there).
 *
 * The g_inHttpHandler guard is what keeps handleClient() from being entered
 * recursively; see the comment on that flag.
 */
void serviceNetwork() {
  if (g_wdtReady) esp_task_wdt_reset();
  if (g_serverStarted && !g_inHttpHandler) server.handleClient();
  // Serial gets pumped too, otherwise USB clients -- the recommended
  // transport -- are stonewalled for the ~14 s of a blanking sweep while
  // HTTP clients stay live. It also makes the abort path reachable: "stop"
  // during a sweep is useless if the port is not being read. Blocking
  // commands are deferred exactly as they are for HTTP.
  handleSerialInput(/*allowBlocking=*/false);
}

/**
 * @brief delay() replacement that keeps the watchdog fed and the AP alive.
 *
 * Integration waits are 100-800 ms, which is far too long to stall HTTP.
 * The 10 ms slice is short enough that the added jitter is negligible
 * against the sensor's integration time.
 */
void delayServiced(uint32_t ms) {
  const uint32_t start = millis();
  while (millis() - start < ms) {
    serviceNetwork();
    uint32_t remaining = ms - (millis() - start);
    delay(remaining > 10 ? 10 : remaining);
  }
  serviceNetwork();
}

// ==========================
// Median Reading Filter
// ==========================

/**
 * @brief Resets the median and EMA filters.
 */
void resetReadingFilter() {
  g_filterIsPrimed     = false;
  g_readingWindowIndex = 0;
  for (int i = 0; i < FILTER_WINDOW_SIZE; i++) {
    g_readingWindow[i] = 0;
  }
  g_emaFilterIsPrimed = false;
}

/**
 * @brief Adds a new reading to the filter and returns the median value.
 */
uint16_t getFilteredReading(uint16_t newReading) {
  if (!g_filterIsPrimed) {
    for (int i = 0; i < FILTER_WINDOW_SIZE; i++) {
      g_readingWindow[i] = newReading;
    }
    g_filterIsPrimed = true;
    return newReading;
  }

  g_readingWindow[g_readingWindowIndex] = newReading;
  g_readingWindowIndex = (g_readingWindowIndex + 1) % FILTER_WINDOW_SIZE;

  uint16_t sortedWindow[FILTER_WINDOW_SIZE];
  memcpy(sortedWindow, g_readingWindow, sizeof(g_readingWindow));
  std::sort(sortedWindow, sortedWindow + FILTER_WINDOW_SIZE);

  return sortedWindow[FILTER_WINDOW_SIZE / 2];
}

// ==========================
// Fault Handling
// ==========================
bool resetSensor() {
  g_sensorResets++;
  Serial.println("! FATAL: Attempting I2C sensor reset...");
  Wire.end();
  delay(100);
  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
  Wire.setClock(100000);
  delay(5);

  uint16_t config_val = VEML_GAIN_2 | g_config.itSettings[0] | VEML_ALS_ENABLE;

  Wire.beginTransmission(VEML7700_ADDR);
  Wire.write(REG_ALS_CONF);
  Wire.write(config_val & 0xFF);
  Wire.write((config_val >> 8) & 0xFF);
  if (Wire.endTransmission() == 0) {
    Serial.println("... Sensor reset successful.");
    g_currentItIndex = 0;
    resetReadingFilter();
    return true;
  } else {
    Serial.println("... Sensor reset FAILED.");
    return false;
  }
}

void handleI2CError() {
  g_i2cErrorCount++;
  Serial.print("! I2C Read Error. Total errors: ");
  Serial.println(g_i2cErrorCount);

  if (g_i2cErrorCount % 5 == 0) { // Try to reset sensor every 5 errors
    resetSensor();
  }
  g_state = IDLE; // Drop to idle on error
  Serial.println("Dropping to IDLE state. Send JSON '{\"command\":\"start\"}' to retry.");
}

// ==========================
// VEML7700 Sensor Helpers
// ==========================
bool vemlWrite16(uint8_t reg, uint16_t val) {
  Wire.beginTransmission(VEML7700_ADDR);
  Wire.write(reg);
  Wire.write(val & 0xFF);
  Wire.write((val >> 8) & 0xFF);
  if (Wire.endTransmission() != 0) {
    return false; // I2C Error
  }
  return true;
}

bool vemlRead16(uint8_t reg, uint16_t &value) {
  Wire.beginTransmission(VEML7700_ADDR);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom(VEML7700_ADDR, static_cast<uint8_t>(2)) != 2) return false;
  uint16_t lo = Wire.read();
  uint16_t hi = Wire.read();
  value = static_cast<uint16_t>((hi << 8) | lo);
  return true;
}

/**
 * @brief Configures the VEML7700 sensor integration time (IT).
 * Also resets auto-ranging hysteresis counters.
 */
bool vemlSetConfig(int itIndex) {
  if (itIndex < 0 || itIndex >= g_config.IT_COUNT) return false;
  g_currentItIndex = itIndex;
  uint16_t config_val =
      VEML_GAIN_2 | g_config.itSettings[itIndex] | VEML_ALS_ENABLE;

  if (!vemlWrite16(REG_ALS_CONF, config_val)) {
    handleI2CError();
    return false;
  }

  resetReadingFilter(); // Reset filter on any "gear change"

  g_consecutiveLowReadings  = 0;
  g_consecutiveHighReadings = 0;

  delayServiced(g_config.itDelays[itIndex] + 5); // Wait for integration to apply
  return true;
}

/**
 * @brief Polls the ALS data register until it leaves the dark baseline.
 *
 * The register only changes when a conversion completes, so the instant it
 * moves IS a conversion boundary -- observed, not assumed. That is the whole
 * point: it replaces arithmetic on a nominal integration time the datasheet
 * never guaranteed.
 *
 * @param baseline the dark value read just before the LED came on.
 * @param timeoutMs give up after this long (one guarded period).
 * @param value    the value seen at the boundary, if one was found.
 * @return millis() at the boundary, or 0 if none was observed.
 */
uint32_t waitForConversionBoundary(uint16_t baseline, uint32_t timeoutMs,
                                   uint16_t &value) {
  const uint32_t t0 = millis();
  while (millis() - t0 < timeoutMs) {
    delayServiced(BOUNDARY_POLL_MS);
    uint16_t v;
    if (!vemlRead16(REG_ALS_DATA_L, v)) return 0;  // caller's read reports it
    int32_t d = (int32_t)v - (int32_t)baseline;
    if (d > (int32_t)BOUNDARY_DELTA || d < -(int32_t)BOUNDARY_DELTA) {
      value = v;
      return millis();
    }
  }
  return 0;
}

/**
 * @brief One complete pulsed reading at the current gear.
 *
 * Every measurement path goes through here so the LED-on window and the
 * sensor's integration window can never drift apart again.
 *
 * HOW THE ILLUMINATED WINDOW IS GUARANTEED
 *
 * The VEML7700 free-runs: it completes a conversion every period on its own
 * internal phase, and a read returns the last one that completed. Turning
 * the LED on and waiting one period is not enough -- the conversion that
 * completes may have started before the LED came on, capturing anywhere from
 * 0 % to 100 % of the illuminated time. Measured before v4.4, twelve reads at
 * a fixed gear with nothing changing gave 573..5222 counts at IT 100 ms
 * (89 % spread), marching in a sawtooth as the polling period beat against
 * the conversion boundary.
 *
 * v4.5 waited 2x the NOMINAL integration time on the reasoning that
 * boundaries are one period apart, so one must fall in (t0+IT, t0+2*IT].
 * The reasoning is right and the premise is wrong: the period is set by an
 * internal oscillator with no specified tolerance, and measures 9.2 % longer
 * than its label here. When the period is longer than assumed the wait lands
 * before the second boundary and returns the first -- 4 of 30 reads at
 * IT 400 ms came back 5-11 % low, at random.
 *
 * So this does not compute where the boundary is, it WATCHES for one:
 *
 *   1. Guarantee a dark conversion has completed, so the register reads dark
 *      rather than still holding the previous pulse's bright value. During a
 *      blanking sweep the cells are read back to back and that is exactly
 *      what it would hold.
 *   2. LED on, settle.
 *   3. Poll until the register jumps out of the dark. That instant is a real
 *      conversion boundary. The conversion that just completed began before
 *      the LED came on, so it is only partly illuminated -- discard it.
 *   4. One guarded period later the NEXT conversion has completed. It began
 *      at the observed boundary, with the LED already on, so it is wholly
 *      illuminated whatever the true period is.
 *
 * The only surviving assumption is an upper bound on the period
 * (IT_PERIOD_GUARD), used for the timeout and the wait in step 4. Being
 * generous there costs LED on-time, never accuracy.
 *
 * If step 3 sees nothing -- a gear so dim its signal is under BOUNDARY_DELTA,
 * which MIN_VALID_BLANK should already have excluded -- it falls back to the
 * blind wait, which is still correct for any period within the guard.
 *
 * Restarting the integration by toggling ALS_SD was tried first, to avoid
 * spending two periods per reading. It does not work on this part: one
 * integration period after the toggle the data register still reads near
 * zero (0..77 counts against an expected 40000+), with the LED held on
 * continuously to rule out any pulse-timing effect. The part needs far
 * longer than 2.5 ms + IT to deliver a first conversion after being woken.
 *
 * Cost: worst case 2 guarded periods of LED on-time, which is what
 * ledOnMsFor() budgets and minSafeRefreshMs() enforces. Typical is ~1.7x IT,
 * since the boundary turns up half a period in on average -- less heating
 * than v4.5's flat 2x.
 *
 * @param pwmIndex gear's LED level; -1 leaves the current duty untouched.
 * @param holdLed  keep the LED at its manual duty afterwards instead of 0.
 */
bool takePulsedReading(int itIndex, int pwmIndex, uint16_t &out,
                       bool holdLed) {
  if (itIndex < 0 || itIndex >= g_config.IT_COUNT) return false;

  const uint32_t guardMs = integrationGuardMs(g_config.itDelays[itIndex]);

  // (1) A dark conversion must have completed since the LED was last on, or
  // the baseline in (3) is the previous pulse's bright value and no jump is
  // visible. The LED is off for this, so it adds wall time but no heating.
  const uint32_t darkFor = millis() - g_ledOffSinceMs;
  if (darkFor < guardMs) delayServiced(guardMs - darkFor);

  // (2)
  if (pwmIndex >= 0) pwmSetLevel(pwmIndex);
  delayServiced(LED_SETTLE_MS);

  uint16_t baseline = 0;
  if (!vemlRead16(REG_ALS_DATA_L, baseline)) {
    pwmSetDutyPercent(holdLed && g_manualLedOn ? g_manualLedPct : 0.0f);
    handleI2CError();
    return false;
  }

  // (3)
  uint16_t atBoundary = 0;
  const uint32_t tBoundary =
      waitForConversionBoundary(baseline, guardMs, atBoundary);

  // (4)
  if (tBoundary != 0) {
    const uint32_t since = millis() - tBoundary;
    if (since < guardMs) delayServiced(guardMs - since);
  } else {
    // Search already spent one guarded period, so this second one puts the
    // read at 2 x guard after LED-on -- still correct for any real period
    // within the guard, just without the anchor's independence from it.
    g_boundaryMisses++;
    delayServiced(guardMs);
  }

  bool ok = vemlRead16(REG_ALS_DATA_L, out);
  pwmSetDutyPercent(holdLed && g_manualLedOn ? g_manualLedPct : 0.0f);
  if (!ok) {
    handleI2CError();
    return false;
  }
  return true;
}

/**
 * @brief Measures the real conversion period of every IT slot.
 *
 * The nominal integration time is a label, not a guarantee -- see
 * IT_PERIOD_GUARD. This measures what the part in front of you actually
 * does, so the guard can be checked rather than trusted.
 *
 * Method: with the register dark, switch the LED on and watch it climb.
 * It steps twice -- first to a partly illuminated conversion, then to a
 * fully illuminated one. Those two steps are exactly one conversion period
 * apart, whatever the phase happened to be.
 *
 * A trial fails when one of the steps is invisible: if the LED came on just
 * after a boundary the partial conversion is nearly dark, and if it came on
 * just before one the partial conversion is nearly full. Both are reported
 * rather than hidden, so the operator can see how many trials landed.
 *
 * Output is one JSON object per IT slot.
 */
void probeConversionPeriod(int pwmIndex) {
  constexpr int TRIALS = 6;
  if (pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) {
    pwmIndex = g_currentPwmIndex;
  }
  const int savedIt = g_currentItIndex;

  for (int i = 0; i < g_config.IT_COUNT; i++) {
    const uint32_t nominal = g_config.itDelays[i];
    const uint32_t guardMs = integrationGuardMs(nominal);
    if (!vemlSetConfig(i)) return;

    uint32_t sum = 0;
    int      ok  = 0;
    uint32_t lo  = 0xFFFFFFFF, hi = 0;

    for (int t = 0; t < TRIALS && !g_abortRequested; t++) {
      pwmSetDutyPercent(0.0f);
      delayServiced(guardMs);              // guarantee a dark conversion

      pwmSetLevel(pwmIndex);
      delayServiced(LED_SETTLE_MS);

      uint16_t dark = 0;
      if (!vemlRead16(REG_ALS_DATA_L, dark)) { handleI2CError(); return; }

      uint16_t vPartial = 0;
      uint32_t t1 = waitForConversionBoundary(dark, guardMs, vPartial);
      uint32_t t2 = 0;
      uint16_t vFull = 0;
      if (t1 != 0) {
        // Second step: from the partial conversion to the full one.
        t2 = waitForConversionBoundary(vPartial, guardMs, vFull);
      }
      pwmSetDutyPercent(0.0f);

      if (t1 != 0 && t2 != 0) {
        uint32_t p = t2 - t1;
        sum += p;
        ok++;
        if (p < lo) lo = p;
        if (p > hi) hi = p;
      }
    }

    String json = "{\"probe\":\"period\",\"it_index\":";
    json += String(i);
    json += ",\"nominal_ms\":";
    json += String(nominal);
    // Paired with the measurement, not sampled separately, so a period and
    // the temperature it was measured at can never be mismatched.
    json += ",\"temp_c\":";
    json += String(temperatureRead(), 1);
    json += ",\"trials\":";
    json += String(TRIALS);
    json += ",\"resolved\":";
    json += String(ok);
    if (ok > 0) {
      const float mean = (float)sum / (float)ok;
      json += ",\"period_ms\":";
      json += String(mean, 1);
      json += ",\"min_ms\":";
      json += String(lo);
      json += ",\"max_ms\":";
      json += String(hi);
      json += ",\"ratio\":";
      json += String(mean / (float)nominal, 3);
      json += ",\"within_guard\":";
      json += ((hi <= (uint32_t)(IT_PERIOD_GUARD * (float)nominal)) ? "true"
                                                                    : "false");
    }
    json += ",\"guard\":";
    json += String(IT_PERIOD_GUARD, 2);
    json += ",\"poll_ms\":";
    json += String(BOUNDARY_POLL_MS);
    json += "}";
    Serial.println(json);
  }

  vemlSetConfig(savedIt);
  pwmSetDutyPercent(0.0f);
}

// ==========================
// PWM Helpers
// ==========================
void pwmSetDutyPercent(float percent) {
  if (percent < 0.0f) percent = 0.0f;
  if (percent > 100.0f) percent = 100.0f;

  const uint32_t maxCount = (1u << LEDC_RES_BITS) - 1u;
  const uint32_t duty =
      static_cast<uint32_t>(percent * maxCount / 100.0f + 0.5f);

  // Every LED-off transition is stamped HERE, not at the call sites, because
  // takePulsedReading()'s dark settle depends on knowing when the LED really
  // went off -- and a dozen places switch it off (stop, abort, the manual
  // hold, the test sweep, the idle guard in loop()). Tracking it per call
  // site is how that invariant would quietly rot.
  if (percent <= 0.0f && g_targetPct > 0.0f) g_ledOffSinceMs = millis();

  analogWrite(LED_PWM_PIN, duty);
  g_targetPct = percent;
}

void pwmSetLevel(int pwmIndex) {
  if (pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) return;
  g_currentPwmIndex = pwmIndex;
  pwmSetDutyPercent(g_config.pwmSettings[pwmIndex]);
  // Filter reset happens in vemlSetConfig(); doing it here would clobber the
  // window between the LED turning on and the read.
}

// ==========================
// NVS (Storage) Functions
// ==========================

/**
 * @brief Loads the `DeviceConfig` struct from NVS.
 * If NVS is empty or corrupt, loads default values.
 *
 * NOTE: DeviceConfig is deliberately unchanged from v2.5 so that a device
 * upgraded to v4 keeps its thresholds and (via BlankingData, also unchanged)
 * its blanking calibration. The hub flag lives in its own NVS key for the
 * same reason.
 */
void loadConfig() {
  if (g_prefs.getBytes(NVS_KEY_CONFIG, &g_config, sizeof(DeviceConfig)) ==
      sizeof(DeviceConfig)) {
    uint32_t crc = calculateCRC32((uint8_t*)&g_config,
                                  sizeof(DeviceConfig) - sizeof(uint32_t));
    if (crc == g_config.crc32) {
      Serial.println("Loaded config from NVS.");

      // Repair the integration-time codes written by v2.5..v4.1. A stored
      // config passes CRC, so without this the broken codes survive the
      // upgrade and the IT ladder keeps running backwards. The blank has to
      // go with them: its cells were measured at the wrong integration
      // times, so every I_0 in the table refers to a gear that no longer
      // means what it says.
      bool itRepaired = false;
      for (int i = 0; i < g_config.IT_COUNT; i++) {
        uint16_t want = itRegisterFor(g_config.itDelays[i]);
        if (want == 0xFFFF) {
          // Stored delay is not a value the sensor supports at all.
          g_config.itDelays[i]   = IT_MS[i + 2 < IT_CHOICE_COUNT ? i + 2 : i];
          want = itRegisterFor(g_config.itDelays[i]);
        }
        if (g_config.itSettings[i] != want) {
          Serial.printf("  IT slot %d: register 0x%04X -> 0x%04X (%lu ms)\n",
                        i, g_config.itSettings[i], want,
                        (unsigned long)g_config.itDelays[i]);
          g_config.itSettings[i] = want;
          itRepaired = true;
        }
      }
      if (itRepaired) {
        Serial.println("!! Integration-time codes repaired (v2.5..v4.1 bug).");
        Serial.println("!! Stored blank discarded -- it was taken at the "
                       "wrong integration times. Re-run 'blank'.");
        g_prefs.remove(NVS_KEY_BLANK);
        g_prefs.remove(NVS_KEY_BLANKFW);
        saveConfig();
      }
      // Older builds stored a 5 s interval, which at IT 800 ms is a 16%
      // LED duty and drifts measurably. Raise any stored value that is
      // below the thermal floor; anything at or above it is the operator's
      // choice and is left alone.
      uint32_t floorMs = minSafeRefreshMs();
      bool needsSave = false;
      for (int i = 0; i < g_config.IT_COUNT; i++) {
        if (g_config.itRefreshTimes[i] < floorMs) {
          g_config.itRefreshTimes[i] = floorMs;
          needsSave = true;
        }
      }
      if (needsSave) {
        Serial.print("Stored sampling interval raised to ");
        Serial.print(floorMs);
        Serial.println(" ms (LED thermal duty limit).");
        saveConfig();
      }
      return;
    }
  }

  Serial.println("No valid config in NVS. Loading defaults.");
  g_config.LOW_THRESHOLD_RAW  = 10000;
  g_config.HIGH_THRESHOLD_RAW = 40000;
  g_config.OPTIMAL_TARGET_RAW = 25000;

  g_config.itDelays[0] = 100;
  g_config.itDelays[1] = 200;
  g_config.itDelays[2] = 400;
  g_config.itDelays[3] = 800;
  for (int i = 0; i < g_config.IT_COUNT; i++) {
    g_config.itSettings[i] = itRegisterFor(g_config.itDelays[i]);
  }

  // DEFAULT_REFRESH_MS, or the thermal floor if that is somehow higher (a
  // hand-edited IT table could push it there). Never below the floor.
  {
    uint32_t floorMs = minSafeRefreshMs();
    uint32_t startMs = (DEFAULT_REFRESH_MS > floorMs) ? DEFAULT_REFRESH_MS
                                                      : floorMs;
    for (int i = 0; i < g_config.IT_COUNT; i++) {
      g_config.itRefreshTimes[i] = startMs;
    }
  }

  applyRecommendedPwmTable();
}

/**
 * @brief Loads the recommended geometric LED ladder.
 *
 * Absorbance is logarithmic in transmitted light, so what matters for even
 * auto-ranging is a constant RATIO between neighbouring levels, not a
 * constant percentage step. Each level here is ~1.75x the light of the one
 * below, which is a uniform ~0.24 AU step everywhere in the range.
 *
 * The old table (5,10,15,20,25,50,75,100) was arithmetic at both ends and
 * gave wildly uneven absorbance steps: 20->25 is only 0.10 AU while 25->50
 * is 0.30 AU. That crowded five levels into the bottom 25% of LED power,
 * where they barely differ, and left 0.30 AU gaps at the top where the
 * auto-ranger had to jump furthest. It also spanned only 20x (1.3 AU); this
 * one spans 50x (1.7 AU).
 *
 * 2% is the sensible floor: at 8-bit resolution that is 5/255 counts, so
 * quantisation is still under 1%, and the LED on-time stays long enough to
 * reach full brightness within each 500 us PWM period.
 */
void applyRecommendedPwmTable() {
  static const float LADDER[DeviceConfig::PWM_COUNT] = {
      2.0f, 3.5f, 6.0f, 10.5f, 18.0f, 32.0f, 57.0f, 100.0f};
  for (int i = 0; i < g_config.PWM_COUNT; i++) {
    g_config.pwmSettings[i] = LADDER[i];
  }
}

void saveConfig() {
  g_config.crc32 = calculateCRC32((uint8_t*)&g_config,
                                  sizeof(DeviceConfig) - sizeof(uint32_t));
  if (g_prefs.putBytes(NVS_KEY_CONFIG, &g_config, sizeof(DeviceConfig))) {
    Serial.println("Config saved to NVS.");
  } else {
    Serial.println("Error saving config to NVS.");
  }
}

bool loadBlankingData() {
  if (g_prefs.getBytes(NVS_KEY_BLANK, &g_blankingData, sizeof(BlankingData)) ==
      sizeof(BlankingData)) {
    uint32_t crc = calculateCRC32(
        (uint8_t*)&g_blankingData, sizeof(BlankingData) - sizeof(uint32_t));
    if (crc == g_blankingData.crc32) {
      // A stored blank passes CRC no matter which firmware measured it, so
      // CRC alone cannot tell a good table from one taken by a measurement
      // path that has since been corrected. The epoch can.
      uint32_t epoch = g_prefs.getUInt(NVS_KEY_BLANKFW, 0);
      if (epoch != BLANK_EPOCH) {
        Serial.print("!! Stored blank was taken by measurement epoch ");
        Serial.print(epoch);
        Serial.print(", this firmware is epoch ");
        Serial.println(BLANK_EPOCH);
        Serial.println("!! Discarded -- its readings are not comparable with "
                       "what this build measures. Re-run 'blank'.");
        g_prefs.remove(NVS_KEY_BLANK);
        memset(&g_blankingData, 0, sizeof(g_blankingData));
        g_blankIsDone = false;
        return false;
      }
      Serial.print("Loaded blanking data from NVS (Timestamp: ");
      Serial.print(g_blankingData.timestamp);
      Serial.println(")");
      g_blankIsDone = true;
      return true;
    }
  }
  Serial.println("No valid blanking data in NVS.");
  g_blankIsDone = false;
  return false;
}

void saveBlankingData() {
  g_blankingData.timestamp = millis(); // Simple timestamp
  g_blankingData.crc32 = calculateCRC32(
      (uint8_t*)&g_blankingData, sizeof(BlankingData) - sizeof(uint32_t));
  if (g_prefs.putBytes(NVS_KEY_BLANK, &g_blankingData, sizeof(BlankingData))) {
    g_prefs.putUInt(NVS_KEY_BLANKFW, BLANK_EPOCH);
    Serial.println("Blanking data saved to NVS.");
  } else {
    Serial.println("Error saving blanking data to NVS.");
  }
}

/**
 * @brief NEW: Enables/disables all hub traffic and persists the choice.
 *
 * With the hub disabled the device never scans for WiFi networks. That
 * matters for direct control: a scan briefly disrupts associated AP
 * clients, so a bench user polling over the AP sees periodic hiccups
 * for a hub that is not even present.
 */
void setHubEnabled(bool enabled) {
  g_hubEnabled = enabled;
  g_prefs.putBool(NVS_KEY_HUB_EN, enabled);
  if (enabled) {
    Serial.println("[NET] Hub mode ENABLED (STA scan/push/poll active).");
    g_wifiState        = WF_IDLE;
    g_wifiNextActionMs = 0;
  } else {
    Serial.println("[NET] Hub mode DISABLED (direct control only).");
    WiFi.disconnect(false, false);
    g_lastKnownSsid = "";
    g_wifiState     = WF_IDLE;
  }
}

// ==========================
// NEW: Sample History
// ==========================

void historyPush(const Sample& s) {
  g_history[g_historyHead] = s;
  g_historyHead = (g_historyHead + 1) % HISTORY_SIZE;
  g_historyCount++;
}

/**
 * @brief Drops every stored sample, so a new experiment starts clean.
 *
 * g_seq is deliberately NOT reset. Clients deduplicate by sequence number,
 * and restarting the count would make every new sample look like one they
 * already had -- the stream would go silent on the PC while the device
 * happily kept measuring. Monotonic seq across a clear costs nothing: the
 * next history reply simply reports the new first_seq.
 */
void historyClear() {
  g_historyHead  = 0;
  g_historyCount = 0;
}

/**
 * @brief Builds a JSON array of samples with seq > sinceSeq.
 *
 * The response is capped at HISTORY_MAX_RESPONSE records; "more":true tells
 * the client to call again with the last seq it received. This is what lets
 * a laptop that slept through an hour of cultivation rebuild the curve
 * without holes.
 */
String buildHistoryJson(uint32_t sinceSeq) {
  // Oldest sample still in the buffer. Once wrapped, the write head points
  // at it; before that the buffer is simply filled from index 0.
  const int readPos = (g_historyCount > (uint32_t)HISTORY_SIZE)
                          ? g_historyHead
                          : 0;

  const uint32_t stored = (g_historyCount > (uint32_t)HISTORY_SIZE)
                              ? (uint32_t)HISTORY_SIZE
                              : g_historyCount;

  // One reserve up front instead of ~60 reallocs; this runs on every poll,
  // and repeated grow-and-copy of a multi-kB String fragments the heap.
  String json;
  json.reserve(8192);
  json += "{\"boot_id\":";
  json += String(g_bootId);
  json += ",\"seq\":";
  json += String(g_seq);
  json += ",\"first_seq\":";
  // Read the oldest record rather than deriving it from how many samples
  // were dropped: after a clear_history the buffer no longer starts at
  // seq 1, and "oldestIndex + 1" would promise the client records the
  // device threw away.
  json += String(stored ? g_history[readPos].seq : g_seq + 1);
  json += ",\"samples\":[";

  int emitted = 0;

  for (uint32_t i = 0; i < stored; i++) {
    const Sample& s = g_history[(readPos + i) % HISTORY_SIZE];
    if (s.seq <= sinceSeq) continue;
    if (emitted >= HISTORY_MAX_RESPONSE) break;

    if (emitted > 0) json += ",";
    json += "{\"seq\":";
    json += String(s.seq);
    json += ",\"t_ms\":";
    json += String(s.t_ms);
    json += ",\"absorbance\":";
    json += String(s.absorbance, ABSORBANCE_DECIMALS);
    json += ",\"raw\":";
    json += String(s.raw);
    json += ",\"i0\":";
    json += String(s.i0);
    json += ",\"it_ms\":";
    json += String(g_config.itDelays[s.itIdx]);
    json += ",\"pwm_pct\":";
    json += String(g_config.pwmSettings[s.pwmIdx], 1);
    json += ",\"hd_mode\":";
    json += (s.flags & SF_HD_MODE) ? "true" : "false";
    json += ",\"sat\":";
    json += (s.flags & SF_SATURATED) ? "true" : "false";
    json += ",\"single\":";
    json += (s.flags & SF_SINGLE) ? "true" : "false";
    json += ",\"manual\":";
    json += (s.flags & SF_MANUAL) ? "true" : "false";
    json += "}";
    emitted++;
  }

  json += "],\"count\":";
  json += String(emitted);
  json += ",\"more\":";
  // "more" is true if we stopped early and newer samples remain.
  bool more = false;
  if (emitted >= HISTORY_MAX_RESPONSE) {
    const Sample& last = g_history[(g_historyHead + HISTORY_SIZE - 1) % HISTORY_SIZE];
    more = (last.seq > sinceSeq + (uint32_t)emitted);
  }
  json += more ? "true" : "false";
  json += "}";
  return json;
}

String buildBlankJson() {
  String json = "{\"blank_done\":";
  json += g_blankIsDone ? "true" : "false";
  json += ",\"timestamp\":";
  json += String(g_blankingData.timestamp);
  json += ",\"sweep_duty_pct\":";
  json += String(g_blankSweepDutyPct, 1);
  json += ",\"sweep_ms\":";
  json += String(g_blankSweepMs);
  json += ",\"it_ms\":[";
  for (int i = 0; i < g_config.IT_COUNT; i++) {
    if (i) json += ",";
    json += String(g_config.itDelays[i]);
  }
  json += "],\"pwm_pct\":[";
  for (int j = 0; j < g_config.PWM_COUNT; j++) {
    if (j) json += ",";
    json += String(g_config.pwmSettings[j], 1);
  }
  json += "],\"i0\":[";
  for (int i = 0; i < g_config.IT_COUNT; i++) {
    if (i) json += ",";
    json += "[";
    for (int j = 0; j < g_config.PWM_COUNT; j++) {
      if (j) json += ",";
      json += String(g_blankingData.blankValues[i][j]);
    }
    json += "]";
  }
  json += "]}";
  return json;
}

String buildStatusJson() {
  const char* stateStr = "idle";
  switch (g_state) {
    case IDLE:      stateStr = "idle";      break;
    case BLANKING:  stateStr = "blanking";  break;
    case MEASURING: stateStr = "measuring"; break;
    case SEARCHING: stateStr = "searching"; break;
  }

  String json = "{\"fw\":\"";
  json += FW_VERSION;
  json += "\",\"name\":\"";
  json += FW_NAME;
  json += "\",\"boot_id\":";
  json += String(g_bootId);
  json += ",\"uptime_ms\":";
  json += String(millis());
  json += ",\"state\":\"";
  json += stateStr;
  json += "\",\"seq\":";
  json += String(g_seq);
  json += ",\"blank_done\":";
  json += g_blankIsDone ? "true" : "false";
  json += ",\"hd_mode\":";
  json += g_highDensityMode ? "true" : "false";
  json += ",\"refresh_ms\":";
  json += String(g_config.itRefreshTimes[g_currentItIndex]);
  // Lets the UI bound its interval control to what the hardware will accept
  // instead of offering settings the device would silently clamp.
  json += ",\"min_refresh_ms\":";
  json += String(minSafeRefreshMs());
  json += ",\"led_duty_limit\":";
  json += String(LED_DUTY_LIMIT, 3);

  // --- v4.1: everything the control UI binds to ---
  json += ",\"auto_range\":";
  json += g_autoRange ? "true" : "false";
  json += ",\"it_index\":";
  json += String(g_currentItIndex);
  json += ",\"pwm_index\":";
  json += String(g_currentPwmIndex);
  json += ",\"it_ms\":";
  json += String(g_config.itDelays[g_currentItIndex]);
  json += ",\"pwm_pct\":";
  json += String(g_config.pwmSettings[g_currentPwmIndex], 1);
  json += ",\"led_duty\":";
  json += String(g_targetPct, 1);
  json += ",\"manual_led\":";
  json += g_manualLedOn ? "true" : "false";
  json += ",\"led_test\":";
  json += g_ledTestEnable ? "true" : "false";
  json += ",\"test_period\":";
  json += String(g_ledTestPeriodMs);
  json += ",\"ema\":";
  json += String(g_emaAlpha, 2);
  json += ",\"it_table\":[";
  for (int i = 0; i < g_config.IT_COUNT; i++) {
    if (i) json += ",";
    json += String(g_config.itDelays[i]);
  }
  json += "],\"pwm_table\":[";
  for (int j = 0; j < g_config.PWM_COUNT; j++) {
    if (j) json += ",";
    json += String(g_config.pwmSettings[j], 1);
  }
  json += "]";
  json += ",\"low\":";
  json += String(g_config.LOW_THRESHOLD_RAW);
  json += ",\"high\":";
  json += String(g_config.HIGH_THRESHOLD_RAW);
  json += ",\"opt\":";
  json += String(g_config.OPTIMAL_TARGET_RAW);
  json += ",\"hub_enabled\":";
  json += g_hubEnabled ? "true" : "false";
  json += ",\"hub_connected\":";
  json += (WiFi.status() == WL_CONNECTED) ? "true" : "false";
  json += ",\"hub_ssid\":\"";
  json += g_lastKnownSsid;
  json += "\",\"ap_ip\":\"";
  json += WiFi.softAPIP().toString();
  json += "\",\"ap_clients\":";
  json += String(WiFi.softAPgetStationNum());
  json += ",\"sta_ip\":\"";
  json += (WiFi.status() == WL_CONNECTED) ? WiFi.localIP().toString() : String("");
  json += "\",\"i2c_errors\":";
  json += String(g_i2cErrorCount);
  json += ",\"saturation_events\":";
  json += String(g_saturationCount);
  json += ",\"sensor_resets\":";
  json += String(g_sensorResets);
  // SoC die temperature. This is the ESP32's own sensor, NOT the VEML7700's
  // and NOT the LED junction -- the VEML7700 exposes no temperature register
  // (its datasheet's six registers are 0x00-0x06). It tracks board and
  // ambient temperature well enough to correlate a period drift against, and
  // reads several degrees above ambient because the die heats itself.
  json += ",\"soc_temp_c\":";
  json += String(temperatureRead(), 1);
  // Reads that fell back to the blind wait because no conversion boundary was
  // visible. Non-zero means a gear too dim to anchor on -- treat its readings
  // with suspicion.
  json += ",\"boundary_misses\":";
  json += String(g_boundaryMisses);
  json += ",\"failed_searches\":";
  json += String(g_consecutiveGearSearches);
  json += ",\"hist_size\":";
  json += String(HISTORY_SIZE);
  json += ",\"hist_stored\":";
  json += String(g_historyCount > (uint32_t)HISTORY_SIZE ? (uint32_t)HISTORY_SIZE
                                                         : g_historyCount);
  json += ",\"free_heap\":";
  json += String(ESP.getFreeHeap());
  json += "}";
  return json;
}

// ==========================
// Blanking Routine
// ==========================
/**
 * @brief Idles with the LED off so one swept cell costs the duty cycle asked
 * for rather than the ~60 % an unpaced sweep runs at.
 *
 * The idle is derived from the cell's own worst-case on-time, so a long
 * integration slot waits proportionally longer and every cell in the table is
 * captured under the same thermal load. Chunked and abort-aware: at 8 % duty
 * an IT 800 ms cell idles for 22 s, and "stop" must not have to wait that out.
 */
void blankCoolDown(uint32_t onMs, float dutyPct) {
  if (dutyPct <= 0.0f || dutyPct >= 100.0f) return;
  const uint32_t idleMs = (uint32_t)((float)onMs * (100.0f / dutyPct - 1.0f));
  const uint32_t start  = millis();
  while (millis() - start < idleMs) {
    if (g_abortRequested) return;
    const uint32_t left = idleMs - (millis() - start);
    delayServiced(left > 200 ? 200 : left);
  }
}

/**
 * @brief Sweeps all 32 gears and stores the blank table.
 *
 * dutyPct > 0 paces the sweep: each cell is followed by an LED-off idle sized
 * to hold the whole sweep at that duty cycle. Unpaced (the default, and what
 * every firmware before v5.2 did) the sweep runs back to back at roughly 60 %
 * duty and delivers, in about 24 s, as much LED energy as five minutes of
 * normal measuring. That spike is captured in the table itself: every cell is
 * measured at a different point along a rising temperature, and the whole
 * table then belongs to a thermal state the instrument immediately leaves.
 *
 * Pacing at LED_DUTY_LIMIT (8 %) takes about three minutes and puts the sweep
 * at the same thermal load the firmware already permits for measuring.
 */
void runBlankingRoutine(float dutyPct) {
  g_state = BLANKING;
  g_abortRequested = false;
  const uint32_t sweepStartedMs = millis();
  const bool     paced          = (dutyPct > 0.0f);
  Serial.println("--- Starting Blanking Routine ---");
  if (paced) {
    Serial.print("Paced sweep at ");
    Serial.print(dutyPct, 1);
    Serial.println("% LED duty: slower, but every cell is measured under the "
                   "same thermal load.");
  }
  Serial.println("Ensure clear media is circulating. This will take a moment...");
  serviceNetwork();

  for (int i = 0; i < g_config.IT_COUNT; i++) {
    if (g_abortRequested) break;
    if (!vemlSetConfig(i)) {
      g_state = IDLE;
      return;
    } // I2C Error
    Serial.print("Setting IT: ");
    Serial.print(g_config.itDelays[i]);
    Serial.println("ms");
    serviceNetwork();
    bool isSaturated = false;

    for (int j = 0; j < g_config.PWM_COUNT; j++) {
      if (g_abortRequested) break;
      serviceNetwork();
      uint16_t reading = 0;
      if (isSaturated) {
        reading = 65535; // If saturated, mark all subsequent PWMs as saturated
      } else {
        uint16_t rawReading;
        if (!takePulsedReading(i, j, rawReading)) {
          g_state = IDLE;
          return;
        }

        reading = rawReading;

        if (reading >= SATURATION_RAW) {
          isSaturated = true;
          reading     = 65535;
        }

        // Only a cell that actually pulsed the LED owes a cool-down; the
        // skipped ones past saturation cost nothing to begin with.
        blankCoolDown(ledOnMsFor(g_config.itDelays[i]), dutyPct);
      }

      g_blankingData.blankValues[i][j] = reading;
      Serial.print("  PWM ");
      Serial.print(g_config.pwmSettings[j]);
      Serial.print("%: RAW = ");
      Serial.println(reading);
    }
  }

  pwmSetDutyPercent(0.0f);

  if (g_abortRequested) {
    // Abort leaves the previously stored blank untouched: a half-swept table
    // would silently corrupt every absorbance computed afterwards.
    Serial.println("--- Blanking ABORTED (previous blank data kept) ---");
    g_abortRequested = false;
    loadBlankingData(); // Restore whatever was last committed
    vemlSetConfig(0);
    g_state = IDLE;
    return;
  }

  saveBlankingData(); // Save to NVS

  // How the table was made, for whoever reads the data later. RAM only: the
  // blank itself survives a reboot in NVS, so after one these read -1 for
  // "stored by an earlier session, pacing unknown" rather than claiming a
  // sweep style the table may not have been made with.
  g_blankSweepDutyPct = paced ? dutyPct : 0.0f;
  g_blankSweepMs      = millis() - sweepStartedMs;

  vemlSetConfig(0);
  g_blankIsDone = true;
  g_state       = IDLE;

  Serial.print("--- Blanking Complete (");
  Serial.print(g_blankSweepMs / 1000.0f, 1);
  Serial.println(" s) ---");
  Serial.println("Send JSON '{\"command\":\"start\"}' to begin measurement.");
}

// ==========================
// Auto-Ranging Measurement
// ==========================

/**
 * @brief Shortest sampling interval that keeps LED heating negligible.
 *
 * Auto-ranging may move to a longer integration time at any moment, and a
 * longer IT means the LED is on for longer per reading. An interval that is
 * safe at IT 100 ms is 8x too fast at IT 800 ms, so while auto-ranging is
 * enabled the limit has to assume the worst gear the device could pick --
 * otherwise the sampling rate silently becomes unsafe the first time the
 * culture gets dense enough to warrant a longer IT.
 *
 * With the gear locked manually there is no such risk, so the limit uses
 * the actual IT in use and fast sampling stays available for short-IT work.
 */
uint32_t minSafeRefreshMs() {
  uint32_t itMs = 0;
  if (g_autoRange) {
    for (int i = 0; i < g_config.IT_COUNT; i++) {
      if (g_config.itDelays[i] > itMs) itMs = g_config.itDelays[i];
    }
  } else {
    itMs = g_config.itDelays[g_currentItIndex];
  }
  return (uint32_t)(ledOnMsFor(itMs) / LED_DUTY_LIMIT);
}

/**
 * @brief Re-clamps the stored interval, e.g. after the gear lock changes.
 */
void enforceRefreshFloor(bool announce) {
  uint32_t floorMs = minSafeRefreshMs();
  bool changed = false;
  for (int i = 0; i < g_config.IT_COUNT; i++) {
    if (g_config.itRefreshTimes[i] < floorMs) {
      g_config.itRefreshTimes[i] = floorMs;
      changed = true;
    }
  }
  if (changed && announce) {
    Serial.print("Sampling interval raised to ");
    Serial.print(floorMs);
    Serial.println(" ms to keep LED duty within the thermal limit.");
  }
}

/**
 * @brief True if this gear has a usable blank.
 *
 * A gear whose blank saturated is stored as 65535 and makes publishSample()
 * emit the -99 error sentinel, so such gears must never be selected -- they
 * can take a reading, but the reading cannot be turned into absorbance.
 */
bool blankIsValid(int itIndex, int pwmIndex) {
  if (!g_blankIsDone) return false;
  uint16_t v = g_blankingData.blankValues[itIndex][pwmIndex];
  // The lower bound is not cosmetic. "v > 0" let cells of 9..109 counts pass
  // as usable gears -- the signature a pre-v4.6 sweep leaves behind -- and
  // the auto-ranger would then divide readings by an I_0 of 9.
  return (v >= MIN_VALID_BLANK && v < SATURATION_RAW);
}

/**
 * @brief The gear with the largest usable blank, i.e. the most light we can
 * put through the sample and still compute absorbance.
 *
 * This is the correct destination for "I need more signal" fallbacks. Jumping
 * to the absolute maximum gear instead (as earlier versions did) lands on a
 * gear whose blank almost certainly saturated during blanking -- with clear
 * media at full power the sensor pegs -- so the device would report -99
 * exactly when the culture is densest.
 */
bool findBrightestValidGear(int &itIndex, int &pwmIndex) {
  itIndex  = -1;
  pwmIndex = -1;
  uint16_t best = 0;
  for (int it = 0; it < g_config.IT_COUNT; it++) {
    for (int pwm = 0; pwm < g_config.PWM_COUNT; pwm++) {
      if (!blankIsValid(it, pwm)) continue;
      uint16_t v = g_blankingData.blankValues[it][pwm];
      if (v > best) {
        best     = v;
        itIndex  = it;
        pwmIndex = pwm;
      }
    }
  }
  return (itIndex >= 0);
}

/**
 * @brief Chooses the gear to begin measuring in ("Smart Start").
 *
 * Biomass only accumulates, so transmitted light only ever falls during a
 * run. The best starting gear is therefore the BRIGHTEST one still inside
 * the auto-ranger's accepted band: that maximises how much absorbance can
 * build up before the first gear change, and every gear change puts a small
 * step discontinuity into the kinetics trace.
 *
 * Earlier versions aimed at OPTIMAL_TARGET (mid-scale), which with the
 * default thresholds leaves only log10(25000/10000) = 0.40 AU of room before
 * the first shift. Starting just under HIGH gives ~0.6 AU, and the readings
 * sit higher above the noise floor for the whole of that span.
 *
 * Note this does not change the TOTAL measurable range -- that is fixed by
 * the brightest valid gear and the noise floor, whatever gear you start in.
 * It reduces how many gear changes a run needs to get there.
 */
void findOptimalBlankGear(int &bestItIndex, int &bestPwmIndex) {
  bestItIndex  = -1;
  bestPwmIndex = -1;
  uint16_t best = 0;

  // Preferred: brightest gear whose blank is inside the accepted band.
  for (int it = 0; it < g_config.IT_COUNT; it++) {
    for (int pwm = 0; pwm < g_config.PWM_COUNT; pwm++) {
      if (!blankIsValid(it, pwm)) continue;
      uint16_t v = g_blankingData.blankValues[it][pwm];
      if (v > g_config.HIGH_THRESHOLD_RAW) continue;
      if (v > best) {
        best         = v;
        bestItIndex  = it;
        bestPwmIndex = pwm;
      }
    }
  }

  // Every valid gear is above HIGH: the optics are brighter than the band
  // allows. Take the dimmest valid gear so we at least start unsaturated.
  if (bestItIndex < 0) {
    uint16_t lowest = 0xFFFF;
    for (int it = 0; it < g_config.IT_COUNT; it++) {
      for (int pwm = 0; pwm < g_config.PWM_COUNT; pwm++) {
        if (!blankIsValid(it, pwm)) continue;
        uint16_t v = g_blankingData.blankValues[it][pwm];
        if (v < lowest) {
          lowest       = v;
          bestItIndex  = it;
          bestPwmIndex = pwm;
        }
      }
    }
    if (bestItIndex >= 0) {
      Serial.println("Smart Start: no gear inside the threshold band; "
                     "using the dimmest valid gear.");
    }
  }

  if (bestItIndex < 0) {
    Serial.println("Smart Start: NO valid blank in any gear. Re-run 'blank'.");
    bestItIndex  = 0;
    bestPwmIndex = 0;
    return;
  }

  Serial.print("Smart Start: IT ");
  Serial.print(g_config.itDelays[bestItIndex]);
  Serial.print("ms, PWM ");
  Serial.print(g_config.pwmSettings[bestPwmIndex]);
  Serial.print("% (blank ");
  Serial.print(g_blankingData.blankValues[bestItIndex][bestPwmIndex]);
  Serial.print(", headroom ");
  Serial.print(log10f((float)g_blankingData.blankValues[bestItIndex][bestPwmIndex] /
                      (float)g_config.LOW_THRESHOLD_RAW), 2);
  Serial.println(" AU before first gear change)");
}

/**
 * @brief Finds the best gear by prioritizing the setting that yields a RAW
 * reading closest to OPTIMAL_TARGET_RAW, while avoiding saturation.
 */
void findAndSetOptimalGear() {
  Serial.println("Signal out of range. Pausing to find optimal new gear...");
  g_state = SEARCHING;
  g_abortRequested = false;
  serviceNetwork();

  pwmSetDutyPercent(0.0f);

  int  bestScoreIT  = -1;
  int  bestScorePWM = -1;
  long bestScore    = -1000000;

  for (int it = 0; it < g_config.IT_COUNT; it++) {
    if (g_abortRequested) break;
    serviceNetwork();

    for (int pwm = 0; pwm < g_config.PWM_COUNT; pwm++) {
      if (g_abortRequested) break;
      serviceNetwork();

      // Skip gears we could never report absorbance for. Besides being
      // wrong to select, testing them wastes an integration period each,
      // so this also shortens the search.
      if (!blankIsValid(it, pwm)) continue;

      // 1. Set config (includes a serviced delay)
      if (!vemlSetConfig(it)) {
        g_state = IDLE;
        return;
      }

      // 2. Pulse Measurement (LED on, restart integration, wait, read, off)
      uint16_t newReading;
      if (!takePulsedReading(it, pwm, newReading)) {
        g_state = IDLE;
        return;
      }

      Serial.print("  Testing IT ");
      Serial.print(g_config.itDelays[it]);
      Serial.print("ms, PWM ");
      Serial.print(g_config.pwmSettings[pwm]);
      Serial.print("%: RAW = ");
      Serial.println(newReading);

      if (newReading >= SATURATION_RAW) {
        Serial.println("  Saturated. Skipping rest of this IT level.");
        g_saturationCount++;
        break; // Stop iterating PWMs for this IT
      }

      // Find the gear closest to OPTIMAL, preferring lower power.
      if (newReading > g_config.LOW_THRESHOLD_RAW &&
          newReading <= g_config.HIGH_THRESHOLD_RAW) {
        long score = -abs(g_config.OPTIMAL_TARGET_RAW - newReading);
        if (score > bestScore) {
          bestScore    = score;
          bestScoreIT  = it;
          bestScorePWM = pwm;
        }
      }
    } // end pwm loop
  } // end it loop

  if (g_abortRequested) {
    Serial.println("--- Gear search ABORTED ---");
    g_abortRequested = false;
    pwmSetDutyPercent(0.0f);
    g_state = IDLE;
    return;
  }

  // --- Decision Logic ---
  if (bestScoreIT != -1) {
    Serial.print("Jumping to BEST gear (Closest to Optimal Target): IT ");
    Serial.print(g_config.itDelays[bestScoreIT]);
    Serial.print("ms, PWM ");
    Serial.print(g_config.pwmSettings[bestScorePWM]);
    Serial.println("%");
    vemlSetConfig(bestScoreIT);
    pwmSetLevel(bestScorePWM);
  } else {
    // Nothing landed in the band. Go as bright as we can while still being
    // able to compute absorbance -- NOT the absolute max gear, whose blank
    // is almost certainly saturated and would yield -99.
    int it, pwm;
    if (findBrightestValidGear(it, pwm)) {
      Serial.print("No reading in target range. Using brightest valid gear: IT ");
      Serial.print(g_config.itDelays[it]);
      Serial.print("ms, PWM ");
      Serial.print(g_config.pwmSettings[pwm]);
      Serial.println("%");
      vemlSetConfig(it);
      pwmSetLevel(pwm);
    } else {
      Serial.println("No valid blank in any gear. Re-run 'blank'.");
      vemlSetConfig(g_config.IT_COUNT - 1);
      pwmSetLevel(g_config.PWM_COUNT - 1);
    }
  }

  g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
  g_state        = MEASURING;
}

/**
 * @brief Performs a single measurement cycle (pulsed read + auto-ranging).
 */
void runMeasurementLoop() {

  uint16_t rawReading;
  if (!takePulsedReading(g_currentItIndex, g_currentPwmIndex, rawReading)) {
    return;
  }

  // --- High Density Mode Check (auto-ranging only) ---
  if (g_highDensityMode && g_autoRange) {
    // In High Density Mode, we only check if the signal has RECOVERED
    if (rawReading > g_config.OPTIMAL_TARGET_RAW) {
      Serial.println("--- High Density Mode Deactivated. Rescanning... ---");
      g_highDensityMode         = false;
      g_consecutiveGearSearches = 0;
      findAndSetOptimalGear();
      return;
    }
  }

  g_lastSat = (rawReading >= SATURATION_RAW);
  if (g_lastSat) {
    g_saturationCount++;
  }

  // --- Apply Median Filter ---
  uint16_t medianReading = getFilteredReading(rawReading);

  if (!g_filterIsPrimed) {
    g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
    return;
  }

  // --- Apply EMA Low-Pass Filter ---
  if (!g_emaFilterIsPrimed) {
    g_emaFilteredRaw    = (float)medianReading;
    g_emaFilterIsPrimed = true;
  } else {
    // alpha is operator-adjustable; 1.0 disables the low-pass entirely
    g_emaFilteredRaw = (g_emaAlpha * (float)medianReading) +
                       ((1.0f - g_emaAlpha) * g_emaFilteredRaw);
  }
  g_lastAlsRaw = (uint16_t)(g_emaFilteredRaw + 0.5f);

  // --- Auto-Ranging Logic (with 10-sample hysteresis & HDM) ---
  bool configChanged = false;

  // Manual mode locks the gear: no searches, no High Density Mode.
  if (g_autoRange && !g_highDensityMode) {

    // --- CASE 1: Signal is too LOW ---
    if (g_lastAlsRaw < g_config.LOW_THRESHOLD_RAW && g_lastAlsRaw > 0) {
      g_consecutiveLowReadings++;
      g_consecutiveHighReadings = 0;
      if (g_consecutiveLowReadings >= 10) {
        g_consecutiveGearSearches++; // Increment failed search counter
        if (g_consecutiveGearSearches >= 3) {
          // --- ACTIVATE HIGH DENSITY MODE ---
          Serial.println("--- High Density Mode Activated (3 failed searches) ---");
          g_highDensityMode         = true;
          g_consecutiveGearSearches = 0;
          g_consecutiveLowReadings  = 0;
          // Brightest gear we can still compute absorbance at. The absolute
          // max gear used previously has a saturated blank in any normal
          // calibration, so HDM used to silently turn every reading into -99.
          int hdIt, hdPwm;
          if (findBrightestValidGear(hdIt, hdPwm)) {
            vemlSetConfig(hdIt);
            pwmSetLevel(hdPwm);
          } else {
            vemlSetConfig(g_config.IT_COUNT - 1);
            pwmSetLevel(g_config.PWM_COUNT - 1);
          }
          configChanged = true;
        } else {
          findAndSetOptimalGear();
          return; // findAndSetOptimalGear will change state
        }
      }
    }
    // --- CASE 2: Signal is too HIGH ---
    else if (g_lastAlsRaw > g_config.HIGH_THRESHOLD_RAW) {
      g_consecutiveHighReadings++;
      g_consecutiveLowReadings  = 0;
      g_consecutiveGearSearches = 0;

      if (g_consecutiveHighReadings >= 10) {
        findAndSetOptimalGear();
        return;
      }
    }
    // --- CASE 3: Signal is IN RANGE ---
    else {
      g_consecutiveLowReadings  = 0;
      g_consecutiveHighReadings = 0;
      g_consecutiveGearSearches = 0;
    }
  }

  if (configChanged) {
    // Only entered when HDM is first activated
    g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
    return;
  }

  publishSample(/*single=*/false);

  // Schedule the next read
  g_nextReadTime = millis() + g_config.itRefreshTimes[g_currentItIndex];
}

/**
 * @brief Computes absorbance from g_lastAlsRaw and publishes one sample.
 *
 * Shared by the measurement loop and the single-shot read so both produce
 * identical records: same seq counter, same ring buffer, same serial line,
 * same hub push. Anything that emits a sample must go through here.
 *
 * @param single true for an unfiltered one-off read (flagged in the record).
 */
void publishSample(bool single) {
  // --- Calculate and Store Absorbance ---
  uint16_t current_I0 =
      g_blankingData.blankValues[g_currentItIndex][g_currentPwmIndex];
  uint16_t current_I = g_lastAlsRaw;

  g_lastI0 = current_I0;

  if (current_I0 == 0 || current_I0 == 65535) {
    g_lastAbsorbance = -99.0f; // Error: Blank is 0 or Saturated
  } else {
    if (current_I > current_I0) {
      current_I = current_I0; // Cap reading at blank value
    }
    if (current_I == 0) {
      g_lastAbsorbance = 9.9f; // Error: True zero reading
    } else {
      // Absorbance = -log10( I / I_0 )
      g_lastAbsorbance = -log10((float)current_I / (float)current_I0);
    }
  }

  // --- Publish (seq/t_ms make gaps detectable) ---
  g_seq++;
  g_lastSampleMs = millis();
  g_lastSingle   = single;

  Sample s;
  s.seq        = g_seq;
  s.t_ms       = g_lastSampleMs;
  s.absorbance = g_lastAbsorbance;
  s.raw        = g_lastAlsRaw;
  s.i0         = g_lastI0;
  s.itIdx      = (uint8_t)g_currentItIndex;
  s.pwmIdx     = (uint8_t)g_currentPwmIndex;
  s.flags      = (uint8_t)((g_highDensityMode ? SF_HD_MODE : 0) |
                           (g_lastSat ? SF_SATURATED : 0) |
                           (single ? SF_SINGLE : 0) |
                           (g_autoRange ? 0 : SF_MANUAL));
  s._pad       = 0;
  historyPush(s);

  buildDataJson();
  Serial.println(g_lastDataJson);

  if (g_hubEnabled && WiFi.status() == WL_CONNECTED) {
    sendDataToHub();
  }
}

/**
 * @brief NEW (v4.1): one pulsed read at the current gear, published as a
 * normal sample but flagged single-shot.
 *
 * Unfiltered on purpose: a one-off read has no median window or EMA history
 * to draw on, and faking one by priming the filters would misrepresent the
 * noise. Use it for alignment checks and spot measurements.
 */
void readOnce() {
  uint16_t rawReading;
  if (!takePulsedReading(g_currentItIndex, g_currentPwmIndex, rawReading,
                         /*holdLed=*/true)) {
    return;
  }

  g_lastSat    = (rawReading >= SATURATION_RAW);
  if (g_lastSat) g_saturationCount++;
  g_lastAlsRaw = rawReading;

  publishSample(/*single=*/true);
}

// =================================
// JSON Parsing / Networking Helpers
// =================================

/**
 * @brief Index of the first character of a key's value, or -1.
 *
 * THE single place that walks "key" -> colon -> value. Whitespace anywhere
 * between the three is accepted, because JSON permits it and real clients
 * emit it: Python's json.dumps() defaults to ", " and ": " separators, so
 * requests' `json=` kwarg produces {"command": "blank"} while the web UI's
 * JSON.stringify() and the desktop app's serial writer both produce the
 * compact {"command":"blank"}.
 *
 * The three getters used to hard-code their own literal separators, and the
 * string one only recognised `"key":"` and `"key" : "` -- neither of which
 * matches `"key": "`. The result was that every {"command":...} arriving over
 * HTTP from the Python app was parsed as no command at all: silently ignored,
 * answered with a 200 and a status object that said "idle", so blank/start/
 * stop did nothing while the numeric settings (whose getters DID skip
 * whitespace after the colon) kept working and the link looked healthy.
 * Same command over USB worked, because the serial transport writes compact
 * JSON. One scanner, so the three getters cannot disagree again.
 */
int jsonValueIndex(const String& json, const String& key) {
  const String needle = "\"" + key + "\"";
  int i = json.indexOf(needle);
  if (i == -1) return -1;
  i += needle.length();

  while (i < (int)json.length() && isspace(json.charAt(i))) i++;
  if (i >= (int)json.length() || json.charAt(i) != ':') return -1;
  i++;
  while (i < (int)json.length() && isspace(json.charAt(i))) i++;
  return (i < (int)json.length()) ? i : -1;
}

/**
 * @brief Finds a numeric value for a given key in a JSON string.
 * Returns -999999 if key not found or value is not numeric.
 */
long getJsonValue(String json, String key) {
  int valueIndex = jsonValueIndex(json, key);
  if (valueIndex == -1) return -999999; // Key not found

  if (json.charAt(valueIndex) == '\"') {
    return -999999; // It's a string, not a number
  }

  int endIndex = json.indexOf(',', valueIndex);
  if (endIndex == -1) {
    endIndex = json.indexOf('}', valueIndex);
  }
  if (endIndex == -1) return -999999; // Malformed

  String valueStr = json.substring(valueIndex, endIndex);
  valueStr.trim();

  if (valueStr.length() == 0 ||
      (!isDigit(valueStr.charAt(0)) && valueStr.charAt(0) != '-')) {
    return -999999;
  }

  return atol(valueStr.c_str());
}

/**
 * @brief Finds a floating-point value for a given key.
 *
 * getJsonValue() truncates to long, which is useless for the EMA
 * coefficient and for fractional LED duty.
 */
float getJsonFloat(String json, String key, bool &found) {
  found = false;
  int valueIndex = jsonValueIndex(json, key);
  if (valueIndex == -1) return 0.0f;
  if (json.charAt(valueIndex) == '\"') {
    return 0.0f; // string, not a number
  }

  int endIndex = json.indexOf(',', valueIndex);
  if (endIndex == -1) endIndex = json.indexOf('}', valueIndex);
  if (endIndex == -1) return 0.0f;

  String valueStr = json.substring(valueIndex, endIndex);
  valueStr.trim();
  if (valueStr.length() == 0 ||
      (!isDigit(valueStr.charAt(0)) && valueStr.charAt(0) != '-' &&
       valueStr.charAt(0) != '.')) {
    return 0.0f;
  }

  found = true;
  return valueStr.toFloat();
}

// ==========================
// NEW (v4.1): Manual Control
// ==========================

/**
 * @brief Enables/disables auto-ranging and persists the choice.
 */
void setAutoRange(bool enabled) {
  g_autoRange = enabled;
  g_prefs.putBool(NVS_KEY_AUTO, enabled);
  if (enabled) {
    Serial.println("Auto-ranging ENABLED.");
    g_consecutiveLowReadings  = 0;
    g_consecutiveHighReadings = 0;
    g_consecutiveGearSearches = 0;
  } else {
    Serial.println("Auto-ranging DISABLED (manual gear lock).");
    g_highDensityMode = false;
  }
  // Re-enabling auto-ranging puts the long integration times back in play,
  // so an interval that was safe under a locked short IT may no longer be.
  enforceRefreshFloor(true);
}

/**
 * @brief Sets the IT/PWM gear explicitly.
 *
 * Restricted to the calibrated 4x8 grid: absorbance needs I_0, and the
 * blanking table only knows I_0 at those combinations. A free-running duty
 * would give a reading with nothing valid to divide it by.
 */
void setManualGear(int itIndex, int pwmIndex) {
  if (itIndex < 0 || itIndex >= g_config.IT_COUNT ||
      pwmIndex < 0 || pwmIndex >= g_config.PWM_COUNT) {
    Serial.println("Error: gear index out of range.");
    return;
  }
  vemlSetConfig(itIndex);
  pwmSetLevel(pwmIndex);
  if (g_state == IDLE) pwmSetDutyPercent(g_manualLedOn ? g_manualLedPct : 0.0f);
  Serial.print("Manual gear set: IT ");
  Serial.print(g_config.itDelays[itIndex]);
  Serial.print("ms, PWM ");
  Serial.print(g_config.pwmSettings[pwmIndex]);
  Serial.println("%");
}

/**
 * @brief Marks the stored blank invalid and erases it from NVS.
 *
 * Called when the operator edits the IT or PWM tables. The blanking table is
 * indexed by gear, so shifting a gear's actual output silently invalidates
 * the I_0 for that cell -- and every absorbance computed from it afterwards
 * would be wrong with no outward sign. Erasing forces a re-blank instead.
 */
void invalidateBlank(const char* reason) {
  g_blankIsDone = false;
  g_prefs.remove(NVS_KEY_BLANK);
  g_prefs.remove(NVS_KEY_BLANKFW);
  memset(&g_blankingData, 0, sizeof(g_blankingData));
  if (g_state == MEASURING || g_state == SEARCHING) {
    g_state = IDLE;
    pwmSetDutyPercent(0.0f);
  }
  Serial.print("!! BLANK INVALIDATED: ");
  Serial.println(reason);
  Serial.println("!! Run 'blank' again before measuring.");
}

/**
 * @brief Finds a string value for a given key in a JSON string.
 */
String getJsonStringValue(String json, String key) {
  int valueIndex = jsonValueIndex(json, key);
  if (valueIndex == -1) return "";           // Key not found
  if (json.charAt(valueIndex) != '\"') return ""; // number, not a string
  valueIndex++;

  int endIndex = json.indexOf('"', valueIndex);
  if (endIndex == -1) return ""; // Malformed

  return json.substring(valueIndex, endIndex);
}

/**
 * @brief Executes a JSON command from serial, HTTP or the hub.
 *
 * NOTE ON RE-ENTRANCY: this can now be called from inside the blocking
 * BLANKING/SEARCHING routines, because those routines service the web
 * server while they wait. Only commands that are safe mid-routine are
 * honoured in those states; everything else is refused rather than
 * allowed to start a second sweep on top of the first.
 *
 * @param allowBlocking false when called from an HTTP handler. "blank" and
 *        "start" take up to ~15 s and pump the web server while they run,
 *        which must not happen underneath an open request. Those two are
 *        parked in g_pendingJson and executed by loop() instead, so the
 *        HTTP client gets its 200 immediately.
 */
void processJsonCommand(String json, bool allowBlocking) {
  Serial.println("[NET] Processing command: " + json);

  String cmd = getJsonStringValue(json, "command");

  // Also accept the {"key":1} shorthand form.
  if (cmd.length() == 0) {
    if (getJsonValue(json, "blank") == 1) cmd = "blank";
    else if (getJsonValue(json, "start") == 1) cmd = "start";
    else if (getJsonValue(json, "stop") == 1) cmd = "stop";
    else if (getJsonValue(json, "print_blank") == 1) cmd = "print_blank";
    else if (getJsonValue(json, "print_config") == 1) cmd = "print_config";
    else if (getJsonValue(json, "print_health") == 1) cmd = "print_health";
    else if (getJsonValue(json, "save_config") == 1) cmd = "save_config";
    else if (getJsonValue(json, "load_config") == 1) cmd = "load_config";
    else if (getJsonValue(json, "test_on") == 1) cmd = "test_on";
    else if (getJsonValue(json, "test_off") == 1) cmd = "test_off";
    else if (getJsonValue(json, "status") == 1) cmd = "status";
    else if (getJsonValue(json, "hub_on") == 1) cmd = "hub_on";
    else if (getJsonValue(json, "hub_off") == 1) cmd = "hub_off";
    else if (getJsonValue(json, "auto") == 1) cmd = "auto";
    else if (getJsonValue(json, "manual") == 1) cmd = "manual";
    else if (getJsonValue(json, "read_once") == 1) cmd = "read_once";
    else if (getJsonValue(json, "probe_period") == 1) cmd = "probe_period";
    else if (getJsonValue(json, "led_off") == 1) cmd = "led_off";
    else if (getJsonValue(json, "reset_health") == 1) cmd = "reset_health";
    else if (getJsonValue(json, "clear_history") == 1) cmd = "clear_history";
  }

  const bool busy = (g_state == BLANKING || g_state == SEARCHING);

  if (cmd.length() > 0) {
    // --- Commands that must work even mid-sweep ---
    if (cmd.equals("stop")) {
      if (busy) {
        Serial.println("--- Abort requested; unwinding current routine ---");
        g_abortRequested = true;
      } else if (g_state == MEASURING) {
        Serial.println("--- Stopping Measurement ---");
        g_state = IDLE;
        pwmSetDutyPercent(0.0f); // Ensure LED is off
        g_consecutiveGearSearches = 0;
        g_highDensityMode         = false;
      }
      return;
    }

    if (cmd.equals("status")) {
      Serial.println(buildStatusJson());
      return;
    }

    if (cmd.equals("history")) {
      long since = getJsonValue(json, "since");
      if (since < 0) since = 0;
      Serial.println(buildHistoryJson((uint32_t)since));
      return;
    }

    // Clearing the buffer touches no hardware, so it is allowed even while a
    // sweep owns the sensor. Answering with the status is what lets a client
    // tell "cleared" from "this firmware has never heard of the command":
    // unknown commands are ignored in silence.
    if (cmd.equals("clear_history")) {
      uint32_t dropped = (g_historyCount > (uint32_t)HISTORY_SIZE)
                             ? (uint32_t)HISTORY_SIZE
                             : g_historyCount;
      historyClear();
      Serial.print("--- Sample history cleared (");
      Serial.print(dropped);
      Serial.println(" sample(s) dropped) ---");
      Serial.println(buildStatusJson());
      return;
    }

    // --- Commands refused while a blocking routine owns the sensor ---
    if (busy && (cmd.equals("blank") || cmd.equals("start") ||
                 cmd.equals("test_on") || cmd.equals("probe_period"))) {
      Serial.println("Error: Busy (blanking or searching). Send 'stop' first.");
      return;
    }

    // --- Blocking commands arriving over HTTP are handed to loop() ---
    // Anything that calls delayServiced() belongs here: those pump the web
    // server, which must not happen underneath an open request.
    if (!allowBlocking && (cmd.equals("blank") || cmd.equals("start") ||
                           cmd.equals("read_once") || cmd.equals("set_gear") ||
                           cmd.equals("probe_period"))) {
      // One slot. Overwriting it would silently drop a command the operator
      // sent -- refuse instead, so the caller sees what happened.
      if (g_pendingJson.length() > 0) {
        Serial.println("Error: a command is already queued. Try again.");
        return;
      }
      g_pendingJson = json;
      Serial.println("[NET] Deferred '" + cmd + "' to main loop.");
      return;
    }

    if (cmd.equals("blank")) {
      if (g_state == IDLE) {
        // No "duty_pct" is the pre-v5.2 back-to-back sweep, so an old client
        // gets exactly the behaviour it was written against.
        bool  found;
        float duty = getJsonFloat(json, "duty_pct", found);
        if (!found) {
          duty = 0.0f;
        } else if (duty < 1.0f) {
          duty = 1.0f;      // below this a sweep takes over 20 minutes
        } else if (duty > 60.0f) {
          duty = 60.0f;     // above this the pacing gains nothing: an
                            // unpaced sweep already runs at about 60 %
        }
        runBlankingRoutine(duty);
      } else {
        Serial.println("Error: Busy");
      }
    } else if (cmd.equals("start")) {
      if (!g_blankIsDone)
        Serial.println("Error: Please run 'blank' first.");
      else if (g_state == IDLE) {
        Serial.println("--- Starting Measurement ---");

        int startIt, startPwm;
        findOptimalBlankGear(startIt, startPwm); // Smart Start

        g_state        = MEASURING;
        g_nextReadTime = millis(); // Start first read immediately
        vemlSetConfig(startIt);
        pwmSetLevel(startPwm);

        g_consecutiveGearSearches = 0;
        g_highDensityMode         = false;
      }
    } else if (cmd.equals("hub_on")) {
      setHubEnabled(true);
    } else if (cmd.equals("hub_off")) {
      setHubEnabled(false);

    // ---- NEW (v4.1): manual hardware control ----
    } else if (cmd.equals("auto")) {
      setAutoRange(true);
    } else if (cmd.equals("manual")) {
      setAutoRange(false);
    } else if (cmd.equals("set_gear")) {
      long it  = getJsonValue(json, "it");
      long pwm = getJsonValue(json, "pwm");
      if (it == -999999 || pwm == -999999) {
        Serial.println("Error: set_gear needs \"it\" and \"pwm\" indices.");
      } else {
        setManualGear((int)it, (int)pwm);
      }
    } else if (cmd.equals("read_once")) {
      if (g_state == IDLE) {
        readOnce();
      } else {
        Serial.println("Error: read_once allowed only in IDLE.");
      }
    } else if (cmd.equals("probe_period")) {
      // Diagnostic: measures what the sensor's conversion period really is,
      // rather than trusting the nominal integration time. Needs the LED, so
      // IDLE only.
      if (g_state != IDLE) {
        Serial.println("Error: probe_period allowed only in IDLE.");
      } else {
        long pwm = getJsonValue(json, "pwm");
        g_abortRequested = false;
        probeConversionPeriod(pwm == -999999 ? -1 : (int)pwm);
      }
    } else if (cmd.equals("led")) {
      if (g_state != IDLE) {
        Serial.println("Error: manual LED allowed only in IDLE.");
      } else {
        bool found;
        float duty = getJsonFloat(json, "duty", found);
        if (!found) {
          Serial.println("Error: led needs \"duty\" (0-100).");
        } else {
          if (duty < 0.0f) duty = 0.0f;
          if (duty > 100.0f) duty = 100.0f;
          g_ledTestEnable = false;     // manual duty overrides the sweep
          g_manualLedOn   = (duty > 0.0f);
          g_manualLedPct  = duty;
          pwmSetDutyPercent(duty);
          Serial.print("Manual LED duty: ");
          Serial.print(duty, 1);
          Serial.println("%");
        }
      }
    } else if (cmd.equals("led_off")) {
      g_manualLedOn  = false;
      g_manualLedPct = 0.0f;
      pwmSetDutyPercent(0.0f);
      Serial.println("Manual LED off.");
    } else if (cmd.equals("set_pwm")) {
      long idx = getJsonValue(json, "index");
      bool found;
      float v = getJsonFloat(json, "value", found);
      if (idx < 0 || idx >= g_config.PWM_COUNT || !found) {
        Serial.println("Error: set_pwm needs \"index\" (0-7) and \"value\" (0-100).");
      } else if (v < 0.0f || v > 100.0f) {
        Serial.println("Error: PWM value must be 0-100.");
      } else {
        g_config.pwmSettings[idx] = v;
        saveConfig();
        invalidateBlank("PWM level table changed");
      }
    } else if (cmd.equals("set_it")) {
      long idx  = getJsonValue(json, "index");
      long code = getJsonValue(json, "code"); // 0..5 -> 25,50,100,200,400,800 ms
      if (idx < 0 || idx >= g_config.IT_COUNT || code < 0 || code > 5) {
        Serial.println("Error: set_it needs \"index\" (0-3) and \"code\" (0-5).");
      } else {
        g_config.itSettings[idx] = (uint16_t)(IT_BITS[code] << 6);
        g_config.itDelays[idx]   = IT_MS[code];
        saveConfig();
        invalidateBlank("integration time table changed");
      }
    } else if (cmd.equals("pwm_preset")) {
      // One shot, one save, one blank invalidation -- setting the eight
      // levels individually would erase the blank eight times over.
      applyRecommendedPwmTable();
      saveConfig();
      invalidateBlank("LED level table reset to the recommended ladder");
    } else if (cmd.equals("reset_health")) {
      g_i2cErrorCount   = 0;
      g_saturationCount = 0;
      g_sensorResets    = 0;
      g_boundaryMisses  = 0;
      Serial.println("Health counters cleared.");
    } else if (cmd.equals("factory")) {
      g_prefs.remove(NVS_KEY_CONFIG);
      loadConfig();
      saveConfig();
      invalidateBlank("factory reset");
      setAutoRange(true);
      g_emaAlpha = 0.8f;
      g_prefs.putFloat(NVS_KEY_EMA, g_emaAlpha);
      Serial.println("Factory defaults restored.");
    } else if (cmd.equals("print_blank")) {
      Serial.println(buildBlankJson());
    } else if (cmd.equals("print_config")) {
      Serial.println(buildStatusJson());
    } else if (cmd.equals("print_health")) {
      Serial.println(buildStatusJson());
    } else if (cmd.equals("save_config")) {
      saveConfig();
    } else if (cmd.equals("load_config")) {
      loadConfig();
    } else if (cmd.equals("test_on")) {
      if (g_state != IDLE) {
        Serial.println("Error: test allowed only in IDLE");
      } else {
        g_ledTestEnable = true;
        g_ledTestPct    = 0.0f;
        g_ledTestDir    = 1;
        g_ledTestNextMs = millis();
        Serial.println("LED test enabled");
      }
    } else if (cmd.equals("test_off")) {
      g_ledTestEnable = false;
      pwmSetDutyPercent(0.0f); // Ensure LED is off
      Serial.println("LED test disabled");
    }
  } // end if(cmd)

  // Check for numeric settings
  long val = getJsonValue(json, "low");
  if (val != -999999) {
    g_config.LOW_THRESHOLD_RAW = (uint16_t)val;
    Serial.print("Live config: LOW_THRESHOLD_RAW set to ");
    Serial.println(g_config.LOW_THRESHOLD_RAW);
  }

  val = getJsonValue(json, "high");
  if (val != -999999) {
    g_config.HIGH_THRESHOLD_RAW = (uint16_t)val;
    Serial.print("Live config: HIGH_THRESHOLD_RAW set to ");
    Serial.println(g_config.HIGH_THRESHOLD_RAW);
  }

  val = getJsonValue(json, "opt");
  if (val != -999999) {
    g_config.OPTIMAL_TARGET_RAW = (uint16_t)val;
    Serial.print("Live config: OPTIMAL_TARGET_RAW set to ");
    Serial.println(g_config.OPTIMAL_TARGET_RAW);
  }

  val = getJsonValue(json, "test_period");
  if (val != -999999) {
    if (val < 5) val = 5;
    g_ledTestPeriodMs = static_cast<uint32_t>(val);
    Serial.print("LED test period set to ");
    Serial.print(g_ledTestPeriodMs);
    Serial.println(" ms");
  }

  // --- NEW (v4.1): sampling interval, applied to every IT level ---
  val = getJsonValue(json, "refresh_ms");
  if (val != -999999) {
    if (val > 3600000L) val = 3600000L;
    long floorMs = (long)minSafeRefreshMs();
    if (val < floorMs) {
      Serial.print("Requested interval ");
      Serial.print(val);
      Serial.print(" ms exceeds the LED thermal duty limit; clamped to ");
      Serial.print(floorMs);
      Serial.println(" ms.");
      val = floorMs;
    }
    for (int i = 0; i < g_config.IT_COUNT; i++) {
      g_config.itRefreshTimes[i] = (uint32_t)val;
    }
    Serial.print("Sampling interval set to ");
    Serial.print(val);
    Serial.println(" ms");
  }

  // --- NEW (v4.1): EMA coefficient ---
  bool foundF;
  float f = getJsonFloat(json, "ema", foundF);
  if (foundF) {
    if (f <= 0.0f) f = 0.01f;
    if (f > 1.0f)  f = 1.0f;
    g_emaAlpha = f;
    g_prefs.putFloat(NVS_KEY_EMA, g_emaAlpha);
    Serial.print("EMA alpha set to ");
    Serial.println(g_emaAlpha, 2);
  }
}

/**
 * @brief Polls the Serial port and processes incoming JSON commands.
 *
 * Re-entrant-safe: serviceNetwork() calls this from inside the blocking
 * sweeps, so a command handled here could otherwise land back in this
 * function through its own nested serviceNetwork() and corrupt the shared
 * line buffer.
 *
 * @param allowBlocking false when called from inside a sweep, so blank and
 *        start are deferred to loop() rather than started on top of the
 *        routine already running.
 */
void handleSerialInput(bool allowBlocking) {
  static char   buf[256];
  static size_t idx = 0;
  static bool   inPump = false;

  if (inPump) return;
  inPump = true;

  while (Serial.available() > 0) {
    int c = Serial.read();
    if (c == '\r') continue;
    if (c == '\n') {
      buf[idx] = '\0';
      idx      = 0;

      String cmd = String(buf);
      cmd.trim();
      if (cmd.length() > 0 && cmd.startsWith("{") && cmd.endsWith("}")) {
        processJsonCommand(cmd, allowBlocking);
      } else if (cmd.length() > 0) {
        Serial.println("Error: Command must be in JSON format. e.g. {\"command\":\"start\"}");
      }
    } else {
      if (idx < sizeof(buf) - 1) {
        buf[idx++] = static_cast<char>(c);
      } else {
        idx = 0; // overflow guard
      }
    }
  }
  inPump = false;
}

/**
 * @brief Creates the sample JSON string from current globals.
 *
 * This is a strict superset of the v2.5 payload: absorbance, raw, it_ms,
 * pwm_pct, blank_done, searching, measuring and hd_mode all keep their old
 * names and meanings, so anything already parsing /readData or the serial
 * stream keeps working. seq/t_ms/boot_id/i0/sat are the new fields.
 */
void buildDataJson() {
  String json = "{";
  json += "\"seq\":";
  json += String(g_seq);
  json += ",\"t_ms\":";
  json += String(g_lastSampleMs);
  json += ",\"boot_id\":";
  json += String(g_bootId);
  json += ",\"absorbance\":";
  json += String(g_lastAbsorbance, ABSORBANCE_DECIMALS);
  json += ",\"raw\":";
  json += String(g_lastAlsRaw);
  json += ",\"i0\":";
  json += String(g_lastI0);
  json += ",\"it_ms\":";
  json += String(g_config.itDelays[g_currentItIndex]);
  json += ",\"pwm_pct\":";

  // Report 0.0 PWM if IDLE, otherwise report the setting
  if (g_state == IDLE) {
    json += "0.0";
  } else {
    json += String(g_config.pwmSettings[g_currentPwmIndex], 1);
  }

  json += ",\"blank_done\":";
  json += g_blankIsDone ? "true" : "false";
  json += ",\"searching\":";
  json += (g_state == SEARCHING) ? "true" : "false";
  json += ",\"measuring\":";
  json += (g_state == MEASURING) ? "true" : "false";
  json += ",\"blanking\":";
  json += (g_state == BLANKING) ? "true" : "false";
  json += ",\"hd_mode\":";
  json += g_highDensityMode ? "true" : "false";
  json += ",\"sat\":";
  json += g_lastSat ? "true" : "false";
  json += ",\"single\":";
  json += g_lastSingle ? "true" : "false";
  json += ",\"manual\":";
  json += g_autoRange ? "false" : "true";
  json += "}";
  g_lastDataJson = json;
}

// --- HTTP GET helper ---
bool httpGet(const String& url, int& code, String& body) {
  http.begin(url);
  http.setReuse(false);
  http.setTimeout(1000); // 1 second timeout
  code = http.GET();
  if (code > 0) body = http.getString();
  else          body = String("err=") + code;
  http.end();
  return code >= 200 && code < 300;
}

/**
 * @brief Pushes the latest sensor data to the Sensor Hub.
 *
 * The URL format is deliberately byte-identical to v2.5 so an existing hub
 * keeps working without any change on its side.
 */
void sendDataToHub() {
  String url = sensorHubDataURL;
  url += "?absorbance=" + String(g_lastAbsorbance, 3);
  url += "&raw=" + String(g_lastAlsRaw);
  url += "&it=" + String(g_config.itDelays[g_currentItIndex]);

  if (g_state == IDLE) {
    url += "&pwm=0.0";
  } else {
    url += "&pwm=" + String(g_config.pwmSettings[g_currentPwmIndex], 1);
  }

  url += "&hd_mode=" + String(g_highDensityMode ? 1 : 0);

  int    code;
  String body;
  if (!httpGet(url, code, body)) {
    Serial.printf("Hub data send FAILED, code %d\n", code);
  }
}

/**
 * @brief Polls the Sensor Hub for pending commands.
 */
void pollHubForCommands() {
  int    code;
  String body;
  if (httpGet(sensorHubCommandURL, code, body)) {
    if (body.length() > 0 && body != "{}") {
      processJsonCommand(body);
    }
  } else {
    Serial.printf("Hub command poll FAILED, code %d\n", code);
  }
}

void checkWifi() {
  if (!g_hubEnabled) return; // Direct-only: never scan, never associate

  unsigned long now = millis();
  if (now < g_wifiNextActionMs) return;

  if (WiFi.status() == WL_CONNECTED) {
    g_wifiState        = WF_IDLE;
    g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
    return;
  }

  switch (g_wifiState) {
    case WF_IDLE:
      if (g_lastKnownSsid != "") {
        Serial.println("[NET] Connecting to known hub: " + g_lastKnownSsid);
        WiFi.disconnect(false, false);
        WiFi.begin(g_lastKnownSsid.c_str(), g_lastKnownSsid.c_str());
        g_wifiState        = WF_CONNECTING;
        g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
      } else {
        Serial.println("[NET] Starting async hub scan...");
        WiFi.scanDelete();
        WiFi.scanNetworks(true, true);
        g_wifiState        = WF_SCANNING;
        g_wifiNextActionMs = now + 100;
      }
      break;

    case WF_SCANNING: {
      int n = WiFi.scanComplete();
      if (n == -1) {
        g_wifiNextActionMs = now + 100;
        break;
      }

      String ssidToTry = "";
      if (n > 0) {
        for (int i = 0; i < n; i++) {
          String ssid = WiFi.SSID(i);
          if (ssid == HUB_SSID_A || ssid == HUB_SSID_B) {
            ssidToTry       = ssid;
            g_lastKnownSsid = ssid;
            break;
          }
        }
      }
      WiFi.scanDelete();

      if (ssidToTry != "") {
        Serial.println("[NET] Hub found: " + ssidToTry + ". Connecting STA.");
        WiFi.disconnect(false, false);
        WiFi.begin(ssidToTry.c_str(), ssidToTry.c_str());
        g_wifiState = WF_CONNECTING;
      } else {
        Serial.println("[NET] Hub not found. Keeping local AP active.");
        g_wifiState = WF_IDLE;
      }
      g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
      break;
    }

    case WF_CONNECTING:
      Serial.println("[NET] Connect attempt timed out. Will scan again later.");
      g_lastKnownSsid    = "";
      g_wifiState        = WF_IDLE;
      g_wifiNextActionMs = now + WIFI_RECONNECT_PERIOD_MS;
      break;
  }
}

// ==========================
// Web Server Handlers
// ==========================

void sendJson(const String& json) {
  server.sendHeader("Access-Control-Allow-Origin", "*");
  server.sendHeader("Cache-Control", "no-store");
  server.send(200, "application/json", json);
}

/**
 * @brief GET / -> the self-contained web UI.
 */
void handleRoot() {
  server.sendHeader("Cache-Control", "no-store");
  server.send_P(200, "text/html", WEB_UI_HTML);
}

/**
 * @brief GET /readData -> latest sample.
 */
void handleReadData() {
  buildDataJson();
  sendJson(g_lastDataJson);
}

void handleStatus() {
  sendJson(buildStatusJson());
}

void handleHistory() {
  uint32_t since = 0;
  if (server.hasArg("since")) {
    long v = server.arg("since").toInt();
    if (v > 0) since = (uint32_t)v;
  }
  sendJson(buildHistoryJson(since));
}

void handleBlankTable() {
  sendJson(buildBlankJson());
}

/**
 * @brief POST /api/command (alias /command) -> accepts JSON commands.
 */
void handleCommand() {
  if (server.hasArg("plain")) {
    String body = server.arg("plain");
    g_inHttpHandler = true;
    processJsonCommand(body, /*allowBlocking=*/false);
    g_inHttpHandler = false;
    server.sendHeader("Access-Control-Allow-Origin", "*");
    server.send(200, "application/json", buildStatusJson());
  } else {
    server.send(400, "text/plain", "Bad Request - No Body");
  }
}

void handleNotFound() {
  server.send(404, "text/plain", "Not Found");
}

// ==========================
// Arduino Entry Points
// ==========================

void setup() {
  Serial.begin(115200);
  delay(50);
  Serial.print("--- Biomass Sensor Firmware v");
  Serial.print(FW_VERSION);
  Serial.println(" (Direct Control) ---");

  g_prefs.begin(NVS_NAMESPACE, false);

  // --- Operator settings FIRST ---
  // loadConfig() clamps the stored sampling interval against the LED
  // thermal floor, and that floor depends on g_autoRange: auto-ranging has
  // to assume the longest integration time, a manual lock does not. Reading
  // these after loadConfig() would compute the floor from the default
  // instead of the stored value, and silently raise the interval of anyone
  // running a deliberately fast manual setup.
  g_hubEnabled = g_prefs.getBool(NVS_KEY_HUB_EN, true); // default ON = v2.5 behaviour
  g_bootId     = g_prefs.getUInt(NVS_KEY_BOOTID, 0) + 1;
  g_prefs.putUInt(NVS_KEY_BOOTID, g_bootId);
  g_emaAlpha   = g_prefs.getFloat(NVS_KEY_EMA, 0.8f);
  if (g_emaAlpha <= 0.0f || g_emaAlpha > 1.0f) g_emaAlpha = 0.8f;
  g_autoRange  = g_prefs.getBool(NVS_KEY_AUTO, true);

  // Load config and blanking data from storage
  loadConfig();
  loadBlankingData();

  Serial.print("Auto-ranging: ");
  Serial.print(g_autoRange ? "ON" : "OFF (manual)");
  Serial.print(" | EMA alpha: ");
  Serial.println(g_emaAlpha, 2);
  Serial.print("Boot ID: ");
  Serial.print(g_bootId);
  Serial.print(" | Hub mode: ");
  Serial.println(g_hubEnabled ? "ENABLED" : "DISABLED (direct only)");

  // ===== PWM Setup (using analogWrite) =====
  pinMode(LED_PWM_PIN, OUTPUT);
  analogWriteFrequency(LED_PWM_PIN, LEDC_FREQ_HZ);
  analogWriteResolution(LED_PWM_PIN, LEDC_RES_BITS);

  Serial.print("PWM configured: Pin ");
  Serial.print(LED_PWM_PIN);
  Serial.print(", Freq: ");
  Serial.print(LEDC_FREQ_HZ);
  Serial.print(" Hz, Res: ");
  Serial.print(LEDC_RES_BITS);
  Serial.println(" bits");

  pwmSetDutyPercent(0.0f); // Set initial duty to 0%

  // ===== I2C Setup =====
  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
  Wire.setClock(100000);
  delay(5);

  if (!vemlSetConfig(0)) {
    Serial.println("VEML7700 init warning.");
  }
  g_currentPwmIndex = 0;

  // ===== WiFi Setup =====
  Serial.println("[NET] Setting mode to WIFI_AP_STA...");
  WiFi.mode(WIFI_AP_STA);

  Serial.printf("[NET] Configuring AP on subnet %s\n", apIP.toString().c_str());
  WiFi.softAPConfig(apIP, apGateway, apSubnet);
  if (WiFi.softAP(AP_SSID, "")) { // No password
    Serial.printf("[NET] AP Started: %s (IP: %s)\n", AP_SSID,
                  WiFi.softAPIP().toString().c_str());
  } else {
    Serial.println("[NET] AP Start Failed!");
  }

  // Configure and start Web Server
  server.on("/",             HTTP_GET,  handleRoot);
  server.on("/readData",     HTTP_GET,  handleReadData);
  server.on("/api/data",     HTTP_GET,  handleReadData);
  server.on("/api/status",   HTTP_GET,  handleStatus);
  server.on("/api/history",  HTTP_GET,  handleHistory);
  server.on("/api/blank",    HTTP_GET,  handleBlankTable);
  server.on("/api/command",  HTTP_POST, handleCommand);
  server.on("/command",      HTTP_POST, handleCommand); // v2.5 alias
  server.onNotFound(handleNotFound);
  server.begin();
  g_serverStarted = true;
  Serial.println("[NET] Web server started. UI at http://192.168.7.1/");

  g_wifiNextActionMs = 0;
  checkWifi();

  // ===== Watchdog Setup =====
  esp_task_wdt_config_t twdt_config = {
      .timeout_ms     = WDT_TIMEOUT_S * 1000,
      .idle_core_mask = 0,    // do not watch idle tasks
      .trigger_panic  = true  // panic on timeout
  };
  esp_err_t err = esp_task_wdt_init(&twdt_config);
  if (err != ESP_OK) {
    Serial.print("esp_task_wdt_init failed, code ");
    Serial.println((int)err);
  } else {
    Serial.println("Watchdog timer initialized.");
  }
  err = esp_task_wdt_add(NULL); // Add this task to WDT
  if (err != ESP_OK) {
    Serial.print("esp_task_wdt_add failed, code ");
    Serial.println((int)err);
  } else {
    g_wdtReady = true;
    Serial.println("Main task added to Watchdog.");
  }

  buildDataJson();
  Serial.println(g_lastDataJson);

  Serial.println("System IDLE.");
  Serial.println("Send JSON commands via Serial, POST /api/command, or Sensor Hub.");
  if (g_blankIsDone)
    Serial.println("e.g. {\"command\":\"start\"} or {\"start\":1}");
  else
    Serial.println("e.g. {\"command\":\"blank\"} or {\"blank\":1}");
}

void loop() {
  esp_task_wdt_reset(); // Feed the watchdog

  server.handleClient(); // Handle incoming HTTP requests on our AP

  // NEVER /*allowBlocking=*/true here. A blocking routine started from inside
  // handleSerialInput() runs with that function's inPump guard still set, so
  // every nested serviceNetwork() -> handleSerialInput() returns immediately
  // and the serial port goes dead for the whole routine. A blanking sweep
  // triggered over USB -- what the desktop app does -- answered no status for
  // ~26 s, so a client waiting to see the sweep start timed out and never
  // sent "start". HTTP never showed it, because handleCommand() already
  // deferred to the block below.
  handleSerialInput(/*allowBlocking=*/false); // Commands from serial

  // Run any blocking command a handler parked for us. Doing it here, outside
  // both handleSerialInput() and handleClient(), is what lets blank/start
  // pump BOTH transports while they run.
  if (g_pendingJson.length() > 0) {
    String pending = g_pendingJson;
    g_pendingJson = "";
    processJsonCommand(pending, /*allowBlocking=*/true);
  }

  uint32_t now = millis();

  checkWifi();

  // --- Periodic hub command poll ---
  if (g_hubEnabled && now - lastHubPollMs >= HUB_POLL_PERIOD_MS) {
    lastHubPollMs = now;
    if (WiFi.status() == WL_CONNECTED) {
      pollHubForCommands();
    }
  }

  // Non-blocking LED test sweep (only runs in IDLE state)
  if (g_ledTestEnable && g_state == IDLE && now >= g_ledTestNextMs) {
    g_ledTestNextMs = now + g_ledTestPeriodMs;
    g_ledTestPct += g_ledTestDir * 2.0f; // 2 percent per step
    if (g_ledTestPct >= 100.0f) {
      g_ledTestPct = 100.0f;
      g_ledTestDir = -1;
    }
    if (g_ledTestPct <= 0.0f) {
      g_ledTestPct = 0.0f;
      g_ledTestDir = 1;
    }
    pwmSetDutyPercent(g_ledTestPct);
  }

  switch (g_state) {
    case IDLE:
      // In IDLE the LED stays off unless the operator is deliberately
      // holding it on (manual duty) or running the test sweep.
      if (!g_ledTestEnable && !g_manualLedOn && g_targetPct > 0.0f) {
        pwmSetDutyPercent(0.0f);
      }
      break;
    case BLANKING:
    case SEARCHING:
      // Event-driven (blocking) states; they return to IDLE or MEASURING
      // when complete and service the web server while they run.
      break;

    case MEASURING:
      if (now >= g_nextReadTime) {
        runMeasurementLoop(); // This performs one pulsed read
      }
      break;
  }
}
