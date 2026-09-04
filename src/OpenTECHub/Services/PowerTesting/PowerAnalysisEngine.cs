using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>
/// Pure implementation of the power-assay science (§4). Composes the primitives in
/// <see cref="PowerCalc"/>; holds no state. See <see cref="IPowerAnalysisEngine"/>.
/// </summary>
public sealed class PowerAnalysisEngine : IPowerAnalysisEngine
{
    public PowerPointResult AnalyzePoint(PowerPointInput input)
    {
        var geometry = input.Geometry;
        var calibration = input.Calibration;
        var fluid = input.Fluid;
        var rpm = input.MeanRpm;
        var tNom = calibration?.MotorRatedTorqueNm ?? input.MotorRatedTorqueNm;

        // Affine calibration is exact on the CI half-width too, so evaluate the torque at the mean
        // and at (mean + CI) and take the difference. Identity calibration collapses to (pct/100)·T_nom.
        double ToNm(double percent) => calibration is { } cal
            ? cal.Scale * (percent / 100.0 * cal.MotorRatedTorqueNm) + cal.Offset
            : percent / 100.0 * tNom;

        var meanTorqueNm = ToNm(input.MeanTorquePercent);
        var torqueCi95Nm = Math.Abs(ToNm(input.MeanTorquePercent + input.TorquePercentCi95) - meanTorqueNm);

        var omega = PowerCalc.AngularVelocity(rpm);
        var shaftPowerW = meanTorqueNm * omega;
        var powerCi95W = torqueCi95Nm * omega;

        var voidPowerW = input.Tare is { } tareForVoid
            ? TareInterpolator.InterpolatePowerW(tareForVoid, rpm)
            : 0.0;
        var netPowerW = shaftPowerW - voidPowerW;

        // Absolute results need BOTH a calibration and a tare (§9); otherwise the point is relative.
        var isRelative = calibration is null || input.Tare is null;

        // The SNR gate needs the tare's σ_τ; without a tare it cannot be judged (§7.2).
        var belowNoiseFloor = false;
        if (input.Tare is { } tareForNoise)
        {
            var sigmaTauPercent = TareInterpolator.InterpolateSigmaTauPercent(tareForNoise, rpm);
            // σ_τ is the scatter of the *reported* torque %. The calibration scales the physical
            // torque — and hence its scatter — by |Scale|, exactly as it scaled netPowerW above.
            // The watt-floor must carry the same factor, or a Scale≠1 calibration mis-scales the
            // gate and can flag a real point as noise (or pass a noisy one) by that factor.
            var calibrationScale = Math.Abs(calibration?.Scale ?? 1.0);
            var noiseFloorW = input.SnrFloorMultiple * calibrationScale * PowerCalc.PowerNoiseFloorW(sigmaTauPercent, tNom, rpm);
            belowNoiseFloor = Math.Abs(netPowerW) <= noiseFloorW;
        }

        var stages = new List<StageNpResult>();
        var referenceDiameterM = 0.0;
        var rho = fluid.DensityKgM3;
        var mu = fluid.ViscosityPaS;
        var count = geometry.Impellers.Count;

        if (count > 0)
        {
            // Equal-split hypothesis: total net power shared across the stages (§4.3).
            var perStageW = netPowerW / count;
            var perStageCi95W = powerCi95W / count;

            foreach (var impeller in geometry.Impellers.OrderBy(i => i.StageIndex))
            {
                var np = PowerCalc.PowerNumber(perStageW, rho, rpm, impeller.DiameterM);
                var re = PowerCalc.ReynoldsNumber(rho, rpm, impeller.DiameterM, mu);
                var npCi95 = Math.Abs(PowerCalc.PowerNumber(perStageCi95W, rho, rpm, impeller.DiameterM));
                stages.Add(new StageNpResult(impeller.StageIndex, impeller.Type, impeller.DiameterM, np, re, npCi95));
                referenceDiameterM = Math.Max(referenceDiameterM, impeller.DiameterM);
            }
        }

        var assemblyNp = referenceDiameterM > 0 ? PowerCalc.PowerNumber(netPowerW, rho, rpm, referenceDiameterM) : double.NaN;
        var assemblyRe = referenceDiameterM > 0 ? PowerCalc.ReynoldsNumber(rho, rpm, referenceDiameterM, mu) : double.NaN;
        var assemblyNpCi95 = referenceDiameterM > 0 ? Math.Abs(PowerCalc.PowerNumber(powerCi95W, rho, rpm, referenceDiameterM)) : double.NaN;

        return new PowerPointResult
        {
            MeanRpm = rpm,
            MeanTorqueNm = meanTorqueNm,
            ShaftPowerW = shaftPowerW,
            VoidPowerW = voidPowerW,
            NetPowerW = netPowerW,
            NetPowerCi95W = powerCi95W,
            BelowNoiseFloor = belowNoiseFloor,
            IsRelative = isRelative,
            Stages = stages,
            AssemblyPowerNumber = assemblyNp,
            AssemblyReynoldsNumber = assemblyRe,
            AssemblyPowerNumberCi95 = assemblyNpCi95,
            ReferenceDiameterM = referenceDiameterM,
        };
    }

    public PlateauFitResult FitPlateau(
        IEnumerable<(double ReynoldsNumber, double PowerNumber, double PowerNumberCi95)> points,
        double reCutoff)
    {
        var used = points
            .Where(p => p.ReynoldsNumber >= reCutoff && double.IsFinite(p.PowerNumber))
            .ToList();

        if (used.Count == 0)
        {
            return new PlateauFitResult { HasFit = false, ReynoldsCutoff = reCutoff };
        }

        var z = RunningStatistics.NormalZ95;

        // Inverse-variance (precision) weighting when every point carries a CI (§16); the tighter
        // a point's CI, the more it counts.
        if (used.All(p => p.PowerNumberCi95 > 0))
        {
            double sumWeight = 0, sumWeighted = 0;
            foreach (var p in used)
            {
                var sigma = p.PowerNumberCi95 / z;
                var weight = 1.0 / (sigma * sigma);
                sumWeight += weight;
                sumWeighted += weight * p.PowerNumber;
            }

            var weightedMean = sumWeighted / sumWeight;
            var combinedCi95 = z * Math.Sqrt(1.0 / sumWeight);
            return new PlateauFitResult
            {
                HasFit = true,
                PowerNumber = weightedMean,
                PowerNumberCi95 = combinedCi95,
                PointsUsed = used.Count,
                ReynoldsCutoff = reCutoff,
            };
        }

        // Fallback (some points have no CI): plain mean, CI from the scatter of the Np values.
        var mean = used.Average(p => p.PowerNumber);
        var ci = 0.0;
        if (used.Count > 1)
        {
            var variance = used.Sum(p => Math.Pow(p.PowerNumber - mean, 2)) / (used.Count - 1);
            ci = z * Math.Sqrt(variance) / Math.Sqrt(used.Count);
        }

        return new PlateauFitResult
        {
            HasFit = true,
            PowerNumber = mean,
            PowerNumberCi95 = ci,
            PointsUsed = used.Count,
            ReynoldsCutoff = reCutoff,
        };
    }

    public EnergyCorrelationResult FitEnergyCorrelation(IEnumerable<(double MechanicalW, double ElectricalW)> pairs)
    {
        var points = pairs
            .Where(p => double.IsFinite(p.MechanicalW) && double.IsFinite(p.ElectricalW))
            .ToList();

        if (points.Count < 2)
        {
            return new EnergyCorrelationResult { HasFit = false, PointCount = points.Count };
        }

        var xBar = points.Average(p => p.MechanicalW);
        var yBar = points.Average(p => p.ElectricalW);
        var sxx = points.Sum(p => Math.Pow(p.MechanicalW - xBar, 2));

        if (sxx <= 0)
        {
            // All at the same mechanical power — a slope is not identifiable.
            return new EnergyCorrelationResult { HasFit = false, PointCount = points.Count };
        }

        var sxy = points.Sum(p => (p.MechanicalW - xBar) * (p.ElectricalW - yBar));
        var slope = sxy / sxx;
        var intercept = yBar - slope * xBar;

        var ssTot = points.Sum(p => Math.Pow(p.ElectricalW - yBar, 2));
        var ssRes = points.Sum(p => Math.Pow(p.ElectricalW - (slope * p.MechanicalW + intercept), 2));
        var rSquared = ssTot > 0 ? 1.0 - ssRes / ssTot : 1.0;

        return new EnergyCorrelationResult
        {
            HasFit = true,
            Slope = slope,
            InterceptW = intercept,
            RSquared = rSquared,
            PointCount = points.Count,
        };
    }
}
