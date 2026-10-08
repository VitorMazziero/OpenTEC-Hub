using System.IO;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampTerminalPersistenceTests
{
    [Fact]
    public async Task UnrepresentablePositivePhCannotObtainADurableStartReceipt()
    {
        using var writer = new BackgroundFileWriter();
        var store = new RecipeRampCheckpointStore(Path.Combine(Path.GetTempPath(), "ramp-ph-off-" + Guid.NewGuid().ToString("N")), writer);
        var start = Start();
        start = start with { Configuration = new(new() { Lines = [new() { Variable = SetpointVariable.Ph,
            StartSource = SetpointStartSource.Explicit, InitialSetpoint = 6.5,
            FinalSetpoint = .004, EndAfterSeconds = 60 }] }, null) };
        await Assert.ThrowsAsync<ArgumentException>(() => store.PersistStartAsync(start));
        Assert.Null(store.ReadStart(start.InitialState.ExecutionId, start.InvocationId));
    }
    [Theory]
    [InlineData(SetpointVariable.Pressure, 100.9, 100)]
    [InlineData(SetpointVariable.Ph, 6.805, 6.81)]
    public async Task CompletionUsesTheSameQuantizedTargetAsTheTrajectory(SetpointVariable variable, double requested, double represented)
    {
        using var writer = new BackgroundFileWriter();
        var store = new RecipeRampCheckpointStore(Path.Combine(Path.GetTempPath(), "ramp-quantized-" + Guid.NewGuid().ToString("N")), writer);
        var start = Start();
        start = start with { Configuration = new(new() { Lines = [new() { Variable = variable,
            StartSource = SetpointStartSource.Explicit, InitialSetpoint = variable == SetpointVariable.Ph ? 6.5 : 90,
            FinalSetpoint = requested, EndAfterSeconds = 60 }] }, null) };
        await store.PersistStartAsync(start);
        var result = Completed(start) with { FinalConfirmations = [new(variable, null, requested,
            RecipeRampConfirmationEvidence.ProcessFeedback, DateTimeOffset.UtcNow, requested, 0)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistTerminalAsync(result));
        result = result with { FinalConfirmations = [result.FinalConfirmations[0] with { Reference = represented, ObservedValue = represented }] };
        await store.PersistTerminalAsync(result);
        Assert.Equal(represented, Assert.Single(store.ReadTerminal(result.ExecutionId, result.InvocationId)!.FinalConfirmations).Reference);
        Assert.Equal(requested, store.ReadStart(result.ExecutionId, result.InvocationId)!.Configuration.Definition.Lines[0].FinalSetpoint);
    }
    private static RecipeRampStartCheckpoint Start() => new(1, Guid.NewGuid(), new(new()
    {
        Lines = [new() { Variable = SetpointVariable.Temperature, StartSource = SetpointStartSource.Explicit,
            InitialSetpoint = 25, FinalSetpoint = 30, EndAfterSeconds = 60 }]
    }, null), new(Guid.NewGuid(), Guid.NewGuid(), "ramp", DateTimeOffset.UtcNow, [], [], null));

    private static RecipeRampTerminalCheckpoint Completed(RecipeRampStartCheckpoint start) => new(1,
        start.InitialState.ExecutionId, start.InvocationId, start.InitialState.SnapshotId, start.InitialState.NodeId,
        RecipeRampTerminalStatus.Completed, RecipeRampReturnOutcome.NotRequired, 60, DateTimeOffset.UtcNow, null,
        [new(SetpointVariable.Temperature, null, 30, RecipeRampConfirmationEvidence.ProcessFeedback, DateTimeOffset.UtcNow, 30, 0)]);

    [Fact]
    public async Task CompletionRequiresDurableStartActiveDurationAndEveryDestinationConfirmation()
    {
        using var writer = new BackgroundFileWriter();
        var root = Path.Combine(Path.GetTempPath(), "ramp-terminal-" + Guid.NewGuid().ToString("N"));
        var store = new RecipeRampCheckpointStore(root, writer);
        var start = Start(); var result = Completed(start);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistTerminalAsync(result));
        await store.PersistStartAsync(start);
        foreach (var invalid in new[] { result with { ActiveSeconds = 59 }, result with { FinalConfirmations = [] },
            result with { SnapshotId = Guid.NewGuid() }, result with { ReturnOutcome = RecipeRampReturnOutcome.RestoredSnapshot },
            result with { FinalConfirmations = [result.FinalConfirmations[0] with { Reference = 29 }] },
            result with { FinalConfirmations = [result.FinalConfirmations[0] with { ObservedValue = null }] },
            result with { FinalConfirmations = [result.FinalConfirmations[0] with { ObservedValue = 31 }] },
            result with { FinalConfirmations = [result.FinalConfirmations[0] with { Evidence = RecipeRampConfirmationEvidence.TransportAccepted }] },
            result with { FinalConfirmations = [result.FinalConfirmations[0] with { Evidence = RecipeRampConfirmationEvidence.ControllerReference }] } })
            await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistTerminalAsync(invalid));
        Assert.Null(store.ReadTerminal(result.ExecutionId, result.InvocationId));
        await store.PersistTerminalAsync(result);
        await store.PersistTerminalAsync(result);
        Assert.Equal(result.SnapshotId, new RecipeRampCheckpointStore(root, writer).ReadTerminal(result.ExecutionId, result.InvocationId)!.SnapshotId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistTerminalAsync(result with { EndedUtc = result.EndedUtc.AddSeconds(1) }));
        var path = Path.Combine(root, result.ExecutionId.ToString("N"), result.InvocationId.ToString("N"), "terminal.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace(result.SnapshotId.ToString(), Guid.NewGuid().ToString()));
        Assert.Throws<InvalidDataException>(() => store.ReadTerminal(result.ExecutionId, result.InvocationId));
    }

    [Fact]
    public async Task CancellationHonorsSavedPolicyAndEmergencySuppressesRestoration()
    {
        using var writer = new BackgroundFileWriter();
        var store = new RecipeRampCheckpointStore(Path.Combine(Path.GetTempPath(), "ramp-terminal-" + Guid.NewGuid().ToString("N")), writer);
        var start = Start(); await store.PersistStartAsync(start);
        var cancelled = Completed(start) with { Status = RecipeRampTerminalStatus.Cancelled,
            ActiveSeconds = 10, FinalConfirmations = [], Reason = "Cancelado pelo operador", ReturnOutcome = RecipeRampReturnOutcome.HeldLastReferences };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistTerminalAsync(cancelled with { Reason = null }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistTerminalAsync(cancelled with { ReturnOutcome = RecipeRampReturnOutcome.RestoredSnapshot }));
        await store.PersistTerminalAsync(cancelled);
        var emergencyStart = Start(); await store.PersistStartAsync(emergencyStart);
        var emergency = Completed(emergencyStart) with { Status = RecipeRampTerminalStatus.EmergencyStopped,
            FinalConfirmations = [], Reason = "Emergência", ReturnOutcome = RecipeRampReturnOutcome.RestoredSnapshot };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistTerminalAsync(emergency));
        await store.PersistTerminalAsync(emergency with { ReturnOutcome = RecipeRampReturnOutcome.SuppressedForEmergency });
    }
}
