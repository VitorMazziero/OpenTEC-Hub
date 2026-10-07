namespace OpenTECHub.Services.Control;

/// <summary>
/// Method for estimating the dissolved-oxygen rate (dDOT/dt).
/// </summary>
public enum CascadeSlopeMethod
{
    /// <summary>Two-point endpoint difference over the window.</summary>
    EndpointDiff,

    /// <summary>Least-squares linear regression over all samples in the window.</summary>
    LeastSquares,
}

/// <summary>
/// The two-loop dissolved oxygen cascade PID controller based on Mazziero et al. (2025)
/// and ReceitasOpenTEC.
/// </summary>
/// <remarks>
/// <para>
/// <b>Outer loop:</b> Proportional gain <c>KDot</c> on predicted measurement error
/// (<c>DOT_set - DOT_pred</c>) produces the rate setpoint <c>(dDOT/dt)_set</c>.
/// </para>
/// <para>
/// <b>Inner loop:</b> Velocity-form PID on rate error (<c>(dDOT/dt)_set - (dDOT/dt)_med</c>)
/// produces <c>dOutput</c> which accumulates into the bounded control effort <c>Output</c>.
/// </para>
/// <para>
/// <b>Anti-windup:</b> Sliding time window (<c>MWindow</c>) on rate error with strict clamp (<c>IMin..IMax</c>).
/// </para>
/// <para>
/// <b>Gain scheduling:</b> Effort-dependent gain scaling <c>g</c> between agitation and aeration
/// active in <see cref="CascadeMode.DualCascade"/> (Cascata).
/// </para>
/// </remarks>
public sealed class CascadeTwoLoopPidController
{
    private readonly Queue<double> _dotHistory = new();
    private readonly Queue<double> _errorWindow = new();

    private double _output;
    private double _integral;
    private double _ePrev;
    private double _dfPrev;
    private bool _hasPrevious;

    public CascadeTwoLoopPidController(CascadeTuning tuning, double setpoint = 0.0)
    {
        Tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        Setpoint = setpoint;
        _output = tuning.OutputMin;
    }

    public CascadeTuning Tuning { get; private set; }

    public double Setpoint { get; set; }

    public double Output => _output;

    public CascadeTerms LastTerms { get; private set; } = CascadeTerms.Empty;

    public void Retune(CascadeTuning tuning)
    {
        Tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _integral = Math.Clamp(_integral, tuning.IntegralMin, tuning.IntegralMax);
        _output = Math.Clamp(_output, tuning.OutputMin, tuning.OutputMax);
    }

    public CascadeTerms Update(
        double measurement,
        double dtSeconds,
        IReadOnlyList<ActuatorWindow>? windows = null,
        bool isCascadeMode = false)
    {
        if (!double.IsFinite(measurement) || !double.IsFinite(dtSeconds) || dtSeconds <= 0)
        {
            return LastTerms;
        }

        var dtNominal = Tuning.IntervalSeconds > 0 ? Tuning.IntervalSeconds : 3.0;
        var dtReal = dtSeconds;

        // Discontinuity guard (> 3x nominal step): reset derivative history to prevent kick.
        if (_hasPrevious && dtReal > 3.0 * dtNominal)
        {
            _dotHistory.Clear();
            _ePrev = 0.0;
            _dfPrev = 0.0;
        }

        var dtEff = Math.Clamp(dtReal, dtNominal, 3.0 * dtNominal);

        _dotHistory.Enqueue(measurement);
        var histMax = Math.Max(Tuning.JAvg, Tuning.NPred) + 2;
        while (_dotHistory.Count > histMax)
        {
            _dotHistory.Dequeue();
        }

        var historyArray = _dotHistory.ToArray();
        var dDotDt = EstimarSlope(historyArray, Tuning.JAvg, dtEff, Tuning.SlopeMethod);
        var dDotDtPred = EstimarSlope(historyArray, Tuning.NPred, dtEff, Tuning.SlopeMethod);

        var dotPred = Math.Clamp(measurement + (dDotDtPred * Tuning.PredictionHorizonSeconds), 0.0, 100.0);

        // Gain scheduling: active only for dual cascade when enabled
        var fatorGanho = 1.0;
        if (isCascadeMode && Tuning.HabilitarGainScheduling && windows != null && windows.Count >= 2)
        {
            fatorGanho = CalcularFatorGanhoEfetivo(_output, windows, Tuning.FatorGanhoAeracao);
        }

        var kDotEff = Tuning.KDot * fatorGanho;
        var kPEff = Tuning.Kp * fatorGanho;

        var eDot = Setpoint - dotPred;
        var dDotDtSet = kDotEff * eDot;
        var e = dDotDtSet - dDotDt;
        var pTerm = e;

        if (!_hasPrevious)
        {
            _ePrev = e;
            _dfPrev = 0.0;
            _hasPrevious = true;
        }

        _errorWindow.Enqueue(e);
        var windowSamples = (int)Math.Max(1, Math.Round(Tuning.MWindow / dtNominal));
        while (_errorWindow.Count > windowSamples)
        {
            _errorWindow.Dequeue();
        }

        var iRaw = 0.0;
        foreach (var err in _errorWindow)
        {
            iRaw += err * dtNominal;
        }
        var iTerm = Math.Clamp(iRaw, Tuning.IntegralMin, Tuning.IntegralMax);
        _integral = iTerm;

        var omega = dtEff / (Tuning.TauD + dtEff);
        var rawDf = dtEff > 0 ? (e - _ePrev) / dtEff : 0.0;
        var df = (omega * rawDf) + ((1.0 - omega) * _dfPrev);

        var deltaOutput = (kPEff * pTerm) + (Tuning.Ki * iTerm) + (Tuning.Kd * df);
        var unclamped = _output + deltaOutput;
        var clamped = Math.Clamp(unclamped, Tuning.OutputMin, Tuning.OutputMax);
        var saturated = Math.Abs(clamped - unclamped) > 1e-12;

        var applied = clamped - _output;
        _output = clamped;
        _ePrev = e;
        _dfPrev = df;

        LastTerms = new CascadeTerms(
            Error: eDot,
            Proportional: kPEff * pTerm,
            Integral: Tuning.Ki * iTerm,
            Derivative: Tuning.Kd * df,
            DeltaOutput: applied,
            Output: _output,
            PredictedMeasurement: dotPred,
            MeasurementRate: dDotDt,
            Saturated: saturated,
            RateSetpoint: dDotDtSet,
            GainFactor: fatorGanho);

        return LastTerms;
    }

    public void Preload(double output)
    {
        if (!double.IsFinite(output))
        {
            throw new ArgumentOutOfRangeException(nameof(output), output, "Output must be finite.");
        }

        _output = Math.Clamp(output, Tuning.OutputMin, Tuning.OutputMax);
        _integral = Math.Clamp(_output, Tuning.IntegralMin, Tuning.IntegralMax);
        _errorWindow.Clear();
    }

    /// <summary>Rebases probe/derivative history after a suspended interval, preserving output and integral window.</summary>
    public void ResumeFromSuspension(double measurement)
    {
        if (!double.IsFinite(measurement)) throw new ArgumentOutOfRangeException(nameof(measurement));
        _dotHistory.Clear();
        _dotHistory.Enqueue(measurement);
        _ePrev = 0;
        _dfPrev = 0;
        _hasPrevious = false;
        LastTerms = LastTerms with { Error = Setpoint - measurement, Proportional = 0,
            Integral = Tuning.Ki * _integral, Derivative = 0, DeltaOutput = 0,
            Output = _output, PredictedMeasurement = measurement, MeasurementRate = 0,
            RateSetpoint = Tuning.KDot * LastTerms.GainFactor * (Setpoint - measurement) };
    }

    public void ResetIntegral()
    {
        _integral = 0.0;
        _errorWindow.Clear();
    }

    public void Reset()
    {
        _dotHistory.Clear();
        _errorWindow.Clear();
        _output = Tuning.OutputMin;
        _integral = 0.0;
        _ePrev = 0.0;
        _dfPrev = 0.0;
        _hasPrevious = false;
        LastTerms = CascadeTerms.Empty;
    }

    private static double EstimarSlope(double[] historia, int janela, double dt, CascadeSlopeMethod metodo)
    {
        var n = historia.Length;
        if (n < 2 || dt <= 0.0)
        {
            return 0.0;
        }

        var count = Math.Min(Math.Max(janela, 1), n - 1);
        if (count < 1)
        {
            return 0.0;
        }

        if (metodo == CascadeSlopeMethod.LeastSquares)
        {
            var m = count + 1;
            var start = n - m;
            var kBar = (m - 1) / 2.0;
            var yBar = 0.0;
            for (var i = 0; i < m; ++i)
            {
                yBar += historia[start + i];
            }
            yBar /= m;

            double num = 0.0, den = 0.0;
            for (var i = 0; i < m; ++i)
            {
                var dk = i - kBar;
                num += dk * (historia[start + i] - yBar);
                den += dk * dk;
            }

            var slopePorAmostra = den > 1e-9 ? num / den : 0.0;
            return slopePorAmostra / dt;
        }

        // EndpointDiff: average of differences across the window
        var soma = 0.0;
        for (var k = 0; k < count; ++k)
        {
            var idxNew = n - 1 - k;
            var idxOld = idxNew - 1;
            soma += (historia[idxNew] - historia[idxOld]) / dt;
        }

        return soma / count;
    }

    private static double CalcularFatorGanhoEfetivo(
        double outputPct,
        IReadOnlyList<ActuatorWindow> windows,
        double fatorGanhoAeracao)
    {
        if (windows.Count == 0)
        {
            return 1.0;
        }

        double somaW = 0.0, somaWF = 0.0;
        double somaFallback = 0.0;
        var nFallback = 0;

        foreach (var w in windows)
        {
            var isAgitation = string.Equals(w.Name, CascadeController.AgitationActuator, StringComparison.OrdinalIgnoreCase) ||
                              w.Name.StartsWith("Agit", StringComparison.OrdinalIgnoreCase);
            var factor = isAgitation ? 1.0 : fatorGanhoAeracao;
            var frac = FracNaJanela(outputPct, w.EffortStart, w.EffortEnd);
            var weight = frac * (1.0 - frac);

            somaW += weight;
            somaWF += weight * factor;

            if (outputPct >= w.EffortStart && outputPct <= w.EffortEnd)
            {
                somaFallback += factor;
                nFallback++;
            }
        }

        if (somaW > 1e-9)
        {
            return somaWF / somaW;
        }

        if (nFallback > 0)
        {
            return somaFallback / nFallback;
        }

        var last = windows[^1];
        if (outputPct >= last.EffortEnd)
        {
            var isAgit = string.Equals(last.Name, CascadeController.AgitationActuator, StringComparison.OrdinalIgnoreCase) ||
                         last.Name.StartsWith("Agit", StringComparison.OrdinalIgnoreCase);
            return isAgit ? 1.0 : fatorGanhoAeracao;
        }

        return 1.0;
    }

    private static double FracNaJanela(double outputPct, double lo, double hi)
    {
        if (outputPct <= lo)
        {
            return 0.0;
        }

        if (outputPct >= hi)
        {
            return 1.0;
        }

        var span = hi - lo;
        return span > 1e-9 ? (outputPct - lo) / span : 1.0;
    }
}
