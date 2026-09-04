using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>
/// The pure dimensional and statistical primitives of the power assay (§4, §12.1). No state,
/// no I/O — every value here is reproducible from its inputs, which is why the whole analysis
/// can be re-run in review. All rotations arrive in rpm; conversions to rev/s are explicit.
/// </summary>
public static class PowerCalc
{
    /// <summary>Standard gravity used in the Froude number (§4.5).</summary>
    public const double GravityMetersPerSecondSquared = 9.80665;

    private const double TwoPiOverSixty = 2.0 * Math.PI / 60.0;   // rpm → rad/s

    /// <summary>Angular velocity ω = 2π·N/60 [rad/s] from rpm.</summary>
    public static double AngularVelocity(double rpm) => rpm * TwoPiOverSixty;

    /// <summary>Rotational speed in rev/s, the unit every dimensionless group needs.</summary>
    public static double RevPerSecond(double rpm) => rpm / 60.0;

    /// <summary>Mechanical shaft power P = τ·ω [W]. Mechanical estimate, not electrical draw (§4.1).</summary>
    public static double ShaftPower(double torqueNm, double rpm) => torqueNm * AngularVelocity(rpm);

    /// <summary>
    /// Torque in N·m from the drive's signed % of nominal, with the static calibration applied
    /// (§9.1). Phase 1 uses scale only; <see cref="TorqueCalibration.Offset"/> defaults to 0.
    /// With the identity calibration this is simply (pct/100)·T_nom.
    /// </summary>
    public static double CalibratedTorqueNm(double torquePercent, TorqueCalibration? calibration)
    {
        var tNom = calibration?.MotorRatedTorqueNm ?? 1.27;
        var raw = torquePercent / 100.0 * tNom;
        if (calibration is null)
        {
            return raw;
        }
        return calibration.Scale * raw + calibration.Offset;
    }

    /// <summary>Impeller Reynolds number Re = ρ·N·D²/μ [–] (§4.3), N in rev/s.</summary>
    public static double ReynoldsNumber(double densityKgM3, double rpm, double diameterM, double viscosityPaS)
    {
        if (viscosityPaS <= 0)
        {
            return double.NaN;
        }
        var n = RevPerSecond(rpm);
        return densityKgM3 * n * diameterM * diameterM / viscosityPaS;
    }

    /// <summary>Power number Np = P/(ρ·N³·D⁵) [–] (§4.3), N in rev/s.</summary>
    public static double PowerNumber(double powerW, double densityKgM3, double rpm, double diameterM)
    {
        var n = RevPerSecond(rpm);
        var denom = densityKgM3 * n * n * n * Math.Pow(diameterM, 5);
        return denom > 0 ? powerW / denom : double.NaN;
    }

    /// <summary>Aeration (gas flow) number Fl_G = Q_g/(N·D³) [–] (§4.5). Phase-2 use; N in rev/s.</summary>
    public static double AerationNumber(double gasFlowLpm, double rpm, double diameterM)
    {
        var n = RevPerSecond(rpm);
        var qM3S = gasFlowLpm / 60000.0;   // L/min → m³/s
        var denom = n * Math.Pow(diameterM, 3);
        return denom > 0 ? qM3S / denom : double.NaN;
    }

    /// <summary>Froude number Fr = N²·D/g [–] (§4.5). Phase-2 use; N in rev/s.</summary>
    public static double FroudeNumber(double rpm, double diameterM)
    {
        var n = RevPerSecond(rpm);
        return n * n * diameterM / GravityMetersPerSecondSquared;
    }

    /// <summary>
    /// The power noise floor at a rotation, from the tare's torque scatter σ_τ (in % of nominal):
    /// σ_power = (σ_τ/100)·T_nom·ω [W]. A net power below k·this is "below the noise" (§7.2).
    /// </summary>
    public static double PowerNoiseFloorW(double sigmaTauPercent, double motorRatedTorqueNm, double rpm)
        => sigmaTauPercent / 100.0 * motorRatedTorqueNm * AngularVelocity(rpm);

    /// <summary>
    /// Computes static 1-point torque calibration (§9.1):
    /// τ_ref = massKg · g · leverArmM
    /// τ_measured = (torquePercent / 100) · motorRatedTorqueNm
    /// Scale = τ_ref / τ_measured, Offset = 0
    /// </summary>
    public static TorqueCalibration ComputeStaticTorqueCalibration(
        double massKg,
        double leverArmM,
        double torquePercent,
        double motorRatedTorqueNm = 1.27)
    {
        if (massKg <= 0 || !double.IsFinite(massKg))
            throw new ArgumentOutOfRangeException(nameof(massKg), "A massa deve ser positiva.");
        if (leverArmM <= 0 || !double.IsFinite(leverArmM))
            throw new ArgumentOutOfRangeException(nameof(leverArmM), "O braço de alavanca deve ser positivo.");
        if (torquePercent <= 0 || !double.IsFinite(torquePercent))
            throw new ArgumentOutOfRangeException(nameof(torquePercent), "O torque medido deve ser positivo.");
        if (motorRatedTorqueNm <= 0 || !double.IsFinite(motorRatedTorqueNm))
            throw new ArgumentOutOfRangeException(nameof(motorRatedTorqueNm), "O torque nominal do motor deve ser positivo.");

        var refNm = massKg * GravityMetersPerSecondSquared * leverArmM;
        var measuredNm = (torquePercent / 100.0) * motorRatedTorqueNm;
        var scale = refNm / measuredNm;

        return new TorqueCalibration
        {
            Scale = scale,
            Offset = 0.0,
            ReferenceNm = refNm,
            ReferenceMassKg = massKg,
            LeverArmM = leverArmM,
            MotorRatedTorqueNm = motorRatedTorqueNm,
            CalibratedUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Fits an affine line P_elec = a * P_mech + b from pairs of (P_mech, P_elec) (§4.8, §12.3).
    /// Returns (Slope, Intercept, R2).
    /// </summary>
    public static (double Slope, double Intercept, double R2)? FitElectricalCorrelation(
        IReadOnlyList<(double MechW, double ElecW)> points)
    {
        if (points == null || points.Count < 2) return null;
        var n = points.Count;
        double sumX = 0, sumY = 0, sumX2 = 0, sumY2 = 0, sumXY = 0;
        foreach (var (x, y) in points)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
            sumX += x;
            sumY += y;
            sumX2 += x * x;
            sumY2 += y * y;
            sumXY += x * y;
        }
        var denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-12) return null;
        var slope = (n * sumXY - sumX * sumY) / denom;
        var intercept = (sumY - slope * sumX) / n;

        var yMean = sumY / n;
        double ssTot = 0, ssRes = 0;
        foreach (var (x, y) in points)
        {
            var yPred = slope * x + intercept;
            ssTot += (y - yMean) * (y - yMean);
            ssRes += (y - yPred) * (y - yPred);
        }
        var r2 = ssTot > 1e-12 ? Math.Max(0.0, 1.0 - (ssRes / ssTot)) : 1.0;
        return (slope, intercept, r2);
    }
}

/// <summary>
/// Welford's online mean/variance, so the runner can accumulate a capture window and read the
/// mean, standard error and 95 % CI after every sample without holding the raw buffer (§12.1,
/// Porta 2). The CI uses the normal approximation ±1.96·SE; autocorrelation is ignored for now,
/// so the CI is a lower bound on the true uncertainty (§12.1, Q6).
/// </summary>
public sealed class RunningStatistics
{
    private double _mean;
    private double _m2;

    public const double NormalZ95 = 1.959963984540054;

    public int Count { get; private set; }
    public double Mean => Count > 0 ? _mean : 0.0;

    /// <summary>
    /// Sample variance (n−1). Zero for fewer than two samples. Clamped at zero: near-constant
    /// input can leave <c>_m2</c> a tiny negative through float cancellation, and an unclamped
    /// √(negative) would make the standard error — and the adaptive-stop CI — NaN.
    /// </summary>
    public double Variance => Count > 1 ? Math.Max(0.0, _m2 / (Count - 1)) : 0.0;
    public double StandardDeviation => Math.Sqrt(Variance);

    /// <summary>Standard error of the mean σ/√n. Zero for fewer than two samples.</summary>
    public double StandardError => Count > 1 ? StandardDeviation / Math.Sqrt(Count) : 0.0;

    /// <summary>Half-width of the 95 % confidence interval on the mean, ±1.96·SE.</summary>
    public double ConfidenceHalfWidth95 => NormalZ95 * StandardError;

    public void Add(double value)
    {
        Count++;
        var delta = value - _mean;
        _mean += delta / Count;
        _m2 += delta * (value - _mean);
    }

    public void Reset()
    {
        _mean = 0;
        _m2 = 0;
        Count = 0;
    }
}

/// <summary>Linear interpolation of the tare curve — void power and noise floor at any rpm (§4.2, §9.2).</summary>
public static class TareInterpolator
{
    /// <summary>Void mechanical power P_void(N) [W], linear between rungs, clamped at the ends.</summary>
    public static double InterpolatePowerW(TareCurve tare, double rpm)
        => Interpolate(tare, rpm, static p => p.PVoidW);

    /// <summary>Torque scatter σ_τ(N) [% of nominal], linear between rungs, clamped at the ends.</summary>
    public static double InterpolateSigmaTauPercent(TareCurve tare, double rpm)
        => Interpolate(tare, rpm, static p => p.SigmaTauPercent);

    private static double Interpolate(TareCurve tare, double rpm, Func<TarePoint, double> selector)
    {
        var points = tare.Points;
        if (points is null || points.Count == 0)
        {
            return 0.0;
        }
        if (points.Count == 1)
        {
            return selector(points[0]);
        }

        var ordered = points.OrderBy(p => p.Rpm).ToList();
        if (rpm <= ordered[0].Rpm)
        {
            return selector(ordered[0]);
        }
        if (rpm >= ordered[^1].Rpm)
        {
            return selector(ordered[^1]);
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            if (rpm <= ordered[i].Rpm)
            {
                var lo = ordered[i - 1];
                var hi = ordered[i];
                var span = hi.Rpm - lo.Rpm;
                if (span <= 0)
                {
                    return selector(lo);
                }
                var t = (rpm - lo.Rpm) / span;
                return selector(lo) + t * (selector(hi) - selector(lo));
            }
        }

        return selector(ordered[^1]);
    }
}
