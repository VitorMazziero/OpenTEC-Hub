using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampRecoveryCoordinatorTests
{
    [Fact]
    public async Task HoldingLastReferencesClosesProducerAndPersistsWithoutSendingAnotherReference()
    {
        var device = new RecordingDeviceService();
        using var writer = new BackgroundFileWriter();
        using var arbiter = new CommandArbiter(device, TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Temperature], "start");
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 25));
        var configuration = new RecipeRampBlockConfiguration(new() { CancellationPolicy = RampCancellationPolicy.HoldLastReferences,
            Lines = [new() { Variable = SetpointVariable.Temperature, FinalSetpoint = 30, EndAfterSeconds = 60 }] }, null,
            RampTemperatureRoute.NativeModule);
        var authority = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "ramp", [ActuatorId.Temperature], TimeSpan.FromSeconds(2));
        await arbiter.DrainReservedCommandsAsync(authority);
        var start = new RecipeRampStartCheckpoint(1, Guid.NewGuid(), configuration,
            RecipeRampInitialState.Capture(configuration, arbiter, authority, TimeProvider.System));
        arbiter.ReleaseReservation(authority);
        var store = new RecipeRampCheckpointStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "ramp-hold-" + Guid.NewGuid().ToString("N")), writer);
        await store.PersistStartAsync(start);
        var coordinator = new RecipeResourceCoordinator(arbiter, TimeProvider.System);
        using var producer = new RecipeRampResourceProducer("ramp", [ActuatorId.Temperature], new RecipeRampActiveClock(TimeProvider.System));
        coordinator.Register(producer);
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 28));
        var count = device.Sent.Count;
        var terminal = await coordinator.HoldInterruptedRampAsync(start, producer, store,
            RecipeRampTerminalStatus.Cancelled, "cancelled", TimeSpan.FromSeconds(5));
        Assert.Equal(count, device.Sent.Count);
        Assert.Equal(RecipeRampReturnOutcome.HeldLastReferences, terminal.ReturnOutcome);
        Assert.Null(terminal.Recovery);
        Assert.False(terminal.HasVerifiedRecovery);
        Assert.Equal(RecipeRampReturnOutcome.HeldLastReferences,
            store.ReadTerminal(start.InitialState.ExecutionId, start.InvocationId)!.ReturnOutcome);
        Assert.True(producer.StopToken.IsCancellationRequested);
        Assert.True(arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 29)).Accepted);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task RecoveryRetainsReservationUntilValidatedDurableReceipt(bool incompleteProof, bool emergency, bool storageFailure)
    {
        using var writer = new BackgroundFileWriter();
        using var arbiter = new CommandArbiter(new RecordingDeviceService(), TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Temperature], "start");
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 25));
        var configuration = new RecipeRampBlockConfiguration(new() { CancellationPolicy = RampCancellationPolicy.RestoreSnapshot,
            Lines = [new() { Variable = SetpointVariable.Temperature, FinalSetpoint = 30, EndAfterSeconds = 60 }] }, null,
            RampTemperatureRoute.NativeModule);
        var authority = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "ramp", [ActuatorId.Temperature], TimeSpan.FromSeconds(2));
        await arbiter.DrainReservedCommandsAsync(authority);
        var start = new RecipeRampStartCheckpoint(1, Guid.NewGuid(), configuration,
            RecipeRampInitialState.Capture(configuration, arbiter, authority, TimeProvider.System));
        arbiter.ReleaseReservation(authority);
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ramp-recovery-coordinator-" + Guid.NewGuid().ToString("N"));
        var store = new RecipeRampCheckpointStore(root, writer);
        await store.PersistStartAsync(start);
        var coordinator = new RecipeResourceCoordinator(arbiter, TimeProvider.System);
        var clock = new RecipeRampActiveClock(TimeProvider.System);
        using var producer = new RecipeRampResourceProducer("ramp", [ActuatorId.Temperature], clock);
        coordinator.Register(producer);
        var altered = start with { InitialState = start.InitialState with { SnapshotId = Guid.NewGuid() } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RecoverRampAsync(altered, producer, store,
            (_, _) => throw new InvalidOperationException("Não deve executar retorno de captura alterada."), TimeSpan.FromSeconds(5)));
        Assert.False(clock.IsSuspended);
        Assert.False(producer.StopToken.IsCancellationRequested);
        var entered = new TaskCompletionSource<CommandAuthorityLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovering = coordinator.RecoverRampAsync(start, producer, store, async (lease, ct) =>
        {
            entered.SetResult(lease);
            await finish.Task.WaitAsync(ct);
            var proof = new RecipeRampFinalConfirmation(SetpointVariable.Temperature, null, 25,
                RecipeRampConfirmationEvidence.ProcessFeedback, DateTimeOffset.UtcNow, 25, .5);
            return new(2, start.InitialState.ExecutionId, start.InvocationId, start.InitialState.SnapshotId,
                "ramp", RecipeRampTerminalStatus.Cancelled, RecipeRampReturnOutcome.RestoredSnapshot, 10,
                DateTimeOffset.UtcNow, "cancelled", []) { Recovery = new(start.InitialState.SnapshotId,
                    incompleteProof ? [] : [proof], null) };
        }, TimeSpan.FromSeconds(5));
        var recoveryAuthority = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(clock.IsSuspended);
        Assert.Null(producer.TryEnterStep());
        Assert.True(arbiter.IsCurrent(recoveryAuthority));
        Assert.False(arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 30)).Accepted);
        Assert.Null(store.ReadTerminal(start.InitialState.ExecutionId, start.InvocationId));
        if (emergency) arbiter.DispatchSafety(OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 0), "emergency during return");
        if (storageFailure) writer.Run(System.IO.Path.Combine(root, start.InitialState.ExecutionId.ToString("N"),
            start.InvocationId.ToString("N"), "controlled-error"), () => throw new System.IO.IOException("terminal storage failure"));
        finish.SetResult();
        if (emergency)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovering);
            Assert.False(arbiter.IsCurrent(recoveryAuthority));
            Assert.Null(store.ReadTerminal(start.InitialState.ExecutionId, start.InvocationId));
            Assert.Equal(CommandOwner.Manual, arbiter.OwnerOf(ActuatorId.Temperature));
        }
        else if (storageFailure)
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => recovering);
            Assert.Contains(failure.Flatten().InnerExceptions, error => error is System.IO.IOException);
            Assert.True(arbiter.IsCurrent(recoveryAuthority));
            Assert.Null(store.ReadTerminal(start.InitialState.ExecutionId, start.InvocationId));
        }
        else if (incompleteProof)
        {
            await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => recovering);
            Assert.True(arbiter.IsCurrent(recoveryAuthority));
            Assert.Null(store.ReadTerminal(start.InitialState.ExecutionId, start.InvocationId));
        }
        else
        {
            Assert.True((await recovering).HasVerifiedRecovery);
            Assert.True(store.ReadTerminal(start.InitialState.ExecutionId, start.InvocationId)!.HasVerifiedRecovery);
            Assert.False(arbiter.IsCurrent(recoveryAuthority));
        }
        Assert.True(producer.StopToken.IsCancellationRequested);
        Assert.Null(producer.TryEnterStep());
    }
}
