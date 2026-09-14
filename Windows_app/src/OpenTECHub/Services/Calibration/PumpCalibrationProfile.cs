using System;
using System.Collections.Generic;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Calibration;

/// <summary>
/// A persistent, named calibration profile for a specific peristaltic pump hose.
/// </summary>
/// <remarks>
/// Profiles exist solely in the application library; the pump hardware maintains only
/// one active curve at a time. Each profile captures the quartic/quadratic C0+C1 curve,
/// the volumetric points it was fitted against, and statistical metadata.
/// </remarks>
public sealed record PumpCalibrationProfile
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>Schema revision of this file for future forward/backward compatibility.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Unique identifier for this profile across renames.</summary>
    public string ProfileId { get; init; } = Guid.NewGuid().ToString("D");

    public string Id => ProfileId;

    /// <summary>User-visible hose name (e.g. "Silicone 2mm", "Tygon R-3603").</summary>
    public required string Name { get; init; }

    /// <summary>When the profile was first created (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the profile was last modified (UTC).</summary>
    public DateTimeOffset ModifiedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Derived flow at the transition, stored for profile summaries.</summary>
    public double TransitionFlowMlMin { get; init; }

    /// <summary>Fitted transition speed St in internal speed units S.</summary>
    public double TransitionSpeedUnits { get; init; }

    /// <summary>Derived low-side derivative at the transition.</summary>
    public double LowSlope { get; init; }

    /// <summary>Derived high-side derivative at the transition.</summary>
    public double HighSlope { get; init; }

    public double LowA { get; init; }
    public double LowB { get; init; }
    public double LowK { get; init; }
    public double LowF { get; init; }
    public double LowC { get; init; }
    public double HighK { get; init; }
    public double HighF { get; init; }
    public double HighC { get; init; }

    /// <summary>Volumetric calibration points associated with this profile.</summary>
    public PumpCalibrationPoint[] CalibrationPoints { get; init; } = [];

    /// <summary>Goodness-of-fit statistics computed when the curve was adjusted.</summary>
    public PumpFitStatistics? FitStatistics { get; init; }

    /// <summary>Fitting algorithm identifier and version (e.g. "1.0", "constrained-lsq-v1").</summary>
    public string AlgorithmVersion { get; init; } = "quartic-quadratic-c1-v2";

    /// <summary>Optional operator notes (e.g. tube lot, installation date).</summary>
    public string? OptionalNotes { get; init; }

    /// <summary>When this profile was last dispatched and confirmed on a pump node.</summary>
    public DateTimeOffset? LastAppliedUtc { get; init; }

    /// <summary>Firmware version of the pump that confirmed the last dispatch (e.g. "3.11").</summary>
    public string? LastAppliedPumpFirmware { get; init; }

    /// <summary>Constructs a <see cref="PumpDualRangeCurve"/> from this profile's parameters.</summary>
    public PumpDualRangeCurve ToCurve() => new(
            new PolynomialCalibration(LowK, LowF, LowC) { A = LowA, B = LowB },
            new PolynomialCalibration(HighK, HighF, HighC),
            TransitionSpeedUnits);

    /// <summary>Creates a profile from an adjusted curve and optional metadata.</summary>
    public static PumpCalibrationProfile FromCurve(
        string name,
        PumpDualRangeCurve curve,
        PumpCalibrationPoint[]? points = null,
        PumpFitStatistics? fitStatistics = null,
        string? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new PumpCalibrationProfile
        {
            Name = name.Trim(),
            TransitionFlowMlMin = curve.TransitionFlow,
            TransitionSpeedUnits = curve.TransitionSpeed,
            LowSlope = curve.LowSlope,
            HighSlope = curve.HighSlope,
            LowA = curve.LowSpeed.A,
            LowB = curve.LowSpeed.B,
            LowK = curve.LowSpeed.K,
            LowF = curve.LowSpeed.F,
            LowC = curve.LowSpeed.C,
            HighK = curve.HighSpeed.K,
            HighF = curve.HighSpeed.F,
            HighC = curve.HighSpeed.C,
            CalibrationPoints = points ?? [],
            FitStatistics = fitStatistics,
            OptionalNotes = notes
        };
    }
}

/// <summary>
/// Summary projection of a filed pump profile, used for fast listings without loading full sample arrays.
/// </summary>
public sealed record PumpCalibrationProfileSummary(
    string Name,
    string ProfileId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    int PointCount,
    double TransitionFlow,
    double TransitionSpeed,
    double LowSlope,
    double HighSlope,
    int SchemaVersion,
    bool IsCompatible,
    DateTimeOffset? LastAppliedUtc = null)
{
    public string DisplayName => Name;
    public string Id => ProfileId;
}

/// <summary>
/// Goodness-of-fit statistics recorded alongside the profile for auditing and inspection.
/// </summary>
public sealed record PumpFitStatistics
{
    public double SSE { get; init; }
    public double RMSE { get; init; }
    public double RSquared { get; init; }
    public int TotalPoints { get; init; }
    public int LowPointCount { get; init; }
    public int HighPointCount { get; init; }
    public double LowSSE { get; init; }
    public double HighSSE { get; init; }
    public double LowRMSE { get; init; }
    public double HighRMSE { get; init; }

    public static PumpFitStatistics FromFitResult(PumpFitResult fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        return new PumpFitStatistics
        {
            SSE = fit.SSE,
            RMSE = fit.RMSE,
            RSquared = fit.RSquared,
            TotalPoints = fit.TotalPoints,
            LowPointCount = fit.LowPointCount,
            HighPointCount = fit.HighPointCount,
            LowSSE = fit.LowSSE,
            HighSSE = fit.HighSSE,
            LowRMSE = fit.LowRMSE,
            HighRMSE = fit.HighRMSE,
        };
    }
}
