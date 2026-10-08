using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Pure automatic selection. The caller must durably record the decision before advancing.</summary>
public static class KlaRecipeAttemptDecider
{
    public static KlaRecipeAttemptResult Decide(KlaAssayApiObservation observation,
        IReadOnlyCollection<KlaRecipeAttemptResult> history, int remainingCultivationAttempts,
        double elapsedBlockSeconds, DateTimeOffset now, KlaRecipePauseReceipt? pause = null)
    {
        observation.Request.Validate();
        var binding = observation.Request.RecipePulse ?? throw new ArgumentException("Pulso sem receita.");
        var invocation = binding.Invocation;
        if (remainingCultivationAttempts < 0 || !double.IsFinite(elapsedBlockSeconds) || elapsedBlockSeconds < 0)
            throw new ArgumentException("Orçamento restante ou tempo do bloco inválido.");
        if (observation.State is KlaAssayApiState.Created or KlaAssayApiState.Running ||
            observation.CompletedUtc is not { } completed || now < completed)
            throw new InvalidOperationException("A tentativa ainda não possui término observado.");
        var result = observation.Result ?? throw new InvalidOperationException("Tentativa sem resultado persistível.");
        pause?.ValidateAgainst(observation);
        if (string.IsNullOrWhiteSpace(result.RunFolder) || result.ReasonCodes.IsDefault ||
            result.ReasonCodes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Resultado sem pasta ou motivos estruturados.");
        foreach (var previous in history)
        {
            previous.Validate();
            var condition = invocation.Definition.Conditions.SingleOrDefault(c => c.ConditionId == previous.ConditionId);
            if (condition is null || previous.ReplicateNumber > condition.RequestedReplicates ||
                previous.AttemptId != KlaRecipePulseBinding.Identity(invocation.Context, previous.ConditionId,
                    previous.ReplicateNumber, previous.AttemptNumber) ||
                previous.PolicyVersion != invocation.Quality.Version || previous.DecisionAuthor != RecipeDecisionAuthor.AutomaticPolicy)
                throw new ArgumentException("Histórico pertence a outra matriz ou política.");
        }
        if (history.Select(a => a.AttemptId).Distinct().Count() != history.Count ||
            history.GroupBy(a => (a.ConditionId, a.ReplicateNumber, a.AttemptNumber)).Any(g => g.Count() != 1) ||
            history.Any(a => a.AttemptId == binding.AttemptId))
            throw new ArgumentException("Tentativa duplicada no histórico.");
        var prior = history.Where(a => a.ConditionId == binding.ConditionId &&
            a.ReplicateNumber == binding.ReplicateNumber).OrderBy(a => a.AttemptNumber).ToArray();
        if (prior.Length != binding.AttemptNumber - 1 || prior.Where((a, i) => a.AttemptNumber != i + 1 ||
            a.Decision != KlaAutomaticDecision.Retry).Any())
            throw new ArgumentException("Nova tentativa sem histórico contínuo de repetição autorizada.");

        var persisted = !string.IsNullOrWhiteSpace(result.PersistenceReceiptId);
        var returned = result.Outcome.Restoration == KlaRestorationState.Confirmed &&
            result.ReturnSnapshotId == invocation.Restoration.BeforeAssay.SnapshotId;
        var decision = KlaAutomaticDecision.Aborted;
        if (pause is not null && returned && persisted)
        {
            // A pause consumes the interrupted attempt. It cannot reset any matrix or cultivation limit.
            decision = binding.AttemptNumber < invocation.Retry.MaximumAttemptsPerReplicate &&
                remainingCultivationAttempts > 0 && now < invocation.AcquisitionDeadlineUtc &&
                elapsedBlockSeconds + invocation.Retry.MinimumInterAssaySeconds < invocation.Retry.MaximumBlockSeconds
                ? KlaAutomaticDecision.Retry : KlaAutomaticDecision.NotSelected;
        }
        if (returned && persisted && observation.State is KlaAssayApiState.Completed or KlaAssayApiState.Inconclusive)
        {
            if (observation.State == KlaAssayApiState.Completed && KlaRecipeQualityEvaluator.Accepts(observation.Request, result))
                decision = KlaAutomaticDecision.Selected;
            else
            {
                // Unknown, operational and assumption-related reasons never become an implicit retry.
                var reasonsAllowRetry = !result.ReasonCodes.IsEmpty && result.ReasonCodes.All(code =>
                    RetryReason(code) is { } reason && invocation.Retry.RecoverableReasons.Contains(reason));
                decision = reasonsAllowRetry && binding.AttemptNumber < invocation.Retry.MaximumAttemptsPerReplicate &&
                    remainingCultivationAttempts > 0 &&
                    elapsedBlockSeconds + invocation.Retry.MinimumInterAssaySeconds < invocation.Retry.MaximumBlockSeconds
                    ? KlaAutomaticDecision.Retry : KlaAutomaticDecision.NotSelected;
            }
        }
        var attempt = new KlaRecipeAttemptResult
        {
            AttemptId = binding.AttemptId, ConditionId = binding.ConditionId,
            ReplicateNumber = binding.ReplicateNumber, AttemptNumber = binding.AttemptNumber,
            RunFolder = result.RunFolder, KlaQuality = result.Outcome.KlaQuality, OurQuality = result.Outcome.OurQuality,
            KlaPerHour = result.KlaPerHour, Restoration = result.Outcome.Restoration,
            ReturnSnapshotId = invocation.Restoration.BeforeAssay.SnapshotId, PersistenceConfirmed = persisted,
            DecisionAuthor = RecipeDecisionAuthor.AutomaticPolicy, Decision = decision,
            PolicyVersion = invocation.Quality.Version, DecidedUtc = now,
            ReasonCodes = pause is null ? result.ReasonCodes : result.ReasonCodes.Add("recipe_pause")
        };
        // A mismatched snapshot is an invalid result, not evidence of the requested return.
        if (result.ReturnSnapshotId != attempt.ReturnSnapshotId)
            throw new InvalidOperationException("Resultado não identifica o snapshot deste pulso.");
        attempt.Validate();
        return attempt;
    }

    private static KlaRetryReason? RetryReason(string code) => code switch
    {
        "invalid_or_short_window" or "insufficient_respiratory_window" or "insufficient_confirmed_recovery" or
            "insufficient_equilibrium_points" => KlaRetryReason.InsufficientWindow,
        "insufficient_signal_to_noise" or "respiratory_signal_too_small" => KlaRetryReason.ExcessiveNoise,
        "nonconstant_rate_in_subwindows" => KlaRetryReason.UnstableCondition,
        _ => null
    };
}
