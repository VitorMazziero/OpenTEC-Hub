using System;
using System.Collections.Generic;
using System.Linq;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>
/// Scientific implementation of 2D power mapping, continuous flooding boundaries, and kLa multivariate regression.
/// </summary>
public sealed class PowerMapEngine : IPowerMapEngine
{
    private const double DefaultGradientTolerance = 1e-6;
    private const int DefaultGradientIterations = 400;

    public PowerMapSurfaceData ReconstructSurface(
        IReadOnlyList<PowerMapAnchorPoint> anchors,
        PowerGeometry geometry,
        FluidProperties fluid,
        PowerMapAlgorithmSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(fluid);

        settings ??= new PowerMapAlgorithmSettings();

        var resN = Math.Clamp(settings.ResolutionN, 10, 500);
        var resQg = Math.Clamp(settings.ResolutionQg, 10, 500);

        var rpmMin = settings.MinRpm;
        var rpmMax = settings.MaxRpm;
        var flowMin = settings.MinFlowLpm;
        var flowMax = settings.MaxFlowLpm;

        // The grid must cover the measured points, not the machine's whole envelope. Taking the
        // union with the 15-1000 rpm defaults spent almost every cell outside the convex hull,
        // so the heatmap rendered as a small island in a mostly empty plot.
        var validAnchors = anchors.Where(a => a.AgitationRpm > 0 && a.NetPowerW >= 0).ToList();
        if (settings.AutoFitDomain && validAnchors.Count >= 3)
        {
            rpmMin = validAnchors.Min(a => a.AgitationRpm);
            rpmMax = validAnchors.Max(a => a.AgitationRpm);
            flowMin = validAnchors.Min(a => a.GasFlowLpm);
            flowMax = validAnchors.Max(a => a.GasFlowLpm);
        }

        if (rpmMax <= rpmMin) rpmMax = rpmMin + 100.0;
        if (flowMax <= flowMin) flowMax = flowMin + 5.0;

        var rpmGrid = new double[resN];
        for (var i = 0; i < resN; i++)
        {
            rpmGrid[i] = rpmMin + (i * (rpmMax - rpmMin) / (resN - 1));
        }

        var flowGrid = new double[resQg];
        for (var j = 0; j < resQg; j++)
        {
            flowGrid[j] = flowMin + (j * (flowMax - flowMin) / (resQg - 1));
        }

        var totalCells = resN * resQg;
        var pNetSurface = new double?[totalCells];
        var pVolumetricSurface = new double?[totalCells];
        var powerRatioSurface = new double?[totalCells];

        // Prepare points for CloughTocher2D (needs at least 3 non-collinear points in normalized [0,1] domain)
        var spanRpm = rpmMax - rpmMin;
        var spanFlow = flowMax - flowMin;
        if (spanRpm <= 0) spanRpm = 1.0;
        if (spanFlow <= 0) spanFlow = 1.0;

        double NormQ(double q) => (q - flowMin) / spanFlow;
        double NormN(double n) => (n - rpmMin) / spanRpm;

        var pNetPoints = new List<(double Q, double N, double Value)>();
        var ratioPoints = new List<(double Q, double N, double Value)>();

        foreach (var a in anchors)
        {
            if (a.AgitationRpm >= 0 && a.GasFlowLpm >= 0 && !double.IsNaN(a.NetPowerW))
            {
                pNetPoints.Add((NormQ(a.GasFlowLpm), NormN(a.AgitationRpm), a.NetPowerW));
            }

            if (a.AgitationRpm >= 0 && a.GasFlowLpm >= 0 && a.PowerRatio.HasValue && !double.IsNaN(a.PowerRatio.Value))
            {
                ratioPoints.Add((NormQ(a.GasFlowLpm), NormN(a.AgitationRpm), a.PowerRatio.Value));
            }
        }

        var gradientTolerance = settings.GradientTolerance > 0 && double.IsFinite(settings.GradientTolerance)
            ? settings.GradientTolerance
            : DefaultGradientTolerance;
        var gradientIterations = Math.Clamp(settings.GradientIterations, 50, 5000);

        CloughTocher2D? ctNet = null;
        if (pNetPoints.Count >= 3)
        {
            try
            {
                ctNet = new CloughTocher2D(pNetPoints, gradientTolerance, gradientIterations);
            }
            catch
            {
                // Degenerate or collinear points: surface remains null outside direct evaluations
            }
        }

        CloughTocher2D? ctRatio = null;
        if (ratioPoints.Count >= 3)
        {
            try
            {
                ctRatio = new CloughTocher2D(ratioPoints, gradientTolerance, gradientIterations);
            }
            catch
            {
                // Degenerate points
            }
        }

        // P/V has to agree with the anchors. When the map's geometry carries no working volume
        // (a freshly created map keeps LiquidVolumeM3 at zero), recover the volume the anchors
        // were reduced with instead of leaving the whole P/V layer null.
        var liquidVolume = geometry.LiquidVolumeM3;
        if (liquidVolume <= 0)
        {
            var inferred = anchors
                .Where(a => a.VolumetricPowerWm3 > 0 && a.NetPowerW > 0 && double.IsFinite(a.VolumetricPowerWm3))
                .Select(a => a.NetPowerW / a.VolumetricPowerWm3)
                .Where(v => v > 0 && double.IsFinite(v))
                .OrderBy(v => v)
                .ToList();

            if (inferred.Count > 0)
            {
                liquidVolume = inferred[inferred.Count / 2];
            }
        }

        for (var iN = 0; iN < resN; iN++)
        {
            var rpm = rpmGrid[iN];
            var normN = NormN(rpm);

            for (var iQ = 0; iQ < resQg; iQ++)
            {
                var q = flowGrid[iQ];
                var normQ = NormQ(q);
                var idx = (iN * resQg) + iQ;

                if (ctNet != null && ctNet.TryEvaluate(normQ, normN, out var netVal))
                {
                    // Mechanical net power must be non-negative
                    var pNet = Math.Max(0.0, netVal);
                    pNetSurface[idx] = pNet;

                    if (liquidVolume > 0)
                    {
                        pVolumetricSurface[idx] = pNet / liquidVolume;
                    }
                }

                if (ctRatio != null && ctRatio.TryEvaluate(normQ, normN, out var ratioVal))
                {
                    powerRatioSurface[idx] = Math.Clamp(ratioVal, 0.0, 1.5);
                }
            }
        }

        return new PowerMapSurfaceData
        {
            ResolutionN = resN,
            ResolutionQg = resQg,
            MinRpm = rpmMin,
            MaxRpm = rpmMax,
            MinFlowLpm = flowMin,
            MaxFlowLpm = flowMax,
            RpmGrid = rpmGrid,
            FlowGrid = flowGrid,
            PNetSurface = pNetSurface,
            PVolumetricSurface = pVolumetricSurface,
            PowerRatioSurface = powerRatioSurface,
            AnchorPoints = anchors,
        };
    }

    public PowerMapFloodingBoundary ComputeFloodingBoundary(
        PowerGeometry geometry,
        IReadOnlyList<FloodingPoint>? experimentalPoints = null,
        double minRpm = 50.0,
        double maxRpm = 1000.0,
        int pointsCount = 50)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        var impeller = geometry.Impellers.Count > 0
            ? geometry.Impellers[0]
            : new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.060 };

        var d = impeller.DiameterM > 0 ? impeller.DiameterM : 0.060;
        var t = geometry.VesselDiameterM > 0 ? geometry.VesselDiameterM : 0.190;

        pointsCount = Math.Max(10, pointsCount);
        minRpm = Math.Max(15.0, minRpm);
        maxRpm = Math.Max(minRpm + 10.0, maxRpm);

        var nienowPoints = new List<FloodingPoint>(pointsCount);

        for (var i = 0; i < pointsCount; i++)
        {
            var rpm = minRpm + (i * (maxRpm - minRpm) / (pointsCount - 1));
            var flowLpm = PowerCalc.NienowFloodingGasFlowLpm(rpm, d, t);
            var fr = PowerCalc.FroudeNumber(rpm, d);
            var flG = PowerCalc.NienowFloodingAerationNumber(d, t, fr);

            nienowPoints.Add(new FloodingPoint
            {
                AgitationRpm = rpm,
                GasFlowLpm = flowLpm,
                GasFlowNumber = flG,
                FroudeNumber = fr,
            });
        }

        var sortedExp = experimentalPoints?
            .OrderBy(p => p.AgitationRpm)
            .ToList() ?? [];

        return new PowerMapFloodingBoundary
        {
            ImpellerType = impeller.Type,
            ImpellerDiameterM = d,
            VesselDiameterM = t,
            ExperimentalPoints = sortedExp,
            NienowTheoreticalPoints = nienowPoints,
        };
    }

    public KlaCorrelationResult FitVanTRietModel(
        IReadOnlyList<KlaPowerPair> pairs,
        out IReadOnlyList<KlaPowerPair> updatedPairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var validList = new List<KlaPowerPair>();
        var excludedNotes = new List<string>();

        for (var i = 0; i < pairs.Count; i++)
        {
            var p = pairs[i];
            if (p.KlaPerHour <= 0)
            {
                excludedNotes.Add($"Ponto #{i + 1} (N={p.AgitationRpm:F0}, Q={p.GasFlowLpm:F1}): kLa não-positivo ({p.KlaPerHour:F2} 1/h) recusado.");
                continue;
            }
            if (p.VolumetricPowerWm3 <= 0)
            {
                excludedNotes.Add($"Ponto #{i + 1} (N={p.AgitationRpm:F0}, Q={p.GasFlowLpm:F1}): P/V não-positivo ({p.VolumetricPowerWm3:F1} W/m³) recusado.");
                continue;
            }
            if (p.SuperficialVelocityMs <= 0)
            {
                excludedNotes.Add($"Ponto #{i + 1} (N={p.AgitationRpm:F0}, Q={p.GasFlowLpm:F1}): velocidade superficial v_s não-positiva ({p.SuperficialVelocityMs:F5} m/s) recusada.");
                continue;
            }

            validList.Add(p);
        }

        var n = validList.Count;
        if (n < 4)
        {
            var emptyResult = new KlaCorrelationResult
            {
                FittedAtUtc = DateTimeOffset.UtcNow,
                ValidPointsCount = n,
                DegreesOfFreedom = Math.Max(0, n - 3),
                ExcludedPointsNotes = excludedNotes.Count > 0 ? excludedNotes : [$"Dados insuficientes para regressão: requer ao menos 4 pontos com kLa > 0, P/V > 0 e v_s > 0 (fornecidos: {n})."],
            };

            updatedPairs = pairs;
            return emptyResult;
        }

        // Multivariable OLS: ln(kLa) = ln(K) + alpha * ln(P/V) + beta * ln(vs)
        // y = ln(kLa), X = [1, ln(P/V), ln(vs)]
        // X^T * X: 3x3 matrix
        double s00 = n, s01 = 0, s02 = 0;
        double s11 = 0, s12 = 0, s22 = 0;
        double rhs0 = 0, rhs1 = 0, rhs2 = 0;
        double ySum = 0;

        var yArr = new double[n];
        var x1Arr = new double[n];
        var x2Arr = new double[n];

        for (var i = 0; i < n; i++)
        {
            var y = Math.Log(validList[i].KlaPerHour);
            var x1 = Math.Log(validList[i].VolumetricPowerWm3);
            var x2 = Math.Log(validList[i].SuperficialVelocityMs);

            yArr[i] = y;
            x1Arr[i] = x1;
            x2Arr[i] = x2;

            ySum += y;

            s01 += x1;
            s02 += x2;
            s11 += x1 * x1;
            s12 += x1 * x2;
            s22 += x2 * x2;

            rhs0 += y;
            rhs1 += y * x1;
            rhs2 += y * x2;
        }

        // Invert 3x3 symmetric matrix M = X^T X
        // [s00 s01 s02]
        // [s01 s11 s12]
        // [s02 s12 s22]
        var m00 = s00; var m01 = s01; var m02 = s02;
        var m10 = s01; var m11 = s11; var m12 = s12;
        var m20 = s02; var m21 = s12; var m22 = s22;

        var c00 = (m11 * m22) - (m12 * m21);
        var c01 = -((m10 * m22) - (m12 * m20));
        var c02 = (m10 * m21) - (m11 * m20);

        var c10 = -((m01 * m22) - (m02 * m21));
        var c11 = (m00 * m22) - (m02 * m20);
        var c12 = -((m00 * m21) - (m01 * m20));

        var c20 = (m01 * m12) - (m02 * m11);
        var c21 = -((m00 * m12) - (m02 * m10));
        var c22 = (m00 * m11) - (m01 * m10);

        var det = (m00 * c00) + (m01 * c01) + (m02 * c02);

        if (Math.Abs(det) < 1e-12)
        {
            var singularResult = new KlaCorrelationResult
            {
                FittedAtUtc = DateTimeOffset.UtcNow,
                ValidPointsCount = n,
                DegreesOfFreedom = n - 3,
                ExcludedPointsNotes = ["A matriz de projeto X^T X é singular ou colinear (variação insuficiente em P/V ou v_s)."],
            };
            updatedPairs = pairs;
            return singularResult;
        }

        var invDet = 1.0 / det;
        var inv00 = c00 * invDet; var inv01 = c10 * invDet; var inv02 = c20 * invDet;
        var inv10 = c01 * invDet; var inv11 = c11 * invDet; var inv12 = c21 * invDet;
        var inv20 = c02 * invDet; var inv21 = c12 * invDet; var inv22 = c22 * invDet;

        // b = M^-1 * rhs
        var b0 = (inv00 * rhs0) + (inv01 * rhs1) + (inv02 * rhs2);
        var b1 = (inv10 * rhs0) + (inv11 * rhs1) + (inv12 * rhs2);
        var b2 = (inv20 * rhs0) + (inv21 * rhs1) + (inv22 * rhs2);

        var k = Math.Exp(b0);
        var alpha = b1;
        var beta = b2;

        // Residuals and variance
        var yMean = ySum / n;
        double ssr = 0.0;
        double sst = 0.0;

        for (var i = 0; i < n; i++)
        {
            var yPred = b0 + (b1 * x1Arr[i]) + (b2 * x2Arr[i]);
            var res = yArr[i] - yPred;
            ssr += res * res;
            var dev = yArr[i] - yMean;
            sst += dev * dev;
        }

        var df = n - 3;
        var s2 = df > 0 ? ssr / df : 0.0;
        var rmse = Math.Sqrt(s2);
        var r2 = sst > 0 ? Math.Clamp(1.0 - (ssr / sst), 0.0, 1.0) : 0.0;
        var adjR2 = (sst > 0 && df > 0) ? Math.Clamp(1.0 - ((ssr / df) / (sst / (n - 1))), 0.0, 1.0) : r2;

        var seB0 = Math.Sqrt(Math.Max(0.0, s2 * inv00));
        var seAlpha = Math.Sqrt(Math.Max(0.0, s2 * inv11));
        var seBeta = Math.Sqrt(Math.Max(0.0, s2 * inv22));
        var seK = k * seB0; // Delta method approximation

        var covMatrix = new double[][]
        {
            [s2 * inv00, s2 * inv01, s2 * inv02],
            [s2 * inv10, s2 * inv11, s2 * inv12],
            [s2 * inv20, s2 * inv21, s2 * inv22],
        };

        // Update pair records with predicted kLa and residuals
        var outPairs = new List<KlaPowerPair>(pairs.Count);
        foreach (var p in pairs)
        {
            if (p.VolumetricPowerWm3 > 0 && p.SuperficialVelocityMs > 0 && p.KlaPerHour > 0)
            {
                var pred = k * Math.Pow(p.VolumetricPowerWm3, alpha) * Math.Pow(p.SuperficialVelocityMs, beta);
                var residual = p.KlaPerHour - pred;
                var relErr = pred > 0 ? Math.Abs(residual) / pred : 0.0;

                outPairs.Add(p with
                {
                    PredictedKlaPerHour = pred,
                    Residual = residual,
                    RelativeErrorFraction = relErr,
                });
            }
            else
            {
                outPairs.Add(p);
            }
        }

        updatedPairs = outPairs;

        return new KlaCorrelationResult
        {
            FittedAtUtc = DateTimeOffset.UtcNow,
            K = k,
            Alpha = alpha,
            Beta = beta,
            StdErrorK = seK,
            StdErrorAlpha = seAlpha,
            StdErrorBeta = seBeta,
            R2 = r2,
            AdjustedR2 = adjR2,
            RootMeanSquareError = rmse,
            CovarianceMatrix = covMatrix,
            ValidPointsCount = n,
            DegreesOfFreedom = df,
            ModelFormula = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"kLa = {k:F6} · (P/V)^{alpha:F4} · (vs)^{beta:F4}"),
            ExcludedPointsNotes = excludedNotes,
        };
    }

    public double EstimateSpecificPowerForKla(
        double targetKla,
        double superficialVelocityMs,
        KlaCorrelationResult correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        return PowerCalc.ScaleUpRequiredVolumetricPower(
            targetKla,
            superficialVelocityMs,
            correlation.K,
            correlation.Alpha,
            correlation.Beta);
    }
}
