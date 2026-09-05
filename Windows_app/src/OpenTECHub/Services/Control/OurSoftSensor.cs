namespace OpenTECHub.Services.Control;

/// <summary>Why a conditional-OUR sample was or was not accepted this frame.</summary>
public enum OurStatus
{
    /// <summary>Not enough oxygen history yet to know the rate of change.</summary>
    WarmingUp,

    /// <summary>DOT has not yet reached its setpoint band, so the run is still starting up.</summary>
    WaitingForSetpoint,

    /// <summary>No live kLa — no published map is active, or the operating point is off the map.</summary>
    NoKla,

    /// <summary>DOT is outside the stability band around the setpoint.</summary>
    OutOfBand,

    /// <summary>DOT is moving too fast: the quasi-steady assumption does not hold.</summary>
    NotQuasiSteady,

    /// <summary>Quasi-steady and on-band: the estimate is trustworthy.</summary>
    Accepted,
}

/// <summary>
/// Tuning for the conditional-OUR soft sensor. Defaults are the manuscript's
/// (<c>analysis/2_our_soft_sensor</c>), except the rate window, which is causal here.
/// </summary>
public sealed record OurSensorConfig
{
    /// <summary>Dissolved-oxygen saturation C* in mmol L⁻¹ (0.21 at 37 °C in the paper).</summary>
    public double OxygenSaturationMmolPerL { get; init; } = 0.21;

    /// <summary>Half-width of the DOT stability band around the setpoint, in percentage points.</summary>
    public double SetpointTolerancePercentPoints { get; init; } = 5.0;

    /// <summary>Largest |dDOT/dt| still considered quasi-steady, in percentage points per hour.</summary>
    public double RateLimitPointsPerHour { get; init; } = 10.0;

    /// <summary>Half-width of the gate band DOT must first enter before evaluation begins.</summary>
    public double GateTolerancePercentPoints { get; init; } = 2.0;

    /// <summary>
    /// Length of the causal least-squares window for dDOT/dt, in seconds. The paper uses a
    /// centred 1800 s Savitzky-Golay derivative offline; live estimation cannot see the future,
    /// so a shorter causal window trades a little lag for a usable warm-up.
    /// </summary>
    public double RateWindowSeconds { get; init; } = 900.0;

    /// <summary>Minimum samples in the rate window before a rate — and acceptance — is trusted.</summary>
    public int MinimumRateSamples { get; init; } = 3;

    /// <summary>
    /// Largest gap between accepted samples still integrated as one continuous interval, in
    /// seconds. A longer gap breaks the interval, so OUR is never integrated across missing data.
    /// </summary>
    public double MaximumIntegrationGapSeconds { get; init; } = 30.0;
}

/// <summary>One conditional-OUR reading and the running accepted total behind it.</summary>
/// <param name="DotPercent">The dissolved-oxygen tension used, in % air saturation.</param>
/// <param name="DotRatePointsPerHour">Causal dDOT/dt estimate, in percentage points per hour.</param>
/// <param name="KlaPerHour">The kLa used, or null when none is available.</param>
/// <param name="OurMmolPerLPerHour">
/// The unconditional inferred OUR (kLa·C*·(1−DOT/100)), or null without a kLa. This is
/// <b>not</b> shown as the sensor value unless <paramref name="Accepted"/>.
/// </param>
/// <param name="ConditionalOurMmolPerLPerHour">
/// The OUR when accepted, else null — <b>never zero</b>. A refused interval has no value, not a
/// value of zero.
/// </param>
/// <param name="Accepted">True only when the reading is quasi-steady, on-band and has a kLa.</param>
/// <param name="Status">The single reason for the accept/refuse decision.</param>
/// <param name="CumulativeMmolPerL">Trapezoidal ∫OUR dt over accepted intervals only, in mmol L⁻¹.</param>
/// <param name="AcceptedDurationHours">Total accepted time behind the cumulative, in hours.</param>
public readonly record struct OurSample(
    double DotPercent,
    double DotRatePointsPerHour,
    double? KlaPerHour,
    double? OurMmolPerLPerHour,
    double? ConditionalOurMmolPerLPerHour,
    bool Accepted,
    OurStatus Status,
    double CumulativeMmolPerL,
    double AcceptedDurationHours)
{
    public static OurSample Empty { get; } = new(
        DotPercent: double.NaN,
        DotRatePointsPerHour: 0.0,
        KlaPerHour: null,
        OurMmolPerLPerHour: null,
        ConditionalOurMmolPerLPerHour: null,
        Accepted: false,
        Status: OurStatus.WarmingUp,
        CumulativeMmolPerL: 0.0,
        AcceptedDurationHours: 0.0);
}

/// <summary>
/// The conditional oxygen-uptake-rate soft sensor from the manuscript
/// (<c>analysis/2_our_soft_sensor</c>), made causal for live data.
/// </summary>
/// <remarks>
/// <para>
/// At quasi-steady state the dissolved-oxygen balance <c>dC/dt = kLa·(C*−C) − OUR</c> collapses
/// to <c>OUR = kLa·(C*−C)</c>, i.e. <c>OUR = kLa·C*·(1 − DOT/100)</c>. That inversion is only
/// valid when the process is on-band and not moving, so the sensor accepts a sample only when
/// raw DOT sits within a band of the setpoint <b>and</b> the causal |dDOT/dt| is small. Anything
/// else is refused and carries <b>no value</b> — the manuscript never fills a refused interval
/// with zero, and neither does this.
/// </para>
/// <para>
/// The offline reference uses a centred Savitzky-Golay derivative; a live sensor cannot see the
/// future, so the rate is a windowed least-squares slope (<see cref="LeastSquaresRateEstimator"/>),
/// the same estimator the cascade already trusts against the probe's quantisation staircase.
/// </para>
/// <para>Pure and single-threaded: the owning service drives it from the telemetry loop.</para>
/// </remarks>
public sealed class OurSoftSensor
{
    private readonly OurSensorConfig _config;
    private readonly LeastSquaresRateEstimator _rate;

    private bool _gated;
    private double _cumulativeMmolPerL;
    private double _acceptedHours;
    private double? _lastAcceptedTimeSeconds;
    private double? _lastAcceptedOur;

    public OurSoftSensor(OurSensorConfig? config = null)
    {
        _config = config ?? new OurSensorConfig();
        _rate = new LeastSquaresRateEstimator(_config.RateWindowSeconds);
    }

    /// <summary>The running accepted-interval integral of OUR, in mmol L⁻¹.</summary>
    public double CumulativeMmolPerL => _cumulativeMmolPerL;

    /// <summary>Total accepted time behind <see cref="CumulativeMmolPerL"/>, in hours.</summary>
    public double AcceptedDurationHours => _acceptedHours;

    /// <summary>Mean conditional OUR over the accepted intervals, or null before any is accepted.</summary>
    public double? MeanMmolPerLPerHour =>
        _acceptedHours > 0 ? _cumulativeMmolPerL / _acceptedHours : null;

    /// <summary>
    /// Advances the sensor by one telemetry frame.
    /// </summary>
    /// <param name="timeSeconds">Monotonic timestamp in seconds.</param>
    /// <param name="dotPercent">Dissolved-oxygen tension, in % air saturation.</param>
    /// <param name="setpointPercent">The DOT setpoint the run is holding to.</param>
    /// <param name="klaPerHour">
    /// The live kLa at the current operating point (h⁻¹), or null when no published map is active
    /// or the operating point is off the map.
    /// </param>
    public OurSample Update(double timeSeconds, double dotPercent, double setpointPercent, double? klaPerHour)
    {
        _rate.Add(timeSeconds, dotPercent);
        var ratePpH = _rate.Rate * 3600.0;

        if (Math.Abs(dotPercent - setpointPercent) <= _config.GateTolerancePercentPoints)
        {
            _gated = true;
        }

        double? our = klaPerHour is { } kla
            ? InferOur(kla, dotPercent, _config.OxygenSaturationMmolPerL)
            : null;

        var status = Classify(dotPercent, setpointPercent, ratePpH, our.HasValue);
        var accepted = status == OurStatus.Accepted;

        Integrate(timeSeconds, our, accepted);

        return new OurSample(
            DotPercent: dotPercent,
            DotRatePointsPerHour: ratePpH,
            KlaPerHour: klaPerHour,
            OurMmolPerLPerHour: our,
            ConditionalOurMmolPerLPerHour: accepted ? our : null,
            Accepted: accepted,
            Status: status,
            CumulativeMmolPerL: _cumulativeMmolPerL,
            AcceptedDurationHours: _acceptedHours);
    }

    /// <summary>
    /// The quasi-steady OUR inversion: <c>OUR = kLa · C* · (1 − DOT/100)</c>, clamped at zero.
    /// </summary>
    /// <remarks>Pure; shared by the live loop and the manuscript-parity fixture test.</remarks>
    public static double InferOur(double klaPerHour, double dotPercent, double oxygenSaturationMmolPerL)
        => Math.Max(klaPerHour * oxygenSaturationMmolPerL * (1.0 - (dotPercent / 100.0)), 0.0);

    /// <summary>
    /// The manuscript's quasi-steady acceptance test: DOT within the stability band of the
    /// setpoint <b>and</b> |dDOT/dt| within the rate limit. The startup gate is applied separately.
    /// </summary>
    public static bool IsQuasiSteady(
        double dotPercent, double setpointPercent, double dotRatePointsPerHour, OurSensorConfig config)
        => Math.Abs(dotPercent - setpointPercent) <= config.SetpointTolerancePercentPoints
           && Math.Abs(dotRatePointsPerHour) <= config.RateLimitPointsPerHour;

    /// <summary>Clears the gate, rate history and accepted total, e.g. on re-arm after a link loss.</summary>
    public void Reset()
    {
        _rate.Reset();
        _gated = false;
        _cumulativeMmolPerL = 0.0;
        _acceptedHours = 0.0;
        _lastAcceptedTimeSeconds = null;
        _lastAcceptedOur = null;
    }

    private OurStatus Classify(double dot, double setpoint, double ratePpH, bool hasKla)
    {
        if (!_gated)
        {
            return OurStatus.WaitingForSetpoint;
        }

        if (!hasKla)
        {
            return OurStatus.NoKla;
        }

        if (_rate.SampleCount < _config.MinimumRateSamples)
        {
            return OurStatus.WarmingUp;
        }

        if (Math.Abs(dot - setpoint) > _config.SetpointTolerancePercentPoints)
        {
            return OurStatus.OutOfBand;
        }

        if (Math.Abs(ratePpH) > _config.RateLimitPointsPerHour)
        {
            return OurStatus.NotQuasiSteady;
        }

        return OurStatus.Accepted;
    }

    private void Integrate(double timeSeconds, double? our, bool accepted)
    {
        if (!accepted || our is not { } value)
        {
            // A refused frame breaks the interval; OUR is never integrated across a gap or a
            // rejected sample, which is what keeps the accepted total honest.
            _lastAcceptedTimeSeconds = null;
            _lastAcceptedOur = null;
            return;
        }

        if (_lastAcceptedTimeSeconds is { } lastTime &&
            _lastAcceptedOur is { } lastOur &&
            timeSeconds > lastTime &&
            timeSeconds - lastTime <= _config.MaximumIntegrationGapSeconds)
        {
            var dtHours = (timeSeconds - lastTime) / 3600.0;
            _cumulativeMmolPerL += 0.5 * (lastOur + value) * dtHours;
            _acceptedHours += dtHours;
        }

        _lastAcceptedTimeSeconds = timeSeconds;
        _lastAcceptedOur = value;
    }
}
