using System.IO;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeRampTerminalPersistenceTests
{
    [Fact]
    public async Task RestoredCancellationRequiresProofOfTheCapturedReferenceRatherThanTheRampTarget()
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
        var initial = RecipeRampInitialState.Capture(configuration, arbiter, authority, TimeProvider.System);
        arbiter.ReleaseReservation(authority);
        var start = new RecipeRampStartCheckpoint(1, Guid.NewGuid(), configuration, initial);
        var root = Path.Combine(Path.GetTempPath(), "ramp-return-proof-" + Guid.NewGuid().ToString("N"));
        var store = new RecipeRampCheckpointStore(root, writer);
        await store.PersistStartAsync(start);
        var proof = new RecipeRampFinalConfirmation(SetpointVariable.Temperature, null, 25,
            RecipeRampConfirmationEvidence.ProcessFeedback, DateTimeOffset.UtcNow, 25, .5);
        var terminal = new RecipeRampTerminalCheckpoint(2, initial.ExecutionId, start.InvocationId, initial.SnapshotId,
            initial.NodeId, RecipeRampTerminalStatus.Cancelled, RecipeRampReturnOutcome.RestoredSnapshot, 10,
            DateTimeOffset.UtcNow, "cancelled", []) { Recovery = new(initial.SnapshotId, [proof], null) };
        foreach (var invalid in new[] { terminal with { Recovery = null }, terminal with { SchemaVersion = 1 },
            terminal with { Recovery = terminal.Recovery with { SnapshotId = Guid.NewGuid() } },
            terminal with { Recovery = terminal.Recovery with { References = [] } },
            terminal with { Recovery = terminal.Recovery with { References = [proof with { Reference = 30, ObservedValue = 30 }] } },
            terminal with { Recovery = terminal.Recovery with { References = [proof with { Evidence = RecipeRampConfirmationEvidence.TransportAccepted }] } },
            terminal with { Recovery = terminal.Recovery with { References = [proof with { ObservedValue = 26 }] } },
            terminal with { Recovery = terminal.Recovery with { References = [proof with { RecordedUtc = initial.CapturedUtc.AddSeconds(-1) }] } } })
            await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistTerminalAsync(invalid));
        Assert.Null(store.ReadTerminal(initial.ExecutionId, start.InvocationId));
        var saved = await store.PersistTerminalAsync(terminal);
        Assert.True(saved.HasVerifiedRecovery);
        Assert.Equal(25, Assert.Single(store.ReadTerminal(initial.ExecutionId, start.InvocationId)!.Recovery!.References).Reference);
        var legacyStart = start with { InvocationId = Guid.NewGuid() };
        await store.PersistStartAsync(legacyStart);
        var legacy = terminal with { SchemaVersion = 1, InvocationId = legacyStart.InvocationId, Recovery = null };
        File.WriteAllText(Path.Combine(root, initial.ExecutionId.ToString("N"), legacyStart.InvocationId.ToString("N"), "terminal.json"),
            System.Text.Json.JsonSerializer.Serialize(legacy));
        Assert.False(store.ReadTerminal(initial.ExecutionId, legacyStart.InvocationId)!.HasVerifiedRecovery);
    }

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
