using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>D-074: an operator-chosen start effort for re-engaging the oxygen loop mid-run.</summary>
public sealed class CascadeRestartEffortTests
{
    [Fact]
    public void Engaging_with_a_restart_effort_starts_the_loop_there_instead_of_at_the_manual_setpoints()
    {
        var (service, device, clock, _) = Build();
        using (service)
        {
            service.Engage(50, 0.5, 40);
            Push(device, clock, oxygen: 30); // at the setpoint: the loop barely moves
            Assert.InRange(service.Terms.Output, 38, 42);
            var (rpm, _) = new WindowAllocation(service.Windows[0], service.Windows[1]).Allocate(40);
            Assert.InRange(service.LastCommandedActuation!.AgitationRpm, rpm - 20, rpm + 20);
        }

        var (bumpless, device2, clock2, _) = Build();
        using (bumpless)
        {
            bumpless.Engage(50, 0.5, null);  // 50 rpm is the bottom of the window
            Push(device2, clock2, oxygen: 30);
            Assert.InRange(bumpless.Terms.Output, 0, 2);
        }
    }

    [Fact]
    public void Disengaging_records_the_effort_for_the_next_restart()
    {
        var (service, device, clock, settings) = Build();
        using (service)
        {
            service.Engage(50, 0.5, 35);
            Push(device, clock, oxygen: 30);
            var held = service.Terms.Output;
            service.Disengage("teste");

            Assert.Equal(Math.Round(held, 1), settings.Current.Cascade.LastEffortPercent);
            Assert.Equal(clock.GetUtcNow(), settings.Current.Cascade.LastEffortAt);
        }
    }

    [Fact]
    public void Oxygen_settings_save_the_restart_effort_and_offer_the_last_one()
    {
        var settings = new MemorySettingsService(new AppSettings
        {
            Cascade = new CascadeSettings { LastEffortPercent = 42.5, LastEffortAt = DateTimeOffset.UnixEpoch },
        });
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(new RecordingDeviceService(), clock);
        using var service = new CascadeService(arbiter, arbiter, settings, new FakeKlaProfileStore(), clock);
        using var vm = new OxygenConfigViewModel(service, settings, new FakeKlaProfileStore());

        Assert.False(vm.UseRestartEffort);
        Assert.Contains("parte dos setpoints manuais", vm.RestartPreviewText, StringComparison.Ordinal);
        Assert.True(vm.UseLastEffortCommand.CanExecute(null));
        vm.UseLastEffortCommand.Execute(null);
        Assert.True(vm.UseRestartEffort);
        Assert.Equal("42.5", vm.RestartEffortText);
        Assert.Contains("rpm", vm.RestartPreviewText, StringComparison.Ordinal);

        vm.RestartEffortText = "130";
        vm.ApplyCommand.Execute(null);
        Assert.Contains("0 e 100", vm.ValidationError, StringComparison.Ordinal);

        vm.RestartEffortText = "35";
        vm.ApplyCommand.Execute(null);
        Assert.True(vm.DialogResult, vm.ValidationError);
        Assert.True(settings.Current.Cascade.UseRestartEffort);
        Assert.Equal(35, settings.Current.Cascade.RestartEffortPercent);
        Assert.Equal(42.5, settings.Current.Cascade.LastEffortPercent); // the record survives an apply
    }

    private static (CascadeService, RecordingDeviceService, TestClock, MemorySettingsService) Build()
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(new AppSettings { Cascade = new CascadeSettings { OxygenSetpointPercent = 30 } });
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(device, clock);
        return (new CascadeService(arbiter, arbiter, settings, new FakeKlaProfileStore(), clock), device, clock, settings);
    }

    private static void Push(RecordingDeviceService device, TestClock clock, double oxygen)
    {
        clock.Advance(TimeSpan.FromSeconds(2));
        device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = oxygen });
    }
}
