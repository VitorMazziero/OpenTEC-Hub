using System.IO;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.Services.Recipes;

public static class RecipeRampApplicationConfiguration
{
    public static RecipeRampExecutionConfiguration Create(string recipesDirectory, bool simulation, BackgroundFileWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipesDirectory);
        ArgumentNullException.ThrowIfNull(writer);
        var store = new RecipeRampCheckpointStore(Path.Combine(recipesDirectory, "AutomacaoRampas",
            simulation ? "simulacao" : "fisico"), writer);
        return new(store, (engine, configuration) => Criteria(configuration).CreateDestination(engine, configuration, null),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(300), TimeSpan.FromMilliseconds(100))
        {
            PrepareConfiguration = configuration => configuration.CompletionCriteria is null
                ? configuration with { CompletionCriteria = RecipeRampCompletionCriteria.OperationalDefaults } : configuration,
            CreateCapturedDestination = (engine, start) => Criteria(start.Configuration).CreateCapturedDestination(engine, start)
        };
    }

    private static RecipeRampCompletionCriteria Criteria(RecipeRampBlockConfiguration configuration)
        => configuration.CompletionCriteria ?? throw new InvalidOperationException("Rampa sem critérios congelados de confirmação.");
}
