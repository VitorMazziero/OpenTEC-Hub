using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Telemetry;
using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The command arbiter: one owner per actuator, an honest command lifecycle, and a
/// safe abort that hands the wire back to the operator when the link fails.
/// </summary>
public sealed class CommandArbiterTests
{
    private static (CommandArbiter Arbiter, RecordingDeviceService Device, TestClock Clock) Build()
    {
        var device = new RecordingDeviceService();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        return (new CommandArbiter(device, clock), device, clock);
    }

    private static OpenTECCommand Temp(double c) => OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, c);

    // ── Ownership ────────────────────────────────────────────────────────────

    [Fact]
    public void Everything_starts_owned_by_Manual()
    {
        var (arbiter, _, _) = Build();

        Assert.All(CommandActuators.All, a => Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(a)));
    }

    [Fact]
    public void A_manual_send_reaches_the_wire()
    {
        var (arbiter, device, _) = Build();

        arbiter.Send(Temp(37.5));

        Assert.Equal("""{"tempSetpoint":37.5}""", Assert.Single(device.Sent));
    }

    [Fact]
    public void A_non_owner_command_is_refused_and_nothing_is_sent()
    {
        var (arbiter, device, _) = Build();
        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Agitation], "cascata assume agitação");

        CommandRejection? rejection = null;
        arbiter.CommandRejected += r => rejection = r;

        var result = arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.MotorSetpoint(500));

        Assert.False(result.Accepted);
        Assert.Contains(ActuatorId.Agitation, result.Refused);
        Assert.Empty(device.Sent);
        Assert.NotNull(rejection);
        Assert.Equal(CommandOwner.Automatic, Assert.Single(rejection!.Conflicts).Owner);
    }

    [Fact]
    public void The_owner_may_write_its_own_actuator()
    {
        var (arbiter, device, _) = Build();
        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Agitation], "cascata");

        var result = arbiter.Dispatch(CommandOwner.Automatic, CommandBuilders.MotorSetpoint(500));

        Assert.True(result.Accepted);
        Assert.Equal("""{"motorSetpoint":500}""", Assert.Single(device.Sent));
    }

    /// <summary>One owned-by-another actuator refuses the whole frame; a partial send is worse.</summary>
    [Fact]
    public void A_mixed_frame_is_refused_atomically_even_for_actuators_the_requester_owns()
    {
        var (arbiter, device, _) = Build();
        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Aeration], "cascata assume aeração");

        // Manual owns temperature but not aeration; the combined frame touches both.
        var combined = Temp(37.5).Merge(CommandBuilders.FlowSetpoint(2.5, 20));
        var result = arbiter.Dispatch(CommandOwner.Manual, combined);

        Assert.False(result.Accepted);
        Assert.Empty(device.Sent); // temperature was NOT sent on its own
    }

    [Fact]
    public void Returning_to_manual_revokes_every_owner()
    {
        var (arbiter, _, _) = Build();
        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Agitation, ActuatorId.Aeration], "cascata");

        OwnershipTransfer? change = null;
        arbiter.OwnershipChanged += t => change = t;

        arbiter.ReturnToManual("operador retomou o comando");

        Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(ActuatorId.Aeration));
        Assert.NotNull(change);
        Assert.False(change!.IsSafeAbort);
    }

    /// <summary>The transfer carries the last commanded state, so a new owner starts bumpless.</summary>
    [Fact]
    public void Claiming_returns_the_last_commanded_state_for_a_bumpless_start()
    {
        var (arbiter, _, _) = Build();
        arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.MotorSetpoint(450));

        var transfer = arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Agitation], "cascata");

        var entry = Assert.Single(transfer.LastDesired);
        Assert.Equal(ActuatorId.Agitation, entry.Actuator);
        Assert.Contains("motorSetpoint=450", entry.Summary, StringComparison.Ordinal);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    [Fact]
    public void A_sent_command_is_accepted_by_the_transport()
    {
        var (arbiter, _, _) = Build();

        arbiter.Send(Temp(37.5));

        var entry = Assert.Single(arbiter.Lifecycle);
        Assert.Equal(ActuatorId.Temperature, entry.Actuator);
        Assert.Equal(CommandPhase.TransportAccepted, entry.Phase);
    }

    /// <summary>Only aeration has a setpoint echo, so only it can be telemetry-confirmed.</summary>
    [Fact]
    public void Aeration_is_confirmed_when_the_flow_setpoint_is_echoed()
    {
        var (arbiter, device, _) = Build();

        arbiter.Send(CommandBuilders.FlowSetpoint(2.5, 20));
        device.PushTelemetry(new SensorSnapshot { FlowSetpoint = 2.5 });

        var entry = Assert.Single(arbiter.Lifecycle);
        Assert.Equal(ActuatorId.Aeration, entry.Actuator);
        Assert.Equal(CommandPhase.TelemetryConfirmed, entry.Phase);
    }

    /// <summary>A later unrelated command must not wipe a still-pending flow confirmation.</summary>
    [Fact]
    public void An_unrelated_command_does_not_lose_a_pending_flow_confirmation()
    {
        var (arbiter, device, _) = Build();

        arbiter.Send(CommandBuilders.FlowSetpoint(2.5, 20));
        arbiter.Send(Temp(37.5)); // unrelated; must not clear the flow confirm target
        device.PushTelemetry(new SensorSnapshot { FlowSetpoint = 2.5 });

        var aeration = Assert.Single(arbiter.Lifecycle, e => e.Actuator == ActuatorId.Aeration);
        Assert.Equal(CommandPhase.TelemetryConfirmed, aeration.Phase);
    }

    /// <summary>
    /// Temperature has no setpoint echo. It must rest at transport-accepted and say so,
    /// not claim a confirmation the wire never gives.
    /// </summary>
    [Fact]
    public void A_channel_without_an_echo_rests_at_accepted_and_labels_itself_honestly()
    {
        var (arbiter, device, _) = Build();

        arbiter.Send(Temp(37.5));
        device.PushTelemetry(new SensorSnapshot { Temperature = 37.5, FlowSetpoint = 99 });

        var entry = Assert.Single(arbiter.Lifecycle);
        Assert.Equal(CommandPhase.TransportAccepted, entry.Phase);
        Assert.Contains("sem eco", entry.ConfirmationChannel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_command_the_transport_never_accepts_times_out()
    {
        var (arbiter, device, clock) = Build();
        device.RaiseCommandSentOnSend = false; // buffered, never flushed — a dropped link

        arbiter.Dispatch(CommandOwner.Manual, Temp(37.5));
        Assert.Equal(CommandPhase.Issued, Assert.Single(arbiter.Lifecycle).Phase);

        clock.Advance(TimeSpan.FromSeconds(9));
        device.PushTelemetry(new SensorSnapshot());

        Assert.Equal(CommandPhase.TimedOut, Assert.Single(arbiter.Lifecycle).Phase);
    }

    // ── Safe abort ───────────────────────────────────────────────────────────

    [Fact]
    public void A_link_loss_holds_oxygen_and_recipe_owners_until_the_link_returns()
    {
        var (arbiter, device, _) = Build();
        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Agitation, ActuatorId.Aeration], "cascata");
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Temperature], "receita");
        arbiter.Claim(CommandOwner.PowerAssay, [ActuatorId.FlaskAgitator], "potência");
        var holds = new List<bool>();
        arbiter.LinkHoldChanged += holds.Add;
        OwnershipTransfer? revoked = null;
        arbiter.OwnershipRevoked += t => revoked = t;

        device.PushState(ConnectionState.Faulted);

        Assert.Equal(CommandOwner.Automatic, arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.Equal(CommandOwner.Recipe, arbiter.OwnerOf(ActuatorId.Temperature));
        Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(ActuatorId.FlaskAgitator));
        Assert.True(revoked!.IsSafeAbort);
        Assert.DoesNotContain(ActuatorId.Agitation, revoked.Actuators);
        Assert.True(arbiter.IsLinkHeld);
        var before = device.Sent.Count;
        var refused = arbiter.Dispatch(CommandOwner.Automatic, CommandBuilders.MotorSetpoint(300));
        Assert.False(refused.Accepted);
        Assert.True(refused.LinkUnavailable);
        Assert.Equal(before, device.Sent.Count);

        var epoch = arbiter.LinkEpoch;
        device.PushState(ConnectionState.Connected);
        Assert.False(arbiter.IsLinkHeld);
        Assert.Equal(epoch + 1, arbiter.LinkEpoch);
        Assert.Equal([true, false], holds);
        Assert.True(arbiter.Dispatch(CommandOwner.Automatic, CommandBuilders.MotorSetpoint(300)).Accepted);
    }

    [Fact]
    public void An_explicit_disconnect_returns_held_owners_to_manual()
    {
        var (arbiter, device, _) = Build();
        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Agitation, ActuatorId.Aeration], "cascata");
        device.PushState(ConnectionState.Reconnecting);
        Assert.True(arbiter.IsLinkHeld);

        device.PushState(ConnectionState.Disconnected, cause: ConnectionTransitionCause.UserDisconnect);

        Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(ActuatorId.Agitation));
        Assert.False(arbiter.IsLinkHeld);
    }

    [Fact]
    public void A_link_loss_times_out_a_command_that_had_not_been_accepted()
    {
        var (arbiter, device, _) = Build();
        device.RaiseCommandSentOnSend = false;

        arbiter.Dispatch(CommandOwner.Manual, Temp(37.5));
        device.PushState(ConnectionState.Reconnecting);

        Assert.Equal(CommandPhase.TimedOut, Assert.Single(arbiter.Lifecycle).Phase);
    }

    // ── Forwarding ───────────────────────────────────────────────────────────

    [Fact]
    public void Zeroing_the_session_clock_is_forwarded_to_the_transport()
    {
        var (arbiter, device, _) = Build();

        arbiter.ZeroSessionTime();

        Assert.Equal(1, device.ZeroSessionTimeCalls);
    }

    [Fact]
    public void The_session_zero_echo_is_forwarded_to_subscribers()
    {
        var (arbiter, device, _) = Build();
        double? offset = null;
        arbiter.SessionTimeZeroed += o => offset = o;

        device.PushSessionTimeZeroed(12.5);

        Assert.Equal(12.5, offset);
    }

    // ── Audit journal ────────────────────────────────────────────────────────

    [Fact]
    public void Ownership_transfers_and_refusals_are_journalled()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, new TestClock(DateTimeOffset.UnixEpoch));
        using var journal = new EventJournal(arbiter, arbiter, new MemorySettingsService());

        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Agitation], "cascata assume agitação");
        arbiter.Dispatch(CommandOwner.Manual, CommandBuilders.MotorSetpoint(500));

        var entries = journal.Snapshot();
        Assert.Contains(entries, e =>
            e.Source == AuditSource.Command && e.Message.Contains("Posse transferida", StringComparison.Ordinal));
        Assert.Contains(entries, e =>
            e.Source == AuditSource.Command && e.Severity == AuditSeverity.Warning &&
            e.Message.Contains("recusado", StringComparison.Ordinal));
    }

    [Fact]
    public void A_safe_abort_raises_an_alarm_severity_event()
    {
        var device = new RecordingDeviceService();
        using var arbiter = new CommandArbiter(device, new TestClock(DateTimeOffset.UnixEpoch));
        using var journal = new EventJournal(arbiter, arbiter, new MemorySettingsService());

        arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Aeration], "cascata");
        device.PushState(ConnectionState.Disconnected);

        Assert.Contains(journal.Snapshot(), e =>
            e.Source == AuditSource.Alarm && e.Message.Contains("Aborto seguro", StringComparison.Ordinal));
    }
}
