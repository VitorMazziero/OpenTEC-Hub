using OpenTECHub.Protocol;
using OpenTECHub.ViewModels;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class ExternalBathViewModelTests
{
    [Fact]
    public void Telemetry_keeps_reactor_pv_separate_from_c404_pv_and_unlocks_commands()
    {
        var device = new RecordingDeviceService();
        var dispatcher = new StubDispatcher();
        using var vm = new ExternalBathViewModel(device, new MemorySettingsService(), dispatcher)
        {
            IsTempControlViaBath = false,
            IsCommEnabled = false,
        };

        device.PushTelemetry(new SensorSnapshot
        {
            HasBathTelemetry = true,
            BathOnline = true,
            BathCommEnabled = true,
            TempControlViaBath = true,
            Temperature = 31.25,
            BathPv = 26.5,
            BathSp = 30,
            BathTarget = 31,
            BathState = "RUN",
            BathPhase = "HOLD",
            BathCascadeState = "active",
        });

        Assert.Equal("31,25", vm.ReactorPvText);
        Assert.Equal("26,50", vm.BathPvText);
        Assert.True(vm.CanApplyNow);
        Assert.True(vm.SynchronizeCommand.CanExecute(null));
    }

    [Fact]
    public void Route_toggle_reverts_when_manual_arbiter_refuses_temperature_actuator()
    {
        var device = new RecordingDeviceService();
        var dispatcher = new StubDispatcher { RefuseWith = [ActuatorId.Temperature] };
        using var vm = new ExternalBathViewModel(device, new MemorySettingsService(), dispatcher);

        vm.IsTempControlViaBath = true;

        Assert.False(vm.IsTempControlViaBath);
        Assert.Contains("recusado", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Route_and_panel_preferences_round_trip_without_startup_commands()
    {
        var settings = new MemorySettingsService(new AppSettings());
        var device = new RecordingDeviceService();
        var dispatcher = new StubDispatcher();
        using (var vm = new ExternalBathViewModel(device, settings, dispatcher)
               {
                   IsTempControlViaBath = true,
                   IsCommEnabled = true,
                   IsPanelExpanded = false,
               })
        {
            Assert.Equal(2, dispatcher.Sent.Count);
        }

        Assert.True(settings.Current.ExternalBath.PreferExternalRoute);
        Assert.True(settings.Current.ExternalBath.CommunicationEnabled);
        Assert.False(settings.Current.ExternalBath.PanelExpanded);

        using var restored = new ExternalBathViewModel(device, settings, new StubDispatcher());
        Assert.True(restored.IsTempControlViaBath);
        Assert.True(restored.IsCommEnabled);
        Assert.False(restored.IsPanelExpanded);
    }
}
