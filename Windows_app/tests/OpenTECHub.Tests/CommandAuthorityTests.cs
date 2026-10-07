using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class CommandAuthorityTests
{
    [Fact]
    public void Delayed_transport_completion_does_not_confirm_a_newer_reference()
    {
        var (arbiter, device) = Build(); using var owner = arbiter;
        device.RaiseCommandSentOnSend = false;
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(300));
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(500));
        device.PushCommandSent(CommandBuilders.MotorSetpoint(300).ToJson());
        Assert.Equal(CommandPhase.Issued, Assert.Single(arbiter.Lifecycle).Phase);
        device.PushCommandSent(CommandBuilders.MotorSetpoint(500).ToJson());
        Assert.Equal(CommandPhase.TransportAccepted, Assert.Single(arbiter.Lifecycle).Phase);
    }

    [Fact]
    public async Task Unknown_prior_configuration_cannot_be_filled_with_an_instantaneous_measurement()
    {
        var (arbiter, device) = Build(); using var owner = arbiter;
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(300));
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.FlowRoute(2, 10, GasRoute.Reactor, GasRigConfiguration.Default));
        device.PushTelemetry(new() { ServoRpm = 300, FlowSetpoint = 2, FlowRate = 2, MotorControlViaModbus = true });
        var lease = await new RecipeResourceCoordinator(arbiter, TimeProvider.System).ReserveForAssayAsync(
            RecipeExecutionContractTests.Request().Context, [ActuatorId.Agitation, ActuatorId.Aeration], TimeSpan.FromSeconds(1));
        Assert.Throws<InvalidOperationException>(() => { lease.CaptureReturnSnapshot(GasRigConfiguration.Default, TimeProvider.System,
            device.Latest, DateTimeOffset.UtcNow); });
        lease.AbortBeforeAssay();
        Assert.Equal(CommandOwner.Recipe, arbiter.OwnerOf(ActuatorId.Agitation));
    }

    [Fact]
    public async Task Desired_capture_requires_a_drained_generation_and_contains_only_actual_authorized_enqueues()
    {
        var (arbiter, device) = Build(); using var owner = arbiter;
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorControlMode(false));
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(300));
        var lease = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "kla", [ActuatorId.Agitation], TimeSpan.FromSeconds(1));
        Assert.Throws<InvalidOperationException>(() => arbiter.CaptureReservedDesiredState(lease));
        await arbiter.DrainReservedCommandsAsync(lease);
        var captured = Assert.Single(arbiter.CaptureReservedDesiredState(lease));
        Assert.Equal("0", OpenTECCommand.Parse(captured.DesiredCommandJson).GetRawValue(CommandKeys.MotorControlMode));
        Assert.Equal("300", OpenTECCommand.Parse(captured.DesiredCommandJson).GetRawValue(CommandKeys.MotorSetpoint));
        Assert.False(arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(900)).Accepted);
        Assert.Equal(captured, Assert.Single(arbiter.CaptureReservedDesiredState(lease)));
        arbiter.DispatchReserved(lease, CommandBuilders.MotorSetpoint(450));
        Assert.Throws<InvalidOperationException>(() => arbiter.CaptureReservedDesiredState(lease));
        await arbiter.DrainReservedCommandsAsync(lease);
        Assert.Equal("450", OpenTECCommand.Parse(Assert.Single(arbiter.CaptureReservedDesiredState(lease)).DesiredCommandJson).GetRawValue(CommandKeys.MotorSetpoint));
        arbiter.ReturnToManual("stop", true);
        Assert.Throws<InvalidOperationException>(() => arbiter.CaptureReservedDesiredState(lease));
    }

    [Theory]
    [InlineData("{\"motorSetpoint\":300,\"motorSetpoint\":400}")]
    [InlineData("{\"motorSetpoint\":[300]}")]
    [InlineData("{\"motorSetpoint\":{\"value\":300}}")]
    [InlineData("{\"motorSetpoint\":1e999}")]
    [InlineData("{\"motorSetpoint\":null}")]
    [InlineData("{\"motorSetpoint\":true}")]
    public void Frozen_command_parser_rejects_ambiguous_or_non_wire_values(string json)
        => Assert.Throws<ArgumentException>(() => OpenTECCommand.Parse(json));

    [Fact]
    public void Selected_command_fields_are_independent_of_the_mutable_builder()
    {
        var command = OpenTECCommand.Create().Set(CommandKeys.MotorSetpoint, 300).Set(CommandKeys.FlowSetpoint, 2.0);
        var motor = command.SelectKeys(key => CommandActuators.ForKey(key) == ActuatorId.Agitation);
        command.Set(CommandKeys.MotorSetpoint, 900);
        Assert.Equal(CommandBuilders.MotorSetpoint(300).ToJson(), motor.ToJson());
        Assert.Equal(command.ToJson(), OpenTECCommand.Parse(command.ToJson()).ToJson());
    }

    private static readonly ActuatorId[] Resources = [ActuatorId.Agitation, ActuatorId.Aeration, ActuatorId.Oxygen];
    private static (CommandArbiter Arbiter, RecordingDeviceService Device) Build()
    {
        var device = new RecordingDeviceService();
        var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, Resources, "recipe start");
        return (arbiter, device);
    }

    [Fact]
    public async Task Reservation_excludes_other_blocks_with_the_same_owner_but_preserves_independent_resources()
    {
        var (arbiter, device) = Build(); using var owner = arbiter;
        var lease = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "kla", Resources, TimeSpan.FromSeconds(1));
        Assert.False(arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(400)).Accepted);
        Assert.False(arbiter.DispatchReserved(lease, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 37)).Accepted);
        Assert.True(arbiter.Dispatch(CommandOwner.Manual, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 37)).Accepted);
        Assert.True(arbiter.DispatchReserved(lease, CommandBuilders.MotorSetpoint(400)).Accepted);
        Assert.Equal(2, device.Sent.Count);
        arbiter.ReleaseReservation(lease);
        Assert.True(arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(500)).Accepted);
    }

    [Fact]
    public async Task Handoff_is_atomic_and_old_generations_cannot_dispatch_or_return_ownership()
    {
        var (arbiter, _) = Build(); using var owner = arbiter;
        var lease = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "kla", Resources, TimeSpan.FromSeconds(1));
        var transfers = new List<OwnershipTransfer>(); arbiter.OwnershipChanged += transfers.Add;
        Assert.Throws<InvalidOperationException>(() => arbiter.TransferReserved(lease, CommandOwner.KlaAssay, "before transport barrier"));
        await arbiter.DrainReservedCommandsAsync(lease);
        var assay = arbiter.TransferReserved(lease, CommandOwner.KlaAssay, "assay begin");
        Assert.False(arbiter.IsCurrent(lease)); Assert.True(arbiter.IsCurrent(assay));
        Assert.False(arbiter.DispatchReserved(lease, CommandBuilders.MotorSetpoint(400)).Accepted);
        Assert.Throws<InvalidOperationException>(() => arbiter.TransferReserved(lease, CommandOwner.Recipe, "late callback"));
        Assert.Throws<InvalidOperationException>(() => arbiter.Claim(CommandOwner.Automatic, [ActuatorId.Agitation], "steal"));
        Assert.True(arbiter.DispatchReserved(assay, CommandBuilders.MotorSetpoint(400)).Accepted);
        await arbiter.DrainReservedCommandsAsync(assay);
        var returned = arbiter.TransferReserved(assay, CommandOwner.Recipe, "confirmed return");
        Assert.Equal(2, returned.Generation);
        Assert.All(Resources, a => Assert.Equal(CommandOwner.Recipe, arbiter.OwnerOf(a)));
        Assert.Equal([CommandOwner.KlaAssay, CommandOwner.Recipe], transfers.Select(t => t.To).ToArray());
        arbiter.ReleaseReservation(returned);
    }

    [Fact]
    public async Task Conflicting_reservations_wait_as_whole_sets_and_cancel_without_partial_acquisition()
    {
        var (arbiter, _) = Build(); using var owner = arbiter;
        var first = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "first", [ActuatorId.Agitation], TimeSpan.FromSeconds(1));
        using var cancel = new CancellationTokenSource();
        var pending = arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "second",
            [ActuatorId.Aeration, ActuatorId.Agitation], TimeSpan.FromSeconds(1), cancel.Token);
        Assert.False(pending.IsCompleted);
        var independent = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "flow",
            [ActuatorId.Aeration], TimeSpan.FromSeconds(1));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        arbiter.ReleaseReservation(independent); arbiter.ReleaseReservation(first);
        var next = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "next", Resources.Reverse().ToArray(), TimeSpan.FromSeconds(1));
        Assert.Equal(Resources.OrderBy(a => a), next.Resources);
    }

    [Fact]
    public async Task Releasing_resources_wakes_a_waiter_and_timeout_never_changes_ownership()
    {
        var (arbiter, _) = Build(); using var owner = arbiter;
        var first = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "first", Resources, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(),
            "timeout", Resources, TimeSpan.FromMilliseconds(10)));
        Assert.True(arbiter.IsCurrent(first));
        var pending = arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "second", Resources, TimeSpan.FromSeconds(1));
        arbiter.ReleaseReservation(first);
        var second = await pending;
        Assert.True(arbiter.IsCurrent(second)); Assert.False(arbiter.IsCurrent(first));
    }

    [Fact]
    public async Task Emergency_invalidates_all_authorities_and_a_late_return_cannot_restart_actuation()
    {
        var (arbiter, device) = Build(); using var owner = arbiter;
        var lease = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "kla", Resources, TimeSpan.FromSeconds(1));
        await arbiter.DrainReservedCommandsAsync(lease);
        var assay = arbiter.TransferReserved(lease, CommandOwner.KlaAssay, "begin");
        arbiter.DispatchSafety(CommandBuilders.MotorSetpoint(0), "emergency");
        Assert.False(arbiter.IsCurrent(assay));
        Assert.False(arbiter.DispatchReserved(assay, CommandBuilders.MotorSetpoint(400)).Accepted);
        Assert.Throws<InvalidOperationException>(() => arbiter.TransferReserved(assay, CommandOwner.Recipe, "late return"));
        Assert.Equal(CommandBuilders.MotorSetpoint(0).ToJson(), Assert.Single(device.Sent));
    }

    [Fact]
    public async Task Transport_barrier_fails_before_handoff_if_a_previous_frame_was_not_written()
    {
        var (arbiter, device) = Build(); using var owner = arbiter;
        device.RaiseCommandSentOnSend = false;
        arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(400));
        var lease = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "kla", Resources, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IOException>(() => arbiter.DrainReservedCommandsAsync(lease));
        Assert.All(Resources, a => Assert.Equal(CommandOwner.Recipe, arbiter.OwnerOf(a)));
        Assert.True(arbiter.IsCurrent(lease));
    }

    [Fact]
    public void Ownership_changed_during_tracking_callback_is_rechecked_before_enqueue()
    {
        var (arbiter, device) = Build(); using var owner = arbiter;
        arbiter.CommandTracked += _ => arbiter.ReturnToManual("emergency during callback", isSafeAbort: true);
        Assert.False(arbiter.Dispatch(CommandOwner.Recipe, CommandBuilders.MotorSetpoint(500)).Accepted);
        Assert.Empty(device.Sent);
    }
}
