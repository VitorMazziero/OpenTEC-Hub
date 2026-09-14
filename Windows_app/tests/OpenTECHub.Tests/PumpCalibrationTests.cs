using System.Globalization;
using System.IO;
using System.Text.Json;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Calibration;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Dialogs;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PumpCalibrationTests
{
    private static PumpCalibrationProfileStore FreshStore() => new(
        Path.Combine(Path.GetTempPath(), "OpenTECHub.Tests", "PumpProfiles", Guid.NewGuid().ToString("N")));

    private sealed class TestClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class TestDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public bool PromptResult { get; set; } = true;
        public string PromptText { get; set; } = "Novo Perfil";
        public List<string> Confirmations { get; } = [];

        public bool ConfirmDestructive(string title, string consequence, string exactCommand) => ConfirmResult;

        public bool Confirm(string title, string message, string confirmText = "Confirmar", string cancelText = "Cancelar", bool isDanger = false)
        {
            Confirmations.Add($"{title}: {message}");
            return ConfirmResult;
        }

        public bool PromptInput(string title, string message, out string response, string initialValue = "")
        {
            response = PromptText;
            return PromptResult;
        }

        public RecipeStartOption PromptRecipeStart(string recipeName) => RecipeStartOption.Cancel;
    }

    [Fact]
    public void Pump_calibration_previews_match_quartic_quadratic_equations()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PumpCalibrationViewModel(device, settings, profileStore: FreshStore());

        // Low line and high quadratic meet at St=500 with Q=16 and derivative 0.03.
        var low = new PolynomialCalibration(K: 0.0, F: 0.03, C: 1.0);
        var high = new PolynomialCalibration(K: 0.00002, F: 0.01, C: 6.0);
        vm.SetCurve(new PumpDualRangeCurve(low, high, 500.0));

        Assert.True(vm.IsValid);
        Assert.Equal(8.5.ToString("F2", CultureInfo.CurrentCulture) + " mL/min", vm.Preview250Text);
        // S = 500: Q = 16.0 mL/min
        Assert.Equal(16.0.ToString("F2", CultureInfo.CurrentCulture) + " mL/min", vm.Preview500Text);
        Assert.Equal(36.0.ToString("F2", CultureInfo.CurrentCulture) + " mL/min", vm.Preview1000Text);
    }

    [Fact]
    public void Pump_calibration_rejects_non_positive_slope_and_invalid_transition()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PumpCalibrationViewModel(device, settings, profileStore: FreshStore());

        var previous = Assert.IsType<PumpDualRangeCurve>(vm.Curve);
        vm.TransitionSpeedInputText = "0.0";
        Assert.False(vm.IsValid);
        Assert.False(vm.CanApply);
        Assert.Equal(previous.FlowFromSpeed(250).ToString("F2", CultureInfo.CurrentCulture) + " mL/min", vm.Preview250Text);
        Assert.NotNull(vm.ValidationError);

        vm.TransitionSpeedInputText = "-5.0";
        Assert.False(vm.IsValid);

        vm.TransitionSpeedInputText = "500.0";
        var decreasing = new PolynomialCalibration(0, -0.02, 16.0);
        vm.SetCurve(new PumpDualRangeCurve(decreasing, decreasing, 500.0));
        Assert.False(vm.IsValid);
        Assert.False(vm.CanApply);

        vm.SetCurve(new PumpDualRangeCurve(decreasing, decreasing, 500.0));
        Assert.False(vm.IsValid);
    }

    [Fact]
    public void Pump_calibration_applies_updates_settings_and_writes_json_receipt()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var receiptDirectory = Path.Combine(Path.GetTempPath(), "OpenTECHub.Tests", Guid.NewGuid().ToString("N"));
        using var vm = new PumpCalibrationViewModel(
            device,
            settings,
            timeProvider: clock,
            calibrationsDirectory: receiptDirectory,
            profileStore: FreshStore());

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            HubFirmwareVersion = "10.4.0-test",
            PumpNode = new ExternalNodeIdentity("192.168.4.12", "AA:BB:CC:DD:EE:FF", "3.12")
        });
        Assert.True(vm.IsPumpOnline);
        Assert.True(vm.IsFirmwareCompatible);

        vm.SetCurve(new PumpDualRangeCurve(0.02, 0.03, 500.0, 16.0));
        Assert.True(vm.CanApply);

        vm.ApplyCommand.Execute(null);

        Assert.Equal("{\"pumpA1\":0.0,\"pumpB1\":0.0,\"pumpK1\":0.0,\"pumpF1\":0.02,\"pumpC1\":6.0,\"pumpK2\":0.0,\"pumpF2\":0.02,\"pumpC2\":6.0,\"pumpTransitionSpeed\":500.0}", device.Sent[^1]);
        Assert.True(vm.IsAwaitingCalibration);
        Assert.NotEqual(0.02, settings.Current.PumpControl.CalibrationMLow);
        Assert.False(Directory.Exists(receiptDirectory));

        // Matching telemetry echo confirms calibration
        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpA1 = 0, PumpB1 = 0, PumpK1 = 0, PumpF1 = 0.02, PumpC1 = 6.0,
            PumpK2 = 0, PumpF2 = 0.02, PumpC2 = 6.0,
            PumpTransitionSpeed = 500.0,
            PumpCalCrc = 0x1234,
            PumpCommandPending = false,
            HubFirmwareVersion = "10.4.0-test",
            PumpNode = new ExternalNodeIdentity("192.168.4.12", "AA:BB:CC:DD:EE:FF", "3.12")
        });

        Assert.False(vm.IsAwaitingCalibration);
        Assert.Equal(0.02, settings.Current.PumpControl.CalibrationMLow);
        Assert.Equal(0.02, settings.Current.PumpControl.CalibrationMHigh);
        Assert.Equal(500.0, settings.Current.PumpControl.CalibrationSt);
        Assert.Equal(16.0, settings.Current.PumpControl.CalibrationQt);

        // Verify JSON receipt file
        var receiptPath = Path.Combine(receiptDirectory, "bomba-externa-2026-09-12_12-00-00.json");
        Assert.True(File.Exists(receiptPath));

        var json = File.ReadAllText(receiptPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("external_pump", root.GetProperty("node").GetString());
        Assert.Equal("quartic_quadratic_c0_c1", root.GetProperty("method").GetString());
        Assert.Equal(0.02, root.GetProperty("requested").GetProperty("f1").GetDouble());
        Assert.Equal(6.0, root.GetProperty("requested").GetProperty("c1").GetDouble());
        Assert.Equal(500.0, root.GetProperty("requested").GetProperty("transitionSpeed").GetDouble());
        Assert.Equal(0.02, root.GetProperty("applied").GetProperty("f2").GetDouble());
        Assert.Equal("10.4.0-test", root.GetProperty("hubFirmwareVersion").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("appVersion").GetString()));
        Assert.Equal("3.12", root.GetProperty("pumpNode").GetProperty("firmwareVersion").GetString());
    }

    [Fact]
    public void Pump_calibration_does_not_write_receipt_when_echo_never_matches()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var receiptDirectory = Path.Combine(Path.GetTempPath(), "OpenTECHub.Tests", Guid.NewGuid().ToString("N"));
        using var vm = new PumpCalibrationViewModel(
            device,
            settings,
            timeProvider: clock,
            calibrationsDirectory: receiptDirectory,
            profileStore: FreshStore());

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            HubFirmwareVersion = "10.4.0",
            PumpNode = new ExternalNodeIdentity("192.168.4.12", "AA:BB:CC:DD:EE:FF", "3.12")
        });
        vm.SetCurve(new PumpDualRangeCurve(0.02, 0.03, 500.0, 16.0));
        vm.ApplyCommand.Execute(null);

        clock.Advance(TimeSpan.FromSeconds(16));
        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpA1 = 0, PumpB1 = 0, PumpK1 = 0, PumpF1 = 0.015, PumpC1 = 8.5,
            PumpK2 = 0, PumpF2 = 0.015, PumpC2 = 8.5,
            PumpTransitionSpeed = 500.0,
        });

        Assert.False(vm.IsAwaitingCalibration);
        Assert.False(Directory.Exists(receiptDirectory));
        Assert.Contains("nenhum recibo", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pump_calibration_blocks_apply_on_legacy_firmware()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PumpCalibrationViewModel(device, settings, profileStore: FreshStore());

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpNode = new ExternalNodeIdentity("192.168.4.12", "AA:BB:CC:DD:EE:FF", "3.10")
        });

        Assert.False(vm.IsFirmwareCompatible);
        Assert.False(vm.CanApply);
        Assert.Contains("3.12", vm.FirmwareUnsupportedReason);
    }

    [Theory]
    [InlineData("10.2.9")]
    [InlineData("v10.2.0-dev")]
    public void Pump_calibration_blocks_apply_on_legacy_hub(string hubVersion)
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpCalibrationViewModel(device, new MemorySettingsService(), profileStore: FreshStore());
        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            HubFirmwareVersion = hubVersion,
            PumpNode = new ExternalNodeIdentity("192.168.4.12", "AA:BB:CC:DD:EE:FF", "v3.12.0-dev")
        });
        vm.SetCurve(new PumpDualRangeCurve(0.02, 0.03, 500, 16));

        Assert.True(vm.IsFirmwareCompatible);
        Assert.False(vm.IsHubCompatible);
        Assert.False(vm.CanApply);
        Assert.Contains("10.4", vm.HubUnsupportedReason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Pump_calibration_blocks_apply_while_operational_profile_is_active_or_waiting(bool active, bool waiting)
    {
        var device = new RecordingDeviceService();
        using var vm = new PumpCalibrationViewModel(device, new MemorySettingsService(), profileStore: FreshStore());
        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpActive = active,
            PumpWaiting = waiting,
            HubFirmwareVersion = "10.4.0",
            PumpNode = new ExternalNodeIdentity("192.168.4.12", "AA:BB:CC:DD:EE:FF", "3.12")
        });
        vm.SetCurve(new PumpDualRangeCurve(0.02, 0.03, 500, 16));

        Assert.False(vm.CanApply);
    }

    [Fact]
    public void Complete_parameter_echo_without_crc_or_finished_ack_does_not_confirm()
    {
        var (vm, device, settings, _) = OnlinePump();
        using var _ = vm;
        vm.SetCurve(new PumpDualRangeCurve(0.02, 0.03, 500, 16));
        vm.ApplyCommand.Execute(null);

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpA1 = 0, PumpB1 = 0, PumpK1 = 0, PumpF1 = 0.02, PumpC1 = 6,
            PumpK2 = 0, PumpF2 = 0.02, PumpC2 = 6,
            PumpTransitionSpeed = 500,
            PumpCommandPending = false,
        });
        Assert.True(vm.IsAwaitingCalibration);
        Assert.NotEqual(0.02, settings.Current.PumpControl.CalibrationMLow);

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpA1 = 0, PumpB1 = 0, PumpK1 = 0, PumpF1 = 0.02, PumpC1 = 6,
            PumpK2 = 0, PumpF2 = 0.02, PumpC2 = 6,
            PumpTransitionSpeed = 500,
            PumpCalCrc = 123,
            PumpCommandPending = true,
        });
        Assert.True(vm.IsAwaitingCalibration);
    }

    [Fact]
    public void Pump_calibration_telemetry_echoes_update_readouts()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PumpCalibrationViewModel(device, settings, profileStore: FreshStore());

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpA1 = 0, PumpB1 = 0, PumpK1 = 0, PumpF1 = 0.0215, PumpC1 = 5.18,
            PumpK2 = 0, PumpF2 = 0.0215, PumpC2 = 5.18,
            PumpTransitionSpeed = 480.0,
            PumpFlow = 8.5,
            PumpVolume = 150.2,
        });

        Assert.Contains("0,0215", vm.AppliedLowSlopeText.Replace('.', ','));
        Assert.Contains("0,0215", vm.AppliedHighSlopeText.Replace('.', ','));
        Assert.Equal(480.0.ToString("F1", CultureInfo.CurrentCulture), vm.AppliedStText);
        Assert.Equal(15.5.ToString("F2", CultureInfo.CurrentCulture), vm.AppliedQtText);
        Assert.Contains(8.5.ToString("F3", CultureInfo.CurrentCulture), vm.CurrentFlowText);
        Assert.Contains(150.2.ToString("F3", CultureInfo.CurrentCulture), vm.CurrentVolumeText);
    }

    [Fact]
    public void Pump_calibration_reset_volume_awaits_echo_and_times_out()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PumpCalibrationViewModel(device, settings, timeProvider: clock, profileStore: FreshStore());

        device.PushTelemetry(new SensorSnapshot { HasPumpTelemetry = true, PumpOnline = true, PumpVolume = 250.0 });
        Assert.True(vm.CanResetVolume);

        vm.ResetVolumeCommand.Execute(null);
        Assert.Contains("""{"pump_command":"reset_volume"}""", device.Sent[^1]);
        Assert.Contains("Aguardando confirmação", vm.StatusText);

        // Advance clock by 6 seconds without volume zeroing
        clock.Advance(TimeSpan.FromSeconds(6));
        device.PushTelemetry(new SensorSnapshot { HasPumpTelemetry = true, PumpOnline = true, PumpVolume = 250.0 });
        Assert.Contains("Aviso: nó da bomba não confirmou zeramento", vm.StatusText);

        // Execute again, and confirm zeroing
        vm.ResetVolumeCommand.Execute(null);
        device.PushTelemetry(new SensorSnapshot { HasPumpTelemetry = true, PumpOnline = true, PumpVolume = 0.0 });
        Assert.Contains("zerado com sucesso", vm.StatusText);
    }

    [Fact]
    public void Pump_calibration_serializes_apply_reset_and_node_pending_operations()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var receiptDirectory = Path.Combine(Path.GetTempPath(), "OpenTECHub.Tests", Guid.NewGuid().ToString("N"));
        using var vm = new PumpCalibrationViewModel(device, settings, calibrationsDirectory: receiptDirectory, profileStore: FreshStore());

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpVolume = 10.0,
            PumpCommandPending = false,
            HubFirmwareVersion = "10.4.0",
            PumpNode = new ExternalNodeIdentity("192.168.4.12", "AA:BB:CC:DD:EE:FF", "3.12")
        });

        vm.SetCurve(new PumpDualRangeCurve(0.02, 0.03, 500.0, 16.0));
        vm.ApplyCommand.Execute(null);

        Assert.False(vm.CanEditCalibration);
        Assert.False(vm.CanResetVolume);
        Assert.False(vm.RevertCommand.CanExecute(null));

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpA1 = 0, PumpB1 = 0, PumpK1 = 0, PumpF1 = 0.02, PumpC1 = 6,
            PumpK2 = 0, PumpF2 = 0.02, PumpC2 = 6,
            PumpTransitionSpeed = 500.0,
            PumpCalCrc = 0x789A,
            PumpVolume = 10.0,
            PumpCommandPending = false,
            PumpNode = new ExternalNodeIdentity("192.168.4.12", "AA:BB:CC:DD:EE:FF", "3.12")
        });
        Assert.True(vm.CanEditCalibration);
        Assert.True(vm.CanResetVolume);

        vm.ResetVolumeCommand.Execute(null);
        Assert.False(vm.CanApply);
        Assert.False(vm.CanResetVolume);

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpVolume = 0.0,
            PumpCommandPending = false
        });
        Assert.True(vm.CanApply);
        Assert.True(vm.CanResetVolume);
    }

    [Fact]
    public void Displayed_curve_is_available_to_the_calibration_chart()
    {
        using var vm = new PumpCalibrationViewModel(new RecordingDeviceService(), new MemorySettingsService(), profileStore: FreshStore());
        vm.SetCurve(new PumpDualRangeCurve(0.02, 0.03, 500.0, 16.0));

        Assert.True(vm.TryGetDisplayedCurve(out PumpDualRangeCurve curve));
        Assert.Equal(0.02, curve.LowSlope, 8);
        Assert.Equal(0.02, curve.HighSlope, 8);
        Assert.Equal(500.0, curve.TransitionSpeed, 8);
        Assert.Equal(16.0, curve.TransitionFlow, 8);
    }

    private static (PumpCalibrationViewModel Vm, RecordingDeviceService Device, MemorySettingsService Settings, TestClock Clock) OnlinePump()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        var vm = new PumpCalibrationViewModel(device, settings, timeProvider: clock,
            calibrationsDirectory: Path.Combine(Path.GetTempPath(), "OpenTECHub.Tests", Guid.NewGuid().ToString("N")),
            profileStore: FreshStore());
        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            HubFirmwareVersion = "10.4.0",
            PumpNode = new ExternalNodeIdentity("192.168.4.12", "AA:BB:CC:DD:EE:FF", "3.12")
        });
        return (vm, device, settings, clock);
    }

    [Fact]
    public void Volumetric_run_holds_speed_and_stops_itself_from_the_app_clock()
    {
        var (vm, device, _, clock) = OnlinePump();
        using var _ = vm;

        vm.RunSpeedText = "500";
        vm.RunDurationText = "60";
        Assert.True(vm.CanStartRun);

        vm.StartRunCommand.Execute(null);

        Assert.Contains("""{"pump_speed":500,"pump_speed_ms":63000}""", device.Sent[^1]);
        Assert.True(vm.IsRunning);
        Assert.False(vm.CanApply);
        Assert.False(vm.CanStartRun);

        clock.Advance(TimeSpan.FromSeconds(30));
        vm.Tick();
        Assert.True(vm.IsRunning);
        Assert.Equal(50.0, vm.RunProgressPercent, 1);
        Assert.Contains("30 s", vm.RunCountdownText);

        clock.Advance(TimeSpan.FromSeconds(30.4));
        vm.Tick();
        Assert.Contains("""{"pump_speed":0}""", device.Sent[^1]);
        Assert.False(vm.IsRunning);
        Assert.True(vm.HasPendingRun);
        Assert.Contains(60.4.ToString("F1", CultureInfo.CurrentCulture) + " s", vm.RunSummaryText);
    }

    [Fact]
    public void Telemetry_arrival_also_ends_an_overdue_run()
    {
        var (vm, device, _, clock) = OnlinePump();
        using var _ = vm;
        vm.RunDurationText = "10";
        vm.StartRunCommand.Execute(null);

        clock.Advance(TimeSpan.FromSeconds(11));
        device.PushTelemetry(new SensorSnapshot { HasPumpTelemetry = true, PumpOnline = true, PumpSpeed = 500 });

        Assert.False(vm.IsRunning);
        Assert.Contains("""{"pump_speed":0}""", device.Sent[^1]);
    }

    [Fact]
    public void Abort_stops_the_pump_and_creates_no_point()
    {
        var (vm, device, _, clock) = OnlinePump();
        using var _ = vm;
        vm.StartRunCommand.Execute(null);
        clock.Advance(TimeSpan.FromSeconds(5));

        vm.AbortRunCommand.Execute(null);

        Assert.Contains("""{"pump_speed":0}""", device.Sent[^1]);
        Assert.False(vm.IsRunning);
        Assert.False(vm.HasPendingRun);
        Assert.Empty(vm.Runs);
    }

    [Fact]
    public void Points_derive_flow_and_fit_continuous_dual_range_curve()
    {
        var (vm, _, settings, clock) = OnlinePump();
        using var _ = vm;

        // Set Qt = 16.0
        vm.TransitionSpeedInputText = "500.0";

        // Four points on known dual-range curve:
        // St = 500, Qt = 16.0, m_baixo = 0.02, m_alto = 0.03
        // S = 100: Q = 16.0 + 0.02 * (-400) = 8.0 mL/min -> in 60s: 8.0 mL
        // S = 300: Q = 16.0 + 0.02 * (-200) = 12.0 mL/min -> in 60s: 12.0 mL
        // S = 600: Q = 16.0 + 0.03 * (100) = 19.0 mL/min -> in 60s: 19.0 mL
        // S = 800: Q = 16.0 + 0.03 * (300) = 25.0 mL/min -> in 60s: 25.0 mL
        foreach (var (speed, volume) in new[] { (100.0, 8.0), (300.0, 12.0), (600.0, 19.0), (800.0, 25.0) })
        {
            vm.RunSpeedText = speed.ToString(CultureInfo.InvariantCulture);
            vm.RunDurationText = "60";
            vm.StartRunCommand.Execute(null);
            clock.Advance(TimeSpan.FromSeconds(60));
            vm.Tick();
            Assert.True(vm.HasPendingRun);

            vm.MeasuredVolumeText = volume.ToString(CultureInfo.InvariantCulture);
            Assert.True(vm.CanAddRunPoint);
            vm.AddRunPointCommand.Execute(null);
        }

        Assert.Equal(4, vm.Runs.Count);
        Assert.Equal(12.0, vm.Runs[1].FlowMlPerMin, 3);
        Assert.True(vm.HasFit);
        Assert.Null(vm.FitWarning);
        Assert.NotNull(vm.Fit?.Curve);

        var fit = vm.Fit!.Curve!.Value;
        Assert.Equal(0.03, fit.LowSlope, 4);
        Assert.Equal(0.03, fit.HighSlope, 4);
        Assert.Equal(500.0, fit.TransitionSpeed, 2);
        Assert.Equal(16.0, fit.TransitionFlow, 2);
        Assert.Equal(1.0, vm.Fit.RSquared, 4);

        // Check segment labels
        Assert.Equal("Baixo", vm.Runs[0].SegmentLabel);
        Assert.Equal("Baixo", vm.Runs[1].SegmentLabel);
        Assert.Equal("Alto", vm.Runs[2].SegmentLabel);
        Assert.Equal("Alto", vm.Runs[3].SegmentLabel);

        // Check UseFitCommand
        Assert.True(vm.CanUseFit);
        vm.UseFitCommand.Execute(null);

        Assert.Contains("S⁴", vm.LowSlopeText);
        Assert.Contains("S²", vm.HighSlopeText);
        Assert.Equal(500.0.ToString("F1", CultureInfo.InvariantCulture), vm.TransitionSpeedText);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void Single_speed_runs_cannot_fit_and_say_so()
    {
        var (vm, _, _, clock) = OnlinePump();
        using var _ = vm;
        for (var i = 0; i < 2; i++)
        {
            vm.RunSpeedText = "400";
            vm.StartRunCommand.Execute(null);
            clock.Advance(TimeSpan.FromSeconds(60));
            vm.Tick();
            vm.MeasuredVolumeText = "10";
            vm.AddRunPointCommand.Execute(null);
        }

        Assert.False(vm.HasFit);
        Assert.False(vm.CanUseFit);
        Assert.Contains("velocidades distintas", vm.FitWarning);
    }

    [Fact]
    public void Receipt_records_the_runs_and_the_fit_behind_the_applied_coefficients()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        var receiptDirectory = Path.Combine(Path.GetTempPath(), "OpenTECHub.Tests", Guid.NewGuid().ToString("N"));
        using var vm = new PumpCalibrationViewModel(device, new MemorySettingsService(), timeProvider: clock,
            calibrationsDirectory: receiptDirectory, profileStore: FreshStore());
        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            HubFirmwareVersion = "10.4.0",
            PumpNode = new ExternalNodeIdentity("192.168.4.12", "AA:BB:CC:DD:EE:FF", "3.12")
        });

        vm.TransitionSpeedInputText = "500.0";
        foreach (var (speed, volume) in new[] { (100.0, 8.0), (300.0, 12.0), (600.0, 19.0), (800.0, 25.0) })
        {
            vm.RunSpeedText = speed.ToString(CultureInfo.InvariantCulture);
            vm.RunDurationText = "30";
            vm.StartRunCommand.Execute(null);
            clock.Advance(TimeSpan.FromSeconds(30));
            vm.Tick();
            vm.MeasuredVolumeText = (volume / 2.0).ToString(CultureInfo.InvariantCulture); // same flow in 30s
            vm.AddRunPointCommand.Execute(null);
        }

        vm.UseFitCommand.Execute(null);
        vm.ApplyCommand.Execute(null);
        var fit = vm.Fit!.Curve!.Value;

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpA1 = fit.LowSpeed.A, PumpB1 = fit.LowSpeed.B, PumpK1 = fit.LowSpeed.K,
            PumpF1 = fit.LowSpeed.F, PumpC1 = fit.LowSpeed.C,
            PumpK2 = fit.HighSpeed.K, PumpF2 = fit.HighSpeed.F, PumpC2 = fit.HighSpeed.C,
            PumpTransitionSpeed = Math.Round(fit.TransitionSpeed, 1),
            PumpCalCrc = 0x4567,
            PumpCommandPending = false,
            HubFirmwareVersion = "10.4.0",
            PumpNode = new ExternalNodeIdentity("192.168.4.12", "AA:BB:CC:DD:EE:FF", "3.12")
        });

        var receipt = Directory.GetFiles(receiptDirectory, "bomba-externa-*.json").Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(receipt));
        var root = doc.RootElement;
        Assert.Equal("quartic_quadratic_c0_c1", root.GetProperty("method").GetString());
        Assert.Equal(4, root.GetProperty("runs").GetArrayLength());
        Assert.Equal(8.0, root.GetProperty("runs")[0].GetProperty("flowMlPerMin").GetDouble(), 6);
        Assert.Equal(4, root.GetProperty("fit").GetProperty("count").GetInt32());
    }

    [Fact]
    public void Link_loss_during_a_run_owes_a_stop_on_reconnect()
    {
        var (vm, device, _, clock) = OnlinePump();
        using var _ = vm;
        vm.StartRunCommand.Execute(null);
        clock.Advance(TimeSpan.FromSeconds(3));

        device.PushState(ConnectionState.Disconnected);
        Assert.False(vm.IsRunning);
        Assert.Contains("pode continuar girando", vm.StatusText);
        var sentBeforeReconnect = device.Sent.Count;

        device.PushState(ConnectionState.Connected);
        Assert.Equal(sentBeforeReconnect + 1, device.Sent.Count);
        Assert.Contains("""{"pump_speed":0}""", device.Sent[^1]);
    }

    [Fact]
    public void Manual_control_does_not_create_points()
    {
        var (vm, device, _, _) = OnlinePump();
        using var _ = vm;

        vm.ManualSpeedText = "500";
        Assert.True(vm.CanStartManual);

        vm.StartManualCommand.Execute(null);
        Assert.Contains("""{"pump_speed":500}""", device.Sent[^1]);
        Assert.True(vm.IsManualRunning);

        vm.StopManualCommand.Execute(null);
        Assert.Contains("""{"pump_speed":0}""", device.Sent[^1]);
        Assert.False(vm.IsManualRunning);
        Assert.Empty(vm.Runs);
    }

    [Fact]
    public void Hose_profiles_workflow_load_save_and_delete()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OpenTECHub.Tests", Guid.NewGuid().ToString("N"));
        var store = new PumpCalibrationProfileStore(tempDir);
        store.SaveProfile(PumpCalibrationProfile.FromCurve("Silicone 2mm", new PumpDualRangeCurve(0.02, 0.03, 500, 16)));
        store.SaveProfile(PumpCalibrationProfile.FromCurve("Marprene 3mm", new PumpDualRangeCurve(0.025, 0.035, 480, 18)));
        var dialogs = new TestDialogService();
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();

        using var vm = new PumpCalibrationViewModel(device, settings, profileStore: store, dialogs: dialogs);

        // Verify initial profiles loaded
        Assert.NotEmpty(vm.AvailableProfiles);
        var initialCount = vm.AvailableProfiles.Count;

        // Selection does not alter active editor without Load
        var secondProfile = vm.AvailableProfiles[1];
        vm.SelectedProfile = secondProfile;

        // Load profile
        vm.LoadSelectedProfileCommand.Execute(null);
        Assert.Equal(secondProfile.Id, vm.ActiveProfile?.Id);
        Assert.False(vm.IsCurrentProfileDirty);

        // Edit a field -> marks dirty
        vm.TransitionSpeedInputText = "500.0";
        Assert.True(vm.IsCurrentProfileDirty);

        // Save current profile
        vm.SaveCurrentProfileCommand.Execute(null);
        Assert.False(vm.IsCurrentProfileDirty);

        // Save as new profile
        dialogs.PromptText = "Mangueira Especial 1-4";
        vm.SaveCurrentProfileAsCommand.Execute(null);
        Assert.Equal(initialCount + 1, vm.AvailableProfiles.Count);
        Assert.Equal("Mangueira Especial 1-4", vm.ActiveProfile?.Name);

        // Delete active profile (with confirmation)
        dialogs.ConfirmResult = true;
        vm.DeleteSelectedProfileCommand.Execute(null);
        Assert.Equal(initialCount, vm.AvailableProfiles.Count);
    }

    [Fact]
    public void Dual_range_curve_is_continuous_and_uses_low_segment_at_transition()
    {
        var curve = new PumpDualRangeCurve(0.02, 0.03, 500.0, 16.0);

        Assert.True(curve.Validate(out var error), error);
        Assert.Equal(16.0, curve.FlowFromSpeed(500.0), 10);
        Assert.Equal(16.0, curve.FlowFromSpeed(500.0 - 1e-9), 8);
        Assert.Equal(16.0, curve.FlowFromSpeed(500.0 + 1e-9), 8);
        Assert.Equal(500.0, curve.SpeedFromFlow(16.0), 10);
    }

    [Fact]
    public void Dual_range_curve_round_trips_flow_and_speed_in_both_ranges()
    {
        var curve = new PumpDualRangeCurve(0.02, 0.03, 500.0, 16.0);

        foreach (var speed in new[] { 100.0, 500.0, 800.0 })
        {
            Assert.Equal(speed, curve.SpeedFromFlow(curve.FlowFromSpeed(speed)), 10);
        }
    }

    [Fact]
    public void Dual_range_fit_recovers_known_continuous_curve()
    {
        var points = new[]
        {
            Point(100.0, 8.0), Point(300.0, 12.0), Point(450.0, 15.0),
            Point(600.0, 18.0), Point(800.0, 22.0), Point(900.0, 24.0),
        };

        var result = PumpDualRangeMath.FitDualRange(points, transitionSpeed: 500.0);

        Assert.True(result.IsValid, result.Error);
        var curve = Assert.IsType<PumpDualRangeCurve>(result.Curve);
        Assert.Equal(500.0, curve.TransitionSpeed, 4);
        Assert.Equal(0.02, curve.LowSlope, 5);
        Assert.Equal(0.02, curve.HighSlope, 5);
        Assert.Equal(0.0, result.SSE, 8);
        Assert.Equal(3, result.LowPointCount);
        Assert.Equal(3, result.HighPointCount);
    }

    [Fact]
    public void Dual_range_fit_rejects_invalid_transition_or_insufficient_ranges()
    {
        var validPoints = new[]
        {
            Point(100.0, 8.0), Point(300.0, 12.0), Point(450.0, 15.0),
            Point(600.0, 18.0), Point(800.0, 22.0), Point(900.0, 24.0),
        };

        var invalidTransition = PumpDualRangeMath.FitDualRange(validPoints, 0.0);
        Assert.False(invalidTransition.IsValid);
        Assert.Contains("St", invalidTransition.Error);

        var insufficientRanges = PumpDualRangeMath.FitDualRange(
            new[] { Point(100.0, 8.0), Point(200.0, 10.0), Point(200.0, 10.0), Point(300.0, 12.0) },
            500.0);
        Assert.False(insufficientRanges.IsValid);
    }

    [Fact]
    public void Linear_migration_preserves_the_legacy_equation()
    {
        var curve = PumpDualRangeCurve.FromLinear(0.028, 1.7602);

        foreach (var speed in new[] { 0.0, 250.0, 500.0, 1000.0 })
        {
            Assert.Equal(0.028 * speed + 1.7602, curve.FlowFromSpeed(speed), 10);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => PumpDualRangeCurve.FromLinear(0.01, -100.0));
    }

    private static PumpCalibrationPoint Point(double speed, double flow) => new()
    {
        SpeedUnits = speed,
        Seconds = 60.0,
        VolumeMl = flow,
    };
}
