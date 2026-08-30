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
}
