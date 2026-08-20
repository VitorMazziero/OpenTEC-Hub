namespace TecnalHub.Services.Control;

/// <summary>
/// Estimates the rate of change of a signal by a least-squares straight-line fit over
/// a sliding time window.
/// </summary>
/// <remarks>
/// <para>
/// The polarographic oxygen probe reports in coarse quantisation steps - the reading
/// holds flat and then jumps a whole count, tracing a staircase rather than a smooth
/// curve. A two-point endpoint difference <c>(y[k] - y[k-1]) / dt</c> reads that
/// staircase as alternating zero and enormous slopes, which is useless to a derivative
/// term or a prediction horizon.
/// </para>
/// <para>
/// A least-squares slope over many samples averages the staircase back into the smooth
/// trend it approximates. This is the rate the cascade's prediction horizon and
/// derivative action both consume. See <c>docs/ROADMAP.md</c> Phase 2, "rate estimation
/// by least squares over the window rather than endpoint difference".
/// </para>
/// <para>
/// Not thread-safe. The controller that owns it drives it from a single loop.
/// </para>
/// </remarks>
public sealed class LeastSquaresRateEstimator
{
    private readonly double _windowSeconds;
    private readonly Queue<Sample> _samples = new();

    private readonly record struct Sample(double TimeSeconds, double Value);

    /// <param name="windowSeconds">
    /// Length of the fit window. Longer rejects more quantisation noise but lags a
    /// genuine change; the manuscript's estimator sits in the tens of seconds.
    /// </param>
    public LeastSquaresRateEstimator(double windowSeconds)
    {
        if (!double.IsFinite(windowSeconds) || windowSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowSeconds), windowSeconds, "Window must be a positive number of seconds.");
        }

        _windowSeconds = windowSeconds;
    }

    /// <summary>Samples currently inside the window.</summary>
    public int SampleCount => _samples.Count;

    /// <summary>Fit window length in seconds.</summary>
    public double WindowSeconds => _windowSeconds;

    /// <summary>
    /// Adds a sample at an absolute (monotonically increasing) time and drops anything
    /// that has aged out of the window.
    /// </summary>
    /// <param name="timeSeconds">
    /// Monotonic timestamp in seconds. The caller supplies its own clock - usually the
    /// controller's accumulated loop time - so estimation stays deterministic in tests
    /// and independent of wall-clock jitter.
    /// </param>
    /// <param name="value">The measured value at that time.</param>
    public void Add(double timeSeconds, double value)
    {
        if (!double.IsFinite(timeSeconds) || !double.IsFinite(value))
        {
            // A sentinel or a parse gap must not enter the fit; skipping it leaves the
            // last good slope in place rather than poisoning it with a spurious point.
            return;
        }

        _samples.Enqueue(new Sample(timeSeconds, value));

        var oldest = timeSeconds - _windowSeconds;
        while (_samples.Count > 0 && _samples.Peek().TimeSeconds < oldest)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>
    /// Slope of the best-fit line through the windowed samples, in units per second.
    /// </summary>
    /// <remarks>
    /// Zero until at least two samples exist, and zero when every sample shares a
    /// timestamp (no time base to divide by) - both cases mean "no rate is knowable
    /// yet", which is the safe answer for a prediction term to receive.
    /// </remarks>
    public double Rate
    {
        get
        {
            var n = _samples.Count;
            if (n < 2)
            {
                return 0.0;
            }

            // Shift time to the first sample so the sums stay small and well-conditioned
            // even after hours of run time.
            var t0 = _samples.Peek().TimeSeconds;

            double sumT = 0, sumY = 0, sumTt = 0, sumTy = 0;
            foreach (var s in _samples)
            {
                var t = s.TimeSeconds - t0;
                sumT += t;
                sumY += s.Value;
                sumTt += t * t;
                sumTy += t * s.Value;
            }

            var denominator = (n * sumTt) - (sumT * sumT);
            if (Math.Abs(denominator) < 1e-12)
            {
                return 0.0;
            }

            return ((n * sumTy) - (sumT * sumY)) / denominator;
        }
    }

    /// <summary>Clears all history, e.g. when the loop is re-armed after a disconnect.</summary>
    public void Reset() => _samples.Clear();
}
