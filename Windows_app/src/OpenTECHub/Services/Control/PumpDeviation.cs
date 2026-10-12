namespace OpenTECHub.Services.Control;

/// <summary>
/// Delivered minus planned volume at the current time, with an alert when it stays above a
/// relative tolerance for longer than a hold time (plan 2026-10-11, Phase 7.3).
/// </summary>
/// <remarks>
/// A single sample above the tolerance is normal in pulse dosing: right before a pulse the
/// delivered staircase sits a whole pulse below the plan. Only a deviation that persists for
/// more than <see cref="HoldMinutes"/> (two pulse periods, or two minutes for profiles) alerts.
/// </remarks>
public sealed class PumpDeviationTracker(double tolerance = 0.05)
{
    private double? _aboveSinceMin;

    public double Tolerance { get; } = tolerance;

    /// <summary>How long the deviation must persist before <see cref="IsAlert"/>, minutes.</summary>
    public double HoldMinutes { get; set; } = 2.0;

    /// <summary>Last delivered − planned, mL; null before the first sample.</summary>
    public double? DeviationMl { get; private set; }

    /// <summary>Last deviation relative to the planned volume; null when the plan is still at zero.</summary>
    public double? Relative { get; private set; }

    public bool IsAlert { get; private set; }

    /// <summary>Records one sample on the plan's clock.</summary>
    public void Update(double minutes, double plannedMl, double deliveredMl)
    {
        DeviationMl = deliveredMl - plannedMl;
        Relative = plannedMl > 1e-9 ? DeviationMl / plannedMl : null;
        if (Relative is { } r && Math.Abs(r) > Tolerance)
        {
            _aboveSinceMin ??= minutes;
            IsAlert = minutes - _aboveSinceMin.Value > HoldMinutes;
        }
        else
        {
            _aboveSinceMin = null;
            IsAlert = false;
        }
    }

    public void Reset()
    {
        _aboveSinceMin = null;
        DeviationMl = null;
        Relative = null;
        IsAlert = false;
    }
}

/// <summary>Live samples on a plan's clock (minutes), capped so the chart stays cheap.</summary>
public sealed class PumpLiveSeries(int capacity = 4000)
{
    private readonly List<double> _minutes = [];
    private readonly List<double> _flow = [];
    private readonly List<double> _volume = [];

    public IReadOnlyList<double> Minutes => _minutes;
    public IReadOnlyList<double> Flow => _flow;
    public IReadOnlyList<double> Volume => _volume;
    public int Count => _minutes.Count;

    /// <summary>Adds a sample; a time that goes backwards means a new run and clears the series first.</summary>
    public void Add(double minutes, double flow, double volume)
    {
        if (!double.IsFinite(minutes) || !double.IsFinite(flow) || !double.IsFinite(volume))
        {
            return;
        }

        if (_minutes.Count > 0 && minutes < _minutes[^1] - 1e-6)
        {
            Clear();
        }

        if (_minutes.Count >= capacity)
        {
            // Drop every other sample: keeps the whole run visible at half the resolution.
            for (var i = _minutes.Count - 1; i > 0; i -= 2)
            {
                _minutes.RemoveAt(i - 1);
                _flow.RemoveAt(i - 1);
                _volume.RemoveAt(i - 1);
            }
        }

        _minutes.Add(minutes);
        _flow.Add(flow);
        _volume.Add(volume);
    }

    public void Clear()
    {
        _minutes.Clear();
        _flow.Clear();
        _volume.Clear();
    }

    /// <summary>Immutable copies for a binding (the chart redraws when the reference changes).</summary>
    public (double[] Minutes, double[] Flow, double[] Volume) Snapshot()
        => ([.. _minutes], [.. _flow], [.. _volume]);
}
