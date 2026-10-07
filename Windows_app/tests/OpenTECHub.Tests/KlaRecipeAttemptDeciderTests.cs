using OpenTECHub.Services.KlaTesting;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class KlaRecipeAttemptDeciderTests
{
    [Theory]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Single)]
    [InlineData(KlaAssayProtocol.Abiotic, KlaCaptureMode.Multiple)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Single)]
    [InlineData(KlaAssayProtocol.Biotic, KlaCaptureMode.Multiple)]
    public void First_valid_attempt_is_selected_without_operator_approval(KlaAssayProtocol protocol, KlaCaptureMode mode)
    {
        var observation = Observation(RecipeExecutionContractTests.Request(protocol, mode));
        var selected = KlaRecipeAttemptDecider.Decide(observation, [], 0, 0, observation.CompletedUtc!.Value);
        Assert.Equal(KlaAutomaticDecision.Selected, selected.Decision);
        Assert.Equal(KlaOperatorDecision.Pending, observation.Result!.Outcome.OperatorDecision);
        Assert.Throws<ArgumentException>(() => KlaRecipeAttemptDecider.Decide(observation, [selected], 1, 0, selected.DecidedUtc));
    }

    [Fact]
    public void Conditional_requires_all_reasons_explicitly_allowed_and_valid_our_when_required()
    {
        var invocation = RecipeExecutionContractTests.Request(KlaAssayProtocol.Biotic);
        var observation = Observation(invocation);
        observation = observation with { Result = observation.Result! with
        { Outcome = observation.Result.Outcome with { KlaQuality = KlaScientificQuality.Conditional }, ReasonCodes = ["probe_unknown"] } };
        Assert.Equal(KlaAutomaticDecision.NotSelected, Decide(observation).Decision);
        invocation = invocation with { Quality = invocation.Quality with { AllowedConditionalReasonCodes = ["probe_unknown"] } };
        observation = observation with { Request = Pulse(invocation) };
        Assert.Equal(KlaAutomaticDecision.Selected, Decide(observation).Decision);
        invocation = invocation with { Quality = invocation.Quality with { RequireValidOur = true } };
        observation = observation with { Request = Pulse(invocation), Result = observation.Result with
        { Outcome = observation.Result.Outcome with { OurQuality = KlaScientificQuality.Conditional } } };
        Assert.Equal(KlaAutomaticDecision.NotSelected, Decide(observation).Decision);
    }

    [Theory]
    [InlineData("invalid_or_short_window", true)]
    [InlineData("unrecognized_failure", false)]
    [InlineData("acquisition_cancelled", false)]
    [InlineData("probe_unknown", false)]
    public void Only_known_enabled_reasons_allow_retry(string reason, bool retry)
    {
        var observation = Observation(RecipeExecutionContractTests.Request());
        observation = observation with { State = KlaAssayApiState.Inconclusive, Result = observation.Result! with
        { Outcome = observation.Result.Outcome with { KlaQuality = KlaScientificQuality.Inconclusive }, KlaPerHour = null, ReasonCodes = [reason] } };
        Assert.Equal(retry ? KlaAutomaticDecision.Retry : KlaAutomaticDecision.NotSelected, Decide(observation).Decision);
        Assert.Equal(KlaAutomaticDecision.NotSelected,
            KlaRecipeAttemptDecider.Decide(observation, [], 0, 0, observation.CompletedUtc!.Value).Decision);
        Assert.Equal(KlaAutomaticDecision.NotSelected, KlaRecipeAttemptDecider.Decide(observation, [], 1,
            observation.Request.RecipePulse!.Invocation.Retry.MaximumBlockSeconds, observation.CompletedUtc!.Value).Decision);
    }

    [Theory]
    [InlineData(KlaAssayApiState.Cancelled)]
    [InlineData(KlaAssayApiState.PersistenceFailed)]
    [InlineData(KlaAssayApiState.RestorationFailed)]
    [InlineData(KlaAssayApiState.Interrupted)]
    public void Failure_never_selects_even_when_numeric_quality_is_valid(KlaAssayApiState state)
    {
        var observation = Observation(RecipeExecutionContractTests.Request()) with { State = state };
        Assert.Equal(KlaAutomaticDecision.Aborted, Decide(observation).Decision);
    }

    [Fact]
    public void Missing_receipt_failed_return_and_wrong_snapshot_do_not_advance()
    {
        var observation = Observation(RecipeExecutionContractTests.Request());
        Assert.Equal(KlaAutomaticDecision.Aborted, Decide(observation with { Result = observation.Result! with { PersistenceReceiptId = null } }).Decision);
        Assert.Equal(KlaAutomaticDecision.Aborted, Decide(observation with { Result = observation.Result! with
        { Outcome = observation.Result.Outcome with { Restoration = KlaRestorationState.Failed } } }).Decision);
        Assert.Throws<InvalidOperationException>(() => Decide(observation with { Result = observation.Result! with { ReturnSnapshotId = Guid.NewGuid() } }));
        Assert.Throws<InvalidOperationException>(() => Decide(observation with { State = KlaAssayApiState.Running }));
    }

    [Fact]
    public void Second_attempt_requires_authorized_retry_and_stops_at_replicate_limit()
    {
        var invocation = RecipeExecutionContractTests.Request();
        invocation = invocation with { Retry = invocation.Retry with { MaximumAttemptsPerReplicate = 2 } };
        var observation = Observation(invocation) with { State = KlaAssayApiState.Inconclusive };
        observation = observation with { Result = observation.Result! with { KlaPerHour = null,
            Outcome = observation.Result.Outcome with { KlaQuality = KlaScientificQuality.Inconclusive }, ReasonCodes = ["invalid_or_short_window"] } };
        var first = Decide(observation);
        Assert.Equal(KlaAutomaticDecision.Retry, first.Decision);
        var second = observation with { Request = Pulse(invocation, 2) };
        Assert.Throws<ArgumentException>(() => Decide(second));
        Assert.Equal(KlaAutomaticDecision.NotSelected, KlaRecipeAttemptDecider.Decide(second, [first], 1, 0, second.CompletedUtc!.Value).Decision);
        Assert.Throws<ArgumentException>(() => KlaRecipeAttemptDecider.Decide(second,
            [first with { Decision = KlaAutomaticDecision.NotSelected }], 1, 0, second.CompletedUtc!.Value));
    }

    private static KlaRecipeAttemptResult Decide(KlaAssayApiObservation observation) =>
        KlaRecipeAttemptDecider.Decide(observation, [], 1, 0, observation.CompletedUtc!.Value);
    private static KlaAssayApiRequest Pulse(KlaRecipeRequest invocation, int attempt = 1)
    {
        var frozen = RecipeContractSerializer.Snapshot(invocation);
        frozen = frozen with { Definition = frozen.Definition with
        { Settings = frozen.Definition.Settings with { MaxDegassingTimeMinutes = 0.5, MaxPrestageSeconds = 5 } } };
        return KlaRecipePulseMapper.Create(frozen, "test", frozen.Definition.Conditions[0].ConditionId,
            1, attempt, frozen.Restoration.BeforeAssay.CapturedUtc.AddSeconds(1));
    }
    private static KlaAssayApiObservation Observation(KlaRecipeRequest invocation) => new(Pulse(invocation), KlaAssayApiState.Completed,
        invocation.Restoration.BeforeAssay.CapturedUtc.AddSeconds(1), invocation.Restoration.BeforeAssay.CapturedUtc.AddSeconds(2),
        new(new KlaRunOutcome { KlaQuality = KlaScientificQuality.Valid, OurQuality = invocation.Definition.Protocol == KlaAssayProtocol.Abiotic
            ? KlaScientificQuality.NotApplicable : KlaScientificQuality.Valid, Restoration = KlaRestorationState.Confirmed }, 40, "session", "run")
        { ReturnSnapshotId = invocation.Restoration.BeforeAssay.SnapshotId, PersistenceReceiptId = "receipt" });
}
