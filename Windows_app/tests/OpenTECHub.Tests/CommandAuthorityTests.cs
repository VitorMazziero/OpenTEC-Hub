using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class CommandAuthorityTests
{
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
