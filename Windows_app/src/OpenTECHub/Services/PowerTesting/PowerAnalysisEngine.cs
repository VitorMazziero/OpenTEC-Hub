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
        var fluid = input.Fluid;
        var rpm = input.MeanRpm;
        var tNom = input.MotorRatedTorqueNm;

        // O sistema utiliza diretamente o torque nominal do servo; a tara no ar é a calibração de base.
        double ToNm(double percent) => percent / 100.0 * tNom;

        var meanTorqueNm = ToNm(input.MeanTorquePercent);
        var torqueCi95Nm = Math.Abs(ToNm(input.MeanTorquePercent + input.TorquePercentCi95) - meanTorqueNm);

        var omega = PowerCalc.AngularVelocity(rpm);
        var shaftPowerW = meanTorqueNm * omega;
        var powerCi95W = torqueCi95Nm * omega;

        var voidPowerW = input.Tare is { } tareForVoid
            ? TareInterpolator.InterpolatePowerW(tareForVoid, rpm)
            : 0.0;
        var netPowerW = shaftPowerW - voidPowerW;
        var tarePowerCi95W = input.Tare is { } tareForUncertainty
            ? TareInterpolator.InterpolatePowerCi95W(tareForUncertainty, rpm)
            : 0.0;
        var netPowerCi95W = Math.Sqrt(powerCi95W * powerCi95W + tarePowerCi95W * tarePowerCi95W);

        // A tara no ar é a via de calibração do sistema: desconta o atrito mecânico de selos/mancais.
        // Com tara aplicada, o resultado é absoluto/calibrado; sem tara, o ensaio opera em modo relativo.
        var isRelative = input.Tare is null;

        // The SNR gate needs the tare's σ_τ; without a tare it cannot be judged (§7.2).
        var belowNoiseFloor = false;
        if (input.Tare is { } tareForNoise)
        {
            var sigmaTauPercent = TareInterpolator.InterpolateSigmaTauPercent(tareForNoise, rpm);
            var noiseFloorW = input.SnrFloorMultiple * PowerCalc.PowerNoiseFloorW(sigmaTauPercent, tNom, rpm);
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
            var perStageCi95W = netPowerCi95W / count;

            foreach (var impeller in geometry.Impellers.OrderBy(i => i.StageIndex))
            {
                var np = PowerCalc.PowerNumber(perStageW, rho, rpm, impeller.DiameterM);
                var re = PowerCalc.ReynoldsNumber(rho, rpm, impeller.DiameterM, mu);
                var npCi95 = Math.Abs(PowerCalc.PowerNumber(perStageCi95W, rho, rpm, impeller.DiameterM));
                stages.Add(new StageNpResult(impeller.StageIndex, impeller.Type, impeller.DiameterM, np, re, npCi95, impeller.Label));
                referenceDiameterM = Math.Max(referenceDiameterM, impeller.DiameterM);
            }
        }

        var assemblyNp = referenceDiameterM > 0 ? PowerCalc.PowerNumber(netPowerW, rho, rpm, referenceDiameterM) : double.NaN;
        var assemblyRe = referenceDiameterM > 0 ? PowerCalc.ReynoldsNumber(rho, rpm, referenceDiameterM, mu) : double.NaN;
        var assemblyNpCi95 = referenceDiameterM > 0 ? Math.Abs(PowerCalc.PowerNumber(netPowerCi95W, rho, rpm, referenceDiameterM)) : double.NaN;

        // Gassed and flooding evaluations (§4.5, §11, §16)
        double? gasFlowLpm = input.GasFlowLpm;
        double? gasFlowVvm = input.GasFlowVvm;
        if (gasFlowLpm is { } flow && flow >= 0)
        {
            if (gasFlowVvm is null && geometry.LiquidVolumeM3 > 0)
            {
                gasFlowVvm = PowerCalc.LpmToVvm(flow, geometry.LiquidVolumeM3);
            }
        }
        double? gasFlowNumber = (referenceDiameterM > 0 && gasFlowLpm.HasValue && gasFlowLpm.Value >= 0 && rpm > 0)
            ? PowerCalc.AerationNumber(gasFlowLpm.Value, rpm, referenceDiameterM)
            : null;
        double? froudeNumber = (referenceDiameterM > 0 && rpm > 0)
            ? PowerCalc.FroudeNumber(rpm, referenceDiameterM)
            : null;
        double? gassedPowerW = gasFlowLpm.HasValue ? netPowerW : null;

        double? refP0 = input.ReferenceP0W;
        double? refP0Ci = input.ReferenceP0Ci95W;
        var p0Prov = input.P0Provenance;
        double? powerRatio = null;
        double? powerRatioCi = null;

        if (refP0 is { } p0Val && p0Val > 0 && gassedPowerW.HasValue)
        {
            var (r, rCi) = PowerCalc.PropagatePowerRatioUncertainty(
                gassedPowerW.Value, netPowerCi95W, p0Val, refP0Ci ?? 0.0);
            powerRatio = r;
            powerRatioCi = rCi;
        }

        return new PowerPointResult
        {
            MeanRpm = rpm,
            MeanTorqueNm = meanTorqueNm,
            ShaftPowerW = shaftPowerW,
            VoidPowerW = voidPowerW,
            NetPowerW = netPowerW,
            NetPowerCi95W = netPowerCi95W,
            BelowNoiseFloor = belowNoiseFloor,
            IsRelative = isRelative,
            Stages = stages,
            AssemblyPowerNumber = assemblyNp,
            AssemblyReynoldsNumber = assemblyRe,
            AssemblyPowerNumberCi95 = assemblyNpCi95,
            ReferenceDiameterM = referenceDiameterM,
            GasFlowLpm = gasFlowLpm,
            GasFlowVvm = gasFlowVvm,
            GasFlowNumber = gasFlowNumber,
            FroudeNumber = froudeNumber,
            GassedPowerW = gassedPowerW,
            ReferenceP0W = refP0,
            ReferenceP0Ci95W = refP0Ci,
            P0Provenance = p0Prov,
            PowerRatio = powerRatio,
            PowerRatioCi95 = powerRatioCi,
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

    public (double? P0W, double? Ci95P0W, P0Provenance Provenance) ResolveReferenceP0(
        double rpm,
        PowerTestDocument doc,
        PlateauFitResult? plateauFit = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (rpm <= 0)
        {
            return (null, null, P0Provenance.None);
        }

        // 1st: Reconstruct from fitted plateau (§4.5)
        var fit = plateauFit;
        if (fit is null || !fit.HasFit)
        {
            var ungassedPoints = doc.Runs
                .Where(r => r.Phase == PowerRunPhase.Accepted &&
                            r.GasMode == PowerGasMode.Ungassed &&
                            r.Analysis is not null)
                .Select(r => (
                    r.Analysis!.AssemblyReynoldsNumber,
                    r.Analysis.AssemblyPowerNumber,
                    r.Analysis.AssemblyPowerNumberCi95))
                .ToList();
            fit = FitPlateau(ungassedPoints, reCutoff: 10_000);
        }

        if (fit.HasFit && doc.Geometry.Impellers.Count > 0)
        {
            var nRps = PowerCalc.RevPerSecond(rpm);
            var rho = doc.Fluid.DensityKgM3;
            var dRef = doc.Geometry.Impellers.Max(i => i.DiameterM);
            if (dRef > 0)
            {
                var factor = rho * Math.Pow(nRps, 3) * Math.Pow(dRef, 5);
                var p0 = fit.PowerNumber * factor;
                var p0Ci = fit.PowerNumberCi95 * factor;
                return (p0, p0Ci, P0Provenance.PlateauFit);
            }
        }

        // 2nd: Fallback to measured ungassed point in same test at same N (±1 rpm)
        var match = doc.Runs.FirstOrDefault(r =>
            r.Phase == PowerRunPhase.Accepted &&
            r.GasMode == PowerGasMode.Ungassed &&
            r.NetPowerW is { } np && np > 0 &&
            Math.Abs(r.AgitationRpm - rpm) <= 1.0);

        if (match is not null)
        {
            return (match.NetPowerW, match.Ci95PowerW, P0Provenance.MeasuredUngassed);
        }

        // 3rd: Missing -> null
        return (null, null, P0Provenance.None);
    }

    public FloodingAnalysisResult? DetectFlooding(
        IReadOnlyList<PowerRunSummary> runs,
        PowerGeometry geometry,
        int referenceStageIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(geometry);

        var validPoints = runs
            .Where(r => r.Phase == PowerRunPhase.Accepted &&
                        r.GasMode != PowerGasMode.Ungassed &&
                        r.PowerRatio is { } ratio && double.IsFinite(ratio) &&
                        r.GasFlowNumber is { } flg && double.IsFinite(flg))
            .OrderBy(r => r.GasFlowNumber!.Value)
            .ToList();

        if (validPoints.Count < 3)
        {
            return null;
        }

        // Find minimum of PowerRatio x Fl_G
        var minRun = validPoints[0];
        for (var i = 1; i < validPoints.Count; i++)
        {
            if (validPoints[i].PowerRatio!.Value < minRun.PowerRatio!.Value)
            {
                minRun = validPoints[i];
            }
        }

        var refImpeller = geometry.Impellers.FirstOrDefault(i => i.StageIndex == referenceStageIndex)
            ?? geometry.Impellers.FirstOrDefault()
            ?? new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.06 };

        var rpm = minRun.MeanRpmMeasured > 0 ? minRun.MeanRpmMeasured : minRun.AgitationRpm;
        var fr = minRun.FroudeNumber ?? PowerCalc.FroudeNumber(rpm, refImpeller.DiameterM);
        var nienowFlG = PowerCalc.NienowFloodingAerationNumber(refImpeller.DiameterM, geometry.VesselDiameterM, fr);
        var relDev = nienowFlG > 0 ? (minRun.GasFlowNumber!.Value - nienowFlG) / nienowFlG * 100.0 : 0.0;

        return new FloodingAnalysisResult
        {
            ExperimentalFlG = minRun.GasFlowNumber!.Value,
            ExperimentalRpm = rpm,
            ExperimentalFlowLpm = minRun.GasFlowLpm ?? 0.0,
            TheoreticalFlGNienow = nienowFlG,
            RelativeDeviationPercent = relDev,
            ReferenceStageIndex = refImpeller.StageIndex,
            ReferenceImpellerType = refImpeller.Type,
            Method = FloodingDetectionMethod.Automatic,
            DeterminedUtc = DateTimeOffset.UtcNow,
            Notes = $"Transição experimental identificada no mínimo PG/P0 = {minRun.PowerRatio!.Value:F3}",
        };
    }

    public IReadOnlyList<(double Rpm, double FlowLpm, double FlG, double Fr)> GenerateNienowBoundary(
        PowerGeometry geometry,
        double minRpm,
        double maxRpm,
        int stepCount = 20,
        int referenceStageIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (minRpm <= 0 || maxRpm <= minRpm || stepCount < 2)
        {
            return [];
        }

        var refImpeller = geometry.Impellers.FirstOrDefault(i => i.StageIndex == referenceStageIndex)
            ?? geometry.Impellers.FirstOrDefault()
            ?? new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.06 };

        var list = new List<(double Rpm, double FlowLpm, double FlG, double Fr)>();
        var step = (maxRpm - minRpm) / (stepCount - 1);

        for (var i = 0; i < stepCount; i++)
        {
            var rpm = minRpm + i * step;
            var fr = PowerCalc.FroudeNumber(rpm, refImpeller.DiameterM);
            var flG = PowerCalc.NienowFloodingAerationNumber(refImpeller.DiameterM, geometry.VesselDiameterM, fr);
            var flowLpm = PowerCalc.NienowFloodingGasFlowLpm(rpm, refImpeller.DiameterM, geometry.VesselDiameterM);
            list.Add((rpm, flowLpm, flG, fr));
        }

        return list;
    }
}
