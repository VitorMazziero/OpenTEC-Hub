using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Calibration;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Simulator;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// Pump 3.13 / Hub 10.9 integration (plan 2026-10-11, Phase 3): dosing commands, the new
/// telemetry, the dosing arithmetic, and the integration fixes on the app side.
/// </summary>
public sealed class PumpIntegration313Tests
{
    private static readonly PumpDoseParameters Cultivo = new(0.35, 300.0, 120.0, 5.4, 50.0, 0.0);

    // ── Wire ────────────────────────────────────────────────────────────────

    [Fact]
    public void Dose_commands_use_the_frozen_wire_keys()
    {
        Assert.Equal(
            """{"pump_dose_ml":0.35,"pump_dose_period_s":300.0,"pump_dose_speed":120.0,"pump_dose_rate_mlmin":5.4,"pump_dose_total_ml":50.0,"pump_dose_dead_ms":0.0,"pump_dose":1}""",
            CommandBuilders.PumpDoseStart(Cultivo).ToJson());
        Assert.Equal("""{"pump_dose":0}""", CommandBuilders.PumpDoseStop().ToJson());
        Assert.DoesNotContain("pump_dose\":", CommandBuilders.PumpDoseConfigure(Cultivo).ToJson());
    }

    [Fact]
    public void Dose_builders_refuse_what_the_node_would_refuse()
    {
        var tooShort = Cultivo with { VolumeMl = 0.015 };
        Assert.Throws<ArgumentOutOfRangeException>(() => CommandBuilders.PumpDoseStart(tooShort));
    }

    [Theory]
    [InlineData(0.35, 300, 120, 5.4, 50, 0, null)]
    [InlineData(0.0, 300, 120, 5.4, 50, 0, "dose_ml")]
    [InlineData(0.35, 1, 120, 5.4, 50, 0, "dose_period")]
    [InlineData(0.35, 300, 0.5, 5.4, 50, 0, "dose_speed")]
    [InlineData(0.35, 300, 120, 0, 50, 0, "dose_rate")]
    [InlineData(0.35, 300, 120, 5.4, -1, 0, "dose_total")]
    [InlineData(0.35, 300, 120, 5.4, 50, 2500, "dose_dead")]
    [InlineData(0.0181, 16, 120, 5.4, 50, 0, null)]          // 201 ms of delivery
    [InlineData(0.015, 16, 120, 5.4, 50, 0, "dose_short")]   // 167 ms
    [InlineData(0.35, 4.0, 120, 5.4, 50, 0, "dose_fit")]     // 3.9 s + 0.5 s > 4 s
    [InlineData(0.35, 4.5, 120, 5.4, 50, 0, null)]
    public void Dose_rules_mirror_the_firmware(double ml, double per, double spd, double rate, double tot, double dead, string? code)
        => Assert.Equal(code, PumpDoseRules.Validate(new PumpDoseParameters(ml, per, spd, rate, tot, dead)));

    [Fact]
    public void Pump_313_telemetry_is_parsed_and_absent_on_older_pumps()
    {
        var parser = new TelemetryParser();
        parser.Parse("""{"PumpOnline":true,"PumpCommEnabled":true,"PumpVol":3.5,"PumpFlow":0.0,"PumpUptimeS":3600,"PumpBootCount":12,"PumpResetReason":7,"PumpRejCmdId":512345007,"PumpRejErr":"dose_fit","PumpDrive":5,"PumpLiquidSensor":false,"PumpLiquidOk":true,"PumpProfileMin":0.0,"PumpDoseActive":true,"PumpDosePulse":false,"PumpDoseMl":0.35,"PumpDosePeriodS":300.0,"PumpDoseSpeed":120.0,"PumpDoseRate":5.4,"PumpDoseTotal":50.0,"PumpDoseDeadMs":40,"PumpDoseDelivered":3.5,"PumpDosePulses":10,"PumpDoseNextS":133,"PumpDoseShortfall":0.0}""");
        var r = parser.Readings;
        Assert.Equal(3600, r.PumpUptimeS);
        Assert.Equal(12, r.PumpBootCount);
        Assert.Equal(7, r.PumpResetReason);
        Assert.Equal(512345007, r.PumpRejCmdId);
        Assert.Equal("dose_fit", r.PumpRejErr);
        Assert.Equal((int)PumpDriveState.DoseIdle, r.PumpDrive);
        Assert.True(r.PumpDoseActive);
        Assert.Equal(40.0, r.PumpDoseDeadMs);
        Assert.Equal(3.5, r.PumpDoseDelivered);
        Assert.Equal(10, r.PumpDosePulses);
        Assert.Equal(133, r.PumpDoseNextS);

        // A 3.12 pump (or Hub 10.8): nothing of the above, and it does not stick.
        parser.Parse("""{"PumpOnline":true,"PumpCommEnabled":true,"PumpVol":3.5,"PumpFlow":0.0}""");
        Assert.Null(parser.Readings.PumpDrive);
        Assert.Null(parser.Readings.PumpDoseActive);
        Assert.Null(parser.Readings.PumpBootCount);
        Assert.Null(parser.Readings.PumpRejErr);
    }

    [Fact]
    public void Firmware_catalog_knows_313()
        => Assert.True(NodeFirmwareCatalog.IsValidated(NodeFirmwareCatalog.Pump, "3.13"));

    // ── Dosing arithmetic ───────────────────────────────────────────────────

    private static readonly PumpDoseSource Point = new(120.0, 5.4);
    private const double FiftyIn12h = 50.0 / 720.0; // 0.0694 mL/min

    [Fact]
    public void Plan_reproduces_the_cultivation_case()
    {
        var plan = PumpDoseMath.Plan(FiftyIn12h, 50.0, Point, 302.0)!;
        Assert.Equal(300.0, plan.PeriodS);                  // rounded to the 5 s step
        Assert.Equal(0.3472, plan.VolumeMl, 3);
        Assert.Equal(3858.0, plan.DeliveryMs, 0);
        Assert.Equal(144.0, plan.TotalPulses);
        Assert.Equal(12.0, plan.PulsesPerHour, 6);
        Assert.Equal(PumpDoseZone.Good, plan.Zone);
        Assert.Null(PumpDoseRules.Validate(plan.Parameters));
    }

    [Fact]
    public void Range_spans_firmware_minimum_to_operator_maximum()
    {
        var range = PumpDoseMath.Range(FiftyIn12h, Point);
        Assert.True(range.Feasible);
        Assert.Equal(FiftyIn12h / 5.4, range.Duty, 9);
        // 200 ms of delivery at d = 1.29 % needs T ≥ 15.6 s → 16 s.
        Assert.Equal(16.0, range.MinPeriodS);
        Assert.Equal(1800.0, range.MaxPeriodS);
        // Without a pulse check the recommended minimum is 2 s of delivery → 155.5 s → 160 s.
        Assert.Equal(160.0, range.RecommendedMinPeriodS);

        foreach (var period in new[] { range.MinPeriodS, range.RecommendedMinPeriodS, range.MaxPeriodS })
        {
            var plan = PumpDoseMath.Plan(FiftyIn12h, 50.0, Point, period)!;
            Assert.Null(PumpDoseRules.Validate(plan.Parameters));
        }

        Assert.Equal(PumpDoseZone.LowPrecision, PumpDoseMath.Plan(FiftyIn12h, 50.0, Point, 16.0)!.Zone);
    }

    [Fact]
    public void Range_is_infeasible_above_half_duty()
    {
        var range = PumpDoseMath.Range(3.0, Point);
        Assert.False(range.Feasible);
        Assert.Contains("metade", range.Reason);
        Assert.Null(PumpDoseMath.Plan(3.0, 0.0, Point, 60.0));
    }

    [Fact]
    public void Slider_and_period_round_trip_on_a_log_scale()
    {
        var range = PumpDoseMath.Range(FiftyIn12h, Point);
        Assert.Equal(range.MinPeriodS, PumpDoseMath.SliderToPeriod(0.0, range));
        Assert.Equal(range.MaxPeriodS, PumpDoseMath.SliderToPeriod(1.0, range));
        foreach (var p in new[] { 0.1, 0.33, 0.5, 0.77 })
        {
            var period = PumpDoseMath.SliderToPeriod(p, range);
            Assert.Equal(period, PumpDoseMath.SliderToPeriod(PumpDoseMath.PeriodToSlider(period, range), range));
        }
    }

    [Theory]
    [InlineData(38.6, 39.0)]
    [InlineData(302.0, 300.0)]
    [InlineData(1213.0, 1215.0)]
    public void Periods_round_to_readable_steps(double raw, double expected)
        => Assert.Equal(expected, PumpDoseMath.RoundPeriod(raw));

    [Fact]
    public void Dead_time_is_compensated_and_reported()
    {
        var plan = PumpDoseMath.Plan(FiftyIn12h, 50.0, Point with { DeadMs = 40.0, RecommendedMinOnMs = 800.0 }, 78.0)!;
        Assert.Equal(40.0, plan.Parameters.DeadMs);
        Assert.Equal(plan.DeliveryMs + 40.0, plan.OnMs, 6);
        Assert.Equal(40.0 / plan.OnMs, plan.StartErrorFraction!.Value, 6);
        Assert.Equal(PumpDoseZone.Good, plan.Zone); // 1 s of delivery ≥ the measured 0.8 s
    }

    [Fact]
    public void Preview_has_a_pulse_train_and_a_staircase_up_to_the_total()
    {
        var plan = PumpDoseMath.Plan(FiftyIn12h, 50.0, Point, 300.0)!;
        var preview = PumpDoseMath.Preview(plan);
        Assert.Contains(5.4, preview.TrainFlowMlMin);
        // 144 pulses of 0.3472 mL: the 0.003 mL remainder is below the minimum pulse and is not dosed.
        Assert.Equal(plan.TotalPulses!.Value * plan.VolumeMl, preview.VolumeMl[^1], 6);
        Assert.True(preview.VolumeMl[^1] <= 50.0);
        Assert.True(preview.VolumeMinutes.Zip(preview.VolumeMinutes.Skip(1)).All(p => p.Second >= p.First));
    }

    // ── Card integration fixes ──────────────────────────────────────────────

    private static SensorSnapshot Online(double volume, int? drive = null, long? boot = null,
        long? rejId = null, string? rejErr = null, bool? liquidOk = null)
        => new()
        {
            HasPumpTelemetry = true, PumpOnline = true, PumpCommEnabled = true,
            PumpVolume = volume, PumpFlow = 0.0, PumpDrive = drive, PumpBootCount = boot,
            PumpRejCmdId = rejId, PumpRejErr = rejErr, PumpLiquidOk = liquidOk,
        };

    [Fact]
    public void Run_state_follows_the_node_drive_field()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, new MemorySettingsService());
        device.PushTelemetry(Online(3.5, drive: (int)PumpDriveState.DoseIdle));
        Assert.Equal("Dosando (entre pulsos)", vm.PumpRunStateText);

        // Without the field (pump 3.12) the old reading stands.
        device.PushTelemetry(Online(3.5));
        Assert.Equal("Parada", vm.PumpRunStateText);
    }

    [Fact]
    public void A_refusal_is_shown_once_with_its_reason()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, new MemorySettingsService());
        device.PushTelemetry(Online(0.0, rejId: 0, rejErr: ""));
        Assert.DoesNotContain("recusou", vm.StatusText);

        device.PushTelemetry(Online(0.0, rejId: 512345007, rejErr: "dose_fit"));
        Assert.Contains("A bomba recusou o último comando: o pulso não cabe no período", vm.StatusText);
    }

    [Fact]
    public void Proportional_gas_holds_across_a_pump_reboot()
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, new MemorySettingsService())
        {
            IsEnabled = true, InitialVolumeText = "1.0", VvmText = "0.5", GasProportionalEnabled = true,
        };
        device.PushTelemetry(Online(1000.0, boot: 4));
        Assert.Contains("""{"flowSetpoint":1.0""", device.Sent[^1]);
        device.Sent.Clear();

        // An older pump forgot its volume on reboot: hold the gas, do not fall to 0.5 L/min.
        device.PushTelemetry(Online(0.0, boot: 5));
        Assert.Empty(device.Sent);
        Assert.Contains("reiniciou", vm.StatusText);

        // Once the volume is back where it was, the coupling resumes.
        device.PushTelemetry(Online(1200.0, boot: 5));
        Assert.Contains("""{"flowSetpoint":1.1""", device.Sent[^1]);
    }

    [Fact]
    public void Dry_liquid_sensor_is_reported_after_five_seconds()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        using var vm = new PumpControlViewModel(device, new MemorySettingsService(), timeProvider: clock);
        device.PushTelemetry(Online(1.0, drive: (int)PumpDriveState.DosePulse, liquidOk: false));
        Assert.DoesNotContain("Sensor de líquido", vm.StatusText);
        clock.ShiftUtc(TimeSpan.FromSeconds(6));
        device.PushTelemetry(Online(1.0, drive: (int)PumpDriveState.DosePulse, liquidOk: false));
        Assert.Contains("Sensor de líquido seco", vm.StatusText);
    }

    [Fact]
    public void Manual_fill_always_carries_a_deadline_and_renews_it()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        using var vm = new PumpCalibrationViewModel(device, new MemorySettingsService(), timeProvider: clock,
            profileStore: new PumpCalibrationProfileStore(Path.Combine(Path.GetTempPath(), "OpenTECHub.Tests", "PumpProfiles", Guid.NewGuid().ToString("N"))));
        device.PushTelemetry(Online(0.0));
        vm.ManualSpeedText = "250";
        vm.StartManualCommand.Execute(null);
        Assert.Equal("""{"pump_speed":250,"pump_speed_ms":60000}""", device.Sent[^1]);

        device.Sent.Clear();
        clock.ShiftUtc(TimeSpan.FromSeconds(10));
        device.PushTelemetry(Online(0.0));
        Assert.Empty(device.Sent);

        clock.ShiftUtc(TimeSpan.FromSeconds(11));
        device.PushTelemetry(Online(0.0));
        Assert.Equal("""{"pump_speed":250,"pump_speed_ms":60000}""", Assert.Single(device.Sent));

        vm.StopManualCommand.Execute(null);
        device.Sent.Clear();
        clock.ShiftUtc(TimeSpan.FromSeconds(30));
        device.PushTelemetry(Online(0.0));
        Assert.Empty(device.Sent);
    }

    // ── Simulator ───────────────────────────────────────────────────────────

    [Fact]
    public void Simulator_doses_refuses_and_reports_reboots()
    {
        var model = new DeviceModel();
        model.PumpEnabled = true;

        WireCodec.ApplyCommand(model, CommandBuilders.PumpDoseStart(Cultivo with { PeriodS = 4.5 }).ToJson(), out _);
        Assert.True(model.PumpDoseActive);
        Assert.Equal("", model.PumpRejErr);

        WireCodec.ApplyCommand(model, """{"pump_dose_period_s":3}""", out _);
        Assert.Equal("dose_fit", model.PumpRejErr);
        Assert.NotEqual(0, model.PumpRejCmdId);
        Assert.Equal(4.5, model.PumpDose.PeriodS); // nothing applied

        WireCodec.ApplyCommand(model, CommandBuilders.PumpStopProfile().ToJson(), out _);
        Assert.False(model.PumpDoseActive);       // mode:0 cancels dosing (safe stop)
        Assert.Equal("", model.PumpRejErr);

        var boot = model.PumpBootCount;
        model.RestartPump(7);
        Assert.Equal(boot + 1, model.PumpBootCount);
        Assert.Equal(7, model.PumpResetReason);
    }
}
