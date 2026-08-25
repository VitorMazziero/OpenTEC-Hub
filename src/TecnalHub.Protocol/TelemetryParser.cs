using System.Globalization;
using System.Text.Json;

namespace TecnalHub.Protocol;

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

    public SpikeFilterConfig PHFilter { get; init; } = SpikeFilterConfig.ForPH();
    public SpikeFilterConfig OxygenFilter { get; init; } = SpikeFilterConfig.ForOxygen();

    /// <summary>
    /// If no Distance key arrives for this long, Distance is forced back to the
    /// not-received sentinel.
    /// </summary>
    public TimeSpan DistanceTimeout { get; init; } = TimeSpan.FromSeconds(3);
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

        ParseTemperature(root);
        ParseOxygen(root);
        ParsePH(root);
        ParseFlowAndMisc(root);
        ParseBiomass(root);
        ParsePump(root);
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
            Readings.Temperature = value;
        }
    }

    private void ParseOxygen(JsonElement root)
    {
        if (!TryGetDouble(root, TelemetryKeys.OxygenRaw, out var raw) || raw <= 0.1)
        {
            return; // sentinel: probe absent or not yet valid
        }

        if (_oxygenFilter.Update(raw) is not { } accepted)
        {
            return;
        }

        Readings.OxygenRaw = accepted;
        var calibrated = (_config.OxygenCalibrationA * accepted) + _config.OxygenCalibrationB;
        Readings.OxygenCalibrated = Math.Round(Math.Max(calibrated, 0.0), 4);
    }

    private void ParsePH(JsonElement root)
    {
        if (!TryGetDouble(root, TelemetryKeys.PHRaw, out var raw) || raw <= 0.1)
        {
            return;
        }

        if (_phFilter.Update(raw) is not { } acceptedRaw)
        {
            return;
        }

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

    private void ParseFlowAndMisc(JsonElement root)
    {
        if (TryGetDouble(root, TelemetryKeys.Pressure, out var pressure))
        {
            Readings.Pressure = pressure;
        }

        if (TryGetDouble(root, TelemetryKeys.FlowRate, out var flowRate))
        {
            Readings.FlowRate = flowRate;
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
        }

        if (TryGetBool(root, TelemetryKeys.FlowmeterOnline, out var flowmeterOnline))
        {
            Readings.FlowmeterOnline = flowmeterOnline;
        }

        if (TryGetBool(root, TelemetryKeys.FlowControlEnabled, out var flowEnabled))
        {
            Readings.FlowControlEnabled = flowEnabled;
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
        AssignInt(root, TelemetryKeys.FlowCommandId, v => Readings.FlowCommandId = v);
        AssignInt(root, TelemetryKeys.FlowCommandAck, v => Readings.FlowCommandAck = v);
        AssignInt(root, TelemetryKeys.FlowCommandDeliveries, v => Readings.FlowCommandDeliveries = v);
        AssignInt(root, TelemetryKeys.FlowCommandAgeMs, v => Readings.FlowCommandAgeMs = v);
        AssignInt(root, TelemetryKeys.HubStations, v => Readings.HubStations = v);

        ParseDistance(root);
    }

    private void ParseDistance(JsonElement root)
    {
        var now = _time.GetUtcNow();

        if (root.TryGetProperty(TelemetryKeys.Distance, out _))
        {
            if (TryGetDouble(root, TelemetryKeys.Distance, out var distance) &&
                distance is >= 0.0 and < 1000.0)
            {
                Readings.Distance = distance;
                Readings.DistanceLastSeenAt = now;
            }

            return;
        }

        // The ultrasonic sensor simply stops emitting when unplugged, so silence has
        // to be aged out explicitly or a stale height would sit on screen forever.
        if (Readings.DistanceLastSeenAt is { } lastSeen &&
            now - lastSeen >= _config.DistanceTimeout)
        {
            Readings.Distance = SensorReadings.NotReceived;
        }
    }

    private void ParseBiomass(JsonElement root)
    {
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

    private void ParsePump(JsonElement root)
    {
        if (TryGetDouble(root, TelemetryKeys.PumpFlow, out var flow))
        {
            Readings.PumpFlow = flow;
        }

        if (TryGetDouble(root, TelemetryKeys.PumpVolume, out var volume))
        {
            Readings.PumpVolume = volume;
        }
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
}
