using System;
using System.Collections.Generic;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Calibration;

/// <summary>
/// A persistent, named calibration profile for a specific peristaltic pump hose.
/// </summary>
/// <remarks>
/// Profiles exist solely in the application library; the pump hardware maintains only
/// one active curve at a time. Each profile captures the continuous piecewise-linear
/// dual-range curve, the volumetric points it was fitted against, and statistical metadata.
/// </remarks>
public sealed record PumpCalibrationProfile
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema revision of this file for future forward/backward compatibility.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Unique identifier for this profile across renames.</summary>
    public string ProfileId { get; init; } = Guid.NewGuid().ToString("D");

    /// <summary>User-visible hose name (e.g. "Silicone 2mm", "Tygon R-3603").</summary>
    public required string Name { get; init; }

    /// <summary>When the profile was first created (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the profile was last modified (UTC).</summary>
    public DateTimeOffset ModifiedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Transition flow Qt in mL/min where low and high slopes meet.</summary>
    public double TransitionFlowMlMin { get; init; }

    /// <summary>Fitted transition speed St in internal speed units S.</summary>
    public double TransitionSpeedUnits { get; init; }

    /// <summary>Low-speed slope (mL/min per S unit) for S &lt;= St.</summary>
    public double LowSlope { get; init; }

    /// <summary>High-speed slope (mL/min per S unit) for S &gt; St.</summary>
    public double HighSlope { get; init; }

    /// <summary>Volumetric calibration points associated with this profile.</summary>
    public PumpCalibrationPoint[] CalibrationPoints { get; init; } = [];

    /// <summary>Goodness-of-fit statistics computed when the curve was adjusted.</summary>
    public PumpFitStatistics? FitStatistics { get; init; }

    /// <summary>Fitting algorithm identifier and version (e.g. "1.0", "constrained-lsq-v1").</summary>
    public string AlgorithmVersion { get; init; } = "1.0";

    /// <summary>Optional operator notes (e.g. tube lot, installation date).</summary>
    public string? OptionalNotes { get; init; }

    /// <summary>When this profile was last dispatched and confirmed on a pump node.</summary>
    public DateTimeOffset? LastAppliedUtc { get; init; }

    /// <summary>Firmware version of the pump that confirmed the last dispatch (e.g. "3.11").</summary>
    public string? LastAppliedPumpFirmware { get; init; }

    /// <summary>Constructs a <see cref="PumpDualRangeCurve"/> from this profile's parameters.</summary>
    public PumpDualRangeCurve ToCurve() =>
        new(LowSlope, HighSlope, TransitionSpeedUnits, TransitionFlowMlMin);

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
    DateTimeOffset? LastAppliedUtc = null);

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
