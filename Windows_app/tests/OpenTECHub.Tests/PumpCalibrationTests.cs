using System.Globalization;
using System.IO;
using System.Text.Json;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class PumpCalibrationTests
{
    private sealed class TestClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    [Fact]
    public void Pump_calibration_previews_match_linear_equation()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PumpCalibrationViewModel(device, settings);

        // Q = slope * internal speed S + intercept (S is 0..1000, not raw PWM duty).
        vm.SlopeText = "1.0";
        vm.InterceptText = "0.0";
        Assert.True(vm.IsValid);
        Assert.Equal(250.0.ToString("F2", CultureInfo.CurrentCulture) + " mL/min", vm.Preview250Text);
        Assert.Equal(500.0.ToString("F2", CultureInfo.CurrentCulture) + " mL/min", vm.Preview500Text);
        Assert.Equal(1000.0.ToString("F2", CultureInfo.CurrentCulture) + " mL/min", vm.Preview1000Text);

        // Slope = 0.5, Intercept = 10.0
        vm.SlopeText = "0.5";
        vm.InterceptText = "10.0";
        Assert.True(vm.IsValid);
        Assert.Equal(135.0.ToString("F2", CultureInfo.CurrentCulture) + " mL/min", vm.Preview250Text);
        Assert.Equal(260.0.ToString("F2", CultureInfo.CurrentCulture) + " mL/min", vm.Preview500Text);
        Assert.Equal(510.0.ToString("F2", CultureInfo.CurrentCulture) + " mL/min", vm.Preview1000Text);
    }

    [Fact]
    public void Pump_calibration_rejects_non_positive_slope_and_invalid_intercept()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PumpCalibrationViewModel(device, settings);

        vm.SlopeText = "0.0";
        vm.InterceptText = "0.0";
        Assert.False(vm.IsValid);
        Assert.False(vm.CanApply);
        Assert.Equal("—", vm.Preview250Text);
        Assert.NotNull(vm.ValidationError);

        vm.SlopeText = "-1.5";
        Assert.False(vm.IsValid);

        vm.SlopeText = "1.5";
        vm.InterceptText = "not_a_number";
        Assert.False(vm.IsValid);
        Assert.False(vm.CanApply);
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
            calibrationsDirectory: receiptDirectory);

        device.PushTelemetry(new SensorSnapshot { HasPumpTelemetry = true, PumpOnline = true });
        Assert.True(vm.IsPumpOnline);

        vm.SlopeText = "1.5";
        vm.InterceptText = "2.5";
        Assert.True(vm.CanApply);

        vm.ApplyCommand.Execute(null);

        Assert.Contains("""{"pumpSlope":1.5,"pumpIntercept":2.5}""", device.Sent[^1]);
        Assert.True(vm.IsAwaitingCalibration);
        Assert.NotEqual(1.5, settings.Current.PumpControl.CalibrationSlope);
        Assert.False(Directory.Exists(receiptDirectory));

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpSlope = 1.5,
            PumpIntercept = 2.5,
            HubFirmwareVersion = "10.2.0-test",
            PumpNode = new ExternalNodeIdentity("192.168.4.12", "AA:BB:CC:DD:EE:FF", "3.9")
        });

        Assert.False(vm.IsAwaitingCalibration);
        Assert.Equal(1.5, settings.Current.PumpControl.CalibrationSlope);
        Assert.Equal(2.5, settings.Current.PumpControl.CalibrationIntercept);

        // Verify JSON receipt file
        var receiptPath = Path.Combine(receiptDirectory, "bomba-externa-2026-09-12_12-00-00.json");
        Assert.True(File.Exists(receiptPath));

        var json = File.ReadAllText(receiptPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("external_pump", root.GetProperty("node").GetString());
        Assert.Equal(1.5, root.GetProperty("requested").GetProperty("slope").GetDouble());
        Assert.Equal(2.5, root.GetProperty("requested").GetProperty("intercept").GetDouble());
        Assert.Equal(1.5, root.GetProperty("applied").GetProperty("slope").GetDouble());
        Assert.Equal(2.5, root.GetProperty("applied").GetProperty("intercept").GetDouble());
        Assert.Equal("10.2.0-test", root.GetProperty("hubFirmwareVersion").GetString());
        Assert.Equal("3.9", root.GetProperty("pumpNode").GetProperty("firmwareVersion").GetString());
        Assert.Equal(1.5 * 250 + 2.5, root.GetProperty("previews").GetProperty("speed_250").GetDouble());
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
            calibrationsDirectory: receiptDirectory);

        device.PushTelemetry(new SensorSnapshot { HasPumpTelemetry = true, PumpOnline = true });
        vm.SlopeText = "1.5";
        vm.InterceptText = "2.5";
        vm.ApplyCommand.Execute(null);

        clock.Advance(TimeSpan.FromSeconds(16));
        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpSlope = 1.4,
            PumpIntercept = 2.5
        });

        Assert.False(vm.IsAwaitingCalibration);
        Assert.False(Directory.Exists(receiptDirectory));
        Assert.Contains("nenhum recibo", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pump_calibration_telemetry_echoes_update_readouts()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PumpCalibrationViewModel(device, settings);

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpSlope = 1.75,
            PumpIntercept = 0.25,
            PumpFlow = 8.5,
            PumpVolume = 150.2,
        });

        Assert.Equal(1.75.ToString("F4", CultureInfo.CurrentCulture), vm.AppliedSlopeText);
        Assert.Equal(0.25.ToString("F4", CultureInfo.CurrentCulture), vm.AppliedInterceptText);
        Assert.Contains(8.5.ToString("F3", CultureInfo.CurrentCulture), vm.CurrentFlowText);
        Assert.Contains(150.2.ToString("F3", CultureInfo.CurrentCulture), vm.CurrentVolumeText);
    }

    [Fact]
    public void Pump_calibration_reset_volume_awaits_echo_and_times_out()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PumpCalibrationViewModel(device, settings, timeProvider: clock);

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
        using var vm = new PumpCalibrationViewModel(device, settings, calibrationsDirectory: receiptDirectory);

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpVolume = 10.0,
            PumpCommandPending = false
        });

        vm.SlopeText = "1.5";
        vm.InterceptText = "2.5";
        vm.ApplyCommand.Execute(null);

        Assert.False(vm.CanEditCalibration);
        Assert.False(vm.CanResetVolume);
        Assert.False(vm.RevertCommand.CanExecute(null));

        device.PushTelemetry(new SensorSnapshot
        {
            HasPumpTelemetry = true,
            PumpOnline = true,
            PumpSlope = 1.5,
            PumpIntercept = 2.5,
            PumpVolume = 10.0,
            PumpCommandPending = false
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
            PumpCommandPending = true
        });
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
    public void Pump_control_viewmodel_reset_volume_timeout_and_echo()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new PumpControlViewModel(device, settings, timeProvider: clock);

        Assert.False(vm.CanResetVolume); // not enabled

        vm.IsEnabled = true;
        device.PushTelemetry(new SensorSnapshot { HasPumpTelemetry = true, PumpOnline = true, PumpVolume = 120.0 });
        Assert.True(vm.CanResetVolume);

        vm.ResetVolumeCommand.Execute(null);
        Assert.Contains("""{"pump_command":"reset_volume"}""", device.Sent[^1]);
        Assert.Contains("Aguardando confirmação", vm.StatusText);

        clock.Advance(TimeSpan.FromSeconds(6));
        device.PushTelemetry(new SensorSnapshot { HasPumpTelemetry = true, PumpOnline = true, PumpVolume = 120.0 });
        Assert.Contains("Aviso: nó da bomba não confirmou zeramento", vm.StatusText);

        vm.ResetVolumeCommand.Execute(null);
        device.PushTelemetry(new SensorSnapshot { HasPumpTelemetry = true, PumpOnline = true, PumpVolume = 0.01 });
        Assert.Contains("zerado com sucesso", vm.StatusText);
    }

    [Fact]
    public void Biomass_acquisition_queue_dispatches_sequentially_on_pending_clear()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new BiomassControlViewModel(device, settings, timeProvider: clock);

        vm.IsEnabled = true;
        device.PushTelemetry(new SensorSnapshot { HasBiomassTelemetry = true, BiomassOnline = true, BiomassAbsorbance = 0.5 });
        Assert.True(vm.CanApplyAcquisition);

        vm.AcquisitionIntegrationTimeText = "200";
        vm.AcquisitionPwmText = "60.0";
        vm.AcquisitionGainGearText = "3";
        vm.AcquisitionEmaFactorText = "0.35";
        vm.AcquisitionProbePeriodMsText = "2000";

        vm.ApplyAcquisitionCommand.Execute(null);

        // Staged values are persisted only after the node clears pending for every command.
        Assert.NotEqual(200, settings.Current.BiomassControl.IntegrationTime);

        // Gear is first because set_it/set_pwm modify the currently selected slots.
        Assert.Contains("""{"biomassGear":3}""", device.Sent[^1]);
        Assert.Contains("(1/5)", vm.StatusText);

        // Telemetry arrives with BiomassCommandPending = true -> node still processing, does NOT send next
        device.Sent.Clear();
        device.PushTelemetry(new SensorSnapshot
        {
            HasBiomassTelemetry = true,
            BiomassOnline = true,
            BiomassAbsorbance = 0.5,
            BiomassCommandPending = true
        });
        Assert.Empty(device.Sent);

        // Telemetry arrives with BiomassCommandPending = false -> dispatches IT code 3 (200 ms).
        device.PushTelemetry(new SensorSnapshot
        {
            HasBiomassTelemetry = true,
            BiomassOnline = true,
            BiomassAbsorbance = 0.5,
            BiomassCommandPending = false
        });
        Assert.Contains("""{"biomassIt":3}""", Assert.Single(device.Sent));
        Assert.Contains("(2/5)", vm.StatusText);

        // Send 3rd (PWM)
        device.Sent.Clear();
        device.PushTelemetry(new SensorSnapshot
        {
            HasBiomassTelemetry = true,
            BiomassOnline = true,
            BiomassAbsorbance = 0.5,
            BiomassCommandPending = false
        });
        Assert.Contains("""{"biomassPwm":60.0}""", Assert.Single(device.Sent));
        Assert.Contains("(3/5)", vm.StatusText);

        // Send 4th (ema)
        device.Sent.Clear();
        device.PushTelemetry(new SensorSnapshot
        {
            HasBiomassTelemetry = true,
            BiomassOnline = true,
            BiomassAbsorbance = 0.5,
            BiomassCommandPending = false
        });
        Assert.Contains("""{"biomassEma":0.35}""", Assert.Single(device.Sent));
        Assert.Contains("(4/5)", vm.StatusText);

        // Send 5th (probe period)
        device.Sent.Clear();
        device.PushTelemetry(new SensorSnapshot
        {
            HasBiomassTelemetry = true,
            BiomassOnline = true,
            BiomassAbsorbance = 0.5,
            BiomassCommandPending = false
        });
        Assert.Contains("""{"biomassProbePeriodMs":2000}""", Assert.Single(device.Sent));
        Assert.Contains("(5/5)", vm.StatusText);

        // Final pending = false confirms completion
        device.Sent.Clear();
        device.PushTelemetry(new SensorSnapshot
        {
            HasBiomassTelemetry = true,
            BiomassOnline = true,
            BiomassAbsorbance = 0.5,
            BiomassCommandPending = false
        });
        Assert.Empty(device.Sent);
        Assert.Contains("Todos os parâmetros de aquisição foram confirmados", vm.StatusText);
        Assert.False(vm.CanCancelAcquisition);
        Assert.Equal(200, settings.Current.BiomassControl.IntegrationTime);
        Assert.Equal(60.0, settings.Current.BiomassControl.PwmPercent);
        Assert.Equal(3, settings.Current.BiomassControl.GainGear);
        Assert.Equal(0.35, settings.Current.BiomassControl.EmaFactor);
        Assert.Equal(2000, settings.Current.BiomassControl.ProbePeriodMs);
    }

    [Fact]
    public void Biomass_acquisition_queue_can_be_cancelled()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new BiomassControlViewModel(device, settings, timeProvider: clock);

        vm.IsEnabled = true;
        device.PushTelemetry(new SensorSnapshot { HasBiomassTelemetry = true, BiomassOnline = true, BiomassAbsorbance = 0.5 });

        vm.ApplyAcquisitionCommand.Execute(null);
        Assert.True(vm.CanCancelAcquisition);

        vm.CancelAcquisitionCommand.Execute(null);
        Assert.False(vm.CanCancelAcquisition);
        Assert.Contains("Fila de aquisição cancelada", vm.StatusText);

        // Further telemetry does not send any more queued commands
        device.Sent.Clear();
        device.PushTelemetry(new SensorSnapshot
        {
            HasBiomassTelemetry = true,
            BiomassOnline = true,
            BiomassAbsorbance = 0.5,
            BiomassCommandPending = false
        });
        Assert.Empty(device.Sent);
    }

    [Fact]
    public void Biomass_acquisition_rejects_unsupported_it_and_accepts_combined_gear_range()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService();
        using var vm = new BiomassControlViewModel(device, settings);

        vm.IsEnabled = true;
        device.PushTelemetry(new SensorSnapshot
        {
            HasBiomassTelemetry = true,
            BiomassOnline = true,
            BiomassAbsorbance = 0.5
        });

        vm.AcquisitionIntegrationTimeText = "150";
        Assert.False(vm.IsAcquisitionValid);
        Assert.Contains("25, 50, 100, 200, 400 ou 800", vm.AcquisitionValidationError);

        vm.AcquisitionIntegrationTimeText = "800";
        vm.AcquisitionGainGearText = "31";
        Assert.True(vm.IsAcquisitionValid);

        vm.AcquisitionGainGearText = "32";
        Assert.False(vm.IsAcquisitionValid);
    }
}
