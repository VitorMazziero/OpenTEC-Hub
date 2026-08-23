using TecnalHub.Protocol;

namespace TecnalHub.Services.Telemetry;

/// <summary>
/// A channel that can be plotted.
/// </summary>
/// <remarks>
/// Mirrors the set v.6's graphs page offered, restricted to channels this app can
/// actually produce. OUR is deliberately absent: it arrives with the soft sensor in
/// Phase 2, and offering an always-empty chart would be worse than not offering it.
/// </remarks>
public enum TelemetryChannel
{
    Temperature,
    Oxygen,
    PH,
    Flow,
    Pressure,
    MotorRpm,
    Antifoam,
    Distance,
    Biomass,
    PumpFlow,
    PumpVolume,
    CascadeEffort,
    CascadePredictedO2,
    CascadeRateSetpoint,
    CascadeRateMeasured,
    CascadeKlaDemand,
}

/// <summary>Downsampled series ready for a chart.</summary>
/// <param name="Minutes">Elapsed process time per point.</param>
/// <param name="Values">The reading at each point.</param>
public readonly record struct ChannelSeries(double[] Minutes, double[] Values)
{
    public static ChannelSeries Empty { get; } = new([], []);

    public int Count => Minutes.Length;
}

/// <summary>Accumulates telemetry for the charts and the session log.</summary>
public interface ITelemetryHistory
{
    /// <summary>Samples currently retained.</summary>
    int Count { get; }

    /// <summary>Process time of the newest sample, in minutes.</summary>
    double LatestMinutes { get; }

    /// <summary>Records a frame.</summary>
    void Add(SensorSnapshot snapshot, double commandedRpm);

    /// <summary>Records cascade control terms for the latest frame.</summary>
    void RecordCascade(double effort, double predictedO2, double rateSetpoint, double rateMeasured, double? klaDemand);

    /// <summary>Discards everything, e.g. when a new run starts.</summary>
    void Clear();

    /// <summary>
    /// A channel's series, restricted to the last <paramref name="window"/> and
    /// downsampled to at most <paramref name="maxPoints"/>.
    /// </summary>
    ChannelSeries GetSeries(TelemetryChannel channel, TimeSpan? window, int maxPoints);
}

/// <summary>
/// Fixed-capacity ring buffer of telemetry.
/// </summary>
/// <remarks>
/// <para>
/// <b>A ring buffer, not a growing list.</b> The roadmap's non-functional target is
/// flat memory across a 24 h run, and a cultivation can run far longer. At the field
/// <c>dataDelay</c> of 2 s the default capacity holds about 48 hours; older samples
/// are overwritten rather than accumulated. All channels together cost roughly 8 MB.
/// </para>
/// <para>
/// Reads are downsampled before they reach a chart. No display has 86,000 horizontal
/// pixels, and handing a plotting library every point costs time and shows nothing
/// extra.
/// </para>
/// </remarks>
public sealed class TelemetryHistory(int capacity = 86_400) : ITelemetryHistory
{
    private static readonly int ChannelCount = Enum.GetValues<TelemetryChannel>().Length;

    private readonly Lock _gate = new();
    private readonly double[] _minutes = new double[capacity];

    /// <summary>One row per channel, indexed by the enum value.</summary>
    private readonly double[][] _series =
        [.. Enumerable.Range(0, ChannelCount).Select(_ => new double[capacity])];

    private int _head;   // next write index
    private int _count;

    public int Count
    {
        get { lock (_gate) { return _count; } }
    }

    public double LatestMinutes
    {
        get
        {
            lock (_gate)
            {
                return _count == 0 ? 0 : _minutes[(_head - 1 + capacity) % capacity];
            }
        }
    }

    public void Add(SensorSnapshot snapshot, double commandedRpm)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            var i = _head;
            _minutes[i] = snapshot.TimeMinutes;

            // Sentinels are stored as NaN so a chart shows a gap rather than a line
            // dropping to -1, which would read as a real measurement.
            Set(TelemetryChannel.Temperature, i, snapshot.Temperature);
            Set(TelemetryChannel.Oxygen, i, snapshot.OxygenCalibrated);
            Set(TelemetryChannel.PH, i, snapshot.PHCalibrated);
            Set(TelemetryChannel.Flow, i, snapshot.FlowRate);
            Set(TelemetryChannel.Pressure, i, snapshot.Pressure);
            Set(TelemetryChannel.Antifoam, i, snapshot.Antifoam);
            Set(TelemetryChannel.Distance, i, snapshot.Distance);
            Set(TelemetryChannel.Biomass, i, snapshot.BiomassAbsorbance);
            Set(TelemetryChannel.PumpFlow, i, snapshot.PumpFlow);
            Set(TelemetryChannel.PumpVolume, i, snapshot.PumpVolume);

            // Default cascade control channels to NaN until written
            _series[(int)TelemetryChannel.CascadeEffort][i] = double.NaN;
            _series[(int)TelemetryChannel.CascadePredictedO2][i] = double.NaN;
            _series[(int)TelemetryChannel.CascadeRateSetpoint][i] = double.NaN;
            _series[(int)TelemetryChannel.CascadeRateMeasured][i] = double.NaN;
            _series[(int)TelemetryChannel.CascadeKlaDemand][i] = double.NaN;

            // Agitation has no feedback path, so what is charted is what was
            // commanded. Zero means "not commanded", not "measured zero".
            _series[(int)TelemetryChannel.MotorRpm][i] =
                commandedRpm > 0 ? commandedRpm : double.NaN;

            _head = (_head + 1) % capacity;
            if (_count < capacity)
            {
                _count++;
            }
        }
    }

    public void RecordCascade(double effort, double predictedO2, double rateSetpoint, double rateMeasured, double? klaDemand)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return;
            }

            var i = (_head - 1 + capacity) % capacity;
            _series[(int)TelemetryChannel.CascadeEffort][i] = double.IsFinite(effort) ? effort : double.NaN;
            _series[(int)TelemetryChannel.CascadePredictedO2][i] = double.IsFinite(predictedO2) ? predictedO2 : double.NaN;
            _series[(int)TelemetryChannel.CascadeRateSetpoint][i] = double.IsFinite(rateSetpoint) ? rateSetpoint : double.NaN;
            _series[(int)TelemetryChannel.CascadeRateMeasured][i] = double.IsFinite(rateMeasured) ? rateMeasured : double.NaN;
            _series[(int)TelemetryChannel.CascadeKlaDemand][i] = klaDemand.HasValue && double.IsFinite(klaDemand.Value) ? klaDemand.Value : double.NaN;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _head = 0;
            _count = 0;
        }
    }

    public ChannelSeries GetSeries(TelemetryChannel channel, TimeSpan? window, int maxPoints)
    {
        if (maxPoints < 2)
        {
            maxPoints = 2;
        }

        lock (_gate)
        {
            if (_count == 0)
            {
                return ChannelSeries.Empty;
            }

            var source = _series[(int)channel];
            var oldest = (_head - _count + capacity) % capacity;
            var latest = _minutes[(_head - 1 + capacity) % capacity];

            // Walk forward to the first sample inside the window.
            var start = 0;
            if (window is { } span)
            {
                var cutoff = latest - span.TotalMinutes;
                while (start < _count && _minutes[(oldest + start) % capacity] < cutoff)
                {
                    start++;
                }
            }

            var available = _count - start;
            if (available <= 0)
            {
                return ChannelSeries.Empty;
            }

            // Stride sampling. Cheap, and for a slowly-varying process it loses
            // nothing a chart could have shown anyway - a spike narrower than one
            // screen pixel is not information the operator can act on.
            var stride = Math.Max(1, (int)Math.Ceiling(available / (double)maxPoints));
            var length = (available + stride - 1) / stride;

            var minutes = new double[length];
            var values = new double[length];

            for (var i = 0; i < length; i++)
            {
                var index = (oldest + start + (i * stride)) % capacity;
                minutes[i] = _minutes[index];
                values[i] = source[index];
            }

            return new ChannelSeries(minutes, values);
        }
    }

    private void Set(TelemetryChannel channel, int index, double value)
        => _series[(int)channel][index] =
            value <= SensorReadings.NotReceived ? double.NaN : value;
}
