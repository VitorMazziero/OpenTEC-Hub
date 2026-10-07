using OpenTECHub.Services.KlaTesting;
using Xunit;

namespace OpenTECHub.Tests;

public sealed partial class KlaDeterminationViewModelTests
{
    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic)] [InlineData(KlaAssayProtocol.Biotic)]
    public void Automatic_session_shows_bad_attempts_and_selected_replica_without_operator_approval(KlaAssayProtocol protocol)
    {
        var request = RecipeExecutionContractTests.Request(protocol);
        var document = _store.CreateTest("Automatic result", request.Definition);
        document.RecipeRequest = request;
        document.Runs.Add(KlaAutomaticResultsTests.Run(request, 1, KlaAutomaticDecision.Retry));
        document.Runs.Add(KlaAutomaticResultsTests.Run(request, 2, KlaAutomaticDecision.Selected));
        _vm.CurrentTest = document;
        _vm.RefreshConditionsList();
        Assert.True(_vm.IsAutomaticSession); Assert.False(_vm.CanStartSequence);
        Assert.False(_vm.CanEditPreparation); Assert.False(_vm.CanEditProtocol);
        Assert.Equal(2, _vm.RecordedAutomaticAttempts.Count);
        Assert.Contains("inconclusivo", _vm.RecordedAutomaticAttempts[0].Kla);
        Assert.Contains("Selecionada automaticamente", _vm.MatrixRows.Single().DisplayStatus);
        Assert.Equal(40, _vm.MatrixRows.Single().KlaPerHour);
        Assert.Contains(request.Context.NodeId, _vm.AutomaticSessionSummary);
        Assert.Contains("ainda não confirmado", _vm.AutomaticTerminalStatus);
        Assert.All(document.Runs, r => Assert.Equal(KlaOperatorDecision.Pending, r.EffectiveOutcome.OperatorDecision));
    }

    [Fact]
    public async Task Automatic_attempt_can_be_opened_for_reading_but_cannot_be_reclassified_or_reconfigured()
    {
        var request = RecipeExecutionContractTests.Request();
        var document = _store.CreateTest("Automatic history", request.Definition);
        document.RecipeRequest = request;
        var run = KlaAutomaticResultsTests.Run(request, 1, KlaAutomaticDecision.NotSelected);
        document.Runs.Add(run);
        _store.SaveRunRawData(document.FolderName, run.FolderName,
            [new(DateTimeOffset.UtcNow, 0, RunPhase.Reoxygenating, 20, 20, 2, 2, 300, false, false, true)]);
        var analysis = new KlaAnalysisRevision { RevisionNumber = 1, KlaPerHour = 77, Quality = DecisionQuality.Acceptable };
        _store.SaveRunAnalysis(document.FolderName, run.FolderName, analysis);
        _vm.CurrentTest = document; _vm.RefreshConditionsList();
        _vm.LoadRecordedAutomaticAttempt(_vm.RecordedAutomaticAttempts.Single());
        Assert.True(_vm.IsReviewOpen); Assert.Single(_vm.LivePoints); Assert.False(_vm.CanDecideRun);
        var before = KlaTestFileContracts.SerializeTestDocument(document);
        _vm.SettingDOMin = 1; _vm.ApplyLiveSettings(); _vm.SaveAdvancedSettings();
        _vm.RemoveMatrixRow(_vm.MatrixRows.Single()); _vm.AddManualCondition();
        _vm.RecomputeReviewAnalysis(); await _vm.AcceptCurrentRunAsync();
        Assert.Equal(before, KlaTestFileContracts.SerializeTestDocument(document));
        Assert.Equal(77, _store.LoadRunAnalysis(document.FolderName, run.FolderName)!.KlaPerHour);
        var manual = _store.CreateTest("Operator session after automatic history", new KlaTestSettings());
        _vm.LoadTest(manual.FolderName);
        Assert.False(_vm.IsAutomaticSession); Assert.Empty(_vm.RecordedAutomaticAttempts);
    }
}
