namespace TecnalHub.Services.Control;

/// <summary>One point on the live cascade trend: PV, setpoint, kLa demand and effort over time.</summary>
public readonly record struct CascadeSample(
    double ElapsedMinutes,
    double Pv,
    double Setpoint,
    double Output,
    double Kla,
    bool HasKla);

/// <summary>An immutable, ScottPlot-ready copy of the trend, safe to hand to the UI thread.</summary>
/// <remarks>
/// The kLa series is sparse — it exists only while engaged on the path — so it carries its
/// own x-coordinates (<see cref="KlaMinutes"/>) rather than being padded onto the main axis.
/// </remarks>
public sealed record CascadeTrendSnapshot(
    double[] Minutes,
    double[] Pv,
    double[] Setpoint,
    double[] Output,
    double[] KlaMinutes,
    double[] Kla)
{
    public int Count => Minutes.Length;

    public int KlaCount => Kla.Length;

    public static CascadeTrendSnapshot Empty { get; } = new([], [], [], [], [], []);
}

/// <summary>
/// A bounded ring of cascade samples for the tuning chart.
/// </summary>
/// <remarks>
/// Owned by <see cref="CascadeService"/> and filled on every armed step, so the chart shows
/// the loop tracking whether it is computing and whether it is engaged. Fixed capacity,
/// allocated once — a long run must not grow memory, the same discipline the telemetry ring
/// buffers keep.
/// </remarks>
public sealed class CascadeTrend
{
    /// <summary>Thirty minutes at the 2 s field cadence.</summary>
    private const int Capacity = 900;

    private readonly Lock _gate = new();
    private readonly Queue<CascadeSample> _samples = new(Capacity);
    private double _startMinutes = double.NaN;

    /// <summary>Appends a sample, timestamped in minutes relative to the first one.</summary>
    public void Add(double pv, double setpoint, double? kla, double output, double nowMinutes)
    {
        lock (_gate)
        {
            if (double.IsNaN(_startMinutes))
            {
                _startMinutes = nowMinutes;
            }

            _samples.Enqueue(new CascadeSample(
                nowMinutes - _startMinutes, pv, setpoint, output, kla ?? 0.0, kla.HasValue));

            while (_samples.Count > Capacity)
            {
                _samples.Dequeue();
            }
        }
    }

    /// <summary>Drops every sample and re-bases the clock, on arm or disarm.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _samples.Clear();
            _startMinutes = double.NaN;
        }
    }

    /// <summary>A copy split into the parallel arrays ScottPlot consumes.</summary>
    public CascadeTrendSnapshot Snapshot()
    {
        lock (_gate)
        {
            var count = _samples.Count;
            if (count == 0)
            {
                return CascadeTrendSnapshot.Empty;
            }

            var minutes = new double[count];
            var pv = new double[count];
            var setpoint = new double[count];
            var output = new double[count];
            var klaMinutes = new List<double>(count);
            var kla = new List<double>(count);
            var i = 0;
            foreach (var sample in _samples)
            {
                minutes[i] = sample.ElapsedMinutes;
                pv[i] = sample.Pv;
                setpoint[i] = sample.Setpoint;
                output[i] = sample.Output;
                if (sample.HasKla)
                {
                    klaMinutes.Add(sample.ElapsedMinutes);
                    kla.Add(sample.Kla);
                }

                i++;
            }

            return new CascadeTrendSnapshot(minutes, pv, setpoint, output, [.. klaMinutes], [.. kla]);
        }
    }
}
