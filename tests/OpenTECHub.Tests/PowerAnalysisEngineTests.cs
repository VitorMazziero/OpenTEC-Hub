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
            Points =
            {
                new TarePoint(300, 0.6, 0.62) { PVoidCi95W = 0.10 },
                new TarePoint(600, 1.5, 0.41) { PVoidCi95W = 0.20 },
            },
        };

        Assert.Equal(1.05, TareInterpolator.InterpolatePowerW(tare, 450), 6); // midpoint
        Assert.Equal(0.6, TareInterpolator.InterpolatePowerW(tare, 100), 6);  // clamp low
        Assert.Equal(1.5, TareInterpolator.InterpolatePowerW(tare, 900), 6);  // clamp high
        Assert.Equal(0.515, TareInterpolator.InterpolateSigmaTauPercent(tare, 450), 6);
        Assert.Equal(0.15, TareInterpolator.InterpolatePowerCi95W(tare, 450), 6);
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
    public void AnalyzePoint_Combines_Measurement_And_Tare_Confidence_Intervals()
    {
        var input = SinglePointInput(torquePercent: 2.0, torqueCi95: 0.2, rpm: 300) with
        {
            Calibration = new TorqueCalibration { Scale = 1.0, MotorRatedTorqueNm = 1.27 },
            Tare = new TareCurve
            {
                Points = { new TarePoint(300, 0.5, 0.05) { PVoidCi95W = 0.06 } },
            },
        };

        var result = _engine.AnalyzePoint(input);
        var measurementCiW = PowerCalc.ShaftPower(0.2 / 100.0 * 1.27, 300);
        var expectedCombinedCiW = System.Math.Sqrt(measurementCiW * measurementCiW + 0.06 * 0.06);

        Assert.Equal(expectedCombinedCiW, result.NetPowerCi95W, 6);
        Assert.Equal(
            PowerCalc.PowerNumber(expectedCombinedCiW, 998, 300, 0.06),
            result.AssemblyPowerNumberCi95,
            6);
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

    [Fact]
    public void FitEnergyCorrelation_Recovers_Line_From_Four_Noisy_Pairs()
    {
        // P_elec ≈ 2·P_mec + 10 with a little scatter; the spec asks for ≥4 pairs (§17, §20).
        var pairs = new[] { (1.0, 12.1), (2.0, 13.9), (3.0, 16.1), (4.0, 17.9) };

        var fit = _engine.FitEnergyCorrelation(pairs);

        Assert.True(fit.HasFit);
        Assert.Equal(4, fit.PointCount);
        Assert.InRange(fit.Slope, 1.9, 2.1);
        Assert.InRange(fit.InterceptW, 9.8, 10.3);
        Assert.True(fit.RSquared > 0.99);
    }

    // ---- Numerical robustness (audit) -----------------------------------------------------

    [Fact]
    public void AnalyzePoint_SnrFloor_Scales_With_Calibration_Scale()
    {
        // σ_τ 0.62 % at 300 rpm, P_void 0. At 1.5 % torque the Scale=1 net (~0.598 W) sits
        // between the unscaled floor (~0.742 W) and, at Scale=3, the correctly-scaled floor
        // (~2.23 W) — so the net (~1.795 W) must read BELOW noise. Before the fix the floor was
        // left unscaled (~0.742 W) and the point wrongly read above noise.
        var tare = new TareCurve { Points = { new TarePoint(300, 0.0, 0.62) } };
        var input = SinglePointInput(torquePercent: 1.5, torqueCi95: 0.1, rpm: 300) with
        {
            Calibration = new TorqueCalibration { Scale = 3.0, Offset = 0, MotorRatedTorqueNm = 1.27 },
            Tare = tare,
            SnrFloorMultiple = 3.0,
        };

        var result = _engine.AnalyzePoint(input);

        Assert.True(result.BelowNoiseFloor);
        // Net power itself carries the Scale, confirming the two are compared in the same units.
        Assert.Equal(3.0 * (1.5 / 100.0 * 1.27) * PowerCalc.AngularVelocity(300), result.NetPowerW, 4);
    }

    [Fact]
    public void AnalyzePoint_Handles_Empty_Impeller_List_Without_Throwing()
    {
        var input = new PowerPointInput
        {
            MeanTorquePercent = 2.0,
            TorquePercentCi95 = 0.1,
            MeanRpm = 300,
            Fluid = new FluidProperties(),
            Geometry = new PowerGeometry(), // no impellers
        };

        var result = _engine.AnalyzePoint(input);

        Assert.Empty(result.Stages);
        Assert.Equal(0.0, result.ReferenceDiameterM, 6);
        Assert.True(double.IsNaN(result.AssemblyPowerNumber));
        Assert.True(double.IsFinite(result.ShaftPowerW)); // the shaft power is still well-defined
    }

    [Theory]
    [InlineData(15.0)]
    [InlineData(1000.0)]
    public void AnalyzePoint_Is_Finite_At_Hardware_Rpm_Extremes(double rpm)
    {
        var result = _engine.AnalyzePoint(SinglePointInput(torquePercent: 5.0, torqueCi95: 0.2, rpm: rpm));

        var stage = Assert.Single(result.Stages);
        Assert.True(double.IsFinite(stage.PowerNumber));
        Assert.True(double.IsFinite(stage.ReynoldsNumber));
        Assert.True(stage.ReynoldsNumber > 0);
        Assert.True(double.IsFinite(result.NetPowerW));
    }

    [Fact]
    public void AnalyzePoint_Accepts_Negative_Braking_Torque_As_Data()
    {
        // Negative torque is legitimate (braking); it must not be rejected or clamped (§19).
        var result = _engine.AnalyzePoint(SinglePointInput(torquePercent: -2.0, torqueCi95: 0.2, rpm: 300));

        Assert.True(result.NetPowerW < 0);
        Assert.True(result.Stages[0].PowerNumber < 0);
        Assert.True(double.IsFinite(result.Stages[0].PowerNumber));
        Assert.True(result.Stages[0].PowerNumberCi95 >= 0); // the CI half-width stays non-negative
    }

    [Fact]
    public void RunningStatistics_Constant_Input_Has_Zero_Spread_Not_NaN()
    {
        var stats = new RunningStatistics();
        for (var i = 0; i < 200; i++)
        {
            stats.Add(1.5);
        }

        Assert.Equal(0.0, stats.Variance, 12);
        Assert.Equal(0.0, stats.StandardDeviation, 12);
        Assert.False(double.IsNaN(stats.ConfidenceHalfWidth95));
        Assert.Equal(0.0, stats.ConfidenceHalfWidth95, 12);
    }

    [Fact]
    public void FitPlateau_Single_Point_Returns_Its_Own_Ci()
    {
        var fit = _engine.FitPlateau(new (double, double, double)[] { (20_000, 5.0, 0.2) }, reCutoff: 10_000);

        Assert.True(fit.HasFit);
        Assert.Equal(1, fit.PointsUsed);
        Assert.Equal(5.0, fit.PowerNumber, 6);
        Assert.Equal(0.2, fit.PowerNumberCi95, 6);
    }

    [Fact]
    public void FitPlateau_Falls_Back_To_Plain_Mean_When_A_Ci_Is_NonPositive()
    {
        // One point carries no CI (0) → the inverse-variance path is abandoned for a plain mean,
        // avoiding an infinite weight.
        var points = new (double, double, double)[]
        {
            (20_000, 5.0, 0.2),
            (30_000, 5.2, 0.0),
        };

        var fit = _engine.FitPlateau(points, reCutoff: 10_000);

        Assert.True(fit.HasFit);
        Assert.Equal(2, fit.PointsUsed);
        Assert.Equal(5.1, fit.PowerNumber, 6); // unweighted average, not dominated by the 0-CI point
        Assert.True(double.IsFinite(fit.PowerNumberCi95));
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

    // ---- Gassed and Flooding Scientific Primitives (Phase 2) -----------------

    [Fact]
    public void Flow_Conversions_Lpm_And_Vvm_Are_Bidirectional()
    {
        // 5 L liquid volume = 0.005 m3. 1.0 vvm = 5.0 L/min.
        var volM3 = 0.005;
        var lpm = PowerCalc.VvmToLpm(1.5, volM3);
        Assert.Equal(7.5, lpm, 4);

        var vvm = PowerCalc.LpmToVvm(lpm, volM3);
        Assert.Equal(1.5, vvm, 4);

        // Zero or invalid volume returns NaN
        Assert.True(double.IsNaN(PowerCalc.VvmToLpm(1.0, 0.0)));
        Assert.True(double.IsNaN(PowerCalc.LpmToVvm(5.0, -1.0)));
    }

    [Theory]
    [InlineData(0.33, 0.190, 300)]
    [InlineData(0.40, 0.190, 450)]
    public void Nienow_Flooding_Correlation_And_Inverses_Round_Trip(double dOverT, double vesselDiameterM, double rpm)
    {
        var d = dOverT * vesselDiameterM;
        var fr = PowerCalc.FroudeNumber(rpm, d);
        var expectedFlG = 30.0 * System.Math.Pow(dOverT, 3.5) * fr;

        var computedFlG = PowerCalc.NienowFloodingAerationNumber(d, vesselDiameterM, fr);
        Assert.Equal(expectedFlG, computedFlG, 6);

        // Compute flooding flow for this rpm
        var qFloodingLpm = PowerCalc.NienowFloodingGasFlowLpm(rpm, d, vesselDiameterM);
        Assert.True(qFloodingLpm > 0);

        // Inverse calculation: from qFloodingLpm, get back rpm
        var reconstructedRpm = PowerCalc.NienowFloodingRpm(qFloodingLpm, d, vesselDiameterM);
        Assert.Equal(rpm, reconstructedRpm, 3);
    }

    [Fact]
    public void PropagatePowerRatioUncertainty_Computes_Exact_Values()
    {
        // PG = 10 W, CI = 1.0 W; P0 = 20 W, CI = 2.0 W
        // Ratio R = 10 / 20 = 0.5
        // Term1 = 1.0 / 20 = 0.05
        // Term2 = (10 * 2.0) / (20 * 20) = 20 / 400 = 0.05
        // CIRatio = sqrt(0.05^2 + 0.05^2) = sqrt(0.005) = 0.0707106
        var (ratio, ciRatio) = PowerCalc.PropagatePowerRatioUncertainty(10.0, 1.0, 20.0, 2.0);
        Assert.NotNull(ratio);
        Assert.NotNull(ciRatio);
        Assert.Equal(0.5, ratio!.Value, 5);
        Assert.Equal(System.Math.Sqrt(0.005), ciRatio!.Value, 5);

        // Case PG = 0: R = 0, Term2 = 0 -> CIRatio = CI(PG) / P0
        var (zeroRatio, zeroCi) = PowerCalc.PropagatePowerRatioUncertainty(0.0, 0.8, 20.0, 1.0);
        Assert.Equal(0.0, zeroRatio!.Value, 5);
        Assert.Equal(0.8 / 20.0, zeroCi!.Value, 5);

        // Invalid P0 <= 0 returns null
        var (nullRatio, nullCi) = PowerCalc.PropagatePowerRatioUncertainty(10.0, 1.0, 0.0, 1.0);
        Assert.Null(nullRatio);
        Assert.Null(nullCi);
    }

    [Fact]
    public void ResolveReferenceP0_Follows_Three_Step_Hierarchy()
    {
        var doc = new PowerTestDocument
        {
            Fluid = new FluidProperties { DensityKgM3 = 1000.0 },
            Geometry = new PowerGeometry
            {
                Impellers = { new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.06 } }
            },
            Runs =
            {
                new PowerRunSummary
                {
                    AgitationRpm = 300,
                    GasMode = PowerGasMode.Ungassed,
                    Phase = PowerRunPhase.Accepted,
                    NetPowerW = 0.60,
                    Ci95PowerW = 0.02,
                }
            }
        };

        // Case 1: Fitted plateau provided
        var plateau = new PlateauFitResult { HasFit = true, PowerNumber = 5.0, PowerNumberCi95 = 0.1 };
        var (p0_plat, ci_plat, prov_plat) = _engine.ResolveReferenceP0(300, doc, plateau);
        Assert.Equal(P0Provenance.PlateauFit, prov_plat);
        Assert.NotNull(p0_plat);
        Assert.True(p0_plat > 0);

        // Case 2: No plateau fit -> fall back to measured ungassed at 300 rpm
        var (p0_meas, ci_meas, prov_meas) = _engine.ResolveReferenceP0(300, doc, plateauFit: null);
        Assert.Equal(P0Provenance.MeasuredUngassed, prov_meas);
        Assert.Equal(0.60, p0_meas!.Value, 4);
        Assert.Equal(0.02, ci_meas!.Value, 4);

        // Case 3: Different rpm (e.g. 800 rpm) with no plateau and no run -> null / None
        var (p0_none, ci_none, prov_none) = _engine.ResolveReferenceP0(800, doc, plateauFit: null);
        Assert.Equal(P0Provenance.None, prov_none);
        Assert.Null(p0_none);
        Assert.Null(ci_none);
    }

    [Fact]
    public void DetectFlooding_Identifies_Minimum_In_Gassed_Sweep()
    {
        var geometry = new PowerGeometry
        {
            VesselDiameterM = 0.190,
            Impellers = { new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.06, StageIndex = 0 } }
        };

        // Sweep of increasing gas flow at constant 400 rpm
        // Power ratio drops to minimum at FlG = 0.03 (point 2) then rises (flooding transition)
        var runs = new List<PowerRunSummary>
        {
            new() { AgitationRpm = 400, GasMode = PowerGasMode.Gassed, Phase = PowerRunPhase.Accepted, GasFlowNumber = 0.01, PowerRatio = 0.85, GasFlowLpm = 1.5 },
            new() { AgitationRpm = 400, GasMode = PowerGasMode.Gassed, Phase = PowerRunPhase.Accepted, GasFlowNumber = 0.02, PowerRatio = 0.70, GasFlowLpm = 3.0 },
            new() { AgitationRpm = 400, GasMode = PowerGasMode.Gassed, Phase = PowerRunPhase.Accepted, GasFlowNumber = 0.03, PowerRatio = 0.55, GasFlowLpm = 4.5 },
            new() { AgitationRpm = 400, GasMode = PowerGasMode.Gassed, Phase = PowerRunPhase.Accepted, GasFlowNumber = 0.04, PowerRatio = 0.65, GasFlowLpm = 6.0 },
            new() { AgitationRpm = 400, GasMode = PowerGasMode.Gassed, Phase = PowerRunPhase.Accepted, GasFlowNumber = 0.05, PowerRatio = 0.75, GasFlowLpm = 7.5 },
        };

        var result = _engine.DetectFlooding(runs, geometry, referenceStageIndex: 0);

        Assert.NotNull(result);
        Assert.Equal(0.03, result!.ExperimentalFlG, 4);
        Assert.Equal(400.0, result.ExperimentalRpm, 1);
        Assert.Equal(4.5, result.ExperimentalFlowLpm, 1);
        Assert.Equal(FloodingDetectionMethod.Automatic, result.Method);
        Assert.True(result.TheoreticalFlGNienow > 0);
        Assert.True(System.Math.Abs(result.RelativeDeviationPercent) < 100);
    }

    [Fact]
    public void GenerateNienowBoundary_Returns_Expected_Grid()
    {
        var geometry = new PowerGeometry
        {
            VesselDiameterM = 0.190,
            Impellers = { new Impeller { Type = ImpellerType.RushtonFlatBlade, DiameterM = 0.06 } }
        };

        var boundary = _engine.GenerateNienowBoundary(geometry, minRpm: 100, maxRpm: 600, stepCount: 6);
        Assert.Equal(6, boundary.Count);
        Assert.Equal(100.0, boundary[0].Rpm, 1);
        Assert.Equal(600.0, boundary[^1].Rpm, 1);

        // Monotonically increasing Froude and flow
        for (var i = 1; i < boundary.Count; i++)
        {
            Assert.True(boundary[i].Fr > boundary[i - 1].Fr);
            Assert.True(boundary[i].FlowLpm > boundary[i - 1].FlowLpm);
        }
    }

    [Fact]
    public void AnalyzePoint_Handles_Gas_Inputs_And_Special_Cases()
    {
        var input = SinglePointInput(torquePercent: 1.5, torqueCi95: 0.1, rpm: 300) with
        {
            GasFlowLpm = 5.0,
            ReferenceP0W = 0.50,
            ReferenceP0Ci95W = 0.02,
            P0Provenance = P0Provenance.PlateauFit,
        };

        var result = _engine.AnalyzePoint(input);

        Assert.Equal(5.0, result.GasFlowLpm);
        Assert.NotNull(result.GasFlowNumber);
        Assert.NotNull(result.FroudeNumber);
        Assert.NotNull(result.PowerRatio);
        Assert.NotNull(result.PowerRatioCi95);
        Assert.Equal(P0Provenance.PlateauFit, result.P0Provenance);

        // Special case: PG / P0 > 1 (incipient cavities / pre-flooding) is preserved as valid data
        var highPowerInput = input with
        {
            ReferenceP0W = 0.10, // PG ~ 0.6W > P0 0.1W -> Ratio ~ 6.0
        };
        var highResult = _engine.AnalyzePoint(highPowerInput);
        Assert.True(highResult.PowerRatio > 1.0);

        // Special case: Qg = 0 -> FlG = 0
        var zeroGasInput = input with { GasFlowLpm = 0.0 };
        var zeroResult = _engine.AnalyzePoint(zeroGasInput);
        Assert.Equal(0.0, zeroResult.GasFlowNumber);
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
