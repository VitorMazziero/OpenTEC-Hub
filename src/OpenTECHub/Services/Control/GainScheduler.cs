namespace OpenTECHub.Services.Control;

/// <summary>The result of advancing the gain scheduler by one loop step.</summary>
/// <param name="Tuning">The base tuning with the effective scheduled gains substituted in.</param>
/// <param name="Effective">The gains actually applied this step, after slew limiting.</param>
/// <param name="Target">The scheduled gains for the current effort, before slew limiting.</param>
/// <param name="Segment">The schedule segment the current effort falls in.</param>
/// <param name="SegmentChanged">True when the effort crossed a breakpoint since the last step.</param>
/// <param name="EffortPercent">The effort the schedule was evaluated at.</param>
public sealed record GainScheduleUpdate(
    CascadeTuning Tuning,
    GainSet Effective,
    GainSet Target,
    int Segment,
    bool SegmentChanged,
    double EffortPercent);

/// <summary>
/// Drives a <see cref="GainSchedule"/> against the live control effort, with <b>bounded</b>
/// transitions: the effective gains move toward the scheduled target no faster than a slew limit,
/// so even a fast effort excursion cannot step-change the loop's gains.
/// </summary>
/// <remarks>
/// The velocity-form controller makes the substitution bumpless in the output — the gains multiply
/// the increment, not the absolute output — so slew limiting the gains bounds only how quickly the
/// loop's <i>responsiveness</i> changes, which is exactly the transition the schedule should tame.
/// Pure and single-threaded; the cascade service drives it from the telemetry loop.
/// </remarks>
public sealed class GainScheduler
{
    private readonly GainSchedule _schedule;
    private readonly CascadeTuning _baseTuning;
    private readonly double _maxSlewPerSecond;

    private GainSet _effective;
    private int _segment;
    private bool _started;

    public GainScheduler(
        GainSchedule schedule, CascadeTuning baseTuning, double maxSlewPerSecond, GainSet initial)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(baseTuning);
        if (!double.IsFinite(maxSlewPerSecond) || maxSlewPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSlewPerSecond), maxSlewPerSecond, "The slew limit must be a positive rate.");
        }

        _schedule = schedule;
        _baseTuning = baseTuning;
        _maxSlewPerSecond = maxSlewPerSecond;
        _effective = initial;
    }

    /// <summary>The gains currently applied, after slew limiting.</summary>
    public GainSet Effective => _effective;

    /// <summary>The schedule segment the last step fell in.</summary>
    public int Segment => _segment;

    /// <summary>
    /// Advances the schedule to <paramref name="effortPercent"/> over <paramref name="dtSeconds"/>,
    /// moving the effective gains toward the scheduled target within the slew limit.
    /// </summary>
    public GainScheduleUpdate Step(double effortPercent, double dtSeconds)
    {
        var target = _schedule.GainsAt(effortPercent);
        var newSegment = _schedule.SegmentAt(effortPercent);
        var segmentChanged = _started && newSegment != _segment;
        _segment = newSegment;
        _started = true;

        var maxStep = _maxSlewPerSecond * Math.Max(dtSeconds, 0.0);
        _effective = new GainSet(
            Slew(_effective.Kp, target.Kp, maxStep),
            Slew(_effective.Ki, target.Ki, maxStep),
            Slew(_effective.Kd, target.Kd, maxStep));

        var tuning = _baseTuning with { Kp = _effective.Kp, Ki = _effective.Ki, Kd = _effective.Kd };
        return new GainScheduleUpdate(tuning, _effective, target, newSegment, segmentChanged, effortPercent);
    }

    /// <summary>Snaps the effective gains to <paramref name="gains"/>, e.g. on a bumpless preload.</summary>
    public void ResetTo(GainSet gains) => _effective = gains;

    private static double Slew(double current, double target, double maxStep)
        => current + Math.Clamp(target - current, -maxStep, maxStep);
}
