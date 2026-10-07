using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Only the host's isolated environment may construct an autonomous executor until physical qualification.</summary>
public sealed class KlaRecipeAssayExecutionFactory(IDeviceService device, ICommandArbiter arbiter, IKlaTestStore store,
    IKlaAnalysisEngine analysis, ISettingsService settings, TimeProvider time, bool isIsolatedEnvironment = false)
{
    public KlaRecipeAssayExecution Create(RecipeAssayResourceLease lease, KlaTestDocument document,
        RecipeAssayRecoveryCriteria recoveryCriteria, KlaAssayExecutionCapabilities capabilities)
    {
        if (!isIsolatedEnvironment || !capabilities.IsIsolatedSimulation)
            throw new InvalidOperationException("Execução autônoma física permanece fechada até qualificação R6.2.");
        return new(device, arbiter, store, analysis, settings, time, lease, document, recoveryCriteria, capabilities);
    }
}
