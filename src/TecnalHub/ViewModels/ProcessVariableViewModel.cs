using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TecnalHub.Protocol;

namespace TecnalHub.ViewModels;

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

    public ProcessVariableViewModel(
        string id,
        string displayName,
        string unit,
        int decimals = 1,
        bool isControllable = true,
        bool isCommandedOnly = false)
    {
        Id = id;
        DisplayName = displayName;
        Unit = unit;
        Decimals = decimals;
        IsControllable = isControllable;
        IsCommandedOnly = isCommandedOnly;
    }

    /// <summary>Stable identifier, used for selection and persistence.</summary>
    public string Id { get; }

    /// <summary>pt-BR label shown to the operator.</summary>
    public string DisplayName { get; }

    public string Unit { get; }

    public int Decimals { get; }

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

    /// <summary>True when the reading is real rather than a sentinel.</summary>
    public bool HasValue => Value is not null;

    /// <summary>
    /// The reading, formatted for display. An em dash when there is nothing to show -
    /// never a zero, which would read as a genuine measurement.
    /// </summary>
    public string FormattedValue => Value is { } v
        ? v.ToString("F" + Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture)
        : "—";

    public string FormattedSetpoint => Setpoint is { } s
        ? s.ToString("F" + Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture)
        : "—";

    /// <summary>
    /// Applies a new reading, deriving the trend.
    /// </summary>
    /// <param name="reading">
    /// Raw value from telemetry. <see cref="SensorReadings.NotReceived"/> and anything
    /// below it is treated as "no data", not as a measurement.
    /// </param>
    public void Push(double reading)
    {
        if (reading <= SensorReadings.NotReceived)
        {
            Value = null;
            Trend = TrendDirection.Flat;
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
            var change = (reading - previous) / scale;

            Trend = change switch
            {
                > TrendDeadband => TrendDirection.Rising,
                < -TrendDeadband => TrendDirection.Falling,
                _ => TrendDirection.Flat,
            };
        }

        _previousValue = reading;
        Value = reading;
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
    }
}
