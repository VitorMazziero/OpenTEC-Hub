using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Telemetry;

namespace OpenTECHub.ViewModels;

/// <summary>
/// How a variable is behaving, for the one colour the UI is allowed to use.
/// </summary>
/// <remarks>See <c>docs/UI_DESIGN.md</c>, "Colour discipline".</remarks>
public enum VariableState
{
    /// <summary>Disabled, or nothing received yet.</summary>
    Idle,

    /// <summary>Reading is present and within band.</summary>
    Ok,

    /// <summary>A controller is actively driving this actuator.</summary>
    Actuating,

    /// <summary>Outside the acceptable band.</summary>
    Warning,

    /// <summary>Alarm condition.</summary>
    Alarm,
}

/// <summary>Direction of recent movement, for the KPI strip's trend arrow.</summary>
public enum TrendDirection
{
    Flat,
    Rising,
    Falling,
}

/// <summary>
/// One live process variable: its reading, its setpoint, and how to display both.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the KPI strip, the synoptic and the detail pane - all three bind to the
/// same instance, so a value can never disagree with itself between views.
/// </para>
/// <para>
/// Deliberately knows nothing about the wire. Sending is the owning
/// <c>SubsystemViewModel</c>'s job; this type only presents.
/// </para>
/// </remarks>
public sealed partial class ProcessVariableViewModel : ObservableObject
{
    /// <summary>Movement smaller than this fraction of the reading counts as flat.</summary>
    private const double TrendDeadband = 0.005;

    private double? _previousValue;
    private double _displayScale = 1.0;
    private double _displayOffset;
    private readonly Queue<double> _healthSamples = new();
    private DateTimeOffset? _lastAcceptedAt;

    public ProcessVariableViewModel(
        string id,
        string displayName,
        string unit,
        int decimals = 1,
        bool isControllable = true,
        bool isCommandedOnly = false,
        TelemetryChannel? channel = null,
        string? detailNote = null)
    {
        Id = id;
        DisplayName = displayName;
        Unit = unit;
        Decimals = decimals;
        IsControllable = isControllable;
        IsCommandedOnly = isCommandedOnly;
        Channel = channel;
        DetailNote = detailNote ??
            "Somente leitura nesta fase. O equipamento informa esta variável, mas o aplicativo ainda não a controla.";
    }

    /// <summary>
    /// One honest sentence for the read-only detail pane: what the reading is and where its
    /// controls live. Defaults to the "measured but uncontrolled" wording; the WP7 dosing
    /// variables point at their Controle cards instead.
    /// </summary>
    public string DetailNote { get; }

    /// <summary>Stable identifier, used for selection and persistence.</summary>
    public string Id { get; }

    /// <summary>pt-BR label shown to the operator.</summary>
    public string DisplayName { get; }

    public string Unit { get; private set; }

    public int Decimals { get; private set; }

    /// <summary>
    /// Series in the history buffer, for the detail pane's inline trend.
    /// </summary>
    /// <remarks>
    /// Null where nothing is recorded. The variable knows which channel it is; the pane
    /// does not have to carry a lookup table that would drift the moment a variable is
    /// added.
    /// </remarks>
    public TelemetryChannel? Channel { get; }

    /// <summary>Calibration page tab associated with this sensor, when one exists.</summary>
    public string? CalibrationTarget => Channel switch
    {
        TelemetryChannel.PH => "ph",
        TelemetryChannel.Oxygen => "oxygen",
        TelemetryChannel.Flow => "flow",
        TelemetryChannel.Biomass => "biomass",
        _ => null,
    };

    public bool HasCalibration => CalibrationTarget is not null;

    /// <summary>
    /// Only sensors requested for the operator health summary participate. Actuators,
    /// commanded-only values and undocumented auxiliary readings deliberately do not.
    /// </summary>
    public bool SupportsSensorHealth => Channel is
        TelemetryChannel.Temperature or
        TelemetryChannel.PH or
        TelemetryChannel.Oxygen or
        TelemetryChannel.Flow or
        TelemetryChannel.Biomass or
        TelemetryChannel.Distance;

    public string LastAcceptedText => _lastAcceptedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) ?? "—";

    public string SignalPresentText => HasValue ? "Sim" : "Não";

    public string HealthSampleCountText => $"{_healthSamples.Count}/30";

    public string HealthNoiseText { get; private set; } = "—";

    public string HealthStatusText { get; private set; } = "Sem sinal";

    public VariableState HealthState { get; private set; } = VariableState.Idle;

    /// <summary>False for read-only readings such as pressure.</summary>
    public bool IsControllable { get; }

    /// <summary>
    /// True when the device reports no feedback for this variable, so the displayed
    /// figure is what was <b>commanded</b>, not what was measured.
    /// </summary>
    /// <remarks>
    /// Agitation is the case: the firmware sends no RPM reading. Showing a commanded
    /// value styled identically to a measured one would imply verification the
    /// equipment never provided, so the View marks these differently.
    /// </remarks>
    public bool IsCommandedOnly { get; }

    /// <summary>Latest reading, or null when nothing valid has arrived.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedValue))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    public partial double? Value { get; set; }

    /// <summary>
    /// A second reading shown beside the first, already formatted. Null for most variables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Agitation is the one variable measured by two instruments at once: the shaft turns at
    /// a speed, and it takes a torque to turn it there. The second number is what says
    /// whether the first one is being reached easily or against a load, and separating them
    /// onto different cards would hide exactly that relationship.
    /// </para>
    /// <para>
    /// Formatted by the caller rather than derived here. This type knows one unit and one
    /// decimal count, and the second reading is a different quantity in a different unit -
    /// pretending otherwise would mean torque inheriting rpm's formatting.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecondaryReading))]
    public partial string? SecondaryText { get; set; }

    /// <summary>What the second reading is, e.g. <c>% torque</c>. Shown beside its value.</summary>
    [ObservableProperty]
    public partial string? SecondaryLabel { get; set; }

    /// <summary>True when the second reading names something wrong (the flow tile's gas route with no destination, or both open).</summary>
    [ObservableProperty]
    public partial bool IsSecondaryAlert { get; set; }

    /// <summary>True while there is a second reading worth the space it takes.</summary>
    public bool HasSecondaryReading => !string.IsNullOrEmpty(SecondaryText);

    /// <summary>Setpoint the device has acknowledged, or null when not controlled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedSetpoint))]
    public partial double? Setpoint { get; set; }

    [ObservableProperty]
    public partial VariableState State { get; set; } = VariableState.Idle;

    [ObservableProperty]
    public partial TrendDirection Trend { get; set; } = TrendDirection.Flat;

    /// <summary>True while the subsystem is enabled on the device.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>
    /// True when this is the variable the shell has selected.
    /// </summary>
    /// <remarks>
    /// Lives on the variable rather than on each view because selection has to look the
    /// same in the KPI strip, the variable rail and the synoptic simultaneously - they
    /// are three windows onto one selection, not three selections.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>True when the reading is real rather than a sentinel.</summary>
    public bool HasValue => Value is not null;

    /// <summary>
    /// The reading, formatted for display. An em dash when there is nothing to show -
    /// never a zero, which would read as a genuine measurement.
    /// </summary>
    public string FormattedValue
    {
        get
        {
            if (Value is not { } v)
            {
                return "—";
            }

            // Agitation used to mask a zero here, because the value was the command and a
            // commanded zero meant an idle loop rather than a measurement. With the servo
            // node reporting real shaft speed, zero is a stopped motor - a reading, and
            // one the operator needs to see. Absence is carried by a null Value now, which
            // the branch above already renders as a dash.
            return ToDisplay(v).ToString(
                "F" + Decimals.ToString(CultureInfo.InvariantCulture),
                CultureInfo.CurrentCulture);
        }
    }

    public string FormattedSetpoint => Setpoint is { } s
        ? ToDisplay(s).ToString(
            "F" + Decimals.ToString(CultureInfo.InvariantCulture),
            CultureInfo.CurrentCulture)
        : "—";

    /// <summary>Converts a canonical protocol value for display.</summary>
    public double ToDisplay(double canonical) => (canonical * _displayScale) + _displayOffset;

    /// <summary>Converts an operator-facing value back to the protocol unit.</summary>
    /// <remarks>
    /// The rounded boundary removes binary conversion residue (for example,
    /// 98.6 °F becoming 36.99999999999999 °C) before a value reaches command JSON.
    /// Twelve decimal places remain well beyond every protocol channel's resolution.
    /// </remarks>
    public double ToCanonical(double display)
        => Math.Round((display - _displayOffset) / _displayScale, 12, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Changes only presentation. Stored readings and setpoints stay in protocol units.
    /// </summary>
    public void SetPresentation(string unit, int decimals, double scale = 1.0, double offset = 0.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        if (!double.IsFinite(scale) || Math.Abs(scale) < 1e-12)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        _displayScale = scale;
        _displayOffset = offset;
        Unit = unit;
        Decimals = Math.Max(0, decimals);

        OnPropertyChanged(nameof(Unit));
        OnPropertyChanged(nameof(Decimals));
        OnPropertyChanged(nameof(FormattedValue));
        OnPropertyChanged(nameof(FormattedSetpoint));
        ResetHealth();
    }

    /// <summary>
    /// Applies a new reading, deriving the trend.
    /// </summary>
    /// <param name="reading">
    /// Raw value from telemetry. <see cref="SensorReadings.NotReceived"/> and anything
    /// below it is treated as "no data", not as a measurement.
    /// </param>
    public void Push(double reading)
        => Push(reading <= SensorReadings.NotReceived ? null : reading);

    /// <summary>
    /// Applies a reading whose validity the caller has already decided.
    /// </summary>
    /// <param name="reading">The measurement, or null for "no data this frame".</param>
    /// <remarks>
    /// The <see cref="Push(double)"/> overload infers absence from the sentinel, which is
    /// right for channels that cannot go negative. It is wrong for the servo: rpm and
    /// torque are legitimately negative - the drive reports small negative speeds at rest
    /// - so a reading of exactly -1.0 would be mistaken for "never received". Callers with
    /// a real presence flag pass it through here instead of encoding absence in the value.
    /// </remarks>
    public void Push(double? reading)
    {
        if (reading is not { } value)
        {
            Value = null;
            Trend = TrendDirection.Flat;
            ResetHealth();
            if (State != VariableState.Alarm)
            {
                State = VariableState.Idle;
            }

            return;
        }

        if (_previousValue is { } previous)
        {
            // Relative deadband: 0.1 rpm and 0.1 pH are not comparable movements.
            var scale = Math.Max(Math.Abs(previous), 1.0);
            var change = (value - previous) / scale;

            Trend = change switch
            {
                > TrendDeadband => TrendDirection.Rising,
                < -TrendDeadband => TrendDirection.Falling,
                _ => TrendDirection.Flat,
            };
        }

        _previousValue = value;
        Value = value;
        RecordHealthSample(value);
    }

    /// <summary>
    /// Records a value the operator commanded, for variables with no feedback.
    /// </summary>
    public void PushCommanded(double? commanded)
    {
        Value = commanded;
        Setpoint = commanded;
        Trend = TrendDirection.Flat;
        State = commanded is > 0 ? VariableState.Actuating : VariableState.Idle;
    }

    /// <summary>Clears the reading and trend, e.g. on disconnect.</summary>
    public void Clear()
    {
        Value = null;
        _previousValue = null;
        Trend = TrendDirection.Flat;
        State = VariableState.Idle;
        ResetHealth();
    }

    private void ResetHealth()
    {
        _lastAcceptedAt = null;
        _healthSamples.Clear();
        HealthNoiseText = "—";
        HealthStatusText = "Sem sinal";
        HealthState = VariableState.Idle;
        NotifyHealthChanged();
    }

    private void RecordHealthSample(double reading)
    {
        if (!SupportsSensorHealth || !double.IsFinite(reading))
        {
            return;
        }

        _lastAcceptedAt = DateTimeOffset.Now;
        _healthSamples.Enqueue(ToDisplay(reading));
        while (_healthSamples.Count > 30)
        {
            _healthSamples.Dequeue();
        }

        if (_healthSamples.Count < 8)
        {
            HealthNoiseText = "—";
            HealthStatusText = $"Coletando ({_healthSamples.Count}/8)";
            HealthState = VariableState.Idle;
            NotifyHealthChanged();
            return;
        }

        // A linear trend is removed before calculating sigma. This estimates short-term
        // readout noise without classifying a legitimate heating or dosing ramp as a bad
        // sensor merely because the process value is moving.
        var samples = _healthSamples.ToArray();
        var n = samples.Length;
        var meanX = (n - 1) / 2.0;
        var meanY = samples.Average();
        var sumXx = 0.0;
        var sumXy = 0.0;
        for (var index = 0; index < n; index++)
        {
            var centeredX = index - meanX;
            sumXx += centeredX * centeredX;
            sumXy += centeredX * (samples[index] - meanY);
        }

        var slope = sumXx > 0 ? sumXy / sumXx : 0.0;
        var residualSquares = 0.0;
        for (var index = 0; index < n; index++)
        {
            var fitted = meanY + slope * (index - meanX);
            var residual = samples[index] - fitted;
            residualSquares += residual * residual;
        }

        var sigma = Math.Sqrt(residualSquares / Math.Max(1, n - 2));
        var (warning, alarm) = HealthNoiseLimits();
        HealthNoiseText = $"{sigma.ToString("F" + Math.Max(Decimals, 2), CultureInfo.CurrentCulture)} {Unit}".TrimEnd();
        (HealthStatusText, HealthState) = sigma switch
        {
            _ when sigma >= alarm => ("Ruído elevado", VariableState.Alarm),
            _ when sigma >= warning => ("Ruído moderado", VariableState.Warning),
            _ => ("Estável", VariableState.Ok),
        };
        NotifyHealthChanged();
    }

    private (double Warning, double Alarm) HealthNoiseLimits() => Channel switch
    {
        TelemetryChannel.Temperature => (0.5, 1.5),
        TelemetryChannel.PH => (0.04, 0.15),
        TelemetryChannel.Oxygen => (1.0, 4.0),
        TelemetryChannel.Flow => (0.15, 0.50),
        TelemetryChannel.Biomass => (0.01, 0.04),
        TelemetryChannel.Distance => (3.0, 10.0),
        _ => (double.PositiveInfinity, double.PositiveInfinity),
    };

    private void NotifyHealthChanged()
    {
        OnPropertyChanged(nameof(LastAcceptedText));
        OnPropertyChanged(nameof(SignalPresentText));
        OnPropertyChanged(nameof(HealthSampleCountText));
        OnPropertyChanged(nameof(HealthNoiseText));
        OnPropertyChanged(nameof(HealthStatusText));
        OnPropertyChanged(nameof(HealthState));
    }
}
