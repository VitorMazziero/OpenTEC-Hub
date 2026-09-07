using System.Collections.Generic;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>Everything <see cref="PowerAnalysisEngine.AnalyzePoint"/> needs to turn a captured mean into results.</summary>
public sealed record PowerPointInput
{
    /// <summary>Mean signed torque over the capture window, % of nominal.</summary>
    public double MeanTorquePercent { get; init; }

    /// <summary>Half-width of the 95 % CI on the torque mean, in the same % units (§12.1).</summary>
    public double TorquePercentCi95 { get; init; }

    /// <summary>Mean measured rotation over the window (ServoRpm), rpm (§2.5).</summary>
    public double MeanRpm { get; init; }

    public FluidProperties Fluid { get; init; } = new();
    public PowerGeometry Geometry { get; init; } = new();

    /// <summary>Null → torque is uncalibrated and the result is relative (§9.1).</summary>
    public TorqueCalibration? Calibration { get; init; }

    /// <summary>Null → no tare; net power falls back to shaft power and the result is relative (§9.2).</summary>
    public TareCurve? Tare { get; init; }

    public double SnrFloorMultiple { get; init; } = 3.0;

    /// <summary>Assumed motor nominal torque when there is no calibration to carry it.</summary>
    public double MotorRatedTorqueNm { get; init; } = 1.27;

    // Gassed inputs (§4.5, §11, §16)
    public double? GasFlowLpm { get; init; }
    public double? GasFlowVvm { get; init; }
    public double? ReferenceP0W { get; init; }
    public double? ReferenceP0Ci95W { get; init; }
    public P0Provenance P0Provenance { get; init; } = P0Provenance.None;
}

/// <summary>The power number of one impeller stage, with its own diameter and Reynolds number (§4.3).</summary>
public sealed record StageNpResult(
    int StageIndex,
    ImpellerType Type,
    double DiameterM,
    double PowerNumber,
    double ReynoldsNumber,
    double PowerNumberCi95,
    string Label = "");

/// <summary>The analysed result of one captured operating point.</summary>
public sealed record PowerPointResult
{
    public double MeanRpm { get; init; }
    public double MeanTorqueNm { get; init; }
    public double ShaftPowerW { get; init; }
    public double VoidPowerW { get; init; }

    /// <summary>Shaft power minus the interpolated tare; equals shaft power in relative mode (§4.2).</summary>
    public double NetPowerW { get; init; }
    public double NetPowerCi95W { get; init; }

    /// <summary>True when the net power does not clear the SNR gate — the point is mostly noise (§7.2).</summary>
    public bool BelowNoiseFloor { get; init; }

    /// <summary>True when calibration or tare is missing: comparisons are relative, not absolute (§9).</summary>
    public bool IsRelative { get; init; }

    /// <summary>Per-impeller power numbers (equal-split hypothesis, §4.3).</summary>
    public IReadOnlyList<StageNpResult> Stages { get; init; } = [];

    /// <summary>Whole-shaft power number over the reference diameter, for very asymmetric sets (§4.3).</summary>
    public double AssemblyPowerNumber { get; init; }
    public double AssemblyReynoldsNumber { get; init; }
    public double AssemblyPowerNumberCi95 { get; init; }
    public double ReferenceDiameterM { get; init; }

    // Gassed and flooding fields (§4.5, §11, §16)
    public double? GasFlowLpm { get; init; }
    public double? GasFlowVvm { get; init; }
    public double? GasFlowNumber { get; init; }
    public double? FroudeNumber { get; init; }
    public double? GassedPowerW { get; init; }
    public double? ReferenceP0W { get; init; }
    public double? ReferenceP0Ci95W { get; init; }
    public P0Provenance P0Provenance { get; init; } = P0Provenance.None;
    public double? PowerRatio { get; init; }
    public double? PowerRatioCi95 { get; init; }
}

/// <summary>The turbulent-plateau power number fitted over the accepted points (§4.3, §16).</summary>
public sealed record PlateauFitResult
{
    public bool HasFit { get; init; }
    public double PowerNumber { get; init; }
    public double PowerNumberCi95 { get; init; }
    public int PointsUsed { get; init; }
    public double ReynoldsCutoff { get; init; }
}

/// <summary>The affine mechanical→electrical correlation P_elec ≈ a·P_mec + b (§4.8).</summary>
public sealed record EnergyCorrelationResult
{
    public bool HasFit { get; init; }
    public double Slope { get; init; }
    public double InterceptW { get; init; }
    public double RSquared { get; init; }
    public int PointCount { get; init; }
}
