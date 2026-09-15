using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Calibration;

/// <summary>Pump Q(S): quartic below St and quadratic above it, like the flowmeter.</summary>
public readonly record struct PumpDualRangeCurve
{
    public const double MinimumSpeed = 0.0;
    public const double MaximumSpeed = 1000.0;

    public PumpDualRangeCurve(PolynomialCalibration lowSpeed, PolynomialCalibration highSpeed, double transitionSpeed)
    {
        LowSpeed = lowSpeed;
        HighSpeed = highSpeed;
        TransitionSpeed = transitionSpeed;
    }

    public PolynomialCalibration LowSpeed { get; }
    public PolynomialCalibration HighSpeed { get; }
    public double TransitionSpeed { get; }
    public double TransitionFlow => LowSpeed.Evaluate(TransitionSpeed);
    public double LowSlope => LowSpeed.Derivative(TransitionSpeed);
    public double HighSlope => HighSpeed.Derivative(TransitionSpeed);
    public double ValueDiscontinuity => HighSpeed.Evaluate(TransitionSpeed) - TransitionFlow;
    public double DerivativeDiscontinuity => HighSpeed.Derivative(TransitionSpeed) - LowSlope;

    public double FlowFromSpeed(double speed) =>
        (speed <= TransitionSpeed ? LowSpeed : HighSpeed).Evaluate(speed);

    /// <summary>Numerical inverse shared in behavior with the firmware implementation.</summary>
    public double SpeedFromFlow(double flow)
    {
        if (!double.IsFinite(flow))
        {
            throw new ArgumentOutOfRangeException(nameof(flow), flow, "A vazão deve ser finita.");
        }

        if (!Validate(out var error))
        {
            throw new InvalidOperationException(error);
        }

        if (flow <= FlowFromSpeed(MinimumSpeed))
        {
            return MinimumSpeed;
        }

        if (flow >= FlowFromSpeed(MaximumSpeed))
        {
            return MaximumSpeed;
        }

        var lower = flow <= TransitionFlow ? MinimumSpeed : TransitionSpeed;
        var upper = flow <= TransitionFlow ? TransitionSpeed : MaximumSpeed;
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var middle = (lower + upper) / 2.0;
            if (FlowFromSpeed(middle) < flow)
            {
                lower = middle;
            }
            else
            {
                upper = middle;
            }
        }

        return (lower + upper) / 2.0;
    }

    public bool Validate(out string? error)
    {
        if (TransitionSpeed <= MinimumSpeed || TransitionSpeed >= MaximumSpeed || !double.IsFinite(TransitionSpeed))
        {
            error = "A velocidade de transição (St) deve estar em (0, 1000).";
            return false;
        }

        if (!Finite(LowSpeed) || !Finite(HighSpeed))
        {
            error = "Todos os coeficientes da curva devem ser finitos.";
            return false;
        }

        var valueScale = Math.Max(1.0, Math.Abs(TransitionFlow));
        if (Math.Abs(ValueDiscontinuity) > 1e-6 * valueScale)
        {
            error = "Os segmentos não são contínuos em valor na velocidade de transição.";
            return false;
        }

        var slopeScale = Math.Max(1.0, Math.Abs(LowSlope));
        if (Math.Abs(DerivativeDiscontinuity) > 1e-6 * slopeScale)
        {
            error = "Os segmentos não são contínuos em inclinação na velocidade de transição.";
            return false;
        }

        const int samples = 128;
        var previous = FlowFromSpeed(MinimumSpeed);
        if (!double.IsFinite(previous) || previous < -1e-6)
        {
            error = "A curva produz vazão inválida ou negativa em S = 0.";
            return false;
        }

        for (var i = 1; i <= samples; i++)
        {
            var speed = MaximumSpeed * i / samples;
            var current = FlowFromSpeed(speed);
            if (!double.IsFinite(current) || current + 1e-7 < previous)
            {
                error = "A curva deve ser finita e monotonicamente crescente em 0 ≤ S ≤ 1000.";
                return false;
            }
            previous = current;
        }

        if (previous <= FlowFromSpeed(MinimumSpeed) + 1e-6)
        {
            error = "A curva deve aumentar a vazão ao longo da faixa de velocidade.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool Finite(PolynomialCalibration curve) =>
        double.IsFinite(curve.A) && double.IsFinite(curve.B) && double.IsFinite(curve.K) &&
        double.IsFinite(curve.F) && double.IsFinite(curve.C);
}

public sealed record PumpFitResult
{
    public required bool IsValid { get; init; }
    public PumpDualRangeCurve? Curve { get; init; }
    public string? Error { get; init; }
    public double SSE { get; init; }
    public double RMSE { get; init; }
    public double RSquared { get; init; }
    public int TotalPoints { get; init; }
    public int LowPointCount { get; init; }
    public int HighPointCount { get; init; }
    public double LowSSE { get; init; }
    public double HighSSE { get; init; }
    public double LowRMSE { get; init; }
    public double HighRMSE { get; init; }
    public IReadOnlyList<double>? Residuals { get; init; }
    public static PumpFitResult Fail(string error) => new() { IsValid = false, Error = error };
}

public static class PumpDualRangeMath
{
    /// <summary>Finds the physically valid St with the lowest residual error for the recorded points.</summary>
    public static double? SuggestTransitionSpeed(IReadOnlyList<PumpCalibrationPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var valid = points.Where(point =>
                double.IsFinite(point.SpeedUnits) && double.IsFinite(point.FlowMlPerMin) &&
                point.SpeedUnits >= PumpDualRangeCurve.MinimumSpeed && point.SpeedUnits <= PumpDualRangeCurve.MaximumSpeed &&
                point.FlowMlPerMin >= 0.0)
            .ToArray();
        if (valid.Length < 4)
        {
            return null;
        }

        var lower = Math.Max(1, (int)Math.Ceiling(valid.Min(point => point.SpeedUnits)));
        var upper = Math.Min(999, (int)Math.Floor(valid.Max(point => point.SpeedUnits)));
        double? best = null;
        var bestRmse = double.PositiveInfinity;
        for (var candidate = lower; candidate <= upper; candidate++)
        {
            var fit = FitDualRange(valid, candidate);
            if (!fit.IsValid || !double.IsFinite(fit.RMSE))
            {
                continue;
            }
            if (fit.RMSE < bestRmse - 1e-9)
            {
                bestRmse = fit.RMSE;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>Uses the flowmeter's quartic/quadratic C0+C1 fit around operator-selected St.</summary>
    public static PumpFitResult FitDualRange(IReadOnlyList<PumpCalibrationPoint> points, double transitionSpeed)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (transitionSpeed <= PumpDualRangeCurve.MinimumSpeed ||
            transitionSpeed >= PumpDualRangeCurve.MaximumSpeed || !double.IsFinite(transitionSpeed))
        {
            return PumpFitResult.Fail("A velocidade de transição (St) deve estar em (0, 1000).");
        }

        var valid = points.Where(p =>
            double.IsFinite(p.SpeedUnits) && double.IsFinite(p.FlowMlPerMin) &&
            p.SpeedUnits >= PumpDualRangeCurve.MinimumSpeed &&
            p.SpeedUnits <= PumpDualRangeCurve.MaximumSpeed && p.FlowMlPerMin >= 0.0).ToArray();
        var lowCount = valid.Count(p => p.SpeedUnits <= transitionSpeed);
        var highCount = valid.Length - lowCount;
        if (lowCount < 2 || highCount < 2 ||
            valid.Where(p => p.SpeedUnits <= transitionSpeed).Select(p => p.SpeedUnits).Distinct().Count() < 2 ||
            valid.Where(p => p.SpeedUnits > transitionSpeed).Select(p => p.SpeedUnits).Distinct().Count() < 2)
        {
            return PumpFitResult.Fail("São necessárias pelo menos 2 velocidades distintas em cada faixa definida por St.");
        }

        FlowCalibrationCurve fitted;
        try
        {
            fitted = CalibrationMath.FitFlowCurve(
                valid.Select(p => (Voltage: p.SpeedUnits, Flow: p.FlowMlPerMin)), transitionSpeed,
                validateAsVoltage: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return PumpFitResult.Fail(ex.Message);
        }

        if (fitted.LowVoltage is not { } low || fitted.HighVoltage is not { } high)
        {
            return PumpFitResult.Fail("Os pontos não determinam as duas equações da bomba.");
        }

        var curve = new PumpDualRangeCurve(low, high, transitionSpeed);
        if (!curve.Validate(out var error))
        {
            // Keep the candidate for graph inspection; it remains invalid and cannot be applied.
            return Statistics(valid, curve, lowCount, highCount) with { IsValid = false, Error = error };
        }

        return Statistics(valid, curve, lowCount, highCount);
    }

    private static PumpFitResult Statistics(PumpCalibrationPoint[] points, PumpDualRangeCurve curve, int lowCount, int highCount)
    {
        var residuals = new double[points.Length];
        var sse = 0.0;
        var lowSse = 0.0;
        var highSse = 0.0;
        var mean = points.Average(p => p.FlowMlPerMin);
        var sst = 0.0;
        for (var i = 0; i < points.Length; i++)
        {
            var residual = points[i].FlowMlPerMin - curve.FlowFromSpeed(points[i].SpeedUnits);
            residuals[i] = residual;
            var squared = residual * residual;
            sse += squared;
            if (points[i].SpeedUnits <= curve.TransitionSpeed)
            {
                lowSse += squared;
            }
            else
            {
                highSse += squared;
            }

            var centered = points[i].FlowMlPerMin - mean;
            sst += centered * centered;
        }

        return new PumpFitResult
        {
            IsValid = true,
            Curve = curve,
            SSE = sse,
            RMSE = Math.Sqrt(sse / points.Length),
            RSquared = sst > 1e-12 ? 1.0 - sse / sst : 1.0,
            TotalPoints = points.Length,
            LowPointCount = lowCount,
            HighPointCount = highCount,
            LowSSE = lowSse,
            HighSSE = highSse,
            LowRMSE = Math.Sqrt(lowSse / lowCount),
            HighRMSE = Math.Sqrt(highSse / highCount),
            Residuals = residuals,
        };
    }
}
