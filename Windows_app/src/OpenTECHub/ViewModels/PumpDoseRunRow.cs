using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenTECHub.Services.Calibration;

namespace OpenTECHub.ViewModels;

/// <summary>
/// One editable row of the dose point table: speed, time and volume as typed, the flow derived,
/// an optional t₀ and whether the row counts for the point. A row that does not parse stays in
/// the table but out of the point.
/// </summary>
public sealed partial class PumpDoseRunRow : ObservableObject
{
    public PumpDoseRunRow(PumpDoseRun? run = null)
    {
        if (run is { } r)
        {
            SpeedText = DosingInput.Format(r.SpeedUnits, 0);
            SecondsText = DosingInput.Format(r.Seconds, r.Seconds % 1 == 0 ? 0 : 2);
            VolumeText = DosingInput.Format(r.VolumeMl, 4);
            DeadMsText = r.DeadTimeMs is { } d ? DosingInput.Format(d, 0) : "";
            IsActive = r.IsActive;
            Source = r.Source;
            CapturedAtUtc = r.CapturedAtUtc;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyPropertyChangedFor(nameof(Speed))]
    public partial string SpeedText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyPropertyChangedFor(nameof(RateText))]
    public partial string SecondsText { get; set; } = "60";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyPropertyChangedFor(nameof(RateText))]
    public partial string VolumeText { get; set; } = "";

    /// <summary>Start-up dead time t₀, ms (0..2000); empty = not given.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeadMsError))]
    public partial string DeadMsText { get; set; } = "";

    /// <summary>Counts for the dose point. Active rows share one speed.</summary>
    [ObservableProperty] public partial bool IsActive { get; set; }

    /// <summary>null = measured here; curve = copied from the curve's runs; manual = typed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    public partial string? Source { get; set; }

    public string? CapturedAtUtc { get; init; }

    public double? Speed => DosingInput.TryParseDouble(SpeedText, out var s) && s is >= 1 and <= 1000 ? Math.Round(s) : null;

    public PumpDoseRepetition? Repetition =>
        DosingInput.TryParseDouble(SecondsText, out var s) && s > 0 && double.IsFinite(s) &&
        DosingInput.TryParseDouble(VolumeText, out var v) && v > 0 && double.IsFinite(v)
            ? new PumpDoseRepetition(s, v)
            : null;

    /// <summary>The typed t₀, or null when empty or out of range.</summary>
    public double? DeadMs =>
        DosingInput.TryParseDouble(DeadMsText, out var d) && d is >= 0 and <= PumpDosePointMath.MaxDeadMs ? d : null;

    public string? DeadMsError =>
        string.IsNullOrWhiteSpace(DeadMsText) || DeadMs is not null ? null : "t₀: 0 a 2000 ms";

    public bool IsValid => Speed is not null && Repetition is not null;

    public string RateText => Repetition is { } r ? r.RateMlMin.ToString("F3", CultureInfo.CurrentCulture) : "—";

    public string SourceText => Source switch
    {
        PumpDosePointMath.SourceCurve => "curva",
        PumpDosePointMath.SourceManual => "digitado",
        _ => "ensaio",
    };

    public PumpDoseRun? ToRun() => Speed is { } s && Repetition is { } r
        ? new PumpDoseRun
        {
            SpeedUnits = s, Seconds = r.Seconds, VolumeMl = r.VolumeMl, DeadTimeMs = DeadMs,
            IsActive = IsActive, Source = Source, CapturedAtUtc = CapturedAtUtc,
        }
        : null;
}
