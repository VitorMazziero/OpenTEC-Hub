using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipeSequenceTests
{
    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Multiple)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Single)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Multiple)]
    public void Matrix_finishes_all_replicates_without_operator_reviews(KlaAssayProtocol protocol, KlaCaptureMode mode)
    {
        var request = RecipeExecutionContractTests.Request(protocol, mode);
        var decisions = new List<KlaRecipeAttemptResult>();
        foreach (var condition in request.Definition.Conditions.OrderBy(c => c.OrderIndex))
            for (var replicate = 1; replicate <= condition.RequestedReplicates; replicate++)
            {
                var next = KlaRecipeSequence.Next(request, decisions);
                Assert.Null(next.TerminalStatus);
                Assert.Equal(new(condition.ConditionId, replicate, 1), next.Next);
                decisions.Add(Attempt(request, next.Next!, KlaAutomaticDecision.Selected));
            }
        Assert.Null(KlaRecipeSequence.Next(request, decisions).Next);
        Assert.Equal(KlaRecipeTerminalStatus.Completed, KlaRecipeSequence.Next(request, decisions).TerminalStatus);
        var first = decisions[0];
        Assert.Throws<ArgumentException>(() => KlaRecipeSequence.Next(request,
            [first, first with { AttemptId = Guid.NewGuid(), AttemptNumber = 2 }]));
    }

    [Fact]
    public void Retry_stays_on_replica_and_exhaustion_obeys_failure_policy()
    {
        var request = RecipeExecutionContractTests.Request(mode: KlaCaptureMode.Multiple);
        var first = KlaRecipeSequence.Next(request, []).Next!;
        var failed = Attempt(request, first, KlaAutomaticDecision.Retry);
        Assert.Equal(first with { AttemptNumber = 2 }, KlaRecipeSequence.Next(request, [failed]).Next);
        var exhausted = Attempt(request, first with { AttemptNumber = 2 }, KlaAutomaticDecision.NotSelected);
        Assert.Equal(KlaRecipeTerminalStatus.Inconclusive, KlaRecipeSequence.Next(request, [failed, exhausted]).TerminalStatus);
        request = request with { FailurePolicy = KlaRecipeFailurePolicy.ContinueWithoutResultAfterRestoration };
        Assert.Equal(new(first.ConditionId, 2, 1), KlaRecipeSequence.Next(request, [failed, exhausted]).Next);
        Assert.Equal(KlaRecipeTerminalStatus.RestorationFailure,
            KlaRecipeSequence.Next(request, [failed with { Decision = KlaAutomaticDecision.Aborted, Restoration = KlaRestorationState.Failed }]).TerminalStatus);
        Assert.Equal(KlaRecipeTerminalStatus.PersistenceFailure,
            KlaRecipeSequence.Next(request, [failed with { Decision = KlaAutomaticDecision.Aborted, PersistenceConfirmed = false }]).TerminalStatus);
    }

    [Fact]
    public void Snapshot_can_be_recaptured_between_pulses_but_identity_and_policy_cannot_change()
    {
        var request = RecipeExecutionContractTests.Request();
        var item = KlaRecipeSequence.Next(request, []).Next!;
        var selected = Attempt(request, item, KlaAutomaticDecision.Selected) with { ReturnSnapshotId = Guid.NewGuid() };
        Assert.Equal(KlaRecipeTerminalStatus.Completed, KlaRecipeSequence.Next(request, [selected]).TerminalStatus);
        Assert.Throws<ArgumentException>(() => KlaRecipeSequence.Next(request, [selected with { PolicyVersion = "other" }]));
        Assert.Throws<ArgumentException>(() => KlaRecipeSequence.Next(request, [selected with
            { KlaQuality = KlaScientificQuality.Conditional, ReasonCodes = ["probe_unknown"] }]));
    }

    [Fact]
    public void Operational_abort_cancellation_and_optional_conditional_our_have_distinct_terminal_states()
    {
        var request = RecipeExecutionContractTests.Request(KlaAssayProtocol.Biotic);
        var item = KlaRecipeSequence.Next(request, []).Next!;
        var aborted = Attempt(request, item, KlaAutomaticDecision.Aborted);
        Assert.Equal(KlaRecipeTerminalStatus.OperationalFailure, KlaRecipeSequence.Next(request, [aborted]).TerminalStatus);
        Assert.Equal(KlaRecipeTerminalStatus.Cancelled, KlaRecipeSequence.Next(request,
            [aborted with { ReasonCodes = ["acquisition_cancelled"] }]).TerminalStatus);
        var selected = Attempt(request, item, KlaAutomaticDecision.Selected);
        Assert.Equal(KlaRecipeTerminalStatus.CompletedWithWarnings, KlaRecipeSequence.Next(request,
            [selected with { OurQuality = KlaScientificQuality.Conditional }]).TerminalStatus);
        Assert.Throws<ArgumentException>(() => KlaRecipeSequence.Next(request, [selected with { KlaPerHour = 0 }]));
    }

    private static KlaRecipeAttemptResult Attempt(KlaRecipeRequest request, KlaQueueItem item, KlaAutomaticDecision decision) => new()
    {
        AttemptId = KlaRecipePulseMapper.Create(request with { Definition = request.Definition with
            { Settings = request.Definition.Settings with { MaxDegassingTimeMinutes = 0.5, MaxPrestageSeconds = 5 } } },
            "test", item.ConditionId, item.ReplicateNumber, item.AttemptNumber,
            request.Restoration.BeforeAssay.CapturedUtc.AddSeconds(1)).RequestId,
        ConditionId = item.ConditionId, ReplicateNumber = item.ReplicateNumber, AttemptNumber = item.AttemptNumber,
        RunFolder = "run-" + item.AttemptNumber, KlaQuality = decision == KlaAutomaticDecision.Selected
            ? KlaScientificQuality.Valid : KlaScientificQuality.Inconclusive,
        OurQuality = request.Definition.Protocol == KlaAssayProtocol.Biotic ? KlaScientificQuality.Valid : KlaScientificQuality.NotApplicable,
        KlaPerHour = decision == KlaAutomaticDecision.Selected ? 40 : null, Restoration = KlaRestorationState.Confirmed,
        ReturnSnapshotId = request.Restoration.BeforeAssay.SnapshotId, PersistenceConfirmed = true,
        DecisionAuthor = RecipeDecisionAuthor.AutomaticPolicy, Decision = decision, PolicyVersion = request.Quality.Version,
        DecidedUtc = request.Restoration.BeforeAssay.CapturedUtc.AddSeconds(2)
    };
}
