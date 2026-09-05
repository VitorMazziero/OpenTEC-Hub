namespace OpenTECHub.Protocol;

/// <summary>
/// Tuning for one <see cref="SpikeFilter"/> channel.
/// </summary>
/// <param name="AbsoluteThreshold">
/// Accept a reading immediately when it is within this distance of the last good
/// value.
/// </param>
/// <param name="FollowTolerance">
/// Width of the candidate window. A reading further than this from the current
/// candidate restarts candidacy instead of confirming it.
/// </param>
/// <param name="ConfirmRuns">
/// Consecutive confirmations required before a candidate is accepted as a real step
/// change rather than a spike.
/// </param>
/// <remarks>
/// <b>Units are raw ADC counts, not engineering units.</b> Re-calibrating a channel
/// therefore changes what this filter considers a spike. That coupling is inherited
/// from v.6 and is logged as a known defect - see
/// <c>docs/MIGRATION.md</c> section 3, item 4.
/// </remarks>
public readonly record struct SpikeFilterConfig(
    double AbsoluteThreshold,
    double FollowTolerance,
    int ConfirmRuns)
{
    /// <summary>pH channel defaults, as used in the field by v.6.</summary>
    public static SpikeFilterConfig ForPH() => new(500.0, 200.0, 3);

    /// <summary>Oxygen channel defaults, as used in the field by v.6.</summary>
    public static SpikeFilterConfig ForOxygen() => new(150.0, 50.0, 3);
}

/// <summary>
/// Stateful single-channel spike / step-change filter.
/// </summary>
/// <remarks>
/// <para>
/// Rejects one-sample outliers while still following genuine step changes, by
/// requiring a departure to repeat <see cref="SpikeFilterConfig.ConfirmRuns"/> times
/// before it is believed. Until then the previous good value is held.
/// </para>
/// <para>
/// Direct port of <c>SpikeFilter</c> in v.6 <c>communication/data_parser.py</c>;
/// behaviour must stay identical. See <c>docs/PROTOCOL.md</c> section 2.1.
/// </para>
/// </remarks>
public sealed class SpikeFilter(SpikeFilterConfig config)
{
    private SpikeFilterConfig _config = config;

    private double? _lastGood;
    private double? _candidate;
    private int _candidateRuns;

    /// <summary>The most recent accepted value, or null before the first reading.</summary>
    public double? LastGood => _lastGood;

    /// <summary>
    /// Feeds a new raw reading and returns the value that should be used: either
    /// the new reading (accepted) or the previous good value (held).
    /// </summary>
    /// <returns>
    /// Null only when <paramref name="rawValue"/> is null and no good value exists yet.
    /// </returns>
    public double? Update(double? rawValue)
    {
        if (rawValue is not { } value)
        {
            return _lastGood;
        }

        // Bootstrap: the very first real reading is accepted unconditionally.
        if (_lastGood is not { } lastGood)
        {
            _lastGood = value;
            _candidate = null;
            _candidateRuns = 0;
            return value;
        }

        // Normal evolution: close enough to the last good value, accept it.
        if (Math.Abs(value - lastGood) <= _config.AbsoluteThreshold)
        {
            _lastGood = value;
            _candidate = null;
            _candidateRuns = 0;
            return value;
        }

        // --- Spike region: the reading is far from the last good value. ---
        if (_candidate is not { } candidate ||
            Math.Abs(value - candidate) > _config.FollowTolerance)
        {
            // No candidate yet, or this reading disagrees with the running
            // candidate: start candidacy over and keep holding.
            _candidate = value;
            _candidateRuns = 1;
            return lastGood;
        }

        _candidateRuns++;
        if (_candidateRuns >= _config.ConfirmRuns)
        {
            // The departure repeated often enough: a real step change.
            _lastGood = value;
            _candidate = null;
            _candidateRuns = 0;
            return value;
        }

        return lastGood;
    }

    /// <summary>Clears all state, as if no reading had ever been seen.</summary>
    public void Reset()
    {
        _lastGood = null;
        _candidate = null;
        _candidateRuns = 0;
    }

    /// <summary>
    /// Applies new tuning. Also resets the filter, because thresholds and retained
    /// state are only meaningful together.
    /// </summary>
    public void Reconfigure(SpikeFilterConfig config)
    {
        _config = config;
        Reset();
    }
}
