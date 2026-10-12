using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The pulse panel (plan 2026-10-11, Phase 6) and the live overlay and deviation (Phase 7).
/// </summary>
public sealed class PumpDosingTests
{
    private static readonly PumpDosePointSettings Point = new()
    {
        SpeedUnits = 120, RateMlMin = 5.4, ProfileName = "Mangueira 1", CapturedAtUtc = "2026-10-10T12:00:00Z",
    };

    private static MemorySettingsService WithPoint(Func<PumpDoseSettings, PumpDoseSettings>? tweak = null)
    {
        var settings = new MemorySettingsService();
        settings.Update(s => s with
        {
            PumpControl = s.PumpControl with { Dose = (tweak ?? (d => d))(s.PumpControl.Dose with { Point = Point }) },
        });
        return settings;
    }

    private static SensorSnapshot Node(
        bool? doseActive = false,
        int? drive = (int)PumpDriveState.Stopped,
        PumpDoseParameters? echo = null,
        double? delivered = null,
        long? pulses = null,
        long? next = null,
        bool? pulse = false,
        long? rejId = 0,
        string? rejErr = "",
        bool? liquidOk = true,
        double? profileMin = null,
        double cycle = 0.0,
        double target = 0.0) => new()
    {
        HasPumpTelemetry = true, PumpOnline = true, PumpCommEnabled = true, PumpVolume = 0.0, PumpFlow = 0.0,
        PumpDrive = drive, PumpDoseActive = doseActive, PumpDosePulse = pulse,
        PumpDoseMl = echo?.VolumeMl, PumpDosePeriodS = echo?.PeriodS, PumpDoseSpeed = echo?.Speed,
        PumpDoseRate = echo?.RateMlMin, PumpDoseTotal = echo?.TotalMl, PumpDoseDeadMs = echo?.DeadMs,
        PumpDoseDelivered = delivered, PumpDosePulses = pulses, PumpDoseNextS = next,
        PumpRejCmdId = rejId, PumpRejErr = rejErr, PumpLiquidOk = liquidOk,
        PumpProfileMin = profileMin, PumpCycleVolume = cycle, PumpTargetVolume = target,
    };

    // ── Slider and plan ─────────────────────────────────────────────────────

    [Fact]
    public void The_cultivation_example_plans_the_recommended_period_and_round_trips_the_slider()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, WithPoint());
        var d = vm.Dosing;

        // 50 mL in 12 h at S 120 → 5.40 mL/min: Q̄ = 0.0694 mL/min, d = 1.29 %.
        Assert.NotNull(d.Plan);
        Assert.Equal(50.0 / 720.0, d.MeanFlowMlMin, 9);
        // Without a pulse check the recommended pulse is 2 s: T = 2 s / d ≈ 155.5 s → 160 s (5 s steps).
        Assert.Equal(160.0, d.Plan!.PeriodS);
        Assert.Equal(PumpDoseZone.Good, d.Plan.Zone);
        Assert.InRange(d.AmberFraction, 0.0, 1.0);

        d.SliderPosition = 1.0;
        Assert.Equal(1800.0, d.Plan!.PeriodS); // 30 min, the default longest interval
        Assert.Contains("a cada 30:00", d.PulseText);

        d.SliderPosition = 0.0;
        Assert.Equal(PumpDoseZone.LowPrecision, d.Plan!.Zone);
        Assert.True(d.Plan.DeliveryMs >= PumpDoseRules.MinOnMs);
        Assert.Null(PumpDoseRules.Validate(d.Plan.Parameters));
    }

    [Fact]
    public void A_saved_period_out_of_the_new_range_is_clipped_and_announced()
    {
        var settings = WithPoint(d => d with { PeriodS = 1800.0 });
        using var vm = new PumpControlViewModel(new RecordingDeviceService(), settings);
        Assert.Equal(1800.0, vm.Dosing.Plan!.PeriodS);
        Assert.Null(vm.Dosing.ClampNotice);

        // The operator lowers the longest interval below the saved period.
        vm.Dosing.MaxIntervalMinutesText = "10";
        Assert.Equal(600.0, vm.Dosing.Plan!.PeriodS);
        Assert.Contains("ajustado para 10:00", vm.Dosing.ClampNotice);
    }

    [Fact]
    public void A_mean_flow_above_half_the_dose_point_is_infeasible()
    {
        using var vm = new PumpControlViewModel(new RecordingDeviceService(), WithPoint());
        vm.Dosing.IsTargetByFlow = true;
        vm.Dosing.MeanFlowText = "3.0";
        Assert.Null(vm.Dosing.Plan);
        Assert.Contains("metade", vm.Dosing.PlanError);
        Assert.False(vm.Dosing.CanStart);
    }

    [Fact]
    public void Without_a_dose_point_there_is_no_plan()
    {
        using var vm = new PumpControlViewModel(new RecordingDeviceService(), new MemorySettingsService());
        Assert.Null(vm.Dosing.Plan);
        Assert.Contains("Sem ponto de dosagem", vm.Dosing.DosePointText);
    }

    [Fact]
    public void The_exact_volume_entry_sets_the_period()
    {
        using var vm = new PumpControlViewModel(new RecordingDeviceService(), WithPoint());
        vm.Dosing.ExactVolumeText = "0.35";
        vm.Dosing.ApplyExactVolumeCommand.Execute(null);
        Assert.Equal(300.0, vm.Dosing.Plan!.PeriodS); // 60·0.35/0.0694 = 302.4 s → 300 s
    }

    // ── Pulses segment ──────────────────────────────────────────────────────

    [Fact]
    public void Pulses_need_pump_313_and_a_running_dose_opens_the_panel()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, WithPoint());
        Assert.True(vm.CanUsePulseMode); // nothing heard yet: planning is allowed

        device.PushTelemetry(Node(doseActive: null, drive: null)); // a 3.12 pump
        Assert.False(vm.CanUsePulseMode);
        Assert.Contains("3.13", vm.PulseModeHint);

        device.PushTelemetry(Node(doseActive: true, drive: (int)PumpDriveState.DoseIdle,
            echo: new PumpDoseParameters(0.35, 300, 120, 5.4, 50, 0), delivered: 0.7, pulses: 2, next: 100));
        Assert.True(vm.CanUsePulseMode);
        Assert.True(vm.IsPulseMode);
    }

    [Fact]
    public void Pulses_do_not_show_the_no_curve_warning()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, WithPoint());
        vm.NoCurveOnNode = true;
        Assert.True(vm.ShowNoCurveWarning);
        vm.IsPulseMode = true;
        Assert.False(vm.ShowNoCurveWarning);
    }

    // ── Start, echo, refusal, stop ──────────────────────────────────────────

    [Fact]
    public void Start_sends_one_frame_and_is_confirmed_only_by_the_echo()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        var settings = WithPoint();
        using var vm = new PumpControlViewModel(device, settings, timeProvider: clock);
        device.PushTelemetry(Node());
        var d = vm.Dosing;
        Assert.True(d.CanStart);

        d.StartCommand.Execute(null);
        var frame = Assert.Single(device.Sent);
        Assert.Equal(CommandBuilders.PumpDoseStart(d.Plan!.Parameters).ToJson(), frame);
        Assert.True(d.IsAwaitingEcho);
        Assert.False(d.CanStart);

        // Active but with other parameters: not yet confirmed.
        device.PushTelemetry(Node(doseActive: true, drive: 5, echo: d.Plan.Parameters with { PeriodS = 999 }, delivered: 0, pulses: 0, next: 1));
        Assert.True(d.IsAwaitingEcho);

        device.PushTelemetry(Node(doseActive: true, drive: 5, echo: d.Plan.Parameters, delivered: 0, pulses: 0, next: 1));
        Assert.False(d.IsAwaitingEcho);
        Assert.True(d.IsDosing);
        Assert.Contains("confirmada", d.StatusText);
        Assert.Equal("2026-10-12T08:00:00.0000000Z", settings.Current.PumpControl.Dose.RunStartedUtc);
        Assert.Equal(d.Plan.PeriodS, settings.Current.PumpControl.Dose.PeriodS);
        Assert.True(d.CanStop);
    }

    [Fact]
    public void A_refusal_ends_the_wait_with_the_translated_reason()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, WithPoint());
        device.PushTelemetry(Node());
        vm.Dosing.StartCommand.Execute(null);

        device.PushTelemetry(Node(rejId: 512345007, rejErr: "dose_busy"));
        Assert.False(vm.Dosing.IsAwaitingEcho);
        Assert.Contains("há um perfil contínuo em execução", vm.Dosing.StatusText);
    }

    [Fact]
    public void No_echo_in_15_s_times_out()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, WithPoint(), timeProvider: clock);
        device.PushTelemetry(Node());
        vm.Dosing.StartCommand.Execute(null);
        clock.ShiftUtc(TimeSpan.FromSeconds(16));
        device.PushTelemetry(Node());
        Assert.False(vm.Dosing.IsAwaitingEcho);
        Assert.Contains("não confirmou", vm.Dosing.StatusText);
    }

    [Fact]
    public void Another_owner_blocks_the_start()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        using var vm = new PumpControlViewModel(device, WithPoint(), arbiter: arbiter);
        device.PushTelemetry(Node());
        Assert.True(vm.Dosing.CanStart);

        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.ExternalPump], "receita");
        Assert.False(vm.Dosing.CanStart);
        device.Sent.Clear();
        vm.Dosing.StartCommand.Execute(null);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public void Stop_and_end_of_run_are_told_apart()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, WithPoint());
        var p = new PumpDoseParameters(0.35, 300, 120, 5.4, 50, 0);

        // Completed: the node goes inactive with everything delivered.
        device.PushTelemetry(Node(doseActive: true, drive: 5, echo: p, delivered: 49.7, pulses: 142, next: 10));
        device.PushTelemetry(Node(doseActive: false, echo: p, delivered: 50.0, pulses: 143));
        Assert.Contains("Concluída: 50,00 mL em 143 pulsos", vm.Dosing.StatusText.Replace('.', ','));

        // Interrupted by the dry sensor.
        device.PushTelemetry(Node(doseActive: true, drive: 5, echo: p, delivered: 21.3, pulses: 61, next: 10));
        device.PushTelemetry(Node(doseActive: false, echo: p, delivered: 21.3, pulses: 61, liquidOk: false));
        Assert.Contains("Interrompida", vm.Dosing.StatusText);
        Assert.Contains("sensor de líquido seco", vm.Dosing.StatusText);

        // Stopped by the operator.
        device.PushTelemetry(Node(doseActive: true, drive: 5, echo: p, delivered: 1.0, pulses: 3, next: 10));
        device.Sent.Clear();
        vm.Dosing.StopCommand.Execute(null);
        Assert.Equal("""{"pump_dose":0}""", Assert.Single(device.Sent));
        device.PushTelemetry(Node(doseActive: false, echo: p, delivered: 1.0, pulses: 3));
        Assert.Contains("Parada pelo operador", vm.Dosing.StatusText);
        Assert.False(vm.Dosing.IsAwaitingEcho);
    }

    // ── Phase 7: live overlay and deviation ─────────────────────────────────

    [Fact]
    public void Live_volume_is_placed_on_the_node_clock_and_a_lasting_shortfall_alerts()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, WithPoint());
        var p = new PumpDoseParameters(0.35, 300, 120, 5.4, 50, 0);

        // 10 pulses done, next in 100 s → elapsed = 10·300 − 100 = 2900 s.
        device.PushTelemetry(Node(doseActive: true, drive: 5, echo: p, delivered: 3.5, pulses: 10, next: 100));
        Assert.Equal(2900.0 / 60.0, vm.Dosing.LiveMinutes![^1], 6);
        Assert.Equal(3.5, vm.Dosing.LiveVolume![^1]);
        Assert.Contains("Desvio +0,000", vm.Dosing.DeviationText.Replace('.', ','));
        Assert.False(vm.Dosing.IsDeviationAlert);

        // The sensor held the motor: delivery stops growing while the plan goes on.
        for (var k = 11; k <= 14; k++)
        {
            device.PushTelemetry(Node(doseActive: true, drive: 5, echo: p, delivered: 3.5, pulses: k, next: 100));
        }

        Assert.True(vm.Dosing.IsDeviationAlert);
        Assert.Contains("dois períodos", vm.Dosing.DeviationText);
    }

    [Fact]
    public void Profile_overlay_uses_the_node_profile_clock()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, WithPoint());
        device.PushTelemetry(Node(doseActive: null, drive: (int)PumpDriveState.ProfileRunning, profileMin: 12.0, cycle: 11.8, target: 12.0));
        Assert.Equal(12.0, vm.LiveMinutes![^1]);
        Assert.Equal(11.8, vm.LiveVolume![^1]);
        Assert.Equal("", vm.LiveNote);

        // A pump without t_min: nothing is aligned, and the card says why.
        device.PushTelemetry(Node(doseActive: null, drive: (int)PumpDriveState.ProfileRunning, cycle: 12.0, target: 12.0));
        Assert.Contains("3.13", vm.LiveNote);
    }

    [Fact]
    public void Deviation_tracker_needs_the_excess_to_persist()
    {
        var t = new PumpDeviationTracker { HoldMinutes = 2.0 };
        t.Update(0.0, 10.0, 9.0);
        Assert.False(t.IsAlert);
        t.Update(1.0, 10.0, 9.0);
        Assert.False(t.IsAlert);
        t.Update(2.5, 10.0, 9.0);
        Assert.True(t.IsAlert);
        t.Update(3.0, 10.0, 9.9);
        Assert.False(t.IsAlert);
    }

    [Fact]
    public void Live_series_restarts_when_time_goes_back()
    {
        var series = new PumpLiveSeries();
        series.Add(1, 0, 1);
        series.Add(2, 0, 2);
        series.Add(0.5, 0, 0);
        Assert.Equal(1, series.Count);
    }

    // ── Regression: a profile send must not roll back what Calibrações saved ──

    [Fact]
    public void Sending_a_profile_keeps_the_dose_point_and_curve_saved_meanwhile()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PumpControlViewModel(device, settings) { IsEnabled = true };
        device.PushTelemetry(Node(doseActive: null, drive: null));

        // Calibrações saves a dose point and a curve after the card loaded.
        settings.Update(s => s with
        {
            PumpControl = s.PumpControl with { CalibrationSt = 420.0, Dose = s.PumpControl.Dose with { Point = Point } },
        });

        vm.LambdaConstText = "2";
        vm.ApplyProfileCommand.Execute(null);

        Assert.Equal(2.0, settings.Current.PumpControl.LambdaConst);
        Assert.Equal(420.0, settings.Current.PumpControl.CalibrationSt);
        Assert.Equal(Point, settings.Current.PumpControl.Dose.Point);
    }
}
