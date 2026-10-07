using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using Xunit;
using OpenTECHub.ViewModels;

namespace OpenTECHub.Tests;

public sealed class KlaAutomaticResultsTests
{
    internal static KlaTestRunSummary Run(KlaRecipeRequest request, int attempt, KlaAutomaticDecision decision)
    {
        var selected = decision == KlaAutomaticDecision.Selected;
        var outcome = new KlaRecipeAttemptResult
        {
            AttemptId = Guid.NewGuid(), ConditionId = request.Definition.Conditions[0].ConditionId,
            ReplicateNumber = 1, AttemptNumber = attempt, RunFolder = $"run-{attempt}",
            KlaQuality = selected ? KlaScientificQuality.Valid : KlaScientificQuality.Inconclusive,
            OurQuality = request.Definition.Protocol == KlaAssayProtocol.Abiotic ? KlaScientificQuality.NotApplicable : KlaScientificQuality.Conditional,
            KlaPerHour = selected ? 40 : null, OurPercentPointsPerHour = request.Definition.Protocol == KlaAssayProtocol.Biotic ? 5 : null,
            Restoration = KlaRestorationState.Confirmed, ReturnSnapshotId = request.Restoration.BeforeAssay.SnapshotId,
            PersistenceConfirmed = true, DecisionAuthor = RecipeDecisionAuthor.AutomaticPolicy, Decision = decision,
            PolicyVersion = request.Quality.Version, DecidedUtc = request.Restoration.BeforeAssay.CapturedUtc.AddMinutes(attempt),
            ReasonCodes = [selected ? "qualified_result" : "insufficient_confirmed_recovery"]
        };
        outcome.Validate();
        return new() { RunId = Guid.NewGuid(), ConditionId = outcome.ConditionId, ReplicateNumber = 1, AttemptNumber = attempt,
            FolderName = outcome.RunFolder, AgitationRpm = 300, AirflowLpm = 2, Phase = RunPhase.Completed,
            StartedUtc = outcome.DecidedUtc.AddSeconds(-30), CompletedUtc = outcome.DecidedUtc,
            KlaPerHour = outcome.KlaPerHour, AutomaticDecision = outcome,
            Outcome = new() { KlaQuality = outcome.KlaQuality, OurQuality = outcome.OurQuality, Restoration = outcome.Restoration,
                OperatorDecision = KlaOperatorDecision.Pending } };
    }

    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic)] [InlineData(KlaAssayProtocol.Biotic)]
    public void Summary_keeps_every_attempt_and_independent_science_and_does_not_replace_policy_with_latest_revision(KlaAssayProtocol protocol)
    {
        var request = RecipeExecutionContractTests.Request(protocol);
        var document = new KlaTestDocument { Protocol = protocol, RecipeRequest = request };
        document.Runs.Add(Run(request, 1, KlaAutomaticDecision.Retry) with { KlaPerHour = 999 });
        document.Runs.Add(Run(request, 2, KlaAutomaticDecision.Selected));
        var csv = KlaAutomaticResultsSummary.Format(document);
        Assert.Equal(3, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("\"Retry\"", csv); Assert.Contains("\"Selected\"", csv);
        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("", rows[1].Split(',')[17]);
        Assert.Equal("40", rows[2].Split(',')[17]);
        Assert.Contains("\"AutomaticPolicy\"", csv);
        Assert.Contains(protocol == KlaAssayProtocol.Abiotic ? "\"NotApplicable\"" : "\"Conditional\"", csv);
        Assert.Contains("\"Confirmed\"", csv); Assert.Contains("insufficient_confirmed_recovery", csv);
        var row = new KlaRecordedAttemptViewModel(document.Runs[0], protocol);
        Assert.Contains("inconclusivo", row.Kla); Assert.DoesNotContain("999", row.Kla);
        Assert.Contains(protocol == KlaAssayProtocol.Abiotic ? "não aplicável" : "condicional", row.Our);
        Assert.Contains("nova tentativa", row.Decision);
    }

    [Fact]
    public void Csv_quotes_user_context_and_never_trusts_a_decision_bound_to_another_attempt()
    {
        var request = RecipeExecutionContractTests.Request();
        request = request with { Context = request.Context with { CultivationId = "=culture,\"line\"\nnext" } };
        var document = new KlaTestDocument { RecipeRequest = request };
        document.Runs.Add(Run(request, 1, KlaAutomaticDecision.Selected));
        Assert.Contains("\"'=culture,\"\"line\"\"\nnext\"", KlaAutomaticResultsSummary.Format(document));
        document.Runs[0] = document.Runs[0] with { AttemptNumber = 2 };
        Assert.Throws<ArgumentException>(() => KlaAutomaticResultsSummary.Format(document));
    }

    [Fact]
    public void Request_round_trips_through_common_manifest_without_changing_frozen_contract()
    {
        var request = RecipeExecutionContractTests.Request();
        request = request with { Definition = request.Definition with { SequenceLimits = null } };
        var document = new KlaTestDocument { RecipeRequest = request };
        var reopened = KlaTestFileContracts.DeserializeTestDocument(KlaTestFileContracts.SerializeTestDocument(document))!;
        var before = RecipeContractSerializer.Serialize(request);
        var after = RecipeContractSerializer.Serialize(reopened.RecipeRequest!);
        var firstDifference = before.Zip(after).TakeWhile(pair => pair.First == pair.Second).Count();
        var start = Math.Max(0, firstDifference - 100);
        Assert.True(before == after, $"Before: {before.Substring(start, Math.Min(300, before.Length - start))}\nAfter: {after.Substring(start, Math.Min(300, after.Length - start))}");
    }
}
