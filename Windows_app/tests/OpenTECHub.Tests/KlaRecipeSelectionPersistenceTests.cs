using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipeSelectionPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recipe-selection-" + Guid.NewGuid().ToString("N"));
    private async Task<(KlaTestStore Store, KlaRecipeSelectionCheckpoint Selection)> Prepare(BackgroundFileWriter writer)
    {
        var invocation = RecipeExecutionContractTests.Request();
        invocation = invocation with { Definition = invocation.Definition with { Settings = invocation.Definition.Settings with
            { MaxDegassingTimeMinutes = 0.5, MaxPrestageSeconds = 5 } } };
        var request = KlaRecipePulseMapper.Create(invocation, "test", invocation.Definition.Conditions[0].ConditionId,
            1, 1, invocation.Restoration.BeforeAssay.CapturedUtc.AddSeconds(1));
        var store = new KlaTestStore(_root, writer);
        var doc = store.CreateTest("automatic session", invocation.Definition);
        var run = new KlaTestRun { ConditionId = request.RecipePulse!.ConditionId, ReplicateNumber = 1,
            AgitationRpm = 300, AirflowLpm = 2, StartedUtc = invocation.Restoration.BeforeAssay.CapturedUtc };
        store.InitializeRunFolder(doc.FolderName, run);
        var outcome = new KlaRunOutcome { KlaQuality = KlaScientificQuality.Valid, OurQuality = KlaScientificQuality.NotApplicable,
            Restoration = KlaRestorationState.Confirmed };
        doc.Runs.Add(new() { RunId = run.RunId, ConditionId = run.ConditionId, ReplicateNumber = 1, AttemptNumber = 1,
            FolderName = run.FolderName, KlaPerHour = 40, Outcome = outcome, Phase = RunPhase.Reviewing });
        store.SaveRunAnalysis(doc.FolderName, run.FolderName, new() { Outcome = outcome, KlaPerHour = 40 });
        store.SaveTestManifest(doc);
        var prepared = new KlaAttemptPersistenceCheckpoint { Request = request, TestId = doc.TestId, RunId = run.RunId,
            TestFolder = doc.FolderName, RunFolder = run.FolderName, Phase = KlaAttemptPersistencePhase.BeforeActuation,
            Authority = new(Guid.NewGuid(), invocation.Context.RecipeRunId, invocation.Context.NodeId, CommandOwner.Recipe,
                0, invocation.Restoration.BeforeAssay.Actuators.Select(a => a.Actuator).ToImmutableArray()) };
        await store.PersistRecipeAttemptAsync(prepared);
        var result = new KlaAssayApiResult(outcome, 40, doc.FolderName, run.FolderName)
            { ReturnSnapshotId = invocation.Restoration.BeforeAssay.SnapshotId };
        var receipt = await store.PersistRecipeAttemptAsync(prepared with { Phase = KlaAttemptPersistencePhase.Terminal,
            Authority = prepared.Authority with { Owner = CommandOwner.KlaAssay, Generation = 1 }, Result = result,
            DecisionJson = "{\"State\":\"AwaitingSelection\"}" });
        var observation = new KlaAssayApiObservation(request, KlaAssayApiState.Completed, run.StartedUtc,
            run.StartedUtc.AddSeconds(2), result with { PersistenceReceiptId = receipt.ReceiptId });
        var selected = KlaRecipeAttemptDecider.Decide(observation, [], 9, 2, run.StartedUtc.AddSeconds(2));
        return (store, new() { Observation = observation, Decision = selected, RemainingCultivationAttempts = 9, ElapsedBlockSeconds = 2 });
    }

    [Fact]
    public async Task Durable_selection_reopens_and_repairs_stale_counters_without_human_acceptance()
    {
        using var writer = new BackgroundFileWriter(synchronous: true);
        var (store, selection) = await Prepare(writer);
        var before = store.LoadTest(selection.Observation.Result!.TestFolder!)!;
        Assert.Equal(0, before.Conditions[0].AcceptedReplicates);
        var receipt = await store.PersistRecipeSelectionAsync(selection);
        Assert.Equal(receipt, await store.PersistRecipeSelectionAsync(selection));
        // Simulate the last persisted manifest preceding projection of the immutable selection.
        store.SaveTestManifest(before); store.SaveConditionsTable(before.FolderName, before.Conditions);
        var reopened = new KlaTestStore(_root).LoadTest(before.FolderName)!;
        var run = Assert.Single(reopened.Runs);
        Assert.Equal(KlaOperatorDecision.Pending, run.EffectiveOutcome.OperatorDecision);
        Assert.True(KlaSequence.IsAccepted(run));
        Assert.Equal(1, reopened.Conditions[0].AcceptedReplicates);
        Assert.Empty(KlaSequence.Pending(reopened, reopened.Conditions));
        var copy = store.ReadRecipeSelection(before.FolderName, run.FolderName, selection.Decision.AttemptId)!;
        Assert.Equal(JsonSerializer.Serialize(selection), JsonSerializer.Serialize(copy));
    }

    [Fact]
    public async Task Altered_terminal_result_raw_hash_and_ambiguous_json_are_rejected()
    {
        using var writer = new BackgroundFileWriter(synchronous: true);
        var (store, selection) = await Prepare(writer);
        var altered = selection with { Observation = selection.Observation with
            { Result = selection.Observation.Result! with { KlaPerHour = 80 } }, Decision = selection.Decision with { KlaPerHour = 80 } };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistRecipeSelectionAsync(altered));
        await store.PersistRecipeSelectionAsync(selection);
        var result = selection.Observation.Result!;
        var path = Path.Combine(_root, result.TestFolder!, KlaTestFileContracts.RunsDirectoryName,
            result.RunFolder!, $"receita-{selection.Decision.AttemptId:N}-Selection.json");
        var json = File.ReadAllText(path);
        File.WriteAllText(path, "{\"SchemaVersion\":1," + json[1..]);
        Assert.Throws<InvalidDataException>(() => store.ReadRecipeSelection(result.TestFolder!, result.RunFolder!, selection.Decision.AttemptId));
        File.WriteAllText(path, json);
        File.AppendAllText(store.GetRunRawDataPath(result.TestFolder!, result.RunFolder!), "altered");
        Assert.Throws<InvalidDataException>(() => store.ReadRecipeSelection(result.TestFolder!, result.RunFolder!, selection.Decision.AttemptId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_selection_write_cannot_authorize_next_replica(bool synchronous)
    {
        using var writer = new BackgroundFileWriter(synchronous);
        var (store, selection) = await Prepare(writer);
        var result = selection.Observation.Result!;
        Directory.CreateDirectory(Path.Combine(_root, result.TestFolder!, KlaTestFileContracts.RunsDirectoryName,
            result.RunFolder!, $"receita-{selection.Decision.AttemptId:N}-Selection.json"));
        await Assert.ThrowsAsync<AggregateException>(() => store.PersistRecipeSelectionAsync(selection));
        Assert.Null(store.ReadRecipeSelection(result.TestFolder!, result.RunFolder!, selection.Decision.AttemptId));
        var run = Assert.Single(new KlaTestStore(_root).LoadTest(result.TestFolder!)!.Runs);
        Assert.Null(run.AutomaticDecision);
        Assert.False(KlaSequence.IsAccepted(run));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
