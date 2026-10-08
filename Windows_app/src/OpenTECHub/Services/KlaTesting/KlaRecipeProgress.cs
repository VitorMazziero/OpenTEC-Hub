using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

public enum KlaRecipeProgressStage
{
    Preparing, WaitingCultivationInterval, ReservingResources, Acquiring,
    Recovering, WaitingBetweenAttempts, RecordingDecision, RecordingResult
}

public sealed record KlaRecipeOrchestratorProgress(KlaRecipeProgressStage Stage, RunPhase? Phase,
    KlaQueueItem? Item, int FinishedAttempts, int SelectedAttempts);

/// <summary>Read-only observation; remaining cultivation limits come from the shared API journal.</summary>
public sealed record KlaRecipeProgress(RecipeInvocationContext Context, KlaAssayProtocol Protocol,
    PeriodicBlockInvocation? PeriodicInvocation, string ProfileId, string ProfileVersion,
    string? SessionFolder, KlaRecipeProgressStage Stage, RunPhase? Phase, KlaQueueItem? Item, KlaAssayCondition? Condition,
    int FinishedAttempts, int SelectedAttempts, double ElapsedBlockSeconds, double RemainingBlockSeconds,
    KlaCultivationAssayBudget? CultivationBudget, DateTimeOffset? BudgetObservedUtc, string? BudgetObservationError);
