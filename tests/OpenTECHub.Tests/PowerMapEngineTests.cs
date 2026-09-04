using System;
using System.Collections.Generic;
using System.Linq;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PowerMapEngineTests
{
    private readonly PowerMapEngine _engine = new();

    [Fact]
    public void PowerCalc_GasSuperficialVelocity_And_VolumetricPower_And_ScaleUp_Primitives()
    {
        // 1. Gas superficial velocity: T = 0.190 m, Qg = 5.0 L/min
        var vs = PowerCalc.GasSuperficialVelocity(5.0, 0.190);
        var expectedArea = Math.PI / 4.0 * 0.190 * 0.190;
        var expectedQ = 5.0 / 60000.0;
        Assert.Equal(expectedQ / expectedArea, vs, precision: 6);
        Assert.True(vs > 0.0029 && vs < 0.0030);

        // Degenerate inputs
        Assert.Equal(0.0, PowerCalc.GasSuperficialVelocity(-1.0, 0.190));
        Assert.Equal(0.0, PowerCalc.GasSuperficialVelocity(5.0, 0.0));

        // 2. Volumetric power: P = 4.5 W, VL = 0.010 m³ (10 L)
        var pv = PowerCalc.VolumetricPower(4.5, 0.010);
        Assert.Equal(450.0, pv, precision: 4);
        Assert.True(double.IsNaN(PowerCalc.VolumetricPower(4.5, 0.0)));

        // 3. Inverse scale-up estimator: kLa = K * (P/V)^alpha * (vs)^beta
        // Let K = 0.026, alpha = 0.5, beta = 0.4, vs = 0.005, target kLa = 50 1/h
        var k = 0.026;
        var alpha = 0.5;
        var beta = 0.4;
        var targetKla = 50.0;
        var testVs = 0.005;

        var requiredPv = PowerCalc.ScaleUpRequiredVolumetricPower(targetKla, testVs, k, alpha, beta);
        Assert.True(requiredPv > 0);

        // Verify roundtrip
        var predictedKla = k * Math.Pow(requiredPv, alpha) * Math.Pow(testVs, beta);
        Assert.Equal(targetKla, predictedKla, precision: 4);
    }

    [Fact]
    public void PowerMapEngine_ReconstructSurface_RegularGrid_And_ConvexHull_Nulls()
    {
        var geom = new PowerGeometry
        {
            Impellers = [new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.060 }],
            VesselDiameterM = 0.190,
            LiquidVolumeM3 = 0.010,
        };
        var fluid = new FluidProperties { DensityKgM3 = 1000.0, ViscosityPaS = 0.001 };

        // 4 points forming a rectangular convex hull in (N, Qg): [100..500] rpm x [0..10] L/min
        var anchors = new List<PowerMapAnchorPoint>
        {
            new() { AgitationRpm = 100, GasFlowLpm = 0, NetPowerW = 1.0, PowerRatio = 1.0 },
            new() { AgitationRpm = 100, GasFlowLpm = 10, NetPowerW = 0.7, PowerRatio = 0.7 },
            new() { AgitationRpm = 500, GasFlowLpm = 0, NetPowerW = 25.0, PowerRatio = 1.0 },
            new() { AgitationRpm = 500, GasFlowLpm = 10, NetPowerW = 17.5, PowerRatio = 0.7 },
        };

        var settings = new PowerMapAlgorithmSettings
        {
            ResolutionN = 10,
            ResolutionQg = 10,
            MinRpm = 100,
            MaxRpm = 500,
            MinFlowLpm = 0,
            MaxFlowLpm = 10,
        };

        var surface = _engine.ReconstructSurface(anchors, geom, fluid, settings);

        Assert.Equal(10, surface.ResolutionN);
        Assert.Equal(10, surface.ResolutionQg);
        Assert.Equal(100, surface.PNetSurface.Length);
        Assert.Equal(100, surface.PVolumetricSurface.Length);
        Assert.Equal(100, surface.PowerRatioSurface.Length);

        // Check corner values
        var idx00 = surface.GetIndex(0, 0); // 100 rpm, 0 L/min
        Assert.NotNull(surface.PNetSurface[idx00]);
        Assert.Equal(1.0, surface.PNetSurface[idx00]!.Value, precision: 2);
        Assert.Equal(100.0, surface.PVolumetricSurface[idx00]!.Value, precision: 1); // 1.0 / 0.010

        // Interior point (approx center: 300 rpm, 5 L/min)
        var idxMid = surface.GetIndex(5, 5);
        Assert.NotNull(surface.PNetSurface[idxMid]);
        Assert.True(surface.PNetSurface[idxMid]!.Value > 1.0 && surface.PNetSurface[idxMid]!.Value < 25.0);
        Assert.NotNull(surface.PowerRatioSurface[idxMid]);
        Assert.True(surface.PowerRatioSurface[idxMid]!.Value >= 0.7 && surface.PowerRatioSurface[idxMid]!.Value <= 1.0);
    }

    [Fact]
    public void PowerMapEngine_ReconstructSurface_With_Collinear_Points_Leaves_Surface_Null_Safely()
    {
        var geom = new PowerGeometry();
        var fluid = new FluidProperties();

        // 3 collinear points (all at Qg = 5.0)
        var anchors = new List<PowerMapAnchorPoint>
        {
            new() { AgitationRpm = 100, GasFlowLpm = 5.0, NetPowerW = 1.0 },
            new() { AgitationRpm = 200, GasFlowLpm = 5.0, NetPowerW = 3.0 },
            new() { AgitationRpm = 300, GasFlowLpm = 5.0, NetPowerW = 6.0 },
        };

        var surface = _engine.ReconstructSurface(anchors, geom, fluid);
        Assert.NotNull(surface);
        // Because collinear points fail triangulation, all surface cells remain null without throwing exceptions
        Assert.All(surface.PNetSurface, cell => Assert.Null(cell));
    }

    [Fact]
    public void PowerMapEngine_FitVanTRietModel_Recovers_Known_Synthetic_Parameters()
    {
        // Ground truth: kLa = 0.026 * (P/V)^0.60 * (vs)^0.35
        const double trueK = 0.026;
        const double trueAlpha = 0.60;
        const double trueBeta = 0.35;

        // 3 x 3 grid of operational points:
        // P/V in { 100, 300, 800 } W/m³
        // vs in { 0.002, 0.005, 0.015 } m/s
        var pvs = new[] { 100.0, 300.0, 800.0 };
        var vss = new[] { 0.002, 0.005, 0.015 };

        var pairs = new List<KlaPowerPair>();
        foreach (var pv in pvs)
        {
            foreach (var vs in vss)
            {
                var kla = trueK * Math.Pow(pv, trueAlpha) * Math.Pow(vs, trueBeta);
                pairs.Add(new KlaPowerPair
                {
                    VolumetricPowerWm3 = pv,
                    SuperficialVelocityMs = vs,
                    KlaPerHour = kla,
                    ConfidenceInterval95 = kla * 0.05,
                });
            }
        }

        var result = _engine.FitVanTRietModel(pairs, out var updatedPairs);

        Assert.Equal(9, result.ValidPointsCount);
        Assert.Equal(6, result.DegreesOfFreedom); // 9 - 3 = 6
        Assert.True(result.R2 > 0.999);
        Assert.True(result.RootMeanSquareError < 1e-4);

        Assert.Equal(trueK, result.K, precision: 3);
        Assert.Equal(trueAlpha, result.Alpha, precision: 3);
        Assert.Equal(trueBeta, result.Beta, precision: 3);

        // Covariance matrix 3x3
        Assert.Equal(3, result.CovarianceMatrix.Length);
        Assert.Equal(3, result.CovarianceMatrix[0].Length);

        // Updated pairs
        Assert.Equal(9, updatedPairs.Count);
        foreach (var p in updatedPairs)
        {
            Assert.NotNull(p.PredictedKlaPerHour);
            Assert.NotNull(p.Residual);
            Assert.Equal(p.KlaPerHour, p.PredictedKlaPerHour.Value, precision: 3);
            Assert.True(Math.Abs(p.Residual.Value) < 1e-3);
        }
    }

    [Fact]
    public void PowerMapEngine_FitVanTRietModel_With_Literature_Reference_Data()
    {
        // Reference dataset typical of air-water coalescence in stirred tanks:
        // alpha in [0.4, 0.7], beta in [0.2, 0.5]
        var testData = new (double pv, double vs, double kla)[]
        {
            (150, 0.0025, 25.2),
            (250, 0.0025, 32.1),
            (450, 0.0025, 48.0),
            (150, 0.0050, 32.5),
            (250, 0.0050, 42.4),
            (450, 0.0050, 61.2),
            (150, 0.0100, 42.8),
            (250, 0.0100, 56.5),
            (450, 0.0100, 81.0),
        };

        var pairs = testData.Select(d => new KlaPowerPair
        {
            VolumetricPowerWm3 = d.pv,
            SuperficialVelocityMs = d.vs,
            KlaPerHour = d.kla,
        }).ToList();

        var result = _engine.FitVanTRietModel(pairs, out _);

        Assert.True(result.R2 > 0.95, $"Expected R2 > 0.95, got {result.R2}");
        Assert.InRange(result.Alpha, 0.40, 0.70);
        Assert.InRange(result.Beta, 0.20, 0.50);
        Assert.True(result.K > 0);
        Assert.Empty(result.ExcludedPointsNotes);
    }

    [Fact]
    public void PowerMapEngine_FitVanTRietModel_Rejects_Invalid_Points_And_Logs_Notes()
    {
        var pairs = new List<KlaPowerPair>
        {
            new() { VolumetricPowerWm3 = 200, SuperficialVelocityMs = 0.005, KlaPerHour = 30 },
            new() { VolumetricPowerWm3 = -10, SuperficialVelocityMs = 0.005, KlaPerHour = 30 }, // Invalid P/V
            new() { VolumetricPowerWm3 = 200, SuperficialVelocityMs = 0.0, KlaPerHour = 30 },   // Invalid vs
            new() { VolumetricPowerWm3 = 200, SuperficialVelocityMs = 0.005, KlaPerHour = -5 }, // Invalid kLa
        };

        var result = _engine.FitVanTRietModel(pairs, out _);

        // Only 1 valid point remains -> n < 4, regression not attempted
        Assert.Equal(1, result.ValidPointsCount);
        Assert.True(result.ExcludedPointsNotes.Count >= 3);
    }

    [Fact]
    public void PowerMapEngine_FitVanTRietModel_Singular_Matrix_Is_Handled_Safely()
    {
        // 4 points with identical P/V and vs -> det(X^T X) == 0
        var pairs = new List<KlaPowerPair>
        {
            new() { VolumetricPowerWm3 = 200, SuperficialVelocityMs = 0.005, KlaPerHour = 30 },
            new() { VolumetricPowerWm3 = 200, SuperficialVelocityMs = 0.005, KlaPerHour = 32 },
            new() { VolumetricPowerWm3 = 200, SuperficialVelocityMs = 0.005, KlaPerHour = 31 },
            new() { VolumetricPowerWm3 = 200, SuperficialVelocityMs = 0.005, KlaPerHour = 33 },
        };

        var result = _engine.FitVanTRietModel(pairs, out var updatedPairs);

        Assert.Equal(0, result.K);
        Assert.Equal(0, result.Alpha);
        Assert.Equal(0, result.Beta);
        Assert.Contains(result.ExcludedPointsNotes, n => n.Contains("singular ou colinear", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PowerMapEngine_ComputeFloodingBoundary_Calculates_Nienow_Curve_And_Preserves_Experimental()
    {
        var geom = new PowerGeometry
        {
            Impellers = [new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.060 }],
            VesselDiameterM = 0.190,
        };

        var expPoints = new List<FloodingPoint>
        {
            new() { AgitationRpm = 400, GasFlowLpm = 12.0, GasFlowNumber = 0.04, FroudeNumber = 0.25 },
            new() { AgitationRpm = 200, GasFlowLpm = 2.0, GasFlowNumber = 0.03, FroudeNumber = 0.06 },
        };

        var boundary = _engine.ComputeFloodingBoundary(geom, expPoints, minRpm: 100, maxRpm: 600, pointsCount: 20);

        Assert.NotNull(boundary);
        Assert.Equal(0.060, boundary.ImpellerDiameterM);
        Assert.Equal(0.190, boundary.VesselDiameterM);

        // Experimental points sorted by RPM
        Assert.Equal(2, boundary.ExperimentalPoints.Count);
        Assert.Equal(200, boundary.ExperimentalPoints[0].AgitationRpm);
        Assert.Equal(400, boundary.ExperimentalPoints[1].AgitationRpm);

        // Theoretical points generated
        Assert.Equal(20, boundary.NienowTheoreticalPoints.Count);
        Assert.Equal(100, boundary.NienowTheoreticalPoints[0].AgitationRpm);
        Assert.Equal(600, boundary.NienowTheoreticalPoints[^1].AgitationRpm);

        // Gas flow increases with rotation cubically: Qg,F(600) >> Qg,F(100)
        Assert.True(boundary.NienowTheoreticalPoints[^1].GasFlowLpm > boundary.NienowTheoreticalPoints[0].GasFlowLpm * 50);
    }
}
