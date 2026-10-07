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
        Assert.Contains("recusado", vm.LastActionText, StringComparison.OrdinalIgnoreCase);
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
    public void Toggles_show_the_hub_route_and_flag_a_diverging_preference()
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

        // W1: the Hub's route wins over the saved preference, and the divergence is shown.
        Assert.False(vm.IsTempControlViaBath);
        Assert.True(vm.IsCommEnabled);
        Assert.False(vm.CanApplyNow);
        Assert.Contains("Preferência salva", vm.PreferenceMismatchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Hub_on_external_route_is_shown_even_when_the_preference_says_uart()
    {
        var device = new RecordingDeviceService();
        using var vm = new ExternalBathViewModel(device, new MemorySettingsService(), new StubDispatcher());

        device.PushTelemetry(new SensorSnapshot
        {
            HasBathTelemetry = true,
            BathOnline = true,
            BathCommEnabled = true,
            TempControlViaBath = true,
            BathMode = 1,
        });

        Assert.True(vm.IsTempControlViaBath);
        Assert.True(vm.IsCommEnabled);
        Assert.True(vm.CanApplyNow);
    }

    [Fact]
    public void Stop_stays_available_while_a_bath_command_is_pending()
    {
        var device = new RecordingDeviceService();
        var dispatcher = new StubDispatcher();
        using var vm = new ExternalBathViewModel(device, new MemorySettingsService(), dispatcher);
        device.PushTelemetry(new SensorSnapshot
        {
            HasBathTelemetry = true,
            BathOnline = true,
            BathCommEnabled = true,
            TempControlViaBath = true,
            BathCommandPending = true,
            BathCommandCompletionPending = true,
            BathCascadeState = "actuator_busy",
        });

        Assert.True(vm.StopCommand.CanExecute(null));
        vm.StopCommand.Execute(null);
        Assert.Contains(dispatcher.Sent, c => c.Contains("\"bathAbort\":1", StringComparison.Ordinal));
        Assert.Contains("manual", vm.LastActionText, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_is_offered_only_on_a_fault_and_the_reason_is_explained()
    {
        var device = new RecordingDeviceService();
        using var vm = new ExternalBathViewModel(device, new MemorySettingsService(), new StubDispatcher());
        device.PushTelemetry(new SensorSnapshot
        {
            HasBathTelemetry = true,
            BathOnline = true,
            BathCommEnabled = true,
            TempControlViaBath = true,
            BathCascadeState = "controlling",
        });
        Assert.False(vm.ResetFaultCommand.CanExecute(null));

        device.PushTelemetry(new SensorSnapshot
        {
            HasBathTelemetry = true,
            BathOnline = true,
            BathCommEnabled = true,
            TempControlViaBath = true,
            BathCascadeState = "fault",
            BathCascadeFaultReason = "node_rejected:range",
        });
        Assert.True(vm.ResetFaultCommand.CanExecute(null));
        Assert.Contains("recusou", vm.CascadeFaultText, StringComparison.Ordinal);
    }

    [Fact]
    public void Tuning_fields_follow_the_hub_echo_and_invalid_drafts_are_blocked()
    {
        var device = new RecordingDeviceService();
        using var vm = new ExternalBathViewModel(device, new MemorySettingsService(), new StubDispatcher());
        var echo = BathCascadeTuning.Defaults with { Kp = 0.8 };
        device.PushTelemetry(new SensorSnapshot
        {
            HasBathTelemetry = true,
            BathOnline = true,
            BathCommEnabled = true,
            TempControlViaBath = true,
            BathCascadeConfig = echo,
        });

        Assert.Equal(0.8.ToString("0.###", CultureInfo.CurrentCulture), vm.CascadeKpText);
        Assert.False(vm.CanApplyTuning);  // nothing to send: draft equals the Hub

        vm.CascadeOutputMinText = "50";
        vm.CascadeOutputMaxText = "40";
        Assert.False(vm.CanApplyTuning);  // the Hub would refuse min >= max

        vm.CascadeOutputMinText = "10";
        Assert.True(vm.CanApplyTuning);
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

    [Fact]
    public void Route_toggle_automatically_enables_communication_when_turning_on()
    {
        var device = new RecordingDeviceService();
        var dispatcher = new StubDispatcher();
        using var vm = new ExternalBathViewModel(device, new MemorySettingsService(), dispatcher);
        device.PushTelemetry(new SensorSnapshot
        {
            HasBathTelemetry = true,
            BathOnline = true,
            BathCommEnabled = false,
            TempControlViaBath = false,
        });

        Assert.False(vm.IsCommEnabled);
        vm.IsTempControlViaBath = true;

        Assert.True(vm.IsCommEnabled);
        Assert.Equal(2, dispatcher.Sent.Count);
        Assert.Contains(dispatcher.Sent, c => c.Contains("\"bathComm\":1", StringComparison.Ordinal));
        Assert.Contains(dispatcher.Sent, c => c.Contains("\"tempControlMode\":1", StringComparison.Ordinal));
    }
}
