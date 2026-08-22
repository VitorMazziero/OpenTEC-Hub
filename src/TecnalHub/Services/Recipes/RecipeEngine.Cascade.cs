using TecnalHub.Services.Control;

namespace TecnalHub.Services.Recipes;

// Cascade control block execution in recipes
public sealed partial class RecipeEngine
{
    private async Task ExecuteCascadeAsync(RecipeNode node, CancellationToken ct)
    {
        Log(RecipeLogSeverity.Info, "Bloco de controle em cascata iniciado.", node.Id);
        await Task.CompletedTask;
    }
}
