using System.IO;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampCheckpointStoreTests
{
    [Fact]
    public async Task CapturedDirectStateRequiresMatchingAcceptedEvidenceAndStorageFailureGivesNoReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "ramp-checkpoint-" + Guid.NewGuid().ToString("N"));
        using var writer = new BackgroundFileWriter();
        using var arbiter = new CommandArbiter(new RecordingDeviceService(), TimeProvider.System);
        arbiter.Claim(CommandOwner.Recipe, [ActuatorId.Temperature], "recipe");
        arbiter.Dispatch(CommandOwner.Recipe, OpenTECCommand.Create().Set(CommandKeys.TempSetpoint, 25));
        var configuration = new RecipeRampBlockConfiguration(new()
        {
            Lines = [new() { Variable = SetpointVariable.Temperature, FinalSetpoint = 30, EndAfterSeconds = 60 }],
            CancellationPolicy = RampCancellationPolicy.RestoreSnapshot
        }, null);
        var authority = await arbiter.ReserveAsync(CommandOwner.Recipe, Guid.NewGuid(), "ramp",
            RecipeRampInitialState.ResourcesFor(configuration.Definition), TimeSpan.FromSeconds(5));
        await arbiter.DrainReservedCommandsAsync(authority);
        var initial = RecipeRampInitialState.Capture(configuration, arbiter, authority, TimeProvider.System);
        var checkpoint = new RecipeRampStartCheckpoint(1, Guid.NewGuid(), configuration, initial);
        var store = new RecipeRampCheckpointStore(root, writer);
        await store.PersistStartAsync(checkpoint);
        var corrupt = checkpoint with { InvocationId = Guid.NewGuid(), InitialState = initial with
        { References = [initial.References[0] with { Reference = 26 }] } };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistStartAsync(corrupt));
        Assert.Null(store.ReadStart(initial.ExecutionId, corrupt.InvocationId));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledCheckpoint = checkpoint with { InvocationId = Guid.NewGuid() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PersistStartAsync(cancelledCheckpoint, cancelled.Token));
        Assert.Null(store.ReadStart(initial.ExecutionId, cancelledCheckpoint.InvocationId));
        var blocked = root + ".file";
        File.WriteAllText(blocked, "occupied");
        await Assert.ThrowsAnyAsync<IOException>(() => new RecipeRampCheckpointStore(blocked, writer).PersistStartAsync(checkpoint));
    }

    [Fact]
    public async Task DurableStartIsIdempotentAndRejectsAnotherSnapshotForTheInvocation()
    {
        var root = Path.Combine(Path.GetTempPath(), "ramp-checkpoint-" + Guid.NewGuid().ToString("N"));
        using var writer = new BackgroundFileWriter();
        var store = new RecipeRampCheckpointStore(root, writer);
        var initial = new RecipeRampInitialState(Guid.NewGuid(), Guid.NewGuid(), "ramp", DateTimeOffset.UtcNow, [], [], null);
        var checkpoint = new RecipeRampStartCheckpoint(1, Guid.NewGuid(), new(new()
        {
            Lines = [new() { Variable = SetpointVariable.Temperature, StartSource = SetpointStartSource.Explicit,
                InitialSetpoint = 25, FinalSetpoint = 30, EndAfterSeconds = 60 }]
        }, null), initial);
        await store.PersistStartAsync(checkpoint);
        await store.PersistStartAsync(checkpoint);
        Assert.Equal(initial.SnapshotId, new RecipeRampCheckpointStore(root, writer)
            .ReadStart(initial.ExecutionId, checkpoint.InvocationId)!.InitialState.SnapshotId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistStartAsync(checkpoint with
        { InitialState = initial with { SnapshotId = Guid.NewGuid() } }));
        Assert.Equal(initial.SnapshotId, store.ReadStart(initial.ExecutionId, checkpoint.InvocationId)!.InitialState.SnapshotId);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistStartAsync(checkpoint with
        { Configuration = checkpoint.Configuration with { Definition = checkpoint.Configuration.Definition with { CancellationPolicy = RampCancellationPolicy.RestoreSnapshot } } }));
        var path = Path.Combine(root, initial.ExecutionId.ToString("N"), checkpoint.InvocationId.ToString("N"), "start.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace(checkpoint.InvocationId.ToString(), Guid.NewGuid().ToString()));
        Assert.Throws<InvalidDataException>(() => store.ReadStart(initial.ExecutionId, checkpoint.InvocationId));
    }
}
