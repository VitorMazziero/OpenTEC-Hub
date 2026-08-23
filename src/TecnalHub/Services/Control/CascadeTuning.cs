namespace TecnalHub.Services.Control;

/// <summary>
/// The tunable parameters of the oxygen cascade's dual-loop PID.
/// </summary>
public sealed record CascadeTuning
{
    /// <summary>Outer loop proportional gain, rate setpoint per percent-DO of error (s⁻¹).</summary>
    public double KDot { get; init; } = 0.07;

    /// <summary>Inner loop proportional gain.</summary>
    public double Kp { get; init; } = 0.065;

    /// <summary>Inner loop integral gain.</summary>
    public double Ki { get; init; } = 0.001;

    /// <summary>Inner loop derivative gain.</summary>
    public double Kd { get; init; } = 0.50;

    /// <summary>Prediction horizon in seconds for probe dead-time compensation.</summary>
    public double PredictionHorizonSeconds { get; init; } = 60.0;

    /// <summary>Alias for <see cref="PredictionHorizonSeconds"/>.</summary>
    public double TPred
    {
        get => PredictionHorizonSeconds;
        init => PredictionHorizonSeconds = value;
    }

    /// <summary>Derivative low-pass filter time constant in seconds.</summary>
    public double TauD { get; init; } = 20.0;

    /// <summary>Lower saturation of the integral contribution (%).</summary>
    public double IntegralMin { get; init; } = -30.0;

    /// <summary>Alias for <see cref="IntegralMin"/>.</summary>
    public double IMin
    {
        get => IntegralMin;
        init => IntegralMin = value;
    }

    /// <summary>Upper saturation of the integral contribution (%).</summary>
    public double IntegralMax { get; init; } = 30.0;

    /// <summary>Alias for <see cref="IntegralMax"/>.</summary>
    public double IMax
    {
        get => IntegralMax;
        init => IntegralMax = value;
    }

    /// <summary>Sliding time window for the rate error integrator, in seconds.</summary>
    public int MWindow { get; init; } = 120;

    /// <summary>Sample count for the inner rate estimate (derivative term).</summary>
    public int JAvg { get; init; } = 9;

    /// <summary>Sample count for the predictor rate estimate.</summary>
    public int NPred { get; init; } = 7;

    /// <summary>Slope calculation method for rate estimation.</summary>
    public CascadeSlopeMethod SlopeMethod { get; init; } = CascadeSlopeMethod.LeastSquares;

    /// <summary>Aeration gain multiplier for gain scheduling in dual cascade mode.</summary>
    public double FatorGanhoAeracao { get; init; } = 1.43;

    /// <summary>Whether gain scheduling is enabled in dual cascade mode.</summary>
    public bool HabilitarGainScheduling { get; init; } = true;

    /// <summary>Lowest control effort the loop may command, in percent.</summary>
    public double OutputMin { get; init; }

    /// <summary>Highest control effort the loop may command, in percent.</summary>
    public double OutputMax { get; init; } = 100.0;

    /// <summary>Legacy rate window length (seconds), for backward compatibility.</summary>
    public double RateWindowSeconds { get; init; } = 25.0;

    /// <summary>Nominal loop period in seconds.</summary>
    public double IntervalSeconds { get; init; } = 3.0;

    /// <summary>
    /// Returns the reasons this tuning is unusable, or an empty list when it is valid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();

        if (!IsFinite(KDot, Kp, Ki, Kd, IntegralMin, IntegralMax, OutputMin, OutputMax,
                PredictionHorizonSeconds, TauD, IntervalSeconds, FatorGanhoAeracao))
        {
            issues.Add("All tuning parameters must be finite numbers.");
            return issues;
        }

        if (KDot < 0 || Kp < 0 || Ki < 0 || Kd < 0)
        {
            issues.Add("Gains KDot, Kp, Ki and Kd must be non-negative.");
        }

        if (IntegralMin >= IntegralMax)
        {
            issues.Add("IntegralMin must be below IntegralMax.");
        }

        if (OutputMin < 0 || OutputMax > 100 || OutputMin >= OutputMax)
        {
            issues.Add("Output window must satisfy 0 <= OutputMin < OutputMax <= 100.");
        }

        if (PredictionHorizonSeconds < 0)
        {
            issues.Add("Prediction horizon cannot be negative.");
        }

        if (TauD <= 0)
        {
            issues.Add("Derivative filter time constant TauD must be positive.");
        }

        if (MWindow <= 0)
        {
            issues.Add("Integrator window MWindow must be positive.");
        }

        if (JAvg < 1 || NPred < 1)
        {
            issues.Add("Sample windows JAvg and NPred must be at least 1.");
        }

        if (IntervalSeconds < 0.1 || IntervalSeconds > 60)
        {
            issues.Add("Loop interval must be between 0.1 and 60 seconds.");
        }

        return issues;
    }

    /// <summary>True when <see cref="Validate"/> finds nothing wrong.</summary>
    public bool IsValid => Validate().Count == 0;

    private static bool IsFinite(params double[] values)
    {
        foreach (var v in values)
        {
            if (!double.IsFinite(v))
            {
                return false;
            }
        }

        return true;
    }
}
