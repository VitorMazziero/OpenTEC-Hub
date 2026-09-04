using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// WP6 live cascade actuation: the kLa-path allocation, the three modes, and the ownership
/// handshake — engage bumplessly, dispatch under Automatic, and safe-abort on stale oxygen,
/// link loss or a loss of ownership.
/// </summary>
public sealed class CascadeActuationTests
{
    private sealed class Harness : IDisposable
    {
        public Harness(CascadeSettings? configuration = null)
        {
            Device = new RecordingDeviceService();
            Clock = new TestClock(DateTimeOffset.UnixEpoch);
            Arbiter = new CommandArbiter(Device, Clock);
            Store = new FakeKlaProfileStore();
            Settings = new MemorySettingsService(new AppSettings
            {
                Cascade = configuration ?? new CascadeSettings { OxygenSetpointPercent = 30 },
            });
            Service = new CascadeService(Arbiter, Arbiter, Settings, Store, Clock);
        }

        public RecordingDeviceService Device { get; }
        public TestClock Clock { get; }
        public CommandArbiter Arbiter { get; }
        public FakeKlaProfileStore Store { get; }
        public MemorySettingsService Settings { get; }
        public CascadeService Service { get; }

        public void PushOxygen(double oxygenPercent, int frames = 1)
        {
            for (var i = 0; i < frames; i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(2));
                Device.PushTelemetry(new SensorSnapshot { OxygenCalibrated = oxygenPercent });
            }
        }

        public bool CascadeOwnsActuators() =>
            Arbiter.OwnerOf(ActuatorId.Agitation) == CommandOwner.Automatic &&
            Arbiter.OwnerOf(ActuatorId.Aeration) == CommandOwner.Automatic &&
            Arbiter.OwnerOf(ActuatorId.Oxygen) == CommandOwner.Automatic;

        public void Dispose()
        {
            Service.Dispose();
            Arbiter.Dispose();
        }
    }

    // ── Allocation ───────────────────────────────────────────────────────────

    [Fact]
    public void The_kla_path_maps_effort_across_the_published_allocation()
    {
        var allocation = new KlaPathAllocation(KlaTestProfiles.Linear().Payload.Allocation);

        Assert.Equal((200, 2.0), allocation.Allocate(0));
        Assert.Equal((700, 8.0), allocation.Allocate(100));
        Assert.Equal((440, 5.0), allocation.Allocate(50)); // kLa 70 -> the middle sample
    }

    [Fact]
    public void The_kla_path_inverts_an_actuator_to_an_effort_for_a_bumpless_start()
    {
        var allocation = new KlaPathAllocation(KlaTestProfiles.Linear().Payload.Allocation);

        Assert.Equal(50, allocation.EffortForAgitation(440), precision: 3);
        Assert.Equal(50, allocation.EffortForAeration(5.0), precision: 3);
    }

    [Fact]
    public void A_single_actuator_mode_holds_the_other_actuator()
    {
        var agitationOnly = SingleActuatorAllocation.Agitation(200, 700, heldAerationLpm: 3.0);

        Assert.Equal((200, 3.0), agitationOnly.Allocate(0));
        Assert.Equal((700, 3.0), agitationOnly.Allocate(100)); // aeration held throughout
    }

    // ── Mode / path selection ────────────────────────────────────────────────

    [Fact]
    public void The_default_mode_is_the_dual_cascade()
    {
        using var h = new Harness();
        Assert.Equal(CascadeMode.DualCascade, h.Service.Mode);
    }

    [Fact]
    public async Task Loading_paths_populates_the_available_list()
    {
        using var h = new Harness();
        h.Store.Published.Add(KlaTestProfiles.Linear("Ensaio A"));
        h.Store.Published.Add(KlaTestProfiles.Linear("Ensaio B"));

        await h.Service.LoadAvailablePathsAsync();

        Assert.Equal(2, h.Service.AvailablePaths.Count);
    }

    [Fact]
    public void Engaging_the_trajectory_mode_needs_a_published_path()
    {
        using var h = new Harness();
        h.Service.SelectMode(CascadeMode.KlaPath);

        Assert.False(h.Service.CanEngage(out var reason));
        Assert.Contains("mapa kLa", reason, StringComparison.OrdinalIgnoreCase);

        h.Service.SelectPath(KlaTestProfiles.Linear());
        Assert.True(h.Service.CanEngage(out _));
    }

    [Fact]
    public void A_disconnected_link_cannot_engage()
    {
        using var h = new Harness();
        h.Service.SelectPath(KlaTestProfiles.Linear());
        h.Device.PushState(ConnectionState.Faulted);

        Assert.False(h.Service.CanEngage(out var reason));
        Assert.Contains("Conecte-se", reason, StringComparison.Ordinal);
    }

    // ── Engage / dispatch / bumpless ─────────────────────────────────────────

    [Fact]
    public void Engaging_claims_the_oxygen_actuators_and_dispatches_each_frame()
    {
        using var h = new Harness();
        h.Service.SelectPath(KlaTestProfiles.Linear());

        h.Service.Engage(currentAgitationRpm: 440, currentAerationLpm: 5.0);
        Assert.True(h.Service.IsEngaged);
        Assert.True(h.CascadeOwnsActuators());

        h.PushOxygen(30, frames: 2);

        Assert.NotEmpty(h.Device.Sent);
        Assert.Contains(h.Device.Sent, frame =>
            frame.Contains("motorSetpoint", StringComparison.Ordinal) &&
            frame.Contains("oxygenMonitor", StringComparison.Ordinal));
    }

    [Fact]
    public void The_transfer_to_automatic_is_bumpless_from_the_current_actuators()
    {
        using var h = new Harness();
        h.Service.SelectMode(CascadeMode.KlaPath);
        h.Service.SelectPath(KlaTestProfiles.Linear());
        h.Service.Engage(currentAgitationRpm: 440, currentAerationLpm: 5.0);

        // Oxygen sitting at setpoint: the first automatic frame must reproduce the actuator
        // the operator left running, not snap somewhere else.
        h.PushOxygen(30);

        Assert.NotEmpty(h.Device.Sent);
        Assert.NotNull(h.Service.LastActuation);
        Assert.Equal(440, h.Service.LastActuation!.AgitationRpm);
        Assert.Equal(5.0, h.Service.LastActuation.AerationLpm, precision: 1);
    }

    [Fact]
    public void The_kla_demand_is_exposed_while_engaged_on_the_path()
    {
        using var h = new Harness();
        h.Service.SelectMode(CascadeMode.KlaPath);
        h.Service.SelectPath(KlaTestProfiles.Linear());
        h.Service.Engage(440, 5.0);

        h.PushOxygen(30);

        Assert.NotNull(h.Service.ActiveKlaDemand);
        Assert.Equal(70, h.Service.ActiveKlaDemand!.Value, precision: 0);
    }

    [Fact]
    public void The_mode_cannot_change_while_engaged()
    {
        using var h = new Harness();
        h.Service.SelectPath(KlaTestProfiles.Linear());
        h.Service.Engage(440, 5.0);

        h.Service.SelectMode(CascadeMode.AgitationOnly);

        Assert.Equal(CascadeMode.DualCascade, h.Service.Mode);
    }

    // ── Safe abort ───────────────────────────────────────────────────────────

    [Fact]
    public void Stale_oxygen_safe_aborts_and_hands_the_actuators_back()
    {
        using var h = new Harness();
        h.Service.SelectPath(KlaTestProfiles.Linear());
        h.Service.Engage(440, 5.0);

        // Three consecutive frames with no usable oxygen: the loop is flying blind.
        h.PushOxygen(SensorReadings.NotReceived, frames: 3);

        Assert.False(h.Service.IsEngaged);
        Assert.Equal(CommandOwner.Manual, h.Arbiter.OwnerOf(ActuatorId.Agitation));
    }

    [Fact]
    public void A_link_loss_revokes_ownership_and_disengages_the_cascade()
    {
        using var h = new Harness();
        h.Service.SelectPath(KlaTestProfiles.Linear());
        h.Service.Engage(440, 5.0);
        Assert.True(h.Service.IsEngaged);

        h.Device.PushState(ConnectionState.Faulted); // the arbiter safe-aborts ownership

        Assert.False(h.Service.IsEngaged);
        Assert.True(h.Arbiter.OwnerOf(ActuatorId.Aeration) == CommandOwner.Manual);
    }

    [Fact]
    public void A_manual_takeover_disengages_the_cascade()
    {
        using var h = new Harness();
        h.Service.SelectPath(KlaTestProfiles.Linear());
        h.Service.Engage(440, 5.0);

        h.Arbiter.ReturnToManual("operador retomou o comando");

        Assert.False(h.Service.IsEngaged);
    }

    [Fact]
    public void Disengaging_releases_ownership_but_keeps_the_advisory_loop_running()
    {
        using var h = new Harness();
        h.Service.SelectPath(KlaTestProfiles.Linear());
        h.Service.Engage(440, 5.0);
        h.PushOxygen(30);

        h.Service.Disengage("teste");

        Assert.False(h.Service.IsEngaged);
        Assert.True(h.Service.IsArmed); // still computing for the display
        Assert.Equal(CommandOwner.Manual, h.Arbiter.OwnerOf(ActuatorId.Oxygen));
    }

    // ── Advisory regression ──────────────────────────────────────────────────

    [Fact]
    public void Advisory_arming_still_never_sends()
    {
        using var h = new Harness();
        h.Service.Arm();

        h.PushOxygen(8, frames: 10);

        Assert.True(h.Service.IsArmed);
        Assert.False(h.Service.IsEngaged);
        Assert.Empty(h.Device.Sent);
    }
}
