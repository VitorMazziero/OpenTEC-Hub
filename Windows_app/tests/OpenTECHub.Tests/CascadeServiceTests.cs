using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The advisory cascade runtime: it computes on live telemetry and, above all, never sends.
/// </summary>
public class CascadeServiceTests
{
    [Theory]
    [InlineData(3.535679803241002, 3.55)]
    [InlineData(3.534811063355768, 3.55)]
    [InlineData(3.5338705503630163, 3.55)]
    [InlineData(3.5249, 3.50)]
    [InlineData(3.525, 3.55)]
    [InlineData(3.575, 3.60)]
    [InlineData(0, 0)]
    public void Aeration_commands_use_steps_of_five_hundredths(double requested, double expected)
        => Assert.Equal(expected, CascadeController.QuantizeAeration(requested));

    [Fact]
    public void Unchanged_commands_are_not_resent_and_reengaging_sends_the_first_command()
    {
        var (service, device, clock) = Build();
        using (service)
        {
            service.SelectMode(CascadeMode.AerationOnly);
            service.Engage(331, 3.535679803241002);
            device.Sent.Clear();
            PushOxygen(device, clock, 30);
            Assert.Contains("\"flowSetpoint\":3.55", Assert.Single(device.Sent));
            PushOxygen(device, clock, 30, frames: 5);
            Assert.Single(device.Sent);
            Assert.Equal(3.55, service.LastCommandedActuation!.AerationLpm);

            service.Disengage("Fim");
            service.Engage(331, 3.535679803241002);
            device.Sent.Clear();
            PushOxygen(device, clock, 30);
            Assert.Single(device.Sent);
        }
    }

    [Fact]
    public void Quantization_never_exceeds_configured_flow_endpoints()
    {
        var controller = CascadeController.CreateDefault();
        controller.SetAllocation(SingleActuatorAllocation.Aeration(0.12, 0.13, 331));
        controller.Preload(0);
        Assert.Equal(0.12, controller.Update(30, 2).AerationLpm);
        controller.Preload(100);
        Assert.Equal(0.13, controller.Update(30, 2).AerationLpm);
        controller.SetAllocation(SingleActuatorAllocation.Agitation(200, 800, 3.535));
        Assert.Equal(3.535, controller.Update(30, 2).AerationLpm);
    }
    [Fact]
    public void Advisory_outputs_are_not_published_as_commands_and_disengage_clears_the_command()
    {
        var (service, device, clock) = Build();
        using (service)
        {
            service.Arm();
            PushOxygen(device, clock, 8);
            Assert.NotNull(service.LastActuation);
            Assert.Null(service.LastCommandedActuation);
            service.Engage(50, 0.5);
            PushOxygen(device, clock, 8);
            Assert.Equal(service.LastActuation, service.LastCommandedActuation);
            Assert.NotNull(service.LastCommandedActuation);
            service.Disengage("Fim do teste");
            Assert.Null(service.LastCommandedActuation);
        }
    }
    [Fact]
    public void E2_SuspensionHoldsControllerTermsDuringAssay()
    {
        var (service, device, clock) = Build();
        service.Arm();
        PushOxygen(device, clock, 8, 5);
        var before = service.Terms;
        service.SuspendForKlaAssay();
        PushOxygen(device, clock, 80, 50);
        Assert.Equal(before, service.Terms);
        service.ResumeAfterKlaAssay(false, 450, 3);
        PushOxygen(device, clock, 80);
        Assert.NotEqual(before, service.Terms);
    }
    private static (CascadeService Service, RecordingDeviceService Device, TestClock Clock) Build(
        CascadeSettings? configuration = null)
    {
        var device = new RecordingDeviceService();
        var settings = new MemorySettingsService(new AppSettings
        {
            Cascade = configuration ?? new CascadeSettings { OxygenSetpointPercent = 30 },
        });
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var arbiter = new CommandArbiter(device, clock);
        return (new CascadeService(arbiter, arbiter, settings, new FakeKlaProfileStore(), clock), device, clock);
    }

    private static void PushOxygen(
        RecordingDeviceService device, TestClock clock, double oxygenPercent, int frames = 1)
    {
        for (var i = 0; i < frames; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = oxygenPercent });
        }
    }

    [Fact]
    public void A_disarmed_service_tracks_oxygen_but_does_not_step_the_loop()
    {
        var (service, device, clock) = Build();

        PushOxygen(device, clock, 42, frames: 3);

        Assert.Equal(42, service.LatestOxygen);
        Assert.Equal(CascadeTerms.Empty, service.Terms);
        Assert.Null(service.LastActuation);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public void An_armed_service_computes_actuation_and_never_sends()
    {
        var (service, device, clock) = Build(new CascadeSettings { OxygenSetpointPercent = 30 });
        service.Arm();

        // Oxygen held well below the 30 % target: the loop should raise its effort.
        PushOxygen(device, clock, 8, frames: 40);

        Assert.True(service.IsArmed);
        Assert.NotNull(service.LastActuation);
        Assert.True(service.Terms.Output > 0, "The loop should command a positive effort when oxygen is short.");
        Assert.InRange(service.LastActuation!.AgitationRpm, 200, 800);

        // The whole point of this WP: advisory, so nothing reaches the wire.
        Assert.Empty(device.Sent);
    }

    [Fact]
    public void Effort_rises_while_oxygen_stays_below_setpoint()
    {
        var (service, device, clock) = Build(new CascadeSettings { OxygenSetpointPercent = 30 });
        service.Arm();

        PushOxygen(device, clock, 8, frames: 5);
        var early = service.Terms.Output;

        PushOxygen(device, clock, 8, frames: 20);
        var later = service.Terms.Output;

        Assert.True(later > early, $"Integral action should keep raising effort: {early} -> {later}.");
    }

    [Fact]
    public void Disarming_clears_the_live_terms()
    {
        var (service, device, clock) = Build();
        service.Arm();
        PushOxygen(device, clock, 8, frames: 5);
        Assert.NotNull(service.LastActuation);

        service.Disarm();

        Assert.False(service.IsArmed);
        Assert.Equal(CascadeTerms.Empty, service.Terms);
        Assert.Null(service.LastActuation);
    }

    [Fact]
    public void The_sentinel_oxygen_reading_is_not_treated_as_a_measurement()
    {
        var (service, device, clock) = Build();
        service.Arm();

        PushOxygen(device, clock, SensorReadings.NotReceived, frames: 3);

        Assert.Null(service.LatestOxygen);
        Assert.Null(service.LastActuation);
    }

    [Fact]
    public void Configure_updates_the_setpoint_and_the_actuator_windows()
    {
        var (service, _, _) = Build();

        service.Configure(new CascadeSettings
        {
            OxygenSetpointPercent = 45,
            AgitationMaxRpm = 900,
        });

        Assert.Equal(45, service.OxygenSetpoint);
        var agitation = service.Windows.Single(w => w.Name == CascadeController.AgitationActuator);
        Assert.Equal(900, agitation.Max);
    }

    [Fact]
    public void Updated_fires_for_every_frame()
    {
        var (service, device, clock) = Build();
        var count = 0;
        service.Updated += () => count++;

        PushOxygen(device, clock, 30, frames: 4);

        Assert.Equal(4, count);
    }

    [Fact]
    public void CanEngage_refuses_when_proportional_gas_is_active()
    {
        var (service, _, _) = Build();
        service.ProportionalGasActivePredicate = () => true;

        var canEngage = service.CanEngage(out var reason);

        Assert.False(canEngage);
        Assert.NotNull(reason);
        Assert.Contains("gás proporcional", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanEngage_permits_when_proportional_gas_is_inactive()
    {
        var (service, _, _) = Build();
        service.ProportionalGasActivePredicate = () => false;

        var canEngage = service.CanEngage(out var reason);

        Assert.True(canEngage);
        Assert.Null(reason);
    }
}
