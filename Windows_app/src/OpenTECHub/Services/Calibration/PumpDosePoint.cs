namespace OpenTECHub.Services.Calibration;

/// <summary>One timed run at the dose speed and the volume it delivered.</summary>
/// <remarks>
/// Like every pump calibration: a set time at one speed, the volume read on a graduated
/// cylinder, and the flow follows from the two.
/// </remarks>
/// <param name="Seconds">Motor time, timed by the node (<c>pump_speed_ms</c>), s.</param>
/// <param name="VolumeMl">Volume read on the graduated cylinder, mL.</param>
public sealed record PumpDoseRepetition(double Seconds, double VolumeMl)
{
    public double RateMlMin => Seconds > 0.0 ? VolumeMl / (Seconds / 60.0) : double.NaN;
}

/// <summary>
/// One row of the dose point's table: a continuous run at one speed, the volume read, an
/// optional start-up dead time typed by the operator, and whether it counts for the point.
/// </summary>
/// <remarks>
/// Rows come from runs in this section, from the curve's runs (copied, not linked) or are
/// typed by hand. The active rows (all at one speed) make the dose point: their mean flow, and
/// the mean of the t₀ typed on them.
/// </remarks>
public sealed record PumpDoseRun
{
    public double SpeedUnits { get; init; }
    public double Seconds { get; init; }
    public double VolumeMl { get; init; }

    /// <summary>Start-up dead time typed by the operator, ms; null = not given.</summary>
    public double? DeadTimeMs { get; init; }

    public bool IsActive { get; init; }

    /// <summary>null = measured here; <see cref="PumpDosePointMath.SourceCurve"/> or <see cref="PumpDosePointMath.SourceManual"/>.</summary>
    public string? Source { get; init; }

    public string? CapturedAtUtc { get; init; }

    public double RateMlMin => Seconds > 0.0 ? VolumeMl / (Seconds / 60.0) : double.NaN;
}

/// <summary>N pulses of a fixed length run by the node's own dosing, and the volume they delivered.</summary>
public sealed record PumpPulseCheck(double OnMs, int Count, double VolumeMl)
{
    public double PerPulseMl => Count > 0 ? VolumeMl / Count : double.NaN;
}

/// <summary>
/// The dose point of a hose: one fixed speed and the flow measured there (plan 2026-10-11 §6).
/// </summary>
/// <remarks>
/// Independent of the polynomial curve: pulse dosing runs on this point alone, so a hose
/// profile may hold it without a fitted curve. It does not expire (decision D7); the card shows
/// its date and recalibrating is the operator's call.
/// </remarks>
public sealed record PumpDosePoint
{
    public string Id { get; init; } = Guid.NewGuid().ToString("D");

    public double SpeedUnits { get; init; }

    public PumpDoseRepetition[] Repetitions { get; init; } = [];

    /// <summary>Mean of the repetitions, mL/min.</summary>
    public double RateMlMin { get; init; }

    /// <summary>Coefficient of variation of the repetitions, %.</summary>
    public double CvPercent { get; init; }

    public PumpPulseCheck[] PulseChecks { get; init; } = [];

    /// <summary>Start-up dead time t₀ from the pulse checks, ms; null = not measured.</summary>
    public double? DeadTimeMs { get; init; }

    /// <summary>Shortest delivering time meeting the precision target, ms; null = not measured.</summary>
    public double? RecommendedMinOnMs { get; init; }

    public string CapturedAtUtc { get; init; } = "";

    /// <summary>
    /// Where the repetitions came from: null = measured in this section; otherwise
    /// <see cref="PumpDosePointMath.SourceCurve"/>, <see cref="PumpDosePointMath.SourceManual"/> or
    /// <see cref="PumpDosePointMath.SourceMixed"/>. Shown to the operator, never used in arithmetic.
    /// </summary>
    public string? Source { get; init; }
}

/// <summary>Arithmetic of the dose-point calibration.</summary>
public static class PumpDosePointMath
{
    /// <summary>Repetitions above this CV ask for another run before saving, %.</summary>
    public const double MaxCvPercent = 3.0;

    /// <summary>Precision target of the recommended minimum pulse (decision D8).</summary>
    public const double PrecisionTarget = 0.05;

    public const double MaxDeadMs = 2000.0;

    /// <summary>Repetitions copied from the curve's runs at one speed (a copy: not linked to the curve).</summary>
    public const string SourceCurve = "curve";

    /// <summary>Repetitions typed or edited by hand in the table.</summary>
    public const string SourceManual = "manual";

    /// <summary>Some measured here, some imported or typed.</summary>
    public const string SourceMixed = "mixed";

    public static string DescribeSource(string? source) => source switch
    {
        null or "" => "medido nesta seção",
        SourceCurve => "importado dos ensaios da curva",
        SourceManual => "digitado/editado na tabela",
        SourceMixed => "misto (medido, importado ou digitado)",
        _ => source,
    };

    /// <summary>Speeds with at least one valid curve run, ascending, for the import list.</summary>
    public static IReadOnlyList<double> ImportableSpeeds(IEnumerable<OpenTECHub.Services.Persistence.PumpCalibrationPoint> runs)
        => [.. runs.Where(IsUsable).Select(r => r.SpeedUnits).Distinct().Order()];

    /// <summary>Every valid curve run at <paramref name="speed"/>, as dose repetitions (values copied).</summary>
    public static IReadOnlyList<PumpDoseRepetition> RepetitionsAt(IEnumerable<OpenTECHub.Services.Persistence.PumpCalibrationPoint> runs, double speed)
        => [.. runs.Where(r => IsUsable(r) && Math.Abs(r.SpeedUnits - speed) < 1e-9).Select(r => new PumpDoseRepetition(r.Seconds, r.VolumeMl))];

    private static bool IsUsable(OpenTECHub.Services.Persistence.PumpCalibrationPoint r)
        => double.IsFinite(r.SpeedUnits) && r.SpeedUnits >= 1 && r.SpeedUnits <= 1000 &&
           double.IsFinite(r.Seconds) && r.Seconds > 0 && double.IsFinite(r.VolumeMl) && r.VolumeMl > 0;

    public static (double Mean, double CvPercent) MeanAndCv(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return (double.NaN, double.NaN);
        }

        var mean = values.Average();
        if (values.Count < 2 || mean == 0.0)
        {
            return (mean, 0.0);
        }

        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return (mean, Math.Sqrt(variance) / Math.Abs(mean) * 100.0);
    }

    /// <summary>
    /// t₀ of one pulse check: <c>t_on − 60000·v_real/Q</c>. Negative or above 2 s means a
    /// measuring error (null).
    /// </summary>
    public static double? DeadTimeMs(PumpPulseCheck check, double rateMlMin)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (rateMlMin <= 0.0 || check.Count <= 0 || check.VolumeMl <= 0.0)
        {
            return null;
        }

        var t0 = check.OnMs - (60000.0 * check.PerPulseMl / rateMlMin);
        return t0 is >= 0.0 and <= MaxDeadMs ? t0 : null;
    }

    /// <summary>
    /// t₀ over all checks. With two or more pulse lengths it is the intercept of the fit
    /// <c>v = (Q/60000)·(t_on − t₀)</c> with Q fixed, which is the mean of the single estimates.
    /// </summary>
    public static double? DeadTimeMs(IReadOnlyList<PumpPulseCheck> checks, double rateMlMin)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var estimates = checks.Select(c => DeadTimeMs(c, rateMlMin)).Where(v => v is not null).Select(v => v!.Value).ToArray();
        return estimates.Length == 0 ? null : estimates.Average();
    }

    /// <summary>
    /// Shortest delivering time that meets <see cref="PrecisionTarget"/>, from repeated checks at
    /// one pulse length: CV(t) ≈ CV_c·t_c/t. Needs at least two checks of the same length.
    /// </summary>
    public static double? RecommendedMinOnMs(IReadOnlyList<PumpPulseCheck> checks, double deadMs)
    {
        ArgumentNullException.ThrowIfNull(checks);
        double? best = null;
        foreach (var group in checks.GroupBy(c => Math.Round(c.OnMs)).Where(g => g.Count() >= 2))
        {
            var (_, cv) = MeanAndCv(group.Select(c => c.PerPulseMl).ToArray());
            var deliveryMs = group.Key - deadMs;
            if (deliveryMs <= 0.0)
            {
                continue;
            }

            var recommended = Math.Max(200.0, deliveryMs * (cv / 100.0) / PrecisionTarget);
            best = best is null ? recommended : Math.Min(best.Value, recommended);
        }

        return best;
    }

    /// <summary>Builds the point from the repetitions and pulse checks gathered so far.</summary>
    public static PumpDosePoint Build(double speed, IReadOnlyList<PumpDoseRepetition> repetitions,
        IReadOnlyList<PumpPulseCheck> checks, DateTimeOffset capturedAt, string? id = null, string? source = null,
        double? typedDeadMs = null)
    {
        ArgumentNullException.ThrowIfNull(repetitions);
        ArgumentNullException.ThrowIfNull(checks);
        var (mean, cv) = MeanAndCv(repetitions.Select(r => r.RateMlMin).ToArray());
        // A t₀ typed by the operator wins over the one derived from pulse checks.
        var dead = typedDeadMs is { } typed && typed is >= 0.0 and <= MaxDeadMs ? typed : DeadTimeMs(checks, mean);
        return new PumpDosePoint
        {
            Id = id ?? Guid.NewGuid().ToString("D"),
            SpeedUnits = speed,
            Repetitions = [.. repetitions],
            RateMlMin = mean,
            CvPercent = cv,
            PulseChecks = [.. checks],
            DeadTimeMs = dead,
            RecommendedMinOnMs = RecommendedMinOnMs(checks, dead ?? 0.0),
            CapturedAtUtc = capturedAt.ToString("o"),
            Source = source,
        };
    }
}
