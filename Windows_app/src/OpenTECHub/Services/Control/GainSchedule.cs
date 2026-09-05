namespace OpenTECHub.Services.Control;

/// <summary>One PID gain triple (velocity-form Kp/Ki/Kd).</summary>
public readonly record struct GainSet(double Kp, double Ki, double Kd);

/// <summary>A gain triple pinned to a control-effort breakpoint on the schedule curve.</summary>
/// <param name="EffortPercent">The scheduling variable — the loop's control effort, 0-100 %.</param>
public sealed record GainScheduleBreakpoint(double EffortPercent, double Kp, double Ki, double Kd);

/// <summary>
/// A gain schedule: PID gains as a piecewise-linear function of the control effort.
/// </summary>
/// <remarks>
/// <para>
/// The manuscript's loop gain scales as 1/kLa, and the effort maps monotonically onto kLa along
/// the published path — so effort is the natural scheduling variable, and a schedule that raises
/// the gains with effort holds the loop gain roughly constant across the operating range. The
/// paper shows a single fixed set is workable <i>without</i> scheduling because sensitivity is
/// nearly flat across a fourteen-fold kLa change; this is therefore an opt-in refinement, not a
/// requirement (<c>docs/DECISIONS.md</c> D-020).
/// </para>
/// <para>Pure. Gains are interpolated linearly between breakpoints and held flat outside them.</para>
/// </remarks>
public sealed class GainSchedule
{
    private readonly GainScheduleBreakpoint[] _breakpoints;

    public GainSchedule(IReadOnlyList<GainScheduleBreakpoint> breakpoints)
    {
        ArgumentNullException.ThrowIfNull(breakpoints);
        var issues = ValidateBreakpoints(breakpoints);
        if (issues.Count > 0)
        {
            throw new ArgumentException(issues[0], nameof(breakpoints));
        }

        _breakpoints = [.. breakpoints.OrderBy(b => b.EffortPercent)];
    }

    /// <summary>The breakpoints, ordered by effort.</summary>
    public IReadOnlyList<GainScheduleBreakpoint> Breakpoints => _breakpoints;

    /// <summary>The number of straight segments between breakpoints.</summary>
    public int SegmentCount => _breakpoints.Length - 1;

    /// <summary>
    /// The index of the segment containing <paramref name="effortPercent"/>, clamped to a real
    /// segment at either end. This is the discrete quantity whose change is journalled.
    /// </summary>
    public int SegmentAt(double effortPercent)
    {
        for (var i = 0; i < _breakpoints.Length - 1; i++)
        {
            if (effortPercent < _breakpoints[i + 1].EffortPercent)
            {
                return i;
            }
        }

        return _breakpoints.Length - 2;
    }

    /// <summary>
    /// The scheduled gains at <paramref name="effortPercent"/>: linear between the bracketing
    /// breakpoints, and the nearest breakpoint's gains outside the mapped range.
    /// </summary>
    public GainSet GainsAt(double effortPercent)
    {
        if (effortPercent <= _breakpoints[0].EffortPercent)
        {
            return ToSet(_breakpoints[0]);
        }

        var last = _breakpoints[^1];
        if (effortPercent >= last.EffortPercent)
        {
            return ToSet(last);
        }

        var segment = SegmentAt(effortPercent);
        var low = _breakpoints[segment];
        var high = _breakpoints[segment + 1];
        var span = high.EffortPercent - low.EffortPercent;
        var t = span > 0 ? (effortPercent - low.EffortPercent) / span : 0.0;

        return new GainSet(
            Lerp(low.Kp, high.Kp, t),
            Lerp(low.Ki, high.Ki, t),
            Lerp(low.Kd, high.Kd, t));
    }

    /// <summary>Returns the reasons a breakpoint set is unusable, or an empty list when valid.</summary>
    public static IReadOnlyList<string> ValidateBreakpoints(IReadOnlyList<GainScheduleBreakpoint> breakpoints)
    {
        var issues = new List<string>();

        if (breakpoints.Count < 2)
        {
            issues.Add("A gain schedule needs at least two breakpoints.");
            return issues;
        }

        var ordered = breakpoints.OrderBy(b => b.EffortPercent).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var b = ordered[i];
            if (!double.IsFinite(b.EffortPercent) || b.EffortPercent < 0 || b.EffortPercent > 100)
            {
                issues.Add("Breakpoint efforts must be finite and within 0-100 %.");
            }

            if (!double.IsFinite(b.Kp) || !double.IsFinite(b.Ki) || !double.IsFinite(b.Kd) ||
                b.Kp < 0 || b.Ki < 0 || b.Kd < 0)
            {
                issues.Add("Breakpoint gains must be finite and non-negative.");
            }

            if (i > 0 && ordered[i].EffortPercent <= ordered[i - 1].EffortPercent)
            {
                issues.Add("Breakpoint efforts must strictly increase.");
            }
        }

        return issues;
    }

    private static GainSet ToSet(GainScheduleBreakpoint b) => new(b.Kp, b.Ki, b.Kd);

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
