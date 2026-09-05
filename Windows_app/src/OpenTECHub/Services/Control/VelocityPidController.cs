namespace OpenTECHub.Services.Control;

/// <summary>
/// The oxygen cascade's inner control law: a velocity-form PID with dead-time
/// prediction and explicit anti-windup.
/// </summary>
/// <remarks>
/// <para>
/// This is the corrected controller carried over from the ReceitasOpenTEC design, not
/// from v.6. It fixes the three structural defects recorded in
/// <c>docs/ROADMAP.md</c> Phase 2:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Velocity form.</b> The law emits an increment and the output accumulates it:
/// <c>Saída[i] = Saída[i-1] + dSaída[i]</c>. At setpoint the increment is zero, so the
/// actuator <b>holds its last level</b> instead of collapsing to zero the way v.6's
/// positional PID did - which matters because the organism keeps consuming oxygen even
/// when the error is momentarily zero.
/// </item>
/// <item>
/// <b>Anti-windup.</b> Because the output is itself the integrator and is saturated to
/// the actuator window every step, there is no unbounded integral state to wind up the
/// way v.6's did over a long transient - when the error reverses, the proportional term
/// pulls the output straight off the rail. The reported integral term is separately
/// bounded by <see cref="CascadeTuning.IntegralMin"/>/<see cref="CascadeTuning.IntegralMax"/>
/// and held while the output is railed, so the operator can cap and read integral
/// authority explicitly.
/// </item>
/// <item>
/// <b>Prediction horizon.</b> The error is formed against a predicted measurement,
/// <c>DOT_pred = DOT + rate · horizon</c>, so the loop reacts to where dissolved oxygen
/// is heading rather than to the 20-40 s stale value the probe reports.
/// </item>
/// </list>
/// <para>
/// The measurement rate comes from a least-squares fit, not an endpoint difference, so
/// the probe's quantisation staircase does not reach the derivative or the prediction
/// (<see cref="LeastSquaresRateEstimator"/>).
/// </para>
/// <para>
/// Pure control math: no telemetry, no wire, no UI. It is driven a step at a time and is
/// unit-tested against a simulated first-order plant with dead time
/// (<c>docs/ARCHITECTURE.md</c> section 5).
/// </para>
/// </remarks>
public sealed class VelocityPidController
{
    /// <summary>
    /// Guards the integrator against a pathological time step - a debugger break or a
    /// machine sleep - being integrated into a wild jump. Mirrors the simulator's clamp.
    /// </summary>
    private const double MaxStepSeconds = 10.0;

    private LeastSquaresRateEstimator _rate;

    private double _elapsedSeconds;
    private double _output;
    private double _integral;
    private double _previousError;
    private double _previousRate;
    private bool _hasPrevious;

    public VelocityPidController(CascadeTuning tuning, double setpoint = 0.0)
    {
        Tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        Setpoint = setpoint;
        _rate = new LeastSquaresRateEstimator(tuning.RateWindowSeconds);
        _output = tuning.OutputMin;
    }

    /// <summary>The active tuning. Replacing it re-arms the rate window on the next step.</summary>
    public CascadeTuning Tuning { get; private set; }

    /// <summary>The controlled variable's target, in percent dissolved oxygen.</summary>
    public double Setpoint { get; set; }

    /// <summary>The current commanded control effort, in percent.</summary>
    public double Output => _output;

    /// <summary>The most recent decomposition, for the live-terms display.</summary>
    public CascadeTerms LastTerms { get; private set; } = CascadeTerms.Empty;

    /// <summary>
    /// Applies new tuning. Gains and limits take effect immediately; the rate window is
    /// rebuilt only when its length changes, so a gain tweak does not throw away the
    /// probe history the prediction depends on.
    /// </summary>
    public void Retune(CascadeTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);

        if (Math.Abs(tuning.RateWindowSeconds - Tuning.RateWindowSeconds) > 1e-9)
        {
            var replacement = new LeastSquaresRateEstimator(tuning.RateWindowSeconds);
            _rate = replacement;
            _hasPrevious = false;
        }

        Tuning = tuning;
        _integral = Math.Clamp(_integral, tuning.IntegralMin, tuning.IntegralMax);
        _output = Math.Clamp(_output, tuning.OutputMin, tuning.OutputMax);
    }

    /// <summary>
    /// Advances the loop by <paramref name="dtSeconds"/> against a new measurement and
    /// returns the decomposed result.
    /// </summary>
    /// <param name="measurement">Latest dissolved-oxygen reading, in percent.</param>
    /// <param name="dtSeconds">Elapsed time since the previous step, in seconds.</param>
    /// <remarks>
    /// A non-positive or non-finite step is ignored and the previous terms are returned:
    /// a controller must never integrate against a clock that ran backwards.
    /// </remarks>
    public CascadeTerms Update(double measurement, double dtSeconds)
    {
        if (!double.IsFinite(measurement) || !double.IsFinite(dtSeconds) || dtSeconds <= 0)
        {
            return LastTerms;
        }

        var dt = Math.Min(dtSeconds, MaxStepSeconds);
        _elapsedSeconds += dt;
        _rate.Add(_elapsedSeconds, measurement);
        var rate = _rate.Rate;

        // Prediction horizon: steer by where the measurement is heading, not the stale
        // value the probe currently shows.
        var predicted = measurement + (rate * Tuning.PredictionHorizonSeconds);
        var error = Setpoint - predicted;

        if (!_hasPrevious)
        {
            // Seed the differences so the first step produces no proportional or
            // derivative kick from a phantom "previous" error of zero.
            _previousError = error;
            _previousRate = rate;
            _hasPrevious = true;
        }

        // Velocity-form increments. The output is the accumulated sum of these, so it is
        // always relative to the last actuator position - which is what lets it hold at
        // setpoint instead of collapsing to zero, and makes windup structurally
        // impossible (there is no unbounded integrator state, only the clamped output).
        var deltaP = Tuning.Kp * (error - _previousError);
        var integralStep = Tuning.Ki * error * dt;

        // Derivative acts on the measurement rate, not the error, so a setpoint change
        // cannot produce a derivative spike. The sign opposes fast oxygen movement.
        var deltaD = -Tuning.Kd * (rate - _previousRate);

        var deltaOutput = deltaP + integralStep + deltaD;
        var unclamped = _output + deltaOutput;
        var clamped = Math.Clamp(unclamped, Tuning.OutputMin, Tuning.OutputMax);
        var saturated = Math.Abs(clamped - unclamped) > 1e-12;

        // Anti-windup is structural in velocity form: the output IS the integrator, and it
        // is clamped here, so there is no unbounded state to unwind - the moment the error
        // reverses, the proportional term pulls the output straight off the rail. The
        // reported integral term is separately held while the output is railed into a
        // limit, so the live "I" reflects the authority that is actually effective rather
        // than a figure climbing against a stop (docs/UI_DESIGN.md section 5.2).
        var railedIntoLimit =
            (clamped >= Tuning.OutputMax && integralStep > 0) ||
            (clamped <= Tuning.OutputMin && integralStep < 0);
        if (!(saturated && railedIntoLimit))
        {
            _integral = Math.Clamp(
                _integral + integralStep, Tuning.IntegralMin, Tuning.IntegralMax);
        }

        var applied = clamped - _output;
        _output = clamped;
        _previousError = error;
        _previousRate = rate;

        LastTerms = new CascadeTerms(
            Error: error,
            Proportional: Tuning.Kp * error,
            Integral: _integral,
            Derivative: -Tuning.Kd * rate,
            DeltaOutput: applied,
            Output: _output,
            PredictedMeasurement: predicted,
            MeasurementRate: rate,
            Saturated: saturated);

        return LastTerms;
    }

    /// <summary>
    /// Clears the reported integral contribution to zero, keeping the output and probe
    /// history.
    /// </summary>
    /// <remarks>
    /// The operator-facing "reset integral". In velocity form the output <i>is</i> the
    /// integrator and cannot structurally wind up, so this clears the bounded integral
    /// <i>term</i> that the live display shows — a deliberate re-baseline of accumulated
    /// authority — without snapping the actuator or discarding the rate window.
    /// </remarks>
    public void ResetIntegral() => _integral = 0;

    /// <summary>
    /// Clears all loop state. Used when the cascade is disarmed - a reconnect, a mode
    /// change - so it does not resume with a stale integral or a stale rate history.
    /// </summary>
    public void Reset()
    {
        _rate.Reset();
        _elapsedSeconds = 0;
        _output = Tuning.OutputMin;
        _integral = 0;
        _previousError = 0;
        _previousRate = 0;
        _hasPrevious = false;
        LastTerms = CascadeTerms.Empty;
    }

    /// <summary>
    /// Places the loop at a known operating point without a transient - the actuator is
    /// already at <paramref name="output"/> and the integral should own that level.
    /// </summary>
    /// <remarks>
    /// Bumpless arming. When the cascade takes over from manual control the actuator is
    /// wherever the operator left it; starting the integral there means the first
    /// automatic step nudges from the real position instead of snapping to zero.
    /// </remarks>
    public void Preload(double output)
    {
        if (!double.IsFinite(output))
        {
            throw new ArgumentOutOfRangeException(nameof(output), output, "Output must be finite.");
        }

        _output = Math.Clamp(output, Tuning.OutputMin, Tuning.OutputMax);
        _integral = Math.Clamp(_output, Tuning.IntegralMin, Tuning.IntegralMax);
    }
}
