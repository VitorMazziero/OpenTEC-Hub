using OpenTECHub.Protocol;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>D-073: reactor probe correction, the bath start delta and the one-off bath setpoint.</summary>
public sealed class TemperatureCorrectionAndBathSetpointTests
{
    [Fact]
    public void Parser_reports_the_real_temperature_and_keeps_the_probe_reading()
    {
        var parser = new TelemetryParser(new ParserConfig { TemperatureOffsetC = 0.2 });
        parser.Parse("""{"Tempval":29.80,"BathOnline":true,"TempSetpoint":29.8,"BathCascadePvFiltered":29.75}""");
        var s = parser.Readings.Snapshot();

        Assert.Equal(30.0, s.Temperature, 6);
        Assert.Equal(29.8, s.TemperatureRaw, 6);
        Assert.Equal(30.0, s.TempSetpoint!.Value, 6);          // the Hub echoes the module scale
        Assert.Equal(29.95, s.BathCascadePvFiltered!.Value, 6);

        parser.Parse("""{"Tempval":29.80,"BathOnline":true,"TempSetpoint":0,"BathCascadePvFiltered":0}""");
        s = parser.Readings.Snapshot();
        Assert.Equal(0, s.TempSetpoint);                        // "off" is not a temperature
        Assert.Equal(0, s.BathCascadePvFiltered);
    }

    [Fact]
    public void An_out_of_range_correction_is_ignored_rather_than_applied()
    {
        var parser = new TelemetryParser(new ParserConfig { TemperatureOffsetC = 12 });
        parser.Parse("""{"Tempval":30.0}""");
        Assert.Equal(30.0, parser.Readings.Snapshot().Temperature, 6);
    }

    [Fact]
    public void The_reactor_setpoint_leaves_in_the_module_scale_and_zero_stays_off()
    {
        var command = OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 30.0).Set(CommandKeys.MotorSetpoint, 200);

        Assert.Equal("""{"tempSetpoint":29.8,"motorSetpoint":200}""", TemperatureCorrection.ToModule(command, 0.2).ToJson());
        Assert.Equal("""{"tempSetpoint":30.0,"motorSetpoint":200}""", command.ToJson()); // the original is not mutated
        Assert.Same(command, TemperatureCorrection.ToModule(command, 0));

        var off = OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 0.0);
        Assert.Equal("""{"tempSetpoint":0.0}""", TemperatureCorrection.ToModule(off, 0.2).ToJson());
    }

    [Fact]
    public void Calibration_page_computes_the_correction_from_a_reference_and_saves_it()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(new AppSettings());
        using var vm = new TemperatureCalibrationViewModel(device, settings);

        device.PushTelemetry(new SensorSnapshot { Temperature = 29.8, TemperatureRaw = 29.8 });
        vm.ReferenceText = "30,0";
        vm.ComputeFromReferenceCommand.Execute(null);
        Assert.True(vm.ApplyCommand.CanExecute(null));
        vm.ApplyCommand.Execute(null);

        Assert.Equal(0.2, settings.Current.Calibration.TemperatureOffsetC, 6);
        Assert.Equal(0.2, settings.Current.ToParserConfig().TemperatureOffsetC, 6);
        Assert.Empty(device.Sent); // applying only saves the setting

        vm.OffsetText = "6";
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("ph", 0)]
    [InlineData("oxygen", 1)]
    [InlineData("temperature", 2)]
    [InlineData("flow", 3)]
    [InlineData("biomass", 4)]
    [InlineData("pump", 5)]
    public void Calibration_targets_open_their_own_tab(string target, int index)
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(new AppSettings());
        using var vm = new CalibrationViewModel(device, settings, new PHControlViewModel(device, settings));
        vm.Select(target);
        Assert.Equal(index, vm.SelectedTabIndex);
    }

    [Theory]
    [InlineData("10.8.0-dev", true)]
    [InlineData("10.9.1", true)]
    [InlineData("10.7.1-dev", false)]
    [InlineData(null, false)]
    public void Direct_bath_setpoint_needs_hub_10_8(string? firmware, bool supported)
        => Assert.Equal(supported, ExternalBathViewModel.HubAtLeast(firmware, new Version(10, 8, 0)));

    [Fact]
    public void Direct_bath_setpoint_is_sent_only_with_the_cascade_stopped()
    {
        var device = new RecordingDeviceService();
        var dispatcher = new StubDispatcher();
        var settings = new MemorySettingsService(new AppSettings
        {
            ExternalBath = new ExternalBathSettings { PreferExternalRoute = true, CommunicationEnabled = true },
        });
        using var vm = new ExternalBathViewModel(device, settings, dispatcher);
        var frame = new SensorSnapshot
        {
            HasBathTelemetry = true, BathOnline = true, BathCommEnabled = true, TempControlViaBath = true,
            HubFirmwareVersion = "10.8.0-dev", BathCascadeActive = true, BathCascadeState = "controlling",
        };

        device.PushTelemetry(frame);
        Assert.False(vm.SendDirectSetpointCommand.CanExecute(null));
        Assert.Contains("Parar banho", vm.DirectSetpointHint, StringComparison.Ordinal);

        device.PushTelemetry(frame with { BathCascadeActive = false, BathCascadeState = "off" });
        vm.DirectSetpointText = "29,0";
        Assert.True(vm.SendDirectSetpointCommand.CanExecute(null));
        vm.SendDirectSetpointCommand.Execute(null);
        Assert.Equal("""{"bathSetpoint":29.0}""", Assert.Single(dispatcher.SeparateFrames));
        Assert.Equal("Parada", vm.CascadeHeadline);

        device.PushTelemetry(frame with { BathCascadeActive = false, HubFirmwareVersion = "10.7.1-dev" });
        Assert.False(vm.SendDirectSetpointCommand.CanExecute(null));
        Assert.Contains("10.8", vm.DirectSetpointHint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_start_delta_is_limited_to_ten_degrees_either_way()
    {
        var tuning = BathCascadeTuning.Defaults;
        Assert.Null((tuning with { BiasC = -1.2 }).Validate());
        Assert.NotNull((tuning with { BiasC = -10.5 }).Validate());
    }
}
