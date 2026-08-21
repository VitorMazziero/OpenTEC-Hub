using TecnalHub.Services.KlaMapping;

namespace TecnalHub.Services.Control;

/// <summary>The three explicit operator modes for the oxygen cascade (WP6).</summary>
/// <remarks>
/// v.6 kept agitation-only and aeration-only fallback modes for when one actuator must be
/// held; the scientific mode is the simultaneous kLa-path allocation from the published
/// receipt. Nitrogen enrichment stays out until its own path is proven.
/// </remarks>
public enum CascadeMode
{
    /// <summary>Only agitation follows the effort; aeration is held at its engaged value.</summary>
    AgitationOnly,

    /// <summary>Only aeration follows the effort; agitation is held at its engaged value.</summary>
    AerationOnly,

    /// <summary>Both actuators follow the published kLa gradient-path allocation.</summary>
    KlaPath,
}

/// <summary>
/// Maps the cascade's scalar control effort (0-100 %) onto the agitation and aeration
/// commands. WP6 replaces the linear window split with the published kLa path, while the
/// velocity-form controller above it is unchanged.
/// </summary>
public abstract class CascadeAllocation
{
    /// <summary>The agitation (rpm) and aeration (L/min) command for a control effort.</summary>
    public abstract (double AgitationRpm, double AerationLpm) Allocate(double effortPercent);

    /// <summary>The effort windows, for the workspace's stacked bar. Empty where it has none.</summary>
    public virtual IReadOnlyList<ActuatorWindow> Windows => [];

    /// <summary>The effort that reproduces a given agitation command — for bumpless arming.</summary>
    public double EffortForAgitation(double agitationRpm)
        => Invert(effort => Allocate(effort).AgitationRpm, agitationRpm);

    /// <summary>The effort that reproduces a given aeration command — for bumpless arming.</summary>
    public double EffortForAeration(double aerationLpm)
        => Invert(effort => Allocate(effort).AerationLpm, aerationLpm);

    /// <summary>
    /// Bisects the effort axis for the value that produces <paramref name="target"/> on a
    /// monotonic non-decreasing selector. Used to initialise the loop from the current
    /// commanded actuator so the transfer to automatic has no setpoint jump.
    /// </summary>
    private static double Invert(Func<double, double> selector, double target)
    {
        double lo = 0, hi = 100;
        var low = selector(lo);
        var high = selector(hi);
        if (high <= low)
        {
            // Not driven by effort in this mode (a held actuator): effort cannot reproduce it.
            return 0;
        }

        if (target <= low)
        {
            return lo;
        }

        if (target >= high)
        {
            return hi;
        }

        for (var i = 0; i < 48; i++)
        {
            var mid = (lo + hi) / 2;
            if (selector(mid) < target)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return (lo + hi) / 2;
    }
}

/// <summary>The Phase 1/WP2 linear split: two overlapping effort windows.</summary>
public sealed class WindowAllocation(ActuatorWindow agitation, ActuatorWindow aeration) : CascadeAllocation
{
    public override (double AgitationRpm, double AerationLpm) Allocate(double effortPercent)
        => (agitation.Evaluate(effortPercent), aeration.Evaluate(effortPercent));

    public override IReadOnlyList<ActuatorWindow> Windows => [agitation, aeration];
}

/// <summary>
/// A v.6 fallback mode: one actuator follows the effort across its full band, the other is
/// held at the value it had when the cascade engaged.
/// </summary>
public sealed class SingleActuatorAllocation : CascadeAllocation
{
    private readonly bool _driveAgitation;
    private readonly ActuatorWindow _driven;
    private readonly double _heldAgitationRpm;
    private readonly double _heldAerationLpm;

    private SingleActuatorAllocation(
        bool driveAgitation, ActuatorWindow driven, double heldAgitationRpm, double heldAerationLpm)
    {
        _driveAgitation = driveAgitation;
        _driven = driven;
        _heldAgitationRpm = heldAgitationRpm;
        _heldAerationLpm = heldAerationLpm;
    }

    /// <summary>Agitation follows effort across [minRpm, maxRpm]; aeration is held.</summary>
    public static SingleActuatorAllocation Agitation(double minRpm, double maxRpm, double heldAerationLpm)
        => new(true, new ActuatorWindow(CascadeController.AgitationActuator, minRpm, maxRpm, 0, 100),
            heldAgitationRpm: 0, heldAerationLpm);

    /// <summary>Aeration follows effort across [minLpm, maxLpm]; agitation is held.</summary>
    public static SingleActuatorAllocation Aeration(double minLpm, double maxLpm, double heldAgitationRpm)
        => new(false, new ActuatorWindow(CascadeController.AerationActuator, minLpm, maxLpm, 0, 100),
            heldAgitationRpm, heldAerationLpm: 0);

    public override (double AgitationRpm, double AerationLpm) Allocate(double effortPercent)
    {
        var value = _driven.Evaluate(effortPercent);
        return _driveAgitation ? (value, _heldAerationLpm) : (_heldAgitationRpm, value);
    }

    public override IReadOnlyList<ActuatorWindow> Windows => [_driven];
}

/// <summary>
/// The scientific mode: control effort selects a kLa demand across the published path's
/// range, and the monotonic allocation table gives the (aeration, agitation) that realises
/// it along the paper's gradient/headroom-optimal trajectory.
/// </summary>
public sealed class KlaPathAllocation : CascadeAllocation
{
    private readonly IReadOnlyList<KlaAllocationSample> _table;

    public KlaPathAllocation(IReadOnlyList<KlaAllocationSample> allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        if (allocation.Count < 2)
        {
            throw new ArgumentException("A published kLa path needs at least two allocation samples.", nameof(allocation));
        }

        _table = allocation;
        MinimumKlaPerHour = allocation[0].KlaPerHour;
        MaximumKlaPerHour = allocation[^1].KlaPerHour;
    }

    public double MinimumKlaPerHour { get; }

    public double MaximumKlaPerHour { get; }

    /// <summary>The kLa demand a given effort maps to, for display.</summary>
    public double KlaForEffort(double effortPercent)
    {
        var fraction = Math.Clamp(effortPercent / 100.0, 0.0, 1.0);
        return MinimumKlaPerHour + (fraction * (MaximumKlaPerHour - MinimumKlaPerHour));
    }

    public override (double AgitationRpm, double AerationLpm) Allocate(double effortPercent)
    {
        var sample = Sample(KlaForEffort(effortPercent));
        return (sample.AgitationRpm, sample.AirflowLpm);
    }

    /// <summary>Interpolates the monotonic table, clamping at the ends — as WP5's receipt does.</summary>
    private KlaAllocationSample Sample(double requestedKla)
    {
        if (requestedKla <= _table[0].KlaPerHour)
        {
            return _table[0];
        }

        if (requestedKla >= _table[^1].KlaPerHour)
        {
            return _table[^1];
        }

        var lower = 0;
        var upper = _table.Count - 1;
        while (upper - lower > 1)
        {
            var middle = (lower + upper) / 2;
            if (_table[middle].KlaPerHour <= requestedKla)
            {
                lower = middle;
            }
            else
            {
                upper = middle;
            }
        }

        var left = _table[lower];
        var right = _table[upper];
        var fraction = (requestedKla - left.KlaPerHour) / (right.KlaPerHour - left.KlaPerHour);
        return new KlaAllocationSample(
            requestedKla,
            left.AirflowLpm + (fraction * (right.AirflowLpm - left.AirflowLpm)),
            left.AgitationRpm + (fraction * (right.AgitationRpm - left.AgitationRpm)));
    }
}
