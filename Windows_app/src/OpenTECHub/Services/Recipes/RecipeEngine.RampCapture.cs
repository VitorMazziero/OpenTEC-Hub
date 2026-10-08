using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;

namespace OpenTECHub.Services.Recipes;

public sealed partial class RecipeEngine
{
    /// <summary>Caller reserves the destinations and quiesces any coordinated cascade before capturing.</summary>
    public RecipeRampInitialState CaptureRampInitialState(RecipeRampBlockConfiguration configuration,
        CommandAuthorityLease authority)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(authority);
        lock (_lock)
        {
            if (State != RecipeRunState.Running || authority.ExecutionId != ExecutionId ||
                _arbiter is not ICommandAuthorityArbiter arbiter)
                throw new InvalidOperationException("Captura exige execução atual e árbitro com reservas.");
            ControllerReturnSnapshot? controller = null;
            if (configuration.Definition.Lines.Any(line => line.OxygenTarget == RampOxygenTarget.ActiveCascadeReference))
            {
                if (configuration.CascadeNodeId is null || !_cascadeGates.TryGetValue(configuration.CascadeNodeId, out var gate) ||
                    !gate.IsQuiescentPaused || !_liveCascades.TryGetValue(configuration.CascadeNodeId, out var live))
                    throw new InvalidOperationException("Captura exige a cascata associada suspensa e sem comando em andamento.");
                controller = new() { ControllerId = configuration.CascadeNodeId, WasActive = true,
                    StateVersion = "recipe-cascade-v1", StateJson = live.CaptureStateJson() };
            }
            return RecipeRampInitialState.Capture(configuration, arbiter, authority, _time, controller);
        }
    }
}
