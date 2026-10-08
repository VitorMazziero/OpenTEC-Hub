namespace OpenTECHub.Services.Recipes;

/// <summary>Application-owned destination policies and durable storage; no guessed confirmation thresholds.</summary>
public sealed record RecipeRampExecutionConfiguration(RecipeRampCheckpointStore Store,
    Func<RecipeEngine, RecipeRampBlockConfiguration, RecipeRampFrameDestination> CreateDestination,
    TimeSpan PreparationTimeout, TimeSpan ConfirmationTimeout, TimeSpan RecoveryTimeout,
    TimeSpan MinimumDispatchInterval)
{
    public Func<RecipeEngine, RecipeRampStartCheckpoint, RecipeRampFrameDestination>? CreateCapturedDestination { get; init; }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Store); ArgumentNullException.ThrowIfNull(CreateDestination);
        foreach (var interval in new[] { PreparationTimeout, ConfirmationTimeout, RecoveryTimeout, MinimumDispatchInterval })
            if (interval <= TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentException("Configuração da rampa exige prazos positivos e finitos.");
    }
}
