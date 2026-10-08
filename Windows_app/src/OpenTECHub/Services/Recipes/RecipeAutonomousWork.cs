using OpenTECHub.Services.KlaTesting;

namespace OpenTECHub.Services.Recipes;

public sealed record RecipeKlaWork(string NodeId, KlaAssayExecutionCapabilities Capabilities,
    Func<PeriodicBlockInvocation?, CancellationToken, Task<KlaRecipeResult>> Execute,
    KlaRecipePauseControl? PauseControl = null);

public sealed record RecipePeriodicDefinition(PeriodicBlockInvocation Identity, TimeSpan DispatchTolerance,
    Func<RecipePeriodicSlotRecord, Task> Record);

public sealed record RecipeAutonomousExecutionPlan(IReadOnlyList<RecipeKlaWork> Assays,
    IReadOnlyList<RecipePeriodicDefinition> Schedules);

/// <summary>Resolves qualified installation profiles before the engine claims any actuator.</summary>
public interface IRecipeAutonomousWorkSource
{
    bool CanExecute(RecipeDocument recipe, out string? reason);
    RecipeAutonomousExecutionPlan CreateWork(RecipeDocument recipe, Guid executionId,
        RecipeResourceCoordinator resources);
}
