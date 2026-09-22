using System.Globalization;
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
        var settings = new MemorySettingsService(new AppSettings
        {
            ExternalBath = new ExternalBathSettings
            {
                PreferExternalRoute = true,
                CommunicationEnabled = true,
            },
        });
        using var vm = new ExternalBathViewModel(device, settings, dispatcher);

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
        var settings = new MemorySettingsService(new AppSettings
        {
            ExternalBath = new ExternalBathSettings { CommunicationEnabled = true },
        });
        using var vm = new ExternalBathViewModel(device, settings, dispatcher);
        device.PushTelemetry(new SensorSnapshot
        {
            HasBathTelemetry = true,
            BathOnline = true,
            BathCommEnabled = true,
            TempControlViaBath = false,
        });

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
        using (var vm = new ExternalBathViewModel(device, settings, dispatcher))
        {
            device.PushTelemetry(new SensorSnapshot
            {
                HasBathTelemetry = true,
                BathOnline = true,
                BathCommEnabled = false,
                TempControlViaBath = false,
            });
            vm.IsCommEnabled = true;
            device.PushTelemetry(new SensorSnapshot
            {
                HasBathTelemetry = true,
                BathOnline = true,
                BathCommEnabled = true,
                BathCommandPending = false,
                TempControlViaBath = false,
            });
            vm.IsTempControlViaBath = true;
            vm.IsPanelExpanded = false;
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

    [Fact]
    public void Unsupported_hub_cannot_stage_bath_route_communication_or_mode_commands()
    {
        var device = new RecordingDeviceService();
        var dispatcher = new StubDispatcher();
        using var vm = new ExternalBathViewModel(device, new MemorySettingsService(), dispatcher);

        vm.IsTempControlViaBath = true;
        vm.IsCommEnabled = true;
        vm.IsAutomatic = false;

        Assert.False(vm.IsTempControlViaBath);
        Assert.False(vm.IsCommEnabled);
        Assert.True(vm.IsAutomatic);
        Assert.Empty(dispatcher.Sent);
    }

    [Fact]
    public void Hub_echo_does_not_erase_operator_route_intent_or_unlock_before_confirmation()
    {
        var settings = new MemorySettingsService(new AppSettings
        {
            ExternalBath = new ExternalBathSettings
            {
                PreferExternalRoute = true,
                CommunicationEnabled = true,
            },
        });
        var device = new RecordingDeviceService();
        using var vm = new ExternalBathViewModel(device, settings, new StubDispatcher());

        device.PushTelemetry(new SensorSnapshot
        {
            HasBathTelemetry = true,
            BathOnline = true,
            BathCommEnabled = true,
            TempControlViaBath = false,
        });

        Assert.True(vm.IsTempControlViaBath);
        Assert.True(vm.IsCommEnabled);
        Assert.False(vm.CanApplyNow);
    }

    [Fact]
    public void Persisted_decimal_tuning_is_valid_in_pt_br_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            var settings = new MemorySettingsService(new AppSettings
            {
                ExternalBath = new ExternalBathSettings
                {
                    PreferExternalRoute = true,
                    CommunicationEnabled = true,
                    CascadeKp = 0.75,
                    CascadeBias = 0.65,
                    CascadeBand = 0.15,
                },
            });
            var device = new RecordingDeviceService();
            using var vm = new ExternalBathViewModel(device, settings, new StubDispatcher());

            device.PushTelemetry(new SensorSnapshot
            {
                HasBathTelemetry = true,
                BathOnline = true,
                BathCommEnabled = true,
                TempControlViaBath = true,
            });

            Assert.Equal("0,75", vm.CascadeKpText);
            Assert.True(vm.CanApplyTuning);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
