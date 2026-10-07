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
        Authority = prepared.Authority with { Owner = CommandOwner.KlaAssay, Generation = prepared.Authority.Generation + 1 },
        Phase = KlaAttemptPersistencePhase.Terminal, DecisionJson = "{\"author\":\"AutomaticPolicy\"}",
        Result = new(new() { Restoration = KlaRestorationState.Confirmed, KlaQuality = KlaScientificQuality.Valid }, 40, "session", "run")
        { ReturnSnapshotId = prepared.Request.RecipePulse!.Invocation.Restoration.BeforeAssay.SnapshotId }
    };

    [Fact]
    public async Task TerminalCannotSkipOrReuseAuthorityGenerations()
    {
        var prepared = Prepare();
        var store = new KlaTestStore(_root);
        await store.PersistRecipeAttemptAsync(prepared);
        File.WriteAllText(store.GetRunRawDataPath("session", "run"), "raw");
        var terminal = Terminal(prepared);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistRecipeAttemptAsync(terminal with
            { Authority = terminal.Authority with { Generation = 0 } }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistRecipeAttemptAsync(terminal with
            { Authority = terminal.Authority with { Generation = 2 } }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PersistRecipeAttemptAsync(terminal with
            { Authority = terminal.Authority with { Owner = CommandOwner.Recipe } }));
        Assert.Null(store.ReadRecipeAttemptReceipt("session", "run", prepared.Request.RequestId, KlaAttemptPersistencePhase.Terminal));
        await store.PersistRecipeAttemptAsync(terminal);
    }

    [Fact]
    public async Task FailureDuringRawRecordingPreventsTerminalReceiptButPreservesPreparation()
    {
        var prepared = Prepare();
        using var writer = new BackgroundFileWriter();
        var store = new KlaTestStore(_root, writer);
        var receipt = await store.PersistRecipeAttemptAsync(prepared);
        var raw = store.GetRunRawDataPath("session", "run");
        writer.AppendLine(raw, "0,40", "time,oxygen");
        writer.Run(raw, () => throw new IOException("Injected acquisition write failure"));
        await Assert.ThrowsAsync<AggregateException>(() => store.PersistRecipeAttemptAsync(Terminal(prepared)));
        Assert.Equal(receipt, store.ReadRecipeAttemptReceipt("session", "run", prepared.Request.RequestId,
            KlaAttemptPersistencePhase.BeforeActuation));
        Assert.Null(store.ReadRecipeAttemptReceipt("session", "run", prepared.Request.RequestId, KlaAttemptPersistencePhase.Terminal));
        var observation = new KlaAssayApiObservation(prepared.Request, KlaAssayApiState.Interrupted,
            StartedUtc: DateTimeOffset.UtcNow);
        var reconciled = KlaAttemptReconciliation.Read(new KlaTestStore(_root), observation, "session", "run");
        Assert.Equal(KlaAttemptReconciliationState.PreparationOnly, reconciled.State);
        Assert.True(reconciled.ChargeReservedBudget);
    }

    [Theory]
    [InlineData(true, KlaAttemptPersistencePhase.BeforeActuation)]
    [InlineData(false, KlaAttemptPersistencePhase.BeforeActuation)]
    [InlineData(true, KlaAttemptPersistencePhase.Terminal)]
    [InlineData(false, KlaAttemptPersistencePhase.Terminal)]
    public async Task CheckpointWriteFailureCannotProduceReceipt(bool synchronous, KlaAttemptPersistencePhase phase)
    {
        var prepared = Prepare();
        using var writer = new BackgroundFileWriter(synchronous);
        var store = new KlaTestStore(_root, writer);
        if (phase == KlaAttemptPersistencePhase.Terminal)
        {
            await store.PersistRecipeAttemptAsync(prepared);
            File.WriteAllText(store.GetRunRawDataPath("session", "run"), "raw");
        }
        var path = Path.Combine(_root, "session", KlaTestFileContracts.RunsDirectoryName, "run",
            $"receita-{prepared.Request.RequestId:N}-{phase}.json");
        Directory.CreateDirectory(path);
        await Assert.ThrowsAsync<AggregateException>(() => store.PersistRecipeAttemptAsync(
            phase == KlaAttemptPersistencePhase.Terminal ? Terminal(prepared) : prepared));
        Assert.Null(store.ReadRecipeAttemptReceipt("session", "run", prepared.Request.RequestId, phase));
        await Assert.ThrowsAsync<AggregateException>(() => store.PersistRecipeAttemptAsync(
            phase == KlaAttemptPersistencePhase.Terminal ? Terminal(prepared) : prepared));
    }

    [Fact]
    public async Task DuplicateAndUnknownPropertiesCannotChangeCheckpointMeaning()
    {
        var prepared = Prepare();
        var store = new KlaTestStore(_root);
        await store.PersistRecipeAttemptAsync(prepared);
        var path = Path.Combine(_root, "session", KlaTestFileContracts.RunsDirectoryName, "run",
            $"receita-{prepared.Request.RequestId:N}-BeforeActuation.json");
        var original = File.ReadAllText(path);
        File.WriteAllText(path, "{\"SchemaVersion\":1," + original[1..]);
        Assert.Throws<InvalidDataException>(() => store.ReadRecipeAttemptReceipt("session", "run", prepared.Request.RequestId,
            KlaAttemptPersistencePhase.BeforeActuation));
        File.WriteAllText(path, "{\"UnexpectedField\":1," + original[1..]);
        Assert.Throws<System.Text.Json.JsonException>(() => store.ReadRecipeAttemptReceipt("session", "run", prepared.Request.RequestId,
            KlaAttemptPersistencePhase.BeforeActuation));
    }

    [Fact]
    public async Task ApiReconciliationPersistsReservedAttemptWithoutRedispatchOnReopen()
    {
        var prepared = Prepare();
        var store = new KlaTestStore(_root);
        await store.PersistRecipeAttemptAsync(prepared);
        var journal = Path.Combine(_root, "api.json");
        using (var api = new KlaAssayApi(journal, new NoExecution()))
        {
            api.Create(prepared.Request);
            var reconciled = api.ReconcileRecipeAttempt(prepared.Request.RequestId, store, "session", "run");
            Assert.Equal(KlaAssayApiState.Interrupted, reconciled.State);
            Assert.NotNull(reconciled.StartedUtc);
        }
        using var reopened = new KlaAssayApi(journal, new NoExecution());
        var resumed = await reopened.StartAsync(prepared.Request.RequestId);
        Assert.Equal(KlaAssayApiState.Interrupted, resumed.State);
        Assert.NotNull(resumed.StartedUtc);
        File.WriteAllText(store.GetRunRawDataPath("session", "run"), "raw");
        var receipt = await store.PersistRecipeAttemptAsync(Terminal(prepared));
        var terminal = reopened.ReconcileRecipeAttempt(prepared.Request.RequestId, store, "session", "run");
        Assert.Equal(KlaAssayApiState.Interrupted, terminal.State);
        Assert.Equal(receipt.ReceiptId, terminal.Result!.PersistenceReceiptId);
        Assert.False(terminal.MayContinueRecipe);
    }

    [Fact]
    public async Task ASecondWriterCannotCommitWhileTheSessionCheckpointIsLocked()
    {
        var prepared = Prepare();
        var directory = Path.Combine(_root, "session", KlaTestFileContracts.RunsDirectoryName, "run");
        using var externalLease = new FileStream(Path.Combine(directory, "receita-checkpoint.lease"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => new KlaTestStore(_root).PersistRecipeAttemptAsync(prepared));
    }

    private sealed class NoExecution : IKlaAssayExecution
    {
        public bool IsValidated => false;
        public Task<KlaAssayApiResult> ExecuteWithRecoveryAsync(KlaAssayApiRequest request, CancellationToken token)
            => throw new InvalidOperationException("Reconciliation must never command equipment.");
    }

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
