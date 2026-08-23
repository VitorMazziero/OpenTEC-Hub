namespace TecnalHub.Services.Control;

/// <summary>
/// The tunable parameters of the oxygen cascade's velocity-form PID.
/// </summary>
/// <remarks>
/// <para>
/// Every field here maps to a control on <c>Controle → Controle de oxigênio</c>
/// (<c>docs/UI_DESIGN.md</c> section 5.2). The form deliberately exposes the corrected
/// design rather than hiding it: <see cref="IntegralMin"/>/<see cref="IntegralMax"/>
/// exist because v.6 wound its integral up over long transients, and
/// <see cref="PredictionHorizonSeconds"/> exists because the polarographic probe has
/// 20-40 s of dead time.
/// </para>
/// <para>
/// The defaults are a coherent starting point for the localhost simulator, <b>not</b> a
/// validated field tuning. Real gains come from a bioreactor run; anything tuned against
/// the simulator's placeholder kLa is provisional (<c>docs/ROADMAP.md</c> Phase 2).
/// </para>
/// </remarks>
public sealed record CascadeTuning
{
    /// <summary>Proportional gain, effort-percent per percent-DO of error.</summary>
    /// <remarks>
    /// Deliberately small. The prediction folds into the error, so the proportional gain
    /// also sets the loop's effective derivative action (<c>Kp · horizon</c>); a large Kp
    /// on a dead-time-dominant oxygen process therefore injects a huge derivative and
    /// drives a limit cycle. The prediction horizon, not a high proportional gain,
    /// supplies the anticipation.
    /// </remarks>
    public double Kp { get; init; } = 0.25;

    /// <summary>Integral gain, effort-percent per percent-DO-second.</summary>
    /// <remarks>
    /// The integral does the steady-state work that the small proportional gain leaves;
    /// it is kept modest because the process itself is slow (tens of seconds of lag).
    /// </remarks>
    public double Ki { get; init; } = 0.02;

    /// <summary>
    /// Explicit derivative gain, effort-percent per percent-DO-per-second.
    /// </summary>
    /// <remarks>
    /// Zero by default: the prediction horizon already provides the lead compensation, so
    /// a separate derivative term is redundant and, stacked on top, destabilising. It is
    /// still exposed for manual tuning.
    /// </remarks>
    public double Kd { get; init; }

    /// <summary>Lower saturation of the integral contribution.</summary>
    public double IntegralMin { get; init; }

    /// <summary>Upper saturation of the integral contribution.</summary>
    public double IntegralMax { get; init; } = 100.0;

    /// <summary>Lowest control effort the loop may command, in percent.</summary>
    public double OutputMin { get; init; }

    /// <summary>Highest control effort the loop may command, in percent.</summary>
    public double OutputMax { get; init; } = 100.0;

    /// <summary>
    /// How far ahead the loop predicts the measurement to compensate probe dead time.
    /// </summary>
    /// <remarks>
    /// <c>DOT_pred = DOT + (dDOT/dt) · t_pred</c>. The UI offers 30-60 s; a horizon near
    /// the probe's own dead time is the point of the term.
    /// </remarks>
    public double PredictionHorizonSeconds { get; init; } = 25.0;

    /// <summary>Length of the least-squares window that estimates the measurement rate.</summary>
    public double RateWindowSeconds { get; init; } = 25.0;

    /// <summary>
    /// Nominal loop period. The controller integrates against the real elapsed time it is
    /// handed, so this is the expected cadence (and the value the UI edits), not a divisor
    /// baked into the math.
    /// </summary>
    public double IntervalSeconds { get; init; } = 2.0;

    /// <summary>
    /// Returns the reasons this tuning is unusable, or an empty list when it is valid.
    /// </summary>
    /// <remarks>
    /// Mirrors the validation listed for the cascade form in
    /// <c>docs/UI_DESIGN.md</c> section 9. Messages are English because they are
    /// developer- and log-facing; the Phase 2 tuning UI localises its own field errors.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();

        if (!IsFinite(Kp, Ki, Kd, IntegralMin, IntegralMax, OutputMin, OutputMax,
                PredictionHorizonSeconds, RateWindowSeconds, IntervalSeconds))
        {
            issues.Add("All tuning parameters must be finite numbers.");
            return issues;
        }

        if (Kp < 0 || Ki < 0 || Kd < 0)
        {
            issues.Add("Gains Kp, Ki and Kd must be non-negative.");
        }

        if (IntegralMin >= IntegralMax)
        {
            issues.Add("IntegralMin must be below IntegralMax.");
        }

        // Output is a control effort in percent, so its window lives inside 0..100.
        if (OutputMin < 0 || OutputMax > 100 || OutputMin >= OutputMax)
        {
            issues.Add("Output window must satisfy 0 <= OutputMin < OutputMax <= 100.");
        }

        if (PredictionHorizonSeconds < 0)
        {
            issues.Add("Prediction horizon cannot be negative.");
        }

        if (RateWindowSeconds <= 0)
        {
            issues.Add("Rate-estimation window must be positive.");
        }

        // The PID interval bound is a real firmware constraint: below 0.1 s the shared
        // UART cannot keep up, and above 60 s the loop is no longer controlling.
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
