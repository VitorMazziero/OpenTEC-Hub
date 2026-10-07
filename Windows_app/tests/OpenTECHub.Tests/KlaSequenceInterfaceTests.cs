using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenTECHub.Services.KlaMapping;
using OpenTECHub.Services.KlaTesting;
using OpenTECHub.ViewModels;
using Xunit;

namespace OpenTECHub.Tests;

public sealed partial class KlaDeterminationViewModelTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task E5_Multiple_queue_continues_the_next_condition_and_can_be_ended_without_losing_history(bool biotic)
    {
        CreateCommonSession(biotic, false);
        _vm.NewConditionRpm = 600; _vm.NewConditionFlow = 4; _vm.AddManualCondition();
        _vm.NitrogenIsolationConfirmed = biotic; _vm.NitrogenSourceConfirmed = !biotic;
        await _vm.StartSequenceAsync();
        var first = _runner.CurrentCondition!;
        var doc = _vm.CurrentTest!;
        doc.Runs.Add(new() { ConditionId = first.ConditionId, ReplicateNumber = 1, Phase = RunPhase.Accepted,
            Decision = DecisionQuality.Acceptable, RemovalSeconds = 20, CompletedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            Outcome = new() { OperatorDecision = KlaOperatorDecision.Accepted, KlaQuality = KlaScientificQuality.Valid,
                Restoration = biotic ? KlaRestorationState.Confirmed : KlaRestorationState.NotRequired } });
        _runner.Phase = RunPhase.Accepted; _runner.RaiseStateChanged();
        await _vm.ContinueQueueAsync();
        Assert.Equal(600, _runner.CurrentCondition!.AgitationRpm);
        Assert.Equal(1, _runner.CurrentRun!.ReplicateNumber);
        Assert.True(_vm.HasQueuedRuns);
        _vm.StopQueue();
        Assert.False(_vm.HasQueuedRuns);
        Assert.Single(doc.Runs);
        Assert.Contains("QueueStopped", File.ReadAllText(Path.Combine(_store.RootDirectory, doc.FolderName, "eventos.jsonl")));
    }

    [Fact]
    public async Task E5_Single_point_import_saves_context_and_stays_a_draft_without_publishing_a_map()
    {
        var context = new KlaMeasurementContext { Medium = "Meio A", CultivationId = "Cultivo 7", TimeWindow = "Fase 1", Source = KlaMeasurementSource.Simulation };
        var source = SaveAcceptedE5Point("Ponto E5", context, 40);
        var profiles = new KlaProfileStore(Path.Combine(_testRoot, "Mapas"));
        var mapping = new KlaMappingViewModel(new KlaMappingEngine(), profiles, new FakeFileInteractionService(), _dialogs, _journal, _store);
        await mapping.InitializeAsync();
        await mapping.CreateExperimentCommand.ExecuteAsync(null);
        mapping.OpenImportFromTestDialog();
        mapping.SelectedKlaTestForImport = _store.ListTests().Single(t => t.TestId == source.TestId);
        Assert.True(Assert.Single(mapping.KlaTestImportCandidates).CanImport);
        mapping.ImportSelectedKlaTest();
        Assert.Single(mapping.Anchors);
        await mapping.SaveExperimentCommand.ExecuteAsync(null);
        var saved = Assert.Single(await profiles.LoadExperimentsAsync());
        Assert.Equal(KlaWorkflowStage.Draft, saved.Stage);
        Assert.False(saved.IsAvailableForControl);
        Assert.Equal(context, saved.Snapshot.MeasurementContext);
        Assert.Equal(KlaAssayProtocol.Biotic, saved.Snapshot.MeasurementProtocol);
        Assert.Equal(context, Assert.Single(saved.ImportedMeasurements).Context);
        Assert.Empty(await profiles.LoadPublishedAsync());
        Assert.NotEmpty(new KlaMappingEngine().Validate(saved.Snapshot));

        source = SaveAcceptedE5Point("Ponto E5", context, 60, source);
        mapping.SelectedKlaTestForImport = null;
        mapping.SelectedKlaTestForImport = _store.ListTests().Single(t => t.TestId == source.TestId);
        Assert.Single(mapping.KlaTestImportCandidates);
        mapping.ImportSelectedKlaTest();
        Assert.Equal(60, Assert.Single(mapping.Anchors).KlaValue);
        await mapping.SaveExperimentCommand.ExecuteAsync(null);
        saved = Assert.Single(await profiles.LoadExperimentsAsync());
        Assert.Equal(2, saved.ImportedMeasurements.Length);
        Assert.Single(saved.ImportedMeasurements.Where(m => m.Included));

        var other = SaveAcceptedE5Point("Outro meio E5", context with { Medium = "Meio B" }, 90);
        mapping.SelectedKlaTestForImport = _store.ListTests().Single(t => t.TestId == other.TestId);
        var incompatible = Assert.Single(mapping.KlaTestImportCandidates);
        Assert.False(incompatible.CanImport);
        incompatible.IsSelected = true; // The execution path rechecks, even if selection is forced.
        mapping.ImportSelectedKlaTest();
        Assert.Equal(60, Assert.Single(mapping.Anchors).KlaValue);
        Assert.Contains("correspondem", mapping.StatusMessage);
    }

    private KlaTestDocument SaveAcceptedE5Point(string name, KlaMeasurementContext context, double kla, KlaTestDocument? existing = null)
    {
        var condition = existing is null ? new KlaAssayCondition(Guid.NewGuid(), 0, 400, 3, 1) : KlaAssayCondition.From(existing.Conditions[0]);
        var definition = new KlaAssayDefinition { Protocol = KlaAssayProtocol.Biotic, CaptureMode = KlaCaptureMode.Single,
            Context = context, Conditions = [condition] };
        var doc = existing ?? _store.CreateTest(name, definition);
        var run = new KlaTestRun { TestId = doc.TestId, ConditionId = condition.ConditionId, ReplicateNumber = 1,
            AgitationRpm = 400, AirflowLpm = 3, Context = context,
            Definition = KlaRunDefinition.Create(doc, doc.Conditions[0], 1) };
        _store.InitializeRunFolder(doc.FolderName, run);
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        _store.SaveRunRawData(doc.FolderName, run.FolderName, Enumerable.Range(0, 3)
            .Select(i => new KlaRawDataPoint(start.AddSeconds(i), i, RunPhase.Reoxygenating, 10000, 40 + i, 3, 3, 400, false, true, false)));
        var outcome = new KlaRunOutcome { OperatorDecision = KlaOperatorDecision.Accepted, KlaQuality = KlaScientificQuality.Valid, Restoration = KlaRestorationState.Confirmed };
        _store.SaveRunPhysicalOutcome(doc.FolderName, run.FolderName, outcome);
        _store.SaveRunAnalysis(doc.FolderName, run.FolderName, new() { RevisionNumber = 1, Quality = DecisionQuality.Acceptable,
            KlaPerHour = kla, Outcome = outcome, AnalyzedUtc = start.AddMinutes(1) });
        _store.FlushAsync().GetAwaiter().GetResult();
        return _store.LoadTest(doc.FolderName)!;
    }

    [Fact]
    public async Task E5_Saving_imported_measurements_preserves_the_published_file_and_creates_an_unpublished_draft()
    {
        var context = new KlaMeasurementContext { Medium = "Meio A", CultivationId = "Cultivo 7", TimeWindow = "Fase 1", Source = KlaMeasurementSource.Simulation };
        var source = SaveAcceptedE5Point("Medição nova E5", context, 40);
        var profiles = new KlaProfileStore(Path.Combine(_testRoot, "Mapas"));
        var published = new KlaExperimentDocument
        {
            // Historical publication fixture; this test verifies file isolation, not numerical publication.
            Snapshot = new() { Name = "Mapa publicado", MeasurementContext = context, MeasurementProtocol = KlaAssayProtocol.Biotic,
                Anchors = [new(2, 200, 10), new(7, 200, 20), new(12, 200, 30), new(2, 500, 40), new(7, 500, 60), new(12, 500, 70)] },
            Stage = KlaWorkflowStage.Published, IsAvailableForControl = true,
        };
        await profiles.SaveExperimentAsync(published);
        var publishedPath = profiles.GetExperimentFilePath(published.Snapshot.Id);
        var before = File.ReadAllBytes(publishedPath);
        var mapping = new KlaMappingViewModel(new KlaMappingEngine(), profiles, new FakeFileInteractionService(), _dialogs, _journal, _store);
        await mapping.InitializeAsync();
        mapping.OpenImportFromTestDialog();
        mapping.SelectedKlaTestForImport = _store.ListTests().Single(t => t.TestId == source.TestId);
        mapping.ImportSelectedKlaTest();
        await mapping.SaveExperimentCommand.ExecuteAsync(null);
        Assert.Equal(before, File.ReadAllBytes(publishedPath));
        var documents = await profiles.LoadExperimentsAsync();
        Assert.Equal(2, documents.Count);
        var draft = Assert.Single(documents.Where(d => d.Snapshot.Id != published.Snapshot.Id));
        Assert.False(draft.IsAvailableForControl);
        Assert.Equal(KlaWorkflowStage.Draft, draft.Stage);
        Assert.Equal(draft.Snapshot.Name, mapping.ExperimentName);
        Assert.Equal(published.Snapshot.Id, Assert.Single(await profiles.LoadPublishedAsync()).Payload.ProfileId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task E5_Automatic_advance_waits_for_the_interval_and_stopping_the_queue_cancels_that_wait(bool stop)
    {
        CreateCommonSession(false, false);
        _vm.NewConditionRpm = 600; _vm.AddManualCondition();
        _vm.NitrogenSourceConfirmed = true;
        await _vm.StartSequenceAsync();
        var doc = _vm.CurrentTest!;
        var firstRun = _runner.CurrentRun;
        doc.Runs.Add(new() { ConditionId = _runner.CurrentCondition!.ConditionId, ReplicateNumber = 1,
            Phase = RunPhase.Accepted, Decision = DecisionQuality.Acceptable, RemovalSeconds = 20,
            CompletedUtc = DateTimeOffset.UtcNow,
            Outcome = new() { OperatorDecision = KlaOperatorDecision.Accepted, Restoration = KlaRestorationState.NotRequired } });
        doc.ProtocolSettings = doc.ProtocolSettings! with { AerationReturn = doc.ProtocolSettings.AerationReturn with
            { MinimumInterAssaySeconds = stop ? 60 : 0.1 } };
        _runner.Phase = RunPhase.Accepted; _runner.RaiseStateChanged();
        _vm.AutoAdvanceQueue = true;
        var advance = _vm.ContinueQueueAsync();
        if (stop) _vm.StopQueue();
        await advance.WaitAsync(TimeSpan.FromSeconds(5));
        if (stop) Assert.Same(firstRun, _runner.CurrentRun);
        else Assert.Equal(600, _runner.CurrentCondition!.AgitationRpm);
    }
}
