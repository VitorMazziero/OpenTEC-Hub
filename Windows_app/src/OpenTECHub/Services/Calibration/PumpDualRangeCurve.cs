using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Calibration;

/// <summary>
/// Continuous piecewise-linear calibration <c>Q(S)</c> with two slopes meeting at <c>(St, Qt)</c>.
/// <code>
/// Q(S) = Qt + LowSlope  · (S - St), for S &lt;= St
/// Q(S) = Qt + HighSlope · (S - St), for S &gt;  St
/// </code>
/// </summary>
/// <remarks>
/// The two segments share the point <c>(St, Qt)</c>, so continuity of value is guaranteed by
/// construction. Only a slope change is permitted at the transition, never a flow jump.
/// <c>S = St</c> belongs to the low segment.
/// </remarks>
public readonly record struct PumpDualRangeCurve(
    double LowSlope,
    double HighSlope,
    double TransitionSpeed,
    double TransitionFlow)
{
    /// <summary>The default transition speed used when migrating the legacy linear curve.</summary>
    public const double MigrationTransitionSpeed = 500.0;

    /// <summary>Converts an internal speed <paramref name="s"/> to flow in mL/min.</summary>
    public double FlowFromSpeed(double s)
    {
        var slope = s <= TransitionSpeed ? LowSlope : HighSlope;
        return TransitionFlow + slope * (s - TransitionSpeed);
    }

    /// <summary>Converts a flow <paramref name="q"/> in mL/min to internal speed units.</summary>
    /// <exception cref="InvalidOperationException">When the relevant slope does not allow inversion.</exception>
    public double SpeedFromFlow(double q)
    {
        var slope = q <= TransitionFlow ? LowSlope : HighSlope;
        if (slope <= 0.0 || !double.IsFinite(slope))
        {
            throw new InvalidOperationException(
                "Inclinação não positiva ou não finita não permite inversão.");
        }

        return TransitionSpeed + (q - TransitionFlow) / slope;
    }

    /// <summary>
    /// Migrates the legacy linear calibration <c>Q = slope · S + intercept</c> to a dual-range
    /// curve with equal slopes and <c>St = 500</c>.
    /// </summary>
    /// <remarks>
    /// Because both slopes are identical, the migrated curve reproduces the original line exactly
    /// across the entire speed range.
    /// </remarks>
    public static PumpDualRangeCurve FromLinear(double slope, double intercept)
    {
        if (slope <= 0.0 || !double.IsFinite(slope))
        {
            throw new ArgumentOutOfRangeException(
                nameof(slope), slope, "A inclinação deve ser positiva e finita.");
        }

        if (!double.IsFinite(intercept))
        {
            throw new ArgumentOutOfRangeException(
                nameof(intercept), intercept, "O intercepto deve ser finito.");
        }

        const double st = MigrationTransitionSpeed;
        var qt = slope * st + intercept;
        var curve = new PumpDualRangeCurve(slope, slope, st, qt);
        if (!curve.Validate(out var error))
        {
            throw new ArgumentOutOfRangeException(nameof(intercept), intercept, error);
        }

        return curve;
    }

    /// <summary>Validates that the curve parameters are physically meaningful.</summary>
    /// <param name="error">Describes the first validation failure, or null when valid.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public bool Validate(out string? error)
    {
        if (TransitionFlow <= 0.0 || !double.IsFinite(TransitionFlow))
        {
            error = "A vazão de transição (Qt) deve ser positiva e finita.";
            return false;
        }

        if (TransitionSpeed <= 0.0 || TransitionSpeed >= 1000.0 || !double.IsFinite(TransitionSpeed))
        {
            error = "A velocidade de transição (St) deve estar em (0, 1000).";
            return false;
        }

        if (LowSlope <= 0.0 || !double.IsFinite(LowSlope))
        {
            error = "A inclinação da faixa baixa deve ser positiva e finita.";
            return false;
        }

        if (HighSlope <= 0.0 || !double.IsFinite(HighSlope))
        {
            error = "A inclinação da faixa alta deve ser positiva e finita.";
            return false;
        }

        // Check that the curve does not produce negative flow at S = 0.
        var flowAtZero = FlowFromSpeed(0.0);
        if (flowAtZero < 0.0)
        {
            error = $"A curva extrapola para vazão negativa em S = 0 ({flowAtZero:F4} mL/min).";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>Structured result of a dual-range pump curve fit.</summary>
public sealed record PumpFitResult
{
    /// <summary>Whether the fit produced a valid curve.</summary>
    public required bool IsValid { get; init; }

    /// <summary>The fitted curve, or null when the fit failed.</summary>
    public PumpDualRangeCurve? Curve { get; init; }

    /// <summary>Describes why the fit failed, or null on success.</summary>
    public string? Error { get; init; }

    // ---- Global statistics ----

    /// <summary>Sum of squared errors across all points.</summary>
    public double SSE { get; init; }

    /// <summary>Root mean squared error across all points.</summary>
    public double RMSE { get; init; }

    /// <summary>Coefficient of determination (R²) across all points.</summary>
    public double RSquared { get; init; }

    /// <summary>Total number of calibration points used.</summary>
    public int TotalPoints { get; init; }

    // ---- Per-segment statistics ----

    public int LowPointCount { get; init; }
    public int HighPointCount { get; init; }
    public double LowSSE { get; init; }
    public double HighSSE { get; init; }
    public double LowRMSE { get; init; }
    public double HighRMSE { get; init; }

    /// <summary>Residuals per point, in the same order as the input.</summary>
    public IReadOnlyList<double>? Residuals { get; init; }

    /// <summary>Creates a failure result with a descriptive message.</summary>
    public static PumpFitResult Fail(string error) => new()
    {
        IsValid = false,
        Error = error,
    };
}

/// <summary>
/// Pure math for the pump's dual-range calibration curve.
/// </summary>
public static class PumpDualRangeMath
{
    /// <summary>
    /// Fits a dual-range curve to volumetric calibration <paramref name="points"/> for a given
    /// <paramref name="transitionFlow"/> (<c>Qt</c>).
    /// </summary>
    /// <remarks>
    /// The algorithm searches over candidate transition speeds <c>St</c> derived from the
    /// observed speed values. For each candidate, slopes are computed by constrained least
    /// squares around the fixed point <c>(St, Qt)</c>. The solution with the lowest total
    /// sum of squared errors is selected.
    /// </remarks>
    public static PumpFitResult FitDualRange(
        IReadOnlyList<PumpCalibrationPoint> points,
        double transitionFlow)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (transitionFlow <= 0.0 || !double.IsFinite(transitionFlow))
        {
            return PumpFitResult.Fail("A vazão de transição (Qt) deve ser positiva e finita.");
        }

        if (points.Count < 4)
        {
            return PumpFitResult.Fail(
                "São necessários pelo menos 4 pontos (2 por faixa com velocidades distintas).");
        }

        // Filter out non-finite points.
        var valid = points
            .Where(p => double.IsFinite(p.SpeedUnits) && double.IsFinite(p.FlowMlPerMin)
                        && p.SpeedUnits >= 0.0 && p.SpeedUnits <= 1000.0)
            .ToArray();

        if (valid.Length < 4)
        {
            return PumpFitResult.Fail(
                "Pontos válidos insuficientes após filtrar valores fora da faixa.");
        }

        // Build candidate St values: midpoints between adjacent distinct speeds,
        // plus a small set of anchor points near boundaries.
        var sortedSpeeds = valid.Select(p => p.SpeedUnits).Distinct().OrderBy(s => s).ToArray();
        if (sortedSpeeds.Length < 2)
        {
            return PumpFitResult.Fail(
                "Todos os pontos estão na mesma velocidade; ajuste impossível.");
        }

        var candidates = new List<double>();
        for (var i = 0; i < sortedSpeeds.Length - 1; i++)
        {
            var mid = (sortedSpeeds[i] + sortedSpeeds[i + 1]) / 2.0;
            if (mid > 0.0 && mid < 1000.0)
            {
                candidates.Add(mid);
            }
        }

        // Also try at each observed speed (shifted by epsilon to keep that speed in the low side).
        foreach (var speed in sortedSpeeds)
        {
            var justAbove = speed + 1e-6;
            if (justAbove > 0.0 && justAbove < 1000.0)
            {
                candidates.Add(justAbove);
            }
        }

        // The minimum for a partition need not lie at its midpoint. Search each interval
        // between consecutive measured speeds while keeping the same low/high membership.
        for (var i = 0; i < sortedSpeeds.Length - 1; i++)
        {
            if (TryFindBestTransitionInInterval(
                    valid, transitionFlow, sortedSpeeds[i], sortedSpeeds[i + 1], out var st))
            {
                candidates.Add(st);
            }
        }

        if (candidates.Count == 0)
        {
            return PumpFitResult.Fail("Nenhum candidato de St é válido em (0, 1000).");
        }

        PumpFitResult? best = null;
        var bestSSE = double.PositiveInfinity;

        foreach (var st in candidates)
        {
            var result = TryFitAtSt(valid, transitionFlow, st);
            if (result.IsValid && result.SSE < bestSSE)
            {
                bestSSE = result.SSE;
                best = result;
            }
        }

        return best ?? PumpFitResult.Fail(
            "Nenhuma partição de St produziu um ajuste válido com inclinações positivas e finitas.");
    }

    private static bool TryFindBestTransitionInInterval(
        PumpCalibrationPoint[] points,
        double qt,
        double lowerSpeed,
        double upperSpeed,
        out double bestTransition)
    {
        // At an observed speed the segment convention changes. Within an interval the
        // constrained least-squares objective is smooth, so it can be minimized directly.
        const double epsilon = 1e-6;
        var lower = Math.Max(epsilon, lowerSpeed + epsilon);
        var upper = Math.Min(1000.0 - epsilon, upperSpeed - epsilon);
        bestTransition = double.NaN;
        if (lower >= upper)
        {
            return false;
        }

        const double goldenRatioConjugate = 0.6180339887498949;
        var left = lower;
        var right = upper;
        var x1 = right - goldenRatioConjugate * (right - left);
        var x2 = left + goldenRatioConjugate * (right - left);
        var f1 = ObjectiveAt(points, qt, x1);
        var f2 = ObjectiveAt(points, qt, x2);

        for (var iteration = 0; iteration < 48; iteration++)
        {
            if (f1 <= f2)
            {
                right = x2;
                x2 = x1;
                f2 = f1;
                x1 = right - goldenRatioConjugate * (right - left);
                f1 = ObjectiveAt(points, qt, x1);
            }
            else
            {
                left = x1;
                x1 = x2;
                f1 = f2;
                x2 = left + goldenRatioConjugate * (right - left);
                f2 = ObjectiveAt(points, qt, x2);
            }
        }

        bestTransition = f1 <= f2 ? x1 : x2;
        return double.IsFinite(Math.Min(f1, f2));
    }

    private static double ObjectiveAt(PumpCalibrationPoint[] points, double qt, double st)
    {
        var result = TryFitAtSt(points, qt, st);
        return result.IsValid ? result.SSE : double.PositiveInfinity;
    }

    private static PumpFitResult TryFitAtSt(
        PumpCalibrationPoint[] points,
        double qt,
        double st)
    {
        // Partition: low side is S <= St, high side is S > St.
        var low = points.Where(p => p.SpeedUnits <= st).ToArray();
        var high = points.Where(p => p.SpeedUnits > st).ToArray();

        // Require at least 2 points with distinct speeds in each partition.
        if (low.Select(p => p.SpeedUnits).Distinct().Count() < 2)
        {
            return PumpFitResult.Fail("Faixa baixa sem velocidades distintas suficientes.");
        }

        if (high.Select(p => p.SpeedUnits).Distinct().Count() < 2)
        {
            return PumpFitResult.Fail("Faixa alta sem velocidades distintas suficientes.");
        }

        // Constrained least-squares around fixed point (St, Qt):
        //   Q_i = Qt + m * (S_i - St)   =>   (Q_i - Qt) = m * (S_i - St)
        //   m = Σ[(Si-St)(Qi-Qt)] / Σ[(Si-St)²]
        var mLow = FitSlopeAroundPivot(low, st, qt);
        var mHigh = FitSlopeAroundPivot(high, st, qt);

        if (!double.IsFinite(mLow) || mLow <= 0.0 ||
            !double.IsFinite(mHigh) || mHigh <= 0.0)
        {
            return PumpFitResult.Fail("Inclinação não positiva ou não finita.");
        }

        var curve = new PumpDualRangeCurve(mLow, mHigh, st, qt);

        // Validate the curve (monotonicity, extrapolation, etc.)
        if (!curve.Validate(out var validationError))
        {
            return PumpFitResult.Fail(validationError!);
        }

        // Compute residuals and statistics.
        return ComputeStatistics(points, curve, low.Length, high.Length);
    }

    private static double FitSlopeAroundPivot(PumpCalibrationPoint[] points, double st, double qt)
    {
        var numerator = 0.0;
        var denominator = 0.0;
        foreach (var p in points)
        {
            var ds = p.SpeedUnits - st;
            var dq = p.FlowMlPerMin - qt;
            numerator += ds * dq;
            denominator += ds * ds;
        }

        if (denominator <= 1e-12)
        {
            return double.NaN;
        }

        return numerator / denominator;
    }

    private static PumpFitResult ComputeStatistics(
        PumpCalibrationPoint[] points,
        PumpDualRangeCurve curve,
        int lowCount,
        int highCount)
    {
        var n = points.Length;
        var residuals = new double[n];
        var sse = 0.0;
        var lowSSE = 0.0;
        var highSSE = 0.0;
        var meanQ = points.Average(p => p.FlowMlPerMin);
        var sst = 0.0;

        for (var i = 0; i < n; i++)
        {
            var predicted = curve.FlowFromSpeed(points[i].SpeedUnits);
            var residual = points[i].FlowMlPerMin - predicted;
            residuals[i] = residual;
            var r2 = residual * residual;
            sse += r2;

            if (points[i].SpeedUnits <= curve.TransitionSpeed)
            {
                lowSSE += r2;
            }
            else
            {
                highSSE += r2;
            }

            var diff = points[i].FlowMlPerMin - meanQ;
            sst += diff * diff;
        }

        var rSquared = sst > 1e-12 ? 1.0 - sse / sst : 1.0;
        var rmse = n > 0 ? Math.Sqrt(sse / n) : 0.0;
        var lowRMSE = lowCount > 0 ? Math.Sqrt(lowSSE / lowCount) : 0.0;
        var highRMSE = highCount > 0 ? Math.Sqrt(highSSE / highCount) : 0.0;

        return new PumpFitResult
        {
            IsValid = true,
            Curve = curve,
            SSE = sse,
            RMSE = rmse,
            RSquared = rSquared,
            TotalPoints = n,
            LowPointCount = lowCount,
            HighPointCount = highCount,
            LowSSE = lowSSE,
            HighSSE = highSSE,
            LowRMSE = lowRMSE,
            HighRMSE = highRMSE,
            Residuals = residuals,
        };
    }
}
