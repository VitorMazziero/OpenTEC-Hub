namespace TecnalHub.Services.Control;

/// <summary>
/// One actuator's operating band and the slice of control effort that drives it.
/// </summary>
/// <remarks>
/// <para>
/// The cascade produces a single scalar effort in 0-100 %; the allocator spreads that
/// effort across the actuators that raise kLa - agitation first, then aeration - each
/// over its own band. This is the "Janelas de atuação" model in
/// <c>docs/UI_DESIGN.md</c> section 5.2, drawn there as a stacked bar with an overlap
/// region.
/// </para>
/// <para>
/// <see cref="EffortStart"/>/<see cref="EffortEnd"/> place the actuator on the effort
/// axis. Below the start it rests at <see cref="Min"/>; above the end it is pinned at
/// <see cref="Max"/>; between them it ramps linearly. Two windows whose effort spans
/// overlap ramp together through the overlap - which is how agitation and aeration can
/// be made to share the mid-range rather than handing off abruptly.
/// </para>
/// </remarks>
/// <param name="Name">Stable identifier, e.g. <c>agitation</c> or <c>aeration</c>.</param>
/// <param name="Min">Value commanded at or below <see cref="EffortStart"/>.</param>
/// <param name="Max">Value commanded at or above <see cref="EffortEnd"/>.</param>
/// <param name="EffortStart">Effort percent at which the actuator begins to move.</param>
/// <param name="EffortEnd">Effort percent at which the actuator reaches <see cref="Max"/>.</param>
public sealed record ActuatorWindow(
    string Name,
    double Min,
    double Max,
    double EffortStart,
    double EffortEnd)
{
    /// <summary>The actuator value for a given control effort, in the actuator's own unit.</summary>
    public double Evaluate(double effortPercent)
    {
        var span = EffortEnd - EffortStart;
        if (span <= 0)
        {
            // A zero-width window is a pure threshold: off below the point, full at or above.
            return effortPercent >= EffortEnd ? Max : Min;
        }

        var fraction = Math.Clamp((effortPercent - EffortStart) / span, 0.0, 1.0);
        return Min + (fraction * (Max - Min));
    }

    /// <summary>Validation reasons, or an empty list when the window is usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            issues.Add("Actuator window needs a name.");
        }

        if (!double.IsFinite(Min) || !double.IsFinite(Max) || Min > Max)
        {
            issues.Add($"Window '{Name}': Min must be finite and not above Max.");
        }

        if (!double.IsFinite(EffortStart) || !double.IsFinite(EffortEnd) ||
            EffortStart < 0 || EffortEnd > 100 || EffortStart > EffortEnd)
        {
            issues.Add($"Window '{Name}': require 0 <= EffortStart <= EffortEnd <= 100.");
        }

        return issues;
    }
}

/// <summary>
/// Maps the cascade's scalar control effort onto every actuator's command value.
/// </summary>
/// <remarks>
/// Deliberately independent of <i>which</i> effort to command - that is the PID's job -
/// and of <i>how</i> effort relates to kLa. The kLa gradient-path surface that will pick
/// the effort split by maximising actuator headroom is a later Phase 2 work package and
/// an open export-format decision (<c>docs/DECISIONS.md</c> D-008); it replaces this
/// linear split without touching the controller.
/// </remarks>
public sealed class ActuatorWindowAllocator
{
    private readonly ActuatorWindow[] _windows;

    public ActuatorWindowAllocator(params ActuatorWindow[] windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Length == 0)
        {
            throw new ArgumentException("At least one actuator window is required.", nameof(windows));
        }

        _windows = (ActuatorWindow[])windows.Clone();
    }

    /// <summary>The configured windows, in declaration order.</summary>
    public IReadOnlyList<ActuatorWindow> Windows => _windows;

    /// <summary>
    /// Evaluates every actuator at the given effort, preserving declaration order.
    /// </summary>
    public IReadOnlyList<double> Allocate(double effortPercent)
    {
        var result = new double[_windows.Length];
        for (var i = 0; i < _windows.Length; i++)
        {
            result[i] = _windows[i].Evaluate(effortPercent);
        }

        return result;
    }

    /// <summary>Evaluates a single named actuator, e.g. to feed one wire field.</summary>
    public double Allocate(string name, double effortPercent)
    {
        foreach (var window in _windows)
        {
            if (string.Equals(window.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return window.Evaluate(effortPercent);
            }
        }

        throw new KeyNotFoundException($"No actuator window named '{name}'.");
    }
}
