using System.Globalization;
using System.Text.Json;

namespace OpenTECHub.Protocol;

/// <summary>
/// Calibration coefficients and filter tuning used by <see cref="TelemetryParser"/>.
/// </summary>
/// <remarks>
/// Defaults are the values actually in the field (v.6 <c>preferences.json</c>), not
/// the stale hard-coded defaults in v.6's <c>ParserConfig</c> - those two disagreed,
/// so any fallback silently applied a different calibration. See
/// <c>docs/MIGRATION.md</c> section 3, item 2.
/// </remarks>
public sealed record ParserConfig
{
    /// <summary>Oxygen calibration slope: <c>value = a * raw + b</c>.</summary>
    public double OxygenCalibrationA { get; init; } = 0.0305473419314;

    /// <summary>Oxygen calibration intercept.</summary>
    public double OxygenCalibrationB { get; init; } = -25.09136520919;

    /// <summary>pH calibration slope: <c>pH = slope * raw + intercept</c>.</summary>
    public double PHSlope { get; init; } = 0.0005012405704;

    /// <summary>pH calibration intercept.</summary>
    public double PHIntercept { get; init; } = -0.600385955239;

    /// <summary>Reactor probe correction in °C: <c>real = read + offset</c> (D-073).</summary>
    public double TemperatureOffsetC { get; init; }

    public SpikeFilterConfig PHFilter { get; init; } = SpikeFilterConfig.ForPH();
    public SpikeFilterConfig OxygenFilter { get; init; } = SpikeFilterConfig.ForOxygen();

    /// <summary>
    /// If no Distance key arrives for this long, Distance is forced back to the
    /// not-received sentinel.
    /// </summary>
    public TimeSpan DistanceTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Local presence window for the biomass node, used only when the Hub does not
    /// publish an explicit presence flag.
    /// </summary>
    /// <remarks>
    /// Longer than the Hub's own 10 s biomass window so the two do not race: if the Hub
    /// is going to declare the node absent, it does so first and the app follows.
    /// </remarks>
    public TimeSpan BiomassTimeout { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>Local presence window for the external pump. It pushes once a second.</summary>
    public TimeSpan PumpTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Local presence window for the flask agitator, once it pushes at all.</summary>
    public TimeSpan AgitatorTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Local presence window for the ASDA-B2 servo node. It pushes once a second.</summary>
    /// <remarks>
    /// The Hub's own window is 6 s and is authoritative whenever it publishes
    /// <c>ServoOnline</c>; this is the fallback for a Hub that predates the key. Two
    /// seconds of slack is one aggregate frame at the default <c>dataDelay</c>, which is
    /// the resolution anything downstream can actually observe - a tighter window would
    /// race the Hub and declare the node absent first.
    /// </remarks>
    public TimeSpan ServoTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Hub bath presence window is 5 s; keep a small margin for aggregate polling.</summary>
    public TimeSpan BathTimeout { get; init; } = TimeSpan.FromSeconds(7);
}

/// <summary>Outcome of parsing one line read from the device.</summary>
public enum ParseOutcome
{
    /// <summary>Telemetry decoded and <see cref="SensorReadings"/> updated.</summary>
    Updated,

    /// <summary>
    /// A device log line (prefixed <c>[ESP32_</c>). Not telemetry, not an error -
    /// must not count toward the parse-failure streak.
    /// </summary>
    DeviceLog,

    /// <summary>
    /// The bare <c>OK</c> the device returns after accepting a command.
    /// </summary>
    /// <remarks>
    /// On USB this shares the serial stream with telemetry, so it surfaces here
    /// rather than at the transport. Confirmed on hardware 2026-08-19: it is normal
    /// traffic, and must not count as a parse failure. v.6 does count it, and only
    /// survives because it needs three <i>consecutive</i> failures and telemetry
    /// usually interleaves - sending several commands in quick succession can trip a
    /// false link-loss there.
    /// </remarks>
    CommandAck,

    /// <summary>A cached external-node diagnostic envelope. Not telemetry and not a parse failure.</summary>
    NodeDiag,

    /// <summary>The line was not valid JSON.</summary>
    Malformed,

    /// <summary>The line was empty or whitespace.</summary>
    Empty,
}

/// <summary>
/// Turns a raw JSON line from the device into <see cref="SensorReadings"/>, applying
/// spike filtering and linear calibration.
/// </summary>
/// <remarks>
/// <para>
/// Port of <c>DataParser</c> in v.6 <c>communication/data_parser.py</c>. The
/// per-key semantics are load-bearing and are specified in
/// <c>docs/PROTOCOL.md</c> section 2: a missing key means "no update", never zero;
/// some keys are sticky and some are not; several have validity windows.
/// </para>
/// <para>
/// This type does <b>not</b> send anything. When the calibrated pH changes it raises
/// <see cref="PHCalibrationChanged"/> and the connection manager decides what to do.
/// </para>
/// </remarks>
public sealed class TelemetryParser
{
    private const string DeviceLogPrefix = "[ESP32_";
    private const string CommandAcknowledgement = "OK";

    private readonly TimeProvider _time;

    private ParserConfig _config;
    private SpikeFilter _phFilter;
    private SpikeFilter _oxygenFilter;

    private double? _phLastValidCalibrated;
    private double? _phLastSentCalibrated;

    /// <param name="config">Calibration and filter tuning.</param>
    /// <param name="timeProvider">
    /// Clock used to age out the Distance channel. Injectable so the timeout is
    /// testable without sleeping; defaults to the system clock.
    /// </param>
    public TelemetryParser(ParserConfig? config = null, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _config = config ?? new ParserConfig();
        _phFilter = new SpikeFilter(_config.PHFilter);
        _oxygenFilter = new SpikeFilter(_config.OxygenFilter);
    }

    /// <summary>The live readings object, mutated in place each frame.</summary>
    public SensorReadings Readings { get; } = new();

    /// <summary>
    /// Raised when a newly calibrated pH differs from the last value sent back to the
    /// device. The subscriber is expected to buffer a <c>pHCal</c> command.
    /// See <c>docs/PROTOCOL.md</c> section 2.2.
    /// </summary>
    public event Action<double>? PHCalibrationChanged;

    /// <summary>The reactor probe correction in force, or zero when the configured one is unusable.</summary>
    private double Offset => TemperatureCorrection.IsValidOffset(_config.TemperatureOffsetC) ? _config.TemperatureOffsetC : 0;

    /// <summary>The calibration and filter tuning in force.</summary>
    public ParserConfig Config => _config;

    /// <summary>Applies new calibration and filter tuning.</summary>
    public void Reconfigure(ParserConfig config)
    {
        _config = config;
        _phFilter = new SpikeFilter(config.PHFilter);
        _oxygenFilter = new SpikeFilter(config.OxygenFilter);
    }

    /// <summary>
    /// Records that the <c>pHCal</c> command carrying <paramref name="value"/> was
    /// actually flushed, so it is not retransmitted every frame.
    /// </summary>
    public void MarkPHSent(double value) => _phLastSentCalibrated = value;

    /// <summary>Parses one line and updates <see cref="Readings"/> in place.</summary>
    public ParseOutcome Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return ParseOutcome.Empty;
        }

        var text = line.Trim();

        // The ESP32 interleaves human-readable log lines with telemetry. They are
        // expected traffic, not corruption - counting them as parse failures would
        // tear down a healthy link.
        if (text.StartsWith(DeviceLogPrefix, StringComparison.Ordinal))
        {
            return ParseOutcome.DeviceLog;
        }

        // Command acknowledgement, echoed onto the same stream as telemetry on USB.
        if (string.Equals(text, CommandAcknowledgement, StringComparison.Ordinal))
        {
            return ParseOutcome.CommandAck;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ParseOutcome.Malformed;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return ParseOutcome.Malformed;
        }

        if (root.TryGetProperty("NodeDiag", out _))
        {
            return ParseOutcome.NodeDiag;
        }

        // One clock read per frame: every presence window must age against the same
        // instant, or two devices in the same frame disagree about what "now" is.
        var now = _time.GetUtcNow();

        Readings.TemperatureUpdated = false;
        Readings.OxygenUpdated = false;
        Readings.PHUpdated = false;
        Readings.OxygenFrameReceived = root.TryGetProperty(TelemetryKeys.OxygenRaw, out _);
        Readings.PHFrameReceived = root.TryGetProperty(TelemetryKeys.PHRaw, out _);
        ParseTemperature(root);
        ParseOxygen(root);
        ParsePH(root);
        ParseFlowAndMisc(root, now);
        ParseBiomass(root, now);
        ParsePump(root, now);
        ParseAgitator(root, now);
        ParseHubIdentity(root);
        ParseNodeIdentity(root);
        ParseServo(root, now);
        ParseBath(root, now);
        ParseTime(root);

        return ParseOutcome.Updated;
    }

    // ------------------------------------------------------------------
    // Channel parsers
    // ------------------------------------------------------------------

    private void ParseTemperature(JsonElement root)
    {
        // Outside 10..100 degC the reading is not physically plausible for this
        // equipment; hold the previous value rather than showing a glitch.
        if (TryGetDouble(root, TelemetryKeys.Temperature, out var value) &&
            value is > 10.0 and < 100.0)
        {
            Readings.TemperatureRaw = value;
            Readings.Temperature = TemperatureCorrection.ToReal(value, Offset);
            Readings.TemperatureUpdated = true;
        }
    }

    private void ParseOxygen(JsonElement root)
    {
        if (!TryGetDouble(root, TelemetryKeys.OxygenRaw, out var raw) || !double.IsFinite(raw) || raw <= 0.1)
        {
            return; // sentinel: probe absent or not yet valid
        }

        if (_oxygenFilter.Update(raw) is not { } accepted)
        {
            return;
        }

        Readings.OxygenUpdated = accepted == raw;
        Readings.OxygenRaw = accepted;
        var calibrated = (_config.OxygenCalibrationA * accepted) + _config.OxygenCalibrationB;
        Readings.OxygenCalibrated = Math.Round(Math.Max(calibrated, 0.0), 4);
    }

    private void ParsePH(JsonElement root)
    {
        if (!TryGetDouble(root, TelemetryKeys.PHRaw, out var raw) || !double.IsFinite(raw) || raw <= 0.1)
        {
            return;
        }

        if (_phFilter.Update(raw) is not { } acceptedRaw)
        {
            return;
        }

        Readings.PHUpdated = acceptedRaw == raw;
        Readings.PHRaw = acceptedRaw;

        var calibrated = Math.Round((_config.PHSlope * acceptedRaw) + _config.PHIntercept, 2);
        if (calibrated < 0.0)
        {
            calibrated = 0.0;
        }

        if (calibrated is >= 0.0 and < 25_000.0)
        {
            Readings.PHCalibrated = calibrated;
            _phLastValidCalibrated = calibrated;
        }

        // Only ask for a retransmission when the value actually moved.
        if (_phLastValidCalibrated is { } latest && latest != _phLastSentCalibrated)
        {
            PHCalibrationChanged?.Invoke(latest);
        }
    }

    private void ParseFlowAndMisc(JsonElement root, DateTimeOffset now)
    {
        Readings.FlowRateUpdated = false;
        Readings.FlowFeedbackUpdated = false;
        Readings.PressureUpdated = false;
        Readings.SensorCommUpdated = false;
        Readings.PHSetpoint = TryGetFiniteDouble(root, TelemetryKeys.PHSetpoint, out var phReference) ? phReference : null;
        Readings.PHError = TryGetFiniteDouble(root, TelemetryKeys.PHError, out var phError) ? phError : null;
        Readings.PHControlActive = TryGetBool(root, TelemetryKeys.PHControlActive, out var phActive) ? phActive : null;
        Readings.PHCommandPending = TryGetBool(root, TelemetryKeys.PHCommandPending, out var phPending) ? phPending : null;
        Readings.PressureReference = TryGetFiniteDouble(root, TelemetryKeys.PressureReference, out var pressureReference) ? pressureReference : null;
        Readings.PressureControlActive = TryGetBool(root, TelemetryKeys.PressureControlActive, out var pressureActive) ? pressureActive : null;
        Readings.PressureCommandPending = TryGetBool(root, TelemetryKeys.PressureCommandPending, out var pressurePending) ? pressurePending : null;
        if (TryGetDouble(root, TelemetryKeys.Pressure, out var pressure))
        {
            Readings.Pressure = pressure;
            Readings.PressureUpdated = double.IsFinite(pressure) && pressure >= 0;
        }

        if (TryGetDouble(root, TelemetryKeys.FlowRate, out var flowRate))
        {
            Readings.FlowRate = flowRate;
            Readings.FlowRateUpdated = double.IsFinite(flowRate) && flowRate >= 0;
        }

        if (TryGetDouble(root, TelemetryKeys.FlowSetpoint, out var flowSetpoint))
        {
            Readings.FlowSetpoint = flowSetpoint;
        }

        if (TryGetDouble(root, TelemetryKeys.Antifoam, out var antifoam))
        {
            Readings.Antifoam = antifoam;
        }

        if (TryGetDouble(root, TelemetryKeys.FlowVoltage, out var flowVoltage))
        {
            Readings.FlowVoltage = flowVoltage;
        }

        // Sticky booleans: absence means "unchanged".
        if (TryGetBool(root, TelemetryKeys.SensorCommOk, out var sensorOk))
        {
            Readings.SensorCommOk = sensorOk;
            Readings.SensorCommUpdated = true;
        }

        if (TryGetBool(root, TelemetryKeys.FlowmeterOnline, out var flowmeterOnline))
        {
            Readings.FlowmeterOnline = flowmeterOnline;

            // Hub v7 deliberately omits flow values after its internal v05 timeout.
            // Do not keep publishing the last good sample as fresh telemetry while
            // the flowmeter is offline.
            if (!flowmeterOnline)
            {
                Readings.FlowRateUpdated = false;
                Readings.FlowRate = SensorReadings.NotReceived;
                Readings.FlowSetpoint = SensorReadings.NotReceived;
                Readings.FlowVoltage = SensorReadings.NotReceived;
                Readings.FlowValve1 = -1;
                Readings.FlowValve2 = -1;
                Readings.FlowValveMain = -1;
            }
        }

        if (TryGetBool(root, TelemetryKeys.FlowControlEnabled, out var flowEnabled))
        {
            Readings.FlowControlEnabled = flowEnabled;
        }

        // Hub v10 only emits this while the flowmeter is online, and a v05 node never
        // reports it at all. Absence therefore means "unknown", not "switched off", so
        // the last known value stands rather than defaulting to a false alarm.
        if (TryGetBool(root, TelemetryKeys.FlowmeterReconnectWifi, out var flowReconnect))
        {
            Readings.FlowmeterReconnectWifi = flowReconnect;
        }

        // NOT sticky: a frame without this key means no command is pending.
        Readings.FlowCommandPending =
            TryGetBool(root, TelemetryKeys.FlowCommandPending, out var pending) && pending;

        if (root.TryGetProperty(TelemetryKeys.FlowCommandSource, out var source) &&
            source.ValueKind == JsonValueKind.String)
        {
            Readings.FlowCommandSource = source.GetString() ?? Readings.FlowCommandSource;
        }

        AssignInt(root, TelemetryKeys.Valve1, v => Readings.FlowValve1 = v);
        AssignInt(root, TelemetryKeys.Valve2, v => Readings.FlowValve2 = v);
        AssignInt(root, TelemetryKeys.ValveFlow, v => Readings.FlowValveMain = v);
        AssignLong(root, TelemetryKeys.FlowCommandId, v => Readings.FlowCommandId = v);
        AssignLong(root, TelemetryKeys.FlowCommandAck, v => Readings.FlowCommandAck = v);
        AssignInt(root, TelemetryKeys.FlowCommandDeliveries, v => Readings.FlowCommandDeliveries = v);
        AssignInt(root, TelemetryKeys.FlowCommandAgeMs, v => Readings.FlowCommandAgeMs = v);
        AssignInt(root, TelemetryKeys.HubStations, v => Readings.HubStations = v);

        // External-node echoes (Hub 10.2) - strictly non-sticky
        Readings.FlowKp =
            TryGetDouble(root, TelemetryKeys.FlowKp, out var kp) ? kp : null;
        Readings.FlowKi =
            TryGetDouble(root, TelemetryKeys.FlowKi, out var ki) ? ki : null;
        Readings.FlowFfGain =
            TryGetDouble(root, TelemetryKeys.FlowFfGain, out var ffGain) ? ffGain : null;
        Readings.FlowFfOffset =
            TryGetDouble(root, TelemetryKeys.FlowFfOffset, out var ffOffset) ? ffOffset : null;
        Readings.FlowRampRate =
            TryGetDouble(root, TelemetryKeys.FlowRampRate, out var rampRate) ? rampRate : null;
        Readings.FlowOutput =
            TryGetDouble(root, TelemetryKeys.FlowOutput, out var flowOutput) ? flowOutput : null;
        Readings.FlowSetpointCorrected =
            TryGetDouble(root, TelemetryKeys.FlowSetpointCorrected, out var spCorr) ? spCorr : null;
        Readings.FlowTransitionVoltage =
            TryGetDouble(root, TelemetryKeys.FlowTransitionVoltage, out var transVolt) ? transVolt : null;
        Readings.FlowmeterCalCrc =
            TryGetCounter(root, TelemetryKeys.FlowmeterCalCrc, out var flowCalCrc) ? flowCalCrc : null;

        // Sticky flowmeter boot_id (long?)
        if (TryGetCounter(root, TelemetryKeys.FlowmeterBootId, out var bootId))
        {
            Readings.FlowmeterBootId = bootId;
        }

        // Confirmation must not combine current measurements with retained command/valve echoes.
        Readings.FlowFeedbackUpdated = Readings.FlowRateUpdated &&
            TryGetBool(root, TelemetryKeys.FlowmeterOnline, out var currentOnline) && currentOnline &&
            TryGetDouble(root, TelemetryKeys.FlowSetpoint, out var currentSetpoint) && double.IsFinite(currentSetpoint) && currentSetpoint >= 0 &&
            TryGetBool(root, TelemetryKeys.FlowCommandPending, out _) &&
            TryGetCounter(root, TelemetryKeys.FlowCommandId, out _) && TryGetCounter(root, TelemetryKeys.FlowCommandAck, out _) &&
            TryGetInt(root, TelemetryKeys.Valve1, out var currentValve1) && currentValve1 is 0 or 1 &&
            TryGetInt(root, TelemetryKeys.Valve2, out var currentValve2) && currentValve2 is 0 or 1;
        ParseDistance(root, now);
    }

    private void ParseDistance(JsonElement root, DateTimeOffset now)
    {
        var sawValue = false;

        if (TryGetPropertyCaseInsensitive(root, TelemetryKeys.Distance, out _))
        {
            if (TryGetDouble(root, TelemetryKeys.Distance, out var distance) &&
                distance is >= 0.0 and < 1000.0)
            {
                Readings.Distance = distance;
                Readings.DistanceLastSeenAt = now;
                sawValue = true;
            }
        }
        else if (Readings.DistanceLastSeenAt is { } lastSeen &&
                 now - lastSeen >= _config.DistanceTimeout)
        {
            // The ultrasonic sensor simply stops emitting when unplugged, so silence has to
            // be aged out explicitly or a stale height would sit on screen forever.
            Readings.Distance = SensorReadings.NotReceived;
        }

        var presence = ResolvePresence(
            root,
            TelemetryKeys.DistanceOnline,
            sawValue,
            _config.DistanceTimeout,
            new Presence(Readings.HasDistanceTelemetry, Readings.DistanceOnline, Readings.DistanceLastSeenAt),
            now);

        Readings.HasDistanceTelemetry = presence.HasTelemetry;
        Readings.DistanceOnline = presence.Online;
        Readings.DistanceLastSeenAt = presence.LastSeenAt;

        if (presence.HasTelemetry && !presence.Online)
        {
            Readings.Distance = SensorReadings.NotReceived;
        }

        if (TryGetBool(root, TelemetryKeys.DistanceCommEnabled, out var commEnabled))
        {
            Readings.DistanceCommEnabled = commEnabled;
        }

        // External-node echoes (Hub 10.2) - strictly non-sticky
        Readings.DistanceOffsetMm =
            TryGetDouble(root, TelemetryKeys.DistanceOffsetMm, out var offsetMm) ? offsetMm : null;
        Readings.DistanceSamplePeriodMs =
            TryGetInt(root, TelemetryKeys.DistanceSamplePeriodMs, out var sampleMs) ? sampleMs : null;
        Readings.DistanceSendPeriodMs =
            TryGetInt(root, TelemetryKeys.DistanceSendPeriodMs, out var sendMs) ? sendMs : null;
        Readings.DistanceCommandPending =
            TryGetBool(root, TelemetryKeys.DistanceCommandPending, out var distPending) ? distPending : null;
    }

    private void ParseBiomass(JsonElement root, DateTimeOffset now)
    {
        var sawValues = TryGetPropertyCaseInsensitive(root, TelemetryKeys.BiomassAbs, out _) ||
                        TryGetPropertyCaseInsensitive(root, TelemetryKeys.BiomassRaw, out _);

        var presence = ResolvePresence(
            root,
            TelemetryKeys.BiomassOnline,
            sawValues,
            _config.BiomassTimeout,
            new Presence(Readings.HasBiomassTelemetry, Readings.BiomassOnline, Readings.BiomassLastSeenAt),
            now);

        Readings.HasBiomassTelemetry = presence.HasTelemetry;
        Readings.BiomassOnline = presence.Online;
        Readings.BiomassLastSeenAt = presence.LastSeenAt;

        if (TryGetBool(root, TelemetryKeys.BiomassCommEnabled, out var commEnabled))
        {
            Readings.BiomassCommEnabled = commEnabled;
        }

        // Not sticky, exactly like FlowCommandPending: a frame that does not mention a
        // pending command is saying there is none.
        // Null, not false, when the key is absent: a Hub with no acknowledgement channel
        // for this device is silent about it, and silence is not a confirmation.
        Readings.BiomassCommandPending =
            TryGetBool(root, TelemetryKeys.BiomassCommandPending, out var pending) ? pending : null;

        // External-node echoes (Hub 10.2) - strictly non-sticky
        Readings.BiomassGear =
            TryGetInt(root, TelemetryKeys.BiomassGear, out var gear) ? gear : null;
        Readings.BiomassEma =
            TryGetDouble(root, TelemetryKeys.BiomassEma, out var ema) ? ema : null;
        Readings.BiomassProbePeriodMs =
            TryGetInt(root, TelemetryKeys.BiomassProbePeriodMs, out var probeMs) ? probeMs : null;

        if (presence.HasTelemetry && !presence.Online)
        {
            // The Hub stops publishing the four biomass channels once the node's window
            // lapses. Holding the last good sample would leave a ten-minute-old absorbance
            // on screen looking live - the defect this whole change exists to close.
            ClearBiomassReadings();
            return;
        }

        // Online with no sample block. The Hub tracks presence and sample freshness on
        // separate clocks: the node heartbeats while idle, so it stays online after the
        // operator stops acquisition, but the Hub drops the sample block as soon as the
        // reading goes stale. On a Hub that publishes presence, that absence is a
        // statement - "there, not measuring" - and the last absorbance must go with it.
        if (TryGetPropertyCaseInsensitive(root, TelemetryKeys.BiomassOnline, out _) && !sawValues)
        {
            ClearBiomassReadings();
            return;
        }

        if (TryGetDouble(root, TelemetryKeys.BiomassAbs, out var absorbance))
        {
            Readings.BiomassAbsorbance = absorbance;
        }

        AssignInt(root, TelemetryKeys.BiomassRaw, v => Readings.BiomassRaw = v);
        AssignInt(root, TelemetryKeys.BiomassIntegrationTime, v => Readings.BiomassIntegrationTimeMs = v);

        if (TryGetDouble(root, TelemetryKeys.BiomassPwm, out var pwm))
        {
            Readings.BiomassPwmPercent = pwm;
        }
    }

    private void ParsePump(JsonElement root, DateTimeOffset now)
    {
        var sawValues = TryGetPropertyCaseInsensitive(root, TelemetryKeys.PumpFlow, out _) ||
                        TryGetPropertyCaseInsensitive(root, TelemetryKeys.PumpVolume, out _);

        var presence = ResolvePresence(
            root,
            TelemetryKeys.PumpOnline,
            sawValues,
            _config.PumpTimeout,
            new Presence(Readings.HasPumpTelemetry, Readings.PumpOnline, Readings.PumpLastSeenAt),
            now);

        Readings.HasPumpTelemetry = presence.HasTelemetry;
        Readings.PumpOnline = presence.Online;
        Readings.PumpLastSeenAt = presence.LastSeenAt;

        if (TryGetBool(root, TelemetryKeys.PumpCommEnabled, out var commEnabled))
        {
            Readings.PumpCommEnabled = commEnabled;
        }

        // Null, not false, when the key is absent: a Hub with no acknowledgement channel
        // for this device is silent about it, and silence is not a confirmation.
        Readings.PumpCommandPending =
            TryGetBool(root, TelemetryKeys.PumpCommandPending, out var pending) ? pending : null;

        // External-node echoes (Hub 10.2) - strictly non-sticky
        Readings.PumpPidKp =
            TryGetDouble(root, TelemetryKeys.PumpPidKp, out var kp) ? kp : null;
        Readings.PumpPidKi =
            TryGetDouble(root, TelemetryKeys.PumpPidKi, out var ki) ? ki : null;
        Readings.PumpPidKd =
            TryGetDouble(root, TelemetryKeys.PumpPidKd, out var kd) ? kd : null;
        Readings.PumpPotEnabled =
            TryGetBool(root, TelemetryKeys.PumpPotEnabled, out var pot) ? pot : null;
        Readings.PumpCycleVolume =
            TryGetDouble(root, TelemetryKeys.PumpCycleVolume, out var cycleVolume) ? cycleVolume : null;

        Readings.PumpTransitionSpeed =
            TryGetDouble(root, TelemetryKeys.PumpTransitionSpeed, out var transSpeed) ? transSpeed : null;
        Readings.PumpA1 = TryGetDouble(root, TelemetryKeys.PumpA1, out var pumpA1) ? pumpA1 : null;
        Readings.PumpB1 = TryGetDouble(root, TelemetryKeys.PumpB1, out var pumpB1) ? pumpB1 : null;
        Readings.PumpK1 = TryGetDouble(root, TelemetryKeys.PumpK1, out var pumpK1) ? pumpK1 : null;
        Readings.PumpF1 = TryGetDouble(root, TelemetryKeys.PumpF1, out var pumpF1) ? pumpF1 : null;
        Readings.PumpC1 = TryGetDouble(root, TelemetryKeys.PumpC1, out var pumpC1) ? pumpC1 : null;
        Readings.PumpK2 = TryGetDouble(root, TelemetryKeys.PumpK2, out var pumpK2) ? pumpK2 : null;
        Readings.PumpF2 = TryGetDouble(root, TelemetryKeys.PumpF2, out var pumpF2) ? pumpF2 : null;
        Readings.PumpC2 = TryGetDouble(root, TelemetryKeys.PumpC2, out var pumpC2) ? pumpC2 : null;
        Readings.PumpCalCrc =
            TryGetCounter(root, TelemetryKeys.PumpCalCrc, out var calCrc) ? calCrc : null;

        if (presence.HasTelemetry && !presence.Online)
        {
            // A Hub without the pump presence window republishes the last sample forever.
            // Invalidating here is what stops the card - and the proportional-gas coupling
            // that reads PumpVolume - from acting on a dead node's numbers.
            Readings.PumpFlow = SensorReadings.NotReceived;
            Readings.PumpVolume = SensorReadings.NotReceived;
            Readings.PumpPwm = SensorReadings.NotReceived;
            Readings.PumpSpeed = SensorReadings.NotReceived;
            Readings.PumpTargetVolume = SensorReadings.NotReceived;
            Readings.PumpMode = -1;
            Readings.PumpActive = false;
            Readings.PumpWaiting = false;
            return;
        }

        if (TryGetDouble(root, TelemetryKeys.PumpFlow, out var flow))
        {
            Readings.PumpFlow = flow;
        }

        if (TryGetDouble(root, TelemetryKeys.PumpVolume, out var volume))
        {
            Readings.PumpVolume = volume;
        }

        AssignInt(root, TelemetryKeys.PumpMode, v => Readings.PumpMode = v);

        if (TryGetDouble(root, TelemetryKeys.PumpPwm, out var pwm))
        {
            Readings.PumpPwm = pwm;
        }

        if (TryGetDouble(root, TelemetryKeys.PumpSpeed, out var speed))
        {
            Readings.PumpSpeed = speed;
        }

        if (TryGetDouble(root, TelemetryKeys.PumpTargetVolume, out var targetVolume))
        {
            Readings.PumpTargetVolume = targetVolume;
        }

        // Sticky: the node's profile state only changes when it says so.
        if (TryGetBool(root, TelemetryKeys.PumpActive, out var active))
        {
            Readings.PumpActive = active;
        }

        if (TryGetBool(root, TelemetryKeys.PumpWaiting, out var waiting))
        {
            Readings.PumpWaiting = waiting;
        }
    }

    private void ClearBiomassReadings()
    {
        Readings.BiomassAbsorbance = SensorReadings.NotReceived;
        Readings.BiomassRaw = 0;
        Readings.BiomassIntegrationTimeMs = 0;
        Readings.BiomassPwmPercent = 0;
    }

    /// <summary>
    /// The flask agitator, which reports nothing at all through a Hub that predates its
    /// push handler.
    /// </summary>
    /// <remarks>
    /// That case leaves <c>HasAgitatorTelemetry</c> false, and the UI must read that as
    /// "no evidence" rather than "offline". The agitator is the one device where an
    /// unflashed Hub cannot be worked around locally: there are no value keys to age.
    /// </remarks>
    private void ParseAgitator(JsonElement root, DateTimeOffset now)
    {
        var sawValues = TryGetPropertyCaseInsensitive(root, TelemetryKeys.AgitatorPercent, out _);

        var presence = ResolvePresence(
            root,
            TelemetryKeys.AgitatorOnline,
            sawValues,
            _config.AgitatorTimeout,
            new Presence(Readings.HasAgitatorTelemetry, Readings.AgitatorOnline, Readings.AgitatorLastSeenAt),
            now);

        Readings.HasAgitatorTelemetry = presence.HasTelemetry;
        Readings.AgitatorOnline = presence.Online;
        Readings.AgitatorLastSeenAt = presence.LastSeenAt;

        // Null, not false, when the key is absent: a Hub with no acknowledgement channel
        // for this device is silent about it, and silence is not a confirmation.
        Readings.AgitatorCommandPending =
            TryGetBool(root, TelemetryKeys.AgitatorCommandPending, out var pending) ? pending : null;

        if (presence.HasTelemetry && !presence.Online)
        {
            Readings.AgitatorPercent = SensorReadings.NotReceived;
            Readings.AgitatorDirection = -1;
            Readings.AgitatorPotActive = false;
            Readings.AgitatorSource = "unknown";
            return;
        }

        if (TryGetDouble(root, TelemetryKeys.AgitatorPercent, out var percent))
        {
            Readings.AgitatorPercent = percent;
        }

        AssignInt(root, TelemetryKeys.AgitatorDirection, v => Readings.AgitatorDirection = v);

        if (TryGetBool(root, TelemetryKeys.AgitatorPotActive, out var potActive))
        {
            Readings.AgitatorPotActive = potActive;
        }

        if (TryGetPropertyCaseInsensitive(root, TelemetryKeys.AgitatorSource, out var source) &&
            source.ValueKind == JsonValueKind.String)
        {
            Readings.AgitatorSource = source.GetString() ?? Readings.AgitatorSource;
        }
    }

    /// <summary>
    /// The ASDA-B2 servo node: presence, routing, queue state and the ten measurements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Presence and routing are <b>orthogonal</b> here, and this method exists mainly to
    /// keep them apart. All four combinations are legitimate and only one is a failure:
    /// a node that is absent while routing is on. Routing off with the node present is a
    /// deliberate configuration; both off is a module that simply has no servo, which is
    /// the bench module's permanent and correct state.
    /// </para>
    /// <para>
    /// So the four always-published keys are read <i>before</i> any early return. They
    /// are the whole point: without <c>ServoCommEnabled</c>, "this module has no servo"
    /// and "the servo went missing" look identical, and the second is an alarm.
    /// </para>
    /// </remarks>
    /// <summary>Who is on the other end, for diagnostics and for the session header.</summary>
    /// <remarks>
    /// Sticky: a Hub that published its identity once has not stopped being that Hub, and
    /// the aggregate frame carries these on every frame anyway. Absence means a Hub built
    /// before the keys existed, which leaves the version null and -1 rather than guessing.
    /// </remarks>
    private void ParseHubIdentity(JsonElement root)
    {
        if (TryGetPropertyCaseInsensitive(root, TelemetryKeys.HubFirmwareVersion, out var firmware) &&
            firmware.ValueKind == JsonValueKind.String)
        {
            Readings.HubFirmwareVersion = firmware.GetString();
        }

        AssignInt(root, TelemetryKeys.HubProtocolVersion, v => Readings.HubProtocolVersion = v);
    }

    /// <summary>Who each external node is, as the Hub registered it (10.1).</summary>
    /// <remarks>
    /// Sticky per member, like the Hub identity: a key that is absent from one frame keeps
    /// the last value, because the Hub only emits <c>*NodeVer</c>/<c>*NodeMac</c> once the
    /// node has registered and an older Hub never emits them at all. The one value that
    /// does clear is an IP of <c>0.0.0.0</c>, which the Hub sends on every frame and which
    /// means "never seen" - the Hub rebooted and forgot, and so must the app.
    /// </remarks>
    private void ParseNodeIdentity(JsonElement root)
    {
        Readings.DistanceNode = MergeNode(root, Readings.DistanceNode,
            TelemetryKeys.DistanceIP, TelemetryKeys.DistanceNodeVer, TelemetryKeys.DistanceNodeMac);
        Readings.AgitatorNode = MergeNode(root, Readings.AgitatorNode,
            TelemetryKeys.AgitatorIP, TelemetryKeys.AgitatorNodeVer, TelemetryKeys.AgitatorNodeMac);
        Readings.PumpNode = MergeNode(root, Readings.PumpNode,
            TelemetryKeys.PumpIP, TelemetryKeys.PumpNodeVer, TelemetryKeys.PumpNodeMac);
        Readings.FlowmeterNode = MergeNode(root, Readings.FlowmeterNode,
            TelemetryKeys.FlowmeterIP, TelemetryKeys.FlowmeterNodeVer, TelemetryKeys.FlowmeterNodeMac);
        Readings.BiomassNode = MergeNode(root, Readings.BiomassNode,
            TelemetryKeys.BiomassIP, TelemetryKeys.BiomassNodeVer, TelemetryKeys.BiomassNodeMac);
        Readings.BathNode = MergeNode(root, Readings.BathNode,
            TelemetryKeys.BathIP, TelemetryKeys.BathNodeVer, TelemetryKeys.BathNodeMac);
    }

    private static ExternalNodeIdentity MergeNode(
        JsonElement root, ExternalNodeIdentity current, string ipKey, string verKey, string macKey)
    {
        var ip = current.Ip;
        if (TryGetPropertyCaseInsensitive(root, ipKey, out var ipEl) && ipEl.ValueKind == JsonValueKind.String)
        {
            // Present on every 10.x frame; "0.0.0.0" is an explicit "never seen" and clears.
            ip = ExternalNodeIdentity.Normalize(ipEl.GetString());
        }

        var ver = current.FirmwareVersion;
        if (TryGetPropertyCaseInsensitive(root, verKey, out var verEl) && verEl.ValueKind == JsonValueKind.String)
        {
            ver = ExternalNodeIdentity.Normalize(verEl.GetString()) ?? ver;
        }

        var mac = current.Mac;
        if (TryGetPropertyCaseInsensitive(root, macKey, out var macEl) && macEl.ValueKind == JsonValueKind.String)
        {
            mac = ExternalNodeIdentity.Normalize(macEl.GetString()) ?? mac;
        }

        return ip == current.Ip && ver == current.FirmwareVersion && mac == current.Mac
            ? current
            : new ExternalNodeIdentity(ip, mac, ver);
    }

    private void ParseBath(JsonElement root, DateTimeOffset now)
    {
        var sawValues = TryGetPropertyCaseInsensitive(root, TelemetryKeys.BathSp, out _) ||
                        TryGetPropertyCaseInsensitive(root, TelemetryKeys.BathPv, out _) ||
                        TryGetPropertyCaseInsensitive(root, TelemetryKeys.BathState, out _);
        var presence = ResolvePresence(
            root, TelemetryKeys.BathOnline, sawValues, _config.BathTimeout,
            new Presence(Readings.HasBathTelemetry, Readings.BathOnline, Readings.BathLastSeenAt), now);

        Readings.HasBathTelemetry = presence.HasTelemetry;
        Readings.BathOnline = presence.Online;
        Readings.BathLastSeenAt = presence.LastSeenAt;
        Readings.BathCommEnabled = TryGetBool(root, TelemetryKeys.BathCommEnabled, out var comm) ? comm : null;
        Readings.BathCommandPending = TryGetBool(root, TelemetryKeys.BathCommandPending, out var pending) ? pending : null;
        AssignLong(root, TelemetryKeys.BathCommandId, v => Readings.BathCommandId = v);
        AssignLong(root, TelemetryKeys.BathCommandAck, v => Readings.BathCommandAck = v);
        AssignInt(root, TelemetryKeys.TempControlMode, v => Readings.BathTempControlMode = v);
        Readings.TempControlViaBath = TryGetBool(root, TelemetryKeys.TempControlViaBath, out var via) ? via : null;
        Readings.BathCascadeEnabled = TryGetBool(root, TelemetryKeys.BathCascadeEnabled, out var enabled) ? enabled : null;
        Readings.BathCommandLatestWins = TryGetBool(root, TelemetryKeys.BathCommandLatestWins, out var latest) && latest;
        Readings.BathCommandCompletionPending = TryGetBool(root, TelemetryKeys.BathCommandCompletionPending, out var completion) && completion;
        AssignLong(root, TelemetryKeys.BathCommandLastSentId, v => Readings.BathCommandLastSentId = v);
        AssignLong(root, TelemetryKeys.BathCommandLastDoneId, v => Readings.BathCommandLastDoneId = v);
        AssignInt(root, TelemetryKeys.BathCommandCompletionAgeMs, v => Readings.BathCommandCompletionAgeMs = v);

        // The Hub echoes the reference in the module's scale; 0 is "off" and is not shifted.
        Readings.TempSetpoint = ReadNullableDouble(root, TelemetryKeys.TempSetpoint) is { } reference && reference > 0
            ? TemperatureCorrection.ToReal(reference, Offset)
            : ReadNullableDouble(root, TelemetryKeys.TempSetpoint);
        Readings.BathSp = ReadNullableDouble(root, TelemetryKeys.BathSp);
        Readings.BathTarget = ReadNullableDouble(root, TelemetryKeys.BathTarget);
        Readings.BathPv = ReadNullableDouble(root, TelemetryKeys.BathPv);
        Readings.BathDisplaySp = ReadNullableDouble(root, TelemetryKeys.BathDisplaySp);
        Readings.BathDeviation = ReadNullableDouble(root, TelemetryKeys.BathDeviation);
        Readings.BathCommandSetpoint = ReadNullableDouble(root, TelemetryKeys.BathCommandSetpoint);
        Readings.BathCommandConfirmed = ReadNullableDouble(root, TelemetryKeys.BathCommandConfirmed);
        Readings.BathCascadeError = ReadNullableDouble(root, TelemetryKeys.BathCascadeError);
        // Zero is the Hub's "no filtered PV yet" (cascade off), not a temperature.
        Readings.BathCascadePvFiltered = ReadNullableDouble(root, TelemetryKeys.BathCascadePvFiltered) is { } filtered && filtered > 0
            ? TemperatureCorrection.ToReal(filtered, Offset)
            : ReadNullableDouble(root, TelemetryKeys.BathCascadePvFiltered);
        Readings.BathCascadeP = ReadNullableDouble(root, TelemetryKeys.BathCascadeP);
        Readings.BathCascadeI = ReadNullableDouble(root, TelemetryKeys.BathCascadeI);
        Readings.BathCascadeFine = TryGetBool(root, TelemetryKeys.BathCascadeFine, out var fine) ? fine : null;
        Readings.BathCascadeSlopeCMin = ReadNullableDouble(root, TelemetryKeys.BathCascadeSlopeCMin);
        if (TryGetCounter(root, TelemetryKeys.BathCascadeLastUpdateMs, out var updateMs))
            Readings.BathCascadeLastUpdateMs = updateMs;

        Readings.BathMode = TryGetInt(root, TelemetryKeys.BathMode, out var mode) ? mode : null;
        Readings.BathSpSource = TryGetInt(root, TelemetryKeys.BathSpSource, out var source) ? source : null;
        Readings.BathCascadeSaturated = TryGetBool(root, TelemetryKeys.BathCascadeSaturated, out var saturated) && saturated;
        Readings.BathCascadeState = ReadString(root, TelemetryKeys.BathCascadeState) ?? Readings.BathCascadeState;
        Readings.BathState = ReadString(root, TelemetryKeys.BathState) ?? Readings.BathState;
        Readings.BathPhase = ReadString(root, TelemetryKeys.BathPhase) ?? Readings.BathPhase;
        Readings.BathError = ReadString(root, TelemetryKeys.BathError) ?? Readings.BathError;
        Readings.BathGuard = ReadString(root, TelemetryKeys.BathGuard) ?? Readings.BathGuard;
        Readings.BathCascadePausedReason = ReadString(root, TelemetryKeys.BathCascadePausedReason) ?? Readings.BathCascadePausedReason;

        Readings.TemperatureValid = TryGetBool(root, TelemetryKeys.TempvalValid, out var tempValid) ? tempValid : null;
        Readings.TemperatureAgeMs = TryGetCounter(root, TelemetryKeys.TempvalAgeMs, out var tempAge) ? tempAge : null;
        Readings.BathOwned = TryGetBool(root, TelemetryKeys.BathOwned, out var owned) ? owned : null;
        Readings.BathCascadeActive = TryGetBool(root, TelemetryKeys.BathCascadeActive, out var active) ? active : null;
        Readings.BathCascadeFaultReason = ReadString(root, TelemetryKeys.BathCascadeFaultReason) ?? "";
        Readings.BathStopPending = TryGetBool(root, TelemetryKeys.BathStopPending, out var stopPending) && stopPending;
        Readings.BathCommandCompletion = ReadString(root, TelemetryKeys.BathCommandCompletion) ?? "";
        Readings.BathOperationError = ReadString(root, TelemetryKeys.BathOperationError) ?? "";
        AssignLong(root, TelemetryKeys.BathNodeRejectId, v => Readings.BathNodeRejectId = v);
        Readings.BathNodeRejectError = ReadString(root, TelemetryKeys.BathNodeRejectError) ?? "";
        Readings.BathNodeSpMin = ReadNullableDouble(root, TelemetryKeys.BathNodeSpMin);
        Readings.BathNodeSpMax = ReadNullableDouble(root, TelemetryKeys.BathNodeSpMax);
        Readings.TempSetpointCommanded = TryGetBool(root, TelemetryKeys.TempSetpointCommanded, out var commanded) ? commanded : null;
        Readings.TempModuleActuatorOn = TryGetBool(root, TelemetryKeys.TempModuleActuatorOn, out var moduleOn) ? moduleOn : null;
        Readings.BathCascadeConfigError = ReadString(root, TelemetryKeys.BathCascadeConfigError) ?? "";
        Readings.BathCascadeConfig = ReadBathTuning(root);

        if (presence.HasTelemetry && !presence.Online)
        {
            // The node's last report is not a fact once the Hub stops seeing it.
            Readings.BathSp = null;
            Readings.BathTarget = null;
            Readings.BathPv = null;
            Readings.BathDeviation = null;
            Readings.BathDisplaySp = null;
            Readings.BathMode = null;
            Readings.BathSpSource = null;
            Readings.BathState = "";
            Readings.BathPhase = "";
            Readings.BathError = "";
            Readings.BathGuard = "";
        }
    }

    /// <summary>The Hub's vigent cascade tuning (10.6), or null when any key is missing.</summary>
    private static BathCascadeTuning? ReadBathTuning(JsonElement root)
    {
        if (!TryGetFiniteDouble(root, TelemetryKeys.BathCascadeKp, out var kp) ||
            !TryGetFiniteDouble(root, TelemetryKeys.BathCascadeTiS, out var ti) ||
            !TryGetFiniteDouble(root, TelemetryKeys.BathCascadeBiasC, out var bias) ||
            !TryGetFiniteDouble(root, TelemetryKeys.BathCascadePeriodMs, out var period) ||
            !TryGetFiniteDouble(root, TelemetryKeys.BathCascadeFilterS, out var filter) ||
            !TryGetFiniteDouble(root, TelemetryKeys.BathCascadeCommandMinMs, out var minMs) ||
            !TryGetFiniteDouble(root, TelemetryKeys.BathCascadeCommandBandC, out var band) ||
            !TryGetFiniteDouble(root, TelemetryKeys.BathCascadeSlewCMin, out var slew) ||
            !TryGetFiniteDouble(root, TelemetryKeys.BathCascadeOffsetHighC, out var high) ||
            !TryGetFiniteDouble(root, TelemetryKeys.BathCascadeOffsetLowC, out var low) ||
            !TryGetFiniteDouble(root, TelemetryKeys.BathCascadeOutputMinC, out var outMin) ||
            !TryGetFiniteDouble(root, TelemetryKeys.BathCascadeOutputMaxC, out var outMax))
        {
            return null;
        }

        return new BathCascadeTuning(kp, ti, bias, (int)Math.Round(period), filter,
            (int)Math.Round(minMs), band, slew, high, low, outMin, outMax);
    }

    private void ParseServo(JsonElement root, DateTimeOffset now)
    {
        var sawValues = TryGetPropertyCaseInsensitive(root, TelemetryKeys.ServoRpm, out _) ||
                        TryGetPropertyCaseInsensitive(root, TelemetryKeys.ServoPowerW, out _);

        var presence = ResolvePresence(
            root,
            TelemetryKeys.ServoOnline,
            sawValues,
            _config.ServoTimeout,
            new Presence(Readings.HasServoTelemetry, Readings.ServoOnline, Readings.ServoLastSeenAt),
            now);

        Readings.HasServoTelemetry = presence.HasTelemetry;
        Readings.ServoOnline = presence.Online;
        Readings.ServoLastSeenAt = presence.LastSeenAt;

        if (TryGetBool(root, TelemetryKeys.ServoCommEnabled, out var commEnabled))
        {
            Readings.ServoCommEnabled = commEnabled;
        }

        AssignInt(root, TelemetryKeys.ServoCommandQueueDepth,
            v => Readings.ServoCommandQueueDepth = v);

        Readings.MotorControlViaModbus =
            TryGetBool(root, TelemetryKeys.MotorControlViaModbus, out var viaModbus)
                ? viaModbus
                : null;
        AssignInt(root, TelemetryKeys.ServoMotorRouteAck, v =>
        {
            if (v is >= -1 and <= 1)
            {
                Readings.ServoMotorRouteAck = v;
            }
        });

        // Not sticky, and null rather than false when the key is absent: a Hub that does
        // not mention a pending command is saying there is none, but a Hub that has no
        // such channel at all is saying nothing, and silence is not a confirmation.
        Readings.ServoCommandPending =
            TryGetBool(root, TelemetryKeys.ServoCommandPending, out var pending) ? pending : null;

        // The Hub publishes the ten measurements only when there is a publishable
        // sample - fresh presence AND routing on - so their absence carries meaning and
        // has to invalidate. This covers both ways they can stop arriving: the node
        // going away, and routing being switched off with the node still pushing. In the
        // second case presence stays true, so an offline check alone would miss it and
        // leave the last sample on screen looking live.
        Readings.HasServoSample = sawValues;

        if (!sawValues)
        {
            ClearServoReadings();
            return;
        }

        if (TryGetFiniteDouble(root, TelemetryKeys.ServoRpm, out var rpm))
        {
            Readings.ServoRpm = rpm;
        }

        if (TryGetFiniteDouble(root, TelemetryKeys.ServoTorquePct, out var torquePct))
        {
            Readings.ServoTorquePct = torquePct;
        }

        if (TryGetFiniteDouble(root, TelemetryKeys.ServoTorqueNm, out var torqueNm))
        {
            Readings.ServoTorqueNm = torqueNm;
        }

        if (TryGetFiniteDouble(root, TelemetryKeys.ServoLoadPct, out var loadPct))
        {
            Readings.ServoLoadPct = loadPct;
        }

        if (TryGetFiniteDouble(root, TelemetryKeys.ServoPowerW, out var powerW))
        {
            Readings.ServoPowerW = powerW;
        }

        if (TryGetFiniteDouble(root, TelemetryKeys.ServoEnergyWh, out var energyWh))
        {
            Readings.ServoEnergyWh = energyWh;
        }

        // The node validates the state before pushing and the Hub rejects the sample
        // outright if it is outside 0-3, so a stray value here means something upstream
        // is wrong. Refusing it keeps a nonsense state out of the alarm path, where 3
        // means ALARM.
        AssignInt(root, TelemetryKeys.ServoState, v =>
        {
            if (v is >= 0 and <= 3)
            {
                Readings.ServoState = v;
            }
        });

        AssignInt(root, TelemetryKeys.ServoAlarm, v => Readings.ServoAlarm = v);

        if (TryGetCounter(root, TelemetryKeys.ServoCommOk, out var commOk))
        {
            Readings.ServoCommOk = commOk;
        }

        if (TryGetCounter(root, TelemetryKeys.ServoCommErr, out var commErr))
        {
            Readings.ServoCommErr = commErr;
        }
    }

    /// <summary>
    /// Returns the ten servo measurements to their sentinels, leaving presence, routing
    /// and queue state alone.
    /// </summary>
    /// <remarks>
    /// Sentinels, never zero. Zero rpm, zero torque and zero power are all legitimate
    /// readings from a stopped motor, so a zero here would claim a measurement that was
    /// never taken - and the UI would have no way to render the dash it owes the
    /// operator.
    /// </remarks>
    private void ClearServoReadings()
    {
        Readings.ServoRpm = SensorReadings.NotReceived;
        Readings.ServoTorquePct = SensorReadings.NotReceived;
        Readings.ServoTorqueNm = SensorReadings.NotReceived;
        Readings.ServoLoadPct = SensorReadings.NotReceived;
        Readings.ServoPowerW = SensorReadings.NotReceived;
        Readings.ServoEnergyWh = SensorReadings.NotReceived;
        Readings.ServoState = -1;
        Readings.ServoAlarm = -1;
        Readings.ServoCommOk = -1;
        Readings.ServoCommErr = -1;
    }

    // ------------------------------------------------------------------
    // External-device presence
    // ------------------------------------------------------------------

    /// <summary>One external device's presence, as of the frame being parsed.</summary>
    private readonly record struct Presence(bool HasTelemetry, bool Online, DateTimeOffset? LastSeenAt);

    /// <summary>
    /// Resolves whether an external node is present, from the best evidence in this frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two sources, in order of authority. The Hub's own flag wins whenever the frame
    /// carries it: only the Hub can time a node's pushes, and only the Hub can tell
    /// "stopped" from "gone" for a node that goes quiet when idle.
    /// </para>
    /// <para>
    /// Without the flag - any Hub built before it existed - the only evidence is whether
    /// the device's value keys are still arriving, so they are aged out locally. That is
    /// slower and coarser than the Hub's window, and deliberately kept: the app has to
    /// stay honest against an unflashed Hub rather than assume the newest firmware.
    /// </para>
    /// <para>
    /// A device the Hub has never mentioned keeps <see cref="Presence.HasTelemetry"/>
    /// false. The UI renders that as <i>awaiting telemetry</i>, never as <i>offline</i>:
    /// absence of evidence is not evidence of absence, and an operator must not be told a
    /// device failed when nothing has been claimed about it.
    /// </para>
    /// </remarks>
    private static Presence ResolvePresence(
        JsonElement root,
        string onlineKey,
        bool sawValues,
        TimeSpan timeout,
        Presence previous,
        DateTimeOffset now)
    {
        if (TryGetBool(root, onlineKey, out var reported))
        {
            return new Presence(true, reported, reported ? now : previous.LastSeenAt);
        }

        if (sawValues)
        {
            return new Presence(true, true, now);
        }

        if (previous.LastSeenAt is { } lastSeen && now - lastSeen >= timeout)
        {
            return previous with { Online = false };
        }

        return previous;
    }

    private void ParseTime(JsonElement root)
    {
        if (TryGetDouble(root, TelemetryKeys.Time, out var seconds))
        {
            Readings.TimeRawSeconds = seconds;
        }
    }

    // ------------------------------------------------------------------
    // JSON helpers
    // ------------------------------------------------------------------

    private static bool TryGetPropertyCaseInsensitive(JsonElement root, string key, out JsonElement value)
    {
        if (root.TryGetProperty(key, out value))
        {
            return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <remarks>
    /// The firmware is not consistent about quoting numerics, so a string that
    /// parses as a number is accepted. Parsing is always invariant.
    /// </remarks>
    private static bool TryGetDouble(JsonElement root, string key, out double value)
    {
        value = 0;
        if (!TryGetPropertyCaseInsensitive(root, key, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDouble(out value);

            case JsonValueKind.String:
                return double.TryParse(
                    element.GetString()?.Replace(',', '.'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);

            default:
                return false;
        }
    }

    /// <summary>
    /// <see cref="TryGetDouble"/> plus a finiteness check.
    /// </summary>
    /// <remarks>
    /// JSON has no NaN or infinity literals, so a numeric value cannot be either - but a
    /// <i>string</i> value can: <c>double.TryParse</c> happily accepts <c>"NaN"</c> and
    /// <c>"Infinity"</c>, and the firmware quotes some numbers. A NaN reaching a reading
    /// would poison every average and comparison downstream while looking like data, so
    /// the servo channels refuse it and keep whatever they had.
    /// </remarks>
    private static bool TryGetFiniteDouble(JsonElement root, string key, out double value)
        => TryGetDouble(root, key, out value) && double.IsFinite(value);

    private static double? ReadNullableDouble(JsonElement root, string key)
    {
        if (!TryGetPropertyCaseInsensitive(root, key, out var element))
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.Null
            ? null
            : TryGetFiniteDouble(root, key, out var value) ? value : null;
    }

    private static string? ReadString(JsonElement root, string key)
        => TryGetPropertyCaseInsensitive(root, key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>Reads a <c>uint32</c> counter into a <c>long</c>, refusing anything outside the range.</summary>
    /// <remarks>
    /// The counters are unsigned 32-bit on the wire. A negative or oversized value is not
    /// a counter that wrapped, it is a frame that should not be trusted - and accepting
    /// one would show up as a huge negative delta in the error <i>rate</i>, which is what
    /// the alarm watches.
    /// </remarks>
    private static bool TryGetCounter(JsonElement root, string key, out long value)
    {
        value = -1;

        if (!TryGetFiniteDouble(root, key, out var raw) || raw < 0 || raw > uint.MaxValue)
        {
            return false;
        }

        value = (long)raw;
        return true;
    }

    private static bool TryGetBool(JsonElement root, string key, out bool value)
    {
        value = false;
        if (!TryGetPropertyCaseInsensitive(root, key, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;

            case JsonValueKind.False:
                return true;

            // Tolerate 1/0, which the firmware uses for some flags.
            case JsonValueKind.Number when element.TryGetDouble(out var number):
                value = number != 0;
                return true;

            default:
                return false;
        }
    }

    private static void AssignInt(JsonElement root, string key, Action<int> assign)
    {
        if (!TryGetPropertyCaseInsensitive(root, key, out var element))
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt32(out var direct):
                assign(direct);
                break;

            // v.6 coerces through float, so 1.0 must land as 1.
            case JsonValueKind.Number when element.TryGetDouble(out var asDouble):
                assign((int)asDouble);
                break;

            case JsonValueKind.String when int.TryParse(
                element.GetString()?.Replace(',', '.'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                assign(parsed);
                break;

            default:
                break;
        }
    }

    private static void AssignLong(JsonElement root, string key, Action<long> assign)
    {
        if (!root.TryGetProperty(key, out var element))
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt64(out var direct):
                assign(direct);
                break;
            case JsonValueKind.String when long.TryParse(
                element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                assign(parsed);
                break;
        }
    }

    private static bool TryGetInt(JsonElement root, string key, out int value)
    {
        value = 0;
        if (!TryGetPropertyCaseInsensitive(root, key, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt32(out var direct):
                value = direct;
                return true;

            // v.6 coerces through float, so 1.0 must land as 1.
            case JsonValueKind.Number when element.TryGetDouble(out var asDouble):
                value = (int)asDouble;
                return true;

            case JsonValueKind.String when int.TryParse(
                element.GetString()?.Replace(',', '.'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;

            default:
                return false;
        }
    }
}
