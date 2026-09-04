using System;
using System.Linq;
using OpenTECHub.Services.PowerMapping;
using OpenTECHub.Services.PowerTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Bioprocess scale-up (§18.3 step 7). The calculator has to be honest about what it cannot know:
/// a criterion alone does not determine both N and Q_g, and extrapolation is named as such.
/// </summary>
public sealed class BioprocessScaleUpTests
{
    private readonly BioprocessScaleUpEngine _engine = new();

    private static ScaleUpReference Reference(KlaCorrelationResult? correlation = null) => new()
    {
        LiquidVolumeM3 = 0.010,
        VesselDiameterM = 0.190,
        ImpellerDiameterM = 0.060,
        DensityKgM3 = 1000.0,
        ViscosityPaS = 0.001,
        TurbulentPowerNumber = 5.0,
        AgitationRpm = 500.0,
        GasFlowLpm = 5.0,
        Correlation = correlation,
        CalibratedMinVolumetricPower = 50,
        CalibratedMaxVolumetricPower = 400,
        CalibratedMinSuperficialVelocity = 0.001,
        CalibratedMaxSuperficialVelocity = 0.01,
    };

    private static ScaleUpTarget Target(
        ScaleUpCriterion criterion = ScaleUpCriterion.ConstantVolumetricPower,
        ScaleUpGasRule gasRule = ScaleUpGasRule.ConstantVvm,
        double gasValue = 0.5,
        double volumeL = 20.0) => new()
    {
        LiquidVolumeM3 = volumeL / 1000.0,
        VesselDiameterM = 0.300,
        ImpellerDiameterM = 0.100,
        MinRpm = 20,
        MaxRpm = 800,
        Criterion = criterion,
        GasRule = gasRule,
        GasRuleValue = gasValue,
    };

    private static KlaCorrelationResult Correlation() => new()
    {
        HasFit = true,
        K = 0.026,
        Alpha = 0.5,
        Beta = 0.4,
        R2 = 0.97,
        ValidPointsCount = 9,
        DegreesOfFreedom = 6,
    };

    [Fact]
    public void Missing_gas_rule_is_refused_rather_than_resolved_by_assumption()
    {
        var result = _engine.Solve(Reference(), Target(gasRule: ScaleUpGasRule.None));

        Assert.False(result.IsSolved);
        Assert.Contains(result.Refusals, r => r.Contains("duas incógnitas", StringComparison.Ordinal));
        Assert.Equal(0, result.TargetAgitationRpm);
    }

    [Fact]
    public void Missing_target_geometry_is_refused()
    {
        var target = Target() with { VesselDiameterM = 0, ImpellerDiameterM = 0, LiquidVolumeM3 = 0 };

        var result = _engine.Solve(Reference(), target);

        Assert.False(result.IsSolved);
        Assert.Contains(result.Refusals, r => r.Contains("Volume útil", StringComparison.Ordinal));
        Assert.Contains(result.Refusals, r => r.Contains("Diâmetro do vaso", StringComparison.Ordinal));
        Assert.Contains(result.Refusals, r => r.Contains("Diâmetro do impelidor", StringComparison.Ordinal));
    }

    [Fact]
    public void Constant_kla_without_a_fitted_correlation_is_refused()
    {
        var result = _engine.Solve(Reference(correlation: null), Target(ScaleUpCriterion.ConstantKla));

        Assert.False(result.IsSolved);
        Assert.Contains(result.Refusals, r => r.Contains("van 't Riet", StringComparison.Ordinal));
    }

    [Fact]
    public void Constant_kla_with_an_explicitly_failed_correlation_is_refused()
    {
        var failed = Correlation() with
        {
            HasFit = false,
            FailureReason = "matriz singular",
        };

        var result = _engine.Solve(Reference(failed), Target(ScaleUpCriterion.ConstantKla));

        Assert.False(result.IsSolved);
        Assert.Contains(result.Refusals, r => r.Contains("van 't Riet", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Non_finite_target_gas_rule_is_refused(double gasRuleValue)
    {
        var result = _engine.Solve(Reference(), Target(gasValue: gasRuleValue));

        Assert.False(result.IsSolved);
        Assert.Contains(result.Refusals, r => r.Contains("finito", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Constant_tip_speed_scales_rotation_with_the_diameter_ratio()
    {
        var reference = Reference();
        var target = Target(ScaleUpCriterion.ConstantTipSpeed);

        var result = _engine.Solve(reference, target);

        Assert.True(result.IsSolved);

        // pi*N*D constant: N_target = N_ref * D_ref / D_target = 500 * 60/100 = 300 rpm.
        Assert.Equal(300.0, result.TargetAgitationRpm, 6);

        var referenceTipSpeed = Math.PI * PowerCalc.RevPerSecond(reference.AgitationRpm) * reference.ImpellerDiameterM;
        Assert.Equal(referenceTipSpeed, result.TipSpeedMs, 9);
    }

    [Fact]
    public void Constant_volumetric_power_reproduces_the_reference_power_density()
    {
        var reference = Reference();

        var result = _engine.Solve(reference, Target());

        Assert.True(result.IsSolved);
        Assert.Equal(result.ReferenceVolumetricPowerWm3, result.VolumetricPowerWm3, 6);

        // Torque and shaft power must be consistent with the solved rotation.
        var rps = PowerCalc.RevPerSecond(result.TargetAgitationRpm);
        Assert.Equal(result.ShaftPowerW / (2 * Math.PI * rps), result.TorqueNm, 9);
        Assert.Equal(result.ShaftPowerW / (20.0 / 1000.0), result.VolumetricPowerWm3, 6);
    }

    [Fact]
    public void Constant_kla_holds_the_predicted_kla_across_the_scale_jump()
    {
        var reference = Reference(Correlation());

        var result = _engine.Solve(reference, Target(ScaleUpCriterion.ConstantKla));

        Assert.True(result.IsSolved);
        Assert.NotNull(result.ReferenceKlaPerHour);
        Assert.NotNull(result.PredictedKlaPerHour);

        // The whole point of the criterion: the same kLa at the new scale.
        Assert.Equal(result.ReferenceKlaPerHour!.Value, result.PredictedKlaPerHour!.Value, 6);
    }

    [Fact]
    public void The_gas_rule_alone_sets_the_target_flow()
    {
        // 0.5 vvm on 20 L is 10 L/min, whatever the criterion does to the rotation.
        var vvm = _engine.Solve(Reference(), Target(gasRule: ScaleUpGasRule.ConstantVvm, gasValue: 0.5));
        Assert.True(vvm.IsSolved);
        Assert.Equal(10.0, vvm.TargetGasFlowLpm, 6);
        Assert.Equal(0.5, vvm.TargetGasFlowVvm, 6);

        // A fixed flow passes straight through.
        var fixedFlow = _engine.Solve(Reference(), Target(gasRule: ScaleUpGasRule.FixedFlow, gasValue: 7.5));
        Assert.True(fixedFlow.IsSolved);
        Assert.Equal(7.5, fixedFlow.TargetGasFlowLpm, 6);

        // Constant superficial velocity reproduces v_s on the target's own cross-section.
        var superficial = _engine.Solve(
            Reference(),
            Target(gasRule: ScaleUpGasRule.ConstantSuperficialVelocity, gasValue: 0.004));
        Assert.True(superficial.IsSolved);
        Assert.Equal(0.004, superficial.TargetSuperficialVelocityMs, 9);
    }

    [Fact]
    public void Flooding_in_the_new_geometry_is_evaluated_and_warned_about()
    {
        // A deliberately excessive flow at the target scale.
        var result = _engine.Solve(Reference(), Target(gasRule: ScaleUpGasRule.FixedFlow, gasValue: 400.0));

        Assert.True(result.IsSolved);
        Assert.True(result.IsFlooded);
        Assert.True(result.FloodingMargin > 1.0);
        Assert.Contains(result.Warnings, w => w.Contains("Afogamento", StringComparison.Ordinal));
    }

    [Fact]
    public void Extrapolation_beyond_the_calibrated_domain_is_named()
    {
        var reference = Reference(Correlation()) with
        {
            CalibratedMinVolumetricPower = 50,
            CalibratedMaxVolumetricPower = 60,
            CalibratedMinSuperficialVelocity = 0.001,
            CalibratedMaxSuperficialVelocity = 0.0011,
        };

        var result = _engine.Solve(reference, Target());

        Assert.True(result.IsSolved);
        Assert.Contains(result.Warnings, w => w.Contains("fora do domínio calibrado", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("extrapolação", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_large_single_step_and_a_broken_geometric_similarity_are_both_flagged()
    {
        // 10 mL bench to 2000 L, and D/T moving from 0.316 to 0.500.
        var target = Target(volumeL: 2000) with { VesselDiameterM = 1.20, ImpellerDiameterM = 0.60 };

        var result = _engine.Solve(Reference(), target);

        Assert.True(result.IsSolved);
        Assert.Contains(result.Warnings, w => w.Contains("Salto de escala", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("D/T muda", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rotation_outside_the_admissible_range_is_flagged_without_hiding_the_number()
    {
        var target = Target() with { MinRpm = 400, MaxRpm = 450 };

        var result = _engine.Solve(Reference(), target);

        Assert.True(result.IsSolved);
        Assert.True(result.TargetAgitationRpm < 400 || result.TargetAgitationRpm > 450);
        Assert.Contains(result.Warnings, w => w.Contains("fora da faixa admissível", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_csv_carries_the_sizing_sheet_and_the_disclaimer()
    {
        var reference = Reference(Correlation());
        var target = Target();
        var result = _engine.Solve(reference, target);

        var csv = BioprocessScaleUpEngine.BuildSummaryCsv(reference, target, result);

        Assert.Contains("Folha de dimensionamento de bioprocesso", csv, StringComparison.Ordinal);
        Assert.Contains("nao e validacao do processo", csv, StringComparison.Ordinal);
        Assert.Contains("operacao;rotacao N", csv, StringComparison.Ordinal);
        Assert.Contains("adimensional;Reynolds", csv, StringComparison.Ordinal);
        Assert.Contains("flooding;margem Qg/Qg_F", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Refused_calculations_export_their_reasons_instead_of_numbers()
    {
        var reference = Reference();
        var target = Target(gasRule: ScaleUpGasRule.None);
        var result = _engine.Solve(reference, target);

        var csv = BioprocessScaleUpEngine.BuildSummaryCsv(reference, target, result);

        Assert.Contains("secao;recusa", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("operacao;rotacao N", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_publishes_the_sheet_the_refusals_and_the_warnings()
    {
        var viewModel = new BioprocessScaleUpViewModel(_engine);
        viewModel.SetReference(Reference(Correlation()), "bancada 10 L");

        // Without a gas rule the calculator refuses and shows no sheet.
        viewModel.SelectedGasRule = ScaleUpGasRule.None;
        viewModel.Calculate();

        Assert.False(viewModel.HasResult);
        Assert.True(viewModel.HasRefusals);
        Assert.Empty(viewModel.SheetRows);

        // With one, it solves and fills the sizing sheet.
        viewModel.SelectedGasRule = ScaleUpGasRule.ConstantVvm;
        viewModel.GasRuleValue = 0.5;
        viewModel.Calculate();

        Assert.True(viewModel.HasResult);
        Assert.False(viewModel.HasRefusals);
        Assert.NotEmpty(viewModel.SheetRows);
        Assert.Contains(viewModel.SheetRows, r => r.Quantity.Contains("Rotação", StringComparison.Ordinal));
        Assert.Contains(viewModel.SheetRows, r => r.Quantity.Contains("Torque", StringComparison.Ordinal));
        Assert.Contains(viewModel.SheetRows, r => r.Quantity.Contains("flooding", StringComparison.OrdinalIgnoreCase));

        var csv = viewModel.BuildSummaryCsv();
        Assert.Contains("operacao;rotacao N", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_csv_with_zero_vessel_diameter_does_not_produce_infinity()
    {
        var reference = Reference();
        var incompleteTarget = Target() with { VesselDiameterM = 0 };
        var result = _engine.Solve(reference, incompleteTarget);

        var csv = BioprocessScaleUpEngine.BuildSummaryCsv(reference, incompleteTarget, result);

        Assert.DoesNotContain("Infinity", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NaN", csv, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("geometria;razao D/T;", csv, StringComparison.Ordinal);
        Assert.Contains("secao;recusa", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Solve_warns_when_viscosity_is_missing_or_unphysical()
    {
        var zeroViscosityRef = Reference() with { ViscosityPaS = 0.0 };
        var target = Target();

        var result = _engine.Solve(zeroViscosityRef, target);

        Assert.True(result.IsSolved);
        Assert.Contains(result.Warnings, w => w.Contains("Viscosidade dinâmica", StringComparison.Ordinal));
        Assert.True(double.IsNaN(result.ReynoldsNumber));

        var csv = BioprocessScaleUpEngine.BuildSummaryCsv(zeroViscosityRef, target, result);
        Assert.Contains("adimensional;Reynolds;;-;-", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_safely_formats_non_finite_reynolds_and_dimensionless_numbers()
    {
        var viewModel = new BioprocessScaleUpViewModel(_engine);
        var zeroViscosityRef = Reference(Correlation()) with { ViscosityPaS = 0.0 };
        viewModel.SetReference(zeroViscosityRef, "bancada sem viscosidade");
        viewModel.SelectedGasRule = ScaleUpGasRule.ConstantVvm;
        viewModel.GasRuleValue = 0.5;

        viewModel.Calculate();

        Assert.True(viewModel.HasResult);
        var reynoldsRow = viewModel.SheetRows.FirstOrDefault(r => r.Quantity == "Reynolds");
        Assert.NotNull(reynoldsRow);
        Assert.Equal("—", reynoldsRow.Target);
    }
}
