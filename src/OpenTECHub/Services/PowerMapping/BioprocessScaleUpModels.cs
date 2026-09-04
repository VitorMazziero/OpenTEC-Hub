using System;
using System.Collections.Generic;

namespace OpenTECHub.Services.PowerMapping;

/// <summary>The scale-up criterion held constant between the calibrated scale and the target (§18.3 step 7.1).</summary>
public enum ScaleUpCriterion
{
    /// <summary>Same volumetric power density P/V.</summary>
    ConstantVolumetricPower,

    /// <summary>Same kLa, resolved through the calibrated van 't Riet model.</summary>
    ConstantKla,

    /// <summary>Same impeller tip speed π·N·D, for shear-sensitive cultures.</summary>
    ConstantTipSpeed,
}

/// <summary>
/// How the gas variable is pinned at the target scale. A criterion alone fixes one equation for
/// two unknowns (N and Q_g), so this rule is what makes the problem identifiable.
/// </summary>
public enum ScaleUpGasRule
{
    /// <summary>No rule given: the calculation must be refused, not guessed.</summary>
    None,

    /// <summary>Volumes of gas per volume of liquid per minute.</summary>
    ConstantVvm,

    /// <summary>Same superficial gas velocity v_s [m/s].</summary>
    ConstantSuperficialVelocity,

    /// <summary>A gas flow stated directly in L/min.</summary>
    FixedFlow,
}

/// <summary>The calibrated scale the extrapolation departs from.</summary>
public sealed record ScaleUpReference
{
    /// <summary>Working liquid volume of the calibrated vessel [m³].</summary>
    public double LiquidVolumeM3 { get; init; }

    public double VesselDiameterM { get; init; } = 0.190;

    public double ImpellerDiameterM { get; init; } = 0.060;

    public double DensityKgM3 { get; init; } = 1000.0;

    public double ViscosityPaS { get; init; } = 0.001;

    /// <summary>Turbulent plateau power number measured on this rig (§4.3).</summary>
    public double TurbulentPowerNumber { get; init; }

    /// <summary>Operating point the criterion is anchored on.</summary>
    public double AgitationRpm { get; init; }

    public double GasFlowLpm { get; init; }

    /// <summary>Calibrated van 't Riet correlation; required by the constant-kLa criterion.</summary>
    public KlaCorrelationResult? Correlation { get; init; }

    /// <summary>Range of P/V actually covered by the assays behind the correlation [W/m³].</summary>
    public double? CalibratedMinVolumetricPower { get; init; }

    public double? CalibratedMaxVolumetricPower { get; init; }

    /// <summary>Range of superficial velocity actually covered [m/s].</summary>
    public double? CalibratedMinSuperficialVelocity { get; init; }

    public double? CalibratedMaxSuperficialVelocity { get; init; }
}

/// <summary>The vessel being scaled to, plus the rules that make the solution unique.</summary>
public sealed record ScaleUpTarget
{
    /// <summary>Working liquid volume of the target vessel [m³].</summary>
    public double LiquidVolumeM3 { get; init; }

    public double VesselDiameterM { get; init; }

    public double ImpellerDiameterM { get; init; }

    public double MinRpm { get; init; } = 15.0;

    public double MaxRpm { get; init; } = 1000.0;

    public ScaleUpCriterion Criterion { get; init; } = ScaleUpCriterion.ConstantVolumetricPower;

    public ScaleUpGasRule GasRule { get; init; } = ScaleUpGasRule.None;

    /// <summary>Value carried by <see cref="GasRule"/>: vvm, m/s, or L/min depending on the rule.</summary>
    public double GasRuleValue { get; init; }
}

/// <summary>
/// What the calculator produced, or why it refused. A result is an estimate for sizing, never a
/// validation of the process at the new scale (§18.3 step 7.1).
/// </summary>
public sealed record ScaleUpResult
{
    public bool IsSolved { get; init; }

    /// <summary>Reasons the calculation was refused; empty when solved.</summary>
    public IReadOnlyList<string> Refusals { get; init; } = [];

    /// <summary>Extrapolation and safety warnings that do not block the estimate.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public double TargetAgitationRpm { get; init; }

    public double TargetGasFlowLpm { get; init; }

    public double TargetGasFlowVvm { get; init; }

    public double TargetSuperficialVelocityMs { get; init; }

    /// <summary>Ungassed shaft power at the solved point [W].</summary>
    public double ShaftPowerW { get; init; }

    public double VolumetricPowerWm3 { get; init; }

    public double TorqueNm { get; init; }

    public double TipSpeedMs { get; init; }

    public double ReynoldsNumber { get; init; }

    public double FroudeNumber { get; init; }

    public double GasFlowNumber { get; init; }

    /// <summary>Predicted kLa at the solved point, when a correlation is available [1/h].</summary>
    public double? PredictedKlaPerHour { get; init; }

    /// <summary>Nienow flooding flow at the solved rotation, in the target geometry [L/min].</summary>
    public double FloodingGasFlowLpm { get; init; }

    /// <summary>Q_g / Q_g,F: at or above 1 the impeller is flooded.</summary>
    public double FloodingMargin { get; init; }

    public bool IsFlooded { get; init; }

    /// <summary>Volume ratio between target and reference, for the scale-jump warning.</summary>
    public double ScaleFactor { get; init; }

    /// <summary>Values the reference scale ran at, echoed for the summary sheet.</summary>
    public double ReferenceVolumetricPowerWm3 { get; init; }

    public double ReferenceSuperficialVelocityMs { get; init; }

    public double ReferenceAgitationRpm { get; init; }

    public double ReferenceGasFlowLpm { get; init; }

    public double? ReferenceKlaPerHour { get; init; }

    public ScaleUpCriterion Criterion { get; init; }

    public ScaleUpGasRule GasRule { get; init; }
}
