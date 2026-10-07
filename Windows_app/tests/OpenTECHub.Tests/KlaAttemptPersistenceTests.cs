using System.IO;
using System.Collections.Immutable;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaAttemptPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kla-receipt-" + Guid.NewGuid().ToString("N"));
    private KlaAttemptPersistenceCheckpoint Prepare()
    {
        var invocation = RecipeExecutionContractTests.Request();
        invocation = invocation with { Definition = invocation.Definition with { Settings = invocation.Definition.Settings with
            { MaxDegassingTimeMinutes = 0.5, MaxPrestageSeconds = 15 } } };
        var request = KlaRecipePulseMapper.Create(invocation, "simulator-A", invocation.Definition.Conditions[0].ConditionId,
            1, 1, invocation.Restoration.BeforeAssay.CapturedUtc.AddMinutes(1));
        Directory.CreateDirectory(Path.Combine(_root, "session", KlaTestFileContracts.RunsDirectoryName, "run"));
        return new()
        {
            Request = request, TestId = Guid.NewGuid(), RunId = Guid.NewGuid(), TestFolder = "session", RunFolder = "run",
            Phase = KlaAttemptPersistencePhase.BeforeActuation,
            Authority = new(Guid.NewGuid(), invocation.Context.RecipeRunId, invocation.Context.NodeId, CommandOwner.Recipe, 0,
                invocation.Restoration.BeforeAssay.Actuators.Select(a => a.Actuator).ToImmutableArray())
        };
    }
    private static KlaAttemptPersistenceCheckpoint Terminal(KlaAttemptPersistenceCheckpoint prepared) => prepared with
    {
        Phase = KlaAttemptPersistencePhase.Terminal, DecisionJson = "{\"author\":\"AutomaticPolicy\"}",
        Result = new(new() { Restoration = KlaRestorationState.Confirmed, KlaQuality = KlaScientificQuality.Valid }, 40, "session", "run")
        { ReturnSnapshotId = prepared.Request.RecipePulse!.Invocation.Restoration.BeforeAssay.SnapshotId }
    };

    [Fact]
    public async Task ReconciliationPreservesBudgetAndDoesNotInferPhysicalRecoveryAfterInterruption()
    {
        var prepared = Prepare();
        var store = new KlaTestStore(_root);
        var observation = new KlaAssayApiObservation(prepared.Request, KlaAssayApiState.Interrupted,
            StartedUtc: DateTimeOffset.UtcNow);
        Assert.True(KlaAttemptReconciliation.Read(store, observation, "session", "run").ChargeReservedBudget);
        await store.PersistRecipeAttemptAsync(prepared);
        var pending = KlaAttemptReconciliation.Read(store, observation, "session", "run");
        Assert.Equal(KlaAttemptReconciliationState.PreparationOnly, pending.State);
        Assert.True(pending.RequiresRecoveryVerification);
        File.WriteAllText(store.GetRunRawDataPath("session", "run"), "time,oxygen\n0,30\n");
        var receipt = await store.PersistRecipeAttemptAsync(Terminal(prepared));
        var restored = KlaAttemptReconciliation.Read(new KlaTestStore(_root), observation, "session", "run");
        Assert.Equal(KlaAttemptReconciliationState.TerminalPersisted, restored.State);
        Assert.True(restored.ChargeReservedBudget);
        Assert.True(restored.RequiresRecoveryVerification);
        Assert.Equal(receipt.ReceiptId, restored.PersistedResult!.PersistenceReceiptId);
        Assert.Equal(KlaAssayApiState.Interrupted, observation.State);
    }

    [Fact]
    public async Task ReconciliationRejectsConflictingJournalResults()
    {
        var prepared = Prepare();
        var store = new KlaTestStore(_root);
        await store.PersistRecipeAttemptAsync(prepared);
        File.WriteAllText(store.GetRunRawDataPath("session", "run"), "raw");
        var terminal = Terminal(prepared);
        await store.PersistRecipeAttemptAsync(terminal);
        var observation = new KlaAssayApiObservation(prepared.Request, KlaAssayApiState.Completed,
            Result: terminal.Result! with { KlaPerHour = 99 });
        Assert.Throws<InvalidDataException>(() => KlaAttemptReconciliation.Read(store, observation, "session", "run"));
    }

    [Fact]
    public async Task ReceiptsAreIdempotentAndSurviveReopeningWithoutAnotherAttempt()
    {
        var prepared = Prepare();
        var store = new KlaTestStore(_root);
        var first = await store.PersistRecipeAttemptAsync(prepared);
        Assert.Equal(first, await store.PersistRecipeAttemptAsync(prepared));
        File.WriteAllText(store.GetRunRawDataPath("session", "run"), "time,oxygen\n0,30\n1,31\n");
        var terminal = await store.PersistRecipeAttemptAsync(Terminal(prepared));
        var reopened = new KlaTestStore(_root);
        Assert.Equal(first, reopened.ReadRecipeAttemptReceipt("session", "run", prepared.Request.RequestId, KlaAttemptPersistencePhase.BeforeActuation));
        Assert.Equal(terminal, reopened.ReadRecipeAttemptReceipt("session", "run", prepared.Request.RequestId, KlaAttemptPersistencePhase.Terminal));
        Assert.Equal(64, terminal.RawDataSha256!.Length);
        File.AppendAllText(store.GetRunRawDataPath("session", "run"), "2,32\n");
        Assert.Throws<InvalidDataException>(() => reopened.ReadRecipeAttemptReceipt("session", "run", prepared.Request.RequestId, KlaAttemptPersistencePhase.Terminal));
    }

    [Fact]
    public async Task MissingRawDataOrMissingPreparationCannotProduceTerminalReceipt()
    {
        var prepared = Prepare();
        var store = new KlaTestStore(_root);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.PersistRecipeAttemptAsync(Terminal(prepared)));
        await store.PersistRecipeAttemptAsync(prepared);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistRecipeAttemptAsync(Terminal(prepared)));
        Assert.Null(store.ReadRecipeAttemptReceipt("session", "run", prepared.Request.RequestId, KlaAttemptPersistencePhase.Terminal));
    }

    [Fact]
    public async Task ConflictingPayloadCannotReplaceCommittedCheckpoint()
    {
        var prepared = Prepare();
        var store = new KlaTestStore(_root);
        var receipt = await store.PersistRecipeAttemptAsync(prepared);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistRecipeAttemptAsync(prepared with { RunId = Guid.NewGuid() }));
        Assert.Equal(receipt, store.ReadRecipeAttemptReceipt("session", "run", prepared.Request.RequestId, KlaAttemptPersistencePhase.BeforeActuation));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
