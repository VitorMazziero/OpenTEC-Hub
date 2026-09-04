using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using OpenTECHub.Services.PowerTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PowerAnalysisEngineTests
{
    private readonly PowerAnalysisEngine _engine = new();

    // ---- PowerCalc primitives -------------------------------------------------------------

    [Fact]
    public void ShaftPower_Reproduces_Bench_Number()
    {
        // Bench 2026-09-02 (PLANO_SERVO_POTENCIA_APP §1.2): 1.4 % torque at 92.7 rpm, T_nom 1.27.
        var torqueNm = PowerCalc.CalibratedTorqueNm(1.4, calibration: null); // 0.01778
        Assert.Equal(0.01778, torqueNm, 5);

        var powerW = PowerCalc.ShaftPower(torqueNm, 92.7);
        Assert.Equal(0.1726, powerW, 3);
    }

    [Fact]
    public void Reynolds_And_PowerNumber_Match_Hand_Calculation()
    {
        // ρ=998, μ=0.001, D=0.06, N=300 rpm (5 rev/s).
        Assert.Equal(17964.0, PowerCalc.ReynoldsNumber(998, 300, 0.06, 0.001), 1);

        // Np = 0.6 / (998·5³·0.06⁵) = 0.6 / 0.0970092 = 6.1852.
        Assert.Equal(6.1852, PowerCalc.PowerNumber(0.6, 998, 300, 0.06), 3);
    }

    [Fact]
    public void AerationAndFroude_Are_Finite_And_Positive()
    {
        Assert.True(PowerCalc.AerationNumber(5.0, 300, 0.06) > 0);   // 5 L/min
        Assert.True(PowerCalc.FroudeNumber(300, 0.06) > 0);
    }

    // ---- RunningStatistics (Welford) ------------------------------------------------------

    [Fact]
    public void RunningStatistics_Match_Known_Sample()
    {
        var stats = new RunningStatistics();
        foreach (var v in new[] { 2.0, 4, 4, 4, 5, 5, 7, 9 })
        {
            stats.Add(v);
        }

        // Σ(x−5)² = 32; sample variance (n−1) = 32/7 ≈ 4.5714.
        Assert.Equal(8, stats.Count);
        Assert.Equal(5.0, stats.Mean, 6);
        Assert.Equal(32.0 / 7.0, stats.Variance, 6);
        Assert.Equal(System.Math.Sqrt(32.0 / 7.0), stats.StandardDeviation, 6);
        Assert.Equal(System.Math.Sqrt(32.0 / 7.0) / System.Math.Sqrt(8), stats.StandardError, 6);
        Assert.Equal(RunningStatistics.NormalZ95 * stats.StandardError, stats.ConfidenceHalfWidth95, 6);
    }

    // ---- TareInterpolator -----------------------------------------------------------------

    [Fact]
    public void TareInterpolator_Interpolates_And_Clamps()
    {
        var tare = new TareCurve
        {
            Points = { new TarePoint(300, 0.6, 0.62), new TarePoint(600, 1.5, 0.41) },
        };

        Assert.Equal(1.05, TareInterpolator.InterpolatePowerW(tare, 450), 6); // midpoint
        Assert.Equal(0.6, TareInterpolator.InterpolatePowerW(tare, 100), 6);  // clamp low
        Assert.Equal(1.5, TareInterpolator.InterpolatePowerW(tare, 900), 6);  // clamp high
        Assert.Equal(0.515, TareInterpolator.InterpolateSigmaTauPercent(tare, 450), 6);
    }

    // ---- AnalyzePoint ---------------------------------------------------------------------

    [Fact]
    public void AnalyzePoint_Relative_When_No_Calibration_Or_Tare()
    {
        var input = SinglePointInput(torquePercent: 2.0, torqueCi95: 0.2, rpm: 300);

        var result = _engine.AnalyzePoint(input);

        Assert.True(result.IsRelative);
        Assert.Equal(0.0, result.VoidPowerW, 6);
        Assert.Equal(result.ShaftPowerW, result.NetPowerW, 6); // no tare → net = shaft
        Assert.False(result.BelowNoiseFloor);                  // no tare → SNR not judged
        Assert.Single(result.Stages);
    }

    [Fact]
    public void AnalyzePoint_Subtracts_Tare_And_Is_Absolute_With_Calibration()
    {
        var input = SinglePointInput(torquePercent: 2.0, torqueCi95: 0.2, rpm: 300) with
        {
            Calibration = new TorqueCalibration { Scale = 1.0, Offset = 0, MotorRatedTorqueNm = 1.27 },
            Tare = new TareCurve { Points = { new TarePoint(300, 0.5, 0.05) } },
        };

        var result = _engine.AnalyzePoint(input);

        Assert.False(result.IsRelative);
        Assert.Equal(0.5, result.VoidPowerW, 6);
        // shaft = (2/100·1.27)·ω(300) = 0.0254·31.41593 = 0.79796; net = 0.29796.
        Assert.Equal(0.79796, result.ShaftPowerW, 4);
        Assert.Equal(0.29796, result.NetPowerW, 4);
    }

    [Fact]
    public void AnalyzePoint_Splits_Power_Across_Stages_With_Each_Diameter()
    {
        var geometry = new PowerGeometry
        {
            Impellers =
            {
                new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.06, StageIndex = 0 },
                new Impeller { Type = ImpellerType.ElephantEar, DiameterM = 0.08, StageIndex = 1 },
            },
        };

        // Choose torque so shaft power is 0.6 W at 300 rpm (no tare, relative).
        var torqueNmForHalfWatt = 0.6 / PowerCalc.AngularVelocity(300);
        var torquePercent = torqueNmForHalfWatt / 1.27 * 100.0;

        var input = new PowerPointInput
        {
            MeanTorquePercent = torquePercent,
            TorquePercentCi95 = 0,
            MeanRpm = 300,
            Fluid = new FluidProperties(),
            Geometry = geometry,
        };

        var result = _engine.AnalyzePoint(input);

        Assert.Equal(0.6, result.NetPowerW, 4);
        Assert.Equal(2, result.Stages.Count);

        // Per stage carries 0.3 W; Np uses each stage's own D.
        Assert.Equal(PowerCalc.PowerNumber(0.3, 998, 300, 0.06), result.Stages[0].PowerNumber, 4);
        Assert.Equal(PowerCalc.PowerNumber(0.3, 998, 300, 0.08), result.Stages[1].PowerNumber, 4);

        // Assembly uses the largest diameter and the whole net power.
        Assert.Equal(0.08, result.ReferenceDiameterM, 6);
        Assert.Equal(PowerCalc.PowerNumber(0.6, 998, 300, 0.08), result.AssemblyPowerNumber, 4);
    }

    [Fact]
    public void AnalyzePoint_Propagates_Torque_Ci_To_Power_And_Np()
    {
        var input = SinglePointInput(torquePercent: 2.0, torqueCi95: 0.2, rpm: 300);

        var result = _engine.AnalyzePoint(input);

        // CI on power = ΔτNm·ω = (0.2/100·1.27)·31.41593 = 0.00254·31.41593 = 0.07980 W.
        Assert.Equal(0.07980, result.NetPowerCi95W, 4);
        // CI on Np propagates linearly through the same ρN³D⁵ factor.
        Assert.Equal(PowerCalc.PowerNumber(result.NetPowerCi95W, 998, 300, 0.06), result.Stages[0].PowerNumberCi95, 4);
    }

    [Fact]
    public void AnalyzePoint_Flags_Below_Noise_Floor_At_Low_Signal()
    {
        // σ_τ 0.62 % at 300 rpm → floor ≈ 3·(0.62/100·1.27·ω) ≈ 0.742 W.
        var tare = new TareCurve { Points = { new TarePoint(300, 0.0, 0.62) } };

        var lowSignal = SinglePointInput(torquePercent: 1.5, torqueCi95: 0.1, rpm: 300) with
        {
            Calibration = new TorqueCalibration { Scale = 1.0, MotorRatedTorqueNm = 1.27 },
            Tare = tare,
            SnrFloorMultiple = 3.0,
        };
        Assert.True(_engine.AnalyzePoint(lowSignal).BelowNoiseFloor);   // ~0.60 W < 0.742 W

        var highSignal = lowSignal with { MeanTorquePercent = 10.0 };
        Assert.False(_engine.AnalyzePoint(highSignal).BelowNoiseFloor); // ~4 W > floor
    }

    // ---- FitPlateau -----------------------------------------------------------------------

    [Fact]
    public void FitPlateau_Weights_By_Precision_And_Honours_Cutoff()
    {
        var points = new (double, double, double)[]
        {
            (5_000, 3.0, 0.5),    // below cutoff → excluded
            (20_000, 5.0, 0.2),
            (30_000, 5.2, 0.1),   // tightest CI → dominates
        };

        var fit = _engine.FitPlateau(points, reCutoff: 10_000);

        Assert.True(fit.HasFit);
        Assert.Equal(2, fit.PointsUsed);
        Assert.InRange(fit.PowerNumber, 5.1, 5.2);       // pulled toward the tighter point
        Assert.True(fit.PowerNumberCi95 < 0.1);          // combined tighter than either
    }

    [Fact]
    public void FitPlateau_Returns_NoFit_When_All_Below_Cutoff()
    {
        var points = new (double, double, double)[] { (5_000, 3.0, 0.5) };
        Assert.False(_engine.FitPlateau(points, reCutoff: 10_000).HasFit);
    }

    // ---- FitEnergyCorrelation -------------------------------------------------------------

    [Fact]
    public void FitEnergyCorrelation_Recovers_Affine_Line()
    {
        // P_elec = 2·P_mec + 10.
        var pairs = new[] { (1.0, 12.0), (2.0, 14.0), (3.0, 16.0) };

        var fit = _engine.FitEnergyCorrelation(pairs);

        Assert.True(fit.HasFit);
        Assert.Equal(2.0, fit.Slope, 6);
        Assert.Equal(10.0, fit.InterceptW, 6);
        Assert.Equal(1.0, fit.RSquared, 6);
        Assert.Equal(3, fit.PointCount);
    }

    [Fact]
    public void FitEnergyCorrelation_Needs_Two_Distinct_Points()
    {
        Assert.False(_engine.FitEnergyCorrelation(new[] { (1.0, 12.0) }).HasFit);
        Assert.False(_engine.FitEnergyCorrelation(new[] { (1.0, 12.0), (1.0, 15.0) }).HasFit); // no x spread
    }

    // ---- Culture --------------------------------------------------------------------------

    [Fact]
    public void Analysis_Is_Culture_Invariant()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("pt-BR");
            var np = PowerCalc.PowerNumber(0.6, 998, 300, 0.06);
            Assert.Equal(6.1852, np, 3);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    private static PowerPointInput SinglePointInput(double torquePercent, double torqueCi95, double rpm) => new()
    {
        MeanTorquePercent = torquePercent,
        TorquePercentCi95 = torqueCi95,
        MeanRpm = rpm,
        Fluid = new FluidProperties(),
        Geometry = new PowerGeometry
        {
            Impellers = { new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.06, StageIndex = 0 } },
        },
    };
}
