using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Calibration;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>Calibrações › Bomba Externa › Ponto de dosagem (plan 2026-10-11, Phase 4).</summary>
public sealed class PumpDosePointTests
{
    private static string FreshDir() =>
        Path.Combine(Path.GetTempPath(), "OpenTECHub.Tests", "PumpProfiles", Guid.NewGuid().ToString("N"));

    private static SensorSnapshot Online(int? drive = null, bool? doseActive = null, long? pulses = null,
        double? doseSpeed = null, double? doseRate = null, double? doseDead = null)
        => new()
        {
            HasPumpTelemetry = true, PumpOnline = true, PumpCommEnabled = true, PumpVolume = 0.0, PumpFlow = 0.0,
            PumpDrive = drive, PumpDoseActive = doseActive, PumpDosePulses = pulses,
            PumpDoseSpeed = doseSpeed, PumpDoseRate = doseRate, PumpDoseDeadMs = doseDead,
        };

    private static (PumpCalibrationViewModel Vm, RecordingDeviceService Device, TestClock Clock, MemorySettingsService Settings, string Dir) Create()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var dir = FreshDir();
        var vm = new PumpCalibrationViewModel(device, settings, timeProvider: clock, profileStore: new PumpCalibrationProfileStore(dir));
        device.PushTelemetry(Online(drive: (int)PumpDriveState.Stopped, doseActive: false));
        return (vm, device, clock, settings, dir);
    }

    // ── Arithmetic ──────────────────────────────────────────────────────────

    [Fact]
    public void Mean_and_cv_of_repetitions()
    {
        var (mean, cv) = PumpDosePointMath.MeanAndCv([5.40, 5.36, 5.43]);
        Assert.Equal(5.3967, mean, 4);
        Assert.Equal(0.65, cv, 2);
    }

    [Fact]
    public void Dead_time_from_a_pulse_check()
    {
        // 20 pulses of 1.0 s at 5.40 mL/min should give 1.80 mL; 1.72 mL means t₀ ≈ 44 ms.
        var t0 = PumpDosePointMath.DeadTimeMs(new PumpPulseCheck(1000.0, 20, 1.72), 5.40);
        Assert.Equal(44.4, t0!.Value, 1);
        Assert.Null(PumpDosePointMath.DeadTimeMs(new PumpPulseCheck(1000.0, 20, 2.00), 5.40)); // negative: measuring error
    }

    [Fact]
    public void Recommended_minimum_needs_repeated_checks_of_one_length()
    {
        Assert.Null(PumpDosePointMath.RecommendedMinOnMs([new PumpPulseCheck(1000, 20, 1.72)], 44.0));
        // Per-pulse 0.086 and 0.0878 mL: CV ≈ 1.5 % at 956 ms of delivery → 5 % at ≈ 284 ms.
        var rec = PumpDosePointMath.RecommendedMinOnMs([new PumpPulseCheck(1000, 20, 1.72), new PumpPulseCheck(1000, 20, 1.756)], 44.0);
        Assert.InRange(rec!.Value, 250.0, 320.0);
    }

    // ── Profile schema 3 ────────────────────────────────────────────────────

    [Fact]
    public void Schema_2_profile_loads_without_dose_points_and_is_saved_as_3()
    {
        var dir = FreshDir();
        Directory.CreateDirectory(dir);
        var store = new PumpCalibrationProfileStore(dir);
        File.WriteAllText(Path.Combine(dir, PumpProfileFileContracts.ProfileFileName("Silicone 2mm")),
            """{"schemaVersion":2,"profileId":"0b7c7c2e-0000-4000-8000-000000000001","hasFittedCurve":false,"name":"Silicone 2mm","calibrationPoints":[],"algorithmVersion":"points-only-v1"}""");

        var loaded = store.LoadProfile("Silicone 2mm")!;
        Assert.Empty(loaded.DosePoints);
        Assert.True(Assert.Single(store.ListProfiles()).IsCompatible);

        var point = PumpDosePointMath.Build(120, [new PumpDoseRepetition(120, 10.8)], [], DateTimeOffset.UtcNow);
        store.SaveProfile(loaded with { DosePoints = [point] }, overwrite: true);

        var text = File.ReadAllText(Path.Combine(dir, PumpProfileFileContracts.ProfileFileName("Silicone 2mm")));
        Assert.Contains("\"schemaVersion\": 3", text.Replace("\"schemaVersion\":3", "\"schemaVersion\": 3"));
        var again = store.LoadProfile("Silicone 2mm")!;
        Assert.Equal(5.4, Assert.Single(again.DosePoints).RateMlMin, 9);
        Assert.Equal(1, Assert.Single(store.ListProfiles()).DosePointCount);
    }

    // ── View model ──────────────────────────────────────────────────────────

    [Fact]
    public void Continuous_run_is_timed_by_the_node()
    {
        var (vm, device, clock, _, _) = Create();
        using var _ = vm;
        var dose = vm.DosePoint;
        dose.SpeedText = "120";
        dose.DurationText = "120";
        dose.StartRunCommand.Execute(null);
        Assert.Equal("""{"pump_speed":120,"pump_speed_ms":120000}""", device.Sent[^1]);
        Assert.False(vm.CanStartManual);   // curve tools wait

        clock.ShiftUtc(TimeSpan.FromSeconds(121));
        dose.Tick();
        Assert.True(dose.HasPendingRun);
        Assert.DoesNotContain(device.Sent, f => f == """{"pump_speed":0}""");

        clock.ShiftUtc(TimeSpan.FromSeconds(3));
        dose.Tick();
        Assert.Equal("""{"pump_speed":0}""", device.Sent[^1]); // backup only
        Assert.False(dose.IsRunning);

        dose.VolumeText = "10.80";
        dose.RegisterRepetitionCommand.Execute(null);
        Assert.Equal(5.4, dose.MeanRateMlMin, 9);
        Assert.Contains("5", dose.RepetitionSummaryText);
    }

    [Fact]
    public void Calibration_is_blocked_while_the_node_doses_or_runs_a_profile()
    {
        var (vm, device, _, _, _) = Create();
        using var _ = vm;
        device.PushTelemetry(Online(drive: (int)PumpDriveState.DoseIdle, doseActive: true));
        Assert.False(vm.DosePoint.CanStartRun);
        Assert.NotNull(vm.DosePoint.InterlockText);

        device.PushTelemetry(Online(drive: (int)PumpDriveState.Stopped, doseActive: false));
        Assert.True(vm.DosePoint.CanStartRun);
    }

    [Fact]
    public void Pulse_check_runs_on_the_node_dosing_and_measures_dead_time()
    {
        var (vm, device, clock, _, _) = Create();
        using var _ = vm;
        var dose = vm.DosePoint;
        dose.StartRunCommand.Execute(null);
        clock.ShiftUtc(TimeSpan.FromSeconds(125));
        dose.Tick();
        dose.VolumeText = "10.80";
        dose.RegisterRepetitionCommand.Execute(null);

        dose.PulseOnText = "1.0";
        dose.PulseCountText = "20";
        dose.PulseIntervalText = "5";
        Assert.True(dose.CanStartPulseCheck);
        dose.StartPulseCheckCommand.Execute(null);
        Assert.Equal(
            """{"pump_dose_ml":0.09,"pump_dose_period_s":5.0,"pump_dose_speed":120.0,"pump_dose_rate_mlmin":5.4,"pump_dose_total_ml":1.8,"pump_dose_dead_ms":0.0,"pump_dose":1}""",
            device.Sent[^1]);

        device.PushTelemetry(Online(drive: (int)PumpDriveState.DosePulse, doseActive: true, pulses: 3));
        Assert.True(dose.IsPulseCheckRunning);
        device.PushTelemetry(Online(drive: (int)PumpDriveState.Stopped, doseActive: false, pulses: 20));
        Assert.True(dose.HasPendingPulseCheck);

        dose.PulseVolumeText = "1.72";
        dose.RegisterPulseCheckCommand.Execute(null);
        Assert.Single(dose.PulseChecks);
        Assert.Contains("t₀", dose.PulseCheckSummaryText);
    }

    [Fact]
    public void Save_send_and_use_for_dosing()
    {
        var (vm, device, clock, settings, dir) = Create();
        using var _ = vm;
        vm.ActiveProfileName = "Silicone 2mm";
        var dose = vm.DosePoint;
        dose.StartRunCommand.Execute(null);
        clock.ShiftUtc(TimeSpan.FromSeconds(125));
        dose.Tick();
        dose.VolumeText = "10.80";
        dose.RegisterRepetitionCommand.Execute(null);

        // Saving works without any curve point: the dose point is independent of the curve.
        dose.SaveToProfileCommand.Execute(null);
        var saved = new PumpCalibrationProfileStore(dir).LoadProfile("Silicone 2mm")!;
        Assert.False(saved.HasFittedCurve);
        Assert.Equal(5.4, Assert.Single(saved.DosePoints).RateMlMin, 9);

        dose.UseForDosingCommand.Execute(null);
        var point = settings.Current.PumpControl.Dose.Point!;
        Assert.Equal(120.0, point.SpeedUnits);
        Assert.Equal("Silicone 2mm", point.ProfileName);

        dose.SendToNodeCommand.Execute(null);
        Assert.Equal("""{"pump_dose_speed":120.0,"pump_dose_rate_mlmin":5.4,"pump_dose_dead_ms":0.0}""", device.Sent[^1]);
        device.PushTelemetry(Online(drive: 0, doseActive: false, doseSpeed: 120.0, doseRate: 5.4, doseDead: 0));
        Assert.Contains("confirmado pelo nó", dose.StatusText);
    }

    [Fact]
    public void Loading_a_profile_restores_its_dose_point()
    {
        var dir = FreshDir();
        var store = new PumpCalibrationProfileStore(dir);
        var point = PumpDosePointMath.Build(80, [new PumpDoseRepetition(120, 7.2), new PumpDoseRepetition(120, 7.3)], [], DateTimeOffset.UtcNow);
        store.SaveProfile(new PumpCalibrationProfile { Name = "Tygon", HasFittedCurve = false, DosePoints = [point] });

        var device = new RecordingDeviceService();
        using var vm = new PumpCalibrationViewModel(device, new MemorySettingsService(), profileStore: store);
        Assert.Equal("80", vm.DosePoint.SpeedText);
        Assert.Equal(2, vm.DosePoint.Repetitions.Count);
        Assert.Contains("Tygon", vm.DosePoint.ProfileText);
        // A profile saved before the table existed: its repetitions become active rows.
        Assert.Equal(2, vm.DosePoint.Table.Count);
        Assert.All(vm.DosePoint.Table, r => Assert.True(r.IsActive));
    }

    // ── Dose point table ────────────────────────────────────────────────────

    private static (PumpCalibrationViewModel Vm, RecordingDeviceService Device, PumpCalibrationProfileStore Store) WithCurveRuns(TestClock? clock = null)
    {
        var store = new PumpCalibrationProfileStore(FreshDir());
        store.SaveProfile(new PumpCalibrationProfile
        {
            Name = "Mangueira 5x3mm", HasFittedCurve = false,
            CalibrationPoints =
            [
                new PumpCalibrationPoint { SpeedUnits = 100, Seconds = 60, VolumeMl = 4.56 },
                new PumpCalibrationPoint { SpeedUnits = 100, Seconds = 60, VolumeMl = 4.60 },
                new PumpCalibrationPoint { SpeedUnits = 500, Seconds = 60, VolumeMl = 15.77 },
            ],
        });
        var device = new RecordingDeviceService();
        var vm = new PumpCalibrationViewModel(device, new MemorySettingsService(), timeProvider: clock, profileStore: store);
        device.PushTelemetry(Online(drive: (int)PumpDriveState.Stopped, doseActive: false));
        return (vm, device, store);
    }

    [Fact]
    public void Curve_runs_are_imported_as_a_copy_and_form_the_point()
    {
        var (vm, _, _) = WithCurveRuns();
        using var _ = vm;
        var dose = vm.DosePoint;
        Assert.Equal([100.0, 500.0], dose.ImportSpeeds);

        dose.SelectedImportSpeed = 100;
        dose.ImportFromCurveCommand.Execute(null);
        Assert.Equal(2, dose.Table.Count);
        Assert.All(dose.Table, r => Assert.Equal(PumpDosePointMath.SourceCurve, r.Source));
        Assert.Equal(4.58, dose.MeanRateMlMin, 9);
        Assert.Equal("100", dose.SpeedText);
        Assert.True(dose.CanSave);

        // Not linked: editing the curve afterwards leaves the table alone.
        vm.Runs[0].VolumeText = "9.99";
        Assert.Equal(4.58, dose.MeanRateMlMin, 9);
    }

    [Fact]
    public void Typed_dead_time_wins_and_is_sent_with_the_point()
    {
        var (vm, device, _) = WithCurveRuns();
        using var _ = vm;
        var dose = vm.DosePoint;
        dose.SelectedImportSpeed = 500;
        dose.ImportFromCurveCommand.Execute(null);

        dose.Table[0].DeadMsText = "45";
        Assert.Equal(45.0, dose.TypedDeadMs);
        Assert.Contains("t₀ 45 ms (digitado)", dose.RepetitionSummaryText);

        dose.SendToNodeCommand.Execute(null);
        Assert.Contains("\"pump_dose_dead_ms\":45", device.Sent[^1]);

        dose.Table[0].DeadMsText = "9000"; // out of range: ignored, flagged on the cell
        Assert.Null(dose.TypedDeadMs);
        Assert.NotNull(dose.Table[0].DeadMsError);
    }

    [Fact]
    public void Active_rows_share_one_speed_and_rows_can_be_removed()
    {
        var (vm, _, _) = WithCurveRuns();
        using var _ = vm;
        var dose = vm.DosePoint;
        dose.SelectedImportSpeed = 100;
        dose.ImportFromCurveCommand.Execute(null);
        dose.SelectedImportSpeed = 500;
        dose.ImportFromCurveCommand.Execute(null); // the S 100 rows are deactivated
        Assert.Equal(3, dose.Table.Count);
        Assert.Single(dose.Repetitions);
        Assert.Equal(15.77, dose.MeanRateMlMin, 9);

        // Picking an S 100 row moves the point back and drops the S 500 row from it.
        dose.Table[0].IsActive = true;
        Assert.False(dose.Table[2].IsActive);
        Assert.Equal(4.56, dose.MeanRateMlMin, 9);

        dose.RemoveTableRowCommand.Execute(dose.Table[0]);
        Assert.Equal(2, dose.Table.Count);
        Assert.False(dose.CanSave); // no active row left
        Assert.Contains("Nenhuma linha ativa", dose.RepetitionSummaryText);
    }

    [Fact]
    public void A_run_at_another_speed_joins_the_table_inactive()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        var (vm, _, _) = WithCurveRuns(clock);
        using var _ = vm;
        var dose = vm.DosePoint;
        dose.SelectedImportSpeed = 100;
        dose.ImportFromCurveCommand.Execute(null);

        dose.SpeedText = "200";
        dose.DurationText = "10";
        dose.StartRunCommand.Execute(null);
        clock.ShiftUtc(TimeSpan.FromSeconds(11));
        dose.Tick();
        dose.VolumeText = "1.5";
        dose.RegisterRepetitionCommand.Execute(null);

        Assert.Equal(3, dose.Table.Count);
        Assert.False(dose.Table[^1].IsActive);
        Assert.Equal(4.58, dose.MeanRateMlMin, 9); // the point is unchanged
        Assert.Contains("inativo", dose.StatusText);
    }

    [Fact]
    public void Hand_typed_rows_and_t0_are_saved_with_the_profile()
    {
        var (vm, _, store) = WithCurveRuns();
        using var _ = vm;
        var dose = vm.DosePoint;
        dose.SpeedText = "120";
        dose.AddTableRowCommand.Execute(null);
        var row = dose.Table[^1];
        row.SecondsText = "120";
        row.VolumeText = "10.8";
        row.DeadMsText = "40";
        Assert.True(row.IsActive);
        Assert.Equal(5.4, dose.MeanRateMlMin, 9);

        dose.SaveToProfileCommand.Execute(null);
        var saved = store.LoadProfile("Mangueira 5x3mm")!;
        var run = Assert.Single(saved.DoseRuns);
        Assert.Equal(40.0, run.DeadTimeMs);
        Assert.Equal(PumpDosePointMath.SourceManual, run.Source);
        var point = Assert.Single(saved.DosePoints);
        Assert.Equal(40.0, point.DeadTimeMs);
        Assert.Equal(PumpDosePointMath.SourceManual, point.Source);
    }
}
