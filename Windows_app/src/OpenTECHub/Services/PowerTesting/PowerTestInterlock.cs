using OpenTECHub.Services.Control;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>Read-only gate that keeps the assay out of a live cultivation or oxygen cascade.</summary>
public interface IPowerTestInterlock
{
    bool CanStart(out string? reason);
}

public sealed class PowerTestInterlock(
    ICascadeService cascade,
    IRecipeEngine recipes) : IPowerTestInterlock
{
    public bool CanStart(out string? reason)
    {
        if (cascade.IsEngaged)
        {
            reason = "Desengate a cascata de O₂ antes de iniciar o ensaio de potência.";
            return false;
        }
        if (recipes.State is RecipeRunState.Running or RecipeRunState.Paused)
        {
            reason = "Encerre a receita/cultivo ativo antes de iniciar o ensaio de potência.";
            return false;
        }
        reason = null;
        return true;
    }
}
