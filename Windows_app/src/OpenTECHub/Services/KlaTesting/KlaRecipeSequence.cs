using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

public sealed record KlaRecipeQueueProgress(KlaQueueItem? Next, KlaRecipeTerminalStatus? TerminalStatus);

/// <summary>Recipe scheduling uses persisted automatic decisions, not pending operator reviews.</summary>
public static class KlaRecipeSequence
{
    public static KlaRecipeQueueProgress Next(KlaRecipeRequest request, IReadOnlyCollection<KlaRecipeAttemptResult> decisions)
    {
        request.Validate();
        if (decisions.Select(a => a.AttemptId).Distinct().Count() != decisions.Count)
            throw new ArgumentException("Histórico de matriz duplicado.");
        foreach (var attempt in decisions)
        {
            attempt.Validate();
            var condition = request.Definition.Conditions.SingleOrDefault(c => c.ConditionId == attempt.ConditionId);
            if (condition is null || attempt.ReplicateNumber > condition.RequestedReplicates ||
                attempt.AttemptNumber > request.Retry.MaximumAttemptsPerReplicate ||
                attempt.AttemptId != KlaRecipePulseBinding.Identity(request.Context, attempt.ConditionId, attempt.ReplicateNumber, attempt.AttemptNumber) ||
                attempt.DecisionAuthor != RecipeDecisionAuthor.AutomaticPolicy || attempt.PolicyVersion != request.Quality.Version)
                throw new ArgumentException("Decisão pertence a outra matriz ou política.");
            if (attempt.Decision == KlaAutomaticDecision.Selected &&
                (attempt.KlaPerHour is not { } value || value <= 0 ||
                 request.Quality.RequireValidOur && attempt.OurQuality != KlaScientificQuality.Valid ||
                 attempt.KlaQuality == KlaScientificQuality.Conditional && (attempt.ReasonCodes.IsEmpty ||
                    attempt.ReasonCodes.Any(r => !request.Quality.AllowedConditionalReasonCodes.Contains(r)))))
                throw new ArgumentException("Seleção incompatível com a política científica.");
        }
        foreach (var group in decisions.GroupBy(a => (a.ConditionId, a.ReplicateNumber)))
        {
            var ordered = group.OrderBy(a => a.AttemptNumber).ToArray();
            if (ordered.Where((a, i) => a.AttemptNumber != i + 1 ||
                i < ordered.Length - 1 && a.Decision != KlaAutomaticDecision.Retry).Any())
                throw new ArgumentException("Histórico incompleto ou tentativa após decisão terminal da réplica.");
        }
        if (decisions.Count > request.Retry.MaximumAttemptsPerCultivation)
            throw new ArgumentException("Histórico excede orçamento da matriz.");
        if (decisions.Any(a => a.Restoration != KlaRestorationState.Confirmed))
            return new(null, KlaRecipeTerminalStatus.RestorationFailure);
        if (decisions.Any(a => !a.PersistenceConfirmed))
            return new(null, KlaRecipeTerminalStatus.PersistenceFailure);
        if (decisions.Any(a => a.Decision == KlaAutomaticDecision.Aborted))
            return new(null, decisions.Any(a => a.ReasonCodes.Contains("acquisition_cancelled"))
                ? KlaRecipeTerminalStatus.Cancelled : KlaRecipeTerminalStatus.OperationalFailure);
        var exhausted = decisions.Any(a => a.Decision == KlaAutomaticDecision.NotSelected);
        if (exhausted && request.FailurePolicy == KlaRecipeFailurePolicy.StopAfterRestoration)
            return new(null, KlaRecipeTerminalStatus.Inconclusive);
        foreach (var condition in request.Definition.Conditions.OrderBy(c => c.OrderIndex))
        {
            for (var replicate = 1; replicate <= condition.RequestedReplicates; replicate++)
            {
                var prior = decisions.Where(a => a.ConditionId == condition.ConditionId && a.ReplicateNumber == replicate)
                    .OrderBy(a => a.AttemptNumber).ToArray();
                if (prior.LastOrDefault()?.Decision is KlaAutomaticDecision.Selected or KlaAutomaticDecision.NotSelected) continue;
                if (prior.Length >= request.Retry.MaximumAttemptsPerReplicate || decisions.Count >= request.Retry.MaximumAttemptsPerCultivation)
                    return new(null, KlaRecipeTerminalStatus.Inconclusive);
                return new(new(condition.ConditionId, replicate, prior.Length + 1), null);
            }
        }
        return new(null, exhausted ? KlaRecipeTerminalStatus.Inconclusive :
            decisions.Any(a => a.KlaQuality == KlaScientificQuality.Conditional || a.OurQuality == KlaScientificQuality.Conditional)
                ? KlaRecipeTerminalStatus.CompletedWithWarnings : KlaRecipeTerminalStatus.Completed);
    }
}
